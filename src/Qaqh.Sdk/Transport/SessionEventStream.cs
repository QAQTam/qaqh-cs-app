using System.Net.Http;
using System.Text.Json;
using Qaqh.Sdk.Contract;

namespace Qaqh.Sdk.Transport;

/// <summary>SSE 流对宿主的传输面（由 <see cref="RingingClient"/> 实现）。</summary>
internal interface IV2StreamApi
{
    ClientLeaseState RequireLease();

    Task<SessionBootstrap> BootstrapAsync(string sessionId, CancellationToken cancellationToken);

    Task<(HttpResponseMessage Response, Stream Content)> SubscribeAsync(
        string sessionId, string? sinceCursor, CancellationToken cancellationToken);

    /// <summary>租约重协商信号：每次换租约即取消（链接型取消源）。</summary>
    CancellationToken LeaseChangedToken { get; }
}

/// <summary>
/// 每会话一条的常驻 v2 单流读循环。语义对齐 qaqh-client 的 <c>V2Stream</c>：
/// 无 cursor 先 bootstrap 再订阅（裸订阅会被服务端解释为「只看 live」）；
/// epoch 变化归零 cursor；字节层探活复位 45s 空闲计时；
/// <c>ringing.resync_required</c> 后服务端断流，cursor 归零重新 bootstrap；
/// 重连退避 1s→30s 指数，会话未物化（404/409）保持短退避不抬指数。
/// </summary>
internal sealed class SessionEventStream
{
    private const int IdleTimeoutMs = 45_000;
    private const int RetryBaseMs = 1_000;
    private const int RetryMaxMs = 30_000;

    private readonly string _sessionId;
    private readonly IV2StreamApi _api;
    private string? _cursor;
    private string? _lastEpoch;

    public event Action<string, SessionEventFrame>? Event;
    public event Action<string, RingingV2ResyncRequired>? Reset;
    public event Action<string, StreamStatus>? Status;
    public event Action? Liveness;

    public SessionEventStream(string sessionId, IV2StreamApi api)
    {
        _sessionId = sessionId;
        _api = api;
    }

    /// <summary>运行直到取消。除停止外不把错误抛给调用方。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryMs = RetryBaseMs;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _api.LeaseChangedToken);
            var leaseChanged = _api.LeaseChangedToken.IsCancellationRequested;
            try
            {
                await ConnectOnceAsync(scope.Token);
                // 干净返回（resync / 租约重协商）：立即重连，不退避。
                retryMs = RetryBaseMs;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // 租约重协商：立即用新租约重连。
                continue;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested || _api.LeaseChangedToken.IsCancellationRequested)
                {
                    continue;
                }
                var notReady = ex is SdkException sdk && sdk.IsSessionNotReady;
                Status?.Invoke(_sessionId, new StreamStatus.Reconnecting(
                    retryMs,
                    notReady ? null : ReasonOf(ex),
                    _cursor));
                try
                {
                    await Task.Delay(retryMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                retryMs = notReady ? RetryBaseMs : Math.Min(retryMs * 2, RetryMaxMs);
            }
        }
    }

    private static string? ReasonOf(Exception ex) => ex switch
    {
        SdkException { Kind: SdkErrorKind.Api, Detail: not null } detail => $"api:{detail.Detail}",
        SdkException { Kind: SdkErrorKind.Http, Status: var status } => $"http:{status}",
        _ => ex.GetType().Name,
    };

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        Status?.Invoke(_sessionId, new StreamStatus.Connecting());
        var lease = _api.RequireLease();
        if (_lastEpoch != lease.ServerEpoch)
        {
            // epoch 变化 → 旧序号失效，归零后从 snapshot 重新续传。
            _cursor = null;
            _lastEpoch = lease.ServerEpoch;
        }
        if (_cursor is null)
        {
            var bootstrap = await _api.BootstrapAsync(_sessionId, cancellationToken);
            _cursor = bootstrap.State.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var (response, content) = await _api.SubscribeAsync(_sessionId, _cursor, cancellationToken);
        using (response)
        await using (content)
        {
            Status?.Invoke(_sessionId, new StreamStatus.Open(lease.ServerEpoch, _cursor));
            await ReadLoopAsync(content, cancellationToken);
        }
    }

    private async Task ReadLoopAsync(Stream content, CancellationToken cancellationToken)
    {
        var decoder = new SseDecoder();
        var buffer = new byte[8192];
        while (true)
        {
            int read;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idle.CancelAfter(IdleTimeoutMs);
                try
                {
                    read = await content.ReadAsync(buffer, idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw SdkException.Transport("v2 SSE idle timeout");
                }
            }
            if (read == 0)
            {
                throw SdkException.Transport("v2 SSE stream ended");
            }
            Liveness?.Invoke();
            decoder.Push(buffer.AsSpan(0, read));

            SseFrame? frame;
            while ((frame = decoder.NextFrame()) is not null)
            {
                if (frame.EventType == "ringing.resync_required")
                {
                    var reset = JsonSerializer.Deserialize<RingingV2ResyncRequired>(frame.Data, ContractJson.Options)
                        ?? throw SdkException.Protocol("resync frame is empty");
                    // 服务端发完 resync 即断流；cursor 归零，重连后重新 bootstrap。
                    _cursor = null;
                    Reset?.Invoke(_sessionId, reset);
                    return;
                }
                var sessionFrame = JsonSerializer.Deserialize<SessionEventFrame>(frame.Data, ContractJson.Options)
                    ?? throw SdkException.Protocol("event frame is empty");
                _cursor = sessionFrame.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Event?.Invoke(_sessionId, sessionFrame);
            }
        }
    }
}

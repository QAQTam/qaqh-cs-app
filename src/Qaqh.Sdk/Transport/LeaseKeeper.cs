using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Qaqh.Sdk.Contract;
using Qaqh.Sdk.Discovery;

namespace Qaqh.Sdk.Transport;

/// <summary>
/// 租约协商 + 后台续期。语义对齐 qaqh-client 的 <c>RingingSession</c>：
/// open 带 10s 超时（冷启动窗口 TCP 可达但 HTTP 未 accept 时防挂死）；
/// 续期间隔 = max(1s, renew_interval_ms/2)；连续失败 ≥2 判定租约已死，
/// 直接重新 open 换新租约；本地发现模式下续租失败先重读 daemon.json 原地
/// 换凭据（daemon 重启换端口+token 的唯一自愈路径）。
/// </summary>
internal sealed class LeaseKeeper
{
    private const int MaxRenewFailures = 2;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RenewTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly string _clientInstanceId;
    private readonly RingingV2ClientKind? _clientKind;
    private readonly bool _localDiscovery;
    private readonly bool _allowSpawn;
    private readonly object _credentialsLock = new();
    private string _baseUrl = "";
    private string _token = "";
    private ClientLeaseState? _state;
    private int _renewFailures;

    /// <summary>新租约广播（open 或重协商成功）。SSE 流据此重连。</summary>
    public event Action<ClientLeaseState>? LeaseOpened;

    public LeaseKeeper(
        HttpClient http,
        string clientInstanceId,
        RingingV2ClientKind? clientKind,
        bool localDiscovery,
        bool allowSpawn = false)
    {
        _http = http;
        _clientInstanceId = clientInstanceId;
        _clientKind = clientKind;
        _localDiscovery = localDiscovery;
        _allowSpawn = allowSpawn;
    }

    public ClientLeaseState? State => _state;

    public (string BaseUrl, string Token) Credentials
    {
        get
        {
            lock (_credentialsLock)
            {
                return (_baseUrl, _token);
            }
        }
    }

    public void AdoptCredentials(string baseUrl, string token)
    {
        lock (_credentialsLock)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _token = token;
        }
    }

    /// <summary>重读 daemon.json 原地换值；返回是否真的变了（只接受 pid 存活的记录）。</summary>
    public bool RefreshDiscovery()
    {
        if (!_localDiscovery)
        {
            return false;
        }
        var discovery = DaemonLocator.TryRead();
        if (discovery is null || !DaemonLocator.PidIsRunning(discovery.Pid))
        {
            return false;
        }
        string baseUrl;
        try
        {
            baseUrl = discovery.BaseUrl().TrimEnd('/');
        }
        catch (SdkException)
        {
            return false;
        }
        lock (_credentialsLock)
        {
            if (_baseUrl == baseUrl && _token == discovery.Token)
            {
                return false;
            }
            _baseUrl = baseUrl;
            _token = discovery.Token;
        }
        return true;
    }

    /// <summary><c>POST /ringing/v2/clients/open</c> — 唯一握手，成功后广播新租约。</summary>
    public async Task<ClientLeaseState> OpenAsync(CancellationToken cancellationToken = default)
    {
        var (baseUrl, token) = Credentials;
        if (baseUrl.Length == 0)
        {
            throw SdkException.Negotiation("no credentials for open");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OpenTimeout);
        var request = new RingingV2OpenRequest
        {
            Schema = ContractConstants.RingingSchema,
            Version = ContractConstants.RingingV2Version,
            ClientInstanceId = _clientInstanceId,
            CommandContractRevision = ContractConstants.CommandContractRevision,
            SessionContractRevision = ContractConstants.SessionContractRevision,
            ClientKind = _clientKind,
        };
        using var openMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}{ContractConstants.RingingV2BasePath}/clients/open")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, ContractJson.Options),
                Encoding.UTF8,
                "application/json"),
        };
        openMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(openMessage, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, "/ringing/v2/clients/open");
        }
        var result = await response.Content.ReadFromJsonAsync<RingingV2OpenResponse>(ContractJson.Options, timeout.Token)
            ?? throw SdkException.Protocol("open response is empty");
        ValidateOpen(result);
        var state = new ClientLeaseState(
            _clientInstanceId,
            result.ClientSessionId,
            result.ServerEpoch,
            result.LeaseTtlMs,
            result.RenewIntervalMs,
            result.Capabilities ?? new RingingV2Capabilities());
        _state = state;
        _renewFailures = 0;
        LeaseOpened?.Invoke(state);
        return state;
    }

    private static void ValidateOpen(RingingV2OpenResponse result)
    {
        if (!result.Accepted)
        {
            throw SdkException.Negotiation("open not accepted by daemon");
        }
        if (result.CommandContractRevision != ContractConstants.CommandContractRevision
            || result.SessionContractRevision != ContractConstants.SessionContractRevision)
        {
            throw SdkException.Negotiation(
                $"open returned a mismatched contract revision: {result.CommandContractRevision} / {result.SessionContractRevision}");
        }
        if (result.ClientSessionId.Length == 0
            || result.ServerEpoch.Length == 0
            || result.LeaseTtlMs == 0
            || result.RenewIntervalMs == 0)
        {
            throw SdkException.Negotiation("open returned an incomplete session");
        }
    }

    /// <summary>启动后台续期循环；退出即取消。返回的 Task 常驻。</summary>
    public Task StartRenewalAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = _state;
                if (state is null)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }
                var interval = TimeSpan.FromMilliseconds(Math.Max(1000, state.RenewIntervalMs / 2));
                using var timer = new PeriodicTimer(interval);
                try
                {
                    while (await timer.WaitForNextTickAsync(cancellationToken))
                    {
                        await RenewTickAsync(cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                // 内层循环异常（续租抛错不应终止整个循环）：重置 timer 走下一轮。
            }
        }, CancellationToken.None);
    }

    private async Task RenewTickAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        if (state is null)
        {
            return;
        }
        bool renewed;
        try
        {
            renewed = await RenewOnceAsync(state.ClientSessionId, cancellationToken);
        }
        catch (Exception ex) when (ex is SdkException or OperationCanceledException or HttpRequestException or TaskCanceledException)
        {
            renewed = false;
        }
        if (renewed)
        {
            _renewFailures = 0;
            return;
        }
        if (RefreshDiscovery())
        {
            // 采纳了新 daemon 记录，继续重试即可。
        }
        _renewFailures++;
        if (_renewFailures < MaxRenewFailures)
        {
            return;
        }
        // 达到阈值：跳过注定失败的 renew，直接重新协商。
        RefreshDiscovery();
        // daemon 空闲自退（stop-if-idle 设计）后 discovery 变陈旧：lock 持有者
        // 也不在 → 没有任何实例在启动 → 本地模式下重新拉起，否则 open 永远
        // 打在死端口上（与 daemon 生命周期设计配套的自愈路径）。
        if (_allowSpawn && _localDiscovery
            && !DaemonLocator.PidIsRunning(DaemonLocator.TryRead()?.Pid ?? 0)
            && !DaemonLocator.LockHolderAlive())
        {
            try
            {
                DaemonLauncher.SpawnDetached(DaemonLauncher.ResolveExecutable());
            }
            catch (SdkException)
            {
                // 可执行文件缺失：保持失败计数，下个 interval 重试。
            }
        }
        try
        {
            await OpenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is SdkException or OperationCanceledException or HttpRequestException or TaskCanceledException)
        {
            // 保持失败计数，下个 interval 重试 open。
        }
    }

    private async Task<bool> RenewOnceAsync(string clientSessionId, CancellationToken cancellationToken)
    {
        var (baseUrl, token) = Credentials;
        if (baseUrl.Length == 0)
        {
            return false;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RenewTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{ContractConstants.RingingV2BasePath}/leases/renew");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(ContractConstants.ClientSessionHeader, clientSessionId);
        using var response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }
        return true;
    }
}

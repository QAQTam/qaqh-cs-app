using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Qaqh.Sdk.Contract;
using Qaqh.Sdk.Discovery;
using Qaqh.Sdk.Serialization;
using Qaqh.Sdk.Transport;
using SdkException = Qaqh.Sdk.SdkException;

namespace Qaqh.Sdk;

/// <summary>
/// Ringing v2 客户端门面：发现/拉起 daemon、open+续期、每会话单流、
/// 命令信封、服务面 RPC、content 上传下载、审批/配对/停止。
/// 对齐 qaqh-client 的 <c>Client</c>：Cloneable 语义此处为单实例；
/// 事件在后台线程触发，UI 侧自行调度回 UI 线程。
/// </summary>
public sealed class RingingClient : IV2StreamApi, IDisposable
{
    private readonly RingingClientOptions _options;
    private readonly HttpClient _http;
    private readonly LeaseKeeper _lease;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _leaseChanged = new();
    private readonly object _streamsLock = new();
    private readonly Dictionary<string, StreamHandle> _streams = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attached = new(StringComparer.Ordinal);
    private Task? _renewalTask;

    public RingingClient(RingingClientOptions options)
    {
        _options = options;
        _http = BuildHttpClient(options.Remote?.TlsFingerprint);
        var kind = Enum.TryParse<RingingV2ClientKind>(options.ClientKind, ignoreCase: true, out var parsed)
            ? parsed
            : (RingingV2ClientKind?)null;
        _lease = new LeaseKeeper(
            _http,
            options.ClientInstanceId,
            kind,
            localDiscovery: options.Remote is null,
            allowSpawn: options.LaunchDaemonIfMissing && options.Remote is null);
        _lease.LeaseOpened += state =>
        {
            SignalLeaseChanged();
            LeaseOpened?.Invoke(state);
        };
    }

    // ── 事件面 ────────────────────────────────────────────────────────

    /// <summary>新租约（首次 open 或重协商）。</summary>
    public event Action<ClientLeaseState>? LeaseOpened;

    /// <summary>每会话事件帧（每 seed 一条 SSE 流）。</summary>
    public event Action<string, SessionEventFrame>? V2Event;

    /// <summary>服务端要求 resync：调用方应重新 bootstrap（流内部已自动处理）。</summary>
    public event Action<string, RingingV2ResyncRequired>? V2Reset;

    /// <summary>流状态迁移（每 seed 一条）。</summary>
    public event Action<string, StreamStatus>? V2Status;

    /// <summary>存活信号：从 socket 收到字节（含 keepalive 注释行）。</summary>
    public event Action? Liveness;

    public ClientLeaseState? Lease => _lease.State;

    public static IReadOnlyList<string> QueryMethods { get; } = new[]
    {
        "daemon.version", "session.list", "clients.list", "session.activity", "config.load",
        "workspace.list", "skills.list_tools", "skills.catalog", "fs.list",
        "fs.read", "session.dashboard", "todo.status", "session.meta", "plan.read",
        "session.turn", "session.get_activity", "workspace.get", "todo.list",
        "diagnostics.memory.snapshot",
    };

    private static IReadOnlyList<string> AccountServiceMethods { get; } = new[]
    {
        "siwc.status", "siwc.profiles", "siwc.models", "siwc.sign_in.start", "siwc.sign_in.status",
        "siwc.sign_in.cancel", "siwc.profile.select", "siwc.sign_out",
        "dsaccount.status", "dsaccount.balance", "dsaccount.bonuses", "dsaccount.sign_in.start",
        "dsaccount.sign_in.status", "dsaccount.sign_in.cancel", "dsaccount.sign_out",
        "dsaccount.bonus.ack", "dsaccount.models", "dsaccount.verify",
    };

    // ── 连接生命周期 ──────────────────────────────────────────────────

    /// <summary>发现（必要时拉起）daemon → open 协商 → 启动续期与 attach 重放。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Remote is { } remote)
        {
            var base_url = remote.BaseUrl.TrimEnd('/');
            if (!base_url.StartsWith("http://", StringComparison.Ordinal)
                && !base_url.StartsWith("https://", StringComparison.Ordinal))
            {
                throw SdkException.Discovery($"remote base_url must start with http:// or https://: {base_url}");
            }
            _lease.AdoptCredentials(base_url, remote.Token);
        }
        else
        {
            var discovery = DaemonLocator.TryRead();
            if (discovery is not null && DaemonLocator.PidIsRunning(discovery.Pid)
                && await DaemonLocator.EndpointReachable(discovery))
            {
                _lease.AdoptCredentials(discovery.BaseUrl(), discovery.Token);
            }
            else if (_options.LaunchDaemonIfMissing)
            {
                discovery = await DaemonLauncher.EnsureRunningAsync(_options.StartTimeout, cancellationToken);
                _lease.AdoptCredentials(discovery.BaseUrl(), discovery.Token);
            }
            else
            {
                throw SdkException.Discovery("no live daemon discovery (daemon.json missing or stale)");
            }
        }

        await _lease.OpenAsync(cancellationToken);
        _renewalTask ??= _lease.StartRenewalAsync(_stop.Token);
        StartAttachReplay();
    }

    /// <summary>租约重协商后为已 attach 的 seed 重放 attach（BUG-2026-09-12-10 同款处理）。</summary>
    private void StartAttachReplay()
    {
        _ = Task.Run(async () =>
        {
            var previous = _lease.State;
            while (!_stop.IsCancellationRequested)
            {
                ClientLeaseState? current;
                lock (_leaseChanged)
                {
                    current = _lease.State;
                }
                if (current is not null && previous is not null
                    && current.ClientSessionId != previous.ClientSessionId)
                {
                    foreach (var sessionId in _attached.ToArray())
                    {
                        try
                        {
                            await AttachAsync(sessionId, CancellationToken.None);
                        }
                        catch (Exception)
                        {
                            // 下一轮重协商再试。
                        }
                    }
                    previous = current;
                }
                else if (current is not null && previous is null)
                {
                    previous = current;
                }
                try
                {
                    await Task.Delay(500, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    private void SignalLeaseChanged()
    {
        // 换新租约：打断所有正在用旧 client_session_id 的 SSE 读循环，
        // 流会立刻用新租约重连（连接前重新 RequireLease）。
        var previous = _leaseChanged;
        _leaseChanged = new CancellationTokenSource();
        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        previous.Dispose();
    }

    public void Close()
    {
        _stop.Cancel();
        lock (_streamsLock)
        {
            foreach (var handle in _streams.Values)
            {
                handle.Stop();
            }
            _streams.Clear();
        }
    }

    public void Dispose()
    {
        Close();
        _stop.Dispose();
        _http.Dispose();
    }

    // ── 每会话流 ──────────────────────────────────────────────────────

    /// <summary>启动（或替换）一条 seed 的单流。seed 须先 attach。</summary>
    public void ActivateStream(string sessionId)
    {
        StreamHandle handle;
        lock (_streamsLock)
        {
            if (_streams.Remove(sessionId, out var prev))
            {
                prev.Stop();
            }
            handle = new StreamHandle(sessionId, this);
            _streams[sessionId] = handle;
        }
        handle.Start(_stop.Token);
    }

    /// <summary>停掉一条 seed 的单流（其余不动）。</summary>
    public void DeactivateStream(string sessionId)
    {
        lock (_streamsLock)
        {
            if (_streams.Remove(sessionId, out var handle))
            {
                handle.Stop();
            }
        }
    }

    public IReadOnlyList<string> ActiveStreams()
    {
        lock (_streamsLock)
        {
            return _streams.Keys.ToArray();
        }
    }

    private sealed class StreamHandle
    {
        private readonly SessionEventStream _stream;
        private readonly CancellationTokenSource _stop = new();

        /// <summary>
        /// linked CTS 必须被持有到 Stop：不持有的话 GC 会在流运行期间回收它，
        /// 取消无法传播 → 旧流泄漏 → 每个事件被重复投递 N 次。
        /// </summary>
        private CancellationTokenSource? _linked;

        public StreamHandle(string sessionId, RingingClient client)
        {
            _stream = new SessionEventStream(sessionId, client);
            _stream.Event += (id, frame) => client.V2Event?.Invoke(id, frame);
            _stream.Reset += (id, reset) => client.V2Reset?.Invoke(id, reset);
            _stream.Status += (id, status) => client.V2Status?.Invoke(id, status);
            _stream.Liveness += () => client.Liveness?.Invoke();
        }

        public void Start(CancellationToken clientStop)
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource(clientStop, _stop.Token);
            _ = _stream.RunAsync(_linked.Token);
        }

        public void Stop()
        {
            _stop.Cancel();
            _linked?.Dispose();
            _linked = null;
        }
    }

    // IV2StreamApi
    ClientLeaseState IV2StreamApi.RequireLease() =>
        _lease.State ?? throw SdkException.Negotiation("v2 session not open");

    CancellationToken IV2StreamApi.LeaseChangedToken => _leaseChanged.Token;

    async Task<SessionBootstrap> IV2StreamApi.BootstrapAsync(string sessionId, CancellationToken cancellationToken)
    {
        var lease = ((IV2StreamApi)this).RequireLease();
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/sessions/{Uri.EscapeDataString(sessionId)}/bootstrap";
        using var response = await SendAsync(HttpMethod.Get, baseUrl, token, path, lease.ClientSessionId, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        var bootstrap = await response.Content.ReadFromJsonAsync<SessionBootstrap>(ContractJson.Options, cancellationToken)
            ?? throw SdkException.Protocol("bootstrap response is empty");
        if (bootstrap.SessionContractRevision != ContractConstants.SessionContractRevision)
        {
            throw SdkException.Protocol($"unsupported session contract revision: {bootstrap.SessionContractRevision}");
        }
        return bootstrap;
    }

    async Task<(HttpResponseMessage, Stream)> IV2StreamApi.SubscribeAsync(
        string sessionId, string? sinceCursor, CancellationToken cancellationToken)
    {
        var lease = ((IV2StreamApi)this).RequireLease();
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/sessions/{Uri.EscapeDataString(sessionId)}/events";
        var url = sinceCursor is null ? $"{baseUrl}{path}" : $"{baseUrl}{path}?since_cursor={Uri.EscapeDataString(sinceCursor)}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(ContractConstants.ClientSessionHeader, lease.ClientSessionId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        return (response, await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    // ── 命令 ──────────────────────────────────────────────────────────

    /// <summary>命令结果：Accepted 与否 + 拒绝原因/同步结果。</summary>
    public sealed record AckResult(string CommandId, bool Accepted, string? RejectionReason, JsonElement? Result);

    /// <summary>提交命令（target = sessionId 或全局）。</summary>
    public Task<AckResult> SendCommandAsync(
        string? sessionId, RingingCommand command,
        IReadOnlyList<CommandPrecondition>? preconditions = null,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            sessionId is null ? (CommandTarget)new CommandTargetGlobal() : new CommandTargetSession { SessionId = sessionId },
            command, preconditions, cancellationToken);

    /// <summary>提交命令（显式 target + 前置条件 CAS）。</summary>
    public async Task<AckResult> SendCommandAsync(
        CommandTarget target, RingingCommand command,
        IReadOnlyList<CommandPrecondition>? preconditions = null,
        CancellationToken cancellationToken = default)
    {
        var lease = ((IV2StreamApi)this).RequireLease();
        var (baseUrl, token) = _lease.Credentials;
        var commandId = Ulid.NewUlid();
        var envelope = new RingingV2CommandEnvelope
        {
            Schema = ContractConstants.RingingSchema,
            Version = ContractConstants.RingingV2Version,
            ContractRevision = ContractConstants.CommandContractRevision,
            CommandId = commandId,
            ClientInstanceId = lease.ClientInstanceId,
            ClientSessionId = lease.ClientSessionId,
            Target = target,
            Command = command,
            Preconditions = preconditions is { Count: > 0 } ? [.. preconditions] : null,
        };
        var path = $"{ContractConstants.RingingV2BasePath}/commands";
        using var response = await SendAsync(
            HttpMethod.Post, baseUrl, token, path, lease.ClientSessionId,
            new StringContent(JsonSerializer.Serialize(envelope, ContractJson.Options), Encoding.UTF8, "application/json"),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        var ack = await response.Content.ReadFromJsonAsync<RingingV2CommandAck>(ContractJson.Options, cancellationToken)
            ?? throw SdkException.Protocol("command ack is empty");
        if (ack.CommandId != commandId)
        {
            throw SdkException.Protocol("RingingV2 command ack id does not match submission");
        }
        return new AckResult(ack.CommandId, ack.Status == RingingCommandAckStatus.Accepted, ack.Code ?? ack.Message, ack.Result);
    }

    /// <summary>attach（session.resume）并把 seed 记入归属集合。</summary>
    public async Task AttachAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var ack = await SendCommandAsync(
            new CommandTargetSession { SessionId = sessionId },
            new RingingCommandSessionResume(),
            preconditions: null,
            cancellationToken);
        if (ack.Accepted)
        {
            lock (_attached)
            {
                _attached.Add(sessionId);
            }
        }
    }

    /// <summary>权威快照（读模型水位 + timeline/tools/interactions）。</summary>
    public Task<SessionBootstrap> BootstrapAsync(string sessionId, CancellationToken cancellationToken = default) =>
        ((IV2StreamApi)this).BootstrapAsync(sessionId, cancellationToken);

    // ── 服务面 ────────────────────────────────────────────────────────

    /// <summary>服务面直调（封闭枚举方法名；params 为 null 时发空对象）。</summary>
    public async Task<JsonElement> QueryAsync(
        string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        var lease = ((IV2StreamApi)this).RequireLease();
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/service/{Uri.EscapeDataString(method)}";
        var body = JsonSerializer.Serialize(parameters ?? (object)new Dictionary<string, object?>(), ContractJson.Options);
        using var response = await SendAsync(
            HttpMethod.Post, baseUrl, token, path, lease.ClientSessionId,
            new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        return await response.Content.ReadFromJsonAsync<JsonElement>(ContractJson.Options, cancellationToken);
    }

    /// <summary>账号面白名单方法（siwc.* / dsaccount.*）。</summary>
    public Task<JsonElement> ServiceAsync(
        string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!AccountServiceMethods.Contains(method))
        {
            throw SdkException.Protocol($"service method not allowed: {method}");
        }
        return QueryAsync(method, parameters, cancellationToken);
    }

    // ── content ───────────────────────────────────────────────────────

    public sealed record ContentUploadResult(string ContentId, string MediaType, string Sha256, long Size, bool Truncated);

    /// <summary><c>POST /ringing/v2/content</c>：multipart 三字段（session_id / media_type / content）。</summary>
    public async Task<ContentUploadResult> UploadContentAsync(
        string sessionId, string mediaType, byte[] data, CancellationToken cancellationToken = default)
    {
        var lease = ((IV2StreamApi)this).RequireLease();
        var (baseUrl, token) = _lease.Credentials;
        var boundary = $"qaqh-{Guid.NewGuid():N}";
        using var body = new MultipartFormDataContent(boundary);
        body.Add(new StringContent(sessionId), "session_id");
        body.Add(new StringContent(mediaType), "media_type");
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(content, "content");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{ContractConstants.RingingV2BasePath}/content")
        {
            Content = body,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(ContractConstants.ClientSessionHeader, lease.ClientSessionId);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, "/ringing/v2/content", cancellationToken);
        }
        var wire = await response.Content.ReadFromJsonAsync<RingingV2ContentUploadResponse>(ContractJson.Options, cancellationToken)
            ?? throw SdkException.Protocol("content upload response is empty");
        return new ContentUploadResult(wire.ContentId, wire.MediaType, wire.Sha256, wire.Size, wire.Truncated);
    }

    /// <summary>按 canonical id（sha256:&lt;hex&gt; 或裸 hex）下载并校验摘要。</summary>
    public async Task<byte[]> DownloadContentAsync(string contentId, CancellationToken cancellationToken = default)
    {
        var lease = ((IV2StreamApi)this).RequireLease();
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/content/{Uri.EscapeDataString(contentId)}";
        using var response = await SendAsync(HttpMethod.Get, baseUrl, token, path, lease.ClientSessionId, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var expected = contentId.StartsWith("sha256:", StringComparison.Ordinal) ? contentId["sha256:".Length..] : contentId;
        if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw SdkException.Protocol($"content digest mismatch for {contentId}: expected {expected}, received {digest}");
        }
        return bytes;
    }

    /// <summary>provider 目录当前修订（provider.* 写命令的 CAS 前置条件）。取自 provider.list 命令的同步结果。</summary>
    public async Task<long> GetProviderCatalogRevisionAsync(CancellationToken cancellationToken = default)
    {
        var ack = await SendCommandAsync(
            target: new CommandTargetGlobal(),
            command: new RingingCommandProviderList(),
            preconditions: null,
            cancellationToken);
        if (!ack.Accepted || ack.Result is not { ValueKind: JsonValueKind.Object } catalog)
        {
            throw SdkException.Protocol($"provider.list did not return a catalog: {ack.RejectionReason}");
        }
        return catalog.TryGetProperty("revision", out var revision) && revision.TryGetInt64(out var value)
            ? value
            : throw SdkException.Protocol("provider.list result has no revision field");
    }

    /// <summary>团队投影快照（roster + inbox + task board + message board）。</summary>
    public Task<TeamResponse> GetTeamSnapshotAsync(string sessionId, CancellationToken cancellationToken = default) =>
        GetSessionJsonAsync<TeamResponse>($"sessions/{Uri.EscapeDataString(sessionId)}/team", cancellationToken);

    // ── 审批 ──────────────────────────────────────────────────────────

    /// <summary>会话作用域 GET 的 JSON 出口（审批面/团队快照共用）。</summary>
    private async Task<JsonElement> GetSessionJsonAsync(string subPath, CancellationToken cancellationToken)
    {
        var lease = ((IV2StreamApi)this).RequireLease();
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/{subPath}";
        using var response = await SendAsync(HttpMethod.Get, baseUrl, token, path, lease.ClientSessionId, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        return await response.Content.ReadFromJsonAsync<JsonElement>(ContractJson.Options, cancellationToken);
    }

    private async Task<T> GetSessionJsonAsync<T>(string subPath, CancellationToken cancellationToken)
    {
        var lease = ((IV2StreamApi)this).RequireLease();
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/{subPath}";
        using var response = await SendAsync(HttpMethod.Get, baseUrl, token, path, lease.ClientSessionId, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken)
            ?? throw SdkException.Protocol($"empty response for {path}");
    }

    /// <summary>admin 档审批投影（裸 JSON：pending_permission / pending_interaction）。</summary>
    public Task<JsonElement> PendingApprovalsAsync(string sessionId, CancellationToken cancellationToken = default) =>
        GetSessionJsonAsync($"sessions/{Uri.EscapeDataString(sessionId)}/approvals", cancellationToken);

    /// <summary>device 档一次性 challenge 列表。</summary>
    public Task<RingingV2DeviceChallenges> DeviceChallengesAsync(string sessionId, CancellationToken cancellationToken = default) =>
        GetSessionJsonAsync<RingingV2DeviceChallenges>($"sessions/{Uri.EscapeDataString(sessionId)}/approvals", cancellationToken);

    /// <summary>device 侧审批应答：一次性 challenge → canonical 命令。</summary>
    public async Task<RingingV2CommandAck> RespondDeviceApprovalAsync(
        string sessionId, RingingV2ApprovalRespondRequest request, CancellationToken cancellationToken = default)
    {
        var lease = ((IV2StreamApi)this).RequireLease();
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/sessions/{Uri.EscapeDataString(sessionId)}/approvals/respond";
        using var response = await SendAsync(
            HttpMethod.Post, baseUrl, token, path, lease.ClientSessionId,
            new StringContent(JsonSerializer.Serialize(request, ContractJson.Options), Encoding.UTF8, "application/json"),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        return await response.Content.ReadFromJsonAsync<RingingV2CommandAck>(ContractJson.Options, cancellationToken)
            ?? throw SdkException.Protocol("approval ack is empty");
    }

    // ── 配对 / 设备 / 停止 ────────────────────────────────────────────

    public sealed record PairTokenRequest(string ScopeGrant, string DeviceName, string Platform);

    public async Task<RingingV2PairTokenResponse> IssuePairingTokenAsync(
        string scopeGrant, string deviceName, string platform, CancellationToken cancellationToken = default)
    {
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/pairing/tokens";
        var body = JsonSerializer.Serialize(
            new RingingV2PairTokenRequest { ScopeGrant = scopeGrant, DeviceName = deviceName, Platform = platform },
            ContractJson.Options);
        using var response = await SendAsync(
            HttpMethod.Post, baseUrl, token, path, sessionHeader: null,
            new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        return await response.Content.ReadFromJsonAsync<RingingV2PairTokenResponse>(ContractJson.Options, cancellationToken)
            ?? throw SdkException.Protocol("pairing token response is empty");
    }

    /// <summary>免鉴权配对端点：一次性 pairing_token 换 device_token（明文仅出现一次）。</summary>
    public static async Task<RingingV2PairResponse> PairRemoteAsync(
        string baseUrl, string pairingToken, string deviceName, string platform,
        string? tlsFingerprint = null, CancellationToken cancellationToken = default)
    {
        var base_url = baseUrl.TrimEnd('/');
        using var http = BuildHttpClient(tlsFingerprint);
        var path = $"{ContractConstants.RingingV2BasePath}/pair";
        var body = JsonSerializer.Serialize(
            new RingingV2PairRequest { PairingToken = pairingToken, DeviceName = deviceName, Platform = platform },
            ContractJson.Options);
        using var response = await http.PostAsync(
            $"{base_url}{path}",
            new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        return await response.Content.ReadFromJsonAsync<RingingV2PairResponse>(ContractJson.Options, cancellationToken)
            ?? throw SdkException.Protocol("pair response is empty");
    }

    /// <summary>已配对设备列表（不含任何 token 材料）。</summary>
    public async Task<IReadOnlyList<RingingV2DeviceWire>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/devices";
        using var response = await SendAsync(HttpMethod.Get, baseUrl, token, path, sessionHeader: null, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
        var wire = await response.Content.ReadFromJsonAsync<RingingV2DevicesResponse>(ContractJson.Options, cancellationToken)
            ?? throw SdkException.Protocol("devices response is empty");
        return wire.Devices;
    }

    /// <summary>吊销设备（device_id 先按路径安全字符收口）。</summary>
    public async Task RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId.Length == 0 || deviceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or ':')))
        {
            throw SdkException.Protocol($"device_id contains non-path-safe characters: {deviceId}");
        }
        var (baseUrl, token) = _lease.Credentials;
        var path = $"{ContractConstants.RingingV2BasePath}/devices/{Uri.EscapeDataString(deviceId)}/revoke";
        using var response = await SendAsync(HttpMethod.Post, baseUrl, token, path, sessionHeader: null, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await SdkException.FromResponseAsync(response, path, cancellationToken);
        }
    }

    public enum StopStatus { Stopping, Busy, Unsupported }

    /// <summary><c>POST /control/v1/stop[-if-idle]</c>。</summary>
    public async Task<StopStatus> StopDaemonAsync(bool idleOnly, CancellationToken cancellationToken = default)
    {
        var (baseUrl, token) = _lease.Credentials;
        var path = idleOnly ? "/control/v1/stop-if-idle" : "/control/v1/stop";
        using var response = await SendAsync(HttpMethod.Post, baseUrl, token, path, sessionHeader: null, null, cancellationToken);
        return (int)response.StatusCode switch
        {
            200 => StopStatus.Stopping,
            409 => StopStatus.Busy,
            _ => StopStatus.Unsupported,
        };
    }

    // ── 传输底座 ──────────────────────────────────────────────────────

    public static HttpClient BuildHttpClient(string? tlsFingerprint)
    {
        var handler = new HttpClientHandler();
        if (tlsFingerprint is not null)
        {
            var expected = TlsFingerprint.Parse(tlsFingerprint)
                ?? throw SdkException.Protocol($"invalid tls fingerprint: {tlsFingerprint}");
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is X509Certificate2 x509 && TlsFingerprint.Matches(x509, expected);
        }
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan, // SSE 长连接；单请求超时由调用方用 CTS 控制
        };
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string baseUrl, string token, string path,
        string? sessionHeader, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, $"{baseUrl}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (sessionHeader is not null)
        {
            request.Headers.Add(ContractConstants.ClientSessionHeader, sessionHeader);
        }
        return await SharedHttpClient.Value.SendAsync(request, cancellationToken);
    }

    private static readonly Lazy<HttpClient> SharedHttpClient = new(() =>
        new HttpClient(new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan });
}

using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Qaqh.Sdk;
using Qaqh.Sdk.Discovery;
using Qaqh.Sdk.Contract;
using Qaqh.Sdk.Transport;
using SdkException = Qaqh.Sdk.SdkException;

namespace Qaqh_App;

/// <summary>
/// 应用壳的状态与 RingingV2 交互中枢：连接生命周期、工作区/会话列表、
/// 会话管理命令、会话激活（attach + 单流）。
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly RingingClient _client;

    /// <summary>底层 Ringing 客户端（ChatViewModel 拉 bootstrap 用）。</summary>
    public RingingClient Client => _client;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, SessionItemVm> _sessionsById = new(StringComparer.Ordinal);
    private CancellationTokenSource? _connectCts;

    /// <summary>会话被激活（attach 完成、事件流已起），壳层据此切到聊天页。</summary>
    public event Action<SessionItemVm>? SessionActivated;

    /// <summary>一条会话的实时事件帧（聊天页按 sessionId 分派）。</summary>
    public event Action<string, SessionEventFrame>? SessionEvent;

    public ShellViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _client = new RingingClient(new RingingClientOptions
        {
            ClientKind = "Desktop",
            LaunchDaemonIfMissing = true,
            StartTimeout = TimeSpan.FromSeconds(30),
        });
        _client.LeaseOpened += OnLeaseOpened;
        _client.V2Status += OnV2Status;
        _client.V2Event += OnV2Event;
    }

    public ObservableCollection<WorkspaceVm> Workspaces { get; } = new();

    public ObservableCollection<SessionItemVm> Sessions { get; } = new();

    [ObservableProperty]
    private WorkspaceVm? _selectedWorkspace;

    [ObservableProperty]
    private SessionItemVm? _selectedSession;

    [ObservableProperty]
    private string _connectionText = "未连接";

    [ObservableProperty]
    private ConnectionKind _connectionKind = ConnectionKind.Disconnected;

    [NotifyPropertyChangedFor(nameof(ConnectionBrush))]
    [ObservableProperty]
    private string? _lastError;

    public Microsoft.UI.Xaml.Media.Brush ConnectionBrush => UiMap.ConnectionBrush(ConnectionKind);

    public bool IsConnected => ConnectionKind == ConnectionKind.Connected;

    partial void OnConnectionKindChanged(ConnectionKind value) => OnPropertyChanged(nameof(IsConnected));

    partial void OnSelectedWorkspaceChanged(WorkspaceVm? value)
    {
        foreach (var session in Sessions)
        {
            session.Archived = false; // 切工作区先复位过滤，再按归属重算
        }
        FilterSessions();
    }

    // ── 连接 ──────────────────────────────────────────────────────────

    public async Task ConnectAsync()
    {
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        ConnectionKind = ConnectionKind.Connecting;
        ConnectionText = "正在连接 daemon…";
        LastError = null;
        try
        {
            await _client.ConnectAsync(_connectCts.Token);
            await RefreshWorkspacesAsync();
            await RefreshSessionsAsync();
            ConnectionKind = ConnectionKind.Connected;
            ConnectionText = $"已连接 · daemon {DaemonLocator.TryRead()?.DaemonVersion ?? ""}";
        }
        catch (Exception ex) when (ex is SdkException or OperationCanceledException)
        {
            ConnectionKind = ConnectionKind.Error;
            ConnectionText = "连接失败";
            LastError = ex.Message;
        }
    }

    private void OnLeaseOpened(ClientLeaseState lease)
    {
        // 换了 lease（重协商/daemon 重启）→ 重新拉会话列表。
        RunOnUi(async () =>
        {
            ConnectionKind = ConnectionKind.Connected;
            ConnectionText = "已连接 · 会话已重新协商";
            try
            {
                await RefreshWorkspacesAsync();
                await RefreshSessionsAsync();
            }
            catch (SdkException)
            {
                // 下一轮续租失败路径会再触发刷新，这里不吞掉状态。
            }
        });
    }

    private void OnV2Status(string sessionId, StreamStatus status)
    {
        if (status is StreamStatus.Reconnecting { Reason: not null } reconnecting)
        {
            RunOnUi(() => { ConnectionText = $"流重连中：{reconnecting.Reason}"; });
        }
    }

    private void OnV2Event(string sessionId, SessionEventFrame frame) =>
        RunOnUi(() =>
        {
            ApplyEventToSession(sessionId, frame);
            if (_chats.TryGetValue(sessionId, out var chat))
            {
                chat.OnFrame(frame);
            }
            SessionEvent?.Invoke(sessionId, frame);
        });

    // ── 聊天视图模型 ──────────────────────────────────────────────────

    private readonly Dictionary<string, ChatViewModel> _chats = new(StringComparer.Ordinal);

    public ChatViewModel GetChat(string sessionId)
    {
        if (!_chats.TryGetValue(sessionId, out var chat))
        {
            chat = new ChatViewModel(this, sessionId);
            _chats[sessionId] = chat;
        }
        return chat;
    }

    /// <summary>拉取权威快照填充聊天视图（打开会话时调用一次；事件流随后增量）。</summary>
    public Task EnsureChatLoadedAsync(string sessionId) => GetChat(sessionId).LoadAsync();

    // ── 审批响应 ──────────────────────────────────────────────────────

    /// <summary>工具权限审批（trust_folder = 信任整个工作区目录）。</summary>
    public Task RespondPermissionAsync(string sessionId, string toolCallId, bool approved, bool trustFolder) =>
        SendApprovalAsync(sessionId, new RingingCommandToolPermissionRespond
        {
            ToolCallId = toolCallId,
            Approved = approved,
            TrustFolder = trustFolder,
        });

    // 计划评审响应（plan.review.respond）已随 LTS 契约删除；计划面命令为 plan.action
    // （item_id + action + user_comment），待前端计划 UI 重设计后接入。

    /// <summary>Ask 问答（question_id 暂为空串，答案为自由文本）。</summary>
    public Task RespondAskAsync(string sessionId, string interactionId, string answer) =>
        SendApprovalAsync(sessionId, new RingingCommandInteractionAskRespond
        {
            InteractionId = interactionId,
            Answers = [new AskAnswer { QuestionId = "", Answer = answer }],
        });

    public Task DismissAskAsync(string sessionId, string interactionId) =>
        SendApprovalAsync(sessionId, new RingingCommandInteractionAskDismiss { InteractionId = interactionId });

    /// <summary>创建工作区（workspace.create，全局命令）。</summary>
    public async Task CreateWorkspaceAsync(string path)
    {
        EnsureConnected();
        var ack = await _client.SendCommandAsync(
            sessionId: null,
            new RingingCommandWorkspaceCreate { Path = path });
        if (!ack.Accepted)
        {
            throw SdkException.Protocol($"workspace.create 被拒绝：{ack.RejectionReason ?? "unknown"}");
        }
        await RefreshWorkspacesAsync();
    }

    /// <summary>签发设备配对令牌（Admin）。</summary>
    public Task<Qaqh.Sdk.Contract.RingingV2PairTokenResponse> IssuePairingTokenAsync(string scope, string deviceName, string platform) =>
        _client.IssuePairingTokenAsync(scope, deviceName, platform);

    /// <summary>团队快照（roster + inbox + board）。</summary>
    public Task<Qaqh.Sdk.Contract.TeamResponse> GetTeamAsync(string sessionId) =>
        _client.GetTeamSnapshotAsync(sessionId);

    /// <summary>中断 exec 会话（target = ExecSession）。</summary>
    public Task InterruptExecAsync(string execSessionId) =>
        SendTargetedAsync(new CommandTargetExecSession { Id = execSessionId }, new RingingCommandExecSessionInterrupt());

    /// <summary>终止 exec 会话。</summary>
    public Task TerminateExecAsync(string execSessionId) =>
        SendTargetedAsync(new CommandTargetExecSession { Id = execSessionId }, new RingingCommandExecSessionTerminate());

    private async Task SendTargetedAsync(CommandTarget target, RingingCommand command)
    {
        EnsureConnected();
        var ack = await _client.SendCommandAsync(target, command);
        if (!ack.Accepted)
        {
            throw SdkException.Protocol($"命令被拒绝：{ack.RejectionReason ?? "unknown"}");
        }
    }

    private async Task SendApprovalAsync(string sessionId, RingingCommand command)
    {
        EnsureConnected();
        var ack = await _client.SendCommandAsync(sessionId, command);
        if (!ack.Accepted)
        {
            throw SdkException.Protocol($"审批响应被拒绝：{ack.RejectionReason ?? "unknown"}");
        }
    }

    private void ApplyEventToSession(string sessionId, SessionEventFrame frame)
    {
        if (!_sessionsById.TryGetValue(sessionId, out var session))
        {
            return;
        }
        switch (frame)
        {
            case SessionEventFrameActivityChanged changed:
                session.ActivityState = MapActivity(changed.Activity);
                break;
            case SessionEventFrameTurnStarted turnStarted:
                _currentTurnBySession[sessionId] = turnStarted.TurnId;
                break;
            case SessionEventFrameTurnEnded:
                _currentTurnBySession[sessionId] = null;
                break;
            case SessionEventFrameSessionMetaChanged meta when !string.IsNullOrWhiteSpace(meta.Title):
                session.Title = meta.Title!;
                break;
        }
    }

    private static SessionRunStatus MapActivity(ActivityState state) => state switch
    {
        ActivityState.Running => SessionRunStatus.Running,
        ActivityState.WaitingUser => SessionRunStatus.WaitingUser,
        ActivityState.Cancelling => SessionRunStatus.Cancelling,
        _ => SessionRunStatus.Idle,
    };

    // ── 列表 ──────────────────────────────────────────────────────────

    public async Task RefreshWorkspacesAsync()
    {
        var list = await _client.QueryAsync("workspace.list");
        var entries = list.ValueKind == JsonValueKind.Array
            ? list.Deserialize<WorkspaceEntry[]>(ContractJson.Options) ?? []
            : [];
        Workspaces.Clear();
        foreach (var entry in entries)
        {
            Workspaces.Add(new WorkspaceVm { Id = entry.Id, Name = entry.Title, Path = entry.Path });
        }
    }

    public async Task RefreshSessionsAsync()
    {
        var list = await _client.QueryAsync("session.list");
        var entries = list.ValueKind == JsonValueKind.Array
            ? list.Deserialize<SessionListEntry[]>(ContractJson.Options) ?? []
            : [];
        _sessionsById.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            seen.Add(entry.SessionId);
            if (!_sessionsById.TryGetValue(entry.SessionId, out var vm))
            {
                vm = new SessionItemVm { SessionId = entry.SessionId };
                _sessionsById[entry.SessionId] = vm;
            }
            vm.UpdateFromListEntry(entry.UpdatedAt, entry.Model, entry.WorkspaceId, entry.Archived, entry.Title, entry.Status);
        }
        Sessions.Clear();
        foreach (var vm in _sessionsById.Values.OrderByDescending(s => s.UpdatedAt))
        {
            Sessions.Add(vm);
        }
        FilterSessions();
    }

    private void FilterSessions()
    {
        var selected = SelectedWorkspace;
        // 就地过滤保持选中与滚动：借用一个副本重建集合。
        var visible = Sessions.Where(s =>
            selected is null
                ? s.WorkspaceId is null
                : s.WorkspaceId == selected.Id)
            .ToList();
        foreach (var session in Sessions.ToList())
        {
            if (!visible.Contains(session))
            {
                Sessions.Remove(session);
            }
        }
        var index = 0;
        foreach (var session in visible)
        {
            if (!Sessions.Contains(session))
            {
                Sessions.Insert(Math.Min(index, Sessions.Count), session);
            }
            index++;
        }
    }

    // ── 会话管理 ──────────────────────────────────────────────────────

    public async Task CreateSessionAsync()
    {
        EnsureConnected();
        var ack = await _client.SendCommandAsync(
            sessionId: null,
            new RingingCommandSessionCreate { Cwd = SelectedWorkspace?.Path });
        if (!ack.Accepted)
        {
            throw SdkException.Protocol($"session.create 被拒绝：{ack.RejectionReason ?? "unknown"}");
        }
        await RefreshSessionsAsync();
    }

    public async Task RenameSessionAsync(SessionItemVm session, string title)
    {
        EnsureConnected();
        await _client.SendCommandAsync(
            session.SessionId,
            new RingingCommandSessionRename { Title = title });
        session.Title = title;
    }

    public Task ArchiveSessionAsync(SessionItemVm session) => SendSessionCommand(session, new RingingCommandSessionArchive());

    public Task UnarchiveSessionAsync(SessionItemVm session) => SendSessionCommand(session, new RingingCommandSessionUnarchive());

    public Task DeleteSessionAsync(SessionItemVm session) => SendSessionCommand(session, new RingingCommandSessionDelete());

    private async Task SendSessionCommand(SessionItemVm session, RingingCommand command)
    {
        EnsureConnected();
        var ack = await _client.SendCommandAsync(session.SessionId, command);
        if (!ack.Accepted)
        {
            throw SdkException.Protocol($"{command} 被拒绝：{ack.RejectionReason ?? "unknown"}");
        }
        await RefreshSessionsAsync();
    }

    /// <summary>激活会话：attach → 起单流 → 通知壳层切页。</summary>
    public async Task OpenSessionAsync(SessionItemVm session)
    {
        EnsureConnected();
        SelectedSession = session;
        await _client.AttachAsync(session.SessionId);
        _client.ActivateStream(session.SessionId);
        SessionActivated?.Invoke(session);
    }

    public void CloseDaemonConnection() => _client.Close();

    /// <summary>优雅停止 daemon（<c>/control/v1/stop[-if-idle]</c>）。</summary>
    public Task<RingingClient.StopStatus> StopDaemonAsync(bool idleOnly) => _client.StopDaemonAsync(idleOnly);

    // ── 对话 ──────────────────────────────────────────────────────────

    private readonly Dictionary<string, string?> _currentTurnBySession = new(StringComparer.Ordinal);

    public async Task SendMessageAsync(string sessionId, string text)
    {
        EnsureConnected();
        await _client.SendCommandAsync(
            sessionId,
            new RingingCommandConversationSendMessage { Text = text });
    }

    public Task CancelTurnAsync(string sessionId)
    {
        EnsureConnected();
        _currentTurnBySession.TryGetValue(sessionId, out var turnId);
        return _client.SendCommandAsync(
            sessionId,
            new RingingCommandConversationCancel { TurnId = turnId });
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw SdkException.Negotiation("尚未连接 daemon");
        }
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _ = _dispatcher.TryEnqueue(() => action());
        }
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Qaqh.Sdk;
using Qaqh.Sdk.Contract;

namespace Qaqh_App;

/// <summary>
/// 一条会话的聊天视图模型：bootstrap 快照合流 + 20 种会话事件的增量应用。
/// timeline 是权威顺序；工具卡片按 call_id 归并；审批卡片按 interaction_id
/// 增删。UI 线程独占（ShellViewModel 已把事件调度回 UI）。
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Dictionary<string, ChatAssistantVm> _blocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatToolVm> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatApprovalVm> _approvals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatExecVm> _execs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatSubagentVm> _subagents = new(StringComparer.Ordinal);
    private ulong _order;

    public ChatViewModel(ShellViewModel shell, string sessionId)
    {
        _shell = shell;
        SessionId = sessionId;
    }

    public string SessionId { get; }

    public ObservableCollection<ChatItemVm> Items { get; } = new();

    /// <summary>是否有未响应的审批卡（输入区据此显示提示条）。</summary>
    [ObservableProperty]
    private bool _hasPendingApproval;

    [ObservableProperty]
    private bool _loaded;

    /// <summary>拉取权威快照并重建条目。事件流按 seq 增量续传。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var bootstrap = await _shell.Client.BootstrapAsync(SessionId, cancellationToken);
        Items.Clear();
        _blocks.Clear();
        _tools.Clear();
        _approvals.Clear();
        _order = 0;
        IngestState(bootstrap.State);
        Loaded = true;
    }

    // ── 快照合流 ──────────────────────────────────────────────────────

    private void IngestState(SessionState state)
    {
        foreach (var entry in state.Timeline)
        {
            ApplyTimelineEntry(entry);
        }
        foreach (var tool in state.Tools)
        {
            UpdateToolFromView(tool);
        }
        foreach (var interaction in state.Interactions)
        {
            if (interaction.Resolution is null)
            {
                UpsertApproval(interaction);
            }
        }
        foreach (var exec in state.ExecSessions)
        {
            UpsertExec(exec);
        }
        foreach (var subagent in state.Subagents)
        {
            UpsertSubagent(subagent);
        }
        ReevaluatePending();
    }

    private void ApplyTimelineEntry(TimelineEntry entry)
    {
        switch (entry)
        {
            case TimelineEntryInput input:
                Items.Add(new ChatUserVm
                {
                    Order = _order++,
                    Text = TextOf(input.Data.Content),
                });
                break;
            case TimelineEntryAssistantBlock block:
            {
                // message_delta 可能已为同一 block_id 建卡（delta 先于 timeline 落场）。
                if (_blocks.TryGetValue(block.Data.BlockId, out var existing))
                {
                    existing.Text = TextOf(block.Data.Content);
                    break;
                }
                var vm = new ChatAssistantVm
                {
                    Order = _order++,
                    BlockId = block.Data.BlockId,
                    IsReasoning = block.Data.BlockKind == AssistantBlockKind.Reasoning,
                    Text = TextOf(block.Data.Content),
                };
                _blocks[vm.BlockId] = vm;
                Items.Add(vm);
                break;
            }
            case TimelineEntryToolCall call:
            {
                var vm = new ChatToolVm { Order = _order++, CallId = call.Data.CallId };
                if (_tools.TryGetValue(vm.CallId, out var existing))
                {
                    // ToolResult 先落了 timeline、ToolCall 后补的场景：原位替换。
                    var index = Items.IndexOf(existing);
                    if (index >= 0)
                    {
                        Items[index] = vm;
                    }
                }
                else
                {
                    Items.Add(vm);
                }
                _tools[vm.CallId] = vm;
                break;
            }
            case TimelineEntryToolResult result:
            {
                if (_tools.TryGetValue(result.Data.CallId, out var tool))
                {
                    tool.StateText = "已完成";
                    tool.IsFinished = true;
                    if (result.Data.Content is not null)
                    {
                        tool.AppendOutput(TextOf(result.Data.Content));
                    }
                }
                else
                {
                    var vm = new ChatToolVm
                    {
                        Order = _order++,
                        CallId = result.Data.CallId,
                        StateText = "已完成",
                        IsFinished = true,
                        OutputTail = result.Data.Content is null ? "" : TextOf(result.Data.Content),
                    };
                    _tools[vm.CallId] = vm;
                    Items.Add(vm);
                }
                break;
            }
            case TimelineEntryCompaction:
                Items.Add(new ChatDividerVm { Order = _order++, Text = "历史回合已压缩" });
                break;
        }
    }

    // ── 事件增量 ──────────────────────────────────────────────────────

    public void OnFrame(SessionEventFrame frame)
    {
        switch (frame)
        {
            case SessionEventFrameTimelineAppended appended:
                ApplyTimelineEntry(appended.Entry);
                break;
            case SessionEventFrameMessageDelta delta:
            {
                if (!_blocks.TryGetValue(delta.BlockId, out var block))
                {
                    block = new ChatAssistantVm
                    {
                        Order = _order++,
                        BlockId = delta.BlockId,
                        IsReasoning = delta.BlockKind == AssistantBlockKind.Reasoning,
                    };
                    _blocks[block.BlockId] = block;
                    Items.Add(block);
                }
                // 契约：同一 block_id 的后续 delta **替换**已展示内容（全量快照，非增量）。
                block.Text = delta.Text;
                break;
            }
            case SessionEventFrameToolDeclared declared:
            {
                if (!_tools.TryGetValue(declared.CallId, out var tool))
                {
                    tool = new ChatToolVm { Order = _order++, CallId = declared.CallId };
                    _tools[tool.CallId] = tool;
                    Items.Add(tool);
                }
                tool.ToolName = string.IsNullOrEmpty(declared.ToolName) ? tool.ToolName : declared.ToolName;
                if (declared.Args is { ValueKind: System.Text.Json.JsonValueKind.Object } argsJson)
                {
                    tool.ArgsSummary = argsJson.GetRawText();
                }
                tool.StateText = "已声明";
                break;
            }
            case SessionEventFrameToolStateChanged state:
                if (_tools.TryGetValue(state.CallId, out var stateTool))
                {
                    stateTool.StateText = ToolStateText(state.State);
                    stateTool.IsFinished = state.State is ToolCallStateFinished;
                }
                break;
            case SessionEventFrameToolOutputDelta output:
                if (_tools.TryGetValue(output.CallId, out var outputTool))
                {
                    outputTool.AppendOutput(output.Text);
                }
                break;
            case SessionEventFrameToolFinished finished:
                if (_tools.TryGetValue(finished.CallId, out var finishedTool))
                {
                    finishedTool.StateText = "已完成";
                    finishedTool.IsFinished = true;
                }
                break;
            case SessionEventFrameInteractionRequested requested:
                UpsertApproval(requested.Interaction);
                ReevaluatePending();
                break;
            case SessionEventFrameInteractionResolved resolved:
                if (_approvals.Remove(resolved.InteractionId, out var approval))
                {
                    approval.Resolved = true;
                    Items.Remove(approval);
                }
                ReevaluatePending();
                break;
            case SessionEventFrameExecSessionStarted started:
                UpsertExec(started.Session);
                break;
            case SessionEventFrameExecSessionStateChanged execState:
                UpsertExec(execState.Session);
                break;
            case SessionEventFrameExecSessionFinished execFinished:
                UpsertExec(execFinished.Session);
                break;
            case SessionEventFrameExecSessionOutputDelta execDelta:
                if (_execs.TryGetValue(execDelta.ExecSessionId, out var exec))
                {
                    exec.ApplyDelta(execDelta.StartOffset, execDelta.Text);
                }
                break;
            case SessionEventFrameSubagentUpdated subagentUpdated:
                UpsertSubagent(subagentUpdated.Subagent);
                break;
        }
    }

    private void UpsertApproval(InteractionView interaction)
    {
        var request = interaction.Request is ContentValueInline inline ? inline.Data.Text : "";
        if (_approvals.TryGetValue(interaction.InteractionId, out var existing))
        {
            existing.RequestText = request;
            return;
        }
        var vm = new ChatApprovalVm
        {
            Order = _order++,
            InteractionId = interaction.InteractionId,
            CallId = interaction.CallId ?? interaction.InteractionId,
            Kind = interaction.Kind,
            RequestText = request,
            Choices = new ObservableCollection<InteractionChoice>(interaction.Choices ?? []),
        };
        _approvals[vm.InteractionId] = vm;
        Items.Add(vm);
    }

    private void ReevaluatePending() => HasPendingApproval = _approvals.Count > 0;

    private void UpsertExec(ExecSessionView view)
    {
        if (!_execs.TryGetValue(view.Id, out var exec))
        {
            exec = new ChatExecVm { Order = _order++, ExecSessionId = view.Id };
            _execs[view.Id] = exec;
            Items.Add(exec);
        }
        exec.Command = view.Command;
        exec.IsRunning = view.State is ExecSessionStateRunning or ExecSessionStateStopping;
        exec.StateText = view.State switch
        {
            ExecSessionStateRunning => "运行中",
            ExecSessionStateStopping => "停止中",
            _ => "已结束",
        };
    }

    private void UpsertSubagent(SubagentView view)
    {
        if (!_subagents.TryGetValue(view.ChildSessionId, out var subagent))
        {
            subagent = new ChatSubagentVm { Order = _order++, ChildSessionId = view.ChildSessionId };
            _subagents[view.ChildSessionId] = subagent;
            Items.Add(subagent);
        }
        subagent.Role = string.IsNullOrEmpty(view.Role) ? subagent.Role : view.Role!;
        subagent.StatusText = view.Status is null ? "运行中" : "已结束";
        subagent.ResultText = view.Result is ContentValueInline inline ? inline.Data.Text : subagent.ResultText;
    }

    private void UpdateToolFromView(ToolCallView view)
    {
        if (!_tools.TryGetValue(view.CallId, out var tool))
        {
            tool = new ChatToolVm { Order = _order++, CallId = view.CallId };
            _tools[view.CallId] = tool;
            // 快照合流时工具卡按 call_id 插在对应 ToolCall 占位上（若 timeline 已含）。
            Items.Add(tool);
        }
        tool.ToolName = string.IsNullOrEmpty(view.ToolName) ? tool.ToolName : view.ToolName!;
        tool.StateText = ToolStateText(view.State);
        tool.IsFinished = view.State is ToolCallStateFinished;
    }

    private static string ToolStateText(ToolCallState state) => state switch
    {
        ToolCallStateDeclared => "已声明",
        ToolCallStateAwaitingInteraction => "等待审批",
        ToolCallStateRunning => "运行中",
        ToolCallStateCancelling => "取消中",
        ToolCallStateFinished => "已完成",
        _ => state.GetType().Name,
    };

    private static string TextOf(ContentValue? content) => content switch
    {
        ContentValueInline inline => inline.Data.Text,
        _ => "",
    };
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Qaqh.Sdk.Contract;

namespace Qaqh_App;

/// <summary>聊天流条目基类（模板选择器按具体类型分流）。</summary>
public abstract class ChatItemVm : ObservableObject
{
    /// <summary>按秒递增的本地序号，用作稳定 id 与插入排序。</summary>
    public ulong Order { get; init; }
}

/// <summary>用户输入消息。</summary>
public partial class ChatUserVm : ChatItemVm
{
    [ObservableProperty]
    private string _text = "";
}

/// <summary>助手输出块（reasoning / answer），流式增长。</summary>
public partial class ChatAssistantVm : ChatItemVm
{
    public required string BlockId { get; init; }

    public bool IsReasoning { get; init; }

    [ObservableProperty]
    private string _text = "";

    /// <summary>流式期间节流重绘的脏标记（视图侧消费后复位）。</summary>
    [ObservableProperty]
    private bool _renderPending;
}

/// <summary>工具调用卡片（declared → running → finished，输出尾部增量）。</summary>
public partial class ChatToolVm : ChatItemVm
{
    public required string CallId { get; init; }

    [ObservableProperty]
    private string _toolName = "工具";

    [ObservableProperty]
    private string _stateText = "已声明";

    [ObservableProperty]
    private string _argsSummary = "";

    [ObservableProperty]
    private string _outputTail = "";

    [ObservableProperty]
    private bool _isFinished;

    public void AppendOutput(string chunk) => OutputTail = (OutputTail + chunk) switch
    {
        var s when s.Length <= 4000 => s,
        var s => s[^4000..],
    };
}

/// <summary>审批/交互卡片：ask（回答/忽略）、permission（批准/拒绝+信任目录）。
/// plan-review 交互类型已随 LTS 契约删除（SessionInteractionKind 只剩 ask|permission）。</summary>
public partial class ChatApprovalVm : ChatItemVm
{
    public required string InteractionId { get; init; }

    public required string CallId { get; init; }

    public required SessionInteractionKind Kind { get; init; }

    // 模板内 x:Bind 不能写比较表达式，用计算属性分流审批面板。
    public bool IsPermission => Kind == SessionInteractionKind.Permission;

    public bool IsAsk => Kind == SessionInteractionKind.Ask;

    [ObservableProperty]
    private string _requestText = "";

    [ObservableProperty]
    private ObservableCollection<InteractionChoice> _choices = new();

    /// <summary>permission 面板：信任此文件夹。</summary>
    [ObservableProperty]
    private bool _trustFolder;

    [ObservableProperty]
    private bool _resolved;
}

/// <summary>回合/压缩分隔线。</summary>
public sealed class ChatDividerVm : ChatItemVm
{
    public string Text { get; init; } = "";
}

/// <summary>exec 会话终端卡（命令 + 状态 + 输出尾部 + 中断/终止）。</summary>
public partial class ChatExecVm : ChatItemVm
{
    public required string ExecSessionId { get; init; }

    [ObservableProperty]
    private string _command = "";

    [ObservableProperty]
    private string _stateText = "运行中";

    [ObservableProperty]
    private bool _isRunning = true;

    [ObservableProperty]
    private string _outputTail = "";

    /// <summary>输出缓冲末端偏移（契约按 [start_offset,end_offset) 区间投递）。</summary>
    private long _bufferEnd;

    public void ApplyDelta(long startOffset, string text)
    {
        // 区间从缓冲末尾连续 → 追加；回跳（重发/乱序）→ 截断后替换。
        if (startOffset < _bufferEnd)
        {
            var keep = (int)Math.Min(startOffset, OutputTail.Length);
            OutputTail = OutputTail[..keep];
        }
        OutputTail = (OutputTail + text) switch
        {
            var s when s.Length <= 6000 => s,
            var s => s[^6000..],
        };
        _bufferEnd = startOffset + text.Length;
    }
}

/// <summary>子代理卡片（状态 + 结果摘要）。</summary>
public partial class ChatSubagentVm : ChatItemVm
{
    public required string ChildSessionId { get; init; }

    [ObservableProperty]
    private string _role = "subagent";

    [ObservableProperty]
    private string _statusText = "运行中";

    [ObservableProperty]
    private string _resultText = "";
}

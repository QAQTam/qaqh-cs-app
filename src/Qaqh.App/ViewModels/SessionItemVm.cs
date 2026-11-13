using CommunityToolkit.Mvvm.ComponentModel;
using Qaqh.Sdk.Contract;

namespace Qaqh_App;

/// <summary>侧栏会话条目。数据来自 <c>session.list</c>（SessionListEntry）+ 活动事件。</summary>
public partial class SessionItemVm : ObservableObject
{
    /// <summary>XAML 类型信息会反射构造本类，不用 required 成员。</summary>
    public string SessionId { get; init; } = "";

    [ObservableProperty]
    private string _title = "";

    /// <summary>活动状态；x:Bind 函数绑定的参数，变化即刷新字形/文案。</summary>
    [ObservableProperty]
    private SessionRunStatus _activityState = SessionRunStatus.NotRunning;

    [ObservableProperty]
    private string _model = "";

    [ObservableProperty]
    private string? _workspaceId;

    [ObservableProperty]
    private bool _archived;

    [ObservableProperty]
    private long _updatedAt;

    /// <summary>从 session.list 条目填充运行期字段。</summary>
    public void UpdateFromListEntry(long updatedAt, string model, string? workspaceId, bool archived, string? title, SessionRunStatus status)
    {
        UpdatedAt = updatedAt;
        Model = model;
        WorkspaceId = workspaceId;
        Archived = archived;
        Title = string.IsNullOrWhiteSpace(title) ? FallbackTitle() : title!;
        ActivityState = status;
    }

    private string FallbackTitle() => SessionId.Length > 8 ? $"会话 {SessionId[^8..]}" : $"会话 {SessionId}";
}

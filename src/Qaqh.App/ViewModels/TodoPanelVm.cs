using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Qaqh.Sdk.Contract;

namespace Qaqh_App;

public partial class TodoItemPanelVm : ObservableObject
{
    /// <summary>XamlTypeInfo 需要可写属性（required/init 会崩）。</summary>
    public string Id { get; set; } = "";

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _description = "";

    public string StatusGlyph => Status switch
    {
        "done" or "completed" => "\uE73E",   // checkmark
        "in_progress" or "running" => "\uE768",
        "cancelled" => "\uE711",
        _ => "\uE738",                        // pending → pause-ish circle
    };
}

/// <summary>会话 todos 面板（todo.list 查询 + todo.cancel 命令）。</summary>
public partial class TodoPanelVm : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly string _sessionId;

    public TodoPanelVm(ShellViewModel shell, string sessionId)
    {
        _shell = shell;
        _sessionId = sessionId;
    }

    public ObservableCollection<TodoItemPanelVm> Items { get; } = new();

    [ObservableProperty]
    private string _summary = "";

    public async Task RefreshAsync()
    {
        var result = await _shell.Client.QueryAsync(
            "todo.list",
            new Dictionary<string, object?> { ["session_id"] = _sessionId });
        Items.Clear();
        if (result.ValueKind != JsonValueKind.Object)
        {
            Summary = "";
            return;
        }
        var snapshot = result.Deserialize<TodoListSnapshot>(ContractJson.Options);
        if (snapshot is null)
        {
            return;
        }
        foreach (var item in snapshot.Items)
        {
            Items.Add(new TodoItemPanelVm
            {
                Id = item.Id,
                Title = item.Title,
                Status = item.Status,
                Description = item.Description,
            });
        }
        var total = snapshot.Counts.Pending + snapshot.Counts.InProgress + snapshot.Counts.Completed + snapshot.Counts.Cancelled;
        Summary = $"进行中 {snapshot.Counts.InProgress} · 完成 {snapshot.Counts.Completed} · 共 {total}";
    }

    public Task CancelAsync(string id) => _shell.Client.SendCommandAsync(
        _sessionId, new RingingCommandTodoCancel { Id = id });
}

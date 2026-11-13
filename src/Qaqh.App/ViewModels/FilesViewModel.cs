using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Qaqh.Sdk.Contract;

namespace Qaqh_App;

public partial class FileEntryVm : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _isDir;

    public string Glyph => IsDir ? "\uE8B7" : "\uE8A5"; // folder / file
}

/// <summary>文件浏览页状态：fs.list + fs.read 直连服务面（会话沙盒内）。</summary>
public partial class FilesViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public FilesViewModel(ShellViewModel shell) => _shell = shell;

    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private string _hint = "选择会话后按其工作区浏览（fs 面按会话 cwd 沙盒）";

    public ObservableCollection<FileEntryVm> Entries { get; } = new();

    /// <summary>attach 选中会话并以会话 cwd 为根打开。返回错误消息或 null。</summary>
    public async Task<string?> OpenSessionRootAsync()
    {
        var session = _shell.SelectedSession;
        if (session is null)
        {
            return "先在侧栏打开一个会话";
        }
        try
        {
            await _shell.Client.AttachAsync(session.SessionId);
            var meta = await _shell.Client.QueryAsync(
                "session.meta",
                new Dictionary<string, object?> { ["session_id"] = session.SessionId });
            var cwd = meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("cwd", out var cwdEl)
                && cwdEl.ValueKind == JsonValueKind.String
                    ? cwdEl.GetString()
                    : null;
            if (string.IsNullOrEmpty(cwd))
            {
                return "该会话未绑定工作区（cwd 为空）";
            }
            await ListAsync(cwd!);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task ListAsync(string path)
    {
        var result = await _shell.Client.QueryAsync("fs.list", new Dictionary<string, object?> { ["path"] = path });
        Entries.Clear();
        if (result.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var entry in result.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            var isDir = entry.TryGetProperty("kind", out var kindEl)
                && kindEl.GetString() is "dir" or "directory";
            Entries.Add(new FileEntryVm { Name = name, IsDir = isDir });
        }
        Path = path;
    }

    /// <summary>fs.read 读文本预览（≤64KB）；返回 null 表示读取失败或非文本。</summary>
    public async Task<string?> ReadAsync(string path)
    {
        var result = await _shell.Client.QueryAsync(
            "fs.read",
            new Dictionary<string, object?> { ["path"] = path, ["max_bytes"] = 64_000 });
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }
        return result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    }
}

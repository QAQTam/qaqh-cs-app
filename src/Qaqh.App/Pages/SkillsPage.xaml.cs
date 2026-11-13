using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Qaqh.Sdk.Contract;

namespace Qaqh_App.Pages;

public sealed class SkillRowVm
{
    public string Name { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed partial class SkillsPage : Page
{
    public ObservableCollection<SkillRowVm> Skills { get; } = new();

    public string StatusText { get; private set; } = "加载中…";

    public SkillsPage()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var result = await App.Shell.Client.QueryAsync("skills.catalog");
            Skills.Clear();
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("skills", out var skills)
                && skills.ValueKind == JsonValueKind.Array)
            {
                var entries = skills.Deserialize<SkillCatalogEntry[]>(ContractJson.Options) ?? [];
                foreach (var entry in entries)
                {
                    Skills.Add(new SkillRowVm { Name = entry.Name, Scope = entry.Scope, Description = entry.Description });
                }
                StatusText = $"共 {entries.Length} 个技能";
            }
            else
            {
                StatusText = "技能目录为空";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        Bindings.Update();
    }
}

using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qaqh.Sdk.Discovery;

namespace Qaqh_App.Pages;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        try
        {
            var raw = File.ReadAllText(DaemonLocator.DiscoveryPath());
            DiscoveryText.Text = JsonSerializer.Serialize(
                JsonSerializer.Deserialize<JsonElement>(raw),
                Qaqh.Sdk.Contract.ContractJson.Indented);
        }
        catch (Exception ex)
        {
            DiscoveryText.Text = $"无法读取发现记录：{ex.Message}";
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();
}

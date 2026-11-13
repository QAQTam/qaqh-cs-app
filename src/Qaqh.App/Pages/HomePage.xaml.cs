using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qaqh.Sdk;

namespace Qaqh_App.Pages;

public sealed partial class HomePage : Page
{
    private ShellViewModel Vm => App.Shell;

    public HomePage()
    {
        InitializeComponent();
    }

    public static bool HasError(string? error) => !string.IsNullOrEmpty(error);

    public static bool NotConnected(ConnectionKind kind) => kind != ConnectionKind.Connected;

    public static bool IsConnecting(ConnectionKind kind) => kind == ConnectionKind.Connecting;

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        await App.Shell.ConnectAsync();
    }

    private async void StopDaemon_Click(object sender, RoutedEventArgs e)
    {
        await StopAsync(idleOnly: false);
    }

    private async void StopDaemonIfIdle_Click(object sender, RoutedEventArgs e)
    {
        await StopAsync(idleOnly: true);
    }

    private async Task StopAsync(bool idleOnly)
    {
        var status = await App.Shell.StopDaemonAsync(idleOnly);
        // StopStatus 定义在 RingingClient 嵌套。
        var message = status switch
        {
            RingingClient.StopStatus.Stopping => "daemon 正在停止。",
            RingingClient.StopStatus.Busy => "daemon 正忙，未停止。",
            _ => "daemon 不支持该停止指令。",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "停止 daemon",
            Content = message,
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }
}

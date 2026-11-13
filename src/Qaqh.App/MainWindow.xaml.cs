using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Qaqh_App.Pages;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Qaqh_App;

public sealed partial class MainWindow : Window
{
    public ShellViewModel Vm { get; }

    private bool _navSelectionGuard;

    public MainWindow()
    {
        Vm = App.Shell; // 单一实例：HomePage 等页面与壳层共享同一连接状态
        Vm.SessionActivated += OnSessionActivated;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ResizeToDip(1200, 800);

        NavFrame.Navigated += NavFrame_Navigated;
        NavFrame.Navigate(typeof(HomePage));
        _ = Vm.ConnectAsync();
    }

    private void ResizeToDip(double widthDip, double heightDip)
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    // ── 导航 ──────────────────────────────────────────────────────────

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navSelectionGuard)
        {
            return;
        }
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
            return;
        }
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }
        switch (item.Tag)
        {
            case "home":
                NavFrame.Navigate(typeof(HomePage));
                break;
            case "skills":
                NavFrame.Navigate(typeof(SkillsPage));
                break;
            case "devices":
                NavFrame.Navigate(typeof(DevicesPage));
                break;
            case "files":
                NavFrame.Navigate(typeof(FilesPage));
                break;
            case "diagnostics":
                NavFrame.Navigate(typeof(DiagnosticsPage));
                break;
            default:
                throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
        }
    }

    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // 会话页不属于任何导航项：清空 NavView 选中态（防重入）。
        if (e.SourcePageType == typeof(ChatPage))
        {
            _navSelectionGuard = true;
            try
            {
                NavView.SelectedItem = null;
            }
            finally
            {
                _navSelectionGuard = false;
            }
        }
    }

    private void OnSessionActivated(SessionItemVm session)
    {
        NavFrame.Navigate(typeof(ChatPage), session.SessionId);
    }

    // ── 会话区 ────────────────────────────────────────────────────────

    private void WorkspacePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedItem 的 TwoWay 绑定已更新 Vm.SelectedWorkspace；此处只触发重导航无必要。
    }

    private async void NewWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "新建工作区",
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var box = new TextBox { PlaceholderText = "绝对路径，如 D:\\workspace" };
        dialog.Content = box;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim().Length == 0)
        {
            return;
        }
        try
        {
            await Vm.CreateWorkspaceAsync(box.Text.Trim());
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("创建工作区失败", ex);
        }
    }

    private async void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Vm.CreateSessionAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("新建会话失败", ex);
        }
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionItemVm session)
        {
            return;
        }
        try
        {
            await Vm.OpenSessionAsync(session);
        }
        catch (Exception ex)
        {
            SessionList.SelectedItem = null;
            await ShowErrorAsync("打开会话失败", ex);
        }
    }

    private async void SessionRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuFlyoutHost(sender) is not SessionItemVm session)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "重命名会话",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var box = new TextBox { PlaceholderText = "新标题", Text = session.Title };
        dialog.Content = box;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0)
        {
            try
            {
                await Vm.RenameSessionAsync(session, box.Text.Trim());
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("重命名失败", ex);
            }
        }
    }

    private async void SessionArchive_Click(object sender, RoutedEventArgs e)
    {
        if (MenuFlyoutHost(sender) is not SessionItemVm session)
        {
            return;
        }
        try
        {
            await Vm.ArchiveSessionAsync(session);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("归档失败", ex);
        }
    }

    private async void SessionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuFlyoutHost(sender) is not SessionItemVm session)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "删除会话",
            Content = $"确定删除「{session.Title}」？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await Vm.DeleteSessionAsync(session);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("删除失败", ex);
            }
        }
    }

    private static SessionItemVm? MenuFlyoutHost(object sender) =>
        (sender as FrameworkElement)?.DataContext as SessionItemVm;

    private async void ConnectionRetryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Vm.ConnectAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("重连失败", ex);
        }
    }

    private async Task ShowErrorAsync(string title, Exception ex)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = ex.Message,
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }
}

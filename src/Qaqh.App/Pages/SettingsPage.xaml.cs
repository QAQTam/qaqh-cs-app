using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qaqh_App;

namespace Qaqh_App.Pages;

public sealed partial class SettingsPage : Page
{
    private SettingsViewModel Vm { get; }

    public SettingsPage()
    {
        Vm = new SettingsViewModel(App.Shell, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await Vm.LoadAsync();
            Bindings.Update();
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"加载失败：{ex.Message}";
        }
    }

    private async void SiwcSignIn_Click(object sender, RoutedEventArgs e) => await Vm.Accounts.SignInSiwcAsync();

    private async void SiwcSignOut_Click(object sender, RoutedEventArgs e) => await Vm.Accounts.SignOutSiwcAsync();

    private async void DsSignIn_Click(object sender, RoutedEventArgs e) => await Vm.Accounts.SignInDsAsync();

    private async void DsSignOut_Click(object sender, RoutedEventArgs e) => await Vm.Accounts.SignOutDsAsync();

    private async void SaveDefault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Vm.SaveDefaultSelectionAsync();
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"设置默认模型失败：{ex.Message}";
        }
    }

    private async void PermissionLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm.PermissionLevelIndex < 0 || Vm.SuppressPermissionEvents)
        {
            return;
        }
        try
        {
            await Vm.SetPermissionLevelAsync(Vm.PermissionLevelIndex + 1);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"设置失败：{ex.Message}";
        }
    }

    private async void ProviderUpsert_Click(object sender, RoutedEventArgs e)
    {
        var id = ProviderIdBox.Text.Trim();
        if (id.Length == 0)
        {
            Vm.StatusText = "provider ID 不能为空";
            return;
        }
        try
        {
            await Vm.UpsertProviderAsync(
                id,
                ProviderNameBox.Text.Trim(),
                ProviderUrlBox.Text.Trim(),
                wire: "chat",
                credential: "api_key",
                apiKey: ProviderKeyBox.Password.Length > 0 ? ProviderKeyBox.Password : null);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"保存失败：{ex.Message}";
        }
    }

    private async void ProviderDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "删除 Provider",
            Content = $"确定删除 {id}？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            await Vm.DeleteProviderAsync(id);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"删除失败：{ex.Message}";
        }
    }

    private async void ProviderRefresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Vm.RefreshProvidersAsync();
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"刷新失败：{ex.Message}";
        }
    }
}

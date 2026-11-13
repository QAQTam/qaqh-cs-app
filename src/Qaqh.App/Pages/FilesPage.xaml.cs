using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Qaqh_App;
using Windows.System;

namespace Qaqh_App.Pages;

public sealed partial class FilesPage : Page
{
    private FilesViewModel Vm { get; }

    public FilesPage()
    {
        Vm = new FilesViewModel(App.Shell);
        InitializeComponent();
        _ = OpenSessionRootAsync();
    }

    private async Task OpenSessionRootAsync()
    {
        var error = await Vm.OpenSessionRootAsync();
        if (error is not null)
        {
            Vm.Hint = error;
        }
        Bindings.Update();
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await OpenAsync(Vm.Path.Trim());

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await OpenAsync(Vm.Path.Trim());
        }
    }

    private async Task OpenAsync(string path)
    {
        if (path.Length == 0)
        {
            return;
        }
        try
        {
            await Vm.ListAsync(path);
            PreviewText.Text = "";
            Vm.Hint = "当前目录：" + path;
        }
        catch (Exception ex)
        {
            PreviewText.Text = $"读取目录失败：{ex.Message}";
        }
        Bindings.Update();
    }

    private async void EntryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FileEntryVm entry)
        {
            return;
        }
        var baseDir = Vm.Path.TrimEnd('/');
        var full = $"{baseDir}/{entry.Name}";
        if (entry.IsDir)
        {
            await OpenAsync(full);
        }
        else
        {
            try
            {
                PreviewText.Text = await Vm.ReadAsync(full) ?? "（二进制或读取失败）";
            }
            catch (Exception ex)
            {
                PreviewText.Text = $"读取失败：{ex.Message}";
            }
        }
    }
}

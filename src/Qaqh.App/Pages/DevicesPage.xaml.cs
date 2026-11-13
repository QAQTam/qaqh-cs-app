using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Qaqh_App;
using Qaqh.Sdk.Discovery;

namespace Qaqh_App.Pages;

public sealed class DeviceRowVm
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Scope { get; set; } = "";
}

public sealed partial class DevicesPage : Page
{
    public ObservableCollection<DeviceRowVm> Devices { get; } = new();

    public string StatusText { get; private set; } = "";

    /// <summary>配对二维码（PNG 内存图）；null = 未签发。</summary>
    public BitmapImage? QrImage { get; private set; }

    /// <summary>二维码下的手工录入文本（base_url + token）。</summary>
    public string PairingText { get; private set; } = "";

    public bool HasQr => QrImage is not null;

    public DevicesPage()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var devices = await App.Shell.Client.ListDevicesAsync();
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(new DeviceRowVm { DeviceId = device.DeviceId, Name = device.Name, Scope = device.Scope });
            }
            StatusText = $"已配对 {devices.Count} 台设备";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        Bindings.Update();
    }

    private async void IssuePairing_Click(object sender, RoutedEventArgs e)
    {
        var scope = (ScopePicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "interact";
        try
        {
            var token = await App.Shell.Client.IssuePairingTokenAsync(scope, "paired-device", "windows");
            // 配对载荷：手机端消费 base_url + token（+ 可选 TLS 指纹）。
            var discovery = DaemonLocator.TryRead();
            var baseUrl = discovery?.LanEndpoint ?? discovery?.BaseUrl() ?? "";
            var payload = $"qaqh://pair?base={Uri.EscapeDataString(baseUrl)}&token={token.PairingToken}";
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 6);
            var image = new BitmapImage();
            image.SetSource(new MemoryStream(png).AsRandomAccessStream());
            QrImage = image;
            PairingText = $"base: {baseUrl}\ntoken: {token.PairingToken}\n有效期 {token.ExpiresInMs / 1000} 秒 · 档位 {scope}";
            StatusText = "配对令牌已签发（一次性）";
        }
        catch (Exception ex)
        {
            StatusText = $"签发失败：{ex.Message}";
            QrImage = null;
            PairingText = "";
        }
        Bindings.Update();
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string deviceId)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "吊销设备",
            Content = $"确定吊销 {deviceId}？",
            PrimaryButtonText = "吊销",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            await App.Shell.Client.RevokeDeviceAsync(deviceId);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"吊销失败：{ex.Message}";
            Bindings.Update();
        }
    }
}

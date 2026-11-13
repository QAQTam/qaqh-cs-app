using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Qaqh.Sdk;
using Qaqh.Sdk.Contract;
using SdkException = Qaqh.Sdk.SdkException;

namespace Qaqh_App;

/// <summary>
/// 账号面（siwc = Sign in with ChatGPT；dsaccount = DeepSeek 账号）。
/// 状态查询 + OAuth 登录（authorizeUrl 交浏览器）+ 状态轮询。
/// </summary>
public partial class AccountsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public AccountsViewModel(ShellViewModel shell) => _shell = shell;

    // siwc
    [ObservableProperty]
    private string _siwcStatus = "未查询";

    [ObservableProperty]
    private string _siwcLabel = "";

    // dsaccount
    [ObservableProperty]
    private string _dsStatus = "未查询";

    [ObservableProperty]
    private string _dsAccount = "";

    [ObservableProperty]
    private string _signingMessage = "";

    private bool _busy;

    public async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }
        try
        {
            var siwc = await _shell.Client.ServiceAsync("siwc.status");
            var siwcParsed = siwc.Deserialize<SiwcStatusResult>(ContractJson.Options);
            SiwcStatus = siwcParsed is null ? "unknown" : siwcParsed.Status.ToString();
            SiwcLabel = siwcParsed?.Label ?? "";
        }
        catch (SdkException ex)
        {
            SiwcStatus = $"查询失败：{ex.Detail ?? ex.Message}";
        }
        try
        {
            var ds = await _shell.Client.ServiceAsync("dsaccount.status");
            var dsParsed = ds.Deserialize<DsAccountStatusResult>(ContractJson.Options);
            DsStatus = dsParsed?.SignedIn == true ? "已登录" : "未登录";
            DsAccount = dsParsed?.Account is { } account ? $"{account.Label} ({account.UserId})" : "";
        }
        catch (SdkException ex)
        {
            DsStatus = $"查询失败：{ex.Detail ?? ex.Message}";
        }
    }

    private static string JsonSegment(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? string.Join(", ", element.EnumerateObject().Select(p => $"{p.Name}={p.Value}"))
            : element.ToString() ?? "";

    /// <summary>siwc 登录：start → 打开 authorizeUrl → 轮询 status 到终态。</summary>
    public async Task SignInSiwcAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        SigningMessage = "正在发起登录…";
        try
        {
            var start = await _shell.Client.ServiceAsync("siwc.sign_in.start");
            var parsed = start.Deserialize<SiwcSignInStartResult>(ContractJson.Options);
            if (parsed?.AuthorizeUrl is not { Length: > 0 } url)
            {
                SigningMessage = "start 未返回授权链接";
                return;
            }
            SigningMessage = "请在浏览器中完成登录…";
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000);
                var status = await _shell.Client.ServiceAsync("siwc.sign_in.status");
                switch (status.Deserialize<SiwcSignInStatusResult>(ContractJson.Options))
                {
                    case SiwcSignInStatusResultCompleted:
                        SigningMessage = "登录成功";
                        await RefreshAsync();
                        return;
                    case SiwcSignInStatusResultFailed failed:
                        SigningMessage = $"登录失败：{failed.Error}";
                        return;
                }
            }
            SigningMessage = "登录等待超时";
        }
        catch (SdkException ex)
        {
            SigningMessage = $"登录失败：{ex.Detail ?? ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task SignOutSiwcAsync()
    {
        try
        {
            await _shell.Client.ServiceAsync("siwc.sign_out");
            SigningMessage = "已登出 ChatGPT 账号";
            await RefreshAsync();
        }
        catch (SdkException ex)
        {
            SigningMessage = $"登出失败：{ex.Detail ?? ex.Message}";
        }
    }

    /// <summary>dsaccount 登录：start → authorizeUrl → 轮询 status.flow.state。</summary>
    public async Task SignInDsAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        SigningMessage = "正在发起 DeepSeek 登录…";
        try
        {
            var start = await _shell.Client.ServiceAsync("dsaccount.sign_in.start");
            var parsed = start.Deserialize<DsAccountSignInStartResult>(ContractJson.Options);
            if (parsed?.AuthorizeUrl is not { Length: > 0 } url)
            {
                SigningMessage = "start 未返回授权链接";
                return;
            }
            SigningMessage = "请在浏览器中完成登录…";
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000);
                var status = await _shell.Client.ServiceAsync("dsaccount.status");
                if (status.TryGetProperty("flow", out var flow)
                    && flow.TryGetProperty("state", out var stateEl)
                    && stateEl.GetString() is { } state)
                {
                    SigningMessage = $"登录流程：{state}";
                    if (state is "completed" or "done")
                    {
                        SigningMessage = "登录成功";
                        await RefreshAsync();
                        return;
                    }
                    if (state is "failed" or "error")
                    {
                        SigningMessage = "登录失败";
                        return;
                    }
                }
                if (status.TryGetProperty("signedIn", out var signed) && signed.ValueKind == JsonValueKind.True)
                {
                    SigningMessage = "登录成功";
                    await RefreshAsync();
                    return;
                }
            }
            SigningMessage = "登录等待超时";
        }
        catch (SdkException ex)
        {
            SigningMessage = $"登录失败：{ex.Detail ?? ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task SignOutDsAsync()
    {
        try
        {
            await _shell.Client.ServiceAsync("dsaccount.sign_out");
            SigningMessage = "已登出 DeepSeek 账号";
            await RefreshAsync();
        }
        catch (SdkException ex)
        {
            SigningMessage = $"登出失败：{ex.Detail ?? ex.Message}";
        }
    }
}

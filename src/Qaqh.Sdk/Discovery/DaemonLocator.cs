using System.Net.Sockets;
using System.Text.Json;

namespace Qaqh.Sdk.Discovery;

/// <summary>
/// 发现文件的读取与判活。判据与 qaqh-client 的 <c>discovery_is_live</c> 同构：
/// pid 存活 <b>且</b> 端点真的在监听——两道判据缺一不可（pid 复用与
/// 「daemon 活着但没在听」都靠端口探测兜住）；判不了就放行，宁可放行也不
/// 对健康 daemon 重复拉起。
/// </summary>
public static class DaemonLocator
{
    private static readonly TimeSpan EndpointProbeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>数据根：QAQH_DATA_DIR 覆盖，否则 Windows 为 <c>%USERPROFILE%\.qaqh</c>。</summary>
    public static string DataDir()
    {
        var configured = Environment.GetEnvironmentVariable("QAQH_DATA_DIR");
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".qaqh");
    }

    public static string DiscoveryPath() => Path.Combine(DataDir(), "daemon.json");

    public static string LockPath() => Path.Combine(DataDir(), "daemon.lock");

    public static DaemonDiscovery? TryRead()
    {
        try
        {
            var raw = File.ReadAllText(DiscoveryPath());
            return JsonSerializer.Deserialize<DaemonDiscovery>(raw, Contract.ContractJson.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsLive(DaemonDiscovery discovery) =>
        PidIsRunning(discovery.Pid) && EndpointReachable(discovery).GetAwaiter().GetResult();

    public static bool PidIsRunning(uint pid)
    {
        if (pid == 0)
        {
            return false;
        }
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>本地回环 TCP 探测（≤300ms）。仅对 http:// 端点检查；其余一律放行。</summary>
    public static async Task<bool> EndpointReachable(DaemonDiscovery discovery)
    {
        string baseUrl;
        try
        {
            baseUrl = discovery.BaseUrl();
        }
        catch (SdkException)
        {
            return true;
        }
        if (!baseUrl.StartsWith("http://", StringComparison.Ordinal))
        {
            return true;
        }
        var authority = baseUrl["http://".Length..].Split('/')[0];
        if (authority.Length == 0)
        {
            return true;
        }
        var hostPart = authority;
        var port = 80;
        var colon = authority.LastIndexOf(':');
        if (colon > 0 && int.TryParse(authority[(colon + 1)..], out var parsed))
        {
            hostPart = authority[..colon];
            port = parsed;
        }
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(hostPart, port);
            var finished = await Task.WhenAny(connect, Task.Delay(EndpointProbeTimeout)).ConfigureAwait(false);
            return finished == connect && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>daemon.lock 持有者存活 = 有实例正在启动/运行（冷启动期 discovery 尚未发布）。</summary>
    public static bool LockHolderAlive()
    {
        try
        {
            var value = File.ReadAllText(LockPath()).Trim();
            return uint.TryParse(value, out var pid) && PidIsRunning(pid);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

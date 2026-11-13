namespace Qaqh.Sdk;

/// <summary>远端 daemon 直连目标（与本地发现互斥）。</summary>
public sealed record RemoteEndpoint(string BaseUrl, string Token, string? TlsFingerprint);

public sealed class RingingClientOptions
{
    /// <summary>壳层逻辑身份，跨租约协商保持稳定。</summary>
    public string ClientInstanceId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>open 自报的客户端种类（presence/诊断）。</summary>
    public string ClientKind { get; init; } = "Desktop";

    /// <summary>无存活发现记录时是否拉起 <c>qaqh-daemon run</c>。</summary>
    public bool LaunchDaemonIfMissing { get; init; }

    /// <summary>显式 daemon 可执行文件路径；null 走候选序解析。</summary>
    public string? DaemonPath { get; init; }

    /// <summary>等待 daemon 发布发现记录的最长时间。</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>设置后跳过本地发现/spawn，直连该端点。</summary>
    public RemoteEndpoint? Remote { get; init; }
}

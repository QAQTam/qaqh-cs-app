using System.Text.Json.Serialization;

namespace Qaqh.Sdk.Discovery;

/// <summary>
/// daemon 发布在 <c>&lt;data_dir&gt;/daemon.json</c> 的发现记录，
/// 与 qaqh-types 的 <c>DdaemonDiscovery</c> 同形（snake_case wire）。
/// daemon 每次启动换端口与随机 token，凭据不可烘死。
/// </summary>
public sealed record DaemonDiscovery
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = "";

    [JsonPropertyName("token")]
    public string Token { get; init; } = "";

    [JsonPropertyName("pid")]
    public uint Pid { get; init; }

    [JsonPropertyName("server_epoch")]
    public string ServerEpoch { get; init; } = "";

    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("daemon_version")]
    public string DaemonVersion { get; init; } = "";

    [JsonPropertyName("build_id")]
    public string BuildId { get; init; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; init; } = "";

    [JsonPropertyName("executable")]
    public string Executable { get; init; } = "";

    [JsonPropertyName("lan_endpoint")]
    public string? LanEndpoint { get; init; }

    [JsonPropertyName("tls_fingerprint")]
    public string? TlsFingerprint { get; init; }

    /// <summary>endpoint 去掉路径、保留 scheme://host:port（对齐 DiscoveryExt::base_url）。</summary>
    public string BaseUrl()
    {
        string rest;
        string scheme;
        if (Endpoint.StartsWith("http://", StringComparison.Ordinal))
        {
            rest = Endpoint["http://".Length..];
            scheme = "http";
        }
        else if (Endpoint.StartsWith("https://", StringComparison.Ordinal))
        {
            rest = Endpoint["https://".Length..];
            scheme = "https";
        }
        else
        {
            throw SdkException.Discovery($"unexpected endpoint: {Endpoint}");
        }
        var host = rest.Split('/')[0];
        if (host.Length == 0)
        {
            throw SdkException.Discovery("endpoint has no host");
        }
        return $"{scheme}://{host}";
    }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Qaqh.Sdk.Transport;

/// <summary>
/// 自签证书指纹 pin（sha256:&lt;hex&gt; 或裸 hex，整张 DER 的 sha256），语义与
/// qaqh-client 的 <c>tls_pin</c> 一致。LAN 远端模式专用；回环明文 http 不经过这里。
/// </summary>
public static class TlsFingerprint
{
    /// <summary>解析指纹字符串为 32 字节；非法输入返回 null（裸 hex 或 sha256: 前缀均可）。</summary>
    public static byte[]? Parse(string fingerprint)
    {
        var hex = fingerprint.Trim();
        if (hex.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex["sha256:".Length..];
        }
        if (hex.Length != 64)
        {
            return null;
        }
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool Matches(X509Certificate2 certificate, byte[] expected)
    {
        var hash = SHA256.HashData(certificate.Export(X509ContentType.Cert));
        return hash.AsSpan().SequenceEqual(expected);
    }
}

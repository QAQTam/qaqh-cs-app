namespace Qaqh.Sdk;

/// <summary>SDK 错误面，与 qaqh-client 的 <c>ClientError</c> 逐变体对齐。</summary>
public class SdkException : Exception
{
    public SdkErrorKind Kind { get; }

    /// <summary>HTTP 状态（<see cref="SdkErrorKind.Http"/> / <see cref="SdkErrorKind.Api"/>）。</summary>
    public int Status { get; }

    /// <summary>失败的请求路径（Http）或 daemon 的结构化错误码（Api，如 <c>lease_required</c>）。</summary>
    public string? Detail { get; }

    public SdkException(SdkErrorKind kind, string message, int status = 0, string? detail = null)
        : base(message)
    {
        Kind = kind;
        Status = status;
        Detail = detail;
    }

    public static SdkException Discovery(string message) => new(SdkErrorKind.Discovery, message);

    public static SdkException Negotiation(string message) => new(SdkErrorKind.Negotiation, message);

    public static SdkException Transport(string message) => new(SdkErrorKind.Transport, message);

    public static SdkException Protocol(string message) => new(SdkErrorKind.Protocol, message);

    public static SdkException Http(int status, string path) =>
        new(SdkErrorKind.Http, $"HTTP {status} on {path}", status, path);

    /// <summary>非 2xx 响应：daemon 的 <c>{"code","message"}</c> 体优先，解析不出退回 Http。</summary>
    public static async Task<SdkException> FromResponseAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken cancellationToken = default)
    {
        var status = (int)response.StatusCode;
        string body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // 读不出 body 就按 Http 处理，绝不假装成功。
        }

        try
        {
            var wire = System.Text.Json.JsonSerializer.Deserialize<ErrorWire>(body, Contract.ContractJson.Options);
            if (wire?.Code is { Length: > 0 } code)
            {
                return new SdkException(SdkErrorKind.Api, wire.Message.Length > 0 ? wire.Message : body, status, code);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return Http(status, path);
    }

    private sealed record ErrorWire(
        [property: System.Text.Json.Serialization.JsonPropertyName("code")] string? Code,
        [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message);

    /// <summary>
    /// 会话尚未物化（canonical 目录/快照还没落盘）——流侧按瞬态短退避处理，
    /// 与 qaqh-client 的 <c>is_session_not_ready</c> 同判据。
    /// </summary>
    public bool IsSessionNotReady =>
        Kind == SdkErrorKind.Http && Status is 404 or 409
        || Kind == SdkErrorKind.Api && (Status is 404 or 409 || Detail is "session_not_found" or "snapshot_missing");
}

public enum SdkErrorKind
{
    Discovery,
    Negotiation,
    Transport,
    Protocol,
    Http,
    Api,
}

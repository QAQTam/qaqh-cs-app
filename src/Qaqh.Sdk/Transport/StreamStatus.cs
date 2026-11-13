namespace Qaqh.Sdk.Transport;

/// <summary>单条会话流的状态迁移（扁平 record 层级，模式匹配消费）。</summary>
public abstract record StreamStatus
{
    public sealed record Connecting : StreamStatus;

    public sealed record Open(string ServerEpoch, string? Cursor) : StreamStatus;

    /// <summary>Reason 与 qaqh-ffi 的投影一致：<c>lagged:&lt;n&gt;</c> / <c>stream_terminated:&lt;code&gt;</c>。</summary>
    public sealed record Reconnecting(int RetryMs, string? Reason, string? LastCursor) : StreamStatus;

    public sealed record Closed(string Reason) : StreamStatus;
}

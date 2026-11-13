namespace Qaqh.Sdk.Transport;

/// <summary>一帧 SSE（<c>id:</c> / <c>event:</c> / <c>data:</c> 字段块，空行定界）。</summary>
public sealed record SseFrame
{
    public string Id { get; init; } = "";
    public string EventType { get; init; } = "";
    public string Data { get; init; } = "";
}

/// <summary>
/// 游标式 SSE 帧解码器，语义与 qaqh-client 的 <c>SseDecoder</c> 逐条对齐：
/// 按 <c>\n</c> 切行；空行定界；<c>:</c> 注释行（daemon 的 15s keepalive）不产出帧；
/// 多 <c>data:</c> 行以单个 <c>\n</c> 连接；非法 UTF-8 行跳过绝不 lossy；
/// 流首 BOM 剥离一次；EOF 不冲刷残帧（上层报流结束）。
/// </summary>
public sealed class SseDecoder
{
    private readonly System.IO.MemoryStream _buf = new();
    private int _consumed;
    private SseFrame? _pending;
    private bool _bomChecked;

    public void Push(ReadOnlySpan<byte> chunk)
    {
        Compress();
        _buf.Write(chunk);
    }

    /// <summary>取下一完整帧；<c>null</c> = 尚无完整帧，需等更多数据。</summary>
    public SseFrame? NextFrame()
    {
        if (!_bomChecked)
        {
            var avail = Span(_consumed, (int)_buf.Length - _consumed);
            if (avail.Length >= 3)
            {
                if (avail[0] == 0xEF && avail[1] == 0xBB && avail[2] == 0xBF)
                {
                    _consumed += 3;
                }
                _bomChecked = true;
            }
            else
            {
                return null;
            }
        }

        while (true)
        {
            int rel = IndexOfNewline(_consumed, (int)_buf.Length - _consumed);
            if (rel < 0)
            {
                return null;
            }
            int end = _consumed + rel;
            int length = end - _consumed;
            string line;
            try
            {
                // 严格 UTF-8；\r\n 与行尾空白一并去除（Rust 侧 trim_end 等价）。
                line = StrictUtf8.GetString(_buf.GetBuffer(), _consumed, length).TrimEnd();
            }
            catch (System.Text.DecoderFallbackException)
            {
                _consumed = end + 1;
                continue;
            }
            _consumed = end + 1;

            if (line.Length == 0)
            {
                var frame = _pending;
                _pending = null;
                if (frame is not null)
                {
                    return frame;
                }
                continue;
            }
            if (line[0] == ':')
            {
                continue;
            }

            _pending ??= new SseFrame();
            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                _pending = _pending with { Id = line[3..].Trim() };
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                _pending = _pending with { EventType = line[6..].Trim() };
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = line[5..].Trim();
                _pending = _pending with { Data = _pending.Data.Length == 0 ? data : _pending.Data + "\n" + data };
            }
        }
    }

    private void Compress()
    {
        if (_consumed <= 0)
        {
            return;
        }
        var rest = Span(_consumed, (int)_buf.Length - _consumed).ToArray();
        _buf.SetLength(0);
        _buf.Write(rest);
        _consumed = 0;
    }

    private Span<byte> Span(int start, int length) =>
        _buf.Length == 0 ? Span<byte>.Empty : _buf.GetBuffer().AsSpan(start, length);

    private int IndexOfNewline(int start, int count)
    {
        var span = Span(start, count);
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static readonly System.Text.Encoding StrictUtf8 =
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

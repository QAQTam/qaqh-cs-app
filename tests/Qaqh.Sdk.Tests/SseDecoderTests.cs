using System.Text;
using Qaqh.Sdk.Transport;
using Xunit;

namespace Qaqh.Sdk.Tests;

/// <summary>SSE 解码器语义锁：与 qaqh-client 的 SseDecoder 逐条对齐。</summary>
public class SseDecoderTests
{
    [Fact]
    public void ParsesIdEventDataFrame()
    {
        var decoder = new SseDecoder();
        decoder.Push("id: epoch-1:conversation:7\nevent: turn_started\ndata: {\"x\":1}\n\n"u8);
        var frame = decoder.NextFrame();
        Assert.NotNull(frame);
        Assert.Equal("epoch-1:conversation:7", frame.Id);
        Assert.Equal("turn_started", frame.EventType);
        Assert.Equal("{\"x\":1}", frame.Data);
        Assert.Null(decoder.NextFrame());
    }

    [Fact]
    public void FrameSplitAcrossChunksIsReassembled()
    {
        var decoder = new SseDecoder();
        decoder.Push("id: e:tool:1\nevent: tool_star"u8);
        Assert.Null(decoder.NextFrame());
        decoder.Push("ted\ndata: {}\n\n"u8);
        var frame = decoder.NextFrame();
        Assert.NotNull(frame);
        Assert.Equal("e:tool:1", frame.Id);
        Assert.Equal("tool_started", frame.EventType);
        Assert.Equal("{}", frame.Data);
    }

    [Fact]
    public void Utf8CharSplitAcrossChunksIsNotCorrupted()
    {
        var decoder = new SseDecoder();
        // "中" = E4 B8 AD，切到两个 push（必须用原始字节，不能用 \x 字符转义）。
        decoder.Push(new byte[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a', (byte)':', (byte)' ', (byte)'{', (byte)'"', (byte)'t', (byte)'"', (byte)':', (byte)'"', 0xE4, 0xB8 });
        Assert.Null(decoder.NextFrame());
        decoder.Push(new byte[] { 0xAD, (byte)'"', (byte)'}', (byte)'\n', (byte)'\n' });
        var frame = decoder.NextFrame();
        Assert.NotNull(frame);
        Assert.Equal("{\"t\":\"中\"}", frame.Data);
    }

    [Fact]
    public void KeepaliveCommentFramesEmitNothing()
    {
        var decoder = new SseDecoder();
        decoder.Push(": keepalive\n\n"u8);
        Assert.Null(decoder.NextFrame());
    }

    [Fact]
    public void MultipleDataLinesAggregate()
    {
        var decoder = new SseDecoder();
        decoder.Push("data: first\n"u8);
        Assert.Null(decoder.NextFrame());
        decoder.Push("data: second\n\n"u8);
        Assert.Equal("first\nsecond", decoder.NextFrame()!.Data);
    }

    [Fact]
    public void CrlfLineEndingsAreHandled()
    {
        var decoder = new SseDecoder();
        decoder.Push("data: {\"a\":1}\r\n\r\n"u8);
        Assert.Equal("{\"a\":1}", decoder.NextFrame()!.Data);
    }

    [Fact]
    public void LeadingBomIsStrippedAndFirstFrameCursorSurvives()
    {
        var decoder = new SseDecoder();
        decoder.Push(Encoding.UTF8.GetBytes("\uFEFFid: epoch-1:conversation:7\nevent: turn_started\ndata: {\"x\":1}\n\n"));
        var frame = decoder.NextFrame();
        Assert.NotNull(frame);
        Assert.Equal("epoch-1:conversation:7", frame.Id);
    }

    [Fact]
    public void MultipleFramesInOneChunk()
    {
        var decoder = new SseDecoder();
        decoder.Push("data: {\"a\":1}\n\n"u8);
        decoder.Push("data: {\"b\":2}\n\n"u8);
        Assert.Equal("{\"a\":1}", decoder.NextFrame()!.Data);
        Assert.Equal("{\"b\":2}", decoder.NextFrame()!.Data);
        Assert.Null(decoder.NextFrame());
    }
}

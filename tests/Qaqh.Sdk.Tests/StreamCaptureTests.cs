using System.Text.Json;
using Qaqh.Sdk;
using Qaqh.Sdk.Contract;
using Qaqh.Sdk.Discovery;
using Xunit;
using Xunit.Abstractions;

namespace Qaqh.Sdk.Tests;

/// <summary>
/// 调试探针：attach 最新会话 → bootstrap → 原始订阅流 → 打印每帧
/// (seq, type, 关键载荷)，定位聊天内容重复发生在 daemon 端还是客户端。
/// </summary>
public class StreamCaptureTests
{
    private readonly ITestOutputHelper _output;

    public StreamCaptureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task CaptureRawEventFrames()
    {
        var discovery = DaemonLocator.TryRead();
        if (discovery is null || !DaemonLocator.PidIsRunning(discovery.Pid))
        {
            _output.WriteLine("no live daemon; skipping");
            return;
        }

        var client = new RingingClient(new RingingClientOptions
        {
            ClientInstanceId = "qaqh-cs-probe",
            ClientKind = "Desktop",
            LaunchDaemonIfMissing = false,
        });
        try
        {
            await client.ConnectAsync();
            var sessions = await client.QueryAsync("session.list");
            var first = sessions.EnumerateArray().FirstOrDefault();
            var sessionId = first.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
            if (sessionId is null)
            {
                _output.WriteLine("no sessions");
                return;
            }
            _output.WriteLine($"session: {sessionId}");

            await client.AttachAsync(sessionId);
            var bootstrap = await client.BootstrapAsync(sessionId);
            _output.WriteLine($"bootstrap seq={bootstrap.State.Seq} timeline={bootstrap.State.Timeline.Length}");

            client.V2Event += (id, frame) =>
            {
                var payload = frame switch
                {
                    SessionEventFrameMessageDelta m => $"delta block={m.BlockId} text={JsonSerializer.Serialize(m.Text)[..Math.Min(40, JsonSerializer.Serialize(m.Text).Length)]}",
                    SessionEventFrameTimelineAppended a => $"timeline kind={a.Entry.GetType().Name}",
                    SessionEventFrameTurnStarted => "turn_started",
                    SessionEventFrameTurnEnded => "turn_ended",
                    SessionEventFrameActivityChanged act => $"activity={act.Activity}",
                    _ => frame.GetType().Name.Replace("SessionEventFrame", ""),
                };
                _output.WriteLine($"[{frame.Seq}] {payload}");
            };
            client.ActivateStream(sessionId);
            await Task.Delay(1000);
            _output.WriteLine("=== probe subscribed; sending message via conversation.send_message ===");

            await client.SendCommandAsync(sessionId, new RingingCommandConversationSendMessage { Text = "探针：请回复一句话" });
            await Task.Delay(8000);
        }
        finally
        {
            client.Close();
        }
    }
}

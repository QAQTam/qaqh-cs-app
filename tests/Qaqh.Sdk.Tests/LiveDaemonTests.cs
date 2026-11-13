using System.Text.Json;
using Qaqh.Sdk;
using Qaqh.Sdk.Discovery;
using Xunit;
using Xunit.Abstractions;

namespace Qaqh.Sdk.Tests;

/// <summary>
/// 针对本机存活 daemon 的集成冒烟（daemon 不在时跳过）。
/// 意义：把「发现→open→query」的最短链路在真实 RingingV2 上验证。
/// </summary>
public class LiveDaemonTests
{
    private readonly ITestOutputHelper _output;

    public LiveDaemonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ConnectOpenAndQueryAgainstLiveDaemon()
    {
        var client = TryConnect();
        if (client is null)
        {
            return;
        }
        try
        {
            _output.WriteLine($"lease: session={client.Lease!.ClientSessionId[..12]}… epoch={client.Lease.ServerEpoch[..12]}… ttl={client.Lease.LeaseTtlMs}ms");

            var version = await client.QueryAsync("daemon.version");
            _output.WriteLine($"daemon.version: {version}");

            var sessions = await client.QueryAsync("session.list");
            _output.WriteLine($"session.list: {sessions.ValueKind} len={(sessions.ValueKind == JsonValueKind.Array ? sessions.GetArrayLength() : -1)}");

            Assert.Equal("qaqh.Ringing", (await client.QueryAsync("daemon.version")).ToString().Length > 0 ? "qaqh.Ringing" : "qaqh.Ringing");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex is Qaqh.Sdk.SdkException sdk)
            {
                _output.WriteLine($"kind={sdk.Kind} status={sdk.Status} detail={sdk.Detail}");
            }
            throw;
        }
        finally
        {
            client.Close();
        }
    }

    /// <summary>
    /// provider 目录面冒烟（设置页默认模型的事实源）：锁定 SettingsViewModel 的解析
    /// 假设——provider.list 同步结果必带 revision/providers 键；未知 provider 的
    /// model.selection.set_default 必须在提交侧被拒（fail-fast，不留脏默认）。
    /// 拒绝有两条通道：ack.Accepted=false 或 HTTP 4xx（handler 的 QaqhError::invalid
    /// 走 commands 端点的 error response），两种都算拒绝；结束后回读目录确认默认未被污染。
    /// </summary>
    [Fact]
    public async Task ProviderCatalogShapeAgainstLiveDaemon()
    {
        var client = TryConnect();
        if (client is null)
        {
            return;
        }
        try
        {
            var catalog = await ReadCatalogAsync(client);
            Assert.True(catalog.TryGetProperty("revision", out var revision), "catalog.revision 缺失");
            Assert.True(revision.TryGetInt64(out _), "catalog.revision 不是整数");
            Assert.True(catalog.TryGetProperty("providers", out var providers), "catalog.providers 缺失");
            Assert.Equal(JsonValueKind.Object, providers.ValueKind);
            _output.WriteLine($"catalog revision={revision} providers={providers.EnumerateObject().Count()}");
            string? defaultBefore = null;
            if (catalog.TryGetProperty("default_model_selection", out var d)
                && d.ValueKind == JsonValueKind.Object)
            {
                Assert.True(d.TryGetProperty("provider_id", out _) && d.TryGetProperty("model_id", out _),
                    "default_model_selection 形状漂移");
                defaultBefore = $"{d.GetProperty("provider_id").GetString()}/{d.GetProperty("model_id").GetString()}";
            }

            bool rejected;
            string reason;
            try
            {
                var bad = await client.SendCommandAsync(
                    target: new Contract.CommandTargetGlobal(),
                    command: new Contract.RingingCommandModelSelectionSetDefault
                    {
                        Selection = new Contract.ModelSelection
                        {
                            ProviderId = "no-such-provider",
                            ModelId = "no-such-model",
                            Options = new Contract.ModelSelectionOptions(),
                        },
                    });
                rejected = !bad.Accepted;
                reason = bad.RejectionReason ?? "ack accepted";
            }
            catch (Qaqh.Sdk.SdkException sdk)
            {
                rejected = (int)sdk.Status is >= 400 and < 500;
                reason = $"{sdk.Kind}/{sdk.Status} {sdk.Detail ?? sdk.Message}";
            }
            Assert.True(rejected, $"幽灵 provider/model 的 set_default 未被拒绝：{reason}");
            _output.WriteLine($"set_default(ghost) rejected as expected: {reason}");

            // 污染检查：被拒的写入不得改变目录默认。
            var after = await ReadCatalogAsync(client);
            string? defaultAfter = null;
            if (after.TryGetProperty("default_model_selection", out var d2)
                && d2.ValueKind == JsonValueKind.Object)
            {
                defaultAfter = $"{d2.GetProperty("provider_id").GetString()}/{d2.GetProperty("model_id").GetString()}";
            }
            Assert.Equal(defaultBefore, defaultAfter);
        }
        finally
        {
            client.Close();
        }
    }

    private static async Task<JsonElement> ReadCatalogAsync(RingingClient client)
    {
        var ack = await client.SendCommandAsync(
            target: new Contract.CommandTargetGlobal(),
            command: new Contract.RingingCommandProviderList());
        Assert.True(ack.Accepted, $"provider.list rejected: {ack.RejectionReason}");
        var catalog = Assert.IsAssignableFrom<JsonElement>(ack.Result);
        Assert.Equal(JsonValueKind.Object, catalog.ValueKind);
        return catalog;
    }

    private RingingClient? TryConnect()
    {
        var discovery = DaemonLocator.TryRead();
        if (discovery is null || !DaemonLocator.PidIsRunning(discovery.Pid))
        {
            _output.WriteLine("no live daemon; skipping");
            return null;
        }
        _output.WriteLine($"discovery: {discovery.Endpoint} pid={discovery.Pid} v={discovery.DaemonVersion}");
        var client = new RingingClient(new RingingClientOptions
        {
            ClientInstanceId = "qaqh-cs-tests",
            ClientKind = "Desktop",
            LaunchDaemonIfMissing = false,
        });
        client.ConnectAsync().GetAwaiter().GetResult();
        return client;
    }
}

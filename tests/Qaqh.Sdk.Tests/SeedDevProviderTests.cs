using Qaqh.Sdk;
using Qaqh.Sdk.Contract;
using Qaqh.Sdk.Discovery;
using Xunit;
using Xunit.Abstractions;

namespace Qaqh.Sdk.Tests;

/// <summary>
/// 开发环境种子：向存活 daemon 写入占位 provider/model/默认选择，
/// 打通 session.create 前置条件（正式 UI 是 M3 设置页；此测试同时是
/// provider 三命令 + config_revision CAS 的真机验证）。
/// </summary>
public class SeedDevProviderTests
{
    private readonly ITestOutputHelper _output;

    public SeedDevProviderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SeedPlaceholderProviderAndDefaultModel()
    {
        var discovery = DaemonLocator.TryRead();
        if (discovery is null || !DaemonLocator.PidIsRunning(discovery.Pid))
        {
            _output.WriteLine("no live daemon; skipping");
            return;
        }

        var client = new RingingClient(new RingingClientOptions
        {
            ClientInstanceId = "qaqh-cs-seed",
            ClientKind = "Desktop",
            LaunchDaemonIfMissing = false,
        });
        try
        {
            await client.ConnectAsync();

            // provider 目录命令要求 config_revision CAS；修订随每次写变更，逐步取。
            var revision = await client.GetProviderCatalogRevisionAsync();
            _output.WriteLine($"catalog revision: {revision}");

            var upsert = await client.SendCommandAsync(
                target: new CommandTargetGlobal(),
                command: new RingingCommandProviderUpsert
                {
                    Id = "dev-openai",
                    Provider = new ProviderUpsertDto
                    {
                        Name = "Dev Placeholder",
                        BaseUrl = "https://api.openai.com/v1",
                        Wire = Wire.Chat,
                        Credential = CredentialSource.ApiKey,
                        Headers = new Dictionary<string, string>(),
                        ApiKey = new SecretWriteSet { Value = "sk-dev-placeholder" },
                    },
                },
                preconditions: [new CommandPreconditionConfigRevision { Revision = revision }]);
            Assert.True(upsert.Accepted, $"provider.upsert rejected: {upsert.RejectionReason}");

            revision = await client.GetProviderCatalogRevisionAsync();
            var model = await client.SendCommandAsync(
                target: new CommandTargetGlobal(),
                command: new RingingCommandProviderModelUpsert
                {
                    ProviderId = "dev-openai",
                    ModelId = "gpt-4o-mini",
                    Model = new ModelDef
                    {
                        Enabled = true,
                        ContextWindow = 128_000,
                        MaxOutputTokens = 4_096,
                        ReasoningLevels = new[] { "low", "medium", "high" },
                        Defaults = new ModelDefaults(),
                        Capabilities = new ModelCapabilities
                        {
                            ToolCalls = true,
                            ImageInput = true,
                            NativeWebSearch = false,
                            Reasoning = true,
                        },
                    },
                },
                preconditions: [new CommandPreconditionConfigRevision { Revision = revision }]);
            Assert.True(model.Accepted, $"provider.model.upsert rejected: {model.RejectionReason}");

            revision = await client.GetProviderCatalogRevisionAsync();
            var setDefault = await client.SendCommandAsync(
                target: new CommandTargetGlobal(),
                command: new RingingCommandModelSelectionSetDefault
                {
                    Selection = new ModelSelection
                    {
                        ProviderId = "dev-openai",
                        ModelId = "gpt-4o-mini",
                        Options = new ModelSelectionOptions(),
                    },
                },
                preconditions: [new CommandPreconditionConfigRevision { Revision = revision }]);
            Assert.True(setDefault.Accepted, $"model.selection.set_default rejected: {setDefault.RejectionReason}");
            _output.WriteLine("seeded: dev-openai/gpt-4o-mini as default");
        }
        finally
        {
            client.Close();
        }
    }
}

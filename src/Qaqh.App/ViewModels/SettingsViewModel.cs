using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Qaqh.Sdk;
using Qaqh.Sdk.Contract;
using SdkException = Qaqh.Sdk.SdkException;

namespace Qaqh_App;

/// <summary>服务商目录行（管理列表用，形状对齐 provider.list 的 ProviderDef）。</summary>
public partial class ProviderRowVm : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _baseUrl = "";

    [ObservableProperty]
    private string _wire = "";

    [ObservableProperty]
    private string _credential = "";

    [ObservableProperty]
    private bool _hasApiKey;
}

/// <summary>目录树里一个 provider 的模型条目。</summary>
public sealed class ModelChoiceVm
{
    public required string Id { get; init; }
    public string Display { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string[] ReasoningLevels { get; init; } = [];
}

/// <summary>目录树里一个 provider（默认模型选择器的数据源）。</summary>
public sealed class ProviderCatalogVm
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public bool HasKey { get; init; }
    public List<ModelChoiceVm> Models { get; } = new();
}

/// <summary>
/// 设置页状态。LTS 基线（v3.0.20261110）事实源：<b>providers.toml 目录</b>。
/// 默认模型 = 读 <c>provider.list</c> 的 ProfilesDocument（revision /
/// default_model_selection / providers），写走
/// <c>model.selection.set_default</c>（config_revision CAS）。
/// config.save/config.load 的模型八键已被后端删除——<b>不得</b>再拼
/// {model,baseUrl,wire,credential,apiKey,maxTokens,contextLength,reasoningEffort}
/// （未知键会被 serde 静默忽略，产生假成功）。权限等级仍走
/// <c>config.set_permission_level</c> 单写口。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherQueue _dispatcher;
    private long _catalogRevision;
    private string? _defaultProviderId;
    private string? _defaultModelId;
    private string? _defaultEffort;

    public SettingsViewModel(ShellViewModel shell, DispatcherQueue dispatcher)
    {
        _shell = shell;
        _dispatcher = dispatcher;
        Accounts = new AccountsViewModel(shell);
    }

    // ── 权限等级（config 面仅存的模型无关项）────────────────────────
    [ObservableProperty]
    private long _permissionLevel = 1;

    /// <summary>RadioButtons 绑定用（等级 1-4 → 索引 0-3，SelectedIndex 是 int）。</summary>
    public int PermissionLevelIndex
    {
        get => (int)(PermissionLevel - 1);
        set
        {
            if (value >= 0)
            {
                PermissionLevel = value + 1;
            }
        }
    }

    [ObservableProperty]
    private bool _loaded;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>加载期间抑制权限等级回写（初始绑定会触发 SelectionChanged）。</summary>
    public bool SuppressPermissionEvents { get; private set; } = true;

    public AccountsViewModel Accounts { get; }

    // ── provider 目录 ─────────────────────────────────────────────────
    public ObservableCollection<ProviderRowVm> Providers { get; } = new();

    /// <summary>默认模型选择器的 provider 列表（与 Providers 同源，一次解析）。</summary>
    public ObservableCollection<ProviderCatalogVm> Catalog { get; } = new();

    [ObservableProperty]
    private ProviderCatalogVm? _selectedProvider;

    /// <summary>当前选中 provider 的模型列表（SelectedProvider 变化时重建）。</summary>
    public ObservableCollection<ModelChoiceVm> ProviderModels { get; } = new();

    [ObservableProperty]
    private ModelChoiceVm? _selectedModel;

    /// <summary>选中模型的 reasoning 档位候选（无档位则空列表，选项留空 = 跟随模型默认）。</summary>
    public ObservableCollection<string> EffortOptions { get; } = new();

    [ObservableProperty]
    private string? _selectedEffort;

    [ObservableProperty]
    private string _catalogDefaultText = "（未设置）";

    partial void OnSelectedProviderChanged(ProviderCatalogVm? value)
    {
        ProviderModels.Clear();
        foreach (var model in value?.Models ?? [])
        {
            ProviderModels.Add(model);
        }
        SelectedModel = ProviderModels.FirstOrDefault(m => m.Enabled);
    }

    partial void OnSelectedModelChanged(ModelChoiceVm? value)
    {
        EffortOptions.Clear();
        foreach (var level in value?.ReasoningLevels ?? [])
        {
            EffortOptions.Add(level);
        }
        if (!string.IsNullOrEmpty(SelectedEffort) && !EffortOptions.Contains(SelectedEffort))
        {
            SelectedEffort = null;
        }
    }

    // ── 读 ────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        _ = Accounts.RefreshAsync();
        var config = await _shell.Client.QueryAsync("config.load");
        if (config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("permissionLevel", out var pl)
            && pl.TryGetInt64(out var plv))
        {
            PermissionLevel = plv;
        }
        await RefreshCatalogAsync();
        Loaded = true;
        // 等渲染完初始选中态再放开回写。
        _ = _dispatcher.TryEnqueue(() => SuppressPermissionEvents = false);
    }

    /// <summary><c>provider.list</c> 全量读：revision、default_model_selection、
    /// providers（含每个 provider 的模型表）。一次解析喂饱两个列表。</summary>
    public async Task RefreshCatalogAsync()
    {
        var ack = await _shell.Client.SendCommandAsync(
            target: new CommandTargetGlobal(),
            command: new RingingCommandProviderList());
        if (!ack.Accepted || ack.Result is not { ValueKind: JsonValueKind.Object } catalog)
        {
            throw SdkException.Protocol($"provider.list 未返回目录：{ack.RejectionReason ?? "unknown"}");
        }
        _catalogRevision = catalog.TryGetProperty("revision", out var revision)
            && revision.TryGetInt64(out var value) ? value : 0;

        Providers.Clear();
        Catalog.Clear();
        if (catalog.TryGetProperty("providers", out var providers)
            && providers.ValueKind == JsonValueKind.Object)
        {
            foreach (var provider in providers.EnumerateObject())
            {
                var entry = new ProviderCatalogVm
                {
                    Id = provider.Name,
                    Name = GetString(provider.Value, "name"),
                    BaseUrl = GetString(provider.Value, "base_url"),
                    HasKey = GetString(provider.Value, "api_key") == "set",
                };
                if (provider.Value.TryGetProperty("models", out var models)
                    && models.ValueKind == JsonValueKind.Object)
                {
                    foreach (var model in models.EnumerateObject())
                    {
                        entry.Models.Add(new ModelChoiceVm
                        {
                            Id = model.Name,
                            Display = string.IsNullOrEmpty(GetString(model.Value, "display_name"))
                                ? model.Name : GetString(model.Value, "display_name"),
                            Enabled = !model.Value.TryGetProperty("enabled", out var enabled)
                                || enabled.GetBoolean(),
                            ReasoningLevels = model.Value.TryGetProperty("reasoning_levels", out var levels)
                                && levels.ValueKind == JsonValueKind.Array
                                ? levels.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                                : [],
                        });
                    }
                }
                Providers.Add(new ProviderRowVm
                {
                    Id = entry.Id,
                    Name = entry.Name,
                    BaseUrl = entry.BaseUrl,
                    Wire = GetString(provider.Value, "wire"),
                    Credential = GetString(provider.Value, "credential"),
                    HasApiKey = entry.HasKey,
                });
                Catalog.Add(entry);
            }
        }

        // 当前默认 + 选择器种子（按目录里的 default_model_selection 回填）。
        _defaultProviderId = _defaultModelId = _defaultEffort = null;
        if (catalog.TryGetProperty("default_model_selection", out var d)
            && d.ValueKind == JsonValueKind.Object)
        {
            _defaultProviderId = GetString(d, "provider_id");
            _defaultModelId = GetString(d, "model_id");
            if (d.TryGetProperty("options", out var options)
                && options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("reasoning_effort", out var effort)
                && effort.ValueKind == JsonValueKind.String)
            {
                _defaultEffort = effort.GetString();
            }
            var providerName = Catalog.FirstOrDefault(p => p.Id == _defaultProviderId)?.Name
                ?? _defaultProviderId;
            var modelDisplay = Catalog.FirstOrDefault(p => p.Id == _defaultProviderId)?
                .Models.FirstOrDefault(m => m.Id == _defaultModelId)?.Display ?? _defaultModelId;
            CatalogDefaultText = $"{providerName} / {modelDisplay}"
                + (string.IsNullOrEmpty(_defaultEffort) ? "" : $"（{_defaultEffort}）");
        }
        else
        {
            CatalogDefaultText = "（未设置）";
        }
        SeedSelection();
    }

    /// <summary>兼容旧调用点：provider 管理按钮刷新目录。</summary>
    public Task RefreshProvidersAsync() => RefreshCatalogAsync();

    // ── 写：默认模型 ──────────────────────────────────────────────────

    /// <summary>把当前选择写为全局默认模型；CAS 前置 + <b>回读校验</b>——
    /// 后端对未知键/旧形状是静默容忍的，成功回执不等于落盘，读回不一致必须报错。</summary>
    public async Task SaveDefaultSelectionAsync()
    {
        var provider = SelectedProvider
            ?? throw SdkException.Protocol("请选择 provider");
        var model = SelectedModel
            ?? throw SdkException.Protocol("请选择模型");
        if (!model.Enabled)
        {
            throw SdkException.Protocol($"模型 {provider.Id}/{model.Id} 已禁用，不能作为默认");
        }
        var options = new ModelSelectionOptions();
        if (!string.IsNullOrEmpty(SelectedEffort))
        {
            options = options with { ReasoningEffort = SelectedEffort };
        }
        var selection = new ModelSelection
        {
            ProviderId = provider.Id,
            ModelId = model.Id,
            Options = options,
        };
        var ack = await _shell.Client.SendCommandAsync(
            target: new CommandTargetGlobal(),
            command: new RingingCommandModelSelectionSetDefault { Selection = selection },
            preconditions: [new CommandPreconditionConfigRevision { Revision = _catalogRevision }]);
        if (!ack.Accepted)
        {
            throw SdkException.Protocol($"model.selection.set_default 被拒绝：{ack.RejectionReason ?? "unknown"}");
        }
        await RefreshCatalogAsync();
        if (_defaultProviderId != provider.Id || _defaultModelId != model.Id
            || (SelectedEffort ?? "") != (_defaultEffort ?? ""))
        {
            throw SdkException.Protocol(
                "写入未生效：目录读回的默认模型与提交不一致（疑似契约漂移，请核对后端）");
        }
        StatusText = $"默认模型已设为 {provider.Id}/{model.Id}"
            + (string.IsNullOrEmpty(SelectedEffort) ? "" : $"（{SelectedEffort}）");
    }

    // ── 写：权限等级 ──────────────────────────────────────────────────

    public async Task SetPermissionLevelAsync(long level)
    {
        var ack = await _shell.Client.SendCommandAsync(
            sessionId: null,
            new RingingCommandConfigSetPermissionLevel { Level = level });
        if (!ack.Accepted)
        {
            throw SdkException.Protocol($"permission 命令被拒绝：{ack.RejectionReason ?? "unknown"}");
        }
        PermissionLevel = level;
        StatusText = $"权限等级已设为 {level}";
    }

    // ── 写：provider 目录 CRUD ────────────────────────────────────────

    public async Task UpsertProviderAsync(string id, string name, string baseUrl, string wire, string credential, string? apiKey)
    {
        var dto = new ProviderUpsertDto
        {
            Name = name,
            BaseUrl = baseUrl,
            Wire = Enum.TryParse<Qaqh.Sdk.Contract.Wire>(wire, ignoreCase: true, out var w) ? w : Qaqh.Sdk.Contract.Wire.Chat,
            Credential = Enum.TryParse<Qaqh.Sdk.Contract.CredentialSource>(credential, ignoreCase: true, out var c) ? c : Qaqh.Sdk.Contract.CredentialSource.ApiKey,
            Headers = new Dictionary<string, string>(),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            dto = dto with { ApiKey = new SecretWriteSet { Value = apiKey } };
        }
        var ack = await _shell.Client.SendCommandAsync(
            target: new CommandTargetGlobal(),
            command: new RingingCommandProviderUpsert { Id = id, Provider = dto },
            preconditions: [new CommandPreconditionConfigRevision { Revision = _catalogRevision }]);
        if (!ack.Accepted)
        {
            throw SdkException.Protocol($"provider.upsert 被拒绝：{ack.RejectionReason ?? "unknown"}");
        }
        await RefreshCatalogAsync();
        StatusText = $"provider {id} 已保存";
    }

    public async Task DeleteProviderAsync(string id)
    {
        var ack = await _shell.Client.SendCommandAsync(
            target: new CommandTargetGlobal(),
            command: new RingingCommandProviderDelete { Id = id },
            preconditions: [new CommandPreconditionConfigRevision { Revision = _catalogRevision }]);
        if (!ack.Accepted)
        {
            throw SdkException.Protocol($"provider.delete 被拒绝：{ack.RejectionReason ?? "unknown"}");
        }
        await RefreshCatalogAsync();
        StatusText = $"provider {id} 已删除";
    }

    // ── 杂项 ──────────────────────────────────────────────────────────

    private void SeedSelection()
    {
        var provider = Catalog.FirstOrDefault(p => p.Id == _defaultProviderId)
            ?? Catalog.FirstOrDefault();
        if (provider is null)
        {
            SelectedProvider = null;
            SelectedModel = null;
            SelectedEffort = null;
            return;
        }
        SelectedProvider = provider;
        SelectedModel = provider.Models.FirstOrDefault(m => m.Id == _defaultModelId)
            ?? provider.Models.FirstOrDefault(m => m.Enabled);
        SelectedEffort = !string.IsNullOrEmpty(_defaultEffort) && EffortOptions.Contains(_defaultEffort)
            ? _defaultEffort
            : null;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

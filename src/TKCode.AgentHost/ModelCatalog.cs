using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TKCode.AgentHost.Catalog;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Ai.Models;
using TKCode.Config;
using TKCode.Rpc;

namespace TKCode.AgentHost;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModelCompat))]
internal sealed partial class CompatJsonContext : JsonSerializerContext;

/// <summary>厂商下的一个模型（含设置页需要的来源信息）。</summary>
/// <param name="Api">该模型实际使用的协议。</param>
/// <param name="ApiOverridden">协议与厂商默认值不同（用户在设置中指定，或目录中的固定规则）。</param>
/// <param name="DefaultApi">没有用户覆盖时的协议（厂商默认值，或目录中的固定规则）。</param>
public sealed record ProviderModelState(ModelInfo Model, bool Enabled, CatalogModel? Catalog, bool IsDiscovered, bool HasOverride,
    string? Api = null, bool ApiOverridden = false, string? DefaultApi = null)
{
    public bool InCatalog => Catalog is not null;
}

/// <summary>一个厂商的解析结果（设置页与模型选择器共用）。</summary>
/// <param name="SupportsDiscovery">自定义或本地厂商：模型列表可通过 GET /models 获取。</param>
public sealed record ProviderState(
    string Id,
    string Name,
    string? Api,
    string? BaseUrl,
    bool IsBuiltIn,
    bool IsConfigured,
    bool Enabled,
    ResolvedKey Key,
    bool HasModelsOverride,
    IReadOnlyList<ProviderModelState> Models,
    IReadOnlyList<string> Errors,
    bool SupportsDiscovery = false,
    string? DiscoveryError = null)
{
    public bool IsActive => Enabled && Errors.Count == 0 && Key.IsAvailable && Models.Any(m => m.Enabled);

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public bool BearerForMessages { get; init; }
}

/// <summary>
/// 可用模型目录 = 内置目录（<see cref="BuiltInCatalog"/>）+ ~/.tkcode/settings.json（规格见 docs/settings.md）。
/// <see cref="Reload"/> 在文件变化时由 <see cref="SettingsWatcher"/> 调用；解析失败时保留上一次正确的结果。
/// </summary>
public sealed class ModelCatalog
{
    private const int DefaultContextWindow = 128_000;
    private const int DefaultMaxTokens = 16_384;

    private readonly ILogger<ModelCatalog> _logger;
    private readonly BuiltInCatalog _builtIn;
    private readonly ModelDiscovery _discovery;
    private readonly HashSet<string> _discovering = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Snapshot _current = new([], [], [], []);

    public ModelCatalog(ILogger<ModelCatalog> logger, BuiltInCatalog builtIn, ModelDiscovery discovery)
    {
        _logger = logger;
        _builtIn = builtIn;
        _discovery = discovery;
        LegacyMigration.Run(logger);
        SettingsSchema.EnsureWritten();
        Reload();
    }

    /// <summary>模型列表变化（包括配置错误变化）后触发。</summary>
    public event Action? Changed;

    public BuiltInCatalog BuiltIn => _builtIn;
    public IReadOnlyList<ModelInfo> Models => _current.Models;
    public IReadOnlyList<ProviderState> Providers => _current.Providers;
    /// <summary>settings.json 本身的错误（无法解析）。此时沿用上一次正确的配置。单个厂商的问题见 <see cref="ProviderState.Errors"/>。</summary>
    public IReadOnlyList<string> Errors => _current.Errors;
    public IReadOnlyList<string> Warnings => _current.Warnings;

    /// <summary>settings.json 的 promptCacheTtl（缓存断点有效期，全局）。文件有错误时沿用上一次的值。</summary>
    public PromptCacheTtl CacheTtl { get; private set; }

    /// <summary>settings.json 的 agent.shell.profile（bash 工具使用的 shell，null 为默认选择）。文件有错误时沿用上一次的值。</summary>
    public string? ShellProfile { get; private set; }

    /// <summary>settings.json 的 permissions.additionalDirectories（全局附加目录）。文件有错误时沿用上一次的值。</summary>
    public IReadOnlyList<string> AdditionalDirectories { get; private set; } = [];

    /// <summary>快速模型引用；文件有错误时沿用上一次成功解析的配置。</summary>
    public string? FastModelRef { get; private set; }

    /// <summary>主模型引用（新会话的默认模型，可能不可用）。</summary>
    public string? DefaultModelRef { get; private set; }

    /// <summary>重新读取 settings.json。返回 false 表示文件有错误（沿用旧配置）。</summary>
    public bool Reload()
    {
        var result = SettingsFile.Load();
        lock (_gate)
        {
            if (result.Settings is null)
            {
                _logger.LogError("settings.json is invalid, keeping previous configuration: {Errors}", string.Join("; ", result.Errors));
                _current = _current with { Errors = result.Errors, Warnings = result.Warnings };
            }
            else
            {
                var providers = Build(result.Settings);
                var models = providers.Where(p => p.IsActive).SelectMany(p => p.Models.Where(m => m.Enabled).Select(m => m.Model)).ToList();
                // Fake 厂商：显式开启，或者没有任何真实模型时提供，便于离线体验
                if (Environment.GetEnvironmentVariable("TKCODE_FAKE_PROVIDER") == "1" || models.Count == 0)
                {
                    models.InsertRange(0, FakeWireApi.Models);
                }
                foreach (var provider in providers.Where(p => p.Errors.Count > 0))
                {
                    _logger.LogWarning("providers.{Provider}: {Errors}", provider.Id, string.Join("; ", provider.Errors));
                }
                _current = new Snapshot(models, providers, [], result.Warnings);
                CacheTtl = result.Settings.PromptCacheTtl == "1h" ? PromptCacheTtl.OneHour : PromptCacheTtl.FiveMinutes;
                ShellProfile = result.Settings.Agent?.Shell?.Profile;
                AdditionalDirectories = result.Settings.Permissions?.AdditionalDirectories?.Where(d => !string.IsNullOrWhiteSpace(d)).ToArray() ?? [];
                DefaultModelRef = result.Settings.Model;
                FastModelRef = result.Settings.FastModel;
                foreach (var warning in result.Warnings)
                {
                    _logger.LogWarning("settings.json: {Warning}", warning);
                }
                _logger.LogInformation("Model catalog loaded: {Models} models from {Providers} active providers",
                    models.Count, providers.Count(p => p.IsActive));
            }
        }
        Changed?.Invoke();
        ScheduleDiscovery();
        return result.IsValid;
    }

    /// <summary>重新获取某个厂商的模型列表（设置页“刷新”），完成后重载。</summary>
    public async Task RefreshModelsAsync(string providerId, CancellationToken ct = default)
    {
        _discovery.Forget(providerId);
        if (_current.Providers.FirstOrDefault(p => p.Id == providerId) is { SupportsDiscovery: true } provider)
        {
            await DiscoverAsync(provider, ct);
        }
        Reload();
    }

    /// <summary>为尚无缓存的可发现厂商在后台获取模型列表，完成后重载一次。</summary>
    private void ScheduleDiscovery()
    {
        List<ProviderState> pending;
        lock (_gate)
        {
            pending = _current.Providers
                .Where(p => p.SupportsDiscovery && p.Enabled && p.Key.IsAvailable && _discovery.Cached(p.Id) is null && _discovering.Add(p.Id))
                .ToList();
        }
        if (pending.Count == 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.WhenAll(pending.Select(p => DiscoverAsync(p, CancellationToken.None)));
            lock (_gate)
            {
                foreach (var p in pending)
                {
                    _discovering.Remove(p.Id);
                }
            }
            Reload();
        });
    }

    private async Task DiscoverAsync(ProviderState provider, CancellationToken ct)
    {
        if (provider.Api is null || provider.BaseUrl is null)
        {
            return;
        }
        var result = await _discovery.FetchAsync(provider.Api, provider.BaseUrl, provider.Key.Value, provider.Headers,
            provider.BearerForMessages, ct);
        _discovery.Store(provider.Id, result);
        if (result.Success)
        {
            _logger.LogInformation("Discovered {Count} models from {Provider}", result.Ids!.Count, provider.Id);
        }
        else
        {
            _logger.LogWarning("Model discovery for {Provider} failed: {Error}", provider.Id, result.Error);
        }
    }

    /// <summary>按引用（厂商/模型）或裸模型 id 查找。</summary>
    public ModelInfo? Find(string? reference)
    {
        if (reference is null)
        {
            return null;
        }
        var models = _current.Models;
        return models.FirstOrDefault(m => RefOf(m) == reference) ?? models.FirstOrDefault(m => m.Id == reference);
    }

    public ModelInfo? Find(string? provider, string? modelId) =>
        provider is null || modelId is null ? Find(modelId) : Find($"{provider}/{modelId}") ?? Find(modelId);

    public ModelInfo Get(string reference) =>
        Find(reference) ?? throw new ArgumentException($"Unknown model '{reference}'.", nameof(reference));

    /// <summary>每次请求时调用，所以 Key 改动无需重建会话即可生效。</summary>
    public string? GetApiKey(string provider) =>
        _current.Providers.FirstOrDefault(p => p.Id == provider)?.Key.Value;

    public static string RefOf(ModelInfo model) => $"{model.Provider}/{model.Id}";

    private List<ProviderState> Build(TKCodeSettings settings)
    {
        var states = new List<ProviderState>();
        var configured = settings.Providers;
        var ids = configured.Keys
            .Concat(BuiltInCatalog.Recommended)
            // 未配置但检测到环境变量 Key 的内置厂商也列出来
            .Concat(_builtIn.Providers.Where(p => p.Value.Env.Any(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))).Select(p => p.Key))
            // 用户在设置页移除的厂商（hidden）完全不出现，直到重新添加
            .Where(id => configured.GetValueOrDefault(id)?.Hidden != true)
            .Distinct(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            states.Add(BuildProvider(id, configured.GetValueOrDefault(id), _builtIn.Find(id)));
        }
        return states;
    }

    private ProviderState BuildProvider(string id, ProviderSettings? user, CatalogProvider? catalog)
    {
        var errors = new List<string>();
        var api = user?.Api ?? catalog?.Api;
        var wireApi = KnownApisShort.ToWireApi(api);
        var baseUrl = user?.BaseUrl ?? catalog?.BaseUrl;
        if (wireApi is null)
        {
            errors.Add(api is null ? "\"api\" is required for a custom provider (chat, responses or messages)." : $"unknown api \"{api}\".");
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            errors.Add("\"baseUrl\" is required for a custom provider.");
        }
        var key = Secrets.Resolve(user?.ApiKey, catalog?.Env);
        if (!key.IsAvailable && catalog?.KeyOptional == true && user?.ApiKey is null)
        {
            key = new ResolvedKey(null, KeySource.NotRequired, null);
        }

        var providerCompat = user?.Compat;
        var headers = user?.Headers?.ToDictionary(h => h.Key, h => Secrets.Resolve(h.Value).Value ?? "", StringComparer.OrdinalIgnoreCase);

        // 自定义厂商、本地服务与声明了 Discover 的内置厂商：模型列表可以通过 GET /models 获取，models 可以省略（省略时启用全部发现的模型）
        var supportsDiscovery = (catalog is null || catalog.KeyOptional || catalog.Discover) && wireApi is not null && !string.IsNullOrWhiteSpace(baseUrl);
        var discovered = supportsDiscovery ? _discovery.Cached(id) : null;
        var discoveredIds = (discovered?.Ids ?? []).Where(m => catalog?.IsExcluded(m) != true).ToArray();
        var enabledIds = user?.Models is { } listed
            ? listed.Keys.ToHashSet(StringComparer.Ordinal)
            : supportsDiscovery ? discoveredIds.ToHashSet(StringComparer.Ordinal)
            : (catalog is null ? [] : _builtIn.FeaturedModels(catalog).ToHashSet(StringComparer.Ordinal));
        if (user is not null && (catalog is null || catalog.Discover) && user.Models is not { Count: > 0 } && discovered is { Success: false })
        {
            errors.Add($"no models: list them in \"models\" or fix model discovery ({discovered.Error}).");
        }

        var allIds = (catalog?.Models.Keys ?? Enumerable.Empty<string>())
            .Concat(user?.Models?.Keys ?? Enumerable.Empty<string>())
            .Concat(discoveredIds)
            .Distinct(StringComparer.Ordinal);
        var models = new List<ProviderModelState>();
        foreach (var modelId in allIds)
        {
            // 目录外的厂商（代理、本地服务）按模型 id 借用目录中同名模型的元数据
            var fromCatalog = catalog?.Models.GetValueOrDefault(modelId) ?? _builtIn.FindModel(modelId, api);
            var fromUser = user?.Models?.GetValueOrDefault(modelId);
            if (wireApi is null || baseUrl is null)
            {
                break;
            }
            // 模型可以单独指定协议与 Base URL；默认依次为目录中的固定规则、厂商协议
            var defaultApi = catalog?.ApiFor(modelId) ?? api;
            var modelApi = fromUser?.Api ?? defaultApi;
            var modelWireApi = KnownApisShort.ToWireApi(modelApi) ?? wireApi;
            var modelBaseUrl = NormalizeBaseUrl(modelApi, fromUser?.BaseUrl ?? baseUrl);
            try
            {
                models.Add(new ProviderModelState(BuildModel(id, modelId, modelWireApi, modelBaseUrl, headers, fromCatalog, fromUser, providerCompat),
                    enabledIds.Contains(modelId), fromCatalog, discoveredIds.Contains(modelId), fromUser is not null && fromUser != new ModelSettings(),
                    modelApi, fromUser?.Api is not null || modelApi != api, defaultApi));
            }
            catch (JsonException ex)
            {
                errors.Add($"models.{modelId}.compat: {ex.Message}");
            }
        }
        // 启用的模型排在前面，保持推荐 / 列出的顺序
        var order = user?.Models is { } listedOrder
            ? listedOrder.Keys.ToList()
            : (catalog is null ? [] : _builtIn.FeaturedModels(catalog).ToList());
        models.Sort((a, b) =>
        {
            if (a.Enabled != b.Enabled)
            {
                return a.Enabled ? -1 : 1;
            }
            var ia = order.IndexOf(a.Model.Id);
            var ib = order.IndexOf(b.Model.Id);
            return (ia < 0 ? int.MaxValue : ia).CompareTo(ib < 0 ? int.MaxValue : ib);
        });

        return new ProviderState(id, user?.Name ?? catalog?.Name ?? id, api, baseUrl, catalog is not null, user is not null,
            user?.Enabled ?? true, key, user?.Models is not null, models, errors, supportsDiscovery, discovered?.Error)
        {
            Headers = headers,
            BearerForMessages = user?.Compat?["authHeader"]?.ToString().Equals("bearer", StringComparison.OrdinalIgnoreCase) ?? false,
        };
    }

    private static ModelInfo BuildModel(string providerId, string modelId, string wireApi, string baseUrl, Dictionary<string, string>? headers,
        CatalogModel? catalog, ModelSettings? user, JsonObject? providerCompat)
    {
        // compat：目录推导 → 厂商级 → 模型级，后者覆盖前者
        var compat = new JsonObject();
        if (catalog?.Efforts is { Count: > 0 } efforts)
        {
            compat["reasoningEfforts"] = new JsonArray(efforts.Select(e => (JsonNode)e).ToArray());
            // 目录给出了推理选项但没有开关：思考不能关闭
            compat["supportsDisabledThinking"] = catalog.CanDisableThinking;
        }
        if (catalog?.ReasoningContent == true)
        {
            compat["requiresReasoningContentOnAssistant"] = true;
        }
        if (user?.Efforts is { Count: > 0 } userEfforts)
        {
            compat["reasoningEfforts"] = new JsonArray(userEfforts.Select(e => (JsonNode)e).ToArray());
        }
        Merge(compat, providerCompat);
        Merge(compat, user?.Compat);

        return new ModelInfo
        {
            Id = modelId,
            Name = user?.Name ?? catalog?.Name ?? modelId,
            Description = user?.Description ?? catalog?.Description,
            Provider = providerId,
            Api = wireApi,
            BaseUrl = baseUrl,
            Headers = headers,
            Reasoning = user?.Reasoning ?? catalog?.Reasoning ?? false,
            SupportsImages = user?.Images ?? catalog?.Images ?? false,
            SupportsFastMode = user?.FastMode ?? catalog?.FastMode ?? false,
            ContextWindow = user?.ContextWindow ?? catalog?.ContextWindow ?? DefaultContextWindow,
            MaxTokens = user?.MaxTokens ?? catalog?.MaxTokens ?? DefaultMaxTokens,
            DefaultContextWindow = user?.DefaultContextWindow ?? catalog?.DefaultContextWindow,
            Compat = compat.Count == 0 ? null : compat.Deserialize(CompatJsonContext.Default.ModelCompat),
        };
    }

    /// <summary>
    /// Messages 适配器自行拼接 /v1/messages。网关常把 Base URL 写成 …/v1（供 Chat / Responses 使用），
    /// 同一 Base URL 下的模型改用 Messages 协议时去掉末尾的 /v1，避免请求 /v1/v1/messages。
    /// </summary>
    private static string NormalizeBaseUrl(string? api, string baseUrl) =>
        api == KnownApisShort.Messages && baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal)
            ? baseUrl.TrimEnd('/')[..^3]
            : baseUrl;

    private static void Merge(JsonObject target, JsonObject? source)
    {
        foreach (var (key, value) in source ?? [])
        {
            target[key] = value?.DeepClone();
        }
    }

    private sealed record Snapshot(
        IReadOnlyList<ModelInfo> Models,
        IReadOnlyList<ProviderState> Providers,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings);
}

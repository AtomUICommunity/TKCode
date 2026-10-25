using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TKCode.Config;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Rpc;
using TKCode.Shell;

namespace TKCode.Desktop.ViewModels;

public enum SettingsSection
{
    General,
    Providers,
    Agents,
    Mcp,
    Assets,
    About,
}

public sealed partial class SettingsNavItemViewModel(SettingsSection section, LucideKind icon, TKCodeLangResourceKind title) : ObservableObject
{
    public SettingsSection Section { get; } = section;
    public LucideKind Icon { get; } = icon;
    public string Title => Strings.Get(title);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void RefreshLanguage() => OnPropertyChanged(nameof(Title));
}

/// <summary>下拉 / 分段选择中的一项。</summary>
public sealed partial class SettingsChoiceViewModel(string value, string label) : ObservableObject
{
    public string Value { get; } = value;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Arc 的分段选择等控件对普通对象显示 ToString()。</summary>
    public override string ToString() => Label;

    /// <summary>
    /// 选项（值与文字）没变时只更新选中状态：设置每写一次都会刷新这里，整表重建会把分段控件的容器换掉，选中条不再滑动、焦点也丢了。
    /// 选项变了（切换语言、模型列表变化）才重建。
    /// </summary>
    internal static void Fill(ObservableCollection<SettingsChoiceViewModel> target, IEnumerable<(string Value, string Label)> items, string selected)
    {
        var list = items as IReadOnlyList<(string Value, string Label)> ?? items.ToList();
        var same = target.Count == list.Count;
        for (var i = 0; same && i < list.Count; i++)
        {
            same = target[i].Value == list[i].Value && target[i].Label == list[i].Label;
        }
        if (same)
        {
            // 先选中新的再取消旧的：中间不会出现“没有选中项”
            foreach (var choice in target.Where(c => c.Value == selected))
            {
                choice.IsSelected = true;
            }
            foreach (var choice in target.Where(c => c.Value != selected))
            {
                choice.IsSelected = false;
            }
            return;
        }
        target.Clear();
        foreach (var (value, label) in list)
        {
            target.Add(new SettingsChoiceViewModel(value, label) { IsSelected = value == selected });
        }
    }
}

public sealed partial class ProviderModelItemViewModel(ProviderModelDto model) : ObservableObject
{
    public ProviderModelDto Model { get; private set; } = model;

    /// <summary>Host 的新快照：原地更新这一行（行里的开关不重建，正在播放的动画和焦点都还在）。</summary>
    public void Update(ProviderModelDto dto)
    {
        Model = dto;
        IsEnabled = dto.Enabled;
        OnPropertyChanged(string.Empty);
    }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public bool ShowId => Model.Name != Model.Id;
    public string ContextText => ModelPickerViewModel.FormatTokens(Model.ContextWindow);
    public string OutputText => Model.MaxTokens > 0 ? Strings.Format(TKCodeLangResourceKind.OutputFormat, ModelPickerViewModel.FormatTokens(Model.MaxTokens)) : "";
    public bool InCatalog => Model.InCatalog;
    public bool IsDiscovered => Model.IsDiscovered;
    public bool HasOverride => Model.HasOverride;
    public bool Reasoning => Model.Reasoning;
    public bool Images => Model.Images;
    public bool FastMode => Model.FastMode;
    public bool HasPrice => Model.InputPrice is not null && Model.OutputPrice is not null;
    public string PriceText => !HasPrice ? ""
        : Model.InputPrice == 0 && Model.OutputPrice == 0 ? Strings.Get(TKCodeLangResourceKind.PriceFree)
        : Strings.Format(TKCodeLangResourceKind.PriceFormat, Price(Model.InputPrice!.Value), Price(Model.OutputPrice!.Value));

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = model.Enabled;

    /// <summary>单独指定了协议时，在行内显示协议名。</summary>
    public bool ApiOverridden => Model.ApiOverridden;
    public string ApiText => ProtocolLabel(Model.Api);

    public static string ProtocolLabel(string? api) => Strings.Get(api switch
    {
        "responses" => TKCodeLangResourceKind.ProtocolShortResponses,
        "messages" => TKCodeLangResourceKind.ProtocolShortMessages,
        _ => TKCodeLangResourceKind.ProtocolShortChat,
    });

    private static string Price(double value) => value >= 1 ? value.ToString("0.##") : value.ToString("0.###");
}

public enum ProviderStatus
{
    Connected,
    Local,
    MissingKey,
    NoModels,
    Disabled,
    Error,
    NotConfigured,
}

/// <summary>左栏中的一个厂商。</summary>
public sealed partial class ProviderListItemViewModel(string id, string name, ProviderStatus status, bool isConfigured) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public ProviderStatus Status { get; } = status;
    public bool IsConfigured { get; } = isConfigured;
    public string? LogoKey { get; } = ModelIcons.ForProvider(id);
    public bool IsOk => Status is ProviderStatus.Connected or ProviderStatus.Local;
    public bool IsWarning => Status is ProviderStatus.MissingKey or ProviderStatus.NoModels or ProviderStatus.Error;
    public string StatusText => ProviderDetailViewModel.StatusLabel(Status);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// 右栏：选中厂商的详情。连接表单（协议 / Base URL / Key）统一保存，可先测试；模型表逐个启用、编辑覆盖。
/// 所有修改写回 settings.json，由 Host 的文件监听生效。
/// </summary>
public sealed partial class ProviderDetailViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public ProviderDetailViewModel(SettingsViewModel owner, ProviderDto dto)
    {
        _owner = owner;
        Dto = dto;
        ProtocolInput = dto.Api.Length > 0 ? dto.Api : "chat";
        BaseUrlInput = dto.BaseUrl ?? "";
        RebuildModels();
    }

    public ProviderDto Dto { get; private set; }
    public string Id => Dto.Id;
    public string Name => Dto.Name;
    public bool IsBuiltIn => Dto.IsBuiltIn;
    public bool IsCustom => !Dto.IsBuiltIn;
    public bool IsConfigured => Dto.IsConfigured;
    /// <summary>只按厂商 id 取图标；自定义厂商显示通用图标，不借用模型图标以免误导。</summary>
    public string? LogoKey => ModelIcons.ForProvider(Dto.Id);
    public ObservableCollection<ProviderModelItemViewModel> Models { get; } = [];
    public ObservableCollection<ProviderModelItemViewModel> VisibleModels { get; } = [];
    public IReadOnlyList<string> Errors => Dto.Errors;
    public bool HasErrors => Dto.Errors.Count > 0;
    public bool SupportsDiscovery => Dto.SupportsDiscovery;
    public string? DiscoveryError => Dto.DiscoveryError is { } e ? Strings.Format(TKCodeLangResourceKind.DiscoveryFailedFormat, e) : null;
    public bool HasDiscoveryError => Dto.DiscoveryError is not null;
    public bool HasModels => Models.Count > 0;
    public bool ShowModelsPlaceholder => !HasModels && !Dto.IsActive && Dto.KeySource == KeySourceDto.None;

    public ProviderStatus Status => ComputeStatus(Dto);
    public string StatusText => StatusLabel(Status);
    public bool IsOk => Status is ProviderStatus.Connected or ProviderStatus.Local;

    /// <summary>只有 settings.json 中已有的厂商显示启用开关（未配置、缺 Key 的厂商开关没有意义）。</summary>
    public bool ShowEnableSwitch => Dto.IsConfigured;

    /// <summary>既不在 settings.json 里也没有环境变量 Key：只是“添加厂商”选出来的草稿，移除按钮的作用是放弃。</summary>
    public bool IsPureDraft => !Dto.IsConfigured && Dto.KeySource != KeySourceDto.Environment;

    public string RemoveTooltip => Strings.Get(IsPureDraft ? TKCodeLangResourceKind.DiscardDraft : TKCodeLangResourceKind.Remove);

    public string KeyHint => Dto.KeySource switch
    {
        KeySourceDto.Environment => Strings.Format(TKCodeLangResourceKind.KeyFromEnvFormat, Dto.KeyVariable ?? ""),
        KeySourceDto.Settings => Strings.Get(TKCodeLangResourceKind.KeyKeepHint),
        KeySourceDto.NotRequired => Strings.Get(TKCodeLangResourceKind.KeyNotRequired),
        _ => Dto.KeyVariable is { } v ? Strings.Format(TKCodeLangResourceKind.KeyMissingFormat, v) : Strings.Get(TKCodeLangResourceKind.StatusMissingKey),
    };

    /// <summary>已保存的明文 Key 的遮罩，作为输入框的占位文字。</summary>
    public string KeyPlaceholder => Dto.MaskedKey ?? Strings.Get(TKCodeLangResourceKind.ApiKeyPlaceholder);
    public bool HasSavedKey => Dto.KeySource == KeySourceDto.Settings;
    public string ModelsSummary => Strings.Format(TKCodeLangResourceKind.ModelsEnabledFormat, Models.Count(m => m.IsEnabled), Models.Count);

    public bool IsDirty => KeyInput.Trim().Length > 0
                           || BaseUrlInput.Trim() != (Dto.BaseUrl ?? "")
                           || (IsCustom && ProtocolInput != Dto.Api);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string KeyInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string BaseUrlInput { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string ProtocolInput { get; set; }

    /// <summary>协议分段选择（自定义厂商可改）：选中项跟随 <see cref="ProtocolInput"/>。</summary>
    public IReadOnlyList<SettingsChoiceViewModel> ProtocolChoices { get; } =
        [new("chat", "Chat Completions"), new("responses", "Responses"), new("messages", "Anthropic Messages")];

    partial void OnProtocolInputChanged(string value)
    {
        foreach (var choice in ProtocolChoices)
        {
            choice.IsSelected = choice.Value == value;
        }
    }

    /// <summary>API Key 输入框是否显示明文（Arc 密码框的显示开关，双向绑定）。</summary>
    [ObservableProperty]
    public partial bool IsKeyRevealed { get; set; }

    [ObservableProperty]
    public partial string ModelFilter { get; set; } = "";

    [ObservableProperty]
    public partial string NewModelId { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestResult { get; private set; }

    [ObservableProperty]
    public partial bool TestSucceeded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    public partial bool IsTesting { get; private set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; private set; }

    /// <summary>正在编辑覆盖值的模型（弹层）。</summary>
    [ObservableProperty]
    public partial ModelOverrideEditorViewModel? Editing { get; private set; }

    public bool HasTestResult => TestResult is not null;

    partial void OnModelFilterChanged(string value) => RefreshVisibleModels();

    public static ProviderStatus ComputeStatus(ProviderDto dto) =>
        !dto.IsConfigured && dto.KeySource == KeySourceDto.None ? ProviderStatus.NotConfigured
        : !dto.Enabled ? ProviderStatus.Disabled
        : dto.Errors.Count > 0 ? ProviderStatus.Error
        : dto.KeySource == KeySourceDto.None ? ProviderStatus.MissingKey
        : !dto.Models.Any(m => m.Enabled) ? ProviderStatus.NoModels
        : dto.KeySource == KeySourceDto.NotRequired ? ProviderStatus.Local
        : ProviderStatus.Connected;

    public static string StatusLabel(ProviderStatus status) => Strings.Get(status switch
    {
        ProviderStatus.Connected => TKCodeLangResourceKind.StatusConnected,
        ProviderStatus.Local => TKCodeLangResourceKind.StatusLocal,
        ProviderStatus.Disabled => TKCodeLangResourceKind.StatusDisabled,
        ProviderStatus.Error => TKCodeLangResourceKind.StatusError,
        ProviderStatus.NoModels => TKCodeLangResourceKind.StatusNoModels,
        ProviderStatus.NotConfigured => TKCodeLangResourceKind.StatusNotConfigured,
        _ => TKCodeLangResourceKind.StatusMissingKey,
    });

    /// <summary>用 Host 的新快照更新，保留未保存的输入（Base URL 未改动时跟随新值）。</summary>
    public void Update(ProviderDto dto)
    {
        var baseUrlUntouched = BaseUrlInput.Trim() == (Dto.BaseUrl ?? "");
        Dto = dto;
        if (baseUrlUntouched)
        {
            BaseUrlInput = dto.BaseUrl ?? "";
        }
        RebuildModels();
        OnPropertyChanged(string.Empty);
    }

    public void RefreshLanguage() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void ChooseProtocol(SettingsChoiceViewModel choice) => ProtocolInput = choice.Value;

    /// <summary>尚未写入 settings.json 的厂商（从“添加厂商”选择后）：保存即添加。</summary>
    public bool IsDraft => !Dto.IsConfigured;

    private bool CanSave() => IsDirty || IsDraft;

    /// <summary>一次写入连接表单的全部修改。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var key = KeyInput.Trim();
        var url = BaseUrlInput.Trim();
        var api = ProtocolInput;
        var custom = IsCustom;
        var defaultUrl = IsBuiltIn ? _owner.CatalogBaseUrl(Id) : null;
        KeyInput = "";
        _owner.Write(root =>
        {
            var provider = SettingsFile.Provider(root, Id);
            // 之前被移除（hidden）的内置厂商重新添加
            provider.Remove("hidden");
            if (key.Length > 0)
            {
                provider["apiKey"] = key;
            }
            SettingsFile.Set(provider, "baseUrl", url.Length == 0 || url == defaultUrl ? null : JsonValue.Create(url));
            if (custom)
            {
                provider["api"] = api;
            }
        });
    }

    private bool CanTest() => !IsTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        TestResult = Strings.Get(TKCodeLangResourceKind.Testing);
        try
        {
            var result = await _owner.TestAsync(new ProviderTestRequest(Id, IsCustom ? ProtocolInput : null,
                BaseUrlInput.Trim().Length > 0 ? BaseUrlInput.Trim() : null, KeyInput.Trim().Length > 0 ? KeyInput.Trim() : null));
            TestSucceeded = result.Success;
            TestResult = result.Success
                ? Strings.Format(TKCodeLangResourceKind.TestOkFormat, result.Message, (int)result.LatencyMs)
                : Strings.Format(TKCodeLangResourceKind.TestFailedFormat, result.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void ClearKey() => _owner.Write(root => SettingsFile.Provider(root, Id).Remove("apiKey"));

    [RelayCommand]
    private void ToggleEnabled() =>
        _owner.Write(root => SettingsFile.Set(SettingsFile.Provider(root, Id), "enabled", Dto.Enabled ? JsonValue.Create(false) : null));

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveProviderAsync(this);

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        IsRefreshing = true;
        try
        {
            await _owner.RefreshModelsAsync(Id);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// 切换模型启用。settings.json 还没有 models 时，先写入当前启用的模型，再做增删（见 docs/settings.md §5）。
    /// </summary>
    [RelayCommand]
    private void ToggleModel(ProviderModelItemViewModel model)
    {
        var enabled = Models.Where(m => m.IsEnabled).Select(m => m.Id).ToList();
        if (model.IsEnabled)
        {
            enabled.Remove(model.Id);
        }
        else
        {
            enabled.Add(model.Id);
        }
        WriteEnabled(enabled);
    }

    [RelayCommand]
    private void EnableAll() => WriteEnabled(Models.Select(m => m.Id).ToList());

    [RelayCommand]
    private void DisableAll() => WriteEnabled([]);

    [RelayCommand]
    private void AddModel()
    {
        var id = NewModelId.Trim();
        if (id.Length == 0)
        {
            return;
        }
        NewModelId = "";
        var enabled = Models.Where(m => m.IsEnabled).Select(m => m.Id).ToList();
        if (!enabled.Contains(id))
        {
            enabled.Add(id);
        }
        WriteEnabled(enabled);
    }

    [RelayCommand]
    private void EditModel(ProviderModelItemViewModel model) => Editing = new ModelOverrideEditorViewModel(this, model);

    [RelayCommand]
    private void CloseEditor() => Editing = null;

    /// <summary>在 providers.&lt;id&gt;.models.&lt;modelId&gt; 现有对象上修改（不存在时创建）。</summary>
    internal void WriteModelOverride(string modelId, Action<JsonObject> patch)
    {
        var enabled = Models.Where(m => m.IsEnabled).Select(m => m.Id).ToList();
        Editing = null;
        _owner.Write(root =>
        {
            var provider = SettingsFile.Provider(root, Id);
            var models = EnsureModels(provider, enabled);
            if (models[modelId] is not JsonObject values)
            {
                values = new JsonObject();
                models[modelId] = values;
            }
            patch(values);
        });
    }

    private void WriteEnabled(List<string> enabled) =>
        _owner.Write(root =>
        {
            var provider = SettingsFile.Provider(root, Id);
            var existing = provider["models"] as JsonObject;
            var models = new JsonObject();
            foreach (var id in enabled)
            {
                // 保留用户为该模型写过的覆盖
                models[id] = existing?[id]?.DeepClone() ?? new JsonObject();
            }
            provider["models"] = models;
        });

    private static JsonObject EnsureModels(JsonObject provider, List<string> enabled)
    {
        if (provider["models"] is JsonObject models)
        {
            return models;
        }
        models = new JsonObject();
        foreach (var id in enabled)
        {
            models[id] = new JsonObject();
        }
        provider["models"] = models;
        return models;
    }

    /// <summary>
    /// 每次写设置 Host 都会推来新快照：已有的模型（按 Id）原地更新，列表只做必要的移动、插入和删除。
    /// 整表重建会把每行的 Arc 开关销毁再创建，用户刚拨的开关动画被打断、焦点丢失。
    /// </summary>
    private void RebuildModels()
    {
        var existing = Models.ToDictionary(m => m.Id);
        var desired = new List<ProviderModelItemViewModel>(Dto.Models.Count);
        foreach (var dto in Dto.Models)
        {
            if (existing.Remove(dto.Id, out var item))
            {
                item.Update(dto);
            }
            else
            {
                item = new ProviderModelItemViewModel(dto);
            }
            desired.Add(item);
        }
        SyncList(Models, desired);
        RefreshVisibleModels();
    }

    private void RefreshVisibleModels()
    {
        SyncList(VisibleModels, Models.Where(m => ModelFilter.Length == 0 ||
                                                  m.Id.Contains(ModelFilter, StringComparison.OrdinalIgnoreCase) ||
                                                  m.Name.Contains(ModelFilter, StringComparison.OrdinalIgnoreCase)).ToList());
        OnPropertyChanged(nameof(ModelsSummary));
        OnPropertyChanged(nameof(HasModels));
    }

    /// <summary>把 <paramref name="target"/> 调整成 <paramref name="desired"/>：只移动、插入、删除变了的项，其余行的容器不动。</summary>
    private static void SyncList<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var at = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], desired[i]))
                {
                    at = j;
                    break;
                }
            }
            if (at == i)
            {
                continue;
            }
            if (at > i)
            {
                target.Move(at, i);
            }
            else
            {
                target.Insert(i, desired[i]);
            }
        }
        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

/// <summary>单个模型的覆盖值编辑（写入 providers.&lt;id&gt;.models.&lt;modelId&gt;）。空字段表示使用目录或默认值。</summary>
public sealed partial class ModelOverrideEditorViewModel : ObservableObject
{
    private readonly ProviderDetailViewModel _provider;

    public ModelOverrideEditorViewModel(ProviderDetailViewModel provider, ProviderModelItemViewModel model)
    {
        _provider = provider;
        ModelId = model.Id;
        NameInput = model.Name == model.Id ? "" : model.Name;
        ContextInput = model.Model.ContextWindow.ToString();
        MaxTokensInput = model.Model.MaxTokens > 0 ? model.Model.MaxTokens.ToString() : "";
        Reasoning = model.Reasoning;
        Images = model.Images;
        // 与默认值相同就视为“跟随”，避免把目录的固定规则（NextCoWork 的 Claude → Messages）当成用户覆盖
        var defaultApi = model.Model.DefaultApi ?? provider.Dto.Api;
        ProviderApiText = SettingsTexts.ProviderDefault(defaultApi);
        ApiChoices = [new("", ProviderApiText), new("chat", "Chat"), new("responses", "Responses"), new("messages", "Anthropic")];
        ApiInput = model.Model.Api is { Length: > 0 } effective && effective != defaultApi ? effective : "";
        _original = (NameInput, ContextInput, MaxTokensInput, Reasoning, Images);
        _originalApi = ApiInput;
    }

    private readonly string _originalApi;

    /// <summary>"" 表示跟随厂商；否则为 chat / responses / messages。</summary>
    [ObservableProperty]
    public partial string ApiInput { get; set; }

    /// <summary>“跟随厂商（Chat）”。</summary>
    public string ProviderApiText { get; }

    /// <summary>协议分段选择：第一项跟随厂商，选中项跟随 <see cref="ApiInput"/>。</summary>
    public IReadOnlyList<SettingsChoiceViewModel> ApiChoices { get; }

    partial void OnApiInputChanged(string value)
    {
        foreach (var choice in ApiChoices)
        {
            choice.IsSelected = choice.Value == value;
        }
    }

    [RelayCommand]
    private void ChooseApi(SettingsChoiceViewModel choice) => ApiInput = choice.Value;

    private readonly (string Name, string Context, string MaxTokens, bool Reasoning, bool Images) _original;

    public string ModelId { get; }
    public string Hint => Strings.Format(TKCodeLangResourceKind.ModelOverrideHint, _provider.Id);

    [ObservableProperty]
    public partial string NameInput { get; set; }

    [ObservableProperty]
    public partial string ContextInput { get; set; }

    [ObservableProperty]
    public partial string MaxTokensInput { get; set; }

    [ObservableProperty]
    public partial bool Reasoning { get; set; }

    [ObservableProperty]
    public partial bool Images { get; set; }

    /// <summary>
    /// 只改动本次编辑过的字段，其余已有的覆盖值保持不变（保持配置最小）。
    /// 协议选“跟随厂商”时删除 api 字段。
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        var name = NameInput.Trim();
        var contextText = ContextInput.Trim();
        var maxText = MaxTokensInput.Trim();
        var reasoning = Reasoning;
        var images = Images;
        var api = ApiInput;
        var original = _original;
        var originalApi = _originalApi;
        _provider.WriteModelOverride(ModelId, values =>
        {
            if (name != original.Name)
            {
                SettingsFile.Set(values, "name", name.Length > 0 ? JsonValue.Create(name) : null);
            }
            if (contextText != original.Context && int.TryParse(contextText, out var context) && context > 0)
            {
                values["contextWindow"] = context;
            }
            if (maxText != original.MaxTokens && int.TryParse(maxText, out var max) && max > 0)
            {
                values["maxTokens"] = max;
            }
            if (reasoning != original.Reasoning)
            {
                values["reasoning"] = reasoning;
            }
            if (images != original.Images)
            {
                values["images"] = images;
            }
            if (api != originalApi)
            {
                SettingsFile.Set(values, "api", api.Length > 0 ? JsonValue.Create(api) : null);
            }
        });
    }

    [RelayCommand]
    private void Reset() => _provider.WriteModelOverride(ModelId, values => values.Clear());
}

public enum ProviderTemplateKind
{
    BuiltIn,
    Custom,
}

/// <summary>“添加厂商”选择框中的一个模板：内置厂商，或某种协议的自定义厂商。</summary>
public sealed record ProviderTemplateViewModel(ProviderTemplateKind Kind, string Id, string Name, string? Subtitle, string? LogoKey, string? Api = null)
{
    public bool IsCustom => Kind == ProviderTemplateKind.Custom;
}

public sealed record ProviderTemplateGroupViewModel(string Title, IReadOnlyList<ProviderTemplateViewModel> Items);

/// <summary>差异中的一个字段变化，按字段类型格式化（上下文 200K → 1M，价格 $3 → $2…）。</summary>
public sealed record CatalogFieldChangeItemViewModel(string Label, string OldText, string NewText)
{
    public static CatalogFieldChangeItemViewModel From(CatalogFieldChangeDto change)
    {
        var label = Strings.Get(change.Field switch
        {
            "name" => TKCodeLangResourceKind.FieldName,
            "contextWindow" => TKCodeLangResourceKind.FieldContext,
            "maxTokens" => TKCodeLangResourceKind.FieldMaxTokens,
            "reasoning" => TKCodeLangResourceKind.FieldReasoning,
            "images" => TKCodeLangResourceKind.FieldImages,
            "efforts" => TKCodeLangResourceKind.FieldEfforts,
            "thinkingToggle" => TKCodeLangResourceKind.FieldThinkingToggle,
            "inputPrice" => TKCodeLangResourceKind.FieldInputPrice,
            "outputPrice" => TKCodeLangResourceKind.FieldOutputPrice,
            "baseUrl" => TKCodeLangResourceKind.FieldBaseUrl,
            _ => TKCodeLangResourceKind.FieldApi,
        });
        return new CatalogFieldChangeItemViewModel(label, Format(change.Field, change.OldValue), Format(change.Field, change.NewValue));
    }

    private static string Format(string field, string? value) => value is null ? "—" : field switch
    {
        "contextWindow" or "maxTokens" when int.TryParse(value, out var n) => ModelPickerViewModel.FormatTokens(n),
        "inputPrice" or "outputPrice" => "$" + value,
        "reasoning" or "images" or "thinkingToggle" => Strings.Get(value == "true" ? TKCodeLangResourceKind.Yes : TKCodeLangResourceKind.No),
        "efforts" => value.Replace(",", ", "),
        _ => value,
    };
}

public sealed record CatalogModelChangeItemViewModel(CatalogModelChangeDto Dto)
{
    public string Name => Dto.Name;
    public string Id => Dto.ModelId;
    public bool ShowId => Dto.Name != Dto.ModelId;
    public bool IsAdded => Dto.Kind == CatalogChangeKind.Added;
    public bool IsRemoved => Dto.Kind == CatalogChangeKind.Removed;
    public bool IsChanged => Dto.Kind == CatalogChangeKind.Changed;
    public IReadOnlyList<CatalogFieldChangeItemViewModel> Changes { get; } = Dto.Changes.Select(CatalogFieldChangeItemViewModel.From).ToArray();
}

/// <summary>差异中的一个厂商：厂商级字段变化 + 模型变化（最多显示 maxModels 项）。</summary>
public sealed class CatalogProviderChangeItemViewModel(CatalogProviderChangeDto dto, int maxModels)
{
    public CatalogProviderChangeDto Dto { get; } = dto;
    public string Name => Dto.Name;
    public string? LogoKey { get; } = ModelIcons.ForProvider(dto.ProviderId);
    public bool IsConfigured => Dto.IsConfigured;
    public string KindText => Strings.Get(Dto.Kind switch
    {
        CatalogChangeKind.Added => TKCodeLangResourceKind.ChangeAdded,
        CatalogChangeKind.Removed => TKCodeLangResourceKind.ChangeRemoved,
        _ => TKCodeLangResourceKind.ChangeChanged,
    });
    public IReadOnlyList<CatalogFieldChangeItemViewModel> Changes { get; } = dto.Changes.Select(CatalogFieldChangeItemViewModel.From).ToArray();
    public IReadOnlyList<CatalogModelChangeItemViewModel> Models { get; } = dto.Models.Take(maxModels).Select(m => new CatalogModelChangeItemViewModel(m)).ToArray();
    public string SummaryText => Strings.Format(TKCodeLangResourceKind.CatalogUpdateSummaryFormat,
        Dto.Models.Count(m => m.Kind == CatalogChangeKind.Added), Dto.Models.Count(m => m.Kind == CatalogChangeKind.Removed),
        Dto.Models.Count(m => m.Kind == CatalogChangeKind.Changed));
    public bool HasMore => Dto.Models.Count > maxModels;
    public string MoreText => Strings.Format(TKCodeLangResourceKind.MoreChangesFormat, Dto.Models.Count - maxModels);
}

/// <summary>设置 › 智能体 中的一个外部运行时。</summary>
public sealed record RuntimeItemViewModel(RuntimeDto Dto)
{
    public string Name => Dto.Name;
    public string Id => Dto.Id;
    public string? LogoKey => ModelIcons.ForProvider(Dto.Id);
    public bool IsReady => Dto.Status == RuntimeStatusDto.Ready;
    public bool IsError => Dto.Status == RuntimeStatusDto.Error;
    public string StatusText => Strings.Get(Dto.Status switch
    {
        RuntimeStatusDto.Ready => TKCodeLangResourceKind.RuntimeReady,
        RuntimeStatusDto.NotInstalled => TKCodeLangResourceKind.RuntimeNotInstalled,
        RuntimeStatusDto.Error => TKCodeLangResourceKind.RuntimeError,
        _ => TKCodeLangResourceKind.RuntimeUnknown,
    });
    public string? VersionText => Dto.Version is { } v ? Strings.Format(TKCodeLangResourceKind.RuntimeVersionFormat, v) : null;
    public string? AuthText => Dto.AuthMethod is { } a ? Strings.Format(TKCodeLangResourceKind.RuntimeAuthFormat, a) : null;
    public string ModelsText => Strings.Format(TKCodeLangResourceKind.ModelCountFormat, Dto.ModelCount);
    public bool HasVersion => VersionText is not null;
    public bool HasAuth => AuthText is not null;
    public bool HasPath => Dto.ExecutablePath is not null;
    public bool HasError => Dto.Error is not null;
}

internal static class SettingsTexts
{
    /// <summary>“跟随厂商（Chat）”。</summary>
    public static string ProviderDefault(string providerApi) =>
        $"{Strings.Get(TKCodeLangResourceKind.ProtocolFollowProvider)} ({ProviderModelItemViewModel.ProtocolLabel(providerApi)})";
}

public sealed record CatalogProviderItemViewModel(CatalogProviderDto Dto)
{
    public string Id => Dto.Id;
    public string Name => Dto.Name;
    public string? LogoKey => ModelIcons.ForProvider(Dto.Id);
    /// <summary>目录里没有模型的厂商（模型靠 /models 发现，如 NextCoWork）显示地址，而不是“0 个模型”。</summary>
    public string Meta => Dto.ModelCount > 0 ? Strings.Format(TKCodeLangResourceKind.ModelCountFormat, Dto.ModelCount) : Dto.BaseUrl;
}

/// <summary>
/// 设置页：通用（语言 / 主题 / 默认模型 / 配置文件）、模型与厂商、关于。
/// 读：用户配置来自 <see cref="UserSettingsService"/>，厂商状态来自 Host 的 providers/list；
/// 写：修改 settings.json 后请求 Host 立即重载（文件监听同样会触发），再刷新厂商状态。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly Regex ProviderIdPattern = new("^[A-Za-z0-9_-]+$");
    private readonly UserSettingsService _settings;
    private readonly AgentHostConnection _host;
    private readonly ConversationViewModel _conversation;
    private readonly ThemeService _themes;
    private readonly Func<string, Task> _setLanguage;
    private readonly ILogger _logger;
    private IReadOnlyList<CatalogProviderItemViewModel> _catalog = [];

    public SettingsViewModel(UserSettingsService settings, AgentHostConnection host, ConversationViewModel conversation, ThemeService themes,
        Func<string, Task> setLanguage, ILogger logger)
    {
        _settings = settings;
        _host = host;
        _conversation = conversation;
        _themes = themes;
        _setLanguage = setLanguage;
        _logger = logger;
        Nav =
        [
            new(SettingsSection.General, LucideKind.SlidersHorizontal, TKCodeLangResourceKind.SettingsGeneral) { IsSelected = true },
            new(SettingsSection.Providers, LucideKind.Box, TKCodeLangResourceKind.SettingsProviders),
            new(SettingsSection.Agents, LucideKind.Bot, TKCodeLangResourceKind.SettingsAgents),
            new(SettingsSection.Mcp, LucideKind.Plug, TKCodeLangResourceKind.SettingsMcp),
            new(SettingsSection.Assets, LucideKind.SquareTerminal, TKCodeLangResourceKind.SettingsAssets),
            new(SettingsSection.About, LucideKind.CircleAlert, TKCodeLangResourceKind.SettingsAbout),
        ];
        settings.Changed += () =>
        {
            RefreshGeneral();
            _ = RefreshProvidersAsync();
        };
        AgentShell = new ShellPickerViewModel(ShellUse.Agent, TKCodeLangResourceKind.AgentShellLabel, TKCodeLangResourceKind.AgentShellHint,
            () => settings.Current.Agent?.Shell?.Profile, settings.SetAgentShell);
        TerminalShell = new ShellPickerViewModel(ShellUse.Terminal, TKCodeLangResourceKind.TerminalShellLabel, TKCodeLangResourceKind.TerminalShellHint,
            () => settings.Current.Terminal?.DefaultProfile, settings.SetTerminalShell);
        Mcp = new McpSettingsViewModel(settings, host, () => conversation.Active?.Cwd ?? conversation.Workspace?.Primary, logger);
        Assets = new AssetsSettingsViewModel(host, settings, () => conversation.CurrentCwd, logger);
        conversation.ModelsChanged += OnModelsChanged;
        Strings.LanguageChanged += (_, _) => RefreshLanguage();
        RefreshGeneral();
    }

    private void OnModelsChanged(ModelsChangedNotification notification)
    {
        RefreshDefaultModels();
        _ = RefreshProvidersAsync();
        _ = RefreshCatalogStatusAsync();
        // 运行时检测完成后 Host 会推送 models/changed
        _ = RefreshRuntimesAsync();
    }

    public IReadOnlyList<SettingsNavItemViewModel> Nav { get; }

    /// <summary>设置 › MCP 服务器。</summary>
    public McpSettingsViewModel Mcp { get; }

    /// <summary>设置 › 命令、Skills 与模式。</summary>
    public AssetsSettingsViewModel Assets { get; }
    public ObservableCollection<SettingsChoiceViewModel> Languages { get; } = [];
    public ObservableCollection<SettingsChoiceViewModel> Themes { get; } = [];
    public ObservableCollection<SettingsChoiceViewModel> CacheTtls { get; } = [];
    /// <summary>界面缩放的分段档位（<see cref="UiScaleLevels.Stops"/>）；当前值不在档位上（手改 settings.json）时没有选中项。</summary>
    public ObservableCollection<SettingsChoiceViewModel> UiScales { get; } = [];
    public ObservableCollection<SettingsChoiceViewModel> DefaultModels { get; } = [];
    public ObservableCollection<SettingsChoiceViewModel> FastModels { get; } = [];

    /// <summary>Agent 的 bash 工具使用的 shell（settings.json 的 agent.shell.profile）。</summary>
    public ShellPickerViewModel AgentShell { get; }

    /// <summary>新建终端默认使用的 shell（settings.json 的 terminal.defaultProfile）。</summary>
    public ShellPickerViewModel TerminalShell { get; }
    /// <summary>左栏“已启用”：settings.json 中已有，或检测到环境变量 Key 的厂商。</summary>
    public ObservableCollection<ProviderListItemViewModel> ConfiguredList { get; } = [];
    /// <summary>“添加厂商”选择框的分组（常用 / 本地 / 自定义 / 全部内置厂商）。</summary>
    public ObservableCollection<ProviderTemplateGroupViewModel> TemplateGroups { get; } = [];

    public bool HasNoProviders => ConfiguredList.Count == 0;
    public ObservableCollection<SettingsChoiceViewModel> Protocols { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];

    public string SettingsPath => _settings.FilePath;
    public string DataDirectory => ConfigPaths.Home;
    public string VersionDisplay => TKCode.Desktop.Updates.AppVersionInfo.Display;
    public static string RepositoryUrl => $"https://github.com/{TKCode.Desktop.Updates.GitHubReleaseFeed.Repository}";
    public bool HasErrors => Errors.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral), nameof(IsProviders), nameof(IsAgents), nameof(IsMcp), nameof(IsAssets), nameof(IsAbout), nameof(IsScrollSection))]
    public partial SettingsSection Section { get; private set; }

    public bool IsGeneral => Section == SettingsSection.General;
    public bool IsProviders => Section == SettingsSection.Providers;
    public bool IsAbout => Section == SettingsSection.About;
    public bool IsAgents => Section == SettingsSection.Agents;
    public bool IsMcp => Section == SettingsSection.Mcp;
    public bool IsAssets => Section == SettingsSection.Assets;

    /// <summary>通用 / 外部智能体 / 关于：共用一个滚动区域；主从双栏的分区各自滚动。</summary>
    public bool IsScrollSection => !IsProviders && !IsMcp;

    // ---- 外部智能体（自动发现，免配置） ----

    /// <summary>已安装（就绪 / 出错 / 检测中）的运行时。</summary>
    public ObservableCollection<RuntimeItemViewModel> InstalledRuntimes { get; } = [];

    /// <summary>未安装的运行时（默认折叠）。</summary>
    public ObservableCollection<RuntimeItemViewModel> MissingRuntimes { get; } = [];

    [ObservableProperty]
    public partial bool ShowMissingRuntimes { get; set; }

    public bool HasMissingRuntimes => MissingRuntimes.Count > 0;
    public bool HasNoRuntimes => InstalledRuntimes.Count == 0 && MissingRuntimes.Count == 0;
    public string MissingRuntimesTitle => Strings.Format(TKCodeLangResourceKind.NotInstalledGroupFormat, MissingRuntimes.Count);

    [RelayCommand]
    private void ToggleMissingRuntimes() => ShowMissingRuntimes = !ShowMissingRuntimes;

    public async Task RefreshRuntimesAsync()
    {
        IReadOnlyList<RuntimeDto> runtimes;
        try
        {
            var server = await _host.GetServerAsync();
            runtimes = await server.ListRuntimesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "List runtimes failed");
            return;
        }
        InstalledRuntimes.Clear();
        MissingRuntimes.Clear();
        foreach (var runtime in runtimes.OrderBy(r => r.Status switch { RuntimeStatusDto.Ready => 0, RuntimeStatusDto.Error => 1, RuntimeStatusDto.Unknown => 2, _ => 3 })
                     .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            (runtime.Status == RuntimeStatusDto.NotInstalled ? MissingRuntimes : InstalledRuntimes).Add(new RuntimeItemViewModel(runtime));
        }
        OnPropertyChanged(nameof(HasMissingRuntimes));
        OnPropertyChanged(nameof(HasNoRuntimes));
        OnPropertyChanged(nameof(MissingRuntimesTitle));
    }

    [ObservableProperty]
    public partial string? CatalogText { get; private set; }

    [ObservableProperty]
    public partial string ProviderSearch { get; set; } = "";

    /// <summary>右栏当前显示的厂商。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ProviderDetailViewModel? Selected { get; private set; }

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    public partial bool IsCreatingCustom { get; private set; }

    public bool ShowEmptyDetail => Selected is null && !IsCreatingCustom;

    partial void OnSelectedChanged(ProviderDetailViewModel? value) => OnPropertyChanged(nameof(ShowEmptyDetail));

    partial void OnIsCreatingCustomChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyDetail));

    partial void OnProviderSearchChanged(string value) => RebuildLists();

    [ObservableProperty]
    public partial bool IsPickingTemplate { get; private set; }

    [ObservableProperty]
    public partial string TemplateSearch { get; set; } = "";

    partial void OnTemplateSearchChanged(string value) => RebuildTemplates();

    /// <summary>从选择框选中、尚未保存的内置厂商；保存后写入 settings.json，离开则丢弃。</summary>
    private string? _draftId;

    [ObservableProperty]
    public partial string CustomId { get; set; } = "";

    [ObservableProperty]
    public partial string CustomName { get; set; } = "";

    [ObservableProperty]
    public partial string CustomBaseUrl { get; set; } = "";

    [ObservableProperty]
    public partial string CustomApiKey { get; set; } = "";


    [ObservableProperty]
    public partial string? CustomError { get; private set; }


    /// <summary>打开设置页时刷新一次厂商状态与目录状态。</summary>
    public async Task OpenAsync(SettingsSection? section = null)
    {
        if (section is { } s)
        {
            Navigate(Nav.First(n => n.Section == s));
        }
        RefreshGeneral();
        await RefreshProvidersAsync();
        await RefreshCatalogStatusAsync();
        await RefreshRuntimesAsync();
        await Mcp.RefreshAsync();
    }

    // ---- 模型目录（models.dev，手动检查 → 查看差异 → 应用） ----

    private const int MaxModelChangesShown = 20;

    [ObservableProperty]
    public partial CatalogStatusDto? Catalog { get; private set; }

    /// <summary>最近一次检查的结果；HasPending 时显示差异与“应用 / 放弃”。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingUpdate), nameof(IsUpToDate), nameof(UpdateSummaryText))]
    public partial CatalogUpdateDto? CatalogUpdate { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCatalogCommand), nameof(ApplyCatalogUpdateCommand))]
    public partial bool IsCheckingCatalog { get; private set; }

    /// <summary>只显示用户已添加厂商的差异（默认开启，与之无关的大量变化可以展开查看）。</summary>
    [ObservableProperty]
    public partial bool ShowOnlyConfiguredChanges { get; set; } = true;

    public ObservableCollection<CatalogProviderChangeItemViewModel> CatalogChanges { get; } = [];

    public bool HasPendingUpdate => CatalogUpdate is { HasPending: true };
    public bool IsUpToDate => CatalogUpdate is { UpToDate: true };
    public bool HasNoVisibleChanges => HasPendingUpdate && CatalogChanges.Count == 0;
    public string UpdateSummaryText => CatalogUpdate is { HasPending: true } u
        ? Strings.Format(TKCodeLangResourceKind.CatalogUpdateSummaryFormat, u.AddedModels, u.RemovedModels, u.ChangedModels)
        : "";

    public string CatalogSourceText => Catalog is not { } c ? ""
        : c.Source == "synced" && c.SyncedAt is { } at
            ? Strings.Format(TKCodeLangResourceKind.CatalogSyncedFormat, at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentUICulture))
            : Strings.Format(TKCodeLangResourceKind.CatalogEmbeddedFormat, c.Generated ?? "");

    public string CatalogCountsText => Catalog is { } c ? Strings.Format(TKCodeLangResourceKind.CatalogCountsFormat, c.ProviderCount, c.ModelCount) : "";
    public string? CatalogErrorText => Catalog?.LastError is { } e ? Strings.Format(TKCodeLangResourceKind.SyncFailedFormat, e) : null;
    public bool HasCatalogError => Catalog?.LastError is not null;

    partial void OnCatalogChanged(CatalogStatusDto? value)
    {
        OnPropertyChanged(nameof(CatalogSourceText));
        OnPropertyChanged(nameof(CatalogCountsText));
        OnPropertyChanged(nameof(CatalogErrorText));
        OnPropertyChanged(nameof(HasCatalogError));
    }

    partial void OnCatalogUpdateChanged(CatalogUpdateDto? value) => RebuildCatalogChanges();

    partial void OnShowOnlyConfiguredChangesChanged(bool value) => RebuildCatalogChanges();

    public async Task RefreshCatalogStatusAsync()
    {
        try
        {
            var server = await _host.GetServerAsync();
            Catalog = await server.GetCatalogStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog status failed");
        }
    }

    private bool CanUseCatalog() => !IsCheckingCatalog;

    /// <summary>下载并比较，不会改变当前目录。</summary>
    [RelayCommand(CanExecute = nameof(CanUseCatalog))]
    private async Task CheckCatalogAsync()
    {
        IsCheckingCatalog = true;
        try
        {
            var server = await _host.GetServerAsync();
            var update = await server.CheckCatalogAsync();
            // 已添加的厂商没有变化时直接展示全部差异
            ShowOnlyConfiguredChanges = update.Providers.Any(p => p.IsConfigured);
            CatalogUpdate = update;
            Catalog = update.Status;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog check failed");
            Catalog = Catalog is { } c ? c with { LastError = ex.Message } : null;
        }
        finally
        {
            IsCheckingCatalog = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseCatalog))]
    private async Task ApplyCatalogUpdateAsync()
    {
        IsCheckingCatalog = true;
        try
        {
            var server = await _host.GetServerAsync();
            Catalog = await server.ApplyCatalogUpdateAsync();
            CatalogUpdate = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Applying catalog update failed");
            Catalog = Catalog is { } c ? c with { LastError = ex.Message } : null;
        }
        finally
        {
            IsCheckingCatalog = false;
        }
        await RefreshProvidersAsync();
    }

    [RelayCommand]
    private async Task DiscardCatalogUpdateAsync()
    {
        CatalogUpdate = null;
        try
        {
            var server = await _host.GetServerAsync();
            await server.DiscardCatalogUpdateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Discard catalog update failed");
        }
    }

    private void RebuildCatalogChanges()
    {
        CatalogChanges.Clear();
        foreach (var provider in CatalogUpdate?.Providers ?? [])
        {
            if (!ShowOnlyConfiguredChanges || provider.IsConfigured)
            {
                CatalogChanges.Add(new CatalogProviderChangeItemViewModel(provider, MaxModelChangesShown));
            }
        }
        OnPropertyChanged(nameof(HasNoVisibleChanges));
    }

    [RelayCommand]
    private void Navigate(SettingsNavItemViewModel item)
    {
        foreach (var n in Nav)
        {
            n.IsSelected = n == item;
        }
        Section = item.Section;
        if (item.Section == SettingsSection.Assets)
        {
            _ = Assets.RefreshAsync();
        }
    }

    /// <summary>Markdown 图片按原始清晰度显示（settings.json 的 originalImages）。</summary>
    public bool OriginalImages
    {
        get => _settings.Current.OriginalImages == true;
        set
        {
            if (value != OriginalImages)
            {
                _settings.SetOriginalImages(value);
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private Task ChooseLanguageAsync(SettingsChoiceViewModel choice) => _setLanguage(choice.Value);

    /// <summary>减少动态效果（settings.json 的 reduceMotion）。</summary>
    /// <summary>关闭窗口时隐藏到托盘（默认开启）。</summary>
    public bool CloseToTray
    {
        get => _settings.Current.CloseToTray != false;
        set
        {
            if (value != CloseToTray)
            {
                _settings.SetCloseToTray(value);
            }
        }
    }

    public bool ReduceMotion
    {
        get => _settings.Current.ReduceMotion == true;
        set
        {
            if (value != ReduceMotion)
            {
                _settings.SetReduceMotion(value);
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private Task ChooseThemeAsync(SettingsChoiceViewModel choice) => _themes.SetModeAsync(Enum.Parse<ThemeMode>(choice.Value));

    /// <summary>提示缓存有效期（settings.json 的 promptCacheTtl）；Host 每次请求读取，进行中的会话也立即生效。</summary>
    [RelayCommand]
    private void ChooseCacheTtl(SettingsChoiceViewModel choice) => _settings.SetPromptCacheTtl(choice.Value);

    /// <summary>界面缩放（settings.json 的 uiScale）：写回后由 App 同步到窗口，立即生效。</summary>
    [RelayCommand]
    private void ChooseUiScale(SettingsChoiceViewModel choice) => _settings.SetUiScale(double.Parse(choice.Value, CultureInfo.InvariantCulture));

    /// <summary>界面缩放行的说明，含当前平台的快捷键修饰键。</summary>
    public string UiScaleHint => Strings.Format(TKCodeLangResourceKind.UiScaleHint, OperatingSystem.IsMacOS() ? "⌘" : "Ctrl");

    [RelayCommand]
    private void ChooseDefaultModel(SettingsChoiceViewModel choice) => _settings.SetDefaultModel(choice.Value.Length == 0 ? null : choice.Value);

    [RelayCommand]
    private void ChooseFastModel(SettingsChoiceViewModel choice) => _settings.SetFastModel(choice.Value.Length == 0 ? null : choice.Value);

    [RelayCommand]
    private void OpenSettingsFile()
    {
        if (!File.Exists(SettingsPath))
        {
            _settings.Update(_ => { });
        }
        OpenWithShell(SettingsPath);
    }

    [RelayCommand]
    private void RevealSettingsFile() => OpenWithShell(Path.GetDirectoryName(SettingsPath)!);

    [RelayCommand]
    private void OpenRepository() => OpenWithShell(RepositoryUrl);

    private readonly Dictionary<string, ProviderDetailViewModel> _details = new(StringComparer.Ordinal);
    private ProvidersSnapshotDto? _snapshot;
    private string? _pendingSelect;

    [RelayCommand]
    private void SelectProvider(ProviderListItemViewModel item)
    {
        IsCreatingCustom = false;
        if (item.Id != _draftId && _draftId is not null)
        {
            _draftId = null;
            RebuildLists();
        }
        Select(item.Id);
    }

    [RelayCommand]
    private void OpenTemplatePicker()
    {
        TemplateSearch = "";
        RebuildTemplates();
        IsPickingTemplate = true;
    }

    [RelayCommand]
    private void CloseTemplatePicker() => IsPickingTemplate = false;

    /// <summary>选择模板：内置厂商进入草稿详情（填 Key 后保存）；自定义模板进入自定义表单并预选协议。</summary>
    [RelayCommand]
    private void PickTemplate(ProviderTemplateViewModel template)
    {
        IsPickingTemplate = false;
        if (template.IsCustom)
        {
            StartCustomProvider(template.Api ?? "chat");
            return;
        }
        IsCreatingCustom = false;
        if (_snapshot?.Providers.FirstOrDefault(p => p.Id == template.Id) is not { IsConfigured: true })
        {
            _draftId = template.Id;
        }
        RebuildLists();
        Select(template.Id);
    }

    private void StartCustomProvider(string api)
    {
        Selected = null;
        IsCreatingCustom = true;
        CustomError = null;
        foreach (var p in Protocols)
        {
            p.IsSelected = p.Value == api;
        }
        foreach (var item in ConfiguredList)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    private void ChooseProtocol(SettingsChoiceViewModel choice)
    {
        foreach (var p in Protocols)
        {
            p.IsSelected = p == choice;
        }
    }

    /// <summary>创建自定义厂商。模型列表留空，由 Host 通过 GET /models 自动获取。</summary>
    [RelayCommand]
    private void CreateCustomProvider()
    {
        var id = CustomId.Trim();
        var baseUrl = CustomBaseUrl.Trim();
        if (!ProviderIdPattern.IsMatch(id) || _snapshot?.Providers.Any(p => p.Id == id) == true || _catalog.Any(c => c.Id == id))
        {
            CustomError = Strings.Get(TKCodeLangResourceKind.ProviderIdInvalid);
            return;
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            CustomError = Strings.Format(TKCodeLangResourceKind.TestFailedFormat, Strings.Get(TKCodeLangResourceKind.BaseUrlLabel));
            return;
        }
        CustomError = null;
        var name = CustomName.Trim();
        var key = CustomApiKey.Trim();
        var api = Protocols.FirstOrDefault(p => p.IsSelected)?.Value ?? "chat";
        _pendingSelect = id;
        Write(root =>
        {
            var provider = SettingsFile.Provider(root, id);
            if (name.Length > 0)
            {
                provider["name"] = name;
            }
            provider["api"] = api;
            provider["baseUrl"] = baseUrl;
            provider["apiKey"] = key;
        });
        IsCreatingCustom = false;
        CustomId = CustomName = CustomBaseUrl = CustomApiKey = "";
    }

    /// <summary>确认框（由主窗口提供）：标题、正文、确定按钮文字。</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// 移除厂商。草稿只是放弃（不写文件）；其余先确认。
    /// 靠环境变量被检测出来的内置厂商删掉条目后仍会被检测到，所以改写成 { "hidden": true }，重新添加时清除。
    /// </summary>
    internal async Task RemoveProviderAsync(ProviderDetailViewModel provider)
    {
        var id = provider.Id;
        if (provider.IsPureDraft)
        {
            DiscardDraft(id);
            return;
        }
        var variable = provider.IsBuiltIn ? DetectedEnvironmentVariable(id) : null;
        var text = variable is null
            ? Strings.Get(TKCodeLangResourceKind.ProviderRemoveConfirmText)
            : Strings.Format(TKCodeLangResourceKind.ProviderHideConfirmText, variable);
        if (ConfirmAsync is not { } confirm
            || !await confirm(Strings.Format(TKCodeLangResourceKind.ProviderRemoveConfirmTitle, provider.Name), text, Strings.Get(TKCodeLangResourceKind.Remove)))
        {
            return;
        }
        Selected = null;
        _details.Remove(id);
        Write(root =>
        {
            SettingsFile.RemoveProvider(root, id);
            if (variable is not null)
            {
                SettingsFile.Provider(root, id)["hidden"] = true;
            }
        });
    }

    private void DiscardDraft(string id)
    {
        if (_draftId == id)
        {
            _draftId = null;
        }
        _details.Remove(id);
        Selected = null;
        RebuildLists();
        if (ConfiguredList.FirstOrDefault() is { } first)
        {
            Select(first.Id);
        }
    }

    /// <summary>内置厂商登记的环境变量中已设置的那个；没有则为 null。</summary>
    private string? DetectedEnvironmentVariable(string id) =>
        _catalog.FirstOrDefault(c => c.Id == id)?.Dto.EnvVariables
            .FirstOrDefault(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)));

    internal string? CatalogBaseUrl(string id) => _catalog.FirstOrDefault(c => c.Id == id)?.Dto.BaseUrl;

    internal async Task<ProviderTestResult> TestAsync(ProviderTestRequest request)
    {
        try
        {
            var server = await _host.GetServerAsync();
            return await server.TestProviderAsync(request);
        }
        catch (Exception ex)
        {
            return new ProviderTestResult(false, null, 0, ex.Message);
        }
    }

    internal async Task RefreshModelsAsync(string id)
    {
        try
        {
            var server = await _host.GetServerAsync();
            await server.RefreshProviderModelsAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh models failed");
        }
        await RefreshProvidersAsync();
    }

    /// <summary>写回 settings.json 并让 Host 立即重载。</summary>
    internal void Write(Action<JsonObject> mutate)
    {
        _settings.Update(mutate);
        _ = ReloadHostAsync();
    }

    private async Task ReloadHostAsync()
    {
        try
        {
            var server = await _host.GetServerAsync();
            await server.ReloadSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reload settings failed");
        }
        await RefreshProvidersAsync();
    }

    public async Task RefreshProvidersAsync()
    {
        ProvidersSnapshotDto snapshot;
        try
        {
            var server = await _host.GetServerAsync();
            snapshot = await server.ListProvidersAsync();
            if (_catalog.Count == 0)
            {
                _catalog = (await server.ListCatalogProvidersAsync()).Select(p => new CatalogProviderItemViewModel(p)).ToArray();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "List providers failed");
            return;
        }
        _snapshot = snapshot;
        CatalogText = snapshot.CatalogDate is { } date ? Strings.Format(TKCodeLangResourceKind.AboutCatalogFormat, date) : null;
        foreach (var dto in snapshot.Providers)
        {
            if (_details.TryGetValue(dto.Id, out var detail))
            {
                detail.Update(dto);
            }
        }
        RebuildLists();
        if (_pendingSelect is { } pending && snapshot.Providers.Any(p => p.Id == pending))
        {
            _pendingSelect = null;
            Select(pending);
        }
        else if (Selected is { } current)
        {
            Select(current.Id);
        }
        else if (!IsCreatingCustom && ConfiguredList.FirstOrDefault() is { } first)
        {
            Select(first.Id);
        }
        Errors.Clear();
        foreach (var error in _settings.Errors.Concat(snapshot.Errors).Distinct())
        {
            Errors.Add(error);
        }
        OnPropertyChanged(nameof(HasErrors));
        RefreshDefaultModels();
    }

    private void Select(string id)
    {
        var dto = _snapshot?.Providers.FirstOrDefault(p => p.Id == id) ?? FromCatalog(id);
        if (dto is null)
        {
            return;
        }
        if (!_details.TryGetValue(id, out var detail))
        {
            detail = new ProviderDetailViewModel(this, dto);
            _details[id] = detail;
        }
        else if (detail.Dto != dto)
        {
            detail.Update(dto);
        }
        Selected = detail;
        foreach (var item in ConfiguredList)
        {
            item.IsSelected = item.Id == id;
        }
    }

    /// <summary>目录中尚未列出的厂商：合成一个“未配置”的快照，保存 Key 后才写入 settings.json。</summary>
    private ProviderDto? FromCatalog(string id) =>
        _catalog.FirstOrDefault(c => c.Id == id) is { } c
            ? new ProviderDto(c.Id, c.Name, c.Dto.Api, c.Dto.BaseUrl, true, false, true, false,
                c.Dto.KeyOptional ? KeySourceDto.NotRequired : KeySourceDto.None, c.Dto.EnvVariables.FirstOrDefault(), null, false, [], [])
            : null;

    /// <summary>左栏：已配置或检测到环境变量 Key 的厂商，以及当前草稿。</summary>
    private void RebuildLists()
    {
        var providers = _snapshot?.Providers ?? [];
        // 草稿保存后成为已配置厂商
        if (_draftId is { } draft && providers.Any(p => p.Id == draft && p.IsConfigured))
        {
            _draftId = null;
        }
        ConfiguredList.Clear();
        bool Matches(string id, string name) => ProviderSearch.Length == 0 ||
                                                 id.Contains(ProviderSearch, StringComparison.OrdinalIgnoreCase) ||
                                                 name.Contains(ProviderSearch, StringComparison.OrdinalIgnoreCase);
        if (_draftId is { } draftId && (providers.FirstOrDefault(p => p.Id == draftId) ?? FromCatalog(draftId)) is { } draftDto)
        {
            ConfiguredList.Add(new ProviderListItemViewModel(draftDto.Id, draftDto.Name, ProviderStatus.NotConfigured, false));
        }
        foreach (var dto in providers.Where(p => p.Id != _draftId && (p.IsConfigured || p.KeySource == KeySourceDto.Environment) && Matches(p.Id, p.Name))
                     .OrderByDescending(p => p.IsActive).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            ConfiguredList.Add(new ProviderListItemViewModel(dto.Id, dto.Name, ProviderDetailViewModel.ComputeStatus(dto), true));
        }
        foreach (var item in ConfiguredList)
        {
            item.IsSelected = item.Id == Selected?.Id;
        }
        OnPropertyChanged(nameof(HasNoProviders));
    }

    private static readonly string[] PopularProviders = ["nextcowork", "anthropic", "openai", "deepseek", "moonshotai-cn", "zhipuai", "alibaba-cn", "openrouter", "volcengine", "siliconflow-cn", "minimax-cn"];
    private static readonly string[] LocalProviders = ["ollama", "lmstudio"];

    private void RebuildTemplates()
    {
        TemplateGroups.Clear();
        var configured = (_snapshot?.Providers ?? []).Where(p => p.IsConfigured).Select(p => p.Id).ToHashSet();
        bool Matches(string id, string name) => TemplateSearch.Length == 0 ||
                                                 id.Contains(TemplateSearch, StringComparison.OrdinalIgnoreCase) ||
                                                 name.Contains(TemplateSearch, StringComparison.OrdinalIgnoreCase);
        ProviderTemplateViewModel BuiltIn(CatalogProviderItemViewModel c) =>
            new(ProviderTemplateKind.BuiltIn, c.Id, c.Name, c.Meta, c.LogoKey);

        void Group(TKCodeLangResourceKind title, IEnumerable<ProviderTemplateViewModel> items)
        {
            var list = items.ToList();
            if (list.Count > 0)
            {
                TemplateGroups.Add(new ProviderTemplateGroupViewModel(Strings.Get(title), list));
            }
        }

        var byId = _catalog.ToDictionary(c => c.Id);
        Group(TKCodeLangResourceKind.TemplatePopular, PopularProviders
            .Where(id => byId.ContainsKey(id) && !configured.Contains(id))
            .Select(id => BuiltIn(byId[id]))
            .Where(t => Matches(t.Id, t.Name)));
        Group(TKCodeLangResourceKind.TemplateLocal, LocalProviders
            .Where(id => byId.ContainsKey(id) && !configured.Contains(id))
            .Select(id => BuiltIn(byId[id]) with { Subtitle = byId[id].Dto.BaseUrl })
            .Where(t => Matches(t.Id, t.Name)));
        Group(TKCodeLangResourceKind.TemplateCustom, new[]
        {
            new ProviderTemplateViewModel(ProviderTemplateKind.Custom, "custom-chat", Strings.Get(TKCodeLangResourceKind.TemplateChat),
                Strings.Get(TKCodeLangResourceKind.TemplateChatHint), "openai", "chat"),
            new ProviderTemplateViewModel(ProviderTemplateKind.Custom, "custom-responses", Strings.Get(TKCodeLangResourceKind.TemplateResponses),
                Strings.Get(TKCodeLangResourceKind.TemplateResponsesHint), "openai", "responses"),
            new ProviderTemplateViewModel(ProviderTemplateKind.Custom, "custom-messages", Strings.Get(TKCodeLangResourceKind.TemplateMessages),
                Strings.Get(TKCodeLangResourceKind.TemplateMessagesHint), "anthropic", "messages"),
        }.Where(t => Matches(t.Id, t.Name)));
        // 全部内置厂商：无搜索词时按名称列出，避免一次渲染上百项时过长
        var shown = TemplateGroups.SelectMany(g => g.Items).Select(t => t.Id).ToHashSet();
        Group(TKCodeLangResourceKind.TemplateAll, _catalog
            .Where(c => !configured.Contains(c.Id) && !shown.Contains(c.Id) && Matches(c.Id, c.Name))
            .Take(TemplateSearch.Length == 0 ? 60 : 200)
            .Select(BuiltIn));
    }

    private void RefreshGeneral()
    {
        OnPropertyChanged(nameof(ReduceMotion));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(OriginalImages));
        var language = _settings.Current.Language ?? "zh-CN";
        SettingsChoiceViewModel.Fill(Languages, [("zh-CN", Strings.Get(TKCodeLangResourceKind.LanguageChinese)), ("en-US", Strings.Get(TKCodeLangResourceKind.LanguageEnglish))], language);
        SettingsChoiceViewModel.Fill(Themes,
        [
            (nameof(ThemeMode.System), Strings.Get(TKCodeLangResourceKind.ThemeSystem)),
            (nameof(ThemeMode.Dark), Strings.Get(TKCodeLangResourceKind.ThemeDark)),
            (nameof(ThemeMode.Light), Strings.Get(TKCodeLangResourceKind.ThemeLight)),
        ], _settings.Theme.ToString());
        SettingsChoiceViewModel.Fill(CacheTtls,
        [
            ("5m", Strings.Get(TKCodeLangResourceKind.CacheTtl5m)),
            ("1h", Strings.Get(TKCodeLangResourceKind.CacheTtl1h)),
        ], _settings.Current.PromptCacheTtl == "1h" ? "1h" : "5m");
        OnPropertyChanged(nameof(UiScaleHint));
        var scale = _settings.UiScale;
        var currentStop = UiScaleLevels.Stops.Where(s => UiScaleLevels.Same(s, scale)).Select(ScaleKey).FirstOrDefault() ?? "";
        SettingsChoiceViewModel.Fill(UiScales, UiScaleLevels.Stops.Select(s => (ScaleKey(s), $"{s * 100:0}%")), currentStop);
        var protocol = Protocols.FirstOrDefault(p => p.IsSelected)?.Value ?? "chat";
        SettingsChoiceViewModel.Fill(Protocols,
        [
            ("chat", Strings.Get(TKCodeLangResourceKind.ProtocolChat)),
            ("responses", Strings.Get(TKCodeLangResourceKind.ProtocolResponses)),
            ("messages", Strings.Get(TKCodeLangResourceKind.ProtocolMessages)),
        ], protocol);
        RefreshDefaultModels();
        AgentShell.Refresh();
        TerminalShell.Refresh();
        Errors.Clear();
        foreach (var error in _settings.Errors)
        {
            Errors.Add(error);
        }
        OnPropertyChanged(nameof(HasErrors));
    }

    private void RefreshDefaultModels()
    {
        var models = _conversation.Picker.AllModels;
        var choices = new List<(string, string)> { ("", Strings.Get(TKCodeLangResourceKind.FirstAvailableModel)) };
        choices.AddRange(models.Select(m => (m.Ref, $"{m.ProviderName} · {m.Name}")));
        SettingsChoiceViewModel.Fill(DefaultModels, choices, _settings.Current.Model ?? "");
        DefaultModelLabel = DefaultModels.FirstOrDefault(c => c.IsSelected)?.Label ?? _settings.Current.Model ?? "";

        var fastChoices = new List<(string, string)> { ("", Strings.Get(TKCodeLangResourceKind.FollowSessionModel)) };
        fastChoices.AddRange(models.Where(m => m.Runtime is null).Select(m => (m.Ref, $"{m.ProviderName} · {m.Name}")));
        SettingsChoiceViewModel.Fill(FastModels, fastChoices, _settings.Current.FastModel ?? "");
        FastModelLabel = FastModels.FirstOrDefault(c => c.IsSelected)?.Label ?? _settings.Current.FastModel ?? "";
    }

    [ObservableProperty]
    public partial string DefaultModelLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string FastModelLabel { get; private set; } = "";

    private void RefreshLanguage()
    {
        foreach (var n in Nav)
        {
            n.RefreshLanguage();
        }
        foreach (var p in _details.Values)
        {
            p.RefreshLanguage();
        }
        Mcp.RefreshLanguage();
        RefreshGeneral();
        _ = RefreshProvidersAsync();
    }

    private static string ScaleKey(double scale) => scale.ToString("0.##", CultureInfo.InvariantCulture);

    private void OpenWithShell(string path) => ShellOpen.Open(path, _logger);
}


using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TKCode.Config;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>设置 › MCP 服务器左栏中的一行。</summary>
public sealed partial class McpServerItemViewModel(McpServerDto dto) : ObservableObject
{
    public McpServerDto Dto { get; } = dto;
    public string Key => Dto.Key;
    public string Title => Dto.Title;
    public bool IsProject => Dto.Source == "project";
    public string ToolCountText => Dto.State == "connected" ? Dto.ToolCount.ToString() : "";

    /// <summary>状态点：ok（绿）/ busy（黄）/ warn（橙：需要登录或待信任）/ error（红）/ off（灰）。</summary>
    public string Dot => McpTexts.Dot(Dto);
    public bool IsOk => Dot == "ok";
    public bool IsBusy => Dot == "busy";
    public bool IsWarn => Dot == "warn";
    public bool IsError => Dot == "error";
    public bool IsOff => Dot == "off";
    public string StatusText => McpTexts.Status(Dto);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>一个工具（详情页工具列表）。</summary>
public sealed partial class McpToolItemViewModel(McpToolDto dto) : ObservableObject
{
    public McpToolDto Dto { get; } = dto;
    public string Name => Dto.Name;
    public string DisplayName => string.IsNullOrWhiteSpace(Dto.Title) ? Dto.Name : Dto.Title!;
    public bool ShowName => !string.IsNullOrWhiteSpace(Dto.Title) && Dto.Title != Dto.Name;
    public string Description => Dto.Description?.Trim() ?? "";
    public bool HasDescription => Description.Length > 0;
    public string TokensText => $"~{McpTexts.Tokens(Dto.SchemaChars + Description.Length)}";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = !dto.Disabled;

    [ObservableProperty]
    public partial bool AlwaysAllow { get; set; } = dto.AlwaysAllow;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

internal static class McpTexts
{
    public static string Dot(McpServerDto dto) => dto.Availability switch
    {
        "invalid" => "error",
        "disabled" or "ignored" => "off",
        "untrusted" => "warn",
        _ => dto.State switch
        {
            "connected" => "ok",
            "connecting" => "busy",
            "needsAuth" => "warn",
            "failed" => "error",
            _ => "off",
        },
    };

    public static string Status(McpServerDto dto) => Strings.Get(dto.Availability switch
    {
        "invalid" => TKCodeLangResourceKind.McpStateInvalid,
        "disabled" => TKCodeLangResourceKind.McpStateDisabled,
        "ignored" => TKCodeLangResourceKind.McpStateIgnored,
        "untrusted" => TKCodeLangResourceKind.McpStateUntrusted,
        _ => dto.State switch
        {
            "connected" => TKCodeLangResourceKind.McpStateConnected,
            "connecting" => TKCodeLangResourceKind.McpStateConnecting,
            "needsAuth" => TKCodeLangResourceKind.McpStateNeedsAuth,
            "failed" => TKCodeLangResourceKind.McpStateFailed,
            _ => TKCodeLangResourceKind.McpStateIdle,
        },
    });

    /// <summary>按约 4 字符 1 token 估算。</summary>
    public static string Tokens(int chars) => chars < 4000 ? $"{chars / 4}" : $"{chars / 4000.0:0.#}k";
}

/// <summary>
/// 右栏：一个服务器的表单、状态、工具与日志（docs/design/mcp.md §11）。新增服务器时 <see cref="IsNew"/> 为 true。
/// 全局服务器写回 settings.json，项目服务器写回项目的 .mcp.json；写完后让 Host 重新加载。
/// </summary>
public sealed partial class McpServerDetailViewModel : ObservableObject
{
    private readonly McpSettingsViewModel _owner;
    private McpServerConfig? _original;

    public McpServerDetailViewModel(McpSettingsViewModel owner, McpServerDto? dto)
    {
        _owner = owner;
        Dto = dto;
        IsNew = dto is null;
        if (dto is not null)
        {
            _original = McpServerConfig.ParseServers(new JsonObject { [dto.Key] = JsonNode.Parse(dto.ConfigJson) }, McpConfigSource.Global).Single();
            Load(_original);
            IsProjectScope = dto.Source == "project";
        }
        else
        {
            IsProjectScope = false;
        }
        FillChoices();
        _dirty = false;
    }

    private bool _dirty;

    public McpServerDto? Dto { get; private set; }
    public bool IsNew { get; }
    public bool IsExisting => !IsNew;
    public bool CanChooseScope => IsNew && _owner.Cwd is not null;

    public ObservableCollection<McpToolItemViewModel> Tools { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>范围（全局 / 项目）与连接方式（本地命令 / 远程 URL）的分段选择：选中项跟随 <see cref="IsProjectScope"/> / <see cref="IsRemote"/>。</summary>
    public ObservableCollection<SettingsChoiceViewModel> ScopeChoices { get; } = [];
    public ObservableCollection<SettingsChoiceViewModel> TransportChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string NameInput { get; set; } = "";

    /// <summary>true：远程 URL；false：本地命令。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IsLocal))]
    public partial bool IsRemote { get; set; }

    public bool IsLocal => !IsRemote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string CommandInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string ArgsInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string CwdInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string EnvInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string UrlInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string HeadersInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial bool IsProjectScope { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string? TestResult { get; private set; }

    [ObservableProperty]
    public partial bool TestSucceeded { get; private set; }

    [ObservableProperty]
    public partial bool IsTesting { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingTools { get; private set; }

    [ObservableProperty]
    public partial string? ToolsError { get; private set; }

    [ObservableProperty]
    public partial bool ShowLog { get; set; }

    public bool IsDirty => _dirty || IsNew;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasTestResult => TestResult is not null;
    public bool HasTools => Tools.Count > 0;
    public bool HasNoTools => Tools.Count == 0 && !IsLoadingTools;
    public bool HasLog => LogLines.Count > 0;

    public string Title => IsNew ? Strings.Get(TKCodeLangResourceKind.AddMcpServer) : Dto!.Title;
    public string StatusText => Dto is null ? "" : McpTexts.Status(Dto);
    public bool IsOk => Dto is not null && McpTexts.Dot(Dto) == "ok";
    public bool IsWarn => Dto is not null && McpTexts.Dot(Dto) is "warn" or "busy";
    public bool IsBad => Dto is not null && McpTexts.Dot(Dto) == "error";
    public bool IsEnabled => Dto?.Enabled ?? true;
    public bool IsProject => Dto?.Source == "project";
    public bool IsUntrusted => Dto?.Availability is "untrusted" or "ignored";
    public bool CanLogin => Dto is { UsesOAuth: true, Availability: "ready" };
    public bool NeedsLogin => Dto?.State == "needsAuth";
    public bool CanRestart => Dto?.Availability == "ready";
    public string? StatusError => Dto?.Availability == "invalid" || Dto?.State is "failed" or "needsAuth" ? Dto?.Error : null;
    public bool HasStatusError => !string.IsNullOrEmpty(StatusError);
    public string? MissingVariables => Dto is { MissingVariables.Count: > 0 } d
        ? Strings.Format(TKCodeLangResourceKind.McpMissingVariablesFormat, string.Join(", ", d.MissingVariables)) : null;
    public bool HasMissingVariables => MissingVariables is not null;
    public bool ShadowedGlobal => Dto?.ShadowedGlobal == true;
    public string? ServerInfo => Dto?.ServerName is { } name
        ? $"{name} {Dto.ServerVersion}".Trim() + (Dto.ProtocolVersion is { } v ? " · " + Strings.Format(TKCodeLangResourceKind.McpProtocolFormat, v) : "")
        : null;
    public bool HasServerInfo => ServerInfo is not null;
    public string? Instructions => Dto?.Instructions;
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Instructions);
    public string ToolsSummary => Strings.Format(TKCodeLangResourceKind.McpToolsSummaryFormat, Tools.Count(t => t.IsEnabled),
        McpTexts.Tokens(Tools.Where(t => t.IsEnabled).Sum(t => t.Dto.SchemaChars + t.Description.Length)));

    partial void OnNameInputChanged(string value) => MarkDirty();
    partial void OnIsRemoteChanged(bool value)
    {
        FillChoices();
        MarkDirty();
    }

    partial void OnIsProjectScopeChanged(bool value) => FillChoices();
    partial void OnCommandInputChanged(string value) => MarkDirty();
    partial void OnArgsInputChanged(string value) => MarkDirty();
    partial void OnCwdInputChanged(string value) => MarkDirty();
    partial void OnEnvInputChanged(string value) => MarkDirty();
    partial void OnUrlInputChanged(string value) => MarkDirty();
    partial void OnHeadersInputChanged(string value) => MarkDirty();
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnTestResultChanged(string? value) => OnPropertyChanged(nameof(HasTestResult));

    partial void OnIsLoadingToolsChanged(bool value) => OnPropertyChanged(nameof(HasNoTools));

    private void MarkDirty()
    {
        _dirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void FillChoices()
    {
        SettingsChoiceViewModel.Fill(ScopeChoices,
            [("global", Strings.Get(TKCodeLangResourceKind.McpScopeGlobal)), ("project", Strings.Get(TKCodeLangResourceKind.McpScopeProject))],
            IsProjectScope ? "project" : "global");
        SettingsChoiceViewModel.Fill(TransportChoices,
            [("local", Strings.Get(TKCodeLangResourceKind.McpTransportLocal)), ("remote", Strings.Get(TKCodeLangResourceKind.McpTransportRemote))],
            IsRemote ? "remote" : "local");
    }

    /// <summary>界面语言变了：重建分段选项的文字，并让状态等文字重新取值。</summary>
    internal void RefreshLanguage()
    {
        FillChoices();
        OnPropertyChanged(string.Empty);
    }

    private void Load(McpServerConfig config)
    {
        NameInput = config.Key;
        IsRemote = config.IsRemote;
        CommandInput = config.Command ?? "";
        ArgsInput = string.Join("\n", config.Args);
        CwdInput = config.Cwd ?? "";
        EnvInput = string.Join("\n", config.Env.Select(p => $"{p.Key}={p.Value}"));
        UrlInput = config.Url ?? "";
        HeadersInput = string.Join("\n", config.Headers.Select(p => $"{p.Key}: {p.Value}"));
    }

    /// <summary>刷新状态（列表重新拉取后）；未修改的表单同时更新为最新配置。</summary>
    internal void Update(McpServerDto dto)
    {
        Dto = dto;
        if (!_dirty)
        {
            _original = McpServerConfig.ParseServers(new JsonObject { [dto.Key] = JsonNode.Parse(dto.ConfigJson) }, McpConfigSource.Global).Single();
            Load(_original);
            _dirty = false;
            OnPropertyChanged(nameof(IsDirty));
        }
        foreach (var name in new[]
                 {
                     nameof(Dto), nameof(Title), nameof(StatusText), nameof(IsOk), nameof(IsWarn), nameof(IsBad), nameof(IsEnabled), nameof(IsUntrusted),
                     nameof(CanLogin), nameof(NeedsLogin), nameof(CanRestart), nameof(StatusError), nameof(HasStatusError), nameof(MissingVariables),
                     nameof(HasMissingVariables), nameof(ServerInfo), nameof(HasServerInfo), nameof(Instructions), nameof(HasInstructions),
                     nameof(ShadowedGlobal), nameof(IsProject),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>由表单生成配置（保留原配置中表单不涉及的字段：disabledTools、alwaysAllow、超时、oauth 等）。</summary>
    internal JsonObject? BuildJson(out string? error)
    {
        error = null;
        var obj = _original?.ToJson() ?? new JsonObject();
        foreach (var key in new[] { "type", "command", "args", "env", "cwd", "envFile", "url", "headers" })
        {
            if (key == "envFile" && !IsRemote)
            {
                continue;
            }
            obj.Remove(key);
        }
        if (IsRemote)
        {
            var url = UrlInput.Trim();
            if (!Uri.TryCreate(url.Replace("${", "x", StringComparison.Ordinal).Replace("{", "x", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal),
                    UriKind.Absolute, out _))
            {
                error = Strings.Get(TKCodeLangResourceKind.McpUrlInvalid);
                return null;
            }
            if (_original?.Transport == McpTransportKind.Sse)
            {
                obj["type"] = "sse";
            }
            obj["url"] = url;
            var headers = ParsePairs(HeadersInput, ':');
            if (headers.Count > 0)
            {
                obj["headers"] = headers;
            }
            obj.Remove("envFile");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(CommandInput))
            {
                error = Strings.Get(TKCodeLangResourceKind.McpCommandRequired);
                return null;
            }
            obj["command"] = CommandInput.Trim();
            var args = ArgsInput.Split('\n').Select(a => a.TrimEnd('\r')).Where(a => a.Length > 0).ToArray();
            if (args.Length > 0)
            {
                obj["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            }
            if (CwdInput.Trim() is { Length: > 0 } cwd)
            {
                obj["cwd"] = cwd;
            }
            var env = ParsePairs(EnvInput, '=');
            if (env.Count > 0)
            {
                obj["env"] = env;
            }
        }
        return obj;
    }

    private static JsonObject ParsePairs(string text, char separator)
    {
        var obj = new JsonObject();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var idx = line.IndexOf(separator);
            if (line.Length == 0 || idx <= 0)
            {
                continue;
            }
            obj[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return obj;
    }

    internal void SetTools(McpToolsDto tools)
    {
        Tools.Clear();
        foreach (var tool in tools.Tools)
        {
            var item = new McpToolItemViewModel(tool);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(McpToolItemViewModel.IsEnabled) or nameof(McpToolItemViewModel.AlwaysAllow))
                {
                    _owner.SaveToolFlags(this);
                    OnPropertyChanged(nameof(ToolsSummary));
                }
            };
            Tools.Add(item);
        }
        ToolsError = tools.Error;
        OnPropertyChanged(nameof(HasTools));
        OnPropertyChanged(nameof(HasNoTools));
        OnPropertyChanged(nameof(ToolsSummary));
    }

    internal void SetLog(IReadOnlyList<string> lines)
    {
        LogLines.Clear();
        foreach (var line in lines.TakeLast(300))
        {
            LogLines.Add(line);
        }
        OnPropertyChanged(nameof(HasLog));
    }

    internal void SetTest(McpTestResult result)
    {
        TestSucceeded = result.Ok;
        TestResult = result.Ok
            ? Strings.Format(TKCodeLangResourceKind.McpTestOkFormat, result.ToolCount, $"{result.ServerName} {result.ServerVersion}".Trim())
            : result.NeedsAuth ? Strings.Get(TKCodeLangResourceKind.McpStateNeedsAuth) : result.Error;
        SetLog(result.Log);
    }

    internal void SetTesting(bool value) => IsTesting = value;

    internal void SetToolsLoading(bool value) => IsLoadingTools = value;

    internal void SetError(string? value) => Error = value;

    internal void Saved()
    {
        _dirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    [RelayCommand]
    private void ChooseTransport(SettingsChoiceViewModel choice) => IsRemote = choice.Value == "remote";

    [RelayCommand]
    private void ChooseScope(SettingsChoiceViewModel choice) => IsProjectScope = choice.Value == "project";

    [RelayCommand]
    private Task SaveAsync() => _owner.SaveAsync(this);

    [RelayCommand]
    private Task TestAsync() => _owner.TestAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(this);

    [RelayCommand]
    private Task ToggleEnabledAsync() => _owner.SetEnabledAsync(this, !IsEnabled);

    [RelayCommand]
    private Task RestartAsync() => _owner.RestartAsync(this, login: false);

    [RelayCommand]
    private Task LoginAsync() => _owner.RestartAsync(this, login: true);

    [RelayCommand]
    private Task LogoutAsync() => _owner.LogoutAsync(this);

    [RelayCommand]
    private Task TrustAsync() => _owner.TrustAsync(this, true);

    [RelayCommand]
    private Task IgnoreAsync() => _owner.TrustAsync(this, false);

    [RelayCommand]
    private Task LoadToolsAsync() => _owner.LoadToolsAsync(this);

    [RelayCommand]
    private async Task ToggleLogAsync()
    {
        ShowLog = !ShowLog;
        if (ShowLog)
        {
            await _owner.LoadLogAsync(this);
        }
    }
}

/// <summary>设置 › MCP 服务器（主从双栏）。</summary>
public sealed partial class McpSettingsViewModel : ObservableObject
{
    private readonly UserSettingsService _settings;
    private readonly AgentHostConnection _host;
    private readonly Func<string?> _cwd;
    private readonly ILogger _logger;
    private IReadOnlyList<McpServerDto> _servers = [];

    public McpSettingsViewModel(UserSettingsService settings, AgentHostConnection host, Func<string?> cwd, ILogger logger)
    {
        _settings = settings;
        _host = host;
        _cwd = cwd;
        _logger = logger;
        host.McpChanged += notification => _ = RefreshAsync();
        settings.Changed += () => _ = RefreshAsync();
    }

    /// <summary>当前项目目录（决定显示哪个项目的 .mcp.json）。</summary>
    public string? Cwd => _cwd();

    public ObservableCollection<McpServerItemViewModel> GlobalServers { get; } = [];
    public ObservableCollection<McpServerItemViewModel> ProjectServers { get; } = [];

    public bool HasProject => Cwd is not null;
    public bool HasProjectServers => ProjectServers.Count > 0;
    public bool HasNoServers => GlobalServers.Count == 0 && ProjectServers.Count == 0;
    public string ProjectGroupTitle => Strings.Format(TKCodeLangResourceKind.McpProjectGroupFormat,
        Cwd is null ? "" : Path.GetFileName(Cwd.TrimEnd(Path.DirectorySeparatorChar)));

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial string? ProjectError { get; private set; }

    public bool HasProjectError => !string.IsNullOrEmpty(ProjectError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowEmptyDetail))]
    public partial McpServerDetailViewModel? Selected { get; private set; }

    public bool HasSelection => Selected is not null;
    public bool ShowEmptyDetail => Selected is null;

    partial void OnSearchChanged(string value) => RebuildLists();

    /// <summary>界面语言变了：列表里的状态文字、分组标题与详情里的分段选项都按新语言重取。</summary>
    internal void RefreshLanguage()
    {
        RebuildLists();
        Selected?.RefreshLanguage();
        Import?.RefreshLanguage();
    }

    partial void OnProjectErrorChanged(string? value) => OnPropertyChanged(nameof(HasProjectError));

    public async Task RefreshAsync()
    {
        McpServersDto result;
        try
        {
            var server = await _host.GetServerAsync();
            result = await server.ListMcpServersAsync(Cwd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "List MCP servers failed");
            return;
        }
        _servers = result.Servers;
        ProjectError = result.ProjectError;
        RebuildLists();
        if (Selected is { Dto: { } selected } detail && _servers.FirstOrDefault(s => s.Key == selected.Key && s.Source == selected.Source) is { } fresh)
        {
            var toolsChanged = fresh.ToolCount != selected.ToolCount || fresh.State != selected.State;
            detail.Update(fresh);
            if (toolsChanged && fresh.State == "connected")
            {
                _ = LoadToolsAsync(detail);
            }
        }
    }

    private void RebuildLists()
    {
        var filter = Search.Trim();
        bool Match(McpServerDto s) => filter.Length == 0 || s.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                                        || s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                                        || s.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var selectedKey = Selected?.Dto is { } dto ? (dto.Key, dto.Source) : default;
        GlobalServers.Clear();
        ProjectServers.Clear();
        foreach (var server in _servers.Where(Match).OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            var item = new McpServerItemViewModel(server) { IsSelected = (server.Key, server.Source) == selectedKey };
            (server.Source == "project" ? ProjectServers : GlobalServers).Add(item);
        }
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasProjectServers));
        OnPropertyChanged(nameof(HasNoServers));
        OnPropertyChanged(nameof(ProjectGroupTitle));
    }

    [RelayCommand]
    private async Task SelectAsync(McpServerItemViewModel item)
    {
        foreach (var other in GlobalServers.Concat(ProjectServers))
        {
            other.IsSelected = other == item;
        }
        var detail = new McpServerDetailViewModel(this, item.Dto);
        Selected = detail;
        if (item.Dto.Availability == "ready")
        {
            await LoadToolsAsync(detail);
        }
    }

    // ---- 导入 / 导出（docs/design/mcp.md §12） ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImporting))]
    public partial McpImportViewModel? Import { get; private set; }

    public bool IsImporting => Import is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExporting))]
    public partial McpExportViewModel? Export { get; private set; }

    public bool IsExporting => Export is not null;

    [RelayCommand]
    private void OpenImport() => Import = new McpImportViewModel(this);

    internal void CloseImport() => Import = null;

    [RelayCommand]
    private void OpenExport()
    {
        var servers = _servers
            .Select(s => McpServerConfig.ParseServers(new JsonObject { [s.Key] = JsonNode.Parse(s.ConfigJson) }, McpConfigSource.Global).Single())
            .ToList();
        Export = new McpExportViewModel(servers, Cwd, () => Export = null);
    }

    /// <summary>目标位置是否已有同名服务器；<paramref name="sameConfig"/> 非 null 时要求配置也相同。</summary>
    internal bool Exists(string key, bool project, JsonObject? sameConfig)
    {
        var existing = _servers.FirstOrDefault(s => s.Key == key && (s.Source == "project") == project);
        if (existing is null || sameConfig is null)
        {
            return existing is not null;
        }
        var normalized = McpServerConfig.ParseServers(new JsonObject { [key] = sameConfig.DeepClone() }, McpConfigSource.Global).Single();
        return normalized.ToJson().ToJsonString() == existing.ConfigJson;
    }

    /// <summary>写入导入的服务器（同名覆盖），返回错误信息。</summary>
    internal async Task<string?> ImportAsync(IReadOnlyList<(string Key, JsonObject Config)> servers, bool project)
    {
        try
        {
            if (project)
            {
                foreach (var (key, config) in servers)
                {
                    McpProjectConfig.Update(Cwd ?? throw new InvalidOperationException("No project is open."), key, config);
                }
            }
            else
            {
                _settings.Update(root =>
                {
                    foreach (var (key, config) in servers)
                    {
                        SettingsFile.SetMcpServer(root, key, config);
                    }
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ex.Message;
        }
        await ReloadAsync();
        if (project)
        {
            // 用户自己写入项目配置：视为已信任
            await TrustKeysAsync(servers.Select(s => s.Key));
        }
        return null;
    }

    private async Task TrustKeysAsync(IEnumerable<string> keys)
    {
        if (Cwd is not { } cwd)
        {
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            foreach (var key in keys)
            {
                await server.TrustMcpServerAsync(key, cwd, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trusting imported MCP servers failed");
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void Add()
    {
        foreach (var other in GlobalServers.Concat(ProjectServers))
        {
            other.IsSelected = false;
        }
        Selected = new McpServerDetailViewModel(this, null);
    }

    internal async Task LoadToolsAsync(McpServerDetailViewModel detail)
    {
        if (detail.Dto is not { } dto)
        {
            return;
        }
        detail.SetToolsLoading(true);
        try
        {
            var server = await _host.GetServerAsync();
            detail.SetTools(await server.ListMcpToolsAsync(dto.Key, Cwd));
        }
        catch (Exception ex)
        {
            detail.SetTools(new McpToolsDto([], ex.Message));
        }
        finally
        {
            detail.SetToolsLoading(false);
        }
    }

    internal async Task LoadLogAsync(McpServerDetailViewModel detail)
    {
        if (detail.Dto is not { } dto)
        {
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            detail.SetLog((await server.GetMcpLogAsync(dto.Key, Cwd)).Lines);
        }
        catch (Exception ex)
        {
            detail.SetLog([ex.Message]);
        }
    }

    internal async Task SaveAsync(McpServerDetailViewModel detail)
    {
        var key = detail.NameInput.Trim();
        if (!McpServerConfig.IsValidKey(key))
        {
            detail.SetError(Strings.Get(TKCodeLangResourceKind.McpNameInvalid));
            return;
        }
        var project = detail.IsNew ? detail.IsProjectScope : detail.IsProject;
        var renamed = detail.Dto is { } existing && existing.Key != key;
        if ((detail.IsNew || renamed) && _servers.Any(s => s.Key == key && (s.Source == "project") == project))
        {
            detail.SetError(Strings.Get(TKCodeLangResourceKind.McpNameExists));
            return;
        }
        if (detail.BuildJson(out var error) is not { } json)
        {
            detail.SetError(error);
            return;
        }
        detail.SetError(null);
        try
        {
            Write(project, key, json);
            if (renamed)
            {
                Write(project, detail.Dto!.Key, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            detail.SetError(ex.Message);
            return;
        }
        detail.Saved();
        await ReloadAsync();
        if (project)
        {
            await TrustKeysAsync([key]);
        }
        if (_servers.FirstOrDefault(s => s.Key == key && (s.Source == "project") == project) is { } saved)
        {
            var item = GlobalServers.Concat(ProjectServers).FirstOrDefault(i => i.Dto == saved) ?? new McpServerItemViewModel(saved);
            await SelectAsync(item);
        }
    }

    internal async Task TestAsync(McpServerDetailViewModel detail)
    {
        if (detail.BuildJson(out var error) is not { } json)
        {
            detail.SetError(error);
            return;
        }
        detail.SetError(null);
        detail.SetTesting(true);
        try
        {
            var server = await _host.GetServerAsync();
            var key = McpServerConfig.IsValidKey(detail.NameInput.Trim()) ? detail.NameInput.Trim() : "test";
            detail.SetTest(await server.TestMcpServerAsync(new McpTestRequest(key, json.ToJsonString(), Cwd)));
        }
        catch (Exception ex)
        {
            detail.SetTest(new McpTestResult(false, ex.Message, 0, null, null, null, [], false));
        }
        finally
        {
            detail.SetTesting(false);
        }
    }

    internal async Task RemoveAsync(McpServerDetailViewModel detail)
    {
        if (detail.Dto is not { } dto)
        {
            Selected = null;
            return;
        }
        try
        {
            Write(dto.Source == "project", dto.Key, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            detail.SetError(ex.Message);
            return;
        }
        Selected = null;
        await ReloadAsync();
    }

    internal async Task SetEnabledAsync(McpServerDetailViewModel detail, bool enabled)
    {
        if (detail.Dto is not { } dto)
        {
            return;
        }
        await MutateAsync(dto, obj => SettingsFile.Set(obj, "enabled", enabled ? null : JsonValue.Create(false)));
    }

    /// <summary>工具开关与“始终允许”：写回 disabledTools / alwaysAllow。</summary>
    internal void SaveToolFlags(McpServerDetailViewModel detail)
    {
        if (detail.Dto is not { } dto)
        {
            return;
        }
        var disabled = detail.Tools.Where(t => !t.IsEnabled).Select(t => t.Name).ToArray();
        var allow = detail.Tools.Where(t => t.AlwaysAllow).Select(t => t.Name).ToList();
        _ = MutateAsync(dto, obj =>
        {
            // 写了 "*" 的保持整个服务器允许
            if (obj["alwaysAllow"] is JsonArray existing && existing.Any(n => n?.ToString() == "*"))
            {
                allow.Insert(0, "*");
            }
            SettingsFile.Set(obj, "disabledTools", disabled.Length == 0 ? null : new JsonArray(disabled.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()));
            SettingsFile.Set(obj, "alwaysAllow", allow.Count == 0 ? null : new JsonArray(allow.Distinct().Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()));
        });
    }

    internal async Task RestartAsync(McpServerDetailViewModel detail, bool login)
    {
        if (detail.Dto is not { } dto)
        {
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            await server.RestartMcpServerAsync(dto.Key, Cwd, login);
        }
        catch (Exception ex)
        {
            detail.SetError(ex.Message);
        }
    }

    internal async Task LogoutAsync(McpServerDetailViewModel detail)
    {
        if (detail.Dto is not { } dto)
        {
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            await server.LogoutMcpServerAsync(dto.Key, Cwd);
        }
        catch (Exception ex)
        {
            detail.SetError(ex.Message);
        }
    }

    internal async Task TrustAsync(McpServerDetailViewModel detail, bool trusted)
    {
        if (detail.Dto is not { } dto || Cwd is not { } cwd)
        {
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            await server.TrustMcpServerAsync(dto.Key, cwd, trusted);
        }
        catch (Exception ex)
        {
            detail.SetError(ex.Message);
        }
        await RefreshAsync();
    }

    private async Task MutateAsync(McpServerDto dto, Action<JsonObject> mutate)
    {
        var obj = JsonNode.Parse(dto.ConfigJson) as JsonObject ?? new JsonObject();
        mutate(obj);
        try
        {
            Write(dto.Source == "project", dto.Key, obj);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Writing MCP server {Key} failed", dto.Key);
            return;
        }
        await ReloadAsync();
    }

    private void Write(bool project, string key, JsonObject? server)
    {
        if (project)
        {
            McpProjectConfig.Update(Cwd ?? throw new InvalidOperationException("No project is open."), key, server);
        }
        else
        {
            _settings.Update(root => SettingsFile.SetMcpServer(root, key, server));
        }
    }

    private async Task ReloadAsync()
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
        await RefreshAsync();
    }
}

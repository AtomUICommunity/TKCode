using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Config;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.ViewModels;

/// <summary>导入预览中的一个服务器（docs/design/mcp.md §12.4）。</summary>
public sealed partial class McpImportItemViewModel : ObservableObject
{
    private readonly McpImportViewModel _owner;

    public McpImportItemViewModel(McpImportViewModel owner, McpImportCandidate candidate)
    {
        _owner = owner;
        Candidate = candidate;
        Key = candidate.Key;
        IsSelected = candidate.Error is null;
        Inputs = candidate.Inputs.Select(i => new McpImportInputViewModel(i)).ToList();
        foreach (var input in Inputs)
        {
            input.PropertyChanged += (_, _) => _owner.Revalidate();
        }
    }

    public McpImportCandidate Candidate { get; }
    public string Summary => Candidate.Summary;
    public bool IsRemote => Candidate.Config["url"] is not null;
    public string TypeText => Strings.Get(IsRemote ? TKCodeLangResourceKind.McpTransportRemote : TKCodeLangResourceKind.McpTransportLocal);
    public string? Error => Candidate.Error;
    public bool HasError => Error is not null;
    public bool CanSelect => !HasError;
    public IReadOnlyList<string> Notes => Candidate.Notes.Select(n => n.Kind switch
    {
        McpImportNoteKind.RunsCommand => Strings.Format(TKCodeLangResourceKind.McpNoteRunsCommandFormat, n.Arg),
        McpImportNoteKind.UnknownType => Strings.Format(TKCodeLangResourceKind.McpNoteUnknownTypeFormat, n.Arg),
        McpImportNoteKind.AllowList => Strings.Get(TKCodeLangResourceKind.McpNoteAllowList),
        McpImportNoteKind.HeadersHelper => Strings.Get(TKCodeLangResourceKind.McpNoteHeadersHelper),
        McpImportNoteKind.Trust => Strings.Get(TKCodeLangResourceKind.McpNoteTrust),
        _ => Strings.Get(TKCodeLangResourceKind.McpNoteFileReference),
    }).ToList();
    public bool HasNotes => Notes.Count > 0;
    public string? DroppedText => Candidate.Dropped.Count == 0 ? null
        : Strings.Format(TKCodeLangResourceKind.McpImportDroppedFormat, string.Join(", ", Candidate.Dropped));
    public bool HasDropped => DroppedText is not null;
    public IReadOnlyList<McpImportInputViewModel> Inputs { get; }
    public bool HasInputs => Inputs.Count > 0;
    public string? AutoApproveText => Candidate.AutoApprove.Count == 0 ? null
        : Strings.Format(TKCodeLangResourceKind.McpImportAutoApproveFormat, string.Join(", ", Candidate.AutoApprove));
    public bool HasAutoApprove => AutoApproveText is not null;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string Key { get; set; }

    /// <summary>默认不导入源配置中的自动批准，由用户勾选。</summary>
    [ObservableProperty]
    public partial bool ImportAutoApprove { get; set; }

    /// <summary>名字问题：不合法 / 与已有服务器重名（覆盖）/ 与本次导入的其他项重名。</summary>
    [ObservableProperty]
    public partial string? KeyProblem { get; private set; }

    [ObservableProperty]
    public partial bool Overwrites { get; private set; }

    public bool HasKeyProblem => KeyProblem is not null;

    partial void OnKeyChanged(string value) => _owner.Revalidate();
    partial void OnIsSelectedChanged(bool value) => _owner.Revalidate();
    partial void OnKeyProblemChanged(string? value) => OnPropertyChanged(nameof(HasKeyProblem));

    internal void SetValidation(string? problem, bool overwrites)
    {
        KeyProblem = problem;
        Overwrites = overwrites;
    }

    /// <summary>界面语言变了：类型、备注等文字重新取值。</summary>
    internal void RefreshLanguage() => OnPropertyChanged(string.Empty);

    internal JsonObject Resolve() =>
        Candidate.Resolve(Inputs.Where(i => i.Value.Length > 0).ToDictionary(i => i.Id, i => i.Value), ImportAutoApprove);
}

public sealed partial class McpImportInputViewModel(McpImportInput input) : ObservableObject
{
    public string Id => input.Id;
    public string Label => input.Description ?? input.Id;
    public bool IsPassword => input.Password;

    /// <summary>明文或 {env:VAR}。</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = "";
}

/// <summary>本机发现的配置来源（扫描结果）。</summary>
public sealed partial class McpImportSourceViewModel(McpImportSource source) : ObservableObject
{
    public McpImportSource Source { get; } = source;
    public string Client => Source.Client;
    public string Path => Source.Path;
    public string CountText => Source.Result.Servers.Count.ToString();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// 导入对话框（docs/design/mcp.md §12）：粘贴 / 文件 / 本机扫描三个入口共用一个预览。
/// 导入不会自动启动服务器；写入后由 Host 重新加载。
/// </summary>
public sealed partial class McpImportViewModel : ObservableObject
{
    private readonly McpSettingsViewModel _owner;

    public McpImportViewModel(McpSettingsViewModel owner)
    {
        _owner = owner;
        FillChoices();
    }

    /// <summary>入口（粘贴 / 本机扫描）与写入位置（全局 / 项目）的分段选择：选中项跟随 <see cref="IsScanMode"/> / <see cref="IsProjectTarget"/>。</summary>
    public ObservableCollection<SettingsChoiceViewModel> ModeChoices { get; } = [];
    public ObservableCollection<SettingsChoiceViewModel> TargetChoices { get; } = [];

    public ObservableCollection<McpImportItemViewModel> Items { get; } = [];
    public ObservableCollection<McpImportSourceViewModel> Sources { get; } = [];

    /// <summary>true：本机扫描；false：粘贴（或从文件读入的文本）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasteMode))]
    public partial bool IsScanMode { get; set; }

    public bool IsPasteMode => !IsScanMode;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string? ParseError { get; private set; }

    [ObservableProperty]
    public partial string? FormatText { get; private set; }

    [ObservableProperty]
    public partial bool IsProjectTarget { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public bool HasParseError => !string.IsNullOrEmpty(ParseError);
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasItems => Items.Count > 0;
    public bool HasSources => Sources.Count > 0;
    public bool HasNoSources => Sources.Count == 0;
    public bool CanTargetProject => _owner.Cwd is not null;
    public int SelectedCount => Items.Count(i => i.IsSelected && i.CanSelect);
    public bool CanImport => SelectedCount > 0 && Items.Where(i => i.IsSelected).All(i => i.KeyProblem is null);
    public string ImportButtonText => Strings.Format(TKCodeLangResourceKind.McpImportButtonFormat, SelectedCount);

    partial void OnTextChanged(string value) => ParseText();
    partial void OnIsProjectTargetChanged(bool value)
    {
        FillChoices();
        Revalidate();
    }
    partial void OnParseErrorChanged(string? value) => OnPropertyChanged(nameof(HasParseError));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsScanModeChanged(bool value)
    {
        FillChoices();
        Items.Clear();
        ParseError = null;
        FormatText = null;
        if (value)
        {
            Scan();
        }
        else
        {
            ParseText();
        }
        Revalidate();
    }

    [RelayCommand]
    private void ChooseMode(SettingsChoiceViewModel choice) => IsScanMode = choice.Value == "scan";

    [RelayCommand]
    private void ChooseTarget(SettingsChoiceViewModel choice) => IsProjectTarget = choice.Value == "project";

    private void FillChoices()
    {
        SettingsChoiceViewModel.Fill(ModeChoices,
            [("paste", Strings.Get(TKCodeLangResourceKind.McpImportPaste)), ("scan", Strings.Get(TKCodeLangResourceKind.McpImportScan))],
            IsScanMode ? "scan" : "paste");
        SettingsChoiceViewModel.Fill(TargetChoices,
            [("global", Strings.Get(TKCodeLangResourceKind.McpScopeGlobal)), ("project", Strings.Get(TKCodeLangResourceKind.McpScopeProject))],
            IsProjectTarget ? "project" : "global");
    }

    /// <summary>界面语言变了：重建分段选项，预览行与按钮文字重新取值。</summary>
    internal void RefreshLanguage()
    {
        FillChoices();
        OnPropertyChanged(string.Empty);
        foreach (var item in Items)
        {
            item.RefreshLanguage();
        }
    }

    /// <summary>从文件读入（视图层负责弹出文件选择框）。</summary>
    public void LoadFile(string path, string text)
    {
        IsScanMode = false;
        _fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        Text = text;
    }

    private string? _fileName;

    private void ParseText()
    {
        Items.Clear();
        if (string.IsNullOrWhiteSpace(Text))
        {
            ParseError = null;
            FormatText = null;
            Revalidate();
            return;
        }
        var result = McpImport.Parse(Text, _owner.Cwd is null ? null : McpProjectConfig.ProjectRoot(_owner.Cwd), _fileName);
        ParseError = result.Error;
        FormatText = result.Format.Length == 0 ? null : Strings.Format(TKCodeLangResourceKind.McpImportFormatFormat, result.Format);
        Fill(result.Servers);
    }

    private void Scan()
    {
        Sources.Clear();
        foreach (var source in McpImport.Discover(_owner.Cwd))
        {
            Sources.Add(new McpImportSourceViewModel(source));
        }
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasNoSources));
        if (Sources.Count > 0)
        {
            SelectSource(Sources[0]);
        }
    }

    [RelayCommand]
    private void SelectSource(McpImportSourceViewModel source)
    {
        foreach (var other in Sources)
        {
            other.IsSelected = other == source;
        }
        Items.Clear();
        Fill(source.Source.Result.Servers);
    }

    private void Fill(IEnumerable<McpImportCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var item = new McpImportItemViewModel(this, candidate);
            // 与已有服务器完全相同的条目默认不勾选
            if (_owner.Exists(candidate.Key, IsProjectTarget, candidate.Config))
            {
                item.IsSelected = false;
            }
            Items.Add(item);
        }
        Revalidate();
    }

    internal void Revalidate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            var key = item.Key.Trim();
            string? problem = null;
            var overwrites = false;
            if (item.IsSelected)
            {
                if (!McpServerConfig.IsValidKey(key))
                {
                    problem = Strings.Get(TKCodeLangResourceKind.McpNameInvalid);
                }
                else if (!seen.Add(key))
                {
                    problem = Strings.Get(TKCodeLangResourceKind.McpNameExists);
                }
                else
                {
                    overwrites = _owner.Exists(key, IsProjectTarget, null);
                }
            }
            item.SetValidation(problem, overwrites);
        }
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(ImportButtonText));
        OnPropertyChanged(nameof(CanTargetProject));
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (!CanImport)
        {
            return;
        }
        var selected = Items.Where(i => i.IsSelected && i.CanSelect).Select(i => (i.Key.Trim(), i.Resolve())).ToList();
        Error = await _owner.ImportAsync(selected, IsProjectTarget);
        if (Error is null)
        {
            _owner.CloseImport();
        }
    }

    [RelayCommand]
    private void Close() => _owner.CloseImport();
}

/// <summary>导出对话框：通用 {"mcpServers": …} 格式。</summary>
public sealed partial class McpExportViewModel : ObservableObject
{
    private readonly IReadOnlyList<McpServerConfig> _servers;
    private readonly string? _cwd;
    private readonly Action _close;

    public McpExportViewModel(IReadOnlyList<McpServerConfig> servers, string? cwd, Action close)
    {
        _servers = servers;
        _cwd = cwd;
        _close = close;
        Json = McpImport.Export(servers, false, cwd);
    }

    [ObservableProperty]
    public partial string Json { get; private set; }

    /// <summary>展开变量为当前值（会包含密钥）。</summary>
    [ObservableProperty]
    public partial bool Expand { get; set; }

    partial void OnExpandChanged(bool value) => Json = McpImport.Export(_servers, value, _cwd);

    [RelayCommand]
    private void Close() => _close();
}

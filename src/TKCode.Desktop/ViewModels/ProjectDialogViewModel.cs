using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Config;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.ViewModels;

/// <summary>弹窗「源文件夹」列表中的一行。</summary>
public sealed partial class ProjectFolderItemViewModel(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = Project.FolderName(path);

    /// <summary>用户目录下的路径显示为 ~/…。</summary>
    public string DisplayPath { get; } = Shorten(path);

    public bool IsMissing { get; } = !Directory.Exists(path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMakePrimary))]
    public partial bool IsPrimary { get; set; }

    public bool CanMakePrimary => !IsPrimary;

    /// <summary>重复添加同一个文件夹时短暂高亮已有的行。</summary>
    [ObservableProperty]
    public partial bool IsFlashing { get; set; }

    public string MakePrimaryTip => Strings.Format(TKCodeLangResourceKind.MakePrimaryFormat, Name);
    public string RemoveTip => Strings.Format(TKCodeLangResourceKind.RemoveFolderFormat, Name);

    private static string Shorten(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && PathRules.IsUnder(path, home) && !PathRules.SamePath(path, home)
            ? "~" + path[home.TrimEnd(System.IO.Path.DirectorySeparatorChar).Length..]
            : path;
    }
}

/// <summary>
/// 创建 / 编辑项目弹窗（docs/design/projects.md §9）：名称、设备、源文件夹（第一个是主文件夹）。
/// 校验：至少一个文件夹；重复的不加入；嵌套的给出警告但允许提交。
/// </summary>
public sealed partial class ProjectDialogViewModel : ObservableObject
{
    private readonly Project? _editing;

    /// <param name="editing">要编辑的项目；null 表示新建。</param>
    public ProjectDialogViewModel(Project? editing = null)
    {
        _editing = editing;
        Name = editing?.Name ?? "";
        Folders.CollectionChanged += (_, _) => OnFoldersChanged();
        if (editing is not null)
        {
            AddFolders(editing.Folders);
        }
        OnFoldersChanged();
    }

    public bool IsEdit => _editing is not null;
    public Project? Editing => _editing;

    public string Title => Strings.Get(IsEdit ? TKCodeLangResourceKind.EditProject : TKCodeLangResourceKind.CreateProject);
    public string SubmitText => Strings.Get(IsEdit ? TKCodeLangResourceKind.ProjectSaveAction : TKCodeLangResourceKind.ProjectCreateAction);

    /// <summary>设备：现在只有本机（为远程设备保留，§9.2）。</summary>
    public IReadOnlyList<string> Devices { get; } = [Strings.Get(TKCodeLangResourceKind.ThisComputer)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial string Name { get; set; }

    /// <summary>名称留空时提交主文件夹的名称，这里作为占位文字提示。</summary>
    public string NamePlaceholder => Folders.Count > 0 ? Folders[0].Name : Strings.Get(TKCodeLangResourceKind.ProjectNamePlaceholder);

    public ObservableCollection<ProjectFolderItemViewModel> Folders { get; } = [];

    public bool HasFolders => Folders.Count > 0;

    /// <summary>提交进行中（按钮显示加载，弹窗不能关闭）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial bool IsBusy { get; set; }

    public bool CanSubmit => HasFolders && !IsBusy && Name.Length <= MaxNameLength;

    public const int MaxNameLength = 80;

    /// <summary>警告（嵌套的文件夹、更换了主文件夹）；为 null 时不显示。</summary>
    [ObservableProperty]
    public partial string? Warning { get; private set; }

    /// <summary>保存失败的原因；为 null 时不显示。</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>提交的名称：去掉首尾空白，留空时为 null（由 <see cref="Project"/> 取主文件夹名）。</summary>
    public string? ResultName => string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();

    public IReadOnlyList<string> ResultFolders => Folders.Select(f => f.Path).ToArray();

    /// <summary>加入文件夹（文件夹选择框、拖放）：规范化、去掉已有的（闪一下已有的行）。第一个加入的成为主文件夹。</summary>
    public void AddFolders(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            var normalized = PathRules.Normalize(path);
            var real = PathRules.RealPath(normalized);
            if (Folders.FirstOrDefault(f => PathRules.SamePath(f.Path, normalized) || PathRules.SamePath(PathRules.RealPath(f.Path), real)) is { } existing)
            {
                _ = FlashAsync(existing);
                continue;
            }
            Folders.Add(new ProjectFolderItemViewModel(normalized));
        }
    }

    [RelayCommand]
    private void MakePrimary(ProjectFolderItemViewModel item)
    {
        var index = Folders.IndexOf(item);
        if (index > 0)
        {
            Folders.Move(index, 0);
        }
    }

    [RelayCommand]
    private void Remove(ProjectFolderItemViewModel item) => Folders.Remove(item);

    private void OnFoldersChanged()
    {
        for (var i = 0; i < Folders.Count; i++)
        {
            Folders[i].IsPrimary = i == 0;
        }
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(NamePlaceholder));
        Warning = BuildWarning();
    }

    private string? BuildWarning()
    {
        var lines = new List<string>();
        var real = Folders.Select(f => PathRules.RealPath(f.Path)).ToArray();
        for (var i = 0; i < Folders.Count; i++)
        {
            for (var j = 0; j < Folders.Count; j++)
            {
                if (i != j && PathRules.IsUnder(real[i], real[j]) && !PathRules.SamePath(real[i], real[j]))
                {
                    lines.Add(Strings.Format(TKCodeLangResourceKind.NestedFolderWarningFormat, Folders[i].Name, Folders[j].Name));
                    break;
                }
            }
        }
        if (_editing is not null && Folders.Count > 0 && !PathRules.SamePath(Folders[0].Path, _editing.Primary))
        {
            lines.Add(Strings.Get(TKCodeLangResourceKind.PrimaryChangedNote));
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static async Task FlashAsync(ProjectFolderItemViewModel item)
    {
        item.IsFlashing = true;
        await Task.Delay(600);
        item.IsFlashing = false;
    }
}

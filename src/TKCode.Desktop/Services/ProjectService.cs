using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Config;

namespace TKCode.Desktop.Services;

/// <summary>
/// 项目表（~/.tkcode/projects.json，docs/design/projects.md §5）。Desktop 是唯一的写入方，Host 只读。
/// 第一次运行时把 state.json 里旧的项目路径列表迁移成单文件夹项目。
/// </summary>
public sealed class ProjectService
{
    private readonly StateService _state;
    private readonly string _path;
    private readonly ILogger _logger;
    private List<Project> _projects;

    public ProjectService(StateService state, ILogger<ProjectService>? logger = null)
        : this(state, ConfigPaths.ProjectsFile, logger)
    {
    }

    /// <summary>指定项目表位置（测试用）。</summary>
    public ProjectService(StateService state, string path, ILogger? logger = null)
    {
        _state = state;
        _path = path;
        _logger = logger ?? NullLogger.Instance;
        var result = ProjectsFile.Load(path);
        if (result.Error is not null)
        {
            var aside = ProjectsFile.MoveAside(path);
            _logger.LogWarning("projects.json could not be read ({Error}); moved to {Aside} and starting empty.", result.Error, aside);
        }
        _projects = [.. result.Projects];
        MigrateLegacy(save: !result.Exists || result.Error is not null);
    }

    /// <summary>按 projects.json 中的顺序（新建的在前）。</summary>
    public IReadOnlyList<Project> Projects => _projects;

    /// <summary>侧栏顺序：置顶的在前，其余保持 projects.json 中的顺序。</summary>
    public IReadOnlyList<Project> Ordered => [.. _projects.Where(p => p.Pinned), .. _projects.Where(p => !p.Pinned)];

    /// <summary>项目表有变化（新建、修改、移除、置顶）。</summary>
    public event Action? Changed;

    public Project? Find(string? id) => id is null ? null : _projects.FirstOrDefault(p => p.Id == id);

    /// <summary>主文件夹等于 <paramref name="path"/> 的项目优先，其次是包含这个文件夹的项目。</summary>
    public Project? FindByPath(string path) =>
        _projects.FirstOrDefault(p => PathRules.SamePath(p.Primary, path)) ?? _projects.FirstOrDefault(p => p.HasFolder(path));

    /// <summary>会话所属的项目（§5.3）。</summary>
    public Project? Resolve(string? projectId, string cwd) => ProjectCatalog.Resolve(_projects, projectId, cwd);

    public Project Add(string? name, IEnumerable<string> folders)
    {
        var project = Project.Create(name, folders);
        _projects.Insert(0, project);
        Commit();
        return project;
    }

    /// <summary>托盘、跳转列表与 <c>--open-project</c>：已有项目包含这个文件夹时用它，否则新建一个单文件夹项目。</summary>
    public Project AddFolder(string path) => FindByPath(path) ?? Add(null, [path]);

    /// <summary>按 id 替换（编辑项目）；项目已被移除时忽略。</summary>
    public Project? Update(Project project)
    {
        var index = _projects.FindIndex(p => p.Id == project.Id);
        if (index < 0)
        {
            return null;
        }
        _projects[index] = project;
        Commit();
        return project;
    }

    public void Remove(string id)
    {
        if (_projects.RemoveAll(p => p.Id == id) == 0)
        {
            return;
        }
        _state.Current.RecentProjects.Remove(id);
        _state.Current.PermissionModes.Remove(id);
        if (_state.Current.LastProjectId == id)
        {
            _state.Current.LastProjectId = null;
        }
        _state.Save();
        Commit();
    }

    public void SetPinned(string id, bool pinned)
    {
        if (Find(id) is { } project && project.Pinned != pinned)
        {
            Update(project with { Pinned = pinned });
        }
    }

    /// <summary>
    /// 托盘 / Dock / 跳转列表的项目菜单：最近使用的在前；用过的不足 <paramref name="count"/> 个时按侧栏顺序补齐，
    /// 这样从没切换过项目时菜单也不是空的。
    /// </summary>
    public IReadOnlyList<Project> Recent(int count)
    {
        var ordered = new List<Project>();
        foreach (var id in _state.Current.RecentProjects)
        {
            if (Find(id) is { } known && !ordered.Contains(known))
            {
                ordered.Add(known);
            }
        }
        foreach (var project in Ordered)
        {
            if (!ordered.Contains(project))
            {
                ordered.Add(project);
            }
        }
        // 主文件夹已不存在的项目打不开，不放进菜单
        return ordered.Where(p => Directory.Exists(p.Primary)).Take(count).ToList();
    }

    private void Commit()
    {
        try
        {
            ProjectsFile.Save(_projects, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to save projects.json.");
            throw;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// state.json 里旧的 <see cref="AppState.Workspaces"/> / <see cref="AppState.LastWorkspace"/> / <see cref="AppState.RecentWorkspaces"/>
    /// 与按路径记住的权限档位，换成项目与项目 id（§5.2）。旧字段迁移后清空；以后再出现（例如降级运行过旧版本）时同样并入。
    /// </summary>
    private void MigrateLegacy(bool save)
    {
        var state = _state.Current;
        var changed = false;
        var created = new List<Project>();
        foreach (var path in state.Workspaces)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && FindByPath(path) is null && !created.Any(p => p.HasFolder(path)))
            {
                created.Add(Project.Create(null, [path]));
            }
        }
        if (created.Count > 0)
        {
            _projects.InsertRange(0, created);
            save = true;
        }
        if (state.Workspaces.Count > 0)
        {
            state.Workspaces.Clear();
            changed = true;
        }
        if (state.LastWorkspace is { } last)
        {
            state.LastProjectId = FindByPath(last)?.Id ?? state.LastProjectId;
            state.LastWorkspace = null;
            changed = true;
        }
        if (state.RecentWorkspaces.Count > 0)
        {
            var ids = state.RecentWorkspaces.Select(p => FindByPath(p)?.Id).OfType<string>().Distinct().ToList();
            state.RecentProjects = [.. ids, .. state.RecentProjects.Where(id => !ids.Contains(id))];
            state.RecentWorkspaces.Clear();
            changed = true;
        }
        foreach (var key in state.PermissionModes.Keys.ToList())
        {
            // 旧版按路径记，新版按 id 记（32 位十六进制，不含路径分隔符）
            if (key.Contains(Path.DirectorySeparatorChar) || key.Contains(Path.AltDirectorySeparatorChar))
            {
                var mode = state.PermissionModes[key];
                state.PermissionModes.Remove(key);
                if (FindByPath(key) is { } project)
                {
                    state.PermissionModes.TryAdd(project.Id, mode);
                }
                changed = true;
            }
        }
        if (save)
        {
            try
            {
                ProjectsFile.Save(_projects, _path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to write projects.json during migration.");
                return;
            }
        }
        if (changed)
        {
            _state.Save();
        }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TKCode.Config;

/// <summary>
/// 一个项目（docs/design/projects.md §5.1）：名称、设备与若干文件夹，第一个文件夹是主文件夹。
/// 会话通过 <see cref="Id"/> 归属到项目。文件夹都是规范化后的绝对路径，且互不重复。
/// </summary>
public sealed record Project(string Id, string Name, string Device, IReadOnlyList<string> Folders, bool Pinned, DateTimeOffset CreatedAt)
{
    public const string LocalDevice = "local";

    /// <summary>主文件夹：新会话的 cwd，也是 git、AGENTS.md、.agents、.mcp.json 与记忆的来源。</summary>
    public string Primary => Folders[0];

    /// <summary>主文件夹以外的文件夹，保持原来的顺序。</summary>
    public IReadOnlyList<string> Additional => Folders.Count > 1 ? Folders.Skip(1).ToArray() : [];

    public bool HasFolder(string folder) => Folders.Any(f => PathRules.SamePath(f, folder));

    public static string NewId() => Guid.CreateVersion7().ToString("N");

    /// <summary>新建项目；<paramref name="name"/> 为空时取主文件夹的名称。</summary>
    public static Project Create(string? name, IEnumerable<string> folders, bool pinned = false)
    {
        var list = NormalizeFolders(folders);
        if (list.Count == 0)
        {
            throw new ArgumentException("A project needs at least one folder.", nameof(folders));
        }
        return new Project(NewId(), NameOrDefault(name, list[0]), LocalDevice, list, pinned, DateTimeOffset.UtcNow);
    }

    /// <summary>修改名称与文件夹（id、创建时间不变）。</summary>
    public Project With(string? name, IEnumerable<string> folders)
    {
        var list = NormalizeFolders(folders);
        if (list.Count == 0)
        {
            throw new ArgumentException("A project needs at least one folder.", nameof(folders));
        }
        return this with { Name = NameOrDefault(name, list[0]), Folders = list };
    }

    /// <summary>文件夹的默认显示名：最后一级目录名（根目录时为路径本身）。</summary>
    public static string FolderName(string folder)
    {
        var normalized = PathRules.Normalize(folder);
        var name = Path.GetFileName(normalized);
        return name.Length > 0 ? name : normalized;
    }

    /// <summary>规范化、去掉空项与重复项（按 <see cref="PathRules.SamePath"/>），保持原来的顺序。</summary>
    public static IReadOnlyList<string> NormalizeFolders(IEnumerable<string> folders)
    {
        var list = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }
            var normalized = PathRules.Normalize(folder);
            if (!list.Any(f => PathRules.SamePath(f, normalized)))
            {
                list.Add(normalized);
            }
        }
        return list;
    }

    private static string NameOrDefault(string? name, string primary) =>
        string.IsNullOrWhiteSpace(name) ? FolderName(primary) : name.Trim();
}

/// <param name="Exists">文件存在（不存在时 <see cref="Projects"/> 为空，Desktop 据此决定是否从 state.json 迁移）。</param>
/// <param name="Error">文件存在但无法解析时的原因；此时 <see cref="Projects"/> 为空。</param>
public sealed record ProjectsLoadResult(IReadOnlyList<Project> Projects, bool Exists, string? Error);

/// <summary>读写 ~/.tkcode/projects.json。保存时先写临时文件再替换，读取时跳过缺少 id 或文件夹的条目。</summary>
public static class ProjectsFile
{
    public const int CurrentVersion = 1;

    public static ProjectsLoadResult Load(string? path = null)
    {
        path ??= ConfigPaths.ProjectsFile;
        if (!File.Exists(path))
        {
            return new ProjectsLoadResult([], false, null);
        }
        try
        {
            return new ProjectsLoadResult(Parse(File.ReadAllText(path)), true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProjectsLoadResult([], true, ex.Message);
        }
        catch (JsonException ex)
        {
            return new ProjectsLoadResult([], true, ex.Message);
        }
    }

    public static IReadOnlyList<Project> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var document = JsonSerializer.Deserialize(text, ProjectsJsonContext.Default.ProjectsDocument) ?? new ProjectsDocument();
        var projects = new List<Project>();
        foreach (var item in document.Projects)
        {
            var folders = Project.NormalizeFolders(item.Folders ?? []);
            if (string.IsNullOrWhiteSpace(item.Id) || folders.Count == 0 || projects.Any(p => p.Id == item.Id))
            {
                continue;
            }
            projects.Add(new Project(item.Id, string.IsNullOrWhiteSpace(item.Name) ? Project.FolderName(folders[0]) : item.Name,
                string.IsNullOrWhiteSpace(item.Device) ? Project.LocalDevice : item.Device, folders, item.Pinned, item.CreatedAt));
        }
        return projects;
    }

    public static void Save(IReadOnlyList<Project> projects, string? path = null)
    {
        path ??= ConfigPaths.ProjectsFile;
        var document = new ProjectsDocument
        {
            Version = CurrentVersion,
            Projects = projects.Select(p => new ProjectJson
            {
                Id = p.Id,
                Name = p.Name,
                Device = p.Device,
                Folders = [.. p.Folders],
                Pinned = p.Pinned,
                CreatedAt = p.CreatedAt,
            }).ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, ProjectsJsonContext.Default.ProjectsDocument) + "\n", new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>把无法解析的文件改名留存（projects.json.corrupt-时间），之后按空表重新开始，不覆盖用户的数据。</summary>
    public static string? MoveAside(string? path = null)
    {
        path ??= ConfigPaths.ProjectsFile;
        try
        {
            var target = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Host 端读取项目表：按路径、修改时间与长度缓存，每次访问只做一次 stat。文件损坏或正在被替换时保留上一次成功读取的结果。
/// </summary>
public sealed class ProjectCatalog
{
    /// <summary>会话查询的项目筛选值：只要不属于任何项目的会话。</summary>
    public const string NoProjectFilter = "";

    private readonly Func<string> _path;
    private readonly Lock _gate = new();
    private (string Path, DateTime Time, long Length)? _stamp;
    private IReadOnlyList<Project> _projects = [];

    /// <param name="path">项目表的位置；默认 <see cref="ConfigPaths.ProjectsFile"/>（每次访问时重新取，跟随 TKCODE_HOME）。</param>
    public ProjectCatalog(Func<string>? path = null)
    {
        _path = path ?? (() => ConfigPaths.ProjectsFile);
    }

    /// <summary>当前的项目，按 projects.json 中的顺序。</summary>
    public IReadOnlyList<Project> Projects
    {
        get
        {
            var path = _path();
            var info = new FileInfo(path);
            lock (_gate)
            {
                if (!info.Exists)
                {
                    _stamp = (path, DateTime.MinValue, -1);
                    _projects = [];
                    return _projects;
                }
                var stamp = (path, info.LastWriteTimeUtc, info.Length);
                if (_stamp != stamp)
                {
                    var result = ProjectsFile.Load(path);
                    if (result.Error is null)
                    {
                        _projects = result.Projects;
                        _stamp = stamp;
                    }
                }
                return _projects;
            }
        }
    }

    public Project? Find(string? id) => id is null ? null : Projects.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// 会话所属的项目（docs/design/projects.md §5.3）：<paramref name="projectId"/> 指向现存的项目时归它；
    /// 否则（旧会话，或项目已被移除）归到第一个主文件夹等于 <paramref name="cwd"/> 的项目；都没有时为 null。
    /// </summary>
    public Project? Resolve(string? projectId, string cwd) => Resolve(Projects, projectId, cwd);

    public static Project? Resolve(IReadOnlyList<Project> projects, string? projectId, string cwd) =>
        (projectId is null ? null : projects.FirstOrDefault(p => p.Id == projectId))
        ?? projects.FirstOrDefault(p => PathRules.SamePath(p.Primary, cwd));
}

internal sealed class ProjectsDocument
{
    public int Version { get; set; } = ProjectsFile.CurrentVersion;
    public List<ProjectJson> Projects { get; set; } = [];
}

internal sealed class ProjectJson
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Device { get; set; } = Project.LocalDevice;
    public List<string>? Folders { get; set; }
    public bool Pinned { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ProjectsDocument))]
internal sealed partial class ProjectsJsonContext : JsonSerializerContext;

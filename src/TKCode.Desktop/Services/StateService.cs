using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TKCode.Config;

namespace TKCode.Desktop.Services;

public enum ThemeMode
{
    System,
    Dark,
    Light,
}

/// <summary>程序自己记录的状态（~/.tkcode/state.json），用户无需手工编辑。用户配置在 settings.json（<see cref="UserSettingsService"/>）。</summary>
public sealed class AppState
{
    /// <summary>旧版的项目路径列表：只在迁移时读取（docs/design/projects.md §5.2），迁移后清空。项目在 projects.json。</summary>
    public List<string> Workspaces { get; set; } = [];
    /// <summary>旧版：上次选择的项目路径；迁移成 <see cref="LastProjectId"/>。</summary>
    public string? LastWorkspace { get; set; }
    /// <summary>旧版：最近使用的项目路径；迁移成 <see cref="RecentProjects"/>。</summary>
    public List<string> RecentWorkspaces { get; set; } = [];
    /// <summary>侧栏上次选中的项目（新会话的默认项目）。</summary>
    public string? LastProjectId { get; set; }
    /// <summary>最近使用的项目 id，最近的在前；托盘、Dock、任务栏跳转列表的项目菜单按它排序。</summary>
    public List<string> RecentProjects { get; set; } = [];
    /// <summary>上次选择的模型（Ref：厂商/模型）。</summary>
    public string? LastModelId { get; set; }
    /// <summary>收藏的模型（Ref）。</summary>
    public List<string> FavoriteModels { get; set; } = [];
    /// <summary>上次选择的会话参数；新会话按当前模型的可选值就近修正。</summary>
    public string? LastThinkingLevel { get; set; }
    public bool LastFastMode { get; set; }
    public int? LastContextWindow { get; set; }
    /// <summary>每个项目（id）上次选择的权限档位：ask / autoApprove / fullAccess。新会话沿用，包括完全授权。</summary>
    public Dictionary<string, string> PermissionModes { get; set; } = [];
    /// <summary>每个工作区最近在输入框里 @ 过的路径（相对路径，目录以 / 结尾，最近的在前）。</summary>
    public Dictionary<string, List<string>> RecentMentions { get; set; } = [];
    /// <summary>应用内更新的记录（docs/design/auto-update.md §8）。</summary>
    public UpdateRecord Update { get; set; } = new();

    /// <summary>把项目移到最近使用的最前面（超出上限的丢弃）。</summary>
    public void TouchRecentProject(string id)
    {
        RecentProjects.Remove(id);
        RecentProjects.Insert(0, id);
        if (RecentProjects.Count > 32)
        {
            RecentProjects.RemoveRange(32, RecentProjects.Count - 32);
        }
    }
}

public sealed class UpdateRecord
{
    public DateTimeOffset? LastCheckAt { get; set; }
    /// <summary>Release 列表的 ETag（响应体缓存在 updates/releases-cache.json）：304 时复用，不计入 GitHub 未认证配额。</summary>
    public string? ETag { get; set; }
    /// <summary>「跳过此版本」：自动检查不再提醒这个版本，手动检查仍然显示。</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>已下载并校验、等待安装的版本（暂存在 ~/.tkcode/updates/&lt;版本&gt;/）。</summary>
    public string? PendingVersion { get; set; }
    /// <summary>已启动安装程序的版本：下次启动时据此判断更新成功（当前版本等于它）还是没有完成。</summary>
    public string? InstallingVersion { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppState))]
internal sealed partial class StateJsonContext : JsonSerializerContext;

/// <summary>读写 state.json。首次运行时从旧的 desktop.json 迁移（语言、主题移到 settings.json）。</summary>
public sealed class StateService
{
    private readonly string _path;

    public StateService()
        : this(ConfigPaths.StateFile)
    {
    }

    /// <summary>指定状态文件位置（测试用）。</summary>
    public StateService(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppState Current { get; }

    public void Save() => Write(Current);

    private AppState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(_path), StateJsonContext.Default.AppState) ?? new();
            }
            var legacy = Path.Combine(Path.GetDirectoryName(_path)!, "desktop.json");
            if (File.Exists(legacy))
            {
                return MigrateLegacy(legacy);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
        return new AppState();
    }

    /// <summary>desktop.json → state.json；其中的 language / themeMode 移到 settings.json（settings.json 已有时不覆盖）。</summary>
    private AppState MigrateLegacy(string legacy)
    {
        var text = File.ReadAllText(legacy);
        var state = JsonSerializer.Deserialize(text, StateJsonContext.Default.AppState) ?? new AppState();
        if (JsonNode.Parse(text) is JsonObject old)
        {
            var language = old["language"]?.GetValue<string>();
            var theme = old["themeMode"]?.GetValue<string>()?.ToLowerInvariant();
            if (language is not null || theme is not null)
            {
                SettingsFile.Update(root =>
                {
                    if (language is not null && root["language"] is null)
                    {
                        root["language"] = language;
                    }
                    if (theme is not null && root["theme"] is null)
                    {
                        root["theme"] = theme;
                    }
                }, Path.Combine(Path.GetDirectoryName(_path)!, "settings.json"));
            }
        }
        Write(state);
        File.Move(legacy, legacy + ".bak", overwrite: true);
        return state;
    }

    private void Write(AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(state, StateJsonContext.Default.AppState));
    }
}

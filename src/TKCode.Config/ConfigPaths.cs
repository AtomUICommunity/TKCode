namespace TKCode.Config;

/// <summary>TKCode 数据目录（默认 ~/.tkcode，环境变量 TKCODE_HOME 可覆盖）与其中的文件。</summary>
public static class ConfigPaths
{
    public const string HomeVariable = "TKCODE_HOME";

    public static string Home =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tkcode");

    public static string SettingsFile => Path.Combine(Home, "settings.json");
    public static string SchemaFile => Path.Combine(Home, "settings.schema.json");
    public static string StateFile => Path.Combine(Home, "state.json");

    /// <summary>项目表（docs/design/projects.md §5.1）：Desktop 写、Host 读。</summary>
    public static string ProjectsFile => Path.Combine(Home, "projects.json");
}

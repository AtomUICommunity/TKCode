using Microsoft.Extensions.Logging;
using TKCode.Config;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels.Completion;

/// <summary>
/// 当前项目生效的 command / skill / role（Host 的 <c>assets/list</c>），按 cwd 缓存；
/// 定义文件变化（<c>assets/changed</c>）或换了项目时重新拉取。
/// </summary>
public sealed class AssetCatalogCache
{
    private readonly AgentHostConnection _host;
    private readonly Func<string?> _cwd;
    private readonly ILogger _logger;
    private (string? Cwd, Task<AgentAssetsDto?> Task)? _cached;

    public AssetCatalogCache(AgentHostConnection host, Func<string?> cwd, ILogger logger)
    {
        _host = host;
        _cwd = cwd;
        _logger = logger;
        host.AssetsChanged += _ =>
        {
            _cached = null;
            Changed?.Invoke();
        };
    }

    /// <summary>定义文件变化（UI 线程上触发）。</summary>
    public event Action? Changed;

    public Task<AgentAssetsDto?> GetAsync()
    {
        var cwd = _cwd();
        if (_cached is { } cached && cached.Cwd == cwd && !cached.Task.IsFaulted)
        {
            return cached.Task;
        }
        var task = LoadAsync(cwd);
        _cached = (cwd, task);
        return task;
    }

    private async Task<AgentAssetsDto?> LoadAsync(string? cwd)
    {
        try
        {
            var server = await _host.GetServerAsync();
            return await server.ListAssetsAsync(cwd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing commands, skills and roles failed");
            _cached = null;
            return null;
        }
    }

    public static string SourceLabel(string source) =>
        Strings.Get(source == "project" ? TKCodeLangResourceKind.AssetSourceProject : TKCodeLangResourceKind.AssetSourceGlobal);
}

/// <summary>/ 补全：自定义 command（~/.tkcode/commands 与 .agents/commands），选中后插入 command tag（docs/design/composer.md §9.2）。</summary>
public sealed class CommandCompletionProvider(AssetCatalogCache assets) : ICompletionProvider
{
    public char Trigger => '/';

    public int Order => 20;

    public async ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        if (await assets.GetAsync() is not { } catalog)
        {
            return [];
        }
        var items = new List<CompletionItem>();
        foreach (var command in catalog.Commands)
        {
            // 与内置命令重名的自定义 command 被遮住（设置页提示）
            if (BuiltinCommandProvider.Names.Contains(command.Name) ||
                FuzzyMatcher.Match(query, command.Name, command.Name.LastIndexOf(':') + 1) is not { } match)
            {
                continue;
            }
            var tag = new TagSegment(TagKind.Command, command.Name, command.Name);
            items.Add(new CompletionItem(command.Name, command.Description, LucideKind.SquareTerminal, AssetCatalogCache.SourceLabel(command.Source),
                new CompletionAction(tag), match.Score, match.Positions));
        }
        return items;
    }
}

/// <summary>/ 补全：内置命令。多数在界面里直接执行、不发给模型（docs/design/composer.md §9.1）；
/// Run 为 null 的是提示词型命令（/init、/commit），选中后插入同名 command tag，正文由 Host 展开后发给模型（§9.3）。</summary>
public sealed class BuiltinCommandProvider(IReadOnlyList<BuiltinCommandProvider.Command> commands) : ICompletionProvider
{
    /// <param name="Run">选中后执行；null 表示插入 command tag。</param>
    /// <param name="IsAvailable">当前是否可用（例如 /fork 需要已有会话）；null 表示总是可用。</param>
    public sealed record Command(string Name, TKCodeLangResourceKind Description, LucideKind Icon, Action? Run, Func<bool>? IsAvailable = null);

    /// <summary>内置命令的名称：同名的自定义 command 被遮住。</summary>
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase) { "new", "model", "fork", "role", "compact", "init", "commit", "agent", "plan", "acp", "add-dir" };

    public char Trigger => '/';

    public int Order => 10;

    public ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        var items = new List<CompletionItem>();
        foreach (var command in commands)
        {
            if (command.IsAvailable?.Invoke() == false || FuzzyMatcher.Match(query, command.Name) is not { } match)
            {
                continue;
            }
            var action = command.Run is { } run
                ? new CompletionAction(Run: run)
                : new CompletionAction(new TagSegment(TagKind.Command, command.Name, command.Name));
            items.Add(new CompletionItem(command.Name, Strings.Get(command.Description), command.Icon, Strings.Get(TKCodeLangResourceKind.CommandSourceBuiltin),
                action, match.Score, match.Positions));
        }
        return ValueTask.FromResult<IReadOnlyList<CompletionItem>>(items);
    }
}

/// <summary>/ 补全：skills（~/.tkcode/skills 与 .agents/skills），选中后插入 skill tag；发送时 Host 把 SKILL.md 附在消息前面（docs/design/composer.md §10.2）。</summary>
public sealed class SkillCompletionProvider(AssetCatalogCache assets) : ICompletionProvider
{
    public char Trigger => '/';

    public int Order => 30;

    public async ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        if (await assets.GetAsync() is not { } catalog)
        {
            return [];
        }
        var items = new List<CompletionItem>();
        foreach (var skill in catalog.Skills)
        {
            if (BuiltinCommandProvider.Names.Contains(skill.Name) || FuzzyMatcher.Match(query, skill.Name) is not { } match)
            {
                continue;
            }
            var tag = new TagSegment(TagKind.Skill, skill.Name, skill.Name);
            items.Add(new CompletionItem(skill.Name, skill.Description, LucideKind.Sparkles, AssetCatalogCache.SourceLabel(skill.Source),
                new CompletionAction(tag), match.Score, match.Positions));
        }
        return items;
    }
}

/// <summary>/ 补全：role（模式），选中即切换，不生成 tag（docs/design/composer.md §10.3）。</summary>
public sealed class RoleCompletionProvider(RolePickerViewModel roles) : ICompletionProvider
{
    public char Trigger => '/';

    public int Order => 5;

    public ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        var items = new List<CompletionItem>();
        foreach (var role in roles.Roles.ToList())
        {
            if (role.IsSelected || FuzzyMatcher.Match(query, role.Name) is not { } match)
            {
                continue;
            }
            var name = role.Name;
            items.Add(new CompletionItem(role.Name, role.Description, role.Icon, Strings.Get(TKCodeLangResourceKind.RoleTitle),
                new CompletionAction(Run: () => roles.Choose(name)), match.Score, match.Positions));
        }
        return ValueTask.FromResult<IReadOnlyList<CompletionItem>>(items);
    }
}

/// <summary>
/// / 补全：外部运行时自带的命令（Claude / ACP Agent 报告的列表），选中后插入运行时命令 tag，
/// 发送时原样以 /名称 交给运行时（docs/design/composer.md §11）。
/// </summary>
/// <param name="commands">当前会话的运行时命令与运行时名称；不是外部运行时会话时为空。</param>
public sealed class RuntimeCommandProvider(Func<(IReadOnlyList<RuntimeCommandDto> Commands, string? RuntimeName)> commands) : ICompletionProvider
{
    public char Trigger => '/';

    public int Order => 40;

    public ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        var (list, runtime) = commands();
        var items = new List<CompletionItem>();
        foreach (var command in list)
        {
            if (FuzzyMatcher.Match(query, command.Name) is not { } match)
            {
                continue;
            }
            var subtitle = command.ArgumentHint is { Length: > 0 } hint ? $"{hint}  {command.Description}".Trim() : command.Description;
            items.Add(new CompletionItem(command.Name, subtitle, LucideKind.SquareTerminal, runtime,
                new CompletionAction(new TagSegment(TagKind.RuntimeCommand, command.Name, command.Name)), match.Score, match.Positions));
        }
        return ValueTask.FromResult<IReadOnlyList<CompletionItem>>(items);
    }
}

using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Sessions;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Tasks;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai;

namespace TKCode.Agent.Coding;

/// <summary>内置 Agent 会话的构建选项。子 Agent 通过共享工作区、后台任务、任务清单与审批记录与父会话协作。</summary>
public sealed record BuiltinOptions
{
    public WorkspaceRoots? Workspace { get; init; }
    public BackgroundTasks? BackgroundTasks { get; init; }
    public TaskList? Tasks { get; init; }
    public SessionApprovals? Approvals { get; init; }
    public ToolSet ToolSet { get; init; } = ToolSet.Full;

    /// <summary>追加在 cwd 节之前的系统提示节（例如子 Agent 的角色说明）。</summary>
    public IReadOnlyList<PromptSection>? ExtraPromptSections { get; init; }

    public ToolServices Services { get; init; } = ToolServices.None;

    /// <summary>自动压缩上下文（settings.json 的 autoCompact，docs/design/compaction.md §4）；手动 /compact 不受影响。</summary>
    public bool AutoCompact { get; init; } = true;

    /// <summary>缓存断点的有效期（settings.json 的 promptCacheTtl），每次请求读取；为 null 时用 5 分钟。</summary>
    public Func<PromptCacheTtl>? CacheTtl { get; init; }

    /// <summary>bash 工具使用的 shell（settings.json 的 agent.shell.profile），每次执行命令时读取；为 null 时用默认选择。</summary>
    public Func<string?>? ShellProfile { get; init; }

    /// <summary>
    /// 附加目录的来源（docs/design/projects.md §6.3）：每批用户输入之前取一次，用 <see cref="WorkspaceRoots.Sync"/> 替换工作区的附加目录。
    /// 返回全部附加目录与其中只属于本会话的目录。null 表示不同步（子 Agent 跟随父会话的工作区）。
    /// </summary>
    public Func<SessionStore, (IReadOnlyList<string> All, IReadOnlyList<string> SessionOnly)>? AdditionalDirectories { get; init; }

    /// <summary>
    /// 项目记忆目录（settings.json 的 autoMemory，docs/design/memory.md）：加 memory 节、在会话开头注入索引、目录读写免审批。
    /// null 表示不启用（子 Agent 不启用）。
    /// </summary>
    public string? MemoryDirectory { get; init; }
}

public enum ToolSet
{
    /// <summary>全部工具（主会话）。</summary>
    Full,

    /// <summary>不能再派生子 Agent（general-purpose 子 Agent）。</summary>
    NoSubagents,

    /// <summary>只读：不能写文件、不能派生子 Agent（explore / plan 子 Agent）。</summary>
    ReadOnly,
}

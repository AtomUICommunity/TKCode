using System.Text;
using TKCode.Agent.Coding.Memory;
using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Tools.Files;
using TKCode.Agent.Coding.Tools.Tasks;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Models;

namespace TKCode.Agent.Coding.Tools;

/// <summary>同一会话内所有内置工具共享的状态。工具实例按会话创建，并持有这份上下文。</summary>
public sealed class ToolContext(WorkspaceRoots workspace, string sessionId, BackgroundTasks? backgroundTasks = null, TaskList? tasks = null)
{
    private readonly HashSet<string> _nestedContextFiles = new(PathUtil.Comparer);
    private readonly Lock _gate = new();

    public WorkspaceRoots Workspace { get; } = workspace;

    public string Cwd => Workspace.Cwd;

    /// <summary>当前会话 id（子 Agent 为子会话自己的 id）。</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>命令输出等临时文件的目录：~/.tkcode/tmp/&lt;会话 id&gt;。</summary>
    public string TempDirectory => Path.Combine(TKCodePaths.Home, "tmp", SessionId);

    /// <summary>后台任务（主会话与子 Agent 共用）。</summary>
    public BackgroundTasks BackgroundTasks { get; } = backgroundTasks ?? new BackgroundTasks();

    /// <summary>任务清单（主会话与子 Agent 共用）。</summary>
    public TaskList Tasks { get; } = tasks ?? new TaskList();

    /// <summary>宿主提供的外部能力（网页搜索、子 Agent 协调等）；为 null 的能力对应的工具不会出现。</summary>
    public ToolServices Services { get; init; } = ToolServices.None;

    /// <summary>本会话的动态工具来源（MCP）；会话关闭时释放。</summary>
    public IDynamicToolSource? DynamicTools { get; set; }

    /// <summary>会话的 role（类似 Cursor 的模式）：过滤工具并加入 role 节，在下一批用户输入前生效。null 等同于 agent。</summary>
    public Assets.SessionRole? Role { get; set; }

    /// <summary>本会话的计划文件（docs/design/plan-mode.md §6）：~/.tkcode/plans/&lt;会话文件名&gt;.md；子 Agent 为 null。</summary>
    public string? PlanFile { get; init; }

    /// <summary>本会话的项目记忆（docs/design/memory.md）：加 memory 节、注入索引；子 Agent 与关闭了 autoMemory 时为 null。</summary>
    public ProjectMemory? Memory { get; init; }

    /// <summary>会话文件（完整记录）的路径；清空上下文后的交接消息据此告诉模型去哪里查。</summary>
    public string? TranscriptPath { get; init; }

    /// <summary>只读工具集（explore / plan 子 Agent）。</summary>
    public bool ReadOnlyToolSet { get; init; }

    /// <summary>只读上下文（只读工具集或只读模式）：可能修改工作区的命令在任何档位都要审批（docs/design/plan-mode.md §8）。</summary>
    public bool IsReadOnly => ReadOnlyToolSet || Role is { IsReadOnly: true };

    /// <summary>处于 Plan 模式：write / edit 只放行计划文件与记忆文件（docs/design/memory.md §5）。</summary>
    public bool InPlanMode => PlanFile is not null && Role?.Name == Assets.SessionRole.PlanName;

    public bool IsPlanFile(string path) => PlanFile is not null && PathUtil.SamePath(path, PlanFile);

    /// <summary>Plan 模式下 write / edit 可以写的路径：计划文件与记忆文件。</summary>
    public bool WritableInPlanMode(string path) => IsPlanFile(path) || Workspace.IsMemoryFile(path);

    /// <summary>
    /// write / edit 写入前的内容处理：记忆文件补 frontmatter 字段（docs/design/memory.md §6），其他文件原样返回。
    /// </summary>
    public string PrepareWrite(string path, string content) =>
        Workspace.MemoryDirectory is { } memory && Workspace.IsMemoryFile(path) ? ProjectMemory.PrepareWrite(memory, path, content, SessionId) : content;

    /// <summary>write / edit 写入后附在结果后的提醒：MEMORY.md 接近或超过上限时才有。</summary>
    public string? WriteReminder(string path, string content) =>
        Workspace.MemoryDirectory is { } memory && Workspace.IsMemoryFile(path) ? ProjectMemory.WriteReminder(memory, path, content) : null;

    /// <summary>Plan 模式下写计划文件以外的路径时的错误（docs/design/plan-mode.md §12.7）。</summary>
    public string PlanModeWriteMessage =>
        $"In Plan mode you can only write the plan file ({PlanFile}). Put the change in the plan instead; it will be made after the user approves.";

    /// <summary>工具切换了模式之后调用：新的工具集与模式节在下一次模型请求前生效（Agent.RequestContextRefresh）。</summary>
    public Action RequestContextRefresh { get; set; } = () => { };

    /// <summary>下一次模型请求前把历史换成交接消息（Agent.RequestHandoff）。</summary>
    public Action<string> RequestHandoff { get; set; } = _ => { };

    /// <summary>bash 工具使用的 shell（settings.json 的 agent.shell.profile，null 为默认）；每次执行命令时读取，修改后立即生效。</summary>
    public Func<string?> ShellProfile { get; set; } = () => null;

    /// <summary>当前模型（模型可在会话中途切换）。</summary>
    public Func<ModelInfo?> CurrentModel { get; set; } = () => null;

    /// <summary>同一文件的写入串行。</summary>
    public FileMutationQueue Mutations { get; } = new();

    /// <summary>当前模型能否查看图片（模型可在会话中途切换）。</summary>
    public Func<bool> ModelSupportsImages { get; set; } = () => true;

    /// <summary>读过的文件（先读后改、过期检测）。</summary>
    public FileStateTracker Files { get; } = new();

    /// <summary>用户在界面上回退的文件，下一批输入前告诉 Agent（docs/design/message-rewind.md §5.3）。</summary>
    public RevertNotices RevertNotices { get; } = new();

    /// <summary>压缩后重新按需加载子目录的上下文文件（之前附在工具结果里的已被摘要替换）。</summary>
    public void ResetNestedInstructions()
    {
        lock (_gate)
        {
            _nestedContextFiles.Clear();
        }
    }

    /// <summary>
    /// 子目录的上下文文件按需加载：访问 cwd 子目录中的文件时，返回途中尚未加载过的 AGENTS.md 等，
    /// 作为 &lt;system-reminder&gt; 附在工具结果后面。cwd 及其上级的文件已在系统提示中，不再重复。
    /// </summary>
    public string? NestedInstructionsFor(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (dir is null || !PathUtil.IsUnder(PathUtil.RealPath(dir), PathUtil.RealPath(Cwd)) || PathUtil.SamePath(dir, Cwd))
        {
            return null;
        }
        var found = new List<string>();
        for (var current = new DirectoryInfo(dir); current is not null && !PathUtil.SamePath(current.FullName, Cwd); current = current.Parent)
        {
            if (SystemPromptBuilder.ContextFileNames.Select(n => Path.Combine(current.FullName, n)).FirstOrDefault(File.Exists) is { } file)
            {
                lock (_gate)
                {
                    if (_nestedContextFiles.Add(PathUtil.RealPath(file)))
                    {
                        found.Add(file);
                    }
                }
            }
        }
        if (found.Count == 0)
        {
            return null;
        }
        found.Reverse();
        var sb = new StringBuilder("<system-reminder>\n");
        // @import 只展开项目根之内的文件（docs/design/memory.md §7）；导入过的文件同样只附加一次
        var scope = PathUtil.RealPath(SystemPromptBuilder.ProjectRoot(Cwd));
        foreach (var file in found)
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            sb.Append($"Instructions from {file} (they apply to files under {Path.GetDirectoryName(file)}):\n").Append(Clip(content)).Append("\n\n");
            foreach (var (imported, text) in InstructionImports.Expand(file, content, scope, FirstNestedVisit))
            {
                sb.Append($"Instructions from {imported} (imported by {file}):\n").Append(Clip(text)).Append("\n\n");
            }
        }
        return sb.ToString().TrimEnd() + "\n</system-reminder>";

        static string Clip(string content)
        {
            content = content.Trim();
            return content.Length > 16 * 1024 ? content[..(16 * 1024)] + "\n[truncated]" : content;
        }
    }

    private bool FirstNestedVisit(string realPath)
    {
        lock (_gate)
        {
            return _nestedContextFiles.Add(realPath);
        }
    }
}

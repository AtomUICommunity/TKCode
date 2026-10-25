using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TKCode.Agent.Coding.Processes;

namespace TKCode.Agent.Coding.Tools.Shell;

/// <summary>
/// 在工作区根目录执行 shell 命令（docs/design/builtin-agent-tools.md §6）。
/// 前台：超时默认 2 分钟、最长 10 分钟，输出保留尾部，完整输出写入日志文件；中止或超时时终止整个进程树。
/// 后台（run_in_background）：立即返回任务 id 与日志路径，结束时向会话注入 task-notification。
/// </summary>
public sealed class BashTool(ToolContext context) : IAgentTool
{
    public const int DefaultTimeoutMs = 120_000;
    public const int MaxTimeoutMs = 600_000;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PipeDrainTimeout = TimeSpan.FromSeconds(2);

    public string Name => "bash";
    public string Label => "Tool.Bash";

    public string Description
    {
        get
        {
            // 不写具体的 shell：工具集在会话开始时冻结，而用户可以随时改 shell；当前的 shell 在系统提示的 environment 节里，会随之更新。
            return $"Execute a shell command on {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")} " +
                   "in the primary working directory and return its combined stdout/stderr. Write the command in the syntax of the shell named on the Shell line of the environment section. " +
                   "Each call runs in a fresh process: `cd` and environment variables do not persist between calls, so use `cd dir && command`. " +
                   $"timeout is in milliseconds (default {DefaultTimeoutMs}, max {MaxTimeoutMs}). " +
                   $"Output is truncated to the last {OutputCapture.MaxLines} lines or {OutputCapture.MaxBytes / 1024}KB; the full output is saved to a file whose path is included when truncated. " +
                   "Set run_in_background=true for long-running processes (dev servers, watchers); you will be notified when it finishes, can read its output file with the read tool, and can stop it with task_stop. " +
                   "Avoid interactive commands; stdin is closed.";
        }
    }

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "command":{"type":"string","description":"The command to execute."},
          "description":{"type":"string","description":"Clear, concise description of what this command does (5-10 words), shown to the user."},
          "timeout":{"type":"integer","minimum":1000,"maximum":600000,"description":"Timeout in milliseconds (default 120000, max 600000)."},
          "run_in_background":{"type":"boolean","description":"Run the command in the background and return immediately."}},
         "required":["command"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Execute shell commands (optionally in the background)";

    public IReadOnlyList<string> PromptGuidelines =>
    [
        "Prefer the dedicated tools (read, edit, write, grep, glob, ls) over shell equivalents such as cat, sed, grep, find or echo > file.",
        "Quote paths that contain spaces in shell commands.",
    ];

    /// <summary>命令之间可能有依赖（例如先构建后测试），同一批内的调用串行执行。</summary>
    public ToolConcurrency Concurrency => ToolConcurrency.Exclusive;

    public ToolPermission DescribePermission(JsonElement arguments)
    {
        var command = arguments.Required("command");
        return new(ToolAccess.Execute, Command: command, Title: command);
    }

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        var command = args.Required("command");
        var description = args.String("description") ?? Truncate(command, 80);
        var timeout = Math.Clamp(args.Int("timeout") ?? DefaultTimeoutMs, 1000, MaxTimeoutMs);
        var background = args.Bool("run_in_background") ?? false;

        var logPath = Path.Combine(context.TempDirectory, $"bash-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..40] + ".log");
        var capture = new OutputCapture(logPath);
        Process process;
        try
        {
            process = Start(command, context.Cwd, ShellResolver.Resolve(context.ShellProfile()));
        }
        catch (Exception ex)
        {
            await capture.DisposeAsync();
            return AgentToolResult.Error($"Failed to start shell: {ex.Message}");
        }
        var pumps = Task.WhenAll(PumpAsync(process.StandardOutput, capture), PumpAsync(process.StandardError, capture));

        if (background)
        {
            var task = context.BackgroundTasks.Start("bash", description, logPath, () =>
            {
                ProcessRunner.Kill(process);
                return Task.CompletedTask;
            }, context.SessionId);
            _ = MonitorBackgroundAsync(task, process, pumps, capture);
            return AgentToolResult.Text(
                    $"Started background task {task.Id}: {description}\nOutput file: {logPath}\n" +
                    "You will be notified when it finishes. Use the read tool on the output file to check progress, and task_stop to stop it.")
                with
                {
                    Details = new CommandDetails(command, null, OutputPath: logPath, TaskId: task.Id).ToJson(),
                };
        }

        var timedOut = false;
        using var progressStop = new CancellationTokenSource();
        var progress = ReportProgressAsync(capture, invocation.Progress, progressStop.Token);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                ProcessRunner.Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
            }
            // 命令启动的后台子进程可能继续占用管道，只等待片刻。
            await pumps.WaitAsync(PipeDrainTimeout, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        finally
        {
            await progressStop.CancelAsync();
            await progress;
            await capture.DisposeAsync();
        }
        if (cancellationToken.IsCancellationRequested)
        {
            TryDelete(logPath);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var exitCode = timedOut ? (int?)null : process.ExitCode;
        process.Dispose();
        var (tail, truncated, totalLines) = capture.Tail();
        var sb = new StringBuilder();
        if (truncated)
        {
            sb.Append($"[Output truncated: showing the last lines of {totalLines}. Full output: {logPath}]\n");
        }
        sb.Append(tail.Length == 0 ? "(no output)" : tail);
        if (timedOut)
        {
            sb.Append($"\n\nCommand timed out after {timeout / 1000.0:0.#} seconds and was terminated. Use a larger timeout or run_in_background for long-running commands.");
        }
        else if (exitCode != 0)
        {
            sb.Append($"\n\nCommand exited with code {exitCode}.");
        }
        if (!truncated)
        {
            TryDelete(logPath);
        }
        return new AgentToolResult([new TKCode.Ai.Messages.TextContent(sb.ToString())])
        {
            IsError = timedOut || exitCode != 0,
            Details = new CommandDetails(command, exitCode, timedOut, truncated ? logPath : null).ToJson(),
        };
    }

    private async Task MonitorBackgroundAsync(BackgroundTask task, Process process, Task pumps, OutputCapture capture)
    {
        int? exitCode = null;
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            exitCode = process.ExitCode;
            await pumps.WaitAsync(PipeDrainTimeout, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        finally
        {
            await capture.DisposeAsync();
            process.Dispose();
        }
        var (tail, _, totalLines) = capture.Tail();
        var lastLines = string.Join('\n', tail.Split('\n').TakeLast(20));
        var status = task.StopRequested ? "stopped" : exitCode == 0 ? "completed" : "failed";
        context.BackgroundTasks.Complete(task, status,
            $"Exit code: {exitCode?.ToString() ?? "unknown"}. Output: {totalLines} lines (last lines below; read the output file for everything).\n{lastLines}");
    }

    private static Process Start(string command, string cwd, ShellInfo shell)
    {
        var info = new ProcessStartInfo(shell.Executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in shell.PrefixArguments)
        {
            info.ArgumentList.Add(arg);
        }
        info.ArgumentList.Add(shell.CommandPrefix + command);
        // 避免分页器与交互式提示卡住命令。
        info.Environment["PAGER"] = "cat";
        info.Environment["GIT_PAGER"] = "cat";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["TKCODE_AGENT"] = "1";
        var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
        process.StandardInput.Close();
        return process;
    }

    private static async Task PumpAsync(StreamReader reader, OutputCapture capture)
    {
        var buffer = new char[8192];
        int read;
        try
        {
            while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                capture.Append(buffer.AsSpan(0, read));
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task ReportProgressAsync(OutputCapture capture, IProgress<AgentToolResult>? progress, CancellationToken ct)
    {
        if (progress is null)
        {
            return;
        }
        long reported = -1;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ProgressInterval, ct);
                var total = capture.TotalChars;
                if (total != reported)
                {
                    reported = total;
                    progress.Report(AgentToolResult.Text(capture.Snapshot()));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Truncate(string text, int max)
    {
        var line = text.Split('\n')[0];
        return line.Length <= max ? line : line[..max] + "…";
    }
}

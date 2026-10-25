using System.Text.Json;
using System.Text.RegularExpressions;
using TKCode.Ai.Models;
using TKCode.Runtimes.Discovery;
using TKCode.Runtimes.Json;
using TKCode.Runtimes.Process;

namespace TKCode.Runtimes.Claude;

/// <summary>
/// Claude Agent（<c>claude -p</c> stream-json + 控制通道，规格 docs/protocols/claude-code-stream-json.md）。
/// 界面上称为 “Claude Agent”；运行时 id 为 <c>claude-code</c>。
/// 探测：<c>--version</c>、<c>auth status</c>，以及一个只做 initialize 的短命进程（不触发模型轮次，§5.1）。
/// </summary>
public sealed partial class ClaudeRuntime(ExecutableLocator? locator = null) : IAgentRuntime
{
    public const string RuntimeId = "claude-code";
    public const string DefaultModelId = "default";
    private static readonly Version MinimumVersion = new(2, 1, 290);
    private static readonly TimeSpan ProbeInitializeTimeout = TimeSpan.FromSeconds(15);

    private readonly ExecutableLocator _locator = locator ?? ExecutableLocator.Default;
    private RuntimeDescriptor _descriptor = new(RuntimeId, "Claude Agent", RuntimeStatus.Unknown);

    public string Id => RuntimeId;
    public string DisplayName => "Claude Agent";
    public RuntimeDescriptor Descriptor => _descriptor;
    public event Action? DescriptorChanged { add { } remove { } }

    private static readonly RuntimeCapabilities Capabilities = new()
    {
        SupportsSteering = true,
        SupportsResume = true,
        SupportsModelSwitch = true,
        StreamsToolArguments = true,
        SupportsSubagents = true,
        NativePlanMode = true,
    };

    /// <summary>所有会话都带的参数（§2.2）。</summary>
    internal static readonly string[] BaseArguments =
    [
        "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
        "--replay-user-messages", "--permission-prompt-tool", "stdio", "--thinking-display", "summarized", "--forward-subagent-text",
    ];

    public async Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken)
    {
        // Windows 只启动原生 claude.exe，拒绝 npm 的 .cmd/.bat shim（§2.1）
        var path = _locator.Find("claude", "TKCODE_CLAUDE_PATH", [Path.Combine(Home, ".claude", "local")],
            p => !OperatingSystem.IsWindows() || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (path is null)
        {
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.NotInstalled);
        }
        var versionText = await CliProcess.RunToCompletionAsync(path, ["--version"], TimeSpan.FromSeconds(10), cancellationToken, _locator);
        var version = versionText is null ? null : VersionPattern().Match(versionText) is { Success: true } m ? m.Value : null;
        var auth = await ReadAuthAsync(path, cancellationToken);
        if (auth is null)
        {
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.Error, path, version, null,
                "Claude is not logged in. Run `claude` in a terminal to log in.") { Capabilities = Capabilities };
        }
        IReadOnlyList<ModelInfo> models;
        try
        {
            models = await DiscoverModelsAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.Error, path, version, auth, ex.Message)
            {
                Capabilities = Capabilities,
            };
        }
        var warning = Version.TryParse(version, out var parsed) && parsed < MinimumVersion
            ? $"Claude {version} is older than the supported minimum {MinimumVersion}; some features may not work."
            : null;
        return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.Ready, path, version, auth, warning)
        {
            Capabilities = Capabilities,
            Models = models,
        };
    }

    public async Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
    {
        if (_descriptor is not { Status: RuntimeStatus.Ready, ExecutablePath: { } path })
        {
            throw new InvalidOperationException($"Claude Agent is not available ({_descriptor.Error ?? _descriptor.Status.ToString()}).");
        }
        var options = request.Options;
        var sessionId = request.ResumeSessionId is { } resume && Guid.TryParse(resume, out _) ? resume : null;
        var newSessionId = sessionId is null ? Guid.NewGuid().ToString() : null;
        var model = options.Model.Id == DefaultModelId ? null : options.Model.Id;
        var effort = EffortFor(options.Model, options.ThinkingLevel);
        // 完全授权 = bypassPermissions；其余档位用 default（自动放行由 TKCode 自动允许审批实现）。
        // --allow-dangerously-skip-permissions 只是允许会话中途切换到 bypassPermissions（§5.3），本身不放开任何权限。
        var args = new List<string>(BaseArguments)
        {
            "--permission-mode", PermissionModeFor(options), "--allow-dangerously-skip-permissions",
        };
        if (model is not null)
        {
            args.AddRange(["--model", model]);
        }
        if (effort is not null)
        {
            args.AddRange(["--effort", effort]);
        }
        // --resume / --session-id 一律用 --flag=value 形式，防止以 '-' 开头的值被解析成参数（§2.2）
        args.Add(sessionId is not null ? $"--resume={sessionId}" : $"--session-id={newSessionId}");
        args.AddRange(DirectoryArguments(options.AdditionalDirectories));
        string? mcpConfig = null;
        if (request.McpBridge is { } bridge)
        {
            // TKCode 的 MCP 桥接：配置写入 0600 临时文件（令牌在 env 中，不进命令行）；审批由 TKCode 桥接完成，
            // 因此让 Claude 对 tkcode 服务器的工具免询问，避免同一调用问两次（docs/design/mcp.md §10）
            mcpConfig = WriteMcpConfig(bridge);
            args.AddRange(["--mcp-config", mcpConfig, $"--allowedTools=mcp__{McpBridgeLaunch.ServerName}"]);
        }

        var cli = CliProcess.Start(path, args, request.Cwd, Environment(request.Cwd), _locator);
        var session = new ClaudeSession(cli.StandardOutput, cli.StandardInput, request.Host, sessionId ?? newSessionId, cli,
            () => cli.DescribeExit("Claude"))
        {
            TempFile = mcpConfig,
        };
        session.SetInitialOptions(options.Model.Id, effort, PermissionModeFor(options));
        try
        {
            await session.StartAsync(cancellationToken);
        }
        catch (Exception) when (sessionId is not null)
        {
            // 恢复失败（会话不存在等）：新建会话；新 id 由 system/init 上报
            await session.DisposeAsync();
            return await StartSessionAsync(request with { ResumeSessionId = null }, cancellationToken);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        if (newSessionId is not null)
        {
            request.Host.OnNativeSessionId(newSessionId);
        }
        return session;
    }

    /// <summary>项目的其他文件夹与本会话追加的文件夹（docs/design/projects.md §6.8）；同样用 --flag=value 形式，防止以 '-' 开头的路径被解析成参数。</summary>
    internal static IEnumerable<string> DirectoryArguments(IReadOnlyList<string> directories) =>
        directories.Distinct(StringComparer.Ordinal).Select(d => $"--add-dir={d}");

    /// <summary>子进程环境（§2.3）：去掉 CLAUDECODE，入口标记为 sdk-cli，开启会话状态帧。</summary>
    private static Dictionary<string, string?> Environment(string? cwd) => new()
    {
        ["CLAUDECODE"] = null,
        ["CLAUDE_CODE_ENTRYPOINT"] = "sdk-cli",
        ["CLAUDE_CODE_SDK_READS_SESSION_STATE"] = "1",
        ["PWD"] = cwd,
    };

    internal static string PermissionModeFor(TKCode.Agent.PermissionMode mode) =>
        mode == TKCode.Agent.PermissionMode.FullAccess ? "bypassPermissions" : "default";

    /// <summary>只读模式（TKCode 的 Plan）映射到 Claude 原生的 plan 权限模式（§5.3），优先于权限档位。</summary>
    internal static string PermissionModeFor(TurnOptions options) =>
        options.Mode.ReadOnly ? "plan" : PermissionModeFor(options.Permission);

    internal static string WriteMcpConfig(McpBridgeLaunch bridge)
    {
        var json = JsonBuilder.Object(w =>
        {
            w.WriteStartObject("mcpServers");
            w.WriteStartObject(McpBridgeLaunch.ServerName);
            bridge.WriteObject(w);
            w.WriteEndObject();
            w.WriteEndObject();
        });
        var path = Path.Combine(Path.GetTempPath(), $"tkcode-mcp-{Guid.NewGuid():N}.json");
        using (var stream = OperatingSystem.IsWindows()
                   ? new FileStream(path, FileMode.CreateNew, FileAccess.Write)
                   : new FileStream(path, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew,
                       Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                   }))
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            json.WriteTo(writer);
        }
        return path;
    }

    /// <summary>ThinkingLevel → --effort / effortLevel；模型不支持 effort 时返回 null（§2.2）。</summary>
    internal static string? EffortFor(ModelInfo model, ThinkingLevel level)
    {
        var levels = model.Compat?.ReasoningEfforts;
        if (levels is not { Count: > 0 } || level == ThinkingLevel.Off)
        {
            return null;
        }
        return ThinkingLevels.ChooseEffort(level, levels);
    }

    /// <summary><c>claude auth status</c>：退出码 0 表示已登录；只读取认证方式，不保存邮箱等个人信息（§12.1）。</summary>
    private async Task<string?> ReadAuthAsync(string path, CancellationToken ct)
    {
        var output = await CliProcess.RunToCompletionAsync(path, ["auth", "status"], TimeSpan.FromSeconds(10), ct, _locator);
        if (output is null)
        {
            return null;
        }
        try
        {
            var status = JsonBuilder.Parse(output.Trim());
            if (!status.Bool("loggedIn", true))
            {
                return null;
            }
            var method = status.Str("authMethod") switch
            {
                "claude.ai" => status.Str("subscriptionType") is { } plan ? $"Claude ({plan})" : "Claude subscription",
                "api_key" or "api_key_helper" => "API key",
                "oauth_token" => "OAuth token",
                "third_party" => status.Str("apiProvider") ?? "Third-party provider",
                var other => other,
            };
            return method ?? "Logged in";
        }
        catch (JsonException)
        {
            return "Logged in";
        }
    }

    /// <summary>短命进程只做 initialize，读取模型目录（不触发模型轮次）。</summary>
    private async Task<IReadOnlyList<ModelInfo>> DiscoverModelsAsync(string path, CancellationToken ct)
    {
        await using var cli = CliProcess.Start(path, BaseArguments, null, Environment(null), _locator);
        var session = new ClaudeSession(cli.StandardOutput, cli.StandardInput, NullHost.Instance, null, null, () => cli.DescribeExit("Claude"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeInitializeTimeout);
            await session.StartAsync(timeout.Token);
            return ParseModels(session.InitializeResponse);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    internal IReadOnlyList<ModelInfo> ParseModels(JsonElement initialize)
    {
        var models = new List<ModelInfo>();
        foreach (var m in initialize.Arr("models"))
        {
            if (m.Str("value") is not { } value)
            {
                continue;
            }
            var efforts = m.Arr("supportedEffortLevels").Select(e => e.GetString()).OfType<string>().ToArray();
            models.Add(RuntimeModels.Create(this, value, m.Str("displayName"), m.Str("description"),
                reasoning: efforts.Length > 0, contextWindow: 200_000, supportsImages: true) with
            {
                SupportsFastMode = m.Bool("supportsFastMode"),
                Compat = efforts.Length > 0 ? new ModelCompat { ReasoningEfforts = efforts } : null,
            });
        }
        return models.Count > 0 ? models : [RuntimeModels.Create(this, DefaultModelId, "Default")];
    }

    private static string Home => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();

    private sealed class NullHost : IRuntimeHost
    {
        public static readonly NullHost Instance = new();

        public ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalDecision.Deny());

        public void OnNativeSessionId(string nativeSessionId)
        {
        }
    }
}

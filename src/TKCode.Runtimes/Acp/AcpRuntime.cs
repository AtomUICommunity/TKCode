using TKCode.Ai.Models;
using TKCode.Runtimes.Discovery;
using TKCode.Runtimes.Process;

namespace TKCode.Runtimes.Acp;

/// <summary>
/// 一个原生 ACP Agent（规格 docs/protocols/acp-v1.md）。
/// OpenCode 可通过 models 命令提前发现模型；其他 Agent 在真实会话中从 config options 得知模型列表。
/// 不为探测创建空会话；会话中的配置变化会持续更新模型列表并缓存到磁盘。
/// </summary>
public sealed class AcpRuntime(AcpAgentDefinition agent, RuntimeCache? cache = null, ExecutableLocator? locator = null) : IAgentRuntime
{
    public const string DefaultModelId = "default";
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    private readonly ExecutableLocator _locator = locator ?? ExecutableLocator.Default;
    private readonly Lock _modelsGate = new();
    private IReadOnlyList<CachedModel> _models = [];
    private RuntimeDescriptor _descriptor = new(agent.RuntimeId, agent.DisplayName, RuntimeStatus.Unknown);

    public string Id => agent.RuntimeId;
    public string DisplayName => agent.DisplayName;
    public RuntimeDescriptor Descriptor => _descriptor;
    public event Action? DescriptorChanged;

    private static readonly RuntimeCapabilities Capabilities = new()
    {
        SupportsSteering = false,
        SupportsResume = true,
        SupportsModelSwitch = true,
        StreamsToolArguments = false,
    };

    public async Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken)
    {
        var path = _locator.Find(agent.Executable, agent.OverrideVariable, accept: AcpAgents.IsUsable);
        if (path is null)
        {
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.NotInstalled);
        }
        // 只用 --version 探测：不要用 “acp --help” 一类命令，有的 CLI 会把未知参数当作 prompt（§17.1）。
        var output = await CliProcess.RunToCompletionAsync(path, ["--version"], VersionTimeout, cancellationToken, _locator);
        var version = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (output is null)
        {
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.Error, path, null, null,
                $"'{Path.GetFileName(path)} --version' failed.") { Capabilities = Capabilities };
        }
        var models = cache?.Read(Id, path, version, RuntimeCacheJsonContext.Default.IReadOnlyListCachedModel);
        if (agent.Id == "opencode" &&
            await OpenCodeModelDiscovery.DiscoverAsync(path, _locator, cancellationToken) is { } discovered)
        {
            // 每次探测都重新列举：新增 provider / 更换凭据并不会改变 CLI 版本。
            // CLI 只提供 ID；保留真实 ACP 会话已经提供的名称、说明和推理档位。
            var previous = (models ?? []).DistinctBy(m => m.Id).ToDictionary(m => m.Id, StringComparer.Ordinal);
            models = discovered.Select(m => previous.GetValueOrDefault(m.Id) ?? m).ToArray();
            cache?.Write(Id, path, version, models, RuntimeCacheJsonContext.Default.IReadOnlyListCachedModel);
        }
        lock (_modelsGate)
        {
            _models = models ?? [];
            return _descriptor = new RuntimeDescriptor(Id, DisplayName, RuntimeStatus.Ready, path, version)
            {
                Capabilities = Capabilities,
                Models = [DefaultModel(), .. _models.Select(ToModel)],
            };
        }
    }

    public async Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
    {
        if (_descriptor is not { Status: RuntimeStatus.Ready, ExecutablePath: { } path })
        {
            throw new InvalidOperationException($"{DisplayName} is not available ({_descriptor.Error ?? _descriptor.Status.ToString()}).");
        }
        var process = CliProcess.Start(path, agent.Arguments, request.Cwd, agent.Environment, _locator);
        var session = new AcpSession(process.StandardOutput, process.StandardInput, agent, request.Host, process,
            () => process.DescribeExit(DisplayName))
        {
            McpBridge = request.McpBridge,
            AdditionalDirectories = request.Options.AdditionalDirectories,
        };
        // 先订阅，避免漏掉 session/new 响应前后的配置通知。
        session.ConfigChanged += UpdateModels;
        try
        {
            await session.StartAsync(request.Cwd, request.ResumeSessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            if (ex is TKCode.Rpc.RpcConnectionLostException or OperationCanceledException && process.HasExited)
            {
                throw new InvalidOperationException(process.DescribeExit(DisplayName), ex);
            }
            throw;
        }
        return session;
    }

    /// <summary>真实会话的配置是权威来源：更新选择器并缓存，包括后续名称、说明和推理档位变化。</summary>
    private void UpdateModels(AcpConfigState config)
    {
        var efforts = config.ReasoningEfforts;
        var models = config.Models
            .Select(v => new CachedModel(v.Value, v.Name, v.Description, efforts.Count > 0 ? efforts : null))
            .ToArray();
        lock (_modelsGate)
        {
            if (models.Length == 0 || _descriptor.ExecutablePath is not { } path || SameModels(_models, models))
            {
                return;
            }
            cache?.Write(Id, path, _descriptor.Version, models, RuntimeCacheJsonContext.Default.IReadOnlyListCachedModel);
            _models = models;
            _descriptor = _descriptor with { Models = [DefaultModel(), .. models.Select(ToModel)] };
        }
        DescriptorChanged?.Invoke();
    }

    private static bool SameModels(IReadOnlyList<CachedModel> current, IReadOnlyList<CachedModel> next) =>
        current.Count == next.Count && current.Zip(next).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.Description == pair.Second.Description &&
            (pair.First.ReasoningEfforts ?? []).SequenceEqual(pair.Second.ReasoningEfforts ?? []));

    private ModelInfo DefaultModel() =>
        RuntimeModels.Create(this, DefaultModelId, "Default", $"{DisplayName} chooses the model", reasoning: false,
            supportsImages: true);

    private ModelInfo ToModel(CachedModel m) => RuntimeModels.Create(this, m.Id, m.Name, m.Description,
        reasoning: m.ReasoningEfforts is { Count: > 0 }, supportsImages: true) with
    {
        Compat = m.ReasoningEfforts is { Count: > 0 } efforts ? new ModelCompat { ReasoningEfforts = efforts } : null,
    };
}

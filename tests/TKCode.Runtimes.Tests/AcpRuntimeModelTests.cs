using System.Text.Json;
using System.Text.Json.Nodes;
using TKCode.Agent;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Runtimes.Acp;
using TKCode.Runtimes.Discovery;
using Xunit;

namespace TKCode.Runtimes.Tests;

public sealed class AcpRuntimeModelTests
{
    private const string SessionId = "model-test-session";
    private const string ModelId = "provider/kept";
    private const string ModelName = "ACP model name";
    private const string InitialDescription = "Initial ACP description";
    private static readonly AcpAgentDefinition OpenCode = AcpAgents.Known.Single(agent => agent.Id == "opencode");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Probe_FirstRun_DiscoversAndCachesModelsWithoutStartingAcp()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeCli();
        cli.SetModels("openrouter/org/model:free\nopenai/gpt-5.1\n");
        var runtime = cli.CreateRuntime();

        var descriptor = await runtime.ProbeAsync(CancellationToken.None);

        AssertReady(descriptor, cli);
        Assert.Equal([AcpRuntime.DefaultModelId, "openrouter/org/model:free", "openai/gpt-5.1"],
            descriptor.Models.Select(model => model.Id));
        Assert.All(descriptor.Models.Skip(1), model =>
        {
            Assert.Equal(model.Id, model.Name);
            Assert.Null(model.Description);
            Assert.False(model.Reasoning);
            Assert.Null(model.Compat);
            Assert.True(model.SupportsImages);
            Assert.Equal(runtime.Id, model.Provider);
        });
        AssertCacheMatchesDescriptor(cli, runtime);
        Assert.Equal(["1\t--version", "1\tmodels"], cli.Invocations());
    }

    [Fact]
    public async Task Probe_EveryCall_ReplacesIdsInDiscoveryOrderAndPreservesMatchingCachedMetadata()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeCli();
        cli.SeedCache(OpenCode,
            new CachedModel(ModelId, ModelName, InitialDescription, ["low", "high"]),
            new CachedModel("provider/removed", "Removed model", "Stale", ["max"]));
        cli.SetModels("provider/new\n" + ModelId + "\n");
        var runtime = cli.CreateRuntime();

        var first = await runtime.ProbeAsync(CancellationToken.None);

        Assert.Equal([AcpRuntime.DefaultModelId, "provider/new", ModelId], first.Models.Select(model => model.Id));
        AssertModel(first.Models[^1], ModelName, InitialDescription, ["low", "high"]);
        AssertCacheMatchesDescriptor(cli, runtime);

        cli.SetModels(ModelId + "\nprovider/latest\n");
        var second = await runtime.ProbeAsync(CancellationToken.None);

        Assert.Equal([AcpRuntime.DefaultModelId, ModelId, "provider/latest"], second.Models.Select(model => model.Id));
        AssertModel(second.Models[1], ModelName, InitialDescription, ["low", "high"]);
        Assert.Equal("provider/latest", second.Models[^1].Name);
        Assert.Null(second.Models[^1].Description);
        Assert.Null(second.Models[^1].Compat);
        AssertCacheMatchesDescriptor(cli, runtime);
        Assert.Equal(["1\t--version", "1\tmodels", "1\t--version", "1\tmodels"], cli.Invocations());
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("\n \n", 0)]
    [InlineData("INFO no models available\n", 0)]
    [InlineData("provider/untrusted\n", 7)]
    public async Task Probe_EmptyInvalidOrFailedDiscovery_PreservesCachedModels(string output, int exitCode)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeCli();
        cli.SeedCache(OpenCode, new CachedModel(ModelId, ModelName, InitialDescription, ["medium", "high"]));
        cli.SetModels(output, exitCode);
        var runtime = cli.CreateRuntime();

        var descriptor = await runtime.ProbeAsync(CancellationToken.None);

        AssertReady(descriptor, cli);
        Assert.Equal([AcpRuntime.DefaultModelId, ModelId], descriptor.Models.Select(model => model.Id));
        AssertModel(descriptor.Models[1], ModelName, InitialDescription, ["medium", "high"]);
        AssertCacheMatchesDescriptor(cli, runtime);
        Assert.Equal(["1\t--version", "1\tmodels"], cli.Invocations());
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("INFO no models available\n", 0)]
    [InlineData("provider/untrusted\n", 7)]
    public async Task Probe_NoCacheAndNoUsableModels_IsReadyWithDefaultOnly(string output, int exitCode)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeCli();
        cli.SetModels(output, exitCode);

        var descriptor = await cli.CreateRuntime().ProbeAsync(CancellationToken.None);

        AssertReady(descriptor, cli);
        Assert.Equal(AcpRuntime.DefaultModelId, Assert.Single(descriptor.Models).Id);
        Assert.Equal(["1\t--version", "1\tmodels"], cli.Invocations());
    }

    [Fact]
    public async Task Probe_NewRuntimeInstance_UsesModelsCachedByPreviousProbeWhenDiscoveryFails()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeCli();
        cli.SetModels("provider/first\nprovider/second\n");
        await cli.CreateRuntime().ProbeAsync(CancellationToken.None);
        cli.SetModels("", exitCode: 7);
        var restarted = cli.CreateRuntime();

        var descriptor = await restarted.ProbeAsync(CancellationToken.None);

        AssertReady(descriptor, cli);
        Assert.Equal([AcpRuntime.DefaultModelId, "provider/first", "provider/second"],
            descriptor.Models.Select(model => model.Id));
        AssertCacheMatchesDescriptor(cli, restarted);
        Assert.Equal(["1\t--version", "1\tmodels", "1\t--version", "1\tmodels"], cli.Invocations());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_VersionChange_InvalidatesOldIdsAndMetadata(bool discoverSameId)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeCli();
        cli.SeedCache(OpenCode, new CachedModel(ModelId, ModelName, InitialDescription, ["high"]));
        var runtime = cli.CreateRuntime();
        var first = await runtime.ProbeAsync(CancellationToken.None);
        AssertModel(first.Models[1], ModelName, InitialDescription, ["high"]);

        cli.SetVersion("2.0.0");
        cli.SetModels(discoverSameId ? ModelId + "\n" : "");
        var second = await runtime.ProbeAsync(CancellationToken.None);

        AssertReady(second, cli);
        Assert.Equal("2.0.0", second.Version);
        if (discoverSameId)
        {
            Assert.Equal([AcpRuntime.DefaultModelId, ModelId], second.Models.Select(model => model.Id));
            AssertModel(second.Models[1], ModelId, null, []);
            AssertCacheMatchesDescriptor(cli, runtime);
        }
        else
        {
            Assert.Equal(AcpRuntime.DefaultModelId, Assert.Single(second.Models).Id);
            Assert.Null(cli.ReadCache(runtime));
        }
        Assert.Equal(["1\t--version", "1\tmodels", "1\t--version", "1\tmodels"], cli.Invocations());
    }

    public static IEnumerable<object[]> OtherAgents => AcpAgents.Known
        .Where(agent => agent.Id != "opencode")
        .Select(agent => new object[] { agent.Id });

    [Theory]
    [MemberData(nameof(OtherAgents))]
    public async Task Probe_OtherAcpAgents_OnlyRunVersionAndRetainTheirCache(string agentId)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeCli();
        var agent = AcpAgents.Known.Single(candidate => candidate.Id == agentId);
        cli.SeedCache(agent, new CachedModel(ModelId, ModelName, InitialDescription, ["low"]));
        cli.SetModels("provider/must-not-be-discovered\n");
        var runtime = cli.CreateRuntime(agent);

        await runtime.ProbeAsync(CancellationToken.None);
        var descriptor = await runtime.ProbeAsync(CancellationToken.None);

        AssertReady(descriptor, cli);
        Assert.Equal([AcpRuntime.DefaultModelId, ModelId], descriptor.Models.Select(model => model.Id));
        AssertModel(descriptor.Models[1], ModelName, InitialDescription, ["low"]);
        AssertCacheMatchesDescriptor(cli, runtime);
        Assert.Equal(["1\t--version", "1\t--version"], cli.Invocations());
    }

    [Fact]
    public async Task Session_InitialConfig_ReplacesDiscoveredModelsAndCachesAcpMetadata()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeCli(SessionScript(ConfigOptions(InitialDescription, ["low", "high"])));
        cli.SetModels("discovery/only\n");
        var runtime = cli.CreateRuntime();
        await runtime.ProbeAsync(CancellationToken.None);
        var changed = NextChange(runtime);

        await using var session = await StartSessionAsync(cli, runtime);
        var descriptor = await changed.WaitAsync(Timeout);

        Assert.Equal(SessionId, session.NativeSessionId);
        Assert.Equal([AcpRuntime.DefaultModelId, ModelId], descriptor.Models.Select(model => model.Id));
        AssertModel(descriptor.Models[1], ModelName, InitialDescription, ["low", "high"]);
        AssertCacheMatchesDescriptor(cli, runtime);
        Assert.Equal(["1\t--version", "1\tmodels", "1\tacp"], cli.Invocations());
        Assert.Equal(["initialize", "session/new"], cli.RequestMethods());
    }

    [Theory]
    [InlineData("Updated description", "low")]
    [InlineData(InitialDescription, "high,max")]
    [InlineData(InitialDescription, "")]
    public async Task Session_ConfigNotification_RefreshesDescriptionOrReasoningEvenWhenIdsAndNamesMatch(
        string description, string effortList)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        var efforts = effortList.Split(',', StringSplitOptions.RemoveEmptyEntries);
        using var cli = new FakeCli(SessionScript(
            ConfigOptions(InitialDescription, ["low"]), notificationOptions: ConfigOptions(description, efforts)));
        var runtime = cli.CreateRuntime();
        await runtime.ProbeAsync(CancellationToken.None);
        await using var session = await StartSessionAsync(cli, runtime);
        var before = runtime.Descriptor;
        var changed = NextChange(runtime);

        await RunTurnAsync(session, runtime, ThinkingLevel.Low);
        var after = await changed.WaitAsync(Timeout);

        Assert.Equal(before.Models.Select(model => (model.Id, model.Name)), after.Models.Select(model => (model.Id, model.Name)));
        AssertModel(after.Models[1], ModelName, description, efforts);
        Assert.Equal(efforts, session.Config.ReasoningEfforts);
        AssertCacheMatchesDescriptor(cli, runtime);
        Assert.Equal(["initialize", "session/new", "session/prompt"], cli.RequestMethods());
        Assert.Equal(["1\t--version", "1\tmodels", "1\tacp"], cli.Invocations());
    }

    [Fact]
    public async Task Session_SetConfigResponse_RefreshesMetadataAndCacheWithoutNotification()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeCli(SessionScript(
            ConfigOptions(InitialDescription, ["low", "high"]),
            setOptions: ConfigOptions("Description from set response", ["low", "high", "max"], "high")));
        var runtime = cli.CreateRuntime();
        await runtime.ProbeAsync(CancellationToken.None);
        await using var session = await StartSessionAsync(cli, runtime);
        var changed = NextChange(runtime);

        await RunTurnAsync(session, runtime, ThinkingLevel.High);
        var descriptor = await changed.WaitAsync(Timeout);

        AssertModel(descriptor.Models[1], ModelName, "Description from set response", ["low", "high", "max"]);
        AssertCacheMatchesDescriptor(cli, runtime);
        Assert.Equal(["initialize", "session/new", "session/set_config_option", "session/prompt"], cli.RequestMethods());
        using var request = JsonDocument.Parse(File.ReadAllLines(cli.RequestsFile)[2]);
        Assert.Equal("thought", request.RootElement.GetProperty("params").GetProperty("configId").GetString());
        Assert.Equal("high", request.RootElement.GetProperty("params").GetProperty("value").GetString());
        Assert.Equal(["1\t--version", "1\tmodels", "1\tacp"], cli.Invocations());
    }

    private static void AssertReady(RuntimeDescriptor descriptor, FakeCli cli)
    {
        Assert.Equal(RuntimeStatus.Ready, descriptor.Status);
        Assert.Null(descriptor.Error);
        Assert.Equal(cli.Executable, descriptor.ExecutablePath);
        Assert.Equal(AcpRuntime.DefaultModelId, descriptor.Models[0].Id);
        Assert.Equal("Default", descriptor.Models[0].Name);
        Assert.False(descriptor.Models[0].Reasoning);
    }

    private static void AssertModel(ModelInfo model, string name, string? description, IReadOnlyList<string> efforts)
    {
        Assert.Equal(name, model.Name);
        Assert.Equal(description, model.Description);
        Assert.Equal(efforts.Count > 0, model.Reasoning);
        Assert.Equal(efforts, model.Compat?.ReasoningEfforts ?? []);
        if (efforts.Count == 0)
        {
            Assert.Null(model.Compat);
        }
    }

    private static void AssertCacheMatchesDescriptor(FakeCli cli, AcpRuntime runtime)
    {
        var cached = cli.ReadCache(runtime);
        Assert.NotNull(cached);
        Assert.Equal(runtime.Descriptor.Models.Skip(1).Select(model => model.Id), cached.Select(model => model.Id));
        foreach (var (model, entry) in runtime.Descriptor.Models.Skip(1).Zip(cached))
        {
            Assert.Equal(model.Name, entry.Name);
            Assert.Equal(model.Description, entry.Description);
            Assert.Equal(model.Compat?.ReasoningEfforts ?? [], entry.ReasoningEfforts ?? []);
        }
    }

    private static Task<RuntimeDescriptor> NextChange(AcpRuntime runtime)
    {
        var changed = new TaskCompletionSource<RuntimeDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            runtime.DescriptorChanged -= OnChanged;
            changed.TrySetResult(runtime.Descriptor);
        }
        runtime.DescriptorChanged += OnChanged;
        return changed.Task;
    }

    private static async Task<AcpSession> StartSessionAsync(FakeCli cli, AcpRuntime runtime)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        var options = new TurnOptions(runtime.Descriptor.Models[0], ThinkingLevel.Low, false, PermissionMode.Ask);
        return Assert.IsType<AcpSession>(await runtime.StartSessionAsync(
            new RuntimeSessionRequest(cli.Directory, options, null, new DenyingHost()), cancellation.Token));
    }

    private static async Task RunTurnAsync(AcpSession session, AcpRuntime runtime, ThinkingLevel thinking)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        List<Message> messages = [];
        var model = runtime.Descriptor.Models[0];
        var writer = new TurnWriter(model, _ => ValueTask.CompletedTask, messages.Add);
        await session.RunTurnAsync(UserMessage.FromText("hi"),
            new TurnOptions(model, thinking, false, PermissionMode.Ask), writer, cancellation.Token).WaitAsync(Timeout);
        Assert.True(writer.IsCompleted);
        var reply = Assert.IsType<AssistantMessage>(Assert.Single(messages));
        Assert.Equal(StopReason.Stop, reply.StopReason);
        Assert.Equal("ok", Assert.IsType<TextContent>(Assert.Single(reply.Content)).Text);
    }

    private static string ConfigOptions(string description, IReadOnlyList<string> efforts, string currentEffort = "low")
    {
        var options = new JsonArray(new JsonObject
        {
            ["id"] = "model", ["name"] = "Model", ["category"] = "model", ["type"] = "select", ["currentValue"] = ModelId,
            ["options"] = new JsonArray(new JsonObject { ["value"] = ModelId, ["name"] = ModelName, ["description"] = description }),
        });
        if (efforts.Count > 0)
        {
            options.Add((JsonNode)new JsonObject
            {
                ["id"] = "thought", ["name"] = "Thinking", ["category"] = "thought_level", ["type"] = "select",
                ["currentValue"] = currentEffort,
                ["options"] = new JsonArray(efforts.Select(effort => (JsonNode)new JsonObject
                {
                    ["value"] = effort, ["name"] = effort,
                }).ToArray()),
            });
        }
        return options.ToJsonString();
    }

    private static string ConfigResponse(int id, string options, bool newSession = false)
    {
        var result = new JsonObject { ["configOptions"] = JsonNode.Parse(options) };
        if (newSession)
        {
            result["sessionId"] = SessionId;
        }
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
    }

    private static string ConfigNotification(string options) => new JsonObject
    {
        ["jsonrpc"] = "2.0", ["method"] = "session/update",
        ["params"] = new JsonObject
        {
            ["sessionId"] = SessionId,
            ["update"] = new JsonObject { ["sessionUpdate"] = "config_option_update", ["configOptions"] = JsonNode.Parse(options) },
        },
    }.ToJsonString();

    private static string PrintJson(string json) => "printf '%s\\n' '" + json.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string SessionScript(string initialOptions, string? notificationOptions = null, string? setOptions = null)
    {
        var promptId = setOptions is null ? 2 : 3;
        var promptResponse = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = promptId, ["result"] = new JsonObject { ["stopReason"] = "end_turn" },
        }.ToJsonString();
        return $$"""
            while IFS= read -r line; do
                printf '%s\n' "$line" >> "$0.requests"
                case "$line" in
                    *'"method":"initialize"'*)
                        {{PrintJson("""{"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{},"authMethods":[]}}""")}}
                        ;;
                    *'"method":"session/new"'*)
                        {{PrintJson(ConfigResponse(1, initialOptions, newSession: true))}}
                        ;;
                    *'"method":"session/set_config_option"'*)
                        {{(setOptions is null ? "exit 65" : PrintJson(ConfigResponse(2, setOptions)))}}
                        ;;
                    *'"method":"session/prompt"'*)
                        {{(notificationOptions is null ? ":" : PrintJson(ConfigNotification(notificationOptions)))}}
                        {{PrintJson("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"model-test-session","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"ok"}}}}""")}}
                        {{PrintJson(promptResponse)}}
                        ;;
                    *) exit 66 ;;
                esac
            done
            """;
    }

    private sealed class DenyingHost : IRuntimeHost
    {
        public ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalDecision.Deny());
        public void OnNativeSessionId(string nativeSessionId) { }
    }

    private sealed class FakeCli : IDisposable
    {
        private readonly DirectoryInfo _directory = System.IO.Directory.CreateTempSubdirectory("tkcode-acp models-");
        private string _version = "1.0.0";

        internal FakeCli(string acpBody = "exit 64")
        {
            Executable = Path.Combine(_directory.FullName, "fake agent");
            Cache = new RuntimeCache(Path.Combine(_directory.FullName, "cache"));
            File.WriteAllText(Executable, """
                #!/bin/sh
                printf '%s' "$#" >> "$0.argv"
                for arg do printf '\t%s' "$arg" >> "$0.argv"; done
                printf '\n' >> "$0.argv"
                if [ "$#" -ne 1 ]; then exit 64; fi
                case "$1" in
                    --version)
                        IFS= read -r version < "$0.version"
                        printf '%s\n' "$version"
                        ;;
                    models)
                        while IFS= read -r line || [ -n "$line" ]; do printf '%s\n' "$line"; done < "$0.models"
                        IFS= read -r code < "$0.exit"
                        exit "$code"
                        ;;
                    acp)
                """ + "\n" + acpBody + "\n;;\n*) exit 64 ;;\nesac\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            SetVersion(_version);
            SetModels("");
        }

        internal string Executable { get; }
        internal string Directory => _directory.FullName;
        internal string RequestsFile => Executable + ".requests";
        internal RuntimeCache Cache { get; }

        internal AcpRuntime CreateRuntime(AcpAgentDefinition? agent = null)
        {
            agent ??= OpenCode;
            var locator = new ExecutableLocator(name => name == agent.OverrideVariable ? Executable : null, () => []);
            return new AcpRuntime(agent, Cache, locator);
        }

        internal void SetVersion(string version)
        {
            _version = version;
            File.WriteAllText(Executable + ".version", version + "\n");
        }

        internal void SetModels(string output, int exitCode = 0)
        {
            File.WriteAllText(Executable + ".models", output);
            File.WriteAllText(Executable + ".exit", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }

        internal void SeedCache(AcpAgentDefinition agent, params CachedModel[] models) =>
            Cache.Write(agent.RuntimeId, Executable, _version, (IReadOnlyList<CachedModel>)models,
                RuntimeCacheJsonContext.Default.IReadOnlyListCachedModel);

        internal IReadOnlyList<CachedModel>? ReadCache(AcpRuntime runtime) =>
            Cache.Read(runtime.Id, Executable, runtime.Descriptor.Version, RuntimeCacheJsonContext.Default.IReadOnlyListCachedModel);

        internal string[] Invocations() => File.ReadAllLines(Executable + ".argv");

        internal string[] RequestMethods() => File.ReadAllLines(RequestsFile).Select(line =>
        {
            using var request = JsonDocument.Parse(line);
            return request.RootElement.GetProperty("method").GetString()!;
        }).ToArray();

        public void Dispose() => _directory.Delete(recursive: true);
    }
}

using TKCode.Runtimes.Discovery;
using Xunit;

namespace TKCode.Runtimes.Tests;

public sealed class OpenCodeModelDiscoveryTests
{
    private static readonly ExecutableLocator Locator = new(_ => null, () => []);

    [Fact]
    public void Parse_PreservesCompleteIds_AndUsesIdsAsNames()
    {
        var models = OpenCodeModelDiscovery.Parse(
            "openai/gpt-5.1\nopenrouter/meta-llama/llama-3.3-70b-instruct:free\n" +
            "amazon-bedrock/us.anthropic.claude-sonnet-4-5-v1:0\ncustom/@org/model+variant\n");

        Assert.NotNull(models);
        Assert.Equal(
            ["openai/gpt-5.1", "openrouter/meta-llama/llama-3.3-70b-instruct:free",
                "amazon-bedrock/us.anthropic.claude-sonnet-4-5-v1:0", "custom/@org/model+variant"],
            models.Select(model => model.Id));
        Assert.All(models, model =>
        {
            Assert.Equal(model.Id, model.Name);
            Assert.Null(model.Description);
            Assert.Null(model.ReasoningEfforts);
        });
    }

    [Fact]
    public void Parse_TrimsCrLfAndBlankLines_AndDeduplicatesOrdinally()
    {
        var models = OpenCodeModelDiscovery.Parse(
            "\r\n  openai/gpt-5.1  \r\n\r\nopenai/gpt-5.1\r\nOpenAI/gpt-5.1\r\nopenai/GPT-5.1");

        Assert.NotNull(models);
        Assert.Equal(["openai/gpt-5.1", "OpenAI/gpt-5.1", "openai/GPT-5.1"], models.Select(model => model.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\r\n\t")]
    [InlineData("INFO loading providers\nNo models available\n")]
    [InlineData("/model\nprovider/\nprovider//model\nprovider/model/\n")]
    [InlineData("provider/model name\nprovider/model\tname\nprovider/model\rname")]
    [InlineData("provider/mo\0del\nprovider/mo\u007fdel\n\u001b[32mprovider/model\u001b[0m")]
    [InlineData("https://example.com/models\n../models\nprovider/../model\nprovider/./model")]
    [InlineData("[INFO] provider/model\n{\"model\":\"provider/model\"}")]
    public void Parse_EmptyOrInvalidOutput_ReturnsNull(string? output)
    {
        Assert.Null(OpenCodeModelDiscovery.Parse(output));
    }

    [Fact]
    public void Parse_PreservesAliasesAndCustomProviderIdsWithoutAnAsciiWhitelist()
    {
        var models = OpenCodeModelDiscovery.Parse(
            "openrouter/~anthropic/claude-sonnet-latest\nkilo/~openai/gpt-sol-latest\n" +
            "本地/模型-v1\n_proxy/gpt-5\nollama/model=tag\ncustom/model?revision=1\n");

        Assert.NotNull(models);
        Assert.Equal(["openrouter/~anthropic/claude-sonnet-latest", "kilo/~openai/gpt-sol-latest",
            "本地/模型-v1", "_proxy/gpt-5", "ollama/model=tag", "custom/model?revision=1"],
            models.Select(model => model.Id));
    }

    [Fact]
    public void Parse_IgnoresNoiseWithoutDiscardingValidModels()
    {
        var models = OpenCodeModelDiscovery.Parse(
            "INFO loading /tmp/config\n\u001b[32mcolored/model\u001b[0m\nopenai/gpt-5.1\n" +
            "provider/invalid model\nopenrouter/org/model\nWarning: update available\n");

        Assert.NotNull(models);
        Assert.Equal(["openai/gpt-5.1", "openrouter/org/model"], models.Select(model => model.Id));
    }

    [Fact]
    public async Task Discover_MissingExecutable_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"tkcode-missing-opencode-{Guid.NewGuid():N}");

        Assert.Null(await OpenCodeModelDiscovery.DiscoverAsync(missing, Locator, CancellationToken.None));
    }

    [Fact]
    public async Task Discover_AlreadyCancelled_PropagatesCancellationBeforeStarting()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var missing = Path.Combine(Path.GetTempPath(), $"tkcode-missing-opencode-{Guid.NewGuid():N}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenCodeModelDiscovery.DiscoverAsync(missing, Locator, cancellation.Token));
    }

    [Fact]
    public async Task Discover_RunsOnlyModels_WithNoFlagsOrAcpSession()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeOpenCode("printf 'openrouter/org/model:free\\nopenai/gpt-5.1\\n'");

        var models = await OpenCodeModelDiscovery.DiscoverAsync(cli.Executable, Locator, CancellationToken.None);

        Assert.NotNull(models);
        Assert.Equal(["openrouter/org/model:free", "openai/gpt-5.1"], models.Select(model => model.Id));
        Assert.Equal("models\n", await File.ReadAllTextAsync(cli.ArgumentsFile));
    }

    [Theory]
    [InlineData("exit 0")]
    [InlineData("printf '\\n \\n'")]
    [InlineData("printf 'INFO no models available\\n'")]
    [InlineData("printf 'openai/gpt-5.1\\n'; exit 7")]
    [InlineData("printf 'openai/gpt-5.1\\n' >&2")]
    public async Task Discover_EmptyInvalidOrFailedCommand_ReturnsNull(string body)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeOpenCode(body);

        Assert.Null(await OpenCodeModelDiscovery.DiscoverAsync(cli.Executable, Locator, CancellationToken.None));
    }

    [Fact]
    public async Task Discover_NonExecutableFile_ReturnsNull()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses Unix executable permissions.");
        using var cli = new FakeOpenCode("exit 0");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(cli.Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Null(await OpenCodeModelDiscovery.DiscoverAsync(cli.Executable, Locator, CancellationToken.None));
    }

    [Fact]
    public async Task Discover_CancellationDuringCommand_Propagates()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeOpenCode(": > \"$0.started\"\nexec /bin/sleep 30");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var discovery = OpenCodeModelDiscovery.DiscoverAsync(cli.Executable, Locator, cancellation.Token);
        await cli.WaitUntilStartedAsync();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Discover_CommandTimeout_ReturnsNull()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a local POSIX shell fixture.");
        using var cli = new FakeOpenCode("exec /bin/sleep 30");

        Assert.Null(await OpenCodeModelDiscovery.DiscoverAsync(cli.Executable, Locator, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20)));
    }

    private sealed class FakeOpenCode : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tkcode-opencode models-");

        internal FakeOpenCode(string body)
        {
            Executable = Path.Combine(_directory.FullName, "fake opencode");
            File.WriteAllText(Executable,
                "#!/bin/sh\n" +
                "printf '%s\\n' \"$@\" >> \"$0.args\"\n" +
                "if [ \"$#\" -ne 1 ] || [ \"$1\" != models ]; then exit 64; fi\n" +
                body + "\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        internal string Executable { get; }
        internal string ArgumentsFile => Executable + ".args";

        internal async Task WaitUntilStartedAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(Executable + ".started"))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}

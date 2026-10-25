using System.Text.Json;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Runtimes.Acp;
using TKCode.Runtimes.Claude;
using TKCode.Runtimes.Json;
using Xunit;

namespace TKCode.Runtimes.Tests;

/// <summary>多文件夹项目在外部运行时中（docs/design/projects.md §6.8）：Claude --add-dir、ACP additionalDirectories、目录变化后重启会话。</summary>
public sealed class ProjectDirectoriesTests
{
    private static ModelInfo Model => ExternalAgentTests.Model;

    [Fact]
    public async Task ExternalAgent_PassesDirectories_AndRestartsWhenTheyChange()
    {
        IReadOnlyList<string> directories = ["/work/shared"];
        var runtime = new ScriptedRuntime((_, _, _, _) => Task.CompletedTask);
        var agent = new ExternalAgent(runtime, Model, "/work/app") { AdditionalDirectories = () => directories };

        await agent.PromptAsync(UserMessage.FromText("a"));
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(["/work/shared"], runtime.LastRequest!.Options.AdditionalDirectories);
        Assert.Equal(["/work/shared"], runtime.LastOptions!.AdditionalDirectories);

        // 没有变化：沿用会话
        await agent.PromptAsync(UserMessage.FromText("b"));
        Assert.Equal(1, runtime.Starts);

        // 变化了：下一轮之前重启会话，新的目录在这一轮生效
        directories = ["/work/shared", "/work/docs"];
        await agent.PromptAsync(UserMessage.FromText("c"));
        Assert.Equal(2, runtime.Starts);
        Assert.Equal(["/work/shared", "/work/docs"], runtime.LastRequest!.Options.AdditionalDirectories);
    }

    [Fact]
    public async Task ExternalAgent_DoesNotRestart_WhenTheContextCannotBeResumed()
    {
        IReadOnlyList<string> directories = [];
        var runtime = new ScriptedRuntime((_, _, host, _) =>
        {
            host.OnNativeSessionId("native-1");
            return Task.CompletedTask;
        });
        var agent = new ExternalAgent(runtime, Model, "/work/app") { AdditionalDirectories = () => directories };
        await agent.PromptAsync(UserMessage.FromText("a"));

        // 运行时不能按原生会话 id 恢复（SupportsResume = false）：重启会丢掉上下文，等下次打开会话时再生效
        directories = ["/work/shared"];
        await agent.PromptAsync(UserMessage.FromText("b"));
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(["/work/shared"], runtime.LastOptions!.AdditionalDirectories);
    }

    [Fact]
    public void Claude_AddsOneFlagPerDirectory()
    {
        Assert.Equal(["--add-dir=/work/shared", "--add-dir=-odd"], ClaudeRuntime.DirectoryArguments(["/work/shared", "-odd", "/work/shared"]));
        Assert.Empty(ClaudeRuntime.DirectoryArguments([]));
    }

    [Fact]
    public async Task Acp_SendsAdditionalDirectories_OnlyWhenTheAgentSupportsThem()
    {
        var agent = new AcpAgentDefinition("example", "Example", "example", ["acp"]);
        await using var session = new AcpSession(new MemoryStream(), new MemoryStream(), agent, new NullHost())
        {
            AdditionalDirectories = ["/work/shared", "/work", "/work/shared"],
        };
        Assert.False(session.SessionParams(null, "/work").TryGetProperty("additionalDirectories", out _));

        session.AgentCapabilities = JsonBuilder.Parse("""{"sessionCapabilities":{"additionalDirectories":{}}}""");
        var sent = session.SessionParams("s1", "/work").GetProperty("additionalDirectories").EnumerateArray().Select(e => e.GetString());
        // 完整列表，去掉重复项与等于 cwd 的项
        Assert.Equal(["/work/shared"], sent);
    }

    private sealed class NullHost : IRuntimeHost
    {
        public ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalDecision.Deny("test"));

        public void OnNativeSessionId(string nativeSessionId)
        {
        }
    }
}

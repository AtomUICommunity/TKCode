using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Agent.Coding.Sessions;
using TKCode.AgentHost.Catalog;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Ai.Messages;
using TKCode.Config;
using TKCode.Runtimes;
using Xunit;

namespace TKCode.AgentHost.Tests;

/// <summary>多文件夹项目在 Host 中（docs/design/projects.md §6）：会话的工作区来自项目的文件夹与本会话追加的文件夹，并实时同步。</summary>
public sealed class ProjectSessionTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable(ConfigPaths.HomeVariable);
    private readonly string _home = Directory.CreateTempSubdirectory("tkcode-projects-").FullName;

    public ProjectSessionTests() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _home);

    public void Dispose() => Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _savedHome);

    private static string NewDir() => Directory.CreateTempSubdirectory("tkcode-project-dir-").FullName;

    [Fact]
    public async Task NewSession_GetsProjectFolders_AndFollowsProjectEdits()
    {
        var primary = NewDir();
        var shared = NewDir();
        var missing = Path.Combine(NewDir(), "gone");
        var project = Project.Create("demo", [primary, shared, missing]);
        ProjectsFile.Save([project]);
        await using var sessions = CreateManager();

        var session = await sessions.CreateAsync(primary, FakeWireApi.Models[1], null, project.Id, null);
        Assert.Equal(project.Id, session.Store.ProjectId);
        // 不存在的文件夹跳过
        Assert.Equal([shared], session.Workspace.Additional);
        Assert.True(session.Workspace.Contains(Path.Combine(shared, "lib.cs")));

        session.Prompt("hello");
        await session.Agent.WaitForIdleAsync();
        // 第一轮只有记忆索引（docs/design/memory.md §4.2），没有系统提示补丁
        Assert.DoesNotContain(session.Agent.Messages, IsPromptPatch);

        // 编辑项目：去掉 shared、加入 docs；下一轮开始前生效
        var docs = NewDir();
        ProjectsFile.Save([project.With("demo", [primary, docs])]);
        File.SetLastWriteTimeUtc(ConfigPaths.ProjectsFile, DateTime.UtcNow.AddSeconds(5));
        session.Prompt("again");
        await session.Agent.WaitForIdleAsync();
        Assert.Equal([docs], session.Workspace.Additional);
        Assert.False(session.Workspace.Contains(Path.Combine(shared, "lib.cs")));
        var patch = Assert.Single(session.Agent.Messages.OfType<SystemMessage>(), IsPromptPatch);
        Assert.Contains(docs.Replace('\\', '/'), patch.Text);

        // 会话头记下了项目：重新打开后同样同步
        var reopened = SessionStore.Read(session.Store.FilePath);
        Assert.Equal(project.Id, reopened.ProjectId);
    }

    [Fact]
    public async Task LegacySession_UsesTheProjectWhosePrimaryFolderIsItsCwd()
    {
        var primary = NewDir();
        var shared = NewDir();
        ProjectsFile.Save([Project.Create("demo", [primary, shared])]);
        await using var sessions = CreateManager();

        // 旧会话（没有 ProjectId）按 cwd 等于主文件夹归属
        var (all, sessionOnly) = sessions.DirectoriesFor(primary, null, []);
        Assert.Equal([shared], all);
        Assert.Empty(sessionOnly);
        // cwd 只是项目的其他文件夹时不归属
        Assert.Empty(sessions.DirectoriesFor(shared, null, []).All);
    }

    [Fact]
    public async Task SessionDirectories_ArePersisted_AndLabelledAsSessionOnly()
    {
        var cwd = NewDir();
        var extra = NewDir();
        await using var sessions = CreateManager();
        var session = await sessions.CreateAsync(cwd, FakeWireApi.Models[1], null, null, [extra, cwd, extra]);
        Assert.Equal([extra], session.Store.SessionDirectories);
        Assert.True(session.Workspace.IsSessionOnly(extra));

        var later = NewDir();
        await sessions.AddSessionDirectoryAsync(session.Id, later);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.AddSessionDirectoryAsync(session.Id, Path.Combine(later, "nope")));
        await sessions.RemoveSessionDirectoryAsync(session.Id, extra);
        Assert.Equal([later], session.Store.SessionDirectories);

        session.Prompt("hello");
        await session.Agent.WaitForIdleAsync();
        Assert.Equal([later], session.Workspace.Additional);
        Assert.Contains("(added for this session only)", ((TKCode.Agent.Agent)session.Agent).SystemPrompt);

        var snapshot = SessionStore.Read(session.Store.FilePath);
        Assert.Equal([later], snapshot.SessionDirectories);
    }

    private static bool IsPromptPatch(Message message) =>
        message is SystemMessage { Text: var text } && text.StartsWith("Updated system prompt section", StringComparison.Ordinal);

    private static SessionManager CreateManager()
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        return new SessionManager(new WireApiRegistry([new FakeWireApi(TimeSpan.Zero)]), catalog, new RuntimeRegistry([]),
            new TKCode.AgentHost.Peers.PeerDirectory(NullLogger<TKCode.AgentHost.Peers.PeerDirectory>.Instance),
            new TKCode.Mcp.McpManager(new TKCode.Mcp.McpHost(), null, () => []), NullLogger<SessionManager>.Instance);
    }
}

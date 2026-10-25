using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Sessions;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Ai.Messages;
using TKCode.Config;
using Xunit;

namespace TKCode.Agent.Tests;

/// <summary>多文件夹项目（docs/design/projects.md）：项目表、会话的附加目录、cwd 节与会话文件。</summary>
public sealed class ProjectTests
{
    private static string NewDir(string prefix = "tkcode-project-") => Directory.CreateTempSubdirectory(prefix).FullName;

    [Fact]
    public void ProjectsFile_RoundTrips_AndSkipsInvalidEntries()
    {
        var home = NewDir();
        var path = Path.Combine(home, "projects.json");
        var a = NewDir();
        var b = NewDir();
        var project = Project.Create(null, [a, b + Path.DirectorySeparatorChar, a]);
        Assert.Equal([a, b], project.Folders);
        Assert.Equal(Project.FolderName(a), project.Name);
        Assert.Equal(a, project.Primary);
        Assert.Equal([b], project.Additional);

        ProjectsFile.Save([project, project with { Id = "pinned", Name = "Pinned", Pinned = true }], path);
        var loaded = ProjectsFile.Load(path);
        Assert.True(loaded.Exists);
        Assert.Null(loaded.Error);
        Assert.Equal(["Pinned", project.Name], loaded.Projects.OrderByDescending(p => p.Pinned).Select(p => p.Name));
        Assert.Equal(project.Folders, loaded.Projects[0].Folders);

        // 缺少 id 或文件夹的条目、重复的 id 跳过；名称为空时取主文件夹名
        File.WriteAllText(path, $$"""
            {
              // 允许注释与尾逗号
              "projects": [
                { "id": "", "folders": ["{{Escape(a)}}"] },
                { "id": "x", "folders": [] },
                { "id": "y", "name": "", "folders": ["{{Escape(b)}}"] },
                { "id": "y", "name": "dup", "folders": ["{{Escape(a)}}"] },
              ]
            }
            """);
        var parsed = Assert.Single(ProjectsFile.Load(path).Projects);
        Assert.Equal(("y", Project.FolderName(b), Project.LocalDevice), (parsed.Id, parsed.Name, parsed.Device));
    }

    [Fact]
    public void ProjectsFile_ReportsCorruptFile_AndMovesItAside()
    {
        var path = Path.Combine(NewDir(), "projects.json");
        Assert.False(ProjectsFile.Load(path).Exists);
        File.WriteAllText(path, "{ not json");

        var result = ProjectsFile.Load(path);
        Assert.True(result.Exists);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Projects);

        var aside = ProjectsFile.MoveAside(path);
        Assert.NotNull(aside);
        Assert.False(File.Exists(path));
        Assert.Equal("{ not json", File.ReadAllText(aside));
    }

    [Fact]
    public void Catalog_ReloadsOnChange_AndResolvesSessionsById_ThenByPrimaryFolder()
    {
        var path = Path.Combine(NewDir(), "projects.json");
        var catalog = new ProjectCatalog(() => path);
        Assert.Empty(catalog.Projects);

        var primary = NewDir();
        var other = NewDir();
        var first = Project.Create("first", [primary, other]);
        ProjectsFile.Save([first], path);
        Assert.Same(catalog.Projects, catalog.Projects);
        Assert.Equal(first.Id, catalog.Find(first.Id)?.Id);

        // 会话头里的 id 优先；没有 id（旧会话）时按 cwd 等于主文件夹归属；cwd 只是其他文件夹时不归属
        Assert.Equal(first.Id, catalog.Resolve(first.Id, NewDir())?.Id);
        Assert.Equal(first.Id, catalog.Resolve(null, primary)?.Id);
        Assert.Null(catalog.Resolve(null, other));

        // 项目被移除后，按 cwd 回退到另一个主文件夹相同的项目
        var second = Project.Create("second", [primary]);
        ProjectsFile.Save([second], path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(second.Id, catalog.Resolve(first.Id, primary)?.Id);

        // 文件损坏时沿用上一次成功读取的结果
        File.WriteAllText(path, "{");
        Assert.Equal(second.Id, Assert.Single(catalog.Projects).Id);
    }

    [Fact]
    public void WorkspaceRoots_Sync_ReplacesAdditionalDirectories()
    {
        var cwd = NewDir();
        var a = NewDir();
        var b = NewDir();
        var workspace = new WorkspaceRoots(cwd);
        Assert.False(workspace.Contains(Path.Combine(a, "x.cs")));

        Assert.True(workspace.Sync([a, b, a, cwd], sessionOnly: [b]));
        Assert.Equal([a, b], workspace.Additional);
        Assert.True(workspace.Contains(Path.Combine(a, "x.cs")));
        Assert.True(workspace.IsSessionOnly(b));
        Assert.False(workspace.IsSessionOnly(a));
        Assert.False(workspace.Sync([a, b], [b]));

        Assert.True(workspace.Sync([b]));
        Assert.False(workspace.Contains(Path.Combine(a, "x.cs")));
        Assert.True(workspace.Contains(cwd));
        Assert.False(workspace.IsSessionOnly(b));
    }

    [Fact]
    public void CwdSection_IsBarePath_ForOneFolder_AndListsAdditionalDirectories()
    {
        var cwd = NewDir();
        var workspace = new WorkspaceRoots(cwd);
        Assert.Equal(Slashes(cwd), Cwd(workspace));

        var shared = NewDir();
        var notes = NewDir();
        workspace.Sync([shared, notes], [notes]);
        Assert.Equal(
            $"Primary working directory: {Slashes(cwd)}\n" +
            "Additional working directories (read, search and edit them just like the primary one; refer to their files by absolute path, " +
            "because relative paths and shell commands resolve against the primary working directory):\n" +
            $"- {Slashes(shared)}\n" +
            $"- {Slashes(notes)} (added for this session only)",
            Cwd(workspace));

        var rules = SystemPromptBuilder.Build(workspace, [], FakeWireApi.Models[0]).Single(s => s.Name == "rules").Text;
        Assert.Contains("a relative path is relative to the primary working directory, an absolute path is used as is", rules);
    }

    [Fact]
    public async Task SessionFile_RecordsProjectAndSessionDirectories()
    {
        var cwd = NewDir();
        var extra = NewDir();
        var store = SessionStore.Create(cwd, Path.Combine(cwd, ".sessions"), projectId: "p1");
        await store.AppendDirectoriesAsync([extra]);
        Assert.Equal([extra], store.SessionDirectories);
        await store.AppendMessageAsync(UserMessage.FromText("hi"));
        await store.AppendDirectoriesAsync([]);
        await store.AppendDirectoriesAsync([extra, cwd]);

        var snapshot = SessionStore.Read(store.FilePath);
        Assert.Equal("p1", snapshot.ProjectId);
        Assert.Equal([extra, cwd], snapshot.SessionDirectories);
        var summary = SessionStore.ReadSummary(store.FilePath);
        Assert.Equal("p1", summary.ProjectId);
        var (reopened, _) = SessionStore.OpenSnapshot(store.FilePath);
        Assert.Equal("p1", reopened.ProjectId);
        Assert.Equal([extra, cwd], reopened.SessionDirectories);
    }

    [Fact]
    public async Task Session_SyncsDirectoriesBeforeEachBatch_AndAppendsCwdPatch()
    {
        var cwd = NewDir();
        var shared = NewDir();
        IReadOnlyList<string> current = [];
        var apis = new WireApiRegistry([new FakeWireApi(TimeSpan.Zero)]);
        var store = SessionStore.Create(cwd, Path.Combine(cwd, ".sessions"));
        await using var session = AgentSession.CreateBuiltin(apis, FakeWireApi.Models[1], store, options: new BuiltinOptions
        {
            Workspace = new WorkspaceRoots(cwd),
            AdditionalDirectories = _ => (current, []),
        });
        var agent = (Agent)session.Agent;
        session.Prompt("hello");
        await agent.WaitForIdleAsync();
        Assert.DoesNotContain(agent.Messages, m => m is SystemMessage);
        Assert.False(session.Workspace.Contains(Path.Combine(shared, "a.txt")));

        // 项目加了一个文件夹：下一批输入前同步，cwd 节以补丁更新，开头的系统提示不变
        var initial = agent.SystemPrompt;
        current = [shared];
        session.Prompt("again");
        await agent.WaitForIdleAsync();
        Assert.True(session.Workspace.Contains(Path.Combine(shared, "a.txt")));
        var patch = Assert.Single(agent.Messages.OfType<SystemMessage>());
        Assert.StartsWith("Updated system prompt section \"cwd\"", patch.Text);
        Assert.Contains("- " + Slashes(shared), patch.Text);
        Assert.Equal(initial, agent.SystemPrompt);
    }

    private static string Cwd(WorkspaceRoots workspace) =>
        SystemPromptBuilder.Build(workspace, [], FakeWireApi.Models[0]).Single(s => s.Name == "cwd").Text;

    private static string Slashes(string path) => path.Replace('\\', '/');

    private static string Escape(string path) => path.Replace("\\", "\\\\");
}

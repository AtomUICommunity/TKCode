using TKCode.Desktop.Localization;
using TKCode.Desktop.ViewModels;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>文件改动：diff 行号、按文件合并、撤销的分支（不经过界面）。</summary>
public sealed class FileChangeTests
{
    [Fact]
    public void DiffLines_NumbersFollowHunkHeaders()
    {
        const string diff = """
            --- a/x.txt
            +++ b/x.txt
            @@ -3,3 +3,4 @@
             keep
            -old
            +new
            +more
             tail

            """;

        var lines = ParsedToolDetails.DiffLinesOf(diff);

        Assert.Equal(DiffLineKind.Hunk, lines[0].Kind);
        Assert.Equal((3, 3), (lines[1].OldNumber, lines[1].NewNumber));
        Assert.Equal((4, null), (lines[2].OldNumber, lines[2].NewNumber));
        Assert.Equal((null, 4), (lines[3].OldNumber, lines[3].NewNumber));
        Assert.Equal((null, 5), (lines[4].OldNumber, lines[4].NewNumber));
        Assert.Equal((5, 6), (lines[5].OldNumber, lines[5].NewNumber));
        Assert.Equal("+", lines[3].Sign);
    }

    [Fact]
    public void Group_KeepsFirstSnapshotAndLastFingerprint_PerFile()
    {
        var edits = new[]
        {
            Edit("/p/a.cs", before: "one", after: "h1", added: 1, removed: 1),
            Edit("/p/b.cs", before: "b0", after: "hb", added: 2, removed: 0),
            Edit("/p/a.cs", before: "two", after: "h2", added: 3, removed: 0),
        };

        var files = FileChangeSet.Group(edits, _ => Task.FromResult(new RevertFileResult(null)));

        Assert.Equal(["/p/a.cs", "/p/b.cs"], files.Select(f => f.Path).ToArray());
        var a = files[0];
        Assert.Equal("one", a.Before);
        Assert.Equal("h2", a.AfterSha256);
        Assert.Equal((4, 1), (a.Added, a.Removed));
        Assert.True(a.CanRevert);
    }

    [Fact]
    public async Task Revert_NewFile_SendsDeleteRequest()
    {
        RevertFileRequest? sent = null;
        var files = FileChangeSet.Group([Edit("/p/new.txt", created: true, after: "h")], request =>
        {
            sent = request;
            return Task.FromResult(new RevertFileResult(null));
        });

        await files[0].RevertCoreAsync();

        Assert.Equal(new RevertFileRequest("/p/new.txt", "h", null), sent);
        Assert.True(files[0].IsReverted);
    }

    [Fact]
    public async Task Revert_WithoutSnapshot_IsNotAvailable()
    {
        var called = false;
        var files = FileChangeSet.Group([Edit("/p/big.bin", before: null, after: "h")], _ =>
        {
            called = true;
            return Task.FromResult(new RevertFileResult(null));
        });

        Assert.False(files[0].CanRevert);
        await files[0].RevertCoreAsync();
        Assert.False(called);
    }

    [Fact]
    public void TurnCard_HidesFilesPastThePreviewUntilExpanded()
    {
        var many = Card(TurnChangesItemViewModel.PreviewCount + 4);
        Assert.True(many.HasOverflow);
        Assert.False(many.ShowAll);
        Assert.Equal(TurnChangesItemViewModel.PreviewCount, many.VisibleFiles.Count());
        Assert.Equal("/p/f0.cs", many.VisibleFiles.First().Path);

        many.ToggleRestCommand.Execute(null);
        Assert.True(many.ShowAll);
        Assert.Equal(TurnChangesItemViewModel.PreviewCount + 4, many.VisibleFiles.Count());

        many.ToggleRestCommand.Execute(null);
        Assert.Equal(TurnChangesItemViewModel.PreviewCount, many.VisibleFiles.Count());

        var few = Card(TurnChangesItemViewModel.PreviewCount);
        Assert.False(few.HasOverflow);
        Assert.Equal(TurnChangesItemViewModel.PreviewCount, few.VisibleFiles.Count());
    }

    private static TurnChangesItemViewModel Card(int count)
    {
        var files = Enumerable.Range(0, count)
            .Select(i => FileChangeSet.Group([Edit($"/p/f{i}.cs")], _ => Task.FromResult(new RevertFileResult(null)))[0])
            .ToArray();
        return new TurnChangesItemViewModel(files, _ => { });
    }

    [Fact]
    public void TurnCard_OpenAndViewChanges_GoThroughTheCallbacks()
    {
        var opened = new List<string>();
        var views = 0;
        var files = new[] { "a.cs", "b.cs" }
            .Select(name => FileChangeSet.Group([Edit("/p/" + name)], _ => Task.FromResult(new RevertFileResult(null)))[0])
            .ToArray();
        var card = new TurnChangesItemViewModel(files, opened.Add, () => views++);

        Assert.Equal(Strings.Format(TKCodeLangResourceKind.ChangesFilesChanged, 2), card.Header);
        Assert.Equal("+2", card.AddedText);
        Assert.Equal("−0", card.RemovedText);

        card.OpenCommand.Execute(files[1]);
        Assert.Equal(["/p/b.cs"], opened);

        card.ViewChangesCommand.Execute(null);
        Assert.Equal(1, views);
    }

    [Fact]
    public async Task Revert_HostRefusal_KeepsFileAndShowsError()
    {
        var files = FileChangeSet.Group([Edit("/p/a.cs", before: "x", after: "h")],
            _ => Task.FromResult(new RevertFileResult("has changed since the agent edited it")));

        await files[0].RevertCoreAsync();

        Assert.False(files[0].IsReverted);
        Assert.True(files[0].HasError);
    }

    private static ParsedToolDetails Edit(string path, string? before = null, string? after = null, bool created = false,
        int added = 1, int removed = 0) => new()
    {
        Kind = "diff",
        Path = path,
        Before = before,
        AfterSha256 = after,
        Created = created,
        Added = added,
        Removed = removed,
        DiffLines = [new DiffLineViewModel("+x", DiffLineKind.Added)],
    };

    [Fact]
    public void ChangePaths_ExcludePlanFiles_AndShowWorkspacePathsRelative()
    {
        var home = Path.Combine(Path.GetTempPath(), "tkcode-home-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("TKCODE_HOME");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        try
        {
            var plan = Path.Combine(home, "plans", "session.md");
            var workspace = Path.Combine(home, "src");

            Assert.False(ChangePaths.IsTracked(plan));
            Assert.True(ChangePaths.IsTracked(Path.Combine(workspace, "a.cs")));
            // 只排除 plans 目录本身，同名前缀的兄弟目录仍算工作区改动
            Assert.True(ChangePaths.IsTracked(Path.Combine(home, "plans-old", "session.md")));

            Assert.Equal("src/a.cs", ChangePaths.Display(Path.Combine(workspace, "a.cs"), home));
            Assert.Equal(plan, ChangePaths.Display(plan, workspace));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TKCODE_HOME", previous);
        }
    }
}

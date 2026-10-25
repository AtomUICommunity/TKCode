using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>项目记住的权限档位：完全授权也沿用到该项目的新会话，其它项目不受影响。</summary>
public sealed class PermissionMemoryTests
{
    [Fact]
    public async Task FullAccess_FollowsNewSessionsInTheSameProject()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var projectA = Directory.CreateTempSubdirectory("tkcode-ui-project-a-").FullName;
        var projectB = Directory.CreateTempSubdirectory("tkcode-ui-project-b-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        var settings = new StateService(Path.Combine(home, "state.json"));
        settings.Current.Workspaces.Add(projectA);
        settings.Current.Workspaces.Add(projectB);
        settings.Current.LastWorkspace = projectA;
        settings.Save();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var conversation = provider.GetRequiredService<MainWindowViewModel>().Conversation;
            var projects = provider.GetRequiredService<ProjectService>();
            await WaitUntil(() => conversation.Picker.Selected is not null && conversation.Workspace?.Primary == projectA, "ready");
            Assert.Equal(PermissionPickerViewModel.Ask, conversation.Permission.Mode);

            await Send(conversation, "你好");
            await WaitUntil(() => conversation.Active is { IsRunning: false }, "first session");
            var firstId = conversation.Active!.SessionId;
            Assert.Equal(PermissionPickerViewModel.Ask, conversation.Active.Options.PermissionMode);

            // 在项目 A 的会话里改成完全授权：记到这个项目，并同步到当前会话
            conversation.Permission.ChooseCommand.Execute(PermissionPickerViewModel.FullAccess);
            conversation.Permission.ConfirmFullAccessCommand.Execute(null);
            await WaitUntil(() => conversation.Active!.Options.PermissionMode == PermissionPickerViewModel.FullAccess, "permission applied");
            Assert.Equal(PermissionPickerViewModel.FullAccess,
                provider.GetRequiredService<StateService>().Current.PermissionModes[projects.FindByPath(projectA)!.Id]);

            conversation.NewChat();
            Assert.Null(conversation.Active);
            Assert.Equal(PermissionPickerViewModel.FullAccess, conversation.Permission.Mode);

            await Send(conversation, "再来一条");
            await WaitUntil(() => conversation.Active is { IsRunning: false } && conversation.Active.SessionId != firstId, "second session");
            Assert.Equal(PermissionPickerViewModel.FullAccess, conversation.Active!.Options.PermissionMode);

            // 另一个项目仍是询问；回到项目 A 的空状态继续沿用完全授权
            conversation.SetWorkspace(projects.FindByPath(projectB));
            Assert.Equal(PermissionPickerViewModel.Ask, conversation.Permission.Mode);
            conversation.SetWorkspace(projects.FindByPath(projectA));
            Assert.Equal(PermissionPickerViewModel.FullAccess, conversation.Permission.Mode);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static async Task Send(ConversationViewModel conversation, string text)
    {
        conversation.ComposerText = text;
        await conversation.SendCommand.ExecuteAsync(null);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
    }
}

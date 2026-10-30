using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class ModelPickerSnapshotTests
{
    private static readonly ModelDto[] Models =
    [
        new("claude-opus-5-5", "Claude Opus 5.5", "anthropic", "anthropic-messages", true, "Best for everyday, complex tasks",
            ["Low", "Medium", "High", "XHigh", "Max"], "High", [200_000, 1_000_000], true, "anthropic/claude-opus-5-5", "Anthropic"),
        new("claude-fable-5-1", "Claude Fable 5.1", "anthropic", "anthropic-messages", true, "Most capable model for the hardest problems",
            ["Low", "Medium", "High", "XHigh", "Max"], "High", [200_000, 1_000_000], false, "anthropic/claude-fable-5-1", "Anthropic"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "anthropic", "anthropic-messages", true, "Fast and capable for everyday coding",
            ["Low", "Medium", "High", "XHigh", "Max"], "High", [200_000, 1_000_000], false, "anthropic/claude-sonnet-5-5", "Anthropic"),
        new("gpt-6.1-sol", "GPT-6.1 Sol", "openai", "openai-responses", true, null,
            ["Low", "Medium", "High", "XHigh", "Max"], "High", [200_000], false, "openai/gpt-6.1-sol", "OpenAI"),
        new("deepseek-v4-pro", "DeepSeek V4 Pro", "deepseek", "openai-completions", true, null,
            ["Off", "Low", "High", "Max"], "High", [128_000], false, "deepseek/deepseek-v4-pro", "DeepSeek"),
    ];

    [Fact]
    public async Task Picker_RendersChipPopupAndSubmenu()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var settings = new StateService(Path.Combine(Path.GetTempPath(), $"tkcode-test-{Guid.NewGuid():N}.json"));
            var picker = new ModelPickerViewModel(settings);
            picker.Load(Models);
            Assert.Equal("Opus 5.5", picker.Selected?.ShortName);
            Assert.EndsWith("200K · 关", picker.Summary);

            var view = new ModelPickerView { DataContext = picker, Margin = new Thickness(40), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            var window = new Avalonia.Controls.Window { Width = 900, Height = 560, Content = view, Background = Avalonia.Media.Brushes.Black };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            HeadlessApp.Save(window, "model-picker-chip");

            // 无头平台没有原生弹出窗口，使用窗口的覆盖层渲染弹层，便于整窗截图。
            view.GetVisualDescendants().OfType<Popup>().Single().ShouldUseOverlayLayer = true;
            view.Toggle();
            Dispatcher.UIThread.RunJobs();
            var popup = view.GetVisualDescendants().OfType<Popup>().Single();
            Assert.True(popup.IsOpen);
            Assert.Equal(3, picker.VisibleModels.Count);
            HeadlessApp.Save(window, "model-picker-popup");

            picker.ToggleParameterCommand.Execute(picker.Reasoning);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, picker.Reasoning.Choices.Count);
            HeadlessApp.Save(window, "model-picker-reasoning");

            picker.ChooseParameterCommand.Execute(picker.Reasoning.Choices[3]);
            Assert.Equal("XHigh", picker.ThinkingLevel);
            Assert.StartsWith("超高", picker.Summary);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Picker_LongList_KeepsFavoriteButtonClearOfScrollBar()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var settings = new StateService(Path.Combine(Path.GetTempPath(), $"tkcode-test-{Guid.NewGuid():N}.json"));
            var picker = new ModelPickerViewModel(settings);
            picker.Load(Enumerable.Range(1, 9).Select(i => new ModelDto($"glm-{i}", $"GLM-5.{i}", "zai", "openai-completions", true, null,
                ["Off", "High"], "High", [128_000], i == 1, $"zai/glm-{i}", "Z.ai")).ToArray());
            var view = new ModelPickerView { DataContext = picker, Margin = new Thickness(40), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            var window = new Avalonia.Controls.Window { Width = 900, Height = 560, Content = view, Background = Avalonia.Media.Brushes.Black };
            window.Show();
            view.GetVisualDescendants().OfType<Popup>().Single().ShouldUseOverlayLayer = true;
            view.Toggle();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(9, picker.VisibleModels.Count);
            HeadlessApp.Save(window, "model-picker-scroll");   // 列表出现滚动条：右侧收藏按钮不被盖住
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Picker_GroupsRuntimes_AndFiltersBySessionScope()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var settings = new StateService(Path.Combine(Path.GetTempPath(), $"tkcode-test-{Guid.NewGuid():N}.json"));
            var picker = new ModelPickerViewModel(settings);
            ModelDto[] models =
            [
                Models[0],
                new("claude-opus-5-5", "Claude Opus 5.5", "claude-code", "external", true, null, ["High"], "High", [200_000], false,
                    "claude-code/claude-opus-5-5", "Claude Agent", Runtime: "claude-code"),
                new("gpt-6.1-sol", "GPT-6.1 Sol", "codex", "external", true, null, ["High"], "High", [200_000], false,
                    "codex/gpt-6.1-sol", "Codex", Runtime: "codex"),
            ];
            picker.Load(models);
            // Agents 分组在内置厂商之后，第一个运行时页签带分隔线
            var tabs = picker.Tabs.Where(t => t.ProviderId is not null).ToList();
            Assert.Equal(["anthropic", "claude-code", "codex"], tabs.Select(t => t.ProviderId));
            Assert.True(tabs[1].StartsRuntimeGroup && tabs[1].IsRuntime);
            Assert.Equal("claudecode", tabs[1].IconKey);

            // 有历史的 claude-code 会话：只能选 claude-code 的模型
            picker.SetScope("claude-code");
            Assert.False(tabs[0].IsAvailable);
            Assert.False(tabs[2].IsAvailable);
            picker.Search = "o";
            Assert.All(picker.VisibleModels, m => Assert.Equal("claude-code", m.Model.Runtime));
            // 内置会话：不显示运行时模型
            picker.SetScope("");
            Assert.All(picker.VisibleModels, m => Assert.Null(m.Model.Runtime));
            // 新会话：全部可选
            picker.SetScope(null);
            Assert.Contains(picker.VisibleModels, m => m.Model.Runtime == "codex");
        }, TestContext.Current.CancellationToken);
    }
}

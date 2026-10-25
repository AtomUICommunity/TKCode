using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;
using TKCode.Desktop.Controls;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>ChoiceSelection：Arc 的分段选择跟随 SettingsChoiceViewModel 的 IsSelected，用户改选时执行命令。</summary>
public sealed class ChoiceSelectionTests
{
    private static ObservableCollection<SettingsChoiceViewModel> Choices(string selected) =>
    [
        new("a", "5 分钟") { IsSelected = selected == "a" },
        new("b", "1 小时") { IsSelected = selected == "b" },
        new("c", "永久") { IsSelected = selected == "c" },
    ];

    private static (Window Window, SegmentedControl Segments) Host(ObservableCollection<SettingsChoiceViewModel> items, RelayCommand<SettingsChoiceViewModel> command)
    {
        var segments = new SegmentedControl { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Left };
        ChoiceSelection.SetCommand(segments, command);
        var window = new Window { Width = 320, Height = 80, Content = new Border { Padding = new Avalonia.Thickness(16), Child = segments } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, segments);
    }

    [Fact]
    public async Task Selection_FollowsIsSelected_AndRebuiltLists()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var items = Choices("b");
            var (window, segments) = Host(items, new RelayCommand<SettingsChoiceViewModel>(_ => { }));
            Assert.Same(items[1], segments.SelectedItem);
            HeadlessApp.Save(window, "choice-segmented");

            // 视图模型先取消旧的再选中新的：中间那一刻没有选中项，控件不能跟着变成“不选”（选中条会消失再跳到新位置）
            var seen = new List<object?>();
            segments.SelectionChanged += (_, _) => seen.Add(segments.SelectedItem);
            items[1].IsSelected = false;
            items[2].IsSelected = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(items[2], segments.SelectedItem);
            Assert.Equal([items[2]], seen);

            // 选项变了时 Fill() 清空后重新添加（新对象）
            items.Clear();
            items.Add(new SettingsChoiceViewModel("a", "5 分钟") { IsSelected = true });
            items.Add(new SettingsChoiceViewModel("b", "1 小时"));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(items[0], segments.SelectedItem);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UserChoice_ExecutesCommandOnce_WithTheChosenItem()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var items = Choices("a");
            var executed = new List<string>();
            var command = new RelayCommand<SettingsChoiceViewModel>(choice =>
            {
                executed.Add(choice!.Value);
                // 视图模型接受选择：换选中项（与 SettingsViewModel 的 Choose* 一致）
                foreach (var item in items)
                {
                    item.IsSelected = ReferenceEquals(item, choice);
                }
            });
            var (window, segments) = Host(items, command);

            segments.SelectedItem = items[2];
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["c"], executed);
            Assert.Same(items[2], segments.SelectedItem);
            Assert.True(items[2].IsSelected);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RejectedChoice_SnapsBackToTheViewModelState()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var items = Choices("a");
            var executed = 0;
            var (window, segments) = Host(items, new RelayCommand<SettingsChoiceViewModel>(_ => executed++));

            segments.SelectedItem = items[1];
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, executed);
            Assert.Same(items[0], segments.SelectedItem);
            Assert.True(items[0].IsSelected);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}

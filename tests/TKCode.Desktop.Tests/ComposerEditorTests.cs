using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Services;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>自绘输入框（docs/design/composer.md §6）：文字与 tag 的排版、光标、选择、键盘与鼠标。</summary>
public sealed class ComposerEditorTests
{
    private static readonly TagSegment Foo = new(TagKind.File, "src/TKCode.Desktop/Views/ComposerView.axaml.cs", "ComposerView.axaml.cs");
    private static readonly TagSegment Review = new(TagKind.Command, "review", "review");
    private static readonly TagSegment Pdf = new(TagKind.Skill, "pdf", "pdf");
    private static readonly TagSegment Src = new(TagKind.Folder, "src/TKCode.Desktop", "src/TKCode.Desktop");

    private static readonly RawInputModifiers Command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [Fact]
    public async Task Renders_TextTagsSelectionAndPlaceholder_InBothThemes()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var empty = Editor([], minLines: 2);
            empty.PlaceholderText = "问点什么，@ 引用文件，/ 使用命令";
            var tags = Editor([Review, new TextSegment(" 看下 "), Foo, new TextSegment(" 的 bug，用 "), Pdf, new TextSegment(" 读一下 "), Src]);
            var wrapped = Editor([new TextSegment("这一段文字很长，会在中间折行，tag 不会被拆成两半："), Foo, new TextSegment("\n第二段\n")]);
            var longTag = Editor([new TagSegment(TagKind.File, "src/a/very/deep/directory/structure/that/goes/on/and/on/SomeVeryLongFileName.cs",
                "src/a/very/deep/directory/structure/that/goes/on/and/on/SomeVeryLongFileName.cs")]);
            var scrolled = Editor([new TextSegment(string.Join("\n", Enumerable.Range(1, 8).Select(i => $"第 {i} 行")))], maxLines: 3);

            var panel = new StackPanel { Spacing = 10, Margin = new Thickness(16), Width = 440 };
            foreach (var editor in new[] { empty, tags, wrapped, longTag, scrolled })
            {
                var frame = new Border
                {
                    Child = editor,
                    Padding = new Thickness(10, 8),
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1),
                };
                frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("TKCode.ComposerBg"));
                frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("TKCode.ComposerBorder"));
                panel.Children.Add(frame);
            }
            var window = new Window { Width = 480, Height = 560, Content = panel };
            window.Bind(Window.BackgroundProperty, window.GetResourceObservable("TKCode.WindowBg"));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 选区跨过 tag；滚动的输入框光标在末尾（滚到最后一行）
            wrapped.Focus();
            wrapped.Select(10, 30);
            scrolled.Focus();
            scrolled.CaretIndex = scrolled.Document.Length;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, Lines(scrolled));
            Assert.True(tags.Bounds.Height < wrapped.Bounds.Height);
            HeadlessApp.Save(window, "composer-editor-dark");

            var themes = provider.GetRequiredService<ThemeService>();
            themes.Attach(Application.Current!);
            await themes.SetModeAsync(ThemeMode.Light);
            Dispatcher.UIThread.RunJobs();
            HeadlessApp.Save(window, "composer-editor-light");
            await themes.SetModeAsync(ThemeMode.Dark);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Keyboard_TreatsTagAsOneCharacter()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var (window, editor) = Host(Editor([]));
            editor.Focus();

            window.KeyTextInput("hello ");
            editor.InsertSegments([Foo]);
            window.KeyTextInput(" world");
            Assert.Equal("hello @" + Foo.Value + " world", editor.Document.ToPlainText());
            Assert.Equal(13, editor.CaretIndex);

            // ← 跨过 " world" 与 tag：tag 只占一步
            for (var i = 0; i < 7; i++)
            {
                Press(window, Key.Left);
            }
            Assert.Equal(6, editor.CaretIndex);
            Press(window, Key.Right, RawInputModifiers.Shift);
            Assert.Equal((6, 7), (editor.SelectionStart, editor.SelectionEnd));

            // 退格删除选中的 tag；撤销恢复
            Press(window, Key.Back);
            Assert.Equal("hello  world", editor.Document.ToPlainText());
            Press(window, Key.Z, Command);
            Assert.Equal(Foo, editor.Document.TagAt(6));

            // 光标在 tag 之后时退格整体删除
            editor.CaretIndex = 7;
            Press(window, Key.Back);
            Assert.Equal("hello  world", editor.Document.ToPlainText());
            Assert.Equal(6, editor.CaretIndex);

            // Shift+Enter 换行；Enter 留给宿主
            editor.CaretIndex = editor.Document.Length;
            Press(window, Key.Enter, RawInputModifiers.Shift);
            Press(window, Key.Enter);
            Assert.EndsWith("world\n", editor.Document.ToPlainText());

            // 全选后输入替换全部
            Press(window, Key.A, Command);
            window.KeyTextInput("x");
            Assert.Equal("x", editor.Document.ToPlainText());
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VerticalMovement_KeepsColumn_AndHomeEndStayOnLine()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var (window, editor) = Host(Editor([new TextSegment("abcdef\nab\nabcdef")]));
            editor.Focus();
            editor.CaretIndex = 5;

            Press(window, Key.Down);
            Assert.Equal(9, editor.CaretIndex);      // 第二行较短：停在行尾
            Press(window, Key.Down);
            Assert.Equal(15, editor.CaretIndex);     // 回到原来的列
            Press(window, Key.Up, RawInputModifiers.Shift);
            Assert.Equal((9, 15), (editor.SelectionStart, editor.SelectionEnd));

            editor.CaretIndex = 8;
            Press(window, Key.End);
            Assert.Equal(9, editor.CaretIndex);
            Press(window, Key.Home);
            Assert.Equal(7, editor.CaretIndex);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Mouse_PlacesCaretBySideOfTag_AndDoubleClickSelectsTag()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var (window, editor) = Host(Editor([new TextSegment("看下 "), Foo, new TextSegment(" 的 bug")]));
            var left = editor.CaretBounds(3);
            var right = editor.CaretBounds(4);
            Assert.True(right.X - left.X > 40, "tag 有宽度");
            var y = left.Center.Y;

            Click(window, editor, new Point(right.X - 4, y));
            Assert.True(editor.IsFocused);
            Assert.Equal(4, editor.CaretIndex);
            Click(window, editor, new Point(left.X + 4, y));
            Assert.Equal(3, editor.CaretIndex);

            // 双击 tag：选中整个 tag
            Click(window, editor, new Point(left.X + 20, y));
            Click(window, editor, new Point(left.X + 20, y));
            Assert.Equal((3, 4), (editor.SelectionStart, editor.SelectionEnd));

            // 从 tag 前拖到文本末尾
            var start = editor.TranslatePoint(new Point(left.X + 1, y), window)!.Value;
            var end = editor.TranslatePoint(editor.CaretBounds(editor.Document.Length).Center, window)!.Value;
            window.MouseMove(new Point(1, 1));
            Dispatcher.UIThread.RunJobs();
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal((3, editor.Document.Length), (editor.SelectionStart, editor.SelectionEnd));
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Height_GrowsWithContent_UpToMaxLines()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var (window, editor) = Host(Editor([], minLines: 1, maxLines: 4));
            var one = editor.Bounds.Height;
            editor.Focus();
            for (var i = 0; i < 2; i++)
            {
                Press(window, Key.Enter, RawInputModifiers.Shift);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(one * 3, editor.Bounds.Height, 1);
            for (var i = 0; i < 5; i++)
            {
                Press(window, Key.Enter, RawInputModifiers.Shift);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(one * 4, editor.Bounds.Height, 1);
            // 光标在最后一行：已滚动，最后一行可见
            Assert.True(editor.CaretBounds(editor.CaretIndex).Bottom <= editor.Bounds.Height + 0.5);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Ime_PreeditShowsAtCaret_AndCommitReplacesIt()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var (window, editor) = Host(Editor([new TextSegment("看 "), Foo, new TextSegment(" ")]));
            editor.Focus();
            editor.CaretIndex = editor.Document.Length;
            var args = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.RaiseEvent(args);
            var ime = Assert.IsAssignableFrom<TextInputMethodClient>(args.Client);

            // tag 在 SurroundingText 里记为 U+FFFC，位置与文档一一对应
            Assert.Equal("看 \uFFFC ", ime.SurroundingText);
            Assert.Equal(new TextSelection(4, 4), ime.Selection);

            var before = ime.CursorRectangle;
            ime.SetPreeditText("zhong'wen");
            Dispatcher.UIThread.RunJobs();
            Assert.True(ime.CursorRectangle.X > before.X + 20, "候选框跟随预编辑文字");
            Assert.Equal(4, editor.Document.Length);   // 预编辑文字不进入文档
            HeadlessApp.Save(window, "composer-editor-preedit");

            // 提交：预编辑文字被提交的文字取代
            window.KeyTextInput("中文");
            ime.SetPreeditText(null);
            Assert.Equal("看 @" + Foo.Value + " 中文", editor.Document.ToPlainText());
            Assert.Equal(6, editor.CaretIndex);
            Assert.False(editor.HasPreedit);

            // 输入法替换选区（例如 macOS 的重音选择）
            ime.Selection = new TextSelection(5, 6);
            window.KeyTextInput("字");
            Assert.Equal("看 @" + Foo.Value + " 中字", editor.Document.ToPlainText());
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Clipboard_RoundTripsTags_AndExternalTextStaysText()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var (window, editor) = Host(Editor([new TextSegment("看 "), Foo, new TextSegment(" 的 bug")]));
            editor.Focus();
            editor.Select(0, 4);
            await editor.CopyAsync();
            Assert.Equal("看 @" + Foo.Value + " ", await window.Clipboard!.TryGetTextAsync());

            editor.CaretIndex = editor.Document.Length;
            await editor.PasteAsync();
            Assert.Equal([new TextSegment("看 "), Foo, new TextSegment(" 的 bug看 "), Foo, new TextSegment(" ")], editor.Document.Segments);

            // 剪切
            editor.Select(0, 4);
            await editor.CutAsync();
            Assert.Equal("的 bug看 @" + Foo.Value + " ", editor.Document.ToPlainText());

            // 外部纯文本：@路径 不会变成 tag；换行统一为 \n
            await window.Clipboard!.SetTextAsync("@src/a.cs\r\nnext");
            editor.SelectAll();
            await editor.PasteAsync();
            Assert.Equal([new TextSegment("@src/a.cs\nnext")], editor.Document.Segments);

            // 宿主处理了粘贴（例如图片）：不再插入文本
            editor.Pasting += (_, e) => e.Handled = true;
            await editor.PasteAsync();
            Assert.Equal("@src/a.cs\nnext", editor.Document.ToPlainText());
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ContextMenu_IsArcMenu_WithEditCommands()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var view = new Views.ComposerView();
            var window = new AtomUI.Desktop.Controls.Window { Width = 600, Height = 300, Content = view };
            TKCode.Arc.Motion.ArcMotion.SetLevel(window, TKCode.Arc.Motion.ArcMotionLevel.Off);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var editor = view.FindControl<ComposerEditor>("Input")!;
            editor.Document = new ComposerDocument([new TextSegment("abc")]);

            // 右键打开 Arc 菜单：剪切 / 复制 / 粘贴 / 全选
            var menu = TKCode.Arc.ContextMenu.GetMenu(editor)!;
            editor.RaiseEvent(new ContextRequestedEventArgs());
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsOpen);
            var entries = menu.Items.OfType<TKCode.Arc.MenuEntry>().ToArray();
            Assert.Equal(4, entries.Length);
            HeadlessApp.Save(window, "composer-context-menu");

            entries[3].PerformClick();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((0, 3), (editor.SelectionStart, editor.SelectionEnd));
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static ComposerEditor Editor(IEnumerable<Segment> segments, int minLines = 1, int maxLines = 10) => new()
    {
        Document = new ComposerDocument(segments),
        MinLines = minLines,
        MaxLines = maxLines,
        FontSize = 14,
    };

    private static (Window, ComposerEditor) Host(ComposerEditor editor)
    {
        var window = new Window
        {
            Width = 420, Height = 300,
            Content = new Border { Child = editor, Padding = new Thickness(10), VerticalAlignment = VerticalAlignment.Top },
        };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("TKCode.WindowBg"));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, editor);
    }

    private static int Lines(ComposerEditor editor) =>
        (int)Math.Round(editor.Bounds.Height / editor.CaretBounds(0).Height);

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, ComposerEditor editor, Point local)
    {
        var point = editor.TranslatePoint(local, window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}

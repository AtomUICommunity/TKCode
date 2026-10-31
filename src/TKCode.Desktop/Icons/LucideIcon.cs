using Avalonia;
using Avalonia.Media;

namespace TKCode.Desktop.Icons;

public enum LucideKind
{
    SquarePen, Search, Bot, LayoutGrid, Plus, Folder, FolderPlus, ListFilter, Settings,
    PanelLeft, PanelRight, ArrowLeft, ArrowRight, ArrowUp, Mic, ChevronDown, ChevronRight,
    Check, GitBranch, Laptop, GitCompare, Globe, SquareTerminal, File, FileText, Ellipsis,
    ExternalLink, Rocket, Database, ListChecks, Bug, Square, X, RefreshCw, Star, Sun, Moon,
    Lightbulb, Wrench, Copy, Layers, Maximize, Brain, CircleAlert,
    StarFilled, Box, Paperclip, Monitor, Zap, SlidersHorizontal, CornerDownLeft, MessageSquare, ArrowDown, Languages, StopFilled, Eye, EyeOff, Image, Trash, Pencil, FolderTree, Maximize2, Minimize2, FileCode, Braces, History, BookOpen, Crosshair, Code, ZoomIn, ZoomOut, Scan,
    ShieldQuestion, ShieldCheck, ShieldAlert, Pin, Plug, LogIn, Download, Upload, Sparkles, GitCommit, MessageCircleQuestion,
    ClipboardCheck, ClipboardList, ArrowUpToLine, ChevronUp, Undo2, GitFork,
}

/// <summary>
/// Lucide 风格的线性图标（24×24、2px 圆头描边），基于 <see cref="GlyphIcon"/>，
/// 未指定 StrokeBrush 时跟随继承的前景色（按钮悬停 / 禁用）。
/// </summary>
public sealed class LucideIcon : GlyphIcon
{
    public static readonly StyledProperty<LucideKind> KindProperty =
        AvaloniaProperty.Register<LucideIcon, LucideKind>(nameof(Kind));

    private static readonly Dictionary<LucideKind, GlyphPath[]> Cache = new();

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(KindProperty);
    }

    public LucideIcon()
    {
    }

    public LucideIcon(LucideKind kind) : this() => Kind = kind;

    public LucideKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    internal override IReadOnlyList<GlyphPath> Paths => Get(Kind);

    private static GlyphPath[] Get(LucideKind kind)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(kind, out var instructions))
            {
                instructions = LucidePaths.For(kind).Select(Create).ToArray();
                Cache[kind] = instructions;
            }
            return instructions;
        }
    }

    private static GlyphPath Create(string data)
    {
        var filled = data.StartsWith('#');
        return new GlyphPath(StreamGeometry.Parse(filled ? data[1..] : data), filled);
    }
}

using TKCode.Agent.Coding.Workspace;
using TKCode.Ai.Messages;

namespace TKCode.Agent.Coding.Tools.Files;

/// <summary>
/// 用户在界面上回退、而会话仍在继续的文件（docs/design/message-rewind.md §5.3）。
/// 累积到下一批输入之前（<see cref="TKCode.Agent.Agent.ContextUpdates"/>），作为一条系统消息告诉 Agent，
/// 历史只追加、只在两次模型请求之间变化。编辑前的"先读后改"由 <see cref="FileStateTracker"/> 按内容比对另行保证。
/// </summary>
public sealed class RevertNotices
{
    private readonly Lock _gate = new();
    private readonly List<string> _paths = [];

    public void Add(string path)
    {
        lock (_gate)
        {
            if (!_paths.Exists(p => PathUtil.SamePath(p, path)))
            {
                _paths.Add(path);
            }
        }
    }

    /// <summary>取出并清空累积的回退；没有时为 null。</summary>
    public SystemMessage? Take()
    {
        lock (_gate)
        {
            if (_paths.Count == 0)
            {
                return null;
            }
            var message = new SystemMessage(Text(_paths));
            _paths.Clear();
            return message;
        }
    }

    /// <summary>发给模型的文字（设计与评审见 docs/design/message-rewind.md §5.3）。</summary>
    public static string Text(IReadOnlyList<string> paths) =>
        "<system-reminder>\n" +
        "The user reverted these files to how they were before your earlier edits, so those edits are gone. " +
        "Read a file again before relying on its content or editing it:\n" +
        string.Join("\n", paths.Select(p => "- " + p)) + "\n" +
        "</system-reminder>";
}

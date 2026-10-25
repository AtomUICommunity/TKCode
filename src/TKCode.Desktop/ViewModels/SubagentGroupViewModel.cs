using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 转录里连续启动的子 Agent 合为一个条目：标题“启动了 N 个子 Agent”，下面每个子 Agent 一行（docs/design/subagent-view.md §6.2）。
/// 由 TranscriptAdapter 按组内第一个工具调用缓存，行按引用就地增删，已有的行与视图不重建。
/// </summary>
public sealed partial class SubagentGroupViewModel : TranscriptItemViewModel
{
    public ObservableCollection<SubagentCardViewModel> Agents { get; } = [];

    [ObservableProperty]
    public partial string Header { get; private set; } = "";

    public void SetAgents(IReadOnlyList<SubagentCardViewModel> agents)
    {
        for (var i = Agents.Count - 1; i >= 0; i--)
        {
            if (!agents.Contains(Agents[i]))
            {
                Agents.RemoveAt(i);
            }
        }
        for (var i = 0; i < agents.Count; i++)
        {
            var at = Agents.IndexOf(agents[i]);
            if (at < 0)
            {
                Agents.Insert(i, agents[i]);
            }
            else if (at != i)
            {
                Agents.Move(at, i);
            }
        }
        RefreshLanguage();
    }

    public void RefreshLanguage()
    {
        Header = Agents.Count == 1
            ? Strings.Get(TKCodeLangResourceKind.SubagentGroupSingle)
            : Strings.Format(TKCodeLangResourceKind.SubagentGroupFormat, Agents.Count);
        foreach (var agent in Agents)
        {
            agent.RefreshLanguage();
        }
    }
}

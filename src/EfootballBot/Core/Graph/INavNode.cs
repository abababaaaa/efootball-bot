using EfootballBot.Core.Bot;

namespace EfootballBot.Core.Graph;

/// <summary>导航节点判定状态。</summary>
public enum NodeState
{
    /// <summary>已锚定在目标位置。</summary>
    Anchored,

    /// <summary>在合法路径上但尚未到达，继续向目标移动。</summary>
    Transit,

    /// <summary>检测到非目标内容，记错误并走回。</summary>
    OffTrack,

    /// <summary>信号不足，只等待不动作。</summary>
    Uncertain,
}

/// <summary>
/// 可导航页面区域节点：回答「状态对不对 / 下一步怎么走」。
/// 每个可到达的页面区域实现该接口，交由 <see cref="NavigationService"/> 闭环执行。
/// </summary>
public interface INavNode
{
    /// <summary>动作步数预算（Uncertain 等待不扣预算）。</summary>
    int StepBudget { get; }

    /// <summary>基于当前观察判定状态。</summary>
    NodeState Check(EfootballBot.Core.Bot.Observation obs);

    /// <summary>朝目标的下一步动作（OffTrack 时同样给出走回动作）。</summary>
    IReadOnlyList<NavAction> NextStep(EfootballBot.Core.Bot.Observation obs);

    /// <summary>当前状态的人类可读描述（日志 / 报错用）。</summary>
    string Describe(EfootballBot.Core.Bot.Observation obs);
}

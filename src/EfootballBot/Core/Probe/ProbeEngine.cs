using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Probe;

public sealed class ProbeResult
{
    public GameScreen Screen;
    public double RawScore;
    public List<string> Evidence = new();
    public GameScreen? Override;
    public string? OverrideReason;
    public bool HoldMatch;
}

/// <summary>页面定义：探针 + 优先级 + 阈值。</summary>
public sealed class PageDef
{
    public GameScreen Screen;
    public Probe[] Probes = Array.Empty<Probe>();
    public double MinScore;
    public int Priority;
}

/// <summary>
/// 声明式探针引擎。
/// 1. 统一命中口径（rawScore，阈值只基于原始值）
/// 2. 禁止早返回（所有探针都计算，Override 写入结果对象）
/// 3. 声明式优先级（PageDef.Priority 消解冲突）
/// 4. 迟滞 holdMatch
/// </summary>
public sealed class ProbeEngine
{
    private readonly List<PageDef> _pages;
    private bool _holdMatch;

    public ProbeEngine(IEnumerable<PageDef> pages) => _pages = pages.ToList();

    /// <summary>重置迟滞状态（每次开始挂机前调用）。</summary>
    public void Reset() => _holdMatch = false;

    public ProbeResult Classify(EfootballBot.Core.Bot.Observation obs)
    {
        var result = new ProbeResult();

        // 1. 计算所有页面探针分数（禁止早返回：全部计算）
        var candidates = ComputeCandidates(obs);

        // 2. Override 信号（写入结果，不中断计算）
        if (HasRewardPopup(obs))
        {
            result.Override = GameScreen.MyLeagueHome;
            result.OverrideReason = "reward-popup";
        }
        if (HasFixedAssistPopup(obs))
        {
            result.Override = GameScreen.Dialog;
            result.OverrideReason = "fixed-assist-popup";
        }
        // 关键比赛通知弹窗（白色 OK + 蓝色取消，背景透出赛程表会喂高 Schedule）：强制 Dialog
        if (ScreenClassifier.IsKeyNotice(obs.Ocr))
        {
            result.Override = GameScreen.Dialog;
            result.OverrideReason = "key-notice";
        }
        if (IsLoadingSpinner(obs))
        {
            result.Override = GameScreen.Loading;
            result.OverrideReason = "spinner";
        }

        // 3. Override 优先于迟滞：积分弹窗/固定辅助弹窗/加载转圈是强制信号（旧分类器中优先级最高）。
        //    积分弹窗只可能出现在主页（比赛确定结束）；转圈后靠草皮探针重新进入 hold。
        if (result.Override.HasValue)
        {
            _holdMatch = false;
            result.Screen = result.Override.Value;
            result.Evidence.Insert(0, result.OverrideReason ?? "override");
            return result;
        }

        // 4. 迟滞 holdMatch
        if (_holdMatch)
        {
            if (IsStrongMatchExit(candidates))
            {
                _holdMatch = false;
            }
            else
            {
                result.Screen = GameScreen.InMatch;
                result.HoldMatch = true;
                result.Evidence.Add("hold");
                if (candidates.Count > 0)
                    result.Evidence.Add(candidates[0].Ev.FirstOrDefault() ?? "");
                return result;
            }
        }

        // 5. 冲突消解（Priority 降序，同分取 RawScore 高）
        var best = candidates
            .OrderByDescending(c => c.Page.Priority)
            .ThenByDescending(c => c.Raw)
            .FirstOrDefault();
        if (best.Page is not null)
        {
            result.Screen = best.Page.Screen;
            result.RawScore = best.Raw;
            result.Evidence = best.Ev;
            if (best.Page.Screen == GameScreen.InMatch) _holdMatch = true;
        }

        return result;
    }

    /// <summary>
    /// 计算所有页面的探针候选（达到 MinScore 的页面）。
    /// 提取为独立方法，供 Classify 内部使用与可视化面板取 Top-N 候选。
    /// </summary>
    private List<(PageDef Page, double Raw, List<string> Ev)> ComputeCandidates(EfootballBot.Core.Bot.Observation obs)
    {
        var candidates = new List<(PageDef Page, double Raw, List<string> Ev)>();
        foreach (var p in _pages)
        {
            double raw = 0;
            var ev = new List<string>();
            foreach (var probe in p.Probes)
            {
                double s = probe.Score(obs);
                if (s > 0)
                {
                    raw += s;
                    ev.Add(probe.ToString() ?? probe.GetType().Name);
                }
            }
            if (raw >= p.MinScore) candidates.Add((p, raw, ev));
        }
        return candidates;
    }

    /// <summary>
    /// 返回 Top-N 探针候选（按优先级降序、同优先级按原始分降序），供可视化面板展示。
    /// 不触发 Override / 迟滞逻辑，仅反映原始探针打分。
    /// </summary>
    public List<(GameScreen Screen, double Raw, List<string> Ev)> GetTopCandidates(
        EfootballBot.Core.Bot.Observation obs, int topN = 5)
    {
        return ComputeCandidates(obs)
            .OrderByDescending(c => c.Page.Priority)
            .ThenByDescending(c => c.Raw)
            .Take(topN)
            .Select(c => (c.Page.Screen, c.Raw, c.Ev))
            .ToList();
    }

    /// <summary>赛后奖励 / 通知弹窗（积分弹窗 / 收件箱 / 联赛积分 / 物品已过期）。</summary>
    private static bool HasRewardPopup(EfootballBot.Core.Bot.Observation o)
        => o.Ocr.Contains("获得的活动积分")
           || o.Ocr.Contains("距离下个奖励")
           || o.Ocr.Contains("物品已送到收件箱")
           || o.Ocr.Contains("送到收件箱")
           || o.Ocr.Contains("收到我的联赛积分")
           || o.Ocr.Contains("过期")
           || o.Ocr.ContainsJoined("过期")
           || o.Ocr.Contains("获取新物品");

    /// <summary>「使用固定智能辅助设置的活动」模态弹窗。</summary>
    private static bool HasFixedAssistPopup(EfootballBot.Core.Bot.Observation o)
        => o.Ocr.ContainsJoined("固定智能辅助")
           || o.Ocr.ContainsJoined("智能辅助固定");

    /// <summary>KONAMI 过渡加载画面：暗屏 + 白色点状转圈。</summary>
    private static bool IsLoadingSpinner(EfootballBot.Core.Bot.Observation o)
    {
        if (o.Frame is null || o.Ocr.Words.Count > 5) return false;
        if (o.Ocr.Words.Any(w =>
            global::System.Text.RegularExpressions.Regex.IsMatch(w.Text, @"^\d{1,2}[:：]\d{2}$")))
            return false;
        if (FrameAnalyzer.MeanLuminance(o.Frame) > 30) return false;
        var spinnerRoi = new NormRect(0.74, 0.24, 0.14, 0.12);
        double white = FrameAnalyzer.ColorRatioExact(o.Frame, HsvFilter.White, spinnerRoi);
        return white is >= 0.004 and <= 0.08;
    }

    /// <summary>
    /// 比赛锁定中的强退出信号：
    /// ① 赛后/断线/标题画面（出现即退）；
    /// ② 稳定菜单页强证据（raw≥3，多个专属词命中）——真机常见比赛末尾连按 A 跳完整条结算、
    ///    下一帧直接是主页积分弹窗或商店，此时不会再出现 Result/Rewards，必须靠菜单页退出 hold。
    ///    要求高分是为了防止比赛直播中广告牌/观众席偶现菜单词导致误退。
    /// </summary>
    private static readonly GameScreen[] StrongExitScreens =
    {
        GameScreen.Result, GameScreen.Rewards, GameScreen.FullTime, GameScreen.HalfTime,
        GameScreen.Disconnected, GameScreen.TitleScreen,
    };

    private static readonly GameScreen[] MenuExitScreens =
    {
        GameScreen.Home, GameScreen.MatchHub, GameScreen.EventHub, GameScreen.EventInfo,
        GameScreen.ChallengeList, GameScreen.ChallengeHome, GameScreen.ActivityDetail,
        GameScreen.Schedule, GameScreen.Shop, GameScreen.ShopConfirm, GameScreen.DailyRewards,
        GameScreen.EventSettings, GameScreen.KeyMatchBadge, GameScreen.MyLeagueHome,
        GameScreen.CampaignBoard, GameScreen.NoEvent,
    };

    private const double MenuExitMinRaw = 3.0;

    private static bool IsStrongMatchExit(List<(PageDef Page, double Raw, List<string> Ev)> candidates)
        => candidates.Any(c => StrongExitScreens.Contains(c.Page.Screen))
        || candidates.Any(c => MenuExitScreens.Contains(c.Page.Screen) && c.Raw >= MenuExitMinRaw);
}

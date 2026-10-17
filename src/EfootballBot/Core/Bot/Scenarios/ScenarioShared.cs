using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios;

/// <summary>
/// 各场景共用的小流程片段：标题画面宽限、比赛上下文判断、每轮决策快照。
/// 抽自多个场景逐字重复的副本。统一后基类（含国服我的联赛子类）也获得
/// 赛后 90s 宽限档——此前仅 30s，实机 2026-10-12 记录过全场结束后 33s 处
/// 品牌过场被按 X 打断弹窗链的事故。
/// </summary>
internal static class ScenarioShared
{
    /// <summary>
    /// 标题画面宽限：X 键动作落在「eFootball™+X」帧时，若最近有比赛流程上下文则多为
    /// 开赛加载/赛后品牌过场，先等待不按 X。刚见过终场（弹窗链后的品牌过场会连续
    /// 30s+）宽限 90s，其余 30s；真实标题画面（掉线/游戏重启）停留超宽限期后照常按 X。
    /// 调用方需在非 X 动作时清 <paramref name="titleSince"/>。返回 true = 本轮跳过（等 2000ms）。
    /// </summary>
    public static bool TitleGrace(MlFlowMemory memory, DateTime now, ref DateTime? titleSince,
        Action<string, LogLevel> log, string module)
    {
        bool recentFlow =
            memory.SawWithin(MlScreen.InMatch, TimeSpan.FromMinutes(3), now) ||
            memory.SawWithin(MlScreen.MatchStartPage, TimeSpan.FromMinutes(3), now) ||
            memory.SawWithin(MlScreen.LeaguePage, TimeSpan.FromMinutes(3), now);
        bool postMatchChain = memory.SawWithin(MlScreen.FullTime, TimeSpan.FromMinutes(2), now);
        var grace = postMatchChain ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30);
        titleSince ??= now;
        if (recentFlow && now - titleSince.Value < grace)
        {
            log($"[{module}] eFootball 标志帧（疑似比赛加载/赛后过场画面），暂不按 X，继续观察", LogLevel.Info);
            return true;
        }
        return false;
    }

    /// <summary>比赛上下文：最近 90s 见过比赛画面或起始页。角区探针兜底 InMatch 的前置
    /// 条件——菜单页右上角也可能有内容（2026-10-11 实机联赛主页奖励徽章点亮角区探针），
    /// 无上下文时宁可判 Unknown 等一拍，绝不把菜单当比赛盲按 A。</summary>
    public static bool MatchContext(MlFlowMemory memory, DateTime now) =>
        memory.SawWithin(MlScreen.InMatch, TimeSpan.FromSeconds(90), now) ||
        memory.SawWithin(MlScreen.MatchStartPage, TimeSpan.FromSeconds(90), now);

    /// <summary>
    /// 每轮决策快照（Debug 级）：界面分类 + 连续轮数 + 动作 + OCR 摘要。界面未变也输出——
    /// 此前分类只在切换时记一条、OCR 原文 5~30s 节流且最多 25 词，用户报「某时刻按错了」
    /// 时那一轮的判定依据大概率不在日志里（2026-10-15 关键比赛弹窗排查教训：远程定位全靠猜）。
    /// </summary>
    public static void DecisionSnapshot(Action<string, LogLevel> log, string module,
        object screen, int streak, MlAction action, OcrResult ocr)
    {
        var words = ocr.MergedWords.Count > 0
            ? string.Join("|", ocr.MergedWords.Take(6).Select(w => w.Text))
            : "(无文字)";
        log($"[{module}] 决策快照：{screen} streak={streak} 动作={action.Kind}:{action.Target} OCR[{words}]", LogLevel.Debug);
    }
}

using EfootballBot.Core.Vision;

// ===========================================================================
// 架构锁 · 共享层 Core/Bot/Match（v7.2）
// 本层职责：所有模块共用的能力 —— 界面分类、共享决策、赛中匹配 MatchPlay、流程记忆、像素探针、弹窗判定。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 本层不许 using / 引用 Core/Bot/Scenarios.*（共享层绝不能反向依赖模块）
//   · 场次记账（MatchesDone/KeyMatches）与比赛内挂机节奏只在本层实现，模块不许自己抄一份
// 新增共享能力：本层加文件；需要模块配合的用接口（如 IModuleLifecycle）反向交给模块。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Match;

/// <summary>
/// 各场景共用的小流程片段：标题画面宽限、比赛上下文判断、每轮决策快照。
/// 抽自多个场景逐字重复的副本。统一后基类（含国服我的联赛子类）也获得
/// 赛后 90s 宽限档——此前仅 30s，实机 2026-10-12 记录过全场结束后 33s 处
/// 品牌过场被按 X 打断弹窗链的事故。
/// </summary>
internal static class MatchShared
{
    /// <summary>
    /// 标题画面宽限：X 键动作落在「eFootball™+X」帧时，若最近有比赛流程上下文则多为
    /// 开赛加载/赛后品牌过场，先等待不按 X。刚见过终场（弹窗链后的品牌过场会连续
    /// 30s+）宽限 90s，其余 30s；真实标题画面（掉线/游戏重启）停留超宽限期后照常按 X。
    /// 调用方需在非 X 动作时清 <paramref name="titleSince"/>。返回 true = 本轮跳过（等 2000ms）。
    /// </summary>
    public static bool TitleGrace(MatchFlowMemory memory, DateTime now, ref DateTime? titleSince,
        Action<string, LogLevel> log, string module)
    {
        bool recentFlow =
            memory.SawWithin(MatchScreen.InMatch, TimeSpan.FromMinutes(3), now) ||
            memory.SawWithin(MatchScreen.MatchStartPage, TimeSpan.FromMinutes(3), now) ||
            memory.SawWithin(MatchScreen.LeaguePage, TimeSpan.FromMinutes(3), now);
        bool postMatchChain = memory.SawWithin(MatchScreen.FullTime, TimeSpan.FromMinutes(2), now);
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
    public static bool MatchContext(MatchFlowMemory memory, DateTime now) =>
        memory.SawWithin(MatchScreen.InMatch, TimeSpan.FromSeconds(90), now) ||
        memory.SawWithin(MatchScreen.MatchStartPage, TimeSpan.FromSeconds(90), now);
    /// <summary>掉线重登窗口：最近 ~100s 见过 eFootball 2027 标题页（BootSplash）或未登录页
    /// （NotLoggedIn，掉线重登链第二入口，点登录后同样接奖励弹窗链）。
    /// 门控重登后的弹窗链识别（登录奖励/收件箱等每周轮换页只认推进词；
    /// New Playing Style 网格页标题词唯一，有专用判定不占此门控）
    /// 与加载期 OCR 退化的标志帧兜底——不门控则正常流程里的同名词（联赛页「继续」等）被抢判。
    /// 各场景 Classify 的 loginRecovery 参数统一走这里。</summary>
    public static bool LoginRecovery(MatchFlowMemory memory, DateTime now) =>
        memory.SawWithin(MatchScreen.BootSplash, TimeSpan.FromSeconds(100), now) ||
        memory.SawWithin(MatchScreen.NotLoggedIn, TimeSpan.FromSeconds(100), now);
    /// <summary>
    /// 每轮决策快照（Debug 级）：界面分类 + 连续轮数 + 动作 + OCR 摘要。界面未变也输出——
    /// 此前分类只在切换时记一条、OCR 原文 5~30s 节流且最多 25 词，用户报「某时刻按错了」
    /// 时那一轮的判定依据大概率不在日志里（2026-10-15 关键比赛弹窗排查教训：远程定位全靠猜）。
    /// </summary>
    public static void DecisionSnapshot(Action<string, LogLevel> log, string module,
        object screen, int streak, MatchAction action, OcrResult ocr)
    {
        var words = ocr.MergedWords.Count > 0
            ? string.Join("|", ocr.MergedWords.Take(6).Select(w => w.Text))
            : "(无文字)";
        log($"[{module}] 决策快照：{screen} streak={streak} 动作={action.Kind}:{action.Target} OCR[{words}]", LogLevel.Debug);
    }
}





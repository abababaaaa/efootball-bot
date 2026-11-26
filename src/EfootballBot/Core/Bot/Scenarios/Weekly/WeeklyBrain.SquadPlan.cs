using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 模块层 · 周活动（Core/Bot/Scenarios）· WeeklyBrain 判定
// 本文件是 partial 拆分出来的「一个小活动」：只放它自己的界面判定 / 决策 / 子流程，
// 改这一个活动时不要动别的活动文件；共用逻辑留 `WeeklyBrain.cs`。
// 子活动分工见 docs/MODULES.md「周活动子活动分工」。
// 架构锁与其余约束同一份：不跨模块引用、场次记账与挂机节奏只走 MatchPlay。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.Weekly;

public static partial class WeeklyBrain
{
    // 本文件小活动：阵容挑战（Team Effort）—— 主页、完成态「活动已完成」、球员卡限制弹窗、
    // 阵容页侧边栏、比赛计划红点。

    // ---------------- 阵容挑战 / Team Effort（2026-11-19 用户实机三帧） ----------------
    /// <summary>
    /// 阵容挑战（Team Effort）主页判定：固定 UI 词三件套 —— 左上「前往比赛」+ 左列「赛程」
    /// 面板 + 「下一场比赛」标题行。活动名（Team Effort）/ 队徽 / 奖励文案每周轮换，
    /// 一律不作判定点（可变内容禁令）。
    /// 与 <see cref="WkScreen.EventHome"/> 的积分版主页互斥：那边有「主要奖励 / 总活动积分 /
    /// 胜场积分 / 球员加分」，这边一条都没有；两边共用「前往比赛」这一个按钮，靠后者区分。
    /// 「赛事等级」只在「下一场比赛」漏读时作替补（它同样印在主页右栏），不作必需项——
    /// 活动设置浮层里也有同名词，多认一条只会扩大误判面。
    /// 赛程打完的活动（<see cref="IsActivityCompleted"/>）主页**没有**「下一场比赛」行：
    /// 标题被换成「活动已完成 / 不要忘记从收件箱领取奖励哦。」，若不收这一态，整页会漏成
    /// WkScreen.None + MatchBrain.HasConfirm（底栏 B 返回的蓝框）→ 场景层按「弹窗」盲按 A，
    /// 在已完成的活动上反复按前往比赛（用户 2026-11-27 实机）。
    /// </summary>
    public static bool IsTeamEffortHome(OcrResult ocr)
        => ocr.ContainsJoined("前往比赛") && ocr.ContainsJoined("赛程") &&
           (ocr.ContainsJoined("下一场比赛") || ocr.ContainsJoined("赛事等级") ||
            IsActivityCompleted(ocr));

    /// <summary>
    /// 阵容挑战主页的完成态「活动已完成」（2026-11-27 用户国际服实机帧）：赛程三场全部打完、
    /// 三段奖励全勾，主页中部标题换成**固定文案**「活动已完成」+ 副标题
    /// 「不要忘记从收件箱领取奖励哦。」。此时游戏不再接受开赛（主页已无「下一场比赛」行），
    /// 用户规则是**判定完成就直接退出、不再开赛**（场景层拉黑槽位 → B 回活动列表换下一个）。
    /// 判据只用固定 UI 文案：活动名（Team Effort）/ 队徽 / 比分 / 奖励数值每周轮换，禁作判定点。
    /// 实机帧 OCR：「活动已完成」@(0.45,0.23)、「不要忘记从收件箱领取奖励哦。」@(0.45,0.27)。
    /// 标题漏读时用副标题的**连续片段**「收件箱领取奖励」兜底（拼接串无分隔，OCR 把副标题拆成
    /// 几个词也照样命中；不用「收件箱」+「领取奖励」两条独立判据——那会扩大到其它页面的邮件类文案）。
    /// 实机进度条虽也全绿，但绿条与金色光斑同屏，不作判据。
    /// </summary>
    public static bool IsActivityCompleted(OcrResult ocr)
        => ocr.ContainsJoined("活动已完成") || ocr.ContainsJoined("收件箱领取奖励");

    /// <summary>
    /// 阵容挑战入场弹窗「球员卡限制 / OK」判定（2026-11-19 用户实机帧）：标题行固定文案
    /// 「球员卡限制」；标题被 OCR 漏读时用固定副标题「将无法在后续比赛中使用」+ 屏中部 OK
    /// 按钮兜底（OK 限 y&lt;0.85：底栏「B 返回」蓝框会被同款词/框吃掉，不算弹窗证据）。
    /// 球员卡名 / 胜场数 / 队名都是可变内容，一律不作判定点。
    /// </summary>
    public static bool IsPlayerCardRestrictPopup(OcrResult ocr)
        => ocr.ContainsJoined("球员卡限制") ||
           (ocr.ContainsJoined("将无法在后续比赛中使用") &&
            ocr.FindButton("OK") is { } ok && ok.CenterY < 0.85);

    // ---------------- 比赛计划失效修复（2026-11-22 用户实机六帧） ----------------
    /// <summary>
    /// 赛后「无法进行比赛」弹窗（标题「无法进行比赛」+ 副标题「比赛计划中包含一张不能在后续
    /// 比赛中使用的球员卡。」+ 单枚 OK）：标题行固定文案；标题被 OCR 漏读时用固定副标题片段
    /// 「不能在后续比赛中使用」+ 屏中部 OK 兜底（OK 限 y&lt;0.85，底栏「B 返回」蓝框同款不算）。
    /// 球卡名 / 比分 / 队名是可变内容，一律不作判定点。
    /// 「将无法在后续比赛中使用」（入场弹窗）与本句前缀不同，两者互不吃。
    /// </summary>
    public static bool IsMatchPlanInvalidPopup(OcrResult ocr)
        => ocr.ContainsJoined("无法进行比赛") ||
           (ocr.ContainsJoined("不能在后续比赛中使用") &&
            ocr.FindButton("OK") is { } ok && ok.CenterY < 0.85);

    /// <summary>
    /// 比赛计划编辑页 / 自动配置选项弹窗是否要「接管」进修复子流程（2026-11-22 用户实测回归）：
    /// 只有还没进 <c>MatchPlanSetup</c> 时才接管；已经在子流程里必须把这一帧交回 PlanStep
    /// 状态机。旧写法无条件接管并立刻 return，等于每一帧都把子步骤重置成 Lineup，手柄一个
    /// 方向键都发不出去、屏幕永远停在编辑页（用户实测：卡一整天，不会向左进侧边栏，
    /// 日志每秒刷一行「接管比赛计划页」）。
    /// </summary>
    public static bool ShouldTakeOverPlanEditor(bool planEditorOnScreen, bool alreadyFixingPlan)
        => planEditorOnScreen && !alreadyFixingPlan;

    /// <summary>左列侧边栏固定项「自动配置球员」（x&lt;0.30 限定在侧边栏列，排除正文同名词）。</summary>
    private static bool HasSquadSidebar(OcrResult ocr)
        => ocr.Words.Any(w => w.CenterX < 0.30 && w.NormText.Contains("自动配置球员"));

    /// <summary>
    /// 比赛计划编辑页（主页底栏 Y「比赛计划」打开的阵容页）：左上侧边栏「自动配置球员」+
    /// 底栏「实时更新评价 / 球员详细数据」任一（两枚都是固定 UI 文案，球员名不作判定点）。
    /// 该页没有「前往比赛 / 赛程」，与阵容挑战主页互不抢判。
    /// </summary>
    public static bool IsSquadLineupEdit(OcrResult ocr)
        => HasSquadSidebar(ocr) &&
           (ocr.ContainsJoined("实时更新评价") || ocr.ContainsJoined("球员详细数据"));

    /// <summary>
    /// 「自动配置球员」选项弹窗：编辑页背后侧边栏仍在，屏中部出现选项词「重视」（实机选项
    /// 「重视能力值」）。用户规则是下拉光标选「重视能力值」再点下一步——本判定只负责认出这层，
    /// 光标怎么走由场景层计步（见 WeeklyEventScenario.HandlePlanOptionsAsync）。
    /// </summary>
    public static bool IsAutoConfigOptionsPopup(OcrResult ocr)
        => HasSquadSidebar(ocr) && ocr.Words.Any(w => w.NormText.Contains("重视"));

    /// <summary>
    /// 底栏「Y 比赛计划」按钮右上角的红色叹号徽章（比赛计划里有球员卡失效，2026-11-22 用户实机）：
    /// 命中 = 必须先 Y 进比赛计划修阵容，否则按「前往比赛」只会弹「无法进行比赛」，
    /// 整夜「弹窗 → 前往比赛 → 弹窗」零场次。
    /// 判据：底栏读到「比赛计划」按钮词（证明在活动主页版式上）+ 该按钮右上角区域红像素计数。
    /// 实机 1254×720 帧：徽章区（x 0.700~0.800、y 0.865~0.965）换算红像素≈198；
    /// 干净主页同位 0（该区最大 R=41）；弹窗压暗帧同位≈180（徽章 R 从 255 掉到 112）。
    /// 阈值 60：容得下压暗，也吃不到主页右侧的金色光斑。
    /// </summary>
    public static bool DetectPlanBadge(FrameData frame, OcrResult ocr)
    {
        if (!ocr.Words.Any(w => w.CenterY > 0.85 && w.NormText.Contains("比赛计划"))) return false;
        return RegionProbe.CountRedRef(frame, 0.700, 0.800, 0.865, 0.965) >= 60;
    }

    /// <summary>
    /// 比赛计划编辑页左列侧边栏当前聚焦项（光标所在行）。null = 焦点不在侧边栏
    /// （还在阵容板上，例如 GK 的蓝色光标框）或探针读不出——调用方先左移归位。
    /// 像素判据与介绍页按钮同款：聚焦行是白底圆角块，未聚焦是深色涂层；
    /// 只用固定 UI 词与行底色，不认球员名（可变内容禁令）。
    /// </summary>
    public static string? DetectFocusedSidebarItem(FrameData frame, OcrResult ocr)
    {
        string? best = null;
        int bestLuma = 0, secondLuma = 0;
        foreach (var item in SquadSidebarItems)
        {
            var word = ocr.Words
                .Where(w => w.CenterX < 0.30 && w.NormText.Contains(item))
                .OrderByDescending(w => w.H)
                .FirstOrDefault();
            if (word is null) continue;
            int luma = SidebarBandLuma(frame, word);
            if (luma > bestLuma) { secondLuma = bestLuma; bestLuma = luma; best = item; }
            else if (luma > secondLuma) secondLuma = luma;
        }
        if (bestLuma < SidebarFocusedLuma) return null;
        if (bestLuma - secondLuma < SidebarFocusedLumaGap) return null;
        return best;
    }

    /// <summary>侧边栏行底色亮度：以该词为中心取一条横带（文字左右各 1.5% 宽、上下各 0.6 行高）
    /// 的中位亮度——聚焦行是白条（≈235），未聚焦是深色涂层（≈47）。</summary>
    private static int SidebarBandLuma(FrameData frame, OcrWord w)
    {
        const double padX = 0.015;
        double padY = Math.Max(0.010, w.H * 0.6);
        double x0 = Math.Clamp(w.X - padX, 0, 1), x1 = Math.Clamp(w.X + w.W + padX, 0, 1);
        double y0 = Math.Clamp(w.CenterY - padY, 0, 1), y1 = Math.Clamp(w.CenterY + padY, 0, 1);
        return RegionProbe.MedianLuma(frame, x0, x1, y0, y1);
    }
}

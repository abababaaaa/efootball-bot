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
    // 本文件小活动：进活动入口 —— 介绍页「进入/详情」焦点、比赛页磁贴扫描、锦标赛页/建议面板。

    /// <summary>
    /// 比赛页（大活动页）磁贴行里「活动」磁贴是第几格（0 起）。
    /// 用户 2026-11-13 实机：大活动页第一格已改成「锦标赛」（Round 3 Ranking Event /
    /// 参加锦标赛形式的活动），活动板块退到第二格——旧逻辑「归位最左后从第 1 格开始扫」
    /// 必然先按进锦标赛，随后被当成活动介绍页，整夜在「活动简介 / 详情说明面板」里打转
    /// （反馈包 20261112_233710：23:33 起同一条警告每 2~3 秒一轮、零场次）。
    /// 判定只用磁贴标题词本身：y 落在卡片下半部、按 x 排序取「活动」的序号；
    /// 描述句「参加活动以获得丰厚奖励!」不是整词，不参与排序。
    /// 磁贴行读不全（OCR 漏读/只读到一枚）返回 null，调用方退回原来的逐格扫描。
    /// </summary>
    public static int? FindActivityTileOffset(OcrResult ocr)
        => HubTileLocator.FindTileOffset(ocr, "活动");

    /// <summary>
    /// A 落在大活动页第一格「锦标赛」磁贴上时打开的页面（不是活动介绍页）：
    /// 页内保留比赛 hub 的固定描述句「参加锦标赛形制的活动」（实机 OCR 原文），
    /// 或锦标赛子页的「自定义锦标赛」。
    /// 命中即判定误入，场景层 B 退回比赛页改试下一格——绝不在这页按 A（会一路点开
    /// 「活动简介 → 详情说明面板」，正是反馈包 20261112_233710 里整夜打转的那条链）。
    /// </summary>
    public static bool IsTournamentPage(OcrResult ocr)
        // 实机 OCR 读到的是「参加锦标赛形制的活动。」（形制/形式都可能被 OCR 认错），
        // 故只认「参加锦标赛」这半句固定文案，后半句不作判据。
        => ocr.ContainsJoined("参加锦标赛")
           || (ocr.ContainsJoined("自定义锦标赛") &&
               ocr.Words.Any(w => w.CenterY < 0.25 && w.NormText.Contains("锦标赛")));

    /// <summary>
    /// 「建议」面板是否盖在活动主页上。只用固定 UI 文案，建议正文/日期/球员名是可变内容，不作判定点：
    /// 列表视图＝标题「建议列表」或副标题「显示最近5场比赛的建议。」；
    /// 详情视图＝右上角「关闭」标题（y&lt;0.16，主页没有该词）+ 左列「建议」/分析师署名。
    /// </summary>
    public static bool IsAdvicePanel(OcrResult ocr)
    {
        if (ocr.ContainsJoined("建议列表") || ocr.ContainsJoined("场比赛的建议")) return true;
        bool closeHeader = ocr.Words.Any(w => w.CenterY < 0.16 && w.NormText.Contains("关闭"));
        return closeHeader && (ocr.ContainsJoined("分析师") || ocr.ContainsJoined("建议"));
    }

    // ---------------- 介绍页按钮聚焦（2026-11-09 国际服实机反馈） ----------------
    /// <summary>活动介绍页（进入/详情 纵排按钮）当前焦点落在哪个按钮上。</summary>
    public enum WkIntroFocus
    {
        /// <summary>探针读不出（OCR 漏读按钮词 / 帧过暗）——调用方按「默认光标在进入」保守处理。</summary>
        Unknown,
        /// <summary>焦点在「进入」（白底高亮），A 就是进活动，不需要任何方向键。</summary>
        Enter,
        /// <summary>焦点在「详情」——此时直接 A 只会打开说明面板，必须先上移一格。</summary>
        Detail,
    }

    /// <summary>介绍页这一轮该按什么。</summary>
    public enum WkIntroAction
    {
        /// <summary>直接 A（不按任何方向键）。</summary>
        PressEnter,
        /// <summary>上×1 归位到「进入」后再 A。</summary>
        UpThenEnter,
    }

    /// <summary>
    /// 活动介绍页（「巡回活动 / 挑战活动」大图 + 纵排「进入 / 详情」两枚按钮）的焦点检测。
    /// 用户 2026-11-09 国际服实机：进介绍页时光标默认在「进入」，直接按 A（下一步）即可，
    /// 页面不该有任何方向键——但游戏会把上一页的光标位置记忆下来（点过「详情」后光标停在
    /// 详情上），此时按 A 只会打开灰色说明面板，面板里没有任何能推进的按钮，旧逻辑要干等
    /// 15 帧（约 30s）才 B 退，退回来光标还在详情上 → 整夜在介绍页打转（实机日志
    /// 2026-11-09 23:35~23:48：EventIntro ↔ LeagueInfoPage 每 30s 一轮，零场次）。
    /// 因此先读像素判断焦点：白底高亮 = 聚焦行。
    /// 只认按钮词的位置与按钮底色，不用活动名/图片（可变内容禁令）。
    /// </summary>
    public static WkIntroFocus DetectFocusedIntroButton(FrameData frame, OcrResult ocr)
    {
        var enter = IntroButtonWord(ocr, "进入");
        var detail = IntroButtonWord(ocr, "详情");
        if (enter is null || detail is null) return WkIntroFocus.Unknown;
        int lumaEnter = IntroButtonBandLuma(frame, enter);
        int lumaDetail = IntroButtonBandLuma(frame, detail);
        bool enterLit = lumaEnter >= IntroFocusedLuma;
        bool detailLit = lumaDetail >= IntroFocusedLuma;
        if (enterLit && !detailLit) return WkIntroFocus.Enter;
        if (detailLit && !enterLit) return WkIntroFocus.Detail;
        if (Math.Abs(lumaEnter - lumaDetail) >= IntroFocusedLumaGap)
            return lumaEnter > lumaDetail ? WkIntroFocus.Enter : WkIntroFocus.Detail;
        return WkIntroFocus.Unknown;
    }

    /// <summary>
    /// 介绍页推进决策（纯函数，单测锁定）：焦点在「详情」→ 先上移归位；焦点在「进入」→
    /// 直接 A，一个方向键都不按（用户 2026-11-09 规则）；探针读不出 → 先按用户默认（直接 A）。
    /// 自愈兜底：连续 <paramref name="failedEnters"/> 次都没推进（&gt;=IntroEnterTriesBeforeNudge）
    /// 时，不再相信探针，改上移归位一次——绝不对同一页无限重复同一个无效动作
    /// （探针误读成「进入」而光标实际停在「详情」时，靠这一条脱困）。
    /// </summary>
    public static WkIntroAction DecideIntroEntry(WkIntroFocus focus, int failedEnters)
    {
        if (failedEnters >= IntroEnterTriesBeforeNudge) return WkIntroAction.UpThenEnter;
        if (focus == WkIntroFocus.Detail) return WkIntroAction.UpThenEnter;
        return WkIntroAction.PressEnter;
    }

    /// <summary>
    /// 介绍页「详情」说明面板判定：浅色文档页，右上角只有「关闭」提示，没有「进入」按钮
    /// （进入/详情的介绍页本体恒有「进入」）。命中即 B 当场关闭，不在该页干等预算。
    /// </summary>
    public static bool IsIntroDetailPanel(OcrResult ocr)
        => ocr.Words.Count >= 8
           && !ocr.Words.Any(w => w.NormText.Contains("进入"))
           && ocr.Words.Any(w => w.CenterY < 0.16 && w.NormText.Contains("关闭"));

    /// <summary>介绍页按钮词：取纵向按钮区（y&gt;0.55）里最像按钮的那枚（字高最大）。</summary>
    private static OcrWord? IntroButtonWord(OcrResult ocr, string keyword)
        => ocr.Words.Concat(ocr.MergedWords)
            .Where(w => w.CenterY > 0.55 && w.CenterX < 0.55 && w.NormText.Contains(keyword))
            .OrderByDescending(w => w.H)
            .FirstOrDefault();

    /// <summary>
    /// 按钮底色亮度（中位亮度）：以按钮文字为中心取一条横向亮带（文字左右各 5% 宽，
    /// 上下各半个字高）——聚焦按钮是白底（中位亮度 ~242），未聚焦是深色半透明（~49）。
    /// 区域偏窄是为了不把按钮外的球场背景/相邻按钮圈进来。
    /// </summary>
    private static int IntroButtonBandLuma(FrameData frame, OcrWord w)
    {
        const double padX = 0.05;
        double padY = w.H * 0.5;
        double x0 = Math.Clamp(w.X - padX, 0, 1), x1 = Math.Clamp(w.X + w.W + padX, 0, 1);
        double y0 = Math.Clamp(w.Y - padY, 0, 1), y1 = Math.Clamp(w.Y + w.H + padY, 0, 1);
        return RegionProbe.MedianLuma(frame, x0, x1, y0, y1);
    }
}

using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot.Scenarios.Weekly;

/// <summary>周活动菜单侧界面（WeeklyBrain 判定）。None = 非周活动界面，交给 MyLeagueBrain 分类。</summary>
public enum WkScreen
{
    /// <summary>非周活动界面（主菜单/比赛中/共用弹窗等）。</summary>
    None,
    /// <summary>活动列表页（对阵AI / 对阵玩家 两个页签）。</summary>
    EventsList,
    /// <summary>活动介绍页（巡回活动大图，光标默认在「进入」）。</summary>
    EventIntro,
    /// <summary>进入活动时的弹窗（「设置专用比赛计划」等，光标默认在确认）。</summary>
    EntryPopup,
    /// <summary>活动主页（左上「前往比赛」+ 右栏活动积分/主要奖励进度）。</summary>
    EventHome,
    /// <summary>活动设置浮层（按 X 呼出，右半屏：赛事等级/自动控制/弹性自动控制）。</summary>
    EventSettings,
}

/// <summary>活动设置浮层的解析结果。null = 该项 OCR 未读出（保守：读不到就不动）。</summary>
public sealed record WkActivitySettings(string? Level, bool? AutoControl, bool? FlexibleAutoControl)
{
    public bool AllReadable => Level is not null && AutoControl is not null && FlexibleAutoControl is not null;
}

/// <summary>
/// 周活动决策大脑：菜单侧界面分类 + 活动设置解析 + 完成态探针。纯函数，可单测。
/// 判定只用固定 UI 词（对阵AI/前往比赛/活动设置/自动控制…）与固定几何区；
/// 活动名/卡片图片/积分数字每周轮换，一律不作判定点（可变内容禁令）。
/// 比赛中与共用界面（InMatch/FullTime/弹窗推进等）全部复用 MyLeagueBrain。
/// </summary>
public static class WeeklyBrain
{
    // ---------------- 界面分类 ----------------

    /// <summary>
    /// 周活动界面分类。先于 MyLeagueBrain.Classify 执行——活动主页也含「前往比赛」，
    /// 不先判会被误归 LeaguePage（与关键比赛弹窗被 MatchHub 兜底抢判同一坑）。
    /// 判定顺序：盖层弹窗 → 活动设置 → 入场弹窗 → 活动主页 → 活动列表 → 活动介绍。
    /// </summary>
    public static WkScreen Classify(IReadOnlyList<YoloDetection> dets, OcrResult ocr,
        bool topRightHasContent = false, bool matchContext = false, bool mostlyLightPage = false)
    {
        // 盖层弹窗探测（赛后结算/奖励领取等多步弹窗，用户 2026-10-12 实机教训）：
        // 主页词透过暗化背景仍会被 OCR 读到，误判 EventHome 后按 X/前往比赛全部无效
        //（弹窗挡着，被误判"设置打不开/开赛未生效"）。弹窗自带推进词或确认按钮，
        // 而纯主页没有（前往比赛不被 YOLO 判 btn_confirm、底栏无 OK/下一步/确认，实测帧验证）。
        // 命中 → 一律先按 A 清弹窗，清完才允许开始「已回到主页」的后续判断。
        //「OK」按整词精确匹配（FindButton），防英文球员名/卡面词含 "ok" 子串误触发。
        // YOLO btn_confirm 只认屏幕中部（y<0.85）：主页底栏「B 返回」的蓝色高亮框会被
        // 判成 btn_confirm（实机 Titani 帧 0.91 @(0.12,0.93)），把干净主页误门控成
        // EntryPopup → A 在主页上=前往比赛直接开下一场（实机：终场后 8s 进下一场、
        // 完成态从未被评估）。弹窗按钮都在屏幕中部，底栏确认框不算弹窗证据。
        bool popupOnTop =
            ocr.ContainsJoined("下一步") || ocr.ContainsJoined("确认") ||
            ocr.FindButton("OK") is not null ||
            dets.Any(d => d.ClassName == "btn_confirm" && d.Score >= MyLeagueBrain.MinScore && d.CenterY < 0.85);
        bool settingsTitle = ocr.Words.Any(w => w.CenterY < 0.2 && w.Text.Replace(" ", "").Contains("活动设置"));
        bool eventHomeBody = ocr.ContainsJoined("前往比赛") &&
            (ocr.ContainsJoined("总活动积分") || ocr.ContainsJoined("胜场积分") ||
             ocr.ContainsJoined("球员加分") || ocr.ContainsJoined("主要奖励"));
        bool eventsListTabs = ocr.ContainsJoined("对阵AI") || ocr.ContainsJoined("对阵玩家");
        bool introMark = ocr.ContainsJoined("巡回活动") ||
            (ocr.ContainsJoined("进入") && ocr.ContainsJoined("详情"));
        // 介绍页不参与弹窗门控：其白色「进入」按钮本身会被 YOLO 判 btn_confirm，
        // 而介绍页无论有无弹窗按 A 都是正确动作（光标默认在进入）
        if (popupOnTop && (settingsTitle || eventHomeBody || eventsListTabs))
            return WkScreen.EntryPopup;

        // 活动设置浮层（按 X 呼出，盖在活动主页右半屏）：标题「活动设置」在顶部（y<0.2）。
        // 必须先于活动主页——浮层打开时「前往比赛」仍在左侧可见。
        // 【实测坑】活动主页底栏有「X 活动设置」按钮词（y≈0.93），不限 y 会把主页误判成设置浮层
        if (settingsTitle)
            return WkScreen.EventSettings;

        // 入场弹窗（「设置专用比赛计划」+ OK；可能连续多枚）：先于活动主页——
        // 弹窗背后「前往比赛」仍可见，不先判会被 EventHome 抢走漏点确认
        if (ocr.ContainsJoined("专用比赛计划"))
            return WkScreen.EntryPopup;

        // 活动主页：左上「前往比赛」+ 右栏固定词（总活动积分/胜场积分/球员加分/主要奖励）。
        // 我的联赛主页没有这组词，与 LeaguePage 区分开
        if (eventHomeBody)
            return WkScreen.EventHome;

        // 活动列表页：顶部「对阵AI / 对阵玩家」页签（进入比赛 hub 的活动板块后默认页签为对阵AI）
        if (eventsListTabs)
            return WkScreen.EventsList;

        // 活动介绍页（巡回活动大图）：「巡回活动」标签，或「进入」+「详情」按钮同屏
        //（列表卡只有「进入」条，无「详情」，不冲突）
        if (introMark)
            return WkScreen.EventIntro;

        return WkScreen.None;
    }

    // ---------------- 活动设置解析 ----------------

    /// <summary>在右半屏（x&gt;0.45）找标签词下方的值词（值在标签下一行，y 偏移 0.015~0.13）。</summary>
    private static OcrWord? ValueBelow(OcrResult ocr, double labelY, Func<string, bool> match)
        => ocr.Words.FirstOrDefault(w =>
            w.CenterX > 0.45 && w.CenterY > labelY + 0.015 && w.CenterY < labelY + 0.13 &&
            match(w.Text.Replace(" ", "")));

    /// <summary>
    /// 解析活动设置浮层当前值：赛事等级（难度词）/ 自动控制（开/关）/ 弹性自动控制（开/关）。
    /// 任何一项读不到返回 null——调用方保守处理（绝不盲按，防误翻转）。
    /// </summary>
    public static WkActivitySettings ParseActivitySettings(OcrResult ocr)
    {
        // 「弹性自动控制」包含「自动控制」子串，先匹配弹性行再匹配普通行，普通行要求整行就是「自动控制」
        var flexLabel = ocr.Lines.FirstOrDefault(l => l.Text.Replace(" ", "").Contains("弹性"));
        var autoLabel = ocr.Lines.FirstOrDefault(l => l.Text.Replace(" ", "") == "自动控制");
        var levelLabel = ocr.Lines.FirstOrDefault(l => l.Text.Replace(" ", "").Contains("赛事等级"));

        string? level = null;
        if (levelLabel is not null)
        {
            var v = ValueBelow(ocr, levelLabel.CenterY, t => MyLeagueBrain.Difficulties.Any(d => t.Contains(d)));
            if (v is not null)
                level = MyLeagueBrain.Difficulties.First(d => v.Text.Replace(" ", "").Contains(d));
        }

        bool? ParseSwitch(OcrLine? label)
        {
            if (label is null) return null;
            var v = ValueBelow(ocr, label.CenterY, t => t == "开" || t == "关");
            return v is null ? null : v.Text.Replace(" ", "") == "开";
        }

        return new WkActivitySettings(level, ParseSwitch(autoLabel), ParseSwitch(flexLabel));
    }

    // ---------------- 完成态探针（2026-10-13 用户实机截图校准） ----------------

    /// <summary>活动卡右缘 x 坐标（槽位 0~3；实机 weekly_list_greencheck 帧：卡1 右缘≈0.351）。
    /// 第 4 卡大部分在屏幕外，勾区采样不到自然返回 false（进入后由主页完成态判定兜底）。</summary>
    private static readonly double[] CardRightEdges = { 0.352, 0.643, 0.946, 1.24 };

    /// <summary>区域内绿色像素计数（绿底白勾：G&gt;100 且显著高于 R/B；2px 步进采样）。</summary>
    private static int CountGreen(FrameData f, double x0, double x1, double y0, double y1)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return 0;
        int count = 0;
        for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
            for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
            {
                int i = (y * w + x) * 4;
                if (bgra[i + 1] > 100 && bgra[i + 1] > bgra[i + 2] + 40 && bgra[i + 1] > bgra[i] + 40) count++;
            }
        return count;
    }

    /// <summary>
    /// 活动列表卡右上角绿色勾（该活动主要奖励已完成，不进入）：勾形徽章固定在卡右上角。
    /// 实机 weekly_list_greencheck 帧：卡1 勾区 157 绿像素，未完成卡 0——阈值 50。
    /// </summary>
    public static bool DetectGreenCheck(FrameData frame, int slot)
    {
        if (slot < 0 || slot >= CardRightEdges.Length) return false;
        double right = CardRightEdges[slot];
        return CountGreen(frame, right - 0.065, right - 0.005, 0.150, 0.220) >= 50;
    }

    /// <summary>
    /// 活动主页右栏「主要奖励已获得」：
    /// ① OCR「获得的主要奖励」（完成态标题；进行中是「离获得主要奖励还差…」，无「的」字，
    ///   子串天然不冲突）；
    /// ② 备份：右栏主要奖励行的绿色勾（实机 weekly_reward_done 帧：勾区 100 绿像素；
    ///   注意别把左侧代币图标圈进来——图标在 x≈0.70，勾在 x≈0.94）。
    /// </summary>
    public static bool IsMainRewardDone(OcrResult ocr, FrameData frame)
        => ocr.ContainsJoined("获得的主要奖励") ||
           CountGreen(frame, 0.920, 0.965, 0.635, 0.715) >= 50;

    /// <summary>右栏是否显示「离获得主要奖励还差…」（进行中，可继续刷）——仅用于日志与守卫。</summary>
    public static bool MainRewardPending(OcrResult ocr) => ocr.ContainsJoined("离获得主要奖励");

    /// <summary>难度档位（与我的联赛「选择赛事等级」共用一套词，周活动赛事等级下拉一致）。</summary>
    public static string[] Levels => MyLeagueBrain.Difficulties;
}

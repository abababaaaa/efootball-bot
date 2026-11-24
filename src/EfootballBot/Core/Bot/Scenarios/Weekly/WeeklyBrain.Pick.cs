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
    // 本文件小活动：单场周活动的选页 —— 赛事等级 / 难度档 / 选择地区 / 选择球队 / Y 随机。

    /// <summary>
    /// 单场周活动「选择赛事等级」整页判定：页首（y&lt;0.18）大标题「选择赛事等级」+
    /// 左列（x&lt;0.25）至少 2 个难度名。右栏同句「请选择赛事等级」在 y≈0.31、弹窗版在
    /// y≈0.40，都不入 y&lt;0.18 标题带，不冲突（实机三帧校准：标题 @(0.18,0.11)）。
    /// 标题被 OCR 漏读/遮挡（issue #1 手机照：GUI 覆盖层盖住列表）时本判定落空，
    /// 由共享分类兜成 MatchScreen.DifficultyPage——场景层对两个信号都接管。
    /// </summary>
    public static bool IsEventDifficultyPage(OcrResult ocr) =>
        ocr.Words.Any(w => w.CenterY < 0.18 && w.Text.Replace(" ", "").Contains("选择赛事等级")) &&
        MatchBrain.Difficulties.Count(d => ocr.Words.Any(w =>
            w.CenterX < 0.25 && w.Text.Replace(" ", "").Contains(d))) >= 2;

    // ---------------- 球队选择前置流程（2026-11-08 用户四张实机帧） ----------------
    /// <summary>
    /// 球队确认弹窗「要使用&lt;队名&gt;吗？」（选好球队按 A/进入后弹出）：
    /// 副标题「活动期间，可在[活动设置] &gt; [选择的球队]更改选择的球队。」
    /// 上半句「要使用」本身是固定 UI 文案，队名每周变不作判定点。判据取「要使用 + 取消」同屏
    /// （弹窗恒带取消键）或副标题「更改选择的球队」——单认「要使用」会被别处同词文案误吃。
    /// </summary>
    public static bool IsTeamConfirmPopup(OcrResult ocr) =>
        ocr.ContainsJoined("更改选择的球队") ||
        (ocr.ContainsJoined("要使用") && ocr.ContainsJoined("取消"));

    /// <summary>
    /// 「选择地区」页（球队选择前置流程第一步）：右侧提示词「选择地区」+ 底栏「Y 随机」胶囊。
    /// 两个条件都要——只认提示词会被别处同名文案误触发，只认「随机」会被任何带随机按钮的页吃掉。
    /// </summary>
    public static bool IsRegionSelectPage(OcrResult ocr) =>
        HasRandomChip(ocr) && ocr.Words.Any(w => w.NormText.Contains("选择地区"));

    /// <summary>
    /// 「选择球队」页（同流程第二步）：右侧固定框提示词「选择球队」+ 底栏「Y 随机」。
    /// 该框在用户描述里恒在（右侧一定有这个框），左侧国旗网格/球队名每轮变，一律不作判定点。
    /// </summary>
    public static bool IsTeamSelectPage(OcrResult ocr) =>
        HasRandomChip(ocr) && ocr.Words.Any(w => w.NormText.Contains("选择球队"));

    /// <summary>底栏「Y 随机」胶囊：词「随机」落在屏幕底部（y&gt;0.85，实机 y≈0.94）。</summary>
    private static bool HasRandomChip(OcrResult ocr) =>
        ocr.Words.Any(w => w.CenterY > 0.85 && w.NormText.Contains("随机"));

    /// <summary>选择页（选择地区 / 选择球队）每轮的动作。</summary>
    public enum WkPickAction
    {
        /// <summary>本页还没随机过：按一次 Y（整页只按这一次）。</summary>
        PressRandom,
        /// <summary>随机刚按完：等过渡帧。</summary>
        Wait,
        /// <summary>随机已按过、页面没动：该页没有确认键，A 就是「下一步」。</summary>
        PressNext,
        /// <summary>多次下一步仍不动：B 退出该子流程。</summary>
        Escape,
        /// <summary>B 也退不出去：停止挂机。</summary>
        Stop,
    }

    /// <summary>
    /// 选择页推进决策（纯函数，单测锁定）。用户 2026-11-08 实机规则：
    /// 「随机点一次就行了，不用来回点；没有确认键，点完一次随机就可以点下一步了」。
    /// randomPressed=本页是否已按过 Y；samePageFrames=按完随机后同页已观测帧数；
    /// nextAttempts=本页已按过的「下一步」次数；escapes=子流程内已用的 B 退出次数。
    /// </summary>
    public static WkPickAction DecidePickStep(bool randomPressed, int samePageFrames, int nextAttempts, int escapes)
    {
        if (!randomPressed) return WkPickAction.PressRandom;
        if (samePageFrames < PickConfirmGraceFrames) return WkPickAction.Wait;
        if (nextAttempts < MaxPickNextAttempts) return WkPickAction.PressNext;
        return escapes < MaxPickEscapes ? WkPickAction.Escape : WkPickAction.Stop;
    }

    /// <summary>
    /// 检测难度列表当前聚焦行（光标）：左列难度词行带（词左 -0.28 ~ 词右 +0.06、y±0.035）
    /// 的亮像素（lum&gt;200）计数——白底高亮行 ≈1283/1285，未聚焦行 29~81，说明弹窗盖着时
    /// 全 0（实机 2026-10-30 三帧校准，阈值 400）。返回 null = 未检出（弹窗遮挡/探针失效）。
    /// </summary>
    public static string? DetectFocusedDifficulty(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;
        string? best = null;
        int bestBright = 0;
        foreach (var diff in MatchBrain.Difficulties)
        {
            var word = ocr.Words.FirstOrDefault(x =>
                x.CenterX < 0.25 && x.Text.Replace(" ", "").Contains(diff));
            if (word is null) continue;
            int bright = 0;
            double x0 = Math.Max(0, word.CenterX - word.W / 2 - 0.28);
            double x1 = Math.Min(1, word.CenterX + word.W / 2 + 0.06);
            for (int y = (int)((word.CenterY - 0.035) * h); y < (int)((word.CenterY + 0.035) * h); y += 2)
            {
                if (y < 0 || y >= h) continue;
                for (int x = (int)(x0 * w); x < (int)(x1 * w); x += 2)
                {
                    int i = (y * w + x) * 4;
                    if (((bgra[i + 2] + bgra[i + 1] + bgra[i]) / 3) > 200) bright++;
                }
            }
            if (bright > bestBright) { bestBright = bright; best = diff; }
        }
        return bestBright >= 400 ? best : null;
    }

    /// <summary>
    /// 活动设置浮层「赛事等级」下拉的当前勾选（蓝勾）——与难度页蓝勾位置不同：
    /// 实机 2026-11-23 下拉帧（1280×759）蓝勾在**面板右缘 x≈0.914~0.929**，而非难度词旁
    /// （MyLeagueBrain.DetectSelectedDifficulty 只扫词 ±0.06 窗口，此处返回 null → 误判"读不出"
    /// 而直接确认、改选从未生效，正是"赛事等级设置未成功"的根因）。做法：对每个难度词的 y 带
    /// 扫 x 0.85~0.98 的蓝像素（B&gt;120 且高于 R/G 40+），最多者即勾选行。返回 null=未检出。
    /// </summary>
    public static string? DetectDropdownCheckedLevel(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;
        // 下拉选项行：难度词 x≈0.56（右侧面板），顶部选择框的"新手"也在 x≈0.56 但 y 更靠上；
        // 蓝勾只出现在下拉列表行（y 0.34~0.65），逐个难度词 y 带扫右缘即可。
        string? best = null;
        int bestBlue = 0;
        foreach (var diff in MatchBrain.Difficulties)
        {
            // 取该难度词在下拉列表区的匹配（y 0.32~0.66），避开顶部选择框的同名回显
            var word = ocr.Words.FirstOrDefault(x =>
                x.CenterX is > 0.45 and < 0.70 && x.CenterY is > 0.32 and < 0.66 &&
                x.Text.Replace(" ", "").Contains(diff));
            if (word is null) continue;
            int blue = 0;
            int y0 = (int)((word.CenterY - 0.020) * h), y1 = (int)((word.CenterY + 0.020) * h);
            int xa = (int)(0.85 * w), xb = Math.Min(w, (int)(0.98 * w));
            for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y += 1)
                for (int x = xa; x < xb; x += 1)
                {
                    int i = (y * w + x) * 4;
                    if (bgra[i] > 120 && bgra[i] > bgra[i + 1] + 40 && bgra[i] > bgra[i + 2] + 40)
                        blue++;
                }
            if (blue > bestBlue) { bestBlue = blue; best = diff; }
        }
        return bestBlue >= 20 ? best : null;
    }

    private const double DropdownRowStepY = 0.0425;

    /// <summary>
    /// 在右侧活动设置下拉区域（x 0.45~0.80, y 0.31~0.67）内按从上到下顺序识别难度选项。
    /// </summary>
    public static IReadOnlyList<string> DetectDropdownOptions(OcrResult ocr)
    {
        var hits = new List<(string Diff, double Y)>();
        foreach (var diff in MatchBrain.Difficulties)
        {
            var word = ocr.Words.FirstOrDefault(x =>
                x.CenterX is > 0.45 and < 0.80 && x.CenterY is > 0.31 and < 0.67 &&
                x.Text.Replace(" ", "").Contains(diff, StringComparison.Ordinal));
            if (word is not null) hits.Add((diff, word.CenterY));
        }
        return hits.OrderBy(h => h.Y).Select(h => h.Diff).ToList();
    }

    /// <summary>
    /// 活动设置「赛事等级」下拉列表是否已展开（右半屏下拉纵带出现 ≥3 个难度项）。
    /// 实机展开下时顶部仍保留「活动设置」标题，因此不能依赖 MatchBrain 分类；必须用本探针直接判。
    /// </summary>
    public static bool IsEventSettingsDropdown(OcrResult ocr)
        => DetectDropdownOptions(ocr).Count >= 3;

    /// <summary>
    /// 检测活动设置「赛事等级」下拉列表当前白底高亮的聚焦行（实机聚焦行背景白底 luma≈250，
    /// 未聚焦行深灰 luma≈75；扫文本右侧空白行带 x 0.64~0.88 避开文字与最右缘蓝勾）。返回 null = 未检出。
    /// </summary>
    public static string? DetectDropdownFocusedLevel(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;

        string? best = null;
        int bestLuma = -1, secondLuma = -1;
        foreach (var diff in MatchBrain.Difficulties)
        {
            var word = ocr.Words.FirstOrDefault(x =>
                x.CenterX is > 0.45 and < 0.70 && x.CenterY is > 0.32 and < 0.66 &&
                x.Text.Replace(" ", "").Contains(diff, StringComparison.Ordinal));
            if (word is null) continue;

            double y0 = Math.Clamp(word.CenterY - 0.014, 0, 1);
            double y1 = Math.Clamp(word.CenterY + 0.014, 0, 1);
            int luma = RegionProbe.MedianLuma(frame, 0.64, 0.88, y0, y1);
            if (luma > bestLuma)
            {
                secondLuma = bestLuma;
                bestLuma = luma;
                best = diff;
            }
            else if (luma > secondLuma)
            {
                secondLuma = luma;
            }
        }
        if (best is null || bestLuma < 170) return null;
        if (secondLuma >= 0 && bestLuma - secondLuma < 50) return null;
        return best;
    }

    /// <summary>
    /// 计算下拉列表从 <paramref name="current"/> 移动到 <paramref name="wanted"/> 的步数与方向
    /// （正数 = 向下，负数 = 向上，0 = 已对齐）。优先结合基准难度序与实机行距 0.0425 计算。
    /// </summary>
    public static int ComputeDropdownStepDelta(OcrResult ocr, string current, string wanted)
    {
        if (current == wanted) return 0;
        int curIdx = Array.IndexOf(MatchBrain.Difficulties, current);
        int wantIdx = Array.IndexOf(MatchBrain.Difficulties, wanted);
        int? canonDelta = curIdx >= 0 && wantIdx >= 0 ? wantIdx - curIdx : null;

        var curWord = ocr.Words.FirstOrDefault(x =>
            x.CenterX is > 0.45 and < 0.80 && x.CenterY is > 0.31 and < 0.67 &&
            x.Text.Replace(" ", "").Contains(current, StringComparison.Ordinal));
        var wantWord = ocr.Words.FirstOrDefault(x =>
            x.CenterX is > 0.45 and < 0.80 && x.CenterY is > 0.31 and < 0.67 &&
            x.Text.Replace(" ", "").Contains(wanted, StringComparison.Ordinal));

        if (curWord is not null && wantWord is not null)
        {
            int yDelta = (int)Math.Round((wantWord.CenterY - curWord.CenterY) / DropdownRowStepY);
            if (canonDelta is int cd && Math.Abs(cd - yDelta) <= 1)
                return Math.Clamp(cd, -6, 6);
            return Math.Clamp(yDelta, -6, 6);
        }
        return canonDelta is int d ? Math.Clamp(d, -6, 6) : 0;
    }

    /// <summary>
    /// 难度页的最终确认弹窗：「是否要选择&lt;难度名&gt;？」+ 纵排按钮 进入(上)/取消(下)，
    /// ★默认焦点在「取消」——直接按 A 会取消回列表（用户 2026-10-30 实机）。必须 ↑ 后再 A。
    /// 判定：说明句「是否要选择」；或屏中部纵排「进入」+「取消」双词（x 0.4~0.6）兜底，
    /// 防难度名被 OCR 拼进行（实机帧读成「标准是否要选择传奇奖励」整行）时漏判。
    /// </summary>
    public static bool IsDifficultyConfirmPopup(OcrResult ocr) =>
        ocr.ContainsJoined("是否要选择") ||
        (ocr.Words.Any(w => w.CenterX is > 0.4 and < 0.6 && w.CenterY is > 0.45 and < 0.75 &&
                            w.NormText.Contains("取消")) &&
         ocr.Words.Any(w => w.CenterX is > 0.4 and < 0.6 && w.CenterY is > 0.45 and < 0.62 &&
                            w.NormText.Contains("进入")));
}

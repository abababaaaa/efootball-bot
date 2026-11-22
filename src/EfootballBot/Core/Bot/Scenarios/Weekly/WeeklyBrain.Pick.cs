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

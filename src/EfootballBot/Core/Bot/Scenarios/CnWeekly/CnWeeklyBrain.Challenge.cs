using System.Text.RegularExpressions;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 模块层 · 周活动（Core/Bot/Scenarios）· CnWeeklyBrain 判定
// 本文件是 partial 拆分出来的「一个小活动」：只放它自己的界面判定 / 决策 / 子流程，
// 改这一个活动时不要动别的活动文件；共用逻辑留 `CnWeeklyBrain.cs`。
// 子活动分工见 docs/MODULES.md「周活动子活动分工」。
// 架构锁与其余约束同一份：不跨模块引用、场次记账与挂机节奏只走 MatchPlay。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

public static partial class CnWeeklyBrain
{
    // 本文件小活动：挑战型活动 —— 清单选卡、卡页完成判定、奖励勾/进度护栏。

    // ---------------- 挑战清单选卡页（2026-10-17 实机 cn_challenge_list.png） ----------------
    // 布局不固定卡数：国服当前一屏 3 张、国际服 2 张，都可能更多（用户明示 5 卡/多卡布局
    // 存在）。一律以 OCR 大字卡号自身动态定位：卡号在卡左上角，卡右缘 ≈ 下一张卡号位置
    // （最后一张用相邻间距外推），锁/勾探针在卡内动态取区，不绑定任何固定卡位坐标。
    /// <summary>卡号大字判定：H≥0.075（国服 3 卡布局实机 H≈0.09，普通文字 H≈0.05）、
    /// y 0.15~0.40、x&lt;0.95（出屏残卡的窄边碎片不取）的两位数字。O/0 归一防 "O1" 漏检。</summary>
    private static bool IsChallengeCardNumber(OcrWord w, out int index)
    {
        index = 0;
        if (w.H < 0.075 || w.CenterY < 0.15 || w.CenterY > 0.40 || w.CenterX >= 0.95) return false;
        var t = w.NormText.ToUpper().Replace("O", "0");
        if (t.Length != 2 || !t.All(char.IsDigit)) return false;
        index = int.Parse(t);
        return index >= 1;
    }

    /// <summary>
    /// 挑战清单兜底特征：卡号带里至少两枚大字卡号 + 底栏「返回」。任意卡数布局通用。
    /// </summary>
    private static bool HasChallengeListCardNumbers(OcrResult ocr)
    {
        int count = 0;
        foreach (var w in ocr.Words)
            if (IsChallengeCardNumber(w, out _)) count++;
        return count >= 2 && ocr.ContainsJoined("返回");
    }

    /// <summary>可见卡的动态几何：卡号词按 x 排序定左右次序（Slot），卡右缘取下一张卡号
    /// 减内边距，最后一张用相邻间距外推。返回 (卡号, 次序, 右缘, 左缘) 列表。</summary>
    private static List<(int Index, int Slot, double Right, double Left)> ChallengeCardBounds(
        OcrResult ocr)
    {
        var nums = new List<(int Index, double X)>();
        foreach (var w in ocr.Words)
            if (IsChallengeCardNumber(w, out int idx) && !nums.Any(n => n.Index == idx))
                nums.Add((idx, w.CenterX));
        var ordered = nums.OrderBy(n => n.X).ToList();
        var bounds = new List<(int, int, double, double)>();
        for (int i = 0; i < ordered.Count; i++)
        {
            double pitch = i + 1 < ordered.Count ? ordered[i + 1].X - ordered[i].X : pitchDefault(ordered);
            double right = i + 1 < ordered.Count
                ? ordered[i + 1].X - ChallengeNumberInset
                : ordered[i].X + pitch - ChallengeNumberInset;
            bounds.Add((ordered[i].Index, i, right, ordered[i].X - ChallengeNumberInset));
        }
        return bounds;
        double pitchDefault(List<(int Index, double X)> list)
            => list.Count >= 2 ? list[^1].X - list[^2].X : 0.30;
    }

    /// <summary>
    /// 解析挑战清单当前可见卡：OCR 大字卡号给出「哪张卡在屏幕哪个位置」，再对该卡的
    /// 动态区域做锁（右下角灰锁图标）/勾（右上角绿勾）像素探针。返回按卡号升序的可见卡
    /// 列表（Slot=屏上左右次序）；翻页后同一位置会换卡号，调用方按 Index 跟踪扫描游标。
    /// 卡记录复用国际服 WkChallengeCard（Index/Slot/Locked/Checked 同义）。
    /// </summary>
    public static List<WkChallengeCard> ScanChallengeCards(OcrResult ocr, FrameData frame)
    {
        var list = new List<WkChallengeCard>();
        foreach (var (index, slot, right, left) in ChallengeCardBounds(ocr))
        {
            // 锁：卡右下角。探针取卡右缘内侧一窄条（右界内收 0.01 兜住右缘推导误差、
            // 下界 0.85 抬离卡底边描边；宽不足时收缩到卡内），灰亮像素（三通道接近、
            // 亮度 90~220）——锁卡片深灰底（亮度&lt;90）与白卡（亮度&gt;220）都不计，
            // 只有灰锁图标命中
            double lx1 = right - 0.01, lx0 = Math.Max(left + 0.02, right - 0.12);
            bool locked = CountGray(frame, lx0, lx1, 0.700, 0.850) >= 25;
            // 勾：卡右上角完成徽章（绿底白勾）。绿像素计数 ≥40；⚠ 完成态特征尚未拿到
            // 实机截图校准——漏检由场景层「已通关卡记忆」兜底，误检无副作用（跳过卡而已）
            bool check = CountGreen(frame, Math.Max(left + 0.02, right - 0.10), right - 0.01, 0.150, 0.300) >= 40;
            list.Add(new WkChallengeCard(index, slot, locked, check));
        }
        return list.OrderBy(c => c.Index).ToList();
    }

    /// <summary>区域内亮度中位数（共享实现见 <see cref="RegionProbe.MedianLuma"/>）。</summary>
    private static int RegionMedianLuma(FrameData f, double x0, double x1, double y0, double y1)
        => RegionProbe.MedianLuma(f, x0, x1, y0, y1);
    /// <summary>
    /// 挑战清单当前聚焦卡（游标所在卡）：聚焦卡白底高亮、其余卡深灰底（实机 2026-10-17 晚
    /// cn_challenge_list_done_focus：聚焦白卡中位亮度 200+，深灰卡 &lt;80）。对每张可见卡体
    /// 取亮度中位数，最亮者须过白底下限且显著高于其余卡，否则视为未读出（返回 null，
    /// 调用方保持旧行为直接按 A）。用于把游标主动移到目标卡——清单打开时光标可能停在
    /// 刚打完的勾卡上，并不总在待挑战卡上。
    /// </summary>
    public static int? DetectFocusedChallengeCard(OcrResult ocr, FrameData frame)
    {
        var medians = new List<(int Index, double Slot, int Median)>();
        foreach (var (index, slot, right, left) in ChallengeCardBounds(ocr))
        {
            int median = RegionMedianLuma(frame, left + 0.04, right - 0.04, 0.30, 0.72);
            medians.Add((index, slot, median));
        }
        if (medians.Count == 0) return null;
        var best = medians.OrderByDescending(m => m.Median).First();
        if (best.Median < 180) return null; // 没有白卡：聚焦态未读出
        int restMax = medians.Where(m => m.Index != best.Index)
            .Select(m => m.Median).DefaultIfEmpty(0).Max();
        if (best.Median - restMax < 60) return null; // 与其余卡拉不开差距：不判定
        return best.Index;
    }

    /// <summary>区域内灰色像素计数（共享实现见 <see cref="RegionProbe.CountGray"/>）。</summary>
    private static int CountGray(FrameData f, double x0, double x1, double y0, double y1)
        => RegionProbe.CountGrayRef(f, x0, x1, y0, y1);
    /// <summary>区域内绿色像素计数（共享实现见 <see cref="RegionProbe.CountGreen"/>）。</summary>
    private static int CountGreen(FrameData f, double x0, double x1, double y0, double y1)
        => RegionProbe.CountGreenRef(f, x0, x1, y0, y1);
    // ---------------- 挑战型活动（国服专属词） ----------------
    /// <summary>
    /// 挑战卡详情页右栏「奖励」行末端的绿勾（2026-10-17 实机 ≈(0.96,0.51)）：本卡奖励已
    /// 领取 = 卡已完成，选中未完成的新挑战时该位置无勾。★比「首次以后的奖励」词探针
    /// 可靠——OCR 常漏读该词（23:21 实机 24 词帧漏读后再次空开比赛）。
    /// </summary>
    public static bool ChallengeRewardClaimed(FrameData frame)
        => CountGreen(frame, 0.935, 0.985, 0.470, 0.545) >= 30;
    /// <summary>
    /// 挑战卡详情页积分/进度未达标（用户 2026-10-31 实机反馈：卡 03「以4分或以上为目标」
    /// 积分 3/4，被「已打满 2 局按通关处理」兜底误判通关，主要奖励漏拿）。扫描页面上
    /// "a/b" 形式的独立比值词（原始 Text 保留斜杠，实机帧 0/1、3/4 均为独立词），
    /// 任一 a&lt;b 即视为未达标。限一位数（max≤9）：挑战卡比赛次数上限 3、单局积分 ≤3，

    /// 目标必在个位；两位数比值（日期 10/31 等）不取，防误拦。
    /// ★只作否定护栏：挡「打满 2 局」兜底误判，不反向申报通关（页面上可能同时有
    /// 积分 4/4 与进度 1/3 两种比值，标签错位无法区分谁是目标——达标申报仍由奖励勾
    /// 像素探针与胜场读满负责）。目标已达成而勾探针漏读时本护栏会多拖两局，由
    /// 「反复进入无比赛开始」兜底收尾，无死循环。
    /// </summary>
    public static bool ChallengeScoreBehind(OcrResult ocr)
    {
        foreach (var w in ocr.Words)
        {
            var m = global::System.Text.RegularExpressions.Regex.Match(
                w.Text.Trim(), @"^(\d{1,2})\s*/\s*(\d{1,2})$");
            if (!m.Success) continue;
            int cur = int.Parse(m.Groups[1].Value), max = int.Parse(m.Groups[2].Value);
            if (max is >= 1 and <= 9 && cur < max) return true;
        }
        return false;
    }

    /// <summary>
    /// 国服挑战卡详情页识别补充（2026-10-17 第二周实机 绿茵传奇挑战赛）：卡页词为
    /// 「完成挑战可获得对应的奖励!」「完成挑战N」，没有国际服卡页的「完成的条件」——
    /// 半途启动（未经挑战清单）直接落在卡页时靠它激活挑战子流程。只在场景层已分类
    /// EventHome/EventHomeTour 之后调用；清单/介绍页不含该词，不受干扰。
    /// </summary>
    public static bool IsCnChallengeCardHome(OcrResult ocr) => ocr.ContainsJoined("完成挑战");
}

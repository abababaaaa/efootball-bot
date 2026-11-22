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
    // 本文件小活动：巡回赛主要奖励 —— 主页勾 / i 信息弹窗进度解析、活动卡指纹。

    // ---------------- 巡回赛主要奖励核验（2026-10-17 实机 cn_tour_reward_popup） ----------------
    /// <summary>
    /// 巡回赛主页右栏「主要奖励」行右端的绿勾（≥1 个主要奖励已领取）。实机帧：勾中心
    /// ≈(0.948,0.674)，与国际服活动主页勾区同位。⚠ 勾≠拿满——主页勾表示已有主要奖励
    /// 被领过（主页 高光定向券×1 带勾时 i 弹窗里仍是 1/2），是否拿满以 i 弹窗 x/x 为准。
    /// </summary>
    public static bool MainRewardHasCheck(FrameData frame)
        => CountGreen(frame, 0.920, 0.965, 0.635, 0.715) >= 50;
    /// <summary>
    /// 解析 i 信息弹窗「主要奖励」进度（实机帧：主要奖励 头下方 金币 60/120、
    /// 高光定向券 1/2 各占一项）。进度词只认 主要奖励 头与 活动时间 头之间的
    /// N/M 纯两段词——活动时间行 2026/10/16 为三段不吃。返回 (是否读出至少一项,
    /// 是否全部拿满 cur==max)；读不出按未拿满处理（保守继续刷，绝不提前退场）。
    /// </summary>
    public static (bool Readable, bool AllDone) ParseMainRewardProgress(OcrResult ocr)
    {
        double? top = ocr.Words.FirstOrDefault(w => w.Text.Replace(" ", "") == "主要奖励")?.CenterY;
        if (top is null) return (false, false);
        double? bottom = ocr.Words.FirstOrDefault(w => w.Text.Replace(" ", "").Contains("活动时间"))?.CenterY;
        var items = new List<(int Cur, int Max)>();
        foreach (var w in ocr.Words)
        {
            if (w.CenterY <= top.Value + 0.01) continue;
            if (bottom is not null && w.CenterY >= bottom.Value - 0.01) continue;
            var m = Regex.Match(w.Text.Replace(" ", ""), @"^(\d+)/(\d+)$");
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out int cur) ||
                !int.TryParse(m.Groups[2].Value, out int max) || max <= 0) continue;
            items.Add((cur, max));
        }
        return items.Count > 0 ? (true, items.All(i => i.Cur == i.Max)) : (false, false);
    }

    /// <summary>
    /// 活动卡指纹 = 卡名 + 主页总活动积分（读不出记 ?）：同一张卡的活动主页 OCR 稳定给出同一个
    /// 指纹，卡行右移换卡才变化。用来识别「主要奖励已刷满、退回活动列表后右移却仍停在同一张卡」——
    /// 实机 2026-11-20：槽位从 1 一路涨到 306（右移按到卡行末尾即饱和，光标永远停在最后一张），
    /// 每轮都是同一张已满卡 → 全满 → 退列表 → 槽位 +1，空转 7 小时永不接力。
    /// 返回空串 = 卡名读不出（调用方只用指纹做加速判定，卡位上限兜底）。
    /// </summary>
    public static string CardSignature(OcrResult ocr)
    {
        string? name = LongestTitleWord(ocr, 0.15) ?? LongestTitleWord(ocr, 0.22);
        if (name is null) return "";
        long? points = TryParseTotalPoints(ocr);
        return $"{name}|{points?.ToString() ?? "?"}";
    }

    /// <summary>顶部标题带（0.02 &lt; y ≤ maxY）里最长的非噪声词：活动名在实机帧里是合并整词。</summary>
    private static string? LongestTitleWord(OcrResult ocr, double maxY)
    {
        string? best = null;
        foreach (var w in ocr.MergedWords)
        {
            if (w.CenterY < 0.02 || w.CenterY > maxY) continue;
            string t = w.Text.Replace(" ", "");
            if (t.Length < 4 || t.Length > 20) continue;
            if (CardNameNoise.Any(n => t.Contains(n, StringComparison.Ordinal))) continue;
            if (best is null || t.Length > best.Length) best = t;
        }
        return best;
    }

    /// <summary>
    /// 巡回赛主页「总活动积分」的数值（标签右侧/正下方最近的一个数字词，如 9,548 → 9548）。
    /// 只用于卡身份比较：读数稳定比读得准更重要，两次进同一张卡走同一套判定即可对上。
    /// 标签词被 OCR 合并成一整行时优先取标签自身的数字。
    /// </summary>
    public static long? TryParseTotalPoints(OcrResult ocr)
    {
        OcrWord? label = null;
        foreach (var w in ocr.MergedWords)
            if (w.NormText.Contains("总活动积分", StringComparison.Ordinal)) { label = w; break; }
        if (label is null) return null;
        // 只认标签右侧/下方的独立数字词：OCR 常把同一行的标签和邻词合并成一整行，
        // 从标签自身的文字里抠数字会把远处无关数字（赛事等级奖励 2,156）当成积分。
        long? best = null;
        double bestScore = double.MaxValue;
        foreach (var w in ocr.MergedWords)
        {
            if (ReferenceEquals(w, label)) continue;
            string raw = w.NormText;
            string digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length is < 2 or > 7) continue;
            // 必须是纯数字词：OCR 会把同一行的「赛事等级奖励 2,156」并成一个整词，
            // 这种整词里的数字属于别的栏位，不能当积分
            if (digits.Length != raw.Length) continue;
            double dy = w.CenterY - label.CenterY;
            double dx = w.CenterX - label.CenterX;
            if (dy < -0.05 || dy > 0.22) continue;   // 同行或标签正下方
            if (dx < -0.02 || dx > 0.45) continue;   // 值在标签右侧
            double score = Math.Abs(dy) + Math.Abs(dx) * 0.5;
            if (score >= bestScore) continue;
            bestScore = score;
            best = long.TryParse(digits, out long v) ? v : null;
        }
        return best;
    }
}

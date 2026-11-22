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
    // 本文件小活动：晋级挑战·教练签下链 —— 教练卡属性扫描与签下确认判定。

    // ---------------- 教练选择页（2026-11-04 实机 晋级挑战） ----------------
    // 页面：标题「请选择您的教练」+ 教练卡横排（姓名 / 国旗 / 评价大字数字 / 进攻风格战术板），
    // 底栏 B 返回。★只比较卡上的评价数字（80/81 这类固定 UI 数字），教练姓名/国籍/战术
    // 风格每周轮换，一律不作判定点（可变内容禁令）。
    /// <summary>
    /// 教练评价数字词：H≥0.035（实机 1280×720 评价字高 ≈0.047，普通正文 ≈0.03）、
    /// y 0.30~0.50（卡头姓名下方、进攻风格之上）、x&lt;0.95（出屏残卡碎片不取）的
    /// 2~3 位纯数字。O→0 归一（防 "8O" 漏读）。
    /// </summary>
    private static bool IsCoachRatingWord(OcrWord w, out int rating)
    {
        rating = 0;
        if (w.H < 0.035 || w.CenterY < 0.30 || w.CenterY > 0.50 || w.CenterX >= 0.95) return false;
        var t = w.NormText.ToUpper().Replace("O", "0");
        if (t.Length is < 2 or > 3 || !t.All(char.IsDigit)) return false;
        rating = int.Parse(t);
        return rating is >= 10 and <= 120;
    }

    /// <summary>
    /// 教练选择页兜底特征：同一行 ≥2 个评价数字 + 「进攻风格」（标题漏读时）。单张卡
    ///（残卡/过渡帧）不构成教练选择页。
    /// </summary>
    private static bool HasCoachRatingRow(OcrResult ocr)
    {
        if (!ocr.ContainsJoined("进攻风格")) return false;
        int count = 0;
        foreach (var w in ocr.Words)
            if (IsCoachRatingWord(w, out _)) count++;
        return count >= 2;
    }

    /// <summary>
    /// 解析教练选择页的可见教练卡：评价数字按 x 排序给出屏上左右次序 Slot 与评价 Rating
    ///（同一评价只取一次，防止同一张卡的数字被拆成两词重复计）。返回按 Slot 升序；空 = 未读出。
    /// 调用方比较 Rating 取最大者，用 Slot 做「左饱和归位 → 右×Slot」定位（不依赖可信光标）。
    /// </summary>
    public static List<(int Slot, int Rating)> ScanCoachCards(OcrResult ocr)
    {
        var found = new List<(int Rating, double X)>();
        foreach (var w in ocr.Words)
        {
            if (!IsCoachRatingWord(w, out int rating)) continue;
            if (found.Any(f => f.Rating == rating)) continue;
            found.Add((rating, w.CenterX));
        }
        var ordered = found.OrderBy(f => f.X).ToList();
        var list = new List<(int Slot, int Rating)>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++) list.Add((i, ordered[i].Rating));
        return list;
    }

    /// <summary>
    /// 鼠标点击用（2026-11-05 实机：教练选择页只认鼠标，手柄 Dpad+A 按了没反应）：
    /// 找评价数字最大的那张卡上「评价字」的归一化中心，返回 (Rating, CenterX, CenterY)；
    /// 读不出返回 null。调用方在 Y 上再往下偏一点（评价字在卡头，卡面在下方）。
    /// </summary>
    public static (int Rating, double CenterX, double CenterY)? FindBestCoachCardWord(OcrResult ocr)
    {
        (int Rating, double CenterX, double CenterY)? best = null;
        foreach (var w in ocr.Words)
        {
            if (!IsCoachRatingWord(w, out int rating)) continue;
            if (best is null || rating > best.Value.Rating)
                best = (rating, w.CenterX, w.CenterY);
        }
        return best;
    }

    /// <summary>
    /// 签下确认弹窗（「签下确认」+ 确认/取消，默认焦点在确认，2026-11-04 实机）：
    /// 只要标题词命中即可——弹窗盖在教练资料页上，资料页底栏「RT 确认选择」仍可读，
    /// 靠标题词与资料页区分。处置由场景层按 A（不生效再上移纠正焦点）。
    /// </summary>
    public static bool IsSignConfirmPopup(OcrResult ocr) => ocr.ContainsJoined("签下确认");
}

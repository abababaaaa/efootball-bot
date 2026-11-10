using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios;

/// <summary>弹窗这一轮该按什么（用户 2026-11-10 规则）。</summary>
public enum PopupAction
{
    /// <summary>认不出按钮（OCR 没读到 / 不是弹窗）：调用方按原逻辑处理。</summary>
    None,
    /// <summary>直接按 A（下一步）。单按钮弹窗永远走这条。</summary>
    PressConfirm,
    /// <summary>上×1 挪到推进键再按 A（光标停在下方「取消/返回」上时）。</summary>
    UpThenConfirm,
    /// <summary>下×1 挪到推进键再按 A（光标停在上方「取消/返回」上时）。</summary>
    DownThenConfirm,
}

/// <summary>弹窗里识别到的一枚按钮。<paramref name="Luma"/> 是按钮底色中位亮度（判定高光用）。</summary>
public sealed record PopupButton(
    string Label, bool IsConfirm, double CenterX, double CenterY, double H, int Luma);

/// <summary>
/// 弹窗按钮决策（用户 2026-11-10 实机规则，纯函数 + 像素探针，可单测）：
/// <list type="number">
/// <item>先看弹窗上有几个按键：<b>一个按键</b> → 直接按下一步（A）。</item>
/// <item><b>两个及以上</b> → 判断「白底高亮」的那一枚是不是 确认 / 进行 / OK / 下一步 / 进入 /
///       继续 / 领取 这类推进键；是 → 直接 A。</item>
/// <item>不是（光标停在 取消 / 返回 / 关闭 上）→ 按两枚按钮的上下关系把光标往上或往下挪一格
///       到推进键，再按 A。</item>
/// </list>
/// 为什么需要：eFootball 的弹窗默认焦点五花八门（有的在 OK、有的在取消、有的在上方付款键），
/// 旧逻辑只有「OCR 里有『取消/关闭』就一律上移」一条粗规则——遇到「取消在上、推进键在下」
/// 就反了方向；OCR 漏读取消时又会按在取消上把弹窗关掉。
/// 实机事故（2026-11-09 夜间，国际服周活动）：「物品已送到收件箱 / OK」这种单键弹窗被场景层
/// 一条过期的「其他板块页 → B」规则反复按 B，整夜零场次。
/// 判定只用固定 UI 词与按钮底色亮度，活动名/奖励文案这些可变内容一律不作判定点。
/// </summary>
public static class PopupBrain
{
    /// <summary>聚焦按钮判定阈值：聚焦行是白/亮底（实机白按钮 230~255、青色 OK 键 146），
    /// 未聚焦是深色半透明（实机 40~90）。取 120 留出青色按钮与深色按钮两侧的余量。</summary>
    public const int FocusedButtonLuma = 120;

    /// <summary>两枚按钮都偏亮/偏暗时，靠亮度差判聚焦（差值够大才算数）。</summary>
    public const int FocusedButtonLumaGap = 40;

    /// <summary>推进键词素（规范化 + 去尾部数字后整词比较）。</summary>
    private static readonly string[] ConfirmWords =
        { "确认", "確定", "确定", "下一步", "进行", "进入", "继续", "领取", "获取", "ok", "yes", "next", "confirm" };

    /// <summary>非推进键词素：命中这些一律视为「不该按的键」。</summary>
    private static readonly string[] CancelWords =
        { "取消", "返回", "关闭", "稍后", "否", "离开", "退出", "cancel", "back", "close", "no" };

    /// <summary>按钮候选纵向带：按钮在屏幕中部偏下（底栏 B/A 提示在 y&gt;0.92，排除掉）。</summary>
    private const double MinButtonY = 0.30, MaxButtonY = 0.92;

    /// <summary>按钮候选横向带：按钮居中，左右边缘的「奖励/条件」面板词不算。</summary>
    private const double MinButtonX = 0.10, MaxButtonX = 0.90;

    /// <summary>按钮字高下限（实机按钮词 H≈0.026~0.05，正文 H≈0.02）。</summary>
    private const double MinButtonH = 0.022;

    /// <summary>按钮词长度上限（「取消比赛」「返回主页」这类正文短语不算按钮）。</summary>
    private const int MaxButtonChars = 6;

    /// <summary>找弹窗上的按钮：按标签去重，同一标签只留字高最大的一枚（OCR 常把同一按钮读成两枚）。</summary>
    public static IReadOnlyList<PopupButton> FindButtons(FrameData? frame, OcrResult ocr)
    {
        var map = new Dictionary<string, PopupButton>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in ocr.Words)
        {
            if (w.CenterY < MinButtonY || w.CenterY > MaxButtonY) continue;
            if (w.CenterX < MinButtonX || w.CenterX > MaxButtonX) continue;
            if (w.H < MinButtonH) continue;
            var core = Core(w.NormText);
            if (core.Length == 0 || core.Length > MaxButtonChars) continue;
            bool confirm = ConfirmWords.Any(k => core.Equals(k, StringComparison.OrdinalIgnoreCase));
            bool cancel = CancelWords.Any(k => core.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (!confirm && !cancel) continue;
            int luma = frame is null ? 0 : BandLuma(frame, w);
            var hit = new PopupButton(core, confirm, w.CenterX, w.CenterY, w.H, luma);
            if (map.TryGetValue(core, out var exist) && exist.H >= hit.H) continue;
            map[core] = hit;
        }
        return map.Values.OrderBy(b => b.CenterY).ToList();
    }

    /// <summary>
    /// 弹窗决策：单按钮 → A；多按钮 → 看白底高亮那枚是不是推进键；不是就按上下关系挪一格再 A。
    /// </summary>
    public static PopupAction Decide(FrameData? frame, OcrResult ocr, out string note)
    {
        if (frame is null)
        {
            // 没有像素帧就判不了「白底高光」：交回调用方的旧规则（有取消/关闭就先上移），
            // 绝不拿亮度 0 去猜焦点
            note = "无像素帧，高光无法判定";
            return PopupAction.None;
        }
        var buttons = FindButtons(frame, ocr);
        if (buttons.Count == 0)
        {
            note = "未识别到按钮词";
            return PopupAction.None;
        }
        if (buttons.Count == 1)
        {
            note = $"单按钮「{buttons[0].Label}」→ 直接 A";
            return PopupAction.PressConfirm;
        }

        var confirm = buttons.FirstOrDefault(b => b.IsConfirm);
        var focused = Focused(buttons);

        if (focused is null)
        {
            // 探针读不出高光（低对比主题/过渡帧）：默认焦点多为推进键，直接 A（与旧行为等价）
            note = $"多按钮但高光读不出（{Describe(buttons)}）→ 直接 A";
            return PopupAction.PressConfirm;
        }
        if (focused.IsConfirm)
        {
            note = $"高光在推进键「{focused.Label}」→ 直接 A（{Describe(buttons)}）";
            return PopupAction.PressConfirm;
        }
        if (confirm is null)
        {
            note = $"高光在「{focused.Label}」但没有推进键（{Describe(buttons)}）→ 直接 A";
            return PopupAction.PressConfirm;
        }
        bool confirmBelow = confirm.CenterY > focused.CenterY;
        note = $"高光在「{focused.Label}」上，推进键「{confirm.Label}」在{(confirmBelow ? "下" : "上")}方" +
               $" → {(confirmBelow ? "下" : "上")}×1 → A（{Describe(buttons)}）";
        return confirmBelow ? PopupAction.DownThenConfirm : PopupAction.UpThenConfirm;
    }

    /// <summary>Buttons 的可读摘要（日志用）。</summary>
    public static string Describe(IReadOnlyList<PopupButton> buttons)
        => string.Join(" / ", buttons.Select(b => $"{b.Label}{(b.IsConfirm ? "·推进" : "·取消")}@y{b.CenterY:F2} L{b.Luma}"));

    /// <summary>白底高亮的那一枚（亮度差不够大时返回 null = 认不出高光）。</summary>
    private static PopupButton? Focused(IReadOnlyList<PopupButton> buttons)
    {
        var lit = buttons.Where(b => b.Luma >= FocusedButtonLuma).ToList();
        if (lit.Count == 1) return lit[0];
        var order = buttons.OrderByDescending(b => b.Luma).ToList();
        if (order.Count >= 2 && order[0].Luma - order[1].Luma >= FocusedButtonLumaGap) return order[0];
        return null;
    }

    /// <summary>按钮词芯：去掉词尾数字残留（OCR 常把图标读进词尾，如「OK7」「确定2」）。</summary>
    private static string Core(string norm)
    {
        int end = norm.Length;
        while (end > 0 && char.IsDigit(norm[end - 1])) end--;
        return norm[..end];
    }

    /// <summary>
    /// 按钮底色亮度（中位亮度）：以按钮文字为中心取一条横向亮带（文字左右各 5% 宽、上下各半个
    /// 字高）。聚焦按钮是白/亮底（实机 230~255），未聚焦是深色半透明（实机 &lt;120）。
    /// </summary>
    private static int BandLuma(FrameData frame, OcrWord w)
    {
        const double padX = 0.05;
        double padY = w.H * 0.5;
        double x0 = Math.Clamp(w.X - padX, 0, 1), x1 = Math.Clamp(w.X + w.W + padX, 0, 1);
        double y0 = Math.Clamp(w.Y - padY, 0, 1), y1 = Math.Clamp(w.Y + w.H + padY, 0, 1);
        return RegionProbe.MedianLuma(frame, x0, x1, y0, y1);
    }
}

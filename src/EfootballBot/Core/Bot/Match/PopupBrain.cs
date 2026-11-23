using EfootballBot.Core.Capture;
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
    // 「覆盖」：购买道具替换已有物品确认弹窗（"要覆盖目前的物品吗？"覆盖/取消），
    // 2026-11-23 实机——焦点默认在下方蓝色「取消」，需上移到上方白底「覆盖」才是确认。
    private static readonly string[] ConfirmWords =
        { "确认", "確定", "确定", "下一步", "进行", "进入", "继续", "领取", "获取", "覆盖", "ok", "yes", "next", "confirm" };
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
        // 替换物品确认弹窗（用户 2026-11-23 实机）：标题「要覆盖目前的物品吗？」+「覆盖」/「取消」，
        // 焦点默认在下方「取消」。实测按钮词经常只读出「取消」一枚（「覆盖」被并进标题或漏读），
        // 于是旧逻辑走进「单按钮 → 直接 A」，A 正好点掉取消 —— 购买白做、下一轮又弹同一个窗。
        // 这类弹窗版式固定（覆盖在上、取消在下），不靠按钮 OCR，只认标题里的「覆盖」→ 上×1 再 A。
        if (IsReplaceDialog(ocr))
        {
            note = "替换物品确认弹窗（标题含「覆盖」）→ 上×1 → A";
            return PopupAction.UpThenConfirm;
        }
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

    /// <summary>
    /// 是不是「要覆盖目前的物品吗？」这类替换确认弹窗（购买道具时目标格已有同类物品）。
    /// ★这是共享层判据，所有模块的 HasConfirm 都会走，所以必须收紧，否则别的模块页面上
    /// 出现「覆盖范围：全部球员」这类说明文字就会被误判成替换弹窗、白按一次「上 + A」：
    /// ① 词必须落在弹窗文字带内；② 同屏必须读到「取消」按钮词（替换弹窗一定有）；
    /// ③ 词必须是这句标题的说法（含 目前/物品/吗）或按钮整词「覆盖」，单看「覆盖」二字不算。
    /// 标题被整行合并读长（超过 TitleMaxChars）时这里会漏，但 Classify 的全文拼接判据
    /// 「覆盖目前的物品 / 要覆盖」仍会把它判成 PurchaseDialog，双保险不丢。
    /// </summary>
    public static bool IsReplaceDialog(OcrResult ocr)
    {
        // 替换弹窗必带「取消」；没有就不是（也避免把普通说明页当弹窗）
        if (!ocr.ContainsJoined("取消")) return false;
        foreach (var w in ocr.MergedWords)
        {
            if (!InDialogBand(w)) continue;
            string t = w.NormText;
            if (t.Length == 0 || t.Length > TitleMaxChars) continue;
            if (!t.Contains("覆盖", StringComparison.Ordinal)) continue;
            if (t.Contains("目前", StringComparison.Ordinal) ||
                t.Contains("物品", StringComparison.Ordinal) ||
                t.Contains("吗", StringComparison.Ordinal) ||
                t.Equals("覆盖", StringComparison.Ordinal))
                return true;
        }
        return false;
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

    // =======================================================================
    // 掉线重连弹窗（系统级：与服务器通讯中断时由游戏自己弹出，盖在任何界面上）
    // 实测 2026-11-20 11:10（用户截图 + 日志 10:10:38「界面：Unknown」）：
    //   标题「要重新连接吗？」/ 正文「和 实况足球 进行通讯时发生未知错误。」/
    //   两枚按钮上下排列：上＝取消（默认焦点，填充青蓝），下＝重试。
    // 老逻辑的致命点：「重试」不在推进键词表里 → PopupBrain 判成「没有推进键，直接 A」
    // → A 正好按在「取消」上，等于放弃重连，游戏退回标题/黑屏，挂机整夜空转。
    // 正确动作（用户 2026-11-20 要求）：把光标向下挪一格到「重试」再 A。
    // =======================================================================

    /// <summary>重连弹窗标题词素（含换皮写法，「重新连接/重新连线」）。</summary>
    private static readonly string[] ReconnectTitleWords = { "重新连接", "重新连线" };
    /// <summary>重连弹窗里唯一该按的键（推进键 = 重新连上服务器）。</summary>
    private const string ReconnectRetryWord = "重试";
    /// <summary>取消/放弃键。</summary>
    private const string ReconnectCancelWord = "取消";
    /// <summary>焦点键的青蓝填充占比下限（实地焦点键 RGB(33,150,243)，未聚焦是纯白，只能靠色相区分）。</summary>
    private const double FocusedAccentShare = 0.35;

    // ---- 文字证据的定位约束（2026-11-20 15:23 实机误判后加）----
    // 那一轮：天梯活动页（OCR 满屏字）被误判成掉线弹窗，日志「没读到「重试」按钮」，
    // 于是每 6 秒「下 + A」一次，在活动页上乱点。根因是老的文字证据用的是
    // 「整屏拼接文本里的子串」——满屏噪声词很容易凑出「重新」+「连接」，
    // 再撞上一个含「重试」的碎词就成立。现在改为：必须是**落在弹窗纵带里的整词**。
    /// <summary>弹窗文字所在的纵向带（弹窗盖黑整屏后只剩中央这几行字）。</summary>
    private const double DialogWordMinY = 0.20, DialogWordMaxY = 0.85;
    /// <summary>弹窗文字的横向带（面板居中；贴边的页眉/页脚词不算）。</summary>
    private const double DialogWordMinX = 0.18, DialogWordMaxX = 0.82;
    /// <summary>标题词长度上限（「要重新连接吗」7 字；整行页眉拼出来的是几十字，不是标题）。</summary>
    private const int TitleMaxChars = 12;
    /// <summary>标题与「重试」之间的最大纵向距离（一枚弹窗的标题/按钮不会跨越半屏）。</summary>
    private const double TitleRetryMaxGap = 0.45;
    /// <summary>版式兜底的 OCR 词数上限：掉线弹窗盖黑整屏后只剩自己那几行字（标题 + 正文 + 两枚键，
    /// 实测最多五六个词）；读出一大把词说明画面还完整 —— 此时版式再像也不认
    /// （实机 15:23 天梯活动页就是这么被误判的）。</summary>
    public const int StructuralMaxOcrWords = 6;

    // ---- 反向文字证据（2026-11-20 16:04 教练天梯赛「请选择参赛模式」弹窗后加）----
    // 那一屏的版式和掉线弹窗几乎一模一样：暗背景 + 居中浅灰面板 + 面板内浅灰块 + 底下是填充青蓝的
    // 高亮键（焦点）。单看像素分不出来，但字说得清清楚楚：「请选择参赛模式 / 单场比赛 / 连续比赛 /
    // 取消」。掉线弹窗只会说「要重新连接吗？」「重试」「取消」。
    // 所以：只要弹窗带里读到别的弹窗的动作词或标题语义，就一律不认（交给场景层按自己的弹窗处理）。
    /// <summary>别的弹窗的动作词（掉线弹窗只有「取消/返回/重试」，不含这些）。</summary>
    private static readonly string[] ForeignDialogWords =
    {
        "单场", "连续比赛", "确定", "确认", "进入", "继续", "开始", "下一步",
        "退出", "结束", "领取", "获得", "购买", "关闭", "重来", "重新开始",
    };
    /// <summary>别的弹窗的标题语义（掉线弹窗标题只有「重新连接/重新连线」）。</summary>
    private static readonly string[] ForeignDialogTitles =
    {
        "选择", "是否", "提示", "奖励", "失败", "成功", "注意", "请稍候",
    };
    /// <summary>只有版式证据（读不出字）时，要连续几轮都成立才动手：加载/过场页会闪出同样的版式。</summary>
    public const int StructuralConfirmRounds = 2;

    /// <summary>
    /// 是不是掉线重连弹窗。文字证据优先（弹窗纵带里的标题整词 + 「重试」整词）；
    /// 文字读不出（实机偶发整屏无词、或整屏字都是别页内容）时才用版式兜底。
    /// </summary>
    public static bool IsReconnectDialog(FrameData? frame, OcrResult? ocr)
        => IsReconnectDialog(frame, ocr, out _);

    /// <summary>
    /// 同上，<paramref name="textEvidence"/> 标明这次是靠**画面文字**认出来的（true）
    /// 还是靠**版式**兜底（false）：版式证据单帧不作数，引擎要连成两轮才按键。
    /// </summary>
    public static bool IsReconnectDialog(FrameData? frame, OcrResult? ocr, out bool textEvidence)
    {
        textEvidence = false;
        if (ocr is not null)
        {
            var title = FindTitleWord(ocr);
            var retry = title is null ? null : FindRetryWord(ocr);
            if (title is not null && retry is not null &&
                Math.Abs(title.CenterY - retry.CenterY) <= TitleRetryMaxGap)
            {
                textEvidence = true;
                return true;
            }
            // 读到了别的弹窗的字（「请选择参赛模式 / 单场比赛 / 连续比赛 …」）：
            // 这是场景层该处理的弹窗，不是掉线弹窗 —— 版式再像也不认。
            if (LooksLikeOtherDialog(ocr)) return false;
            // 满屏字却没读出「标题 + 重试」：画面还完整，不是盖黑整屏的系统弹窗
            if (ocr.Words.Count > StructuralMaxOcrWords) return false;
        }
        return frame is not null && IsDialogPanelOnBlack(frame);
    }

    /// <summary>
    /// 弹窗标题整词（按排版行聚合后的词块）：落在弹窗纵带/横带、长度 ≤ <see cref="TitleMaxChars"/>、
    /// 命中「重新连接 / 重新连线」，或同一行里同时有「重新」与「连接/连线」（OCR 常把标题拆成两段）。
    /// 候选多枚取字高最大的一枚。
    /// </summary>
    private static OcrWord? FindTitleWord(OcrResult ocr)
    {
        OcrWord? best = null;
        foreach (var w in ocr.MergedWords)
        {
            if (!InDialogBand(w)) continue;
            string t = w.NormText;
            if (t.Length == 0 || t.Length > TitleMaxChars) continue;
            bool hit = ReconnectTitleWords.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                       (t.Contains("重新") && (t.Contains("连接") || t.Contains("连线")));
            if (!hit) continue;
            if (best is null || w.H > best.H) best = w;
        }
        return best;
    }

    /// <summary>「重试」整词（词尾 OCR 数字残留忽略）：只认整词，长短语里的「重试」不算按钮。</summary>
    private static OcrWord? FindRetryWord(OcrResult ocr)
    {
        OcrWord? best = null;
        foreach (var w in ocr.MergedWords)
        {
            if (!InDialogBand(w)) continue;
            if (!Core(w.NormText).Equals(ReconnectRetryWord, StringComparison.Ordinal)) continue;
            if (best is null || w.H > best.H) best = w;
        }
        return best;
    }

    /// <summary>「取消」整词（判焦点用；读不到不影响决策方向）。</summary>
    private static OcrWord? FindCancelWord(OcrResult ocr)
    {
        OcrWord? best = null;
        foreach (var w in ocr.MergedWords)
        {
            if (!InDialogBand(w)) continue;
            if (!Core(w.NormText).Equals(ReconnectCancelWord, StringComparison.Ordinal)) continue;
            if (best is null || w.H > best.H) best = w;
        }
        return best;
    }

    /// <summary>
    /// 弹窗带里是否读到了「别的弹窗」的字：动作词（单场比赛 / 连续比赛 / 确定 / 进入 …）
    /// 或标题语义（选择 / 是否 / 提示 / 奖励 …）。命中即说明这一屏是场景层自己的弹窗，
    /// 不是掉线弹窗。掉线弹窗自己的词（重新连接 / 重试 / 取消 / 通讯错）先被排除，不会自伤。
    /// </summary>
    private static bool LooksLikeOtherDialog(OcrResult ocr)
    {
        foreach (var w in ocr.MergedWords)
        {
            if (!InDialogBand(w)) continue;
            string t = w.NormText;
            if (t.Length == 0 || t.Length > TitleMaxChars) continue;
            // 先放行掉线弹窗自己的词（换皮标题「是否重新连接？」也在这里被认出来）
            if (t.Contains(ReconnectRetryWord) || ReconnectTitleWords.Any(k => t.Contains(k)) ||
                (t.Contains("重新") && (t.Contains("连接") || t.Contains("连线"))))
                continue;
            if (ForeignDialogWords.Any(k => t.Contains(k))) return true;
            if (ForeignDialogTitles.Any(k => t.Contains(k))) return true;
        }
        return false;
    }

    /// <summary>词块是否落在弹窗的文字带里（贴边的页眉/页脚、角落提示一律不算）。</summary>
    private static bool InDialogBand(OcrWord w)
        => w.CenterY >= DialogWordMinY && w.CenterY <= DialogWordMaxY &&
           w.CenterX >= DialogWordMinX && w.CenterX <= DialogWordMaxX;

    /// <summary>
    /// 重连弹窗这一轮按什么：焦点已在「重试」→ 直接 A；焦点在「取消」/判不出 → 向下挪一格再 A；
    /// 「重试」在上方（换皮版式）→ 向上挪一格。绝不返回「直接 A 在未知焦点上」。
    /// </summary>
    public static PopupAction DecideReconnect(FrameData? frame, OcrResult? ocr, out string note)
    {
        // 只认弹窗带里的整词：老代码用 FindButton/FindFirst（任意位置的子串都算），
        // 活动页/加载页的碎词也能当「重试」，2026-11-20 15:23 就是这么在活页面上乱按的。
        // 现在和判定用同一套取词（FindRetryWord / FindCancelWord），判得出才按得着。
        var retry = ocr is null ? null : FindRetryWord(ocr);
        var cancel = ocr is null ? null : FindCancelWord(ocr);
        if (retry is null)
        {
            // 读不出按钮：弹窗默认焦点就在「取消」上（实机），先下移一格再 A 才不会自我取消
            note = "没读到「重试」按钮：按默认焦点在「取消」处理 → 下移一格再 A";
            return PopupAction.DownThenConfirm;
        }
        if (frame is not null && cancel is not null)
        {
            bool retryLit = AccentShare(frame, retry) >= FocusedAccentShare;
            bool cancelLit = AccentShare(frame, cancel) >= FocusedAccentShare;
            if (retryLit && !cancelLit)
            {
                note = "焦点已在「重试」→ 直接 A";
                return PopupAction.PressConfirm;
            }
            bool retryBelow = retry.CenterY > cancel.CenterY;
            note = (cancelLit ? "焦点在「取消」" : "焦点读不出") +
                   $"，「重试」在{(retryBelow ? "下" : "上")}方 → 挪一格再 A";
            return retryBelow ? PopupAction.DownThenConfirm : PopupAction.UpThenConfirm;
        }
        note = "缺像素帧/「取消」没读到：焦点不可判定，按默认焦点（取消）处理 → 下移一格再 A";
        return PopupAction.DownThenConfirm;
    }

    /// <summary>面板亮度阈值：实机弹窗面板是 200–232 浅灰，聚焦键青蓝（RGB 33,150,243）亮度 131。</summary>
    private const int PanelLuma = 110;

    /// <summary>面板外「暗像素」的亮度上限。</summary>
    private const int OutsideDarkPixel = 12;

    /// <summary>面板外整屏里暗像素（亮度 ≤ <see cref="OutsideDarkPixel"/>）的最低占比：
    /// 系统弹窗背后是一整层黑遮罩 —— 实机重连帧实测 0.999；而活动页里浮起来的浅色面板
    /// （2026-11-20 16:04 教练天梯赛「请选择参赛模式」）只有 0.614。</summary>
    private const double OutsideDarkShare = 0.90;

    /// <summary>
    /// 黑底 + 居中浅色面板：浮在近黑画面上的一枚系统弹窗（重连弹窗的版式特征）。
    /// 只作 OCR 读不出字时的兜底，四道闸缺一不可：
    /// <list type="number">
    /// <item>四条边框近黑（弹窗背后是整屏黑遮罩）；</item>
    /// <item>九条横线 + 九条竖线上的亮段收敛到同一枚矩形（页面亮区会顶到屏幕边缘，几条线也对不齐）；</item>
    /// <item>矩形外面一圈仍是黑的（面板浮在黑上，不是贴边亮带）；</item>
    /// <item>矩形内部浅灰、低饱和、平整（金色徽章 / 彩色海报 / 渐变加载图都不算）。</item>
    /// </list>
    /// 2026-11-20 15:23 实机误判（天梯活动页被当弹窗连点「下 + A」）的原因就是老版只查
    /// 「四周暗 + 中央亮」，深色活动页、过场图全能过；收紧到「真是一枚居中矩形面板」后，
    /// 191 张实机帧里只有 7 张（真弹窗 / 结算确认页）能过，还都要再过一遍 OCR 词数闸。
    /// </summary>
    private static bool IsDialogPanelOnBlack(FrameData frame)
    {
        if (frame.Width < 320 || frame.Height < 240) return false;
        if (RegionProbe.MedianLuma(frame, 0.05, 0.95, 0.02, 0.10) > 30) return false;
        if (RegionProbe.MedianLuma(frame, 0.05, 0.95, 0.90, 0.98) > 30) return false;
        if (RegionProbe.MedianLuma(frame, 0.02, 0.10, 0.30, 0.70) > 30) return false;
        if (RegionProbe.MedianLuma(frame, 0.90, 0.98, 0.30, 0.70) > 30) return false;
        if (!PanelRect(frame, out double px0, out double px1, out double py0, out double py1)) return false;
        double pw = px1 - px0, ph = py1 - py0;
        if (pw < 0.25 || pw > 0.92 || ph < 0.15 || ph > 0.88) return false;
        if (Math.Abs((px0 + px1) / 2 - 0.5) > 0.12) return false;
        double cy = (py0 + py1) / 2;
        if (cy < 0.28 || cy > 0.72) return false;
        if (RingLuma(frame, px0, px1, py0, py1) > 45) return false;
        // 第五道闸：面板外整屏几乎全黑（= 盖了一层黑遮罩的系统弹窗）。
        // 2026-11-20 16:04 教练天梯赛「请选择参赛模式」那帧，面板几何和重连弹窗一模一样
        // （x 0.21–0.79 / y 0.19–0.81 / 里面 232 浅灰 / 底下青蓝焦点键），用同一套 PanelRect
        // 量出来的「面板外」也是大片近黑；唯一分得开的是暗像素占比：重连帧 0.999，天梯帧 0.614
        // （面板外还浮着活动页的卡片和金色文字）。OCR 全灭时靠这一条。
        if (OutsideDarkShareOf(frame, px0, px1, py0, py1) < OutsideDarkShare) return false;
        double ix0 = px0 + 0.06, ix1 = px1 - 0.06, iy0 = py0 + 0.06, iy1 = py1 - 0.06;
        if (ix1 - ix0 < 0.05 || iy1 - iy0 < 0.03) return false;
        if (RegionProbe.MedianLuma(frame, ix0, ix1, iy0, iy1) < 150) return false;
        return MedianChroma(frame, ix0, ix1, iy0, iy1) <= 32;
    }

    /// <summary>面板矩形之外整屏的暗像素占比（4px 网格取样，跳过面板内部；与 BrightExtent 同一套亮度算法）。</summary>
    private static double OutsideDarkShareOf(FrameData frame, double px0, double px1, double py0, double py1)
    {
        var bgra = frame.Bgra;
        int w = frame.Width, h = frame.Height;
        int ix0 = (int)(px0 * w), ix1 = (int)(px1 * w);
        int iy0 = (int)(py0 * h), iy1 = (int)(py1 * h);
        int dark = 0, total = 0;
        for (int y = 0; y < h; y += 4)
        {
            int row = y * w;
            for (int x = 0; x < w; x += 4)
            {
                if (x >= ix0 && x < ix1 && y >= iy0 && y < iy1) continue;
                int i = (row + x) * 4;
                total++;
                if ((bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3 <= OutsideDarkPixel) dark++;
            }
        }
        return total == 0 ? 0 : (double)dark / total;
    }

    /// <summary>
    /// 面板矩形（归一化）：沿 9 条横线 / 9 条竖线各取「亮度 ≥ <see cref="PanelLuma"/> 的最左、最右像素」，
    /// 用中位数收敛成一枚矩形，并要求至少 60% 的线跟中位数对得上（漂移大 = 不是一枚面板，是页面亮区）。
    /// 注意不能用「最长连续亮段」：实机弹窗里标题和按钮都是深色，会把纵向亮段打断成好几截
    /// （测得纵向最长 run 覆盖只有 0.45，而整个面板 0.49 —— 必须用 min/max extent）。
    /// </summary>
    private static bool PanelRect(FrameData frame,
        out double px0, out double px1, out double py0, out double py1)
    {
        px0 = px1 = py0 = py1 = 0;
        var hStarts = new List<double>(9);
        var hEnds = new List<double>(9);
        for (double y = 0.30; y <= 0.7001; y += 0.05)
        {
            if (!BrightExtent(frame, true, y, out double a, out double b)) continue;
            hStarts.Add(a);
            hEnds.Add(b);
        }
        var vStarts = new List<double>(9);
        var vEnds = new List<double>(9);
        for (double x = 0.30; x <= 0.7001; x += 0.05)
        {
            if (!BrightExtent(frame, false, x, out double a, out double b)) continue;
            vStarts.Add(a);
            vEnds.Add(b);
        }
        if (hStarts.Count < 5 || vStarts.Count < 5) return false;
        double hx0 = Median(hStarts), hx1 = Median(hEnds);
        double vy0 = Median(vStarts), vy1 = Median(vEnds);
        if (hx1 - hx0 < 0.20 || vy1 - vy0 < 0.12) return false;
        int hAgree = 0, vAgree = 0;
        for (int i = 0; i < hStarts.Count; i++)
            if (Math.Abs(hStarts[i] - hx0) <= 0.06 && Math.Abs(hEnds[i] - hx1) <= 0.06) hAgree++;
        for (int i = 0; i < vStarts.Count; i++)
            if (Math.Abs(vStarts[i] - vy0) <= 0.06 && Math.Abs(vEnds[i] - vy1) <= 0.06) vAgree++;
        if (hAgree * 10 < hStarts.Count * 6 || vAgree * 10 < vStarts.Count * 6) return false;
        px0 = hx0;
        px1 = hx1;
        py0 = vy0;
        py1 = vy1;
        return true;
    }

    /// <summary>
    /// 某一扫描线上的亮段范围（归一化 start/end）：亮度 ≥ <see cref="PanelLuma"/> 的最早/最末采样点，
    /// 并要求该线至少 35% 是亮的（碎亮块不是面板）。
    /// </summary>
    private static bool BrightExtent(FrameData frame, bool horizontal, double at,
        out double start, out double end)
    {
        start = end = 0;
        var bgra = frame.Bgra;
        int n = horizontal ? frame.Width : frame.Height;
        int fixedMax = (horizontal ? frame.Height : frame.Width) - 1;
        int fixedIdx = (int)Math.Clamp((horizontal ? frame.Height : frame.Width) * at, 0, fixedMax);
        int first = -1, last = -1, hits = 0, samples = 0;
        for (int i = 0; i < n; i += 2)
        {
            samples++;
            int p = (horizontal ? fixedIdx * frame.Width + i : i * frame.Width + fixedIdx) * 4;
            int luma = (bgra[p] + bgra[p + 1] + bgra[p + 2]) / 3;
            if (luma < PanelLuma) continue;
            hits++;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0 || hits * 100 < samples * 35) return false;
        start = (double)first / n;
        end = (double)(last + 1) / n;
        return true;
    }

    /// <summary>面板外面那圈（左右上各 0.012–0.05 宽的一条）中位亮度的最大值；判不出来按亮处理（不认）。</summary>
    private static int RingLuma(FrameData frame, double px0, double px1, double py0, double py1)
    {
        int worst = -1, bands = 0;
        void Band(double x0, double x1, double y0, double y1)
        {
            if (x1 - x0 < 0.008 || y1 - y0 < 0.008) return;
            if (x0 < 0 || y0 < 0 || x1 > 1 || y1 > 1) return;
            bands++;
            worst = Math.Max(worst, RegionProbe.MedianLuma(frame, x0, x1, y0, y1));
        }
        Band(px0 - 0.05, px0 - 0.012, py0 + 0.04, py1 - 0.04);
        Band(px1 + 0.012, px1 + 0.05, py0 + 0.04, py1 - 0.04);
        Band(px0 + 0.04, px1 - 0.04, py0 - 0.05, py0 - 0.012);
        Band(px0 + 0.04, px1 - 0.04, py1 + 0.012, py1 + 0.05);
        return bands < 2 ? 999 : worst;
    }

    /// <summary>区域色度中位数（每像素 max-min 通道差）：浅灰弹窗 ≈0，金色徽章 / 彩色海报 ≫30。</summary>
    private static int MedianChroma(FrameData frame, double x0, double x1, double y0, double y1)
    {
        int px0 = (int)Math.Clamp(x0 * frame.Width, 0, frame.Width - 1);
        int px1 = (int)Math.Clamp(x1 * frame.Width, px0 + 1, frame.Width);
        int py0 = (int)Math.Clamp(y0 * frame.Height, 0, frame.Height - 1);
        int py1 = (int)Math.Clamp(y1 * frame.Height, py0 + 1, frame.Height);
        var vals = new List<int>(Math.Max(16, (px1 - px0) / 3 * ((py1 - py0) / 3) + 1));
        for (int y = py0; y < py1; y += 3)
        {
            int row = y * frame.Width;
            for (int x = px0; x < px1; x += 3)
            {
                int i = (row + x) * 4;
                int b = frame.Bgra[i], g = frame.Bgra[i + 1], r = frame.Bgra[i + 2];
                int mx = Math.Max(b, Math.Max(g, r));
                int mn = Math.Min(b, Math.Min(g, r));
                vals.Add(mx - mn);
            }
        }
        return Median(vals);
    }

    /// <summary>中位数（会排序传入列表的副本，不改原列表）。</summary>
    private static double Median(List<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    /// <summary>中位数（整数版，区域探针与色度共用）。</summary>
    private static int Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        var sorted = new List<int>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// 按钮周围的青蓝填充占比。实机焦点键是填充青蓝（RGB 33,150,243，亮度 131），
    /// 未聚焦是纯白（255）——亮度阈值 120 两枚都过，所以焦点必须靠色相判。
    /// </summary>
    private static double AccentShare(FrameData frame, OcrWord w)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return 0;
        double padX = 0.06, padY = w.H * 0.6;
        int x0 = (int)Math.Clamp((w.X - padX) * frame.Width, 0, frame.Width - 1);
        int x1 = (int)Math.Clamp((w.X + w.W + padX) * frame.Width, 0, frame.Width);
        int y0 = (int)Math.Clamp((w.Y - padY) * frame.Height, 0, frame.Height - 1);
        int y1 = (int)Math.Clamp((w.Y + w.H + padY) * frame.Height, 0, frame.Height);
        int hit = 0, total = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * frame.Width;
            for (int x = x0; x < x1; x++)
            {
                int i = (row + x) * 4;
                int b = frame.Bgra[i], g = frame.Bgra[i + 1], r = frame.Bgra[i + 2];
                total++;
                if (b >= 150 && b - r >= 70 && b - g >= 40) hit++;
            }
        }
        return total == 0 ? 0 : (double)hit / total;
    }
}

/// <summary>掉线弹窗这一轮该做什么。</summary>
public enum ReconnectVerdict
{
    /// <summary>没看到掉线弹窗（或画面满是别页文字，压根不是盖黑整屏的弹窗）。</summary>
    None,
    /// <summary>只有版式证据、还没连成两轮：本轮不动手，等下一轮确认。</summary>
    Pending,
    /// <summary>证据够了，该按「重试」。</summary>
    Act,
}

/// <summary>
/// 掉线弹窗的连轮判定（引擎每轮 Observe 一次）：文字证据（弹窗带里的标题整词 + 「重试」整词）
/// 一票通过；只有版式证据时要连续 <see cref="PopupBrain.StructuralConfirmRounds"/> 轮都成立才动手 ——
/// 加载页 / 过场页会闪出一帧「黑底 + 居中浅色面板」，单帧版式不足以按键。
/// 2026-11-20 15:23 实机事故正是单帧版式证据直接开按，在天梯活动页上「下 + A」死循环。
/// </summary>
public sealed class ReconnectWatch
{
    private int _structuralHits;

    /// <summary>看一轮。<paramref name="textEvidence"/> 说明本轮证据是画面文字（true）还是版式（false）。</summary>
    public ReconnectVerdict Observe(FrameData? frame, OcrResult? ocr, out bool textEvidence)
    {
        textEvidence = false;
        if (!PopupBrain.IsReconnectDialog(frame, ocr, out textEvidence))
        {
            _structuralHits = 0;
            return ReconnectVerdict.None;
        }
        if (textEvidence)
        {
            _structuralHits = 0;
            return ReconnectVerdict.Act;
        }
        _structuralHits++;
        return _structuralHits >= PopupBrain.StructuralConfirmRounds
            ? ReconnectVerdict.Act
            : ReconnectVerdict.Pending;
    }

    /// <summary>清零（重新开始挂机 / 换局时调用，别让上一局的版式证据串场）。</summary>
    public void Reset() => _structuralHits = 0;

    /// <summary>当前连续版式命中轮数（日志与测试用）。</summary>
    public int StructuralHits => _structuralHits;
}





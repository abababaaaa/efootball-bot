using EfootballBot.Core.Bot.Scenarios;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

using EfootballBot.Core.Bot.Match;
namespace EfootballBot.Tests;

/// <summary>
/// 弹窗按键决策（用户 2026-11-10 规则）：单键弹窗直接 A；双键弹窗先看白底高光那枚是不是
/// 确认/进行/OK/下一步 这类推进键，是就 A，不是就按上下关系挪一格再 A。
/// 另锁定 2026-11-09 夜间事故帧（「物品已送到收件箱 / OK」）的处置：必须是 PressConfirm，
/// 且该帧在周活动场景里走弹窗分支（不再是「其他板块页 → B」）。
/// </summary>
public class PopupBrainTests
{
    private readonly ITestOutputHelper _out;
    public PopupBrainTests(ITestOutputHelper output) => _out = output;

    private static string WeeklyDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "weekly");

    // ---------------- 合成帧工具 ----------------

    /// <summary>造一帧：整体灰 240（浅色弹窗底），在给定矩形内涂指定灰度。</summary>
    private static FrameData MakeFrame(int w, int h, params (double X0, double Y0, double X1, double Y1, byte Lum)[] rects)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 240; bgra[i + 1] = 240; bgra[i + 2] = 240; bgra[i + 3] = 255;
        }
        foreach (var r in rects)
        {
            for (int y = (int)(r.Y0 * h); y < (int)(r.Y1 * h); y++)
                for (int x = (int)(r.X0 * w); x < (int)(r.X1 * w); x++)
                {
                    int i = (y * w + x) * 4;
                    bgra[i] = r.Lum; bgra[i + 1] = r.Lum; bgra[i + 2] = r.Lum;
                }
        }
        return new FrameData(bgra, w, h, 0);
    }

    private static OcrResult Ocr(params (string Text, double Cx, double Cy, double W, double H)[] words)
        => new(words.Select(x => new OcrWord(x.Text, x.Cx - x.W / 2, x.Cy - x.H / 2, x.W, x.H)).ToList(), 1280, 720);

    // ---------------- 纯规则 ----------------

    [Fact]
    public void SingleButton_PressesConfirm()
    {
        // 2026-11-09 夜间事故帧的样子：浅色弹窗 + 一枚 OK
        var frame = MakeFrame(1280, 720, (0.30, 0.72, 0.70, 0.86, 250));
        var ocr = Ocr(("物品已送到收件箱", 0.50, 0.55, 0.30, 0.05), ("OK", 0.50, 0.79, 0.03, 0.04));
        var buttons = PopupBrain.FindButtons(frame, ocr);
        _out.WriteLine("buttons: " + PopupBrain.Describe(buttons));
        Assert.Single(buttons);
        Assert.True(buttons[0].IsConfirm);
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void TwoButtons_FocusOnCancelBelow_MovesDownToConfirm()
    {
        // 取消在上且白底高光，确认在下且暗 → 下×1 再 A
        var frame = MakeFrame(1280, 720,
            (0.40, 0.48, 0.60, 0.60, 255),   // 取消：亮
            (0.40, 0.66, 0.60, 0.78, 40));   // 确认：暗
        var ocr = Ocr(("取消", 0.50, 0.54, 0.05, 0.04), ("确认", 0.50, 0.72, 0.05, 0.04));
        var buttons = PopupBrain.FindButtons(frame, ocr);
        _out.WriteLine("buttons: " + PopupBrain.Describe(buttons));
        Assert.Equal(2, buttons.Count);
        Assert.Equal(PopupAction.DownThenConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void TwoButtons_FocusOnCancelAbove_MovesUpToConfirm()
    {
        // 取消在下且白底高光，确认在上且暗 → 上×1 再 A（选难度/选球队那类弹窗）
        var frame = MakeFrame(1280, 720,
            (0.40, 0.48, 0.60, 0.60, 40),    // 进入：暗
            (0.40, 0.66, 0.60, 0.78, 255));  // 取消：亮
        var ocr = Ocr(("进入", 0.50, 0.54, 0.05, 0.04), ("取消", 0.50, 0.72, 0.05, 0.04));
        Assert.Equal(PopupAction.UpThenConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void TwoButtons_FocusOnConfirm_PressesConfirm()
    {
        var frame = MakeFrame(1280, 720,
            (0.40, 0.48, 0.60, 0.60, 255),   // 进入：亮（高光在推进键）
            (0.40, 0.66, 0.60, 0.78, 40));   // 取消：暗
        var ocr = Ocr(("进入", 0.50, 0.54, 0.05, 0.04), ("取消", 0.50, 0.72, 0.05, 0.04));
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void TwoButtons_FocusUnreadable_FallsBackToConfirm()
    {
        // 两枚都亮：探针读不出高光 → 直接 A（与旧行为等价，不冒险乱挪）
        var frame = MakeFrame(1280, 720, (0.40, 0.40, 0.60, 0.80, 255));
        var ocr = Ocr(("进入", 0.50, 0.54, 0.05, 0.04), ("取消", 0.50, 0.72, 0.05, 0.04));
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine(note);
    }

    [Fact]
    public void NoButton_ReturnsNone()
    {
        var frame = MakeFrame(1280, 720);
        var ocr = Ocr(("活动期间：2026/11/05 10:00", 0.30, 0.20, 0.35, 0.04));
        Assert.Empty(PopupBrain.FindButtons(frame, ocr));
        Assert.Equal(PopupAction.None, PopupBrain.Decide(frame, ocr, out _));
    }

    [Fact]
    public void SingleNonConfirmButton_ReturnsNoneInsteadOfBlindConfirm()
    {
        // 双按钮弹窗只读出「取消」而漏读推进词时，绝不能当作单键确认弹窗直接按 A
        var frame = MakeFrame(1280, 720, (0.40, 0.66, 0.60, 0.78, 255));
        var ocr = Ocr(("是否要购买此道具？", 0.50, 0.40, 0.25, 0.04), ("取消", 0.50, 0.72, 0.05, 0.04));
        var buttons = PopupBrain.FindButtons(frame, ocr);
        Assert.Single(buttons);
        Assert.False(buttons[0].IsConfirm);
        Assert.Equal(PopupAction.None, PopupBrain.Decide(frame, ocr, out _));

        // 交由 MatchBrain.Decide 兜底时应走「上移避开取消/关闭」而非直接 A
        var action = MatchBrain.Decide(MatchScreen.HasConfirm, ocr, screenStreak: 0, inMatchRecently: false, frame);
        Assert.Equal(MatchActionKind.PadUpConfirm, action.Kind);
    }

    [Fact]
    public void TwoButtons_PurchaseFocusedOnCancel_MovesUpToConfirm()
    {
        var frame = MakeFrame(1280, 720,
            (0.40, 0.48, 0.60, 0.60, 40),
            (0.40, 0.66, 0.60, 0.78, 255));
        var ocr = Ocr(("购买", 0.50, 0.54, 0.05, 0.04), ("取消", 0.50, 0.72, 0.05, 0.04));
        Assert.Equal(PopupAction.UpThenConfirm, PopupBrain.Decide(frame, ocr, out _));
    }

    [Fact]
    public void BodyTextContainingCancel_IsNotAButton()
    {
        // 正文「取消比赛」不是按钮（长度超限）；底栏 y>0.92 的热键提示也不算
        var frame = MakeFrame(1280, 720);
        var ocr = Ocr(("取消比赛将丢失进度", 0.35, 0.40, 0.30, 0.03), ("下一步", 0.86, 0.95, 0.05, 0.03));
        Assert.Empty(PopupBrain.FindButtons(frame, ocr));
    }

    // ---------------- 实机帧 ----------------

    [Fact]
    public void InboxOkPopup_RealFrame_PressesConfirm()
    {
        var path = Path.Combine(WeeklyDir, "popup_inbox_ok_1109.png");
        if (!File.Exists(path)) { _out.WriteLine("popup_inbox_ok_1109.png missing, skip"); return; }
        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        using var ocrEngine = new OcrRecognizer();
        var ocr = TestOcr.Run(ocrEngine, frame);

        var buttons = PopupBrain.FindButtons(frame, ocr);
        _out.WriteLine("buttons: " + PopupBrain.Describe(buttons));
        Assert.Single(buttons);
        Assert.True(buttons[0].IsConfirm, $"唯一按钮应被认成推进键，实读「{buttons[0].Label}」");
        Assert.Equal(PopupAction.PressConfirm, PopupBrain.Decide(frame, ocr, out var note));
        _out.WriteLine("decide: " + note);

        // 该帧在周活动场景里必须有弹窗分支接住（旧逻辑被「其他板块页 → B」规则吃掉）
        var dets = new List<YoloDetection>
        {
            new("btn_confirm", 0, 0.84f, 0.42, 0.75, 0.10, 0.08), // 实机 YOLO 检出 OK 按钮 0.82~0.85
        };
        var wk = WeeklyBrain.Classify(dets, ocr);
        var ml = MatchBrain.Classify(dets, ocr);
        _out.WriteLine($"classify: wk={wk} ml={ml}");
        Assert.Equal(WkScreen.None, wk);
        Assert.Equal(MatchScreen.HasConfirm, ml);
    }

    // ---------------- 掉线重连弹窗（2026-11-20 实机） ----------------
    // 版式（用户截图）：黑底 + 中央浅灰面板；标题「要重新连接吗？」；正文「和 实况足球 进行
    // 通讯时发生未知错误。」；两枚按钮上下排列：上＝取消（默认焦点，填充青蓝）、下＝重试。
    // 老逻辑把「重试」当不成推进键 → 直接 A 落在「取消」上 = 放弃重连，挂机整夜空转。

    /// <summary>造一帧：整体近黑 + 指定彩色矩形（画顺序即叠放顺序）。</summary>
    private static FrameData MakeBlackFrame(int w, int h,
        params (double X0, double Y0, double X1, double Y1, byte R, byte G, byte B)[] rects)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i + 3] = 255; }
        foreach (var r in rects)
            for (int y = (int)(r.Y0 * h); y < (int)(r.Y1 * h); y++)
                for (int x = (int)(r.X0 * w); x < (int)(r.X1 * w); x++)
                {
                    int i = (y * w + x) * 4;
                    bgra[i] = r.B; bgra[i + 1] = r.G; bgra[i + 2] = r.R;
                }
        return new FrameData(bgra, w, h, 0);
    }

    /// <summary>重连弹窗的实机版式（取消在上且高亮、重试在下）。</summary>
    private static FrameData ReconnectLayout(bool retryFocused)
    {
        var panel = (0.21, 0.24, 0.79, 0.76, (byte)232, (byte)232, (byte)232);
        var cancelLit = (0.33, 0.50, 0.67, 0.58, (byte)33, (byte)150, (byte)243);
        var cancelDim = (0.33, 0.50, 0.67, 0.58, (byte)250, (byte)250, (byte)250);
        var retryLit = (0.33, 0.60, 0.67, 0.68, (byte)33, (byte)150, (byte)243);
        var retryDim = (0.33, 0.60, 0.67, 0.68, (byte)250, (byte)250, (byte)250);
        return MakeBlackFrame(1280, 720, panel,
            retryFocused ? cancelDim : cancelLit,
            retryFocused ? retryLit : retryDim);
    }

    private static OcrResult ReconnectOcr(params (string Text, double Cx, double Cy, double W, double H)[] extra)
        => Ocr(new (string, double, double, double, double)[]
        {
            ("要重新连接吗？", 0.50, 0.36, 0.22, 0.05),
            ("和 实况足球 进行通讯时发生未知错误。", 0.50, 0.47, 0.28, 0.03),
            ("取消", 0.50, 0.54, 0.06, 0.04),
            ("重试", 0.50, 0.64, 0.06, 0.04),
        }.Concat(extra).ToArray());

    [Fact]
    public void ReconnectDialog_DetectedFromOcrText()
    {
        var frame = ReconnectLayout(retryFocused: false);
        Assert.True(PopupBrain.IsReconnectDialog(frame, ReconnectOcr()));
    }

    [Fact]
    public void ReconnectDialog_IgnoreOrdinaryPopup()
    {
        var frame = MakeFrame(1280, 720, (0.30, 0.40, 0.70, 0.80, 240));
        var ocr = Ocr(("关键比赛", 0.50, 0.38, 0.16, 0.05), ("取消", 0.50, 0.72, 0.05, 0.04),
            ("进入", 0.50, 0.54, 0.05, 0.04));
        Assert.False(PopupBrain.IsReconnectDialog(frame, ocr));
    }

    [Fact]
    public void ReconnectDialog_DetectedFromBlackPanelWhenOcrEmpty()
    {
        // 实机偶发整屏 OCR 读不出词（GUI 里「感知」一片空白）：靠版式兜底
        var frame = ReconnectLayout(retryFocused: false);
        Assert.True(PopupBrain.IsReconnectDialog(frame, OcrResult.Empty));
        Assert.True(PopupBrain.IsReconnectDialog(frame, null));
    }

    [Fact]
    public void ReconnectDialog_PlainBlackFrame_NotDetected()
    {
        // 纯黑帧（GDI 兜底取不到画面）不能当成重连弹窗：那是感知全盲，绝不按键
        var frame = MakeBlackFrame(1280, 720);
        Assert.False(PopupBrain.IsReconnectDialog(frame, OcrResult.Empty));
    }

    [Fact]
    public void ReconnectDialog_CancelFocused_MovesDownThenConfirm()
    {
        var frame = ReconnectLayout(retryFocused: false);
        var action = PopupBrain.DecideReconnect(frame, ReconnectOcr(), out var note);
        _out.WriteLine(note);
        Assert.Equal(PopupAction.DownThenConfirm, action);
    }

    [Fact]
    public void ReconnectDialog_RetryFocused_PressesConfirmDirectly()
    {
        var frame = ReconnectLayout(retryFocused: true);
        var action = PopupBrain.DecideReconnect(frame, ReconnectOcr(), out var note);
        _out.WriteLine(note);
        Assert.Equal(PopupAction.PressConfirm, action);
    }

    [Fact]
    public void ReconnectDialog_ButtonsUnreadable_NeverPressesBareA()
    {
        // 只读到标题：焦点不可判定时按「默认焦点在取消」处理，绝不直接 A（那是放弃重连）
        var frame = ReconnectLayout(retryFocused: false);
        var ocr = Ocr(("要重新连接吗？", 0.50, 0.36, 0.22, 0.05));
        var action = PopupBrain.DecideReconnect(frame, ocr, out var note);
        _out.WriteLine(note);
        Assert.Equal(PopupAction.DownThenConfirm, action);
        Assert.Equal(PopupAction.DownThenConfirm, PopupBrain.DecideReconnect(frame, null, out _));
    }

    [Fact]
    public void ReconnectDialog_RealFrame_DetectedAndMovesDownThenConfirm()
    {
        // 实机帧（2026-11-20 11:10 用户截图）：黑底 + 浅灰面板，「取消」在上且填充青蓝（默认焦点），
        // 「重试」在下。这一帧就是挂机原地空转的那一屏。
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "TestData", "cnmatch", "cn_reconnect_popup_20261120.png");
        if (!File.Exists(path)) { _out.WriteLine("cn_reconnect_popup_20261120.png missing, skip"); return; }
        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);

        // 版式兜底：即使 OCR 一片空白也必须认出这枚弹窗（实机 GUI 里「感知」确实读不出词）
        Assert.True(PopupBrain.IsReconnectDialog(frame, OcrResult.Empty),
            "黑底 + 中央浅色面板的实机重连帧必须被识别");

        using var ocrEngine = new OcrRecognizer();
        var ocr = TestOcr.Run(ocrEngine, frame);
        _out.WriteLine("ocr: " + ocr.JoinedText);
        var action = PopupBrain.DecideReconnect(frame, ocr, out var note);
        _out.WriteLine("decide: " + note);
        Assert.Equal(PopupAction.DownThenConfirm, action);
    }

    [Fact]
    public void ReconnectDialog_RetryAboveCancel_MovesUp()
    {
        // 换皮版式（重试在上、取消在下）：按上下关系上移
        var frame = MakeBlackFrame(1280, 720, (0.21, 0.24, 0.79, 0.76, (byte)232, (byte)232, (byte)232));
        var ocr = Ocr(("要重新连接吗？", 0.50, 0.36, 0.22, 0.05),
            ("重试", 0.50, 0.54, 0.06, 0.04), ("取消", 0.50, 0.64, 0.06, 0.04));
        var action = PopupBrain.DecideReconnect(frame, ocr, out var note);
        _out.WriteLine(note);
        Assert.Equal(PopupAction.UpThenConfirm, action);
    }

    // ---------------- 误判回归（2026-11-20 15:23 天梯活动页） ----------------
    // 用户实机：天梯巅峰赛活动页（暗红黑底 + 金色徽章 + 满屏字）被判成掉线弹窗，
    // 日志「没读到「重试」按钮 → 下移一格再 A」每 6 秒一次，在活页面上乱点。
    // 根因：文字证据是「整屏拼接文本的子串」，噪声词能凑出 重新+连接+重试；版式证据只有
    // 「四周暗 + 中央亮」，深色活动页全能过。这两条都在这里钉死。

    /// <summary>造一帧天梯活动页的样子：黑底 + 金色徽章 + 几块暗红卡片（没有居中浅灰面板）。</summary>
    private static FrameData ActivityPageLayout() => MakeBlackFrame(1280, 720,
        (0.00, 0.00, 1.00, 0.14, (byte)22, (byte)10, (byte)10),     // 顶部暗红条
        (0.24, 0.10, 0.76, 0.34, (byte)46, (byte)16, (byte)14),     // 标题区
        (0.44, 0.38, 0.56, 0.56, (byte)226, (byte)186, (byte)62),   // 金色徽章（高饱和）
        (0.06, 0.62, 0.34, 0.92, (byte)40, (byte)14, (byte)14),     // 左下卡片
        (0.66, 0.62, 0.94, 0.92, (byte)40, (byte)14, (byte)14));    // 右下卡片

    /// <summary>活动页会读出来的那种满屏词（含散落的「重新 / 连接 / 重试」碎片）。</summary>
    private static OcrResult ActivityPageOcr() => Ocr(
        ("教练天梯巅峰赛", 0.50, 0.20, 0.34, 0.05),
        ("活动时间", 0.30, 0.28, 0.12, 0.03),
        ("大师联赛", 0.50, 0.44, 0.16, 0.05),
        ("天梯积分", 0.50, 0.52, 0.14, 0.04),
        ("2400", 0.50, 0.58, 0.08, 0.04),
        ("奖励预览", 0.18, 0.66, 0.14, 0.04),
        ("前往比赛", 0.50, 0.66, 0.16, 0.05),
        ("球队管理", 0.82, 0.66, 0.14, 0.04),
        ("每周任务", 0.18, 0.72, 0.14, 0.04),
        ("排行榜", 0.82, 0.72, 0.12, 0.04),
        ("重新", 0.10, 0.12, 0.06, 0.03),
        ("连接", 0.88, 0.90, 0.06, 0.03),
        ("重试", 0.30, 0.90, 0.06, 0.03),
        ("排行榜奖励说明", 0.50, 0.86, 0.30, 0.03));

    [Fact]
    public void ReconnectDialog_ActivityPageWithNoiseWords_NotDetected()
    {
        // 2026-11-20 15:23 事故页：暗底 + 满屏词 + 散落的 重新/连接/重试
        var frame = ActivityPageLayout();
        var ocr = ActivityPageOcr();
        Assert.False(PopupBrain.IsReconnectDialog(frame, ocr),
            "活动页不能被判成掉线弹窗（否则每 6 秒「下 + A」在活页面上乱点）");
        // 整屏拼接文本里确实凑得出「重新」「连接」「重试」——老逻辑就是这么判成立的
        Assert.True(ocr.ContainsJoined("重新") && ocr.ContainsJoined("连接") && ocr.Contains("重试"));
        // 版式也不许兜底：徽章高饱和 + 不是一枚居中矩形面板
        Assert.False(PopupBrain.IsReconnectDialog(frame, OcrResult.Empty));
    }

    [Fact]
    public void ReconnectDialog_DarkPanelButPageWords_NotDetected()
    {
        // 同样的暗底 + 居中面板版式，但画面读出满屏别页文字 → 不是盖黑整屏的系统弹窗。
        // 这是「版式像」与「真的是弹窗」之间唯一的闸：OCR 词数 <= StructuralMaxOcrWords。
        var frame = ReconnectLayout(retryFocused: false);
        var pageWords = Ocr(Enumerable.Range(0, 20)
            .Select(i => ($"正文{i:00}", 0.20 + 0.03 * (i % 6), 0.25 + 0.03 * (i / 6), 0.10, 0.03))
            .ToArray());
        Assert.True(pageWords.Words.Count > PopupBrain.StructuralMaxOcrWords);
        Assert.False(PopupBrain.IsReconnectDialog(frame, pageWords));
        // 同一帧读不出字（弹窗情形）仍然要认出
        Assert.True(PopupBrain.IsReconnectDialog(frame, OcrResult.Empty));
    }

    [Fact]
    public void ReconnectWatch_StructuralEvidence_NeedsTwoRounds()
    {
        // 只有版式证据：第 1 轮只观察（Pending，绝不按键），第 2 轮还成立才动手
        var watch = new ReconnectWatch();
        var frame = ReconnectLayout(retryFocused: false);

        var first = watch.Observe(frame, OcrResult.Empty, out bool text1);
        _out.WriteLine($"round1: {first} text={text1} hits={watch.StructuralHits}");
        Assert.Equal(ReconnectVerdict.Pending, first);
        Assert.False(text1);
        Assert.Equal(1, watch.StructuralHits);

        var second = watch.Observe(frame, OcrResult.Empty, out bool text2);
        _out.WriteLine($"round2: {second} text={text2} hits={watch.StructuralHits}");
        Assert.Equal(ReconnectVerdict.Act, second);
        Assert.False(text2);
    }

    [Fact]
    public void ReconnectWatch_TextEvidence_ActsOnFirstRound()
    {
        // 文字证据（标题 + 重试）一票通过：实机真弹窗不该等第二轮（多等一轮就少连一次）
        var watch = new ReconnectWatch();
        var verdict = watch.Observe(ReconnectLayout(retryFocused: false), ReconnectOcr(), out bool text);
        _out.WriteLine($"verdict: {verdict} text={text}");
        Assert.Equal(ReconnectVerdict.Act, verdict);
        Assert.True(text);
    }

    [Fact]
    public void ReconnectWatch_PopupGone_ResetsStructuralHits()
    {
        // 闪一帧像弹窗的版式，下一帧回到正常页：连轮计数必须清零，
        // 否则「加载页 + 正常页 + 加载页」也能凑成两轮
        var watch = new ReconnectWatch();
        Assert.Equal(ReconnectVerdict.Pending, watch.Observe(ReconnectLayout(false), OcrResult.Empty, out _));
        var normal = MakeFrame(1280, 720, (0.30, 0.40, 0.70, 0.80, 240));
        var normalOcr = Ocr(("关键比赛", 0.50, 0.38, 0.16, 0.05), ("进入", 0.50, 0.54, 0.05, 0.04));
        Assert.Equal(ReconnectVerdict.None, watch.Observe(normal, normalOcr, out _));
        Assert.Equal(0, watch.StructuralHits);
        Assert.Equal(ReconnectVerdict.Pending, watch.Observe(ReconnectLayout(false), OcrResult.Empty, out _));
    }

    [Fact]
    public void ReconnectWatch_ActivityPage_NeverActs()
    {
        // 事故页连看 5 轮：一轮都不能动手（这是用户报的那个 bug 的最终防线）
        var watch = new ReconnectWatch();
        var frame = ActivityPageLayout();
        var ocr = ActivityPageOcr();
        for (int i = 0; i < 5; i++)
        {
            var v = watch.Observe(frame, ocr, out bool text);
            Assert.Equal(ReconnectVerdict.None, v);
            Assert.False(text);
            Assert.Equal(0, watch.StructuralHits);
        }
    }

    // ---------------- 误判回归（2026-11-20 16:04 教练天梯赛「请选择参赛模式」） ----------------
    // 用户实机：暗红活动页上浮起一枚居中浅灰面板，里面「请选择参赛模式 / 即刻进入教练天梯巅峰赛 /
    // 当前剩余参赛场次：14」+ 三枚按钮（单场比赛 / 连续比赛 / 取消，取消填充青蓝 = 焦点）。
    // 版式与掉线弹窗几乎一样（居中浅灰面板 + 四边暗 + 青蓝焦点键），老逻辑只靠「OCR 词数」偶然拦住；
    // 用户要求靠文字识别排除。这里三样一起钉死：反向文字、词数、面板外底色。

    /// <summary>造一帧天梯「请选择参赛模式」弹窗：暗红活动页 + 居中浅灰面板 + 三枚按钮。</summary>
    private static FrameData LadderModeSelectLayout() => MakeBlackFrame(1280, 720,
        (0.00, 0.00, 1.00, 1.00, (byte)40, (byte)16, (byte)14),     // 暗红活动页（面板外不是黑的）
        (0.21, 0.19, 0.79, 0.85, (byte)232, (byte)232, (byte)232),   // 居中浅灰面板
        (0.33, 0.32, 0.67, 0.40, (byte)250, (byte)250, (byte)250),   // 单场比赛
        (0.33, 0.44, 0.67, 0.52, (byte)250, (byte)250, (byte)250),   // 连续比赛
        (0.33, 0.56, 0.67, 0.64, (byte)33, (byte)150, (byte)243));   // 取消（填充青蓝 = 焦点）

    private static OcrResult LadderModeSelectOcr() => Ocr(
        ("请选择参赛模式", 0.50, 0.30, 0.26, 0.05),
        ("即刻进入教练天梯巅峰赛", 0.50, 0.36, 0.40, 0.03),
        ("当前剩余参赛场次：14", 0.50, 0.50, 0.30, 0.03),
        ("单场比赛", 0.50, 0.36, 0.10, 0.04),
        ("连续比赛", 0.50, 0.48, 0.10, 0.04),
        ("取消", 0.50, 0.60, 0.06, 0.04));

    [Fact]
    public void ReconnectDialog_LadderModeSelect_WithText_NotDetected()
    {
        // 用户 16:04 那一屏：字说得清清楚楚是「请选择参赛模式」，绝不是掉线弹窗
        var frame = LadderModeSelectLayout();
        var ocr = LadderModeSelectOcr();
        Assert.False(PopupBrain.IsReconnectDialog(frame, ocr),
            "天梯赛选择弹窗不能被判成掉线弹窗（否则下 + A 会落在「连续比赛」上）");
        // 版式闸单独跑也拦得住：面板外是暗红活动页，不是整屏黑遮罩
        Assert.False(PopupBrain.IsReconnectDialog(frame, OcrResult.Empty),
            "面板外底色不是黑 → 版式兜底也不许认");
    }

    [Fact]
    public void ReconnectDialog_LadderModeSelect_RealFrame_NotDetected()
    {
        // 实机帧（2026-11-20 16:04 用户截图）：暗红活动页 + 居中浅灰面板 + 单场比赛/连续比赛/取消
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "TestData", "cnmatch", "cn_ladder_mode_select_20261120.png");
        if (!File.Exists(path)) { _out.WriteLine("cn_ladder_mode_select_20261120.png missing, skip"); return; }
        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);

        using var ocrEngine = new OcrRecognizer();
        var ocr = TestOcr.Run(ocrEngine, frame);
        _out.WriteLine($"ocr words={ocr.Words.Count}: " + ocr.JoinedText);
        Assert.True(ocr.Words.Count > PopupBrain.StructuralMaxOcrWords,
            $"这一屏满屏是字（读出 {ocr.Words.Count} 词），必须走文字判定而不是版式兜底");
        Assert.False(PopupBrain.IsReconnectDialog(frame, ocr), "实机天梯赛选择帧不能判成掉线弹窗");
        // OCR 全灭时也不认（面板外是暗红活动页）
        Assert.False(PopupBrain.IsReconnectDialog(frame, OcrResult.Empty), "版式兜底也不能认这帧");
    }

    [Fact]
    public void ReconnectWatch_LadderModeSelect_NeverActs()
    {
        // 用户报的就是这一屏：连看 4 轮，一轮都不许动手
        var watch = new ReconnectWatch();
        var frame = LadderModeSelectLayout();
        var ocr = LadderModeSelectOcr();
        for (int i = 0; i < 4; i++)
        {
            var v = watch.Observe(frame, ocr, out bool text);
            Assert.Equal(ReconnectVerdict.None, v);
            Assert.False(text);
        }
        for (int i = 0; i < 4; i++)
            Assert.Equal(ReconnectVerdict.None, watch.Observe(frame, OcrResult.Empty, out _));
        Assert.Equal(0, watch.StructuralHits);
    }
}



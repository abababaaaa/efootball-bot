using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 防掉线重登链回归（eFootball 2027 v6.1.0 实机 8 帧）：掉线/重启回落「按确认按钮」标题页
// → 登录 → 弹窗链（登录奖励/Campaign 网格/物品到收件箱/上一场被放弃）→ 误入抽取页 → 付款确认。
// 核心约定（用户 2026-10-19）：抽取界面必须 B 退出绝不消费金币；误看到付款确认必须直接按 A
// （光标默认在取消，上方是金币付款键——绝不能套用「有取消先上移」规则）。
// 首次运行看 Dump 输出校对 OCR 词；之后作为回归锁定（各帧分类/动作不得回退）。
public class IntlBootRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public IntlBootRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string Dir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "intlboot");

    private static readonly Lazy<YoloDetector> Det = new(() => new YoloDetector(
        Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
        Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
        new OnnxSessionFactory("CPU")));

    private static readonly Lazy<OcrRecognizer> OcrRec = new(() => new OcrRecognizer());

    private (MlScreen Screen, OcrResult Ocr) Load(string name, bool loginRecovery)
    {
        var path = Path.Combine(Dir, name);
        Assert.True(File.Exists(path), $"缺少实机帧: {path}");
        var data = TestFrameLoader.LoadPngBgra(path, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var dets = Det.Value.Detect(frame);
        var ocrRes = OcrRec.Value.RecognizeAsync(frame).GetAwaiter().GetResult();
        var screen = MyLeagueBrain.Classify(dets, ocrRes,
            MyLeagueScenario.CornerHasContent(frame),
            allowCornerInMatch: false,
            MyLeagueScenario.IsMostlyLightPage(frame),
            loginRecovery);

        _out.WriteLine($"==== {name} (loginRecovery={loginRecovery}) -> {screen} ====");
        foreach (var d in dets.Where(d => d.Score >= MyLeagueBrain.MinScore))
            _out.WriteLine($"  YOLO {d.ClassName} {d.Score:F2} @({d.CenterX:F2},{d.CenterY:F2})");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
        return (screen, ocrRes);
    }

    [Fact]
    public void Dump_IntlBoot_Frames()
    {
        foreach (var f in new[]
        {
            "boot_splash.png", "boot_splash_loading.png", "campaign_grid.png",
            "gacha_page.png", "inbox_popup.png", "login_bonus_popup.png", "payment_confirm.png",
        })
        {
            if (File.Exists(Path.Combine(Dir, f))) Load(f, loginRecovery: true);
        }
    }

    [Fact]
    public void BootSplash_ColdBoot_ClassifiesWithoutLoginRecovery()
    {
        // 冷启动/掉线首帧：流程记忆里还没有 BootSplash（loginRecovery=false）也必须判出——
        // 主判底栏「法律及隐私政策」，副判「确认按钮」+konami/版本。判不出会落旧 TitleScreen
        // 规则按 X，X 在此页无效 → 防掉线恢复从第一步就卡死
        var (screen, _) = Load("boot_splash.png", loginRecovery: false);
        Assert.Equal(MlScreen.BootSplash, screen);

        // 前几轮 下→右→A 登录序列；超过 BootLoginNavRounds 改持续按 A（页面提示按「确认」按钮）
        var nav = MyLeagueBrain.Decide(MlScreen.BootSplash, OcrResult.Empty, screenStreak: 0);
        Assert.Equal(MlActionKind.PadLoginNav, nav.Kind);
        var confirm = MyLeagueBrain.Decide(MlScreen.BootSplash, OcrResult.Empty,
            screenStreak: MyLeagueBrain.BootLoginNavRounds);
        Assert.Equal(MlActionKind.PadConfirm, confirm.Kind);
    }

    [Fact]
    public void BootSplash_LoadingFrame_StillClassifiesBootSplash()
    {
        // 转圈加载帧（同页变暗+转圈）：底栏词仍在，同判 BootSplash；加载期按键无效但无害，
        // 绝不能落 Unknown→连续 B 逃生（B 会打开侧边栏/退出登录）
        var (screen, _) = Load("boot_splash_loading.png", loginRecovery: false);
        Assert.Equal(MlScreen.BootSplash, screen);
    }

    [Fact]
    public void GachaPage_AlwaysBacksOut_NeverConsumes()
    {
        // 抽取页（Special Player List）：「有限次数抽取」主判、「查看全部+名球员」兜底。
        // 分类与 loginRecovery 无关（判定块先于门控块）。处置=B 退出，绝无确认类动作
        var (screen, _) = Load("gacha_page.png", loginRecovery: false);
        Assert.Equal(MlScreen.GachaPage, screen);
        var action = MyLeagueBrain.Decide(MlScreen.GachaPage, OcrResult.Empty, screenStreak: 0);
        Assert.Equal(MlActionKind.GamepadBack, action.Kind);
    }

    [Fact]
    public void PaymentConfirmDialog_DirectConfirmOnCancel()
    {
        // 付款确认弹窗（抽取页误按后）：弹窗底下就是抽取页（查看全部/名球员同屏），
        // ★必须先于抽取页判定；含「取消」也绝不能走 HasConfirm 的「有取消先上移」——
        // 上方是金币付款键。直接按 A = 点默认焦点「取消」
        var (screen, ocr) = Load("payment_confirm.png", loginRecovery: true);
        Assert.Equal(MlScreen.PaymentConfirmDialog, screen);
        var action = MyLeagueBrain.Decide(screen, ocr, screenStreak: 0);
        Assert.Equal(MlActionKind.PadConfirm, action.Kind);
        Assert.NotEqual(MlActionKind.PadUpConfirm, action.Kind);

        // 判定序守卫（纯构造）：付款确认 + 抽取页词同屏 → 付款确认赢
        Assert.Equal(MlScreen.PaymentConfirmDialog, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(),
            Ocr(("付款确认", 0.5, 0.3), ("取消", 0.5, 0.6), ("查看全部", 0.2, 0.6), ("名球员", 0.2, 0.3)),
            loginRecovery: true));
    }

    [Fact]
    public void LoginRecoveryPopupChain_ClassifiesHasConfirm_AndPressesA()
    {
        // 重登弹窗链（每周轮换不认具体词，只认推进按钮词）：登录奖励「继续」/
        // Campaign 网格「继续」/物品已送到收件箱「OK」/上一场比赛被放弃「OK」。
        // ★被放弃弹窗底下透出标题页的「法律及隐私政策」——弹窗链判定必须先于 BootSplash
        foreach (var f in new[] { "login_bonus_popup.png", "campaign_grid.png", "inbox_popup.png", "boot_abandoned_popup.png" })
        {
            var (screen, ocr) = Load(f, loginRecovery: true);
            Assert.Equal(MlScreen.HasConfirm, screen);
            var action = MyLeagueBrain.Decide(screen, ocr, screenStreak: 0);
            Assert.True(action.Kind is MlActionKind.PadConfirm or MlActionKind.PadUpConfirm,
                $"{f}: 应为推进类动作，实际 {action.Kind}:{action.Target}");
        }
    }

    [Fact]
    public void LoginRecoveryGate_RequiresBootSplashMemory()
    {
        // 门控守卫：推进词（继续/OK/下一步/确定）只有在最近见过标题页时才抢判 HasConfirm，
        // 否则正常流程里的同名词（联赛页「继续」等）会被抢走
        var ocr = Ocr(("继续", 0.5, 0.75));
        Assert.NotEqual(MlScreen.HasConfirm, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), ocr, loginRecovery: false));
        Assert.Equal(MlScreen.HasConfirm, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), ocr, loginRecovery: true));
    }

    [Fact]
    public void GachaJudge_ConstructedWords()
    {
        // 抽取页判定词守卫：主判词带价格尾巴（有限次数抽取:150/150）Norm 去标点后可命中；
        // 兜底 查看全部+名球员 双词同屏；付款确认词不存在时不误判付款弹窗
        Assert.Equal(MlScreen.GachaPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), Ocr(("有限次数抽取:150/150", 0.8, 0.07)), loginRecovery: false));
        Assert.Equal(MlScreen.GachaPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(), Ocr(("查看全部", 0.2, 0.6), ("10名球员", 0.2, 0.48)), loginRecovery: false));
    }

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1280, 720);
    }
}

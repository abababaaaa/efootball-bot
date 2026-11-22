using EfootballBot.Core.Bot;
using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 2026-11-20 00:52 实机事故回归：国服开赛加载停在球场全景，OCR 只剩水印词
// （实况足球在线 | X | 11.1.0.1.124916…），拼起来的签名 60 多个字符，长度检查放行，
// 于是被当成「可读页面卡住 45 秒」→ 按下/A/B 全无反应（加载页不吃这些键）→
// 「手柄输入已死」→ 挂机被停。实际游戏正在正常渲染。
// 修法：InputLivenessProbe.IsWatermarkPage 只认水印/版本号的一屏一律不参与活性判定；
// 引擎同时用 ScreenProbe.LooksLikePitch 豁免球场画面，并给判死加「3 分钟冷却 + 二次确认」。
public class InputLivenessWatermarkTests
{
    private readonly ITestOutputHelper _out;
    public InputLivenessWatermarkTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");

    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.1, 0.03)).ToList();
        return new OcrResult(list, 1, 1);
    }

    [Fact]
    public void 加载水印页_长度检查会放行_但必须认出是水印()
    {
        // 实机 00:51:10 的 [OCR原文]：实况足球在线 | X | 11.1.0.1.124916.124929.202611101.4611160063
        var ocr = Ocr(
            ("实况足球在线", 0.02, 0.97),
            ("X", 0.50, 0.05),
            ("11.1.0.1.124916.124929.202611101.4611160063", 0.05, 0.93));
        string sig = InputLivenessProbe.TextSignature(ocr);
        _out.WriteLine($"sig={sig} len={sig.Length}");
        Assert.True(InputLivenessProbe.IsReadable(sig));   // 长度检查骗得过 → 必须靠水印判定兜住
        Assert.True(InputLivenessProbe.IsWatermarkPage(ocr));
    }

    [Fact]
    public void 只有logo与版本号_算水印页()
    {
        var ocr = Ocr(("eFootball", 0.4, 0.5), ("PRO EVOLUTION SOCCER", 0.4, 0.6), ("V", 0.9, 0.9));
        Assert.True(InputLivenessProbe.IsWatermarkPage(ocr));
    }

    [Fact]
    public void 真菜单页_不是水印页()
    {
        var ocr = Ocr(
            ("全新版本巡回赛", 0.1, 0.10),
            ("前往比赛", 0.30, 0.62),
            ("比赛历史", 0.30, 0.68),
            ("确定", 0.72, 0.86),
            ("11.1.0.1.124916.124929.202611101.4611160063", 0.05, 0.93),
            ("实况足球在线", 0.02, 0.97));
        Assert.False(InputLivenessProbe.IsWatermarkPage(ocr));
    }

    [Fact]
    public void 加载页带提示文字_不是水印页()
    {
        // 加载页只要读出了一句真文字（"正在加载""请稍候"），就仍应参与活性判定
        var ocr = Ocr(("实况足球在线", 0.02, 0.97), ("正在加载", 0.42, 0.52));
        Assert.False(InputLivenessProbe.IsWatermarkPage(ocr));
    }

    [Fact]
    public void 英文菜单页_不是水印页()
    {
        var ocr = Ocr(("MATCH HISTORY", 0.2, 0.3), ("NEXT MATCH", 0.6, 0.8), ("OK", 0.5, 0.9));
        Assert.False(InputLivenessProbe.IsWatermarkPage(ocr));
    }

    [Fact]
    public void 空OCR_不算水印页()
    {
        // 完全空页交给 IsReadable 处理（本来就排除），这里不能反过来认定成水印页，
        // 免得以后有调用方只用水印判据时把黑屏也放过去
        Assert.False(InputLivenessProbe.IsWatermarkPage(new OcrResult(new List<OcrWord>(), 1, 1)));
    }

    [Fact]
    public void 加载水印页_共享决策只等不按键()
    {
        // 加载页按 A/B 只会把还没走完的开赛流程取消掉：决策必须返回 Wait（不碰手柄）
        var ocr = Ocr(("实况足球在线", 0.02, 0.97), ("X", 0.50, 0.05),
            ("11.1.0.1.124916.124929.202611101.4611160063", 0.05, 0.93));
        foreach (int streak in new[] { 1, 3, 6, 12, MatchBrain.WatermarkWaitGraceStreak })
        {
            var act = MatchBrain.Decide(MatchScreen.Unknown, ocr, streak);
            _out.WriteLine($"streak={streak} → {act.Kind} {act.WaitMs}ms {act.Target}");
            Assert.Equal(MatchActionKind.Wait, act.Kind);
        }
    }

    [Fact]
    public void 加载水印页等太久_仍会走到B逃生()
    {
        // 等了 WatermarkWaitGraceStreak 轮还是一字不差：不能无限干等，交回常规逃生
        var ocr = Ocr(("实况足球在线", 0.02, 0.97),
            ("11.1.0.1.124916.124929.202611101.4611160063", 0.05, 0.93));
        var act = MatchBrain.Decide(MatchScreen.Unknown, ocr, MatchBrain.WatermarkWaitGraceStreak + 1);
        _out.WriteLine($"{act.Kind} {act.WaitMs}ms {act.Target}");
        Assert.Equal(MatchActionKind.GamepadBack, act.Kind);
    }

    [Fact]
    public void 可读菜单页_共享决策照常试探()
    {
        // 提醒：水印豁免只针对「什么都读不出来」的页面，真菜单页的 A 试探/B 逃生节奏不变
        var ocr = Ocr(("全新版本巡回赛", 0.1, 0.10), ("比赛历史", 0.3, 0.66));
        Assert.Equal(MatchActionKind.PadConfirm, MatchBrain.Decide(MatchScreen.Unknown, ocr, 3).Kind);
        Assert.Equal(MatchActionKind.GamepadBack, MatchBrain.Decide(MatchScreen.Unknown, ocr, 7).Kind);
    }

    [Fact]
    public void 实机事故帧_停机前那一屏_现在会被豁免()
    {
        // publish\data\failures\20261120\005211_031_手柄输入无响应.jpg（00:52:11 停机时保存的异常画面）
        // ——就是那晚让挂机自己停掉的一屏。要求：要么认成水印页，要么认成球场，二者其一即豁免。
        var data = TestFrameLoader.LoadPngBgra(
            Path.Combine(TestDir, "cnmatch", "cn_input_dead_load_20261120.jpg"), out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        using var ocrEngine = new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false);
        var res = TestOcr.Run(ocrEngine, frame);
        string words = string.Join(" | ", res.Words.Select(x => x.NormText));
        bool watermark = InputLivenessProbe.IsWatermarkPage(res);
        bool pitch = ScreenProbe.LooksLikePitch(frame);
        _out.WriteLine($"{w}x{h} 水印={watermark} 球场={pitch}");
        _out.WriteLine($"[OCR] {words}");
        _out.WriteLine($"[指纹] {InputLivenessProbe.TextSignature(res)}");
        Assert.True(watermark || pitch, "事故帧必须命中水印页或球场探针之一，否则输入自检仍会误杀");
    }

    [Fact]
    public void 水印页签名_不会触发卡住判据的长度门槛()
    {
        // 回归护栏：确认水印页的 IsReadable 一定是 true（否则上面的豁免就是死代码，
        // 哪天有人把 MinSignatureChars 调到 80 也能在这里发现）
        var ocr = Ocr(("实况足球在线", 0.02, 0.97), ("X", 0.50, 0.05),
            ("11.1.0.1.124916.124929.202611101.4611160063", 0.05, 0.93));
        string sig = InputLivenessProbe.TextSignature(ocr);
        Assert.True(sig.Length >= InputLivenessProbe.MinSignatureChars);
        Assert.Equal(InputLivenessProbe.StuckMs, 45_000);
    }
}

using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 国服周活动右上角「手柄 | 小人」分段控件（用户 2026-11-25 实机截图，晋级挑战卡页）：
//   手柄 = 玩家自主操控，小人 = AI 代理操作；这类要托管打的页面必须让高光停在小人上。
// 本用例锁三件事：
//   ① 实机帧（高光在手柄，白色手柄 + 灰色小人）必须判 PlayerPad；
//   ② 同一帧把胶囊左右两半对调（= 高光在小人的样子）必须判 AiAgent；
//   ③ 比赛帧（右上角是「跳过比赛✅」横幅，更宽）不许被误认成这枚控件。
public class CnWeeklyControlModeTests
{
    private readonly ITestOutputHelper _out;
    public CnWeeklyControlModeTests(ITestOutputHelper output) => _out = output;

    private static string CnWeeklyDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "cnweekly");

    private static FrameData Load(string file)
    {
        using var mat = Cv2.ImRead(file, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        var data = new byte[(int)(bgra.Total() * bgra.ElemSize())];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }

    /// <summary>把胶囊左右两半对调（模拟「高光在小人」的另一半状态）。</summary>
    private static FrameData MirrorCapsule(FrameData src)
    {
        // 实机标定：整枚胶囊 x 0.670~0.752 / y 0.013~0.078（1280×720 内容帧）
        int x0 = (int)(src.Width * 0.670), x1 = (int)(src.Width * 0.752);
        int y0 = (int)(src.Height * 0.013), y1 = (int)(src.Height * 0.078);
        var dst = (byte[])src.Bgra.Clone();
        int w = src.Width;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= (x0 + x1) / 2; x++)
            {
                int mx = x1 - (x - x0);         // 相对右半的镜像列
                if (mx <= x || mx > x1) continue;
                int a = (y * w + x) * 4, b = (y * w + mx) * 4;
                for (int k = 0; k < 4; k++) (dst[a + k], dst[b + k]) = (dst[b + k], dst[a + k]);
            }
        }
        return new FrameData(dst, src.Width, src.Height, 0);
    }

    [Fact]
    public void RealFrame_PadFocused()
    {
        var frame = Load(Path.Combine(CnWeeklyDir, "cn_control_pad_focus_20261125.png"));
        var mode = CnWeeklyBrain.DetectControlMode(frame);
        _out.WriteLine($"mode={mode}");
        Assert.Equal(CnWeeklyBrain.ControlMode.PlayerPad, mode);
    }

    [Fact]
    public void MirroredFrame_AgentFocused()
    {
        var frame = MirrorCapsule(Load(Path.Combine(CnWeeklyDir, "cn_control_pad_focus_20261125.png")));
        var mode = CnWeeklyBrain.DetectControlMode(frame);
        _out.WriteLine($"mode={mode}");
        Assert.Equal(CnWeeklyBrain.ControlMode.AiAgent, mode);
    }

    // 比赛中右上角是「跳过比赛✅」横幅（宽≈0.18，比胶囊宽一倍）：不许被当成控制方式控件，
    // 否则比赛里会去点一个不存在的图标。
    [Fact]
    public void MatchFrame_NotMistakenAsControl()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "cnmatch",
            "cn_skip_banner_20261125.png");
        if (!File.Exists(p)) { _out.WriteLine("缺帧，跳过"); return; }
        var mode = CnWeeklyBrain.DetectControlMode(Load(p));
        _out.WriteLine($"mode={mode}");
        Assert.Equal(CnWeeklyBrain.ControlMode.Unknown, mode);
    }

    // 整窗帧（客户区 1280×720 + 顶部 31px 标题栏）里胶囊整体下移，仍必须认得出来：
    // 探针是在帧顶那条带里自己找胶囊包围盒的，不吃固定坐标。
    [Fact]
    public void WindowFrame_StillDetected()
    {
        var content = Load(Path.Combine(CnWeeklyDir, "cn_control_pad_focus_20261125.png"));
        int left = 1, right = 1, top = 31, bottom = 8;
        int w = content.Width + left + right, h = content.Height + top + bottom;
        var dst = new byte[w * h * 4];
        for (int y = 0; y < content.Height; y++)
            Buffer.BlockCopy(content.Bgra, y * content.Width * 4, dst,
                ((y + top) * w + left) * 4, content.Width * 4);
        var framed = new FrameData(dst, w, h, 0);

        var mode = CnWeeklyBrain.DetectControlMode(framed);
        _out.WriteLine($"mode={mode} 帧 {w}×{h}");
        Assert.Equal(CnWeeklyBrain.ControlMode.PlayerPad, mode);
    }

    // 「先确认托管在小人上，再允许下一步」的页面闸：不能按界面枚举白名单做。
    // 2026-11-25 实机，晋级挑战卡页（重试次数 0/3 + 重新挑战）因底栏「重置」被判成 Bracket，
    // 白名单漏掉它 → 控制方式从没核验过，页面被一路推进到开赛。
    [Theory]
    [InlineData(CwScreen.Bracket, MatchScreen.LeaguePage, true)]      // 实机这一页
    [InlineData(CwScreen.ChallengeCardHome, MatchScreen.LeaguePage, true)]
    [InlineData(CwScreen.EventHome, MatchScreen.LeaguePage, true)]
    [InlineData(CwScreen.EventsList, MatchScreen.LeaguePage, true)]
    [InlineData(CwScreen.None, MatchScreen.LeaguePage, true)]         // 主菜单/联赛主页
    [InlineData(CwScreen.None, MatchScreen.HasConfirm, false)]        // 弹窗覆盖层：不点，别点穿
    [InlineData(CwScreen.None, MatchScreen.Unknown, false)]
    public void ControlProbeAppliesToMenuPagesOnly(CwScreen cw, MatchScreen ml, bool expected)
        => Assert.Equal(expected, CnWeeklyBrain.IsControlProbePage(cw, ml));

    // 端到端钉这一页（用户 2026-11-25 投诉的那一页）：晋级挑战卡页在真实分类下会落进
    // Bracket（底栏「重置」），必须仍然过控制方式闸，且探针要认出「高光在手柄」。
    // 两条合起来 = 这一页会被先切到小人再推进。
    [Fact]
    public async Task RealChallengePage_IsGatedAndNeedsSwitch()
    {
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        using var ocr = new OcrRecognizer();
        var frame = Load(Path.Combine(CnWeeklyDir, "cn_control_pad_focus_20261125.png"));
        var ocrRes = await ocr.RecognizeAsync(frame);

        var cw = CnWeeklyBrain.Classify(det.Detect(frame), ocrRes);
        var mode = CnWeeklyBrain.DetectControlMode(frame);
        _out.WriteLine($"CwScreen={cw} ControlMode={mode} gated={CnWeeklyBrain.IsControlProbePage(cw, MatchScreen.LeaguePage)}");

        Assert.True(CnWeeklyBrain.IsControlProbePage(cw, MatchScreen.LeaguePage),
            $"这一页（判成 {cw}）必须过控制方式闸，否则会被直接推进到开赛");
        Assert.Equal(CnWeeklyBrain.ControlMode.PlayerPad, mode);
    }
}

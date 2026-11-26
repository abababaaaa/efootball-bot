using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 国服「跳过比赛✅」横幅的**帧几何**回归（2026-11-25 用户实机：国服周活动 MIL 6-0 BAR，
// 右上角「☰ 跳过比赛 ✅」全程挂着，bot 却从头到尾没按过暂停键）。
//
// 根因：绿勾核验原用一个写死的归一化窗 (x 0.930~0.990 / y 0.015~0.095)，那是按
// 1280×720 内容帧标的；实机 FrameData 是 WGC 整窗帧 = 客户区 + 标题栏/边框
// （客户区 1280×720 → 帧 1282×759；客户区 1024×576 → 帧 1026×615），
// 游戏内容整体下移约 30px（≈帧高 4~5%），绿勾从内容坐标 y≈0.062 漂到 0.10~0.11，
// 掉出窗口下沿。实测同一张图：内容帧 104 → 1282×759 帧 42 → 1026×615 帧只剩 6，
// 阈值 30 判死，跳过链路整条哑火（日志里只剩「比赛挂机中：随机点击 RT」）。
//
// 修法：SkipBannerHasCheck 增加随「跳过比赛」OCR 词框定位的自适应窗（与帧几何无关），
// 两处取最大。本用例锁住四种窗口几何都能认出绿勾、且无绿勾的目标 toast 仍被拒绝。
public class SkipBannerGeometryTests
{
    private readonly ITestOutputHelper _out;
    public SkipBannerGeometryTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
    private static string CnDir => Path.Combine(TestDir, "TestData", "cnmatch");

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

    /// <summary>把内容帧按 WGC 整窗帧几何补边（左/右/上/下）。</summary>
    private static FrameData Pad(FrameData src, int left, int right, int top, int bottom)
    {
        int w = src.Width + left + right, h = src.Height + top + bottom;
        var dst = new byte[w * h * 4];
        for (int y = 0; y < src.Height; y++)
            Buffer.BlockCopy(src.Bgra, y * src.Width * 4, dst, ((y + top) * w + left) * 4, src.Width * 4);
        return new FrameData(dst, w, h, 0);
    }

    private static FrameData Resize(FrameData src, int w, int h)
    {
        using var m = Mat.FromPixelData(src.Height, src.Width, MatType.CV_8UC4, src.Bgra);
        using var r = new Mat();
        Cv2.Resize(m, r, new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Area);
        var data = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(r.Data, data, 0, data.Length);
        return new FrameData(data, w, h, 0);
    }

    [Fact]
    public async Task SkipBanner_SurvivesWgcWindowGeometry()
    {
        using var ocr = new OcrRecognizer();
        var content = Load(Path.Combine(CnDir, "cn_skip_banner_20261125.png"));
        var ocrRes = await ocr.RecognizeAsync(content);
        Assert.True(ocrRes.ContainsJoined("跳过比赛"), "横幅必须读到「跳过比赛」");
        Assert.False(ocrRes.ContainsJoined("累计进球") || ocrRes.ContainsJoined("至少打进"),
            "这帧是真横幅，不是挑战目标 toast");

        var cases = new (string Name, FrameData Frame)[]
        {
            ("内容 1280×720（旧标定态）", content),
            ("窗口化 1280×720 → 帧 1282×759", Pad(content, 1, 1, 31, 8)),
            ("窗口化 1280×720 → 帧 1282×759（上 37）", Pad(content, 1, 1, 37, 2)),
            ("窗口化 1024×576 → 帧 1026×615", Pad(Resize(content, 1024, 576), 1, 1, 31, 8)),
        };

        foreach (var (name, frame) in cases)
        {
            int oldWindow = RegionProbe.CountGreenRef(frame, 0.930, 0.990, 0.015, 0.095);
            bool hasCheck = MatchBrain.SkipBannerHasCheck(frame, ocrRes);
            var action = MatchBrain.Decide(MatchScreen.InMatch, ocrRes, 2, frame: frame, allowMatchSkip: true);
            _out.WriteLine($"{name}: 帧 {frame.Width}×{frame.Height} 旧窗绿勾={oldWindow} " +
                           $"SkipBannerHasCheck={hasCheck} 动作={action.Kind}");

            Assert.True(hasCheck, $"{name}：带绿勾的横幅必须核验通过（旧窗计数 {oldWindow}）");
            Assert.Equal(MatchActionKind.PadStartPress, action.Kind);
        }
    }

    // 干扰项不能放松：同位置的「跳过比赛 / 累计进球0/3个或以上」目标 toast 没有绿勾，
    // 自适应窗也不许把它认成可跳横幅（否则每帧都开一次暂停菜单）。
    [Fact]
    public async Task GoalToast_StillRejected_InEveryGeometry()
    {
        using var ocr = new OcrRecognizer();
        var content = Load(Path.Combine(CnDir, "cn_skip_goal_toast.png"));
        var ocrRes = await ocr.RecognizeAsync(content);

        var cases = new (string Name, FrameData Frame)[]
        {
            ("内容 1280×720", content),
            ("帧 1282×759", Pad(content, 1, 1, 31, 8)),
            ("帧 1026×615", Pad(Resize(content, 1024, 576), 1, 1, 31, 8)),
        };

        foreach (var (name, frame) in cases)
        {
            bool hasCheck = MatchBrain.SkipBannerHasCheck(frame, ocrRes);
            var action = MatchBrain.Decide(MatchScreen.InMatch, ocrRes, 2, frame: frame, allowMatchSkip: true);
            _out.WriteLine($"{name}: SkipBannerHasCheck={hasCheck} 动作={action.Kind}");
            // toast 的关键判据是词排除（含 累计进球/至少打进），绿勾不是它的拦截手段
            // （toast 帧横幅底的广告板本身就有绿色像素，旧实现同样会误判 True）。
            Assert.Equal(MatchActionKind.MatchIdleTap, action.Kind);
        }
    }
}

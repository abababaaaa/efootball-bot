using EfootballBot.Core.Bot;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

// 满级自动换人：纯判定（界面分类/等级读取/排序箭头像素）+ 状态机全流程脚本（录制手柄）。
// 实机几何依据用户 2026-10-24 截图（1280×720）：详情页数据栏 表头 y≈0.376 / 值 y≈0.425，
// 等级列 x≈0.527、能力点列 x≈0.602；排序弹窗右上箭头 x≈0.937~0.969 / y≈0.080~0.149。
public class MaxLevelSwapTests
{
    /// <summary>构造以中心点定位的 OCR 词（宽 0.15 / 高 0.03，与实机词块量级一致）。</summary>
    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.075, cy - 0.015, 0.15, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1, 1);

    private static FrameData BlackFrame(int w = 640, int h = 360)
        => new(new byte[w * h * 4], w, h, 0);

    /// <summary>详情帧 + 能力点橙圈徽标（能力点值区涂橙色，x 0.55~0.66 / y 0.395~0.475）。</summary>
    private static FrameData OrangeFrame(int w = 640, int h = 360)
    {
        var f = BlackFrame(w, h);
        var px = (byte[])f.Bgra.Clone();
        for (int y = (int)(0.395 * h); y < (int)(0.475 * h); y++)
            for (int x = (int)(0.55 * w); x < (int)(0.66 * w); x++)
            {
                int i = (y * w + x) * 4;
                px[i] = 30; px[i + 1] = 150; px[i + 2] = 235; // BGRA 橙色
            }
        return new FrameData(px, w, h, 0);
    }

    private static readonly Action<string, EfootballBot.Core.Bot.LogLevel> SilentLog = (_, _) => { };
    private static readonly CancellationToken CT = CancellationToken.None;

    // ---------------- 界面分类 ----------------

    private static OcrWord[] LineupOcr() => new[]
    {
        W("完成", 0.90, 0.07), W("整体实力", 0.88, 0.18),
        W("战术", 0.10, 0.28), W("球队", 0.10, 0.40),
        W("替补球员", 0.10, 0.48), W("后备球员", 0.10, 0.55), W("自动配置球员", 0.10, 0.63),
        W("球员详细数据", 0.60, 0.92),
    };

    private static OcrWord[] DetailOcr(string lv, string lvMax, string pts) => new[]
    {
        W("球员动作", 0.42, 0.93), W("完成", 0.06, 0.93),
        W("身高", 0.06, 0.376), W("体重", 0.15, 0.376), W("年龄", 0.25, 0.376),
        W("实时更新", 0.33, 0.376), W("惯用脚", 0.42, 0.376),
        W("等级", 0.527, 0.376), W("能力点", 0.602, 0.376),
        W(lv, 0.520, 0.425), W("/" + lvMax, 0.548, 0.427), W(pts, 0.603, 0.425),
    };

    private static OcrResult PopupOcr() => Ocr(
        W("球员动作", 0.70, 0.10), W("等级训练完成", 0.70, 0.19), W("能力点分配", 0.70, 0.27),
        W("球员融合", 0.70, 0.48), W("锁定", 0.70, 0.55), W("取消", 0.70, 0.93));

    private static OcrResult AllocOcr(string cur) => Ocr(
        W("能力点分配", 0.16, 0.093), W("能力点", 0.28, 0.215),
        W(cur, 0.255, 0.272), W("/72", 0.30, 0.275), W("推荐分配", 0.62, 0.92), W("关闭", 0.90, 0.07));

    private static OcrResult HomeOcr() => Ocr(
        W("前往比赛", 0.17, 0.24), W("赛程表", 0.20, 0.50), W("兑换积分", 0.50, 0.40),
        W("返回", 0.13, 0.94), W("比赛计划", 0.78, 0.94));

    private static OcrResult BenchOcr(int overall = 2403) => Ocr(
        W("后备球员", 0.15, 0.07), W("Gedeon Verlaine", 0.18, 0.24),
        W("整体实力", 0.88, 0.20), W(overall.ToString(), 0.89, 0.26),
        W("剩余球员数:828", 0.88, 0.86));

    private static OcrResult SortOcr() => Ocr(
        W("排序", 0.57, 0.09), W("球员姓名", 0.72, 0.19), W("位置", 0.72, 0.28),
        W("整体评价", 0.72, 0.35), W("当前等级", 0.72, 0.56),
        W("取消", 0.57, 0.93), W("顺序", 0.74, 0.93));

    private static OcrResult FilterOcr() => Ocr(
        W("筛选", 0.24, 0.09), W("俱乐部球队", 0.40, 0.19), W("卡片类型", 0.40, 0.43),
        W("比赛风格", 0.40, 0.67), W("完成", 0.50, 0.93), W("取消", 0.90, 0.07));

    private static OcrResult CardTypeOcr() => Ocr(
        W("卡片类型", 0.50, 0.09), W("普通", 0.50, 0.24), W("状态火热", 0.50, 0.32),
        W("精选", 0.50, 0.39), W("不可用的球员", 0.50, 0.81), W("返回", 0.42, 0.93));

    [Fact]
    public void Classify_DistinctScreens()
    {
        Assert.Equal(SwapScreen.Lineup, MaxLevelSwapBrain.Classify(Ocr(LineupOcr())));
        Assert.Equal(SwapScreen.Detail, MaxLevelSwapBrain.Classify(Ocr(DetailOcr("37", "37", "0"))));
        Assert.Equal(SwapScreen.ActionPopup, MaxLevelSwapBrain.Classify(PopupOcr()));
        Assert.Equal(SwapScreen.Alloc, MaxLevelSwapBrain.Classify(AllocOcr("72")));
        Assert.Equal(SwapScreen.Bench, MaxLevelSwapBrain.Classify(BenchOcr()));
        Assert.Equal(SwapScreen.Sort, MaxLevelSwapBrain.Classify(SortOcr()));
        Assert.Equal(SwapScreen.Filter, MaxLevelSwapBrain.Classify(FilterOcr()));
        Assert.Equal(SwapScreen.CardType, MaxLevelSwapBrain.Classify(CardTypeOcr()));

        // 联赛主页：不得误判成巡检涉及界面（否则巡检收尾会误判卡住）
        Assert.Equal(SwapScreen.None, MaxLevelSwapBrain.Classify(HomeOcr()));
    }

    // ---------------- 等级 / 能力点读取 ----------------

    [Fact]
    public void ReadPlayerStatus_SplitWords_NotMaxed()
    {
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(DetailOcr("23", "26", "44")));
        Assert.NotNull(s);
        Assert.Equal((23, 26, 44), (s!.Level, s.LevelMax, s.Points));
        Assert.False(s.NeedsSwap);
    }

    [Fact]
    public void ReadPlayerStatus_MaxedWithPoints_NeedsSwap()
    {
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(DetailOcr("40", "40", "12")));
        Assert.NotNull(s);
        Assert.True(s!.NeedsSwap);
    }

    [Fact]
    public void ReadPlayerStatus_MaxedPointsZero_NoSwap()
    {
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(DetailOcr("37", "37", "0")));
        Assert.NotNull(s);
        Assert.False(s!.NeedsSwap);
    }

    [Fact]
    public void ReadPlayerStatus_AdjacentColumns_NoCrosstalk()
    {
        // 相邻列横向只差 ~0.05：「/26」离能力点表头 0.054，固定容差会把它并进能力点值
        // （2026-10-24 测试暴露）——值词必须按「离哪个表头更近」归属
        var minimal = new[]
        {
            W("等级", 0.527, 0.376), W("能力点", 0.602, 0.376),
            W("23", 0.520, 0.425), W("/26", 0.548, 0.427), W("44", 0.603, 0.425),
        };
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(minimal));
        Assert.NotNull(s);
        Assert.Equal((23, 26, 44), (s!.Level, s.LevelMax, s.Points));

        var full = DetailOcr("23", "26", "44");
        var s2 = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(full));
        Assert.NotNull(s2);
        Assert.Equal((23, 26, 44), (s2!.Level, s2.LevelMax, s2.Points));
    }

    [Fact]
    public void ReadPlayerStatus_MergedSlashWord()
    {
        // OCR 把 "37/37" 读成一个词（值列只有一条）仍可解析
        var s = MaxLevelSwapBrain.ReadPlayerStatus(Ocr(
            W("球员动作", 0.42, 0.93), W("等级", 0.527, 0.376), W("能力点", 0.602, 0.376),
            W("37/37", 0.527, 0.425), W("0", 0.603, 0.425)));
        Assert.NotNull(s);
        Assert.Equal((37, 37, 0), (s!.Level, s.LevelMax, s.Points));
    }

    [Fact]
    public void ReadPlayerStatus_CropGeometry()
    {
        // 裁切放大帧：数据栏相对几何放大（表头 y≈0.31、值 y≈0.58，容差带调大后仍可读）
        var ocr = Ocr(
            W("等级", 0.31, 0.31), W("能力点", 0.58, 0.31),
            W("23", 0.30, 0.58), W("/26", 0.335, 0.585), W("44", 0.585, 0.58));
        var s = MaxLevelSwapBrain.ReadPlayerStatus(ocr, headYMin: 0.15, valueMaxDy: 0.35);
        Assert.NotNull(s);
        Assert.Equal((23, 26, 44), (s!.Level, s.LevelMax, s.Points));
    }

    [Fact]
    public void ReadRemainingPoints_AllocPage()
    {
        // 分配页左栏：表头 y≈0.215（页首大字标题「能力点分配」y≈0.093 必须被 y 带排除）
        Assert.Equal(72, MaxLevelSwapBrain.ReadRemainingPoints(AllocOcr("72")));
        Assert.Equal(0, MaxLevelSwapBrain.ReadRemainingPoints(AllocOcr("0")));
    }

    [Theory]
    [InlineData("27/29", 27, 29)]
    [InlineData("37/37", 37, 37)]
    [InlineData("537", 5, 37)]    // 斜杠丢失：3 位按 cur ≤ max 拆（不能拆成 53/7）
    [InlineData("2729", 27, 29)]  // 斜杠丢失：4 位等分
    public void ParseLevelPair_HandlesLostSlash(string raw, int cur, int max)
    {
        Assert.Equal((cur, max), MaxLevelSwapBrain.ParseLevelPair(raw));
    }

    [Fact]
    public async Task MaxedZeroPoints_SkipsAlloc_GoesStraightToSwap()
    {
        // 满级 + 能力点 0（白字 0，无橙圈）：不分配，B 关弹窗退详情后直接 A 选中换人
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        int maxedCalls = 0;
        reader.Enqueue(new PlayerStatus(40, 40, 0));
        var lineup = Ocr(LineupOcr());
        Action<int> onMaxed = _ => maxedCalls++;

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT);  // Opening → Inspect
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT);  // Inspect：Y
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT); // 无橙圈 → X
        await c.StepCore(PopupOcr(), BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT); // 满级无点 → B
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT); // LineupSelect：B 退详情
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT);  // A 选中
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, onMaxed, null, CT);  // 侧栏 → 后备球员

        Assert.Equal(new[] { "Y", "Y", "X", "B", "B", "A", "Leftx8", "Upx5", "Downx3", "A" }, pad.Log);
        Assert.Equal(1, maxedCalls);
    }

    [Fact]
    public async Task Alloc_PressesYOnceThenBack_NoInfiniteRetry()
    {
        // 分配页只按一次 Y 就 B 返回（用户 2026-10-24 实机：OCR 丢斜杠曾致无限按 Y）
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        reader.Enqueue(new PlayerStatus(40, 40, 12));
        reader.Enqueue(new PlayerStatus(40, 40, 0));
        var lineup = Ocr(LineupOcr());

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(Ocr(DetailOcr("40", "40", "12")), OrangeFrame(), pad, reader.Read, SilentLog, null, null, CT); // 橙圈 → X
        await c.StepCore(PopupOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // ↓+A
        // 分配页连续出现多轮（无论读出什么数字）也只按一次 Y、然后 B
        await c.StepCore(AllocOcr("72"), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // Y
        await c.StepCore(AllocOcr("72"), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // B（已分配完成）
        var tail = pad.Log.Skip(3).ToList();
        Assert.Equal(new[] { "Down", "A", "Y", "B" }, tail);
    }

    // ---------------- 排序箭头像素判定 ----------------

    private static FrameData ArrowFrame(bool up)
    {
        int w = 640, h = 360;
        var bgra = new byte[w * h * 4];
        void Lit(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int i = (y * w + x) * 4;
            bgra[i] = bgra[i + 1] = bgra[i + 2] = 255;
        }
        // 探针区域：x 588~627（0.92~0.98）；实机箭头为「菱形头 + 细杆」emoji 形态
        // （2026-10-24 实机帧 dump：最宽 22px 的菱形 + 6px 杆），合成帧按同款画
        int cx = 607;
        int[] diamond = { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 22, 22, 22 };
        int y = 24;
        if (up)
        {
            foreach (var hw in diamond) { for (int x = cx - hw / 2; x <= cx + hw / 2; x++) Lit(x, y); y++; }
            for (; y <= 51; y++) { for (int x = cx - 3; x <= cx + 3; x++) Lit(x, y); }   // 杆在下半
        }
        else
        {
            for (; y <= 37; y++) { for (int x = cx - 3; x <= cx + 3; x++) Lit(x, y); }   // 杆在上半
            for (int k = diamond.Length - 1; k >= 0; k--)
            {
                for (int x = cx - diamond[k] / 2; x <= cx + diamond[k] / 2; x++) Lit(x, y);
                y++;
            }
        }
        return new FrameData(bgra, w, h, 0);
    }

    [Fact]
    public void SortArrow_UpDetected() => Assert.True(MaxLevelSwapBrain.IsSortArrowUp(ArrowFrame(up: true)));

    [Fact]
    public void SortArrow_DownDetected() => Assert.False(MaxLevelSwapBrain.IsSortArrowUp(ArrowFrame(up: false)));

    [Fact]
    public void SortArrow_BlackFrame_Unknown() => Assert.Null(MaxLevelSwapBrain.IsSortArrowUp(BlackFrame()));

    // ---------------- 实机帧校准（用户 2026-10-24 排序弹窗截图，1280×720） ----------------

    private static string SwapDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "swap");

    private static OcrRecognizer RealOcr() => new();

    private static FrameData LoadFrame(string file)
    {
        using var mat = Cv2.ImRead(Path.Combine(SwapDir, file), ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }

    [Fact]
    public void RealSortPopup_ArrowDirection()
    {
        Assert.False(MaxLevelSwapBrain.IsSortArrowUp(LoadFrame("sort_arrow_down.png")));
        Assert.True(MaxLevelSwapBrain.IsSortArrowUp(LoadFrame("sort_arrow_up.png")));
    }

    [Fact]
    public void RealSortPopup_OcrClassifySort()
    {
        using var ocr = new OcrRecognizer();
        foreach (var f in new[] { "sort_arrow_down.png", "sort_arrow_up.png" })
            Assert.Equal(SwapScreen.Sort, MaxLevelSwapBrain.Classify(TestOcr.Run(ocr, LoadFrame(f))));
    }

    [Fact]
    public void RealSortPopup_OverallRatingChecked_Probe()
    {
        // 2026-10-27 用户实机帧：勾在「整体评价」行 + 箭头向上 → 已设置探针 true
        var f = LoadFrame("sort_overall_checked.png");
        var ocr = TestOcr.Run(RealOcr(), f);
        Assert.True(MaxLevelSwapBrain.IsOverallRatingChecked(ocr, f));
        Assert.True(MaxLevelSwapBrain.IsSortArrowUp(f));
        Assert.Equal(SwapScreen.Sort, MaxLevelSwapBrain.Classify(ocr));

        // 旧帧（2026-10-24）：勾在「当前等级」行 → 整体评价未勾，必须重新设置
        var old = LoadFrame("sort_checked_up.png");
        Assert.False(MaxLevelSwapBrain.IsOverallRatingChecked(TestOcr.Run(RealOcr(), old), old));

        // sort_arrow_up.png：勾在「整体评价」行（历史帧，同一判据）
        var up = LoadFrame("sort_arrow_up.png");
        Assert.True(MaxLevelSwapBrain.IsOverallRatingChecked(TestOcr.Run(RealOcr(), up), up));
    }

    [Fact]
    public void RealFilterPage_CardTypeSet_Probe()
    {
        // p2 实机帧：卡片类型右侧值 = 精选/高光/… → 已设置
        var ocr = TestOcr.Run(RealOcr(), LoadFrame("filter_cardtype_set.png"));
        Assert.True(MaxLevelSwapBrain.IsCardTypeFilterSet(ocr));

        // 合成帧：值 = 无 / 值词缺失 → 未设置
        Assert.False(MaxLevelSwapBrain.IsCardTypeFilterSet(FilterOcr()));
        var none = Ocr(W("筛选", 0.24, 0.09), W("俱乐部球队", 0.40, 0.19), W("卡片类型", 0.40, 0.43),
            W("无", 0.72, 0.43), W("比赛风格", 0.40, 0.67), W("完成", 0.50, 0.93));
        Assert.False(MaxLevelSwapBrain.IsCardTypeFilterSet(none));
    }

    [Fact]
    public async Task RealReadFailFrame_ColumnCrop_RecoversLevel()
    {
        // 实机落帧（引擎捕获 1227×720）：全帧 OCR 大字「1」漏读只剩 /31 → 曾判「读不出」跳过；
        // 分列放大重读必须救回 (1, 31, 能力点 0)
        var frame = LoadFrame("read_fail_1227.png");
        using var ocrEngine = RealOcr();
        var full = TestOcr.Run(ocrEngine, frame);
        var status = await MaxLevelSwapController.ReadPartsFromFrameAsync(
            ocrEngine, full, frame, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal((1, 31, 0), (status!.Level, status.LevelMax, status.Points));
    }

    [Fact]
    public void SharedClassify_SquadPage()
    {
        // 首发站位页（比赛计划内）：侧栏词 + 整体实力 → LeagueSquadPage；联赛主页（前往比赛可见）不算
        var squad = Ocr(W("完成", 0.90, 0.07), W("整体实力", 0.88, 0.18),
            W("战术", 0.10, 0.28), W("球队", 0.10, 0.40),
            W("替补球员", 0.10, 0.48), W("后备球员", 0.10, 0.55), W("自动配置球员", 0.10, 0.63),
            W("球员详细数据", 0.60, 0.92));
        Assert.Equal(MlScreen.LeagueSquadPage, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), squad));
        Assert.Equal(MlScreen.LeagueSquadPage, MyLeagueBrain.Classify(
            Array.Empty<YoloDetection>(),
            Ocr(W("整体实力", 0.88, 0.18), W("替补球员", 0.10, 0.48), W("球员详细数据", 0.60, 0.92)))); // 侧栏词漏读兜底
        var home = Ocr(W("前往比赛", 0.17, 0.24), W("整体实力", 0.88, 0.18),
            W("替补球员", 0.10, 0.48), W("自动配置球员", 0.10, 0.63));
        Assert.NotEqual(MlScreen.LeagueSquadPage, MyLeagueBrain.Classify(Array.Empty<YoloDetection>(), home));
    }

    [Fact]
    public void Decide_SquadPage_Back()
    {
        // 站位页：B 返回联赛主页交还正常流程（用户手动停在阵容查看页时不再无操作）
        var a = MyLeagueBrain.Decide(MlScreen.LeagueSquadPage, Ocr(), 0, false, null);
        Assert.Equal(MlActionKind.GamepadBack, a.Kind);
    }

    [Fact]
    public async Task StartOnPlan_RunsImmediately_WithoutY()
    {
        // 用户手动停在比赛计划页：跳过按 Y，直接从门将开始巡检
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        reader.Enqueue(new PlayerStatus(23, 26, 44));
        await c.StartOnPlanCore(pad, SilentLog);
        Assert.Empty(pad.Log); // 未按任何键
        await c.StepCore(Ocr(LineupOcr()), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Y" }, pad.Log); // 直接进入门将巡检
    }

    [Fact]
    public async Task SortAlreadyChecked_BReturnsImmediately()
    {
        // 排序弹窗已勾「整体评价」+ 箭头为上：不导航不按 A，直接 B 返回继续筛选
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        var lineup = Ocr(LineupOcr());
        reader.Enqueue(new PlayerStatus(40, 40, 0)); // 满级 0 点 → 直接换人路径
        var checkedSortOcr = TestOcr.Run(RealOcr(), LoadFrame("sort_overall_checked.png"));
        var checkedFrame = LoadFrame("sort_overall_checked.png");

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 无橙圈 → X
        await c.StepCore(PopupOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // 满级无点 → B
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // B 退详情
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // A 选中
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // 侧栏
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // ←+A 开排序

        int before = pad.Log.Count;
        await c.StepCore(checkedSortOcr, checkedFrame, pad, reader.Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "B" }, pad.Log.Skip(before).ToArray());
    }

    [Fact]
    public async Task FilterAlreadySet_CompletesDirectly()
    {
        // 筛选页卡片类型已设置：归顶 ↓×9 直达完成，不进卡牌子页重复勾选
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        var setFilterOcr = TestOcr.Run(RealOcr(), LoadFrame("filter_cardtype_set.png"));

        // 手动把内部状态推到 FilterWait：借 FullFlow 前置太长，直接从排序阶段复用驱动
        var lineup = Ocr(LineupOcr());
        reader.Enqueue(new PlayerStatus(40, 40, 0));
        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 无橙圈 → X
        await c.StepCore(PopupOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // 满级无点 → B
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // B 退详情
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        // 排序弹窗已正确（p1 帧）→ B；回列表 ←↓A 开筛选
        await c.StepCore(TestOcr.Run(RealOcr(), LoadFrame("sort_checked_up.png")), LoadFrame("sort_checked_up.png"),
            pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);

        int before = pad.Log.Count;
        await c.StepCore(setFilterOcr, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Upx9", "Downx9", "A" }, pad.Log.Skip(before).ToArray());
    }

    // ---------------- 状态机全流程（录制手柄） ----------------

    private sealed class FakePad : ISwapPad
    {
        public readonly List<string> Log = new();
        public Task Y() { Log.Add("Y"); return Task.CompletedTask; }
        public Task X() { Log.Add("X"); return Task.CompletedTask; }
        public Task Confirm() { Log.Add("A"); return Task.CompletedTask; }
        public Task Back() { Log.Add("B"); return Task.CompletedTask; }
        public Task Dpad(PadDir d) { Log.Add(d.ToString()); return Task.CompletedTask; }
        public Task DpadRepeat(PadDir d, int n) { Log.Add($"{d}x{n}"); return Task.CompletedTask; }
    }

    private sealed class ScriptReader
    {
        private readonly Queue<PlayerStatus> _q = new();
        public void Enqueue(PlayerStatus s) => _q.Enqueue(s);
        public Task<PlayerStatus?> Read(OcrResult ocr, FrameData f)
            => Task.FromResult<PlayerStatus?>(_q.Count > 0 ? _q.Dequeue() : new PlayerStatus(20, 30, 5));
    }

    /// <summary>驱动到「后备球员页、排序弹窗即将打开」：门将满级 → 分配 → 侧栏 → 后备列表 → ←+A。</summary>
    private static async Task<FakePad> DriveToSortAsync(MaxLevelSwapController c, ScriptReader reader)
    {
        var pad = new FakePad();
        var lineup = Ocr(LineupOcr());
        reader.Enqueue(new PlayerStatus(40, 40, 12)); // 详情：满级 → 换人
        reader.Enqueue(new PlayerStatus(40, 40, 0));  // 分配后详情核验

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // Opening → Inspect
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // Inspect pos0：Y
        await c.StepCore(Ocr(DetailOcr("40", "40", "12")), OrangeFrame(), pad, reader.Read, SilentLog, null, null, CT); // 橙圈=有加点 → X
        await c.StepCore(PopupOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // ↓+A
        await c.StepCore(AllocOcr("72"), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // Y 推荐分配
        await c.StepCore(AllocOcr("0"), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);   // B 关闭
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // B
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // A 选中
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // 侧栏 → 后备球员
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // ←+A 开排序
        return pad;
    }

    [Fact]
    public async Task SortArrowDown_PressesYToFlip()
    {
        var c = new MaxLevelSwapController();
        var pad = await DriveToSortAsync(c, new ScriptReader());

        int before = pad.Log.Count;
        await c.StepCore(SortOcr(), ArrowFrame(up: false), pad,
            new ScriptReader().Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Upx6", "Downx2", "Y", "A" }, pad.Log.Skip(before).ToArray());
    }

    [Fact]
    public async Task SortArrowUp_NoFlip()
    {
        var c = new MaxLevelSwapController();
        var pad = await DriveToSortAsync(c, new ScriptReader());

        int before = pad.Log.Count;
        await c.StepCore(SortOcr(), ArrowFrame(up: true), pad,
            new ScriptReader().Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Upx6", "Downx2", "A" }, pad.Log.Skip(before).ToArray());
    }

    [Fact]
    public async Task FullFlow_SwapsMaxedPlayer_AndFinishes()
    {
        var c = new MaxLevelSwapController();
        var reader = new ScriptReader();
        var pad = await DriveToSortAsync(c, reader);

        var lineup = Ocr(LineupOcr());
        var detailNot = Ocr(DetailOcr("23", "26", "44"));
        var popupNotMaxed = Ocr(   // 未满级弹窗：首项灰字「等级训练」（用户 2026-10-25 对比图）
            W("球员动作", 0.70, 0.10), W("等级训练", 0.70, 0.19), W("能力点分配", 0.70, 0.27),
            W("技巧训练", 0.70, 0.37), W("位置训练", 0.70, 0.47), W("球员融合", 0.70, 0.57),
            W("锁定", 0.70, 0.65), W("取消", 0.70, 0.93));
        var upArrow = ArrowFrame(up: true);
        for (int k = 0; k < 10; k++) reader.Enqueue(new PlayerStatus(23, 26, 44));

        // 排序 → 筛选 → 卡片类型 → 应用 → 选亮卡 A（一定成功）→ B 返回站位页 → → 进阵继续巡检
        await c.StepCore(SortOcr(), upArrow, pad, reader.Read, SilentLog, null, null, CT);        // ↑×6 ↓×5 A
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // ← ↓ A 开筛选
        await c.StepCore(FilterOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // ↑×9 ↓×4 A
        await c.StepCore(CardTypeOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 勾选 + B
        await c.StepCore(FilterOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // ↓×4 ↓ A 应用
        await c.StepCore(BenchOcr(2403), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 暗卡探针(黑帧=0) + A 交换
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // SwapExit r1（换人动画，观察）
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // r2：B 返回站位页
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // → 进阵（固定第 3 位）→ Rewind
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // Rewind：换的是第 1 个 → 3→2（→）

        // 位 1~10 巡检（非满级 B 退出）；路径 ↑←↑→→→→→↓←
        PadDir[] moves = MaxLevelSwapController.CursorMoves;
        for (int p = 1; p <= 10; p++)
        {
            await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // Inspect：move + Y
            await c.StepCore(detailNot, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);   // DetailRead：X 球员动作
            await c.StepCore(popupNotMaxed, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // ActionPopup：未满级 → B
            await c.StepCore(detailNot, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);   // InspectAdvance：Detail → B
            await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // InspectAdvance：Lineup → 推进
        }

        // 收尾：Done（B）→ 主页 → Reset 交还
        int? wait = await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.NotNull(wait);
        wait = await c.StepCore(HomeOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.Null(wait);
        Assert.True(c.Idle);

        // ---- 断言关键按键序列 ----
        var log = pad.Log.ToArray();
        int i = 0;
        Assert.Equal("Y", log[i++]);                                // 进比赛计划
        Assert.Equal("Y", log[i++]);                                // 门将巡检（无移动）
        Assert.Equal("X", log[i++]);                                // 球员动作
        Assert.Equal("Down", log[i++]); Assert.Equal("A", log[i++]); // ↓ 能力点分配 + A
        Assert.Equal("Y", log[i++]);                                // 推荐分配
        Assert.Equal("B", log[i++]);                                // 关闭分配页
        Assert.Equal("B", log[i++]);                                // 退出详情
        Assert.Equal("A", log[i++]);                                // 选中球员
        Assert.Equal(new[] { "Leftx8", "Upx5", "Downx3", "A" }, log[i..(i += 4)]); // 侧栏 → 后备球员
        Assert.Equal(new[] { "Left", "A" }, log[i..(i += 2)]);      // 开排序
        Assert.Equal(new[] { "Upx6", "Downx2", "A" }, log[i..(i += 3)]); // 整体评价（箭头朝上不翻）
        Assert.Equal(new[] { "Left", "Down", "A" }, log[i..(i += 3)]);   // 开筛选
        Assert.Equal(new[] { "Upx9", "Downx4", "A" }, log[i..(i += 3)]); // → 卡片类型
        Assert.Equal(new[] { "Upx9", "Downx2" }, log[i..(i += 2)]);
        for (int k = 0; k < 6; k++)
            Assert.Equal(new[] { "A", "Down" }, log[i..(i += 2)]);  // 6 张高阶卡逐个勾选
        Assert.Equal("B", log[i++]);                                // 回筛选页
        Assert.Equal(new[] { "Upx9", "Downx9", "A" }, log[i..(i += 3)]); // 归顶 ↓×9 → 完成（应用筛选）
        Assert.Equal("A", log[i++]);                                // 交换球员
        Assert.Equal("B", log[i++]);                                // 换人后 B 返回站位页
        Assert.Equal("Right", log[i++]);                            // → 进阵（固定第 3 位）
        Assert.Equal("Right", log[i++]);                            // 换的是第 1 个 → 从第 3 位 → 第 2 位
        Assert.Equal("Y", log[i++]);                                // 第 2 位巡检：Y 打开详情（已在位上）
        // 位 1~10：未满级弹窗（X → 弹窗「等级训练」B → 详情 B）+ 各自路径
        for (int p = 1; p <= 10; p++)
        {
            Assert.Equal("X", log[i++]);                            // 球员动作
            Assert.Equal("B", log[i++]);                            // 弹窗「等级训练」→ 取消
            Assert.Equal("B", log[i++]);                            // 退出详情
            if (p < 10)
            {
                Assert.Equal(moves[p].ToString(), log[i++]);        // 移动到下一位
                Assert.Equal("Y", log[i++]);
            }
        }
        Assert.Equal("B", log[i++]);                                // p=10 推进即完成 → B
        Assert.Equal("B", log[i]);                                  // Done 收尾 B（主页识别前）
        Assert.Equal(i + 1, log.Length);                            // 没有多余按键
    }

    // ---------------- 换入验证 / 暗卡跳过 / 同名卡重试（用户 2026-10-25 实机规则） ----------------

    /// <summary>驱动到「筛选已应用、后备列表就绪」：下一步 StepCore(BenchOcr) 即选卡+按 A 交换。</summary>
    private static async Task<FakePad> DriveToSwapAsync(MaxLevelSwapController c, ScriptReader reader)
    {
        var pad = await DriveToSortAsync(c, reader);
        await c.StepCore(SortOcr(), ArrowFrame(up: true), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(FilterOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(CardTypeOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(FilterOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        return pad;
    }

    [Fact]
    public async Task SwapFlow_BRightThenEnterField_Continue()
    {
        // 用户 2026-10-25 终版：选亮卡 A 一定成功 → B 返回站位页（侧栏光标停在「后备球员」、
        // 右侧预览后备名单且带剩余球员数——仍按站位页判）→ → 进阵（光标回到刚换人的球员）
        // → 继续巡检，全程不退出比赛计划
        var c = new MaxLevelSwapController();
        var reader = new ScriptReader();
        var pad = await DriveToSwapAsync(c, reader);
        var lineup = Ocr(LineupOcr());
        var roster = Ocr(LineupOcr().Append(W("剩余球员数:828", 0.88, 0.86)).ToArray());
        Assert.Equal(SwapScreen.Lineup, MaxLevelSwapBrain.Classify(roster)); // 预览页按站位页判
        Assert.Equal(SwapScreen.Bench, MaxLevelSwapBrain.Classify(BenchOcr())); // 卡格子页仍后备

        int before = pad.Log.Count;
        await c.StepCore(BenchOcr(2403), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // A 交换
        for (int r = 0; r < 8; r++)
            await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 动画期反复 B
        await c.StepCore(roster, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);        // → 进阵 → Rewind
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);         // Rewind：3→2（→）
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);         // Inspect 第 2 位：Y

        var seq = pad.Log.Skip(before).ToList();
        Assert.Equal("A", seq[0]);
        Assert.Equal("Right", seq[^3]);   // → 进阵（固定第 3 位）
        Assert.Equal("Right", seq[^2]);   // 换的是第 1 个 → 3→2
        Assert.Equal("Y", seq[^1]);       // 第 2 位巡检
        Assert.All(seq[1..^3], k => Assert.Equal("B", k)); // 动画期全是 B，无被挡重试/无 →A 组合
    }

    [Fact]
    public async Task SwapExit_ConfirmDialog_UpThenConfirm()
    {
        // 确认窗盖在站位页上（页面词仍在 + 确定/取消）：上移避开取消再 A，之后 → 进阵继续
        var c = new MaxLevelSwapController();
        var reader = new ScriptReader();
        var pad = await DriveToSwapAsync(c, reader);

        int before = pad.Log.Count;
        await c.StepCore(BenchOcr(2403), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // A 交换
        var words = new List<OcrWord>(LineupOcr()) { W("确定", 0.50, 0.50), W("取消", 0.42, 0.62) };
        await c.StepCore(new OcrResult(words, 1, 1), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 确认窗 → Up + A
        await c.StepCore(Ocr(LineupOcr()), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 站位页 → → 进阵
        await c.StepCore(Ocr(LineupOcr()), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // Rewind：3→2（→）
        Assert.Equal(new[] { "A", "Up", "A", "Right", "Right" }, pad.Log.Skip(before).ToArray());
    }

    [Fact]
    public async Task SortFilter_OncePerSession_SecondSwapGoesStraightToCard()
    {
        // 用户 2026-10-25：本轮挂机完成过一次排序+筛选后，后续换人不再进出排序/筛选页直奔选卡
        var c = new MaxLevelSwapController();
        var reader = new ScriptReader();
        var pad = await DriveToSwapAsync(c, reader);
        var lineup = Ocr(LineupOcr());

        // 第一次换人：应用筛选（置会话标志）→ A → B 返回站位页 → → 进阵 → Rewind
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // BenchSwap A
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // r1 观察
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // r2：B 返回
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // → 进阵 → Rewind
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // Rewind（无键，_pos=1）

        // 第二次换人（位 1 满级 0 点）：详情 X → 弹窗 B → 退详情 → A 选中 → 侧栏 → 后备页
        reader.Enqueue(new PlayerStatus(40, 40, 0));
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // Inspect pos1：Up + Y
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 无橙圈 → X
        await c.StepCore(PopupOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // 满级无点 → B
        await c.StepCore(Ocr(DetailOcr("40", "40", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // B 退详情
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // A 选中
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // 侧栏 → 后备球员
        int before = pad.Log.Count;
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // ★不再开排序：直奔选卡
        Assert.Empty(pad.Log.Skip(before).ToArray());                                                 // 本轮零按键
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // BenchSwap：A
        Assert.Equal(new[] { "A" }, pad.Log.Skip(before).ToArray());                                  // 只有选卡 A（黑帧不跳卡）
    }

    [Fact]
    public async Task GameErrorDialog_DismissedWithA_InAnyPhase()
    {
        // 游戏错误弹窗（功能目前无法使用 / C_XIQX_0019）：任何阶段全局拦截按 A 关闭
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        await c.StartCore(pad, SilentLog); // Y 打开比赛计划
        var err = Ocr(W("功能目前无法使用", 0.5, 0.30), W("不便之处敬请原谅", 0.5, 0.45), W("OK", 0.5, 0.60));
        await c.StepCore(err, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Y", "A" }, pad.Log);
    }

    // ---------------- 实机帧回归（暗卡跳过 / 半截等级合并解析） ----------------

    [Fact]
    public async Task RealBenchFrame_DarkCardsSkipped_FirstBrightIsIndex1()
    {
        // 用户 2026-10-25 截图：第一排左一（Koné）与第三排右一（Torres）头像压暗不可选，
        // 第一张亮卡 = 第一排第二张（下标 1）→ BenchSwap 按 →×1 再 A
        var frame = LoadFrame("bench_dark_cards.png");
        using var ocr = RealOcr();
        var res = TestOcr.Run(ocr, frame);
        Assert.Equal(1, MaxLevelSwapBrain.FirstBrightCardIndex(res, frame));

        var c = new MaxLevelSwapController();
        var reader = new ScriptReader();
        var pad = await DriveToSwapAsync(c, reader);
        int before = pad.Log.Count;
        await c.StepCore(res, frame, pad, reader.Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Rightx1", "A" }, pad.Log.Skip(before).ToArray());
    }

    [Fact]
    public async Task RealBenchFrame_SecondEntry_CompressedGrid_SkipsDark()
    {
        // 实机 2026-10-25 第二次进后备列表（跳过排序/筛选时）：网格列距 0.097 vs 标定 0.116，
        // 固定列会探错格按到暗卡——动态列（名字行聚类）必须仍然跳过压暗的第一张
        var frame = LoadFrame("bench_second_entry.png");
        using var ocr = RealOcr();
        var res = TestOcr.Run(ocr, frame);
        Assert.Equal(5, MaxLevelSwapBrain.BenchColumnCenters(res).Length);
        Assert.Equal(1, MaxLevelSwapBrain.FirstBrightCardIndex(res, frame));

        var c = new MaxLevelSwapController();
        var reader = new ScriptReader();
        var pad = await DriveToSwapAsync(c, reader);
        int before = pad.Log.Count;
        await c.StepCore(res, frame, pad, reader.Read, SilentLog, null, null, CT);
        Assert.Equal(new[] { "Rightx1", "A" }, pad.Log.Skip(before).ToArray());
    }

    [Fact]
    public void RealBenchFrame_CardHighlight_DarkVsBrightGap()
    {
        // 暗卡（r1c1/r3c5）高亮像素远低于亮卡（r1c2/r4c3），阈值分离度见 CardHighlightPixels
        var frame = LoadFrame("bench_dark_cards.png");
        using var ocr = RealOcr();
        var res = TestOcr.Run(ocr, frame);
        var cols = MaxLevelSwapBrain.BenchColumnCenters(res);
        Assert.Equal(5, cols.Length); // 名字行聚类得 5 列
        Assert.True(MaxLevelSwapBrain.CardHighlightPixels(frame, 0, 0, cols) < 200);  // Koné 压暗
        Assert.True(MaxLevelSwapBrain.CardHighlightPixels(frame, 2, 4, cols) < 200);  // Torres 压暗
        Assert.True(MaxLevelSwapBrain.CardHighlightPixels(frame, 0, 1, cols) > 250);  // 亮卡
        Assert.True(MaxLevelSwapBrain.CardHighlightPixels(frame, 3, 2, cols) > 250);
        // 黑帧/无名单：探针不可判 → 保守不跳
        Assert.Equal(0, MaxLevelSwapBrain.FirstBrightCardIndex(HomeOcr(), BlackFrame()));
    }

    [Fact]
    public async Task RealReadFailFrame_SplitSources_MergedLevel()
    {
        // 实机落帧（2026-10-25 引擎捕获 1227×720，swap_read_fail_20261025_032020）：
        // 整帧 OCR 读到「/32」漏「1」、裁切帧读到「1」丢「/32」——两源合并必须救回 (1, 32, 0)
        var frame = LoadFrame("read_fail_split_1231.png");
        using var ocrEngine = RealOcr();
        var full = TestOcr.Run(ocrEngine, frame);
        var status = await MaxLevelSwapController.ReadPartsFromFrameAsync(
            ocrEngine, full, frame, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal((1, 32, 0), (status!.Level, status.LevelMax, status.Points));
    }

    [Fact]
    public async Task RealReadFailFrames_20261025_11_VotingAndSentinel()
    {
        // 实机 2026-10-25 11:04 四连读不出（Manu Koné 1/29）：大字「1」整帧恒漏、
        // 紧窗原始读 L/J、紧窗二值读 1、宽窗原始混碎片 5——多配置投票收敛到 1；
        // 110447/110454 连 /29 都全配置丢 → max 哨兵 0（等级 1 远低于上限按未满级放行）
        using var ocrEngine = RealOcr();
        foreach (var (f, lv, mx) in new[] {
            ("read_fail_110427.png", 1, 29), ("read_fail_110447.png", 1, 0),
            ("read_fail_110454.png", 1, 0), ("read_fail_110500.png", 1, 29) })
        {
            var frame = LoadFrame(f);
            var full = TestOcr.Run(ocrEngine, frame);
            var status = await MaxLevelSwapController.ReadPartsFromFrameAsync(
                ocrEngine, full, frame, CancellationToken.None);
            Assert.NotNull(status);
            Assert.Equal((lv, mx, 0), (status!.Level, status.LevelMax, status.Points));
        }
    }

    [Fact]
    public async Task NotMaxed_PopupLevelTraining_GoesToNext()
    {
        // 未满级（弹窗首项灰字「等级训练」）：B 关弹窗 + B 退详情，直接下一位（用户四路矩阵 1/3 行）
        var c = new MaxLevelSwapController();
        var pad = new FakePad();
        var reader = new ScriptReader();
        var lineup = Ocr(LineupOcr());
        var popupNotMaxed = Ocr(
            W("球员动作", 0.70, 0.10), W("等级训练", 0.70, 0.19), W("能力点分配", 0.70, 0.27),
            W("锁定", 0.70, 0.65), W("取消", 0.70, 0.93));

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // Opening → Inspect
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // Y 进详情
        int before = pad.Log.Count;
        await c.StepCore(Ocr(DetailOcr("1", "29", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // X
        Assert.Equal(new[] { "X" }, pad.Log.Skip(before).ToArray());
        before = pad.Log.Count;
        await c.StepCore(popupNotMaxed, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 未满级 → B
        await c.StepCore(Ocr(DetailOcr("1", "29", "0")), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // B 退详情
        Assert.Equal(new[] { "B", "B" }, pad.Log.Skip(before).ToArray());
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // InspectAdvance → pos1
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);          // Inspect pos1：Up + Y
        Assert.Equal(new[] { "Up", "Y" }, pad.Log.Skip(before).Skip(2).ToArray());
    }

    [Fact]
    public void ParseLevelPairLoose_CombinesHalfReadings()
    {
        // 整串优先；两源各读半截时 跨源拼 cur/max
        Assert.Equal((1, 32), MaxLevelSwapBrain.ParseLevelPairLoose("/32", "用脚1"));
        Assert.Equal((27, 29), MaxLevelSwapBrain.ParseLevelPairLoose("2729", null));
        Assert.Equal((23, 26), MaxLevelSwapBrain.ParseLevelPairLoose("23/26", "x"));
        Assert.Null(MaxLevelSwapBrain.ParseLevelPairLoose("用脚", "/"));
        Assert.Null(MaxLevelSwapBrain.ParseLevelPairLoose(null, null));
    }

    [Fact]
    public void ReadOverall_AndBenchRemaining()
    {
        Assert.Equal(2403, MaxLevelSwapBrain.ReadOverallRating(BenchOcr(2403)));
        Assert.Equal(828, MaxLevelSwapBrain.ReadBenchRemaining(BenchOcr()));
        Assert.Null(MaxLevelSwapBrain.ReadOverallRating(HomeOcr()));
        Assert.Null(MaxLevelSwapBrain.ReadBenchRemaining(HomeOcr()));
    }

    [Fact]
    public void RealDetailFrame_NotMisclassifiedAsLineup()
    {
        // 实机 2026-10-25 详情页（Zico 7/7）：左上角球员风格标签「基础战术」曾让
        // ContainsJoined("战术") 误命中 → 详情页判成站位页 → Y 重按死循环。
        // 修复后：详情页判 Detail；侧栏式站位页（整词「战术」）仍判 Lineup
        var frame = LoadFrame("detail_zico_7of7.png");
        using var ocr = new OcrRecognizer();
        var res = TestOcr.Run(ocr, frame);
        Assert.Equal(SwapScreen.Detail, MaxLevelSwapBrain.Classify(res));

        // 合成侧栏页：整词「战术」+ 整体实力（无替补球员/自动配置球员词）→ 仍是站位页
        var sidebar = Ocr(W("完成", 0.90, 0.07), W("整体实力", 0.88, 0.20), W("战术", 0.10, 0.28),
            W("球队", 0.10, 0.40), W("球员详细数据", 0.60, 0.92));
        Assert.Equal(SwapScreen.Lineup, MaxLevelSwapBrain.Classify(sidebar));
        // 详情页同款「基础战术」标签 + 整体实力 → 不再误判站位页
        var detailish = Ocr(W("基础战术", 0.16, 0.12), W("整体实力", 0.88, 0.20),
            W("惯用脚", 0.42, 0.40), W("球员动作", 0.42, 0.93));
        Assert.Equal(SwapScreen.Detail, MaxLevelSwapBrain.Classify(detailish));
    }

    [Fact]
    public void DialogProbes_ErrorAndConfirmWords()
    {
        var err = Ocr(W("功能目前无法使用", 0.5, 0.30), W("不便之处,敬请原谅", 0.5, 0.45), W("OK", 0.5, 0.60));
        Assert.True(MaxLevelSwapBrain.IsGameErrorDialog(err));
        Assert.False(MaxLevelSwapBrain.IsGameErrorDialog(BenchOcr()));
        Assert.False(MaxLevelSwapBrain.IsGameErrorDialog(Ocr(LineupOcr())));

        var words = new List<OcrWord>(BenchOcr(2390).Words) { W("确定", 0.50, 0.50), W("取消", 0.42, 0.62) };
        Assert.True(MaxLevelSwapBrain.HasDialogConfirmWords(new OcrResult(words, 1, 1)));
        Assert.False(MaxLevelSwapBrain.HasDialogConfirmWords(BenchOcr()));
        Assert.False(MaxLevelSwapBrain.HasDialogConfirmWords(Ocr(LineupOcr())));
    }

    /// <summary>
    /// 末位（第 11 位）满级换人回归：2026-10-27 实机在 Rewind 走位 CursorMoves[10] 越界，
    /// 观察循环每轮同一处异常空转。此用例必须走完换人并正常回主页。
    /// </summary>
    [Fact]
    public async Task Eleventh_MaxedLastPlayer_SwapsAndReturnsHome()
    {
        var c = new MaxLevelSwapController();
        var reader = new ScriptReader();
        var pad = new FakePad();
        var lineup = Ocr(LineupOcr());
        var detailNot = Ocr(DetailOcr("23", "26", "44"));
        var detailMax = Ocr(DetailOcr("40", "40", "0"));
        var popupNot = Ocr(
            W("球员动作", 0.70, 0.10), W("等级训练", 0.70, 0.19), W("能力点分配", 0.70, 0.27),
            W("技巧训练", 0.70, 0.37), W("位置训练", 0.70, 0.47), W("球员融合", 0.70, 0.57),
            W("锁定", 0.70, 0.65), W("取消", 0.70, 0.93));
        PadDir[] moves = MaxLevelSwapController.CursorMoves;

        await c.StartCore(pad, SilentLog);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // Opening → Inspect

        // 位 1~10：全部未满级，逐位 Y → X → 弹窗 B → 详情 B → 推进
        for (int q = 0; q < 10; q++)
        {
            await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);    // Inspect：移动 + Y
            await c.StepCore(detailNot, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // X 球员动作
            await c.StepCore(popupNot, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // 未满级 → B
            await c.StepCore(detailNot, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 详情 B
            await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);    // 推进
        }

        // 第 11 位：满级、无能力点 → 直接换人（B 关弹窗 → 选中 → 侧栏 → 后备）
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(detailMax, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(PopupOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(detailMax, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);     // A 选中
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);     // 侧栏 → 后备球员
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // ←+A 开排序
        await c.StepCore(SortOcr(), ArrowFrame(up: true), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // ←↓A 开筛选
        await c.StepCore(FilterOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 卡片类型
        await c.StepCore(CardTypeOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        await c.StepCore(FilterOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 应用筛选
        await c.StepCore(BenchOcr(2403), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT); // 选亮卡 A 换入
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // 换人动画
        await c.StepCore(BenchOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);  // B 返回站位页
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // → 进阵
        await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);      // Rewind：越界前必须收尾

        // Rewind 收尾后 Done：下一轮识别到站位页给出等待，再识别到主页即交还
        int? wait = await c.StepCore(lineup, BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.NotNull(wait);
        wait = await c.StepCore(HomeOcr(), BlackFrame(), pad, reader.Read, SilentLog, null, null, CT);
        Assert.Null(wait);
        Assert.True(c.Idle);

        // ---- 按键序列 ----
        var exp = new List<string> { "Y" };
        for (int q = 0; q < 10; q++)
        {
            if (q > 0) exp.Add(moves[q - 1].ToString());
            exp.AddRange(new[] { "Y", "X", "B", "B" });
        }
        exp.Add(moves[9].ToString());
        exp.AddRange(new[] { "Y", "X", "B", "B", "A" });        // 第 11 位：X → 弹窗 B → 详情 B → 选中
        exp.AddRange(new[] { "Leftx8", "Upx5", "Downx3", "A" }); // 侧栏 → 后备球员
        exp.AddRange(new[] { "Left", "A" });                     // 开排序
        exp.AddRange(new[] { "Upx6", "Downx2", "A" });           // 整体评价
        exp.AddRange(new[] { "Left", "Down", "A" });             // 开筛选
        exp.AddRange(new[] { "Upx9", "Downx4", "A" });           // 卡片类型
        exp.AddRange(new[] { "Upx9", "Downx2" });
        for (int k = 0; k < 6; k++) exp.AddRange(new[] { "A", "Down" });
        exp.Add("B");
        exp.AddRange(new[] { "Upx9", "Downx9", "A" });           // 应用筛选
        exp.Add("A");                                            // 换入
        exp.Add("B");                                            // 返回站位页
        exp.Add("Right");                                        // → 进阵（第 3 位）
        // Rewind 从第 3 位（索引 2）走到末位（索引 10）：CursorMoves[2..9]，不越界
        for (int d = 2; d < moves.Length; d++) exp.Add(moves[d].ToString());
        exp.Add("B");                                            // Rewind 完成 → 站位页返回
        exp.Add("B");                                            // Done 收尾
        Assert.Equal(exp, pad.Log);
    }
}

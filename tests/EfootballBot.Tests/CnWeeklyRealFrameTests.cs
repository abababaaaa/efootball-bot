using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 校准/回归：用户提供的国服周活动 11 张截图（2026-10-13）→ OCR dump + CnWeeklyBrain 分类。
// 首次运行用于看清 OCR 实际读出的词与坐标（固化分类规则的依据）；
// 之后作为回归锁定（各帧分类不得回退）。国服分类为纯 OCR，不依赖 YOLO。
public class CnWeeklyRealFrameTests
{
    private readonly ITestOutputHelper _out;
    public CnWeeklyRealFrameTests(ITestOutputHelper output) => _out = output;

    private static string TestDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..");

    private static string CnDir => Path.Combine(TestDir, "TestData", "cnweekly");

    private static byte[] LoadPngBgra(string file, out int w, out int h)
    {
        using var mat = Cv2.ImRead(file, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图片加载失败: {file}");
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        int byteCount = (int)(bgra.Total() * bgra.ElemSize());
        var data = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, byteCount);
        w = mat.Width; h = mat.Height;
        return data;
    }

    private static readonly (string File, CwScreen Expected)[] Frames =
    {
        ("cn_home_recommended.png", CwScreen.HomeRecommended),
        ("cn_home_recommended_focus.png", CwScreen.HomeRecommended),
        ("cn_home_match.png", CwScreen.HomeMatch),
        ("cn_events_list.png", CwScreen.EventsList),
        ("cn_event_intro.png", CwScreen.EventIntro),
        ("cn_bracket.png", CwScreen.Bracket),
        ("cn_bracket_2.png", CwScreen.Bracket),
        ("cn_event_home.png", CwScreen.EventHome),
        ("cn_squad.png", CwScreen.Squad),
        ("cn_squad_menu.png", CwScreen.SquadMenu),
        ("cn_squad_menu_cursor.png", CwScreen.SquadMenu),
        ("cn_tour_event_settings.png", CwScreen.EventSettings),
        ("cn_challenge_list.png", CwScreen.ChallengeList),
        ("cn_challenge_card_home.png", CwScreen.ChallengeCardHome),
        ("cn_tour_difficulty.png", CwScreen.Difficulty),
    };

    [Fact]
    public async Task Dump_And_Classify_Cn_Frames()
    {
        using var ocr = new OcrRecognizer();
        var failures = new List<string>();

        foreach (var (name, expected) in Frames)
        {
            var path = Path.Combine(CnDir, name);
            if (!File.Exists(path)) { _out.WriteLine($"==== {name}: missing, skip ===="); continue; }
            var data = LoadPngBgra(path, out int w, out int h);
            var frame = new FrameData(data, w, h, 0);
            var ocrRes = await ocr.RecognizeAsync(frame);
            var screen = CnWeeklyBrain.Classify(Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);

            _out.WriteLine($"==== {name} -> {screen} (期望 {expected}) ====");
            foreach (var l in ocrRes.Lines)
                _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
            foreach (var wd in ocrRes.Words)
                _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

            if (screen != expected)
                failures.Add($"{name}: 判定 {screen}，期望 {expected}");
        }

        Assert.True(failures.Count == 0,
            "国服周活动实机帧分类回归失败：\n" + string.Join("\n", failures));
    }

    // 实机 2026-10-17：球星训练·巡回赛 活动设置面板（教练模式=开 + 教练模式内控制=完全托管，
    // 主页词透暗背景同屏）。固化：面板判 EventSettings（先于巡回赛主页/杯赛型主页）+
    // TourSettingsReady=true。OCR 词与坐标全部打印，判定偏差时可直接据此调参。
    // 主页关闭面板的实机帧暂缺，巡回赛主页分类由 CnWeeklyBrainTests 合成帧锁定。
    [Fact]
    public async Task TourEventSettings_Frame_ClassifyAndReady()
    {
        using var ocr = new OcrRecognizer();
        string img = Path.Combine(CnDir, "cn_tour_event_settings.png");
        var data = LoadPngBgra(img, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = CnWeeklyBrain.Classify(
            Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        bool ready = CnWeeklyBrain.TourSettingsReady(ocrRes);

        _out.WriteLine($"screen={screen} tourSettingsReady={ready}");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

        Assert.Equal(CwScreen.EventSettings, screen);
        Assert.True(ready, "应核验通过：教练模式=开 + 完全托管");
    }

    // 实机 2026-10-17：国服挑战清单选卡页（卡 01 白卡待挑战 + 卡 02/03 灰卡右下灰锁 +
    // 底栏 B 返回；第 4 卡出屏残边）。固化：标题判 ChallengeList；动态卡数扫描
    // 卡 1 无锁无勾、卡 2/卡 3 锁定。OCR 词与坐标、各卡锁/勾探针灰/绿像素计数全部打印，
    // 判定偏差时可直接据此调参。
    [Fact]
    public async Task ChallengeList_Frame_ClassifyAndScan()
    {
        using var ocr = new OcrRecognizer();
        string img = Path.Combine(CnDir, "cn_challenge_list.png");
        var data = LoadPngBgra(img, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = CnWeeklyBrain.Classify(
            Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        var cards = CnWeeklyBrain.ScanChallengeCards(ocrRes, frame);

        _out.WriteLine($"screen={screen} cards=[{string.Join(", ", cards.Select(c => $"{c.Index:00}@slot{c.Slot} lock={c.Locked} check={c.Checked}"))}]");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2}) wh=({wd.W:F3},{wd.H:F3})");

        Assert.Equal(CwScreen.ChallengeList, screen);
        Assert.Equal(3, cards.Count);
        Assert.Equal((1, 0, false, false), (cards[0].Index, cards[0].Slot, cards[0].Locked, cards[0].Checked));
        Assert.Equal((2, 1, true, false), (cards[1].Index, cards[1].Slot, cards[1].Locked, cards[1].Checked));
        Assert.Equal((3, 2, true, false), (cards[2].Index, cards[2].Slot, cards[2].Locked, cards[2].Checked));
    }

    // 实机 2026-10-17 晚：打完卡 01 回清单——★勾卡仍是白色聚焦+参赛中（游标不在待挑战卡上），
    // 卡 02 待挑战、卡 03 灰锁。固化：分类 ChallengeList；勾探针完成态首次拿到实机验证
    // （卡 1 勾、卡 3 锁、卡 2 无锁无勾）；聚焦白卡探测=卡 1（场景层据此 右移×1 到卡 2 再 A）。
    // OCR 词、各卡锁/勾判定、聚焦卡全部打印，偏差时据此调参。
    [Fact]
    public async Task ChallengeList_DoneFocus_Frame_ScanAndFocused()
    {
        using var ocr = new OcrRecognizer();
        string img = Path.Combine(CnDir, "cn_challenge_list_done_focus.png");
        var data = LoadPngBgra(img, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = CnWeeklyBrain.Classify(
            Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        var cards = CnWeeklyBrain.ScanChallengeCards(ocrRes, frame);
        int? focused = CnWeeklyBrain.DetectFocusedChallengeCard(ocrRes, frame);

        _out.WriteLine($"screen={screen} focused={focused} cards=[{string.Join(", ", cards.Select(c => $"{c.Index:00}@slot{c.Slot} lock={c.Locked} check={c.Checked}"))}]");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2}) wh=({wd.W:F3},{wd.H:F3})");

        Assert.Equal(CwScreen.ChallengeList, screen);
        var c1 = cards.FirstOrDefault(c => c.Index == 1);
        var c2 = cards.FirstOrDefault(c => c.Index == 2);
        var c3 = cards.FirstOrDefault(c => c.Index == 3);
        Assert.NotNull(c1);
        Assert.True(c1.Checked, "卡 1 应命中绿勾（完成态探针实机校验）");
        Assert.False(c1.Locked);
        Assert.NotNull(c2);
        Assert.False(c2.Locked);
        Assert.False(c2.Checked);
        Assert.NotNull(c3);
        Assert.True(c3.Locked, "卡 3 应命中灰锁");
        Assert.Equal(1, focused);
    }

    // 实机 2026-10-17 晚：进入下一张挑战卡时弹「参加新的挑战？」（覆盖当前进度警告，
    // 进入/取消 两键，默认焦点在取消）。固化：分类仍 ChallengeList（标题可见、进入/取消
    // 不构成推进词门控），场景层弹窗分支靠「参加新的挑战」词上移选进入。
    [Fact]
    public async Task ChallengeList_JoinPopup_Frame_Words()
    {
        using var ocr = new OcrRecognizer();
        string img = Path.Combine(CnDir, "cn_challenge_join_popup.png");
        var data = LoadPngBgra(img, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = CnWeeklyBrain.Classify(
            Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);

        _out.WriteLine($"screen={screen} joinWord={ocrRes.ContainsJoined("参加新的挑战")} " +
                       $"enter={ocrRes.ContainsJoined("进入")} cancel={ocrRes.ContainsJoined("取消")}");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

        Assert.Equal(CwScreen.ChallengeList, screen);
        Assert.True(ocrRes.ContainsJoined("参加新的挑战"), "应读到 参加新的挑战");
    }

    // 实机卡详情页（cn_challenge_card_home.png，已完成卡）：右栏奖励行带绿勾——
    // ChallengeRewardClaimed 探针必须命中。完成态主判据：「首次以后的奖励」词 OCR
    // 常漏读（2026-10-17 23:21 实机 24 词帧漏读后再次空开比赛）
    [Fact]
    public async Task ChallengeCardHome_Frame_RewardCheckClaimed()
    {
        using var ocr = new OcrRecognizer();
        var data = LoadPngBgra(Path.Combine(CnDir, "cn_challenge_card_home.png"), out int w, out int h);
        var frame = new FrameData(data, w, h, 0);

        bool claimed = CnWeeklyBrain.ChallengeRewardClaimed(frame);
        _out.WriteLine($"rewardClaimed={claimed}");

        Assert.True(claimed, "右栏奖励勾应命中（实机已完成卡详情页）");
    }

    // 实机 2026-10-17 晚：挑战清单可见卡全勾（01/02/03 全 ✓ + 第 4 卡屏外残边）。
    // 固化：分类 ChallengeList；三张可见卡全部命中勾探针（场景层据此进入盲点右移
    // 翻页确认屏外卡，不直接报全部完成——卡数不固定且可见卡≠全部卡）
    [Fact]
    public async Task ChallengeList_AllChecked_Frame_VisibleOnly()
    {
        using var ocr = new OcrRecognizer();
        var data = LoadPngBgra(Path.Combine(CnDir, "cn_challenge_list_all_checked.png"), out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = CnWeeklyBrain.Classify(
            Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        var cards = CnWeeklyBrain.ScanChallengeCards(ocrRes, frame);
        var playable = cards.Where(c => !c.Locked && !c.Checked).ToList();

        _out.WriteLine($"screen={screen} cards=[{string.Join(", ", cards.Select(c => $"{c.Index:00} lock={c.Locked} check={c.Checked}"))}] playable={playable.Count}");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2})");

        Assert.Equal(CwScreen.ChallengeList, screen);
        Assert.True(cards.Count >= 3, "应识别出可见卡 01/02/03");
        Assert.All(cards.Take(3), c => Assert.True(c.Checked, $"卡 {c.Index:00} 应命中绿勾"));
        Assert.Empty(playable);
    }

    // 实机 2026-10-17：国服巡回赛「选择比赛难度」页（前往比赛后弹出；5 张难度卡横排，
    // 光标所在卡白底高亮=当前所选 标准，底栏 B 返回 / RT 确定）。固化：标题判 Difficulty；
    // 白底亮度探针检出选中=标准。OCR 词与坐标、各卡卡体亮度全部打印，偏差时据此调参。
    [Fact]
    public async Task TourDifficulty_Frame_ClassifyAndDetectSelected()
    {
        using var ocr = new OcrRecognizer();
        string img = Path.Combine(CnDir, "cn_tour_difficulty.png");
        var data = LoadPngBgra(img, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = CnWeeklyBrain.Classify(
            Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        var selected = CnWeeklyBrain.DetectSelectedCnDifficulty(frame, ocrRes);

        _out.WriteLine($"screen={screen} selected={selected ?? "null"}");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2}) wh=({wd.W:F3},{wd.H:F3})");

        Assert.Equal(CwScreen.Difficulty, screen);
        Assert.Equal("标准", selected);
    }

    // 实机 2026-10-17：巡回赛主页 i 信息弹窗（主要奖励 金币 60/120 + 高光定向券 1/2，
    // 活动时间/活动说明，右上 B 关闭）。固化：判 RewardPopup；x/x 进度读出两项且
    // 未全满（主页勾≠拿满的判据来源）。
    [Fact]
    public async Task RewardPopup_Frame_ClassifyAndParseProgress()
    {
        using var ocr = new OcrRecognizer();
        string img = Path.Combine(CnDir, "cn_tour_reward_popup.png");
        var data = LoadPngBgra(img, out int w, out int h);
        var frame = new FrameData(data, w, h, 0);
        var ocrRes = await ocr.RecognizeAsync(frame);

        var screen = CnWeeklyBrain.Classify(
            Array.Empty<EfootballBot.Core.Vision.Yolo.YoloDetection>(), ocrRes);
        var (readable, allDone) = CnWeeklyBrain.ParseMainRewardProgress(ocrRes);

        _out.WriteLine($"screen={screen} readable={readable} allDone={allDone}");
        foreach (var l in ocrRes.Lines)
            _out.WriteLine($"  L \"{l.Text}\" @({l.CenterX:F2},{l.CenterY:F2})");
        foreach (var wd in ocrRes.Words)
            _out.WriteLine($"  W \"{wd.Text}\" @({wd.CenterX:F2},{wd.CenterY:F2}) wh=({wd.W:F3},{wd.H:F3})");

        Assert.Equal(CwScreen.RewardPopup, screen);
        Assert.True(readable, "应读出主要奖励 x/x 进度词");
        Assert.False(allDone, "60/120 与 1/2 均未拿满");
    }
}

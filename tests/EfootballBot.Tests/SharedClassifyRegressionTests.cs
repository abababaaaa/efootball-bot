using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// v8.0.16 共享分类/决策回归护栏。
///
/// 起因：重训后的 YOLO 把周活动卡片页的 main_menu 误报压掉了，却把 btn_back 召回提上来
/// （这些页正是「只标返回按钮」的负样本）。旧 Classify 里 `btn_back → LeaguePage` 又排在
/// 所有 OCR 兜底之前，于是周活动页被判 LeaguePage；Decide 的 LeaguePage 分支看到底栏
/// 「进入/开始/继续」就按 A —— 实机 14 帧走查里 3 帧会盲按进活动页。
/// 同时那条宽兜底还遮蔽了 hasMyLeague→MatchHub、HasMainMenuNavBar→MainMenu、
/// topRight→InMatch 三条兜底。
/// </summary>
public class SharedClassifyRegressionTests
{
    private static string Frame(string rel) => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", rel.Replace('/', Path.DirectorySeparatorChar));

    private static (OcrResult Ocr, IReadOnlyList<YoloDetection> Dets) Load(string rel)
    {
        string png = Frame(rel);
        Assert.True(File.Exists(png), $"缺实机帧 {png}");
        using var mat = Cv2.ImRead(png, ImreadModes.Color);
        Assert.False(mat.Empty());
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        var data = new byte[(int)(bgra.Total() * bgra.ElemSize())];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);
        var frame = new FrameData(data, mat.Width, mat.Height, 0);
        using var recognizer = new OcrRecognizer();
        var ocr = TestOcr.Run(recognizer, frame);
        using var det = new YoloDetector(
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt"),
            new OnnxSessionFactory("CPU"));
        return (ocr, det.Detect(frame));
    }

    private static OcrResult Ocr(params string[] words)
        => new(words.Select((t, i) => new OcrWord(t, 0.10 + i * 0.01, 0.90, 0.10, 0.04)).ToList(), 1280, 720);

    [Theory]
    [InlineData("weekly/weekly_events_list.png")]
    [InlineData("weekly/weekly_events_list_1029.png")]
    [InlineData("weekly/weekly_events_list_vsplayers.png")]
    [InlineData("weekly/weekly_teameffort_list.png")]
    public void 周活动卡片页_不得判LeaguePage_更不得按A(string rel)
    {
        var (ocr, dets) = Load(rel);
        var screen = MatchBrain.Classify(dets, ocr);
        Assert.NotEqual(MatchScreen.LeaguePage, screen);
        Assert.NotEqual(MatchActionKind.PadConfirm, MatchBrain.Decide(screen, ocr, 1).Kind);
    }

    [Fact]
    public void 国服合约页_不得被btn_back兜底判成LeaguePage()
    {
        var (ocr, dets) = Load("stud/stud_03_contract_home.png");
        Assert.NotEqual(MatchScreen.LeaguePage, MatchBrain.Classify(dets, ocr));
    }

    [Theory]
    [InlineData("weekly/weekly_hub.png", MatchScreen.MatchHub)]
    [InlineData("matchhub_user_1.png", MatchScreen.MatchHub)]
    [InlineData("matchhub_user_2.png", MatchScreen.MatchHub)]
    [InlineData("stud/stud_01_main_menu.png", MatchScreen.MainMenu)]
    [InlineData("stud/stud_12_main_menu_after.png", MatchScreen.MainMenu)]
    [InlineData("weekly/weekly_match_history.png", MatchScreen.MatchHistory)]
    public void 关键页面_分类保持正确(string rel, MatchScreen expected)
    {
        var (ocr, dets) = Load(rel);
        Assert.Equal(expected, MatchBrain.Classify(dets, ocr));
    }

    [Theory]
    [InlineData("intlboot/login_bonus_popup.png")]
    [InlineData("intlboot/inbox_popup.png")]
    public void 主菜单弹窗_不得落LeaguePage(string rel)
    {
        // 弹窗（登录奖励/收件箱）盖在主菜单上。完整契约「判 HasConfirm 按 A 关弹窗」
        // 依赖模型召回（新模型在弹窗帧不误检 main_menu、可检出 btn_confirm），断言
        // Equal HasConfirm 会绑定模型权重 —— PR 分支旧模型检不出 btn_confirm 且仍误检
        // main_menu（:173 先于 btn_confirm 兜底），无法等价复现，该侧由 main 分支
        // （新模型 + 原理论）锁定。这里锁与模型无关的次序底线：v8.0.16 修的
        // 「btn_back→LeaguePage 宽兜底」绝不能把弹窗帧判成 LeaguePage 盲按 A。
        var (ocr, dets) = Load(rel);
        var screen = MatchBrain.Classify(dets, ocr);
        Assert.NotEqual(MatchScreen.LeaguePage, screen);
        Assert.NotEqual(MatchActionKind.PadConfirm, MatchBrain.Decide(screen, ocr, 1).Kind);
    }

    [Fact]
    public void LeaguePage_周活动证据页_不认底栏通用推进词()
    {
        // 周活动卡片页底栏/卡面同样印「继续/进入」，且带「对阵AI/对阵玩家」胶囊；
        // 这类页即使因 OCR 漏读导航条被判 LeaguePage，也必须挡住，否则误进活动
        var weekly = Ocr("继续", "返回", "对阵AI", "对阵玩家");
        Assert.Equal(MatchActionKind.Wait, MatchBrain.Decide(MatchScreen.LeaguePage, weekly, 1).Kind);
    }

    [Fact]
    public void LeaguePage_锦标赛证据页_不认底栏通用推进词()
    {
        // 锦标赛板块页（淘汰赛/小组赛）不在分类排除词里，靠决策层证据词兜住
        var tournament = Ocr("进入", "淘汰赛", "小组赛");
        Assert.Equal(MatchActionKind.Wait, MatchBrain.Decide(MatchScreen.LeaguePage, tournament, 1).Kind);
    }

    [Fact]
    public void LeaguePage_联赛子页只印继续_仍按A推进()
    {
        // 用户确认：真联赛子页底栏只印「继续/进入」时，按 A 才是对的；
        // 没有其它模块证据词就不得转 Wait（v8.0.16.1 放宽）
        var leagueSubPage = Ocr("继续", "返回");
        Assert.Equal(MatchActionKind.PadConfirm,
            MatchBrain.Decide(MatchScreen.LeaguePage, leagueSubPage, 1).Kind);
    }

    [Fact]
    public void LeaguePage_有联赛上下文时_仍能按A推进()
    {
        var league = Ocr("前往比赛", "继续", "赛程表");
        Assert.Equal(MatchActionKind.PadConfirm,
            MatchBrain.Decide(MatchScreen.LeaguePage, league, 1).Kind);
    }

    [Fact]
    public void LeaguePage_类别选择页词_仍被联赛上下文放行()
    {
        // v8.0.9 修的「对阵AI类别」卡死不能被新门控误伤
        var category = Ocr("eFootball联赛", "对阵AI类别");
        Assert.NotEqual(MatchActionKind.GamepadBack,
            MatchBrain.Decide(MatchScreen.LeaguePage, category, 1).Kind);
    }
}

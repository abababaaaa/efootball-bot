using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 训练素材主动收集判定器回归（v8.1.1）。
/// 锁定「主要页面 → tag」的映射：真主菜单/弹窗盖主菜单/周活动卡片/锦标赛卡片/比赛选择页/联赛主页，
/// 以及优先级（弹窗盖菜单要先于真主菜单，否则遮挡帧会被当干净正样本抓）。
/// 判据与模型权重解耦——这里只喂合成 OCR/检测，不跑 ONNX。
/// </summary>
public class TrainingFrameCollectorTests
{
    private static OcrResult Ocr(params string[] words)
        => new(words.Select((t, i) => new OcrWord(t, 0.10 + i * 0.01, 0.90, 0.10, 0.04)).ToList(), 1280, 720);

    /// <summary>顶部导航条词（y≈0.08）：HasMainMenuNavBar 要求「游戏主菜单」在 CenterY&lt;0.20 的行。</summary>
    private static OcrResult TopNav(params string[] words)
        => new(words.Select((t, i) => new OcrWord(t, 0.10 + i * 0.12, 0.06, 0.11, 0.04)).ToList(), 1280, 720);

    private static IReadOnlyList<YoloDetection> Dets(params string[] classes)
        => classes.Select((c, i) => new YoloDetection(c, i, 0.80f, 0.1 + i * 0.1, 0.5, 0.1, 0.05)).ToList();

    [Fact]
    public void 真主菜单_抓正样本()
    {
        var r = MatchBrain.CollectTrainingFrame(TopNav("游戏主菜单", "商店", "学院", "额外选项"), Dets("main_menu"));
        Assert.Equal("train_main_menu", r?.Tag);
    }

    [Fact]
    public void 弹窗盖主菜单_优先于真主菜单()
    {
        // 导航条 + btn_confirm 同屏：必须抓遮挡样本，不能被当干净主菜单正样本
        var ocr = TopNav("游戏主菜单", "商店", "学院");
        var r = MatchBrain.CollectTrainingFrame(ocr, Dets("main_menu", "btn_confirm"));
        Assert.Equal("train_popup_over_menu", r?.Tag);
    }

    [Fact]
    public void 周活动胶囊_抓困难负样本_类别页不误抓()
    {
        var weekly = Ocr("对阵AI", "Team Effort", "赛事等级", "进入");
        Assert.Equal("train_weekly_cards", MatchBrain.CollectTrainingFrame(weekly, null)?.Tag);

        // 类别选择页含胶囊词但带「类别」，是联赛页不是周活动
        var category = Ocr("对阵AI类别", "对阵玩家类别", "前往比赛");
        Assert.NotEqual("train_weekly_cards", MatchBrain.CollectTrainingFrame(category, null)?.Tag);
    }

    [Fact]
    public void 锦标赛卡片页_抓负样本()
    {
        var ocr = Ocr("锦标赛", "Championship Club Event", "已结束", "Round 3 Ranking Event", "查看");
        Assert.Equal("train_tournament_cards", MatchBrain.CollectTrainingFrame(ocr, Dets("btn_back"))?.Tag);
    }

    [Fact]
    public void 比赛选择页_抓磁贴样本()
    {
        // IsMatchHub 强证据：锦标赛 + 争取升级至更高组别
        var ocr = Ocr("锦标赛", "争取升级至更高组别", "我的联赛", "活动");
        Assert.Equal("train_match_hub", MatchBrain.CollectTrainingFrame(ocr, null)?.Tag);
    }

    [Fact]
    public void 联赛主页_抓正样本()
    {
        var ocr = Ocr("前往比赛", "比赛记录", "排名");
        Assert.Equal("train_league_home", MatchBrain.CollectTrainingFrame(ocr, Dets("btn_confirm"))?.Tag);
    }

    [Fact]
    public void 无关页面_不抓()
    {
        Assert.Null(MatchBrain.CollectTrainingFrame(Ocr("设置", "画质", "音量"), null));
        Assert.Null(MatchBrain.CollectTrainingFrame(OcrResult.Empty, null));
    }

    [Fact]
    public void 低分检测框_不算证据()
    {
        // btn_confirm 分数低于 MinScore 时不能把主菜单帧误判成「弹窗盖主菜单」
        var ocr = TopNav("游戏主菜单", "商店", "学院");
        var low = new[] { new YoloDetection("btn_confirm", 2, 0.10f, 0.5, 0.9, 0.1, 0.05) };
        Assert.Equal("train_main_menu", MatchBrain.CollectTrainingFrame(ocr, low)?.Tag);
    }
}

using EfootballBot.Core.Bot.Scenarios.AiLadder;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

// 国际服 AI 天梯联赛决策大脑：界面分类 + 组别解析（合成 OCR 帧，坐标均为归一化值）
public class AiLadderBrainTests
{
    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.05, cy - 0.015, 0.1, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    // ---------------- 各界面正判 ----------------

    [Fact]
    public void Classify_CategorySelect_CardTitles()
    {
        // 实机类别选择页：标题 eFootball™联赛 + 对阵AI类别/对阵玩家类别 两卡
        //（卡片右下角组别角标数字/颜色会变，不作判定点）
        var ocr = Ocr(W("eFootball™联赛", 0.2, 0.11),
            W("对阵AI类别", 0.25, 0.40), W("对阵AI。", 0.25, 0.47),
            W("对阵玩家类别", 0.63, 0.42), W("与其他玩家竞争。", 0.63, 0.47),
            W("返回", 0.12, 0.94));
        Assert.Equal(AlScreen.CategorySelect, AiLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_CategorySelect_TopTitleFallback()
    {
        // 卡片词漏读时：顶部标题行「eFootball™联赛」兜底（比赛页 hub 的同名词是卡片标签 y≈0.7 不命中）
        var ocr = Ocr(W("eFootball™联赛", 0.2, 0.11), W("返回", 0.12, 0.94));
        Assert.Equal(AlScreen.CategorySelect, AiLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_DivisionHome_FullFrame()
    {
        // 实机组别主页（2026-10-14 截图）：组别8 + 阶段3日期 + 前往比赛 + 当前积分 + 比赛场次 + 组别分布
        var ocr = Ocr(W("组别8", 0.155, 0.055), W("阶段3: 2026/10/08 10:00 - 2026/11/05 09:59", 0.31, 0.12),
            W("前往比赛", 0.14, 0.24), W("当前积分", 0.40, 0.16), W("比赛场次", 0.86, 0.16),
            W("组别分布", 0.44, 0.26), W("任务", 0.88, 0.07), W("返回", 0.12, 0.94));
        Assert.Equal(AlScreen.DivisionHome, AiLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_DivisionHome_GoMatchPlusDistributionOnly()
    {
        // 组别分布 + 前往比赛 即可判主页（其余特征词全漏读的兜底）
        var ocr = Ocr(W("前往比赛", 0.14, 0.24), W("组别分布", 0.44, 0.26));
        Assert.Equal(AlScreen.DivisionHome, AiLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_DivisionHome_GoMatchPlusPlayCount()
    {
        var ocr = Ocr(W("前往比赛", 0.14, 0.24), W("比赛场次", 0.86, 0.16));
        Assert.Equal(AlScreen.DivisionHome, AiLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_DivisionHome_GoMatchPlusDivisionTitle()
    {
        // 全部特征词漏读、只余左上 组别X 标题 + 前往比赛的兜底
        var ocr = Ocr(W("组别8", 0.155, 0.055), W("前往比赛", 0.14, 0.24));
        Assert.Equal(AlScreen.DivisionHome, AiLadderBrain.Classify(ocr));
    }

    // ---------------- 互斥（同形词跨页出现） ----------------

    [Fact]
    public void Classify_None_ForWeeklyEventsList()
    {
        // 周活动列表页同有 对阵AI/对阵玩家 页签（无「类别」后缀）：绝不能判类别页
        var ocr = Ocr(W("对阵AI", 0.3, 0.08), W("对阵玩家", 0.6, 0.08),
            W("巡回赛", 0.5, 0.5), W("返回", 0.12, 0.94));
        Assert.Equal(AlScreen.None, AiLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_None_ForMatchHub()
    {
        // 比赛页：卡片标签 eFootball™联赛 在 y≈0.74（非顶部）+ 描述「争取升级至更高组别」无相邻数字
        var ocr = Ocr(W("比赛", 0.09, 0.10),
            W("锦标赛", 0.22, 0.74), W("活动", 0.50, 0.74),
            W("eFootball™联赛", 0.80, 0.74), W("争取升级至更高组别", 0.80, 0.79));
        Assert.Equal(AlScreen.None, AiLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_None_ForMyLeagueHome()
    {
        // 我的联赛主页：有前往比赛、无组别特征词 → 不判组别主页
        var ocr = Ocr(W("前往比赛", 0.14, 0.22), W("赛程表", 0.14, 0.47),
            W("兑换积分", 0.14, 0.60), W("我的联赛积分", 0.6, 0.5));
        Assert.Equal(AlScreen.None, AiLadderBrain.Classify(ocr));
    }

    [Fact]
    public void Classify_DivisionHomeWinsOverBrandWord()
    {
        // 组别主页奖励行「升级至组别7 - eFootball™联赛」含品牌词与联赛字样，
        // 但主页特征（前往比赛+组别分布）先行命中，不得判类别页
        var ocr = Ocr(W("组别8", 0.155, 0.055), W("前往比赛", 0.14, 0.24),
            W("组别分布", 0.44, 0.26),
            W("下一个可实现的目标", 0.83, 0.26), W("升级至组别7 - eFootball™联赛", 0.83, 0.29));
        Assert.Equal(AlScreen.DivisionHome, AiLadderBrain.Classify(ocr));
    }

    // ---------------- 组别解析 ----------------

    [Fact]
    public void ParseDivision_Title8()
    {
        var ocr = Ocr(W("组别8", 0.155, 0.055));
        Assert.Equal(8, AiLadderBrain.ParseDivision(ocr));
    }

    [Fact]
    public void ParseDivision_Title10()
    {
        var ocr = Ocr(W("组别10", 0.16, 0.055));
        Assert.Equal(10, AiLadderBrain.ParseDivision(ocr));
    }

    [Fact]
    public void ParseDivision_TraditionalChar()
    {
        // OCR 偶发繁体「組别」
        var ocr = Ocr(W("組别3", 0.155, 0.055));
        Assert.Equal(3, AiLadderBrain.ParseDivision(ocr));
    }

    [Fact]
    public void ParseDivision_MergedReaderLine()
    {
        // 标题与阶段行被 OCR 合并：组别8阶段32026/10/08... → 取组别后的数字 8
        var ocr = Ocr(W("EFOOTBALL LEAGUE 组别8阶段3: 2026/10/08 10:00 - 2026/11/05 09:59", 0.25, 0.08));
        Assert.Equal(8, AiLadderBrain.ParseDivision(ocr));
    }

    [Fact]
    public void ParseDivision_GoalLine_ExcludedByPosition()
    {
        // 右栏「下一个可实现的目标：升级至组别7」：y≈0.29 超出左上区域（y<0.22）必须排除
        var ocr = Ocr(W("升级至组别7", 0.40, 0.29));
        Assert.Null(AiLadderBrain.ParseDivision(ocr));
    }

    [Fact]
    public void ParseDivision_GoalLine_ExcludedByX()
    {
        // 同一行即使 y 合规，x≈0.83 在右栏也必须排除
        var ocr = Ocr(W("升级至组别7", 0.83, 0.15));
        Assert.Null(AiLadderBrain.ParseDivision(ocr));
    }

    [Fact]
    public void ParseDivision_DistributionLabel_NoDigit_ReturnsNull()
    {
        // 「组别分布」无相邻数字（分布列表徽章是图形非文字）：不猜
        var ocr = Ocr(W("组别分布", 0.44, 0.26));
        Assert.Null(AiLadderBrain.ParseDivision(ocr));
    }

    [Fact]
    public void ParseDivision_Missing_ReturnsNull()
    {
        var ocr = Ocr(W("前往比赛", 0.14, 0.24));
        Assert.Null(AiLadderBrain.ParseDivision(ocr));
    }

    [Fact]
    public void ParseDivision_OutOfRange_Ignored()
    {
        // 数字越界（OCR 误拼 如 组别88/组别0）：不返回，绝不让坏数驱动停止判定
        Assert.Null(AiLadderBrain.ParseDivision(Ocr(W("组别88", 0.16, 0.055))));
        Assert.Null(AiLadderBrain.ParseDivision(Ocr(W("组别0", 0.16, 0.055))));
    }
}

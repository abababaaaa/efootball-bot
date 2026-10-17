using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

// 赛后「经验值」页判定与球员数（合成 OCR 词帧）：
// IsExpResultPage 只认固定 UI 词（底部「查看经验值」提示 / 页首标题+球员行兜底）；
// CountExpResultPlayers 按「等级 N/N」+「加成:+N%」格子计数（X 聚列 → 列内 Y 聚球员）。
// 球员名每场都变，不参与判定（可变内容禁作判定点约定）。
public class ExpResultPageTests
{
    /// <summary>构造以中心点定位的 OCR 词（宽 0.15 / 高 0.03，与实机词块量级一致）。</summary>
    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.075, cy - 0.015, 0.15, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1, 1);

    /// <summary>实机 2026-10-17 截图几何：2×2 网格四名球员（左列 x≈0.22，右列 x≈0.66；
    /// 第一行 等级 y≈0.25 / 加成 y≈0.29；第二行 y≈0.41/0.46）。</summary>
    private static OcrWord[] FourPlayersGrid() => new[]
    {
        W("经验值", 0.127, 0.097),                       // 页首大字标题
        W("Pervis Estupinan", 0.24, 0.222),              // 球员名（不参与判定）
        W("等级29/35", 0.22, 0.253), W("加成:+1100%", 0.22, 0.293),
        W("Alexis Saelemaekers", 0.68, 0.222),
        W("等级23/29", 0.66, 0.247), W("加成:+1100%", 0.66, 0.283),
        W("等级28/34", 0.22, 0.407), W("加成:+300%", 0.22, 0.458),
        W("等级18/37", 0.66, 0.400), W("加成:+1100%", 0.66, 0.432),
    };

    // ---------------- IsExpResultPage ----------------

    [Fact]
    public void HintWord_Alone_DetectsPage()
    {
        // 底部「Y 查看经验值」提示是全流程唯一词，仅凭它即可判定
        var ocr = Ocr(W("Y", 0.42, 0.92), W("查看经验值", 0.47, 0.92), W("下一步", 0.87, 0.92));
        Assert.True(MyLeagueBrain.IsExpResultPage(ocr));
    }

    [Fact]
    public void TitlePlusPlayers_DetectsPage_WhenHintMissed()
    {
        var ocr = Ocr(FourPlayersGrid());
        Assert.True(MyLeagueBrain.IsExpResultPage(ocr));
    }

    [Fact]
    public void TitleWithoutPlayerRows_IsNotExpPage()
    {
        // 物品页/主页面板行同样含「经验值」字样，但无页首标题+球员行组合——不判经验值页
        var ocr = Ocr(W("我的联赛积分", 0.2, 0.10), W("Exp经验值4x", 0.5, 0.55));
        Assert.False(MyLeagueBrain.IsExpResultPage(ocr));
    }

    [Fact]
    public void PlayerRowsWithoutTitleOrHint_IsNotExpPage()
    {
        var ocr = Ocr(W("等级29/35", 0.22, 0.253), W("加成:+1100%", 0.22, 0.293));
        Assert.False(MyLeagueBrain.IsExpResultPage(ocr));
    }

    // ---------------- CountExpResultPlayers ----------------

    [Fact]
    public void CountsFourPlayers_InTwoColumnGrid() =>
        Assert.Equal(4, MyLeagueBrain.CountExpResultPlayers(Ocr(FourPlayersGrid())));

    [Fact]
    public void CountsThreePlayers_WhenOneCellMissing()
    {
        // 右下格整格缺失：3 名球员
        var ocr = Ocr(W("经验值", 0.127, 0.097),
            W("等级29/35", 0.22, 0.253), W("加成:+1100%", 0.22, 0.293),
            W("等级23/29", 0.66, 0.247), W("加成:+1100%", 0.66, 0.283),
            W("等级28/34", 0.22, 0.407), W("加成:+300%", 0.22, 0.458));
        Assert.Equal(3, MyLeagueBrain.CountExpResultPlayers(ocr));
    }

    [Fact]
    public void CountsPlayer_EvenWhenLevelLineMissed()
    {
        // 「等级」行漏读仅剩「加成」：仍计一格，不因漏读少算
        var ocr = Ocr(W("等级29/35", 0.22, 0.253), W("加成:+1100%", 0.22, 0.293),
                      W("加成:+1100%", 0.66, 0.283));
        Assert.Equal(2, MyLeagueBrain.CountExpResultPlayers(ocr));
    }

    [Fact]
    public void SplitWordsOfSameLine_CountAsOnePlayer()
    {
        // OCR 把「等级」与「29/35」拆成相邻两词：同格计一名
        var ocr = Ocr(W("等级", 0.20, 0.253), W("29/35", 0.25, 0.253));
        Assert.Equal(1, MyLeagueBrain.CountExpResultPlayers(ocr));
    }

    [Fact]
    public void NoPlayerWords_CountsZero()
    {
        var ocr = Ocr(W("经验值", 0.127, 0.097), W("查看经验值", 0.47, 0.92));
        Assert.Equal(0, MyLeagueBrain.CountExpResultPlayers(ocr));
    }
}

using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

// 我的联赛道具行匹配（名称与档位按用户设置，固定 UI 词）
public class MlItemRowTests
{
    private static OcrResult Ocr(params (string text, double x, double y)[] words)
    {
        var list = words.Select(w => new OcrWord(w.text, w.x, w.y, 0.35, 0.035)).ToList();
        return new OcrResult(list, 1, 1);
    }

    // 仿实机物品页（flow_6 行结构：状态普通1000/状态好3000/状态杰出5000/Exp经验值2x）
    private static OcrResult ItemPageOcr() => Ocr(
        ("我的联赛物品", 0.52, 0.10),           // 标题（区外）
        ("状态普通1000", 0.512, 0.256),          // 行区
        ("有效场数1场", 0.257, 0.288),
        ("状态好3000", 0.512, 0.422),
        ("有效场数1场", 0.266, 0.454),
        ("状态杰出5000", 0.512, 0.590),
        ("有效场数1场", 0.265, 0.619),
        ("Exp经验值2x有效场数5场3000", 0.479, 0.766),
        ("返回详情商品详情", 0.343, 0.934));     // 底栏（区外）

    [Fact]
    public void SortByItemRow_AscendingOrder()
    {
        // 用户示例：同时购买 经验4x(6) + 教练(13) + 杰出(3) → 购买顺序 第3、第6、第13 行
        var targets = new List<MyLeagueBrain.MlItemTarget>
        {
            new(MyLeagueBrain.MlItemKind.ExpBoost, "4x"),
            new(MyLeagueBrain.MlItemKind.CoachBoost),
            new(MyLeagueBrain.MlItemKind.ConditionBoost, "杰出"),
        };
        var sorted = MyLeagueBrain.SortByItemRow(targets);
        Assert.Equal(new[] { "杰出", "4x", "教练强化" },
            sorted.Select(t => t.Kind switch
            {
                MyLeagueBrain.MlItemKind.ExpBoost => t.Level,
                MyLeagueBrain.MlItemKind.CoachBoost => "教练强化",
                _ => t.Level,
            }).ToArray());
    }

    [Fact]
    public void ExpectedItemRow_FixedOrder()
    {
        // 用户确认的固定行序（2026-10-10）：状态1~3（普通+/好+/杰出）、经验4~6（2x/3x/4x）、主教练13
        Assert.Equal(1, MyLeagueBrain.ExpectedItemRow(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ConditionBoost, "普通+")));
        Assert.Equal(2, MyLeagueBrain.ExpectedItemRow(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ConditionBoost, "好+")));
        Assert.Equal(3, MyLeagueBrain.ExpectedItemRow(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ConditionBoost, "杰出")));
        Assert.Equal(4, MyLeagueBrain.ExpectedItemRow(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, "2x")));
        Assert.Equal(5, MyLeagueBrain.ExpectedItemRow(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, "3x")));
        Assert.Equal(6, MyLeagueBrain.ExpectedItemRow(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, "4x")));
        Assert.Equal(13, MyLeagueBrain.ExpectedItemRow(new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.CoachBoost)));
    }

    [Fact]
    public void FindItemRow_ExpBoost_WithLevel()
    {
        var row = MyLeagueBrain.FindItemRow(ItemPageOcr(),
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, "2x"));
        Assert.NotNull(row);
        Assert.Contains("经验值", row.Text);
        Assert.False(row.InUse); // 未拥有（无使用中标记）→ 可购买
    }

    [Fact]
    public void FindItemRow_InUseRow_SatisfiesLowerWant()
    {
        // 已拥有 4x（使用中）→ 2x 需求判 InUse 跳过；4x 需求也跳过
        var ocr = Ocr(("Exp经验值4x使用中", 0.51, 0.30), ("返回详情商品详情", 0.34, 0.94));
        foreach (var want in new[] { "2x", "4x" })
        {
            var row = MyLeagueBrain.FindItemRow(ocr,
                new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, want));
            Assert.NotNull(row);
            Assert.True(row.InUse);
        }
    }

    [Fact]
    public void FindItemRow_InUseLower_DoesNotSatisfyHigherWant()
    {
        // 已拥有 2x（使用中）→ 4x 需求仍应找可购行（非使用中）
        var ocr = Ocr(("Exp经验值2x使用中", 0.51, 0.30),
            ("Exp经验值4x有效场数5场5000", 0.51, 0.50),
            ("返回详情商品详情", 0.34, 0.94));
        var row = MyLeagueBrain.FindItemRow(ocr,
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, "4x"));
        Assert.NotNull(row);
        Assert.False(row.InUse);
        Assert.Contains("4x", row.Text);
    }

    [Fact]
    public void FindItemRow_Coach_InUse()
    {
        var ocr = Ocr(("主教练强化使用中", 0.50, 0.60), ("返回详情商品详情", 0.34, 0.94));
        var row = MyLeagueBrain.FindItemRow(ocr,
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.CoachBoost));
        Assert.NotNull(row);
        Assert.True(row.InUse);
    }

    [Fact]
    public void FindItemRow_ExpBoost_LevelMismatch_FallsBackToCategory()
    {
        // 档位词没读到/不匹配时兜底按类别词命中，不漏买
        var row = MyLeagueBrain.FindItemRow(ItemPageOcr(),
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, "4x"));
        Assert.NotNull(row);
        Assert.Contains("经验值", row.Text);
    }

    [Fact]
    public void FindItemRow_ConditionBoost_PlusStrippedMatching()
    {
        // 配置「普通+」，OCR 行常丢加号（"状态普通1000"）→ 去加号匹配
        var row = MyLeagueBrain.FindItemRow(ItemPageOcr(),
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ConditionBoost, "普通+"));
        Assert.NotNull(row);
        Assert.Contains("普通", row.Text);
    }

    [Fact]
    public void FindItemRow_ConditionBoost_Outstanding()
    {
        var row = MyLeagueBrain.FindItemRow(ItemPageOcr(),
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ConditionBoost, "杰出"));
        Assert.NotNull(row);
        Assert.Contains("杰出", row.Text);
    }

    [Fact]
    public void FindItemRow_ConditionBoost_LevelNotFound_FallsBack()
    {
        var row = MyLeagueBrain.FindItemRow(ItemPageOcr(),
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ConditionBoost, "好+"));
        Assert.NotNull(row); // 兜底命中状态行
        Assert.Contains("状态", row.Text);
    }

    [Fact]
    public void FindItemRow_NotOnPage_ReturnsNull()
    {
        var ocr = Ocr(("比赛", 0.1, 0.11), ("我的联赛", 0.72, 0.37)); // 非物品页
        Assert.Null(MyLeagueBrain.FindItemRow(ocr,
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.CoachBoost)));
    }

    // ---------------- 主页「使用的物品」面板已拥有判定（flow_3） ----------------

    private static OcrResult PanelOcr(string expItem) => Ocr(
        ("赛程表兑换积分使用的物品", 0.406, 0.371),
        ("我的联赛积分物品剩余比赛场次", 0.497, 0.432),
        (expItem, 0.718, 0.469),
        ("返回设置比赛计划", 0.343, 0.932));

    [Theory]
    [InlineData("Exp经验值4x", "2x")]   // 高档满足低档
    [InlineData("Exp经验值4x", "4x")]
    [InlineData("Exp经验值2x", "3x", false)] // 低档不满足高档
    public void IsOwned_ExpBoost_Panel(string panelItem, string want, bool expected = true)
    {
        var owned = MyLeagueBrain.IsOwned(PanelOcr(panelItem),
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ExpBoost, want));
        Assert.Equal(expected, owned);
    }

    [Theory]
    [InlineData("状态杰出", "普通+", true)]
    [InlineData("状态好", "杰出", false)]
    [InlineData("状态普通", "普通+", true)]
    public void IsOwned_Condition_Panel(string panelItem, string want, bool expected)
    {
        var owned = MyLeagueBrain.IsOwned(PanelOcr(panelItem),
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.ConditionBoost, want));
        Assert.Equal(expected, owned);
    }

    [Fact]
    public void IsOwned_Coach_Panel()
    {
        Assert.True(MyLeagueBrain.IsOwned(PanelOcr("主教练强化"),
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.CoachBoost)));
        Assert.False(MyLeagueBrain.IsOwned(PanelOcr("Exp经验值4x"),
            new MyLeagueBrain.MlItemTarget(MyLeagueBrain.MlItemKind.CoachBoost)));
    }

    [Fact]
    public void PanelState_Detection()
    {
        Assert.True(MyLeagueBrain.ShowsItemsPanel(PanelOcr("Exp经验值4x")));
        Assert.True(MyLeagueBrain.ShowsChallengePanel(Ocr(("赛程表兑换积分挑战", 0.389, 0.368))));
    }
}

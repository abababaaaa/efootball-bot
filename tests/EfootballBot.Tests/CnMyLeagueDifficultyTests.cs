using EfootballBot.Core.Bot.Scenarios.CnMyLeague;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

// 国服我的联赛「选择比赛等级」难度页规划回归（2026-10-18 实机：国服页没有国际服的
// 「底行 取消/确认」结构——光标在 7 行难度列表内游走、A=勾选光标行、RT=确定开赛。
// 国际服基类按「默认光标在底行确认」导航，末次 A 落在 业余 行把勾反复打回——用户实机
// 日志证据：首轮读 当前=新手，此后永远 业余，死循环改选）。规划契约：
// 上×难度表长度饱和归位首行（与光标当前位置无关，列表到头即停不回绕）→
// 按难度表行号（非当帧 OCR 检出子集，漏读即错位）下移落位 → 需改选时 A 勾选 → RT 确定。
public class CnMyLeagueDifficultyTests
{
    [Theory]
    [InlineData("业余", "传奇", 6, true)]   // 第 2 → 第 7 行：下×6，需 A 勾选
    [InlineData("新手", "传奇", 6, true)]
    [InlineData("传奇", "业余", 1, true)]   // 第 7 → 第 2 行：下×1
    [InlineData("新手", "业余", 1, true)]
    [InlineData("标准", "标准", 2, false)]  // 已是目标：落位后直接 RT，无需再勾
    public void Plan_LandsByCanonicalTableRow(string selected, string wanted, int down, bool checkFirst)
    {
        var plan = CnMyLeagueScenario.PlanCnDifficulty(selected, wanted);
        Assert.NotNull(plan);
        Assert.Equal(MyLeagueBrain.Difficulties.Length, plan!.UpStrokes); // 饱和归位，与当前位置无关
        Assert.Equal(down, plan.DownStrokes);
        Assert.Equal(checkFirst, plan.CheckFirst);
        Assert.Contains(wanted, plan.Summary);
    }

    [Theory]
    [InlineData(null)]        // 蓝勾未读出：仍落位目标行再 RT（RT 按光标行=正中目标；按已勾行=游戏默认）
    [InlineData("幽灵难度")]  // 读出的选中名不在难度表（OCR 误读）：同样落位目标行
    public void Plan_UnknownCurrent_StillLandsOnWantedRow(string? selected)
    {
        var plan = CnMyLeagueScenario.PlanCnDifficulty(selected, "传奇");
        Assert.NotNull(plan);
        Assert.Equal(MyLeagueBrain.Difficulties.Length, plan!.UpStrokes);
        Assert.Equal(6, plan.DownStrokes);
        Assert.False(plan.CheckFirst);
    }

    [Fact]
    public void Plan_InvalidWanted_NeverNavigates()
    {
        // 目标难度名不合法（配置异常）：null = 按游戏默认直接确认，绝不盲导航
        Assert.Null(CnMyLeagueScenario.PlanCnDifficulty("业余", "地狱级"));
        Assert.Null(CnMyLeagueScenario.PlanCnDifficulty(null, ""));
    }

    [Fact]
    public void DifficultyTable_MatchesCnPageRowOrder()
    {
        // 国服页 7 行自上而下与难度表一致（实机 2026-10-18：传奇在第 7 行、新手在第 1 行）
        Assert.Equal(7, MyLeagueBrain.Difficulties.Length);
        Assert.Equal("新手", MyLeagueBrain.Difficulties[0]);
        Assert.Equal("传奇", MyLeagueBrain.Difficulties[^1]);
    }

    // ---------------- 新式难度页（2026-11-04 实机改版：国际服同款 UI） ----------------
    // 底行「取消 | 确认」结构出现 → RT 配方作废（RT 无绑定，永远开不了赛），切换
    // 国际服 DifficultyLegPlan 多轮配方。检测契约：取消+确认同在 y>0.85 且页内有难度名；
    // 无底行词（OCR 漏读）→ 回退旧列表游走配方。

    private static OcrWord W(string text, double cx, double cy)
        => new(text, cx - 0.05, cy - 0.015, 0.1, 0.03);

    private static OcrResult Ocr(params OcrWord[] words) => new(words, 1280, 720);

    private static OcrResult ModernDifficultyPage() => new(new[]
    {
        W("选择赛事等级", 0.40, 0.11),
        W("新手", 0.30, 0.35), W("业余", 0.30, 0.40), W("标准", 0.30, 0.45),
        W("职业", 0.30, 0.50), W("顶级球员", 0.30, 0.55), W("超级球星", 0.30, 0.60),
        W("传奇", 0.30, 0.66),
        W("取消", 0.38, 0.94), W("确认", 0.66, 0.94),
    }, 1280, 720);

    [Fact]
    public void ModernPage_HasBottomConfirmRow()
    {
        // 实机截图（2026-11-04）：底行 取消/确认 在 y≈0.94，列表 7 行难度名齐全
        Assert.True(CnMyLeagueScenario.HasBottomConfirmRow(ModernDifficultyPage()));
    }

    [Fact]
    public void OldStylePage_NoBottomRow_FallsBackToLegacyPlan()
    {
        // 旧式页：只有难度列表，无底行取消/确认（2026-10-18 页面形态）
        var old = Ocr(W("选择赛事等级", 0.4, 0.1), W("新手", 0.3, 0.3), W("传奇", 0.3, 0.66));
        Assert.False(CnMyLeagueScenario.HasBottomConfirmRow(old));
        // 旧配方对旧页仍然成立：RT 确定开赛
        Assert.NotNull(CnMyLeagueScenario.PlanCnDifficulty(null, "传奇"));
    }

    [Fact]
    public void ModernLegPlan_ReusesIntlRecipe()
    {
        // 新式页轮次配方直接复用国际服 DifficultyLegPlan：第 1 轮纯 A（确认可能已聚焦）、
        // 第 4 轮 null = B 逃生重进；落底/右拨细目由国际服自身测试锁定，此处不重复
        Assert.Equal((false, 0, 0), MyLeagueBrain.DifficultyLegPlan(1)!.Value);
        Assert.Null(MyLeagueBrain.DifficultyLegPlan(4));
    }
}

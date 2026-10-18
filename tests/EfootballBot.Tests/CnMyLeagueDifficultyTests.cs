using EfootballBot.Core.Bot.Scenarios.CnMyLeague;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
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
}

using EfootballBot.Core.Bot;
using Xunit;

namespace EfootballBot.Tests;

// 周活动「全部完成后接力」的目标校验：两服界面互不通用，跨服目标必须被拒绝
// （配置手改成别的模式名时，绝不在国际服界面上跑国服导航）
public class HandoffTargetTests
{
    [Theory]
    [InlineData("intl", true)]
    [InlineData("intlladder", true)]
    [InlineData("cnmyleague", false)]
    [InlineData("cndaily", false)]
    [InlineData("intlswaptest", false)]
    [InlineData("intlweekly", false)]
    [InlineData("cnweekly", false)]
    [InlineData("", false)]
    public void 国际服只接受国际服模块(string mode, bool expected)
        => Assert.Equal(expected, BotEngine.IsHandoffTarget(mode, cn: false));

    [Theory]
    [InlineData("cnmyleague", true)]
    [InlineData("cndaily", true)]
    [InlineData("intl", false)]
    [InlineData("intlladder", false)]
    [InlineData("cnweekly", false)]
    [InlineData("", false)]
    public void 国服只接受国服模块(string mode, bool expected)
        => Assert.Equal(expected, BotEngine.IsHandoffTarget(mode, cn: true));

    [Fact]
    public void 周活动配置默认不接力()
    {
        Assert.Equal("", new EfootballBot.Core.Config.WeeklyConfig().AfterCompleteModule);
        Assert.Equal("", new EfootballBot.Core.Config.CnWeeklyConfig().AfterCompleteModule);
    }
}

using EfootballBot.Core;
using System.Reflection;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Bot.Modules;
using Xunit;

namespace EfootballBot.Tests;

// 挂机模块注册表契约（v7.2 模块化）：加模块只改 ModuleIds + BotModuleCatalog，别处零改动。
// 注册表写错在运行期只表现为「点了没反应」「接力到另一个服」，日志里看不出来——
// 这几条断言把结构性错误挡在编译/测试阶段。
public class ModuleCatalogTests
{
    [Fact]
    public void 注册表结构自检通过()
    {
        var problems = BotModuleCatalog.SelfCheck();
        Assert.True(problems.Count == 0, string.Join("；", problems));
    }

    [Fact]
    public void ModuleIds_里的每个常量都已登记()
    {
        var consts = typeof(ModuleIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(consts);
        foreach (var id in consts)
            Assert.True(BotModuleCatalog.Find(id) is not null, $"ModuleIds.{id} 未在 BotModuleCatalog 登记");
    }

    [Fact]
    public void 注册表里的每个模块都有唯一小写_id_与名字()
    {
        var ids = BotModuleCatalog.All.Select(d => d.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(BotModuleCatalog.All, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Id));
            Assert.Equal(d.Id.ToLowerInvariant(), d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.DisplayName));
        });
    }

    [Fact]
    public void 国服与国际服各自成组_没有跨服模块()
    {
        var intl = BotModuleCatalog.By(ServerRegion.Intl);
        var cn = BotModuleCatalog.By(ServerRegion.Cn);
        Assert.NotEmpty(intl);
        Assert.NotEmpty(cn);

        // 每个模块的 ServerOf 与它所在的组一致；两组的并集就是全部注册项
        foreach (var d in intl) Assert.Equal(ServerRegion.Intl, BotModuleCatalog.ServerOf(d.Id));
        foreach (var d in cn) Assert.Equal(ServerRegion.Cn, BotModuleCatalog.ServerOf(d.Id));
        Assert.Equal(BotModuleCatalog.All.Count, intl.Count + cn.Count);
    }

    [Fact]
    public void 国服模块清单包含天梯赛_周活动_我的联赛_赛前设置()
    {
        var cnIds = BotModuleCatalog.By(ServerRegion.Cn).Select(d => d.Id).ToArray();
        Assert.Contains(ModuleIds.CnLadder, cnIds);
        Assert.Contains(ModuleIds.CnWeekly, cnIds);
        Assert.Contains(ModuleIds.CnMyLeague, cnIds);
        Assert.Contains(ModuleIds.CnPreMatch, cnIds);
    }

    [Fact]
    public void 国际服模块清单包含我的联赛_周活动_AI天梯_种马_赛前设置()
    {
        var intlIds = BotModuleCatalog.By(ServerRegion.Intl).Select(d => d.Id).ToArray();
        Assert.Contains(ModuleIds.IntlMyLeague, intlIds);
        Assert.Contains(ModuleIds.IntlWeekly, intlIds);
        Assert.Contains(ModuleIds.IntlAiLadder, intlIds);
        Assert.Contains(ModuleIds.IntlStudBuy, intlIds);
        Assert.Contains(ModuleIds.IntlStudTrain, intlIds);
        Assert.Contains(ModuleIds.IntlPreMatch, intlIds);
    }

    [Fact]
    public void 赛前设置是工具页_引擎不接管手柄()
    {
        Assert.False(BotModuleCatalog.IsScenarioMode(ModuleIds.CnPreMatch));
        Assert.False(BotModuleCatalog.IsScenarioMode(ModuleIds.IntlPreMatch));
        // 工具页/未知 id 一律造不出场景（绝不静默跑成别的模块）
        Assert.Null(BotModuleCatalog.Create(null!, ModuleIds.CnPreMatch, (_, _) => { }));
        Assert.Null(BotModuleCatalog.Create(null!, ModuleIds.IntlPreMatch, (_, _) => { }));
    }

    [Theory]
    [InlineData(EngineModes.Observe)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("Intl")]
    public void 未注册或纯观察模式不算挂机场景(string id)
        => Assert.False(BotModuleCatalog.IsScenarioMode(id));

    [Fact]
    public void 全部挂机场景都有工厂_且都算场景模式()
    {
        foreach (var d in BotModuleCatalog.All.Where(d => d.Kind == ModuleKind.Scenario))
        {
            Assert.NotNull(d.Factory);
            Assert.True(BotModuleCatalog.IsScenarioMode(d.Id), $"{d.Id} 应被识别为挂机场景");
        }
    }

    [Theory]
    [InlineData(ModuleIds.IntlMyLeague, false, true)]
    [InlineData(ModuleIds.IntlAiLadder, false, true)]
    [InlineData(ModuleIds.CnMyLeague, true, true)]
    [InlineData(ModuleIds.CnLadder, true, true)]
    public void 接力白名单只含本服可接力场景(string id, bool cn, bool expected)
        => Assert.Equal(expected, BotModuleCatalog.IsHandoffTarget(id, cn));

    [Theory]
    [InlineData(ModuleIds.IntlWeekly, false)]
    [InlineData(ModuleIds.IntlStudBuy, false)]
    [InlineData(ModuleIds.IntlSwapTest, false)]
    [InlineData(ModuleIds.CnWeekly, true)]
    [InlineData(ModuleIds.CnPreMatch, true)]
    [InlineData(EngineModes.Observe, false)]
    [InlineData("", false)]
    public void 周活动_种马_换人测试_工具页不可被接力(string id, bool cn)
        => Assert.False(BotModuleCatalog.IsHandoffTarget(id, cn));

    [Fact]
    public void 跨服接力一律拒绝()
    {
        Assert.False(BotModuleCatalog.IsHandoffTarget(ModuleIds.CnMyLeague, cn: false));
        Assert.False(BotModuleCatalog.IsHandoffTarget(ModuleIds.CnLadder, cn: false));
        Assert.False(BotModuleCatalog.IsHandoffTarget(ModuleIds.IntlMyLeague, cn: true));
        Assert.False(BotModuleCatalog.IsHandoffTarget(ModuleIds.IntlAiLadder, cn: true));
    }

    [Fact]
    public void 接力候选是本服可接力模块_且排除自己()
    {
        var intl = BotModuleCatalog.HandoffCandidates(ServerRegion.Intl, ModuleIds.IntlWeekly);
        Assert.All(intl, d => Assert.Equal(ServerRegion.Intl, d.Server));
        Assert.All(intl, d => Assert.True(d.HandoffEligible));
        Assert.DoesNotContain(intl, d => d.Id == ModuleIds.IntlWeekly);
        Assert.Contains(intl, d => d.Id == ModuleIds.IntlMyLeague);
        Assert.Contains(intl, d => d.Id == ModuleIds.IntlAiLadder);

        var cn = BotModuleCatalog.HandoffCandidates(ServerRegion.Cn, ModuleIds.CnWeekly);
        Assert.All(cn, d => Assert.Equal(ServerRegion.Cn, d.Server));
        Assert.Contains(cn, d => d.Id == ModuleIds.CnMyLeague);
        Assert.Contains(cn, d => d.Id == ModuleIds.CnLadder);
    }

    [Fact]
    public void 引擎的接力校验与注册表一致()
    {
        // 引擎不再自带白名单，只有一层委托：两服四个模块可接力，其余一律拒绝
        Assert.True(BotEngine.IsHandoffTarget(ModuleIds.IntlMyLeague, cn: false));
        Assert.True(BotEngine.IsHandoffTarget(ModuleIds.IntlAiLadder, cn: false));
        Assert.True(BotEngine.IsHandoffTarget(ModuleIds.CnMyLeague, cn: true));
        Assert.True(BotEngine.IsHandoffTarget(ModuleIds.CnLadder, cn: true));
        Assert.False(BotEngine.IsHandoffTarget(ModuleIds.IntlMyLeague, cn: true));
        Assert.False(BotEngine.IsHandoffTarget(ModuleIds.CnLadder, cn: false));
    }
}


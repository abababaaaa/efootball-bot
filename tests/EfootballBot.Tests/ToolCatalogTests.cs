using EfootballBot.Core;
using System.Reflection;
using EfootballBot.Core.Tools;
using Xunit;

namespace EfootballBot.Tests;

// 工具模块注册表契约（v7.2）：与挂机模块同一套思路——
// 工具唯一清单在 ToolCatalog，界面只按 (id, 动作) 派发，不许认识具体工具类型。
public class ToolCatalogTests
{
    [Fact]
    public void 注册表结构自检通过()
    {
        var problems = ToolCatalog.SelfCheck();
        Assert.True(problems.Count == 0, string.Join("；", problems));
    }

    [Fact]
    public void ToolIds_里的每个常量都已登记()
    {
        var consts = typeof(ToolIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();
        Assert.NotEmpty(consts);
        foreach (var id in consts)
            Assert.True(ToolCatalog.Find(id) is not null, $"ToolIds.{id} 未在 ToolCatalog 登记");
    }

    [Fact]
    public void 注册项_id_与实现自报的_id_一致()
        => Assert.All(ToolCatalog.All, d => Assert.Equal(d.Id, d.Tool.Id));

    [Fact]
    public void 每个工具都声明了动作()
        => Assert.All(ToolCatalog.All, d => Assert.NotEmpty(d.Actions));

    [Fact]
    public void 赛前设置按分区区分_路径类工具与分区无关()
    {
        Assert.True(ToolCatalog.Find(ToolIds.PreMatch)!.ServerScoped);
        Assert.False(ToolCatalog.Find(ToolIds.Shell)!.ServerScoped);
        Assert.False(ToolCatalog.Find(ToolIds.WindowRescue)!.ServerScoped);
    }

    [Fact]
    public void 赛前设置支持三个动作()
    {
        var d = ToolCatalog.Find(ToolIds.PreMatch)!;
        Assert.Contains(ToolAction.LaunchGame, d.Actions);
        Assert.Contains(ToolAction.OpenSettingsTool, d.Actions);
        Assert.Contains(ToolAction.OpenGameDirectory, d.Actions);
    }

    [Fact]
    public void 打开路径工具支持打开_定位_打开地址()
    {
        var d = ToolCatalog.Find(ToolIds.Shell)!;
        Assert.Contains(ToolAction.OpenPath, d.Actions);
        Assert.Contains(ToolAction.RevealPath, d.Actions);
        Assert.Contains(ToolAction.OpenUrl, d.Actions);
    }

    [Fact]
    public async Task 未注册工具返回失败而不是静默()
    {
        var ctx = new ToolContext(ServerRegion.Intl, new EfootballBot.Core.Config.AppConfig(), (_, _) => { });
        var r = await ToolCatalog.ExecuteAsync("no-such-tool", ToolAction.OpenPath, ctx);
        Assert.False(r.Ok);
        Assert.Contains("未在注册表登记", r.Message);
    }

    [Fact]
    public async Task 不支持的动作返回失败并说明()
    {
        var ctx = new ToolContext(ServerRegion.Intl, new EfootballBot.Core.Config.AppConfig(), (_, _) => { });
        // 窗口防卡死只支持 RescueWindow，不给它派 LaunchGame
        var r = await ToolCatalog.ExecuteAsync(ToolIds.WindowRescue, ToolAction.LaunchGame, ctx);
        Assert.False(r.Ok);
        Assert.Contains("不支持动作", r.Message);
    }

    [Fact]
    public async Task 打开路径工具缺路径时返回失败()
    {
        var ctx = new ToolContext(ServerRegion.Intl, new EfootballBot.Core.Config.AppConfig(), (_, _) => { });
        var r = await ToolCatalog.ExecuteAsync(ToolIds.Shell, ToolAction.OpenPath, ctx);
        Assert.False(r.Ok);
        Assert.Contains("没有指定", r.Message);
    }

    [Fact]
    public void 两服都有独立的游戏画像与进程名()
    {
        var intl = ToolPaths.Of(ServerRegion.Intl);
        var cn = ToolPaths.Of(ServerRegion.Cn);
        Assert.Equal(EfootballBot.Core.GameProcesses.Intl, intl.ProcessName);
        Assert.Equal(EfootballBot.Core.GameProcesses.Cn, cn.ProcessName);
        Assert.NotNull(intl.SteamAppId);     // 国际服可走 Steam 正规链路
        Assert.Null(cn.SteamAppId);          // 国服绝不走 Steam（会打开国际服设置工具）
        Assert.NotEqual(intl.GameExeName, cn.GameExeName);
    }

    [Fact]
    public void 分区画像的简称用于日志与提示()
    {
        Assert.Equal("国际服", ToolPaths.Of(ServerRegion.Intl).Label);
        Assert.Equal("国服", ToolPaths.Of(ServerRegion.Cn).Label);
        Assert.Equal("国服", ServerRegionText.Display(ServerRegion.Cn));
        Assert.Equal("intl", ServerRegionText.Key(ServerRegion.Intl));
    }
}

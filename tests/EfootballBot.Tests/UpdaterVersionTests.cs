using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

// 版本号截断回归（2026-10-15 "本地还是老版本" 反馈）：Updater 旧写法 ToString(3) 把测试版
// 2.0.5.3 截成 2.0.5 —— 与远端比较永远更小，更新提示永不消失、更新完还提示；
// 关于页/更新横幅/启动日志/反馈包显示的版本号也全部停在旧段位
public class UpdaterVersionTests
{
    [Theory]
    [InlineData("2.0.5.3", "2.0.5.3")] // 测试版：4 段全保留
    [InlineData("2.0.5.1", "2.0.5.1")]
    [InlineData("2.0.6.0", "2.0.6")]   // 正式版（程序集 revision 恒补 0）：渲染 3 段
    [InlineData("2.0.4", "2.0.4")]     // 3 段 Version（revision=-1，负字段省略）
    public void FormatVersion_TestVersionKeepsAllSegments(string input, string expected)
        => Assert.Equal(expected, Updater.FormatVersion(Version.Parse(input)));

    [Fact]
    public void FormatVersion_Null_ReturnsPlaceholder()
        => Assert.Equal("0.0.0", Updater.FormatVersion(null));

    [Fact]
    public void CurrentVersion_KeepsFourthSegment()
    {
        // 程序集版本来自 csproj <Version>：测试版第 4 段必须在（截断则本断言失败）
        var v = Updater.CurrentVersion;
        Assert.True(v.Major >= 2);
        Assert.NotEqual(-1, v.Revision);
    }
}

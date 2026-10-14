using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 「释放玩家 1」机制的纯函数测试：shim vbs 内容生成（路径含空格/引号转义）、
/// shim 路径命名、禁用记录的配置序列化。
/// </summary>
public sealed class PhysicalPadControlTests
{
    [Fact]
    public void BuildShimScript_QuotesExePath_AndRunsHidden()
    {
        var script = HidHideManager.BuildShimScript(@"B:\Efootball 挂机\publish\EfootballBot.exe", "pad-hide");

        // vbs 字符串内双引号加倍：路径必须完整出现在 ""…"" 中，参数跟在后面
        Assert.Contains("\"\"B:\\Efootball 挂机\\publish\\EfootballBot.exe\"\" --hidhide-config pad-hide", script);
        // 0 = 隐藏窗口（登录触发不闪控制台），False = 不等待（任务即发即回）
        Assert.Contains("\", 0, False", script);
    }

    [Fact]
    public void BuildShimScript_EachTaskMode_GetsOwnShimName()
    {
        foreach (var (_, arg) in HidHideManager.AllTasks)
        {
            Assert.Equal($"hidhide-{arg}.vbs",
                System.IO.Path.GetFileName(HidHideManager.ShimPathFor(arg)));
            Assert.Contains($"--hidhide-config {arg}", HidHideManager.BuildShimScript(@"C:\x\EfootballBot.exe", arg));
        }
    }

    [Fact]
    public void DisabledPadDevs_JsonRoundTrip()
    {
        var cfg = new EfootballBot.Core.Config.HidHideConfig();
        cfg.DisabledPadDevs.Add(@"HID\VID_045E&PID_028E&IG_01\3&5D59EC1&0&0000");
        cfg.DisabledPadDevs.Add(@"USB\VID_045E&PID_028E&IG_01\2&DEE0F28&1&01");

        var json = System.Text.Json.JsonSerializer.Serialize(cfg);
        var back = System.Text.Json.JsonSerializer.Deserialize<EfootballBot.Core.Config.HidHideConfig>(json);

        Assert.NotNull(back);
        Assert.Equal(2, back!.DisabledPadDevs.Count);
        Assert.Contains(@"USB\VID_045E&PID_028E&IG_01\2&DEE0F28&1&01", back.DisabledPadDevs);
    }

    [Theory]
    [InlineData(@"HID\VID_045E&PID_028E&IG_01\3&5D59EC1&0&0000", true)]      // 本机真实 360 手柄集合
    [InlineData(@"HID\VID_045E&PID_028E&IG_00\3&34F4D81E&0&0000", true)]     // IG_00 形态
    [InlineData(@"HID\VID_0F0D&PID_00C1&IG_00\6&2DF92C3&0&0000", true)]      // 第三方 XInput 手柄
    [InlineData(@"HID\VID_1EA7&PID_0067&MI_02&COL02\A&1173CC4E&0&0001", false)] // 键盘 HID 集合
    [InlineData(@"USB\VID_045E&PID_028E&IG_01\2&DEE0F28&1&01", false)]       // USB 前缀非 HID 集合
    [InlineData(@"HID\VID_045E&PID_028E\5&2DEE0F28&0&0000", false)]          // 无 IG 段（DInput 形态）
    public void IsXInputStyleInstance_MatchesXusbChildren_Only(string path, bool expected)
        => Assert.Equal(expected, HidDeviceEnumerator.IsXInputStyleInstance(path));
}

using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 手柄接管测试：shim vbs 内容生成（路径含空格/引号转义）、shim/任务命名（pad-hide / pad-restore）、
/// 禁用记录的配置序列化（LegacyPadRestore 消费）、XInput 形态实例路径判定。
/// </summary>
public sealed class PhysicalPadControlTests
{
    [Fact]
    public void BuildShimScript_QuotesExePath_AndRunsHidden()
    {
        var script = PhysicalPadControl.BuildShimScript(@"B:\Efootball 挂机\publish\EfootballBot.exe", "pad-restore");

        // vbs 字符串内双引号加倍：路径必须完整出现在 ""…"" 中，参数跟在后面
        Assert.Contains("\"\"B:\\Efootball 挂机\\publish\\EfootballBot.exe\"\" --pad-config pad-restore", script);
        // 0 = 隐藏窗口（登录触发不闪控制台），False = 不等待（任务即发即回）
        Assert.Contains("\", 0, False", script);
    }

    [Fact]
    public void BuildShimScript_EachTaskMode_GetsOwnShimName()
    {
        Assert.Equal(2, PhysicalPadControl.AllTasks.Length); // pad-hide + pad-restore
        foreach (var (name, arg) in PhysicalPadControl.AllTasks)
        {
            Assert.Equal($"pad-{arg}.vbs",
                System.IO.Path.GetFileName(PhysicalPadControl.ShimPathFor(arg)));
            Assert.Contains($"--pad-config {arg}", PhysicalPadControl.BuildShimScript(@"C:\x\EfootballBot.exe", arg));
        }
        Assert.Contains("PadHide", PhysicalPadControl.PadHideTaskName);
        Assert.Contains("PadRestore", PhysicalPadControl.PadRestoreTaskName);
    }

    [Fact]
    public void DisabledPadDevs_JsonRoundTrip()
    {
        var cfg = new PadTakeoverConfig();
        cfg.DisabledPadDevs.Add(@"HID\VID_045E&PID_028E&IG_01\3&5D59EC1&0&0000");
        cfg.DisabledPadDevs.Add(@"USB\VID_045E&PID_028E&IG_01\2&DEE0F28&1&01");

        var json = System.Text.Json.JsonSerializer.Serialize(cfg);
        var back = System.Text.Json.JsonSerializer.Deserialize<PadTakeoverConfig>(json);

        Assert.NotNull(back);
        Assert.Equal(2, back!.DisabledPadDevs.Count);
        Assert.Contains(@"USB\VID_045E&PID_028E&IG_01\2&DEE0F28&1&01", back.DisabledPadDevs);
    }

    /// <summary>旧配置 JSON 键名 "HidHide" 必须继续可读：升级用户的 DisabledPadDevs
    /// 恢复清单不能因改名丢失（丢失 = 用户真实手柄卡在禁用态）。</summary>
    [Fact]
    public void 旧配置键名HidHide_反序列化到PadTakeover_保留恢复清单()
    {
        const string legacyJson = """
            { "HidHide": { "Enabled": true, "GameImagePath": "C:\\Games\\eFootball.exe",
                "AppliedBlacklist": ["HID\\VID_045E&PID_028E\\X"],
                "DisabledPadDevs": ["HID\\VID_045E&PID_028E&IG_01\\3&5D59EC1&0&0000"] } }
            """;
        var cfg = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(legacyJson);

        Assert.NotNull(cfg);
        Assert.True(cfg!.HidHide.HidePhysicalGamepad);
        var dev = Assert.Single(cfg.HidHide.DisabledPadDevs);
        Assert.Equal(@"HID\VID_045E&PID_028E&IG_01\3&5D59EC1&0&0000", dev);
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

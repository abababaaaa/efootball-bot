using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

// 赛前快捷设置：Settings.exe 三级定位中「向上逐级查找」的纯文件系统单测
// 实测目录结构：国际服 Settings.exe 在游戏根目录；国服在 {根}\SettingTool\ 子目录
public class GameSettingsLocatorTests : IDisposable
{
    private readonly string _root;

    public GameSettingsLocatorTests()
    {
        _root = Directory.CreateTempSubdirectory("bot-locator-test").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string TouchDir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
        return path;
    }

    [Fact]
    public void Finds_Plain_Settings_At_Game_Root_Intl_Layout()
    {
        // 国际服：{根}\Settings.exe，游戏进程在 {根}\eFootball\Binaries\Win64\
        TouchDir("Settings.exe");
        var procDir = Dir("eFootball", "Binaries", "Win64");

        var found = GameSettingsLocator.FindSettingsNear(procDir);

        Assert.Equal(Path.Combine(_root, "Settings.exe"), found);
    }

    [Fact]
    public void Finds_SettingsTool_Subdir_Cn_Layout()
    {
        // 国服：{根}\SettingTool\Settings.exe，游戏进程在 {根}\eFootball\Binaries\Win64\
        TouchDir("SettingTool", "Settings.exe");
        var procDir = Dir("eFootball", "Binaries", "Win64");

        var found = GameSettingsLocator.FindSettingsNear(procDir);

        Assert.Equal(Path.Combine(_root, "SettingTool", "Settings.exe"), found);
    }

    [Fact]
    public void Prefers_Plain_Over_Subdir_At_Same_Level()
    {
        TouchDir("Settings.exe");
        TouchDir("SettingTool", "Settings.exe");
        var procDir = Dir("eFootball", "Binaries", "Win64");

        var found = GameSettingsLocator.FindSettingsNear(procDir);

        Assert.Equal(Path.Combine(_root, "Settings.exe"), found);
    }

    [Fact]
    public void Returns_Null_When_Nothing_Nearby()
    {
        var procDir = Dir("eFootball", "Binaries", "Win64");

        var found = GameSettingsLocator.FindSettingsNear(procDir);

        Assert.Null(found);
    }

    [Fact]
    public void Handles_Null_And_Missing_Dir()
    {
        Assert.Null(GameSettingsLocator.FindSettingsNear(null));
        Assert.Null(GameSettingsLocator.FindSettingsNear(Path.Combine(_root, "not-exist")));
    }

    [Fact]
    public void Locate_Cn_DisabledSteamFallback_NeverFindsIntl()
    {
        // 实机回归（2026-10-13 用户报告）：国服游戏未启动时，Locate 跌进 Steam 回退
        // 打开了国际服的 Settings.exe。国服必须禁用 Steam 回退——进程不在 → null。
        // 本机装有国际服 Steam 版（c:\program files (x86)\steam\...），旧实现此断言失败。
        Assert.Null(GameSettingsLocator.Locate("bot-no-such-game-process", allowSteamFallback: false));
    }

    [Fact]
    public void Locate_Cn_LiveProcess_FindsSettingsTool()
    {
        // 实机验证（2026-10-13）：国服游戏运行中必须定位到 SettingTool\Settings.exe。
        // 国服客户端提权运行，旧 MainModule 方案读不到进程路径 → 国服定位恒失败
        //（用户实机：游戏开着仍报"未定位到"，日志连报 3 次）。
        if (System.Diagnostics.Process.GetProcessesByName("eFootballOnline").Length == 0)
            return; // 国服没开时跳过（GamePaths 路径记忆兜底）
        var found = GameSettingsLocator.Locate("eFootballOnline", allowSteamFallback: false);
        Assert.NotNull(found);
        Assert.True(File.Exists(found), $"定位到不存在的文件: {found}");
    }

    // ---------------- 游戏本体 exe 定位（快速打开游戏） ----------------

    [Fact]
    public void FindGameExeNear_Intl_AtGameRoot()
    {
        // 国际服：{根}\eFootball.exe；线索可以是任一子目录（如设置工具目录），向上逐级命中
        TouchDir("eFootball.exe");
        var hint = Dir("anything");

        var found = GameSettingsLocator.FindGameExeNear(hint, intl: true);

        Assert.Equal(Path.Combine(_root, "eFootball.exe"), found);
    }

    [Fact]
    public void FindGameExeNear_Cn_InBinariesWin64()
    {
        // 国服：{根}\eFootball\Binaries\Win64\eFootballOnline.exe；
        // 线索 = 上次定位的 SettingTool\Settings.exe 所在目录，向上 1 级命中
        var expected = TouchDir("eFootball", "Binaries", "Win64", "eFootballOnline.exe");
        var hint = Dir("SettingTool");

        var found = GameSettingsLocator.FindGameExeNear(hint, intl: false);

        Assert.Equal(expected, found);
    }

    [Fact]
    public void LocateGameExe_Cn_NeverFallsBackToSteam()
    {
        // 与设置工具同规则（实机 2026-10-13 教训）：国服线索找不到 eFootballOnline.exe
        // → null，绝不跌进 Steam 库（那是国际服的安装，会拉起国际服）
        var hint = Dir("SettingTool");

        Assert.Null(GameSettingsLocator.LocateGameExe(intl: false, hint));
    }

    [Fact]
    public void FindGameExeNear_HandlesNullAndMissing()
    {
        Assert.Null(GameSettingsLocator.FindGameExeNear(null, intl: true));
        Assert.Null(GameSettingsLocator.FindGameExeNear(null, intl: false));
        Assert.Null(GameSettingsLocator.FindGameExeNear(Path.Combine(_root, "not-exist"), intl: true));
        // 国服无线索（路径记忆为空）→ null，不抛异常
        Assert.Null(GameSettingsLocator.LocateGameExe(intl: false));
    }
}

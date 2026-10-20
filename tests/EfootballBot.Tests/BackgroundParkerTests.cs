using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>后台停靠失败原因归类：win32err=5(拒绝访问)=游戏提权而本程序未提权(UIPI 拦截)，
/// 其余（含 API 成功但坐标被还原的 0）按独占全屏还原提示。
/// 2026-10-15 实机：国际服游戏以管理员运行，SetWindowPos 恒 ERROR_ACCESS_DENIED，
/// 旧文案误导用户去改无边框窗口模式，实际须管理员重启本程序。</summary>
public class BackgroundParkerTests
{
    [Fact]
    public void UipiAccessDenied_MentionsAdminRestart()
    {
        var msg = BackgroundParker.DescribeFailure(5);
        Assert.Contains("管理员", msg);
        Assert.Contains("以管理员身份运行", msg);
        Assert.DoesNotContain("无边框", msg);
    }

    [Theory]
    [InlineData(0)]     // API 成功但窗口位置被强制还原（独占全屏）
    [InlineData(87)]    // 其他参数类错误
    public void OtherErrors_FallBackToFullscreenHint(int err)
    {
        var msg = BackgroundParker.DescribeFailure(err);
        Assert.Contains("无边框", msg);
        Assert.DoesNotContain("以管理员身份运行", msg);
    }

    /// <summary>2026-10-20 实测定案：eFootball 被停靠移出屏幕后自行缩窗到 70×39（客户区清零，
    /// 渲染/输入全死，bot 对冻结画面按键=「乱点」）。缩窗判定必须拦下这种形态并触发屏外恢复尺寸。</summary>
    [Theory]
    [InlineData(1280, 70, true)]    // 实机缩窗形态：1280→70
    [InlineData(1920, 39, true)]
    [InlineData(1280, 639, true)]   // 恰好缩过半
    [InlineData(1280, 640, false)]  // 半宽边界：未过半不算缩窗
    [InlineData(1280, 1280, false)] // 尺寸正常
    [InlineData(640, 10, false)]    // 原始就小的窗口不参与判定（避免误伤小工具窗）
    [InlineData(0, 0, false)]
    public void WindowShrunk_检测游戏自行缩窗(int originalWidth, int currentWidth, bool expected)
    {
        Assert.Equal(expected, BackgroundParker.WindowShrunk(originalWidth, currentWidth));
    }
}

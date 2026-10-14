using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using Xunit;

namespace EfootballBot.Tests;

// 「我的联赛」说明页亮度探针：白底文档页（从 i 图标误入）vs 深色主题页/球场帧
public class LeagueInfoPageProbeTests
{
    private static FrameData SolidFrame(int w, int h, byte b, byte g, byte r)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }
        return new FrameData(bgra, w, h, 0);
    }

    [Fact]
    public void WhiteDocumentPage_IsMostlyLight()
    {
        // 说明页：白底黑字（黑字占比 <40% 时判亮）——用每 7 像素 1 个黑点（≈14%）模拟，
        // 探针 4 像素步进采样时仅 ~1/28 采样点落在黑点上
        var f = SolidFrame(640, 360, 255, 255, 255);
        var bgra = f.Bgra;
        for (int i = 0; i < bgra.Length; i += 28)
        {
            bgra[i] = 20;
            bgra[i + 1] = 20;
            bgra[i + 2] = 20;
        }
        Assert.True(MyLeagueScenario.IsMostlyLightPage(f));
    }

    [Fact]
    public void DarkThemePage_IsNotLight()
    {
        // 我的联赛深色主页（深蓝黑背景 + 少量亮字）
        var f = SolidFrame(640, 360, 12, 14, 30);
        Assert.False(MyLeagueScenario.IsMostlyLightPage(f));
    }

    [Fact]
    public void PitchGreenFrame_IsNotLight()
    {
        // 球场草绿（比赛帧，RGB 均 <180）
        Assert.False(MyLeagueScenario.IsMostlyLightPage(SolidFrame(640, 360, 60, 140, 70)));
    }
}

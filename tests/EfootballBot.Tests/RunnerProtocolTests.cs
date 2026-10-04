using EfootballBot.Core.Runner;
using Xunit;

namespace EfootballBot.Tests;

public class RunnerProtocolTests
{
    [Fact]
    public void UpLog_RoundTrip()
    {
        var msg = new UpLog("测试日志", "Warn");
        var line = RunnerProtocol.SerializeUp(msg);
        var back = RunnerProtocol.DeserializeUp(line);
        var log = Assert.IsType<UpLog>(back);
        Assert.Equal("测试日志", log.Text);
        Assert.Equal("Warn", log.Level);
    }

    [Fact]
    public void UpHeartbeat_RoundTrip()
    {
        var line = RunnerProtocol.SerializeUp(new UpHeartbeat(12345));
        var hb = Assert.IsType<UpHeartbeat>(RunnerProtocol.DeserializeUp(line));
        Assert.Equal(12345, hb.Tick);
    }

    [Fact]
    public void UpStats_RoundTrip()
    {
        var msg = new UpStats(3, 1, 2, 0, "myleague", "MyLeagueHome", "前往比赛,下一场", "");
        var back = Assert.IsType<UpStats>(RunnerProtocol.DeserializeUp(RunnerProtocol.SerializeUp(msg)));
        Assert.Equal(3, back.MatchesDone);
        Assert.Equal("MyLeagueHome", back.Screen);
    }

    [Fact]
    public void UpPreview_RoundTrip_Base64()
    {
        var msg = new UpPreview(Convert.ToBase64String(new byte[] { 1, 2, 3 }), 480, 270);
        var back = Assert.IsType<UpPreview>(RunnerProtocol.DeserializeUp(RunnerProtocol.SerializeUp(msg)));
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(back.JpegBase64));
        Assert.Equal(480, back.Width);
    }

    [Fact]
    public void DownStop_RoundTrip()
    {
        var line = RunnerProtocol.SerializeDown(new DownStop());
        Assert.IsType<DownStop>(RunnerProtocol.DeserializeDown(line));
    }

    [Fact]
    public void DeserializeUp_UnknownType_ReturnsNull()
    {
        Assert.Null(RunnerProtocol.DeserializeUp("{\"type\":\"nonsense\"}"));
    }

    [Fact]
    public void Deserialize_Garbage_ReturnsNull()
    {
        Assert.Null(RunnerProtocol.DeserializeUp("not json at all"));
        Assert.Null(RunnerProtocol.DeserializeDown(""));
    }
}

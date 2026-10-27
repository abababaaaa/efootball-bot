using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

// 微信实时动态：URL 模板形态 / 分类开关映射 / 事件合并与每日配额。
// 时钟与发送全部注入（SendHook），零网络、全确定性。
public class NotifyPushTests : IDisposable
{
    public NotifyPushTests()
    {
        _now = new DateTime(2026, 10, 26, 9, 0, 0);
        WeChatFeed.Clock = () => _now;
        WeChatFeed.SendHook = (k, t, b) => Sent.Add((k, t, b));
    }

    public void Dispose()
    {
        WeChatFeed.Reset();
        WeChatFeed.SendHook = null;
        WeChatFeed.Clock = () => DateTime.Now;
    }

    private DateTime _now;
    private readonly List<(NotifyKind Kind, string Title, string Body)> Sent = new();

    private static AppConfig Cfg(Action<NotifyConfig>? patch = null)
    {
        var n = new NotifyConfig { WebhookUrl = "https://example.test/send" };
        patch?.Invoke(n);
        return new AppConfig { Notify = n };
    }

    // ---------------- Notifier：URL 模板 ----------------

    [Fact]
    public void GetUrl_MsgPlaceholder_ReplacesEncodedText()
    {
        var url = Notifier.BuildGetUrl("https://x.test/a?msg={msg}", "标题", "正文", "实例", "标题\n正文");
        Assert.NotNull(url);
        Assert.Equal("https://x.test/a?msg=" + Uri.EscapeDataString("标题\n正文"), url);
    }

    [Fact]
    public void GetUrl_TitleBody_SplitsFields()
    {
        var url = Notifier.BuildGetUrl(
            "https://sct.test/KEY.send?title={title}&desp={body}", "标题", "正文", "实例", "标题\n正文");
        Assert.NotNull(url);
        Assert.Equal("https://sct.test/KEY.send?title=" + Uri.EscapeDataString("标题") +
                     "&desp=" + Uri.EscapeDataString("正文"), url);
    }

    [Fact]
    public void GetUrl_Instance_Replaced()
    {
        var url = Notifier.BuildGetUrl("https://x.test/?name={instance}", "t", "b", "号A", "t\nb");
        Assert.NotNull(url);
        Assert.Equal("https://x.test/?name=" + Uri.EscapeDataString("号A"), url);
    }

    [Fact]
    public void GetUrl_NoPlaceholder_ReturnsNull_PostsJson()
    {
        Assert.Null(Notifier.BuildGetUrl("https://x.test/hook", "t", "b", "i", "t\nb"));
    }

    // ---------------- Notifier：企业微信群机器人 ----------------

    [Fact]
    public void WeComUrl_DetectedByDomain()
    {
        Assert.True(Notifier.IsWeComUrl("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=abc"));
        Assert.False(Notifier.IsWeComUrl("https://sctapi.ftqq.com/KEY.send?title={title}"));
    }

    [Fact]
    public void WeComPayload_TextMsgtype_WithOptionalInstancePrefix()
    {
        string withName = Notifier.BuildWeComPayload("号A", "标题", "正文", hasUserName: true);
        Assert.Contains("\"msgtype\":\"text\"", withName);
        Assert.Contains("[号A] 标题", withName);

        string noName = Notifier.BuildWeComPayload("eFootballBot", "标题", "正文", hasUserName: false);
        Assert.Contains("\"msgtype\":\"text\"", noName);
        Assert.DoesNotContain("[eFootballBot]", noName);
    }

    [Fact]
    public void WeComOk_ErrcodeZeroOnly()
    {
        Assert.True(Notifier.IsWeComOk("""{"errcode":0,"errmsg":"ok"}"""));
        Assert.False(Notifier.IsWeComOk("""{"errcode":93000,"errmsg":"invalid webhook"}"""));
        Assert.False(Notifier.IsWeComOk("not json"));
    }

    [Fact]
    public void KindMapping_MapsConfigFields_TestAlwaysOn()
    {
        var n = new NotifyConfig();
        Assert.True(Notifier.IsKindEnabled(n, NotifyKind.Stop));
        Assert.True(Notifier.IsKindEnabled(n, NotifyKind.Error));
        Assert.True(Notifier.IsKindEnabled(n, NotifyKind.MatchDone));
        n.OnMatchDone = false;
        n.OnMaxLevel = false;
        n.OnWarnLog = false;
        n.OnRunningSummary = false;
        Assert.False(Notifier.IsKindEnabled(n, NotifyKind.MatchDone));
        Assert.False(Notifier.IsKindEnabled(n, NotifyKind.MaxLevel));
        Assert.False(Notifier.IsKindEnabled(n, NotifyKind.Warn));
        Assert.False(Notifier.IsKindEnabled(n, NotifyKind.Summary));
        Assert.True(Notifier.IsKindEnabled(n, NotifyKind.Test));
    }

    // ---------------- WeChatFeed：合并窗口 ----------------

    [Fact]
    public void MergeWindow_SecondEventWithinWindow_WaitsThenCombines()
    {
        WeChatFeed.SessionStart(Cfg(c => c.MergeMinutes = 5), () => "摘要", null);

        WeChatFeed.MatchDone("我的联赛", false, 1, 0);
        Assert.Single(Sent);
        WeChatFeed.MatchDone("我的联赛", false, 2, 0); // 窗口内 → 待合并，不推
        Assert.Single(Sent);

        _now = _now.AddMinutes(6);
        WeChatFeed.FlushDue(Cfg(c => c.MergeMinutes = 5), null, null, _now);
        Assert.Equal(2, Sent.Count);
        Assert.Equal(NotifyKind.MatchDone, Sent[1].Kind);
        Assert.Contains("另有 1 起", Sent[1].Body);
    }

    [Fact]
    public void MergeWindow_ElapsedNextEventSendsImmediatelyWithPending()
    {
        WeChatFeed.SessionStart(Cfg(c => c.MergeMinutes = 5), () => "摘要", null);

        WeChatFeed.MatchDone("我的联赛", false, 1, 0);
        WeChatFeed.MatchDone("我的联赛", false, 2, 0); // 待合并
        Assert.Single(Sent);

        _now = _now.AddMinutes(6); // 窗口已过 → 本条立即推，带出积压
        WeChatFeed.MatchDone("我的联赛", false, 3, 0);
        Assert.Equal(2, Sent.Count);
        Assert.Contains("另有 1 起", Sent[1].Body);
    }

    [Fact]
    public void ErrorLog_SendsImmediately_EvenInsideWindow()
    {
        WeChatFeed.SessionStart(Cfg(c => c.MergeMinutes = 30), () => "摘要", null);

        WeChatFeed.LogLine("窗口内第一条警告", LogLevel.Warn);
        WeChatFeed.LogLine("第二批警告", LogLevel.Warn);
        Assert.Single(Sent); // 警告合并
        WeChatFeed.LogLine("严重错误", LogLevel.Error);
        Assert.Equal(2, Sent.Count);
        Assert.Equal(NotifyKind.Warn, Sent[1].Kind);
        Assert.Contains("严重错误", Sent[1].Body);
    }

    [Fact]
    public void KindToggles_SilenceRespectiveEvents()
    {
        WeChatFeed.SessionStart(Cfg(c =>
        {
            c.MergeMinutes = 0;
            c.OnMatchDone = false;
            c.OnMaxLevel = false;
        }), () => "摘要", null);

        WeChatFeed.MatchDone("我的联赛", false, 1, 0);
        WeChatFeed.MaxLevel("比赛中满级提示", 1);
        Assert.Empty(Sent);
    }

    // ---------------- WeChatFeed：每日配额 ----------------

    [Fact]
    public void DailyLimit_BlocksAfterCap_WarnsOnce()
    {
        WeChatFeed.SessionStart(Cfg(c =>
        {
            c.MergeMinutes = 0;
            c.DailySendLimit = 2;
        }), () => "摘要", null);

        WeChatFeed.MatchDone("我的联赛", false, 1, 0);
        WeChatFeed.MatchDone("我的联赛", false, 2, 0);
        WeChatFeed.MatchDone("我的联赛", false, 3, 0); // 超限
        WeChatFeed.MaxLevel("比赛中满级提示", 1);       // 同日仍超限
        Assert.Equal(2, Sent.Count);

        _now = _now.AddDays(1); // 次日配额滚动恢复
        WeChatFeed.MatchDone("我的联赛", false, 4, 0);
        Assert.Equal(3, Sent.Count);
    }

    // ---------------- WeChatFeed：运行摘要 ----------------

    [Fact]
    public void RunningSummary_FiresByInterval_Once()
    {
        WeChatFeed.SessionStart(Cfg(c => { c.OnRunningSummary = true; c.SummaryIntervalMinutes = 60; }),
            () => "摘要正文", null);

        _now = _now.AddMinutes(30);
        WeChatFeed.FlushDue(Cfg(c => { c.OnRunningSummary = true; c.SummaryIntervalMinutes = 60; }),
            () => "摘要正文", null, _now);
        Assert.Empty(Sent); // 未到间隔

        _now = _now.AddMinutes(31);
        WeChatFeed.FlushDue(Cfg(c => { c.OnRunningSummary = true; c.SummaryIntervalMinutes = 60; }),
            () => "摘要正文", null, _now);
        Assert.Single(Sent);
        Assert.Equal(NotifyKind.Summary, Sent[0].Kind);
        Assert.Equal("摘要正文", Sent[0].Body);

        _now = _now.AddMinutes(10);
        WeChatFeed.FlushDue(Cfg(c => { c.OnRunningSummary = true; c.SummaryIntervalMinutes = 60; }),
            () => "摘要正文", null, _now);
        Assert.Single(Sent); // 间隔内不重复
    }
}

using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;
using EfootballBot.Core.Logging;
using Xunit;

namespace EfootballBot.Tests;

// 作息调度器：活跃时段 / 每日上限 / 跨午夜窗口（纯逻辑，注入 Random 确定性验证）
public class SessionSchedulerTests
{
    private static readonly DateTime Day1 = new(2026, 10, 11, 0, 0, 0);

    private static SessionScheduler Make(ScheduleConfig cfg, Action<string, LogLevel>? onLog = null)
        => new(cfg, onLog ?? ((_, _) => { }), rnd: new Random(42));

    private static ScheduleConfig Cfg(string start = "09:00", string end = "23:00", double cap = 6, int drift = 0) =>
        new() { Enabled = true, StartTime = start, EndTime = end, DailyCapHours = cap, DriftMinutes = drift };

    [Fact]
    public void Disabled_AlwaysRuns()
    {
        var s = Make(new ScheduleConfig { Enabled = false });
        Assert.True(s.ShouldRunNow(Day1.AddHours(3)));   // 凌晨也运行
        Assert.True(s.ShouldRunNow(Day1.AddHours(15)));
    }

    [Fact]
    public void OutsideWindow_PausesAndResumes()
    {
        var s = Make(Cfg());
        Assert.False(s.ShouldRunNow(Day1.AddHours(8)));          // 08:00 暂停
        Assert.True(s.ShouldRunNow(Day1.AddHours(10)));          // 10:00 运行
        Assert.False(s.ShouldRunNow(Day1.AddHours(23.5)));       // 23:30 暂停
    }

    [Fact]
    public void OvernightWindow_ActiveAcrossMidnight()
    {
        var s = Make(Cfg(start: "22:00", end: "02:00"));
        // 逐 5 秒推进 19:00 → 次日 03:00：窗口内（22:00–02:00）应持续运行
        // （短暂离开每 30~70 分钟最多 5 分钟，不覆盖整段），窗口外暂停
        DateTime t = Day1.AddHours(19);
        bool ranLate = false, ranAfterMidnight = false;
        for (int i = 0; i < 8 * 60 * 12; i++)
        {
            t = t.AddSeconds(5);
            bool run = s.ShouldRunNow(t);
            var tod = t.TimeOfDay;
            if (run && tod >= TimeSpan.FromHours(22)) ranLate = true;
            if (run && tod < TimeSpan.FromHours(2)) ranAfterMidnight = true;
        }
        Assert.True(ranLate);
        Assert.True(ranAfterMidnight);
        Assert.False(s.ShouldRunNow(t)); // 02:00 窗口结束后暂停
    }

    [Fact]
    public void DailyCap_PausesUntilNextDay()
    {
        var s = Make(Cfg(cap: 2));
        DateTime t = Day1.AddHours(9);
        Assert.True(s.ShouldRunNow(t));
        // 模拟引擎每 5 秒轮询挂机 2 小时
        for (int i = 0; i < 720 * 12; i++) { t = t.AddSeconds(5); s.ShouldRunNow(t); }
        Assert.False(s.ShouldRunNow(t.AddSeconds(5)));           // 达上限 → 暂停
        // 次日 09:00+ 自动恢复
        Assert.True(s.ShouldRunNow(Day1.AddDays(1).AddHours(9).AddMinutes(1)));
    }

    [Fact]
    public void ZeroCap_NeverLimits()
    {
        var s = Make(Cfg(cap: 0));
        DateTime t = Day1.AddHours(9);
        for (int i = 0; i < 24 * 60 * 12; i++) { t = t.AddSeconds(5); s.ShouldRunNow(t); }
        Assert.True(s.ShouldRunNow(t)); // 已连续挂满 24h 仍运行（时段内）
    }

    [Fact]
    public void PauseJustBegan_TrueOnlyOnTransition()
    {
        var s = Make(Cfg());
        DateTime t = Day1.AddHours(10);
        Assert.True(s.ShouldRunNow(t));
        Assert.False(s.PauseJustBegan);
        t = Day1.AddHours(23.9);
        Assert.False(s.ShouldRunNow(t));
        Assert.True(s.PauseJustBegan);                            // 刚转入休息
        Assert.False(s.ShouldRunNow(t.AddSeconds(5)));
        Assert.False(s.PauseJustBegan);                           // 持续休息不再置位
    }

    [Fact]
    public void Drift_ShiftsWindowWithinBounds()
    {
        // 漂移 60 分钟：09:00–23:00 的窗口起点必落在 08:00–10:00 之间
        for (int seed = 0; seed < 20; seed++)
        {
            var logs = new List<string>();
            var s = new SessionScheduler(Cfg(drift: 60), (_, _) => { }, rnd: new Random(seed));
            // 用二分法粗略探测首个运行点：从 07:00 逐分钟推进，首个 true 即窗口起点
            DateTime t = Day1.AddHours(7);
            DateTime? firstRun = null;
            for (int m = 0; m < 240; m++)
            {
                if (s.ShouldRunNow(t)) { firstRun = t; break; }
                t = t.AddMinutes(1);
            }
            Assert.NotNull(firstRun);
            Assert.InRange(firstRun!.Value, Day1.AddHours(8), Day1.AddHours(10));
        }
    }

    [Fact]
    public void InvalidTimeConfig_FallsBackToDefaults()
    {
        var s = Make(Cfg(start: "25:99", end: "bogus"));
        Assert.False(s.ShouldRunNow(Day1.AddHours(8)));           // 默认 09:00 前
        Assert.True(s.ShouldRunNow(Day1.AddHours(10)));
    }

    [Fact]
    public void MicroBreak_TriggersWithinRollWindow()
    {
        // 进入运行态 30~70 分钟后应出现一次 2~5 分钟短暂离开（90 分钟模拟必命中）
        var s = Make(Cfg(cap: 0, drift: 0));
        DateTime t = Day1.AddHours(9);
        bool sawBreak = false;
        for (int i = 0; i < 90 * 60 * 12 && !sawBreak; i++)
        {
            t = t.AddSeconds(5);
            if (!s.ShouldRunNow(t) && t.TimeOfDay < TimeSpan.FromHours(23)) sawBreak = true;
        }
        Assert.True(sawBreak, "90 分钟内应出现至少一次模拟短暂离开");
    }
}

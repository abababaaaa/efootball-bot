using System.Threading;
namespace EfootballBot.Core.Input;

/// <summary>
/// bot 按键门闩：HidMaestroPad 每次状态提交时打点，RealPadActivityMonitor 据此忽略
/// 紧随 bot 按键之后的输入事件。背景：虚拟手柄为 Xbox 形态（XInput 槽位可见），
/// bot 自己的按键必然出现在 XInput 轮询差分里——若算作用户活动，挂机会自触发永久让步死锁。
/// 门闩窗口 600ms：bot 按键保持约 140ms + 间隔，真人不会稳定贴着 bot 的节奏按键；
/// 即便偶有真按键被吞，下一次按键仍会正常判定。
/// </summary>
internal static class BotInputGate
{
    /// <summary>门闩窗口毫秒。</summary>
    internal const int WindowMs = 600;

    private static long _lastSubmitUtcTicks;

    /// <summary>HidMaestroPad 每次状态提交时调用（可注入时间供测试）。</summary>
    internal static void Mark() => Mark(DateTime.UtcNow);

    internal static void Mark(DateTime nowUtc) => Interlocked.Exchange(ref _lastSubmitUtcTicks, nowUtc.Ticks);

    /// <summary>距最近一次 bot 提交是否仍在门闩窗口内（从无提交记录 = 不在窗口内）。</summary>
    internal static bool IsGated(DateTime nowUtc)
    {
        long t = Interlocked.Read(ref _lastSubmitUtcTicks);
        return t != 0 && nowUtc.Ticks - t < TimeSpan.FromMilliseconds(WindowMs).Ticks;
    }
}

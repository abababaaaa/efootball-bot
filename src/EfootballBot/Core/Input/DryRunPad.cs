using EfootballBot.Core.Bot;

namespace EfootballBot.Core.Input;

/// <summary>
/// 影子模式手柄装饰器（dry-run）：把真实虚拟手柄包起来，**吞掉一切输入**并只写日志。
///
/// 用途：
/// 1. 游戏更新/新界面时先跑一遍，看 Bot「本来会按什么」——零风险验证决策是否正确；
/// 2. 用户报「走到不动」时，日志里直接有按键序列，排障不用再靠猜；
/// 3. 顺手产出「画面 → 动作」样本，可作为后续回放/训练素材。
///
/// 连接/断开等生命周期操作照常透传：捕获、slot 检测、自愈逻辑保持与真实运行一致，
/// 这样影子模式看到的就是真实运行轨迹（只是最后一厘米不落地）。
/// </summary>
public sealed class DryRunPad : IVirtualPad
{
    private readonly IVirtualPad _inner;
    private readonly Action<string, LogLevel>? _log;

    /// <summary>被拦截的动作计数（结束时汇总一行，便于判断这一段在干什么）。</summary>
    public int SuppressedActions { get; private set; }

    public PadBackendKind Kind => _inner.Kind;
    public int UserIndex => _inner.UserIndex;
    public bool IsConnected => _inner.IsConnected;

    public DryRunPad(IVirtualPad inner, Action<string, LogLevel>? log = null)
    {
        _inner = inner;
        _log = log;
    }

    public void Connect() => _inner.Connect();
    public void Disconnect() => _inner.Disconnect();
    public void ResetAll() { /* 影子模式无需清键：本来就没有按下任何键 */ }

    public void SetButton(PadButton btn, bool pressed)
    {
        if (!pressed) return;
        SuppressedActions++;
        _log?.Invoke($"[影子] 按下 {Btn(btn)}（未发送到游戏）", LogLevel.Debug);
    }

    public void SetDpad(PadDir? dir)
    {
        if (dir is null) return;
        SuppressedActions++;
        _log?.Invoke($"[影子] 方向 {dir}（未发送到游戏）", LogLevel.Debug);
    }

    public void SetLeftStick(short x, short y)
    {
        if (x == 0 && y == 0) return;
        SuppressedActions++;
        _log?.Invoke($"[影子] 左摇杆 ({x},{y})（未发送到游戏）", LogLevel.Debug);
    }

    public void SetRightStick(short x, short y)
    {
        if (x == 0 && y == 0) return;
        SuppressedActions++;
        _log?.Invoke($"[影子] 右摇杆 ({x},{y})（未发送到游戏）", LogLevel.Debug);
    }

    public void SetLeftTrigger(byte value)
    {
        if (value == 0) return;
        SuppressedActions++;
        _log?.Invoke($"[影子] LT {value}（未发送到游戏）", LogLevel.Debug);
    }

    public void SetRightTrigger(byte value)
    {
        if (value == 0) return;
        SuppressedActions++;
        _log?.Invoke($"[影子] RT {value}（未发送到游戏）", LogLevel.Debug);
    }

    private static string Btn(PadButton b) => b switch
    {
        PadButton.A => "A",
        PadButton.B => "B",
        PadButton.X => "X",
        PadButton.Y => "Y",
        PadButton.Start => "Start",
        PadButton.Back => "Back",
        PadButton.LeftShoulder => "LB",
        PadButton.RightShoulder => "RB",
        PadButton.LeftThumb => "L3",
        PadButton.RightThumb => "R3",
        _ => b.ToString(),
    };

    public void Dispose()
    {
        try { _inner.Dispose(); } catch { }
    }
}

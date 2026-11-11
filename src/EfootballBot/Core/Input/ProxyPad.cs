using System.Diagnostics;

namespace EfootballBot.Core.Input;

/// <summary>
/// XInput 进程内代理后端：本类只负责把按键写进共享内存，真正的"变成 slot0"发生在
/// 目标游戏进程里（游戏目录下的 xinput1_3.dll，见 tools/xinput-proxy）。
///
/// 为什么它比 ViGEm 更符合"完全后台 + 不串前台"：
/// - 不创建任何系统设备，其他进程的 XInput 调用完全不受影响（HidHide 那条路对 XInput 不生效）；
/// - 不抢 XInput 槽位，真实手柄可以照常插着、照常给别的游戏用；
/// - 游戏失焦/最小化/停靠在虚拟显示器上照常轮询，输入持续生效；
/// - 心跳断了（Worker 崩了）代理 1 秒内退回透传，游戏立刻回到真实手柄，不会卡键。
///
/// 判活：<see cref="SharedPadWriter.ProxySeen"/>（代理回写的 proxyCalls/proxySeenMs）。
/// 代理没加载（Steam overlay 绕过、DLL 被更新覆盖）时返回 false，由引擎降级到 ViGEm。
/// </summary>
public sealed class ProxyPad : IVirtualPad
{
    /// <summary>eFootball 只认玩家 1；代理固定把它映射到 slot0。</summary>
    public const int ProxySlot = 0;

    /// <summary>等待"游戏真的读到我们的状态"的默认超时。</summary>
    public const int DefaultProbeTimeoutMs = 5000;

    private readonly SharedPadWriter _writer;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private ushort _buttons;
    private byte _leftTrigger;
    private byte _rightTrigger;
    private short _leftX, _leftY, _rightX, _rightY;

    public ProxyPad(uint targetMask = SharedPadWriter.TargetGlobal,
                    string sharedName = @"Local\EfootballBot.Pad.v1",
                    Action<string>? log = null)
    {
        _writer = new SharedPadWriter(sharedName);
        _writer.SetTargets(targetMask);
        _log = log;
    }

    public PadBackendKind Kind => PadBackendKind.XInputProxy;

    /// <summary>代理恒为玩家 1。</summary>
    public int UserIndex => ProxySlot;

    public bool IsConnected { get; private set; }

    /// <summary>接管 slot0：此后目标游戏读到的是本对象写入的状态。</summary>
    public void Connect()
    {
        lock (_gate)
        {
            _writer.ResetAll();
            _writer.SetActive(true);
            IsConnected = true;
        }
        _log?.Invoke("代理手柄已接管 XInput 玩家 1（共享内存通道，游戏进程内生效）。");
    }

    /// <summary>
    /// 等游戏真的读一次我们的状态（代理回写 proxySeenMs）。
    /// 返回 false = 代理没被加载/被绕过（调用方应降级 ViGEm）。
    /// </summary>
    public bool WaitForProxy(int timeoutMs = DefaultProbeTimeoutMs, Func<bool>? cancel = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cancel?.Invoke() == true) return false;
            if (_writer.ProxySeen(3000)) return true;
            Thread.Sleep(100);
        }
        return _writer.ProxySeen(3000);
    }

    /// <summary>交还 slot0：先 PANIC（全松手）再撤 ACTIVE，避免游戏里卡着半按下的键。</summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            if (!IsConnected) return;
            _writer.SetActive(false, panic: true);
            _writer.ResetAll();
            IsConnected = false;
        }
        _log?.Invoke("代理手柄已交还 XInput 玩家 1。");
    }

    public void SetButton(PadButton btn, bool pressed)
    {
        if (!IsConnected) return;
        lock (_gate)
        {
            var bit = (ushort)btn;
            _buttons = pressed ? (ushort)(_buttons | bit) : (ushort)(_buttons & ~bit);
            _writer.SetButton(_buttons);
        }
    }

    public void SetDpad(PadDir? dir)
    {
        if (!IsConnected) return;
        const ushort mask = (ushort)(PadButton.DpadUp | PadButton.DpadDown | PadButton.DpadLeft | PadButton.DpadRight);
        lock (_gate)
        {
            _buttons = (ushort)(_buttons & ~mask);
            _buttons |= dir switch
            {
                PadDir.Up => (ushort)PadButton.DpadUp,
                PadDir.Down => (ushort)PadButton.DpadDown,
                PadDir.Left => (ushort)PadButton.DpadLeft,
                PadDir.Right => (ushort)PadButton.DpadRight,
                _ => (ushort)0,
            };
            _writer.SetButton(_buttons);
        }
    }

    public void SetLeftStick(short x, short y)
    {
        if (!IsConnected) return;
        lock (_gate) { _leftX = x; _leftY = y; _writer.SetLeftStick(x, y); }
    }

    public void SetRightStick(short x, short y)
    {
        if (!IsConnected) return;
        lock (_gate) { _rightX = x; _rightY = y; _writer.SetRightStick(x, y); }
    }

    public void SetLeftTrigger(byte value)
    {
        if (!IsConnected) return;
        lock (_gate) { _leftTrigger = value; _writer.SetTriggers(_leftTrigger, _rightTrigger); }
    }

    public void SetRightTrigger(byte value)
    {
        if (!IsConnected) return;
        lock (_gate) { _rightTrigger = value; _writer.SetTriggers(_leftTrigger, _rightTrigger); }
    }

    public void ResetAll()
    {
        if (!IsConnected) return;
        lock (_gate)
        {
            _buttons = 0; _leftTrigger = _rightTrigger = 0;
            _leftX = _leftY = _rightX = _rightY = 0;
            _writer.ResetAll();
        }
    }

    /// <summary>供诊断面板展示的当前快照（不含共享内存副作用）。</summary>
    public string Describe()
        => $"代理后端：slot={ProxySlot} 按键=0x{_buttons:X4} LT={_leftTrigger} RT={_rightTrigger} " +
           $"LX={_leftX} LY={_leftY} RX={_rightX} RY={_rightY} 已接管={IsConnected}";

    public void Dispose()
    {
        try { Disconnect(); } catch { }
        _writer.Dispose();
    }
}

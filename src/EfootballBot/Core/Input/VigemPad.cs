using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace EfootballBot.Core.Input;

/// <summary>
/// ViGEm 虚拟 Xbox 360 手柄（唯一后端，v3.3.0 回退定案）：系统按 XInput 轮询，游戏窗口
/// 失焦/后台时输入持续生效；eFootball 对 Xbox 形态全通道消费（按键/十字键/摇杆/扳机，
/// v2.0.x 数周生产实证 + 3.2.1 探针复测）。HIDMaestro 全系档位轴通道不被游戏消费
/// （2026-10-21 探针实锤，右摇杆面板失效），故整体回退 ViGEmBus 方案。
/// 代价 = 虚拟手柄占用 XInput 玩家 1（slot0）：eFootball 只认 slot0（双控制器实测 slot1 无效），
/// 真实手柄由 <see cref="PhysicalPadControl"/> 在挂机期间设备节点级禁用让位（全权接管），
/// 停止挂机自动恢复。
/// 依赖 ViGEmBus 内核驱动（缺失时由 <see cref="EfootballBot.Core.System.DriverInstaller"/> 静默安装）。
/// </summary>
public sealed class VigemPad : IVirtualPad
{
    private readonly Action<string>? _log;
    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private volatile bool _connected;

    /// <summary>提交串行锁：Submit 与 Disconnect 互斥（3.2.0 输入加固：停止路径 Dispose
    /// 控制器与观察循环的并发提交相撞会炸出对象已释放异常）。</summary>
    private readonly object _ioLock = new();

    /// <summary>手柄在系统中的序号（0-3），-1 表示尚未取得。</summary>
    public int UserIndex { get; private set; } = -1;
    public bool IsConnected => _connected;

    public PadBackendKind Kind => PadBackendKind.Vigem;

    public VigemPad(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>ViGEmBus 是内核驱动服务，检查服务注册项即可。</summary>
    public static bool IsDriverInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\ViGEmBus");
            return key is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// eFootball 菜单只响应 XInput Player 1（slot 0）。
    /// 连接前若真实手柄已占用 slot 0，虚拟手柄必然落到 slot 1+，输入被游戏忽略。
    /// 提取为纯静态函数便于单测；返回 null 表示 slot 0 空闲。
    /// </summary>
    public static string? SlotZeroOccupiedMessage(bool slot0Connected)
        => slot0Connected
            ? "检测到 XInput 玩家 1（slot 0）仍被占用（自动禁用未生效：提权计划任务未注册或授权被拒）。请重启本程序并在授权窗口点「是」，或先拔下/关闭真实手柄，再点击开始挂机。"
            : null;

    public void Connect()
    {
        if (_connected) return;
        if (!IsDriverInstalled())
            throw new InvalidOperationException("未检测到 ViGEmBus 驱动，开始挂机会自动安装（需管理员授权，首次约 10-30 秒）。");

        // slot 0 被占用时虚拟手柄必然分到 slot 1+，游戏不响应 → 直接阻断
        if (SlotZeroOccupiedMessage(PadBackendProbe.IsSlot0Occupied()) is { } blockMsg)
            throw new InvalidOperationException(blockMsg);

        // 连接前快照已占用槽位，连接后用 XInput 轮询找到新设备，即虚拟手柄真实 slot
        var before = new HashSet<int>();
        for (int i = 0; i < 4; i++)
            if (PadBackendProbe.IsSlotOccupied(i)) before.Add(i);

        try
        {
            _client ??= new ViGEmClient();
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false; // 手动提交，保证一次状态变化一次上报
            _pad.Connect();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"连接 ViGEm 失败：{ex.Message}。请确认 ViGEmBus 驱动已正确安装并重启过电脑。", ex);
        }

        // 库本身常回报 -1，主动探测真实 slot（最多等约 2.5 秒系统注册设备）
        UserIndex = -1;
        for (int attempt = 0; attempt < 10 && UserIndex < 0; attempt++)
        {
            Thread.Sleep(250);
            for (int i = 0; i < 4; i++)
                if (!before.Contains(i) && PadBackendProbe.IsSlotOccupied(i)) { UserIndex = i; break; }
        }

        _connected = true;
        _log?.Invoke($"ViGEm 虚拟手柄已连接（XInput 玩家 {(UserIndex + 1)}）。");

        // 上电后先提交一次“全释放”状态，确保游戏识别到手柄
        SubmitReport();
    }

    // ---- 状态操作 ----

    private static Xbox360Button Map(PadButton b) => b switch
    {
        PadButton.DpadUp => Xbox360Button.Up,
        PadButton.DpadDown => Xbox360Button.Down,
        PadButton.DpadLeft => Xbox360Button.Left,
        PadButton.DpadRight => Xbox360Button.Right,
        PadButton.Start => Xbox360Button.Start,
        PadButton.Back => Xbox360Button.Back,
        PadButton.LeftThumb => Xbox360Button.LeftThumb,
        PadButton.RightThumb => Xbox360Button.RightThumb,
        PadButton.LeftShoulder => Xbox360Button.LeftShoulder,
        PadButton.RightShoulder => Xbox360Button.RightShoulder,
        PadButton.A => Xbox360Button.A,
        PadButton.B => Xbox360Button.B,
        PadButton.X => Xbox360Button.X,
        PadButton.Y => Xbox360Button.Y,
        _ => throw new ArgumentOutOfRangeException(nameof(b)),
    };

    public void SetButton(PadButton btn, bool pressed)
    {
        lock (_ioLock)
        {
            if (_pad is null) return;
            _pad.SetButtonState(Map(btn), pressed);
            SubmitReport();
        }
    }

    public void SetDpad(PadDir? dir)
    {
        lock (_ioLock)
        {
            if (_pad is null) return;
            _pad.SetButtonState(Xbox360Button.Up, dir == PadDir.Up);
            _pad.SetButtonState(Xbox360Button.Down, dir == PadDir.Down);
            _pad.SetButtonState(Xbox360Button.Left, dir == PadDir.Left);
            _pad.SetButtonState(Xbox360Button.Right, dir == PadDir.Right);
            SubmitReport();
        }
    }

    public void SetLeftStick(short x, short y)
    {
        lock (_ioLock)
        {
            if (_pad is null) return;
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, x);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, y);
            SubmitReport();
        }
    }

    public void SetRightStick(short x, short y)
    {
        lock (_ioLock)
        {
            if (_pad is null) return;
            _pad.SetAxisValue(Xbox360Axis.RightThumbX, x);
            _pad.SetAxisValue(Xbox360Axis.RightThumbY, y);
            SubmitReport();
        }
    }

    public void SetLeftTrigger(byte value)
    {
        lock (_ioLock)
        {
            if (_pad is null) return;
            _pad.SetSliderValue(Xbox360Slider.LeftTrigger, value);
            SubmitReport();
        }
    }

    public void SetRightTrigger(byte value)
    {
        lock (_ioLock)
        {
            if (_pad is null) return;
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, value);
            SubmitReport();
        }
    }

    public void ResetAll()
    {
        lock (_ioLock)
        {
            if (_pad is null) return;
            _pad.ResetReport();
            SubmitReport();
        }
    }

    /// <summary>提交状态报告。驱动/总线失效时抛 <see cref="PadSubmitException"/>
    /// 供引擎按连续失败重建控制器自愈（与 3.2.0 输入加固语义一致）。</summary>
    private void SubmitReport()
    {
        try
        {
            _pad?.SubmitReport();
        }
        catch (Exception ex)
        {
            throw new PadSubmitException($"ViGEm 状态提交失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 断开虚拟手柄但保留客户端（释放 XInput 槽位，使真实手柄或下次连接能拿回 Player 1）。
    /// 断开后可再次调用 Connect 重连。
    /// </summary>
    public void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        lock (_ioLock)
        {
            try { _pad?.ResetReport(); _pad?.SubmitReport(); } catch { }
            try { _pad?.Disconnect(); } catch { }
            _pad = null;
        }
        UserIndex = -1;
    }

    public void Dispose()
    {
        Disconnect();
        try { _client?.Dispose(); } catch { }
        _client = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>虚拟手柄状态提交失败（ViGEm 客户端与总线驱动通信失效/设备掉线等）。专用类型供引擎识别，
/// 触发手柄级重建自愈（ReconnectPad）而非按普通瞬时异常无限退避。</summary>
public sealed class PadSubmitException : InvalidOperationException
{
    public PadSubmitException(string message, Exception innerException) : base(message, innerException) { }
}

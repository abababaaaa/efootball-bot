using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

[Flags]
public enum PadButton : ushort
{
    DpadUp = 0x0001,
    DpadDown = 0x0002,
    DpadLeft = 0x0004,
    DpadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,   // L3
    RightThumb = 0x0080,  // R3
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

public enum PadDir { Up, Down, Left, Right }

/// <summary>
/// ViGEm 虚拟 Xbox 360 手柄（托管封装）。系统按 XInput 轮询，
/// 游戏窗口失去焦点 / 位于后台时输入依然持续生效。
/// 依赖 ViGEmBus 驱动（内核服务）。
/// </summary>
public sealed class VirtualGamepad360 : IDisposable
{
    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private volatile bool _connected;

    /// <summary>手柄在系统中的序号（0-3），-1 表示尚未取得。</summary>
    public int UserIndex { get; private set; } = -1;
    public bool IsConnected => _connected;

    public static bool IsDriverInstalled()
    {
        // ViGEmBus 是内核驱动服务，检查服务注册项即可。
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\ViGEmBus");
            return key is not null;
        }
        catch { return false; }
    }

    public void Connect()
    {
        if (_connected) return;
        if (!IsDriverInstalled())
            throw new InvalidOperationException("未检测到 ViGEmBus 驱动，请先安装 ViGEmBus Setup。");

        // 连接前快照已占用槽位，连接后用 XInput 轮询找到新设备，即虚拟手柄真实 slot
        var before = new HashSet<int>();
        for (int i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == 0) before.Add(i);

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
                if (!before.Contains(i) && XInputGetState(i, out _) == 0) { UserIndex = i; break; }
        }

        _connected = true;

        // 上电后先提交一次“全释放”状态，确保游戏识别到手柄
        _pad.ResetReport();
        _pad.SubmitReport();
    }

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
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
        if (_pad is null) return;
        _pad.SetButtonState(Map(btn), pressed);
        _pad.SubmitReport();
    }

    public void SetDpad(PadDir? dir)
    {
        if (_pad is null) return;
        _pad.SetButtonState(Xbox360Button.Up, dir == PadDir.Up);
        _pad.SetButtonState(Xbox360Button.Down, dir == PadDir.Down);
        _pad.SetButtonState(Xbox360Button.Left, dir == PadDir.Left);
        _pad.SetButtonState(Xbox360Button.Right, dir == PadDir.Right);
        _pad.SubmitReport();
    }

    public void SetLeftStick(short x, short y)
    {
        if (_pad is null) return;
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, x);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY, y);
        _pad.SubmitReport();
    }

    public void SetRightStick(short x, short y)
    {
        if (_pad is null) return;
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, x);
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, y);
        _pad.SubmitReport();
    }

    public void SetLeftTrigger(byte value)
    {
        if (_pad is null) return;
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, value);
        _pad.SubmitReport();
    }

    public void SetRightTrigger(byte value)
    {
        if (_pad is null) return;
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, value);
        _pad.SubmitReport();
    }

    public void ResetAll()
    {
        if (_pad is null) return;
        _pad.ResetReport();
        _pad.SubmitReport();
    }

    // ---- 高级按压（异步，供状态机 await） ----

    public async Task TapAsync(PadButton button, int holdMs = 120, int postDelayMs = 180)
    {
        SetButton(button, true);
        await Task.Delay(holdMs);
        SetButton(button, false);
        await Task.Delay(postDelayMs);
    }

    public async Task TapDpadAsync(PadDir dir, int holdMs = 120, int postDelayMs = 200)
    {
        SetDpad(dir);
        await Task.Delay(holdMs);
        SetDpad(null);
        await Task.Delay(postDelayMs);
    }

    /// <summary>左摇杆轻推一个方向（菜单里摇杆滑动）。</summary>
    public async Task FlickLeftStickAsync(PadDir dir, int holdMs = 160, int postDelayMs = 200)
    {
        const short v = 26000;
        (short x, short y) = dir switch
        {
            PadDir.Up => ((short)0, v),
            PadDir.Down => ((short)0, (short)-v),
            PadDir.Left => ((short)-v, (short)0),
            PadDir.Right => (v, (short)0),
            _ => ((short)0, (short)0),
        };
        SetLeftStick(x, y);
        await Task.Delay(holdMs);
        SetLeftStick(0, 0);
        await Task.Delay(postDelayMs);
    }

    /// <summary>右摇杆轻推一个方向（联赛主页右侧物品栏翻页等）。</summary>
    public async Task FlickRightStickAsync(PadDir dir, int holdMs = 160, int postDelayMs = 200)
    {
        const short v = 26000;
        (short x, short y) = dir switch
        {
            PadDir.Up => ((short)0, v),
            PadDir.Down => ((short)0, (short)-v),
            PadDir.Left => ((short)-v, (short)0),
            PadDir.Right => (v, (short)0),
            _ => ((short)0, (short)0),
        };
        SetRightStick(x, y);
        await Task.Delay(holdMs);
        SetRightStick(0, 0);
        await Task.Delay(postDelayMs);
    }

    /// <summary>
    /// 断开虚拟手柄但保留客户端（释放 XInput 槽位，使真实手柄或下次连接能拿回 Player 1）。
    /// 断开后可再次调用 Connect 重连。
    /// </summary>
    public void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        try { _pad?.ResetReport(); _pad?.SubmitReport(); } catch { }
        try { _pad?.Disconnect(); } catch { }
        _pad = null;
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

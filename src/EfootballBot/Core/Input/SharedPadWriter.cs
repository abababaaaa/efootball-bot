// SharedPadWriter.cs —— Worker 侧共享内存写入器（与 tools/xinput-proxy/xinput1_3.c 的 ABI 严格对应）
// 集成点：new ProxyPad(cfg) : IVirtualPad，每个 SetButton/SetStick 调用都走这里。
// 关键：用 seqlock 保证代理读到的 16 字节状态永远是一次完整快照（不会读到半帧摇杆）。
using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace EfootballBot.Core.Input;

public sealed unsafe class SharedPadWriter : IDisposable
{
    public const int StructSize = 104;          // sizeof(PadSharedV1)
    public const uint Magic = 0x44415045;       // 'EPAD'
    public const uint Version = 1;
    public const uint FlagActive = 0x0001;
    public const uint FlagReady = 0x0002;
    public const uint FlagPanic = 0x0004;
    public const uint TargetGlobal = 0x0001;    // 国际服 eFootball.exe
    public const uint TargetCn = 0x0002;        // 国服 eFootballOnline.exe

    private const int OffMagic = 0, OffVersion = 4, OffSize = 8, OffSeq = 12,
                      OffHeartbeat = 20, OffWriterPid = 28, OffFlags = 32, OffTargetMask = 36,
                      OffButtons = 40, OffLt = 42, OffRt = 43, OffLx = 44, OffLy = 46,
                      OffRx = 48, OffRy = 50, OffProxySeen = 52, OffProxyPid = 60,
                      OffProxyCalls = 64, OffProxyFlags = 68;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _p;
    private readonly Timer _heartbeat;
    private readonly object _gate = new();
    private long _seq;
    private volatile bool _disposed;
    private int _stopping;
    private ushort _buttons;
    private byte _lt, _rt;
    private short _lx, _ly, _rx, _ry;

    public SharedPadWriter(string name = @"Local\EfootballBot.Pad.v1")
    {
        _mmf = MemoryMappedFile.CreateOrOpen(name, StructSize);
        _view = _mmf.CreateViewAccessor(0, StructSize);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _p = p;

        *(uint*)(_p + OffMagic) = Magic;
        *(uint*)(_p + OffVersion) = Version;
        *(uint*)(_p + OffSize) = StructSize;
        *(int*)(_p + OffWriterPid) = Environment.ProcessId;
        *(uint*)(_p + OffFlags) = 0;
        *(uint*)(_p + OffTargetMask) = 0;
        var existingSeq = Volatile.Read(ref *(long*)(_p + OffSeq));
        _seq = (existingSeq & 1) == 0 ? existingSeq : existingSeq + 1;
        if ((existingSeq & 1) != 0)
            Volatile.Write(ref *(long*)(_p + OffSeq), _seq);

        _heartbeat = new Timer(_ =>
        {
            lock (_gate)
            {
                if (!_disposed && _stopping == 0)
                    Volatile.Write(ref *(long*)(_p + OffHeartbeat), (long)Environment.TickCount64);
            }
        }, null, 0, 200);
    }

    /// <summary>接管开关：ACTIVE 时目标游戏看到的是本程序的手柄；否则代理完全透传。</summary>
    public void SetActive(bool active, bool panic = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            SetActiveCore(active, panic);
        }
    }

    private void SetActiveCore(bool active, bool panic)
    {
        uint f = *(uint*)(_p + OffFlags);
        f = active ? f | FlagActive : f & ~FlagActive;
        f = panic ? f | FlagPanic : f & ~FlagPanic;
        Volatile.Write(ref *(uint*)(_p + OffFlags), f);
    }

    public void SetTargets(uint mask)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Volatile.Write(ref *(uint*)(_p + OffTargetMask), mask);
        }
    }

    public void SetButton(ushort mask) { lock (_gate) { _buttons = mask; Publish(); } }
    public void SetTriggers(byte left, byte right) { lock (_gate) { _lt = left; _rt = right; Publish(); } }
    public void SetLeftStick(short x, short y) { lock (_gate) { _lx = x; _ly = y; Publish(); } }
    public void SetRightStick(short x, short y) { lock (_gate) { _rx = x; _ry = y; Publish(); } }

    public void ResetAll() { lock (_gate) { _buttons = 0; _lt = _rt = 0; _lx = _ly = _rx = _ry = 0; Publish(); } }

    /// <summary>一次完整状态发布：seq 奇数 → 写 16 字节 → seq 偶数（代理读侧据此判断撕裂）。</summary>
    private void Publish()
    {
        if (_disposed) return;
        Volatile.Write(ref *(long*)(_p + OffSeq), ++_seq);         // odd: writing
        Thread.MemoryBarrier();
        *(ushort*)(_p + OffButtons) = _buttons;
        *(_p + OffLt) = _lt;
        *(_p + OffRt) = _rt;
        *(short*)(_p + OffLx) = _lx;
        *(short*)(_p + OffLy) = _ly;
        *(short*)(_p + OffRx) = _rx;
        *(short*)(_p + OffRy) = _ry;
        Thread.MemoryBarrier();
        Volatile.Write(ref *(long*)(_p + OffSeq), ++_seq);         // even: stable
    }

    /// <summary>游戏进程是否真的在读我们的状态（>0 且 proxySeenMs 新鲜）。</summary>
    public bool ProxySeen(int withinMs = 3000)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            long seen = Volatile.Read(ref *(long*)(_p + OffProxySeen));
            return seen > 0 && Environment.TickCount64 - seen <= withinMs;
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _stopping, 1, 0) == 0)
        {
            using var done = new ManualResetEvent(false);
            if (_heartbeat.Dispose(done))
                done.WaitOne(1000);
        }
        lock (_gate)
        {
            if (_disposed) return;
            SetActiveCore(false, panic: true);
            _disposed = true;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mmf.Dispose();
        }
    }
}
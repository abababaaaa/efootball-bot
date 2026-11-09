using System.IO;
using System.IO.MemoryMappedFiles;

namespace EfootballBot.Core.Vision.Sidecar;

/// <summary>
/// Rust vision-svc 发布的帧环读者（命名共享内存，双缓冲无锁）。
/// 布局见 <c>rust/vision-svc/src/shm.rs</c>：64 字节头 + N 个等长槽位。
/// 读侧规则：只读偶数 seq；按 <c>(seq/2) % slots</c> 定位槽位；读完复核 seq 未变，否则重读。
/// </summary>
public sealed class SharedFrameRing : IDisposable
{
    public const int HeaderBytes = 64;
    private const int OffWidth = 8, OffHeight = 12, OffStride = 16;
    private const int OffSlots = 20, OffSlotBytes = 24, OffSeq = 32, OffTs = 40;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private byte[] _buf = Array.Empty<byte>();
    private ulong _lastSeq;

    public string Name { get; }
    public int SlotCount { get; }
    public int SlotBytes { get; }
    public long Reads { get; private set; }
    public long Misses { get; private set; }

    private SharedFrameRing(string name, MemoryMappedFile mmf, MemoryMappedViewAccessor view)
    {
        Name = name;
        _mmf = mmf;
        _view = view;
        SlotCount = (int)view.ReadUInt32(OffSlots);
        SlotBytes = (int)view.ReadUInt32(OffSlotBytes);
    }

    /// <summary>打开 sidecar 已创建的帧环；等待最多 <paramref name="timeoutMs"/>（CreateFileMapping 与 init ack 之间存在竞态）。</summary>
    public static SharedFrameRing Open(string name, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        Exception? last = null;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                var ring = new SharedFrameRing(name, mmf, view);
                if (ring.SlotCount < 1 || ring.SlotBytes < 16)
                {
                    ring.Dispose();
                    throw new InvalidDataException($"帧环头部非法：slots={ring.SlotCount} slotBytes={ring.SlotBytes}");
                }
                return ring;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(20);
            }
        }
        throw new InvalidOperationException($"打开帧环 {name} 超时", last);
    }

    /// <summary>取最新一帧；无新帧返回 false（调用方应继续用上一帧）。</summary>
    public bool TryReadLatest(out Capture.FrameData frame)
    {
        frame = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var seq = _view.ReadUInt64(OffSeq);
            if (seq == 0 || (seq & 1) != 0) { Misses++; return false; }
            if (seq == _lastSeq) return false;

            var w = (int)_view.ReadUInt32(OffWidth);
            var h = (int)_view.ReadUInt32(OffHeight);
            var stride = (int)_view.ReadUInt32(OffStride);
            if (w <= 0 || h <= 0 || stride < w * 4) { Misses++; return false; }

            var need = stride * h;
            if (need > SlotBytes) { Misses++; return false; }
            if (_buf.Length != need) _buf = new byte[need];

            var slot = (int)((seq / 2) % (ulong)SlotCount);
            _view.ReadArray(HeaderBytes + slot * SlotBytes, _buf, 0, need);

            if (_view.ReadUInt64(OffSeq) != seq) continue; // 写者翻页了，重读

            _lastSeq = seq;
            var ts = (long)_view.ReadUInt64(OffTs);
            // 拷贝一份交给下游：FrameData 会被 OCR/预览并发持有，不能复用 _buf
            var copy = new byte[need];
            Buffer.BlockCopy(_buf, 0, copy, 0, need);
            frame = new Capture.FrameData(copy, w, h, ts);
            Reads++;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}

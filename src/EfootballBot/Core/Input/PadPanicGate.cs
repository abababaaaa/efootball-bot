// PadPanicGate.cs —— 停止挂机时的「手柄总闸」（GUI 侧，不依赖 Worker）
//
// 为什么需要它：
//   「停止」这条链路很长：GUI 按钮 → 管道 stop 命令 → Worker 读循环 → engine.Stop()
//   → 取消令牌（PAD 层下一拍才看到）→ Pad360.ResetAll/Disconnect。
//   中间任何一环被拖住（WGC 捕获会话回收、会话摘要推送、Worker 刚好被杀的竞态、读循环排队），
//   游戏侧就还能再吃到几拍按键 —— 用户看到的就是"点了停止还在被操作"。
//
// 这里的做法：GUI 直接往 xinput 代理的共享内存写「撤 ACTIVE + PANIC + 全零」，
//   游戏进程内的代理在下一帧（≤16ms）读到空状态并透传，之后 Worker 再怎么写都进不了游戏
//   （ACTIVE 已撤，代理不再合成状态）。一次内存写把松手从"秒级"降到"帧级"。
//
// 与 SharedPadWriter 的 ABI 严格一致（见 tools/xinput-proxy/pad_shared.h）：
//   magic 0 / version 4 / size 8 / seq 12 / heartbeat 20 / writerPid 28 / flags 32 /
//   targetMask 36 / buttons 40 / lt 42 / rt 43 / lx 44 / ly 46 / rx 48 / ry 50 /
//   proxySeen 52 / proxyPid 60 / proxyCalls 64 / proxyFlags 68 / 结构 104 字节。
using System;
using System.IO.MemoryMappedFiles;

namespace EfootballBot.Core.Input;

/// <summary>
/// 停止挂机用的手柄总闸：不经过 Worker，直接把共享内存里的合成状态清零并撤掉 ACTIVE。
/// 幂等、无异常外溢（共享内存还没建立时返回 false，不影响停止流程）。
/// </summary>
public static class PadPanicGate
{
    /// <summary>与 <see cref="SharedPadWriter"/> / tools/xinput-proxy 一致的共享内存名。</summary>
    public const string DefaultName = @"Local\EfootballBot.Pad.v1";

    /// <summary>结构体大小（PadSharedV1）。</summary>
    public const int StructSize = 104;

    private const int OffSeq = 12;
    private const int OffFlags = 32;
    private const int OffButtons = 40;
    private const int OffLt = 42;
    private const int OffRt = 43;
    private const int OffLx = 44;
    private const int OffLy = 46;
    private const int OffRx = 48;
    private const int OffRy = 50;

    /// <summary>PANIC：代理读到本帧时只回空状态（全松手），不再喂按键。</summary>
    public const uint FlagPanic = 0x0004;

    /// <summary>上一次拉闸是否生效（供日志/诊断用）。</summary>
    public static bool LastPanicApplied { get; private set; }

    /// <summary>
    /// 拉闸：清零按键/摇杆/扳机，撤掉 ACTIVE 并置 PANIC。
    /// 返回 true = 共享内存存在且已写入（说明本机走的是进程内代理后端）。
    /// </summary>
    public static bool Panic(string name = DefaultName)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, StructSize);

            // seqlock：写状态前把 seq 变奇数（代理侧看到奇数就跳过本帧）
            long seq = view.ReadInt64(OffSeq);
            long writing = (seq & 1) == 1 ? seq : seq + 1;
            view.Write(OffSeq, writing);

            view.Write(OffButtons, (ushort)0);
            view.Write(OffLt, (byte)0);
            view.Write(OffRt, (byte)0);
            view.Write(OffLx, (short)0);
            view.Write(OffLy, (short)0);
            view.Write(OffRx, (short)0);
            view.Write(OffRy, (short)0);
            // 撤 ACTIVE + 置 PANIC：此后 Worker 即使还活着继续写按键，代理也不会合成给游戏
            view.Write(OffFlags, FlagPanic);

            view.Write(OffSeq, writing + 1); // 偶数 = 稳定，代理可读
            LastPanicApplied = true;
            return true;
        }
        catch
        {
            LastPanicApplied = false;
            return false;
        }
    }

    /// <summary>撤销拉闸（下次「开始挂机」不需要它：引擎 Connect 会重设 ACTIVE 并清 PANIC，
    /// 这里只供诊断/单测显式恢复用）。</summary>
    public static bool Release(string name = DefaultName)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, StructSize);
            view.Write(OffFlags, 0u);
            return true;
        }
        catch { return false; }
    }
}

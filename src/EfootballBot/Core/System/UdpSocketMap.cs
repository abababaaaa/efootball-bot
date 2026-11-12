using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace EfootballBot.Core.System;

/// <summary>
/// UDP 端口 → 拥有者进程 的映射（iphlpapi GetExtendedUdpTable，非管理员可用）。
///
/// 为什么需要它：Npcap 抓包拿到的是「网卡上的所有 UDP 流量」，和进程没有绑定关系。
/// v7.0.1 的 InGameConnectionMonitor 只按「哪个流包最多」猜主连接——机器上只要开着
/// 加速器 / 下载器 / 语音软件，猜中的就可能是别人的流（实测：113.47.5.84:18183 其实是
/// accservice 的加速通道，不是对局对手），于是远端地址、区域、延迟全错。
/// 用这张表把抓到的流按「本地端口是否属于 eFootball.exe」过一次，剩下来的才是游戏的连接。
/// </summary>
public static class UdpSocketMap
{
    private enum UdpTableClass { Basic = 0, OwnerPid = 1, OwnerModule = 2 }

    private const int AF_INET = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr;   // 网络字节序
        public uint LocalPort;   // 高 16 位 = 端口（网络字节序）
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder,
                                                  int ulAf, UdpTableClass tableClass, uint reserved);

    /// <summary>本地 UDP 端口 → PID（IPv4）。失败返回空表（调用方按「未绑定进程」降级处理）。</summary>
    public static Dictionary<ushort, int> PortToPid()
    {
        var map = new Dictionary<ushort, int>();
        int size = 0;
        uint rc = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UdpTableClass.OwnerPid, 0);
        const uint ERROR_INSUFFICIENT_BUFFER = 122;
        if (rc != ERROR_INSUFFICIENT_BUFFER && rc != 0) return map;
        if (size <= 4) return map;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedUdpTable(buf, ref size, false, AF_INET, UdpTableClass.OwnerPid, 0);
            if (rc != 0) return map;
            int count = Marshal.ReadInt32(buf);
            IntPtr row = IntPtr.Add(buf, 4);
            int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
            for (int i = 0; i < count; i++, row = IntPtr.Add(row, rowSize))
            {
                var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(row);
                // dwLocalPort：低 16 位是网络字节序的端口号
                ushort port = (ushort)(((r.LocalPort & 0xFF) << 8) | ((r.LocalPort >> 8) & 0xFF));
                if (port == 0) continue;
                map[port] = unchecked((int)r.OwningPid);
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
        return map;
    }

    /// <summary>游戏进程名（国际服主程序 eFootball.exe；其余为各渠道/旧版命名）。</summary>
    public static readonly string[] GameProcessNames =
        { "eFootball", "eFootballSteam", "eFootball2024", "eFootballOnline" };

    /// <summary>加速器 / 代理类进程：它们自己和加速节点之间的 UDP 流不是对局流量。</summary>
    public static readonly string[] AcceleratorProcessNames =
    {
        "uu", "accservice", "uu_launcher", "uu_ball", "uu_cloudsyn", "uu_repair",
        "xlacc", "xunyou", "xyclient", "xyplatform", "xydaemon",
        "netch", "clash", "Clash for Windows", "v2ray", "v2rayN", "sing-box", "wireguard",
        "SSTap", "Netch", "Speedify", "ExitLag", "wtfast", "booster",
    };

    /// <summary>当前在跑的游戏 PID 列表（0 个 = 游戏没开）。</summary>
    public static List<int> GamePids()
    {
        var list = new List<int>();
        foreach (var n in GameProcessNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { list.Add(p.Id); } catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }
        return list;
    }

    /// <summary>游戏进程当前打开的本地 UDP 端口集合（抓包过滤用）。</summary>
    public static HashSet<ushort> GamePorts(out List<int> pids)
    {
        pids = GamePids();
        return PortsForPids(pids);
    }

    /// <summary>给定一组 PID，返回它们打开的本地 UDP 端口集合。</summary>
    public static HashSet<ushort> PortsForPids(IEnumerable<int> pidList)
    {
        var set = new HashSet<ushort>();
        var want = new HashSet<int>(pidList);
        if (want.Count == 0) return set;
        foreach (var kv in PortToPid())
            if (want.Contains(kv.Value)) set.Add(kv.Key);
        return set;
    }

    /// <summary>
    /// 加速器「隧道」端口：UU/迅游这类工具不是在游戏进程里转发，而是由自己的服务进程
    /// （accservice / uu / xlacc）开 UDP 端口，把游戏流量打包送到加速节点。
    /// 实测（2026-11-12 本机）：对局中 113.47.5.84:18183 的上下行各 ~150 pps 全挂在
    /// accservice 的 58300 上，而 eFootball.exe 自己的端口一包都没有。
    /// 所以「只认游戏 PID 的端口」会把对局流量整条过滤掉——必须把这层隧道也认成对局流量。
    /// </summary>
    public static HashSet<ushort> AcceleratorPorts(out List<(int Pid, string Name)> owners)
    {
        owners = new List<(int, string)>();
        var pids = new List<int>();
        foreach (var name in AcceleratorProcessNames)
        {
            try
            {
                foreach (var pr in Process.GetProcessesByName(name))
                {
                    try { pids.Add(pr.Id); owners.Add((pr.Id, pr.ProcessName)); }
                    catch { }
                    finally { pr.Dispose(); }
                }
            }
            catch { }
        }
        return PortsForPids(pids);
    }

    private static List<string>? _accelCache;
    private static long _accelAt;

    /// <summary>正在运行的加速器/代理进程名（空 = 没有）。命中 5 秒缓存（监控每秒都要问一次）。</summary>
    public static List<string> RunningAccelerators()
    {
        long now = Environment.TickCount64;
        if (_accelCache is not null && now - _accelAt < 5000) return _accelCache;
        _accelAt = now;
        var hit = new List<string>();
        foreach (var n in AcceleratorProcessNames)
        {
            try
            {
                var ps = Process.GetProcessesByName(n);
                bool any = false;
                foreach (var p in ps)
                {
                    any = true;
                    p.Dispose();
                }
                if (any) hit.Add(n);
            }
            catch { }
        }
        _accelCache = hit;
        return hit;
    }

    /// <summary>端口 → 进程名（找不到返回 null）。用于把「远端」旁注成「(accservice)」这种归属提示。</summary>
    public static string? ProcessNameOfPid(int pid)
    {
        if (pid <= 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    /// <summary>本机 IPv4 地址集合（判断抓到的包是发出去还是收进来的）。</summary>
    public static HashSet<string> LocalIPv4()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" };
        try
        {
            foreach (var ni in global::System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != global::System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily == global::System.Net.Sockets.AddressFamily.InterNetwork)
                        set.Add(a.ToString());
                }
            }
        }
        catch { }
        try
        {
            using var s = new global::System.Net.Sockets.Socket(global::System.Net.Sockets.AddressFamily.InterNetwork,
                global::System.Net.Sockets.SocketType.Dgram, global::System.Net.Sockets.ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            if (s.LocalEndPoint is IPEndPoint ep) set.Add(ep.Address.ToString());
        }
        catch { }
        return set;
    }
}

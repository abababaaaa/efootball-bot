using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// 定位 eFootball 客户端内存里的 NetDriver ping 值（只读）。用法见 README。
// 输出：exe 旁 data\network\pingfind.log（也可以直接用参数指定进程名）。
internal static class Program
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

    [DllImport("kernel32.dll")]
    private static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION info, int len);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private static readonly string[] NameTargets =
        { "PlayerState", "NetConnection", "AvgLag", "GameNetDriver", "NetDriver", "ExactPing" };

    private static StreamWriter _log = StreamWriter.Null;

    private static void Main(string[] args)
    {
        string procName = args.Length > 0 ? args[0] : "eFootball";
        string outDir = Path.Combine(AppContext.BaseDirectory, "data", "network");
        Directory.CreateDirectory(outDir);
        _log = new StreamWriter(Path.Combine(outDir, "pingfind.log"), false, new UTF8Encoding(true)) { AutoFlush = true };
        Log($"== pingfind {DateTime.Now:yyyy-MM-dd HH:mm:ss} target={procName}");

        try { Run(procName); }
        catch (Exception ex) { Log("异常：" + ex); }
        finally { _log.Dispose(); }
    }

    private static void Run(string procName)
    {
        var procs = Process.GetProcessesByName(procName);
        if (procs.Length == 0) { Log("游戏没在运行"); return; }
        var proc = procs[0];
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, proc.Id);
        Log($"OpenProcess(read) = 0x{h:X} err={Marshal.GetLastWin32Error()}");
        if (h == IntPtr.Zero)
        {
            // 退一步：只要 LIMITED_INFORMATION 看能不能至少读到模块（游戏可能带保护）
            IntPtr h2 = OpenProcess(0x1000, false, proc.Id);   // PROCESS_QUERY_LIMITED_INFORMATION
            Log($"OpenProcess(limited) = 0x{h2:X} err={Marshal.GetLastWin32Error()}");
            if (h2 != IntPtr.Zero) CloseHandle(h2);
            return;
        }
        try
        {
            // 游戏带保护：Process.MainModule 会 ACCESS_DENIED，用 psapi 走我们自己的句柄拿基址
            long baseAddr = ModuleBase(h);
            if (baseAddr == 0) { Log("拿不到模块基址（EnumProcessModulesEx 失败）"); return; }
            Log($"pid={proc.Id} base=0x{baseAddr:X}");

            var regions = Regions(h, out long totalBytes);
            Log($"可读区域 {regions.Count} 段，共 {totalBytes / 1024 / 1024} MB");

            // ① FName 名称表命中（确认我们看得懂 UE 的内部结构）
            foreach (var name in NameTargets)
            {
                var hits = FindNameEntries(h, regions, name);
                Log($"FName '{name}': {hits.Count} 处");
                foreach (var a in hits.Take(4)) Log($"   0x{a:X}");
            }

            // ② 值猎取：3 轮采样，保留在 1..500 之间、有变化、变化幅度 ≤60 的候选
            var t0 = SampleValues(h, regions);
            Log($"候选(第1轮) {t0.Count}");
            Thread.Sleep(1200);
            var t1 = SampleValues(h, regions);
            Thread.Sleep(1200);
            var t2 = SampleValues(h, regions);
            Log($"候选(第2轮) {t1.Count} / 第3轮 {t2.Count}");

            var keep = new List<(long Addr, string Kind, double V0, double V1, double V2)>();
            foreach (var kv in t0)
            {
                if (!t1.TryGetValue(kv.Key, out var v1) || !t2.TryGetValue(kv.Key, out var v2)) continue;
                double v0 = kv.Value;
                bool changed = Math.Abs(v0 - v1) > 0.001 || Math.Abs(v1 - v2) > 0.001;
                if (!changed) continue;
                if (Math.Abs(v0 - v1) > 60 || Math.Abs(v1 - v2) > 60) continue;
                keep.Add((kv.Key.Addr, kv.Key.Kind, v0, v1, v2));
            }
            Log($"在变化的候选 {keep.Count}");
            foreach (var c in keep.OrderBy(c => Math.Abs(c.V1 - 45)).Take(120))
            {
                string rel = c.Addr >= baseAddr && c.Addr < baseAddr + 0x20000000 ? $"+0x{c.Addr - baseAddr:X}" : "heap";
                Log($"  {c.Kind,-5} 0x{c.Addr:X} ({rel})  {c.V0:0.##} -> {c.V1:0.##} -> {c.V2:0.##}");
            }
            Log("== 结束：把上面「在变化的候选」里数值量级像 ping（30~80 之间跳）的地址发回，我写进 gameping.json");
        }
        finally { CloseHandle(h); }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr h, IntPtr[] modules, int cb, out int needed, uint filter);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetModuleInformation(IntPtr h, IntPtr module, out MODULEINFO info, int cb);

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO { public IntPtr lpBaseOfDll; public uint SizeOfImage; public IntPtr EntryPoint; }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int cls, byte[] info, int len, out int ret);

    /// <summary>不走 psapi：读 PEB → PEB_LDR_DATA → 第一个模块（游戏对模块枚举做了保护）。</summary>
    private static long ModuleBaseFromPeb(IntPtr h)
    {
        var buf = new byte[48];
        int ret;
        if (NtQueryInformationProcess(h, 0, buf, buf.Length, out ret) < 0) return 0;
        long peb = BitConverter.ToInt64(buf, 8);           // PROCESS_BASIC_INFORMATION.PebBaseAddress
        if (peb == 0) return 0;
        Log($"PEB=0x{peb:X}");
        // x64: PEB+0x18 = Ldr；PEB_LDR_DATA+0x20 = InMemoryOrderModuleList（LIST_ENTRY）
        var probe = new byte[16];
        bool pebRead = ReadProcessMemory(h, new IntPtr(peb), probe, probe.Length, out var got);
        Log($"读 PEB 自身：{pebRead} got={got} err={Marshal.GetLastWin32Error()}");
        if (!pebRead) return 0;
        if (!ReadProcessMemory(h, new IntPtr(peb + 0x18), buf, 8, out _)) { Log("读 PEB.Ldr 失败"); return 0; }
        long ldr = BitConverter.ToInt64(buf, 0);
        Log($"Ldr=0x{ldr:X}");
        if (ldr == 0) return 0;
        if (!ReadProcessMemory(h, new IntPtr(ldr + 0x20), buf, 8, out _)) return 0;
        long head = BitConverter.ToInt64(buf, 0);          // 链表头（第一个条目的 Flink）
        if (head == 0) return 0;
        // 第一个条目的 DllBase 在 LDR_DATA_TABLE_ENTRY 的 +0x30（相对 InMemoryOrderLinks）
        if (!ReadProcessMemory(h, new IntPtr(head + 0x30), buf, 8, out _)) return 0;
        long dllBase = BitConverter.ToInt64(buf, 0);
        Log($"main module base=0x{dllBase:X}");
        return dllBase;
    }

    private static long ModuleBase(IntPtr h)
    {
        long fromPeb = ModuleBaseFromPeb(h);
        if (fromPeb != 0) return fromPeb;
        var mods = new IntPtr[1024];
        if (!EnumProcessModulesEx(h, mods, mods.Length * IntPtr.Size, out int needed, 0x03))   // LIST_MODULES_ALL
        {
            Log("EnumProcessModulesEx 失败：" + Marshal.GetLastWin32Error());
            return 0;
        }
        int count = needed / IntPtr.Size;
        Log("模块数=" + count);
        if (count == 0) return 0;
        if (!GetModuleInformation(h, mods[0], out var mi, Marshal.SizeOf<MODULEINFO>())) return 0;
        return (long)mi.lpBaseOfDll;
    }

    private sealed class Region { public long Base; public long Size; }

    private static List<Region> Regions(IntPtr h, out long total)
    {
        var list = new List<Region>();
        total = 0;
        long addr = 0x10000;
        int size = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (true)
        {
            if (VirtualQueryEx(h, new IntPtr(addr), out var mbi, size) == 0) break;
            long regionBase = (long)mbi.BaseAddress;
            long regionSize = (long)mbi.RegionSize;
            if (mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_NOACCESS) == 0 && (mbi.Protect & PAGE_GUARD) == 0
                && (mbi.Protect & 0x02) == 0 /* 只读页也跳过：写过的值才可能在可写页 */)
            {
                list.Add(new Region { Base = regionBase, Size = regionSize });
                total += regionSize;
            }
            long next = regionBase + regionSize;
            if (next <= addr) break;
            addr = next;
            if (addr > 0x7FFFFFFF0000) break;
        }
        return list;
    }

    private static List<long> FindNameEntries(IntPtr h, List<Region> regions, string name)
    {
        var hits = new List<long>();
        byte[] needle = Encoding.ASCII.GetBytes(name);
        var buf = new byte[4 * 1024 * 1024];
        foreach (var r in regions)
        {
            for (long off = 0; off < r.Size; off += buf.Length)
            {
                int want = (int)Math.Min(buf.Length, r.Size - off);
                if (!ReadProcessMemory(h, new IntPtr(r.Base + off), buf, want, out var read)) continue;
                int n = (int)read.ToInt64();
                int idx = IndexOf(buf, needle, n);
                while (idx >= 0)
                {
                    // UE4 FNameEntry: 2 字节头（低位=长度，bit15=是否宽字符），随后是字符
                    if (idx >= 2)
                    {
                        int header = buf[idx - 2] | (buf[idx - 1] << 8);
                        if ((header & 0x7FFF) == name.Length && (header & 0x8000) == 0)
                            hits.Add(r.Base + off + idx - 2);
                    }
                    idx = IndexOf(buf, needle, n, idx + 1);
                }
            }
        }
        return hits;
    }

    private static int IndexOf(byte[] hay, byte[] needle, int len, int start = 0)
    {
        for (int i = start; i <= len - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private sealed record Key(long Addr, string Kind);

    private static Dictionary<Key, double> SampleValues(IntPtr h, List<Region> regions)
    {
        var map = new Dictionary<Key, double>();
        var buf = new byte[2 * 1024 * 1024];
        foreach (var r in regions)
        {
            for (long off = 0; off < r.Size; off += buf.Length)
            {
                int want = (int)Math.Min(buf.Length, r.Size - off);
                if (!ReadProcessMemory(h, new IntPtr(r.Base + off), buf, want, out var read)) continue;
                int n = (int)read.ToInt64();
                for (int i = 0; i + 4 <= n; i += 4)
                {
                    int iv = BitConverter.ToInt32(buf, i);
                    if (iv >= 5 && iv <= 500)
                        map[new Key(r.Base + off + i, "int")] = iv;
                    float fv = BitConverter.ToSingle(buf, i);
                    if (fv >= 0.005f && fv <= 0.5f)          // UE4 AvgLag 是秒
                        map[new Key(r.Base + off + i, "fsec")] = Math.Round(fv * 1000.0, 2);
                    else if (fv >= 5f && fv <= 500f)
                        map[new Key(r.Base + off + i, "float")] = Math.Round(fv, 2);
                }
            }
        }
        return map;
    }

    private static void Log(string s) { Console.WriteLine(s); _log.WriteLine(s); }
}

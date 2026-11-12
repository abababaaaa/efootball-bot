using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EfootballBot.Core.System;

/// <summary>
/// 读游戏客户端自己算出来的 ping（UE4 NetDriver/PlayerState 里的那个值）。
///
/// 为什么必须这么干：「你↔加速节点」的 8~9ms 与游戏内天线格（走加速后的整体路径）不是一个口径，
/// 面板上那个数只会误导（2026-11-12 用户明确要求：假延迟不要显示，只显示游戏内的）。真正的游戏内
/// 延迟只有客户端自己知道；能拿到它的唯一非侵入办法就是读它的内存（只读、短命句柄、不写不注入）。
///
/// 设计成「配置驱动」：偏移在 data\network\gameping.json 里，定位一次就能沿用，
/// 游戏版本更新导致偏移变化时改配置即可，不用重新编译。找不到/读失败一律返回 -1，界面显示「—」。
/// </summary>
public sealed class GamePingReader
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddress, byte[] buffer,
        int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>gameping.json 的结构。</summary>
    public sealed class Config
    {
        /// <summary>true = 启用内存读取（默认关闭，避免误读崩进程）。</summary>
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        /// <summary>进程名（国际服 eFootball / 国服 eFootballOnline）。</summary>
        [JsonPropertyName("process")] public string Process { get; set; } = "eFootball";
        /// <summary>值的类型：int32 / float / uint8。</summary>
        [JsonPropertyName("type")] public string Type { get; set; } = "float";
        /// <summary>模块内偏移（相对 exe 映射基址）；0 表示用绝对地址 AbsoluteAddress。</summary>
        [JsonPropertyName("module_offset")] public long ModuleOffset { get; set; }
        /// <summary>绝对地址（调试用；重启游戏就失效）。</summary>
        [JsonPropertyName("absolute_address")] public long AbsoluteAddress { get; set; }
        /// <summary>指针链：从 module_offset 处读出指针，再逐级加 offset（UE4 对象一般都是多级指针）。</summary>
        [JsonPropertyName("pointer_chain")] public long[] PointerChain { get; set; } = Array.Empty<long>();
        /// <summary>读到的原始值 → 毫秒 的换算（UE4 常见 AvgLag 是秒，需 ×1000）。</summary>
        [JsonPropertyName("scale_ms")] public double ScaleMs { get; set; } = 1000.0;
        /// <summary>合理性区间：超出就当读取失败（防止偏移错位后把垃圾值当延迟）。</summary>
        [JsonPropertyName("min_ms")] public int MinMs { get; set; } = 1;
        [JsonPropertyName("max_ms")] public int MaxMs { get; set; } = 900;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string ConfigPath => Path.Combine(AppPaths.DataDir, "network", "gameping.json");

    private Config? _cfg;
    private DateTime _cfgReadAt = DateTime.MinValue;
    private DateTime _lastErrorAt = DateTime.MinValue;
    private string _lastError = "";

    /// <summary>最近一次读到的值（毫秒）；-1 = 拿不到。</summary>
    public int LastMs { get; private set; } = -1;
    public string Status { get; private set; } = "未启用";

    public Config? Load()
    {
        if (_cfg is not null && (DateTime.Now - _cfgReadAt).TotalSeconds < 10) return _cfg;
        _cfgReadAt = DateTime.Now;
        try
        {
            if (!File.Exists(ConfigPath)) { _cfg = new Config { Enabled = false }; return _cfg; }
            _cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOpts) ?? new Config();
            return _cfg;
        }
        catch (Exception ex)
        {
            _cfg = new Config { Enabled = false };
            _lastError = "配置解析失败：" + ex.Message;
            return _cfg;
        }
    }

    /// <summary>
    /// 读一次。返回毫秒，失败返回 -1（附带 Status 说明原因）。
    /// 每次调用都 开句柄 → 读 → 立刻关，不常驻句柄（见 2026-11-12 的封号风险评估）。
    /// </summary>
    public int Sample()
    {
        var cfg = Load();
        if (cfg is null || !cfg.Enabled) { LastMs = -1; Status = "未启用（gameping.json enabled=false）"; return -1; }
        if (cfg.ModuleOffset == 0 && cfg.AbsoluteAddress == 0) { LastMs = -1; Status = "未定位偏移"; return -1; }

        Process? proc = null;
        IntPtr h = IntPtr.Zero;
        try
        {
            var list = Process.GetProcessesByName(cfg.Process);
            if (list.Length == 0) { LastMs = -1; Status = $"未找到进程 {cfg.Process}"; return -1; }
            proc = list[0];
            h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, proc.Id);
            if (h == IntPtr.Zero)
            {
                LastMs = -1; Status = "打不开游戏进程（需要用管理员运行本程序）"; return -1;
            }

            long addr = cfg.AbsoluteAddress != 0 ? cfg.AbsoluteAddress : (long)proc.MainModule!.BaseAddress + cfg.ModuleOffset;
            foreach (var off in cfg.PointerChain)
            {
                if (!TryRead(h, addr, 8, out var ptrBytes)) { LastMs = -1; Status = "指针链读取失败"; return -1; }
                long ptr = BitConverter.ToInt64(ptrBytes, 0);
                if (ptr == 0) { LastMs = -1; Status = "指针链为空"; return -1; }
                addr = ptr + off;
            }

            int size = cfg.Type switch { "float" => 4, "uint8" => 1, _ => 4 };
            if (!TryRead(h, addr, size, out var raw)) { LastMs = -1; Status = "读值失败"; return -1; }
            double value = cfg.Type switch
            {
                "float" => BitConverter.ToSingle(raw, 0),
                "uint8" => raw[0],
                _ => BitConverter.ToInt32(raw, 0),
            };
            int ms = (int)Math.Round(value * cfg.ScaleMs);
            if (ms < cfg.MinMs || ms > cfg.MaxMs)
            {
                LastMs = -1;
                Status = $"值不合理（{value:0.###} → {ms}ms），偏移可能已失效";
                return -1;
            }
            LastMs = ms;
            Status = $"游戏内 {ms}ms";
            return ms;
        }
        catch (Exception ex)
        {
            LastMs = -1;
            if ((DateTime.Now - _lastErrorAt).TotalSeconds > 30) { _lastError = ex.Message; _lastErrorAt = DateTime.Now; }
            Status = "读取异常：" + _lastError;
            return -1;
        }
        finally
        {
            if (h != IntPtr.Zero) CloseHandle(h);
            proc?.Dispose();
        }
    }

    private static bool TryRead(IntPtr h, long address, int size, out byte[] buffer)
    {
        buffer = new byte[size];
        if (address <= 0) return false;
        return ReadProcessMemory(h, new IntPtr(address), buffer, size, out var read) && read.ToInt64() == size;
    }
}

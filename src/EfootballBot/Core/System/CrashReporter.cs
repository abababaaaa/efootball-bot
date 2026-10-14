using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace EfootballBot.Core.System;

/// <summary>
/// 崩溃自动抓取 minidump（GUI / Worker 双进程共用，反馈包自动附带）。
/// 覆盖两层：
///  ① 原生硬崩溃（显卡驱动 / OpenCV 等原生库把线程打死）——SetUnhandledExceptionFilter
///     顶层过滤器里同步写 dump，写完立即终止（不交 WER 弹窗）；
///  ② 托管未处理异常——AppDomain.UnhandledException 补写一份（拿不到原生异常上下文，
///     dump 只有线程栈；文本日志另有 gui/worker-error.log 承接）。
/// dump 落 data\crash\crash_{源}_v{版本}_{时间}.dmp，只保留最近 3 份防占盘。
/// 约束：硬崩溃回调里只有极小的安全栈可用（栈溢出场景可能写失败），全程 try/catch 吞错；
/// 同一次崩溃托管处理器和原生过滤器会先后触发，用原子标记保证只写一份。
/// </summary>
public static class CrashReporter
{
    private const uint DumpType =
        0x00000000   // MiniDumpNormal：线程栈 + 模块信息
        | 0x00000004 // WithHandleData：句柄表（排查文件/管道占用）
        | 0x00000020 // WithUnloadedModules
        | 0x00000040 // WithIndirectlyReferencedMemory：栈上引用的数据
        | 0x00000100; // WithProcessThreadData

    private const int EXCEPTION_EXECUTE_HANDLER = 1;

    private static int _dumpWritten; // 0=未写 1=已写（同一次崩溃只落一份）
    private static ExceptionFilter? _filterRoot; // 持有委托引用，防 GC 回收后过滤器成野指针

    private delegate int ExceptionFilter(nint exceptionPointers);

    /// <summary>进程入口安装（GUI 与 Worker 各调一次，source 用于文件名区分来源进程）。</summary>
    public static void Install(string source)
    {
        try
        {
            _filterRoot = ptr =>
            {
                TryWriteDump(source, ptr);
                return EXCEPTION_EXECUTE_HANDLER;
            };
            SetUnhandledExceptionFilter(_filterRoot);
            AppDomain.CurrentDomain.UnhandledException += (_, _) => TryWriteDump(source, nint.Zero);
        }
        catch { /* 安装失败不影响主功能 */ }
    }

    /// <summary>启动时检查：within 时间内有新 dump 返回提示文案（引导用户生成反馈包），否则 null。</summary>
    public static string? RecentDumpNotice(TimeSpan within)
    {
        try
        {
            var dir = Path.Combine(AppPaths.DataDir, "crash");
            if (!Directory.Exists(dir)) return null;
            var latest = Directory.GetFiles(dir, "*.dmp")
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime >= DateTime.Now - within)
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
            if (latest.Count == 0) return null;
            var t = latest[0].LastWriteTime.ToString("MM-dd HH:mm");
            return latest.Count == 1
                ? $"检测到 {t} 的一次崩溃记录（{latest[0].Name}）。请到 设置→关于 生成反馈包发到群里，便于定位问题。"
                : $"检测到 {latest.Count} 份崩溃记录（最近 {t}）。请到 设置→关于 生成反馈包发到群里，便于定位问题。";
        }
        catch { return null; }
    }

    private static void TryWriteDump(string source, nint exceptionPointers)
    {
        if (Interlocked.Exchange(ref _dumpWritten, 1) == 1) return;
        WriteDumpCore(source, exceptionPointers, out _);
    }

    /// <summary>写 dump 并带出失败原因（诊断/测试用；崩溃路径上错误只吞不抛）。</summary>
    public static bool WriteDumpCore(string source, nint exceptionPointers, out string error)
    {
        error = "";
        try
        {
            string dir = Path.Combine(AppPaths.DataDir, "crash");
            Directory.CreateDirectory(dir);
            PruneOldDumps(dir, keep: 2); // 加上本次共 3 份
            string ver = Updater.FormatVersion(typeof(CrashReporter).Assembly.GetName().Version);
            string file = Path.Combine(dir,
                $"crash_{source}_v{ver}_{DateTime.Now:yyyyMMdd_HHmmss}.dmp");

            var hFile = CreateFileW(file, GENERIC_WRITE, FILE_SHARE_READ, nint.Zero,
                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nint.Zero);
            if (hFile == INVALID_HANDLE_VALUE)
            {
                error = $"CreateFileW failed win32={Marshal.GetLastWin32Error()}";
                return false;
            }
            try
            {
                nint exp = nint.Zero;
                if (exceptionPointers != nint.Zero)
                {
                    var ei = new MinidumpExceptionInformation
                    {
                        ThreadId = GetCurrentThreadId(),
                        ExceptionPointers = exceptionPointers,
                        ClientPointers = 0,
                    };
                    exp = Marshal.AllocHGlobal(Marshal.SizeOf<MinidumpExceptionInformation>());
                    Marshal.StructureToPtr(ei, exp, false);
                }
                try
                {
                    if (!MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), hFile,
                            DumpType, exp, nint.Zero, nint.Zero))
                    {
                        error = $"MiniDumpWriteDump failed win32={Marshal.GetLastWin32Error()}";
                        return false;
                    }
                }
                finally { if (exp != nint.Zero) Marshal.FreeHGlobal(exp); }
            }
            finally { CloseHandle(hFile); }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void PruneOldDumps(string dir, int keep)
    {
        try
        {
            var old = Directory.GetFiles(dir, "*.dmp")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(keep);
            foreach (var f in old) { try { f.Delete(); } catch { } }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinidumpExceptionInformation
    {
        public uint ThreadId;
        public nint ExceptionPointers;
        public int ClientPointers;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint SetUnhandledExceptionFilter(ExceptionFilter filter);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(nint hProcess, uint processId, nint hFile,
        uint dumpType, nint exceptionParam, nint userStreamParam, nint callbackParam);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileW(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint CREATE_ALWAYS = 2;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private static readonly nint INVALID_HANDLE_VALUE = new(-1);
}

using System.IO;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace EfootballBot.Core.System;

/// <summary>
/// minidump 归因：把 data\crash\*.dmp 解析成一段人可读的结论
/// （CLR 版本 / 线程数 / 未处理异常 / 出错线程的托管栈前 12 帧 / 已加载模块数）。
///
/// 只在「生成反馈包」时调用，不在崩溃路径上调用——崩溃回调里只有极小的安全栈，
/// 任何托管代码都可能二次崩溃。解析失败一律返回原因字符串，绝不抛。
/// </summary>
public static class DumpAnalyzer
{
    /// <summary>解析一份 dump，返回可直接塞进反馈包的报告文本。</summary>
    /// <param name="dumpPath">.dmp 路径</param>
    /// <param name="maxFrames">每个线程最多输出多少帧托管栈</param>
    public static string Analyze(string dumpPath, int maxFrames = 12)
    {
        var sb = new StringBuilder();
        try
        {
            var fi = new FileInfo(dumpPath);
            sb.AppendLine($"# dump: {fi.Name}");
            sb.AppendLine($"# 大小: {fi.Length / 1024 / 1024} MB    时间: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");

            using var target = DataTarget.LoadDump(dumpPath);

            if (target.ClrVersions.Length == 0)
            {
                sb.AppendLine("未发现 CLR：原生崩溃或非托管进程。此 dump 只能看原生调用栈（用 WinDbg/cdb 打开）。");
                return sb.ToString();
            }

            var clr = target.ClrVersions[0];
            sb.AppendLine($"CLR: {clr.Version}    Flavor: {clr.Flavor}");

            using var runtime = clr.CreateRuntime();

            int threads = 0, exceptions = 0;
            try { threads = runtime.Threads.Length; } catch { }
            try { exceptions = runtime.Threads.Count(t => t.CurrentException is not null); } catch { }

            sb.AppendLine($"托管线程: {threads}    其中带未处理异常的: {exceptions}");

            try
            {
                var modules = runtime.EnumerateModules().Count();
                sb.AppendLine($"已加载模块: {modules}");
            }
            catch { }

            sb.AppendLine();
            sb.AppendLine("== 未处理异常 ==");
            var any = false;
            try
            {
                foreach (var t in runtime.Threads)
                {
                    var ex = t.CurrentException;
                    if (ex is null) continue;
                    any = true;
                    sb.AppendLine($"线程 {t.ManagedThreadId}（OS {t.OSThreadId}）: {ex.Type?.Name ?? "<unknown>"}");
                    if (!string.IsNullOrWhiteSpace(ex.Message)) sb.AppendLine($"  消息: {ex.Message}");
                    WriteStack(sb, t, maxFrames);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(枚举异常失败: {ex.Message})");
            }
            if (!any) sb.AppendLine("(没有捕获到未处理异常——多为原生崩溃，或异常已被 catch)");

            // 没有异常时至少给出主线程/线程池里最有信息量的几条栈
            if (!any)
            {
                sb.AppendLine();
                sb.AppendLine("== 线程栈（前 6 个线程）==");
                try
                {
                    foreach (var t in runtime.Threads.Take(6))
                    {
                        sb.AppendLine($"线程 {t.ManagedThreadId}（OS {t.OSThreadId}，{(t.IsAlive ? "alive" : "dead")}）");
                        WriteStack(sb, t, maxFrames);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"(枚举线程栈失败: {ex.Message})");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(dump 解析失败: {ex.GetType().Name}: {ex.Message})");
            sb.AppendLine("提示：ClrMD 需要与 dump 同代的运行时；游戏/驱动导致的原生崩溃通常只能交 WinDbg 分析。");
            return sb.ToString();
        }
    }

    private static void WriteStack(StringBuilder sb, ClrThread thread, int maxFrames)
    {
        try
        {
            var n = 0;
            foreach (var frame in thread.EnumerateStackTrace())
            {
                if (n++ >= maxFrames) { sb.AppendLine("  …（截断）"); break; }
                var name = frame.Method is { } m ? $"{m.Type?.Name}.{m.Name}" : "(native / 无托管方法)";
                sb.AppendLine($"  #{n - 1} {name}");
            }
            if (n == 0) sb.AppendLine("  (无托管栈)");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (栈读取失败: {ex.Message})");
        }
    }
}

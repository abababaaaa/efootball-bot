using System.IO;
using System.Diagnostics;

// ===========================================================================
// 架构锁 · 工具层 Core/Tools（v7.2）
// 本层职责：本地工具（赛前设置 / 打开路径 / 窗口防卡死 …），与挂机模块完全解耦。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 不许引用挂机层（EfootballBot.Core.Bot）—— 共享件放 Core/**，不要借挂机的件
//   · 工具 id 字面量只许在 ToolIds.cs，具体工具只许在 ToolCatalog 里实例化
//   · 界面层（MainWindow*.cs）不许直接 Process.Start：起进程/打开路径一律走 ToolCatalog
// 新增工具：ToolIds 加常量 + ToolCatalog 加一条 + 本目录一个 *Tool.cs 实现。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Tools;

/// <summary>
/// 本地 Shell 动作（工具模块，与分区无关）：打开文件夹 / 打开 URL / 在资源管理器中定位文件。
/// 以前这三件事分散在 MainWindow 的六个按钮处理里各写一段 try/catch + Process.Start，
/// 界面层直接起进程；现在界面只给路径，起进程的动作在这里唯一实现
/// （构建期 ARCH-UI-NO-PROCESS 会挡住界面层再写 Process.Start）。
/// </summary>
public sealed class ShellTool : ITool
{
    public string Id => ToolIds.Shell;
    public string DisplayName => "打开路径";
    public string Summary => "在资源管理器中打开文件夹 / 定位文件 / 用默认程序打开 URL";
    public bool ServerScoped => false;

    public IReadOnlyList<ToolAction> Actions { get; } = new[]
    {
        ToolAction.OpenPath, ToolAction.OpenUrl, ToolAction.RevealPath,
    };

    public Task<ToolResult> ExecuteAsync(ToolAction action, ToolContext ctx, CancellationToken ct = default)
        => Task.FromResult(action switch
        {
            ToolAction.OpenPath => OpenPath(ctx, reveal: false),
            ToolAction.RevealPath => OpenPath(ctx, reveal: true),
            ToolAction.OpenUrl => OpenUrl(ctx),
            _ => ToolResult.Fail($"打开路径工具不支持动作 {action}"),
        });

    private static ToolResult OpenPath(ToolContext ctx, bool reveal)
    {
        string? path = ctx.TargetPath;
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("没有指定要打开的路径");
        try
        {
            if (reveal && File.Exists(path))
            {
                using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return ToolResult.Success(path, path);
            }
            // 目录不存在时先建出来（日志/存帧/诊断目录首次打开的场景）
            if (!File.Exists(path)) Directory.CreateDirectory(path);
            using var __ = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return ToolResult.Success(path, path);
        }
        catch (Exception ex)
        {
            ctx.Log($"打开路径失败（{path}）：{ex.Message}", LogLevel.Warn);
            return ToolResult.Fail($"打开失败：{ex.Message}", path);
        }
    }

    private static ToolResult OpenUrl(ToolContext ctx)
    {
        string? url = ctx.TargetPath;
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Fail("没有指定要打开的地址");
        try
        {
            using var _ = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return ToolResult.Success(url, url);
        }
        catch (Exception ex)
        {
            ctx.Log($"打开地址失败（{url}）：{ex.Message}", LogLevel.Warn);
            return ToolResult.Fail($"打开失败：{ex.Message}", url);
        }
    }
}


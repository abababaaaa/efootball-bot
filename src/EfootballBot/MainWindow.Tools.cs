using System.Threading.Tasks;
using System.Windows.Controls;
using EfootballBot.Core;
using EfootballBot.Core.Tools;

namespace EfootballBot;

/// <summary>
/// 界面 ↔ 工具层的唯一接线：按钮只给（工具 id + 动作 + 分区 + 状态行），
/// 实现全在 Core/Tools/*（构建期 ARCH-UI-NO-PROCESS 禁止界面层直接起进程）。
/// 加一个工具按钮 = 本文件一行 await RunToolAsync(...)，不动任何逻辑。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 跑一个工具动作：结果贴到状态行；失败只记 Warn（工具内部已把细节写进日志），
    /// 绝不在这里抛异常炸界面。status 为 null 表示不需要状态行（只记日志）。
    /// </summary>
    private async Task<ToolResult> RunToolAsync(string toolId, ToolAction action, ServerRegion server,
        TextBlock? status = null, string? path = null, CancellationToken ct = default)
    {
        var ctx = new ToolContext(server, _cfg, AppendLog, path);
        ToolResult result;
        try
        {
            result = await ToolCatalog.ExecuteAsync(toolId, action, ctx, ct);
        }
        catch (Exception ex)
        {
            AppendLog($"[工具] {toolId}/{action} 执行异常：{ex.Message}", LogLevel.Error);
            result = ToolResult.Fail($"执行失败：{ex.Message}");
        }
        if (status is not null) status.Text = result.Message;
        return result;
    }

    /// <summary>打开路径/URL/定位文件（对分区无意义，统一走国际服上下文）。</summary>
    private Task<ToolResult> OpenPathAsync(string path, bool reveal = false)
        => RunToolAsync(ToolIds.Shell, reveal ? ToolAction.RevealPath : ToolAction.OpenPath,
            ServerRegion.Intl, null, path);

    /// <summary>用默认浏览器/程序打开 URL。</summary>
    private Task<ToolResult> OpenUrlAsync(string url)
        => RunToolAsync(ToolIds.Shell, ToolAction.OpenUrl, ServerRegion.Intl, null, url);
}

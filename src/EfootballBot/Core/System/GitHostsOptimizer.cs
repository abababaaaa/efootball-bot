using System.Security.Principal;

namespace EfootballBot.Core.System;

/// <summary>
/// GitHub 直连优化的历史入口（6.4 起整体交给 <see cref="FastGithubKernel"/>）：
/// 以前是自己维护一张静态 IP 表、逐域名 TCP 测速后写 hosts（"# ==== GitHub optimize" 段）。
/// 现在统一走 FastGithub 内核（21 个域名、TCP 初筛 + HTTPS 复验、镜像下载加速），
/// 只保留 <see cref="Optimize"/> 这个签名给 <c>--optimize-github</c> 离线提权入口用。
/// 旧版写下的静态段会在内核 apply 前被自动清掉，不会出现两套方案同时改 github.com。
/// </summary>
public static class GitHostsOptimizer
{
    /// <summary>当前进程是否管理员（写 hosts / 防火墙规则的前提）。</summary>
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>执行 GitHub 直连优化并返回多行结果文本（同步包装：CLI 离线入口用）。</summary>
    public static string Optimize()
    {
        var sb = new global::System.Text.StringBuilder();
        sb.AppendLine("GitHub 直连优化（FastGithub 内核）");
        if (!IsAdministrator()) sb.AppendLine("警告：当前不是管理员，hosts 写入会失败。");
        if (!FastGithubKernel.IsInstalled)
        {
            sb.AppendLine("FastGithub 内核未下载：请先在「工具 → GitHub → 访问加速」点「下载内核」。");
            return sb.ToString();
        }

        try
        {
            var result = FastGithubKernel.ApplyAsync().GetAwaiter().GetResult();
            sb.AppendLine(result.Message);
            foreach (var node in result.Nodes)
                sb.AppendLine($"  {node.Host,-40} {node.Ip,-16} {node.Ms,6}ms  {node.State,-5} code={node.Code}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("优化失败：" + ex.Message);
        }
        return sb.ToString();
    }
}

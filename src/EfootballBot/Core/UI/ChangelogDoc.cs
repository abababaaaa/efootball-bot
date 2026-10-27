// ★本命名空间位于 EfootballBot.Core 之下，而仓库里有 EfootballBot.Core.System，
// 会遮蔽全局 System（裸写 using System.IO 会解析到 Core.System.IO 而报 CS0246）——统一走 global::。
using global::System.IO;
using global::System.Text;
using EfootballBot.Core.System;

namespace EfootballBot.Core.UI;

/// <summary>更新日志里的一个版本段落。<see cref="Version"/> 为 "5.1.0" 形态（不含 v 前缀）。</summary>
public sealed record ChangelogEntry(string Version, string Body)
{
    /// <summary>版本比较用的解析结果；解析不出时为 null（仍可展示，排到最后）。
    /// ★必须写 global::——本仓库有 EfootballBot.Core.System 命名空间，裸 System.Version 会被解析到它。</summary>
    public global::System.Version? Parsed =>
        global::System.Version.TryParse(Version, out var v) ? v : null;
}

/// <summary>
/// 内置更新日志：读取随 exe 内嵌的仓库根 CHANGELOG.md（EmbeddedResource，LogicalName=CHANGELOG.md）。
/// 更新页的「更新日志」模块据此列出全部历史版本——离线可用，不依赖联网（GitHub 在国内常被墙）。
/// 文件格式约定见 CHANGELOG.md 头部：`## vX.Y.Z` 段落，段内 `-` 列点；段前的说明文字忽略。
/// </summary>
public static class ChangelogDoc
{
    private const string ResourceName = "CHANGELOG.md";

    private static IReadOnlyList<ChangelogEntry>? _cache;

    /// <summary>全部版本段落，按版本号从新到旧（解析不出的版本排在末尾，保持文件原顺序）。</summary>
    public static IReadOnlyList<ChangelogEntry> Entries => _cache ??= Load();

    /// <summary>
    /// 找到与当前程序集版本对应的段落，用于「更新日志」模块默认选中当前版本。
    /// ★版本写法在 CHANGELOG 里是三段（`## v5.1`，与 tag 一致），而程序集是 4 段（5.1.0.0，
    /// FormatVersion 渲染成 "5.1.0"）——所以按「完整 → 去末尾 .0 → 主.次」三级候选依次匹配，
    /// 既兼容 `## v5.0.8.1`（4 段测试版）也兼容 `## v5.1`（3 段正式版）。
    /// </summary>
    public static ChangelogEntry? FindCurrent()
    {
        string current = Updater.FormatVersion(Updater.CurrentVersion);
        var candidates = new List<string> { current };
        if (current.EndsWith(".0", StringComparison.Ordinal))
            candidates.Add(current[..^2]);
        var parts = current.Split('.');
        if (parts.Length >= 2) candidates.Add($"{parts[0]}.{parts[1]}");

        foreach (var c in candidates)
        {
            var hit = Entries.FirstOrDefault(e => string.Equals(e.Version, c, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    private static IReadOnlyList<ChangelogEntry> Load()
    {
        var list = new List<ChangelogEntry>();
        string text = ReadResource();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var body = new StringBuilder();
        string? version = null;

        void Flush()
        {
            if (version is null) return;
            string content = body.ToString().Trim();
            list.Add(new ChangelogEntry(version, content));
        }

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                body.Clear();
                // 只认 `## v<数字版本>`；其他二级标题（若将来出现）视为正文分隔，不建条目
                string head = line[3..].Trim();
                version = head.StartsWith("v", StringComparison.OrdinalIgnoreCase) &&
                          global::System.Version.TryParse(head[1..], out _)
                    ? head[1..]
                    : null;
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal)) continue; // 文件标题，跳过
            if (version is not null) body.AppendLine(line);
        }
        Flush();

        // 稳定排序：能解析的按版本新→旧，其余保持原顺序垫底
        return list
            .OrderByDescending(e => e.Parsed ?? new global::System.Version(0, 0))
            .ToList();
    }

    private static string ReadResource()
    {
        try
        {
            var asm = typeof(ChangelogDoc).Assembly;
            using var stream = asm.GetManifestResourceStream(ResourceName);
            if (stream is null) return "";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return ""; // 资源缺失（老构建/裁剪）：模块显示占位文案，不影响其他功能
        }
    }
}

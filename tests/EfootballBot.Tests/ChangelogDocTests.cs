using EfootballBot.Core.System;
using EfootballBot.Core.UI;
using Xunit;

namespace EfootballBot.Tests;

// 内置更新日志：仓库根 CHANGELOG.md 以 EmbeddedResource 嵌进程序集，更新页「更新日志」
// 模块据此列出全部历史版本（离线可查）。这里锁解析规则与"当前版本能被认出来"。
public class ChangelogDocTests
{
    [Fact]
    public void Entries_AreParsedFromEmbeddedResource()
    {
        var entries = ChangelogDoc.Entries;
        Assert.NotEmpty(entries);

        // 每一条都必须有版本号、正文非空（空正文说明段落切分把内容吃掉了）
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Version)));
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Body), $"v{e.Version} 正文为空"));
    }

    [Fact]
    public void Entries_AreSortedNewestFirst()
    {
        var parsed = ChangelogDoc.Entries
            .Select(e => e.Parsed)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();
        Assert.NotEmpty(parsed);
        for (int i = 1; i < parsed.Count; i++)
            Assert.True(parsed[i - 1] >= parsed[i], $"版本降序被破坏：{parsed[i - 1]} < {parsed[i]}");
    }

    [Fact]
    public void Entries_DoNotContainHeaderBoilerplate()
    {
        // 文件头部的说明文字（格式约定/CI 说明）必须被丢掉，不能出现在任何版本正文里
        Assert.DoesNotContain(ChangelogDoc.Entries, e => e.Body.Contains("CI 发 Release 时自动把对应版本段落"));
        // 段落里也不能残留下一个版本的 ## 标题
        Assert.DoesNotContain(ChangelogDoc.Entries, e => e.Body.Contains("## v"));
    }

    [Fact]
    public void FindCurrent_MatchesThisBuild()
    {
        // 发版流程要求先写 CHANGELOG 段再打 tag，所以当前构建的版本必须有对应段落——
        // 缺了就是忘了写 CHANGELOG（更新日志模块会默认落到最新一条，用户看不到"当前版本"标记）
        var hit = ChangelogDoc.FindCurrent();
        Assert.NotNull(hit);
        Assert.Equal(Updater.FormatVersion(Updater.CurrentVersion).Split('.')[0], hit!.Version.Split('.')[0]);
    }
}

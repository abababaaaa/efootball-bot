using EfootballBot.Core.Vision;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 按钮词匹配回归（真机 2026-10-06）：「Opoku」含子串 ok 被旧 FindFirst 误当按钮，
/// 比赛内盲按 A。修复后纯拉丁按钮词要求整词相等，中文词保持子串包含。
/// </summary>
public sealed class FindButtonWordTests
{
    private static OcrResult Result(params OcrWord[] words) => new(words, 1000, 1000);

    [Fact]
    public void PlayerNameContainingOk_NotTreatedAsButton()
    {
        var ocr = Result(new OcrWord("Opoku", 0.30, 0.40, 0.08, 0.04));

        Assert.Null(ocr.FindButton("ok"));
        Assert.Empty(ocr.FindButtonWords("ok"));
    }

    [Fact]
    public void ExactOkWord_Matched()
    {
        var ocr = Result(new OcrWord("OK", 0.50, 0.90, 0.05, 0.04));

        var w = ocr.FindButton("ok");
        Assert.NotNull(w);
        Assert.Equal("OK", w!.Text);
    }

    [Fact]
    public void ChineseKeyword_StillSubstringMatched()
    {
        // OCR 把图标误识字拼到词尾（真机「跳过參」），中文关键词必须仍按子串命中
        var ocr = Result(new OcrWord("跳过參", 0.80, 0.90, 0.08, 0.04));

        Assert.NotNull(ocr.FindButton("跳过"));
    }

    [Fact]
    public void NextKeyword_DoesNotMatchNGoloKante()
    {
        var ocr = Result(new OcrWord("N'Golo Kante", 0.30, 0.40, 0.14, 0.04));

        Assert.Null(ocr.FindButton("next"));
    }
}

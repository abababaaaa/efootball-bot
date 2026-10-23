using EfootballBot.Core.Vision;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

/// <summary>
/// Unlimited-OCR 输出解析（纯函数，不需要外部服务）：模型输出是
/// <c>&lt;|ref|&gt;文本&lt;|/ref|&gt;&lt;|det|&gt;[[x1,y1,x2,y2]]&lt;|/det|&gt;</c>，
/// 坐标 0~999；解析后必须落回与 PP-OCR 相同的归一化 OcrWord 契约，页面探针才能原样复用。
/// </summary>
public class UnlimitedOcrMarkupTests
{
    private readonly ITestOutputHelper _out;
    public UnlimitedOcrMarkupTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Parses_ref_det_pairs_into_normalized_words()
    {
        const string raw =
            "<|ref|>关键比赛<|/ref|><|det|>[[100, 200, 300, 240]]<|/det|>\n" +
            "<|ref|>下一步<|/ref|><|det|>[[800, 900, 950, 960]]<|/det|>";

        var words = UnlimitedOcrMarkup.Parse(raw);

        Assert.Equal(2, words.Count);
        var key = words[0];
        Assert.Equal("关键比赛", key.Text);
        Assert.Equal(100 / 999.0, key.X, 3);
        Assert.Equal(200 / 999.0, key.Y, 3);
        Assert.InRange(key.W, 0.19, 0.21);
        Assert.InRange(key.H, 0.03, 0.05);

        Assert.Equal("下一步", words[1].Text);
        Assert.True(words[1].X > 0.79);
        Assert.True(words[1].Y > 0.89);
    }

    [Fact]
    public void Multiple_boxes_share_one_text_block()
    {
        const string raw = "<|ref|>我的联赛<|/ref|><|det|>[[10, 10, 100, 50], [200, 10, 300, 50]]<|/det|>";

        var words = UnlimitedOcrMarkup.Parse(raw);

        Assert.Equal(2, words.Count);
        Assert.All(words, w => Assert.Equal("我的联赛", w.Text));
        Assert.True(words[1].X > words[0].X);
    }

    [Fact]
    public void Image_refs_are_dropped()
    {
        const string raw =
            "<|ref|>image<|/ref|><|det|>[[0, 0, 200, 200]]<|/det|>\n" +
            "![](images/0.jpg)\n" +
            "<|ref|>赛程表<|/ref|><|det|>[[10, 20, 200, 60]]<|/det|>";

        var words = UnlimitedOcrMarkup.Parse(raw);

        Assert.Single(words);
        Assert.Equal("赛程表", words[0].Text);
    }

    [Fact]
    public void Plain_text_falls_back_to_line_boxes()
    {
        var words = UnlimitedOcrMarkup.Parse("前往比赛\n下一步");

        Assert.Equal(2, words.Count);
        Assert.Equal("前往比赛", words[0].Text);
        Assert.Equal("下一步", words[1].Text);
        Assert.Equal(0, words[0].Y, 3);
        Assert.Equal(0.5, words[1].Y, 3);

        // 无框时按行给出整宽伪框：页面探针的 Contains/ContainsJoined 仍然可用
        var result = new OcrResult(words, 1920, 1080);
        Assert.True(result.ContainsJoined("前往比赛"));
    }

    [Fact]
    public void Parses_det_label_format_like_real_model_output()
    {
        // 2026-10-23 实机冒烟的真实输出形态：<|det|>类型 [x1,y1,x2,y2]<|/det|>正文（正文在闭合标记后）
        const string raw =
            "<|det|>title [119, 69, 180, 130]<|/det|>Gala\n" +
            "<|det|>image [50, 200, 95, 284]<|/det|>\n" +
            "<|det|>title [210, 82, 415, 150]<|/det|>选择赛事等级\n" +
            "<|det|>table [210, 306, 799, 701]<|/det|><table><tr><td>新手</td><td>+ 10 1,120</td></tr></table>";

        var words = UnlimitedOcrMarkup.Parse(raw);

        Assert.Equal(3, words.Count);
        Assert.Equal("Gala", words[0].Text);
        Assert.Equal(119 / 999.0, words[0].X, 3);
        Assert.Equal("选择赛事等级", words[1].Text);
        Assert.Contains("新手", words[2].Text);
    }

    [Fact]
    public void Empty_input_yields_no_words()
    {
        Assert.Empty(UnlimitedOcrMarkup.Parse(""));
        Assert.Empty(UnlimitedOcrMarkup.Parse("   "));
    }
}

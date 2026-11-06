using TranslatorApp.Core.Translate;

namespace TranslatorApp.Tests;

/// <summary>
/// LongTextTranslator 切段规则的规格（2026-11-06 第22轮）。
///
/// 【为什么只测切段，不测翻译循环】
///   翻译循环要发网络请求，属于真机验收（S1~S6）的事。
///   切段是纯函数，是这一轮唯一有分支的地方：阈值、段数上限、
///   单行不切、空行丢弃 —— 四条规则任意一条被改坏，
///   长文本就会变成「切太碎/切太多段/把句子劈开」，而真机验收里
///   这些错误都能靠「看起来还行」蒙过去。
/// </summary>
public class LongTextTranslatorTests
{
    private static string Lines(int count, int charsPerLine)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(new string((char)('a' + i % 26), charsPerLine));
        }
        return sb.ToString();
    }

    [Fact]
    public void 短文本_只切出一段()
    {
        Assert.Single(LongTextTranslator.SplitIntoParagraphs("Hello world."));
    }

    [Fact]
    public void 累积到四百字左右才成一段()
    {
        // 10 行 × 100 字 = 1000 字 → 每 4 行一段 = 400 字，共 3 段（400/400/200）。
        // 每段比整行字数和多 3 个换行符（4 行拼起来是 4×100 + 3）。
        var segments = LongTextTranslator.SplitIntoParagraphs(Lines(10, 100));
        Assert.Equal(3, segments.Count);
        Assert.True(segments[0].Length >= 400, $"第一段只有 {segments[0].Length} 字");
        Assert.Equal(2, segments[2].Split('\n').Length);
    }

    [Fact]
    public void 单行超过阈值不切句子中间()
    {
        // 900 字一整行：不许切成 400+400+100，必须是完整一段。
        var segments = LongTextTranslator.SplitIntoParagraphs(Lines(1, 900));
        Assert.Single(segments);
        Assert.Equal(900, segments[0].Length);
    }

    [Fact]
    public void 段数上限是八段()
    {
        // 8000 字 = 默认阈值下 20 段，必须被抬阈值压到 8 段以内。
        var segments = LongTextTranslator.SplitIntoParagraphs(Lines(80, 100));
        Assert.True(segments.Count <= 8, $"切出了 {segments.Count} 段");
        // 也不能过度切碎：8000 / 8 = 1000，每段该在 1000 字上下。
        Assert.True(segments.Count >= 7, $"只切出 {segments.Count} 段，说明阈值抬过头了");
    }

    [Fact]
    public void 空行和纯空白行被丢弃()
    {
        string text = "first line\n\n   \n\t\nsecond line";
        var segments = LongTextTranslator.SplitIntoParagraphs(text);
        string all = string.Join("\n", segments);
        Assert.DoesNotContain("\n\n", all);
        Assert.Contains("first line", all);
        Assert.Contains("second line", all);
    }

    [Fact]
    public void 切段不丢字()
    {
        // 这条钉住「累积拼接」这个最容易写错的地方：
        // 换行符会被重新拼一次，所以只比非空白字符数。
        string text = Lines(30, 100);
        var segments = LongTextTranslator.SplitIntoParagraphs(text);
        int before = text.Replace("\n", string.Empty).Length;
        int after = string.Join(string.Empty, segments).Replace("\n", string.Empty).Length;
        Assert.Equal(before, after);
    }
}

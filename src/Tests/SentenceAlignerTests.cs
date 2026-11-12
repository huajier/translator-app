using TranslatorApp.Core.Translate;

namespace TranslatorApp.Tests;

/// <summary>
/// 【2026-11-12 第22轮】句子切分 + Gale-Church 对齐的规格。
///
/// 【为什么锁这几条】上一轮「句数相等才配对」永远差一句（标题无句号），
/// 这一轮靠 Gale-Church 按长度比例对齐，**不需要句数相等**。
/// 这几条测试锁住「切句规则」和「对齐必须全覆盖」两个关键性质。
/// </summary>
public class SentenceAlignerTests
{
    [Fact]
    public void 缩写Dr不算句末()
    {
        var r = SentenceAligner.SplitForeign("Contact Dr. Smith for help.");
        Assert.Single(r);
    }

    [Fact]
    public void 小数点不算句末()
    {
        var r = SentenceAligner.SplitForeign("The file occupies 2.5 MB.");
        Assert.Single(r);
    }

    [Fact]
    public void 文件名里的点不算句末()
    {
        var r = SentenceAligner.SplitForeign("config.txt is missing.");
        Assert.Single(r);
    }

    [Fact]
    public void 标题行单独成句因为它后面换行且下一行非小写开头()
    {
        // 用户截图那段的结构：标题（无句号）+ 正文
        string text =
            "Obtaining Spellbooks\n" +
            "The primary use of spellbooks is to read their contents.\n" +
            "Blessed spellbooks\n" +
            "Blessed spellbooks are similar to non-cursed ones.";

        var r = SentenceAligner.SplitForeign(text);
        Assert.Equal(4, r.Count);
        Assert.Equal("Obtaining Spellbooks", r[0]);
        Assert.Equal("Blessed spellbooks", r[2]);
    }

    [Fact]
    public void 中文按句末标点切()
    {
        var r = SentenceAligner.SplitChinese("第一句。第二句！第三句？第四句；");
        Assert.Equal(4, r.Count);
    }

    [Fact]
    public void 句数相等时全是一对一()
    {
        var src = new[] { "Hello there.", "How are you?", "Fine thanks." };
        var dst = new[] { "你好。", "你好吗？", "很好谢谢。" };

        var pairs = SentenceAligner.Align(src, dst);
        Assert.Equal(3, pairs.Count);
        foreach (var p in pairs)
        {
            Assert.Single(p.Source);
            Assert.Single(p.Target);
        }
    }

    [Fact]
    public void 源3句目标4句也能对齐且全覆盖_不丢句不重复()
    {
        var src = new[]
        {
            "This is a fairly long sentence that will probably be split into two.",
            "Short one.",
            "Another medium length sentence here."
        };
        var dst = new[]
        {
            "这是一个相当长的句子。",
            "它可能会被拆成两句。",
            "短句。",
            "这里还有另一个中等长度的句子。"
        };

        var pairs = SentenceAligner.Align(src, dst);
        Assert.NotEmpty(pairs);

        // 全覆盖：每个源下标、每个目标下标都恰好出现一次
        var srcSeen = new List<int>();
        var dstSeen = new List<int>();
        foreach (var p in pairs)
        {
            srcSeen.AddRange(p.Source);
            dstSeen.AddRange(p.Target);
        }
        Assert.Equal(src.Length, srcSeen.Count);
        Assert.Equal(dst.Length, dstSeen.Count);
        Assert.Equal(Enumerable.Range(0, src.Length).OrderBy(x => x), srcSeen.OrderBy(x => x));
        Assert.Equal(Enumerable.Range(0, dst.Length).OrderBy(x => x), dstSeen.OrderBy(x => x));
    }

    [Fact]
    public void 空输入返回空列表()
    {
        Assert.Empty(SentenceAligner.Align(Array.Empty<string>(), Array.Empty<string>()));
        Assert.Empty(SentenceAligner.Align(new[] { "a" }, Array.Empty<string>()));
        Assert.Empty(SentenceAligner.SplitForeign(""));
        Assert.Empty(SentenceAligner.SplitChinese(""));
    }

    [Fact]
    public void 长句被拆时产生一对多()
    {
        var src = new[] { "One." };
        var dst = new[] { "第一句。", "第二句。", "第三句。" };

        var pairs = SentenceAligner.Align(src, dst);
        Assert.NotEmpty(pairs);

        // 必须覆盖全部 3 个目标句
        int total = 0;
        foreach (var p in pairs) total += p.Target.Count;
        Assert.Equal(3, total);
    }
}

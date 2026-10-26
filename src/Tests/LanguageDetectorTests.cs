using TranslatorApp.Core.Capture;

namespace TranslatorApp.Tests;

/// <summary>
/// LanguageDetector 的规格（写在 LanguageDetector.cs 的 remarks 里，测试只照着验，不改规则）。
///
/// 【为什么先测它】
///   纯函数，只依赖 System.Globalization，没有引擎、没有屏幕、没有剪贴板，
///   是整个 Core 里最适合打头的一块。
/// </summary>
public class LanguageDetectorTests
{
    [Fact]
    public void 纯中文_判为中文()
        => Assert.Equal("zh", LanguageDetector.Detect("今天天气很好，我们去公园散步吧"));

    [Fact]
    public void 中英混排_中文占多数时仍判中文()
        => Assert.Equal("zh", LanguageDetector.Detect("这个 API 接口返回 200，表示请求成功"));

    [Fact]
    public void 中文占比刚过三成_判中文()
    {
        // 规格是「中文占比 > 30%」——上面两条的样本都在 60% 以上，
        // 阈值从 0.30 挪到 0.90 时「纯中文」那条（100% 汉字）根本不会红，
        // 「中英混排」那条（80%）才会红。这条样本是 8 个汉字 + 5 个拉丁字母 = 61.5%，
        // 专门钉住 30% 这条线：阈值一动它就翻。
        // 注意样本里不放标点，否则符号浓度会超过 15%、被 LooksLikeSymbolHallucination 拦掉。
        Assert.Equal("zh", LanguageDetector.Detect("今天天气很好 API 返回 200 OK"));
    }

    [Fact]
    public void 纯俄文_判为俄文()
        => Assert.Equal("ru", LanguageDetector.Detect("Файл занимает 2,5 МБ памяти"));

    [Fact]
    public void 纯英文_判为外文()
        => Assert.Equal("en", LanguageDetector.Detect("The quick brown fox jumps over the lazy dog"));

    [Fact]
    public void 空字符串_判为unknown()
        => Assert.Equal("unknown", LanguageDetector.Detect(""));

    [Fact]
    public void null_不抛异常()
        => Assert.Equal("unknown", LanguageDetector.Detect(null));

    [Fact]
    public void 符号幻觉_不被误判为中文()
    {
        // OCR 常把符号认成汉字，代码里有 LooksLikeSymbolHallucination 专门挡这个
        Assert.NotEqual("zh", LanguageDetector.Detect("《》（）：；，。！？《》《》《》"));
    }

    [Fact]
    public void 符号幻觉_汉字占比过线但符号浓度高_仍不判中文()
    {
        // 上面那条其实没测到 LooksLikeSymbolHallucination（那段文本一个汉字都没有，
        // letters == 0 直接返回 unknown）。这条用 LanguageDetector.cs 注释里记录的真实 E3 样本：
        // 汉字占 4/13 = 31%，过了 30% 的中文线；符号占 9/29 = 31%，过了 15% 的符号线
        // → 必须靠 LooksLikeSymbolHallucination 拦下来，才不会被判成中文。
        Assert.NotEqual("zh", LanguageDetector.Detect("《《区刂必（ N01 《 outnd): 山（： fil("));
    }

    [Fact]
    public void 三个西里尔字母_判为俄文()
    {
        // 三个字全是西里尔字母，占比 100% > 15% → 俄文。
        // 【名字改过】原名「西里尔同形字_不被误判为拉丁字母」和实际测的东西对不上：
        //   Detect() 根本不看 CountCyrillicHomoglyphRatio，这里测的只是「识别文字体系」。
        //   同形字那层判据另有两行专门的测试（在下面）。
        Assert.Equal("ru", LanguageDetector.Detect("СРА"));
    }

    [Fact]
    public void 假西里尔_同形字占比超过0_75()
    {
        // 俄文模型在认不出的英文上会硬拼出这种假西里尔：
        //   BATTLEBOARD → ВАТТЦЕВОАВО»
        // 它们全是拉丁同形字，占比 100%，正是 MaxCyrillicHomoglyphRatioForRussian = 0.75 要拦的。
        // 这行是 OcrTextCapture.Foreign.cs 里锁 rus 模型那一关的实际判据。
        double ratio = LanguageDetector.CountCyrillicHomoglyphRatio("ВАТТЦЕВОАВО»");
        Assert.True(ratio > 0.75, $"假西里尔的同形字占比应该 > 0.75，实际 {ratio:P0}");
    }

    [Fact]
    public void 真俄文_同形字占比低于0_75()
    {
        // 真俄文有大量 ш щ ж ы э 这类没有拉丁同形的字母，占比低。
        // 这条和上一条一起钉住 0.75 那条线：调低阈值就会误伤真俄文。
        double ratio = LanguageDetector.CountCyrillicHomoglyphRatio("Съешь же ещё этих мягких французских булок");
        Assert.True(ratio < 0.75, $"真俄文的同形字占比应该 < 0.75，实际 {ratio:P0}");
    }
}

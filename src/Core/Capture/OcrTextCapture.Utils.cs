using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Abstractions;
using TranslatorApp.Core.Logging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace TranslatorApp.Core.Capture;

internal sealed partial class OcrTextCapture : ITextCapture
{

    /// <summary>单遍识别的结果。</summary>
    private readonly record struct PassResult(string Text, int LineCount, long OcrMs);

    /// <summary>
    /// 认一遍：处理引擎尺寸上限 → 转 WinRT → 识别 → 清理文本。
    ///
    /// 【引擎有尺寸上限这件事（规格第 17 条）】
    ///   超限不会自动缩，直接抛异常。所以这里必须先自己缩好再送进去。
    ///   注意不能直接给 using 变量重新赋值（编译器会警告 CS0728，
    ///   而且真正被 Dispose 的还是原来那张，等于泄漏），所以另开一个变量收尾。
    /// </summary>
    private async Task<PassResult> RecognizeOnce(
        OcrEngine engine, Bitmap image, CancellationToken cancellationToken)
    {
        Bitmap? shrunk = null;
        Bitmap working = image;
        try
        {
            int maxSide = OcrEngineFactory.MaxImageDimension;
            if (working.Width > maxSide || working.Height > maxSide)
            {
                AppLogger.Warn($"图片 {working.Width}×{working.Height} 超过引擎上限 {maxSide}，缩小后再识别");
                shrunk = Resize(working, maxSide);
                working = shrunk;
            }

            var softwareBitmap = ToSoftwareBitmap(working);
            using (softwareBitmap)
            {
                long ocrStart = Environment.TickCount64;
                var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken)
                                             .ConfigureAwait(false);
                long ocrMs = Environment.TickCount64 - ocrStart;

                string[] lines = result.Lines
                    .Select(l => l.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToArray();

                return new PassResult(
                    TextNormalizer.Normalize(string.Join("\n", lines)),
                    lines.Length,
                    (int)Math.Min(ocrMs, int.MaxValue));
            }
        }
        finally
        {
            if (shrunk is not null && !ReferenceEquals(shrunk, image)) shrunk.Dispose();
        }
    }

    /// <summary>
    /// 把 GDI 位图转成 WinRT 的 SoftwareBitmap（识别完必须 Dispose）。
    ///
    /// 【为什么绕一圈走内存流】
    ///   识别引擎要的是 SoftwareBitmap，不是 GDI 位图。
    ///   两者之间要么用 BitmapDecoder（WinRT 专用，不认 System.IO.MemoryStream），
    ///   要么用 SoftwareBitmap.CreateCopyFromBuffer（要先自己拼 BGRA 像素缓冲）。
    ///   这里选前者：先把图存成 PNG 字节，再塞进 WinRT 的内存流，
    ///   顺手还压缩了体积，大图也不会撑爆内存。
    /// 【为什么是 internal 而不是 private】
    ///   引擎对比实验（EngineBenchmark）也要调 Windows 自带 OCR，
    ///   那边的 WinRT 转图流程必须和这里**一模一样**才公平。
    ///   如果实验台自己另写一份，测出来的差异可能只是「两边转图方式不同」造成的假象。
    ///   所以直接复用这一个方法，保证测的是「引擎的差别」，不是「代码的差别」。
    /// </summary>
    internal static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        byte[] bytes;
        using (var buffer = new System.IO.MemoryStream())
        {
            bitmap.Save(buffer, ImageFormat.Png);
            bytes = buffer.ToArray();
        }

        using var randomAccess = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(randomAccess.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();

            // DetachStream：不让 DataWriter 在 Dispose 时把底下的流一起关掉，
            // 后面 decoder 还要用它。漏了这一句就是「用完就报流已关闭」。
            writer.DetachStream();
        }

        randomAccess.Seek(0);
        var decoder = BitmapDecoder.CreateAsync(randomAccess).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 日志用的原文预览：换行压成「/」，超长截断。
    /// 排查「读不全」必须能看到实际识别结果，否则无从下手。
    /// </summary>
    private static string Preview(string text)
    {
        if (string.IsNullOrEmpty(text)) return "（空）";
        string flat = text.Replace("\n", " / ").Replace("\r", "");
        return flat.Length <= 300 ? flat : flat[..300] + $"…（共 {flat.Length} 字）";
    }

    /// <summary>
    /// 按比例缩到不超过上限。
    /// </summary>
    private static Bitmap Resize(Bitmap source, int maxSide)
    {
        double scale = (double)maxSide / Math.Max(source.Width, source.Height);
        var result = new Bitmap(
            Math.Max(1, (int)(source.Width * scale)),
            Math.Max(1, (int)(source.Height * scale)),
            PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(result))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, result.Width, result.Height);
        }
        return result;
    }

    private CaptureResult Fail(CaptureFailure reason, long start, string detail)
        => new()
        {
            Channel = ChannelName,
            Failure = reason,
            ElapsedMs = Environment.TickCount64 - start,
            Detail = detail
        };

    private static bool IsAsciiAlnum(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    private static bool OnlyWhitespace(string text, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (!char.IsWhiteSpace(text[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// 是不是 `config.txt` 这种「字母数字.字母数字」，且**只有这一个点**。
    /// `2.0` 也算（X2 里 Windows 自己有 `2.0`，但那份的点读错成 `2 ℃`，
    /// 合并时按 Windows 的写法走，不改动数字）。
    /// </summary>
    private static bool IsDottedToken(string token)
    {
        int dot = token.IndexOf('.');
        if (dot <= 0 || dot != token.LastIndexOf('.') || dot == token.Length - 1) return false;

        for (int i = 0; i < token.Length; i++)
        {
            if (i == dot) continue;
            char c = token[i];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
        }
        return true;
    }

    /// <summary>
    /// 只保留字母和数字（汉字也算字母）。用来判断两份文本「说的是不是同一堆字」。
    /// </summary>
    private static string LettersAndDigitsOnly(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 有没有「孤立单词」：整个词就是 1~3 个拉丁字母。
    /// `config txt` 里的 `txt` 算，`config.txt` 里的不算（跟 config 连着）。
    /// 必须先按空白切开再判整词——直接全文匹配会把 `config` 切成 con/fig，
    /// 两边都命中，这条规则就永远不触发（已踩过）。
    /// </summary>
    private static bool HasIsolatedShortWord(string text)
    {
        foreach (string raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int start = 0, end = raw.Length;
            while (start < end && !char.IsLetterOrDigit(raw[start])) start++;
            while (end > start && !char.IsLetterOrDigit(raw[end - 1])) end--;
            if (end <= start) continue;

            bool allLatin = true;
            for (int i = start; i < end; i++)
            {
                char c = raw[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) { allLatin = false; break; }
            }
            // 长度 1~3 且全是拉丁字母
            if (allLatin && end - start <= 3) return true;
        }
        return false;
    }

    /// <summary>是不是一个汉字（CJK 统一表意文字基本区，和 LanguageDetector 同一口径）。</summary>
    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;
}

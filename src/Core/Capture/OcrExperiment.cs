using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using TranslatorApp.Core.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 识别质量对比实验：同一张图，用几种不同的预处理方案分别识别，看谁认得准。
///
/// 【为什么要有这个工具】
///   用户反馈「单独框选并不能读全」。
///   那到底是「放大害的」「转灰度害的」「还是 Windows 自带认字本身就这样」？
///   **不能靠猜**——同一张图跑一遍几种方案，一比就知道。
///   本项目已经吃过三次「猜错方向」的亏（详见 tools\日志\阶段2-验收.md）。
///
/// 【怎么用】
///   `TranslatorApp.exe --ocr实验 "图片路径"`
///   结果全部写进日志，也会打印到控制台。
/// </summary>
internal static class OcrExperiment
{
    /// <summary>跑一遍对照实验，结果写进日志。</summary>
    public static void Run(string imagePath)
    {
        AppLogger.Info("========== 识别质量对照实验开始 ==========");

        if (!File.Exists(imagePath))
        {
            AppLogger.Warn($"找不到图片：{imagePath}");
            return;
        }

        using var original = new Bitmap(imagePath);
        AppLogger.Info($"原图：{original.Width}×{original.Height}");

        Variant("A 原图（完全不处理）", () => (Bitmap)original.Clone());
        Variant("B 只放大 2 倍（不转灰）", () => Scale(original, 2.0));
        Variant("C 只转灰度（不放大）", () => ToGray(original));
        Variant("D 放大 2 倍 + 转灰度（当前做法）", () => Scale(ToGray(original), 2.0));
        Variant("E 放大 3 倍 + 转灰度", () => Scale(ToGray(original), 3.0));
        Variant("F 只转灰度 + 锐化", () => Sharpen(ToGray(original)));

        // 深色背景（VS Code、终端、深色模式的浏览器）是取词的常见场景，
        // Windows 自带 OCR 在「浅字深底」上经常一个字都认不出来。
        // 所以专门试「反色」：把深底浅字翻成浅底深字，看能不能救回来。
        Variant("G 只反色（深底浅字 → 浅底深字）", () => Invert(original));
        Variant("H 反色 + 放大 2 倍", () => Scale(Invert(original), 2.0));
        Variant("I 反色 + 灰度", () => ToGray(Invert(original)));

        AppLogger.Info("========== 识别质量对照实验结束 ==========");
    }

    /// <summary>跑一个方案：生成图片 → 识别 → 把认出��原文写进日志。</summary>
    private static void Variant(string name, Func<Bitmap> build)
    {
        Bitmap image;
        try
        {
            image = build();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"{name}：生成图片失败 —— {ex.GetType().Name} {ex.Message}");
            return;
        }

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string raw = Recognize(image);
            string cleaned = TextNormalizer.Normalize(raw);
            watch.Stop();

            int chars = TextNormalizer.CountMeaningfulChars(cleaned);
            AppLogger.Info($"--- {name} --- 图片 {image.Width}×{image.Height}，" +
                           $"用时 {watch.ElapsedMilliseconds} 毫秒，有效字符 {chars} 个");
            AppLogger.Info("【原样】" + (raw.Length == 0 ? "（空）" : raw.Replace("\n", " / ")));
            AppLogger.Info("【清理后】" + (cleaned.Length == 0 ? "（空）" : cleaned.Replace("\n", " / ")));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"{name}：识别失败 —— {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            image.Dispose();
        }
    }

    /// <summary>用 Windows 自带 OCR 识别一张图，返回文字。</summary>
    private static string Recognize(Bitmap bitmap)
    {
        if (!OcrEngineFactory.TryGetEngine(preferChinese: true, out var engine) || engine is null)
        {
            return string.Empty;
        }

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            bitmap.Save(buffer, ImageFormat.Png);
            bytes = buffer.ToArray();
        }

        using var randomAccess = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccess.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }

        randomAccess.Seek(0);
        var decoder = BitmapDecoder.CreateAsync(randomAccess).AsTask().GetAwaiter().GetResult();
        using var softwareBitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();

        var result = engine.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    /// <summary>按比例放大。</summary>
    private static Bitmap Scale(Bitmap source, double factor)
    {
        int w = Math.Max(1, (int)Math.Round(source.Width * factor));
        int h = Math.Max(1, (int)Math.Round(source.Height * factor));
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, w, h));
        }
        return result;
    }

    /// <summary>转灰度（去掉彩色花纹的干扰）。</summary>
    private static Bitmap ToGray(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        using (var attributes = new ImageAttributes())
        {
            // 标准感知加权：0.299 R + 0.587 G + 0.114 B
            attributes.SetColorMatrix(new ColorMatrix(new[]
            {
                new[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
                new[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
                new[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
                new[] { 0f,     0f,     0f,     1f, 0f },
                new[] { 0f,     0f,     0f,     0f, 1f },
            }));

            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    /// <summary>
    /// 反色（深色背景下浅色字 → 浅色背景下深色字）。
    ///
    /// 【为什么要做这一步 —— 2026-10-08 实测】
    ///   Windows 自带 OCR 在「白字黑底」（VS Code、终端、深色模式网页）上
    ///   实测**一个字都认不出来**。它的训练数据全是「深字浅底」。
    ///   翻过来就认得了。
    /// </summary>
    private static Bitmap Invert(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        using (var attributes = new ImageAttributes())
        {
            // R' = (1-R)*255，即把每条通道的值反过来
            attributes.SetColorMatrix(new ColorMatrix(new[]
            {
                new[] { -1f, 0f, 0f, 0f, 255f },
                new[] { 0f, -1f, 0f, 0f, 255f },
                new[] { 0f, 0f, -1f, 0f, 255f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            }));

            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    /// <summary>锐化（把插值放大后变虚的笔画找回来一点）。</summary>
    private static Bitmap Sharpen(Bitmap source)
    {
        // 【踩过的坑】同一张图先 LockBits(ReadOnly) 再 LockBits(WriteOnly) 会抛
        //   「Bitmap region is already locked」——
        //   因为读写两次锁的是同一块内存。必须先复制一份到另一张图上再分别锁。
        var input = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(input))
        {
            g.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

        var kernel = new float[,] { { 0f, -1f, 0f }, { -1f, 5f, -1f }, { 0f, -1f, 0f } };

        BitmapData srcData = input.LockBits(
            new Rectangle(0, 0, input.Width, input.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dstData = result.LockBits(
            new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        unsafe
        {
            byte* src = (byte*)srcData.Scan0;
            byte* dst = (byte*)dstData.Scan0;
            int stride = srcData.Stride;

            for (int y = 1; y < result.Height - 1; y++)
            for (int x = 1; x < result.Width - 1; x++)
            {
                for (int c = 0; c < 3; c++)   // 只处理 BGR，alpha 原样拷过去
                {
                    float sum = 0;
                    for (int ky = -1; ky <= 1; ky++)
                    for (int kx = -1; kx <= 1; kx++)
                    {
                        sum += kernel[ky + 1, kx + 1] * src[(y + ky) * stride + (x + kx) * 4 + c];
                    }
                    dst[y * stride + x * 4 + c] = (byte)Math.Clamp(sum, 0, 255);
                }
            }

            // 边缘一圈直接照抄
            for (int x = 0; x < result.Width; x++)
            {
                dst[x * 4 + 3] = src[x * 4 + 3];
                dst[(result.Height - 1) * stride + x * 4 + 3] = src[(result.Height - 1) * stride + x * 4 + 3];
            }
        }

        input.UnlockBits(srcData);
        result.UnlockBits(dstData);
        input.Dispose();
        return result;
    }
}

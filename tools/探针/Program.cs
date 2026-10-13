using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using TranslatorApp.Core.Capture;

/// <summary>
/// 只做一件事：把一张图丢给真正的 ImagePreprocessor，看看几路二值化到底变成了什么。
/// 这里只量像素，不做任何识别准不准的判断。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("用法：探针 <图片路径>"); return 1; }
        using var src = new Bitmap(args[0]);

        Console.WriteLine($"原图 {src.Width}×{src.Height}，平均亮度 {ImagePreprocessor.MeasureAverageLuminance(src):F1}，" +
                          $"要不要整张反色 = {ImagePreprocessor.NeededInversion(src)}");
        ReportRing("原图", src);
        ReportRowProfile("原图", src);

        // 跟识别流程一致：放大 2 倍并转灰度
        using var big = (Bitmap)ImagePreprocessor.UpscaleForForeign(src);
        Console.WriteLine($"放大灰度后 {big.Width}×{big.Height}，平均亮度 {ImagePreprocessor.MeasureAverageLuminance(big):F1}");

        Bitmap[] planes = ImagePreprocessor.PrepareAllPlanes(big);
        string[] names = { "红通道", "绿通道", "蓝通道", "灰度", "局部阈值" };
        for (int i = 0; i < planes.Length; i++)
        {
            Report(names[i], planes[i]);
            planes[i].Dispose();
        }

        using var redGray = ImagePreprocessor.PrepareRedChannelGray(big);
        Report("红通道不二值化", redGray);

        // ---- 复核 BestThreshold 到底算出了什么阈值 ----
        // 灰度直方图（只看放大后的图，补边影响很小）
        var hist = new int[256];
        for (int y = 0; y < big.Height; y++)
            for (int x = 0; x < big.Width; x++)
            {
                Color c = big.GetPixel(x, y);
                hist[(c.R * 299 + c.G * 587 + c.B * 114) / 1000]++;
            }
        long total = hist.Sum();
        Console.WriteLine("  灰度分位数：" + string.Join("  ", new[] { 1, 5, 10, 25, 50, 75, 90, 95, 99 }
            .Select(p => $"p{p}={Percentile(hist, total, p)}")));
        int buggy = OtsuThreshold(hist, total, buggySum: true);
        int right = OtsuThreshold(hist, total, buggySum: false);
        Console.WriteLine($"  现有算法算出的阈值 = {buggy} → 判为文字（黑）的比例 {(double)Cumulative(hist, buggy) * 100 / total:F2}%");
        Console.WriteLine($"  正确算法算出的阈值 = {right} → 判为文字（黑）的比例 {(double)Cumulative(hist, right) * 100 / total:F2}%");
        return 0;
    }

    private static int Percentile(int[] hist, long total, int p)
    {
        long want = total * p / 100, n = 0;
        for (int t = 0; t < 256; t++) { n += hist[t]; if (n >= want) return t; }
        return 255;
    }

    private static long Cumulative(int[] hist, int threshold)
    {
        long n = 0;
        for (int t = 0; t <= threshold; t++) n += hist[t];
        return n;
    }

    /// <summary>buggySum=true 复刻源码里的写法（sumBelow += t * sums[t]）；false 是标准 Otsu。</summary>
    private static int OtsuThreshold(int[] counts, long total, bool buggySum)
    {
        long totalSum = 0;
        for (int t = 0; t < 256; t++) totalSum += (long)t * counts[t];

        double best = double.MinValue;
        int bestT = 128;
        long sumBelow = 0;
        int countBelow = 0;
        for (int t = 0; t <= 255; t++)
        {
            countBelow += counts[t];
            sumBelow += buggySum ? (long)t * ((long)t * counts[t]) : (long)t * counts[t];
            int countAbove = (int)(total - countBelow);
            if (countBelow == 0 || countAbove == 0) continue;
            double meanBelow = (double)sumBelow / countBelow;
            double meanAbove = (double)(totalSum - sumBelow) / countAbove;
            double between = (double)countBelow * countAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best) { best = between; bestT = t; }
        }
        return bestT;
    }

    /// <summary>量一张图：多少比例是黑的、多少是白的、平均亮度多少。</summary>
    private static void Report(string name, Bitmap bmp)
    {
        long black = 0, white = 0, sum = 0, count = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                int v = (int)(bmp.GetPixel(x, y).R * 0.299 + bmp.GetPixel(x, y).G * 0.587 + bmp.GetPixel(x, y).B * 0.114);
                if (v <= 40) black++;
                else if (v >= 215) white++;
                sum += v;
                count++;
            }
        }
        Console.WriteLine($"  {name,-14} {bmp.Width}×{bmp.Height}  黑 {(100.0 * black / count):F2}%  " +
                          $"白 {(100.0 * white / count):F2}%  平均亮度 {sum / (double)count:F1}");
    }

    /// <summary>用主项目真正那个判据算一遍：阈值是多少、四边最外一圈有多少落在暗侧。</summary>
    private static void ReportRing(string name, Bitmap bmp)
    {
        bool need = ImagePreprocessor.AnalyzeBackground(bmp, out int threshold, out int ringDark, out int ringTotal);
        Console.WriteLine($"  [{name}] 反色判据：阈值 {threshold}，四边最外一圈暗侧 {ringDark}/{ringTotal}" +
                          $"（{100.0 * ringDark / Math.Max(1, ringTotal):F1}%）→ {(need ? "反色" : "不反色")}");
    }

    /// <summary>横向亮度剖面：每 1/10 宽度取一列的平均亮度，看背景是不是渐变。</summary>
    private static void ReportRowProfile(string name, Bitmap bmp)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (int k = 0; k < 10; k++)
        {
            int x0 = bmp.Width * k / 10, x1 = Math.Max(x0 + 1, bmp.Width * (k + 1) / 10);
            long sum = 0, n = 0;
            for (int x = x0; x < x1; x++)
                for (int y = 0; y < bmp.Height; y++)
                {
                    Color c = bmp.GetPixel(x, y);
                    sum += (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                    n++;
                }
            parts.Add($"{(sum / (double)n):F0}");
        }
        Console.WriteLine($"  [{name}] 从左到右十段的平均亮度：{string.Join("  ", parts)}");
    }
}

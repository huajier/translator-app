using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;

namespace CardProbe;

/// <summary>
/// 小字密集符号图（公式行）识别实验：
/// 试不同放大倍数 × 是否二值化，看哪种组合读得最干净。
/// </summary>
public static class Probe6
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.8.1";

    public static void Run()
    {
        string probeDir = AppContext.BaseDirectory;
        string ms = System.IO.Path.Combine(DllDir, "models");
        string md = System.IO.Path.Combine(probeDir, "models");
        if (System.IO.Directory.Exists(ms) && !System.IO.Directory.Exists(md)) CopyDir(ms, md);

        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "TranslatorApp.dll"));
        var engType = asm.GetType("TranslatorApp.Core.Capture.RapidOcrEngine")!;
        var settingsType = asm.GetType("TranslatorApp.Core.Capture.AppSettings")!;
        settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        settingsType.GetProperty("RapidOcrPrimary")?.SetValue(null, true);
        var tryM = engType.GetMethod("TryRecognize", BindingFlags.Public | BindingFlags.Static)!;

        string png = @"D:/111Desktop/deepseek/翻译软件/release/v0.4.8.1/debug-screenshots/sel_20261113_102845_675.png";
        if (!System.IO.File.Exists(png)) { Console.WriteLine("找不到 " + png); return; }

        using var src = new Bitmap(png);
        Console.WriteLine($"原图 {src.Width}×{src.Height}");
        Console.WriteLine("真值应为: DamageTaken = MAX(0.75 x Damage - AC, 0) + 0.25 x Damage");
        Console.WriteLine();

        // 试各种放大倍数（1x 到 6x），原图 vs 二值化
        foreach (bool binarize in new[] { false, true })
        {
            Console.WriteLine(binarize ? "=== 二值化版 ===" : "=== 原图（RGB）===");
            foreach (double scale in new[] { 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 6.0 })
            {
                using var up = Scale(src, scale);
                using var input = binarize ? Binarize(up) : new Bitmap(up);

                object?[] a = new object?[] { input, 0.0, "", 0, 0, 0 };
                string txt;
                try { txt = (string?)tryM.Invoke(null, a) ?? ""; }
                catch (Exception ex) { Console.WriteLine($"  {scale}x 失败 {ex.InnerException?.Message}"); continue; }

                int cc = 0; foreach (char c in txt) if (!char.IsWhiteSpace(c)) cc++;
                // 与真值算相似度（简单字符级）
                double sim = Similarity("DamageTaken = MAX(0.75 x Damage - AC, 0) + 0.25 x Damage", txt);
                Console.WriteLine($"  {scale,4:F1}x  {input.Width}×{input.Height}  分{(double)a[1]!:F2}  {cc}字  相似{sim:P0}  {Trunc(txt, 68)}");
            }
            Console.WriteLine();
        }
    }

    private static Bitmap Scale(Bitmap src, double s)
    {
        if (Math.Abs(s - 1.0) < 0.01) { var c = new Bitmap(src); c.SetResolution(96, 96); return c; }
        var d = new Bitmap((int)Math.Round(src.Width * s), (int)Math.Round(src.Height * s));
        d.SetResolution(96, 96);
        using var g = Graphics.FromImage(d);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, d.Width, d.Height);
        return d;
    }

    /// <summary>简单 Otsu 二值化。</summary>
    private static Bitmap Binarize(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        int[] hist = new int[256];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = src.GetPixel(x, y);
                hist[(int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B)]++;
            }
        int total = w * h, sum = 0;
        for (int i = 0; i < 256; i++) sum += i * hist[i];
        int sumB = 0, wB = 0, best = 0; double bestVar = -1;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            int wF = total - wB; if (wF == 0) break;
            sumB += t * hist[t];
            double mB = (double)sumB / wB, mF = (double)(sum - sumB) / wF;
            double v = (double)wB * wF * (mB - mF) * (mB - mF);
            if (v > bestVar) { bestVar = v; best = t; }
        }

        var d = new Bitmap(w, h);
        d.SetResolution(96, 96);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = src.GetPixel(x, y);
                int g = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                d.SetPixel(x, y, g > best ? Color.White : Color.Black);
            }
        return d;
    }

    private static double Similarity(string want, string got)
    {
        string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
        string a = Norm(want), b = Norm(got);
        if (a.Length == 0) return 0;
        int m = a.Length, n = b.Length;
        if (m * n > 400000) return a == b ? 1 : 0;
        var prev = new int[n + 1];
        for (int i = 1; i <= m; i++)
        {
            var cur = new int[n + 1];
            for (int j = 1; j <= n; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            prev = cur;
        }
        return (double)prev[n] / m;
    }

    private static string Trunc(string s, int n)
    {
        s = s.Replace("\n", " / ");
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    private static void CopyDir(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var f in System.IO.Directory.GetFiles(src))
            System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
        foreach (var dd in System.IO.Directory.GetDirectories(src))
            CopyDir(dd, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(dd)));
    }
}

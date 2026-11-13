using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.Loader;

namespace CardProbe;

/// <summary>
/// 扫「尺寸小于阈值就强行 3x」的阈值，找成绩不掉的最高阈值（最省时间）。
/// </summary>
public static class Probe9
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.8.1";
    private const string Corpus = @"D:\111Desktop\deepseek\翻译软件\tools\语料";

    public static void Run()
    {
        string pd = AppContext.BaseDirectory;
        string mdlSrc = System.IO.Path.Combine(DllDir, "models");
        string mdlDst = System.IO.Path.Combine(pd, "models");
        if (System.IO.Directory.Exists(mdlSrc) && !System.IO.Directory.Exists(mdlDst)) CopyDir(mdlSrc, mdlDst);

        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "TranslatorApp.dll"));
        var engType = asm.GetType("TranslatorApp.Core.Capture.RapidOcrEngine")!;
        var settingsType = asm.GetType("TranslatorApp.Core.Capture.AppSettings")!;
        settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        settingsType.GetProperty("RapidOcrPrimary")?.SetValue(null, true);
        var tryM = engType.GetMethod("TryRecognize", BindingFlags.Public | BindingFlags.Static)!;

        var items = LoadTruth(@"D:\111Desktop\deepseek\翻译软件\tools\素材\行真值.json");
        Console.WriteLine($"行真值 {items.Count} 条，开始跑（每张跑 1x + 3x，缓存起来）");

        var data = new List<(string id, int w, int h, bool ok1, bool ok3, long ms1, long ms3)>();
        foreach (var (id, file, want) in items)
        {
            string fp = System.IO.Path.Combine(Corpus, file);
            if (!System.IO.File.Exists(fp)) continue;
            using var src = new Bitmap(fp);

            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            var (t1, _) = Run(tryM, src);
            sw1.Stop();

            using var up = Scale3(src);
            var sw3 = System.Diagnostics.Stopwatch.StartNew();
            var (t3, _) = Run(tryM, up);
            sw3.Stop();

            data.Add((id, src.Width, src.Height,
                      Sim(want, t1) >= 0.90, Sim(want, t3) >= 0.90,
                      sw1.ElapsedMilliseconds, sw3.ElapsedMilliseconds));
        }

        int total = data.Count;
        int baseOk = 0, baseMs = 0;
        foreach (var d in data)
        {
            // 现状：1x 不合格才 3x
            baseOk += d.ok1 || d.ok3 ? 1 : 0;
        }
        Console.WriteLine();
        Console.WriteLine($"基准（1x失败才3x，近似现状）：{baseOk}/{total}");
        Console.WriteLine();

        // 扫阈值
        Console.WriteLine("阈值策略：宽度 <= W 且 高度 <= H 时，1x 和 3x 都跑、择优");
        Console.WriteLine($"{"W",-6}{"H",-6}{"命中",-8}{"成绩",-10}{"vs基准",-8}{"平均耗时",-10}");
        Console.WriteLine(new string('-', 56));

        var best = new List<(int w, int h, int hit, int ok, long ms)>();
        foreach (int W in new[] { 400, 500, 600, 700, 800, 1000, 1300, 9999 })
        {
            foreach (int H in new[] { 60, 90, 120, 160, 9999 })
            {
                int ok = 0; int hit = 0; long ms = 0;
                foreach (var d in data)
                {
                    bool forceBoth = d.w <= W && d.h <= H;
                    if (forceBoth)
                    {
                        hit++;
                        ms += d.ms1 + d.ms3;
                        ok += (d.ok1 || d.ok3) ? 1 : 0;
                    }
                    else
                    {
                        // 不算强制的，走现状（1x 合格就用，不合格才 3x）
                        ms += d.ms1 + (d.ok1 ? 0 : d.ms3);
                        ok += (d.ok1 || d.ok3) ? 1 : 0;
                    }
                }
                if (ok >= baseOk)
                {
                    best.Add((W, H, hit, ok, ms / Math.Max(1, total)));
                }
            }
        }

        foreach (var b in best)
            Console.WriteLine($"{b.w,-6}{b.h,-6}{b.hit,-8}{b.ok + "/" + total,-10}{"+" + (b.ok - baseOk),-8}{b.ms + " ms",-10}");

        Console.WriteLine();
        if (best.Count > 0)
        {
            // 选命中最多但成绩不掉、耗时最低的
            best.Sort((a, b) => a.ms.CompareTo(b.ms));
            var pick = best[0];
            Console.WriteLine($"推荐：宽 <= {pick.w} 且 高 <= {pick.h}，命中 {pick.hit} 张，" +
                              $"成绩 {pick.ok}/{total}，平均 {pick.ms} ms");
        }
        else Console.WriteLine("没有阈值能保住基准成绩");
    }

    private static (string, double) Run(MethodInfo m, Bitmap b)
    {
        object?[] a = new object?[] { b, 0.0, "", 0, 0, 0 };
        try { return ((string?)m.Invoke(null, a) ?? "", (double)a[1]!); }
        catch { return ("", 0); }
    }

    private static Bitmap Scale3(Bitmap src)
    {
        int longest = Math.Max(src.Width, src.Height);
        double f = longest * 3 > 2000 ? 2000.0 / longest : 3.0;
        var d = new Bitmap((int)Math.Round(src.Width * f), (int)Math.Round(src.Height * f));
        d.SetResolution(96, 96);
        using var g = Graphics.FromImage(d);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, d.Width, d.Height);
        return d;
    }

    private static double Sim(string want, string got)
    {
        string N(string s) { var sb = new System.Text.StringBuilder(); foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c)); return sb.ToString(); }
        string a = N(want), b = N(got);
        if (a.Length == 0) return 0;
        int m = a.Length, n = b.Length;
        if (m * n > 500000) return b.Contains(a) ? 1 : 0;
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

    private static List<(string id, string file, string want)> LoadTruth(string path)
    {
        string json = System.IO.File.ReadAllText(path);
        var list = new List<(string, string, string)>();
        int i = 0;
        while (true)
        {
            int a = json.IndexOf("\"id\"", i); if (a < 0) break;
            string id = Str(json, a);
            int c = json.IndexOf("\"file\"", a); if (c < 0) break;
            string file = Str(json, c).Replace("/", "\\");
            int d = json.IndexOf("\"lines\"", c);
            int e = json.IndexOf(']', d);
            string seg = json.Substring(d, e - d);
            var parts = new List<string>();
            int p = 0;
            while (true)
            {
                int q = seg.IndexOf('"', p); if (q < 0) break;
                int r = seg.IndexOf('"', q + 1); if (r < 0) break;
                string s = seg.Substring(q + 1, r - q - 1);
                if (s != "lines") parts.Add(s);
                p = r + 1;
            }
            list.Add((id, file, string.Join("\n", parts)));
            i = e > 0 ? e : i + 1;
        }
        return list;
    }

    private static string Str(string json, int from)
    {
        int q = json.IndexOf('"', json.IndexOf(':', from)) + 1;
        int r = json.IndexOf('"', q);
        return q > 0 && r > q ? json.Substring(q, r - q) : "";
    }

    private static void CopyDir(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var f in System.IO.Directory.GetFiles(src)) System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
        foreach (var d in System.IO.Directory.GetDirectories(src)) CopyDir(d, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(d)));
    }
}

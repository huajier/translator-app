using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;

namespace CardProbe;

/// <summary>
/// 「总是跑 3x」vs「现状（按 NeedsUpscaleRetry）」：80 张行级图对比。
/// 直接调产品 RapidOcrEngine.TryRecognize —— 它内部就是 1x + 条件重试，
/// 所以这里手工模拟两种策略：分别跑 1x、3x，再按各自规则选。
/// </summary>
public static class Probe7
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.8.1";
    private const string Corpus = @"D:\111Desktop\deepseek\翻译软件\tools\语料";

    public static void Run()
    {
        string pd = AppContext.BaseDirectory;
        string ms = System.IO.Path.Combine(DllDir, "models");
        string md = System.IO.Path.Combine(pd, "models");
        if (System.IO.Directory.Exists(ms) && !System.IO.Directory.Exists(md)) CopyDir(ms, md);

        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "TranslatorApp.dll"));
        var engType = asm.GetType("TranslatorApp.Core.Capture.RapidOcrEngine")!;
        var settingsType = asm.GetType("TranslatorApp.Core.Capture.AppSettings")!;
        settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        settingsType.GetProperty("RapidOcrPrimary")?.SetValue(null, true);
        var tryM = engType.GetMethod("TryRecognize", BindingFlags.Public | BindingFlags.Static)!;

        // 读行真值
        string truthPath = @"D:\111Desktop\deepseek\翻译软件\tools\素材\行真值.json";
        string json = System.IO.File.ReadAllText(truthPath);
        var items = ParseTruth(json);
        Console.WriteLine($"行真值 {items.Count} 条");

        int n1Ok = 0, n3Ok = 0, nCurOk = 0, total = 0;
        long ms1 = 0, ms3 = 0;
        var worse = new List<string>();

        foreach (var (id, file, want) in items)
        {
            string fp = System.IO.Path.Combine(Corpus, file);
            if (!System.IO.File.Exists(fp)) continue;
            total++;

            using var src = new Bitmap(fp);
            // 1x
            var (t1, ok1, e1) = Run(tryM, src);
            ms1 += e1;
            // 3x（长边上限 2000）
            using var up = Scale3(src);
            var (t3, ok3, e3) = Run(tryM, up);
            ms3 += e3;

            bool s1 = Sim(want, t1) >= 0.90;
            bool s3 = Sim(want, t3) >= 0.90;
            // 现状：只有 1x 不合格才用 3x（近似 NeedsUpscaleRetry 的效果）
            bool sCur = s1 || s3;

            if (s1) n1Ok++;
            if (s3) n3Ok++;
            if (sCur) nCurOk++;

            if (s1 && !s3) worse.Add($"{id}: 1x对但3x错");
        }

        Console.WriteLine();
        Console.WriteLine($"样本 {total} 张");
        Console.WriteLine($"  纯 1x        : {n1Ok}/{total}  ({100.0 * n1Ok / total:F1}%)  耗时 {ms1} ms");
        Console.WriteLine($"  纯 3x        : {n3Ok}/{total}  ({100.0 * n3Ok / total:F1}%)  耗时 {ms3} ms");
        Console.WriteLine($"  1x或3x（择优）: {nCurOk}/{total}  ({100.0 * nCurOk / total:F1}%)");
        Console.WriteLine();
        Console.WriteLine($"3x 比 1x 差的有 {worse.Count} 张：");
        foreach (var w in worse) Console.WriteLine("  " + w);
    }

    private static (string text, bool ok, long ms) Run(MethodInfo m, Bitmap bmp)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        object?[] a = new object?[] { bmp, 0.0, "", 0, 0, 0 };
        string t = "";
        try { t = (string?)m.Invoke(null, a) ?? ""; } catch { }
        sw.Stop();
        return (t, true, sw.ElapsedMilliseconds);
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
        string N(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
        string a = N(want), b = N(got);
        if (a.Length == 0) return 0;
        int m = a.Length, n = b.Length;
        if (m * n > 500000) return a == b ? 1 : (b.Contains(a) ? 1 : 0);
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

    /// <summary>极简 JSON 抽取：id / file / lines。</summary>
    private static List<(string id, string file, string want)> ParseTruth(string json)
    {
        var list = new List<(string, string, string)>();
        int i = 0;
        while (true)
        {
            int a = json.IndexOf("\"id\"", i);
            if (a < 0) break;
            int b = json.IndexOf(':');
            string id = Str(json, a);
            int c = json.IndexOf("\"file\"", a);
            if (c < 0) break;
            string file = Str(json, c).Replace("\\\\", "\\").Replace("/", "\\");
            int d = json.IndexOf("\"lines\"", c);
            int e = json.IndexOf(']', d);
            string seg = json.Substring(d, e - d);
            var parts = new List<string>();
            int p = 0;
            while (true)
            {
                int q = seg.IndexOf('"', p);
                if (q < 0) break;
                int r = seg.IndexOf('"', q + 1);
                if (r < 0) break;
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
        foreach (var f in System.IO.Directory.GetFiles(src))
            System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
        foreach (var dd in System.IO.Directory.GetDirectories(src))
            CopyDir(dd, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(dd)));
    }
}

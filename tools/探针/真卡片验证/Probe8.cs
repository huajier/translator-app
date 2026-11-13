using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.Loader;

namespace CardProbe;

/// <summary>拿真机存下来的图，直接跑产品识别，1x 与 3x 各跑 3 次看稳不稳定。</summary>
public static class Probe8
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.8.1";
    private const string DumpDir = @"D:\111Desktop\deepseek\翻译软件\tools\临时\ocr-dump";

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

        // 0002 = 659x80（真机那次 87 字，说丢段很严重）
        foreach (string name in new[] { "0002.png", "0004.png", "0005.png", "0008.png" })
        {
            string fp = System.IO.Path.Combine(DumpDir, name);
            if (!System.IO.File.Exists(fp)) { Console.WriteLine($"{name} 不存在"); continue; }

            using var src = new Bitmap(fp);
            Console.WriteLine();
            Console.WriteLine($"=== {name}  {src.Width}×{src.Height} ===");
            Console.WriteLine("  1x：");
            for (int r = 0; r < 3; r++)
            {
                var (t, sc) = Run(tryM, src);
                Console.WriteLine($"    第{r + 1}次 分{sc:F2} {Count(t)}字  {Trunc(t, 76)}");
            }
            using var up = Scale3(src);
            Console.WriteLine($"  3x（{up.Width}×{up.Height}）：");
            for (int r = 0; r < 2; r++)
            {
                var (t, sc) = Run(tryM, up);
                Console.WriteLine($"    第{r + 1}次 分{sc:F2} {Count(t)}字  {Trunc(t, 76)}");
            }
        }
    }

    private static (string, double) Run(MethodInfo m, Bitmap b)
    {
        object?[] a = new object?[] { b, 0.0, "", 0, 0, 0 };
        try { var t = (string?)m.Invoke(null, a) ?? ""; return (t, (double)a[1]!); }
        catch { return ("", 0); }
    }

    private static int Count(string s) { int n = 0; foreach (char c in s) if (!char.IsWhiteSpace(c)) n++; return n; }
    private static string Trunc(string s, int n) { s = s.Replace("\n", " / "); return s.Length <= n ? s : s.Substring(0, n) + "…"; }

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

    private static void CopyDir(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var f in System.IO.Directory.GetFiles(src)) System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
        foreach (var d in System.IO.Directory.GetDirectories(src)) CopyDir(d, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(d)));
    }
}

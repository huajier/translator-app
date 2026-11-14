using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;

namespace CardProbe;

/// <summary>
/// 同一批图：RapidOCR vs Tesseract(eng)，比空格数和内容完整性。
/// Tesseract 走产品的 TesseractOcr 封装（foreignImage = UpscaleForForeign(raw)，放大4倍+灰度）。
/// </summary>
public static class Probe12
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.8.1";
    private const string Dump = @"D:\111Desktop\deepseek\翻译软件\tools\临时\ocr-dump";
    private const string Corpus = @"D:\111Desktop\deepseek\翻译软件\tools\语料";

    public static void Run()
    {
        string pd = AppContext.BaseDirectory;
        string ms = System.IO.Path.Combine(DllDir, "models");
        string md = System.IO.Path.Combine(pd, "models");
        if (System.IO.Directory.Exists(ms) && !System.IO.Directory.Exists(md)) CopyDir(ms, md);
        // tessdata 也要在 exe 旁边
        string ts = System.IO.Path.Combine(DllDir, "tessdata");
        string td = System.IO.Path.Combine(pd, "tessdata");
        if (System.IO.Directory.Exists(ts) && !System.IO.Directory.Exists(td)) CopyDir(ts, td);

        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "TranslatorApp.dll"));
        var settingsType = asm.GetType("TranslatorApp.Core.Capture.AppSettings")!;
        settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        settingsType.GetProperty("RapidOcrPrimary")?.SetValue(null, true);

        var engType = asm.GetType("TranslatorApp.Core.Capture.RapidOcrEngine")!;
        var tessType = asm.GetType("TranslatorApp.Core.Capture.TesseractOcr")!;
        var prepType = asm.GetType("TranslatorApp.Core.Capture.ImagePreprocessor")!;

        var tryM = engType.GetMethod("TryRecognize", BindingFlags.Public | BindingFlags.Static)!;
        var tessEnsure = tessType.GetMethod("EnsureInitialized", BindingFlags.Public | BindingFlags.Static)!;
        tessEnsure.Invoke(null, null);
        Console.WriteLine("Tesseract 可用 = " + tessType.GetProperty("IsAvailable")?.GetValue(null));

        var upscale = prepType.GetMethod("UpscaleForForeign", BindingFlags.Public | BindingFlags.Static)!;
        var tessRec = tessType.GetMethod("TryRecognize", BindingFlags.Public | BindingFlags.Static);
        var langsType = tessType.GetNestedType("Languages")!;
        string engLang = (string)langsType.GetField("English")!.GetValue(null)!;

        var files = new List<(string, string)>();
        foreach (var f in new[] { "0002.png", "0003.png", "0005.png", "0008.png" })
        {
            string p = System.IO.Path.Combine(Dump, f);
            if (System.IO.File.Exists(p)) files.Add(("dump/" + f, p));
        }
        foreach (var f in new[] { @"实机\预览\13-Barony-加成面板-第1行.png",
                                  @"实机\预览\17-Barony-长描述-第2行.png",
                                  @"实机\预览\23-MutantSpider-标题-Boss名.png" })
        {
            string p = System.IO.Path.Combine(Corpus, f);
            if (System.IO.File.Exists(p)) files.Add(("语料/" + System.IO.Path.GetFileName(f), p));
        }

        Console.WriteLine();
        Console.WriteLine($"{"样本",-34}{"引擎",-12}{"空格",-6}{"字母数字",-8}内容");
        Console.WriteLine(new string('-', 110));

        int rapSp = 0, tessSp = 0;
        foreach (var (label, path) in files)
        {
            using var src = new Bitmap(path);

            // --- RapidOCR ---
            object?[] a = new object?[] { src, 0.0, "", 0, 0, 0 };
            string r = "";
            try { r = (string?)tryM.Invoke(null, a) ?? ""; } catch { }
            int rs = Cnt(r, ' '), ra = CntAlnum(r);
            rapSp += rs;
            Console.WriteLine($"{label,-34}{"RapidOCR",-12}{rs,-6}{ra,-8}{Trunc(r, 56)}");

            // --- Tesseract：按产品的真实路径，先判断深底再反色，然后放大 ---
            Bitmap rawForTess = src;
            try
            {
                var analyze = prepType.GetMethod("AnalyzeBackground", BindingFlags.Public | BindingFlags.Static)!;
                var invert = prepType.GetMethod("Invert", BindingFlags.Public | BindingFlags.Static)!;
                object?[] abArgs = new object?[] { src, 0, 0, 0 };
                bool deep = (bool)analyze.Invoke(null, abArgs)!;
                if (deep) rawForTess = (Bitmap)invert.Invoke(null, new object[] { src })!;
            }
            catch { }
            using var up = (Bitmap)upscale.Invoke(null, new object[] { rawForTess })!;
            if (!ReferenceEquals(rawForTess, src)) rawForTess.Dispose();
            string t = "";
            try
            {
                var res = tessRec!.Invoke(null, new object[] { up, engLang });
                if (res is null) t = "(res=null)";
                else
                {
                    var rt = res.GetType();
                    var txtProp = rt.GetProperty("Text");
                    if (txtProp is null) t = "(无 Text 属性: " + string.Join(",", Array.ConvertAll(rt.GetProperties(), x => x.Name)) + ")";
                    else t = (string?)txtProp.GetValue(res) ?? "(Text=null)";
                }
            }
            catch (Exception ex) { t = "(失败:" + ex.InnerException?.Message + ")"; }
            int tsN = Cnt(t, ' ');
            tessSp += tsN;
            Console.WriteLine($"{label,-34}{"Tesseract",-12}{tsN,-6}{CntAlnum(t),-8}{Trunc(t, 56)}");
            Console.WriteLine();
        }

        Console.WriteLine($"总空格数  RapidOCR={rapSp}  Tesseract={tessSp}");
    }

    private static int Cnt(string s, char c) { int n = 0; foreach (char x in s) if (x == c) n++; return n; }
    private static int CntAlnum(string s) { int n = 0; foreach (char x in s) if (char.IsLetterOrDigit(x)) n++; return n; }
    private static string Trunc(string s, int n) { s = s.Replace("\n", " / "); return s.Length <= n ? s : s.Substring(0, n) + "…"; }

    private static void CopyDir(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var f in System.IO.Directory.GetFiles(src)) System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
        foreach (var d in System.IO.Directory.GetDirectories(src)) CopyDir(d, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(d)));
    }
}

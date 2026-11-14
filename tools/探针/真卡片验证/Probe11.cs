using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;

namespace CardProbe;

/// <summary>
/// 对比 en rec 与 latin rec 两个模型在「丢空格」上的表现。
/// 直接调用产品 RapidOcrEngine（它按 AppSettings.RapidOcrRec 选模型），
/// 所以这里通过改 AppSettings 的私有字段来切换。
/// </summary>
public static class Probe11
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

        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "TranslatorApp.dll"));
        var settingsType = asm.GetType("TranslatorApp.Core.Capture.AppSettings")!;

        // 关键：rec 模型在引擎初始化时读取，切一次要重建引擎。
        // 用两个独立进程做对比 —— 这里先跑 en，输出后由调用方重启跑 latin。
        string want = Environment.GetEnvironmentVariable("PROBE_REC") ?? "en";

        settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        settingsType.GetProperty("RapidOcrPrimary")?.SetValue(null, true);
        var recProp = settingsType.GetProperty("RapidOcrRec")!;
        // 私有 setter，用 backing field
        var bf = settingsType.GetField("<RapidOcrRec>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Static);
        bf?.SetValue(null, want);
        Console.WriteLine($"=== rec 模型 = {recProp.GetValue(null)} ===");

        var engType = asm.GetType("TranslatorApp.Core.Capture.RapidOcrEngine")!;
        var tryM = engType.GetMethod("TryRecognize", BindingFlags.Public | BindingFlags.Static)!;

        // 样本：真机存图 + 几张语料
        var files = new List<(string label, string path)>();
        foreach (var f in new[] { "0002.png", "0003.png", "0005.png" })
        {
            string p = System.IO.Path.Combine(Dump, f);
            if (System.IO.File.Exists(p)) files.Add(("dump/" + f, p));
        }
        foreach (var f in new[] { @"实机\预览\13-Barony-加成面板-第1行.png",
                                  @"实机\预览\19-Barony-长描述-第4行.png" })
        {
            string p = System.IO.Path.Combine(Corpus, f);
            if (System.IO.File.Exists(p)) files.Add(("语料/" + System.IO.Path.GetFileName(f), p));
        }

        int totalSpace = 0;
        foreach (var (label, path) in files)
        {
            using var src = new Bitmap(path);
            object?[] a = new object?[] { src, 0.0, "", 0, 0, 0 };
            string txt;
            try { txt = (string?)tryM.Invoke(null, a) ?? ""; } catch (Exception ex) { Console.WriteLine($"{label} 失败 {ex.Message}"); continue; }
            int sp = 0;
            foreach (char c in txt) if (c == ' ') sp++;
            int alnum = 0;
            foreach (char c in txt) if (char.IsLetterOrDigit(c)) alnum++;
            totalSpace += sp;
            Console.WriteLine($"{label,-28} {src.Width}x{src.Height}  空格={sp,-3} 字母数字={alnum,-4} {Trunc(txt, 74)}");
        }
        Console.WriteLine($"--- 总空格数 = {totalSpace} ---");
    }

    private static string Trunc(string s, int n)
    {
        s = s.Replace("\n", " / ");
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    private static void CopyDir(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var f in System.IO.Directory.GetFiles(src)) System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
        foreach (var d in System.IO.Directory.GetDirectories(src)) CopyDir(d, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(d)));
    }
}

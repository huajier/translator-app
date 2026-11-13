using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CardProbe;

/// <summary>
/// 验证「宽高比大且高>=60 的多行图」上，1x 与 3x 谁读得更全。
/// 直接调产品的 RapidOcrEngine（反射），不用重写识别。
/// </summary>
public static class Probe4
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.8.1";
    private const string Corpus = @"D:\111Desktop\deepseek\翻译软件\tools\语料";

    public static void Run()
    {
        // 产品用 AppContext.BaseDirectory 找 models5。
        // 探针的 BaseDirectory 是探针自己的 bin，所以把当前目录切到发布目录，
        // 并且把 AppContext.BaseDirectory 不可能改 —— 只能改成「把模型目录拷过来」。
        string probeDir = AppContext.BaseDirectory;
        string modelSrc = System.IO.Path.Combine(DllDir, "models");
        string modelDst = System.IO.Path.Combine(probeDir, "models");
        if (System.IO.Directory.Exists(modelSrc) && !System.IO.Directory.Exists(modelDst))
        {
            CopyDir(modelSrc, modelDst);
            Console.WriteLine("已把模型拷到探针目录: " + modelDst);
        }

        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "TranslatorApp.dll"));
        var engType = asm.GetType("TranslatorApp.Core.Capture.RapidOcrEngine")!;
        var settingsType = asm.GetType("TranslatorApp.Core.Capture.AppSettings")!;

        settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        settingsType.GetProperty("RapidOcrPrimary")?.SetValue(null, true);
        // 确保小框重试逻辑按配置走
        try { settingsType.GetProperty("RapidOcrSmallBoxUpscale")?.SetValue(null, true); } catch { }

        Console.WriteLine("RapidOCR 可用 = " + engType.GetProperty("IsAvailable")?.GetValue(null));

        // TryRecognize 签名：Bitmap, out double, out string, out int, out int, out int
        var tryM = engType.GetMethod("TryRecognize", BindingFlags.Public | BindingFlags.Static);
        Console.WriteLine("TryRecognize = " + (tryM is null ? "❌" : "✅"));

        // 目标样本：刚统计出来的两张 + 用户那段（从截图无法取原图，用行-25/39 代表）
        var samples = new (string id, string path)[]
        {
            ("行-25", System.IO.Path.Combine(Corpus, @"实机\预览\25-MutantSpider-标题-整块.png")),
            ("行-39", System.IO.Path.Combine(Corpus, @"实机\预览\39-GalacticChallenges-挑战界面-黄条整块.png")),
        };

        foreach (var (id, path) in samples)
        {
            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine($"{id}: 文件不存在 {path}");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"=== {id} ===");
            foreach (int scale in new[] { 1, 3 })
            {
                using var src = new System.Drawing.Bitmap(path);
                System.Drawing.Bitmap input = src;
                if (scale > 1)
                {
                    int mw = Math.Max(src.Width, src.Height) * scale;
                    double f = mw > 2000 ? 2000.0 / Math.Max(src.Width, src.Height) : scale;
                    input = new System.Drawing.Bitmap(src,
                        (int)Math.Round(src.Width * f), (int)Math.Round(src.Height * f));
                    input.SetResolution(96, 96);
                }

                object?[] args = new object?[] { input, 0.0, "", 0, 0, 0 };
                object? result;
                try { result = tryM!.Invoke(null, args); }
                catch (Exception ex) { Console.WriteLine($"  {scale}x 调用失败: {ex.InnerException?.Message}"); input.Dispose(); continue; }

                string text = result as string ?? "";
                int chars = 0;
                foreach (char c in text) if (!char.IsWhiteSpace(c)) chars++;
                Console.WriteLine($"  {scale}x  {input.Width}×{input.Height}  分数 {(double)args[1]!:F2}  有效字符 {chars}");
                Console.WriteLine($"       {Trunc(text.Replace('\n', '/'), 88)}");

                if (!ReferenceEquals(input, src)) input.Dispose();
            }
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    private static void CopyDir(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var f in System.IO.Directory.GetFiles(src))
            System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
        foreach (var d in System.IO.Directory.GetDirectories(src))
            CopyDir(d, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(d)));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using Tesseract;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// Tesseract 识别引擎的临时探针（只为了先确认「它到底能不能跑、准不准」）。
/// 用法：TranslatorApp.exe --tess探测 "图片路径"
/// </summary>
internal static class TessProbe
{
    public static void Run(string imagePath)
    {
        AppLogger.Info("========== Tesseract 探针开始 ==========");

        try
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
            string tessdata = Path.Combine(exeDir, "tessdata");
            AppLogger.Info($"exe 目录：{exeDir}");
            AppLogger.Info($"语言包目录：{tessdata}（存在={Directory.Exists(tessdata)}）");

            // 关键：单文件打包下封装层找不到原生 DLL，必须我们先用绝对路径预加载
            bool preloaded = TranslatorApp.Core.Native.NativeLibraryPreloader.PreloadTesseractChain(exeDir);
            AppLogger.Info($"原生库预加载结果：{(preloaded ? "成功" : "失败（下面大概率会失败）")}");

            if (Directory.Exists(tessdata))
            {
                foreach (var f in Directory.GetFiles(tessdata))
                {
                    AppLogger.Info($"  语言包：{Path.GetFileName(f)}  {new FileInfo(f).Length / 1024} KB");
                }
            }

            using var engine = new TesseractEngine(tessdata, "chi_sim+eng");
            AppLogger.Info("Tesseract 引擎已初始化");

            using var bmp = new Bitmap(imagePath);
            AppLogger.Info($"图片尺寸：{bmp.Width}×{bmp.Height}");

            var watch = System.Diagnostics.Stopwatch.StartNew();

            // 【为什么走「编成 PNG 字节再喂给 Pix」这条路】
            //   这个封装在 net47/net48 上有个 PixConverter.ToPix(Bitmap) 很好用，
            //   但**netstandard2.0 版里没有**（本项目是 .NET 10，走的是 netstandard2.0）。
            //   剩下能用的公开入口就是 Pix.LoadFromMemory(byte[])——
            //   所以把位图编成 PNG 字节再交给它。
            //   顺带还有个好处：PNG 压缩过，传给 Tesseract 的体积小。
            byte[] png;
            using (var buffer = new System.IO.MemoryStream())
            {
                bmp.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
                png = buffer.ToArray();
            }

            using var pix = Pix.LoadFromMemory(png);
            using var page = engine.Process(pix);
            string text = page.GetText();
            watch.Stop();

            AppLogger.Info($"识别耗时：{watch.ElapsedMilliseconds} 毫秒");
            AppLogger.Info($"【Tesseract 结果】{text.Replace("\n", " / ").Trim()}");
            AppLogger.Info($"整体置信度：{page.GetMeanConfidence():F1}");

            // 逐词文本：用来肉眼对比哪个引擎认得更像样。
            // 注意 Next() 必须传层级参数，这个封装的 Next 不是无参的。
            using var iterator = page.GetIterator();
            iterator.Begin();
            var words = new List<string>();
            do
            {
                if (iterator.GetText(PageIteratorLevel.Word) is { Length: > 0 } w)
                {
                    words.Add(w.Trim());
                }
            } while (iterator.Next(PageIteratorLevel.Word));

            AppLogger.Info($"【逐词】{string.Join(" ", words)}");
        }
        catch (Exception ex)
        {
            // 【为什么要把内层异常一层层剥出来】
            //   原生库加载失败时，InteropDotNet 会抛 TargetInvocationException，
            //   真正的原因（缺哪个 DLL、什么错误码）藏在最内层。
            //   只打印最外层的话，日志里只有一句没用的
            //   「Exception has been thrown by the target of an invocation」。
            AppLogger.Error(ex, $"Tesseract 探针失败（第 {Depth(ex)} 层）");

            int level = 0;
            for (Exception? inner = ex; inner is not null; inner = inner.InnerException, level++)
            {
                AppLogger.Info($"  内层异常 [{level}]：{inner.GetType().FullName}");
                AppLogger.Info($"      消息：{inner.Message}");
                if (inner is global::System.DllNotFoundException or global::System.BadImageFormatException)
                {
                    AppLogger.Info("      ↑ 这是「原生 DLL 没找到」或「位数不对」——" +
                                   "检查 x64\\ 下的 tesseract50.dll 和 leptonica-1.82.0.dll 有没有随 exe 一起发布");
                }
                if (inner is System.Runtime.InteropServices.COMException)
                {
                    AppLogger.Info($"      ↑ HRESULT：0x{((System.Runtime.InteropServices.COMException)inner).HResult:X8}");
                }
            }
        }

        AppLogger.Info("========== Tesseract 探针结束 ==========");
    }

    /// <summary>数一数异常套了几层（用来在日志里标明这是第几层）。</summary>
    private static int Depth(Exception ex)
    {
        int n = 1;
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException) n++;
        return n;
    }
}
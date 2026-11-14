using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;

namespace CardProbe;

public static class Probe10
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.8.1";
    private const string Dump = @"D:\111Desktop\deepseek\翻译软件\tools\临时\ocr-dump";

    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, a) =>
        {
            string n = new AssemblyName(a.Name).Name + ".dll";
            string p = System.IO.Path.Combine(DllDir, n);
            return System.IO.File.Exists(p) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(p) : null;
        };

        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "RapidOcrNet.dll"));
        var optType = asm.GetType("RapidOcrNet.RapidOcrOptions")!;
        var ocrType = asm.GetType("RapidOcrNet.RapidOcr")!;
        var skiaAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            System.IO.Path.Combine(DllDir, "SkiaSharp.dll"));

        string mdlDir = System.IO.Path.Combine(DllDir, "models", "v5");
        string det = System.IO.Path.Combine(mdlDir, "ch_PP-OCRv5_mobile_det.onnx");
        string cls = System.IO.Path.Combine(mdlDir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        string rec = System.IO.Path.Combine(mdlDir, "en_PP-OCRv5_rec_mobile.onnx");
        string keys = System.IO.Path.Combine(mdlDir, "ppocrv5_en_dict.txt");

        // 用位图转 SKBitmap
        string png = System.IO.Path.Combine(Dump, "0002.png");
        if (!System.IO.File.Exists(png)) { Console.WriteLine("缺图: " + png); return; }
        using var bmp = new Bitmap(png);
        using var msIo = new System.IO.MemoryStream();
        bmp.Save(msIo, System.Drawing.Imaging.ImageFormat.Png);
        msIo.Position = 0;
        var skBitmapType = skiaAsm.GetType("SkiaSharp.SKBitmap")!;
        var decode = skBitmapType.GetMethod("Decode", new[] { typeof(System.IO.Stream) })!;
        object skBmp = decode.Invoke(null, new object[] { msIo })!;

        var ocr = Activator.CreateInstance(ocrType)!;
        // 5 参数重载: (det, cls, rec, keys, SessionOptions)
        var sessOptType = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            System.IO.Path.Combine(DllDir, "Microsoft.ML.OnnxRuntime.dll"))
            .GetType("Microsoft.ML.OnnxRuntime.SessionOptions")!;
        object sessOpt = Activator.CreateInstance(sessOptType)!;
        var initM = ocrType.GetMethod("InitModels",
            new[] { typeof(string), typeof(string), typeof(string), typeof(string), sessOptType })!;
        initM.Invoke(ocr, new[] { (object)det, cls, rec, keys, sessOpt });

        var defOpt = optType.GetProperty("Default")!.GetValue(null)!;
        foreach (bool wordBox in new[] { false, true })
        {
            var opt = defOpt;
            optType.GetProperty("ReturnWordBox")!.SetValue(opt, wordBox);
            Console.WriteLine();
            Console.WriteLine($"===== ReturnWordBox = {wordBox} =====");
            var detM = ocrType.GetMethod("Detect", new[] { skBitmapType, optType })!;
            object? res = detM.Invoke(ocr, new[] { skBmp, opt });
            if (res is null) { Console.WriteLine("检测返回 null"); continue; }

            var rt = res.GetType();
            var blocksProp = rt.GetProperty("TextBlocks");
            Console.WriteLine("  OcrResult 属性：" + string.Join(", ",
                Array.ConvertAll(rt.GetProperties(), x => x.Name)));

            var blocks = blocksProp?.GetValue(res) as Array;
            Console.WriteLine($"  TextBlocks 数量 = {blocks?.Length ?? 0}");
            if (blocks is not null && blocks.Length > 0)
            {
                var b0 = blocks.GetValue(0)!;
                var bt = b0.GetType();
                Console.WriteLine("  单块属性：" + string.Join(", ",
                    Array.ConvertAll(bt.GetProperties(), x => x.Name)));
                foreach (var nm in new[] { "Text", "WordResults", "Words", "CharScores" })
                {
                    var pp = bt.GetProperty(nm);
                    if (pp is null) continue;
                    var v = pp.GetValue(b0);
                    Console.WriteLine($"    {nm} = {(v is null ? "null" : Trunc(v.ToString(), 110))}");
                }
            }
        }
    }

    private static string Trunc(string s, int n)
    {
        s = s.Replace("\n", " / ");
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}

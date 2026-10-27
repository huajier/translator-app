using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using RapidOcrNet;
using SkiaSharp;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// RapidOCR 认字引擎（PP-OCRv5，ONNX Runtime + SkiaSharp，纯离线纯 C#）。
///
/// 【2026-10-27 阶段三 S3 接入：外文主通道】
///   选型过程与对照数据见 tools\日志\阶段3-S3-第三引擎选型.md。
///   一句话：旧引擎（Tesseract 5.5.3）在这套游戏像素字体上到顶了
///   （L_行 50.7%），换这个引擎裸跑是 63.8%，且「整行丢」从 64% 掉到 6%。
///
/// 【它自带预处理，所以吃原图】
///   DBNet 检测 + 角度分类 + CTC 识别都在内部做，**不需要外部放大/二值化**。
///   旧通道（Windows OCR / Tesseract）的预处理一个字没动，那条路照样用它自己的。
///
/// 【模型路径】
///   包会把 models\v5\ 拷到 exe 旁边（build\RapidOcrNet.targets 干的）。
///   这里**用绝对路径**按 exe 所在目录拼，不依赖工作目录 ——
///   双击启动和从别处调用，工作目录不一样，相对路径会找不到模型。
///
/// 【失败即沉默】
///   模型缺失、初始化抛异常、识别抛异常 → 一律返回 null 并写一行日志，
///   由调用方回退旧通道。**绝不因为新引擎挂了就不认字。**
/// </summary>
internal static class RapidOcrEngine
{
    private static readonly object Gate = new();
    private static RapidOcr? _ocr;
    private static bool _tried;

    /// <summary>识别要串行：一个引擎实例带三份 ONNX 会话，并发调用没有意义也不保证安全。</summary>
    private static readonly object RunGate = new();

    /// <summary>模型目录（跟着 exe 走）。</summary>
    private static string ModelDir => Path.Combine(AppContext.BaseDirectory, "models", "v5");

    /// <summary>引擎是否可用（首次访问会初始化，约 0.3 秒，之后复用）。</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _ocr is not null;
        }
    }

    private static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_tried) return;
            _tried = true;

            string dir = ModelDir;
            string det = Path.Combine(dir, "ch_PP-OCRv5_mobile_det.onnx");
            string cls = Path.Combine(dir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

            // 识别模型按设置挑：en（英文专用，默认）/ latin（包自带）。
            // 只换 rec + dict，det/cls 不变；缺文件就退回 latin，不让主通道整个失效。
            string rec, keys, recName;
            if (AppSettings.RapidOcrRec == "latin")
            {
                rec = Path.Combine(dir, "latin_PP-OCRv5_rec_mobile_infer.onnx");
                keys = Path.Combine(dir, "ppocrv5_latin_dict.txt");
                recName = "latin";
            }
            else
            {
                rec = Path.Combine(dir, "en_PP-OCRv5_rec_mobile.onnx");
                keys = Path.Combine(dir, "ppocrv5_en_dict.txt");
                recName = "en";
                if (!File.Exists(rec) || !File.Exists(keys))
                {
                    AppLogger.Warn($"英文识别模型缺失（{dir}），改用包自带 latin 模型");
                    rec = Path.Combine(dir, "latin_PP-OCRv5_rec_mobile_infer.onnx");
                    keys = Path.Combine(dir, "ppocrv5_latin_dict.txt");
                    recName = "latin（en 缺文件回退）";
                }
            }

            if (!File.Exists(det) || !File.Exists(rec) || !File.Exists(keys))
            {
                AppLogger.Warn($"RapidOCR 模型文件缺失（{dir}），外文继续走旧通道");
                return;
            }

            try
            {
                var start = Stopwatch.StartNew();
                var ocr = new RapidOcr();
                ocr.InitModels(det, cls, rec, keys);
                _ocr = ocr;
                AppLogger.Info($"RapidOCR 引擎已就绪：PP-OCRv5 {recName}，耗时 {start.ElapsedMilliseconds} 毫秒（之后复用，不再重建）");
            }
            catch (Exception ex)
            {
                _ocr = null;
                AppLogger.Warn($"RapidOCR 初始化失败（外文继续走旧通道）：{ex.GetType().Name} {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 认一张图。成功返回按行拼好的文本（行间用 ⏎ 即 U+23CE，和旧通道一致），
    /// 失败返回 null，原因写进 <paramref name="note"/>。
    /// </summary>
    /// <param name="averageScore">各检测行的平均置信度（0~1），调用方拿它决定信不信。</param>
    public static string? TryRecognize(Bitmap bitmap, out double averageScore, out string note)
    {
        averageScore = 0;
        note = string.Empty;

        if (!IsAvailable)
        {
            note = "引擎不可用";
            return null;
        }

        try
        {
            // GDI 位图 → Skia 位图：走一次内存 PNG 编码（裁样都只有几十 KB，1 毫秒量级），
            // 比手写 LockBits 逐像素搬运短得多，也不用碰指针。
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            using var skBitmap = SKBitmap.Decode(ms);
            if (skBitmap is null)
            {
                note = "图片解码失败";
                return null;
            }

            OcrResult? result;
            lock (RunGate)
            {
                result = _ocr!.Detect(skBitmap, RapidOcrOptions.Default);
            }

            var blocks = result?.TextBlocks;
            if (blocks is null || blocks.Length == 0)
            {
                note = "没检测到文字行";
                return null;
            }

            var sb = new StringBuilder();
            var scores = new System.Collections.Generic.List<double>(blocks.Length);
            foreach (var block in blocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                if (sb.Length > 0) sb.Append('\u23CE');
                sb.Append(block.Text);
                if (block.CharScores is { Length: > 0 } chars)
                {
                    scores.Add(chars.Average(c => (double)c));
                }
            }

            if (sb.Length == 0)
            {
                note = "检测到行但没有文字";
                return null;
            }

            averageScore = scores.Count > 0 ? scores.Average() : 0;
            return sb.ToString();
        }
        catch (Exception ex)
        {
            note = $"{ex.GetType().Name} {ex.Message}";
            AppLogger.Warn($"RapidOCR 识别异常（回退旧通道）：{note}");
            return null;
        }
    }

    /// <summary>程序退出时释放 ONNX 会话。</summary>
    public static void Dispose()
    {
        lock (Gate)
        {
            try { _ocr?.Dispose(); } catch { /* 退出路径，忽略 */ }
            _ocr = null;
            _tried = false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 图库回放：把图库里的一批图，逐张喂进**真机那条流水线**，把认出来的文字收集起来。
///
/// 【为什么要它】
///   「这次改完有没有退步」这个问题，靠人手框选回答不了：
///   每框一次，框的松紧、位置、像素都对不齐，比出来的差异分不清是代码变了还是框变了。
///   图库里存的是**同一批图**，同一批图前后各跑一遍，差异就只剩代码。
///
/// 【怎么保证测的是真机】
///   不另写识别代码，而是给 `OcrTextCapture` 塞一张图（<see cref="OcrTextCapture.ReplaySource"/>），
///   让它跳过「截屏」这一步，后面全照原样走。
///
/// 【怎么用】
///   `TranslatorApp.exe --回放 "清单.txt" "结果.txt"`
///   清单每行：`2-E4|D:\...\1.png`
///   结果每行：`2-E4|认出来的文字`
/// </summary>
internal static class ReplayRunner
{
    public static async Task RunAsync(string listPath, string outPath)
    {
        AppLogger.Info($"========== 图库回放开始：{listPath} ==========");

        if (!File.Exists(listPath))
        {
            AppLogger.Warn($"回放：找不到清单 {listPath}");
            return;
        }

        string[] lines = File.ReadAllLines(listPath, Encoding.UTF8);
        var results = new List<string>();
        int ok = 0, failed = 0;

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            int bar = line.IndexOf('|');
            if (bar <= 0) continue;

            string id = line.Substring(0, bar).Trim();
            string path = line.Substring(bar + 1).Trim();

            if (!File.Exists(path))
            {
                AppLogger.Warn($"回放 {id}：找不到图 {path}");
                results.Add($"{id}|<缺图>");
                failed++;
                continue;
            }

            try
            {
                using var bitmap = new Bitmap(path);
                OcrTextCapture.ReplaySource = bitmap;

                var rect = new NativeMethods.RECT
                {
                    Left = 0,
                    Top = 0,
                    Right = bitmap.Width,
                    Bottom = bitmap.Height
                };

                var request = new CaptureRequest
                {
                    PhysicalRect = rect,
                    DipRect = (0, 0, bitmap.Width, bitmap.Height),
                    MonitorRect = rect,
                    MonitorDpi = 96,
                    TriggerMode = "图库回放",
                    SourceProcess = string.Empty
                };

                CaptureResult result = await CaptureDispatcher.Shared
                    .CaptureAsync(request, CancellationToken.None)
                    .ConfigureAwait(true);

                // 换行必须折成 ⏎ 再落盘：结果文件是「一行一段」，
                // 直接把带换行的文字写进去会被拆成好几行，比对那边只读到第一行，
                // 看着像「只认出了第一行」，其实全认出来了（这个坑踩过）。
                // 用 ⏎ 是因为 比对成绩.ps1 的 Norm() 本来就认这个标记。
                string oneLine = result.Text
                    .Replace("\r\n", "⏎")
                    .Replace('\n', '⏎')
                    .Replace('\r', '⏎');
                results.Add($"{id}|{oneLine}");
                if (result.Success) ok++; else failed++;
                AppLogger.Info($"回放 {id}：{(result.Success ? "成功" : "失败")}，{result.Text.Length} 字");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"回放 {id} 失败");
                results.Add($"{id}|<异常>");
                failed++;
            }
            finally
            {
                OcrTextCapture.ReplaySource = null;
            }
        }

        File.WriteAllLines(outPath, results, new UTF8Encoding(false));
        AppLogger.Info($"========== 图库回放结束：成功 {ok}，失败 {failed}，共 {results.Count} 段 → {outPath} ==========");
    }
}

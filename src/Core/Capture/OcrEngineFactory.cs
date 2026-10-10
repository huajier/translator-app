using System;
using System.Collections.Generic;
using System.Linq;
using TranslatorApp.Core.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// OCR 引擎的创建与复用。
///
/// 【本类存在的唯一理由：性能】
///   OCR 引擎**必须初始化一次然后一直复用**。
///   每次重新创建会慢 0.5~2 秒——这是「按一下快捷键、等半天才出结果」的头号原因。
///   所以引擎实例缓存在这里，只在第一次用到（或语言变了）时创建。
///
/// 【语言包检测】
///   先查 <see cref="OcrEngine.AvailableRecognizerLanguages"/>，
///   缺中英文就提前给出安装引导，**不要等抛「不支持的语言」异常**。
///
/// 【线程】
///   WinRT 的 OcrEngine 可以在任意线程调用，但识别本身是异步的；
///   调用方负责把它 await 掉，不阻塞 UI 线程。
/// </summary>
internal static class OcrEngineFactory
{
    /// <summary>缓存下来的引擎实例与它对应的语言。</summary>
    private static OcrEngine? _cached;
    private static string _cachedLanguageTag = string.Empty;
    private static readonly object Gate = new();

    /// <summary>系统支持的中英文语言标签，按优先级排列。</summary>
    private static readonly string[] PreferredLanguages = { "zh-Hans", "zh-Hans-CN", "en-US" };

    /// <summary>系统实际装了哪些识别语言（懒加载一次）。</summary>
    private static IReadOnlyList<string>? _installed;

    /// <summary>系统装了哪些 OCR 识别语言（语言标签列表）。</summary>
    public static IReadOnlyList<string> InstalledLanguages
    {
        get
        {
            if (_installed is null)
            {
                try
                {
                    _installed = OcrEngine.AvailableRecognizerLanguages
                        .Select(l => l.LanguageTag)
                        .ToList();
                    AppLogger.Info($"系统 OCR 支持的语言：{string.Join("、", _installed)}");
                }
                catch (Exception ex)
                {
                    _installed = Array.Empty<string>();
                    AppLogger.Warn($"读取 OCR 支持语言失败：{ex.GetType().Name} {ex.Message}");
                }
            }
            return _installed;
        }
    }

    /// <summary>中英文是否都齐了（齐了才敢按「混合」策略工作）。</summary>
    public static bool HasChinese => InstalledLanguages.Any(t => t.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

    /// <summary>英文是否可用。</summary>
    public static bool HasEnglish => InstalledLanguages.Any(t => t.StartsWith("en", StringComparison.OrdinalIgnoreCase));

    /// <summary>OCR 整体能不能用。</summary>
    public static bool IsAvailable => InstalledLanguages.Count > 0;

    /// <summary>
    /// 拿到一个可用的识别引擎（带缓存）。拿不到返回 false 并写日志。
    /// </summary>
    /// <param name="preferChinese">
    ///   **只表示「偏好」，不表示「排除」**——两种引擎最后都只能拿一个。
    ///
    ///   【踩过的坑 · 2026-10-08 实测】
    ///     最早这里是「中文优先」和「英文优先」两套候选顺序，
    ///     粗判函数又总是返回 true，于是取词时有时会用 **en-US 引擎去认中文**，
    ///     结果整段中文一个都没认出来（实测：中文占比 0 %）。
    ///
    ///     **结论：中文引擎对英文照样能认**，没必要为英文单独换一个引擎。
    ///     统一「有中文引擎就用中文引擎」，行为可预期，也不会在两套引擎之间来回切。
    ///     真正需要按语种分流的只有「要不要放大」——那在 OcrTextCapture 里判断。
    /// </param>
    public static bool TryGetEngine(bool preferChinese, out OcrEngine? engine)
    {
        engine = null;

        // 【2026-10-09 修正：原来那句注释是错的，写在这里的是实测结论】
        //
        //   原来写的是：「中文引擎能认英文，反之不成立，所以中文永远排前面。」
        //   **这句话从来没被验证过，而且被实测推翻了。**
        //
        //   实测（同一批英文内容，同一台机器）：
        //     Windows 中文引擎 → **58.9 分**
        //     Windows 英文引擎 → **100 分**（多段逐字正确）
        //     Tesseract        → **94.7 分**
        //
        //   也就是说：**中文引擎认英文是「能认个大概，但错得没法用」**，
        //   不是「反正都能认」。拿它当默认引擎，英文就会被它毁掉。
        //
        //   至于 `ru-RU`：本机**根本没装**俄文语言包
        //   （TryCreateFromLanguage 返回 null），
        //   所以俄文只能交给 Tesseract，这也正是它在本项目里的定位。
        //
        //   preferChinese 保留只是为了让调用处的意图写清楚，不再影响顺序。
        _ = preferChinese;
        var candidates = new[] { "zh-Hans", "zh-Hans-CN", "en-US" };

        string? chosen = candidates.FirstOrDefault(InstalledLanguages.Contains);

        // 语言包在系统上的标签可能是 zh-Hans-CN 而不是 zh-Hans，做一次前缀匹配兜底
        chosen ??= InstalledLanguages.FirstOrDefault(t =>
            candidates.Any(c => t.StartsWith(c, StringComparison.OrdinalIgnoreCase)));

        if (chosen is null)
        {
            AppLogger.Warn("OCR 不可用：系统没有装任何识别语言（需安装「光学字符识别」可选功能）");
            return false;
        }

        lock (Gate)
        {
            if (_cached is not null && _cachedLanguageTag == chosen)
            {
                engine = _cached;
                return true;
            }

            try
            {
                var start = System.Diagnostics.Stopwatch.StartNew();
                var language = new Language(chosen);
                _cached = OcrEngine.TryCreateFromLanguage(language);
                _cachedLanguageTag = chosen;
                engine = _cached;

                if (_cached is null)
                {
                    AppLogger.Warn($"OCR 引擎创建失败（语言 {chosen}）");
                    return false;
                }

                AppLogger.Info($"OCR 引擎已就绪：语言={chosen}，耗时 {start.ElapsedMilliseconds} 毫秒（之后复用，不再重建）");
                return true;
            }
            catch (Exception ex)
            {
                _cached = null;
                _cachedLanguageTag = string.Empty;
                AppLogger.Warn($"OCR 引擎创建异常：{ex.GetType().Name} {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// WinRT 需要的 <see cref="SoftwareBitmap"/> 上限（引擎超过会直接抛异常）。
    /// 截出来的图超出它就先按比例缩小。
    /// </summary>
    public static int MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    /// <summary>
    /// 丢弃缓存的引擎（程序退出时调用）。
    ///
    /// 【注意】WinRT 的 OcrEngine 没有 Dispose，也不是 IDisposable，
    /// 直接丢掉引用即可；它是 RCW，GC 会回收底层的 COM 引用。
    /// </summary>
    public static void Dispose()
    {
        lock (Gate)
        {
            _cached = null;
            _cachedLanguageTag = string.Empty;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Capture;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Translate;

/// <summary>
/// 翻译总入口：开关 → 要不要翻 → 缓存 → 2.5 秒硬闸 → 交给提供方。
///
/// 【开关关着时一定什么也不做】
///   认字是离线的。开关是 false 的时候，这个类**一个网络请求都不发**，
///   连提供方都不构造（构造会建 HttpClient）。验收 S4 就是查这一点。
///
/// 【缓存 = 这个类里一个 Dictionary，不落盘、不做 TTL（第八轮 §7-3）】
///   为什么砍掉方案原文里的 TranslationCache.cs：
///     · 落盘要处理并发写、损坏恢复、版本迁移、隐私（把用户框到的文字写进硬盘）；
///     · 用户一次使用最多翻几十条，进程一退缓存本来就该没；
///     · 我们要的是「同一块连框两次不重复发网」，内存表完全够。
///   200 条上限 + 先进先出淘汰：一个字典 + 一个队列，不值得单开一个文件。
///   键 = 规范化后的原文 + "|" + 目标语言（方案 §3、Q2=a）：
///     不含坐标/通道/进程名（同一段字在哪儿框出来都该命中），
///     **必须含目标语言**（否则换语言会命中错译文）。
///
/// 【超时硬闸 2500ms 在这里，不在提供方】
///   提供方只管发请求和分类错误；预算只有一个地方说了算，
///   两处都设超时会互相打架，先到的那个先断，日志里就分不清是谁打断的。
/// </summary>
internal sealed class TranslationService
{
    /// <summary>单次翻译的硬闸（用户 2026-11-02 拍板：常态 1500ms，硬闸 2500ms）。</summary>
    public const int HardTimeoutMs = 2500;

    /// <summary>
    /// 取消信号到真正抛出的余量。
    /// </summary>
    /// <remarks>
    /// 取消不是瞬时的：正在做 TCP connect 的 socket，`CancelAfter` 触发后要等内核
    /// 把连接尝试收回来才能抛 OperationCanceledException，实测 24~60ms（S3 三轮：
    /// max 2573 / 2524，每次都恰好多出几十毫秒）。所以预算要先把这段留出来，
    /// 否则「硬闸 2500」在纸面上成立、在秒表上是 2524。
    /// </remarks>
    public const int CancelOverheadMs = 120;

    /// <summary>缓存上限。够了，见类注释。</summary>
    private const int CacheCapacity = 200;

    private static readonly Lazy<TranslationService> LazyShared = new(() => new TranslationService());

    public static TranslationService Shared => LazyShared.Value;

    private readonly object _gate = new();
    private readonly Dictionary<string, TranslationResult> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>提供方按当前 settings 里那个 endpoint 懒建；endpoint 变了就重建。</summary>
    private ITranslationProvider? _provider;
    private string _providerEndpoint = "\u0000";

    /// <summary>
    /// 诊断用：缓存命中次数 / 未命中次数，写进日志用（验收 S5 靠它自证）。
    /// 不参与任何逻辑判断。
    /// </summary>
    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }

    private TranslationService() { }

    /// <summary>
    /// 翻一段文字。**永远不抛异常**：任何失败都变成带 Failure 的结果返回，
    /// 调用方只需要「有译文就显示，没有就只显示原文」。
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(string? text, CancellationToken ct)
        => await TranslateAsync(text, HardTimeoutMs, ct).ConfigureAwait(false);

    /// <summary>
    /// 同上，但允许调用方把**端到端剩多少毫秒**交进来。
    /// </summary>
    /// <remarks>
    /// 为什么要这个重载（S3 实测出来的）：本机连一个「拒绝连接」的回环端口，
    /// 单次 TCP 连接就要 2060 ms（实测 2337/2061/2056/2038）。原来 2500ms 硬闸
    /// 是**从翻译入口起算**的，认字已经花掉的那几百毫秒不在闸内，于是
    /// 「翻译没超闸、用户却等了 2930ms」。硬闸要管的是用户等多久，不是某一层跑多久。
    /// </remarks>
    public async Task<TranslationResult> TranslateAsync(string? text, int budgetMs, CancellationToken ct)
    {
        if (!AppSettings.TranslateEnabled)
            return new TranslationResult { Failure = TranslationFailure.Disabled };

        if (string.IsNullOrWhiteSpace(text))
            return new TranslationResult { Failure = TranslationFailure.Disabled };

        // 母语不翻 —— 但**母语指的是目标语言，不是中文**。
        // 用户原话：「让用户选择一种语言，然后除了这种语言外识别出来的所有都翻译」。
        // 所以只有目标语言是中文时，「本地判成中文就跳过」才成立。
        //
        // 目标语言不是中文时，本地判不出来（LanguageDetector 只有中文/俄文/拉丁三档，
        // 判不出英法德日），一律交给服务端自动检测 —— 宁可发出去让 Bing 判，
        // 也不要本地猜着跳过。这条以前硬编码 Chinese，导致目标语言选成英文后
        // 中文原文永远不翻，是功能死锁，不是边缘情况。
        //
        // 已知代价（明确接受）：目标语言=英文、原文本来就是英文时，会白发一次请求拿回原文。
        // 多烧一次额度、多花几百毫秒，换「中文原文能翻成英文」。**能翻比省一次额度重要。**
        string target = AppSettings.TranslateTargetLang;
        if (IsChineseTarget(target) && LanguageDetector.Detect(text) == LanguageDetector.Chinese)
            return new TranslationResult { Failure = TranslationFailure.Disabled };

        string key = Normalize(text) + "|" + target;

        // 命中的代价要单独量：hit.ElapsedMs 是**当初那次网络请求**的耗时，
        // 拿它当「这次命中有多快」会自欺（800ms 的旧记录看着像这次很慢）。
        // 验收 S5 要的是「第二次拿到结果花了多久」，所以这里现测。
        var hitSw = System.Diagnostics.Stopwatch.StartNew();
        if (TryGetCached(key, out TranslationResult? hit))
        {
            hitSw.Stop();
            CacheHits++;
            AppLogger.Info($"翻译：命中缓存（{hit!.Text.Length} 字，本次命中 {hitSw.ElapsedMilliseconds} ms，" +
                           $"累计命中 {CacheHits} 次；当初联网那次 {hit.ElapsedMs} ms）");
            return hit;
        }
        hitSw.Stop();

        CacheMisses++;

        ITranslationProvider provider = GetProvider();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // budgetMs 是「端到端还剩多少」：认字已经花掉的秒数不在这里重复给。
        // 再扣掉 CancelOverheadMs —— 取消信号落地要时间，那段时间用户也在等。
        // 剩 0 或负数 = 预算已经用完，**立刻收手显原文**，别再拖。
        cts.CancelAfter(Math.Max(budgetMs - CancelOverheadMs, 0));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        TranslationResult result;
        try
        {
            result = await provider.TranslateAsync(text, target, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            result = new TranslationResult
            {
                Failure = TranslationFailure.Timeout,
                // 不写死 2500：断网那条 400ms 就超了，报「超过 2500ms 硬闸」
                // 会让人以为翻译真跑满了 2.5 秒。只报实际发生的事。
                Detail = $"超过硬闸（这段只剩 {budgetMs} ms 预算）",
                ElapsedMs = sw.ElapsedMilliseconds,
                Provider = provider.Name,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            result = new TranslationResult
            {
                Failure = TranslationFailure.Unknown,
                Detail = $"{ex.GetType().Name} {ex.Message}",
                ElapsedMs = sw.ElapsedMilliseconds,
                Provider = provider.Name,
            };
        }

        sw.Stop();
        long total = sw.ElapsedMilliseconds;

        if (result.Success)
        {
            // Empty 是失败，但成功分支里 Text 一定非空；这里只缓存真译文。
            result = Clone(result, total);
            Put(key, result);
            AppLogger.Info($"翻译：成功，{text.Length} 字 → 译 {result.Text.Length} 字，" +
                           $"用时 {total} ms（{provider.Name}，检测到源语言「{result.DetectedLanguage}」）");
        }
        else
        {
            result = Clone(result, total);
            AppLogger.Warn($"翻译：放弃，{result.Failure.ToUserMessage()}（{result.Detail}，用时 {total} ms）");
        }

        return result;
    }

    /// <summary>清缓存（诊断用，不对外暴露权重）。</summary>
    internal void ClearCache()
    {
        lock (_gate)
        {
            _cache.Clear();
            _order.Clear();
        }
    }

    /// <summary>
    /// 目标语言是不是中文（含 zh-Hans / zh-Hant / zh / zh-CN 等）。
    /// 只用来决定「本地中文判据要不要生效」：目标语言是中文时，中文原文不用翻；
    /// 目标语言是别的语言时，中文原文**必须翻**，本地判据一律不生效。
    /// </summary>
    private static bool IsChineseTarget(string to)
        => to.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 规范化：去掉首尾空白、把连续空白（含换行）压成一个空格。
    /// 目的只有一个——同一段字换个换行位置还能命中同一条缓存。
    /// **不改变任何可见字符**，译文本身不受影响。
    /// </summary>
    private static string Normalize(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (char c in text.Trim())
        {
            bool isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (lastWasSpace) continue;
                sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }

    private bool TryGetCached(string key, out TranslationResult? result)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out TranslationResult? found))
            {
                result = Clone(found, found.ElapsedMs, fromCache: true);
                return true;
            }
        }
        result = null;
        return false;
    }

    private void Put(string key, TranslationResult value)
    {
        lock (_gate)
        {
            if (_cache.ContainsKey(key)) return;
            _cache[key] = value;
            _order.Enqueue(key);
            while (_order.Count > CacheCapacity)
            {
                string evicted = _order.Dequeue();
                _cache.Remove(evicted);
            }
        }
    }

    private static TranslationResult Clone(TranslationResult r, long elapsedMs, bool fromCache = false) => new()
    {
        Text = r.Text,
        Provider = r.Provider,
        Failure = r.Failure,
        Detail = r.Detail,
        DetectedLanguage = r.DetectedLanguage,
        FromCache = fromCache || r.FromCache,
        ElapsedMs = elapsedMs,
    };

    /// <summary>按当前 settings 的 endpoint 拿提供方；endpoint 改了会自动换新的。</summary>
    private ITranslationProvider GetProvider()
    {
        string endpoint = AppSettings.TranslateEndpoint;
        lock (_gate)
        {
            if (_provider is not null && _providerEndpoint == endpoint) return _provider;
            _provider = new BingFreeProvider(endpoint);
            _providerEndpoint = endpoint;
            _cache.Clear();
            _order.Clear();
            AppLogger.Info($"翻译：提供方 = {_provider.Name}，endpoint = " +
                           $"{(endpoint.Length == 0 ? BingFreeProvider.DefaultBaseUrl : endpoint)}（换 endpoint 会清缓存）");
            return _provider;
        }
    }
}

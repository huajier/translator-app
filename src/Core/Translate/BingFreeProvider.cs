using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Translate;

/// <summary>
/// 走 Bing 网页翻译的免密钥通道（2026-11-02 阶段四 T3a）。
///
/// 【协议是**抓来的**，不是照 Azure 猜的】
///   全部来自 tools\脚本\探针-免密钥通道-2.ps1 在 2026-11-02 的原始报文
///   （落在 %TEMP%\t3_spike2.txt）。改这里之前先把那份文件调出来对一遍。
///
///   ① GET https://cn.bing.com/translator  → 200 / 405ms / 630932 字节
///      页面里取三样：
///        IG            : 32 位十六进制
///        IID           : data-iid="translator.5023"
///        params_AbusePreventionHelper = [1793589918800,"HSlKWjgg...",3600000]
///        —— **params[0] 是时间戳（作 key），params[1] 才是 token。搞反回 statusCode:400。**
///
///   ② POST https://cn.bing.com/ttranslatev3?isVertical=1&&IG=..&IID=..
///      Content-Type: application/x-www-form-urlencoded
///      body: fromLang=auto-detect&to=<lang>&text=<text>&token=<params[1]>&key=<params[0]>
///      —— 字段名是 **fromLang**，不是 from。第一版写 from= 回 statusCode:400（踩过）。
///
///   ③ 成功响应（200）：结构和 Azure 一致
///      [{"translations":[{"text":"你好","to":"zh-Hans","transliteration":{...}}],
///        "usedLLM":true,"detectedLanguage":{"language":"en"}}]
///
///   ④ 失败**也是 HTTP 200**，错误藏在 body 里（这是这条通道最大的坑）：
///      {"statusCode":400,"errorMessage":""}   ← 目标语言无效 / 文字为空 / IG 置空
///      {"statusCode":205,"errorMessage":""}   ← token 或 key 不认
///      所以：**HTTP 状态和 body.statusCode 都要判**，只看 HTTP 会全部当成功。
///
/// 【token 只活 3600000ms，且绝不落盘】
///   提前 60 秒刷新；收到 205 立刻强制刷新并重试一次，仍 205 才报 AuthFailed。
///   不落盘的原因：这是临时凭证，写进磁盘等于多一个会过期、会残留、会被翻出来的东西。
///
/// 【这条通道的已知风险（第八轮 §4，如实记）】
///   · 非官方接口，没有写进公开文档，微软随时可能改或关；
///   · 按来源 IP 限流，用户多了会成片失效；
///   · 没有 SLA。
///   所以留了 <see cref="ITranslationProvider"/> 接口，换家只写一个类。
/// </summary>
internal sealed class BingFreeProvider : ITranslationProvider
{
    /// <summary>真 Bing 的根地址。留空 translateEndpoint 时用它。</summary>
    public const string DefaultBaseUrl = "https://cn.bing.com";

    /// <summary>token 名义有效期 3600000ms，提前 60 秒换新的。</summary>
    private const long TokenTtlMs = 3_600_000;
    private const long TokenRefreshAheadMs = 60_000;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";

    private static readonly Regex IgPattern =
        new(@"IG\s*:\s*""([0-9A-Fa-f]{32})""", RegexOptions.Compiled);

    private static readonly Regex IidPattern =
        new(@"data-iid=""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex ParamsPattern =
        new(@"params_AbusePreventionHelper\s*=\s*(\[[^\]]*\])", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private readonly object _authGate = new();
    private string _token = string.Empty;
    private string _tokenKey = string.Empty;
    private string _ig = string.Empty;
    private string _iid = string.Empty;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;

    public BingFreeProvider(string? endpoint)
    {
        string baseUrl = string.IsNullOrWhiteSpace(endpoint) ? DefaultBaseUrl : endpoint.Trim().TrimEnd('/');
        _baseUrl = baseUrl;

        // 【P1-C1：回环地址必须绕开系统代理】
        //   本机 IE 配置里就躺着 ProxyServer=127.0.0.1:7890。
        //   验收时把 translateEndpoint 指到本地假服务器，HttpClient 若还走系统代理，
        //   表现是「服务器明明起着，程序说连不上」——这种故障查起来能耗一整天。
        var handler = new SocketsHttpHandler
        {
            // 【第五轮 §2】HttpWebRequest 每次会白等 Expect: 100-continue，几百毫秒就没了
            Expect100ContinueTimeout = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (IsLoopback(baseUrl)) handler.Proxy = null;

        _http = new HttpClient(handler)
        {
            // 超时统一由调用方的 CancellationTokenSource 控制（2.5 秒硬闸），
            // 这里再设一个会变成两套超时互相打架。
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    public string Name => "Bing 网页翻译（免密钥）";

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) &&
        (u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    public async Task<TranslationResult> TranslateAsync(string text, string to, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            AuthTicket ticket = await GetTicketAsync(forceRefresh: false, ct).ConfigureAwait(false);
            if (ticket.Failure != TranslationFailure.None)
                return Fail(ticket.Failure, ticket.Detail, sw);

            (JsonDocument? doc, string raw, TranslationFailure kind) =
                await PostAsync(text, to, ticket, ct).ConfigureAwait(false);

            // 【205 = token/key 不认】刷新一次再试；仍不行就是真的不行
            if (kind == TranslationFailure.AuthFailed)
            {
                AppLogger.Warn($"翻译：token 被拒（{raw}），强制刷新后重试一次");
                AuthTicket fresh = await GetTicketAsync(forceRefresh: true, ct).ConfigureAwait(false);
                if (fresh.Failure != TranslationFailure.None)
                    return Fail(fresh.Failure, fresh.Detail, sw);

                (doc, raw, kind) = await PostAsync(text, to, fresh, ct).ConfigureAwait(false);
            }

            if (kind != TranslationFailure.None)
                return Fail(kind, raw, sw);

            Swedish(doc!, out string translated, out string detected);
            doc!.Dispose();

            sw.Stop();
            if (translated.Length == 0)
                return Fail(TranslationFailure.Empty, "服务端返回了空译文", sw);

            return new TranslationResult
            {
                Text = translated,
                Provider = Name,
                ElapsedMs = sw.ElapsedMilliseconds,
                DetectedLanguage = detected,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 被上游的硬闸打断的。**这不是服务端的错**，别记成 Unknown。
            // 这里不写具体毫秒数：提供方只知道「时间到了」，不知道总预算是多少，
            // 写死 2500 会让 400ms 就超的断网场景在日志里自相矛盾。
            return Fail(TranslationFailure.Timeout, "时间到了，还没译完", sw);
        }
        catch (HttpRequestException ex)
        {
            return Fail(TranslationFailure.NoNetwork, ex.Message, sw);
        }
        catch (Exception ex)
        {
            return Fail(TranslationFailure.Unknown, $"{ex.GetType().Name} {ex.Message}", sw);
        }
    }

    private static TranslationResult Fail(TranslationFailure failure, string detail, System.Diagnostics.Stopwatch sw)
    {
        sw.Stop();
        AppLogger.Warn($"翻译失败：{failure.ToUserMessage()}（{detail}，{sw.ElapsedMilliseconds} ms）");
        return new TranslationResult
        {
            Failure = failure,
            Detail = detail,
            ElapsedMs = sw.ElapsedMilliseconds,
            Provider = "Bing 网页翻译（免密钥）",
        };
    }

    /// <summary>从响应里抠译文和检测到的源语言。抠不出来就当空串，不抛。</summary>
    private static void Swedish(JsonDocument doc, out string translated, out string detected)
    {
        translated = string.Empty;
        detected = string.Empty;
        try
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return;
            JsonElement first = root[0];

            if (first.TryGetProperty("translations", out JsonElement arr) &&
                arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 &&
                arr[0].TryGetProperty("text", out JsonElement t) &&
                t.ValueKind == JsonValueKind.String)
            {
                translated = t.GetString() ?? string.Empty;
            }

            if (first.TryGetProperty("detectedLanguage", out JsonElement dl) &&
                dl.ValueKind == JsonValueKind.Object &&
                dl.TryGetProperty("language", out JsonElement lang) &&
                lang.ValueKind == JsonValueKind.String)
            {
                detected = lang.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"翻译：解析响应结构失败（按空译文处理）：{ex.Message}");
        }
    }

    private async Task<(JsonDocument? doc, string raw, TranslationFailure kind)> PostAsync(
        string text, string to, AuthTicket ticket, CancellationToken ct)
    {
        // IG 和 IID 都在 query 上；isVertical 后面那个空 & 是页面里原样抄下来的
        string url = $"{_baseUrl}/ttranslatev3?isVertical=1&&IG={Uri.EscapeDataString(ticket.Ig)}" +
                     $"&IID={Uri.EscapeDataString(ticket.Iid)}";

        // 字段名是 fromLang（实测），不是 from。
        // fromLang=auto-detect 让服务端自己判源语言——我们从不传具体源语言（方案 9.12）。
        string form = "fromLang=auto-detect" +
                      "&to=" + Uri.EscapeDataString(to) +
                      "&text=" + Uri.EscapeDataString(text) +
                      "&token=" + Uri.EscapeDataString(ticket.Token) +
                      "&key=" + Uri.EscapeDataString(ticket.Key);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        req.Headers.TryAddWithoutValidation("Referer", _baseUrl + "/translator");
        req.Headers.TryAddWithoutValidation("Origin", _baseUrl);

        using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // 真 Bing 恒回 200，走到这里说明碰上了真错（网关 5xx 之类）
            return (null, $"HTTP {(int)resp.StatusCode} {body}", TranslationFailure.Unknown);
        }

        string trimmed = body.TrimStart();
        if (trimmed.StartsWith('['))
        {
            try
            {
                return (JsonDocument.Parse(body), body, TranslationFailure.None);
            }
            catch (JsonException ex)
            {
                return (null, $"响应不是合法 JSON：{ex.Message} / {Trim(body)}", TranslationFailure.Unknown);
            }
        }

        // 错误形态：{"statusCode":205,"errorMessage":""}
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using JsonDocument errDoc = JsonDocument.Parse(body);
                if (errDoc.RootElement.TryGetProperty("statusCode", out JsonElement sc) &&
                    sc.ValueKind == JsonValueKind.Number)
                {
                    int code = sc.GetInt32();
                    return (null, $"statusCode={code} {Trim(body)}", code switch
                    {
                        205 => TranslationFailure.AuthFailed,      // 实测：token / key 不认
                        400 => TranslationFailure.BadRequest,      // 实测：参数不对
                        // 下面三个码真 Bing 上没抓到过，是假服务器造的场景码，
                        // 留着是为了让每条降级路径都有话可说，不代表微软真有这些码。
                        403 => TranslationFailure.QuotaExceeded,
                        429 => TranslationFailure.RateLimited,
                        _ => TranslationFailure.Unknown,
                    });
                }
            }
            catch (JsonException)
            {
                // 落到下面按非 JSON 处理
            }
        }

        return (null, $"响应既不是数组也不是错误体：{Trim(body)}", TranslationFailure.Unknown);
    }

    private static string Trim(string s) => s.Length <= 120 ? s : s[..120] + "…";

    /// <summary>抓一次页面换一张票。带锁：并发调用只抓一次，别把页面拉两遍。</summary>
    private async Task<AuthTicket> GetTicketAsync(bool forceRefresh, CancellationToken ct)
    {
        lock (_authGate)
        {
            if (!forceRefresh &&
                _token.Length > 0 &&
                DateTime.UtcNow < _tokenExpiresUtc)
            {
                return new AuthTicket(_token, _tokenKey, _ig, _iid);
            }
        }

        string page;
        try
        {
            using HttpResponseMessage resp =
                await _http.GetAsync($"{_baseUrl}/translator", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return AuthTicket.Fail(TranslationFailure.NoNetwork, $"取页面 HTTP {(int)resp.StatusCode}");
            page = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 说清「哪一步被打断」，不说「超时」——实际可能只跑了 4ms（预算早用完了）。
            return AuthTicket.Fail(TranslationFailure.Timeout, "取授权页面时被硬闸打断");
        }
        catch (HttpRequestException ex)
        {
            return AuthTicket.Fail(TranslationFailure.NoNetwork, ex.Message);
        }

        string ig = IgPattern.Match(page) is { Success: true } m1 ? m1.Groups[1].Value : string.Empty;
        Match m2 = IidPattern.Match(page);
        string iid = m2.Success ? m2.Groups[1].Value : string.Empty;

        string key = string.Empty, token = string.Empty;
        Match m3 = ParamsPattern.Match(page);
        if (m3.Success)
        {
            try
            {
                using JsonDocument arr = JsonDocument.Parse(m3.Groups[1].Value);
                if (arr.RootElement.ValueKind == JsonValueKind.Array && arr.RootElement.GetArrayLength() >= 2)
                {
                    // [0] 是时间戳（key），[1] 才是 token —— 顺序搞反回 statusCode:400
                    key = arr.RootElement[0].ValueKind == JsonValueKind.String
                        ? arr.RootElement[0].GetString() ?? string.Empty
                        : arr.RootElement[0].GetRawText();
                    token = arr.RootElement[1].ValueKind == JsonValueKind.String
                        ? arr.RootElement[1].GetString() ?? string.Empty
                        : arr.RootElement[1].GetRawText();
                }
            }
            catch (JsonException ex)
            {
                AppLogger.Warn($"翻译：params_AbusePreventionHelper 解析失败：{ex.Message}");
            }
        }

        if (ig.Length == 0 || token.Length == 0)
        {
            // 页面结构变了 = 这条通道可能被改了。**报 AuthFailed 而不是瞎猜**，
            // 详情里写清楚缺哪一样，下次一眼知道是微软改了页面。
            return AuthTicket.Fail(
                TranslationFailure.AuthFailed,
                $"授权页面里取不到凭证（IG {(ig.Length > 0 ? "有" : "缺")}、token {(token.Length > 0 ? "有" : "缺")}，" +
                $"页面 {page.Length} 字节）");
        }

        lock (_authGate)
        {
            _token = token;
            _tokenKey = key;
            _ig = ig;
            _iid = iid;
            _tokenExpiresUtc = DateTime.UtcNow.AddMilliseconds(TokenTtlMs - TokenRefreshAheadMs);
        }

        AppLogger.Info($"翻译：已取到新 token（IG {ig.Length} 位、IID「{iid}」、" +
                       $"有效期 {TokenTtlMs / 60000} 分钟，提前 60 秒换新；**不落盘**）");
        return new AuthTicket(token, key, ig, iid);
    }

    /// <summary>一次请求要用的凭证。**只在内存里活，不写任何文件。**</summary>
    private readonly record struct AuthTicket(
        string Token, string Key, string Ig, string Iid, TranslationFailure Failure = TranslationFailure.None, string Detail = "")
    {
        public static AuthTicket Fail(TranslationFailure failure, string detail) =>
            new(string.Empty, string.Empty, string.Empty, string.Empty, failure, detail);
    }
}

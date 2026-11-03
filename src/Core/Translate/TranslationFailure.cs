namespace TranslatorApp.Core.Translate;

/// <summary>
/// 翻译失败的原因。每一种都要能翻成一句给用户看的大白话。
///
/// 【和 CaptureFailure 分开，不复用】
///   认字失败和翻译失败是两件事：认字失败要显示「没取到文字」，
///   翻译失败**必须照样显示原文**，只在详情行加一句。
///   混成一个枚举迟早有人写成「翻译失败 = 整张卡片变失败」——
///   那就变成白屏了，而约束写死「绝不覆盖原文、绝不白屏」。
/// </summary>
internal enum TranslationFailure
{
    None = 0,

    /// <summary>开关关着，或者这段本来就是中文（母语不用翻）。**不是错误，不显示任何提示。**</summary>
    Disabled,

    /// <summary>连不上（DNS 失败、端口不监听、被防火墙挡）。</summary>
    NoNetwork,

    /// <summary>2.5 秒硬闸到了。</summary>
    Timeout,

    /// <summary>
    /// 额度用尽。
    ///
    /// 【Bing 这条通道上不可达，留着是为了接口稳定】
    ///   2026-11-02 T3-spike-2 实测：Bing 网页翻译没有配额错误码，
    ///   抓到过的只有 statusCode 205（token/密钥不认）和 400（参数不对）。
    ///   真接 Azure 时才会用到它。
    /// </summary>
    QuotaExceeded,

    /// <summary>token 抓不到，或刷新一次后仍然被拒（Bing 的 statusCode 205）。</summary>
    AuthFailed,

    /// <summary>服务端返回成功但译文是空串。**这种不许写缓存。**</summary>
    Empty,

    /// <summary>服务端说请求本身不对（Bing 的 statusCode 400：目标语言无效、文字为空等）。不重试。</summary>
    BadRequest,

    /// <summary>
    /// 被限流（Bing 通道上没抓到过这个码，是假服务器造的 429 场景）。
    /// 【为什么不重试】2.5 秒硬闸下退避重试等于必然超时，不如立刻显原文。
    /// </summary>
    RateLimited,

    /// <summary>没预料到的情况（非 JSON 响应、未知 statusCode 等）。</summary>
    Unknown,
}

internal static class TranslationMessages
{
    /// <summary>
    /// 失败时追加到详情行末尾的那句话。
    /// 空串 = 什么都不显示（<see cref="TranslationFailure.Disabled"/> 是正常状态，不是失败）。
    /// </summary>
    public static string ToUserMessage(this TranslationFailure failure) => failure switch
    {
        TranslationFailure.Disabled => string.Empty,
        TranslationFailure.NoNetwork => "没连上网，只显示原文",
        // 用户 2026-11-02 指定的原话，别改
        TranslationFailure.Timeout => "翻译超时，原文已保留",
        TranslationFailure.QuotaExceeded => "翻译额度用完了，只显示原文",
        TranslationFailure.AuthFailed => "翻译服务没认下这次请求，只显示原文",
        TranslationFailure.Empty => "没翻出内容，只显示原文",
        TranslationFailure.BadRequest => "翻译服务没接受这段文字，只显示原文",
        TranslationFailure.RateLimited => "翻译请求太快被挡了，只显示原文",
        TranslationFailure.Unknown => "翻译没成功，只显示原文",
        _ => string.Empty,
    };
}

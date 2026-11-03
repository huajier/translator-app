namespace TranslatorApp.Core.Translate;

/// <summary>翻译结果：无论成功失败都返回它，不抛异常。</summary>
internal sealed class TranslationResult
{
    /// <summary>译文。失败时是空串——**永远是空串，不是原文**，覆盖原文的 bug 就是这么来的。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>哪个提供方翻的（目前只有 "Bing 网页翻译（免密钥）"）。</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>这次翻译花了多久（毫秒）。命中缓存时是查表那点时间。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>失败原因；成功时是 <see cref="TranslationFailure.None"/>。</summary>
    public TranslationFailure Failure { get; init; } = TranslationFailure.None;

    /// <summary>失败时的补充说明，直接写进日志便于排查。**不进卡片**（卡片只看 ToUserMessage）。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// 服务端自动检测出来的源语言（"en" 等）。
    ///
    /// 【为什么只记录、不进缓存键】
    ///   我们从不传 from，让服务端自己判（见方案 9.12：本地只有中文/俄文/拉丁三档，
    ///   判不出英法德日，硬传 from 会把「除了中文全都翻」砍成三种）。
    ///   这个字段只用来写日志，方便事后核对「它到底当成什么语言翻的」。
    /// </summary>
    public string DetectedLanguage { get; init; } = string.Empty;

    /// <summary>是不是从内存缓存里拿的（验收 S5 要能看出来）。</summary>
    public bool FromCache { get; init; }

    /// <summary>是否成功。</summary>
    public bool Success => Failure == TranslationFailure.None && Text.Length > 0;

    /// <summary>卡片上要不要画译文那一块。</summary>
    public bool Showable => Success;
}

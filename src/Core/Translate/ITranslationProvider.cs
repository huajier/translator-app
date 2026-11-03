namespace TranslatorApp.Core.Translate;

/// <summary>
/// 一个能翻一段文字的提供方。
///
/// 【为什么现在只有一个实现还要留接口（第八轮 §7-2）】
///   免密钥通道是微软给自家浏览器用的，**没有写进公开文档**，随时可能改协议或直接关掉。
///   留着接口，「换一家」就是新写一个类，不用动开关、缓存、超时、卡片这些已经验过的东西。
///
/// 【今天不写 AzureTranslatorProvider / MyMemoryProvider】
///   没人有密钥 → 写了也没法验 → 纯负债。架构师第八轮明确：A 路线整体废弃，只用 Bing。
///
/// 【没有 from 参数】
///   一律省略，让服务端自动检测（方案 9.12）。
///   想加 from 得先回答「本地三档怎么判英法德日」——现在判不出，加了就是错的。
/// </summary>
internal interface ITranslationProvider
{
    /// <summary>给人看的名字，会写进日志和卡片详情行。</summary>
    string Name { get; }

    /// <summary>
    /// 翻一段文字。**不许抛异常**：连不上、超时、服务端报错，全部装进返回值的 Failure 里。
    /// 调用方（<see cref="TranslationService"/>）的职责只是超时和缓存，不做网络错误分类。
    /// </summary>
    /// <param name="text">要翻的原文，非空。</param>
    /// <param name="to">目标语言，例如 "zh-Hans"。</param>
    /// <param name="ct">取消令牌，超时硬闸由调用方给。</param>
    Task<TranslationResult> TranslateAsync(string text, string to, CancellationToken ct);
}

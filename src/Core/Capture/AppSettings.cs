using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 程序设置（目前只有认字引擎相关的一项）。
///
/// 【为什么不写死在代码里、也不做成设置窗口】
///   用户 2026-10-08 选了「Tesseract 留着但默认关掉」。
///   默认关掉 = 代码里的默认值必须是 false；
///   「想开的时候能开」 = 需要一个能改的地方。
///   现在只有一项，做设置窗口太重，用一个配置文件刚刚好。
///   阶段 5 做正式设置界面时，本类可以直接被那个界面复用。
///
/// 【为什么不放程序目录】
///   放程序目录的话：换台电脑复制过去，配置也跟着走，容易误开；
///   而且程序目录可能被设成只读。放用户目录最稳。
/// </summary>
internal static class AppSettings
{
    private static readonly object Gate = new();

    /// <summary>
    /// 「只用 Windows 自带引擎」——**排查/对照用的开关，默认 false**。
    ///
    /// 【为什么不再是 useTesseract（2026-10-09 改）】
    ///   原来这里叫 useTesseract、默认 false，语义是
    ///   「Tesseract 是默认关掉的备用引擎」。
    ///   2026-10-09 用户说明真实用途是翻**英文和俄文**，
    ///   重跑实验后数据完全反过来：
    ///     引擎                      英文平均分   俄文
    ///     Tess-fast（原样）           94.7       100 分 ✅
    ///     Windows 英文引擎            63.3        装不上 ❌
    ///     Windows 中文引擎（曾上线）   58.9        装不上 ❌
    ///   Tesseract 从「备用」变成了**英俄文的主力**。
    ///   如果开关还叫 useTesseract 且默认 false，英文就会一直掉回 58 分。
    ///   所以语义翻转成它的反面：**默认走新分工，只有显式打开才退回老办法。**
    ///
    ///   只有需要「跟旧版本对比」或者「Tesseract 出问题想临时躲开」时才用得上。
    /// </summary>
    public static bool WindowsOnly { get; private set; } = false;

    /// <summary>
    /// 外文主通道用哪个引擎。**【2026-11-14 起改成三档枚举 EngineMode】**
    ///   下面是 2026-10-27 决定「默认关」时的历史依据，保留备查；
    ///   现在默认档是 Auto（长段落 RapidOCR / 小图 Tesseract），不再是「关」。**
    ///
    /// 【2026-10-27 阶段三 S3：为什么最后是 false，而不是原计划的 true】
    ///   集成后按用户 m01422 第 4 条跑 121 行全量 A/B，结果触发了止损线：
    ///     L_行 50.7% → 58.0%（+7.3pp，但门槛是 +8pp = 58.7%，差 0.7pp）
    ///     Barony 17.6% → 14.7%（-2.9pp，超过「单游戏掉 2pp」红线）
    ///   所以按同一条规则「回退，保留开关，不硬上」：代码全留，默认关。
    ///   完整数据见 tools\日志\阶段3-S3-集成AB.md。
    ///
    /// 【什么时候值得再打开】
    ///   只有当形近字问题有解（专属识别器 / 后处理词典）之后再来跑同一批 121 行，
    ///   过线了才改回 true。现在打开它等于让成绩从 50.7% 变成 58.0% 却把
    ///   Barony 这个最难的像素字体样本拖得更差。
    ///
    /// 打开方式：settings.json 里写 `"ocrEngineMode": "rapidocr"`（或 "tesseract" / "auto"）。
    /// 旧通道（Windows OCR / Tesseract 多路）一个字没删，切到 tesseract 就是 V0.3.4.3 的老行为。
    /// </summary>
    public enum OcrEngineMode { Auto, RapidOcr, Tesseract }

    /// <summary>当前引擎档位（默认 Auto）。</summary>
    public static OcrEngineMode EngineMode { get; private set; } = OcrEngineMode.Auto;

    /// <summary>
    /// 自动模式的分界高度：图高 &gt; 这个值走 RapidOCR，否则走 Tesseract。
    ///   为什么长段落反而给 RapidOCR：Tesseract 在长段落上要十几秒，用户嫌慢；
    ///   小图 Tesseract 只多两三秒，换来「内容更全」（同一段 Armor Class，RapidOCR 丢了后半句）。
    /// </summary>
    private const int AutoRapidOcrMinHeight = 160;

    /// <summary>
    /// 外文主通道是否用 RapidOCR。**只读计算属性**（2026-11-14 起由 EngineMode 决定）。
    /// Auto 档下这里返回 false —— 具体走哪条要看图高，用 <see cref="ShouldUseRapidOcr"/>。
    /// 保留这个属性是因为日志、存档、旧代码还在读它。
    /// </summary>
    public static bool RapidOcrPrimary => EngineMode == OcrEngineMode.RapidOcr;

    /// <summary>
    /// 这一次取词该走哪个引擎（**只影响本次，不改配置**）。
    /// 返回 true = 走 RapidOCR 主通道，false = 走老的 Tesseract 多路。
    /// </summary>
    public static bool ShouldUseRapidOcr(int imageHeight, out string reason)
    {
        switch (EngineMode)
        {
            case OcrEngineMode.RapidOcr:
                reason = "用户选 RapidOCR";
                return true;
            case OcrEngineMode.Tesseract:
                reason = "用户选 Tesseract";
                return false;
            default:
                if (imageHeight > AutoRapidOcrMinHeight)
                {
                    reason = $"自动·长段落（图高 {imageHeight}px > {AutoRapidOcrMinHeight}）";
                    return true;
                }
                reason = $"自动·小图（图高 {imageHeight}px ≤ {AutoRapidOcrMinHeight}）";
                return false;
        }
    }

    /// <summary>
    /// RapidOCR 用哪套识别模型：`en`（英文专用，默认）/ `latin`（NuGet 包自带）。
    ///
    /// 【2026-10-27 阶段三 S3 第 2 步：为什么默认换成 en】
    ///   同一批 80 张裁样、同一套 det/cls，只换 rec + dict，裸引擎成绩：
    ///     latin  L_行 44/69 = 63.8%   L_整块 30/52 = 57.7%   改字 44 例
    ///     en     L_行 54/69 = 78.3%   L_整块 32/52 = 61.5%   改字 32 例
    ///   体积：en rec 7.51MB + dict 1.4KB，比 latin（7.54MB + 2.1KB）**净减 0.03MB**。
    ///   两个 dict 都含 `0-9 / % . , - ' ! : ( )`，所以空格丢失不是字典缺字符。
    ///   v6 tiny/small 实测更差（30.4% / 69.6%，且小模型慢 15~30 倍、多出行严重），已排除。
    ///
    /// en 模型文件不进 git（二进制），由 tools\脚本\装模型.ps1 拷到 release 目录；
    /// 缺文件时引擎自动退回 latin 并记一条警告，不会让主通道整个失效。
    /// 切换方式：settings.json 里写 `"rapidOcrRec": "latin"`。
    /// </summary>
    public static string RapidOcrRec { get; private set; } = "en";

    /// <summary>
    /// 诊断用：把**真正交给 RapidOCR 的那张图**存成 PNG 到该目录（默认空 = 不存）。
    ///
    /// 【2026-10-27 阶段三 S3 集成损耗诊断】
    ///   裸跑 en 78.3% / P95 210ms，集成后 66.7% / P95 1110ms，差 11.6pp。
    ///   要判断「是不是喂进去的图不一样」，唯一不靠猜的办法是把实际入参存下来，
    ///   跟 `tools\临时\裁样\NN.png` 逐像素比。**只存图，不改任何识别行为。**
    ///   用法：settings.json 写 `"rapidOcrDumpDir": "D:\\...\\tools\\临时\\入参"`。
    /// </summary>
    public static string RapidOcrDumpDir { get; private set; } = "";

    /// <summary>
    /// 诊断用：新通道吃「反色前的原图」还是「反色后的图」。**默认 false = 现状（反色）**。
    ///
    /// 【2026-10-27 集成损耗诊断：实验 B】
    ///   旧代码为了 Tesseract 在 `OcrTextCapture.cs:134` 一拿到图就把深底整张反色，
    ///   而新通道（RapidOCR 自带检测+归一化）也被迫吃这张反色图。
    ///   存图对照实测：78 张进新通道的裁样里 **62 张是反色图**、16 张原图。
    ///   裸跑吃原图 78.3%，管线吃反色 66.7% → 这就是 11.6pp 的去处。
    ///   打开方式：settings.json 写 `"rapidOcrUseOriginal": true`。
    ///   旧通道（Windows / Tesseract）**照旧吃反色图，一个字节没改**。
    /// </summary>
    public static bool RapidOcrUseOriginal { get; private set; } = false;

    /// <summary>
    /// 新通道要不要保留**行尾标点**（`.` `'` `:` 等）。**默认 false = 现状（照旧削）**。
    ///
    /// 【2026-10-27 集成损耗诊断：第 1 处接线】
    ///   `TrimEdgeSymbols` 是老通道为 Tesseract 写的清洗器，两条规则会误伤：
    ///     · `IsEdgeSymbol` 含 `'` → 行尾 `'` 被当装饰削掉
    ///       （行-57/60 `SG-225 'Breaker'` → `SG-225 'Breaker`）；
    ///     · 「单个纯字母词尾巴上的 `.` `:` 削掉」那条 → 行尾句号被吃
    ///       （行-18 `sneaking.` → `sneaKing`、行-20 `sneak.`）。
    ///   裸跑（不洗）这 4 行全对，管线全错。
    ///   打开 = 新通道只削开头，行尾一律不动（空白由 Trim() 兜）。
    ///   关闭方式：settings.json 写 `"rapidOcrKeepTrailingPunct": false`。
    ///   旧通道（Windows / Tesseract）**照旧用老清洗器，一个字节没改**。
    ///   2026-10-27 用户拍板：默认改 true（A/B：L_行 46→49、Barony 47.1→50.0%、L_整块持平、P95 不变差、零新错；
    ///   行尾标点本来就该保留给翻译）。
    /// </summary>
    public static bool RapidOcrKeepTrailingPunct { get; private set; } = true;

    /// <summary>
    /// 整块没认好时提示用户改成逐行框（S4，2026-10-29）。
    ///
    /// 【做什么】RapidOCR 认到多行时，若「弱字符（字符分 &lt; 0.60）≥ 2」或「单字框 ≥ 2」，
    ///   在结果旁加一行小字提示逐行框。**只影响提示，不改一个识别字符。**
    /// 【为什么默认 true】离线 11 张整块上触发 5 张、5 张全坏、2 张好的不触发，误报 0；
    ///   66 张单行图零触发。引导逐行能回收 4 行 → L_整块 +7.7pp，L_行 不变。
    /// 【怎么关】settings.json 里 <c>blockFallbackHint=false</c>，等于回到没有这个功能。
    /// 【已知漏报】整块坏 9 张里有 4 张不触发（行-38/39/64/65），纯文本信号最多只能到 3/9，
    ///   拿不到逐字分数就只能漏，这是设计上接受的。
    /// </summary>
    public static bool BlockFallbackHint { get; private set; } = true;

    /// <summary>
    /// 小框 3 倍重试（S4-D，2026-10-30）。
    ///
    /// 【做什么】RapidOCR 1x 结果「空」或「有块含非 ASCII 字符」时，把同一张输入图放大 3 倍再认一次；
    ///   3x 结果非空且非 ASCII 字符数不增加才采用。**不改任何阈值、不动回退路径。**
    /// 【为什么默认 true】离线 80 张实测：触发 4 张（行-68/75/76/83），L_行 50→53（+4.3pp）、
    ///   L_整块 36→38（+3.9pp），其余 76 张逐字节不变，Z1/Z2/Z3/R1 逐字节不变，回归 0；
    ///   实机也验过：长句/整块收益概率高、普通行无变差。
    /// 【怎么关】settings.json 里 <c>rapidOcrSmallBoxUpscale=false</c>，等于回到 S4-D 之前。
    /// 【已知小瑕疵】行-75 的 3x 输出首字母是小写（`cLICK`），量具不区分大小写故判对，用户能看到。
    ///   申请单与模拟数据见 tools\日志\阶段3-S4D-申请单-小框3倍重试.md，
    ///   三轮验收与信号定义见 tools\日志\阶段3-S3-分叉结论.md 第 19 节。
    /// </summary>
    public static bool RapidOcrSmallBoxUpscale { get; private set; } = true;

    /// <summary>
    /// 【2026-11-11 第20轮 用户需求「句句对照」】**默认关。**
    ///
    /// 【做什么】开启后卡片里的原文/译文改成逐句富文本：
    ///   鼠标移到某一句，原文那句和右边译文那句**同时高亮**。
    /// 【为什么默认关】这是新功能，用户要自己决定要不要。关着时卡片行为必须和以前完全一致
    ///   （普通 TextBox、可拖选、无高亮）—— 这条是硬要求，别为了省事把两条路径合并。
    /// 【前提】整段翻译完成后再切句，句数相等才高亮（见 SentenceSplitter.TryPair）。
    ///   句数不等时自动降级成整段显示 —— 强行按序号配会配错句，比不高亮更糟。
    /// </summary>
    public static bool SentenceHighlight { get; private set; } = false;

    /// <summary>
    /// 翻译总开关（阶段四，2026-11-02）。**2026-11-04 起默认 true = 装好就能用。**
    ///
    /// 【为什么改成默认开（v2.0 用户实测反馈）】
    ///   默认关的时候，新用户框一段外文，看到的只有原文，得自己去托盘右键菜单里
    ///   找那个勾选项才知道有翻译 —— 功能在那儿但没人用得上，等于没做。
    ///   现在装好就能用，第一次启动弹一次提示明确告知「文字会发出去」。
    /// 【代价，必须知道】
    ///   用户没主动关之前，**每次框选都会把文字发到微软**。这不是偷偷的：
    ///   托盘菜单文字本身就写着「翻译：把文字发给微软（勾选 = 开）」，
    ///   首次提示也会再讲一遍，主窗口的「关于翻译」还写着第三遍。
    /// 【怎么关】托盘右键菜单勾选项 / 主窗口的开关，两处任一。
    ///   菜单文字不许改成「开启翻译」这类不带披露的说法 —— 改了就等于偷偷外发。
    /// </summary>
    public static bool TranslateEnabled { get; private set; } = true;

    /// <summary>
    /// 「默认开启」这条一次性迁移做没做过（阶段四 v2.0，2026-11-04）。
    ///
    /// 【为什么需要它】老用户的配置里已经明明白白写着 `"translateEnabled": false`，
    ///   光把属性默认值改成 true 对老机器毫无作用 —— Load() 会把它读回 false，
    ///   升级完还是关的。所以需要一次「迁移」：见到这个键不存在，
    ///   就当作从没定义过默认值，强制开一次并存盘打标记。
    /// 【为什么要打标记，不能每次启动都强制开】
    ///   用户手动关掉是**他的决定**。没有标记的话，下次启动又会把它打开，
    ///   等于程序不认用户的设置。标记写下去之后，这一支永远不再执行。
    /// </summary>
    public static bool TranslateDefaultOnMigrated { get; private set; } = false;

    /// <summary>
    /// 首次提示是否已经弹过（阶段四）。**弹过一次就写 true，重启不再弹。**
    ///
    /// 【为什么必须存盘】提示框自己只活 3 秒，它记不住「弹过没有」；
    ///   不存的话每次启动都会弹，用户会当成骚扰，最后干脆不看了。
    /// </summary>
    public static bool TranslateNoticeShown { get; private set; } = false;

    /// <summary>
    /// 翻译成哪种语言（阶段四 T3a，2026-11-02）。**默认 zh-Hans。**
    ///
    /// 【为什么是 "zh-Hans" 而不是 "zh"】真 Bing 抓回来的响应里 `to` 就是 `zh-Hans`，
    ///   照它写省得每次请求都被服务端纠正一遍。
    /// 【不做语言校验表】填错就让服务端报错（Bing 回 statusCode:400），
    ///   本地再维护一张语言码表只会两边不一致。
    /// </summary>
    public static string TranslateTargetLang { get; private set; } = "zh-Hans";

    /// <summary>
    /// 翻译服务地址。**默认空 = 用真 Bing（https://cn.bing.com）。**
    ///
    /// 【为什么可改】验收要把请求指到本地假服务器（tools\脚本\假译服务器.ps1），
    ///   真去把额度烧光、真去断网都不现实。
    /// 【为什么空串代表默认】写成 "https://cn.bing.com" 会让「用户没配」和「配成真地址」
    ///   看起来一样，将来换默认地址时改不干净。
    /// </summary>
    public static string TranslateEndpoint { get; private set; } = "";

    private static bool _loaded;

    /// <summary>配置文件位置（放用户目录，不放程序目录）。</summary>
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TranslatorApp",
        "settings.json");

    /// <summary>读一次就够，读坏了也只影响这一个开关。</summary>
    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(ConfigPath))
                {
                    AppLogger.Info($"还没设置文件，用默认分工（中文走 Windows 自带，英文/俄文走 Tesseract）。{ConfigPath}");
                    ApplyDefaultOnMigration();
                    return;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ConfigPath));

                bool any = false;

                if (doc.RootElement.TryGetProperty("windowsOnly", out var v)
                    && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    WindowsOnly = v.GetBoolean();
                    any = true;
                }

                // 2026-11-14：引擎三档（主界面下拉框）。老配置只有 rapidOcrPrimary 时按它换算：
                //   true → RapidOcr，false → Tesseract；两个键都没有 → 保持 Auto 默认。
                if (doc.RootElement.TryGetProperty("ocrEngineMode", out var em)
                    && em.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    EngineMode = (em.GetString() ?? "").Trim().ToLowerInvariant() switch
                    {
                        "rapidocr" => OcrEngineMode.RapidOcr,
                        "tesseract" => OcrEngineMode.Tesseract,
                        _ => OcrEngineMode.Auto,
                    };
                    any = true;
                }
                else if (doc.RootElement.TryGetProperty("rapidOcrPrimary", out var r)
                    && r.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    EngineMode = r.GetBoolean() ? OcrEngineMode.RapidOcr : OcrEngineMode.Tesseract;
                    any = true;
                }

                // 2026-10-27 阶段三 S3 第 2 步：换识别模型（en / latin）
                if (doc.RootElement.TryGetProperty("rapidOcrRec", out var rr)
                    && rr.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = (rr.GetString() ?? "").Trim().ToLowerInvariant();
                    if (s == "latin" || s == "en") { RapidOcrRec = s; any = true; }
                }

                // 2026-10-27 阶段三 S3 集成损耗诊断：把入参图存下来（默认空 = 不存）
                if (doc.RootElement.TryGetProperty("rapidOcrDumpDir", out var dd)
                    && dd.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    RapidOcrDumpDir = (dd.GetString() ?? "").Trim();
                    any = true;
                }

                // 2026-10-27 阶段三 S3 集成损耗诊断：新通道吃原图还是反色图
                if (doc.RootElement.TryGetProperty("rapidOcrUseOriginal", out var uo)
                    && uo.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    RapidOcrUseOriginal = uo.GetBoolean();
                    any = true;
                }

                // 2026-10-27 阶段三 S3 第 1 处接线：新通道保留行尾标点
                if (doc.RootElement.TryGetProperty("rapidOcrKeepTrailingPunct", out var kt)
                    && kt.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    RapidOcrKeepTrailingPunct = kt.GetBoolean();
                    any = true;
                }

                // 2026-10-29 阶段三 S4：整块没认好时的逐行提示开关
                if (doc.RootElement.TryGetProperty("blockFallbackHint", out var bfh)
                    && bfh.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    BlockFallbackHint = bfh.GetBoolean();
                    any = true;
                }

                // 2026-11-11 第20轮：句句对照开关（默认关）
                if (doc.RootElement.TryGetProperty("sentenceHighlight", out var shl)
                    && shl.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    SentenceHighlight = shl.GetBoolean();
                    any = true;
                }

                // 2026-10-30 阶段三 S4-D：小框 3 倍重试开关（2026-10-31 起默认开）
                if (doc.RootElement.TryGetProperty("rapidOcrSmallBoxUpscale", out var sbu)
                    && sbu.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    RapidOcrSmallBoxUpscale = sbu.GetBoolean();
                    any = true;
                }

                // 2026-11-02 阶段四 T1a：翻译总开关（默认关）+ 首次提示是否弹过
                if (doc.RootElement.TryGetProperty("translateEnabled", out var te)
                    && te.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    TranslateEnabled = te.GetBoolean();
                    any = true;
                }

                if (doc.RootElement.TryGetProperty("translateNoticeShown", out var tns)
                    && tns.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    TranslateNoticeShown = tns.GetBoolean();
                    any = true;
                }

                // 2026-11-02 阶段四 T3a：翻成哪种语言 + 翻译服务地址（默认空 = 真 Bing）
                if (doc.RootElement.TryGetProperty("translateTargetLang", out var ttl)
                    && ttl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string s = (ttl.GetString() ?? "").Trim();
                    // 空串当没配：写空进去会把「用默认值」变成「永久空」，请求必 400。
                    if (s.Length > 0) { TranslateTargetLang = s; any = true; }
                }

                if (doc.RootElement.TryGetProperty("translateEndpoint", out var tep)
                    && tep.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    // 空串是合法值（= 用真 Bing），所以这里不判长度，照收。
                    TranslateEndpoint = (tep.GetString() ?? "").Trim();
                    any = true;
                }

                // 2026-11-04 v2.0：默认开启的一次性迁移标记。读到它 = 迁移过了，不再强制开。
                if (doc.RootElement.TryGetProperty("translateDefaultOnMigrated", out var tdm)
                    && tdm.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    TranslateDefaultOnMigrated = tdm.GetBoolean();
                    any = true;
                }

                AppLogger.Info(
                    $"已读取设置：只用 Windows 自带 = {(WindowsOnly ? "是" : "否")}，" +
                    $"引擎 = {EngineMode}，" +
                    $"句句对照 = {(SentenceHighlight ? "是" : "否")}，" +
                    $"翻译 = {(TranslateEnabled ? "开" : "关")}（译成 {TranslateTargetLang}，" +
                    $"地址 = {(TranslateEndpoint.Length == 0 ? "默认 Bing" : TranslateEndpoint)}，" +
                    $"来自 {ConfigPath}）");

                if (any) { ApplyDefaultOnMigration(); return; }

                AppLogger.Info($"设置文件里没有认识的项，按默认（新分工 + RapidOCR 主通道）处理：{ConfigPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"读设置失败（只影响 Tesseract 开关，认字照常）：{ex.GetType().Name} {ex.Message}");
                return;
            }

            ApplyDefaultOnMigration();
        }
    }

    /// <summary>
    /// v2.0 一次性迁移：把「翻译默认关」改成「默认开」，且**只对老配置做一次**。
    ///
    /// 【为什么光改默认值不够】
    ///   属性默认值只在「配置里没有这个键」时生效。老用户的 settings.json 里
    ///   明明白白写着 `"translateEnabled": false`，Load() 会照读回来，升级完还是关的。
    /// 【为什么用「迁移标记」而不是每次启动强制开】
    ///   用户手动关掉就是他的决定。没有标记的话，下次启动又被打开，等于程序不认设置。
    ///   标记 `translateDefaultOnMigrated` 一旦写下去，这一支永远不再执行。
    /// 【什么时候算迁移过】新装（无配置文件）也算：此时本来就是默认 true，
    ///   直接打标记，避免用户在新装后手动关掉、下一次启动又被强制打开。
    /// </summary>
    private static void ApplyDefaultOnMigration()
    {
        if (TranslateDefaultOnMigrated) return;

        bool changed = !TranslateEnabled;
        TranslateEnabled = true;
        TranslateDefaultOnMigrated = true;

        if (changed) AppLogger.Info("v2.0 升级：翻译开关从「关」改成默认「开」（一次性迁移，之后以用户设置为准）");
        Save(WindowsOnly);
    }

    /// <summary>
    /// 写设置。用户想开/关时调它。
    ///
    /// 【键都要写】只写 windowsOnly 会把 rapidOcrPrimary / rapidOcrRec / rapidOcrDumpDir 抹掉，
    /// 下次启动就悄悄回到默认值，排查时会以为「开关没生效」。
    /// 【现在是 13 个键】加键必须同时加在这里和 Load() 的 TryGetProperty 两处，
    /// 漏一处的后果分别是「下次保存被抹掉」和「存了读不回来」。
    /// </summary>
    public static void Save(bool windowsOnly)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath,
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        windowsOnly,
                        // 2026-11-14：引擎三档。少了这一行，下次保存就把用户选的档位抹回 auto。
                        ocrEngineMode = EngineMode.ToString().ToLowerInvariant(),
                        rapidOcrPrimary = RapidOcrPrimary,
                        rapidOcrRec = RapidOcrRec,
                        rapidOcrDumpDir = RapidOcrDumpDir,
                        rapidOcrUseOriginal = RapidOcrUseOriginal,
                        rapidOcrKeepTrailingPunct = RapidOcrKeepTrailingPunct,
                        blockFallbackHint = BlockFallbackHint,
                        rapidOcrSmallBoxUpscale = RapidOcrSmallBoxUpscale,
                        // 2026-11-11 第20轮：少了这一行，下次保存就把句句对照开关抹回默认关
                        sentenceHighlight = SentenceHighlight,
                        // 2026-11-02 阶段四 T1a：这两行少一行，下次保存就把开关状态抹掉，
                        // 用户会看到「我开的翻译怎么自己关了」。
                        translateEnabled = TranslateEnabled,
                        translateNoticeShown = TranslateNoticeShown,
                        // 2026-11-02 阶段四 T3a：同理，这两行少一行，
                        // 用户设的目标语言和服务地址会被下一次保存抹回默认。
                        translateTargetLang = TranslateTargetLang,
                        translateEndpoint = TranslateEndpoint,
                        // 2026-11-04 v2.0：同理，少这一行下次保存就把迁移标记抹掉，
                        // 于是每次启动都会把用户手动关掉的翻译又强制打开一遍。
                        translateDefaultOnMigrated = TranslateDefaultOnMigrated,
                    },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            WindowsOnly = windowsOnly;
            AppLogger.Info($"设置已保存：只用 Windows 自带 = {(windowsOnly ? "是" : "否")} → {ConfigPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"保存设置失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 开/关翻译，并立刻存盘。
    ///
    /// 【为什么不给 Save 加参数】Save 一加参数，调用点迟早有人传错；
    ///   改成「先改属性、再按现状存」，只有一条路，不会出现「存的和内存里不一致」。
    /// </summary>
    public static void SetTranslateEnabled(bool on)
    {
        lock (Gate)
        {
            if (TranslateEnabled == on) return;
            TranslateEnabled = on;
        }
        Save(WindowsOnly);
    }

    /// <summary>
    /// 开/关 RapidOCR 主通道，并立刻存盘（2026-11-09 主窗口加了这个勾选框）。
    ///
    /// 【为什么要有 setter】以前这个开关只能手改 settings.json，用户明确抱怨
    ///   「开关不能放在主页面里面吗」。主窗口勾选框改它，必须当场写盘，
    ///   否则重启就又回到默认值。
    /// 【和 SetTranslateEnabled 同一套路】先改属性再按现状存，只有一条路。
    /// </summary>
    public static void SetEngineMode(OcrEngineMode mode)
    {
        lock (Gate)
        {
            if (EngineMode == mode) return;
            EngineMode = mode;
        }
        Save(WindowsOnly);
    }

    /// <summary>
    /// 老入口（2026-11-09 的勾选框用过）。现在等价于 SetEngineMode：
    ///   true → RapidOcr，false → Tesseract。留着是为了不打断任何还在调它的地方。
    /// </summary>
    public static void SetRapidOcrPrimary(bool on) =>
        SetEngineMode(on ? OcrEngineMode.RapidOcr : OcrEngineMode.Tesseract);

    /// <summary>
    /// 【2026-11-11 第20轮】开/关「句句对照」，并立刻存盘。
    /// 照 SetRapidOcrPrimary 的写法：先改属性再按现状存，只有一条路。
    /// </summary>
    public static void SetSentenceHighlight(bool on)
    {
        lock (Gate)
        {
            if (SentenceHighlight == on) return;
            SentenceHighlight = on;
        }
        Save(WindowsOnly);
    }

    /// <summary>记下「首次提示已经弹过」，并立刻存盘。只该在弹完之后调。</summary>
    public static void SetTranslateNoticeShown()
    {
        lock (Gate)
        {
            if (TranslateNoticeShown) return;
            TranslateNoticeShown = true;
        }
        Save(WindowsOnly);
    }

    /// <summary>
    /// 改「翻成哪种语言」，并立刻存盘（阶段四 T3b，2026-11-04）。
    ///
    /// 【为什么不给 Save 加参数】同 SetTranslateEnabled：只有一条路，不会出现「存的和内存里不一致」。
    /// 【为什么不校验语言码】见 TranslateTargetLang 的注释：本地维护一张表只会和服务端不一致，
    ///   填错让 Bing 回 400 就行。这里只挡一种情况 —— **空串**（存空 = 每次都必 400）。
    /// </summary>
    public static void SetTranslateTargetLang(string code)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return;   // 空串不许存，保留原值

        lock (Gate)
        {
            if (TranslateTargetLang == code) return;
            TranslateTargetLang = code;
        }
        Save(WindowsOnly);
    }
}
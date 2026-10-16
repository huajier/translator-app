using System.Text.RegularExpressions;
using TranslatorApp.Core.Capture;

// 折行取证探针：直接链接 TextNormalizer.cs 源码，问「这些折行到底会不会被合回去」。
// 一次性工具，用完即删。

int pass = 0, fail = 0;

void Case(string name, string raw, string expect)
{
    string got = TextNormalizer.Normalize(raw);
    string g = got.Replace("\n", "<NL>");
    string e = expect.Replace("\n", "<NL>");
    bool ok = g == e;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    Console.WriteLine($"      入: {raw.Replace("\n", "<NL>")}");
    Console.WriteLine($"      出: {g}");
    if (!ok) Console.WriteLine($"      期望: {e}");
    if (ok) pass++; else fail++;
}

void JoinCase(string prev, string cur)
{
    bool r = TextNormalizer.ShouldJoinLines(prev, cur);
    string p = prev[^Math.Min(12, prev.Length)..];
    string c = cur[..Math.Min(12, cur.Length)];
    Console.WriteLine($"  ShouldJoinLines(前「{p}」| 后「{c}」) = {r}");
}

Console.WriteLine("===== 一、真实折行现场，Normalize 之后还剩不剩折行 =====");

// C 类：拉丁+拉丁，本该合并却没合（第64轮 16:58:42 现场）
Case("C类 rely on large / neural",
    "Modern systems rely on large\nneural networks that were trained",
    "Modern systems rely on large neural networks that were trained");

Case("C类 struggles the / most.",
    "It usually struggles the\nmost.",
    "It usually struggles the most.");

Case("C类 recognition / engine.",
    "which is one of the hardest cases for any recognition\nengine.",
    "which is one of the hardest cases for any recognition engine.");

// B 类：汉+汉，词被从中间劈开。
// 第一步（2026-10-16）明确不治这一类，挡它的是 ShouldJoinLines 里「中文一律不合并」那道门。
// 第二步（2026-10-16，用户 m07080「写，动手」）把那道门放开成
//   `IsCjk(tail) != IsCjk(current[0]) → 不合并`，两侧都是汉字时才交给判官。
// 所以这三条**现在期望合并**。
Case("B类 文字 / 并转换",
    "光学字符识别是指计算机识别图像中的文字\n并转换为可编辑文本的过程。",
    "光学字符识别是指计算机识别图像中的文字并转换为可编辑文本的过程。");

Case("B类 票据录 / 入",
    "票据录\n入等场景",
    "票据录入等场景");

Case("B类 场 / 景中",
    "它在扫描文档、电子书、票据录入等场\n景中有非常广泛的应用。",
    "它在扫描文档、电子书、票据录入等场景中有非常广泛的应用。");

// 空行版本同样合并
Case("B类 空行版",
    "票据录\n\n入等场景",
    "票据录入等场景");

// ⚠️ 天花板：真段落但上一行句号被 OCR 丢了 —— 两侧都是汉字，这条规矩分不出来，
//    会把它黏上。历史里 2 次。这里如实钉住，不当成 bug 藏起来。
Case("B类 天花板：句号丢了被黏",
    "数据库连接失败\n请检查配置文件。",
    "数据库连接失败请检查配置文件。");

// A 类：真分行，不该合并
Case("A类 句号结尾（英文）",
    "Server started on port 8080.\nDatabase connection failed.",
    "Server started on port 8080.\nDatabase connection failed.");

Case("A类 句号结尾（中文）",
    "服务已启动，端口 8080。\n数据库连接失败。",
    "服务已启动，端口 8080。\n数据库连接失败。");

Console.WriteLine();
Console.WriteLine("===== 二、逐条问 ShouldJoinLines（不动文本，只看闸门）=====");
JoinCase("Modern systems rely on large", "neural networks that were trained");
JoinCase("翻译软件", "翻译软件");
JoinCase("票据录", "入等场景");
JoinCase("等场", "景中有非常广泛的应用。");
JoinCase("光学字符识别是指计算机识别图像中的文字", "并转换为可编辑文本的过程。");
JoinCase("the", "most.");
JoinCase("recognition", "engine.");

Console.WriteLine();
Console.WriteLine($"合计：PASS {pass}，FAIL {fail}");

// ==========================================================
// 三、真发现场：归档里那条 `large⏎neural`，到底为什么没被合回去？
//    归档里的 `⏎` 就是最终文本里的真换行，所以我把这些行原样喂给 Normalize，
//    看它合了哪些、放过哪些，并把每行结尾那个字符的码点打出来。
// ==========================================================
Console.WriteLine();
Console.WriteLine("===== 三、真实归档里的换行（最终文本）逐行解剖 =====");

const string NewlineMark = "⏎";
string archiveDir = "";
for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
{
    string candidate = Path.Combine(d.FullName, "tools", "日志", "用户实测");
    if (Directory.Exists(candidate)) { archiveDir = candidate; break; }
}
if (archiveDir.Length == 0)
{
    Console.WriteLine("找不到归档目录");
    Environment.Exit(2);
}

foreach (string file in Directory.GetFiles(archiveDir, "*.txt").OrderBy(f => f))
{
    foreach (string line in File.ReadAllLines(file))
    {
        if (!line.StartsWith("原文: ")) continue;
        if (!line.Contains("large")) continue;

        string text = line.Substring(4);
        string[] parts = text.Split(NewlineMark[0]);
        Console.WriteLine($"\n【{Path.GetFileName(file)}】共 {parts.Length} 段");

        // 找 `large` 那一段，把它的结尾字符码点打出来
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].EndsWith("large") || (i > 0 && parts[i - 1].EndsWith("large")))
            {
                int lo = Math.Max(0, i - 1), hi = Math.Min(parts.Length - 1, i + 1);
                for (int k = lo; k <= hi; k++)
                {
                    string p = parts[k];
                    string tailDesc = p.Length == 0 ? "（空）"
                        : $"{p[^1]} U+{(int)p[^1]:X4}";
                    Console.WriteLine($"  段{k}: 尾={tailDesc} 首={(p.Length == 0 ? "（空）" : $"U+{(int)p[0]:X4}")} 「{p}」");
                }
            }
        }

        // 把这些段当行喂给 Normalize
        string rejoined = TextNormalizer.Normalize(string.Join("\n", parts));
        Console.WriteLine($"  喂进 Normalize 之后：{(rejoined.Contains('\n') ? "仍有换行" : "已合成一段")}");
        string[] after = rejoined.Split('\n');
        Console.WriteLine($"  出来是 {after.Length} 行");
        if (after.Length > 1)
        {
            for (int k = 0; k < Math.Min(after.Length, 6); k++)
                Console.WriteLine($"    行{k}: 「{after[k]}」");
        }
        break;
    }
}

// ==========================================================
// 四、收益面：全部归档的最终文本里，有多少相邻行对是
//     「ShouldJoinLines 说该合、可现在却留着换行」的？
//     这就是「把空行也交给 ShouldJoinLines 判」能救回的处数。
// ==========================================================
Console.WriteLine();
Console.WriteLine("===== 四、全部归档里「本该合却没合」的相邻行对 =====");

Func<string, int, string> tail = (s, n) => s.Length <= n ? s : s[^n..];
Func<string, int, string> head = (s, n) => s.Length <= n ? s : s[..n];

int pairs = 0, joinable = 0;
var samples = new List<string>();
foreach (string file in Directory.GetFiles(archiveDir, "*.txt").OrderBy(f => f))
{
    foreach (string line in File.ReadAllLines(file))
    {
        if (!line.StartsWith("原文: ")) continue;
        string[] parts = line.Substring(4).Split(NewlineMark[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            string prev = parts[i - 1], cur = parts[i];
            if (prev.Length == 0 || cur.Length == 0) continue;
            pairs++;
            if (!TextNormalizer.ShouldJoinLines(prev, cur)) continue;
            joinable++;
            if (samples.Count < 25)
                samples.Add($"  ：…{tail(prev, 16)} ⏎ {head(cur, 16)}…");
        }
    }
}

Console.WriteLine($"归档里相邻行对共 {pairs} 处，其中 ShouldJoinLines 认为「该合」的 {joinable} 处");
Console.WriteLine("样例（该合却没合）:");
foreach (string s in samples) Console.WriteLine(s);

// ==========================================================
// 五、根因验证：引擎行与行之间到底是一个换行还是**空行**？
//     Normalize 里 `blankRun >= 1` 会 append 一个 '\n' 而不合并
//     ——如果原文是「空行」，折行就会被原样保留下来。
// ==========================================================
Console.WriteLine();
Console.WriteLine("===== 五、根因：单换行 vs 空行 =====");

void RootCase(string name, string raw)
{
    string got = TextNormalizer.Normalize(raw);
    Console.WriteLine($"  {name}：入 {raw.Replace("\n", "<NL>").Replace("\r", "<CR>")}");
    Console.WriteLine($"      出 {got.Replace("\n", "<NL>")}  → {(got.Contains('\n') ? "仍有折行" : "合成一段")}");
}

RootCase("单换行", "Modern systems rely on large\nneural networks");
RootCase("空行", "Modern systems rely on large\n\nneural networks");
RootCase("空行（中文段）", "识别图像中的文字\n\n并转换为可编辑文本");

// ==========================================================
// 六、候选改法一：Normalize 里「空行」也先问一次 ShouldJoinLines。
//     归档最终文本就是「空行已压成单换行」的产物，
//     所以直接对它逐对应用 ShouldJoinLines，就是改法一的效果。
//     列出全部被合的行对（去重 + 计数），供人工抽查有没有真段落被误合。
// ==========================================================
Console.WriteLine();
Console.WriteLine("===== 六、改法一（空行也问 ShouldJoinLines）会合掉哪些行对 =====");

var merged = new SortedDictionary<string, int>();
int stillSplit = 0, stillSplitPunct = 0;
foreach (string file in Directory.GetFiles(archiveDir, "*.txt").OrderBy(f => f))
{
    foreach (string line in File.ReadAllLines(file))
    {
        if (!line.StartsWith("原文: ")) continue;
        string[] parts = line.Substring(4).Split(NewlineMark[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            string prev = parts[i - 1], cur = parts[i];
            if (prev.Length == 0 || cur.Length == 0) continue;
            if (TextNormalizer.ShouldJoinLines(prev, cur))
            {
                string key = $"…{tail(prev, 14)} + {head(cur, 14)}…";
                merged[key] = merged.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            else
            {
                stillSplit++;
                char t = prev[^1];
                if (t is '.' or '?' or '!' or ';' or ':' or '。' or '？' or '！' or '；' or '：')
                    stillSplitPunct++;
            }
        }
    }
}

Console.WriteLine($"会合掉 {merged.Values.Sum()} 处（去重后 {merged.Count} 种），留下的换行 {stillSplit} 处（其中 {stillSplitPunct} 处前一行以句末标点结尾 = 真段落，本来就该留）");
foreach (var kv in merged)
    Console.WriteLine($"  ×{kv.Value,-3} {kv.Key}");

// ==========================================================
// 七、B 类（汉字+汉字）折行的全貌：去重 + 计数。
//     这一类和 C 类不一样：中文逐字换行排版很常见，
//     合错了会把真段落黏成一坨，所以单独列出来看。
// ==========================================================
Console.WriteLine();
Console.WriteLine("===== 七、B 类（汉+汉）折行全貌 =====");
Func<char, bool> isCjk = c => c >= 0x4E00 && c <= 0x9FFF;
var cjkPairs = new SortedDictionary<string, int>();
int cjkTotal = 0;
foreach (string file in Directory.GetFiles(archiveDir, "*.txt").OrderBy(f => f))
{
    foreach (string line in File.ReadAllLines(file))
    {
        if (!line.StartsWith("原文: ")) continue;
        string[] parts = line.Substring(4).Split(NewlineMark[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            string prev = parts[i - 1], cur = parts[i];
            if (prev.Length == 0 || cur.Length == 0) continue;
            if (!isCjk(prev[^1]) || !isCjk(cur[0])) continue;
            cjkTotal++;
            string key = $"…{tail(prev, 12)} + {head(cur, 12)}…";
            cjkPairs[key] = cjkPairs.TryGetValue(key, out int c) ? c + 1 : 1;
        }
    }
}
Console.WriteLine($"汉+汉折行共 {cjkTotal} 处（去重后 {cjkPairs.Count} 种）");
foreach (var kv in cjkPairs)
    Console.WriteLine($"  ×{kv.Value,-3} {kv.Key}");

// ==========================================================
// 八、B 类（汉+汉）候选规矩对拍。
//
//    ⚠️ 先说一个陷阱：tools\脚本\比对成绩.ps1 的 Norm 会把「汉字⏎汉字」
//    **直接删掉标记**（$1$2），所以拿正式判分口径去量「合并汉+汉折行」，
//    结果是**永远一分不差** —— 量具对这一类根本是瞎的。
//    所以这里改用**对折行敏感**的口径：⏎ 换成空格、一律不删。
//    这样「本来该两行」和「一句话被折了」才会算出不同的分。
// ==========================================================
Console.WriteLine();
Console.WriteLine("===== 八、B 类候选规矩对拍（对折行敏感的口径）=====");

// 折行敏感：⏎ 当空格，不做任何删除。
string NormFoldSensitive(string s)
{
    if (s is null) return "";
    s = s.Replace("\u23ce", " ").Replace("\r", " ").Replace("\n", " ");
    s = Regex.Replace(s, @"\s+", " ");
    return s.Trim();
}

int Edit(string a, string b)
{
    var prev = new int[b.Length + 1];
    var cur = new int[b.Length + 1];
    for (int j = 0; j <= b.Length; j++) prev[j] = j;
    for (int i = 1; i <= a.Length; i++)
    {
        cur[0] = i;
        for (int j = 1; j <= b.Length; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
        }
        Array.Copy(cur, prev, cur.Length);
    }
    return prev[b.Length];
}

// 标准答案：`序号|内容`；没有 `|` 的行是上一段的续行，用**空格**接上
// （答案里的真换行在折行敏感口径下也是空格）。
string materialDir = Path.GetFullPath(Path.Combine(archiveDir, "..", "..", "素材"));
var answers = new List<string>();
foreach (string af in new[] { "验收标准答案.txt", "验收标准答案2.txt", "验收标准答案3.txt" })
{
    string ap = Path.Combine(materialDir, af);
    if (!File.Exists(ap)) { Console.WriteLine($"  ⚠️ 找不到答案文件：{ap}"); continue; }
    foreach (string raw in File.ReadAllLines(ap))
    {
        string l = raw.Trim();
        if (l.Length == 0) continue;
        int bar = l.IndexOf('|');
        if (bar >= 0) answers.Add(l[(bar + 1)..]);
        else if (answers.Count > 0) answers[^1] += " " + l;
    }
}
Console.WriteLine($"  标准答案 {answers.Count} 段（续行用空格接上）");

// 候选规矩：相邻两段「前段末是汉字 且 后段首是汉字」→ 不加分隔符合并。
string MergeCjkFolds(string text)
{
    string[] parts = text.Split(NewlineMark[0]);
    var outp = new System.Text.StringBuilder(parts[0]);
    for (int i = 1; i < parts.Length; i++)
    {
        string prev = parts[i - 1], curP = parts[i];
        bool join = prev.Length > 0 && curP.Length > 0 && isCjk(prev[^1]) && isCjk(curP[0]);
        outp.Append(join ? "" : NewlineMark.ToString());
        outp.Append(curP);
    }
    return outp.ToString();
}

// 0=对 1=错 -1=认不了
int Grade(string text, out string matched, out string shown)
{
    shown = NormFoldSensitive(text);
    int best = int.MaxValue, second = int.MaxValue; matched = "";
    foreach (string a in answers)
    {
        int d = Edit(shown, NormFoldSensitive(a));
        if (d < best) { second = best; best = d; matched = a; }
        else if (d < second) second = d;
    }
    int tol = Math.Max(4, NormFoldSensitive(matched).Length / 12);
    // 完全一样才算过（原来的写法让 best==0 永远认领不了 → 谎报「对 0 条」）
    if (best == 0) return 0;
    // 【口径对齐项目判分规矩】tools\脚本\比对成绩.ps1：编辑距离的容差**只用来认领归属**，
    //   认领之后 D>0 仍然算判错（用户 2026-10-09 定的：差一个字符 = 整段判错，不给部分分）。
    //   所以这里容差内也返回 1（判错），才是项目的真口径。
    bool clearlyBest = second - best < 2 && second < best * 2;
    if (!(best <= tol && clearlyBest)) return -1;
    return 1;
}

// 正式判分口径（tools\脚本\比对成绩.ps1 的 Norm）：汉字之间的折行标记**直接删掉**。
// ⚠️ 只留在这里当**反面教材**，不参与对拍 —— 用它去量「合并汉+汉折行」，
//    结果永远是「一分不差」，等于拿瞎的量具验规矩（第 12 次量具事故那类）。
string NormOfficial(string s)
{
    if (s is null) return "";
    s = Regex.Replace(s, @"([\u4e00-\u9fff])\s*\u23ce\s*([\u4e00-\u9fff])", "$1$2");
    s = s.Replace("\u23ce", " ");
    s = Regex.Replace(s, @"([\u4e00-\u9fff])\s+([\u4e00-\u9fff])", "$1$2");
    s = Regex.Replace(s, @"\s+", " ");
    return s.Trim();
}

int nowOk = 0, nowBad = 0, nowUnk = 0;
int newOk = 0, newBad = 0, newUnk = 0;
int touched = 0;
var regressions = new List<string>();
var fixes = new List<string>();

foreach (string file in Directory.GetFiles(archiveDir, "*.txt").OrderBy(f => f))
{
    foreach (string line in File.ReadAllLines(file))
    {
        if (!line.StartsWith("原文: ")) continue;
        string rawText = line.Substring(4);
        string mergedText = MergeCjkFolds(rawText);
        bool changed = mergedText != rawText;
        if (changed) touched++;

        int before = Grade(rawText, out _, out _);
        if (before == 0) nowOk++; else if (before == 1) nowBad++; else nowUnk++;

        int after = Grade(mergedText, out string afterAns, out string afterShown);
        if (!changed) { if (before == 0) newOk++; else if (before == 1) newBad++; else newUnk++; continue; }
        if (after == 0) newOk++; else if (after == 1) newBad++; else newUnk++;

        string cut = afterShown.Length > 70 ? afterShown[..70] : afterShown;
        if (before == 0 && after != 0)
            regressions.Add($"  {Path.GetFileName(file)}  【{afterAns[..Math.Min(26, afterAns.Length)]}】  →  {cut}");
        if (before != 0 && after == 0)
            fixes.Add($"  {Path.GetFileName(file)}  →  {cut}");
    }
}

Console.WriteLine($"  会被这条规矩动到的记录：{touched} 条（汉+汉折行）");
Console.WriteLine($"  现状：对 {nowOk}，错 {nowBad}，认不了 {nowUnk}");
Console.WriteLine($"  合并后：对 {newOk}，错 {newBad}，认不了 {newUnk}");
Console.WriteLine($"  → 净变化：{(newOk - nowOk):+#;-#;0} 对，{(newBad - nowBad):+#;-#;0} 错");
Console.WriteLine($"  新救回 {fixes.Count} 条，弄坏 {regressions.Count} 条");
if (regressions.Count > 0)
{
    Console.WriteLine("  ⚠️ 合并之后从「对」变「错」的（前 25 条）：");
    foreach (string r in regressions.Take(25)) Console.WriteLine(r);
}
if (fixes.Count > 0)
{
    Console.WriteLine("  ✅ 合并之后从「错/认不了」变「对」的（前 25 条）：");
    foreach (string r in fixes.Take(25)) Console.WriteLine(r);
}

if (fail > 0) Environment.Exit(1);

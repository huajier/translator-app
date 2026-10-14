// 真实数据挖掘 v2：把 app 日志里每一次英文取词的「两票原文」还原出来，配成对，做规矩体检。
//
// 为什么重写：v1 按「看到最终原文就结块」配对，会把上一条取词残留的记录错配进来
// （实证：app-20261012.log 12:42:55 那块混进了别处的 140 字 eng 记录，上下文里根本没有 eng）。
// v2 改用软件自己打的块边界：「开始取词：区域 …」开头、「取词成功：…」结尾，块内归属。
//
// 数据来源都是真机实测留下的日志，不经合成。
// 只读日志与标准答案，不碰界面、不碰鼠标。

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
var logDir = Path.Combine(root, "tools", "日志");
var matDir = Path.Combine(root, "tools", "素材");

var tsRe = new Regex(@"\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]");
var startRe = new Regex(@"开始取词：区域 (?<w>\d+)×(?<h>\d+)");
var okRe = new Regex(@"取词成功：通道=(?<ch>\S+)");
var winRe = new Regex(@"第(?:一|二)遍原文：(?<t>.*?)（人话分\s*(?<score>\d+)[，,]");
var tessRe = new Regex(@"Tesseract 认完（eng）：用时 (?<ms>\d+) 毫秒，置信度 (?<conf>[\d.]+)，(?<chars>\d+) 字");
var wordRe = new Regex(@"【(?<w>[^】]*)】\s*(?<score>[\d.]+)\s*词典(?<d>[✓✗])");
var finRe = new Regex(@"最终原文：(?<t>.*)$");

string Alnum(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray());

// ── 真值：三份材料的标准答案 ──
var truths = new List<(string id, string text)>();
foreach (var f in Directory.GetFiles(matDir, "验收标准答案*.txt"))
    foreach (var line in File.ReadAllLines(f, Encoding.UTF8))
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
        var p = line.Split('|');
        // 材料1/2/3 没有编号行；带编号的（E1|…）取后半段
        var txt = p.Length >= 2 ? p[1] : p[0];
        if (Alnum(txt).Length >= 6) truths.Add((Path.GetFileNameWithoutExtension(f), txt.Trim()));
    }
// 去重（三份材料里 E1/R1 等有重复答案）
truths = truths.GroupBy(t => Alnum(t.text)).Select(g => g.First()).ToList();
Console.WriteLine($"真值库：{truths.Count} 条（来自 tools\\素材\\验收标准答案*.txt）");

int Edit(string a, string b)
{
    var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
    for (int j = 0; j <= b.Length; j++) prev[j] = j;
    for (int i = 1; i <= a.Length; i++)
    {
        cur[0] = i;
        for (int j = 1; j <= b.Length; j++)
            cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        (prev, cur) = (cur, prev);
    }
    return prev[b.Length];
}

// 给一段文本找真值：字母数字串编辑距离最近的那条答案，且必须够近、且明显胜过第二名。
// ⚠️ 容差别卡太死：E6 那种「末尾被咬掉」的失败离答案只差 3 个字符，
// 上一版 tol=max(2,len/40)=2 把最该看的病例 A16 自己滤掉了（实证：最终成绩显示 70/70 全对，而 A16 明明是错的）。
(string text, int dist, int margin)? MatchTruth(string text)
{
    var a = Alnum(text);
    if (a.Length < 6) return null;
    var scored = truths.Select(t => (text: t.text, d: Edit(a, Alnum(t.text)))).OrderBy(x => x.d).ToList();
    int best = scored[0].d, second = scored.Count > 1 ? scored[1].d : int.MaxValue;
    int tol = Math.Max(4, a.Length / 12);
    if (best > tol) return null;
    // 必须明显胜过第二名，防止「谁都不像」时乱认领
    if (second - best < 2 && second < best * 2) return null;
    return (scored[0].text, best, second - best);
}

bool IsSubseq(string small, string big)
{
    int i = 0;
    foreach (char c in big) { if (i < small.Length && small[i] == c) i++; }
    return i == small.Length;
}

// ── 逐块扫描 ──
var blocks = new List<Block>();
foreach (var file in Directory.GetFiles(logDir, "app-*.log").OrderBy(f => f))
{
    var lines = File.ReadAllLines(file, Encoding.UTF8);
    Block cur = null;
    string ts = "";
    for (int i = 0; i < lines.Length; i++)
    {
        var line = lines[i];
        var tm = tsRe.Match(line); if (tm.Success) ts = tm.Groups[1].Value;

        var sm = startRe.Match(line);
        if (sm.Success) { cur = new Block { File = Path.GetFileName(file), Line = i + 1, Ts = ts, W = int.Parse(sm.Groups["w"].Value), H = int.Parse(sm.Groups["h"].Value) }; continue; }
        if (cur is null) continue;

        var wm = winRe.Match(line);
        if (wm.Success)
        {
            var t = wm.Groups["t"].Value.Trim();
            if (t.Length > 0) { if (cur.WinText.Length == 0) cur.WinText = t; else cur.WinText2 = t; }
            continue;
        }

        var em = tessRe.Match(line);
        if (em.Success)
        {
            var rec = new Rec
            {
                Conf = double.Parse(em.Groups["conf"].Value, CultureInfo.InvariantCulture),
                Chars = int.Parse(em.Groups["chars"].Value),
            };
            if (i + 1 < lines.Length && lines[i + 1].Contains("词级自报（eng）"))
            {
                rec.WordsRaw = lines[i + 1];
                foreach (Match m in wordRe.Matches(lines[i + 1]))
                {
                    if (!double.TryParse(m.Groups["score"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sc)) sc = -1;
                    rec.Words.Add((m.Groups["w"].Value, sc, m.Groups["d"].Value == "✓"));
                }
                rec.Truncated = lines[i + 1].Length >= 1050;
            }
            cur.Eng.Add(rec);
            continue;
        }

        var fm = finRe.Match(line);
        if (fm.Success) cur.Final = fm.Groups["t"].Value.Trim();

        var om = okRe.Match(line);
        if (om.Success)
        {
            cur.Channel = om.Groups["ch"].Value;
            blocks.Add(cur); cur = null;
        }
    }
}

Console.WriteLine($"扫出取词块 {blocks.Count} 个（按「开始取词」…「取词成功」划块）");
var engBlocks = blocks.Where(b => b.Channel == "Tesseract" && b.Eng.Count >= 1).ToList();
Console.WriteLine($"  通道=Tesseract 且跑到 eng 的：{engBlocks.Count}");
var twoVote = engBlocks.Where(b => b.Eng.Count >= 2).ToList();
Console.WriteLine($"  其中跑了两票的：{twoVote.Count}");
Console.WriteLine($"     两票都带完整词表的：{twoVote.Count(b => b.Eng[0].Words.Count > 0 && b.Eng[1].Words.Count > 0)}");
Console.WriteLine($"     有条目被截断的（1059 字符上限）：{twoVote.Count(b => b.Eng.Any(r => r.Truncated))}");
Console.WriteLine();

// ── 只留下「能对上标准答案」的块，做规矩体检 ──
// 关键过滤：只看**纯英文**的块。中文段/俄文段虽然也跑了 eng，但那是兜底，
// 拿英文规矩去量它们属于量具错配（实证：中文段 今天天气不错适合出门 的 eng 票是 DATA LE）。
bool HasCjkOrCyrillic(string s) => s.Any(c =>
    (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3040 && c <= 0x30FF) ||
    (c >= 0x0400 && c <= 0x04FF) || (c >= 0xAC00 && c <= 0xD7AF));

var graded = new List<(Block b, string truth, bool firstOk, bool secondOk, bool finalOk)>();
int skippedNonEnglish = 0;
foreach (var b in twoVote)
{
    if (b.Eng[0].Words.Count == 0 || b.Eng[1].Words.Count == 0) continue;
    var m = MatchTruth(b.Final);
    if (m is null) continue;
    if (HasCjkOrCyrillic(b.Final)) { skippedNonEnglish++; continue; }
    string truth = m.Value.text;
    if (HasCjkOrCyrillic(truth)) { skippedNonEnglish++; continue; }
    string t1 = string.Join(" ", b.Eng[0].Words.Select(x => x.w));
    string t2 = string.Join(" ", b.Eng[1].Words.Select(x => x.w));
    graded.Add((b, truth, Alnum(t1) == Alnum(truth), Alnum(t2) == Alnum(truth), Alnum(b.Final) == Alnum(truth)));
}
Console.WriteLine($"（已剔除中文段/俄文段 {skippedNonEnglish} 个——那两类跑 eng 只是兜底，英文规矩量不了它们）");
Console.WriteLine($"【可用样本】两票齐全 + 能对上标准答案的块：{graded.Count} 个");
if (graded.Count == 0) { Console.WriteLine("样本不足，无法体检。"); return; }

int gBoth = graded.Count(g => g.firstOk && g.secondOk);
int gOnlyFirst = graded.Count(g => g.firstOk && !g.secondOk);
int gOnlySecond = graded.Count(g => !g.firstOk && g.secondOk);
int gNeither = graded.Count(g => !g.firstOk && !g.secondOk);
Console.WriteLine($"  第一票对+第二票对 {gBoth}；【第一票对、第二票错】{gOnlyFirst}；第一票错+第二票对 {gOnlySecond}；两边都错 {gNeither}");
Console.WriteLine($"  现软件最终结果对：{graded.Count(g => g.finalOk)}/{graded.Count}");
Console.WriteLine();

var risky = graded.Where(g => g.firstOk && !g.secondOk).ToList();
if (risky.Count > 0)
{
    Console.WriteLine($"⚠️ 「第二票更差」的真实样本 {risky.Count} 个——放宽门槛一旦在这里出手就是弄坏：");
    foreach (var (b, truth, _, _, _) in risky.Take(8))
    {
        string t1 = string.Join(" ", b.Eng[0].Words.Select(x => x.w));
        string t2 = string.Join(" ", b.Eng[1].Words.Select(x => x.w));
        Console.WriteLine($"   {b.File}:{b.Line} {b.Ts}  框 {b.W}×{b.H}");
        Console.WriteLine($"      第一票[{Trunc(t1, 70)}]");
        Console.WriteLine($"      第二票[{Trunc(t2, 70)}]");
    }
}
Console.WriteLine();

// ── 规矩对拍（用真实样本，不用合成图）──
var rules = new (string name, Func<string, string, bool> decide)[]
{
    ("R0 现行：第二票更长且第一票一字不差是它的开头",
        (f, s) => Alnum(s).Length > Alnum(f).Length && Alnum(s).StartsWith(Alnum(f), StringComparison.Ordinal)),
    ("R6 第二票更长，且第一票的字符按原顺序都在第二票里（子序列）",
        (f, s) => Alnum(s).Length > Alnum(f).Length && IsSubseq(Alnum(f), Alnum(s))),
    ("R9 第二票更长 + 两份只差 ≤4 个字符（编辑距离）",
        (f, s) => Alnum(s).Length > Alnum(f).Length && Edit(Alnum(f), Alnum(s)) <= 4),
    ("R11 第一票的字符按原顺序都在第二票里（去掉「更长」这条）",
        (f, s) => Alnum(f) != Alnum(s) && IsSubseq(Alnum(f), Alnum(s))),
    // R9 不是 R6 的超集：子序列能容许「第二票多一大截」，编辑距离≤4 反而会漏掉那种。
    // 所以真正要上线的是并集，必须单独量一遍，确认它不会把 R6 已经能救的丢掉。
    ("R12 并集 = R6 或 R9 任一成立（拟上线版）",
        (f, s) => (Alnum(s).Length > Alnum(f).Length && IsSubseq(Alnum(f), Alnum(s)))
               || (Alnum(s).Length > Alnum(f).Length && Edit(Alnum(f), Alnum(s)) <= 4)),
    // 固定「≤4」在**短串**上等于没门槛：第一票只有 3 个字母时，4 个字符的差别已经超过它本身的长度，
    // 那就是「只要第二票更长就换」（R3，已证会弄坏）。这条按第一票长度的比例卡。
    ("R13 第二票更长 + 编辑距离 ≤ max(1, 第一票长度/4)",
        (f, s) => Alnum(s).Length > Alnum(f).Length && Edit(Alnum(f), Alnum(s)) <= Math.Max(1, Alnum(f).Length / 4)),
    // 真正的候选：固定 4 与比例 1/4 取小。长串（本次全是 59~61）上跟 R9 一模一样，
    // 短串上自动收紧——3 个字母的短词要求差 ≤1，而不是宽容到 4（那等于「随便换」）。
    ("R14 第二票更长 + 编辑距离 ≤ min(4, max(1, 第一票长度/4))（拟上线）",
        (f, s) => Alnum(s).Length > Alnum(f).Length
               && Edit(Alnum(f), Alnum(s)) <= Math.Min(4, Math.Max(1, Alnum(f).Length / 4))),
    // ★ 最终上线版：并集。子序列救「第二票多认一大截」，比例编辑距离救「零星几个字对不上」。
    // 「多认一大截」在历史数据里 0 例，所以不能只靠它；「零星差几个字」不能只靠子序列（A5 就是）。
    ("R15 并集 = 子序列 或 比例编辑距离（最终上线版）",
        (f, s) => Alnum(s).Length > Alnum(f).Length && Alnum(f) != Alnum(s)
               && (IsSubseq(Alnum(f), Alnum(s))
                || Edit(Alnum(f), Alnum(s)) <= Math.Min(4, Math.Max(1, Alnum(f).Length / 4)))),
};
Console.WriteLine("================ 规矩对拍（真机实测样本） ================");
foreach (var (name, decide) in rules)
{
    int fire = 0, fix = 0, brk = 0;
    var details = new List<string>();
    foreach (var (b, truth, fOk, sOk, _) in graded)
    {
        string t1 = string.Join(" ", b.Eng[0].Words.Select(x => x.w));
        string t2 = string.Join(" ", b.Eng[1].Words.Select(x => x.w));
        if (!decide(t1, t2)) continue;
        fire++;
        if (!fOk && sOk) { fix++; details.Add($"     ✅救回 {b.File}:{b.Line} {b.Ts} 框{b.W}×{b.H}"); }
        else if (fOk && !sOk) { brk++; details.Add($"     ❌弄坏 {b.File}:{b.Line} {b.Ts} 框{b.W}×{b.H}"); }
        else details.Add($"     ⚪无变化 {b.File}:{b.Line} {b.Ts} 框{b.W}×{b.H}");
    }
    Console.WriteLine($"{name}");
    Console.WriteLine($"    出手 {fire}/{graded.Count}：救回 {fix}，弄坏 {brk}");
    foreach (var d in details) Console.WriteLine(d);
}
Console.WriteLine();
// ── R6 与 R9 的包含关系体检：决定上线版到底要一个条件还是两个 ──
// 子序列成立时，编辑距离恰好 = 第二票多认的字母数。所以「多认得少」的那种 R9 已经覆盖，
// R6 只在「第二票多认一大截（>4）」时才有额外价值。这里数一遍到底有没有这种仗。
int onlyR6 = 0, onlyR9 = 0, both = 0;
var onlyR6Samples = new List<string>();
foreach (var (b, truth, fOk, sOk, _) in graded)
{
    string a1 = Alnum(string.Join(" ", b.Eng[0].Words.Select(x => x.w)));
    string a2 = Alnum(string.Join(" ", b.Eng[1].Words.Select(x => x.w)));
    if (a2.Length <= a1.Length) continue;
    bool sub = IsSubseq(a1, a2);
    bool ed = Edit(a1, a2) <= 4;
    if (sub && ed) both++;
    else if (sub) { onlyR6++; if (onlyR6Samples.Count < 5) onlyR6Samples.Add($"       多认 {a2.Length - a1.Length} 字：[{Trunc(string.Join(" ", b.Eng[0].Words.Select(x => x.w)), 50)}] → [{Trunc(string.Join(" ", b.Eng[1].Words.Select(x => x.w)), 50)}]"); }
    else if (ed) onlyR9++;
}
Console.WriteLine("================ R6 / R9 包含关系体检 ================");
Console.WriteLine($"  两条都成立 {both} 个；只有子序列成立（多认 >4 字）{onlyR6} 个；只有编辑距离成立 {onlyR9} 个");
foreach (var s in onlyR6Samples) Console.WriteLine(s);
Console.WriteLine(onlyR6 == 0
    ? "  → 没有任何一仗需要「子序列」这条：单用编辑距离就够，代码可以只留一个条件。"
    : "  → 有仗只能靠子序列救：上线版必须保留并集（两个条件）。");
Console.WriteLine();

// ── 「固定 ≤4」在短串上会不会等于没门槛 ──
// 第一票只有 3 个字母时，≤4 已经超过它本身的长度 = 只要第二票更长就换（R3，已证会弄坏）。
// 数一遍 R9 出手的那些仗里，第一票到底有多长。
var r9Lens = graded
    .Where(g => { string a1 = Alnum(string.Join(" ", g.b.Eng[0].Words.Select(x => x.w)));
                  string a2 = Alnum(string.Join(" ", g.b.Eng[1].Words.Select(x => x.w)));
                  return a2.Length > a1.Length && Edit(a1, a2) <= 4; })
    .Select(g => Alnum(string.Join(" ", g.b.Eng[0].Words.Select(x => x.w))).Length)
    .OrderBy(x => x).ToList();
Console.WriteLine("================ R9 出手样本的第一票长度分布 ================");
Console.WriteLine($"  {r9Lens.Count} 次出手，第一票长度：{string.Join("、", r9Lens)}");
Console.WriteLine($"  最短 {r9Lens.Min()}，最长 {r9Lens.Max()}");
Console.WriteLine();

// graded 只覆盖了能对上标准答案的一部分。真上线时规矩会在**全部**两票块上出手，
// 所以必须单独数一遍：在量不了的块里它会不会动手。
var gradedSet = new HashSet<int>(graded.Select(g => g.b.Line));
Console.WriteLine("================ 出手范围体检（含量不了的块） ================");
Console.WriteLine($"两票齐全的块共 {twoVote.Count(b => b.Eng[0].Words.Count > 0 && b.Eng[1].Words.Count > 0)} 个；其中能对答案的 {graded.Count} 个，量不了的 {twoVote.Count(b => b.Eng[0].Words.Count > 0 && b.Eng[1].Words.Count > 0) - graded.Count} 个");
foreach (var (name, decide) in rules)
{
    int inGraded = 0, ungraded = 0;
    var samples = new List<string>();
    foreach (var b in twoVote)
    {
        if (b.Eng[0].Words.Count == 0 || b.Eng[1].Words.Count == 0) continue;
        string t1 = string.Join(" ", b.Eng[0].Words.Select(x => x.w));
        string t2 = string.Join(" ", b.Eng[1].Words.Select(x => x.w));
        if (!decide(t1, t2)) continue;
        if (gradedSet.Contains(b.Line)) inGraded++;
        else
        {
            ungraded++;
            if (samples.Count < 3 && !HasCjkOrCyrillic(t1) && !HasCjkOrCyrillic(t2))
                samples.Add($"       [量不了] {b.File}:{b.Line} 框{b.W}×{b.H} 一[{Trunc(t1, 55)}] 二[{Trunc(t2, 55)}]");
        }
    }
    Console.WriteLine($"  {name[..3]}  能对答案的块里出手 {inGraded}；量不了的块里出手 {ungraded}");
    foreach (var s in samples) Console.WriteLine(s);
}
Console.WriteLine();

// ── 现软件最终还错的（真机实测里的真败绩）──
var failed = graded.Where(g => !g.finalOk).ToList();
Console.WriteLine($"================ 现软件最终仍然错的 {failed.Count} 个（真机实测） ================");
foreach (var (b, truth, fOk, sOk, _) in failed)
{
    string t1 = string.Join(" ", b.Eng[0].Words.Select(x => x.w));
    string t2 = string.Join(" ", b.Eng[1].Words.Select(x => x.w));
    Console.WriteLine($"── {b.File}:{b.Line} {b.Ts} 框 {b.W}×{b.H} ──");
    Console.WriteLine($"   标准答案：{truth}");
    Console.WriteLine($"   第一票  {b.Eng[0].Conf:0.00}：{t1}   [对={fOk}]");
    Console.WriteLine($"   第二票  {b.Eng[1].Conf:0.00}：{t2}   [对={sOk}]");
    Console.WriteLine($"   最终原文：{b.Final}");
    string a1 = Alnum(t1), a2 = Alnum(t2), an = Alnum(b.Final), at = Alnum(truth);
    Console.WriteLine($"   距离：第一票vs答案 {Edit(a1, at)}，第二票vs答案 {Edit(a2, at)}，最终vs答案 {Edit(an, at)}");
    Console.WriteLine($"   R6 会不会出手：{(a2.Length > a1.Length && IsSubseq(a1, a2) ? "会" : "不会")}");
    Console.WriteLine();
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

class Rec
{
    public double Conf; public int Chars; public string WordsRaw = "";
    public bool Truncated;
    public List<(string w, double sc, bool d)> Words = new();
}
class Block
{
    public string File = "", Ts = "", WinText = "", WinText2 = "", Final = "", Channel = "";
    public int Line, W, H;
    public List<Rec> Eng = new();
}

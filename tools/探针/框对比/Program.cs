// 裁剪框对比探针：把产品里 FindTextBounds 的「列阈值比例」从 12% 改动之前，
// 先在图库 277 张图上量一遍：改完之后每张图判对还是判错、有没有从对变错。
//
// 产品里 FindTextBounds 是 private 且写死 0.12，探针不改它，
// 而是把同一段算法复制一份、把比例做成参数，三种改法各跑一遍对比：
//   base = 现状（12%）
//   r30  = 列阈值直接提到 30%（跟行扫描一致）
//   edge = 窄改法：先用 12% 找框，只有当框贴死图片左右边缘（说明是背景撑开的）才用 30% 重找
//
// 判分规则与 tools\脚本\比对回放.ps1 一致：归一化后编辑距离 ≤ 阈值算对。
//
// 用法：dotnet run --project tools\探针\框对比\框对比.csproj -c Release

using System.Drawing;
using System.Drawing.Imaging;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tesseract;
using TranslatorApp.Core.Capture;

string root = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
string rel = Path.Combine(root, "release", "v0.3.4.2");
string tessdata = Path.Combine(rel, "tessdata");
string x64 = Path.Combine(rel, "x64");

Native.SetDllDirectory(x64);
var pending = Directory.GetFiles(x64, "*.dll").ToList();
bool progress = true;
while (progress && pending.Count > 0)
{
    progress = false;
    for (int i = pending.Count - 1; i >= 0; i--)
    {
        try { NativeLibrary.Load(pending[i]); pending.RemoveAt(i); progress = true; } catch { }
    }
}

string corpus = Path.Combine(root, "tools", "语料");
string manifestPath = Path.Combine(corpus, "清单.json");
if (!File.Exists(manifestPath)) { Console.WriteLine($"找不到清单：{manifestPath}"); return 1; }

using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
var images = new List<(string File, int Mat, string Id, string Answer)>();
foreach (var e in doc.RootElement.GetProperty("images").EnumerateArray())
{
    string f = e.GetProperty("file").GetString();
    int mat = e.TryGetProperty("mat", out var mEl) ? mEl.GetInt32() : 0;
    string id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
    string ans = e.TryGetProperty("answer", out var aEl) ? aEl.GetString() : "";
    images.Add((f, mat, id, ans));
}
Console.WriteLine($"清单 {images.Count} 张");

// 56 段的标准答案
var answers = new Dictionary<string, string>();
int matNo = 0;
foreach (string name in new[] { "验收标准答案.txt", "验收标准答案2.txt", "验收标准答案3.txt" })
{
    matNo++;
    string p = Path.Combine(root, "tools", "素材", name);
    if (!File.Exists(p)) { Console.WriteLine($"缺答案：{p}"); return 1; }
    string? curId = null;
    var curText = new StringBuilder();
    foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
    {
        if (line.TrimStart().StartsWith("#") || line.Trim().Length == 0) continue;
        var mm = System.Text.RegularExpressions.Regex.Match(line, @"^([A-Z]\d+)\|(.*)$");
        if (mm.Success)
        {
            if (curId != null) answers[$"材料{matNo}-{curId}"] = Norm(curText.ToString());
            curId = mm.Groups[1].Value;
            curText.Clear().Append(mm.Groups[2].Value);
        }
        else if (curId != null) curText.Append(' ').Append(line.Trim());
    }
    if (curId != null) answers[$"材料{matNo}-{curId}"] = Norm(curText.ToString());
}
Console.WriteLine($"标准答案 {answers.Count} 段");
Console.WriteLine();

// 每个材料判「对」的容错：与 比对回放.ps1 一致，短句只准差 1~2 字
int Tolerance(string want) => Math.Max(1, (int)(want.Length * 0.02)) + 1;

using var engine = new TesseractEngine(tessdata, "eng", EngineMode.Default);
engine.SetVariable("tessedit_pageseg_mode", "6");
engine.SetVariable("classify_enable_learning", "0");

int[] windows = { 15, 25, 35 };
string[] modes = { "base", "r30", "edge" };

// 每段每种改法：只要这一段里有一张图判对，就算这段「这条路能救」
var segOk = new Dictionary<string, Dictionary<string, bool>>();
foreach (var (file, mat, id, ans) in images)
{
    string segKey = mat == 0 ? $"实战-{id}" : $"材料{mat}-{id}";
    if (!segOk.ContainsKey(segKey))
    {
        var d = new Dictionary<string, bool>();
        foreach (string m in modes) d[m] = false;
        segOk[segKey] = d;
    }
}

var sb = new StringBuilder();
var flips = new List<string>();   // base 对、改法错
var fixes = new List<string>();   // base 错、改法对

foreach (var (file, mat, id, ans) in images)
{
    string path = Path.Combine(corpus, file);
    if (!File.Exists(path)) { sb.AppendLine($"[缺图] {file}"); continue; }

    string want = mat == 0 ? Norm(ans) : answers.TryGetValue($"材料{mat}-{id}", out var wa) ? wa : "";
    int tol = want.Length == 0 ? 0 : Tolerance(want);
    string segKey = mat == 0 ? $"实战-{id}" : $"材料{mat}-{id}";

    sb.AppendLine($"{file}  ←{segKey}");
    sb.AppendLine($"    答案：{want}");

    string baseText = "", r30Text = "", edgeText = "";
    bool baseOk = false, r30Ok = false, edgeOk = false;

    foreach (string mode in modes)
    {
        using var raw = new Bitmap(path);
        using var light = ImagePreprocessor.MakeLightBackground(raw);
        using var probe = ImagePreprocessor.BinarizeAdaptive(light);

        var box = mode switch
        {
            "base" => FindTextBoundsRatio(probe, 0.12, 0.30),
            "r30" => FindTextBoundsRatio(probe, 0.30, 0.30),
            _ => FindTextBoundsEdge(probe),
        };

        if (box.Item1 < 0 || box.Item3 < 0)
        {
            sb.AppendLine($"    [{mode}] 放弃裁剪");
            continue;
        }

        int w = box.Item3 - box.Item1 + 1;
        int h = box.Item4 - box.Item2 + 1;
        if (w < probe.Width / 5 || h < probe.Height / 5)
        {
            sb.AppendLine($"    [{mode}] 裁得太小（{w}x{h}）");
            continue;
        }

        using var cropped = CropRect(light, box.Item1, box.Item2, w, h);
        string best = "";
        double bestConf = -1;
        foreach (int win in windows)
        {
            using var bin = ImagePreprocessor.BinarizeAdaptive(cropped, win, 0.2);
            using var ms = new MemoryStream();
            bin.Save(ms, ImageFormat.Png);
            using var pix = Pix.LoadFromMemory(ms.ToArray());
            using var page = engine.Process(pix);
            string t = (page.GetText() ?? "").Replace("\r", "").Replace("\n", " ").Trim();
            double conf = page.GetMeanConfidence();
            if (conf > bestConf) { bestConf = conf; best = t; }
        }

        string got = Norm(best);
        bool ok = want.Length > 0 && EditDist(got, want) <= tol;
        sb.AppendLine($"    [{mode}] 框 {w}x{h}  置信度 {bestConf:F3}  判{(ok ? "对" : "错")}：{got}");

        if (mode == "base") { baseText = got; baseOk = ok; }
        else if (mode == "r30") { r30Text = got; r30Ok = ok; }
        else { edgeText = got; edgeOk = ok; }

        if (ok) segOk[segKey][mode] = true;
    }

    if (want.Length > 0)
    {
        if (baseOk && !r30Ok) flips.Add($"{file}  ←{segKey}\n      原：{baseText}\n      30%：{r30Text}");
        if (baseOk && !edgeOk) flips.Add($"{file}  ←{segKey}\n      原：{baseText}\n      窄改：{edgeText}");
        if (!baseOk && r30Ok) fixes.Add($"{file}  ←{segKey}\n      原：{baseText}\n      30%：{r30Text}");
        if (!baseOk && edgeOk) fixes.Add($"{file}  ←{segKey}\n      原：{baseText}\n      窄改：{edgeText}");
    }
    sb.AppendLine();
}

string outDir = Path.Combine(root, "tools", "临时", "框对比");
Directory.CreateDirectory(outDir);
string outFile = Path.Combine(outDir, "对比明细.txt");
File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));

Console.WriteLine("---- 逐段成绩（这一段只要有一张图判对就算过）----");
int okBase = 0, okR30 = 0, okEdge = 0;
foreach (var kv in segOk)
{
    bool b = kv.Value["base"], r = kv.Value["r30"], e = kv.Value["edge"];
    if (b) okBase++;
    if (r) okR30++;
    if (e) okEdge++;
    if (b != r || b != e)
        Console.WriteLine($"  {kv.Key}: base={(b ? "对" : "错")}  r30={(r ? "对" : "错")}  edge={(e ? "对" : "错")}");
}
Console.WriteLine();
Console.WriteLine($"base 通过 {okBase}/{segOk.Count}");
Console.WriteLine($"r30  通过 {okR30}/{segOk.Count}");
Console.WriteLine($"edge 通过 {okEdge}/{segOk.Count}");
Console.WriteLine();
Console.WriteLine($"全部明细：{outFile}");
return 0;

// ---------- 归一化（与 比对回放.ps1 同一份）----------
static string Norm(string s)
{
    if (string.IsNullOrEmpty(s)) return "";
    s = System.Text.RegularExpressions.Regex.Replace(s, @"([\u4e00-\u9fff])\s*\u23ce\s*([\u4e00-\u9fff])", "$1$2");
    s = s.Replace('\u23ce', ' ');
    s = System.Text.RegularExpressions.Regex.Replace(s, @"([\u4e00-\u9fff])\s+([\u4e00-\u9fff])", "$1$2");
    s = System.Text.RegularExpressions.Regex.Replace(s, "[\u2018\u2019\u201B]", "'");
    s = System.Text.RegularExpressions.Regex.Replace(s, "[\u201C\u201D\u201F]", "\"");
    s = System.Text.RegularExpressions.Regex.Replace(s, "[\u041c\u043c][\u0411\u0431]", "MB");
    s = System.Text.RegularExpressions.Regex.Replace(s, "[Mm][Bb]", "MB");
    s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
    return s.Trim();
}

static int EditDist(string a, string b)
{
    int n = b.Length;
    var prev = new int[n + 1];
    var cur = new int[n + 1];
    for (int j = 0; j <= n; j++) prev[j] = j;
    for (int i = 1; i <= a.Length; i++)
    {
        cur[0] = i;
        char ci = a[i - 1];
        for (int j = 1; j <= n; j++)
        {
            int cost = ci == b[j - 1] ? 0 : 1;
            int m = prev[j - 1] + cost;
            if (prev[j] + 1 < m) m = prev[j] + 1;
            if (cur[j - 1] + 1 < m) m = cur[j - 1] + 1;
            cur[j] = m;
        }
        (prev, cur) = (cur, prev);
    }
    return prev[n];
}

// ===== 产品 FindTextBounds 的复制版，只把「列阈值比例」做成参数 =====
static (int, int, int, int) FindTextBoundsRatio(Bitmap bin, double colRatio, double rowRatio)
{
    int w = bin.Width, h = bin.Height;
    var rows = new int[h];
    long total = 0;
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            Color c = bin.GetPixel(x, y);
            int v = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            if (v < 128) { rows[y]++; total++; }
        }
    if (total == 0) return (-1, -1, -1, -1);

    int maxRow = 0;
    for (int y = 0; y < h; y++) if (rows[y] > maxRow) maxRow = rows[y];
    if (maxRow < 2) return (-1, -1, -1, -1);
    int rowThreshold = Math.Max(2, (int)(maxRow * rowRatio));

    int y0 = -1, y1 = -1;
    for (int y = 0; y < h; y++)
        if (rows[y] >= rowThreshold) { if (y0 < 0) y0 = y; y1 = y; }
    if (y0 < 0) return (-1, -1, -1, -1);

    var cols = new int[w];
    for (int y = y0; y <= y1; y++)
        for (int x = 0; x < w; x++)
        {
            Color c = bin.GetPixel(x, y);
            int v = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            if (v < 128) cols[x]++;
        }

    int maxCol = 0;
    for (int x = 0; x < w; x++) if (cols[x] > maxCol) maxCol = cols[x];
    if (maxCol < 2) return (-1, -1, -1, -1);
    int colThreshold = Math.Max(1, (int)(maxCol * colRatio));

    int x0 = -1, x1 = -1;
    for (int x = 0; x < w; x++)
        if (cols[x] >= colThreshold) { if (x0 < 0) x0 = x; x1 = x; }
    if (x0 < 0) return (-1, -1, -1, -1);

    x0 = Math.Max(0, x0 - 2);
    y0 = Math.Max(0, y0 - 2);
    x1 = Math.Min(w - 1, x1 + 2);
    y1 = Math.Min(h - 1, y1 + 2);
    return (x0, y0, x1, y1);
}

// 窄改法：先用 12% 找框，只有框贴死图片左右边缘时，才用 30% 重找一次
static (int, int, int, int) FindTextBoundsEdge(Bitmap bin)
{
    var box = FindTextBoundsRatio(bin, 0.12, 0.30);
    if (box.Item1 < 0) return box;
    bool leftStuck = box.Item1 == 0;
    bool rightStuck = box.Item3 >= bin.Width - 1;
    if (!leftStuck && !rightStuck) return box;
    return FindTextBoundsRatio(bin, 0.30, 0.30);
}

static Bitmap CropRect(Bitmap source, int x, int y, int w, int h)
{
    var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (Graphics g = Graphics.FromImage(dst))
        g.DrawImage(source, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
    return dst;
}

static class Native
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string lpPathName);
}

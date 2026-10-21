using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Tesseract;
using TranslatorApp.Core.Capture;

// 离线对比三种做法。**完全照产品的先后顺序**：先判深底、深底先反色，再放大 4 倍、再补边、再五路。
//
//   A 现状：补白边，直方图从补完边的图算
//   B 改色：补「这张图出现最多的颜色」，直方图从补完边的图算
//   C 改法：补白边不变，直方图从补边**前**的原图算（白边只保护边缘，不参与定阈值）
//
// 只读图、只跑引擎，不碰界面、不碰鼠标、不改产品代码。
// 用法：dotnet run --project tools\探针\白边E4\baibian.csproj -c Release -- 图1 图2 ...

var root = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
var rel = Path.Combine(root, "release", "v0.3.4.2");
var tessdata = Path.Combine(rel, "tessdata");
var x64 = Path.Combine(rel, "x64");

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

string[] targets;
if (args.Length > 0)
    targets = args.Select(a => Path.IsPathRooted(a) ? a : Path.Combine(root, a)).ToArray();
else
{
    var newest = new DirectoryInfo(Path.Combine(rel, "debug-screenshots"))
        .GetFiles("sel_*.png").OrderByDescending(f => f.LastWriteTime).First();
    targets = new[] { newest.FullName };
}

var engine = new TesseractEngine(tessdata, "eng");
engine.SetVariable("classify_enable_learning", "0");
engine.SetVariable("tessedit_pageseg_mode", "6");

var miRead = typeof(ImagePreprocessor).GetMethod("ReadPlanes", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException("ReadPlanes");
var miBinarize = typeof(ImagePreprocessor).GetMethod("BinarizePlane", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException("BinarizePlane");

string Run(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
    using var pix = Pix.LoadFromMemory(ms.ToArray());
    using var page = engine.Process(pix);
    return (page.GetText() ?? string.Empty).Trim();
}

Bitmap Pad(Bitmap source, int margin, Color color)
{
    var padded = new Bitmap(
        source.Width + margin * 2, source.Height + margin * 2, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(padded))
    {
        g.Clear(color);
        g.DrawImage(source, margin, margin, source.Width, source.Height);
    }
    return padded;
}

Color DominantColor(Bitmap bmp)
{
    var rc = new int[256];
    var gc = new int[256];
    var bc = new int[256];
    for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            Color c = bmp.GetPixel(x, y);
            rc[c.R]++; gc[c.G]++; bc[c.B]++;
        }
    int ArgMax(int[] a) { int best = 0; for (int i = 1; i < 256; i++) if (a[i] > a[best]) best = i; return best; }
    return Color.FromArgb(ArgMax(rc), ArgMax(gc), ArgMax(bc));
}

Bitmap[] FivePlanes(Bitmap padded, Bitmap histogramSource)
{
    var counts = new int[4][];
    var sums = new int[4][];
    var sq = new long[4][];
    for (int p = 0; p < 4; p++)
    {
        counts[p] = new int[256];
        sums[p] = new int[256];
        sq[p] = new long[256];
    }
    miRead.Invoke(null, new object[] { histogramSource, counts, sums, sq });

    var res = new Bitmap[5];
    for (int p = 0; p < 4; p++)
        res[p] = (Bitmap)miBinarize.Invoke(null, new object[] { padded, p, counts[p], sums[p], sq[p] })!;
    res[4] = ImagePreprocessor.BinarizeAdaptive(padded);
    return res;
}

string[] PlaneTexts(Bitmap big, Color padColor, bool histFromPadded)
{
    using var padded = Pad(big, big.Height / 5, padColor);
    var planes = FivePlanes(padded, histFromPadded ? padded : big);
    var texts = new string[5];
    try
    {
        for (int i = 0; i < 5; i++)
        {
            if (planes[i] is null) { texts[i] = string.Empty; continue; }
            using var b = Pad(planes[i], 10, Color.White);
            texts[i] = Run(b);
        }
    }
    finally { foreach (var p in planes) p?.Dispose(); }
    return texts;
}

// P = 直接调产品自己的 PrepareAllPlanes，验证改完的代码端到端是什么效果
string[] ProductPlaneTexts(Bitmap big, bool padPlanes = true)
{
    var planes = ImagePreprocessor.PrepareAllPlanes(big);
    var texts = new string[5];
    try
    {
        for (int i = 0; i < 5; i++)
        {
            if (planes[i] is null) { texts[i] = string.Empty; continue; }
            if (!padPlanes) { texts[i] = Run(planes[i]); continue; }
            using var b = Pad(planes[i], 10, Color.White);
            texts[i] = Run(b);
        }
    }
    finally { foreach (var p in planes) p?.Dispose(); }
    return texts;
}

string[] names = { "红", "绿", "蓝", "灰", "局" };
string Dump(string[] t) => string.Join(" ", t.Select((x, i) => $"{names[i]}{x.Length}"));

Console.WriteLine($"共 {targets.Length} 张图。照产品顺序：判深底 → 深底反色 → 放大 4 倍 → 补边 → 五路。");
Console.WriteLine("A=现状  B=补边改底色  C=白边不变、直方图只算原图");
Console.WriteLine();

int bBroke = 0, cBroke = 0, bFixed = 0, cFixed = 0;
int aAllDead = 0, bAllDead = 0, cAllDead = 0;
int bChanged = 0, cChanged = 0;
int pFixed = 0, pBroke = 0;
int mFixed = 0, mBroke = 0, nFixed = 0, nBroke = 0;
int qFixedTotal = 0, qBrokeTotal = 0;

foreach (var path in targets)
{
    if (!File.Exists(path)) { Console.WriteLine($"跳过（不存在）：{path}"); continue; }

    using var captured = new Bitmap(path);

    // ── 产品第一步：判深底，深底先反色 ──
    bool deep = ImagePreprocessor.AnalyzeBackground(captured, out _, out _, out _);
    using Bitmap raw = deep ? ImagePreprocessor.Invert(captured) : new Bitmap(captured);

    Color mode = DominantColor(raw);
    using var big = ImagePreprocessor.UpscaleForForeign(raw);

    string[] a = PlaneTexts(big, Color.White, histFromPadded: true);
    string[] b = PlaneTexts(big, mode, histFromPadded: true);
    string[] c = PlaneTexts(big, Color.White, histFromPadded: false);
    string[] p = ProductPlaneTexts(big);
    string[] q = ProductPlaneTexts(big, padPlanes: false);

    // ── 10px 白边专项：产品喂给 eng 的第一张图就是「4 倍原图 + 10px 白边」 ──
    string bw, bm, bn;
    using (var t = Pad(big, 10, Color.White)) bw = Run(t);
    using (var t = Pad(big, 10, mode)) bm = Run(t);
    bn = Run(big);

    int aDead = a.Count(t => t.Length == 0);
    int bDead = b.Count(t => t.Length == 0);
    int cDead = c.Count(t => t.Length == 0);
    if (aDead == 5) aAllDead++;
    if (bDead == 5) bAllDead++;
    if (cDead == 5) cAllDead++;

    int bFixedUp = 0, bBrokeN = 0, bChangedN = 0, cFixedUp = 0, cBrokeN = 0, cChangedN = 0;
    for (int i = 0; i < 5; i++)
    {
        if (a[i].Length == 0 && b[i].Length > 0) bFixedUp++;
        if (a[i].Length > 0 && b[i].Length == 0) bBrokeN++;
        if (a[i].Length > 0 && b[i].Length > 0 && a[i] != b[i]) bChangedN++;
        if (a[i].Length == 0 && c[i].Length > 0) cFixedUp++;
        if (a[i].Length > 0 && c[i].Length == 0) cBrokeN++;
        if (a[i].Length > 0 && c[i].Length > 0 && a[i] != c[i]) cChangedN++;
    }
    if (bBrokeN > 0) bBroke++;
    if (cBrokeN > 0) cBroke++;
    if (bFixedUp > 0) bFixed++;
    if (cFixedUp > 0) cFixed++;
    if (bChangedN > 0) bChanged++;
    if (cChangedN > 0) cChanged++;

    int pFixedUp = 0, pBrokeN = 0;
    for (int i = 0; i < 5; i++)
    {
        if (a[i].Length == 0 && p[i].Length > 0) pFixedUp++;
        if (a[i].Length > 0 && p[i].Length == 0) pBrokeN++;
    }
    if (pBrokeN > 0) pBroke++;
    if (pFixedUp > 0) pFixed++;

    Console.WriteLine($"── {Path.GetFileName(path)}  {captured.Width}×{captured.Height}  {(deep ? "深底，已反色" : "浅底")}  补边色({mode.R},{mode.G},{mode.B})");
    Console.WriteLine($"   A 现状  {Dump(a),-30} 瞎 {aDead}/5");
    Console.WriteLine($"   B 改色  {Dump(b),-30} 瞎 {bDead}/5   救{bFixedUp} 瞎{bBrokeN} 变{bChangedN}");
    Console.WriteLine($"   C 改法  {Dump(c),-30} 瞎 {cDead}/5   救{cFixedUp} 瞎{cBrokeN} 变{cChangedN}");
    Console.WriteLine($"   P 产品  {Dump(p),-30} 瞎 {p.Count(t => t.Length == 0)}/5   {(c.SequenceEqual(p) ? "与 C 完全一致 ✅" : "与 C 不一致 ❌")}");
    Console.WriteLine($"   ── 10px 白边专项（4 倍原图直接喂 eng）");
    Console.WriteLine($"      白边 {bw.Length,4} 字 ｜ 底色边 {bm.Length,4} 字 ｜ 不补边 {bn.Length,4} 字");
    if (bw.Length == 0 && bm.Length > 0) mFixed++;
    if (bw.Length > 0 && bm.Length == 0) mBroke++;
    if (bw.Length == 0 && bn.Length > 0) nFixed++;
    if (bw.Length > 0 && bn.Length == 0) nBroke++;
    int qFixed = 0, qBroke = 0;
    for (int i = 0; i < 5; i++)
    {
        if (p[i].Length == 0 && q[i].Length > 0) qFixed++;
        if (p[i].Length > 0 && q[i].Length == 0) qBroke++;
    }
    qFixedTotal += qFixed;
    qBrokeTotal += qBroke;
    Console.WriteLine($"   ── 五路补 10px 白边 vs 不补（产品 P 列）");
    Console.WriteLine($"      {Dump(p),-30} ｜ 不补 {Dump(q),-30} 救{qFixed} 瞎{qBroke}");
    Console.WriteLine();
}

engine.Dispose();

Console.WriteLine("════════ 汇总 ════════");
Console.WriteLine($"  一共 {targets.Length} 张图");
Console.WriteLine($"  五路全瞎：A 现状 {aAllDead} 张 / B 改色 {bAllDead} 张 / C 改法 {cAllDead} 张");
Console.WriteLine($"  救回至少一路：B {bFixed} 张 / C {cFixed} 张");
Console.WriteLine($"  ★ 反而弄瞎某一路：B {bBroke} 张 / C {cBroke} 张   （越少越好，0 最好）");
Console.WriteLine($"  有某一路文字变了：B {bChanged} 张 / C {cChanged} 张");
Console.WriteLine($"  ── 产品实际（P = 改完的 PrepareAllPlanes）：救回 {pFixed} 张，弄瞎 {pBroke} 张");
Console.WriteLine();
Console.WriteLine("════════ 10px 白边专项汇总（4 倍原图喂 eng）════════");
Console.WriteLine($"  白边瞎、换底色边能救：{mFixed} 张");
Console.WriteLine($"  白边能认、换底色边变瞎：{mBroke} 张   ★ 越少越好");
Console.WriteLine($"  白边瞎、不补边能救：{nFixed} 张");
Console.WriteLine($"  白边能认、不补边变瞎：{nBroke} 张   ★ 越少越好");
Console.WriteLine();
Console.WriteLine("════════ 五路补边汇总（产品 P 列 vs 不补）════════");
Console.WriteLine($"  五路去掉 10px 白边后：救回 {qFixedTotal} 路，弄瞎 {qBrokeTotal} 路   ★ 弄瞎越少越好");

static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetDllDirectory(string lpPathName);
}

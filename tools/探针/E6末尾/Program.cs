using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using TranslatorApp.Core.Capture;
using Tesseract;

// 离线压测：拿 E6-1 那张存图，切出成百上千个「用户可能框出来的选区」，
// 每个都跑一遍软件真正的两票（第一票 = UpscaleForForeign，第二票 = PreprocessAndBinarize(UpscaleForForeign)），
// 目的是找出「第一票咬尾巴、第二票能救回来」的触发条件，好造一份能稳定复现的复测材料。
// 只读图、只跑引擎，不碰界面、不碰鼠标。

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
var shotDir = Path.Combine(root, "release", "V0.3.3.0", "debug-screenshots");
var tessdata = Path.Combine(root, "release", "V0.3.3.0", "tessdata");

// 主程序是靠 NativeMethods 预先把 x64 里的原生库逐个 LoadLibrary 的，这里照样先预加载。
var x64 = Path.Combine(AppContext.BaseDirectory, "x64");
SetDllDirectory(x64);
var pending = Directory.GetFiles(x64, "*.dll").ToList();
var loaded = 0;
bool progress = true;
while (progress && pending.Count > 0)
{
    progress = false;
    for (int i = pending.Count - 1; i >= 0; i--)
    {
        try { NativeLibrary.Load(pending[i]); loaded++; pending.RemoveAt(i); progress = true; } catch { }
    }
}
Console.WriteLine($"预加载原生库：成功 {loaded} 个，装不上 {pending.Count} 个（{x64}）");

// ── 断言：直接反射调用真程序集里那条规矩本身，不复制逻辑 ──
int fail = 0;
{
    var asm = Assembly.LoadFrom(Path.Combine(root, "release", "V0.3.3.0", "TranslatorApp.dll"));
    var type = asm.GetType("TranslatorApp.Core.Capture.OcrTextCapture");
    var m = type!.GetMethod("SecondVoteRecoversTail", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("找不到 SecondVoteRecoversTail");
    bool Check(string? a, string? b) => (bool)m.Invoke(null, new object?[] { a, b })!;

    (string? a, string? b, bool want, string why)[] cases =
    {
        ("Optical character recognition converts images of text into editable te",
         "Optical character recognition converts images of text into editable text.", true,  "E6 真错例：第一票咬掉尾巴，第二票补上"),
        ("Optical character recognition converts images of text into editable text.",
         "Optical character recognition converts images of text into editable te", false, "反过来：短的不能当长的用"),
        ("The quick brown fox jumps over the lazy dog",
         "The quick brown fox jumps over the lazy dog 89", true, "结尾多出数字也会触发（已知挡不住，如实记录）"),
        ("Optical character recognition converts images of text into editable text.",
         "Optical character recognition converts images of text into editak le.", false, "中间不一样 → 开头对不上 → 不动"),
        ("abc def", "xyz def", false, "完全不同 → 不动"),
        ("abc", "abc", false, "一样长 → 不动"),
        (null, "abc", false, "第一票没结果 → 不动"),
        ("abc", "   ", false, "第二票空 → 不动"),
    };
    foreach (var (a, b, want, why) in cases)
    {
        bool got = Check(a, b);
        bool ok = got == want;
        if (!ok) fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} 期望{want} 实得{got}  {why}");
    }
}
Console.WriteLine(fail == 0 ? "断言全部通过" : $"断言失败 {fail} 条");
if (fail > 0) Environment.Exit(1);
Console.WriteLine();

[DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool SetDllDirectory(string path);

var shot = Directory.GetFiles(shotDir, "*.png").OrderBy(p => p).First();
using var src = new Bitmap(shot);
Console.WriteLine($"基准存图：{Path.GetFileName(shot)}  {src.Width}×{src.Height}");
const string Target = "Opticalcharacterrecognitionconvertsimagesoftextintoeditabletext";
Console.WriteLine("（基准答案是 " + Target.Length + " 个字母数字）");
Console.WriteLine();

using var engine = new TesseractEngine(tessdata, "eng");

string Alnum(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());

(string text, float conf) Run(Bitmap img)
{
    using var ms = new MemoryStream();
    img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
    using var pix = Pix.LoadFromMemory(ms.ToArray());
    using var page = engine.Process(pix);
    return (page.GetText() ?? "", page.GetMeanConfidence());
}

// 基准：第一票（软件现在给英文用的那张图）
(int len, string text) First(Bitmap raw)
{
    using var up = ImagePreprocessor.UpscaleForForeign(new Bitmap(raw));
    var (t, _) = Run(up);
    return (Alnum(t).Length, t.Replace("\n", " ").Trim());
}
// 第二票：甲方案那张
(int len, string text) Second(Bitmap raw)
{
    using var up = ImagePreprocessor.UpscaleForForeign(new Bitmap(raw));
    using var bi = ImagePreprocessor.PreprocessAndBinarize(up);
    var (t, _) = Run(bi);
    return (Alnum(t).Length, t.Replace("\n", " ").Trim());
}

Bitmap Crop(int x, int y, int w, int h) => src.Clone(new Rectangle(x, y, w, h), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
Bitmap PadRight(int w, int extra)
{
    var bmp = new Bitmap(w + extra, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.White);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
    }
    return bmp;
}

int total = 0, caught = 0, secondOk = 0, both = 0;
var rows = new List<string>();

void Trial(string label, Bitmap img)
{
    total++;
    var (l1, t1) = First(img);
    bool firstBad = l1 < Target.Length;
    var (l2, t2) = Second(img);
    bool secondGood = l2 >= Target.Length;
    if (firstBad) caught++;
    if (secondGood) secondOk++;
    if (firstBad && secondGood) both++;
    rows.Add($"{label,-34} 第一票{l1,3}{(firstBad ? "❌" : "✅")}  第二票{l2,3}{(secondGood ? "✅" : "❌")}   [{Trunc(t1, 46)}] | [{Trunc(t2, 46)}]");
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

Console.WriteLine("【一】右边裁掉几像素（框右边没框到 / 框偏窄）");
for (int cut = 0; cut <= 8; cut++)
{
    var w = src.Width - cut;
    if (w < 20) break;
    using var img = Crop(0, 0, w, src.Height);
    Trial($"右边裁 {cut}px（{w}×{src.Height}）", img);
}

Console.WriteLine("【二】右边多留几像素白边（框右边留空）");
foreach (int extra in new[] { 2, 4, 6, 8, 12, 16, 24 })
{
    using var img = PadRight(src.Width, extra);
    Trial($"右边留白 {extra}px（{img.Width}×{img.Height}）", img);
}

Console.WriteLine("【三】上下裁切（框偏矮 / 偏高）");
foreach (int top in new[] { 0, 1, 2, 3 })
{
    for (int bot = top; bot <= 3; bot++)
    {
        int h = src.Height - top - bot;
        if (h < 10) continue;
        using var img = Crop(0, top, src.Width, h);
        Trial($"上裁 {top} 下裁 {bot}（{src.Width}×{h}）", img);
    }
}

Console.WriteLine("【四】左右同时偏（框没对齐行首）");
foreach (int left in new[] { 0, 1, 2, 3 })
{
    using var img = Crop(left, 0, src.Width - left, src.Height);
    Trial($"左边裁 {left}px（{img.Width}×{img.Height}）", img);
}

Console.WriteLine("【五】整张缩放（相当于换字号 / 换缩放比）");
foreach (double s in new[] { 0.80, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10, 1.25 })
{
    int w = (int)Math.Round(src.Width * s), h = (int)Math.Round(src.Height * s);
    if (h < 10) continue;
    var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
    }
    using (bmp) Trial($"缩放 {s:0.00}（{w}×{h}）", bmp);
}

Console.WriteLine();
Console.WriteLine("================ 逐条明细 ================");
foreach (var r in rows) Console.WriteLine(r);
Console.WriteLine();
Console.WriteLine($"一共试了 {total} 个选区：第一票咬尾巴 {caught} 个（{caught * 100.0 / total:0.0}%），第二票能救回来 {secondOk} 个（{secondOk * 100.0 / total:0.0}%）");
Console.WriteLine($"触发条件同时成立的（第一票错 + 第二票对）：{both} 个");
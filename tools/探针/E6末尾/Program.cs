using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using TranslatorApp.Core.Capture;
using Tesseract;

// 一次性离线试验：拿存图（E6-1 咬成 "te" 那次）换几种预处理，看哪一种能读全。
// 只读图、只跑引擎，不碰界面、不碰鼠标。

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
var shotDir = Path.Combine(root, "release", "V0.3.3.0", "debug-screenshots");
var tessdata = Path.Combine(root, "release", "V0.3.3.0", "tessdata");

// 主程序是靠 NativeMethods 预先把 x64 里的原生库逐个 LoadLibrary 的，
// 这里照样先预加载，否则 Tesseract 的封装找不到 leptonica。
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
//   甲方案唯一的非平凡逻辑就是 SecondVoteRecoversTail（严格前缀关系）。
//   这里用真实字符串跑一遍，正反两面都断掉，才算有检查。
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
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  期望{want} 实得{got}  {why}");
    }
}
Console.WriteLine(fail == 0 ? "断言全部通过" : $"断言失败 {fail} 条");
if (fail > 0) Environment.Exit(1);
Console.WriteLine();

[DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool SetDllDirectory(string path);

var shots = Directory.GetFiles(shotDir, "*.png").OrderBy(p => p).ToList();
Console.WriteLine($"存图 {shots.Count} 张：{shotDir}");
foreach (var s in shots) Console.WriteLine("   " + Path.GetFileName(s));

foreach (var shot in shots)
{
    Console.WriteLine();
    Console.WriteLine("==================================================");
    Console.WriteLine(Path.GetFileName(shot));
    using var src = new Bitmap(shot);
    Console.WriteLine($"原图 {src.Width}×{src.Height}");

    using var engine = new TesseractEngine(tessdata, "eng");

    var variants = new List<(string Name, Bitmap Img)>
    {
        ("原图（不处理）", new Bitmap(src)),
        ("Preprocess()", ImagePreprocessor.Preprocess(src)),
        ("PreprocessAndBinarize()", ImagePreprocessor.PreprocessAndBinarize(src)),
        ("UpscaleForForeign()（外文放大2倍+灰度）", ImagePreprocessor.UpscaleForForeign(src)),
        ("ToGrayscaleOnly()", ImagePreprocessor.ToGrayscaleOnly(src)),
        ("OtsuBinarize(原图)", ImagePreprocessor.OtsuBinarize(new Bitmap(src))),
        ("MakeLightBackground()", ImagePreprocessor.MakeLightBackground(new Bitmap(src))),
        ("PreprocessRedChannel(原图, 2.0)", ImagePreprocessor.PreprocessRedChannel(new Bitmap(src), 2.0)),
        ("PreprocessRedChannel(原图, 1.0)", ImagePreprocessor.PreprocessRedChannel(new Bitmap(src), 1.0)),
        ("PrepareRedChannelGray()", ImagePreprocessor.PrepareRedChannelGray(new Bitmap(src))),
        ("PreprocessAndBinarize(UpscaleForForeign)", ImagePreprocessor.PreprocessAndBinarize(ImagePreprocessor.UpscaleForForeign(src))),
        ("UpscaleForForeign(PreprocessAndBinarize)", ImagePreprocessor.UpscaleForForeign(ImagePreprocessor.PreprocessAndBinarize(src))),
    };

    foreach (var (name, img) in variants)
    {
        string text; float conf; int alnum = 0;
        try
        {
            using var ms = new MemoryStream();
            img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            using var pix = Pix.LoadFromMemory(ms.ToArray());
            using var page = engine.Process(pix);
            text = page.GetText() ?? "";
            conf = page.GetMeanConfidence();
            alnum = text.Count(char.IsLetterOrDigit);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {name,-42} 失败：{ex.GetType().Name} {ex.Message}");
            img.Dispose();
            continue;
        }
        var flag = text.TrimEnd().EndsWith("text.") ? "✅" : "❌";
        Console.WriteLine($"  {name,-42} {flag} 置信度 {conf,5:0.00} 字母数字 {alnum,3}  →  [{text.Trim()}]");
        img.Dispose();
    }
}
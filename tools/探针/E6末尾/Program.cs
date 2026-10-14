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
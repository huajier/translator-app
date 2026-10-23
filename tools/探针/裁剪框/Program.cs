// 裁剪框探针：查 BATTLEBOARD-1\1.png 走「深色裁边兜底」时，
// FindTextBounds 到底把框定在哪里、那个多出来的 `-` 是从哪个窗口出来的。
//
// 完全照产品顺序：MakeLightBackground → BinarizeAdaptive(探针) → FindTextBounds → CropRect → 三个窗口。
// 只读图、只跑引擎，不碰产品代码。
//
// 用法：dotnet run --project tools\探针\裁剪框\jiancai.csproj -c Release

using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Tesseract;
using TranslatorApp.Core.Capture;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

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

string srcPath = Path.Combine(root, "tools", "语料", "实战", "BATTLEBOARD-1", "1.png");
if (!File.Exists(srcPath)) { Console.WriteLine($"找不到图：{srcPath}"); return 1; }

string outDir = Path.Combine(root, "tools", "临时", "裁剪框");
Directory.CreateDirectory(outDir);

using var raw = new Bitmap(srcPath);
Console.WriteLine($"原图 {raw.Width}×{raw.Height}");
Console.WriteLine();

// 第 1 步：深底翻浅底（产品里 TryCroppedLocalRescue 的第一句）
using var light = ImagePreprocessor.MakeLightBackground(raw);
Console.WriteLine($"MakeLightBackground 后 {light.Width}×{light.Height}");
light.Save(Path.Combine(outDir, "1-翻成浅底.png"), ImageFormat.Png);

// 第 2 步：BinarizeAdaptive 默认参数探一次（产品里 PrepareCroppedLocal 的第一句）
using (var probe = ImagePreprocessor.BinarizeAdaptive(light))
{
    probe.Save(Path.Combine(outDir, "2-探针二值化.png"), ImageFormat.Png);

    // 第 3 步：反射调私有 FindTextBounds，看它把框定在哪
    var mi = typeof(ImagePreprocessor).GetMethod(
        "FindTextBounds", BindingFlags.NonPublic | BindingFlags.Static);
    if (mi is null) { Console.WriteLine("反射找不到 FindTextBounds"); return 1; }

    var box = ((int X0, int Y0, int X1, int Y1))mi.Invoke(null, new object[] { probe });
    Console.WriteLine($"FindTextBounds 给出：x0={box.X0} y0={box.Y0} x1={box.X1} y1={box.Y1}");
    Console.WriteLine($"  → 宽 {box.X1 - box.X0 + 1}，高 {box.Y1 - box.Y0 + 1}");
    if (box.X0 < 0) Console.WriteLine("  （返回 -1，说明它放弃了裁剪）");
    Console.WriteLine();

    // 探针图里那一小块是什么样
    if (box.X0 >= 0)
    {
        int w = box.X1 - box.X0 + 1, h = box.Y1 - box.Y0 + 1;
        using var cropProbe = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropProbe))
            g.DrawImage(probe, new Rectangle(0, 0, w, h),
                        new Rectangle(box.X0, box.Y0, w, h), GraphicsUnit.Pixel);
        cropProbe.Save(Path.Combine(outDir, "3-裁出来的探针图.png"), ImageFormat.Png);
    }
}

// 第 4 步：真跑产品那条兜底，三个窗口各是什么结果
Console.WriteLine("---- PrepareCroppedLocal + eng 识别 ----");
using var engine = new TesseractEngine(tessdata, "eng", EngineMode.Default);
engine.SetVariable("tessedit_pageseg_mode", "6");
engine.SetVariable("classify_enable_learning", "0");

Bitmap[] candidates = ImagePreprocessor.PrepareCroppedLocal(light);
Console.WriteLine($"候选图 {candidates.Length} 张");
int[] windows = { 15, 25, 35 };
try
{
    for (int i = 0; i < candidates.Length; i++)
    {
        candidates[i].Save(Path.Combine(outDir, $"4-窗口{windows[i]}.png"), ImageFormat.Png);

        using var ms = new MemoryStream();
        candidates[i].Save(ms, ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(ms.ToArray());
        using var page = engine.Process(pix);
        string text = (page.GetText() ?? "").Replace("\r", "").Replace("\n", "／").Trim();
        Console.WriteLine($"窗口 {windows[i],2}（{candidates[i].Width}×{candidates[i].Height}）" +
                          $"置信度 {page.GetMeanConfidence():F3}  文本【{text}】");
    }
}
finally
{
    foreach (var c in candidates) c?.Dispose();
}

Console.WriteLine();
Console.WriteLine($"图都存到：{outDir}");
return 0;

static class Native
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string lpPathName);
}

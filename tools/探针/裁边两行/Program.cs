// 裁边两行探针：验证「深色裁边兜底」是不是只框到第一行、把第二行切掉。
//
// 现场：实战 2026.10.23-13.02 的 2.png（411×130，WISHLIST NOW 白字 / ON STEAM 黄字）
//   各路都不过 0.78 → 走裁边兜底 → 只返回 `-WISHLIST NOW`，第二行整个没了。
// 要回答两件事：
//   ① FindTextBounds 到底框在哪，第二行的像素在不在框里；
//   ② 同一张浅底图，不裁（整张）直接二阶化识别，能读出几行。
//
// 只读图、只跑引擎，不改产品代码。
// 用法：dotnet run --project tools\探针\裁边两行\裁边两行.csproj -c Release

using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Tesseract;
using TranslatorApp.Core.Capture;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

string root = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
string rel = Path.Combine(root, "release", "v0.3.4.3");
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

string[] names = { "2.png", "5.png" };
using var engine = new TesseractEngine(tessdata, "eng", EngineMode.Default);
engine.SetVariable("tessedit_pageseg_mode", "6");   // 产品用的就是 6
engine.SetVariable("classify_enable_learning", "0");

string outDir = Path.Combine(root, "tools", "临时3", "裁边两行");
Directory.CreateDirectory(outDir);

foreach (string name in names)
{
    string srcPath = Path.Combine(root, "tools", "语料", "实战", "2026.10.23-13.02", name);
    if (!File.Exists(srcPath)) { Console.WriteLine($"找不到图：{srcPath}"); continue; }

    Console.WriteLine($"================ {name} ================");
    using var raw = new Bitmap(srcPath);
    Console.WriteLine($"原图 {raw.Width}×{raw.Height}");

    using var light = ImagePreprocessor.MakeLightBackground(raw);
    light.Save(Path.Combine(outDir, $"{name}-1-翻浅底.png"), ImageFormat.Png);

    using (var probe = ImagePreprocessor.BinarizeAdaptive(light))
    {
        probe.Save(Path.Combine(outDir, $"{name}-2-探针二值化.png"), ImageFormat.Png);

        var mi = typeof(ImagePreprocessor).GetMethod(
            "FindTextBounds", BindingFlags.NonPublic | BindingFlags.Static);
        var box = ((int X0, int Y0, int X1, int Y1))mi!.Invoke(null, new object[] { probe });
        int bh = box.Y1 - box.Y0 + 1;
        Console.WriteLine($"FindTextBounds：x0={box.X0} y0={box.Y0} x1={box.X1} y1={box.Y1}" +
                          $"（宽 {box.X1 - box.X0 + 1}，高 {bh}，整图高 {probe.Height}）");
        Console.WriteLine($"  框底 y={box.Y1}，整图底 y={probe.Height - 1}，" +
                          $"下部被切掉 {probe.Height - 1 - box.Y1} 行像素");
        Console.WriteLine();

        // 逐行统计前景像素，看文字到底分布在哪些行
        Console.Write("  逐行前景占比（每 5 行一格，■>5% ▪>1% ·>0）：");
        var counts = RowForeground(probe);
        for (int y = 0; y < counts.Length; y += 5)
        {
            double r = counts[y];
            Console.Write(r > 0.05 ? "■" : r > 0.01 ? "▪" : r > 0 ? "·" : "　");
        }
        Console.WriteLine();
        Console.WriteLine();
    }

    // ---- 产品那条路：裁完再二值化三次 ----
    Console.WriteLine("---- 产品路：PrepareCroppedLocal（裁边）+ eng ----");
    Bitmap[] cands = ImagePreprocessor.PrepareCroppedLocal(light);
    int[] windows = { 15, 25, 35 };
    try
    {
        for (int i = 0; i < cands.Length; i++)
        {
            cands[i].Save(Path.Combine(outDir, $"{name}-3-裁边窗口{windows[i]}.png"), ImageFormat.Png);
            Console.WriteLine($"  窗口 {windows[i],2}（{cands[i].Width}×{cands[i].Height}）{Recognize(engine, cands[i])}");
        }
    }
    finally { foreach (var c in cands) c?.Dispose(); }

    // ---- B② 候选：整张浅底不裁，直接三个窗口 ----
    Console.WriteLine("---- 候选路：整张不裁 + eng ----");
    foreach (int w in windows)
    {
        using var whole = ImagePreprocessor.BinarizeAdaptive(light, w, 0.2);
        whole.Save(Path.Combine(outDir, $"{name}-4-整张窗口{w}.png"), ImageFormat.Png);
        Console.WriteLine($"  窗口 {w,2}（{whole.Width}×{whole.Height}）{Recognize(engine, whole)}");
    }
    Console.WriteLine();
}

Console.WriteLine($"图都存到：{outDir}");
return 0;

static string Recognize(TesseractEngine engine, Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    using var pix = Pix.LoadFromMemory(ms.ToArray());
    using var page = engine.Process(pix);
    string text = (page.GetText() ?? "").Replace("\r", "").Replace("\n", "／").Trim();
    return $"置信度 {page.GetMeanConfidence():F3}  文本【{text}】";
}

static double[] RowForeground(Bitmap bin)
{
    var rows = new double[bin.Height];
    var data = bin.LockBits(new Rectangle(0, 0, bin.Width, bin.Height),
                            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        unsafe
        {
            for (int y = 0; y < bin.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                int dark = 0;
                for (int x = 0; x < bin.Width; x++)
                    if (row[x * 4] < 128) dark++;
                rows[y] = (double)dark / bin.Width;
            }
        }
    }
    finally { bin.UnlockBits(data); }
    return rows;
}

static class Native
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string lpPathName);
}

// 深色实验探针：同一张深色模式截图，换不同预处理 / 语言模型，看 Tesseract 认成什么、置信度多少。
//
// 用法：cd tools\探针\深色实验 ; dotnet run -c Release
//
// 这个工程只做实验，不改主程序，也不产出任何被主程序引用的文件。

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Tesseract;

string root = FindRoot();
CopyNativeLibraries(root);

string tessdata = Path.Combine(root, "release", "V0.3.4.1", "tessdata");
string srcPath = Path.Combine(root, "tools", "日志", "实测期错例", "第2批-20261017", "图2-13.00-BATTLEBOARD没认出.png");

if (!File.Exists(srcPath))
{
    Console.WriteLine($"找不到截图：{srcPath}");
    return 1;
}
if (!Directory.Exists(tessdata))
{
    Console.WriteLine($"找不到语言包：{tessdata}");
    return 1;
}

Console.WriteLine($"截图：{Path.GetFileName(srcPath)}");
Console.WriteLine();

using var orig = new Bitmap(srcPath);
Console.WriteLine($"原图 {orig.Width}×{orig.Height}");
Console.WriteLine();

var variants = new List<(string Name, Bitmap Img)>
{
    ("A 原图+灰度",              ToGray(orig)),
    ("B 反色+灰度",              ToGray(Invert(orig))),
    ("C 反色+全局阈值(Otsu)",     Otsu(Invert(orig))),
    ("D 反色+局部阈值(Sauvola)",  Sauvola(Invert(orig), 25, 0.2)),
    ("E 反色+Otsu+放大4倍",       Scale(Otsu(Invert(orig)), 4)),
    ("F 反色+Sauvola+放大4倍",    Scale(Sauvola(Invert(orig), 25, 0.2), 4)),
    ("G 不反色+Sauvola",          Sauvola(orig, 25, 0.2)),
    ("H 反色+Sauvola+裁上下10%",  Crop(Sauvola(Invert(orig), 25, 0.2), 0.10)),
    ("I 反色+Otsu+裁上下25%",     Crop(Otsu(Invert(orig)), 0.25)),
    ("J 反色+Otsu+裁上下30%",     Crop(Otsu(Invert(orig)), 0.30)),
    ("K 反色+灰度+裁上下30%",      Crop(ToGray(Invert(orig)), 0.30)),
    ("L 原图灰度+裁上下30%",       Crop(ToGray(orig), 0.30)),
    ("M 反色灰度+裁25%+放大2倍",     Scale(Crop(ToGray(Invert(orig)), 0.25), 2)),
    ("N 反色灰度+裁25%+放大3倍",     Scale(Crop(ToGray(Invert(orig)), 0.25), 3)),
    ("O 反色Otsu+裁25%+放大2倍",     Scale(Crop(Otsu(Invert(orig)), 0.25), 2)),
    ("P 反色Otsu+裁25%+放大3倍",     Scale(Crop(Otsu(Invert(orig)), 0.25), 3)),
    ("Q 反色Otsu+膨胀1",            Dilate(Otsu(Invert(orig)), 1)),
    ("R 反色Otsu+膨胀2",            Dilate(Otsu(Invert(orig)), 2)),
    ("S 反色Otsu+膨胀1+裁25%+放大3倍", Scale(Crop(Dilate(Otsu(Invert(orig)), 1), 0.25), 3)),
    ("T 反色Sauvola+裁左右10%",      CropSides(Sauvola(Invert(orig), 25, 0.2), 0.10)),
    ("U 反色Sauvola+裁左右10%上下10%", Crop(CropSides(Sauvola(Invert(orig), 25, 0.2), 0.10), 0.10)),
    ("V 反色灰度+裁左右10%",         CropSides(ToGray(Invert(orig)), 0.10)),
    ("W 反色Sauvola+裁左右15%",      CropSides(Sauvola(Invert(orig), 25, 0.2), 0.15)),
    ("X 反色Sauvola+裁左右10%上下15%", Crop(CropSides(Sauvola(Invert(orig), 25, 0.2), 0.10), 0.15)),
    ("Y 反色Sauvola+裁四周10%+放大2倍", Scale(Crop(CropSides(Sauvola(Invert(orig), 25, 0.2), 0.10), 0.10), 2)),
    ("Z 反色Sauvola+裁四周10%+放大3倍", Scale(Crop(CropSides(Sauvola(Invert(orig), 25, 0.2), 0.10), 0.10), 3)),
    ("AA 反色Sauvola(25,.2)+自动裁文字边界", CropToText(Sauvola(Invert(orig), 25, 0.2))),
    ("AB 反色Sauvola(15,.25)+自动裁文字边界", CropToText(Sauvola(Invert(orig), 15, 0.25))),
    ("AC 反色Sauvola(25,.25)+自动裁文字边界", CropToText(Sauvola(Invert(orig), 25, 0.25))),
    ("AD 先裁灰度再Sauvola(25,.2)", CropThenBinarize(Invert(orig), 25, 0.2)),
    ("AE 先裁灰度再Sauvola(15,.2)", CropThenBinarize(Invert(orig), 15, 0.2)),
    ("AF 先裁灰度再Sauvola(15,.25)", CropThenBinarize(Invert(orig), 15, 0.25)),
    ("AG 先裁灰度再Sauvola(35,.25)", CropThenBinarize(Invert(orig), 35, 0.25)),
};

var engines = new Dictionary<string, TesseractEngine>();
TesseractEngine Engine(string lang, string psm, string mode)
{
    string key = lang + "|" + psm + "|" + mode;
    if (engines.TryGetValue(key, out var e)) return e;
    var created = new TesseractEngine(tessdata, lang, EngineMode.Default);
    created.SetVariable("tessedit_pageseg_mode", psm);
    created.SetVariable("classify_enable_learning", "0");
    if (mode == "无词典")
    {
        created.SetVariable("load_system_dawg", "0");
        created.SetVariable("load_freq_dawg", "0");
    }
    else if (mode == "不罚词典")
    {
        created.SetVariable("language_model_penalty_non_dict_word", "0");
        created.SetVariable("language_model_penalty_non_freq_dict_word", "0");
    }
    engines[key] = created;
    return created;
}

Console.WriteLine("变体                             语言  psm 词典     置信度   文本");
Console.WriteLine("--------------------------------------------------------------------------------");
foreach (var (name, img) in variants)
{
    foreach (string lang in new[] { "eng" })
    {
        foreach (string mode in new[] { "正常", "无词典", "不罚词典" })
        {
            try
            {
                using var ms = new MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                using var pix = Pix.LoadFromMemory(ms.ToArray());
                using var page = Engine(lang, "6", mode).Process(pix);
                string text = (page.GetText() ?? string.Empty).Replace("\r", "").Replace("\n", "⏎").Trim();
                if (text.Length > 44) text = text.Substring(0, 44) + "…";
                Console.WriteLine($"{name,-32} {lang,-4} 6   {mode,-8} {page.GetMeanConfidence(),-8:F3} {text}");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException;
                string detail = inner is null ? "" : $" | 内层: {inner.GetType().Name}: {inner.Message}";
                Console.WriteLine($"{name,-32} {lang,-4} 6   {mode,-8} 出错    {ex.GetType().Name}: {ex.Message}{detail}");
            }
        }
    }
    img.Dispose();
}

Console.WriteLine("\n================ 局部阈值参数扫描（反色 + 裁四周10%） ================");
using (var croppedSrc = Crop(CropSides(Invert(orig), 0.10), 0.10))
{
    foreach (int win in new[] { 15, 25, 35, 45 })
    {
        foreach (double k in new[] { 0.10, 0.15, 0.20, 0.25, 0.30 })
        {
            using var img = Sauvola(croppedSrc, win, k);
            try
            {
                using var ms = new MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                using var pix = Pix.LoadFromMemory(ms.ToArray());
                using var page = Engine("eng", "6", "正常").Process(pix);
                string text = (page.GetText() ?? "").Replace("\r", "").Replace("\n", "⏎").Trim();
                if (text.Length > 40) text = text.Substring(0, 40) + "…";
                Console.WriteLine($"window={win,2} k={k:F2}   分数 {page.GetMeanConfidence(),-6:F3} {text}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"window={win,2} k={k:F2}   出错 {ex.GetType().Name}");
            }
        }
    }
}

foreach (var e in engines.Values) e.Dispose();

// ==================== 追问：为什么 Otsu 那一路把 R 认成 N ====================
// 把两种二值化的图存下来肉眼对比，再把每个字符的候选项打出来。
string shotDir = Path.Combine(root, "tools", "日志", "实验图");
Directory.CreateDirectory(shotDir);
foreach (var (name, img) in variants)
{
    string key = name.Substring(0, 1);
    if (key is "B" or "C" or "D" or "M" or "P")
    {
        string p = Path.Combine(shotDir, $"{key}_{name.Substring(2).Replace("/", "_").Replace("+", "_")}.png");
        img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
    }
}
Console.WriteLine($"\n关键变体的图已存到 {shotDir}");

Console.WriteLine("\n================ 逐字符（eng，正常词典） ================");
foreach (string want in new[] { "B", "C", "M", "P", "Q", "S" })
{
    var hit = variants.FirstOrDefault(v => v.Item1.StartsWith(want + " "));
    if (hit.Item2 is null) continue;
    Console.WriteLine($"\n--- {hit.Item1} ---");
    try
    {
        using var ms = new MemoryStream();
        hit.Item2.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(ms.ToArray());
        using var eng2 = new TesseractEngine(tessdata, "eng", EngineMode.Default);
        eng2.SetVariable("tessedit_pageseg_mode", "6");
        eng2.SetVariable("classify_enable_learning", "0");
        using var page = eng2.Process(pix);
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            do
            {
                do
                {
                    if (!iter.TryGetBoundingBox(PageIteratorLevel.Symbol, out var rect)) continue;
                    string ch = iter.GetText(PageIteratorLevel.Symbol) ?? "";
                    float conf = iter.GetConfidence(PageIteratorLevel.Symbol);
                    Console.WriteLine($"   x={rect.X1,4} '{ch}' {conf,6:F1}");
                } while (iter.Next(PageIteratorLevel.Symbol));
            } while (iter.Next(PageIteratorLevel.Word));
        } while (iter.Next(PageIteratorLevel.TextLine));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   出错：{ex.GetType().Name}: {ex.Message}");
    }
}

return 0;

// ==================== 下面全是小工具 ====================

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "TranslatorApp", "TranslatorApp.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("没找到项目根目录");
}

// 用和主程序同一套 5.5.3 原生库覆盖 NuGet 自带的，否则引擎版本对不上、结论不可比。
static void CopyNativeLibraries(string root)
{
    string src = Path.Combine(root, "assets", "引擎", "tesseract-5.5.3-win");
    if (!Directory.Exists(src))
    {
        Console.WriteLine($"警告：找不到原生库目录 {src}，将用 NuGet 自带版本");
        return;
    }
    string dst = Path.Combine(AppContext.BaseDirectory, "x64");
    Directory.CreateDirectory(dst);
    int n = 0;
    foreach (string f in Directory.GetFiles(src, "*.dll"))
    {
        string name = Path.GetFileName(f);
        // 两处都放：Tesseract 封装按 x64\ 找，Windows 加载器按 exe 同级找。
        File.Copy(f, Path.Combine(dst, name), true);
        File.Copy(f, Path.Combine(AppContext.BaseDirectory, name), true);
        n++;
    }
    Console.WriteLine($"已把 {n} 个原生库覆盖到 {dst} 和 exe 同级目录");

    // 预加载进进程：Tesseract 的托管封装按名字找 leptonica/tesseract，
    // 不预先 Load 的话在探针里会报 DllNotFoundException（主程序靠 NativeLibraryPreloader 做同样的事）。
    int loaded = 0;
    foreach (string f in Directory.GetFiles(dst, "*.dll"))
    {
        try { NativeLibrary.Load(f); loaded++; } catch { }
    }
    Console.WriteLine($"预加载原生库 {loaded}/{n} 个");
}

static Bitmap ToGray(Bitmap source)
{
    var dst = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
    for (int y = 0; y < source.Height; y++)
    {
        for (int x = 0; x < source.Width; x++)
        {
            Color c = source.GetPixel(x, y);
            int g = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            dst.SetPixel(x, y, Color.FromArgb(255, g, g, g));
        }
    }
    return dst;
}

static Bitmap Invert(Bitmap source)
{
    var dst = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
    for (int y = 0; y < source.Height; y++)
    {
        for (int x = 0; x < source.Width; x++)
        {
            Color c = source.GetPixel(x, y);
            dst.SetPixel(x, y, Color.FromArgb(255, 255 - c.R, 255 - c.G, 255 - c.B));
        }
    }
    return dst;
}

static Bitmap Otsu(Bitmap source)
{
    using Bitmap gray = ToGray(source);
    int w = gray.Width, h = gray.Height;
    var hist = new int[256];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            hist[gray.GetPixel(x, y).R]++;

    int total = w * h;
    long sum = 0;
    for (int i = 0; i < 256; i++) sum += (long)i * hist[i];

    long sumB = 0;
    int wB = 0, threshold = 0;
    double best = 0;
    for (int t = 0; t < 256; t++)
    {
        wB += hist[t];
        if (wB == 0) continue;
        int wF = total - wB;
        if (wF == 0) break;
        sumB += (long)t * hist[t];
        double mB = (double)sumB / wB;
        double mF = (double)(sum - sumB) / wF;
        double between = (double)wB * wF * (mB - mF) * (mB - mF);
        if (between > best) { best = between; threshold = t; }
    }

    var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            int v = gray.GetPixel(x, y).R > threshold ? 255 : 0;
            dst.SetPixel(x, y, Color.FromArgb(255, v, v, v));
        }
    }
    return dst;
}

// 裁掉左右各 fraction 宽度（背景纹理和暗角主要在两侧）
static Bitmap CropSides(Bitmap source, double fraction)
{
    int cut = (int)(source.Width * fraction);
    if (cut <= 0 || source.Width - 2 * cut < 8) return (Bitmap)source.Clone();
    var rect = new Rectangle(cut, 0, source.Width - 2 * cut, source.Height);
    return source.Clone(rect, source.PixelFormat);
}

// 膨胀：让黑（字）变大。取邻域最小值。
static Bitmap Dilate(Bitmap source, int radius)
{
    int w = source.Width, h = source.Height;
    var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            int min = 255;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= h) continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = x + dx;
                    if (nx < 0 || nx >= w) continue;
                    int v = source.GetPixel(nx, ny).R;
                    if (v < min) min = v;
                }
            }
            dst.SetPixel(x, y, Color.FromArgb(255, min, min, min));
        }
    }
    return dst;
}

// 与 src\Core\Capture\ImagePreprocessor.cs 的 BinarizeAdaptive 同算法（Sauvola，dynamicRange=128）
static Bitmap Sauvola(Bitmap source, int window, double k)
{
    const double dynamicRange = 128.0;
    using Bitmap gray = ToGray(source);
    int w = gray.Width, h = gray.Height;
    int half = Math.Max(1, window / 2);

    var px = new byte[w * h];
    var integral = new long[(w + 1) * (h + 1)];
    var integralSq = new long[(w + 1) * (h + 1)];

    for (int y = 0; y < h; y++)
    {
        long rowSum = 0, rowSq = 0;
        for (int x = 0; x < w; x++)
        {
            int lum = gray.GetPixel(x, y).R;
            px[y * w + x] = (byte)lum;
            rowSum += lum;
            rowSq += (long)lum * lum;
            integral[(y + 1) * (w + 1) + (x + 1)] = integral[y * (w + 1) + (x + 1)] + rowSum;
            integralSq[(y + 1) * (w + 1) + (x + 1)] = integralSq[y * (w + 1) + (x + 1)] + rowSq;
        }
    }

    var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    for (int y = 0; y < h; y++)
    {
        int y0 = Math.Max(0, y - half), y1 = Math.Min(h - 1, y + half);
        for (int x = 0; x < w; x++)
        {
            int x0 = Math.Max(0, x - half), x1 = Math.Min(w - 1, x + half);
            long sum = integral[(y1 + 1) * (w + 1) + (x1 + 1)] - integral[y0 * (w + 1) + (x1 + 1)]
                     - integral[(y1 + 1) * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
            long sumSq = integralSq[(y1 + 1) * (w + 1) + (x1 + 1)] - integralSq[y0 * (w + 1) + (x1 + 1)]
                       - integralSq[(y1 + 1) * (w + 1) + x0] + integralSq[y0 * (w + 1) + x0];
            long count = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
            double mean = (double)sum / count;
            double variance = Math.Max(0.0, (double)sumSq / count - mean * mean);
            double stddev = Math.Sqrt(variance);
            double threshold = mean * (1.0 + k * (stddev / dynamicRange - 1.0));
            int v = px[y * w + x] < threshold ? 0 : 255;
            dst.SetPixel(x, y, Color.FromArgb(255, v, v, v));
        }
    }
    return dst;
}

static Bitmap Scale(Bitmap source, int factor)
{
    var dst = new Bitmap(source.Width * factor, source.Height * factor, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(dst);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.DrawImage(source, new Rectangle(0, 0, dst.Width, dst.Height));
    return dst;
}

static Bitmap Crop(Bitmap source, double fraction)
{
    int cut = (int)Math.Round(source.Height * fraction);
    int h = source.Height - cut * 2;
    if (h < 4) return (Bitmap)source.Clone();
    var dst = new Bitmap(source.Width, h, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(dst);
    g.DrawImage(source, new Rectangle(0, 0, source.Width, h), new Rectangle(0, cut, source.Width, h), GraphicsUnit.Pixel);
    return dst;
}

// 自动找文字的边界框：二值图上先按行投影找「字所在的行带」，再只在行带内按列投影找左右边界。
// 判据用「该行墨迹数达到最多那一行的三成」——文字行都很满，零散纹理行差得远。
static (int x0, int y0, int x1, int y1) FindTextBounds(Bitmap bin)
{
    int w = bin.Width, h = bin.Height;
    var rows = new int[h];
    long total = 0;
    var data = bin.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    byte[] buf;
    int stride;
    try
    {
        stride = data.Stride;
        buf = new byte[stride * h];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        for (int y = 0; y < h; y++)
        {
            int rowOff = y * stride;
            for (int x = 0; x < w; x++)
            {
                if (buf[rowOff + x * 4] < 128) { rows[y]++; total++; }
            }
        }
    }
    finally { bin.UnlockBits(data); }

    if (total == 0) return (-1, -1, -1, -1);

    int maxRow = 0;
    for (int y = 0; y < h; y++) if (rows[y] > maxRow) maxRow = rows[y];
    if (maxRow < 2) return (-1, -1, -1, -1);
    int rowTh = Math.Max(2, (int)(maxRow * 0.30));

    int y0 = -1, y1 = -1;
    for (int y = 0; y < h; y++) if (rows[y] >= rowTh) { if (y0 < 0) y0 = y; y1 = y; }
    if (y0 < 0) return (-1, -1, -1, -1);

    // 只在行带内做列投影
    var cols = new int[w];
    for (int y = y0; y <= y1; y++)
    {
        int rowOff = y * stride;
        for (int x = 0; x < w; x++)
        {
            if (buf[rowOff + x * 4] < 128) cols[x]++;
        }
    }
    int maxCol = 0;
    for (int x = 0; x < w; x++) if (cols[x] > maxCol) maxCol = cols[x];
    if (maxCol < 2) return (-1, -1, -1, -1);
    int colTh = Math.Max(1, (int)(maxCol * 0.12));

    int x0 = -1, x1 = -1;
    for (int x = 0; x < w; x++) if (cols[x] >= colTh) { if (x0 < 0) x0 = x; x1 = x; }
    if (x0 < 0) return (-1, -1, -1, -1);

    // 留 2 像素余量，免得字贴着边框被切
    x0 = Math.Max(0, x0 - 2); y0 = Math.Max(0, y0 - 2);
    x1 = Math.Min(w - 1, x1 + 2); y1 = Math.Min(h - 1, y1 + 2);
    return (x0, y0, x1, y1);
}

static Bitmap CropToText(Bitmap bin)
{
    var (x0, y0, x1, y1) = FindTextBounds(bin);
    if (x0 < 0 || y0 < 0) return (Bitmap)bin.Clone();
    int w = x1 - x0 + 1, h = y1 - y0 + 1;
    // 裁得太小或几乎没裁，都不动
    if (w < bin.Width / 5 || h < bin.Height / 5) return (Bitmap)bin.Clone();
    if (w >= bin.Width - 2 && h >= bin.Height - 2) return (Bitmap)bin.Clone();
    var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(dst);
    g.DrawImage(bin, new Rectangle(0, 0, w, h), new Rectangle(x0, y0, w, h), GraphicsUnit.Pixel);
    return dst;
}

static Bitmap CropRect(Bitmap source, int x, int y, int w, int h)
{
    var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(dst);
    g.DrawImage(source, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
    return dst;
}

// 先用一次二值化找到文字边界，把**灰度图**裁到那个边界，再二值化一次。
// 顺序很重要：直接裁二值图，纹理已经把阈值算歪了，裁完分数照样低。
static Bitmap CropThenBinarize(Bitmap gray, int window, double k)
{
    using var probe = Sauvola(gray, window, k);
    var (x0, y0, x1, y1) = FindTextBounds(probe);
    if (x0 < 0 || y0 < 0) return Sauvola(gray, window, k);
    int w = x1 - x0 + 1, h = y1 - y0 + 1;
    if (w < probe.Width / 5 || h < probe.Height / 5) return Sauvola(gray, window, k);
    using var cropped = CropRect(gray, x0, y0, w, h);
    return Sauvola(cropped, window, k);
}

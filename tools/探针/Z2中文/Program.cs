using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Tesseract;
using TranslatorApp.Core.Capture;

// ============================================================
// Z2 中文段的离线探针
//   复刻 tools\素材\快测材料.html 里 .m2 .g5 那一段：
//     底 = repeating-linear-gradient(45deg,#7a9cc6 0 12px,#6b8cb5 12px 24px)
//     字 = #ffffff，15px
//   然后照产品顺序跑：判深底 → 反色 → Preprocess(2倍+灰度) → 补 10px 边 → chi_sim
//   目的：验证「10px 白边」是不是让 chi_sim 一个中文字都认不出来的元凶。
// ============================================================

const string Answer = "光学字符识别是指计算机识别图像中的文字并转换为可编辑文本的过程。";

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
string rel = Path.Combine(root, "release", "v0.3.4.2");
string x64 = Path.Combine(rel, "x64");
string tessdata = Path.Combine(rel, "tessdata");
Native.SetDllDirectory(x64);
foreach (var f in Directory.GetFiles(x64, "*.dll"))
{
    try { NativeLibrary.Load(f); } catch { }
}

using var engine = new TesseractEngine(tessdata, "chi_sim");
engine.SetVariable("classify_enable_learning", "0");
engine.SetVariable("tessedit_pageseg_mode", "6");

// ---------- 画一张 Z2 ----------
Bitmap MakeZ2()
{
    using var measure = new Bitmap(1, 1);
    using var mg = Graphics.FromImage(measure);
    using var mfont = new Font("Microsoft YaHei", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
    var size = mg.MeasureString(Answer, mfont);

    int padX = 16, padY = 10;
    int w = (int)Math.Ceiling(size.Width) + padX * 2;
    int h = (int)Math.Ceiling(size.Height) + padY * 2;

    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    var c1 = Color.FromArgb(0x7a, 0x9c, 0xc6);
    var c2 = Color.FromArgb(0x6b, 0x8c, 0xb5);

    // 45 度斜条纹，12px 一格
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double t = (x - y) * 0.70710678;
            double m = t % 24.0;
            if (m < 0) m += 24.0;
            bmp.SetPixel(x, y, m < 12.0 ? c1 : c2);
        }

    using (var g = Graphics.FromImage(bmp))
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Microsoft YaHei", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        g.DrawString(Answer, font, brush, padX, padY);
    }
    return bmp;
}

double AvgBrightness(Bitmap bmp)
{
    var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        long sum = 0; int n = 0;
        unsafe
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte b = row[x * 4], g2 = row[x * 4 + 1], r = row[x * 4 + 2];
                    sum += (int)(0.299 * r + 0.587 * g2 + 0.114 * b);
                    n++;
                }
            }
        }
        return n == 0 ? 0 : (double)sum / n;
    }
    finally { bmp.UnlockBits(data); }
}

Bitmap Pad(Bitmap src, int border, Color color)
{
    var padded = new Bitmap(src.Width + border * 2, src.Height + border * 2,
        PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(padded);
    g.Clear(color);
    g.DrawImage(src, border, border, src.Width, src.Height);
    return padded;
}

Color ModeColor(Bitmap bmp)
{
    var counts = new Dictionary<int, int>();
    var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        unsafe
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int key = (row[x * 4 + 2] << 16) | (row[x * 4 + 1] << 8) | row[x * 4];
                    counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
                }
            }
        }
    }
    finally { bmp.UnlockBits(data); }
    int best = 0, bestN = -1;
    foreach (var kv in counts) if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
    return Color.FromArgb((best >> 16) & 0xFF, (best >> 8) & 0xFF, best & 0xFF);
}

int Otsu(Bitmap bmp)
{
    var hist = new int[256];
    var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        unsafe
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte b = row[x * 4], g2 = row[x * 4 + 1], r = row[x * 4 + 2];
                    hist[(int)(0.299 * r + 0.587 * g2 + 0.114 * b)]++;
                }
            }
        }
    }
    finally { bmp.UnlockBits(data); }

    long total = bmp.Width * (long)bmp.Height;
    double sumAll = 0;
    for (int i = 0; i < 256; i++) sumAll += i * (double)hist[i];

    double sumB = 0, best = -1;
    long wB = 0;
    int threshold = 0;
    for (int t = 0; t < 256; t++)
    {
        wB += hist[t];
        if (wB == 0) continue;
        long wF = total - wB;
        if (wF == 0) break;
        sumB += t * (double)hist[t];
        double mB = sumB / wB, mF = (sumAll - sumB) / wF;
        double between = wB * (double)wF * (mB - mF) * (mB - mF);
        if (between > best) { best = between; threshold = t; }
    }
    return threshold;
}

string Clean(string s) => (s ?? "").Replace(" ", "").Replace("\n", "").Replace("\r", "").Trim();

(double Conf, string Text) Run(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
    using var pix = Pix.LoadFromMemory(ms.ToArray());
    using var page = engine.Process(pix);
    return (page.GetMeanConfidence(), page.GetText() ?? "");
}

void Report(string label, Bitmap fed)
{
    double otsu = Otsu(fed);
    var (conf, text) = Run(fed);
    string cleaned = Clean(text);
    string mark = cleaned == Clean(Answer) ? "✅ 全对" : (cleaned.Length == 0 ? "❌ 0 字" : "❌ 认错");
    Console.WriteLine($"   {label,-34} Otsu {otsu,3:F0}  亮度 {AvgBrightness(fed),6:F1}  " +
                      $"置信度 {conf:F2}  {cleaned.Length,3} 字  {mark}");
    if (cleaned.Length > 0 && cleaned != Clean(Answer))
        Console.WriteLine($"      → {cleaned}");
}

// ---------- 1. 画图 ----------
using Bitmap z2 = MakeZ2();
Console.WriteLine($"合成 Z2：{z2.Width}×{z2.Height}，平均亮度 {AvgBrightness(z2):F1}");

// ---------- 2. 照产品顺序判深底 ----------
bool deep = ImagePreprocessor.AnalyzeBackground(z2, out int bgThreshold, out int ringDark, out int ringTotal);
Console.WriteLine($"判深底：{deep}（Otsu 阈值 {bgThreshold}，四边最外一圈 {ringDark}/{ringTotal} 落在暗侧）");

using Bitmap raw = deep ? ImagePreprocessor.Invert(z2) : new Bitmap(z2);
Console.WriteLine($"反色后平均亮度 {AvgBrightness(raw):F1}，众数色 {ModeColor(raw)}");
Console.WriteLine();

// ---------- 3. 产品现状那条路：Preprocess(2倍+灰度) → 补 10px 白边 → chi_sim ----------
Console.WriteLine("══ 中文路（Preprocess 2 倍 + 灰度）══");
using (Bitmap chiImage = ImagePreprocessor.Preprocess(raw))
{
    Console.WriteLine($"   Preprocess 后：{chiImage.Width}×{chiImage.Height}，众数色 {ModeColor(chiImage)}");
    using (var v1 = Pad(chiImage, 10, Color.White)) Report("① 现状：+10px 白边", v1);
    Report("② 不补边", chiImage);
    using (var v3 = Pad(chiImage, 10, ModeColor(chiImage))) Report("③ +10px 背景色边", v3);
    using (var v4 = Pad(chiImage, 10, Color.Black)) Report("④ +10px 黑边", v4);
}
Console.WriteLine();

// ---------- 4. 红通道补试那条路 ----------
Console.WriteLine("══ 红通道补试（PreprocessRedChannel 2 倍）══");
using (Bitmap redImage = ImagePreprocessor.PreprocessRedChannel(raw, 2.0))
{
    using (var v1 = Pad(redImage, 10, Color.White)) Report("① 现状：+10px 白边", v1);
    Report("② 不补边", redImage);
}
Console.WriteLine();

// ---------- 5. 如果压根不反色 ----------
Console.WriteLine("══ 不反色（直接拿原图）══");
using (Bitmap noInv = ImagePreprocessor.Preprocess(z2))
{
    using (var v1 = Pad(noInv, 10, Color.White)) Report("① +10px 白边", v1);
    Report("② 不补边", noInv);
}
Console.WriteLine();

Console.WriteLine($"标准答案：{Answer}");

static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetDllDirectory(string lpPathName);
}

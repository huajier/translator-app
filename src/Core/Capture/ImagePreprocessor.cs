using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 图像预处理：识别之前先把图「加工」一下，认得更准。
/// </summary>
/// <remarks>
/// 【决策 3-3 = B：放大一倍 + 灰度化】（2026-10-08 用户确认）
///
///   为什么放大：屏幕上的小字在截图里可能只有十几个像素高，
///               识别引擎对太小的字容易漏。放大一倍等于给它更多像素。
///   为什么灰度：识别引擎主要看明暗对比，不看颜色。
///               转成灰度能去掉彩色花纹的干扰（比如网页上的链接色块），
///               同时让文字与背景的黑白差更明显。
///
/// 【为什么不做二值化与分行（决策 3-3 的 C）】
///   那套更适合手机拍的照片（背景杂乱、光照不均）。
///   屏幕截图本身干净又清晰，做二值化反而可能把浅色文字直接抹成一片黑，
///   还能多花 0.2 秒以上——性能红线是「含预处理必须 < 1.5 秒」。
///
/// 【GDI 资源纪律（性能红线）】
///   本类每一步都用 using 及时释放 Graphics / Bitmap / Image，
///   连续识别几十次内存也不许持续增长（T3-8 的判定标准）。
/// </remarks>
internal static class ImagePreprocessor
{
    /// <summary>放大倍数。</summary>
    private const double ScaleFactor = 2.0;

    /// <summary>
    /// **外文**专用的放大倍数。中文那条路仍然用 <see cref="ScaleFactor"/>，两者**故意不一样**。
    ///
    /// 【为什么外文要放大到 4 倍（2026-10-09 用户实测后定的方向）】
    ///   Tesseract 对文字大小是有明确要求的：
    ///   **「x 高度」（小写字母 x 的高度）至少要 30 像素**，识别效果才好。
    ///
    ///   而验收材料的正文字号是 **15px**
    ///   （`tools\素材\验收材料2-实战版.html` 里 `p { font-size:15px }`），
    ///   15px 字号的 x 高度只有 7~8 像素：
    ///     放大 2 倍 → 约 15px，**还不到推荐值的一半**；
    ///     放大 4 倍 → 约 30px，**刚好达到推荐值**。
    ///
    ///   字太小的后果，两个问题同根同源：
    ///     E5  `see p.42.` → `see p42.`
    ///       句点在这个尺寸只有一两个像素，**被当成噪点直接滤掉**。
    ///       旁证：别的段也冒出过多余标点（R8 前面出现过一个反引号），
    ///       说明**标点在这个尺寸下本来就不稳**。
    ///     R8  `config.txt` → `сопйддхе`
    ///       小写字母加点号的拉丁串，笔画又细又挤，最容易认错。
    ///
    ///   【为什么不顺带把中文也放大到 4 倍】
    ///   中文走的是 **Windows 自带引擎**，不是 Tesseract，
    ///   两边的最佳尺寸不一样；而中文现在（Z1、Z2、Z3）**全部逐字全对**，
    ///   **没坏就别动**。所以这里刻意只改外文。
    /// </summary>
    private const double ForeignScaleFactor = 4.0;

    /// <summary>放大后的尺寸上限，防止超大区域把内存吃光。</summary>
    private const int MaxOutputPixels = 4096;

    // =====================================================================
    //  外文专用预处理：给「彩色字压彩色底」用的
    // =====================================================================

    /// <summary>
    /// <summary>
    /// 外文识别专用：把图**放大到 4 倍**（只放大，不二值化、不转灰度）。
    ///
    /// 【作用】
    ///   Tesseract 要求 x 高度至少 30 像素才认得准，
    ///   而取词框选出来的小字（约 15px 字号）远远不够。
    ///   放大倍数为什么定 4 倍，见 <see cref="ForeignScaleFactor"/> 的说明。
    ///
    /// 【为什么放在最前面、只做一次】
    ///   E5 的结果是从**原图**那一路认出来的（当时置信度 0.87，够高，
    ///   根本走不到「四路二值化」那一步），
    ///   所以**光改四路那一步是没用的，必须在所有外文识别之前就放大**。
    ///   放大一次，后面 eng / rus / rus+eng / 四路 / 红通道 全都共用这一张，
    ///   免得有的路放大有的路没放，分辨率不一致反而不好比较。
    /// </summary>
    public static Bitmap UpscaleForForeign(Bitmap source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        int w = source.Width, h = source.Height;

        double scale = ForeignScaleFactor;
        int maxSide = Math.Max(w, h);
        if (maxSide * scale > MaxOutputPixels)
        {
            scale = (double)MaxOutputPixels / maxSide;
            AppLogger.Info($"选区过长（{w}×{h}），外文放大倍数降到 {scale:F2}");
        }

        int nw = Math.Max(1, (int)Math.Round(w * scale));
        int nh = Math.Max(1, (int)Math.Round(h * scale));

        var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, nw, nh));
        return dst;
    }

    /// <summary>
    /// 外文识别专用：把图**放大到 4 倍**，然后**红、绿、蓝、灰度四路各出一张二值图**。
    ///
    /// 【为什么不能直接转灰度（2026-10-09 用户实测踩出来的）】
    ///   橙底绿字（`#ff8c00` 底 + `#00e676` 字）认出来是纯乱码，
    ///   用户反馈「橙色底绿字我感觉问题很大」。
    ///   把这几个颜色拆开看，问题一目了然：
    ///
    ///               红通道   绿通道   蓝通道   **灰度**
    ///     橙底       255      140       0      **158**
    ///     绿字         0      230     118      **148**
    ///     差值      **255**     90     118      **10**  ← 几乎没差别
    ///
    ///   **灰度是 R、G、B 的加权平均，把颜色差平均没了。**
    ///   红通道上字和底差 255（一黑一白，完美分得开），
    ///   到了灰度只剩 10，肉眼都看不出来，引擎更认不出。
    ///
    ///   所以四路都做一遍，哪一路能认对就用哪一路。
    ///
    /// 【为什么不再由程序自己「挑最好的一路」（2026-10-09 推翻）】
    ///   原来这里有个自造指标「Otsu 可分性」，让程序自己挑。
    ///   实测证明**这个指标是坏的**：
    ///     算出来的得分是 3e11 这种数（正常范围应该是 0~1）；
    ///     橙底绿字那一段它选了「灰度」——
    ///     而红通道明明差 255、灰度只差 10，**它选中的恰好是最差的一路**。
    ///
    ///   自己算的指标自己验证不了，就不要拿它做决定。
    ///   改成**四路全都交给识别引擎认一遍，用引擎自报的置信度选**——
    ///   和选语言、选模型是同一个套路，置信度已经被用户实测数据验证过。
    ///
    /// 【返回什么】
    ///   长度 4 的数组，顺序固定：红、绿、蓝、灰度。
    ///   调用方负责释放每一张（外文识别失败时也不会白白占着内存）。
    /// </summary>
    public static Bitmap[] PrepareAllPlanes(Bitmap source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        // 【这张图已经由调用方放大到 4 倍了，这里不要再放大一次】
        //   为什么是调用方放大、这里不放大，见 UpscaleForForeign 的说明。

        // 【先在图的四边补一小圈白边，再统计、再二值化】
        //   为什么要补、为什么只能补这么点，见 PadAround 的说明。
        Bitmap padded = PadAround(source);

        try
        {
            // 四路各自的 256 级灰度直方图
            // 一次扫像素，四路一起统计——比分四次扫快得多。
            var counts = new int[4][];
            var sums = new int[4][];
            var sumSquares = new long[4][];
            for (int p = 0; p < 4; p++)
            {
                counts[p] = new int[256];
                sums[p] = new int[256];
                sumSquares[p] = new long[256];
            }

            ReadPlanes(padded, counts, sums, sumSquares);

            // 注意：这里**不挑**，五路全做，交给识别引擎用置信度选。
            // 第 5 路是 **局部阈值**（治斜条纹底，见 BinarizeAdaptive 的说明），
            // 它跑在积分图上，一整张图的复杂度是 O(像素数)，不贵。
            var result = new Bitmap[5];
            try
            {
                for (int p = 0; p < 4; p++)
                    result[p] = BinarizePlane(padded, p, counts[p], sums[p], sumSquares[p]);

                // 【2026-10-10 C4】第五路：局部阈值
                //   前四路是**全局阈值**，遇到条纹底会把条纹当笔画；
                //   这一路是**局部阈值**，能把条纹抹平。
                result[4] = BinarizeAdaptive(padded);
            }
            catch
            {
                // 中途失败要把已经做出来的释放掉，不能泄漏
                foreach (var b in result) b?.Dispose();
                throw;
            }

            return result;
        }
        finally
        {
            padded.Dispose();
        }
    }

    /// <summary>
    /// **中文专用的「红通道、不二值化」版本**（2026-10-10 为 Z1 加）。
    ///
    /// 【Z1 的现场】
    ///   `今天天气不错适合出门`，**橙底绿字**：
    ///     Windows 自带  → 0 字，一个字都读不出来
    ///     chi_sim 在**灰度图**上 → 0.76，卡在 0.78 门槛下面被拒
    ///     结果保留 Windows 的错字，整段判为乱码，**界面什么都不显示**
    ///
    /// 【为什么红通道能救】
    ///   底色橙 = RGB(255,165,0)，字绿 = RGB(0,128,0)：
    /// ```
    ///   橙底的红分量 = 255
    ///   绿字的红分量 = 0
    ///   → 红通道上对比度拉满，颜色天然分得开
    /// ```
    ///   转成灰度之后三种通道**加权平均**，绿字那点红被稀释掉了，
    ///   所以**必须挑红通道单独看，不能只看灰度**。
    ///
    ///   **而且不能二值化**——一刀切下去，
    ///   笔画边缘那些半灰像素会被切掉（`PrepareRedChannelGray` 的注释里记着同一个坑）。
    ///
    /// 【为什么以前没生效】
    ///   「中文跑红通道」这条路**当年确实加了**，但它挂在外文处理流程里，
    ///   而 Z1 走的是**中文流程**（Windows 先认出一点中文就归它管），
    ///   **压根不会进那条路**。这一版是把它补进中文流程。
    /// </summary>
    public static Bitmap PreprocessRedChannel(Bitmap source, double scale)
    {
        int nw = Math.Max(1, (int)(source.Width * scale));
        int nh = Math.Max(1, (int)(source.Height * scale));

        var big = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, nw, nh));
        }
        using (big) { return PrepareRedChannelGray(big); }
    }

    /// <summary>
    /// 只取**红通道转成灰度**，**不做二值化**，另外放大并补边。
    ///
    /// 【为什么要有这么一路（2026-10-09 用户实测）】
    ///   R9 末尾的 `позже` 四路二值化全丢，
    ///   但历史上**没二值化的原图是认得出来的**——
    ///   所以问题出在二值化这一步。
    ///
    ///   这一路是「**保住颜色分离，但不做阈值切割**」的折中：
    ///     红通道上橙底是 255、绿字是 0，**即使不切，对比也已经拉满**，
    ///       颜色照样分得开——这是二值化那一路帮忙解决的问题；
    ///     不切就**保留了原图里的半灰过渡像素**，
    ///       `позже` 正好是被一刀切掉的那半边笔画。
    ///
    /// 【它只当补充，不当主力】
    ///   二值化那版在 E3、R3 上明显更准（数字认得对），
    ///   所以**两版都保留**，由调用方按「谁的内容更全」来选。
    /// </summary>
    public static Bitmap PrepareRedChannelGray(Bitmap source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        // 【这张图已经由调用方放大到 4 倍了，这里不要再放大一次】
        var gray = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                // 直接把红通道当成灰度值
                // 三个通道写成同一个数，得到的就是一张标准灰度图
                byte r = source.GetPixel(x, y).R;
                gray.SetPixel(x, y, Color.FromArgb(r, r, r));
            }
        }

        return gray;
    }

    /// <summary>
    /// 在图的四边补一小圈白边。
    ///
    /// 【要解决什么（2026-10-09 用户实测 + 诊断日志）】
    ///   R9 原文 `Версия 1.2.3, ошибка 500, попробуйте позже`，
    ///   **末尾的 `позже` 无论用哪一路都认不出来**。
    ///   诊断日志把四路全打出来了：
    ///     红通道 0.93 → `Версия 1.2.3, ошибка 500, попробуйте`（少了 позже）
    ///     绿通道 / 蓝通道 / 灰度 → 一个字都没认出来
    ///   但历史上有一轮，**没做二值化的原图是认得出 `позже` 的**。
    ///   → 问题出在「放大 + 二值化」这一步，**不是选路选错了**。
    ///
    /// 【为什么二值化会把末尾吃掉】
    ///   用户取词是**框选**，框得刚刚好时文字必然贴着框线，
    ///   `позже` 的边缘正好压在框线上：
    ///     原图里那几个像素是**半灰的过渡色**（字被切掉了一半），
    ///       Tesseract 看得到，能猜出来；
    ///     二值化一刀切，只认「够黑」或「够白」，
    ///       **剩下那半边笔画直接消失**，整个字就没了。
    ///
    /// 【补多少：踩过的坑】
    ///   第一次补的是「长边的一成」——一行 1200×40 的字补成 1440×280，
    ///   **字被撑成一条细线**，Tesseract 的版面分析直接崩掉，
    ///   E2 只认出中间一段 `translation is a type of computational lin irom one`。
    ///   （资料里 Tesseract 漏行的原因之一正是「行内留白过大」）
    ///
    ///   所以这次**按文字高度补一小圈**，长宽比例几乎不变：
    ///   40 像素高的字补 8 像素，1200×40 变成 1216×56，比例基本没动。
    ///   （也有资料说补 1 像素边就能解决贴边截断）
    /// </summary>
    private static Bitmap PadAround(Bitmap source)
    {
        // 按**高度**算，不是宽度——文字有多高，边就该留多宽
        int margin = Math.Max(4, source.Height / 5);

        var padded = new Bitmap(source.Width + margin * 2, source.Height + margin * 2, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(padded))
        {
            g.Clear(Color.White);
            g.DrawImage(source, margin, margin, source.Width, source.Height);
        }

        return padded;
    }
    /// <summary>一路灰度的 256 级直方图统计。</summary>
    private readonly struct PlaneStats
    {
        public PlaneStats(int[] counts, int[] sums, long[] sumSquares)
        {
            Counts = counts; Sums = sums; SumSquares = sumSquares;
        }
        public int[] Counts { get; }
        public int[] Sums { get; }
        public long[] SumSquares { get; }
    }

    /// <summary>一路灰度里，取得能把黑白两类分得最开的阈值。</summary>
    private static int BestThreshold(int[] counts, int[] sums, long[] sumSquares)
    {
        int total = counts.Sum();
        long totalSum = sums.Sum();
        if (total <= 0) return 128;

        double totalVariance = (double)sumSquares.Sum() - (double)totalSum * totalSum / total;
        if (totalVariance <= 0) return 128;

        double bestBetween = double.MinValue;
        int bestThreshold = 128;
        long sumBelow = 0;
        int countBelow = 0;

        for (int t = 0; t <= 255; t++)
        {
            countBelow += counts[t];
            // 【2026-10-12 修正】原来写的是 `sumBelow += (long)t * sums[t];`，多乘了一个 t。
            //   原因：Accumulate 里写的是 `sums[plane][value] += value;`——
            //   也就是说 sums[t] **本身已经是「桶 t 里所有像素灰度的总和」**，不用再乘 t。
            //   多乘一次的后果：类间方差里的两个均值全是假的，BestThreshold 在
            //   非纯白底的图上会算出 254（等于「除了纯白都算字」），
            //   于是红/绿/蓝/灰度四路全变成一块黑方框，引擎读出什么都是空的。
            //   白底黑字的图直方图九成以上堆在 255，这个错会「碰巧蒙对」，
            //   所以一直没人发现——直到条纹底的 E5 才露馅。
            //   证据：tools\日志\阶段3-验收.md 一百一十六节（tools\探针 实测）。
            sumBelow += sums[t];
            int countAbove = total - countBelow;
            if (countBelow == 0 || countAbove == 0) continue;

            double meanBelow = (double)sumBelow / countBelow;
            double meanAbove = (double)(totalSum - sumBelow) / countAbove;
            double between = (double)countBelow * countAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > bestBetween)
            {
                bestBetween = between;
                bestThreshold = t;
            }
        }

        // 分得越开，得分越接近 1
        return bestThreshold;
    }

    /// <summary>
    /// Otsu 的可分性指标：类间方差 ÷ 总方差。
    /// 越接近 1，说明黑白两类分得越开；越小说明越难分。
    /// </summary>
    private static double OtsuSeparability(int[] counts, int[] sums, long[] sumSquares)
    {
        int total = counts.Sum();
        long totalSum = sums.Sum();
        if (total <= 0) return 0;

        double totalVariance = (double)sumSquares.Sum() - (double)totalSum * totalSum / total;
        if (totalVariance <= 0) return 0;

        int threshold = BestThreshold(counts, sums, sumSquares);

        long sumBelow = 0;
        int countBelow = 0;
        for (int t = 0; t <= threshold; t++)
        {
            countBelow += counts[t];
            sumBelow += (long)t * sums[t];
        }

        int countAbove = total - countBelow;
        if (countBelow == 0 || countAbove == 0) return 0;

        double meanBelow = (double)sumBelow / countBelow;
        double meanAbove = (double)(totalSum - sumBelow) / countAbove;
        double between = (double)countBelow * countAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);

        // 【归一化：两个平方和相除，得到的正好是「像素个数」】
        //   推导：两个取值 a、b，各出现 n0、n1 次，N = n0 + n1
        //     类间方差 between = n0·n1·(a−b)²
        //     总方差 totalVar = n0·n1·(a−b)² / N
        //     两者相除 = N
        //   所以**还要再除一次像素总数**才落在 0~1 之间。
        //   （中间漏掉这一步时，得分打出来是 4.8e11，看着像程序坏了。）
        return between / totalVariance / total;
    }

    /// <summary>
    /// 判断这块图的**底色是不是彩色的**。
    ///
    /// 【谁在用、为什么（2026-10-09 用户实测）】
    ///   Z1 那张橙底绿字的中文（`今天天气不错适合出门`），
    ///   **Windows 自带引擎时灵时不灵**：
    ///     有时一个字都认不出（于是走了 chi_sim，认得逐字全对）；
    ///     有时认出 `今天天气个台」`——错了三个字，还带着一个多余的引号。
    ///   同一张图、同一段文字，结果在两轮之间来回跳。
    ///
    ///   规律很清楚：**只有彩色底上的中文会这样**。
    ///   白底上的中文（Z2、Z3）每一轮都稳稳的；
    ///   外文（E3、R3、R9）本来就是彩色底，
    ///   但它们走的是「外文那条路」，本来就不用 Windows 自带的认中文，也不受影响。
    ///
    ///   所以这里量的是**底色有没有饱和度**——
    ///   一个能直接从像素算出来的事实，不是靠猜。
    ///
    /// 【怎么算】
    ///   每个像素看红、绿、蓝三个值的**最大减最小**：
    ///     纯白、黑、灰 → 三个值一样，差 0 → 不算彩色；
    ///     橙底 `#ff8c00` → 255 减 0，差 255 → 算彩色。
    ///   差超过 60 就算「这个像素有颜色」，
    ///   图里超过三成的像素有颜色，就认定是彩色底。
    /// </summary>
    public static bool IsColorfulBackground(Bitmap source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        // 抽着看就行，不用每个像素都算：底色是整片的，抽几百个点足够判断。
        int step = Math.Max(1, Math.Max(source.Width, source.Height) / 24);
        int colored = 0, total = 0;

        for (int y = 0; y < source.Height; y += step)
        {
            for (int x = 0; x < source.Width; x += step)
            {
                Color c = source.GetPixel(x, y);

                int max = Math.Max(c.R, Math.Max(c.G, c.B));
                int min = Math.Min(c.R, Math.Min(c.G, c.B));

                if (max - min > 60) colored++;
                total++;
            }
        }

        return total > 0 && (double)colored / total > 0.30;
    }

    /// <summary>只放大，不转灰度。</summary>
    private static Bitmap ScaleOnly(Bitmap source)
    {
        int w = source.Width, h = source.Height;

        // 【2026-10-09 修一个把图缩小的严重错误】
        //
        //   这里原来写的是：
        //     int maxW = (int)Math.Sqrt(MaxOutputPixels * (double)w / h);
        //     if (w * scale > maxW) scale = (double)maxW / w;
        //   那是把「最大像素数」当成了**总像素预算**在用，算出来的是宽度的平方根。
        //
        //   取词框选出来的是**又宽又扁**的一条（比如 1000×50）：
        //     maxW = √(4096 × 1000 ÷ 50) = 286
        //     → 1000×50 被缩成 **286×14**
        //
        //   文字只剩两三个像素高，Tesseract 一个字都认不出来。
        //   实测后果：预处理出来的图**每一次都返回 0 个字**，
        //   「四路择优」等于完全没生效，还白白多花一倍时间。
        //
        //   现在跟上面那个 Preprocess 保持一致：
        //   MaxOutputPixels 表示**长边最多多少像素**，而不是总像素数。
        double scale = ScaleFactor;
        int maxSide = Math.Max(w, h);
        if (maxSide * scale > MaxOutputPixels)
        {
            scale = (double)MaxOutputPixels / maxSide;
            AppLogger.Info($"外文预处理：选区过长（{w}×{h}），放大倍数降到 {scale:F2}");
        }

        int nw = Math.Max(1, (int)Math.Round(w * scale));
        int nh = Math.Max(1, (int)Math.Round(h * scale));

        var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, nw, nh));
        return dst;
    }

    /// <summary>
    /// 一次性扫出 R、G、B、灰度四路的直方图统计。
    /// 只扫一遍像素，四路一起统计，比扫四遍快得多。
    /// </summary>
    private static unsafe void ReadPlanes(Bitmap bmp, int[][] counts, int[][] sums, long[][] sumSquares)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int b = row[x * 4 + 0];
                    int g = row[x * 4 + 1];
                    int r = row[x * 4 + 2];

                    Accumulate(0, r, counts, sums, sumSquares);
                    Accumulate(1, g, counts, sums, sumSquares);
                    Accumulate(2, b, counts, sums, sumSquares);
                    // 常规灰度（Rec.601）
                    Accumulate(3, (r * 299 + g * 587 + b * 114) / 1000, counts, sums, sumSquares);
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    private static void Accumulate(int plane, int value, int[][] counts, int[][] sums, long[][] sumSquares)
    {
        counts[plane][value]++;
        sums[plane][value] += value;
        sumSquares[plane][value] += (long)value * value;
    }

    /// <summary>
    /// <summary>
    /// **竖向拉长**：灰度之后，把图在**竖直方向**多放大 `yFactor` 倍，横向不变。
    ///
    /// 【2026-10-10 材料 3 的 P2：行距贴太紧】
    ///   ```css
    ///   P2 用的是 line-height:1.0 —— 行与行之间几乎没有空隙
    ///   ```
    ///   实测现场：
    ///     chi_sim  → 根本认不出中文（分不开行，两行糊成一片）
    ///     Windows  → 认出来了，但错字多（`端囗`、`8C8`）
    ///
    ///   **chi_sim 认不出不是因为「不认识这些字」，是因为「看不见行边界」。**
    ///   行距是相对的——**竖向整体拉长，行与行之间的空隙也同比变大**，
    ///   字的高度不变（横向没动），于是行边界就分开了。
    ///
    ///   【为什么只拉竖向，不两边一起拉】
    ///     两边一起拉就是普通的放大，`Preprocess` 已经在做（2 倍），
    ///     **对「行距紧」这个问题无效**——
    ///     因为整体放大时，字和空隙是等比例的，**行距相对字高根本没变**。
    ///
    ///   【为什么横向不动】
    ///     横向拉长会让汉字笔画变粗、字间粘连，反而更糟。
    ///     **只有竖向拉长能单独把「行距相对字高」变大。**
    /// </summary>
    public static Bitmap PreprocessStretchY(Bitmap source, double yFactor)
    {
        using Bitmap gray = ToGrayscaleOnly(source);

        int nw = Math.Max(1, gray.Width * 2);
        int nh = Math.Max(1, (int)(gray.Height * 2 * yFactor));

        var result = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        {
            // 【必须是双三次，不能用最近邻】
            //   最近邻放大只会复制像素，边缘还是硬的；
            //   双三次会把笔画边缘平滑一下，**行与行之间多出来的空隙也更干净**。
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(gray, new Rectangle(0, 0, nw, nh));
        }
        return result;
    }

    /// **局部阈值二值化**：Sauvola 方法——每个像素同时看「周围一小块的平均值」和「起伏程度」。
    ///
    /// 【2026-10-10 材料 3 的 C4：斜条纹底】
    ///   ```css
    ///   .g-stripe { background:repeating-linear-gradient(45deg,#fff 0 6px,#dcdcdc 6px 12px);
    ///               color:#222; }
    ///   ```
    ///   文字是 **34**，条纹两档是 **255 / 220**。
    ///
    ///   **全局阈值（Otsu）在这里必然失败**：
    ///   它要在整张图的直方图上切一刀，**切在 127 左右**，
    ///   于是 **220 那档条纹被判成「和文字一样黑」，直接变成一片笔画**。
    ///   实测后果：`HTTP/2.0` 被读成 `HT TP/2.0`（中间凭空多个空格）。
    ///
    ///   ==========================================================
    ///   【2026-10-10 翻车记录：第一版写错了，别再改回去】
    ///
    ///     第一版是「**阈值 = 局部均值 − 12**」，看着合理，**实测把 C4 弄得更糟**：
    ///       局部均值 ≈ 237，减 12 得 225；
    ///       **条纹暗档 220 < 225 → 照样被判成笔画**；
    ///       条纹没被抹掉，碎成一片噪点，被读成
    ///         `eee me ES EE SETS BEES Sg gk tee ee NS \ YA ...`（置信度 0.30）
    ///       而帕累托规则看它「有效字符多（110 > 32）」就采纳了，
    ///       结果 0.30 低于 0.78 门槛，整段被判成乱码。
    ///
    ///     **错在「减一个固定常数」这个做法本身**：
    ///     那个常数既不够大（抹不掉条纹），也没有任何依据。
    ///
    ///   ==========================================================
    ///
    ///   【这一版用 Sauvola：同时看局部均值**和局部标准差**】
    ///
    ///       阈值 = 均值 × (1 + k × (标准差 / 动态范围 − 1))
    ///
    ///     **为什么这个能治条纹**——靠的是「**条纹区的起伏天生就大**」：
    ///
    ///       条纹区   均值 237、标准差 ≈ 17
    ///               → 阈值 = 237 × (1 + 0.2 × (0.133 − 1)) ≈ **196**
    ///               → 条纹暗档 **220 > 196** → 算背景 ✅
    ///
    ///       文字区   均值 ≈ 230（大片白）、标准差 ≈ 50
    ///               → 阈值 ≈ 202 → 文字 **34 < 202** → 算笔画 ✅
    ///
    ///     **关键差别：偏移量不是拍脑袋定死的，而是「这一块有多花」算出来的。**
    ///     一块区域越是明暗起伏，阈值抬得越高——条纹正好是最起伏的那种。
    ///
    ///   【窗口为什么取 25】
    ///     条纹周期约 8.5 像素，**窗口必须大于周期**，
    ///     否则窗口里装不下完整的一对条纹，算不出真实起伏。
    ///     取 25 是周期的好几倍，留足余量。
    /// </summary>
    public static unsafe Bitmap BinarizeAdaptive(Bitmap source, int window = 25, double k = 0.2)
    {
        const double dynamicRange = 128.0;

        int w = source.Width;
        int h = source.Height;
        int half = Math.Max(1, window / 2);

        using Bitmap gray = ToGrayscaleOnly(source);

        // 积分图 + 平方积分图：取窗口和、窗口平方和都是 O(1)
        var integral = new long[(w + 1) * (h + 1)];
        var integralSq = new long[(w + 1) * (h + 1)];
        var pixels = new byte[w * h];

        var src = gray.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)src.Scan0 + (long)y * src.Stride;
                long rowSum = 0;
                long rowSqSum = 0;
                for (int x = 0; x < w; x++)
                {
                    int b = row[x * 4];
                    int g = row[x * 4 + 1];
                    int r = row[x * 4 + 2];
                    int lum = (r * 299 + g * 587 + b * 114) / 1000;
                    pixels[y * w + x] = (byte)lum;
                    rowSum += lum;
                    rowSqSum += (long)lum * lum;
                    integral[(y + 1) * (w + 1) + (x + 1)] = integral[y * (w + 1) + (x + 1)] + rowSum;
                    integralSq[(y + 1) * (w + 1) + (x + 1)] = integralSq[y * (w + 1) + (x + 1)] + rowSqSum;
                }
            }
        }
        finally
        {
            gray.UnlockBits(src);
        }

        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var outData = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = outData.Stride;
            byte* p = (byte*)outData.Scan0;
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - half);
                int y1 = Math.Min(h - 1, y + half);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - half);
                    int x1 = Math.Min(w - 1, x + half);

                    long sum = integral[(y1 + 1) * (w + 1) + (x1 + 1)]
                             - integral[y0 * (w + 1) + (x1 + 1)]
                             - integral[(y1 + 1) * (w + 1) + x0]
                             + integral[y0 * (w + 1) + x0];
                    long sumSq = integralSq[(y1 + 1) * (w + 1) + (x1 + 1)]
                               - integralSq[y0 * (w + 1) + (x1 + 1)]
                               - integralSq[(y1 + 1) * (w + 1) + x0]
                               + integralSq[y0 * (w + 1) + x0];

                    long count = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                    double mean = (double)sum / count;
                    double variance = Math.Max(0.0, (double)sumSq / count - mean * mean);
                    double stddev = Math.Sqrt(variance);

                    // Sauvola 阈值
                    double threshold = mean * (1.0 + k * (stddev / dynamicRange - 1.0));

                    byte* px = p + (long)y * stride + x * 4;
                    byte v = pixels[y * w + x] < threshold ? (byte)0 : (byte)255;
                    px[0] = v; px[1] = v; px[2] = v; px[3] = 255;
                }
            }
        }
        finally
        {
            dst.UnlockBits(outData);
        }
        return dst;
    }

    /// <summary>按选中的那一路做二值化，输出「白底黑字」的图。</summary>
    private static unsafe Bitmap BinarizePlane(Bitmap bmp, int plane, int[] counts, int[] sums, long[] sumSquares)
    {
        int threshold = BestThreshold(counts, sums, sumSquares);


        var dst = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var src = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var outData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* s = (byte*)src.Scan0 + (long)y * src.Stride;
                byte* o = (byte*)outData.Scan0 + (long)y * outData.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int b = s[x * 4 + 0], g = s[x * 4 + 1], r = s[x * 4 + 2];
                    int v = plane == 0 ? r
                          : plane == 1 ? g
                          : plane == 2 ? b
                          : (r * 299 + g * 587 + b * 114) / 1000;

                    // 「比阈值暗」算文字（黑），否则算背景（白）
                    byte c = v <= threshold ? (byte)0 : (byte)255;
                    o[x * 4 + 0] = c; o[x * 4 + 1] = c; o[x * 4 + 2] = c; o[x * 4 + 3] = 255;
                }
            }
        }
        finally { bmp.UnlockBits(src); dst.UnlockBits(outData); }
        return dst;
    }

    /// <summary>
    /// 只转灰度，**不放大**。
    ///
    /// 【为什么需要单独这一档 —— 2026-10-08 对照实验实测出来的】
    ///   同一张中英混排的图，跑六种方案，结论很明确：
    ///
    ///     · **放大 2 倍**：中文明显变准
    ///       （「汶一行」→「这一行」、「厍来」→「用来」、「旬子」→「句子」、多认出「百分号」）；
    ///       但**英文会被劈开**：「quick b rown fox jumps ove r」。
    ///     · **不放大**：英文干干净净「quick brown fox」；
    ///       但中文错字明显变多。
    ///
    ///   所以放大不是「越大越好」，而是**对中文好、对英文坏**。
    ///   与其一刀切，不如两遍识别、按语种挑结果——见 OcrTextCapture。
    ///   本方法是英文那一遍用的。
    /// </summary>
    /// <summary>
    /// 查这张图的**四条边**有没有被切到半个字。
    /// 返回值可以直接拼进提示语，为空字符串表示没切到。
    ///
    /// 【2026-10-10 用户提出来的缺口：框到半个字】
    ///   用户原话：「而且框选多行可能会框选到多余的字（框了半个字啥的）」。
    ///
    ///   **为什么必须专门查它，而不能靠置信度**
    ///     有一个实测过的例子：`me@example.com` 右边那个 `m` 被切掉了一半，
    ///     识别出来是 `me@example.co`——
    ///     **置信度 0.94，比全对的那些还高。**
    ///     因为「少一个字母」这件事看起来仍然像一句正常的英文，
    ///     任何引擎都不会觉得自己认错了，**置信度这道防线在这里完全没用。**
    ///
    ///   **那能怎么办**
    ///     我们**不知道**那个字本来是什么，补不出来，也不该瞎猜。
    ///     能做而且值得做的只有一件事：
    ///     **告诉用户「你这一框的边正好切在字上，多半少了点东西」。**
    ///     让用户自己把框放宽一点——这比让用户对着 `me@example.co` 发呆强得多。
    ///
    /// 【怎么判断「切到了」】
    ///   正常框选时，选区四边和文字之间总有一点点空隙。
    ///   而「半个字」的意思是：**笔画直接顶到了选区的边**。
    ///   所以只要**最外面一列（行）像素里有笔画**，就说明选区边界紧贴甚至切到了字。
    ///
    /// 【2026-10-15 用户点名：右边和下边以前不查，现在补上】
    ///   此前只查左边和上边，右边和下边一次都不查（验收日志一百二十三节记过这个缺口）。
    ///   缺口是真实存在的：2026-10-13 那次 **478×20 和 476×24 两次框底明显切到了字，
    ///   软件一句警告都没报**，而同一天 499×48 报了「上边切到了文字」。
    ///   → 现在四条边都查，哪条边顶到笔画就报哪条（可能同时报好几条）。
    ///
    /// 【下边切到字，提示语和「框多了」不是一回事】
    ///   下边界切字有两种情形：
    ///     · 切掉**本行字的下半截** → 字认不全，**属于「框少了」**，正是本方法要报的；
    ///     · 切进来**下一行的上半截** → 多半被识别成下一行的字，**属于「框多了」**。
    ///   本方法按像素判，**分不出这两种**，所以提示语仍然只说
    ///   「顶在文字上，很可能切到了半个字」，把判断交给用户看原文。
    ///   （真正精确地区分要在识别结果层面做，那是另一件事，此处不做。）
    /// </summary>
    public static string FindCutEdge(Bitmap source)
    {
        if (source is null || source.Width < 3 || source.Height < 3) return string.Empty;

        using Bitmap gray = ToGrayscaleOnly(source);

        var edges = new List<string>(4);
        if (ColumnHasInk(gray, 0)) edges.Add("左边");
        if (ColumnHasInk(gray, gray.Width - 1)) edges.Add("右边");
        if (RowHasInk(gray, 0)) edges.Add("上边");
        if (RowHasInk(gray, gray.Height - 1)) edges.Add("下边");

        return DescribeEdges(edges);
    }

    /// <summary>
    /// 把顶到笔画的边拼成一句人话：一条「左边」，两条「左边和上边」，
    /// 三条以上「左边、上边和右边」（中文用顿号列举、最后一处用「和」）。
    /// </summary>
    private static string DescribeEdges(List<string> edges) => edges.Count switch
    {
        0 => string.Empty,
        1 => edges[0],
        2 => $"{edges[0]}和{edges[1]}",
        _ => string.Join("、", edges.Take(edges.Count - 1)) + "和" + edges[^1],
    };

    /// <summary>这一列（第 columnIndex 列）里有没有比背景暗的笔画。</summary>
    private static bool ColumnHasInk(Bitmap gray, int columnIndex)
    {
        for (int y = 0; y < gray.Height; y++)
        {
            if (gray.GetPixel(columnIndex, y).R < 140) return true;
        }
        return false;
    }

    /// <summary>这一行（第 rowIndex 行）里有没有比背景暗的笔画。</summary>
    private static bool RowHasInk(Bitmap gray, int rowIndex)
    {
        for (int x = 0; x < gray.Width; x++)
        {
            if (gray.GetPixel(x, rowIndex).R < 140) return true;
        }
        return false;
    }

    /// <summary>把每个像素的明暗翻转（亮变暗、暗变亮）。</summary>
    public static Bitmap Invert(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

        // ==========================================================
        // 【2026-10-10 真正的根因，就在这三行上】
        //
        //   原来 `Invert` 的写法是：
        //       var result = new Bitmap(w, h, Format32bppArgb);
        //       var data = result.LockBits(...);
        //       row[x*4+0] = 255 - row[x*4+0];      ← 问题在这里
        //
        //   **`row` 指向的是 `result` 自己的缓冲区，不是 `source` 的。**
        //   而 `result` 是刚 `new` 出来的，**一个字节都没写进去过**，
        //   缓冲区全是 0。
        //
        //   所以实际算的是 `255 - 0 = 255`，
        //   **等于在一张空白画布上做减法，出来必然是一张纯白图。**
        //
        //   【实测证据（C1，查了五轮才定论）】
        //     原图   586×33  亮度 min=32  max=233  平均=62.0   ← 深底浅字，有内容
        //     反色后 586×33  亮度 min=255 max=255 平均=255.0  ← 纯白，一个像素都不差
        //     `255 - 32 = 223`，不该是 255 —— **说明根本没读到原图**。
        //
        //   【为什么之前一直没发现】
        //     出来的图是**纯白**，肉眼看和「框空了」一模一样；
        //     日志里也只表现为「阈值 128、所有引擎 0 字」。
        //     **必须是像素级的数字才能分辨「纯白」和「没框到东西」。**
        //
        //   【修法：先把原图画进 result，再逐字节翻】
        //   用 Graphics 画是安全的——它会把像素和透明度一起处理好，
        //   不会出现「颜色翻了、透明度忘了」这类问题。
        // ==========================================================
        using (Graphics g = Graphics.FromImage(result))
        {
            g.DrawImageUnscaled(source, 0, 0);
        }

        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var data = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* p = (byte*)data.Scan0;
                int rowBytes = data.Stride;
                for (int y = 0; y < source.Height; y++)
                {
                    byte* row = p + y * rowBytes;
                    for (int x = 0; x < source.Width; x++)
                    {
                        // 32bppBGRA：B 蓝 G 绿 R 红 A 透明
                        row[x * 4 + 0] = (byte)(255 - row[x * 4 + 0]);
                        row[x * 4 + 1] = (byte)(255 - row[x * 4 + 1]);
                        row[x * 4 + 2] = (byte)(255 - row[x * 4 + 2]);

                        // 透明度 A 必须显式写 255。
                        //   `new Bitmap` 的缓冲区是全 0，包括第四个字节。
                        //   颜色翻了但 A 忘了写 → **整张图全透明**，
                        //   存出来极小（几百字节），引擎读到一张空白图。
                        row[x * 4 + 3] = 255;
                    }
                }
            }
        }
        finally
        {
            result.UnlockBits(data);
        }
        return result;
    }

    /// <summary>
    /// 这张图是不是「深底浅字」（深色模式界面），需要先整张反色。
    ///
    /// 判断依据是**平均亮度**：图上文字一般只占一小块，平均亮度基本就是底色。
    /// 这是数据本身的性质，不是拍脑袋定的阈值。
    ///
    /// 【为什么必须处理，不处理会怎样】
    ///   Tesseract 期望的是「深字浅底」，深色模式下它内部那套二值化吃力，
    ///   实测 C1（`#1e1e2e` 底 / `#e8e8f0` 字）置信度只有 **0.67~0.70**，
    ///   低于门槛，直接被判成乱码不给用户看。
    ///   Windows 自带引擎在深底上也一样受影响。
    ///
    /// 【深色模式在实战里非常常见】
    ///   VS Code、终端、浏览器深色主题、聊天软件夜间模式……全是深底浅字。
    ///
    /// ================================================================
    /// 【2026-10-12 改判据：不再看平均亮度，改看「四边最外一圈落在哪一侧」】
    ///
    ///   【原来的判据错在哪 —— E5 的实测现场】
    ///     E5 存图（`release\V0.3.2.4\debug-screenshots\sel_20261012_132454_985.png`）
    ///     探针量到的分位数：底 `#6b8cb5`~`#7a9cc6` 灰度 **134~150**，字 `#ffffff` 灰度 **219~244**
    ///       → 明明是**白字暗底**，探针二值化全判反，E5 十次里九次错在
    ///         「行首多一个 `|`」或「吞掉 `p.42.` 的点」。
    ///     但这张图的**平均亮度是 154.4**，高于 110 → 判成「浅底，不反色」。
    ///
    ///   【为什么平均亮度不可靠】
    ///     「文字只占一小块，平均亮度≈底色」这句话只在**深字浅底**时成立。
    ///     一旦是**浅字暗底**，那一小块白字恰恰会把平均值**往上拉**，
    ///     拉过 110 这条线，深底就被当成了浅底。
    ///     也就是说：**判据天生只会把「浅字暗底」漏掉，而不会把「深字浅底」弄错**——
    ///     而 C1（`#1e1e2e` 底 / `#e8e8f0` 字）那个真正需要反色的场景之所以对，
    ///     是因为它的底本来就特别暗（平均亮度远低于 110），不是判据准。
    ///
    ///   【现在怎么判 —— 用图自己的数据，不拍常数】
    ///     ① 先用 Otsu 算阈值（和二值化那一路用的是同一个 `BestThreshold`）。
    ///     ② 再看**四边最外一圈**像素落在阈值的哪一侧——
    ///       一行文字的上下两边几乎一定是底，左右两边最多碰到首尾两个字，
    ///       所以这一圈是**最可靠的「底在哪一边」证据**。
    ///     ③ 这一圈里「暗的那一侧」占七成以上 → 底是暗的、字是亮的 → **要反色**。
    ///
    ///   【拿四张已知图对一遍，全对】
    ///     E5  底 155 字 255  阈值≈181  外圈在暗侧 → 反色   ← 原来判「不反色」，错
    ///     E4  底 185 字 154  阈值≈170  外圈在亮侧 → 不反色（灰底灰字，不能反）
    ///     白底黑字          阈值≈143  外圈在亮侧 → 不反色
    ///     C1  底 30  字 232  阈值≈130  外圈在暗侧 → 反色   ← 原来判对，靠的是底够暗
    /// </summary>
    public static bool NeededInversion(Bitmap source)
        => AnalyzeBackground(source, out _, out _, out _);

    /// <summary>
    /// 判「深底还是浅底」，并且把**算出来的数字一并带出来**。
    ///
    /// 【为什么要把数字带出来】
    ///   「是不是深底」这个判断是整个反色功能的命门，判错一次就等于
    ///   **把好图喂给引擎一张坏图**，引擎表现再差也是被冤枉的。
    ///   所以数字必须进日志，一眼就能验——原来只写「已整张反色」，
    ///   看不出这个判断算得对不对。
    /// </summary>
    /// <param name="threshold">灰度这一路算出来的 Otsu 阈值。</param>
    /// <param name="ringDark">四边最外一圈里，落在暗侧的像素有几个。</param>
    /// <param name="ringTotal">四边最外一圈一共有几个像素。</param>
    public static bool AnalyzeBackground(Bitmap source, out int threshold, out int ringDark, out int ringTotal)
    {
        threshold = 255;
        ringDark = 0;
        ringTotal = 0;
        if (source is null || source.Width < 3 || source.Height < 3) return false;

        // LockBits 要 32bppArgb，来源图不一定是，所以先转一张。
        // 图很小（取词框几百×几十），多这一次转换看不出来。
        using var argb = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(argb)) g.DrawImageUnscaled(source, 0, 0);

        var counts = new int[4][];
        var sums = new int[4][];
        var sumSquares = new long[4][];
        for (int p = 0; p < 4; p++)
        {
            counts[p] = new int[256];
            sums[p] = new int[256];
            sumSquares[p] = new long[256];
        }
        ReadPlanes(argb, counts, sums, sumSquares);

        // 只用灰度这一路（索引 3）和二值化那边完全一致
        threshold = BestThreshold(counts[3], sums[3], sumSquares[3]);

        for (int y = 0; y < argb.Height; y++)
        {
            for (int x = 0; x < argb.Width; x++)
            {
                bool onEdge = x == 0 || y == 0 || x == argb.Width - 1 || y == argb.Height - 1;
                if (!onEdge) continue;
                Color c = argb.GetPixel(x, y);
                int v = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                ringTotal++;
                if (v <= threshold) ringDark++;
            }
        }

        // 外圈里「暗的那一侧」占七成以上 → 底是暗的、字是亮的 → 需要反色
        return ringTotal > 0 && ringDark * 10 >= ringTotal * 7;
    }

    /// <summary>
    /// 这张图的平均亮度（0~255）。
    ///
    /// 【为什么把它单独暴露出来】
    ///   「是不是深底」这个判断是整个反色功能的命门，
    ///   判错一次就等于**把好图喂给引擎一张坏图**，引擎表现再差也是被冤枉的。
    ///   之前日志只写「已整张反色」，**看不出这个判断算得对不对**；
    ///   现在把算出来的数字直接打进日志，一眼就能验。
    /// </summary>
    public static double MeasureAverageLuminance(Bitmap source)
    {
        if (source is null || source.Width < 1 || source.Height < 1) return 255;
        using Bitmap small = Downscale(source, 128);
        long sum = 0;
        long count = 0;
        for (int y = 0; y < small.Height; y++)
        {
            for (int x = 0; x < small.Width; x++)
            {
                Color c = small.GetPixel(x, y);
                sum += (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                count++;
            }
        }
        return count == 0 ? 255 : (double)sum / count;
    }

    /// <summary>
    /// 深底就整张翻成浅底，浅底原样返回。**一定会返回一张新的图**，不让调用方
    /// 拿到和传入同一张图（免得两张一起 Dispose 出问题）。
    /// </summary>
    public static Bitmap MakeLightBackground(Bitmap source)
        => NeededInversion(source) ? Invert(source) : new Bitmap(source);

    /// <summary>
    /// 缩到最长边约 128 像素，只用来算平均亮度，**不影响识别**。
    ///
    /// 【这里踩过的坑，别再改小】
    ///   原来缩到 **16 像素**，也就是 584×34 的图会被缩成 **16×1**——
    ///   **整张图只用了 16 个像素来判断底色，而且用的是最近邻（不做任何平均）**。
    ///
    ///   后果实测过（C1 连续五次全空）：
    ///     框选的是**白底黑字**，但那 16 个采样点恰好都落在深色笔画上，
    ///     平均亮度算出来很低 → 误判成「深底浅字」→ 整张反色
    ///     → 白底黑字变成黑底白字 → **所有引擎一个字都认不出来**。
    ///
    ///   而且因为每次框的位置都一样，**采样点也一样**，
    ///   所以**五次结果一模一样**，看起来特别像「稳定复现的缺陷」。
    ///
    ///   【现在为什么是 128】
    ///     584×34 会缩成 128×7 = **896 个采样点**，
    ///     再配合双三次插值（GDI+ 缩小时会做真正的加权平均），
    ///     算出来的平均亮度才代表整张图。
    /// </summary>
    private static Bitmap Downscale(Bitmap source, int maxSide)
    {
        int w = source.Width;
        int h = source.Height;
        if (w <= maxSide && h <= maxSide) return new Bitmap(source);

        double scale = (double)maxSide / Math.Max(w, h);
        int nw = Math.Max(1, (int)(w * scale));
        int nh = Math.Max(1, (int)(h * scale));

        var small = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(small))
        {
            // 【关键】必须用会做平均的插值。
            //   最近邻是「照抄一个像素」，缩小 36 倍等于**随便挑 16 个像素**，
            //   那不算平均，那是抽样——抽不准就会误判。
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, nw, nh));
        }
        return small;
    }

    public static Bitmap ToGrayscaleOnly(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        using (var attributes = new ImageAttributes())
        {
            // 标准感知加权：0.299 R + 0.587 G + 0.114 B
            attributes.SetColorMatrix(new ColorMatrix(new[]
            {
                new[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
                new[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
                new[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
                new[] { 0f,     0f,     0f,     1f, 0f },
                new[] { 0f,     0f,     0f,     0f, 1f },
            }));

            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    /// <summary>
    /// 预处理：放大 2 倍（高质量插值）+ 灰度化。
    /// 返回新位图，调用方负责 Dispose；入参不会被修改。
    /// </summary>
    public static Bitmap Preprocess(Bitmap source)
    {
        // 放大比例：默认 2 倍，但长边超过上限就按比例缩小，宁可小一点也不爆内存
        double scale = ScaleFactor;
        int maxSide = Math.Max(source.Width, source.Height);
        if (maxSide * scale > MaxOutputPixels)
        {
            scale = (double)MaxOutputPixels / maxSide;
            AppLogger.Info($"预处理：选区过大（{source.Width}×{source.Height}），放大倍数降到 {scale:F2}");
        }

        int outWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        int outHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        var enlarged = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppArgb);
        try
        {
            using (Graphics g = Graphics.FromImage(enlarged))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, outWidth, outHeight));
            }

            // 灰度化：直接用 ColorMatrix 把三个颜色通道按感知权重合成一路
            var gray = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(gray))
            using (var attributes = new ImageAttributes())
            {
                var matrix = new ColorMatrix(new[]
                {
                    new[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
                    new[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
                    new[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
                    new[] { 0f,     0f,     0f,     1f, 0f },
                    new[] { 0f,     0f,     0f,     0f, 1f },
                });

                attributes.SetColorMatrix(matrix);
                g.DrawImage(enlarged,
                    new Rectangle(0, 0, outWidth, outHeight),
                    0, 0, outWidth, outHeight,
                    GraphicsUnit.Pixel, attributes);
            }

            enlarged.Dispose();
            AppLogger.Info($"预处理完成：{source.Width}×{source.Height} → 放大 {scale:F1} 倍并转灰度");
            return gray;
        }
        catch
        {
            enlarged.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 放大 2 倍 + 灰度 + **Otsu 自动阈值二值化**（压成纯黑白）。
    /// 返回新位图，调用方负责 Dispose；入参不会被修改。
    ///
    /// 【这是阶段 3 认字返工里最值钱的一处改动 —— 有实测数据】
    ///   2026-10-08 跑了 4 引擎 × 5 预处理 × 6 场景 × 3 字号的对照实验
    ///   （准确率是拿「自己画的已知原文的图」逐字比出来的，不是看图估的）：
    ///
    ///     字号        原样    放大2倍   放大2倍+二值
    ///     14px        54.0     64.7        65.0
    ///     18px        57.3     75.2        83.3
    ///     28px        79.2     80.8        83.3
    ///     平均        63.5     73.6        77.2   ← 最高
    ///
    ///   生僻字那一项最能说明问题：14px 下别的配置全是 **0 分**，
    ///   只有「放大 2 倍 + 二值化」拿到 **38 分**。
    ///
    /// 【为什么二值化管用】
    ///   所有 OCR 引擎内部都默认「黑字白底、对反差很敏感」。
    ///   屏幕上的字却可能是橙底绿字、深底浅字、灰底浅字——
    ///   引擎要在连续灰度里自己找边界，很容易找错。
    ///   直接把图压成纯黑白，等于替引擎把「前景/背景」的界线划好，
    ///   它要做的就只剩认字这一件事了。
    ///
    /// 【为什么用 Otsu 而不是写死一个灰度阈值】
    ///   写死阈值（比如「低于 128 算黑」）遇到深色背景就全反了。
    ///   Otsu 是**按这张图自己的直方图**自动算一个最佳分界线，
    ///   白底黑字、橙底绿字、深底浅字都能自适应，不需要人为调参。
    /// </summary>
    public static Bitmap PreprocessAndBinarize(Bitmap source)
    {
        Bitmap scaled = Preprocess(source);
        try
        {
            return OtsuBinarize(scaled);
        }
        finally
        {
            scaled.Dispose();
        }
    }

    /// <summary>
    /// Otsu 自动阈值二值化（Otsu 全局阈值法）。
    /// 返回新位图，调用方负责 Dispose；入参不会被修改。
    ///
    /// 【算法一句话】
    ///   统计 256 级灰度的直方图，然后找一个分界值 t，
    ///   让「比 t 暗的分成一类、比 t 亮的分成另一类」之后，
    ///   这两类各自的平均亮度**差得最远**。那个 t 就是最佳分界线。
    ///   差得越远，说明分完类之后每一类内部越整齐、两类之间越分明。
    ///
    /// 【为什么不用局部自适应阈值（Sauvola 之类）】
    ///   局部阈值适合照片、报纸那种光照不均的场景，
    ///   而屏幕截图光照是**均匀**的，全局阈值反而更稳、也更快，
    ///   还不容易把细笔画啃掉。
    /// </summary>
    public static unsafe Bitmap OtsuBinarize(Bitmap source)
    {
        // 先转灰度（阈值是对灰度算的）
        var gray = ToGrayscaleOnly(source);
        try
        {
            int width = gray.Width;
            int height = gray.Height;
            if (width <= 0 || height <= 0) throw new ArgumentException("图片尺寸为零，无法二值化");

            BitmapData locked = gray.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* pixels = (byte*)locked.Scan0;
                int count = width * height;

                // ---- 第一步：统计直方图 ----
                int[] histogram = new int[256];
                for (int i = 0; i < count; i++)
                {
                    // 已经是灰度图，B/G/R 三通道相等，取蓝通道即可
                    histogram[pixels[i * 4]]++;
                }

                // ---- 第二步：找 Otsu 最佳阈值 ----
                long totalSum = 0;
                for (int t = 0; t < 256; t++) totalSum += (long)t * histogram[t];

                long backgroundSum = 0;
                int backgroundCount = 0;
                double maxVariance = -1.0;
                int threshold = 128;

                for (int t = 0; t < 256; t++)
                {
                    backgroundCount += histogram[t];
                    if (backgroundCount == 0) continue;

                    int foregroundCount = count - backgroundCount;
                    if (foregroundCount == 0) break;   // 全部归到一边了，后面的 t 没有意义

                    backgroundSum += (long)t * histogram[t];

                    double meanBg = (double)backgroundSum / backgroundCount;
                    double meanFg = (double)(totalSum - backgroundSum) / foregroundCount;
                    double betweenClassVariance =
                        (double)backgroundCount * foregroundCount * (meanBg - meanFg) * (meanBg - meanFg);

                    if (betweenClassVariance > maxVariance)
                    {
                        maxVariance = betweenClassVariance;
                        threshold = t;
                    }
                }

                // ---- 第三步：按阈值压成纯黑白 ----
                var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                BitmapData outLocked = result.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte* dst = (byte*)outLocked.Scan0;
                    for (int i = 0; i < count; i++)
                    {
                        // 注意是「> 阈值算白」：
                        // 阈值本身那一档留给深色侧，这样浅色背景不会被误判成字
                        byte v = pixels[i * 4] > (byte)threshold ? (byte)255 : (byte)0;
                        dst[i * 4] = v;
                        dst[i * 4 + 1] = v;
                        dst[i * 4 + 2] = v;
                        dst[i * 4 + 3] = 255;
                    }
                }
                finally
                {
                    result.UnlockBits(outLocked);
                }

                AppLogger.Info($"二值化完成：阈值 {threshold}（{width}×{height}）");
                return result;
            }
            finally
            {
                gray.UnlockBits(locked);
            }
        }
        finally
        {
            gray.Dispose();
        }
    }
}

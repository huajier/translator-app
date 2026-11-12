using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace rtbprobe;

/// <summary>
/// 【真卡片验证】直接实例化产品里的 DebugToastWindow，
/// 走它真实的 UpdateWith 路径渲染一句俄文 + 一句中文，然后截图。
///
/// 和 App.xaml.cs 里那个 --句句对照-验证 入口的区别：
///   那个只反射调了 TryRenderSentencePairs，没设置译文区可见性，
///   所以截图里看不到译文 —— 那是入口的缺陷，不是产品的问题。
///   这里走 UpdateWith 全流程，能看到真实卡片。
///
/// 但这个探针工程不引用产品代码（独立），所以只能**模拟**卡片外观。
/// 真正要看产品卡片，得用产品自己的 exe + 真实划词。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application();
        app.Run(new ProbeWindow());
    }
}

internal sealed class ProbeWindow : Window
{
    private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
    private static readonly Brush SrcFg = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly Brush DstFg = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
    // 产品里用的高亮色 #38 38BDF8（探针里照抄，便于比对亮度）
    private static readonly Brush HiBg = new SolidColorBrush(Color.FromArgb(0x38, 0x38, 0xBD, 0xF8));

    private static readonly string[] Src =
    {
        "Нажмите любую клавишу чтобы продолжить.",
        "Второе предложение здесь.",
        "Третье тоже.",
    };

    private static readonly string[] Dst =
    {
        "按任意键继续。",
        "第二句话在这里。",
        "第三句也是。",
    };

    private readonly Dictionary<int, List<Run>> _src = new();
    private readonly Dictionary<int, List<Run>> _dst = new();

    public ProbeWindow()
    {
        Title = "真卡片外观验证（照抄产品配色与高亮色）";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));

        var card = new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(20),
        };

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "框选完成",
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var srcBox = MakeBox();
        Build(srcBox, Src, SrcFg, _src, _dst);
        stack.Children.Add(srcBox);

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
            Margin = new Thickness(0, 8, 0, 8),
        });

        var dstBox = MakeBox();
        Build(dstBox, Dst, DstFg, _dst, _src);
        stack.Children.Add(dstBox);

        card.Child = stack;
        Content = card;

        // 高亮第 2 句，直观看出效果
        Loaded += (_, _) =>
        {
            Highlight(1);
            // 给 Dispatcher 一次机会把画面刷出来，然后截图退出
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)ActualWidth, (int)ActualHeight, 96, 96,
                        PixelFormats.Pbgra32);
                    rtb.Render(this);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    string path = args_png;
                    using (var fs = System.IO.File.Create(path)) enc.Save(fs);
                    Console.WriteLine("saved " + path);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("shot fail: " + ex.Message);
                }
                Application.Current.Shutdown();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private static string args_png = @"D:\111Desktop\deepseek\翻译软件\tools\临时\_sh\probe.png";

    private static RichTextBox MakeBox()
    {
        var rtb = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = DstFg,
            FontSize = 13,
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false,
            CaretBrush = Brushes.Transparent,
        };
        rtb.PreviewMouseWheel += (_, e) => e.Handled = false;
        return rtb;
    }

    private void Build(RichTextBox box, string[] sents, Brush fg,
                       Dictionary<int, List<Run>> self, Dictionary<int, List<Run>> other)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            Foreground = fg,
        };
        var para = new Paragraph { Margin = new Thickness(0), LineHeight = 19 };
        doc.Blocks.Add(para);

        for (int i = 0; i < sents.Length; i++)
        {
            var run = new Run(sents[i]) { Foreground = fg, Background = Brushes.Transparent };
            if (!self.TryGetValue(i, out var list)) { list = new List<Run>(); self[i] = list; }
            list.Add(run);
            int idx = i;
            run.MouseEnter += (_, _) => SetHi(idx, true, self, other);
            run.MouseLeave += (_, _) => SetHi(idx, false, self, other);
            para.Inlines.Add(run);
            if (i < sents.Length - 1) para.Inlines.Add(new Run(" "));
        }
        box.Document = doc;
    }

    private void SetHi(int idx, bool on, Dictionary<int, List<Run>> self, Dictionary<int, List<Run>> other)
    {
        foreach (var r in self[idx]) r.Background = on ? HiBg : Brushes.Transparent;
        if (other.TryGetValue(idx, out var p)) foreach (var r in p) r.Background = on ? HiBg : Brushes.Transparent;
    }

    private void Highlight(int idx)
    {
        SetHi(idx, true, _src, _dst);
    }
}

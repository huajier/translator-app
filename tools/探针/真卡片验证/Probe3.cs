using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CardProbe;

/// <summary>
/// 验证 gc6：Highlight(g, fromSource) 只滚「另一侧」，且鼠标所在侧一动不动。
///
/// 测四件事：
///   1 从原文侧悬停 → 原文 ScrollViewer 的 offset 全程不变，只有译文在动
///   2 从译文侧悬停 → 译文 offset 全程不变，只有原文在动
///   3 快速扫过 0..19 两遍 → 两侧 offset 稳定（第二遍不再有新位移），不抽抽
///   4 另一侧滚过去之后，该句确实落在可见区内
///   5 两侧高亮仍然同时点亮（这条不许退步）
///   6 fromSource=null（程序主动调）→ 两侧都不滚
/// </summary>
public static class Probe3
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.13.gc6";

    public static void Run()
    {
        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "TranslatorApp.dll"));
        var toastType = asm.GetType("TranslatorApp.UI.Shared.DebugToastWindow")!;
        var settingsType = asm.GetType("TranslatorApp.Core.Capture.AppSettings")!;
        var capType = asm.GetType("TranslatorApp.Core.Capture.CaptureResult")!;
        var trType = asm.GetType("TranslatorApp.Core.Translate.TranslationResult")!;
        var alignType = asm.GetType("TranslatorApp.Core.Translate.SentenceAligner")!;

        settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        settingsType.GetMethod("SetSentenceHighlight", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { true });

        var sbE = new System.Text.StringBuilder();
        var sbZ = new System.Text.StringBuilder();
        for (int i = 1; i <= 20; i++)
        {
            sbE.Append($"This is English sentence number {i}, with some padding text to make it longer. ");
            sbZ.Append($"这是第{i}句中文译文，加些填充文字让它长一点。");
        }
        string en = sbE.ToString(), zh = sbZ.ToString();

        const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        var src = (List<string>)alignType.GetMethod("SplitForeign", SF)!.Invoke(null, new object[] { en })!;
        var dst = (List<string>)alignType.GetMethod("SplitChinese", SF)!.Invoke(null, new object[] { zh })!;
        var pairsObj = alignType.GetMethod("Align", SF)!.Invoke(null, new object?[] { src, dst, 2 })!;
        int groups = ((ICollection)pairsObj).Count;
        Console.WriteLine($"切句 源{src.Count} / 译{dst.Count} → 对齐 {groups} 组");

        var win = (Window)Activator.CreateInstance(toastType)!;
        win.Width = 560; win.Height = 760; win.Left = 200; win.Top = 40;
        win.Show(); win.UpdateLayout();

        toastType.GetMethod("SetAlignedSentences", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(win, new object?[] { src, dst, pairsObj });
        try
        {
            toastType.GetMethod("UpdateWith", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                null, new[] { capType, trType, typeof(bool) }, null)!
                .Invoke(win, new[] { MakeCap(capType, en), MakeTr(trType, zh), false });
        }
        catch { }
        win.UpdateLayout();

        var textScroll = (ScrollViewer)toastType.GetField("TextScroll", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;
        var transScroll = (ScrollViewer)toastType.GetField("TransScroll", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;
        var mainRich = (RichTextBox)toastType.GetField("MainRich", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;
        var transRich = (RichTextBox)toastType.GetField("TransRich", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;

        Console.WriteLine($"MainRich 可见={mainRich.Visibility}  TextScroll viewport={textScroll.ViewportHeight:F0} 可滚={textScroll.ScrollableHeight:F0}");
        Console.WriteLine($"TransRich 可见={transRich.Visibility} TransScroll viewport={transScroll.ViewportHeight:F0} 可滚={transScroll.ScrollableHeight:F0}");
        Console.WriteLine();

        var hi = toastType.GetMethod("Highlight", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Hi(int g, bool? from) { hi.Invoke(win, new object?[] { g, from }); win.UpdateLayout(); }

        // ---------- 1 原文侧悬停：原文必须一动不动 ----------
        Console.WriteLine("=== 1 鼠标在原文侧（fromSource=true）：原文 offset 应当全程不变 ===");
        Hi(0, true);
        double srcBase = textScroll.VerticalOffset;
        double transBase = transScroll.VerticalOffset;
        bool srcMoved = false;
        var transAfter = new List<double>();
        for (int g = 0; g < Math.Min(20, groups); g++)
        {
            Hi(g, true);
            if (Math.Abs(textScroll.VerticalOffset - srcBase) > 0.5) srcMoved = true;
            transAfter.Add(transScroll.VerticalOffset);
        }
        Console.WriteLine($"  原文 offset：起点 {srcBase:F1} → 扫完 {textScroll.VerticalOffset:F1}   变化过? {(srcMoved ? "❌ 变了" : "✅ 没变")}");
        Console.WriteLine($"  译文 offset：起点 {transBase:F1} → 扫完 {transScroll.VerticalOffset:F1}（最小值 {Min(transAfter):F1} 最大值 {Max(transAfter):F1}）");
        Console.WriteLine();
        var trace = new List<string>();
        for (int g = 0; g < Math.Min(12, groups); g++)
        {
            Hi(g, true);
            trace.Add($"{g}→原文{textScroll.VerticalOffset:F0}/译文{transScroll.VerticalOffset:F0}");
        }
        Console.WriteLine("  逐组： " + string.Join("  ", trace));
        Console.WriteLine();

        // ---------- 2 译文侧悬停：译文必须一动不动 ----------
        Console.WriteLine("=== 2 鼠标在译文侧（fromSource=false）：译文 offset 应当全程不变 ===");
        Hi(0, false);
        transBase = transScroll.VerticalOffset;
        srcBase = textScroll.VerticalOffset;
        bool transMoved = false;
        for (int g = 0; g < Math.Min(20, groups); g++)
        {
            Hi(g, false);
            if (Math.Abs(transScroll.VerticalOffset - transBase) > 0.5) transMoved = true;
        }
        Console.WriteLine($"  译文 offset：起点 {transBase:F1} → 扫完 {transScroll.VerticalOffset:F1}   变化过? {(transMoved ? "❌ 变了" : "✅ 没变")}");
        Console.WriteLine($"  原文 offset：起点 {srcBase:F1} → 扫完 {textScroll.VerticalOffset:F1}");
        Console.WriteLine();

        // ---------- 3 快速扫两遍：第二遍不应再产生新位移（=不抽抽） ----------
        Console.WriteLine("=== 3 快速扫过（同一侧连扫两遍）：第二遍应完全无新位移 ===");
        Hi(0, true);
        var first = new List<double>();
        for (int g = 0; g < groups; g++) { Hi(g, true); first.Add(transScroll.VerticalOffset); }
        var second = new List<double>();
        for (int g = 0; g < groups; g++) { Hi(g, true); second.Add(transScroll.VerticalOffset); }
        int diff = 0;
        for (int i = 0; i < first.Count; i++) if (Math.Abs(first[i] - second[i]) > 0.5) diff++;
        Console.WriteLine($"  第一遍终点 {first[^1]:F1} / 第二遍终点 {second[^1]:F1}；两组序列不一致的组数 = {diff} {(diff == 0 ? "✅ 稳定" : "⚠️ 有抖动")}");
        Console.WriteLine();

        // ---------- 4 另一侧确实滚到了、该句在可见区内 ----------
        Console.WriteLine("=== 4 从原文悬停某组后，译文那句是否落在译文可见区内 ===");
        foreach (int g in new[] { 3, 8, 14 })
        {
            if (g >= groups) continue;
            Hi(g, true);
            var runs = RunsOf(win, "_runsByGroupTarget", g);
            if (runs.Count == 0) { Console.WriteLine($"  组{g}: 译文侧没有对应 Run"); continue; }
            Rect r = runs[0].ContentStart.GetCharacterRect(LogicalDirection.Forward);
            double rel = r.Top - transScroll.VerticalOffset;
            bool vis = rel >= 0 && rel <= transScroll.ViewportHeight;
            Console.WriteLine($"  组{g}: 译文句相对可见区 y={rel:F0}（可见区高 {transScroll.ViewportHeight:F0}）{(vis ? "✅ 可见" : "❌ 不可见")}");
        }
        Console.WriteLine();

        // ---------- 5 两侧仍同时点亮 ----------
        Console.WriteLine("=== 5 高亮仍然两侧同时点亮（不许退步）===");
        Hi(8 < groups ? 8 : groups - 1, true);
        int gi = 8 < groups ? 8 : groups - 1;
        string srcColor = ColorOf(win, "_runsByGroupSource", gi);
        string dstColor = ColorOf(win, "_runsByGroupTarget", gi);
        var highlightField = toastType.GetField("_highlightBrush", BindingFlags.NonPublic | BindingFlags.Instance);
        string want = highlightField == null ? "?" : ((SolidColorBrush)highlightField.GetValue(win)!).Color.ToString();
        Console.WriteLine($"  组{gi}: 原文={srcColor}  译文={dstColor}  期望={want}  {((srcColor == want && dstColor == want) ? "✅ 两侧都亮" : "❌ 有一侧没亮")}");
        Console.WriteLine();

        // ---------- 6 fromSource=null：两侧都不滚 ----------
        Console.WriteLine("=== 6 程序主动调 Highlight(g) 不给 fromSource：两侧都不该滚 ===");
        Hi(2, null);
        double a1 = textScroll.VerticalOffset, b1 = transScroll.VerticalOffset;
        Hi(Math.Min(17, groups - 1), null);
        Console.WriteLine($"  原文 {a1:F1} → {textScroll.VerticalOffset:F1}  译文 {b1:F1} → {transScroll.VerticalOffset:F1}  " +
            ((Math.Abs(textScroll.VerticalOffset - a1) < 0.5 && Math.Abs(transScroll.VerticalOffset - b1) < 0.5) ? "✅ 都没滚" : "❌ 滚了"));
        Console.WriteLine();

        // ---------- 截图 ----------
        try
        {
            Hi(Math.Min(14, groups - 1), true);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(560, 760, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(win);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(@"D:\111Desktop\deepseek\翻译软件\tools\临时\_sh\gc6-scroll.png");
            enc.Save(fs);
            Console.WriteLine("截图: tools\\临时\\_sh\\gc6-scroll.png");
        }
        catch (Exception ex) { Console.WriteLine("截图失败 " + ex.Message); }

        win.Close();
    }

    private static object? Field(Window win, string name) => win.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(win);

    private static List<Run> RunsOf(Window win, string field, int g)
    {
        var dict = (System.Collections.IDictionary?)win.GetType()
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win);
        if (dict != null && dict.Contains(g)) return (List<Run>)dict[g]!;
        return new List<Run>();
    }

    private static string ColorOf(Window win, string field, int g)
    {
        var runs = RunsOf(win, field, g);
        if (runs.Count == 0) return "无Run";
        return runs[0].Background is SolidColorBrush b ? b.Color.ToString() : runs[0].Background?.ToString() ?? "null";
    }

    private static double Min(List<double> v) { double m = double.MaxValue; foreach (var x in v) if (x < m) m = x; return v.Count == 0 ? 0 : m; }
    private static double Max(List<double> v) { double m = double.MinValue; foreach (var x in v) if (x > m) m = x; return v.Count == 0 ? 0 : m; }

    private static object MakeCap(Type t, string text)
    {
        var o = Activator.CreateInstance(t)!;
        SetP(t, o, "Text", text); SetP(t, o, "Success", true);
        SetP(t, o, "Channel", "probe"); SetP(t, o, "ElapsedMs", 1L);
        SetP(t, o, "LineCount", 1); SetP(t, o, "Detail", "");
        return o;
    }

    private static object MakeTr(Type t, string text)
    {
        var o = Activator.CreateInstance(t)!;
        SetP(t, o, "Text", text); SetP(t, o, "Provider", "probe");
        SetP(t, o, "ElapsedMs", 1L); SetP(t, o, "Detail", "");
        SetP(t, o, "DetectedLanguage", "en");
        return o;
    }

    private static void SetP(Type t, object o, string name, object? v)
    {
        var p = t.GetProperty(name);
        if (p?.CanWrite == true) { p.SetValue(o, v); return; }
        t.GetField("<" + name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(o, v);
    }
}

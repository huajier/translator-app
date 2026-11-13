using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace CardProbe;

public static class Program
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.13.gc4";

    [STAThread]
    public static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppDomain.CurrentDomain.AssemblyResolve += (_, a) =>
        {
            string n = new AssemblyName(a.Name).Name + ".dll";
            string p = System.IO.Path.Combine(DllDir, n);
            return System.IO.File.Exists(p) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(p) : null;
        };
        try { Probe9.Run(); }
        catch (Exception ex) { Console.WriteLine("ERR " + ex.Message); }
        finally { Application.Current?.Shutdown(); }
    }

    private static void Run()
    {
        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "TranslatorApp.dll"));
        var toastType = asm.GetType("TranslatorApp.UI.Shared.DebugToastWindow")!;
        var settingsType = asm.GetType("TranslatorApp.Core.Capture.AppSettings")!;
        var capType = asm.GetType("TranslatorApp.Core.Capture.CaptureResult")!;
        var trType = asm.GetType("TranslatorApp.Core.Translate.TranslationResult")!;
        var alignType = asm.GetType("TranslatorApp.Core.Translate.SentenceAligner")!;

        settingsType.GetMethod("SetSentenceHighlight", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { true });

        const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        string en = "Obtaining Spellbooks. Besides finding the odd spellbook. Shops can come in several varieties.";
        string zh = "获取法术书。除了找到零散的法术书。商店有几种类型。";
        var src = (List<string>)alignType.GetMethod("SplitForeign", SF)!.Invoke(null, new object[] { en })!;
        var dst = (List<string>)alignType.GetMethod("SplitChinese", SF)!.Invoke(null, new object[] { zh })!;
        var pairsObj = alignType.GetMethod("Align", SF)!.Invoke(null, new object?[] { src, dst, 2 })!;
        Console.WriteLine("对齐 " + ((ICollection)pairsObj).Count + " 组");

        var win = (Window)Activator.CreateInstance(toastType)!;
        win.Width = 520; win.Height = 400; win.Left = 200; win.Top = 200;
        win.Show();
        win.UpdateLayout();

        toastType.GetMethod("SetAlignedSentences", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(win, new object?[] { src, dst, pairsObj });

        var cap = MakeCapture(capType, en);
        var tr = MakeTrans(trType, zh);
        try
        {
            toastType.GetMethod("UpdateWith", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                null, new[] { capType, trType, typeof(bool) }, null)!
                .Invoke(win, new[] { cap, tr, false });
        }
        catch { }
        win.UpdateLayout();

        var mainRich = (RichTextBox)toastType.GetField("MainRich",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;
        Console.WriteLine("MainRich Vis=" + mainRich.Visibility + " HitTest=" + mainRich.IsHitTestVisible);

        Run? firstRun = null;
        foreach (var b in mainRich.Document.Blocks)
        {
            if (b is Paragraph p)
            {
                foreach (var i in p.Inlines)
                {
                    if (i is Run r) { firstRun = r; break; }
                }
                if (firstRun != null) break;
            }
        }

        if (firstRun is null) { Console.WriteLine("没有 Run"); win.Close(); return; }
        Console.WriteLine("第一个 Run: " + firstRun.Text);
        Console.WriteLine("  初始背景 = " + Describe(firstRun.Background));

        firstRun.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
        { RoutedEvent = Mouse.MouseEnterEvent, Source = firstRun });
        win.UpdateLayout();
        Console.WriteLine("  MouseEnter 后 = " + Describe(firstRun.Background));

        firstRun.Background = Brushes.Transparent;
        toastType.GetMethod("Highlight", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(win, new object[] { 0 });
        win.UpdateLayout();
        Console.WriteLine("  直接 Highlight(0) 后 = " + Describe(firstRun.Background));

        var dict = (IDictionary?)toastType.GetField("_runsByParagraph",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win);
        Console.WriteLine("  _runsByParagraph 组数 = " + dict?.Count);

        Console.WriteLine("=== 祖先链 ===");
        DependencyObject? par = VisualTreeHelper.GetParent(firstRun);
        int d = 0;
        while (par != null && d < 8)
        {
            string info = par.GetType().Name;
            if (par is UIElement ue) info += " HitTest=" + ue.IsHitTestVisible + " Vis=" + ue.Visibility;
            Console.WriteLine("  ^ " + info);
            par = VisualTreeHelper.GetParent(par);
            d++;
        }
        win.Close();
    }

    private static string Describe(Brush? b)
        => b is SolidColorBrush sb ? $"#{sb.Color.A:X2}{sb.Color.R:X2}{sb.Color.G:X2}{sb.Color.B:X2}" : (b?.ToString() ?? "null");

    private static object MakeCapture(Type t, string text)
    {
        var o = Activator.CreateInstance(t)!;
        SetP(t, o, "Text", text); SetP(t, o, "Success", true);
        SetP(t, o, "Channel", "probe"); SetP(t, o, "ElapsedMs", 1L);
        SetP(t, o, "LineCount", 1); SetP(t, o, "Detail", "");
        return o;
    }

    private static object MakeTrans(Type t, string text)
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

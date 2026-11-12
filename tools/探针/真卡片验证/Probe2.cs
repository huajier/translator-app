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

/// <summary>
/// 验证 gc4 的 MouseMove 命中测试：模拟鼠标在 RichTextBox 上移动，
/// 看对应 Run 会不会被 Highlight。
/// </summary>
public static class Probe2
{
    private const string DllDir = @"D:\111Desktop\deepseek\翻译软件\release\v0.4.13.gc5";

    public static void Run()
    {
        var asm = Assembly.LoadFrom(System.IO.Path.Combine(DllDir, "TranslatorApp.dll"));
        var toastType = asm.GetType("TranslatorApp.UI.Shared.DebugToastWindow")!;
        var settingsType = asm.GetType("TranslatorApp.Core.Capture.AppSettings")!;
        var capType = asm.GetType("TranslatorApp.Core.Capture.CaptureResult")!;
        var trType = asm.GetType("TranslatorApp.Core.Translate.TranslationResult")!;
        var alignType = asm.GetType("TranslatorApp.Core.Translate.SentenceAligner")!;

        // ★ 关键：先 Load 配置（执行 AI 提示的坑）
        settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        settingsType.GetMethod("SetSentenceHighlight", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { true });

        const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        string en = "Obtaining Spellbooks. Besides finding the odd spellbook. Shops can come in several varieties.";
        string zh = "获取法术书。除了找到零散的法术书。商店有几种类型。";
        var src = (List<string>)alignType.GetMethod("SplitForeign", SF)!.Invoke(null, new object[] { en })!;
        var dst = (List<string>)alignType.GetMethod("SplitChinese", SF)!.Invoke(null, new object[] { zh })!;
        var pairsObj = alignType.GetMethod("Align", SF)!.Invoke(null, new object?[] { src, dst, 2 })!;

        var win = (Window)Activator.CreateInstance(toastType)!;
        win.Width = 520; win.Height = 400; win.Left = 200; win.Top = 200;
        win.Show(); win.UpdateLayout();

        toastType.GetMethod("SetAlignedSentences", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(win, new object?[] { src, dst, pairsObj });

        try
        {
            toastType.GetMethod("UpdateWith", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                null, new[] { capType, trType, typeof(bool) }, null)!
                .Invoke(win, new[] { MakeCapture(capType, en), MakeTrans(trType, zh), false });
        }
        catch { }
        win.UpdateLayout();

        var mainRich = (RichTextBox)toastType.GetField("MainRich",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;
        var transRich = (RichTextBox)toastType.GetField("TransRich",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;

        // 检查外框可见性（执行 AI 说之前 TransScroll 是 Collapsed）
        var transScroll = (ScrollViewer)toastType.GetField("TransScroll",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;
        Console.WriteLine($"[可见性] MainRich={mainRich.Visibility} TransRich={transRich.Visibility} TransScroll={transScroll.Visibility}");

        // 反射调 FindRunAt 验证命中
        var findM = toastType.GetMethod("FindRunAt", BindingFlags.NonPublic | BindingFlags.Instance);
        Console.WriteLine($"[FindRunAt] {(findM is null ? "❌ 找不到" : "✅ 存在")}");

        if (findM is not null)
        {
            // 遍历 MainRich 里每个 Run 的 TextPointer，丢给 FindRunAt 看能不能命中自己
            int checkedN = 0, hitN = 0;
            foreach (var b in mainRich.Document.Blocks)
            {
                if (b is not Paragraph p) continue;
                foreach (var inl in p.Inlines)
                {
                    if (inl is not Run r) continue;
                    checkedN++;
                    // 取该 Run 文本区间的中间位置
                    TextPointer mid = r.ContentStart.GetPositionAtOffset(
                        Math.Max(1, r.Text.Length / 2)) ?? r.ContentStart;
                    var hit = findM.Invoke(win, new object?[] { mainRich, mid }) as Run;
                    bool ok = hit is not null && hit.Text == r.Text;
                    if (ok) hitN++;
                    Console.WriteLine($"  「{Trunc(r.Text, 28)}」Tag={r.Tag} → 命中「{Trunc(hit?.Text ?? "null", 28)}」{(ok ? " ✅" : " ❌")}");
                }
            }
            Console.WriteLine($"命中率 = {hitN}/{checkedN}");
        }

        // 模拟鼠标移动：直接调 box.RaiseEvent(MouseMove) 需要坐标，改用调 Highlight 已验证
        Console.WriteLine();
        Console.WriteLine("[双向高亮] 调 Highlight(1)：");
        toastType.GetMethod("Highlight", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(win, new object[] { 1 });
        win.UpdateLayout();
        foreach (var (label, box) in new[] { ("原文", mainRich), ("译文", transRich) })
        {
            int lit = 0;
            foreach (var b in box.Document.Blocks)
                if (b is Paragraph p)
                    foreach (var inl in p.Inlines)
                        if (inl is Run r && r.Background is SolidColorBrush sb && sb.Color.A > 0)
                            lit++;
            Console.WriteLine($"  {label}侧点亮 Run 数 = {lit}");
        }

        // 截图
        try
        {
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(520, 400, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(win);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            string png = @"D:\111Desktop\deepseek\翻译软件\tools\临时\_sh\gc4-card.png";
            using var fs = System.IO.File.Create(png);
            enc.Save(fs);
            Console.WriteLine("截图: " + png);
        }
        catch (Exception ex) { Console.WriteLine("截图失败 " + ex.Message); }

        win.Close();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

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

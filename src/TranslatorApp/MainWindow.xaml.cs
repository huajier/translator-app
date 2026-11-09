using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TranslatorApp.Core.Capture;
using TranslatorApp.Core.Logging;

namespace TranslatorApp;

/// <summary>
/// MainWindow.xaml.cs —— 主窗口（阶段四 T3b 起：使用说明 + 翻译设置）。
///
/// 这个文件干嘛的：
///   把设置区两个控件的值在「配置文件」和「界面」之间搬来搬去，不含任何翻译、取词逻辑。
///
/// 它和谁交互：
///   - App.xaml.cs 创建它，并挂上 Closing（点 × 时取消关闭、改为隐藏到托盘）；
///   - 标题统一取自 App.WindowTitle（不要在 XAML 里再手写第二份，否则单实例查找会找错窗口）；
///   - AppSettings   存盘/读盘；
///   - TrayIcon      托盘菜单里那个翻译开关，两边必须同步。
///
/// 要改它该动哪里：
///   - 窗口大小、外观     → MainWindow.xaml；
///   - 标题文字           → App.WindowTitle（本文件只做取值）；
///   - 目标语言的可选列表 → LangOptions（下面那个数组，顺序就是下拉框顺序）。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 下拉框里的固定项：显示名 + 语言代码。**顺序不许随手调**（用户拍板的顺序）。
    ///
    /// 【为什么代码里存的是数组、不是 XAML 里的 ComboBoxItem 列表】
    ///   显示名和代码要成对维护，写在 C# 里一眼能看完 8 对；
    ///   散到 XAML 里改一个漏一个，就会出现「界面写日语、存进去 zh-Hans」这种静默错位。
    /// </summary>
    private static readonly (string Name, string Code)[] LangOptions =
    {
        ("简体中文（默认）", "zh-Hans"),
        ("繁体中文", "zh-Hant"),
        ("英语", "en"),
        ("日语", "ja"),
        ("韩语", "ko"),
        ("俄语", "ru"),
        ("法语", "fr"),
        ("德语", "de"),
    };

    /// <summary>「其他…」这一项的 Tag。选中它 = 用户要自己手填，不是要存这个字符串。</summary>
    private const string OtherTag = "__other__";

    /// <summary>正在按配置文件刷新界面 —— 此时控件的变更事件不许往回写，否则会互相触发。</summary>
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        Title = App.WindowTitle;   // 标题只此一处取值，别在 XAML 里再写一份

        BuildLangItems();
        LoadFromSettings();
    }

    /// <summary>按 LangOptions 建下拉项，最后追加「其他…」。</summary>
    private void BuildLangItems()
    {
        foreach (var (name, code) in LangOptions)
        {
            LangBox.Items.Add(new ComboBoxItem { Content = $"{name}（{code}）", Tag = code });
        }

        LangBox.Items.Add(new ComboBoxItem { Content = "其他…（在下拉框里输入语言代码）", Tag = OtherTag });
    }

    /// <summary>把配置文件里的当前值搬到界面上。</summary>
    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            TranslateCheck.IsChecked = AppSettings.TranslateEnabled;

            // 2026-11-09：RapidOCR 开关也搬到主界面，这里把当前值搬上去
            RapidOcrCheck.IsChecked = AppSettings.RapidOcrPrimary;

            string cur = AppSettings.TranslateTargetLang;
            var hit = LangBox.Items.OfType<ComboBoxItem>()
                                     .FirstOrDefault(i => i.Tag as string == cur);

            if (hit is not null)
            {
                // 8 个固定项之一：直接选中
                LangBox.SelectedItem = hit;
            }
            else
            {
                // 用户手填过（或旧配置里是别的代码）：**原样显示在框里**，不许静默跳回简体
                LangBox.SelectedIndex = -1;
                LangBox.Text = cur;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 托盘菜单改了开关之后，把它同步到本窗口（由 TrayIcon 调）。
    /// 窗口可能已隐藏，所以这里只改控件、不碰显示状态。
    /// </summary>
    internal void SyncFromSettings()
    {
        if (TranslateCheck.IsChecked == AppSettings.TranslateEnabled) return;

        _loading = true;
        try { TranslateCheck.IsChecked = AppSettings.TranslateEnabled; }
        finally { _loading = false; }

        AppLogger.Info($"主窗口：翻译开关跟随托盘变成「{(AppSettings.TranslateEnabled ? "开" : "关")}」");
    }

    // ------------------------------------------------------------------ 开关

    private void OnTranslateChecked(object sender, RoutedEventArgs e) => ApplyTranslateChecked(true);

    private void OnTranslateUnchecked(object sender, RoutedEventArgs e) => ApplyTranslateChecked(false);

    private void ApplyTranslateChecked(bool on)
    {
        if (_loading) return;

        AppSettings.SetTranslateEnabled(on);       // 内部存盘，重启后状态还在
        AppLogger.Info($"主窗口：翻译开关被点成「{(on ? "开" : "关")}」");

        // 第一次打开要弹那张「文字会发到微软」的卡片（和托盘走同一个入口，不另写一套）
        if (on) App.EnsureTranslateNotice();
    }

    // ------------------------------------------------------------------ RapidOCR 引擎

    /// <summary>
    /// 主界面「游戏字识别（快）」勾选框（2026-11-09）。
    ///
    /// 【为什么要放主界面】用户原话「开关不能放在主页面里面吗」——
    ///   以前只能手改 settings.json，对零基础用户等于没有这个开关。
    /// 【改了即时存盘】AppSettings.SetRapidOcrPrimary 内部 Save()，
    ///   重启后维持用户的选择。
    /// 【下一帧生效】这次取词已开始的不会被打断，从下一次框选起走新通道。
    /// </summary>
    private void OnRapidOcrChecked(object sender, RoutedEventArgs e) => ApplyRapidOcrChecked(true);

    private void OnRapidOcrUnchecked(object sender, RoutedEventArgs e) => ApplyRapidOcrChecked(false);

    private void ApplyRapidOcrChecked(bool on)
    {
        if (_loading) return;

        AppSettings.SetRapidOcrPrimary(on);
        AppLogger.Info($"主窗口：游戏字识别（RapidOCR）被点成「{(on ? "开" : "关")}」");
    }

    // ------------------------------------------------------------------ 目标语言

    private void OnLangSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (LangBox.SelectedItem is not ComboBoxItem item) return;

        if (item.Tag as string == OtherTag)
        {
            // 选了「其他…」：清空让用户直接打字，**不写盘**（此刻还没有值）
            LangBox.SelectedIndex = -1;
            LangBox.Text = "";
            LangBox.Focus();
            return;
        }

        CommitLang(item.Tag as string);
    }

    private void OnLangLostFocus(object sender, RoutedEventArgs e) => CommitLangFromText();

    // 这里必须写全名：本工程同时引用 WPF 和 WinForms（托盘），两边都有 KeyEventArgs。
    private void OnLangKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CommitLangFromText();
        e.Handled = true;
    }

    /// <summary>
    /// 把下拉框里「当前显示的东西」写进配置。
    ///
    /// 【2026-11-04 真机验收抓到的 bug】原先是直接 `CommitLang(LangBox.Text)`，
    ///   但 ComboBox.Text 是**显示名**：选「日语」时它是 `日语（ja）`，会被原样存进
    ///   `translateTargetLang`，下一次翻译请求带着 `to=日语（ja）` 去问必应 —— 必 400。
    ///   所以先看是不是 8 项里的某一项（读它的 Tag，那才是语言码），
    ///   不在里面才当用户手填的代码用。
    /// </summary>
    private void CommitLangFromText()
    {
        if (_loading) return;

        // 选中了列表里的某一项：以它的 Tag 为准。选中项可能和输入框不同步，
        // 所以先按文字反查一遍，再退回当前选中项。
        string text = (LangBox.Text ?? "").Trim();
        var byText = LangBox.Items.OfType<ComboBoxItem>()
                                   .FirstOrDefault(i => string.Equals(i.Content as string, text, StringComparison.Ordinal));
        if (byText is ComboBoxItem hit && hit.Tag as string != OtherTag)
        {
            CommitLang(hit.Tag as string);
            return;
        }

        if (LangBox.SelectedItem is ComboBoxItem sel && sel.Tag as string != OtherTag &&
            string.IsNullOrEmpty(text))
        {
            CommitLang(sel.Tag as string);
            return;
        }

        CommitLang(text);   // 手填的语言码（xx-YY 这类）
    }

    /// <summary>把语言代码写进配置文件。空串直接丢弃 —— 存空等于让请求必 400。</summary>
    private void CommitLang(string? code)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0 || code == OtherTag) return;      // 空串不许存
        if (code == AppSettings.TranslateTargetLang) return;   // 没变就不折腾

        AppSettings.SetTranslateTargetLang(code);
        AppLogger.Info($"主窗口：目标语言改成「{code}」");
    }
}

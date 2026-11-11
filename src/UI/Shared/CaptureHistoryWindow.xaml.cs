using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TranslatorApp.Core.Capture;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.UI.Shared;

/// <summary>
/// 【2026-11-10 用户需求】主界面的「识别记录」窗口，最近 10 条。
///
/// 【用户原话】「我现在主页面下面加个识别日志，让用户识别出来后不小心点掉了
///   还能在识别日志里面找出来。」
///
/// 【数据从哪来】只读当前 exe 旁边的 logs\取词记录-*.txt ——
///   换版本会换发布目录，所以不跨目录捞历史（见 CaptureRecord.ReadRecent）。
/// 【什么时候读】窗口构造时读一次。主窗口启动时**不读**，
///   这样点按钮之外的开销为零，不会拖慢启动。
/// </summary>
public partial class CaptureHistoryWindow : Window
{
    private readonly List<CaptureRecord.CaptureRecordEntry> _entries = new();

    /// <summary>列表里每一行显示什么（两行：头部 + 原文预览）。</summary>
    private sealed class Row
    {
        public string Header { get; init; } = string.Empty;
        public string Preview { get; init; } = string.Empty;
        public CaptureRecord.CaptureRecordEntry Entry { get; init; }
    }

    /// <summary>构造并填充最近 10 条。</summary>
    public CaptureHistoryWindow()
    {
        InitializeComponent();

        // 【2026-11-11 用户反馈 2：窗口不实时更新】
        //   原来只在构造时读一次，用户框了新词再回到这个窗口还是旧的。
        //   现在每次窗口被激活都重读 —— 打开时、从别的窗口切回来时都会触发。
        //   不用定时器、不起后台线程，只在激活时读一次文件。
        Activated += (_, _) => Reload(preserveSelection: true);

        Reload(preserveSelection: false);
    }

    /// <summary>
    /// 【2026-11-11】重新读最近 10 条并刷新列表。
    ///
    /// 【保持选中项】按「时间 + 原文前 40 字」匹配找回同一条；
    ///   找不到（比如那条已经掉出 10 条）就选第一条（最新）。
    /// </summary>
    private void Reload(bool preserveSelection)
    {
        // 记下当前选中的是哪个「时间+预览」
        string? keepTime = null;
        string? keepPreview = null;
        if (preserveSelection && RecordList.SelectedItem is Row cur)
        {
            keepTime = cur.Entry.Time;
            keepPreview = cur.Preview;
        }

        _entries.Clear();
        _entries.AddRange(CaptureRecord.ReadRecent(10));

        if (_entries.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            RecordList.Visibility = Visibility.Collapsed;
            RecordList.ItemsSource = null;
            FullTextBox.Text = string.Empty;
            CopyButton.IsEnabled = false;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        RecordList.Visibility = Visibility.Visible;

        var rows = new List<Row>(_entries.Count);
        foreach (CaptureRecord.CaptureRecordEntry e in _entries)
        {
            rows.Add(new Row
            {
                Header = $"{e.Time} · {e.Channel} · {e.CharCount} 字",
                Preview = MakePreview(e.Text, 40),
                Entry = e,
            });
        }

        int pick = 0;
        if (keepTime is not null)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Entry.Time == keepTime && rows[i].Preview == keepPreview)
                {
                    pick = i;
                    break;
                }
            }
        }

        RecordList.ItemsSource = rows;
        RecordList.SelectedIndex = pick;
    }

    /// <summary>原文前 <paramref name="max"/> 字，超出加「…」；换行压成空格。</summary>
    private static string MakePreview(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "（空）";
        string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "…";
    }

    private void OnRecordSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordList.SelectedItem is Row row)
        {
            // 【2026-11-11 用户需求 4：记录里也要有译文】
            //   老记录没有译文（Translation = null）→ 只显示原文，不显示 "null"
            FullTextBox.Text = string.IsNullOrEmpty(row.Entry.Translation)
                ? row.Entry.Text
                : row.Entry.Text + "\n\n【译文】\n" + row.Entry.Translation;
            CopyButton.IsEnabled = true;
        }
        else
        {
            FullTextBox.Text = string.Empty;
            CopyButton.IsEnabled = false;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FullTextBox.Text)) return;
        try
        {
            System.Windows.Clipboard.SetText(FullTextBox.Text);
            AppLogger.Info($"识别记录：已复制 {FullTextBox.Text.Length} 字到剪贴板");
        }
        catch (Exception ex)
        {
            // 剪贴板偶尔被别的程序占着，失败不该让窗口崩
            AppLogger.Warn($"复制原文失败：{ex.GetType().Name} {ex.Message}");
            System.Windows.MessageBox.Show(this, "复制失败，请稍后再试或用鼠标选中后按 Ctrl+C。",
                            "识别记录", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Esc 关窗口（用户习惯，且提示词明确要求）。</summary>
    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}

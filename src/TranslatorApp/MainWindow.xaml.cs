using System.Windows;

namespace TranslatorApp;

/// <summary>
/// MainWindow.xaml.cs —— 阶段 1 的空壳主窗口。
///
/// 这个文件干嘛的：
///   只负责「看得见、能关（关 = 缩到托盘）」；不含任何翻译、取词、历史逻辑。
///
/// 它和谁交互：
///   - App.xaml.cs 创建它，并挂上 Closing（点 × 时取消关闭、改为隐藏到托盘）；
///   - 标题统一取自 App.WindowTitle（不要在 XAML 里再手写第二份，否则单实例查找会找错窗口）。
///
/// 要改它该动哪里：
///   - 窗口大小、外观 → MainWindow.xaml；
///   - 标题文字       → App.WindowTitle（本文件只做取值）。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = App.WindowTitle;
    }
}

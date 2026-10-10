<#
查点击归属.ps1 —— 阶段 2 专用：查「屏幕上某一点，现在归哪个窗口管」

这个脚本干嘛的：
  用户反馈「框选的时候下面的窗口还能拖、能选文字，要像截图那样下面的窗口点不动」。
  怎么证明「下面的窗口真的点不动」？
  不用模拟拖拽（模拟拖拽在某些环境下根本拖不动窗口，不可靠），
  而是直接问 Windows：**这一点上的鼠标事件，现在会发给哪个窗口？**

  - 平时（没在框选）：应该发给记事本 → 说明用户的正常操作没被干扰
  - 按住 Ctrl+Alt（正在框选）：应该发给「框选遮罩」→ 说明下面的窗口收不到点击，拖不动、选不了字
  - 松开 Ctrl+Alt：应该还给记事本 → 说明遮罩没有残留

怎么用：
  powershell -ExecutionPolicy Bypass -File "tools\脚本\查点击归属.ps1"
#>

param(
    [int]$x = 650,
    [int]$y = 226
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class HitProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr h, uint flags);   // 2 = GA_ROOT

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    /// 命中测试的结果：哪个窗口的标题、什么类、扩展样式里有没有「鼠标穿透」这一位
    public static string Describe(int x, int y) {
        var p = new POINT { x = x, y = y };
        IntPtr w = GetAncestor(WindowFromPoint(p), 2);

        if (w == IntPtr.Zero) return "（该点下面没有窗口）";

        var title = new StringBuilder(256);
        GetWindowTextW(w, title, 256);
        var cls = new StringBuilder(256);
        GetClassNameW(w, cls, 256);

        // WS_EX_TRANSPARENT = 0x20：有这一位时，Windows 的命中测试会跳过这个窗口，
        // 也就是「鼠标穿透」，点击会落到它下面的窗口去。
        long exStyle = GetWindowLongPtr(w, -20).ToInt64();   // GWL_EXSTYLE = -20
        bool transparent = (exStyle & 0x20) != 0;

        return "窗口='" + title + "'  类=" + cls +
               "  鼠标穿透=" + (transparent ? "开" : "关") +
               "  句柄=0x" + w.ToInt64().ToString("X");
    }
}
'@

Write-Host "查询坐标：($x, $y)" -ForegroundColor Cyan
Write-Host ("结果：" + [HitProbe]::Describe($x, $y))

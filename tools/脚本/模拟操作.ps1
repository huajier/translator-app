<#
模拟操作.ps1 —— 阶段 2 专用：不碰鼠标也能把「框选」这件事完整测一遍

这个脚本干嘛的：
  阶段 2 的验收要反复做同样的动作（按住 Ctrl+Alt 拖一下、按 Esc、只点一下……），
  靠人手点既慢又不准，写成脚本每条都能重复执行、结果可复现。

怎么用：
  powershell -ExecutionPolicy Bypass -File "tools\脚本\模拟操作.ps1" -场景 正常框选

它怎么做到不碰鼠标也能测：
  用 Windows 官方的 SendInput 接口「伪造」一次真实的 Ctrl+Alt+左键拖拽，
  系统会把它当成真人操作一样派发给全局钩子 —— 和你手动拖的效果完全一样。
#>

param(
    # 「中途按右键」「停鼠标」是 2026-08 用户要求改用右键取消时新增的两个场景
    [ValidateSet('正常框选', '中途松Esc', '中途松Ctrl', '只点一下', '不按修饰键', '按住不放', '只按修饰键', '松开按住', '中途按右键', '停鼠标')]
    [string]$场景 = '正常框选',

    [int]$x1 = 300, [int]$y1 = 200,
    [int]$x2 = 800, [int]$y2 = 500
)

$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class InputDriver {
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct Union { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public Union u; }
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] p, int size);

  const uint MOVE=0x01, LEFTDOWN=0x02, LEFTUP=0x04, RIGHTDOWN=0x08, RIGHTUP=0x10, ABSOLUTE=0x8000, VIRTUALDESK=0x4000, KEYUP=0x02;

  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

  static void Send(INPUT[] i) { SendInput((uint)i.Length, i, Marshal.SizeOf(typeof(INPUT))); }

  /// 【踩过的坑 · 测试脚本自己的错，不是程序的错】
  ///   SendInput 的 MOUSEEVENTF_ABSOLUTE **不是直接给像素**，而是把坐标归一化到 0~65535。
  ///   早先直接传 400（以为是像素），实际落点是 400*1919/65535 ≈ 12 像素，
  ///   于是测出来「坐标全不对」，差点冤枉了程序。
  ///   教训：结果不对时，先怀疑尺子（测试方法），再怀疑被量的东西。
  ///   VIRTUALDESK 这个标志也必须有，否则用的是主显示器而不是整个虚拟屏（多显示器会错）。
  static INPUT Mouse(int x,int y,uint f){
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);   // 虚拟屏原点
    int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);   // 虚拟屏宽高
    int nx = vw > 1 ? (int)Math.Round((x - vx) * 65535.0 / (vw - 1)) : 0;
    int ny = vh > 1 ? (int)Math.Round((y - vy) * 65535.0 / (vh - 1)) : 0;
    var i=new INPUT(); i.type=0;
    i.u.mi=new MOUSEINPUT{dx=nx, dy=ny, dwFlags=f|ABSOLUTE|VIRTUALDESK};
    return i;
  }
  static INPUT Key(ushort vk,bool up){ var i=new INPUT(); i.type=1; i.u.ki=new KEYBDINPUT{wVk=vk,dwFlags=up?KEYUP:0u}; return i; }
  static void MoveTo(int x,int y){ Send(new[]{Mouse(x,y,MOVE)}); }

  [StructLayout(LayoutKind.Sequential)] struct Pt { public int x, y; }
  [DllImport("user32.dll")] static extern bool GetCursorPos(out Pt p);

  /// 先把鼠标挪到某处**并且等它真的到了**再往下走。
  ///
  /// 【踩过的坑 · 测试脚本自己的错，不是程序的错】
  ///   原来只是「挪一下 + 睡 90 毫秒」就接着按左键。但 SendInput 是异步投递的，
  ///   光标有时还没走到位，左键就落在「上一位置」了——
  ///   于是同一条命令，有时测出 (400,300) 500×300，有时测出 (741,304) 159×296，
  ///   坐标忽对忽错，很容易误以为是程序坐标换算有 bug，追查半天。
  ///   现在改成**轮询 GetCursorPos，等到光标真的到位（±1 像素）才继续**，最多等 1 秒。
  static void Goto(int x,int y){
    for(int i=0;i<40;i++){
      MoveTo(x,y);
      System.Threading.Thread.Sleep(25);
      Pt p;
      if (GetCursorPos(out p) && Math.Abs(p.x-x)<=1 && Math.Abs(p.y-y)<=1) return;
    }
    Pt last; GetCursorPos(out last);
    Console.WriteLine("  [脚本警告] 光标没能到达 ({0},{1})，实际在 ({2},{3})", x, y, last.x, last.y);
  }

  /// 场景 1：正常框选。按住 Ctrl+Alt，左键从起点拖到终点再松开。
  public static void NormalDrag(int x1,int y1,int x2,int y2,int steps){
    Goto(x1,y1);                                    // 先落到确定的起点
    Send(new[]{Key(0x11,false), Key(0x12,false)});   // Ctrl + Alt 按下
    System.Threading.Thread.Sleep(150);
    Send(new[]{Mouse(x1,y1,LEFTDOWN)});              // 左键按下
    System.Threading.Thread.Sleep(100);
    for(int s=1;s<=steps;s++){                       // 分步移动，产生一串 mousemove
      MoveTo(x1+(x2-x1)*s/steps, y1+(y2-y1)*s/steps);
      System.Threading.Thread.Sleep(30);
    }
    Send(new[]{Mouse(x2,y2,LEFTUP)});                // 左键抬起
    System.Threading.Thread.Sleep(150);
    Send(new[]{Key(0x11,true), Key(0x12,true)});     // 松开 Ctrl + Alt
  }

  /// 场景 2：拖到一半按 Esc 取消。
  public static void EscapeDrag(int x1,int y1,int x2,int y2){
    Send(new[]{Key(0x11,false), Key(0x12,false)});
    System.Threading.Thread.Sleep(150);
    Send(new[]{Mouse(x1,y1,LEFTDOWN)});
    System.Threading.Thread.Sleep(100);
    MoveTo(x2,y2);
    System.Threading.Thread.Sleep(200);
    Send(new[]{Key(0x1B,false), Key(0x1B,true)});         // 按一下 Esc
    System.Threading.Thread.Sleep(200);
    Send(new[]{Mouse(x2,y2,LEFTUP)});
    System.Threading.Thread.Sleep(150);
    Send(new[]{Key(0x11,true), Key(0x12,true)});
  }

  /// 场景 3：拖到一半松开 Ctrl（Alt 仍按着）——应当立即取消。
  public static void ReleaseCtrlMidDrag(int x1,int y1,int x2,int y2){
    Send(new[]{Key(0x11,false), Key(0x12,false)});
    System.Threading.Thread.Sleep(150);
    Send(new[]{Mouse(x1,y1,LEFTDOWN)});
    System.Threading.Thread.Sleep(100);
    MoveTo(x2,y2);
    System.Threading.Thread.Sleep(250);
    Send(new[]{Key(0x11,true)});                          // 只松开 Ctrl
    System.Threading.Thread.Sleep(250);
    Send(new[]{Mouse(x2,y2,LEFTUP)});
    System.Threading.Thread.Sleep(150);
    Send(new[]{Key(0x12,true)});
  }

  /// 场景 4：按住 Ctrl+Alt 但只点一下不拖（框不到 10 像素）——应当什么都不发生。
  public static void ClickOnly(int x,int y){
    Goto(x,y);
    Send(new[]{Key(0x11,false), Key(0x12,false)});
    System.Threading.Thread.Sleep(150);
    Send(new[]{Mouse(x,y,LEFTDOWN)});
    System.Threading.Thread.Sleep(80);
    Send(new[]{Mouse(x,y,LEFTUP)});
    System.Threading.Thread.Sleep(200);
    Send(new[]{Key(0x11,true), Key(0x12,true)});
  }

  /// 场景 5：不按 Ctrl+Alt 直接拖 —— 绝不能出现选框，事件要原样传给下面的软件。
  public static void DragWithoutModifiers(int x1,int y1,int x2,int y2,int steps){
    Send(new[]{Mouse(x1,y1,LEFTDOWN)});
    System.Threading.Thread.Sleep(80);
    for(int s=1;s<=steps;s++){
      MoveTo(x1+(x2-x1)*s/steps, y1+(y2-y1)*s/steps);
      System.Threading.Thread.Sleep(30);
    }
    Send(new[]{Mouse(x2,y2,LEFTUP)});
  }

  /// 场景 6：按住 Ctrl+Alt 拖到某处后**不松手**，让选框一直留在屏幕上，
  /// 好让外部脚本截图取证（证明遮罩确实盖在别的软件上面）。
  /// 用完必须接着跑「松开按住」，否则鼠标会一直卡在按下状态。
  public static void HoldAt(int x1,int y1,int x2,int y2){
    Goto(x1,y1);
    Send(new[]{Key(0x11,false), Key(0x12,false)});
    System.Threading.Thread.Sleep(150);
    Send(new[]{Mouse(x1,y1,LEFTDOWN)});
    System.Threading.Thread.Sleep(100);
    for(int s=1;s<=6;s++){
      MoveTo(x1+(x2-x1)*s/6, y1+(y2-y1)*s/6);
      System.Threading.Thread.Sleep(40);
    }
    MoveTo(x2,y2);
  }

  /// 场景 7：给「按住不放」收尾（松开左键和 Ctrl+Alt）。
  public static void Release(){
    Send(new[]{Mouse(0,0,LEFTUP)});
    System.Threading.Thread.Sleep(150);
    Send(new[]{Key(0x11,true), Key(0x12,true)});
  }

  /// 场景 9：按住 Ctrl+Alt 拖到某处，**不松左键**，改按**右键**取消。
  /// 【2026-10-08 用户要求】用户把「拖到一半按什么取消」从 Esc 改成了右键，
  ///   说「Esc 太反人类」。所以这里要能自动测右键取消。
  /// 用完必须接着跑「松开按住」，否则鼠标会一直卡在按下状态。
  public static void RightClickCancelDrag(int x1,int y1,int x2,int y2){
    Goto(x1,y1);
    Send(new[]{Key(0x11,false), Key(0x12,false)});
    System.Threading.Thread.Sleep(150);
    Send(new[]{Mouse(x1,y1,LEFTDOWN)});
    System.Threading.Thread.Sleep(100);
    for(int s=1;s<=5;s++){
      MoveTo(x1+(x2-x1)*s/5, y1+(y2-y1)*s/5);
      System.Threading.Thread.Sleep(40);
    }
    Send(new[]{Mouse(x2,y2,RIGHTDOWN)});   // 拖到一半按右键 → 应当取消
    System.Threading.Thread.Sleep(200);
    Send(new[]{Mouse(x2,y2,RIGHTUP)});
  }

  /// 场景 10：把鼠标停在卡片上，用来验证「鼠标移上去卡片就不消失」。
  public static void ParkCursor(int x,int y){
    MoveTo(x,y);
  }

  /// 场景 8：只按下 Ctrl+Alt，完全不碰鼠标（保持按住）。
  /// 用途：验证「一按下 Ctrl+Alt 就弹出遮罩并接管鼠标」——
  ///       按住期间，下面的窗口应当拖不动、选不了字（这才是截图工具的手感）。
  /// 用完必须接着跑「松开按住」，否则修饰键会一直卡在按着。
  public static void HoldModifiersOnly(){
    Send(new[]{Key(0x11,false), Key(0x12,false)});
    System.Threading.Thread.Sleep(500);
  }
}
'@

switch ($场景) {
    '正常框选'   { [InputDriver]::NormalDrag($x1, $y1, $x2, $y2, 10); "已执行：按住 Ctrl+Alt 从 ($x1,$y1) 拖到 ($x2,$y2) 并松开" }
    '中途松Esc'  { [InputDriver]::EscapeDrag($x1, $y1, $x2, $y2);    "已执行：拖到一半按 Esc 取消" }
    '中途松Ctrl' { [InputDriver]::ReleaseCtrlMidDrag($x1, $y1, $x2, $y2); "已执行：拖到一半松开 Ctrl" }
    '只点一下'   { [InputDriver]::ClickOnly($x1, $y1);               "已执行：按住 Ctrl+Alt 只点一下不拖" }
    '不按修饰键' { [InputDriver]::DragWithoutModifiers($x1, $y1, $x2, $y2, 10); "已执行：不按 Ctrl+Alt 直接拖动" }
    '按住不放'   { [InputDriver]::HoldAt($x1, $y1, $x2, $y2);           "已执行：按住不放，选框留在屏幕上（截图取证用，记得接着跑「松开按住」）" }
    '只按修饰键' { [InputDriver]::HoldModifiersOnly();                    "已执行：只按住 Ctrl+Alt 不碰鼠标（验证遮罩有没有接管鼠标）" }
    '松开按住'   { [InputDriver]::Release();                             "已执行：松开左键与 Ctrl+Alt" }
    '中途按右键' { [InputDriver]::RightClickCancelDrag($x1, $y1, $x2, $y2); "已执行：拖到一半按右键取消" }
    '停鼠标'     { [InputDriver]::ParkCursor($x1, $y1);                 "已执行：把鼠标停在 ($x1,$y1)" }
}

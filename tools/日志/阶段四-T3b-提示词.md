# 阶段四 · T3b 提示词（主窗口改造：使用说明 + 翻译设置区）

- 时间：2026-11-04
- 前置：`887f0b5` / tag `阶段四-硬闸5000` 已通过第十四轮审阅
- 本文件是新文件，未改动任何已有文件。
- **用户已拍板三件事（见 §1），不许再问，直接做。**

---

## §1 用户已拍板（三条，照做）

| 问题 | 用户选择 |
|---|---|
| 设置放哪 | **改造主窗口**（不新建设置窗口，不搞两个窗口） |
| 托盘的隐私披露文字 | **菜单保留原文，主窗口再写一遍** |
| 主窗口那两段空壳文案 | **换成真的使用说明** |

---

## §2 先看清现状（架构师实读，别凭印象改）

### §2.1 主窗口是个真空壳

`src/TranslatorApp/MainWindow.xaml` **全文 30 行**：

```xml
Title="翻译软件 v0.1 · 空壳"   Height="300" Width="480"
<StackPanel Margin="24" VerticalAlignment="Center" HorizontalAlignment="Center">
    <TextBlock Text="翻译软件 · 最小骨架（v0.1）" FontSize="20" FontWeight="Bold" ... />
    <TextBlock Text="这是一个能运行的空壳程序。&#10;右下角托盘有图标，右键菜单可以…"
               TextAlignment="Center" FontSize="13" LineHeight="24" ... />
</StackPanel>
```

`src/TranslatorApp/MainWindow.xaml.cs` **全文 26 行**：
```csharp
public MainWindow()
{
    InitializeComponent();
    Title = App.WindowTitle;
}
```
注释锁死：**标题统一取自 `App.WindowTitle`，不要在 XAML 里再手写第二份**（否则单实例查找会找错窗口）。

### §2.2 `AppSettings` 缺少目标语言的写入口（P0）

`src/Core/Capture/AppSettings.cs` 现有 **只有两个** setter：

```
:382  public static void SetTranslateEnabled(bool on)
:393  public static void SetTranslateNoticeShown()
```

**没有 `SetTranslateTargetLang`。** 下拉框选完了没地方写 → 必须新增，参照 `:382` 的写法（`lock (Gate)` 改属性 → 调 `Save(WindowsOnly)`，且值没变就早退不存盘）。

**不需要** `SetTranslateEndpoint`：`translateEndpoint` 不进 UI（第八轮已裁），用户的界面里不出现。

### §2.3 托盘开关是局部变量，同步不了（P0）

`src/TranslatorApp/TrayIcon.cs:79`：
```csharp
var translateItem = new ToolStripMenuItem("翻译：把文字发给微软（勾选 = 开）")
{
    Checked = AppSettings.TranslateEnabled,
};
```
`translateItem` 是 **`EnsureMenu()` 里的局部变量**，外部拿不到。
主窗口改了开关 → 托盘菜单的勾选态**不会变**，下次打开菜单还是旧的。

**至少两种改法，你选一种并说明理由**：
- a) 提升成 `private static ToolStripMenuItem? _translateItem;`，加 `internal static void RefreshTranslateChecked()` 供主窗口调；
- b) 菜单在 `Opening` 事件里重新读 `AppSettings.TranslateEnabled` 刷新勾选态（更懒，推荐考虑）。

### §2.4 托盘那句隐私披露不许动

`src/TranslatorApp/TrayIcon.cs:73-78` 注释锁死：

> 菜单文字本身就是隐私披露——「把文字发给微软」写在脸上，用户点之前就知道会发生什么。
> ⚠️ 这段文字**不许**改成像「开启翻译」这种不带披露的说法。
> 一旦要改，就必须同时换成弹窗确认。

**`TrayIcon.cs:79` 那行字符串一个字都不许改。** 用户已拍板「菜单保留原文」。

---

## §3 要做的事（三件，缺一不成立）

### §3.1 主窗口换成真的使用说明

删掉「最小骨架」「空壳程序」两段。换成：

1. **标题**：保留 `Title = App.WindowTitle`（`MainWindow.xaml.cs` 里那行不动）。
2. **怎么用**（大白话，写给不懂技术的人）：
   - 框选屏幕上的文字 → 松手 → 右下角出卡片，显示认出的原文
   - 卡片 3 秒后自动关；内容多可以滚
   - 程序在右下角托盘里，点 × 只是缩起来，不是关掉
3. **翻译说明（隐私披露，主窗口再写一遍）**：
   - 翻译默认是关的
   - 打开后，**你框到的文字会发到微软的翻译服务**（只发文字，不发截图、不发窗口标题、不发程序名）
   - 关掉 = 一个字都不外发
4. 窗口尺寸 `Height="300" Width="480"` 装不下这些 → 自行调整，但**必须保证 1366×768 的小屏也能完整显示**。

### §3.2 翻译设置区

| 控件 | 要求 |
|---|---|
| 开关 | `CheckBox`，绑 `AppSettings.TranslateEnabled`；改动时调 `SetTranslateEnabled`，**并同步托盘菜单勾选态**（§2.3） |
| 目标语言 | `ComboBox`，8 项固定 + 「其他…」手填；改动时调**新增的** `SetTranslateTargetLang` |

**8 项固定值（顺序照抄，别自己编）**：
```
zh-Hans  简体中文（默认）
zh-Hant  繁体中文
en       英语
ja       日语
ko       韩语
ru       俄语
fr       法语
de       德语
```

**「其他…」手填的行为**：
- 选中后弹一个输入框（或就地变成可编辑）。
- **不做本地语言码校验表**（`AppSettings.cs:177-179` 已写明理由：两边不一致，填错让服务端报 400）。
- 空串不许存（`AppSettings.cs` Load 里 `:303` 已经明确「空串当没配」）。

**当前值加载**：`AppSettings.TranslateTargetLang` 默认 `"zh-Hans"`。若存的是 8 项之外的值（用户手填过），下拉框要能显示出来，不能静默跳回简体。

### §3.3 两个入口双向同步

- 托盘菜单勾 → 主窗口开关跟着变
- 主窗口开关 → 托盘菜单勾选态跟着变

**主窗口可能是隐藏状态**（点 × 缩到托盘），所以同步时必须判空：窗口没创建/没显示时不能 NPE。

---

## §4 不许碰的东西（越界即打回）

1. **翻译逻辑一行不许动**：`src/Core/Translate/` 全部五个文件、`HardTimeoutMs`、缓存、`BingFreeProvider` 协议。
2. **`TrayIcon.cs:79` 的字符串**不许改。
3. **认字链路**（`src/Core/Capture/` 除 `AppSettings` 外）不许动。
4. **`UpdateWith` 的三参签名 / 8 秒倒计时 / 3 秒自动关**不许动。
5. **已有 settings.json 的用户不能丢配置**：`Save()` 现在写 12 键，新增键必须一起写，且 `Load()` 要能读旧文件（缺键走默认）。

---

## §5 验收（四条，必须逐条给数字或图）

### S1 编译与启动
- 0 错误 0 警告。
- 启动 → 主窗口显示正常 → 内容不溢出、不重叠。

### S2 双向同步（真机，必须截图）
1. 主窗口勾上翻译 → 打开托盘菜单 → **勾选态是勾上的**（截图）。
2. 托盘菜单取消勾选 → 主窗口开关 → **跟着变关**（截图，主窗口此时可能隐藏，要先显示出来）。
3. 重启程序 → 两处状态都还在。

### S3 目标语言（真机，必须截图）
1. 下拉选「日语」→ 重启 → 还是日语。
2. 选「其他…」手填 `xx-YY`（故意填错）→ 重启 → 下拉框显示 `xx-YY`，**没有被静默改回简体**。
3. `settings.json` 里 `translateTargetLang` 真的写进去了（贴一段 config 内容）。

### S4 不破坏已有功能
- 框选一次 → 卡片正常出原文 + 译文（真 Bing，**不许用假服务器**）。
- 关掉翻译开关 → 框选 → 只有原文、无外发（日志 `翻译=没走`）。

---

## §6 两条纪律（本轮新增，违者打回）

1. **纪律 A：工具结论与可验证数字冲突时，先算一遍数。**
   （上一轮你自己用 `952 + 442 = 1394 < 1920` 推翻了识图误报，这一轮继续这么做。）
2. **纪律 B：自动截图必须先打开看过再交。**
   汇报里必须写「**已看图确认**」，并说明看到了什么。写不出这句 = 没验。

---

## §7 汇报格式（≤15 行）

【结论】做完没 / 卡在哪
【数字】S1~S4 各自的数字或截图文件名
【改了什么】文件 + 行号 + 一句话
【风险】还剩什么没解决
【下一步】等审 / 或下一个任务
【详情】落盘到哪个文件

---

## §8 一句话给执行 AI

> 主窗口现在是 30 行空壳，`AppSettings` 缺 `SetTranslateTargetLang`，托盘开关是局部变量同步不了——
> 这三件事一起解决，翻译逻辑一行别碰，`TrayIcon.cs:79` 那行字别改。

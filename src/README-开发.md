# 开发说明（src 目录）

> **这份文件是干什么的？**
> 告诉你 `src\` 下每个文件夹是干什么的、属于「四层架构」里的哪一层。
> 分层的目的只有一个：**改界面的时候不用碰翻译代码，换翻译引擎的时候不用碰界面**。
>
> 分层定义的出处：`AI版\02-技术架构.md` 第二章。
> 每个子文件夹里还有一句人话的 `README.md`，忘了就点进去看。

---

## 一、四层架构对照表

| 层 | 这一层负责什么 | 放在哪些文件夹 | 什么时候填满 |
|---|---|---|---|
| **① 表现层**（界面） | 把东西画出来、把用户的点击变成「意图」交给上层<br>🚫 不许直接发网络请求、读写文件、调系统底层接口 | `src\TranslatorApp\`（入口 + 主窗口 + 托盘）<br>`src\UI\`（FloatingWindow / OverlayWindow / CornerPanel / HistoryWindow / SettingsWindow / SelectionOverlay / Shared） | 阶段 1（主窗口 + 托盘）→ 阶段 5、6、7、8 补各窗口 |
| **② 应用层**（编排） | 决定「先做什么、后做什么、失败了走哪条路」<br>🚫 不许出现界面代码、系统底层调用、SQL | `src\Core\` 根目录下的协调器 / 状态机<br>`src\Core\Modules\`（模块注册表） | 阶段 3 起陆续出现 |
| **③ 能力层**（干活） | 真正把一件事做出来：翻译、取词、把译文画上去<br>🚫 各实现之间不许互相认识 | `src\Core\Translation\`（各翻译引擎）<br>`src\Core\Capture\`（剪贴板 / OCR 取词）<br>`src\Core\Input\`（键鼠钩子、模拟输入） | 阶段 3、4、5 分别成形 |
| **④ 基础设施层**（底座） | 提供通用能力：配置、历史、日志、系统调用、网络<br>✅ `NativeMethods` 只许放在这里 | `src\Core\Config\` `src\Core\History\`<br>`src\Core\Native\` `src\Core\Logging\` `src\Core\Http\` | 阶段 1（日志）→ 阶段 6、8 补其余 |

**还有一个特殊目录：`src\Core\Abstractions\`**
四个核心接口（`ITranslationEngine` 翻译 / `ITextCapture` 取词 / `IDisplayPresenter` 显示 / `IFeatureModule` 功能模块）将来放这里。
它是 ② 应用层 和 ③ 能力层 之间的「插座规格」——上层只认插座，不认具体是哪家引擎。
阶段 1 它只有一个 README 占位，**不写任何方法签名**（阶段 3 / 4 / 5 才依次成形）。

---

## 二、四层是怎么连起来的

```
① 表现层  ──只通过「服务接口」──▶  ② 应用层
                                      │
                              只依赖 Abstractions 里的接口
                                      ▼
                                   ③ 能力层
                                      │
                              只能用基础设施提供的能力
                                      ▼
                                   ④ 基础设施层
```

举例（阶段 4 之后会真的跑起来）：
用户划词 → ① 把点击转成「取词意图」 → ② 决定先试剪贴板、200 毫秒没成再转 OCR →
③ 的 `ClipboardTextCapture` 去抓字、`DeepSeekEngine` 去翻译 → ④ 提供日志、配置、网络请求。

---

## 三、阶段 1（v0.1）已经填好的文件

| 文件 | 层 | 干什么 |
|---|---|---|
| `TranslatorApp\App.xaml` / `App.xaml.cs` | ① | 程序入口：单实例互斥、全局异常兜底、退出清理 |
| `TranslatorApp\MainWindow.xaml` / `.cs` | ① | 主窗口（空窗，标题带版本号） |
| `TranslatorApp\TrayIcon.cs` | ① | 托盘图标 + 右键菜单（显示主窗口 / 退出） |
| `Core\Native\NativeMethods.cs` | ④ | 全项目唯一的 P/Invoke 声明处 |
| `Core\Logging\AppLogger.cs` | ④ | 写日志到 `tools\日志\`，按天分文件 |

其余文件夹目前只有 `README.md` 占位，按上面的表在对应阶段填。

---

## 四、怎么编译出能双击的 exe

在项目根目录打开 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File "tools\脚本\构建.ps1"
```

产物在 `release\v0.1\TranslatorApp.exe`。
（必须加 `-ExecutionPolicy Bypass`，否则本机会拒绝运行 .ps1 脚本。）

## 五、怎么验证托盘图标真的挂上了

```powershell
powershell -ExecutionPolicy Bypass -File "tools\脚本\托盘检查.ps1" -Launch
```

会打印启动前后的图标数量，差值为 1 就是通过。

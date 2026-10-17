using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using TranslatorApp.Core.Capture;
using Tesseract;

// 离线压测：拿 E6-1 那张存图，切出成百上千个「用户可能框出来的选区」，
// 每个都跑一遍软件真正的两票（第一票 = UpscaleForForeign，第二票 = PreprocessAndBinarize(UpscaleForForeign)），
// 目的是找出「第一票咬尾巴、第二票能救回来」的触发条件，好造一份能稳定复现的复测材料。
// 只读图、只跑引擎，不碰界面、不碰鼠标。

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
var shotDir = Path.Combine(root, "release", "V0.3.3.0", "debug-screenshots");
var tessdata = Path.Combine(root, "release", "V0.3.3.0", "tessdata");

// 主程序是靠 NativeMethods 预先把 x64 里的原生库逐个 LoadLibrary 的，这里照样先预加载。
var x64 = Path.Combine(AppContext.BaseDirectory, "x64");
SetDllDirectory(x64);
var pending = Directory.GetFiles(x64, "*.dll").ToList();
var loaded = 0;
bool progress = true;
while (progress && pending.Count > 0)
{
    progress = false;
    for (int i = pending.Count - 1; i >= 0; i--)
    {
        try { NativeLibrary.Load(pending[i]); loaded++; pending.RemoveAt(i); progress = true; } catch { }
    }
}
Console.WriteLine($"预加载原生库：成功 {loaded} 个，装不上 {pending.Count} 个（{x64}）");

// ── 断言：直接反射调用真程序集里那条规矩本身，不复制逻辑 ──
int fail = 0;
{
    var asm = Assembly.LoadFrom(Path.Combine(root, "release", "V0.3.4.0", "TranslatorApp.dll"));
    var type = asm.GetType("TranslatorApp.Core.Capture.OcrTextCapture");
    var m = type!.GetMethod("SecondVoteRecoversTail", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("找不到 SecondVoteRecoversTail");
    bool Check(string? a, string? b) => (bool)m.Invoke(null, new object?[] { a, b })!;

    (string? a, string? b, bool want, string why)[] cases =
    {
        ("Optical character recognition converts images of text into editable te",
         "Optical character recognition converts images of text into editable text.", true,  "E6 真错例：第一票咬掉尾巴，第二票补上"),
        // ↓ A16 真实病例（2026-10-14 09:16:12）：第一票句中打磕巴多空一格又漏 xt，第二票全对。
        //   老规矩（严丝合缝的开头关系）在这里返回 false，放宽成顺序关系后才救得回来。
        ("Optical character recognition converts images of text into editab e te",
         "Optical character recognition converts images of text into editable text.", true,  "A16 真错例：第一票句中打磕巴，第二票全对"),
        // ↓ A5 真实病例（2026-10-14 12:41:00 框 494×35）：第一票把 editable 读成 edital le
        //   （少 b、多 l），**不是**第二票的子序列，光靠「顺序关系」救不回来。
        //   放宽出「只差零星几个字」这条路之后才救得回来。
        ("Optical character recognition converts images of text into edital le te",
         "Optical character recognition converts images of text into editable text.", true,  "A5 真错例：第一票词里错几个字，第二票全对"),
        // ↓ 短串上必须收窄：第一票只读出 3 个字母时，「差 4 个字符也算对得上」等于没门槛，
        //   那就是「只要第二票更长就换」（已实测会弄坏）。这条钉子锁住按长度收窄这件事。
        ("cat", "dogs", false, "短串：差太远不许换（固定 4 会放行，按长度收窄后挡住）"),
        ("abcd", "abxcd", true, "短串：只差 1 个字且在限额内 → 换"),
        // ↓ 下面两条是这次放宽**新增**的风险面，必须钉死：第二票自己读糊了时不能换。
        ("Small printed text on a paper-like background is the case that trips up",
         "Small printed text on a paper-like background is the case that tnps up", false, "第二票自己读糊了（trips→tnps）→ 第一票的字找不齐 → 不动"),
        ("Optical character recognition converts images of text into editable text.",
         "", false, "第二票交白卷 → 不动"),
        ("Optical character recognition converts images of text into editable text.",
         "Optical character recognition converts images of text into editable te", false, "反过来：短的不能当长的用"),
        ("The quick brown fox jumps over the lazy dog",
         "The quick brown fox jumps over the lazy dog 89", true, "结尾多出数字也会触发（已知挡不住，如实记录）"),
        ("Optical character recognition converts images of text into editable text.",
         "Optical character recognition converts images of text into editak le.", false, "中间不一样且少字 → 找不齐 → 不动"),
        ("abc def", "xyz def", false, "完全不同 → 不动"),
        // ↓ 这三条是 2026-10-14 下午新增的路子③（用户补测 10 次里错了 5 次那类）：
        //   字母数字一模一样、差别只在标点/空格怎么摆 → 采第二票。它**不看长短**。
        ("Optical character recognition converts images of text into editable text",
         "Optical character recognition converts images of text into editable text.", true, "补测真错例：第一票末尾句号丢了，第二票有 → 路子③换"),
        ("Optical character recognition converts images of text into editable text. |",
         "Optical character recognition converts images of text into editable text.", true, "补测真错例：第一票末尾句号变竖线 → 字母数字一样 → 换"),
        ("Optical character recognition converts images of text into editable t ext",
         "Optical character recognition converts images of text into editable text.", true, "补测真错例：第一票把 text 掰成 t ext → 字母数字一样 → 换"),
        ("abc", "abc", true, "字母数字一模一样 → 路子③换第二票（两份连标点都一样时换了也一样）"),
        // 路子③不看长短，唯一要防的是「第二票把对的字母改错、又刚好同长度」。
        // 钉一条：字母被改掉（长度可能还一样）时不许换。
        ("The quick brown fox", "The quuck brown fox", false, "第二票把字母改错（quick→quuck）→ 字母数字不同 → 不换"),
        // ↓ 下面是 2026-10-14 傍晚新增的路子④（A 系列压力材料 24 次里错了 2 次那类）：
        //   第一票**末尾凭空多吐一个字母**（`text.` → `te ext` / `text a`），第二票干净，
        //   但因为多吐的那个字母，第二票**比第一票短一个**。前面三句都要求第二票不短于第一票，
        //   一条都不放行。这条路方向跟前三句**相反**，必须单独钉住。
        ("Optical character recognition converts images of text into editable te ext",
         "Optical character recognition converts images of text into editable text.", true, "A 系列真错例①：第一票末尾多吐一个 e，第二票干净但短一个 → 路子④换"),
        ("Optical character recognition converts images of text into editable text a",
         "Optical character recognition converts images of text into editable text.", true, "A 系列真错例②：第一票末尾多吐一个 a → 路子④换"),
        // 路子④要的正是「第二票更短」，唯一要防的是「第一票读对了、第二票反而少认一个字母」。
        // 离线 240 个能对上标准答案的样本里，它出手 2 次全救回、反例 0；但方向相反就是有这个代价，
        // 按项目惯例如实钉住（跟上面「结尾多出数字也会触发」一样）。
        ("The quick brown fox jumps over the lazy dog",
         "The quick brown fox jumps over the lazy do", true, "路子④已知天花板：第一票对、第二票反而少一个字母 → 仍会换（如实记录）"),
        ("abc", "abcd", true, "第一票漏了尾字、第二票读全 → 采用第二票"),
        ("abc", "xabcx", true, "第二票多读了东西但第一票的字按序都在（顺序关系允许长了）"),
        (null, "abc", false, "第一票没结果 → 不动"),
        ("abc", "   ", false, "第二票空 → 不动"),
    };
    foreach (var (a, b, want, why) in cases)
    {
        bool got = Check(a, b);
        bool ok = got == want;
        if (!ok) fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} 期望{want} 实得{got}  {why}");
    }
}
Console.WriteLine(fail == 0 ? "断言全部通过" : $"断言失败 {fail} 条");
if (fail > 0) Environment.Exit(1);
Console.WriteLine();

// ── 断言：英文第二票的「分数护栏」（2026-10-17，BATTLEBOARD 错例）──
// 规矩本体只问内容对不对得上，不看第二票读得好不好；护栏补上分数这一维。
{
    var asmGuard = Assembly.LoadFrom(Path.Combine(root, "release", "V0.3.4.0", "TranslatorApp.dll"));
    var typeGuard = asmGuard.GetType("TranslatorApp.Core.Capture.OcrTextCapture");
    var mGuard = typeGuard!.GetMethod("SecondVoteTooWeak", BindingFlags.NonPublic | BindingFlags.Static)
                 ?? throw new InvalidOperationException("找不到 SecondVoteTooWeak");
    bool Weak(double a, double b) => (bool)mGuard.Invoke(null, new object?[] { a, b })!;

    (double first, double second, bool want, string why)[] gcases =
    {
        // ↓ 真实现场（2026-10-17 13:03:09.374）：BATTLEBOARD 第一票 0.90、一个字母都没错，
        //   二值化版 0.28 只是末尾多了个冒号，路子③「字母数字一样」就换了，
        //   之后四路择优又选中 0.70 的一版，低于 0.78 → 整块判乱码扔掉。
        (0.90, 0.28, true,  "BATTLEBOARD 真错例：第一票够格、第二票暴跌 → 拦下"),
        // ↓ 另一次真实换票（10:17:01）：0.82 → 0.66，也会被拦，但四路择优救回来了，结果不变。
        (0.82, 0.66, true,  "Issues is:issue 真现场：拦下后结果不变（四路救回）"),
        // 下面几条锁住这条线的两头：太松会误伤正常换票，太紧就拦不住暴跌。
        (0.78, 0.68, true,  "刚好卡在 0.10 上 → 拦（≥ 就拦）"),
        (0.78, 0.69, false, "只差 0.09 → 放行（正常换票不许误伤）"),
        (0.77, 0.20, false, "第一票自己都没过 0.78 → 护栏不插手（该换还得换）"),
        (0.99, 0.89, true,  "高分区间同样按 0.10 算，不因分数高就放宽"),
        (0.50, 0.10, false, "两边都低 → 不归护栏管，交给 0.78 门槛"),
        (0.90, 0.90, false, "一样高分 → 不拦"),
        (0.90, 0.95, false, "第二票更好 → 绝不拦"),
    };
    int gfail = 0;
    foreach (var (a, b, want, why) in gcases)
    {
        bool got = Weak(a, b);
        bool ok = got == want;
        if (!ok) gfail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} 期望{want} 实得{got}  {a:0.00}/{b:0.00}  {why}");
    }
    Console.WriteLine(gfail == 0 ? "第二票护栏断言全部通过" : $"第二票护栏断言失败 {gfail} 条");
    if (gfail > 0) Environment.Exit(1);
    fail += gfail;
}
Console.WriteLine();

// ── 断言：路二「点号回填」（2026-10-15）──
{
    var asm2 = Assembly.LoadFrom(Path.Combine(root, "release", "V0.3.4.0", "TranslatorApp.dll"));
    var type2 = asm2.GetType("TranslatorApp.Core.Capture.OcrTextCapture");
    var m2 = type2!.GetMethod("MergeSplitDottedTokensFrom", BindingFlags.NonPublic | BindingFlags.Static)
             ?? throw new InvalidOperationException("找不到 MergeSplitDottedTokensFrom");
    string Call(string text, string win) => (string)m2.Invoke(null, new object?[] { text, win })!;

    int fail2 = 0;
    (string text, string win, string want, string why)[] dot =
    {
        ("版本 2.0 已于 2024 年发布 config txt", "版本 2 ℃ 已于 2024 年发布 config.txt",
         "版本 2.0 已于 2024 年发布 config.txt", "X2 真错例：Windows 有 config.txt，最终结果被拆成 config txt → 合回来"),
        ("config txt", "config.txt", "config.txt", "最小例"),
        ("config txt", "config txt", "config txt", "Windows 自己也是拆开的 → 没票可回填，不动"),
        ("config txt", "2 ℃ 已于 2024 年发布", "config txt", "Windows 里没有这个带点词 → 一个字都不许造"),
        ("版本 2.0 已发布 config txt", "版本 2 ℃ 已发布 config txt", "版本 2.0 已发布 config txt", "Windows 没有完整写法 → 不动（不许凭 2.0 自己猜）"),
        ("a b", "a.b", "a.b", "短词也照合（凭 Windows 那张票说话，不看长短）"),
        ("foo bar config txt", "foo.bar config.txt", "foo.bar config.txt", "一处不够两处都合"),
        ("see config.txt", "see config.txt", "see config.txt", "本来就带点 → 原样不动"),
        ("", "config.txt", "", "空串 → 不动（不炸）"),
        ("config txt", "", "config txt", "Windows 空 → 不动（不炸）"),
        ("版本 2 · 0 config txt", "版本 2 ℃ config.txt", "版本 2 · 0 config.txt", "只动点号那一处，`2 · 0` 原样保留"),
    };
    foreach (var (text, win, want, why) in dot)
    {
        string got = Call(text, win);
        bool ok = got == want;
        if (!ok) fail2++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  期望[{want}] 实得[{got}]  {why}");
    }
    Console.WriteLine(fail2 == 0 ? "点号回填断言全部通过" : $"点号回填断言失败 {fail2} 条");
    if (fail2 > 0) Environment.Exit(1);
    Console.WriteLine();
}

// ── 断言：路二第二步「删掉垫底的低分单字」（2026-10-15，Z1）──
{
    var asm3 = Assembly.LoadFrom(Path.Combine(root, "release", "V0.3.4.0", "TranslatorApp.dll"));
    var type3 = asm3.GetType("TranslatorApp.Core.Capture.OcrTextCapture");
    var m3 = type3!.GetMethod("DropLowScoreSingleChar", BindingFlags.NonPublic | BindingFlags.Static)
             ?? throw new InvalidOperationException("找不到 DropLowScoreSingleChar");
    var resultType = asm3.GetType("TranslatorApp.Core.Capture.TesseractOcr+Result")
                     ?? throw new InvalidOperationException("找不到 TesseractOcr.Result");

    string Call(string text, double minConf, string? minWord, double secondMin)
    {
        object boxed = Activator.CreateInstance(resultType, new object?[]
        {
            text, 0.89, minConf, minWord, secondMin
        })!;
        return (string)m3.Invoke(null, new object?[] { text, boxed })!;
    }

    int fail3 = 0;
    (string text, double minConf, string? minWord, double secondMin, string want, string why)[] low =
    {
        ("今天天和气不错适合出门", 63.37, "和", 89.05,
         "今天天气不错适合出门", "Z1 真错例：垫底的「和」63.37 对第二低 89.05 → 删掉"),
        ("今天天和气不错适合出门", 66.84, "和", 92.48,
         "今天天气不错适合出门", "Z1 上界那次（66.84 / 92.48）也照删"),
        ("今天天和气不错适合出门", 70.00, "和", 89.05,
         "今天天和气不错适合出门", "正好 70 分不删（门槛是「低于 70」）"),
        ("今天天和气不错适合出门", 63.37, "和", 70.00,
         "今天天和气不错适合出门", "拉不开差距（63.37 ≥ 70×0.85）→ 不删"),
        ("今天天气abc不错适合出门", 63.37, "a", 89.05,
         "今天天气abc不错适合出门", "垫底的不是汉字 → 不删"),
        ("今天天气不错适合出门", 63.37, "天", 89.05,
         "今天天气不错适合出门", "那个字出现两次 → 不知道该删哪个 → 不删"),
        ("今天天气不错适合出门", 63.37, null, 100.0,
         "今天天气不错适合出门", "没取到垫底词 → 不删"),
        ("", 63.37, "和", 89.05, "", "空串 → 不动（不炸）"),
        // ↓ 2026-10-15 用户实测逮到的坑：这个函数**只管删字，不管收拾删完留下的空格**。
        //   chi_sim 原始文本其实是 `今天天 和 气不错适合出门`（字两边各有一个空格，
        //   归一化时才被吞掉）；直接拿原始文本进来删，就剩两个挨着的空格，
        //   而 FixSpuriousSpaces 只看紧挨着的那一个字符，`天␣␣气` 里第二个空格的
        //   左边是空格不是汉字 → 吞不掉，用户就看到 `今天天  气不错适合出门`。
        //   → **调用方必须传归一化之后的文本**（现场就是这么改的）。
        ("今天天 和 气不错适合出门", 63.37, "和", 89.05,
         "今天天  气不错适合出门", "已知陷阱：传原始文本会留下两个空格 → 调用方必须先 Normalize"),
        // ↓ 已知天花板：规矩本身认不出「这个字其实是对的」。真正的护栏是**作用范围**
        //   （只长在「中文兜底」这一个分支里），离线对拍 2245 块里它因此 0 次误伤。
        //   这条断言如实钉住「光看分数分不出对错」这件事。
        ("光学字符识别是指计算机识别图像中的文字", 57.95, "图", 90.85,
         "光学字符识别是指计算机识别像中的文字", "天花板：离线里 Z2 那种「图」垫底时会被删——靠作用范围挡住，规矩本身挡不住"),
    };
    foreach (var (text, minConf, minWord, secondMin, want, why) in low)
    {
        string got = Call(text, minConf, minWord, secondMin);
        bool ok = got == want;
        if (!ok) fail3++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  期望[{want}] 实得[{got}]  {why}");
    }
    Console.WriteLine(fail3 == 0 ? "低分单字断言全部通过" : $"低分单字断言失败 {fail3} 条");
    if (fail3 > 0) Environment.Exit(1);
    Console.WriteLine();
}

// ── 断言：弹框拖动的位置夹取（2026-10-15，用户要求「弹框可以拖动」）──
{
    var asm4 = Assembly.LoadFrom(Path.Combine(root, "release", "V0.3.4.0", "TranslatorApp.dll"));
    var type4 = asm4.GetType("TranslatorApp.UI.Shared.DebugToastWindow")
                ?? throw new InvalidOperationException("找不到 DebugToastWindow");
    var m4 = type4.GetMethod("ClampAxis", BindingFlags.NonPublic | BindingFlags.Static)
             ?? throw new InvalidOperationException("找不到 ClampAxis");
    double Clamp(double v, double lo, double hi) => (double)m4.Invoke(null, new object?[] { v, lo, hi })!;

    int fail4 = 0;
    (double v, double lo, double hi, double want, string why)[] clamp =
    {
        (100, 0, 1920, 100, "屏幕内 → 原样"),
        (-50, 0, 1920, 0, "拖出左边界 → 拉回 0"),
        (2000, 0, 1920, 1920, "拖出右边界 → 拉回上界"),
        (-500, -1920, -100, -500, "副屏在左边（负坐标是合法的）→ 不许当成越界拉回 0"),
        (100, 0, -300, 0, "显示器比卡片还窄（上界 < 下界）→ 待在 lo，不许算出屏幕外"),
    };
    foreach (var (v, lo, hi, want, why) in clamp)
    {
        double got = Clamp(v, lo, hi);
        bool ok = Math.Abs(got - want) < 0.001;
        if (!ok) fail4++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  期望[{want}] 实得[{got}]  {why}");
    }
    Console.WriteLine(fail4 == 0 ? "弹框拖动断言全部通过" : $"弹框拖动断言失败 {fail4} 条");
    if (fail4 > 0) Environment.Exit(1);
    Console.WriteLine();
}

// ── 断言：切边检测的四条边（2026-10-15，用户点名补右边和下边）──
// 为什么要有这一段：此前只查左边和上边，右边和下边一次都不查，
// 2026-10-13 那次 478×20 和 476×24 框底明显切到字却一句警告都没报。
{
    var asm5 = Assembly.LoadFrom(Path.Combine(root, "release", "V0.3.4.0", "TranslatorApp.dll"));
    var type5 = asm5.GetType("TranslatorApp.Core.Capture.ImagePreprocessor")
                ?? throw new InvalidOperationException("找不到 ImagePreprocessor");
    var m5 = type5.GetMethod("FindCutEdge", BindingFlags.Public | BindingFlags.Static)
             ?? throw new InvalidOperationException("找不到 FindCutEdge");

    // 造一张 4×4 白图，只在指定坐标扎黑点；返回 FindCutEdge 的判断。
    string Cut(params (int X, int Y)[] ink)
    {
        using var bmp = new Bitmap(4, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                bmp.SetPixel(x, y, Color.White);
        foreach (var (x, y) in ink) bmp.SetPixel(x, y, Color.Black);
        return (string)m5.Invoke(null, new object?[] { bmp })!;
    }

    int fail5 = 0;
    (string got, string want, string why)[] cutCases =
    {
        (Cut(), "", "整张白纸，四边都没笔画 → 不报"),
        (Cut((0, 1)), "左边", "只有左边有笔画 → 只报左边"),
        (Cut((3, 1)), "右边", "只有右边有笔画 → 只报右边（以前这一边不查，是缺口）"),
        (Cut((1, 0)), "上边", "只有上边有笔画 → 只报上边"),
        (Cut((1, 3)), "下边", "只有下边有笔画 → 只报下边（以前这一边不查，是缺口）"),
        (Cut((0, 1), (1, 0)), "左边和上边", "两条边 → 用「和」连接"),
        (Cut((0, 1), (1, 0), (3, 1)), "左边、右边和上边", "三条边 → 顿号列举、最后用「和」（顺序固定为 左→右→上→下）"),
        (Cut((0, 1), (3, 1), (1, 0), (1, 3)), "左边、右边、上边和下边", "四条边全中 → 四条都报出来"),
        (Cut((0, 0)), "左边和上边", "左上角一个点同时踩中左列和首行 → 如实报两条"),
    };
    foreach (var (got, want, why) in cutCases)
    {
        bool ok = got == want;
        if (!ok) fail5++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  期望[{want}] 实得[{got}]  {why}");
    }

    // 图太小（< 3 像素）时不查，直接返回空——别让一像素的图把小函数弄炸
    using var tiny = new Bitmap(2, 2);
    tiny.SetPixel(0, 0, Color.Black);
    string tinyGot = (string)m5.Invoke(null, new object?[] { tiny })!;
    bool tinyOk = tinyGot == "";
    if (!tinyOk) fail5++;
    Console.WriteLine($"  {(tinyOk ? "PASS" : "FAIL")}  期望[] 实得[{tinyGot}]  2×2 太小 → 不查、不炸");

    Console.WriteLine(fail5 == 0 ? "切边检测断言全部通过" : $"切边检测断言失败 {fail5} 条");
    if (fail5 > 0) Environment.Exit(1);
    Console.WriteLine();
}

// ── 断言：折行修复——空行也要过一遍 ShouldJoinLines（2026-10-16）──
// 为什么要有这一段：`TextNormalizer.Normalize` 原来碰到空行就无条件补换行，
// 连问都不问判官；而 Tesseract 认长段落时常塞空行，于是
// `large⏎neural networks` 这种词中被劈开的折行一路留到用户手里。
// 离线全量 774 处换行里，该合却没合 160 处（其中 148 处是同一句长英文）。
{
    var asm6 = Assembly.LoadFrom(Path.Combine(root, "release", "V0.3.4.0", "TranslatorApp.dll"));
    var type6 = asm6.GetType("TranslatorApp.Core.Capture.TextNormalizer")
                ?? throw new InvalidOperationException("找不到 TextNormalizer");
    var m6 = type6.GetMethod("Normalize", BindingFlags.Public | BindingFlags.Static)
             ?? throw new InvalidOperationException("找不到 Normalize");

    string Fold(string raw) => (string)m6.Invoke(null, new object?[] { raw })!;

    int fail6 = 0;
    (string got, string want, string why)[] foldCases =
    {
        // 这次要治的：空行不该挡住「该接就接」
        (Fold("Modern systems rely on large\n\nneural networks"), "Modern systems rely on large neural networks",
         "病根：空行隔开的两行也该问判官（以前直接保留换行）"),
        (Fold("It usually struggles the\n\nmost."), "It usually struggles the most.",
         "现场第二种：the / most."),
        (Fold("for any recognition\n\nengine."), "for any recognition engine.",
         "现场第三种：recognition / engine."),
        // 单换行本来就会合，不能因为这次改动反而不合了
        (Fold("Modern systems rely on large\nneural networks"), "Modern systems rely on large neural networks",
         "单换行照旧要合（回归保护）"),
        // 真段落：上一行以句末标点结尾，空行隔开也不许合
        (Fold("Server started on port 8080.\n\nDatabase connection failed."),
         "Server started on port 8080.\nDatabase connection failed.",
         "句末标点结尾 = 真段落，判官自己拦住，只把多余空行收掉"),
        (Fold("服务已启动，端口 8080。\n\n数据库连接失败。"), "服务已启动，端口 8080。\n数据库连接失败。",
         "中文句号结尾同样不许合"),
        // 汉字跟汉字之间：第二步（2026-10-16）已把那道门放开，这两条现在要合
        (Fold("票据录\n\n入等场景"), "票据录入等场景",
         "汉+汉折行第二步：两侧都是汉字时交给判官（离线 +70 对 0 错）"),
        (Fold("识别图像中的文字\n\n并转换为可编辑文本"), "识别图像中的文字并转换为可编辑文本",
         "中文词中被折开：第二步已治"),
        // ⚠️ 天花板：真段落但上一行句号被 OCR 丢了 —— 两侧都是汉字，这条规矩分不出来
        (Fold("数据库连接失败\n\n请检查配置文件。"), "数据库连接失败请检查配置文件。",
         "天花板（历史 2 次）：句号丢了 => 真段落被黏，规矩分不出，如实钉住"),
        // 一边汉字一边字母照旧不合并（本次没量过这一类，不顺手放宽）
        (Fold("识别文字abc\n\n并转换"), "识别文字abc\n并转换",
         "汉+拉丁：只有「两边都是汉字」量过，这一类照旧不合并"),
        // 边界：纯空行、首尾空行、只有空行
        (Fold("\n\nModern systems rely on large neural networks\n\n"),
         "Modern systems rely on large neural networks", "首尾空行照旧去掉"),
        (Fold("\n\n\n"), "", "只有空行 → 空串，不炸"),
        (Fold(""), "", "空串 → 空串，不炸"),
    };
    foreach (var (got, want, why) in foldCases)
    {
        bool ok = got == want;
        if (!ok) fail6++;
        string G = got.Replace("\n", "<NL>");
        string W = want.Replace("\n", "<NL>");
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  期望[{W}] 实得[{G}]  {why}");
    }

    Console.WriteLine(fail6 == 0 ? "折行断言全部通过" : $"折行断言失败 {fail6} 条");
    if (fail6 > 0) Environment.Exit(1);
    Console.WriteLine();
}

[DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool SetDllDirectory(string path);

var shot = Directory.GetFiles(shotDir, "*.png").OrderBy(p => p).First();
using var src = new Bitmap(shot);
Console.WriteLine($"基准存图：{Path.GetFileName(shot)}  {src.Width}×{src.Height}");
const string Target = "Opticalcharacterrecognitionconvertsimagesoftextintoeditabletext";
Console.WriteLine("（基准答案是 " + Target.Length + " 个字母数字）");
Console.WriteLine();

using var engine = new TesseractEngine(tessdata, "eng");

string Alnum(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());

(string text, float conf) Run(Bitmap img)
{
    using var ms = new MemoryStream();
    img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
    using var pix = Pix.LoadFromMemory(ms.ToArray());
    using var page = engine.Process(pix);
    return (page.GetText() ?? "", page.GetMeanConfidence());
}

// 基准：第一票（软件现在给英文用的那张图）
(int len, string text) First(Bitmap raw)
{
    using var up = ImagePreprocessor.UpscaleForForeign(new Bitmap(raw));
    var (t, _) = Run(up);
    return (Alnum(t).Length, t.Replace("\n", " ").Trim());
}
// 第二票：甲方案那张
(int len, string text) Second(Bitmap raw)
{
    using var up = ImagePreprocessor.UpscaleForForeign(new Bitmap(raw));
    using var bi = ImagePreprocessor.PreprocessAndBinarize(up);
    var (t, _) = Run(bi);
    return (Alnum(t).Length, t.Replace("\n", " ").Trim());
}

Bitmap Crop(int x, int y, int w, int h) => src.Clone(new Rectangle(x, y, w, h), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
Bitmap PadRight(int w, int extra)
{
    var bmp = new Bitmap(w + extra, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.White);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
    }
    return bmp;
}

int total = 0, caught = 0, secondOk = 0, both = 0;
var rows = new List<string>();
// 每一票都记下来，后面拿不同的「换票规矩」去比，看谁更靠谱。
var samples = new List<(string label, string t1, string t2)>();

void Trial(string label, Bitmap img)
{
    total++;
    var (l1, t1) = First(img);
    bool firstBad = l1 < Target.Length;
    var (l2, t2) = Second(img);
    bool secondGood = l2 >= Target.Length;
    if (firstBad) caught++;
    if (secondGood) secondOk++;
    if (firstBad && secondGood) both++;
    samples.Add((label, t1, t2));
    rows.Add($"{label,-34} 第一票{l1,3}{(firstBad ? "❌" : "✅")}  第二票{l2,3}{(secondGood ? "✅" : "❌")}   [{Trunc(t1, 46)}] | [{Trunc(t2, 46)}]");
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

Console.WriteLine("【一】右边裁掉几像素（框右边没框到 / 框偏窄）");
for (int cut = 0; cut <= 8; cut++)
{
    var w = src.Width - cut;
    if (w < 20) break;
    using var img = Crop(0, 0, w, src.Height);
    Trial($"右边裁 {cut}px（{w}×{src.Height}）", img);
}

Console.WriteLine("【二】右边多留几像素白边（框右边留空）");
foreach (int extra in new[] { 2, 4, 6, 8, 12, 16, 24 })
{
    using var img = PadRight(src.Width, extra);
    Trial($"右边留白 {extra}px（{img.Width}×{img.Height}）", img);
}

Console.WriteLine("【三】上下裁切（框偏矮 / 偏高）");
foreach (int top in new[] { 0, 1, 2, 3 })
{
    for (int bot = top; bot <= 3; bot++)
    {
        int h = src.Height - top - bot;
        if (h < 10) continue;
        using var img = Crop(0, top, src.Width, h);
        Trial($"上裁 {top} 下裁 {bot}（{src.Width}×{h}）", img);
    }
}

Console.WriteLine("【四】左右同时偏（框没对齐行首）");
foreach (int left in new[] { 0, 1, 2, 3 })
{
    using var img = Crop(left, 0, src.Width - left, src.Height);
    Trial($"左边裁 {left}px（{img.Width}×{img.Height}）", img);
}

Console.WriteLine("【五】整张缩放（相当于换字号 / 换缩放比）");
foreach (double s in new[] { 0.80, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10, 1.25 })
{
    int w = (int)Math.Round(src.Width * s), h = (int)Math.Round(src.Height * s);
    if (h < 10) continue;
    var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
    }
    using (bmp) Trial($"缩放 {s:0.00}（{w}×{h}）", bmp);
}

Console.WriteLine();
Console.WriteLine("================ 逐条明细 ================");
foreach (var r in rows) Console.WriteLine(r);
Console.WriteLine();
Console.WriteLine($"一共试了 {total} 个选区：第一票咬尾巴 {caught} 个（{caught * 100.0 / total:0.0}%），第二票能救回来 {secondOk} 个（{secondOk * 100.0 / total:0.0}%）");
Console.WriteLine($"触发条件同时成立的（第一票错 + 第二票对）：{both} 个");

// ── 规矩体检：拿一堆真正不同的英文句子，合成为图片，跑两票，看各条规矩的本事 ──
Console.WriteLine();
Console.WriteLine("================ 规矩体检（合成多句） ================");
string AlnumOnly(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());
string NoSpace(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
bool IsSubseq(string small, string big)
{
    int i = 0;
    foreach (char c in big) { if (i < small.Length && small[i] == c) i++; }
    return i == small.Length;
}
// R4＝拟议的新规矩：两份的字母数字一模一样就算平手，平手取第二票（更长那份）。
var rules = new (string name, Func<string, string, bool> decide)[]
{
    ("R0 现行：第二票更长且第一票是它的前缀（严格）",
        (f, s) => AlnumOnly(s).Length > AlnumOnly(f).Length && AlnumOnly(s).StartsWith(AlnumOnly(f), StringComparison.Ordinal)),
    ("R1 去掉空格后比前缀",
        (f, s) => AlnumOnly(s).Length > AlnumOnly(f).Length && NoSpace(s).StartsWith(NoSpace(f), StringComparison.Ordinal)),
    ("R2 第二票更长，且第一票是第二票的子序列",
        (f, s) => AlnumOnly(s).Length > AlnumOnly(f).Length && IsSubseq(AlnumOnly(f), AlnumOnly(s))),
    ("R3 只要第二票更长就换（无脑）",
        (f, s) => AlnumOnly(s).Length > AlnumOnly(f).Length),
    ("R4 拟议：字母数字一模一样就取第二票",
        (f, s) => AlnumOnly(f) == AlnumOnly(s)),
    // R5＝只治「第一票自己看着就不对」：第一票是第二票的子序列，且第一票里的短碎词比第二票多。
    // 专治 E6 那种「第一票句中插空格 / 咬尾巴」，第二票读垃圾时第一票是干净的、不会命中。
    ("R5 子序列 + 第一票短碎词更多",
        (f, s) => AlnumOnly(s).Length > AlnumOnly(f).Length
                  && IsSubseq(AlnumOnly(f), AlnumOnly(s))
                  && ShortWordCount(f) > ShortWordCount(s)),
    // ↓ 下面是 A16 真实病例逼出来的候选：第一票多吐了一个字（editab e te），第二票反而更短但还是全对。
    ("R6 子序列（不要「第二票更长」这条）",
        (f, s) => AlnumOnly(f) != AlnumOnly(s) && IsSubseq(AlnumOnly(f), AlnumOnly(s))),
    ("R7 子序列 + 第一票短碎词更多（不要长度）",
        (f, s) => AlnumOnly(f) != AlnumOnly(s)
                  && IsSubseq(AlnumOnly(f), AlnumOnly(s))
                  && ShortWordCount(f) > ShortWordCount(s)),
    ("R8 子序列 + 第一票短碎词更多 + 第二票不短太多（-3 以内）",
        (f, s) => AlnumOnly(f) != AlnumOnly(s)
                  && IsSubseq(AlnumOnly(f), AlnumOnly(s))
                  && ShortWordCount(f) > ShortWordCount(s)
                  && AlnumOnly(s).Length >= AlnumOnly(f).Length - 3),
    // ↓ 换一把更讲究的尺子：不再问「是不是子序列」，直接量「两份差几个字符」。
    // 咬尾巴、插空格都只差零星几个字；认成垃圾则差几十个字，一眼分开。
    ("R9 第二票更长 + 两份只差 4 个字符以内（编辑距离）",
        (f, s) => AlnumOnly(s).Length > AlnumOnly(f).Length
                  && EditDistance(AlnumOnly(f), AlnumOnly(s)) <= 4),
    ("R10 第二票更长 + 只差 4 个字符 + 第一票短碎词更多",
        (f, s) => AlnumOnly(s).Length > AlnumOnly(f).Length
                  && EditDistance(AlnumOnly(f), AlnumOnly(s)) <= 4
                  && ShortWordCount(f) > ShortWordCount(s)),
    // R14 是 R9 的收紧版：固定「≤4」在短串上等于没门槛（3 个字母的词宽容到差 4 个 = 随便换）。
    // 长串上跟 R9 完全一样，短串上按比例收紧。真实日志上两者成绩完全相同。
    ("R14 第二票更长 + 编辑距离 ≤ min(4, max(1, 第一票长度/4))（拟上线）",
        (f, s) => AlnumOnly(s).Length > AlnumOnly(f).Length
                  && EditDistance(AlnumOnly(f), AlnumOnly(s))
                     <= Math.Min(4, Math.Max(1, AlnumOnly(f).Length / 4))),
    // ★ R16（候选，治「字母全对、只有末尾符号读错」）：两份字母数字完全相同、只有标点/空格不同就取第二票。
    // 现实例（2026-10-14 14:14:56 / 14:15:49）：第一票 `…editable text. |`、第二票 `…editable text.`。
    // 这条**故意不要求「第二票更长」**——正是「一样长」的时候才需要它，加上「更长」等于自我否决。
    ("R16 字母数字一模一样就取第二票（候选）",
        (f, s) => AlnumOnly(f).Length > 0 && AlnumOnly(f) == AlnumOnly(s) && f != s),
};
int EditDistance(string a, string b)
{
    var prev = new int[b.Length + 1];
    var cur = new int[b.Length + 1];
    for (int j = 0; j <= b.Length; j++) prev[j] = j;
    for (int i = 1; i <= a.Length; i++)
    {
        cur[0] = i;
        for (int j = 1; j <= b.Length; j++)
            cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1),
                              prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        (prev, cur) = (cur, prev);
    }
    return prev[b.Length];
}
int ShortWordCount(string text)
{
    int n = 0;
    foreach (var raw in text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
    {
        var w = raw.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '-', '/', '\\', '|', '~', '`', '“', '”');
        if (w.Length is >= 1 and <= 3 && w.All(char.IsLetter)) n++;
    }
    return n;
}
int r3Fix = 0, r3Break = 0, r3Sane = 0, r3Junk = 0, r3SwapOk = 0, r3SwapBad = 0;

string[] sentences =
{
    "Optical character recognition converts images of text into editable text.",
    "The quick brown fox jumps over the lazy dog.",
    "Learning a language takes practice every single day.",
    "Small printed text is hard to read on a bright screen.",
    "Please restart the application and try again later.",
    "Version 2.0 was released in 2024 for all customers.",
    "Contact support at dev@corp.io for more information.",
    "HTTP/2.0 requires RAM 16GB, see page 42 for details.",
    "Machine translation is not always accurate.",
    "Error 404 (Not Found): the file was not found on disk.",
    "Windows 11 runs smoothly on modern hardware.",
    "The service started successfully on port 8080.",
    "Download speed reached 120 Mbps at midnight today.",
    "Remember to update the client software before use.",
    "This paragraph has several lines of ordinary English text that should be easy for the engine to read correctly without any special tricks at all.",
};
(string font, float size, Color bg, Color fg)[] looks =
{
    ("Segoe UI", 15f, Color.White, Color.FromArgb(17, 17, 17)),
    ("Arial", 15f, Color.White, Color.Black),
    ("Consolas", 14f, Color.White, Color.FromArgb(20, 20, 20)),
    ("Segoe UI", 18f, Color.White, Color.Black),
    ("Arial", 13f, Color.White, Color.FromArgb(30, 30, 30)),
    ("Segoe UI", 15f, Color.FromArgb(232, 240, 254), Color.FromArgb(20, 33, 61)),
    ("Arial", 16f, Color.FromArgb(240, 240, 240), Color.FromArgb(40, 40, 40)),
    ("Times New Roman", 15f, Color.White, Color.Black),
    // ↓ 难看的底子：专门用来压「第二票会不会读出更多垃圾」这种风险
    ("Segoe UI", 15f, Color.FromArgb(185, 185, 185), Color.FromArgb(154, 154, 154)),
    ("Arial", 16f, Color.FromArgb(255, 140, 0), Color.FromArgb(0, 230, 118)),
    ("Segoe UI", 14f, Color.FromArgb(200, 200, 210), Color.FromArgb(120, 120, 200)),
    ("Consolas", 15f, Color.FromArgb(240, 240, 200), Color.FromArgb(160, 160, 90)),
    ("Arial", 20f, Color.FromArgb(210, 225, 210), Color.FromArgb(90, 110, 90)),
    ("Segoe UI", 15f, Color.FromArgb(255, 250, 235), Color.FromArgb(190, 170, 120)),
    // ↓ 深底浅字：二值化在这种图上会不会掉笔画？必须压一压
    ("Segoe UI", 15f, Color.FromArgb(20, 20, 25), Color.White),
    ("Arial", 15f, Color.Black, Color.FromArgb(240, 240, 240)),
    ("Consolas", 14f, Color.FromArgb(30, 30, 60), Color.FromArgb(200, 220, 255)),
};

Bitmap Render(string text, string font, float size, Color bg, Color fg)
{
    using var probe = new Bitmap(8, 8);
    using (var pg = Graphics.FromImage(probe))
    {
        pg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var sz = pg.MeasureString(text, new Font(font, size));
        int w = (int)Math.Ceiling(sz.Width) + 8, h = (int)Math.Ceiling(sz.Height) + 4;
        var bmp = new Bitmap(Math.Max(w, 20), Math.Max(h, 12), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(bg);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString(text, new Font(font, size), new SolidBrush(fg), 2, 2);
        return bmp;
    }
}

var sentSamples = new List<(string label, string t1, string t2, string truth)>();
for (int si = 0; si < sentences.Length; si++)
{
    for (int li = 0; li < looks.Length; li++)
    {
        var (font, size, bg, fg) = looks[li];
        using var bmp = Render(sentences[si], font, size, bg, fg);
        // 模拟用户框：上下各留 1 像素白边
        int pad = 1;
        using var framed = new Bitmap(bmp.Width + pad * 2, bmp.Height + pad * 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(framed))
        {
            g.Clear(Color.White);
            g.DrawImage(bmp, pad, pad);
        }
        var (l1, t1) = First(framed);
        var (l2, t2) = Second(framed);
        sentSamples.Add(($"句{si + 1} {font} {size}px", t1, t2, AlnumOnly(sentences[si])));
    }
}
Console.WriteLine($"合成样本 {sentSamples.Count} 个（{sentences.Length} 句 × {looks.Length} 种外观）");

// ★ 关键体检：第二票有没有「比第一票更差」的时候？
// 换票规矩再漂亮，只要第二票会在某些底子上交白卷/读垃圾，放宽它就是往枪口上撞。
int bothOk = 0, onlyFirst = 0, onlySecond = 0, neither = 0;
var dangerous = new List<(string label, string t1, string t2)>();
foreach (var (label, t1, t2, truth) in sentSamples)
{
    string a1 = AlnumOnly(t1), a2 = AlnumOnly(t2);
    bool ok1 = a1 == truth, ok2 = a2 == truth;
    if (ok1 && ok2) bothOk++;
    else if (ok1) { onlyFirst++; dangerous.Add((label, t1, t2)); }
    else if (ok2) onlySecond++;
    else neither++;
}
Console.WriteLine($"第一票对+第二票也对 {bothOk}；【第一票对、第二票错】{onlyFirst}；第一票错+第二票对 {onlySecond}；两边都错 {neither}");
if (dangerous.Count > 0)
{
    Console.WriteLine("  ⚠️ 以下是「第二票更差」的底子——换票规矩一旦在这里出手就是弄坏：");
    foreach (var (label, t1, t2) in dangerous.Take(12))
        Console.WriteLine($"     {label,-26} 第一票[{Trunc(t1, 40)}] 第二票[{Trunc(t2, 40)}]");
    if (dangerous.Count > 12) Console.WriteLine($"     （还有 {dangerous.Count - 12} 个）");
}

// 注意：比「对不对」更重要的是「别把对的换坏」——第一票本来就对的时候换票有没有风险。
foreach (var (label, t1, t2, truth) in sentSamples)
{
    string a1 = AlnumOnly(t1), a2 = AlnumOnly(t2);
    if (a2 == truth && a1 != truth) r3Fix++;
    if (a1 == truth && a2 != truth) r3Break++;
    if (a1 == truth) r3Sane++;
    if (a2.Length > a1.Length && a2 != truth) r3Junk++;
    if (a2.Length > a1.Length && a2 == truth) r3SwapOk++;
    if (a2.Length > a1.Length && a1 == truth) r3SwapBad++;
}
Console.WriteLine($"第一票本来就对 {r3Sane} 个；第二票更长但读的是垃圾 {r3Junk} 个；第二票更长且读对 {r3SwapOk} 个；第二票更长却把对的换坏 {r3SwapBad} 个");

foreach (var (name, decide) in rules)
{
    int fire = 0, fix = 0, brk = 0;
    foreach (var (label, t1, t2, truth) in sentSamples)
    {
        if (!decide(t1, t2)) continue;
        fire++;
        string a1 = AlnumOnly(t1), a2 = AlnumOnly(t2);
        if (a2 == truth && a1 != truth) fix++;
        else if (a1 == truth && a2 != truth) brk++;
    }
    Console.WriteLine($"{name}");
    Console.WriteLine($"    出手 {fire}/{sentSamples.Count}：救回 {fix}，把对的换坏 {brk}");
}

// ── 真实病例台：直接喂日志里两份原文，看每条规矩认不认 ──
Console.WriteLine();
Console.WriteLine("================ 真实病例（日志原文，不经合成） ================");
// (第一票, 第二票, 正确答案, 说明)
var realCases = new (string f, string s, string truth, string note)[]
{
    ("Optical character recognition converts images of text into editab e te",
     "Optical character recognition converts images of text into editable text.",
     "Opticalcharacterrecognitionconvertsimagesoftextintoeditabletext",
     "A16 实测失败（09:16:12）第一票句中插空格，第二票全对"),
    ("Optical character recognition converts images of text into editable €",
     "Optical character recognition converts images of text into editable text.",
     "Opticalcharacterrecognitionconvertsimagesoftextintoeditabletext",
     "E6 历史失败：末尾咬成 €"),
    ("Optical character recognition converts images of text into editable te",
     "Optical character recognition converts images of text into editable text.",
     "Opticalcharacterrecognitionconvertsimagesoftextintoeditabletext",
     "E6 历史失败：末尾咬成 te（现行规矩能救）"),
    ("Optical character recognition converts images of text into editable text",
     "Optical character recognition converts images of text into editable text.",
     "Opticalcharacterrecognitionconvertsimagesoftextintoeditabletext",
     "两份都对，只是第二票多个句点：不该动手"),
    ("The quick brown fox jumps over the lazy dog.",
     "The quuck brown fox jumps over the lazy dog.",
     "Thequickbrownfoxjumpsoverthelazydog",
     "第二票更差（quuck）：第一票全对，任何规矩都不许换"),
    ("Version 2.0 was released in 2024 for all customers.",
     "Version 2.9 was released in 2024 for all customers.",
     "Version2.0wasreleasedin2024forallcustomers",
     "第二票更差（2.9）：第一票全对，不许换"),
    ("票据录入",
     "票据录人",
     "票据录入",
     "纯中文：这道门根本不该被走到"),
};
int cHit = 0, cBad = 0;
foreach (var ru in rules)
{
    var fired = new List<string>();
    foreach (var (f, s, truth, note) in realCases)
    {
        if (!ru.decide(f, s)) continue;
        string a1 = AlnumOnly(f), a2 = AlnumOnly(s);
        bool ok = a2 == truth, wasOk = a1 == truth;
        fired.Add($"{(ok && !wasOk ? "✅救回" : wasOk && !ok ? "❌弄坏" : "⚪无变化")}({note.Split('：')[0]})");
        if (ok && !wasOk) cHit++;
        if (wasOk && !ok) cBad++;
    }
    Console.WriteLine($"{ru.name}");
    Console.WriteLine($"    {fired.Count} 例出手：{(fired.Count == 0 ? "一次没动" : string.Join(" ｜ ", fired))}");
}
Console.WriteLine();
Console.WriteLine($"【真实病例总计】五条规矩加起来：救回 {cHit} 次，弄坏 {cBad} 次");
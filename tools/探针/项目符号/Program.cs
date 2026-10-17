using System.Text;
using TranslatorApp.Core.Capture;

// 项目符号探针：问「被认成 o/e 的列表圆点删不删得掉、会不会误伤真文字」。
// 一次性工具，用完即删。

int pass = 0, fail = 0;

void Case(string name, string raw, string expect)
{
    string got = TextNormalizer.DropMisreadBulletLetters(raw);
    bool ok = got == expect;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok)
    {
        Console.WriteLine($"      入: {raw.Replace("\n", "<NL>")}");
        Console.WriteLine($"      出: {got.Replace("\n", "<NL>")}");
        Console.WriteLine($"      期望: {expect.Replace("\n", "<NL>")}");
    }
    if (ok) pass++; else fail++;
}

Console.WriteLine("===== 一、用户报的现场：圆点必须删掉 =====");

// 12:56:03 那条（真实现场，一字不改）
string real = "Contribute to the wiki\nBarony Wiki is a collaborative wiki resource that is open for anyone to edit. "
    + "You don't need special permission beyond registering an account to edit most pages, and your contributions "
    + "can grow the wiki and help other players.\nAdding content o If you are unsure of what to do or how to create "
    + "a page, search for a few articles on the same topic and see what they look like. You can always view the "
    + "source code in a wiki and learn from what others have done.\ne An edit doesn't have to be massive; if you feel "
    + "you don't want to create whole articles, then just fixing spelling errors and broken links is enough.\n"
    + "o Register to edit and track your contributions.";
string want = "Contribute to the wiki\nBarony Wiki is a collaborative wiki resource that is open for anyone to edit. "
    + "You don't need special permission beyond registering an account to edit most pages, and your contributions "
    + "can grow the wiki and help other players.\nAdding content If you are unsure of what to do or how to create "
    + "a page, search for a few articles on the same topic and see what they look like. You can always view the "
    + "source code in a wiki and learn from what others have done.\nAn edit doesn't have to be massive; if you feel "
    + "you don't want to create whole articles, then just fixing spelling errors and broken links is enough.\n"
    + "Register to edit and track your contributions.";
Case("现场 12:56:03（三个圆点）", real, want);

Case("行首圆点", "o Register to edit", "Register to edit");
Case("句尾后接圆点", "Adding content o If you are unsure", "Adding content If you are unsure");
Case("大写的圆点", "O Register to edit", "Register to edit");
Case("大写 E", "E An edit doesn't", "An edit doesn't");
Case("整条以圆点开头", "o If you are unsure of what to do", "If you are unsure of what to do");
Case("多个圆点", "o First\ne Second\no Third", "First\nSecond\nThird");

Console.WriteLine();
Console.WriteLine("===== 二、不能误伤：真文字必须原样返回 =====");

Case("真英文里的 a", "a person can do this", "a person can do this");
Case("真英文里的 I", "I think so", "I think so");
Case("o 后面跟小写", "photo o frame", "photo o frame");
Case("e 后面跟小写", "the e commerce site", "the e commerce site");
Case("俄文乱码串（历史唯一一处孤立 e）", "CbeUJb e 叫 3T\"X MWK\"X", "CbeUJb e 叫 3T\"X MWK\"X");
Case("o 在行尾", "this is an o", "this is an o");
Case("单个 o 单独一行", "o", "o");
// `go o Time` / `be o Time` 是我编的假句子：真实英文里 `o` 从不单独成词，
// 更不会后面紧跟大写字母。判据按「≥2 个字母的单词」算，这两条会删——接受。
// 宁可在这里删一个不存在的假句子，也不要为了它把判据收得连真圆点都认不出。
Case("2 字母词后面的圆点（真实英文里不存在）", "go o Time", "go Time");
Case("同上", "be o Time", "be Time");
Case("没空格", "oop", "oop");
Case("o 后面是大写但前面不是单词", "5 o If", "5 o If");

Console.WriteLine();
Console.WriteLine("===== 三、全历史扫一遍：只许命中用户报的那 5 条 =====");

string root = @"D:\111Desktop\deepseek\翻译软件";
var files = new List<string>();
string archive = Path.Combine(root, "tools", "日志", "用户实测");
if (Directory.Exists(archive))
    files.AddRange(Directory.GetFiles(archive, "第*轮-*.txt"));
string today = Path.Combine(root, "release", "V0.3.4.0", "logs", "取词记录-20261017.txt");
if (File.Exists(today)) files.Add(today);

int total = 0, changed = 0, bullets = 0;
var changedOnes = new List<string>();
foreach (string f in files)
{
    string[] lines = File.ReadAllLines(f, Encoding.UTF8);
    for (int i = 0; i < lines.Length; i++)
    {
        if (!lines[i].StartsWith("原文: ")) continue;
        total++;
        string body = lines[i].Substring(4).Replace("⏎", "\n");
        string after = TextNormalizer.DropMisreadBulletLetters(body);
        if (after == body) continue;
        changed++;
        int n = body.Length - after.Length;
        bullets += n / 2;                     // 每删一处少「字母 + 空格」两个字符
        string tm = "?";
        for (int k = i; k >= 0; k--)
            if (lines[k].StartsWith("时间: ")) { tm = lines[k].Substring(4).Trim(); break; }
        changedOnes.Add($"{Path.GetFileNameWithoutExtension(f)} {tm}（删 {n / 2} 处）");
    }
}

Console.WriteLine($"扫了 {files.Count} 个文件、{total} 条原文；被改动 {changed} 条、共删 {bullets} 处圆点。");
foreach (string s in changedOnes) Console.WriteLine($"   改: {s}");

bool scanOk = changed == 5 && bullets == 11;
Console.WriteLine(scanOk ? "PASS  只命中用户报的 5 条（11 处）" : "FAIL  命中范围和预期不符");
if (scanOk) pass++; else fail++;

Console.WriteLine();
Console.WriteLine($"===== 合计：PASS {pass} / FAIL {fail} =====");
return fail == 0 ? 0 : 1;

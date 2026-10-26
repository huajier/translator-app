using System.Diagnostics;
using System.IO;
using System.Text;

namespace TranslatorApp.Tests;

/// <summary>
/// 第二层：图库回放。跑真实程序 + 真实引擎，跟 tools\素材\基线.json 比。
///
/// 【默认不跑】慢（280 秒左右），且要先有 release 构建。显式跑：
///   dotnet test --filter Category=回放
///
/// 【它守的是什么】
///   基线里对的段，改完代码必须还都对。掉一段就是退步，测试变红。
///   这是唯一能测到「识字准不准」的一层——纯函数测试测不到。
///
/// 【跑之前必须做的一件事】
///   先跑 tools\脚本\构建.ps1 出新的 release\v&lt;版本&gt;\。
///   回放.ps1 默认跑 release\ 下版本号最大的那个目录里的 exe，
///   不跑 src\TranslatorApp\bin\Release\（那个目录构建脚本从不更新，会一直停在旧程序）。
///   本测试**故意不替你构建**：构建失败和测试失败是两件事，混在一起会分不清是谁的问题。
///
/// 【为什么调 PowerShell 而不是用 C# 重写比对逻辑】
///   归一化规则、编辑距离、读答案那份逻辑是 比对回放.ps1 和 比对成绩.ps1 共用的。
///   在 C# 里再实现一遍就成了两把尺子量同一个东西，迟早对不上。
///   本测试只负责「调用 + 看退出码」。
/// </summary>
[Trait("Category", "回放")]
public class 回放回归Tests
{
    [Fact]
    public async Task 图库回放_相对基线没有退步()
    {
        string 根 = 找项目根();
        断言产物不比源码旧(根);

        var psi = new ProcessStartInfo(找PowerShell())
        {
            WorkingDirectory = 根,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Path.Combine(根, "tools", "脚本", "回放.ps1"));

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("启动 PowerShell 失败");

        // stderr 和 stdout 必须同时读，顺序读会在大输出时互相塞满管道死锁
        var errTask = p.StandardError.ReadToEndAsync();
        string 输出 = await p.StandardOutput.ReadToEndAsync();
        输出 += await errTask;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(true);
            throw new TimeoutException("回放超过 10 分钟没结束");
        }

        Assert.True(p.ExitCode == 0,
            $"回放退步，退出码 {p.ExitCode}。\n" +
            $"（先确认跑回放之前已经用 tools\\脚本\\构建.ps1 构建过，否则验的是旧程序）\n" +
            $"完整输出：\n{输出}");
    }

    /// <summary>
    /// 断言 release 里的 exe 不比源码旧。
    ///
    /// 【为什么必须有这一关】回放.ps1 跑的是 release\ 下最新的 exe。改完 Core 忘了构建，
    /// 直接跑这条测试，验的就是旧程序 —— 它会绿，然后带着回归提交。
    /// 2026-10-23 手动跑回放时就踩过一模一样一次的坑（验的是旧程序，白跑一轮）。
    /// 手动跑的时候人还会心虚，自动绿了就没人查了。
    ///
    /// 【会误报】只碰了文件没改内容也会红（改完又改回去、编辑器 touch 一下）。
    /// 这是故意的：误报的代价是重跑一次构建（几十秒），漏报的代价是把回归当成功交出去。
    /// 不对称，宁可误报。
    /// </summary>
    static void 断言产物不比源码旧(string 根)
    {
        var exe = Directory.EnumerateFiles(Path.Combine(根, "release"), "TranslatorApp.exe",
                                            SearchOption.AllDirectories)
                           .Select(f => new FileInfo(f))
                           .OrderByDescending(f => f.LastWriteTimeUtc)
                           .FirstOrDefault();

        Assert.True(exe is not null, "release\\ 下没有 TranslatorApp.exe，先跑 构建.ps1");

        var 最新源码 = new[] { "src/Core", "src/UI", "src/TranslatorApp" }
            .Select(d => Path.Combine(根, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        Assert.True(最新源码 <= exe!.LastWriteTimeUtc,
            $"""
             源码比程序新，这次回放验的是旧程序，结果不可信。
             先跑：& ".\tools\脚本\构建.ps1"

             程序   {exe.FullName}
                    构建于 {exe.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}
             最新源码 {最新源码.ToLocalTime():yyyy-MM-dd HH:mm:ss}
             """);
    }

    /// <summary>
    /// 找 pwsh（PowerShell 7），没有就退回 Windows PowerShell 5.1。
    ///
    /// 【为什么不是写死 pwsh】本机实测没有装 PowerShell 7（Get-Command pwsh 找不到），
    /// 脚本都是按 5.1 写的（改 .ps1 后要补 UTF-8 BOM 就是 5.1 的坑）。
    /// 写死 pwsh 会让这条测试在开发机上永远红，而且红的原因和 OCR 毫无关系。
    /// </summary>
    static string 找PowerShell()
    {
        var pwsh = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "pwsh.exe"))
            .FirstOrDefault(File.Exists);
        return pwsh ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    static string 找项目根()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "tools", "脚本", "回放.ps1")))
            d = d.Parent;
        return d?.FullName
            ?? throw new InvalidOperationException("找不到项目根（缺 tools\\脚本\\回放.ps1）");
    }
}

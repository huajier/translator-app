<#
假译服务器.ps1 — 阶段四量具：一个能装成 **Bing 网页翻译** 的本地假服务器

它干嘛的：
  阶段四要验证「翻译失败只追加详情行、绝不覆盖原文」。真去断网、真去把对方
  限流打爆都不现实，所以本地起一个可以按需返回各种失败的服务器，
  程序把 translateEndpoint 指过来，就能把每一条失败路径都跑一遍。

【协议照 2026-11-02 抓到的原始报文抄，不许凭印象写】
  第一版照 Azure 写（POST JSON + 密钥头），被审阅第四轮打回过一次：
  假服务器讲的不是真协议，程序照着假的写 → 本地全绿、真机第一次就废。
  所以 T3a 这一版整体改成 **Bing 网页翻译** 的协议，报文出处：
    %TEMP%\t3_spike2.txt（探针-免密钥通道-2.ps1 在 2026-11-02 抓的）

  GET  /translator                 → 200，一张 HTML，里面有：
         IG                           32 位十六进制
         data-iid="translator.5023"
         params_AbusePreventionHelper = [<时间戳>,"<token>",3600000]
         ★ params[0] 是时间戳（作 key），params[1] 才是 token，搞反回 400（踩过）

  POST /ttranslatev3?isVertical=1&&IG=..&IID=..
       Content-Type: application/x-www-form-urlencoded
       body: fromLang=auto-detect&to=<lang>&text=<text>&token=<params[1]>&key=<params[0]>
       ★ 字段名是 fromLang，不是 from（第一版写 from= 回 400）

  成功：HTTP 200
        [{"translations":[{"text":"你好","to":"zh-Hans","transliteration":{...}}],
          "usedLLM":true,"detectedLanguage":{"language":"en"}}]

  ★★ 失败**也是 HTTP 200**，错误藏在 body 里（这条通道最大的坑）：
        {"statusCode":205,"errorMessage":""}   ← token / key 不认
        {"statusCode":400,"errorMessage":""}   ← 目标语言无效 / 文字为空 / IG 置空
     所以程序必须 **HTTP 状态和 body.statusCode 都判**，只看 HTTP 会全当成功。

为什么不用 HttpListener（架构师实测，硬阻断）：
  HttpListener 在本机（Win10 19045 / PS 5.1.19041.7291）localhost:5099 与 127.0.0.1:5098
  都抛 System.PlatformNotSupportedException「此平台上不支持该操作」，
  [System.Net.HttpListener]::IsSupported = False。TcpListener 往返正常。
  所以这里手写最小 HTTP。

怎么用：
  1) 独立进程起（Start-Job 在本机沙箱起不来，别用）：
       powershell -ExecutionPolicy Bypass -File "tools\脚本\假译服务器.ps1"
     它会打印「实际端口 = N」（5099 被占就 +1，最多试 8 个）。
     ★ 验收脚本必须解析这行 stdout 拿真端口，不许写死 5099。

  2) 程序把 settings.json 的 translateEndpoint 设成 http://127.0.0.1:<端口>

  3) 想模拟「token 过期」：换个 -TokenSalt 重启（同端口），
     程序手里那张旧 token 就变成脏值 → 一次 205 → 强制刷新 → 成功。
     这就是 S6 的造法，**不需要在程序里留任何测试钩子**。

模式（?mode=xxx，加在 POST 的 URL 上）——全部按 Bing 语义，HTTP 恒 200：
  ok       200 + 正常译文数组
  401      200 + {"statusCode":205}   ← Bing 真正的「凭证不认」
  403      200 + {"statusCode":403}   ← 假造额度码（真 Bing 上没有，是模拟场景）
  408      200 + {"statusCode":408}   ← 假造
  429      200 + {"statusCode":429}   ← 假造
  empty    200 + 数组但 text 是空串
  timeout  拖 ?ms=（默认 6000）毫秒才回；程序 2.5s 硬闸应该先打断它
  badjson  200 + 不是 JSON 的响应体

⚠️ 单线程：一次只处理一个请求。够用（测试工具），但别并发打。
⚠️ timeout 模式会占住线程，所以放最后测。
#>

param(
    [int]$Port = 5099,
    [int]$MaxPortTries = 8,
    [string]$TokenSalt = 'v1',
    # 请求 URL 上没带 ?mode= 时用这个。
    # 为什么要它：程序的 endpoint 只是个根地址，它自己不会往 URL 上加 mode，
    # 所以「让程序撞上 timeout / 205」只能靠这个默认值 —— 重启服务器换一个默认模式即可。
    [string]$DefaultMode = 'ok',
    # 【S6 专用】成功翻够 N 次后自动换票。
    # 为什么要它：S6 要验的是「同一个进程里先成功、token 失效、205、刷新、再成功」。
    # 重启服务器换 -TokenSalt 只能让**下个进程**拿到新票，走不到 205 分支。
    # 0 = 关闭。
    [int]$RotateAfter = 0
)

$ErrorActionPreference = 'Stop'

# ---- 页面里的固定素材（改这里就等于换一张票）
$fakeIg = '6B73902BABED4415A2AFF1805CE213A9'
$fakeKey = '1793589918800'
$fakeToken = 'FAKE' + $TokenSalt + 'TOKEN0123456789'

# 页面是每请求现拼的（不是常量），因为 -RotateAfter 要在运行中换票。
function Get-PageHtml {
    param([string]$Token)
    return @"
<!doctype html>
<html><head><meta charset="utf-8"><title>Bing Translator (fake)</title></head>
<body>
<div id="rich_tta" data-iid="translator.5023"></div>
<script>
var params_AbusePreventionHelper = [$fakeKey,"$Token",3600000];
var _G = { IG: "$fakeIg" };
</script>
</body></html>
"@
}

$currentToken = $fakeToken   # -RotateAfter 会在运行中改它
$okCount = 0

# ---- 找一个能绑的端口（5099 被占就 +1）
$listener = $null
$actualPort = 0
for ($i = 0; $i -lt $MaxPortTries; $i++) {
    $tryPort = $Port + $i
    try {
        $candidate = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $tryPort)
        $candidate.Start()
        $listener = $candidate
        $actualPort = $tryPort
        break
    } catch {
        Write-Host ("端口 {0} 占用了，试下一个（{1}）" -f $tryPort, $_.Exception.Message)
    }
}
if ($null -eq $listener) { Write-Host "没有可用端口，放弃"; exit 1 }

Write-Host ("假译服务器（Bing 协议）已起：http://127.0.0.1:{0}/translator" -f $actualPort)
Write-Host ("实际端口 = {0}" -f $actualPort)
Write-Host ("token 盐 = {0}（换一个重启就模拟 token 过期）" -f $TokenSalt)
Write-Host "（Ctrl+C 停。单线程，一次一个请求。）"

# ---- 回包：Content-Length 必须是 UTF-8 字节数，不是字符数
function Send-Json {
    param($Stream, [int]$Code, [string]$Reason, [string]$Body, [int]$DelayMs = 0,
          [string]$ContentType = 'application/json; charset=utf-8')

    if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $head = "HTTP/1.1 $Code $Reason`r`n" +
            "Content-Type: $ContentType`r`n" +
            "Content-Length: $($bytes.Length)`r`n" +
            "Connection: close`r`n" +
            "`r`n"
    $headBytes = [System.Text.Encoding]::ASCII.GetBytes($head)

    $Stream.Write($headBytes, 0, $headBytes.Length)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
}

# Bing 的错误体形态：{"statusCode":N,"errorMessage":"..."}，**配 HTTP 200**
function Bing-Error {
    param([int]$Code, [string]$Message)
    return '{"statusCode":' + $Code + ',"errorMessage":' + (ConvertTo-Json -InputObject $Message -Compress) + '}'
}

function Get-QueryValue {
    param([string]$Path, [string]$Key)
    $m = [regex]::Match($Path, "[?&]$([regex]::Escape($Key))=([^&\s]*)")
    if (-not $m.Success) { return $null }
    return [System.Uri]::UnescapeDataString($m.Groups[1].Value)
}

function Get-FormValue {
    param([string]$Body, [string]$Key)
    if ([string]::IsNullOrEmpty($Body)) { return $null }
    $m = [regex]::Match($Body, "(?:^|&)$([regex]::Escape($Key))=([^&]*)")
    if (-not $m.Success) { return $null }
    $raw = $m.Groups[1].Value -replace '\+', ' '
    return [System.Uri]::UnescapeDataString($raw)
}

# ---- 读一次完整请求：先读到 \r\n\r\n 拿头，再按 Content-Length 读 body
function Read-Request {
    param($Stream)

    $ms = New-Object System.IO.MemoryStream
    $buf = New-Object byte[] 4096
    $headerEnd = -1

    while ($headerEnd -lt 0) {
        $n = $Stream.Read($buf, 0, $buf.Length)
        if ($n -le 0) { return $null }
        $ms.Write($buf, 0, $n)
        $arr = $ms.ToArray()
        for ($i = 0; $i -le $arr.Length - 4; $i++) {
            if ($arr[$i] -eq 13 -and $arr[$i + 1] -eq 10 -and $arr[$i + 2] -eq 13 -and $arr[$i + 3] -eq 10) {
                $headerEnd = $i; break
            }
        }
        if ($ms.Length -gt 262144) { return $null }
    }

    $all = $ms.ToArray()
    $headStr = [System.Text.Encoding]::ASCII.GetString($all, 0, $headerEnd)

    $lines = $headStr -split "`r`n"
    $headers = @{}
    for ($i = 1; $i -lt $lines.Count; $i++) {
        $idx = $lines[$i].IndexOf(':')
        if ($idx -gt 0) {
            $k = $lines[$i].Substring(0, $idx).Trim().ToLowerInvariant()
            $v = $lines[$i].Substring($idx + 1).Trim()
            $headers[$k] = $v
        }
    }

    $bodyStart = $headerEnd + 4
    $have = $all.Length - $bodyStart
    $need = 0
    if ($headers.ContainsKey('content-length')) {
        [void][int]::TryParse($headers['content-length'], [ref]$need)
    }

    $bodyBytes = New-Object System.IO.MemoryStream
    if ($have -gt 0) { $bodyBytes.Write($all, $bodyStart, [Math]::Min($have, [Math]::Max($need, 0))) }

    while ($bodyBytes.Length -lt $need) {
        $n = $Stream.Read($buf, 0, $buf.Length)
        if ($n -le 0) { break }
        $bodyBytes.Write($buf, 0, [Math]::Min($n, $need - $bodyBytes.Length))
    }

    return @{
        RequestLine = $lines[0]
        Headers     = $headers
        Body        = [System.Text.Encoding]::UTF8.GetString($bodyBytes.ToArray())
    }
}

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $stream.ReadTimeout = 30000
            $client.ReceiveTimeout = 30000

            $req = Read-Request -Stream $stream
            if ($null -eq $req) { continue }

            $pathMatch = [regex]::Match($req.RequestLine, '^(\w+)\s+([^\s]+)')
            if (-not $pathMatch.Success) {
                Send-Json -Stream $stream -Code 400 -Reason "Bad Request" -Body (Bing-Error 400 'Bad request line.')
                continue
            }
            $method = $pathMatch.Groups[1].Value.ToUpperInvariant()
            $path = $pathMatch.Groups[2].Value
            $pathOnly = ($path -split '\?')[0]

            $mode = Get-QueryValue -Path $path -Key 'mode'
            if ([string]::IsNullOrEmpty($mode)) { $mode = $DefaultMode }
            $delay = Get-QueryValue -Path $path -Key 'ms'

            Write-Host ("[{0}] {1} {2}  mode={3}" -f (Get-Date -Format 'HH:mm:ss.fff'), $method, $path, $mode)

            # ---- ① 授权页面（程序从这里拿 IG / token / key）
            if ($pathOnly -eq '/translator' -or $pathOnly -eq '/translator/') {
                if ($method -ne 'GET') {
                    Send-Json -Stream $stream -Code 405 -Reason "Method Not Allowed" -Body (Bing-Error 400 'Use GET.')
                    continue
                }
                Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Get-PageHtml -Token $currentToken) -ContentType 'text/html; charset=utf-8'
                continue
            }

            # ---- ② 翻译（Bing 只认 POST + 表单，GET 一律 405）
            if ($pathOnly -ne '/ttranslatev3') {
                Send-Json -Stream $stream -Code 404 -Reason "Not Found" -Body (Bing-Error 400 ("No such path: " + $pathOnly))
                continue
            }
            if ($method -ne 'POST') {
                Send-Json -Stream $stream -Code 405 -Reason "Method Not Allowed" -Body (Bing-Error 400 'Use POST. GET fallback does not count as an acceptance run.')
                continue
            }

            $to = Get-FormValue -Body $req.Body -Key 'to'
            $text = Get-FormValue -Body $req.Body -Key 'text'
            $token = Get-FormValue -Body $req.Body -Key 'token'
            $keyIn = Get-FormValue -Body $req.Body -Key 'key'
            $fromLang = Get-FormValue -Body $req.Body -Key 'fromLang'

            Write-Host ("        fromLang={0} to={1} text={2} 字 token={3} key={4}" -f `
                $fromLang, $to, $([string]$text).Length, `
                $(if ([string]::IsNullOrEmpty($token)) { '**没带**' } else { '有' }), `
                $(if ([string]::IsNullOrEmpty($keyIn)) { '**没带**' } else { '有' }))

            # ---- 源语言字段必填（真 Bing 实测：不传 fromLang 或写成 from 都回 400）
            # 这条必须校验，不能只打印：否则程序漏了 fromLang 本地照样全绿，
            # 到真机上才变成「翻译老是失败」。2026-11-03 真 Bing 对照实测：
            #   fromLang=auto-detect → 200 + 译文；from=auto-detect → 400；不传 → 400。
            if ([string]::IsNullOrWhiteSpace($fromLang)) {
                Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Bing-Error 400 'fromLang missing')
                continue
            }

            # ---- 目标语言必填（真 Bing 缺 to 回 400）
            if ([string]::IsNullOrWhiteSpace($to)) {
                Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Bing-Error 400 'target language missing')
                continue
            }
            $to = ($to -replace '[^A-Za-z0-9\-]', '')
            if ([string]::IsNullOrWhiteSpace($to)) { $to = 'zh-Hans' }

            # ---- 文字必填（真 Bing 空 text 回 400）
            if ([string]::IsNullOrEmpty($text)) {
                Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Bing-Error 400 'text missing')
                continue
            }

            # ---- 【P1-D2 的 Bing 版】token/key 必须校验：
            #      程序忘了送 token，本地照样全绿，真机第一次就 205。
            if ($token -ne $currentToken -or [string]::IsNullOrEmpty($keyIn)) {
                Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Bing-Error 205 '')
                continue
            }

            # 【S6】成功够 N 次就换票：下一次带旧 token 的请求会拿到 205，
            #       程序应强制刷新一次并成功 —— 这才是真·205 路径。
            $okCount++
            if ($RotateAfter -gt 0 -and $okCount -eq $RotateAfter) {
                $currentToken = 'FAKE' + $TokenSalt + 'ROTATED' + $okCount
                Write-Host ("       >>> 已换票（第 {0} 次成功后），旧 token 从此回 205：{1}" -f $okCount, $currentToken)
            }

            $translated = "【假译文】" + $text

            # ConvertTo-Json 负责把引号/反斜杠/换行转义对，比自己拼省事
            $okBody = '[{"translations":[{"text":' +
                      (ConvertTo-Json -InputObject $translated -Compress) +
                      ',"to":"' + $to + '"}],"usedLLM":true,"detectedLanguage":{"language":"en"}}]'

            switch ($mode) {
                'ok' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body $okBody
                }
                '401' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Bing-Error 205 '')
                }
                '403' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Bing-Error 403 'fake quota exceeded')
                }
                '408' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Bing-Error 408 'fake resource missing')
                }
                '429' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Bing-Error 429 'fake rate limit')
                }
                'empty' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" `
                        -Body ('[{"translations":[{"text":"","to":"' + $to + '"}],"detectedLanguage":{"language":"en"}}]')
                }
                'timeout' {
                    # 延迟可配，默认 6 秒：够超过 2.5s 硬闸，又不至于让整套测试停半分钟
                    $ms = 6000
                    if (-not [string]::IsNullOrEmpty($delay)) { [void][int]::TryParse($delay, [ref]$ms) }
                    if ($ms -lt 100) { $ms = 100 }
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -DelayMs $ms -Body $okBody
                }
                'badjson' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body '这不是 JSON，程序应该当失败处理'
                }
                default {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body (Bing-Error 400 ("Unknown fake mode: " + $mode))
                }
            }
        } catch {
            # 客户端提前断开（比如 2.5s 硬闸打断了）——不是服务器的问题，记一句继续
            Write-Host ("  请求处理中断：{0}" -f $_.Exception.Message)
        } finally {
            try { $client.Close() } catch { }
        }
    }
}
finally {
    try { $listener.Stop() } catch { }
    Write-Host "假译服务器已停"
}

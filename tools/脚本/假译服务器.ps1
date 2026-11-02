<#
假译服务器.ps1 — 阶段四 T2 量具：一个能装成微软翻译接口的本地假服务器

它干嘛的：
  阶段四要验证「翻译失败只追加详情行、绝不覆盖原文」。真去断网、真去把额度烧光
  都不现实，所以本地起一个可以按需返回 401 / 403 / 408 / 429 / 空串 / 超时 / 非 JSON 的服务器，
  T3 的程序把 translateEndpoint 指过来，就能把每一条失败路径都跑一遍。

为什么不用 HttpListener（2026-11-02 架构师实测，硬阻断）：
  HttpListener 在本机（Win10 19045 / PS 5.1.19041.7291）localhost:5099 与 127.0.0.1:5098
  都抛 System.PlatformNotSupportedException「此平台上不支持该操作」，
  [System.Net.HttpListener]::IsSupported = False。TcpListener 往返正常。
  所以这里手写最小 HTTP：只认 GET，只回 application/json，够用就行。

怎么用：
  1) 起服务器（另开一个窗口 / 后台作业）：
       powershell -ExecutionPolicy Bypass -File "tools\脚本\假译服务器.ps1"
     它会打印实际端口（5099 被占用就往后 +1，最多试 8 个）。
  2) 打它（mode 不传就是 ok）：
       Invoke-RestMethod "http://127.0.0.1:<端口>/translate?api-version=3.0&mode=ok&text=Hello"

模式（?mode=xxx）：
  ok       200，返回中文假译文（用中文是为了压住 Content-Length 那个坑：字节数 ≠ 字符数）
  401      密钥无效
  403      额度用尽（响应体带 error.code，程序必须读它而不是拿 HTTP 码当结论）
  408      超时
  429      限流（不许说成额度用尽）
  empty    200，但译文是空串
  timeout  拖 20 秒才回（客户端 2.5s 预算应该先打断它）
  badjson  200，但响应体不是 JSON

⚠️ 单线程：一次只处理一个请求。够用（测试工具），但别并发打。
⚠️ timeout 模式会让服务器卡住 20 秒，客户端超时后服务器还在睡——所以把 timeout 放最后测。
#>

param(
    [int]$Port = 5099,
    [int]$MaxPortTries = 8
)

$ErrorActionPreference = 'Stop'

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

Write-Host ("假译服务器已起：http://127.0.0.1:{0}/translate?api-version=3.0" -f $actualPort)
Write-Host ("实际端口 = {0}" -f $actualPort)
Write-Host "（Ctrl+C 停。单线程，一次一个请求。）"

# ---- 回包：Content-Length 必须是 UTF-8 字节数，不是字符数
function Send-Json {
    param($Stream, [int]$Code, [string]$Reason, [string]$Body, [int]$DelayMs = 0)

    if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $head = "HTTP/1.1 $Code $Reason`r`n" +
            "Content-Type: application/json; charset=utf-8`r`n" +
            "Content-Length: $($bytes.Length)`r`n" +
            "Connection: close`r`n" +
            "`r`n"
    $headBytes = [System.Text.Encoding]::ASCII.GetBytes($head)

    $Stream.Write($headBytes, 0, $headBytes.Length)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
}

function Get-QueryValue {
    param([string]$Path, [string]$Key)
    $m = [regex]::Match($Path, "[?&]$([regex]::Escape($Key))=([^&\s]*)")
    if (-not $m.Success) { return $null }
    return [System.Uri]::UnescapeDataString($m.Groups[1].Value)
}

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $stream.ReadTimeout = 20000
            $client.ReceiveTimeout = 20000

            # ---- 读到 \r\n\r\n 为止（请求头结束）
            $buf = New-Object byte[] 8192
            $sb = New-Object System.Text.StringBuilder
            while ($true) {
                $n = $stream.Read($buf, 0, $buf.Length)
                if ($n -le 0) { break }
                [void]$sb.Append([System.Text.Encoding]::ASCII.GetString($buf, 0, $n))
                if ($sb.ToString().Contains("`r`n`r`n")) { break }
                if ($sb.Length -gt 65536) { break }
            }

            $head = $sb.ToString()
            $reqLine = ($head -split "`r`n")[0]
            $pathMatch = [regex]::Match($reqLine, '^GET\s+([^\s]+)')
            if (-not $pathMatch.Success) {
                Send-Json -Stream $stream -Code 405 -Reason "Method Not Allowed" -Body '{"error":{"code":"405","message":"only GET"}}'
                continue
            }

            $path = $pathMatch.Groups[1].Value
            $mode = Get-QueryValue -Path $path -Key 'mode'
            if ([string]::IsNullOrEmpty($mode)) { $mode = 'ok' }
            $text = Get-QueryValue -Path $path -Key 'text'

            Write-Host ("[{0}] {1}  mode={2}" -f (Get-Date -Format 'HH:mm:ss.fff'), $path, $mode)

            switch ($mode) {
                'ok' {
                    $translated = if ([string]::IsNullOrEmpty($text)) { "你好，世界。这是一条假译文。" } else { "【假译文】" + $text }
                    $body = '[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"' + $translated + '","to":"zh-Hans"}]}]'
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body $body
                }
                '401' {
                    Send-Json -Stream $stream -Code 401 -Reason "Unauthorized" `
                        -Body '{"error":{"code":"401001","message":"The request is not authorized because credentials are missing or invalid."}}'
                }
                '403' {
                    # 关键：额度用尽只能靠 error.code 判，不许拿 HTTP 403 直接当结论
                    Send-Json -Stream $stream -Code 403 -Reason "Forbidden" `
                        -Body '{"error":{"code":"403001","message":"Out of call volume quota. Quota will be replenished in 5 days."}}'
                }
                '408' {
                    Send-Json -Stream $stream -Code 408 -Reason "Request Timeout" `
                        -Body '{"error":{"code":"408","message":"The operation timed out."}}'
                }
                '429' {
                    Send-Json -Stream $stream -Code 429 -Reason "Too Many Requests" `
                        -Body '{"error":{"code":"429001","message":"Rate limit is exceeded. Try again later."}}'
                }
                'empty' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" `
                        -Body '[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"","to":"zh-Hans"}]}]'
                }
                'timeout' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -DelayMs 20000 `
                        -Body '[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"太晚了","to":"zh-Hans"}]}]'
                }
                'badjson' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body '这不是 JSON，程序应该当失败处理'
                }
                default {
                    Send-Json -Stream $stream -Code 400 -Reason "Bad Request" `
                        -Body ('{"error":{"code":"400036","message":"unknown mode: ' + $mode + '"}}')
                }
            }
        } catch {
            # 客户端提前断开（比如超时打折了）——不是服务器的问题，记一句继续
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

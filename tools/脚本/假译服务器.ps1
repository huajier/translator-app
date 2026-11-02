<#
假译服务器.ps1 — 阶段四 T2 量具：一个能装成微软翻译接口的本地假服务器

它干嘛的：
  阶段四要验证「翻译失败只追加详情行、绝不覆盖原文」。真去断网、真去把额度烧光
  都不现实，所以本地起一个可以按需返回 401 / 403 / 408 / 429 / 空串 / 超时 / 非 JSON 的服务器，
  T3 的程序把 translateEndpoint 指过来，就能把每一条失败路径都跑一遍。

【协议照官方抄，不许凭印象写（2026-11-02 审阅第四轮 P0-D1 打回重做）】
  上一版只认 GET + 查询串，微软真接口是 POST + JSON body + 密钥头。
  T3 照着上一版写 → 本地全绿、真机第一次就 405。所以这一版认 POST，并且校验密钥头。

  官方文档（链接抄在这里，改协议前先点开看一眼）：
    · 请求/响应格式 https://learn.microsoft.com/en-us/azure/ai-services/translator/text-translation/reference/v3/translate
    · 错误码表     https://learn.microsoft.com/en-us/azure/ai-services/translator/text-translation/reference/status-response-codes
    · 认证头       https://learn.microsoft.com/en-us/azure/ai-services/translator/text-translation/reference/authentication

  要点（照抄官方）：
    POST /translate?api-version=3.0&to=zh-Hans
    必填头：Ocp-Apim-Subscription-Key（区域资源再加 Ocp-Apim-Subscription-Region）
            Content-Type: application/json; charset=UTF-8
    body：  [{"Text":"..."}]           ← 官方示例 Text/text 混用，解析不区分大小写
    响应：  [{"detectedLanguage":{"language":"en","score":1.0},
              "translations":[{"text":"...","to":"zh-Hans"}]}]
            不传 from 时服务端自动检测，detectedLanguage 恒给（我们恒不传 from）

为什么不用 HttpListener（架构师实测，硬阻断）：
  HttpListener 在本机（Win10 19045 / PS 5.1.19041.7291）localhost:5099 与 127.0.0.1:5098
  都抛 System.PlatformNotSupportedException「此平台上不支持该操作」，
  [System.Net.HttpListener]::IsSupported = False。TcpListener 往返正常。
  所以这里手写最小 HTTP。

怎么用：
  1) 起服务器（另开一个窗口 / 后台作业）：
       powershell -ExecutionPolicy Bypass -File "tools\脚本\假译服务器.ps1"
     它会打印实际端口（5099 被占用就往后 +1，最多试 8 个）。
  2) 打它：
       POST http://127.0.0.1:<端口>/translate?api-version=3.0&to=zh-Hans&mode=ok
       Header: Ocp-Apim-Subscription-Key: 假密钥
       Body:   [{"Text":"Hello"}]

模式（?mode=xxx）：
  ok       200，返回中文假译文（中文是为了压住 Content-Length 那个坑：字节数 ≠ 字符数）
  401      凭据缺失或无效     error.code=401000
  403      免费额度用尽       error.code=403001
  408      自定义系统未就绪   error.code=408001   ← 注意：这不是网络超时
  429      限流               error.code=429001
  empty    200，但译文是空串
  timeout  拖 ?ms=（默认 6000）毫秒才回；客户端 2.5s 硬闸应该先打断它
  badjson  200，但响应体不是 JSON

⚠️ 单线程：一次只处理一个请求。够用（测试工具），但别并发打。
⚠️ timeout 模式会占住线程，所以放最后测；默认 6000ms 而不是 20 秒（别让整套测试停半分钟）。
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

# 官方错误响应形态：{"error":{"code":"NNNNNN","message":"..."}}
function Error-Body {
    param([string]$Code, [string]$Message)
    return '{"error":{"code":"' + $Code + '","message":"' + $Message + '"}}'
}

function Get-QueryValue {
    param([string]$Path, [string]$Key)
    $m = [regex]::Match($Path, "[?&]$([regex]::Escape($Key))=([^&\s]*)")
    if (-not $m.Success) { return $null }
    return [System.Uri]::UnescapeDataString($m.Groups[1].Value)
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

# 从各种 body 形态里抠出要翻的文字（官方示例 Text / text 混用）
function Get-TextFromBody {
    param([string]$Body)
    if ([string]::IsNullOrWhiteSpace($Body)) { return $null }
    try { $parsed = $Body | ConvertFrom-Json } catch { return $null }
    $items = @($parsed)          # 单元素数组在 PS 5.1 里会退化成对象，包一层
    if ($items.Count -lt 1) { return $null }
    $first = $items[0]
    if ($null -eq $first) { return $null }
    foreach ($name in @('Text', 'text')) {   # PowerShell 取属性本来就不区分大小写，这是双保险
        $v = $first.PSObject.Properties[$name]
        if ($null -ne $v) { return [string]$v.Value }
    }
    return $null
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
                Send-Json -Stream $stream -Code 400 -Reason "Bad Request" -Body (Error-Body '400000' 'Bad request line.')
                continue
            }
            $method = $pathMatch.Groups[1].Value.ToUpperInvariant()
            $path = $pathMatch.Groups[2].Value

            $mode = Get-QueryValue -Path $path -Key 'mode'
            if ([string]::IsNullOrEmpty($mode)) { $mode = 'ok' }
            $to = Get-QueryValue -Path $path -Key 'to'
            $delay = Get-QueryValue -Path $path -Key 'ms'

            $key = $req.Headers['ocp-apim-subscription-key']
            $hasKey = -not [string]::IsNullOrWhiteSpace($key)

            Write-Host ("[{0}] {1} {2}  mode={3} to={4} key={5}" -f `
                (Get-Date -Format 'HH:mm:ss.fff'), $method, $path, $mode, $to, $(if ($hasKey) { '有' } else { '**没带**' }))

            # ---- 【P1-D2】密钥头必须先校验：否则 T3 忘了送这个头，本地照样全绿
            if (-not $hasKey) {
                Send-Json -Stream $stream -Code 401 -Reason "Unauthorized" `
                    -Body (Error-Body '401000' 'Access denied due to missing subscription key. Make sure to include subscription key when making requests to an API.')
                continue
            }

            # ---- GET 兜底：方便人眼在浏览器里看一眼，但**不算验收通过**
            if ($method -ne 'POST') {
                Send-Json -Stream $stream -Code 405 -Reason "Method Not Allowed" `
                    -Body (Error-Body '405000' 'Use POST. This GET fallback exists for eyeballing only and does not count as an acceptance run.')
                continue
            }

            # ---- 官方要求必填 to，缺了就是 400036（目标语言缺失或无效）
            if ([string]::IsNullOrWhiteSpace($to)) {
                Send-Json -Stream $stream -Code 400 -Reason "Bad Request" `
                    -Body (Error-Body '400036' 'The target language is not valid or missing.')
                continue
            }
            # 回显进响应，让程序能自证传对了；顺手挡掉拼 JSON 的注入
            $to = ($to -replace '[^A-Za-z0-9\-]', '')
            if ([string]::IsNullOrWhiteSpace($to)) { $to = 'zh-Hans' }

            $text = Get-TextFromBody -Body $req.Body
            if ($null -eq $text) {
                Send-Json -Stream $stream -Code 400 -Reason "Bad Request" `
                    -Body (Error-Body '400000' 'The request body is not a valid JSON array of {"Text": "..."}.')
                continue
            }

            $translated = if ([string]::IsNullOrEmpty($text)) { "你好，世界。这是一条假译文。" } else { "【假译文】" + $text }

            # ConvertTo-Json 负责把引号/反斜杠/换行转义对，比自己拼省事
            $okBody = '[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":' +
                      (ConvertTo-Json -InputObject $translated -Compress) + ',"to":"' + $to + '"}]}]'

            switch ($mode) {
                'ok' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body $okBody
                }
                '401' {
                    Send-Json -Stream $stream -Code 401 -Reason "Unauthorized" `
                        -Body (Error-Body '401000' 'Access denied due to invalid subscription key or wrong API endpoint.')
                }
                '403' {
                    # 额度用尽只能靠 error.code 判，不许拿 HTTP 403 直接当结论
                    Send-Json -Stream $stream -Code 403 -Reason "Forbidden" `
                        -Body (Error-Body '403001' 'Out of call volume quota. Quota will be replenished in 5 days.')
                }
                '408' {
                    # 【P1-D1】官方对 408 的描述是「资源缺失 / 自定义系统未就绪」，**不是网络超时**。
                    # 我们真正的超时是客户端那 2.5s 硬闸，跟这个状态码无关。
                    Send-Json -Stream $stream -Code 408 -Reason "Request Timeout" `
                        -Body (Error-Body '408001' 'The custom system is not ready yet. Please retry in a minute or two.')
                }
                '429' {
                    Send-Json -Stream $stream -Code 429 -Reason "Too Many Requests" `
                        -Body (Error-Body '429001' 'Rate limit is exceeded. Try again later.')
                }
                'empty' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" `
                        -Body ('[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"","to":"' + $to + '"}]}]')
                }
                'timeout' {
                    # 【P1-D3】延迟可配，默认 6 秒：够超过 2.5s 硬闸，又不至于让整套测试停半分钟
                    $ms = 6000
                    if (-not [string]::IsNullOrEmpty($delay)) { [void][int]::TryParse($delay, [ref]$ms) }
                    if ($ms -lt 100) { $ms = 100 }
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -DelayMs $ms -Body $okBody
                }
                'badjson' {
                    Send-Json -Stream $stream -Code 200 -Reason "OK" -Body '这不是 JSON，程序应该当失败处理'
                }
                default {
                    # 400036 官方含义是「目标语言缺失」，别拿来当「未知模式」
                    Send-Json -Stream $stream -Code 400 -Reason "Bad Request" `
                        -Body (Error-Body '400000' ("Unknown fake mode: " + $mode))
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

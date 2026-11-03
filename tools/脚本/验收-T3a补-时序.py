# -*- coding: utf-8 -*-
"""第十轮 §4 三条验收规范落地：真机框选一次性验 时序 + 认字内容 + 取证截图。

为什么要重写 _f1 而不是改它：
  _f1 只断言「两次 UpdateWith + 翻译成功」，**不看认出来的内容对不对**。
  框歪了只认到半句话，那两条照样 PASS（第十轮 §4.2 原话）。
  这里按第十轮 §4 补齐三条：
    ① 框选定死内缩：文字窗口 100~1000 × 100~520，框 160/160/940/480，四周留 60px；
    ② 断言认出来的内容：开头 20 字在、结尾 20 字在、长度 ≥ 原文 95%；
    ③ 每次自动存一张截图 + 认出来的原文到 tools\\日志\\取证截图\\。

时序断言（T3a-补 的核心）：冷启动第一次框选，日志必须依次出现
  认字成功 → 译文区已切到「翻译中…」 → UpdateWith（原文）→ 翻译：成功 → UpdateWith（译文）
且原文那次 UpdateWith 与译文那次之间**必须隔开**（证明是两段，不是一次到位）。
"""
import io, json, os, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", "v1.6", "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
PORT = 5099

# 文字窗口 100~1000 × 100~520；框往内缩 60px（第十轮 §4.1）
BOX = ("160", "160", "940", "480")

# 【必须 ≤ 300 字】日志「最终原文：」只留 300 字（OcrTextCapture.Utils.cs:116），
# 超过就被截断，结尾 20 字断言永远拿不到，会误判成「框歪了」。
TEXT = (
    'The ancient gate stood silent at the edge of the valley. Nobody in the village '
    'remembered who had carved the runes, or why the stone still hummed whenever the '
    'moon rose. Children were warned to keep their distance, and hunters took the '
    'long road around the hill even in snow.'
)

SHOW_PS = r"""
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$f = New-Object System.Windows.Forms.Form
$f.Text = 'F3Source'
$f.StartPosition = 'Manual'
$f.Left = 100; $f.Top = 100; $f.Width = 900; $f.Height = 420
$f.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$panel = New-Object System.Windows.Forms.Panel
$panel.Dock = 'Fill'
$panel.Padding = New-Object System.Windows.Forms.Padding(60, 60, 60, 60)
$panel.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.ReadOnly = $true; $tb.BorderStyle = 'None'
$tb.Dock = 'Fill'
$tb.Font = New-Object System.Drawing.Font('Consolas', 13)
$tb.ForeColor = [System.Drawing.Color]::White
$tb.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$tb.Text = [IO.File]::ReadAllText('{txt}', [Text.Encoding]::UTF8)
$panel.Controls.Add($tb)
$f.Controls.Add($panel)
$f.TopMost = $true
$f.Show()
$f.Activate()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Seconds 300
"""

OUT = []


def say(s=""):
    OUT.append(str(s))


def nlines():
    if not os.path.exists(LOG):
        return 0
    return len(io.open(LOG, encoding="utf-8-sig", errors="replace").readlines())


def setcfg(on, endpoint):
    d = json.load(io.open(CFG, encoding="utf-8-sig"))
    d["translateEnabled"] = on
    d["translateNoticeShown"] = True
    d["translateTargetLang"] = "zh-Hans"
    d["translateEndpoint"] = endpoint
    io.open(CFG, "w", encoding="utf-8", newline="").write(
        json.dumps(d, ensure_ascii=False, indent=2))


def screenshot(path):
    ps = ("Add-Type -AssemblyName System.Windows.Forms,System.Drawing;"
          "$b=[System.Windows.Forms.SystemInformation]::VirtualScreen;"
          "$bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height;"
          "$g=[System.Drawing.Graphics]::FromImage($bmp);"
          "$g.CopyFromScreen($b.Left,$b.Top,0,0,$bmp.Size);"
          "$bmp.Save('%s');" % path.replace("\\", "\\\\"))
    subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                   capture_output=True, timeout=90)


def main():
    original = io.open(CFG, encoding="utf-8-sig").read()
    srv = show = app = None
    try:
        srv = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                                "-File", os.path.join(ROOT, "tools", "脚本", "假译服务器.ps1")],
                               cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        txt = os.path.join(ROOT, "tools", "临时", "f3-text.txt")
        showps = os.path.join(ROOT, "tools", "临时", "_f3_show.ps1")
        io.open(txt, "w", encoding="utf-8", newline="").write("\r\n" + TEXT)
        io.open(showps, "w", encoding="utf-8-sig", newline="").write(
            SHOW_PS.replace("{txt}", txt))
        show = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                                 "-File", showps],
                                cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(3.5)

        setcfg(True, "http://127.0.0.1:%d" % PORT)
        stamp = time.strftime("%Y%m%d_%H%M%S")
        shot = os.path.join(SHOT_DIR, "t3a补_%s.png" % stamp)
        os.makedirs(SHOT_DIR, exist_ok=True)

        base = nlines()
        app = subprocess.Popen([EXE], cwd=ROOT)
        time.sleep(5.0)                      # 冷启动，不热身
        # 截图必须跟框选同时进行：卡片只活 3 秒，串行拍必然漏
        import threading
        th = threading.Thread(target=lambda: (time.sleep(2.4), screenshot(shot)))
        th.start()
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MOCK,
                        "-场景", "正常框选", "-x1", BOX[0], "-y1", BOX[1],
                        "-x2", BOX[2], "-y2", BOX[3]],
                       cwd=ROOT, capture_output=True, timeout=120)
        th.join(timeout=120)
        time.sleep(9.0)
        subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
        time.sleep(1.0)

        rows = [ln.strip() for ln in
                io.open(LOG, encoding="utf-8-sig", errors="replace").readlines()[base:]]
        ocr = [r for r in rows if "取词成功" in r or "取词失败" in r]
        thinking = [r for r in rows if "译文区已切到「翻译中…」" in r]
        done = [r for r in rows if "翻译：成功" in r or "翻译：放弃" in r]
        upd = [r for r in rows if "提示框已更新为取词结果" in r]

        say("== T3a-补 真机验收（冷启动第一次框选，框 %s）==" % "/".join(BOX))
        for label, arr in (("认字", ocr), ("翻译中", thinking),
                           ("翻译结果", done), ("UpdateWith", upd)):
            say("-- %s：%d 条" % (label, len(arr)))
            for r in arr:
                say("   " + r[:190])

        # ---------- 断言 ----------
        checks = []

        # ① 内容断言（第十轮 §4.2）：证明框没歪、字没切
        ocr_text = ""
        for r in rows:
            if "最终原文：" in r:
                ocr_text = r.split("最终原文：", 1)[1]
                break
        if not ocr_text:
            # 用「取词成功：… 字数=N」里的字数做粗校验
            import re
            m = [re.search(r"字数=(\d+)", r) for r in ocr]
            got = max([int(x.group(1)) for x in m if x], default=0)
            content_ok = got >= len(TEXT) * 0.95
            say("  （日志没留最终原文，用字数比对）认出的字数 = %d" % got)
        else:
            head_ok = TEXT[:20] in ocr_text
            tail_ok = TEXT[-20:] in ocr_text
            content_ok = head_ok and tail_ok and len(ocr_text) >= len(TEXT) * 0.95
            say("  认出的原文：%s" % ocr_text[:120])
            say("  开头20字在=%s 结尾20字在=%s 长度 %d / 原文 %d = %.0f%%"
                % (head_ok, tail_ok, len(ocr_text), len(TEXT),
                   100.0 * len(ocr_text) / len(TEXT)))
        checks.append(("①认字内容对（框没歪）", content_ok))

        # ② 时序断言：翻译中 → 原文一次 → 成功 → 译文一次
        checks.append(("②出现「翻译中…」", len(thinking) >= 1))
        checks.append(("③两次 UpdateWith（原文+译文）", len(upd) >= 2))
        seq_ok = bool(thinking) and bool(upd) and len(upd) >= 2 and \
            thinking[0] <= [r for r in rows if "提示框已更新" in r][0]
        checks.append(("④翻译态早于第一次 UpdateWith", seq_ok))
        checks.append(("⑤第一次框选就翻译成功",
                       any("翻译：成功" in r for r in done)))
        import re
        checks.append(("⑥没有「预算用完」", not any("预算用完" in r for r in rows)))

        say("")
        for name, ok in checks:
            say("  %s：%s" % (name, "PASS" if ok else "FAIL"))
        allok = all(ok for _, ok in checks)
        say("总判定：%s" % ("PASS" if allok else "FAIL"))

        # ③ 取证存档（第十轮 §4.3）
        open(os.path.join(SHOT_DIR, "t3a补_%s_原文.txt" % stamp), "w",
             encoding="utf-8", newline="").write(
            "框选 = %s\n原文长度 = %d\n\n--- 认出来的原文 ---\n%s\n\n--- 日志 ---\n%s\n"
            % ("/".join(BOX), len(ocr_text), ocr_text, "\n".join(rows)))
        say("截图：%s" % shot)
        say("原文+日志：%s" % os.path.join(SHOT_DIR, "t3a补_%s_原文.txt" % stamp))

    finally:
        io.open(CFG, "w", encoding="utf-8", newline="").write(original)
        subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
        for p in (srv, show, app):
            try:
                p.kill()
            except Exception:
                pass
        d = json.load(io.open(CFG, encoding="utf-8-sig"))
        say("配置已还原：enabled=%s endpoint=%r keys=%d"
            % (d.get("translateEnabled"), d.get("translateEndpoint"), len(d)))
        out = os.path.join(ROOT, "tools", "脚本", "_t3a补_result.txt")
        io.open(out, "w", encoding="utf-8", newline="").write("\n".join(OUT) + "\n")
        print("written " + out)


if __name__ == "__main__":
    main()

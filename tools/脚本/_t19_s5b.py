# -*- coding: utf-8 -*-
"""第 18 轮 S5-3 重测：卡片原文区深色滚动条。

第一次跑 966 字英文，卡片长到 442×623 原文区没溢出 —— 没有滚动条，
像素扫描 #475569 命中 0 个（不是颜色坏了，是压根没滚动）。
这次把文本加到 ~2200 字，必然溢出。翻译撞 5 秒硬闸也无所谓：
本轮只看原文区滚动条，译文出不出来不影响。
"""
import importlib.util, io, os, subprocess, time

BASE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
spec = importlib.util.spec_from_file_location(
    "s4s5", os.path.join(BASE, "tools", "脚本", "_t18_s4s5.py"))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

m.EXE = os.path.join(BASE, "release", "v0.4.1", "TranslatorApp.exe")
OUT = os.path.join(BASE, "tools", "脚本", "_t19_s5b_result.txt")
say = m.say
LONG = (m.T_HUGE_EN + "\n\n") * 3


def main():
    original = io.open(m.CFG, encoding="utf-8-sig").read()
    src = None
    try:
        m.set_source(LONG)
        ps1 = os.path.join(m.TMP, "t19b_show.ps1")
        io.open(ps1, "w", encoding="utf-8-sig", newline="").write(
            m.SHOW_PS.replace("{txt}", m.SRC_TXT))
        src = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                                "-File", ps1],
                               cwd=BASE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(5.0)
        m.kill_app()
        m.setcfg(True, "", "zh-Hans")
        base = m.nlines()
        m.start_app()
        time.sleep(6.5)
        s = os.path.join(m.SHOT_DIR, "t19_s5_scroll2.png")
        m.drag()
        m.wait_final(base, delay=1.0, deadline=200)
        m.screenshot(s)
        say("")
        say("== S5-3（重测）约 2200 字英文：卡片原文区滚动条 ==")
        say("  文本长度 = %d 字" % len(LONG))
        say("  截图 %s" % s)
        m.show(m.rows_since(base), ("最终原文", "取词成功", "翻译：", "提示框已更新"))
    finally:
        if src is not None:
            try:
                src.kill()
            except Exception:
                pass
        m.kill_app()
        io.open(m.CFG, "w", encoding="utf-8", newline="").write(original)
        say("")
        say("配置已还原")
        io.open(OUT, "w", encoding="utf-8", newline="").write("\n".join(m.OUTL) + "\n")


if __name__ == "__main__":
    main()

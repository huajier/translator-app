# -*- coding: utf-8 -*-
"""第 18 轮 S5 回归三条（v0.4.1）。

S5-1 目标语言选「日语」→ 重启 → 仍是「日语」
S5-2 勾选翻译 → 托盘勾选态跟着变（有勾 / 无勾 两张菜单截图）
S5-3 900+ 字英文 → 卡片原文区滚动条仍是深色（无白箭头）

复用第 17 轮的 _t18_s4s5.py：只换 exe 版本和验收步骤，
显示窗口 / 框选 / 等终态 / 截图 全部沿用同一套量具，
免得「换一轮就换一套量法」导致数字不可比。
"""
import importlib.util, io, json, os, subprocess, time

BASE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
spec = importlib.util.spec_from_file_location(
    "s4s5", os.path.join(BASE, "tools", "脚本", "_t18_s4s5.py"))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

m.EXE = os.path.join(BASE, "release", "v0.4.1", "TranslatorApp.exe")
OUT = os.path.join(BASE, "tools", "脚本", "_t19_result.txt")
say = m.say


def main():
    original = io.open(m.CFG, encoding="utf-8-sig").read()
    src = None
    try:
        # ---------- S5-1 日语持久化 ----------
        m.kill_app()
        m.setcfg(True, "", "zh-Hans")
        m.start_app()
        time.sleep(6.5)
        say("")
        say("== S5-1 目标语言选日语 → 重启仍是日语 ==")
        say("  " + m.uia("lang-select", "日语")[:160])
        d = json.load(io.open(m.CFG, encoding="utf-8-sig"))
        say("  配置 translateTargetLang = %r" % d.get("translateTargetLang"))
        m.kill_app()
        m.start_app()
        time.sleep(6.5)
        say("  重启后 " + m.uia("read-main")[:160])

        # ---------- S5-2 勾选翻译 → 托盘勾选态跟着变 ----------
        say("")
        say("== S5-2 托盘勾选态 ==")
        d = json.load(io.open(m.CFG, encoding="utf-8-sig"))
        say("  当前 translateEnabled = %r" % d.get("translateEnabled"))
        p_on = os.path.join(m.SHOT_DIR, "t19_s5_tray_on.png")
        say("  " + m.uia("tray-shot", p_on)[:160])
        time.sleep(0.6)
        m.screenshot(p_on)
        m.uia("tray-close")
        time.sleep(0.6)

        say("  " + m.uia("tray-toggle")[:160])
        d = json.load(io.open(m.CFG, encoding="utf-8-sig"))
        say("  toggle 后 translateEnabled = %r" % d.get("translateEnabled"))
        say("  主窗口 " + m.uia("read-main")[:160])
        p_off = os.path.join(m.SHOT_DIR, "t19_s5_tray_off.png")
        say("  " + m.uia("tray-shot", p_off)[:160])
        time.sleep(0.6)
        m.screenshot(p_off)
        m.uia("tray-close")

        # ---------- S5-3 长英文 → 深色滚动条 ----------
        m.set_source(m.T_HUGE_EN)
        ps1 = os.path.join(m.TMP, "t19_show.ps1")
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
        s = os.path.join(m.SHOT_DIR, "t19_s5_scroll.png")
        m.drag()
        m.wait_final(base)
        m.screenshot(s)
        say("")
        say("== S5-3 900+ 字英文：卡片原文区滚动条 ==")
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

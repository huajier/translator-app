# -*- coding: utf-8 -*-
"""第16轮 P1-J2：不抢前台时，菜单能否「点外面关掉 / 悬停高亮 / 方向键选中」。

一张图对比一件事，条件逐个隔离：
  A 基线  —— 光标在 60,900（离菜单很远）弹菜单 → 应该没有任何高亮
  B 悬停  —— 光标移到菜单第二项 1700,45
  C 方向键 —— 光标挪回 60,900，再按两下 DOWN

再加一条 click-away 验「点外面还能关掉吗」。
结果写 tools/脚本/_r17_j2.txt。
"""
import io, os, subprocess, time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", os.environ.get("VER", "v2.2"), "TranslatorApp.exe")
UIA = os.path.join(ROOT, "tools", "脚本", "_t3b_uia.ps1")
SHOT = os.path.join(ROOT, "tools", "日志", "取证截图")
OUT = []


def say(s=""):
    OUT.append(str(s))
    try:
        print(str(s).encode("gbk", "replace").decode("gbk"))
    except Exception:
        print("[unprintable]")


def uia(step, value=""):
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", UIA,
                        "-Step", step, "-Value", value],
                       cwd=ROOT, capture_output=True, text=True, timeout=240,
                       errors="replace")
    for ln in ((r.stdout or "") + (r.stderr or "")).splitlines():
        if ln.strip():
            say("   " + ln.strip())
    return r.stdout or ""


def kill():
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(1.2)


def main():
    try:
        kill()
        subprocess.Popen([EXE], cwd=ROOT)
        time.sleep(5.0)
        say("== menu-tri（基线 / 悬停 / 方向键） ==")
        uia("menu-tri", "|".join([os.path.join(SHOT, os.environ.get("SHOTPFX","t17_j2_") + "A_base.png"),
                                  os.path.join(SHOT, os.environ.get("SHOTPFX","t17_j2_") + "B_hover.png"),
                                  os.path.join(SHOT, os.environ.get("SHOTPFX","t17_j2_") + "C_keys.png")]))
        uia("click-away", "300,700")
        uia("click-away", "300,760")
    finally:
        kill()
        io.open(os.path.join(ROOT, "tools", "脚本", os.environ.get("OUTTXT", "_r17_j2.txt")),
                "w", encoding="utf-8", newline="").write("\n".join(OUT) + "\n")


if __name__ == "__main__":
    main()

# -*- coding: utf-8 -*-
"""TimeStop：编译 → 打包 → 装机（一条龙）。

★ 关键经验（2026-09-29 实测踩坑）★
  `dotnet build` 在本机会**卡死在 NuGet restore**（无输出、日志空、DLL 不更新，
  与"MSBuild 节点残留"症状相同但根因不同）。现象：dotnet.exe 占 170MB+、3 分钟无输出。
  两个必须同时做的动作：
    1) `--no-restore`：`obj/project.assets.json` 已存在时**跳过 restore**（本次卡死的主因）；
    2) 把 TEMP/TMP/DOTNET_CLI_HOME/NUGET_PACKAGES 全部重定向到工作区内，
       规避沙箱对 `%TEMP%`、`%USERPROFILE%\\.nuget` 的拦截。
  另外**先用 `taskkill /F /IM dotnet.exe` 清残留进程**，否则旧进程锁住 obj/ 也会卡。

  可用 API 备忘：Godot 4 的 C# 绑定**没有** `Transform2D.Xform()`（Godot 3 的老名字），
  要写成 `Transform2D * Vector2`；`CanvasLayer` **没有** `GetCanvasTransform()`
  （只有 `CanvasItem.GetCanvasTransform()`）。

用法：python build_and_install.py [--install]
      不带 --install 只编译；带则继续打包 + 装机 + 清解包缓存。
"""
import os
import shutil
import subprocess
import sys

ROOT = r"C:\Users\txgcs\WorkBuddy\zjb"
DOTNET = os.path.join(ROOT, "tools", "dotnet9", "dotnet.exe")
BASE = os.path.join(ROOT, "mod", "TimeStop")
SRC = os.path.join(BASE, "runtime_src")
PY = r"C:\Users\txgcs\.workbuddy\binaries\python\versions\3.13.12\python.exe"
LOG = os.path.join(SRC, "build_last.log")

HOME = os.path.join(ROOT, "tools", "dotnet_home")
TMPD = os.path.join(HOME, "tmp")
NUGET = os.path.join(ROOT, "tools", "nuget")

UD = r"C:\Users\txgcs\AppData\Roaming\Godot\app_userdata\植物大战僵尸杂交版"
MODS = os.path.join(UD, "Mods")
CACHE = os.path.join(UD, "ModsCache")

lines = []


def log(s=""):
    lines.append(str(s))


def run_compile():
    for d in (HOME, TMPD, NUGET):
        os.makedirs(d, exist_ok=True)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.join(ROOT, "tools", "dotnet9")
    env["DOTNET_CLI_HOME"] = HOME
    env["TEMP"] = TMPD
    env["TMP"] = TMPD
    env["TMPDIR"] = TMPD
    env["NUGET_PACKAGES"] = NUGET
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["MSBUILDDISABLENODEREUSE"] = "1"

    cmd = [DOTNET, "build", "-c", "Release", "--no-restore",
           "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false", "-v:q", "-nologo"]
    p = subprocess.run(cmd, cwd=SRC, env=env, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=300)
    log("[compile] RC=%d" % p.returncode)
    if p.stdout:
        log(p.stdout.strip())
    if p.stderr:
        log(p.stderr.strip())
    return p.returncode == 0


def main():
    # 清残留 dotnet（否则锁 obj/ 导致卡死）
    try:
        subprocess.run(["taskkill", "/F", "/IM", "dotnet.exe"],
                       capture_output=True, timeout=30)
    except Exception:
        pass

    if not run_compile():
        log("编译失败，终止。")
        return 1
    log("[compile] OK")

    if "--install" not in sys.argv:
        return 0

    p = subprocess.run([PY, os.path.join(BASE, "build_pmod.py")], capture_output=True,
                       text=True, encoding="utf-8", errors="replace", timeout=180)
    log("[package] RC=%d" % p.returncode)
    log((p.stdout or "").strip())

    dist = os.path.join(ROOT, "mod", "dist", "TimeStop.pmod")
    if p.returncode != 0 or not os.path.isfile(dist):
        log("打包失败，终止。")
        return 1

    dst = os.path.join(MODS, "TimeStop.pmod")
    shutil.copyfile(dist, dst)
    log("[install] %s (%d B)" % (dst, os.path.getsize(dst)))

    if os.path.isdir(CACHE):
        import time
        ts = time.strftime("%H%M%S")
        for name in os.listdir(CACHE):
            # 只处理"干净"的缓存目录名（不带 .bak），避免把历史备份再备份一层
            if name == "TimeStop":
                src = os.path.join(CACHE, name)
                dstc = os.path.join(CACHE, name + ".bak_" + ts)
                i = 1
                while os.path.exists(dstc):      # 撞名就加序号（首次踩到 WinError 183）
                    i += 1
                    dstc = os.path.join(CACHE, "%s.bak_%s_%d" % (name, ts, i))
                # ★ 用系统 `move`（同盘走 MoveFileEx，瞬间完成）；python 的
                #   `os.rename` 在大目录上会挂起（目录被扫描/句柄占用）。
                try:
                    # `cmd /c move` 的输出是**系统 OEM 编码**（中文 Windows = GBK），
                    # 用 utf-8 解会在 reader 线程抛 UnicodeDecodeError（不致命但很脏）。
                    r = subprocess.run(["cmd", "/c", "move", src, dstc],
                                       capture_output=True, errors="replace",
                                       encoding="gbk", timeout=60)
                    log("[cache] move rc=%d %s" % (
                        r.returncode,
                        ((r.stdout or "") + (r.stderr or "")).strip().replace("\n", " ")))
                except Exception as ex:
                    log("[cache] move 失败（已忽略）：%r" % ex)
    return 0


if __name__ == "__main__":
    rc = 0
    try:
        rc = main()
    except Exception as e:
        import traceback
        log("EXC: %r" % e)
        log(traceback.format_exc())
        rc = 1
    with open(LOG, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print("\n".join(lines))
    sys.exit(rc)

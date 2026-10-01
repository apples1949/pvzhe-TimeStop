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
# --- 本机路径适配（自动注入；换机器只改 mods/_modenv.py）---
import os as _os
import sys as _sys

_MROOT = _os.path.dirname(_os.path.abspath(__file__))
while not _os.path.isfile(_os.path.join(_MROOT, "_modenv.py")):
    _p = _os.path.dirname(_MROOT)
    if _p == _MROOT:
        break
    _MROOT = _p
if _MROOT not in _sys.path:
    _sys.path.insert(0, _MROOT)
from _modenv import (  # noqa: E402
    MODS_ROOT, WORKSPACE, REF_DIR, GAME, UNPACK, UD, MODS_DIR, CACHE,
    PY, DOTNET, DOTNET_ROOT, require_ref_dir, ensure_mods, describe,
)
# --- 适配块结束 ---

# 原为作者的 WorkBuddy 工程根；现在指向本工作区的 mods/ 根
ROOT = MODS_ROOT
DOTNET = DOTNET
BASE = _os.path.dirname(_os.path.abspath(__file__))   # ← 本 Mod 自己的目录
SRC = os.path.join(BASE, "runtime_src")
PY = PY
LOG = os.path.join(SRC, "build_last.log")

HOME = os.path.join(WORKSPACE, ".cache", "dotnet_home")
TMPD = os.path.join(HOME, "tmp")
NUGET = os.path.join(WORKSPACE, ".cache", "nuget")

UD = UD
MODS = os.path.join(UD, "Mods")
CACHE = os.path.join(UD, "ModsCache")

lines = []


def log(s=""):
    lines.append(str(s))


def run_compile():
    for d in (HOME, TMPD, NUGET):
        os.makedirs(d, exist_ok=True)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = DOTNET_ROOT   # ← mods/_modenv.py 从 dotnet.exe 反推
    env["DOTNET_CLI_HOME"] = HOME
    env["TEMP"] = TMPD
    env["TMP"] = TMPD
    env["TMPDIR"] = TMPD
    env["NUGET_PACKAGES"] = NUGET
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["MSBUILDDISABLENODEREUSE"] = "1"

    # 本机适配（原命令写死为 `--no-restore`，且不带工程名/引用目录）：
    #   · 必须显式传 -p:GodotRefDir=...，否则 csproj 里被清空的默认值会让
    #     GodotSharp / PlantsVsZombies 引用解析不到，报一屏 CS0246。
    #   · 去掉 --no-restore：本机 NUGET_PACKAGES 被重定向到工作区 .cache，
    #     该目录是空的，不先 restore 就没有 project.assets.json。
    # 工程文件名是「本 Mod 的名字」，不是子目录名 runtime_src。
    _csproj = next((f for f in sorted(os.listdir(SRC)) if f.endswith(".csproj")), None)
    if not _csproj:
        log("[compile] %s 下找不到 .csproj" % SRC)
        return False
    cmd = [DOTNET, "build", os.path.join(SRC, _csproj),
           "-c", "Release",
           "-p:GodotRefDir=" + require_ref_dir(),
           "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false", "-v:q", "-nologo"]
    if not os.path.isfile(os.path.join(SRC, "obj", "project.assets.json")):
        log("[compile] 无 obj/project.assets.json，先 restore")
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

    dist = os.path.join(BASE, "dist", "TimeStop.pmod")
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

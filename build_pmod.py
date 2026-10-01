# -*- coding: utf-8 -*-
"""打包 TimeStop.pmod（zip + 根 mod.json）。

硬护栏（照技能文档）：
  · mod.json 必须在【根】且【唯一】
  · Runtime/ 下只允许 ModAssembly.dll（多一个可执行文件 → 整包被拒）
  · resources 列表必须与实际包内文件一致
"""
import json
import os
import sys
import zipfile
import hashlib
import shutil
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
    PY, DOTNET, require_ref_dir, ensure_mods, describe,
)
# --- 适配块结束 ---

BASE = _os.path.dirname(_os.path.abspath(__file__))   # ← 本 Mod 自己的目录
RUNTIME = os.path.join(BASE, "Runtime")
BINSRC = os.path.join(BASE, "runtime_src", "bin", "Release", "JTYTimeStop.dll")
OUT_DIR = _os.path.join(BASE, "dist")   # ← 产物落在本 Mod 的 dist/
OUT = os.path.join(OUT_DIR, "TimeStop.pmod")


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if not os.path.isfile(os.path.join(BASE, "mod.json")):
        print("缺少 mod.json")
        return 1
    if not os.path.isfile(os.path.join(RUNTIME, "ModAssembly.dll")):
        print("缺少 Runtime/ModAssembly.dll —— 先跑 dotnet build")
        return 1

    # 护栏 0：编译产物必须与 Runtime/ModAssembly.dll 一致
    runtime_dll = os.path.join(RUNTIME, "ModAssembly.dll")
    if os.path.isfile(BINSRC):
        m_new, m_old = md5(BINSRC), md5(runtime_dll)
        if m_new != m_old:
            print("[护栏0] Runtime/ModAssembly.dll 与最新编译产物不一致 → 自动用编译产物覆盖")
            print("       编译产物 bin/Release:", m_new)
            print("       Runtime/ 原值      :", m_old)
            shutil.copyfile(BINSRC, runtime_dll)
            print("       已更新 Runtime/ModAssembly.dll ->", md5(runtime_dll))
    else:
        print("[护栏0·提示] 未找到 runtime_src/bin/Release/JTYTimeStop.dll，"
              "直接使用现有 Runtime/ModAssembly.dll（无法核对新旧）")

    man = json.load(open(os.path.join(BASE, "mod.json"), encoding="utf-8"))

    # 护栏 1：Runtime 目录只许有 ModAssembly.dll
    names = sorted(os.listdir(RUNTIME))
    bad = [n for n in names if n != "ModAssembly.dll"]
    if bad:
        print("Runtime/ 下有非法文件（会被整包拒收）:", bad)
        return 2

    # 护栏 2：resources 必须与实际内容一致
    actual = sorted(["Runtime/ModAssembly.dll"], key=str.lower)
    declared = sorted(man.get("resources", []), key=str.lower)
    if actual != declared:
        print("resources 不一致\n  实际:", actual, "\n  声明:", declared)
        return 3

    # 护栏 3：runtime 三件套
    if man.get("runtimeAssembly") != "Runtime/ModAssembly.dll":
        print("runtimeAssembly 必须恰好是字面量 Runtime/ModAssembly.dll")
        return 4
    if man.get("runtimeApiVersion") != 1:
        print("runtimeApiVersion 必须恰好 1")
        return 5

    os.makedirs(OUT_DIR, exist_ok=True)
    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(os.path.join(BASE, "mod.json"), "mod.json")
        z.write(os.path.join(RUNTIME, "ModAssembly.dll"), "Runtime/ModAssembly.dll")

    size = os.path.getsize(OUT)
    with zipfile.ZipFile(OUT) as z:
        nl = z.namelist()
    print("OK ->", OUT)
    print("  大小:", size, "字节")
    print("  内容:", nl)
    print("  包内 DLL md5:", md5(runtime_dll), "（应与 bin/Release 及装机包一致）")
    if nl.count("mod.json") != 1 or nl[0] != "mod.json":
        print("  ⚠️ mod.json 不在根或不止一个")
        return 6
    return 0


if __name__ == "__main__":
    sys.exit(main())

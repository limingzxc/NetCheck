#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
NetCheck (C#/WPF) 发布脚本
==========================
生成两种可分发版本，差异很大，按需要选：

  独立版 (self-contained)
      .NET 运行时打包进去，目标机器**无需安装任何东西**，双击即用。
      体积约 60~80 MB。

  精简版 (framework-dependent)
      只打包程序本身，体积约 2~3 MB。
      但目标机器必须装 .NET 8 桌面运行时，否则双击无反应。

用法：
    python build.py              # 两种都出
    python build.py --standalone # 只出独立版
    python build.py --lite       # 只出精简版

用 Python 而不是 .bat/.ps1 编写，避免 Windows 批处理在中文代码页下的编码问题。
"""

import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = "NetCheck.csproj"
APP = "NetCheck"

CONFIGS = {
    "standalone": {
        "dir": "NetCheck-standalone",
        "label": "独立版（免装运行时）",
        "args": [
            "--self-contained", "true",
            # 压缩只在自包含模式下可用，精简版传这个会报 NETSDK1176
            "-p:EnableCompressionInSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
        ],
    },
    "lite": {
        "dir": "NetCheck-lite",
        "label": "精简版（需装 .NET 8 桌面运行时）",
        "args": [
            "--self-contained", "false",
        ],
    },
}


def run(args):
    print("  $ " + " ".join(args))
    return subprocess.call(args, cwd=HERE)


def dir_size_mb(path):
    total = 0
    for dp, _, fs in os.walk(path):
        for f in fs:
            try:
                total += os.path.getsize(os.path.join(dp, f))
            except OSError:
                pass
    return total / 1_048_576


def publish(key, cfg):
    out = os.path.join(HERE, "publish", cfg["dir"])
    print()
    print("-" * 58)
    print("  发布 %s" % cfg["label"])
    print("-" * 58)

    args = [
        "dotnet", "publish", PROJECT,
        "-c", "Release",
        "-r", "win-x64",
        "-p:PublishSingleFile=true",
        "-o", out,
    ] + cfg["args"]

    if run(args) != 0:
        print("\n  [错误] 发布失败")
        return None

    exe = os.path.join(out, APP + ".exe")
    if not os.path.exists(exe):
        print("\n  [警告] 未找到 %s.exe" % APP)
        return None

    exe_mb = os.path.getsize(exe) / 1_048_576
    total_mb = dir_size_mb(out)
    print()
    print("  完成：%s" % out)
    print("    %s.exe        %.1f MB" % (APP, exe_mb))
    print("    整个文件夹     %.1f MB" % total_mb)
    return {"dir": out, "exe_mb": exe_mb, "total_mb": total_mb, "label": cfg["label"]}


def main():
    print("=" * 58)
    print("  %s (C#/WPF) 发布" % APP)
    print("=" * 58)

    # 检查 SDK
    try:
        r = subprocess.run(["dotnet", "--version"], cwd=HERE,
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        ver = r.stdout.decode("utf-8", "replace").strip()
        print("\n[1/3] .NET SDK %s" % ver)
    except Exception as e:
        print("\n[错误] 未找到 dotnet 命令：%s" % e)
        print("       请安装 .NET SDK：https://dotnet.microsoft.com/download")
        return 1

    # 清理
    #
    # 只删 publish/。以前这里连 bin/ 和 obj/ 一起 rmtree，踩过两个坑：
    #   1. 删掉 bin/ 之后，e2e.ps1 和截图脚本按 bin/Release/NetCheck.exe 找不到程序，
    #      报出 rect=0,0,0x0 这种看不懂的错误，排查半天才发现是构建脚本自己删的；
    #   2. obj/ 动辄几十个文件，某些带删除保护的环境（一次删除超过阈值要人工确认）
    #      会直接把进程干掉，留下删了一半的 obj/。
    # 其实这两个目录都是 MSBuild 自己的中间产物，dotnet publish 会按需覆盖和重建，
    # 根本不需要我们先动手。所以现在只清理真正会残留旧程序的 publish/。
    print("\n[2/3] 清理旧产物 …")
    p = os.path.join(HERE, "publish")
    if os.path.isdir(p):
        try:
            shutil.rmtree(p)
            print("      完成")
        except Exception as e:
            # 清理失败不再中止构建：publish 输出目录本来就会被覆盖写，
            # 最坏情况是留下几个上一版的多余文件，不值得让整个发布失败。
            print("      跳过（%s）" % e)
            print("      不影响发布，publish/ 里的旧文件会被逐个覆盖。")
    else:
        print("      无需清理")

    # 发布
    print("\n[3/3] 发布中（首次约需 1-2 分钟）…")
    want = [k for k in ("standalone", "lite")
            if "--%s" % k in sys.argv or len([a for a in sys.argv[1:] if a.startswith("--")]) == 0]

    results = []
    for key in want:
        r = publish(key, CONFIGS[key])
        if r:
            results.append(r)

    print()
    print("=" * 58)
    print("  发布结果")
    print("=" * 58)
    for r in results:
        print("  %-28s %6.1f MB" % (r["label"], r["total_mb"]))
    if results:
        print()
        print("  分发方式：把对应文件夹压缩后发出去，对方解压双击 %s.exe。" % APP)
        print("  不确定对方有没有 .NET 运行时 → 用独立版。")
    print("=" * 58)
    return 0


if __name__ == "__main__":
    sys.exit(main())

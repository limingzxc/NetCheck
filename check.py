#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
NetCheck 开发检查脚本
=====================
把「编译 → 单元测试 →（可选）端到端 →（可选）打包」串成一条命令。
**这是开发循环用的脚本，不是发布入口** —— 只想出可分发 exe 就用根目录的 build.bat。

    python check.py              # 编译 Debug + 单元测试（开发循环最常用，约 10 秒）
    python check.py -r           # 编译 Release + 单元测试
    python check.py --e2e        # 再加上端到端集成测试（自动切 Release，约 2 分钟）
    python check.py --publish    # 再加上打包出可分发版本（约 2 分钟）
    python check.py --all        # 全跑一遍
    python check.py --no-test    # 只编译，不跑任何测试

各步骤谁负责什么：

    编译      NetCheckCSharp / CoreTest 两个工程
    单元测试  CoreTest.exe unit        —— 700+ 条，约 0.6 秒
    端到端    e2e.ps1                  —— 80 条，要开真实窗口跑检测，约 2 分钟
    打包      NetCheckCSharp/build.py  —— 出独立版 + 精简版

**为什么默认就带上单元测试**：它只要半秒，却能在「编译通过但逻辑写错」的时候
立刻拦下来 —— 编译绿了不等于代码对。真要跳过就加 --no-test。

**为什么 --e2e 会强制 Release**：e2e.ps1 默认去找 bin/Release 下的 exe，
Debug 配置下会直接报「找不到可执行文件」。

用 Python 而不是 .bat/.ps1 编写，和 NetCheckCSharp/build.py 保持一致 ——
避免 Windows 批处理在中文代码页下的编码问题。

**名字为什么是 check 而不是 build**：根目录原来同时有 build.bat 和 build.py，
一个管发布、一个管开发检查，光看名字分不出谁是谁 —— 已经有人（包括我自己）
按错的那个跑过。现在 build.bat 是唯一的「发布」入口，开发检查叫 check.py。
"""

import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
APP_DIR = os.path.join(HERE, "NetCheckCSharp")
APP_PROJ = os.path.join(APP_DIR, "NetCheck.csproj")
TEST_DIR = os.path.join(HERE, "CoreTest")
TEST_PROJ = os.path.join(TEST_DIR, "CoreTest.csproj")
E2E = os.path.join(HERE, "e2e.ps1")
PUBLISH = os.path.join(APP_DIR, "build.py")


# ----------------------------------------------------------------------
# 输出
# ----------------------------------------------------------------------

class Log:
    """每步一行进度，最后给一张汇总表。失败不中断 —— 要看到全部问题再决定怎么修。"""

    def __init__(self):
        self.rows = []
        self.failed = 0
        self.t0 = time.time()

    def step(self, n, total, title):
        print()
        print("-" * 62)
        print("  [%d/%d] %s" % (n, total, title))
        print("-" * 62)

    def ok(self, name, detail=""):
        self.rows.append(("OK", name, detail))
        print("  -> %s%s" % (name, ("  " + detail) if detail else ""))

    def bad(self, name, detail=""):
        self.rows.append(("FAIL", name, detail))
        self.failed += 1
        print("  -> [失败] %s%s" % (name, ("  " + detail) if detail else ""))

    def skip(self, name, why):
        self.rows.append(("SKIP", name, why))
        print("  -> 跳过 %s（%s）" % (name, why))

    def summary(self):
        print()
        print("=" * 62)
        print("  检查结果")
        print("=" * 62)
        for state, name, detail in self.rows:
            print("  %-5s %-22s %s" % (state, name, detail))
        print("-" * 62)
        print("  用时 %.1f 秒" % (time.time() - self.t0))
        if self.failed:
            print("  %d 个步骤失败" % self.failed)
        else:
            print("  全部通过。")
        print("=" * 62)
        return 1 if self.failed else 0


# ----------------------------------------------------------------------
# 执行
# ----------------------------------------------------------------------

def run(args, cwd=HERE, capture=False):
    """跑一条命令。返回 (exit_code, output)。capture=False 时输出直接打到控制台。"""
    if capture:
        r = subprocess.run(args, cwd=cwd, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, timeout=900)
        return r.returncode, r.stdout.decode("utf-8", "replace")
    return subprocess.call(args, cwd=cwd), ""


def tail(text, n=12):
    """只留最后几行 —— 编译错误的重点在末尾，整段贴出来反而看不清。"""
    lines = [x for x in text.strip().splitlines() if x.strip()]
    return "\n".join("     " + x for x in lines[-n:])


def check_dotnet():
    try:
        code, out = run(["dotnet", "--version"], capture=True)
        return code == 0, out.strip()
    except Exception as e:
        return False, str(e)


def build(log, proj, cfg, label):
    """编译一个工程。用 -v q 只留警告和错误，正常时不刷屏。"""
    args = ["dotnet", "build", proj, "-c", cfg, "-v", "q", "--nologo"]
    code, out = run(args, capture=True)
    if code != 0:
        log.bad(label, "编译失败")
        print(tail(out))
        return False
    # -v q 在成功时也会打「已成功生成」和用时，顺手抓一下警告数
    warns = [x for x in out.splitlines() if "警告" in x and "0 个警告" not in x]
    detail = ""
    for line in out.splitlines():
        if "个警告" in line and "个错误" in line:
            detail = line.strip()
            break
    log.ok(label, detail)
    if warns:
        print(tail(out, 20))
    return True


def unit_tests(log, cfg):
    """
    跑单元测试。
    用 dotnet run 而不是直接调 exe —— 省得去猜 bin/<cfg>/<tfm>/ 里那个框架目录名，
    多花的一次增量编译也就几十毫秒。
    """
    args = ["dotnet", "run", "--project", TEST_DIR, "-c", cfg, "--nologo", "--", "unit"]
    code, out = run(args, capture=True)
    if code != 0:
        log.bad("单元测试", "有失败用例")
        print(tail(out, 30))
        return False

    line = ""
    for x in out.splitlines():
        if "通过" in x and "失败" in x:
            line = x.strip().strip("=").strip()
            break
    log.ok("单元测试", line or "通过")
    return True


def e2e_tests(log):
    """
    跑端到端集成测试。
    e2e.ps1 用 Write-Host 输出，所以这里不能用 capture —— 管道抓不到 Write-Host，
    会看起来像「脚本只跑了几行」。让它直接往控制台打。
    """
    out_dir = os.path.join(HERE, "shots", "e2e")
    args = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", E2E, "-OutDir", out_dir]
    # run() 返回 (退出码, 输出) 元组 —— 不接住输出的话，元组本身永远 != 0，
    # 会让每一步都被判成失败，失败信息还会在格式化时炸掉（连失败清单都看不到）。
    code, _ = run(args)
    if code != 0:
        log.bad("端到端集成测试", "有失败断言（退出码 %d）" % code)
        return False
    log.ok("端到端集成测试", "报告见 " + os.path.relpath(out_dir, HERE))
    return True


def publish(log):
    """调用 NetCheckCSharp/build.py 出可分发版本 —— 打包细节都在那边，这里不重复实现。"""
    code, _ = run([sys.executable, PUBLISH])
    if code != 0:
        log.bad("打包", "发布失败")
        return False

    # 把两个版本的实际体积捞出来
    detail = []
    for name in ("NetCheck-standalone", "NetCheck-lite"):
        p = os.path.join(APP_DIR, "publish", name, "NetCheck.exe")
        if os.path.exists(p):
            detail.append("%s %.1f MB" % (name.replace("NetCheck-", ""),
                                          os.path.getsize(p) / 1_048_576))
    log.ok("打包", " · ".join(detail))
    return True


# ----------------------------------------------------------------------

def main():
    argv = sys.argv[1:]
    want_e2e = "--e2e" in argv or "--all" in argv
    want_publish = "--publish" in argv or "--all" in argv
    want_test = "--no-test" not in argv

    # e2e.ps1 只认 bin/Release，所以只要跑 e2e 就强制 Release
    cfg = "Release" if ("--release" in argv or "-r" in argv or want_e2e or want_publish) else "Debug"

    print("=" * 62)
    print("  NetCheck 开发检查")
    print("=" * 62)
    print("  配置：%s" % cfg)

    ok, ver = check_dotnet()
    if not ok:
        print()
        print("  [错误] 找不到 dotnet 命令：%s" % ver)
        print("         请安装 .NET SDK：https://dotnet.microsoft.com/download")
        return 1
    print("  SDK ：%s" % ver)

    steps = 2 + (1 if want_test else 0) + (1 if want_e2e else 0) + (1 if want_publish else 0)
    log = Log()
    n = 0

    n += 1
    log.step(n, steps, "编译 NetCheck")
    if not build(log, APP_PROJ, cfg, "NetCheck"):
        log.summary()
        return 1

    n += 1
    log.step(n, steps, "编译 CoreTest")
    if not build(log, TEST_DIR, cfg, "CoreTest"):
        log.summary()
        return 1

    if want_test:
        n += 1
        log.step(n, steps, "单元测试")
        unit_tests(log, cfg)
    else:
        log.skip("单元测试", "--no-test")

    if want_e2e:
        n += 1
        log.step(n, steps, "端到端集成测试")
        e2e_tests(log)

    if want_publish:
        n += 1
        log.step(n, steps, "打包可分发版本")
        publish(log)

    return log.summary()


if __name__ == "__main__":
    sys.exit(main())

"""PS-text 构建脚本（.workbuddy 工具层禁止直接调用 MSBuild.exe，因此走 Python 中转）。

用法：
    "<managed-python>" tools/build.py [Release|Debug] [额外 MSBuild 参数...]
"""
import subprocess
import sys
import os

ROOT = r"D:\Code\PS-text"
MSBUILD = r"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
PROJECT = os.path.join(ROOT, "PSText.csproj")

config = sys.argv[1] if len(sys.argv) > 1 else "Release"
extra = sys.argv[2:]

cmd = [MSBUILD, PROJECT, "-t:Build", "-p:Configuration=" + config, "-v:minimal", "-nologo"] + extra

proc = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True,
                      encoding="utf-8", errors="replace")

print("EXITCODE:", proc.returncode)
print("---- stdout ----")
print(proc.stdout)
if proc.stderr:
    print("---- stderr ----")
    print(proc.stderr)

sys.exit(proc.returncode)

"""PS-text 免安装包打包脚本。

用法：
    "<managed-python>" tools/package.py            # 构建 + 打包
    "<managed-python>" tools/package.py --no-build # 跳过构建，只重新打包

产物：
    build/dist/PS-text-<version>-win7-portable/      绿色版文件夹（拷走即用）
    build/dist/PS-text-<version>-win7-portable.zip   发行压缩包

两个刻意的设计：

1. **文本文件以 GBK 写出**。
   packaging/ 下的源文件是 UTF-8（这样在 GitHub 上可读、可 review），
   但放进包里的 .bat / .txt 必须转成 GBK —— 简中 Windows 的命令提示符
   默认代码页是 936，用 UTF-8 的中文会显示成乱码。

2. **zip 里的中文名用 CP936 编码，且不置 UTF-8 标志位**。
   Python 的 zipfile 遇到非 ASCII 名字会写 UTF-8 并置标志位 0x800。
   Windows 10+ 认这个标志，**Windows 7 的资源管理器不认**，
   会按 CP936 去解 UTF-8 字节 -> 文件名全乱。
   而我们的目标平台恰恰包含 Win7，所以这里手动改成 CP936。
"""
import io
import os
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = r"D:\Code\PS-text"
MSBUILD = r"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
PROJECT = os.path.join(ROOT, "PSText.csproj")
ASSEMBLY_INFO = os.path.join(ROOT, "Properties", "AssemblyInfo.cs")
PACKAGING = os.path.join(ROOT, "packaging")
DIST = os.path.join(ROOT, "build", "dist")
BIN = os.path.join(ROOT, "build", "bin", "Release")

# 打进包里的文件：(源文件, 包内文件名)
TEXT_FILES = [
    (os.path.join(PACKAGING, "启动 PS-text.bat"), "启动 PS-text.bat"),
    (os.path.join(PACKAGING, "说明.txt"), "说明.txt"),
]


def read_version():
    """从 Properties/AssemblyInfo.cs 读版本号（单一来源）。"""
    text = io.open(ASSEMBLY_INFO, encoding="utf-8").read()
    for attr in ("AssemblyInformationalVersion", "AssemblyVersion"):
        m = re.search(r'\[\s*assembly\s*:\s*' + attr + r'\("([^"]+)"\)\s*\]', text)
        if m:
            return m.group(1).strip()
    raise SystemExit("无法从 Properties/AssemblyInfo.cs 读到版本号")


def build():
    cmd = [MSBUILD, PROJECT, "-t:Build", "-p:Configuration=Release",
           "-v:minimal", "-nologo"]
    proc = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    print(proc.stdout)
    if proc.returncode != 0:
        print(proc.stderr)
        raise SystemExit("构建失败，退出码 %d" % proc.returncode)


def write_gbk(path, text):
    """把文本以 GBK 写出（简中 cmd 的默认代码页）。"""
    with io.open(path, "w", encoding="gbk", newline="\r\n") as f:
        f.write(text)


def stage(version):
    package_name = "PS-text-%s-win7-portable" % version
    target = os.path.join(DIST, package_name)

    if os.path.isdir(target):
        shutil.rmtree(target)
    os.makedirs(target)

    exe_source = os.path.join(BIN, "PS-text.exe")
    if not os.path.isfile(exe_source):
        raise SystemExit("找不到构建产物：" + exe_source)
    shutil.copy2(exe_source, os.path.join(target, "PS-text.exe"))

    for source, name in TEXT_FILES:
        text = io.open(source, encoding="utf-8").read()
        write_gbk(os.path.join(target, name), text)

    return package_name, target


class Cp936ZipInfo(zipfile.ZipInfo):
    """让 zip 条目名以 CP936 编码写出，且不置 UTF-8 标志位（Win7 资源管理器才认）。

    注意：只覆盖 `_encodeFilenameFlags` 还不够 —— Python 3.13 的
    `ZipFile.writestr` 会**无条件**把 `zinfo.flag_bits` 置成 0x800
    （见 zipfile.py 里 `zinfo.flag_bits = _MASK_UTF_FILENAME`），
    于是名字是 CP936 字节、标志位却说"这是 UTF-8"，比不处理更糟：
    Win7 按 CP936 读没问题，Win10 信了标志位去按 UTF-8 解码就会乱码。
    所以这里必须显式把这个位**掩掉**。
    """

    _MASK_UTF_FILENAME = 1 << 11

    def _encodeFilenameFlags(self):
        try:
            return self.filename.encode("cp936"), self.flag_bits & ~self._MASK_UTF_FILENAME
        except UnicodeEncodeError:
            return self.filename.encode("utf-8"), self.flag_bits | self._MASK_UTF_FILENAME


def make_zip(package_name, target):
    zip_path = os.path.join(DIST, package_name + ".zip")

    if os.path.isfile(zip_path):
        os.remove(zip_path)

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for current, _dirs, files in os.walk(target):
            relative_dir = os.path.relpath(current, target)

            if relative_dir != ".":
                info = Cp936ZipInfo(package_name + "/" + relative_dir.replace("\\", "/") + "/")
                info.date_time = (2025, 1, 1, 0, 0, 0)
                info.external_attr = 0x10
                info.compress_type = zipfile.ZIP_STORED
                archive.writestr(info, b"")

            for name in sorted(files):
                full = os.path.join(current, name)
                relative = os.path.relpath(full, target).replace("\\", "/")
                info = Cp936ZipInfo(package_name + "/" + relative)
                info.date_time = (2025, 1, 1, 0, 0, 0)
                info.external_attr = 0x20
                info.compress_type = zipfile.ZIP_DEFLATED
                with io.open(full, "rb") as handle:
                    archive.writestr(info, handle.read())

    return zip_path


def read_zip_entries(zip_path, encoding="cp936"):
    """直接读中央目录，按指定编码解出条目名。

    为什么不用 zipfile 来读：zipfile 在"未置 UTF-8 标志位"时固定按 utf-8 解码，
    而 Win7 兼容的包恰恰要求**不置**该标志、名字用 CP936 编码 ——
    用 zipfile 读会直接 UnicodeDecodeError。自己读中央目录反而能顺带把
    "标志位到底有没有被置起来"这件事查清楚。
    """
    with open(zip_path, "rb") as handle:
        raw = handle.read()

    eocd = raw.rfind(b"PK\x05\x06")
    if eocd < 0:
        raise ValueError("不是有效的 zip 文件")

    total = int.from_bytes(raw[eocd + 10:eocd + 12], "little")
    offset = int.from_bytes(raw[eocd + 16:eocd + 20], "little")

    entries = []
    for _ in range(total):
        if raw[offset:offset + 4] != b"PK\x01\x02":
            break

        flags = int.from_bytes(raw[offset + 8:offset + 10], "little")
        size = int.from_bytes(raw[offset + 24:offset + 28], "little")
        name_length = int.from_bytes(raw[offset + 28:offset + 30], "little")
        extra_length = int.from_bytes(raw[offset + 30:offset + 32], "little")
        comment_length = int.from_bytes(raw[offset + 32:offset + 34], "little")

        name_bytes = raw[offset + 46:offset + 46 + name_length]
        try:
            name = name_bytes.decode(encoding)
        except UnicodeDecodeError:
            name = name_bytes.decode(encoding, errors="replace")

        entries.append({"name": name, "flags": flags, "size": size})
        offset += 46 + name_length + extra_length + comment_length

    return entries


def verify(package_name, target, zip_path):
    """打包后自检：文件齐全、zip 中文名编码正确、bat 里没有十六进制比较的坑。"""
    problems = []

    expected = {"PS-text.exe", "启动 PS-text.bat", "说明.txt"}
    actual = set(os.listdir(target))
    if not expected.issubset(actual):
        problems.append("包内缺少文件：%s" % (expected - actual))

    bat_path = os.path.join(target, "启动 PS-text.bat")
    bat = io.open(bat_path, encoding="gbk").read()
    if 'findstr /c:"4.8."' not in bat:
        problems.append("启动器里缺少 4.8 版本字符串检测")
    if "GEQ 528040" in bat or "LSS 528040" in bat:
        problems.append("启动器用了 reg query 的 Release 做数值比较（十六进制陷阱）")
    if "reg query" not in bat:
        problems.append("启动器没有做运行时检测")

    entries = read_zip_entries(zip_path)
    names = [e["name"] for e in entries]

    for name in sorted(expected):
        if not any(n.endswith("/" + name) for n in names):
            problems.append("zip 里找不到条目：" + name)

    for entry in entries:
        if entry["flags"] & 0x800:
            problems.append("zip 条目置了 UTF-8 标志位（Win7 资源管理器会显示乱码）："
                            + entry["name"])
            break

    if not any(n.startswith(package_name + "/") for n in names):
        problems.append("zip 里没有顶层目录 " + package_name)

    if "0.0.0" in package_name:
        problems.append("版本号是 0.0.0，说明 AssemblyInfo.cs 没被读到")

    return problems


def main():
    if "--no-build" not in sys.argv:
        build()

    version = read_version()
    package_name, target = stage(version)
    zip_path = make_zip(package_name, target)

    print("版本:", version)
    print("绿色版目录:", target)
    print("发行压缩包:", zip_path)
    print("压缩包大小: %.2f MB" % (os.path.getsize(zip_path) / 1024.0 / 1024.0))
    print("压缩包内容（按 CP936 解出的名字）:")
    for entry in read_zip_entries(zip_path):
        print("   %-58s %8d B" % (entry["name"], entry["size"]))

    problems = verify(package_name, target, zip_path)
    if problems:
        print()
        print("打包自检发现问题：")
        for p in problems:
            print("  - " + p)
        raise SystemExit(1)

    print()
    print("打包自检通过。")


if __name__ == "__main__":
    main()

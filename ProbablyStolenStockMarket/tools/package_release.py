#!/usr/bin/env python3
"""打包可直接解压到游戏目录的股市 Mod 发布文件，并校验全部内容。"""

from __future__ import annotations

import argparse
import hashlib
import re
import subprocess
import sys
import zipfile
from pathlib import Path, PurePosixPath


PROJECT = Path(__file__).resolve().parents[1]
ROOT = PROJECT.parent
NAME = "ProbablyStolenStockMarket"


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def tracked_assets() -> dict[str, bytes]:
    result = subprocess.run(
        ["git", "ls-files", "-z", "--", f"{NAME}/Assets/"],
        cwd=ROOT,
        capture_output=True,
        check=True,
    )
    files: dict[str, bytes] = {}
    for raw in result.stdout.split(b"\0"):
        if not raw:
            continue
        relative = PurePosixPath(raw.decode("utf-8"))
        if relative.name in {"health_pack.png", ".DS_Store", ".gitkeep"}:
            continue
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError(f"不安全的资源路径：{relative}")
        source = ROOT.joinpath(*relative.parts)
        if source.is_symlink() or not source.is_file():
            raise ValueError(f"版本控制中的资源缺失或不是普通文件：{relative}")
        asset_relative = relative.relative_to(PurePosixPath(NAME) / "Assets")
        target = f"UserData/{NAME}/Assets/{asset_relative}"
        files[target] = source.read_bytes()
    required = f"UserData/{NAME}/Assets/market-config.default.json"
    if required not in files:
        raise ValueError("没有找到版本控制中的默认配置，拒绝生成不完整的发布包。")
    if not any(path.endswith(".png") for path in files):
        raise ValueError("没有找到版本控制中的人物或道具图片。")
    return files


def verify_archive(path: Path, expected: dict[str, bytes]) -> None:
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or set(names) != set(expected):
            raise ValueError("发布包目录校验失败：文件重复、遗漏或包含额外文件。")
        bad = archive.testzip()
        if bad:
            raise ValueError(f"发布包 CRC 校验失败：{bad}")
        for name, data in expected.items():
            if archive.read(name) != data:
                raise ValueError(f"发布包内容校验失败：{name}")
        manifest = archive.read("SHA256SUMS").decode("utf-8")
        for line in manifest.splitlines():
            expected_hash, name = line.split("  ", 1)
            if digest(archive.read(name)) != expected_hash:
                raise ValueError(f"发布包 SHA-256 校验失败：{name}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", help="发布版本；默认读取 ModInfo.cs")
    parser.add_argument("--output-directory", type=Path, default=ROOT / "artifacts")
    args = parser.parse_args()
    match = re.search(
        r'public\s+const\s+string\s+Version\s*=\s*"([^"\r\n]+)"',
        (PROJECT / "ModInfo.cs").read_text(encoding="utf-8"),
    )
    if not match:
        raise ValueError("无法从 ModInfo.cs 读取版本。")
    version = args.version or match.group(1)
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?", version):
        raise ValueError(f"版本格式不正确：{version}")
    if version != match.group(1):
        raise ValueError(f"发布版本 {version} 与 ModInfo.cs 的 {match.group(1)} 不一致，请先修改源码并重新编译。")
    dll = PROJECT / "bin" / "Release" / "net6.0" / f"{NAME}.dll"
    if not dll.is_file():
        raise FileNotFoundError(
            f"缺少 Release DLL：{dll}\n请先执行：dotnet build {NAME}/{NAME}.csproj -c Release"
        )
    instructions = ROOT / "docs" / "RELEASE-INSTALL.md"
    if not instructions.is_file():
        raise FileNotFoundError(f"缺少安装说明：{instructions}")

    files = tracked_assets()
    files[f"Mods/{NAME}.dll"] = dll.read_bytes()
    files["安装说明.md"] = instructions.read_bytes()
    manifest = "".join(f"{digest(data)}  {name}\n" for name, data in sorted(files.items()))
    files["SHA256SUMS"] = manifest.encode("utf-8")
    output = args.output_directory.resolve()
    output.mkdir(parents=True, exist_ok=True)
    destination = output / f"{NAME}-v{version}.zip"
    temporary = destination.with_suffix(".zip.tmp")
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, data in sorted(files.items()):
                entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.create_system = 3
                entry.external_attr = 0o100644 << 16
                archive.writestr(entry, data)
        verify_archive(temporary, files)
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)
    archive_hash = digest(destination.read_bytes())
    checksum = destination.with_suffix(".zip.sha256")
    checksum.write_text(f"{archive_hash}  {destination.name}\n", encoding="utf-8")
    print(f"发布包：{destination}")
    print(f"文件数：{len(files)}；大小：{destination.stat().st_size} 字节")
    print(f"SHA-256：{archive_hash}")
    print(f"校验文件：{checksum}")
    print("已通过 ZIP 文件清单、内容、CRC 和包内 SHA-256 校验。")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        print(f"打包失败：{error}", file=sys.stderr)
        raise SystemExit(1)

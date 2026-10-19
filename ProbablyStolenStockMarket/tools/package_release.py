#!/usr/bin/env python3
"""打包可直接解压到游戏目录的股市 Mod 发布文件，并校验全部内容。"""

from __future__ import annotations

import argparse
import hashlib
import re
import struct
import subprocess
import sys
import zipfile
from pathlib import Path, PurePosixPath


PROJECT = Path(__file__).resolve().parents[1]
ROOT = PROJECT.parent
NAME = "ProbablyStolenStockMarket"


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def assembly_mod_version(data: bytes) -> str:
    """直接读取程序集 ModInfo.Version 常量，不加载游戏或 MelonLoader 依赖。"""
    def number(offset: int, size: int) -> int:
        formats = {1: "<B", 2: "<H", 4: "<I", 8: "<Q"}
        return struct.unpack_from(formats[size], data, offset)[0]

    pe = number(0x3C, 4)
    if data[:2] != b"MZ" or data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Release DLL 不是有效的 PE 程序集。")
    optional = pe + 24
    magic = number(optional, 2)
    if magic not in (0x10B, 0x20B):
        raise ValueError("Release DLL 的 PE 可选头格式无法识别。")
    section_start = optional + number(pe + 20, 2)
    sections = [section_start + i * 40 for i in range(number(pe + 6, 2))]

    def file_offset(rva: int) -> int:
        for section in sections:
            address = number(section + 12, 4)
            length = max(number(section + 8, 4), number(section + 16, 4))
            if address <= rva < address + length:
                return number(section + 20, 4) + rva - address
        raise ValueError("Release DLL 包含无法定位的 CLR 数据。")

    directories = optional + (96 if magic == 0x10B else 112)
    clr = file_offset(number(directories + 14 * 8, 4))
    metadata = file_offset(number(clr + 8, 4))
    if data[metadata:metadata + 4] != b"BSJB":
        raise ValueError("Release DLL 缺少有效 CLR 元数据。")
    cursor = metadata + 16 + number(metadata + 12, 4)
    cursor = (cursor + 3) & ~3
    stream_count = number(cursor + 2, 2)
    cursor += 4
    streams: dict[str, int] = {}
    for _ in range(stream_count):
        offset = number(cursor, 4)
        name_end = data.index(b"\0", cursor + 8)
        name = data[cursor + 8:name_end].decode("ascii")
        streams[name] = metadata + offset
        cursor = (name_end + 4) & ~3
    table = streams.get("#~", streams.get("#-"))
    if table is None or "#Strings" not in streams or "#Blob" not in streams:
        raise ValueError("Release DLL 缺少常量验证所需的 CLR 数据流。")
    flags = number(table + 6, 1)
    string_size, guid_size, blob_size = (4 if flags & flag else 2 for flag in (1, 2, 4))
    valid = number(table + 8, 8)
    cursor = table + 24
    rows: dict[int, int] = {}
    for index in range(64):
        if valid & (1 << index):
            rows[index] = number(cursor, 4)
            cursor += 4

    def index_size(index: int) -> int:
        return 4 if rows.get(index, 0) >= 65536 else 2

    def coded_size(bits: int, indices: tuple[int, ...]) -> int:
        return 4 if max((rows.get(i, 0) for i in indices), default=0) >= (1 << (16 - bits)) else 2

    type_ref = coded_size(2, (2, 1, 27))
    constant_parent = coded_size(2, (4, 8, 23))
    widths = {
        0: 2 + string_size + guid_size * 3,
        1: coded_size(2, (0, 26, 35, 1)) + string_size * 2,
        2: 4 + string_size * 2 + type_ref + index_size(4) + index_size(6),
        3: index_size(4),
        4: 2 + string_size + blob_size,
        5: index_size(6),
        6: 8 + string_size + blob_size + index_size(8),
        7: index_size(8),
        8: 4 + string_size,
        9: index_size(2) + type_ref,
        10: coded_size(3, (2, 1, 26, 6, 27)) + string_size + blob_size,
        11: 2 + constant_parent + blob_size,
    }
    starts: dict[int, int] = {}
    for index in range(12):
        starts[index] = cursor
        cursor += rows.get(index, 0) * widths[index]

    def string(index: int) -> str:
        start = streams["#Strings"] + index
        return data[start:data.index(b"\0", start)].decode("utf-8")

    field_number = None
    for row in range(rows.get(2, 0)):
        start = starts[2] + row * widths[2]
        if string(number(start + 4, string_size)) != "ModInfo":
            continue
        if string(number(start + 4 + string_size, string_size)) != NAME:
            continue
        field_column = 4 + string_size * 2 + type_ref
        first = number(start + field_column, index_size(4))
        end = (number(start + widths[2] + field_column, index_size(4))
               if row + 1 < rows.get(2, 0) else rows.get(4, 0) + 1)
        for field in range(first, end):
            position = starts[4] + (field - 1) * widths[4]
            if string(number(position + 2, string_size)) == "Version":
                field_number = field
                break
    if field_number is None:
        raise ValueError("Release DLL 没有 ProbablyStolenStockMarket.ModInfo.Version 字段。")
    for row in range(rows.get(11, 0)):
        start = starts[11] + row * widths[11]
        if number(start + 2, constant_parent) != field_number << 2:
            continue
        if number(start, 1) != 0x0E:
            raise ValueError("Release DLL 中的 ModInfo.Version 不是字符串常量。")
        blob = streams["#Blob"] + number(start + 2 + constant_parent, blob_size)
        first = number(blob, 1)
        if first & 0x80 == 0:
            length, prefix = first, 1
        elif first & 0xC0 == 0x80:
            length, prefix = ((first & 0x3F) << 8) | number(blob + 1, 1), 2
        elif first & 0xE0 == 0xC0:
            length = ((first & 0x1F) << 24) | int.from_bytes(data[blob + 1:blob + 4], "big")
            prefix = 4
        else:
            raise ValueError("Release DLL 的版本常量长度无效。")
        return data[blob + prefix:blob + prefix + length].decode("utf-16-le")
    raise ValueError("Release DLL 缺少 ModInfo.Version 常量，请重新编译。")


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
    dll_data = dll.read_bytes()
    binary_version = assembly_mod_version(dll_data)
    if binary_version != version:
        raise ValueError(f"Release DLL 版本为 {binary_version}，源码版本为 {version}；请重新编译，避免发布旧文件。")
    instructions = ROOT / "docs" / "RELEASE-INSTALL.md"
    if not instructions.is_file():
        raise FileNotFoundError(f"缺少安装说明：{instructions}")

    files = tracked_assets()
    files[f"Mods/{NAME}.dll"] = dll_data
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
    except (OSError, ValueError, struct.error, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        print(f"打包失败：{error}", file=sys.stderr)
        raise SystemExit(1)

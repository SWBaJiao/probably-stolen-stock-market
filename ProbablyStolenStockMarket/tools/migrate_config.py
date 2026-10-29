#!/usr/bin/env python3
"""将明确指定的旧配置原样复制到正式版配置路径，不覆盖已有配置。"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path


def migrate(source: Path, destination: Path, dry_run: bool = False) -> str:
    source = source.expanduser().resolve(strict=True)
    destination = destination.expanduser().absolute()
    if source == destination.resolve():
        raise ValueError("来源与目标是同一个文件，无需迁移。")
    data = source.read_bytes()
    config = json.loads(data.decode("utf-8-sig"))
    if not isinstance(config, dict) or not isinstance(config.get("Companies"), list) or not config["Companies"]:
        raise ValueError("来源不是有效的股市配置对象：Companies 必须为非空数组。")
    checksum = hashlib.sha256(data).hexdigest()
    if destination.exists():
        if destination.is_file() and destination.read_bytes() == data:
            return f"目标已存在完全相同的配置，未写入：{destination}\nSHA-256：{checksum}"
        raise FileExistsError(f"目标已存在且内容不同，未覆盖：{destination}")
    if dry_run:
        return f"预检查通过，未写入文件。\n来源：{source}\n目标：{destination}\nSHA-256：{checksum}"
    destination.parent.mkdir(parents=True, exist_ok=True)
    # 独占创建防止预检查之后被其他进程写入的配置遭覆盖。
    with destination.open("xb") as output:
        try:
            output.write(data)
            output.flush()
        except BaseException:
            destination.unlink(missing_ok=True)
            raise
    if destination.read_bytes() != data:
        raise OSError("迁移后的配置校验失败，请保留原配置并检查目标文件。")
    return f"已原样复制配置，来源文件保留：{destination}\nSHA-256：{checksum}\n业务配置将在游戏加载时继续执行完整验证。"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="旧 market-config.json 的完整文件路径")
    parser.add_argument("--destination", type=Path, required=True, help="正式版 market-config.json 的完整文件路径")
    parser.add_argument("--dry-run", action="store_true", help="只验证输入和目标，不复制文件")
    args = parser.parse_args()
    print(migrate(args.source, args.destination, args.dry_run))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError) as error:
        print(f"配置迁移失败：{error}", file=sys.stderr)
        raise SystemExit(1)

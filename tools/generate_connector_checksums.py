#!/usr/bin/env python3
"""生成/校验 connectors/ 目录的 SHA256SUMS 清单（供应链完整性）。

用法：
  python tools/generate_connector_checksums.py           # 重新生成 connectors/SHA256SUMS
  python tools/generate_connector_checksums.py --check   # 校验现有清单与目录一致（CI 用）

覆盖范围：connectors/<id>/connector.json 与 connectors/servers/*（内置本地 MCP 服务器
是真正的执行面，必须纳入校验）。schema/ 与 README.md 不纳入（非执行面）。
运行时侧由 ConnectorIntegrity.Verify 在安装时校验同一清单。
"""

from __future__ import annotations

import hashlib
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONNECTORS = ROOT / "connectors"
SUMS_PATH = CONNECTORS / "SHA256SUMS"


def covered_files() -> list[Path]:
    files: list[Path] = []
    for conn_dir in sorted(p for p in CONNECTORS.iterdir() if p.is_dir()):
        manifest = conn_dir / "connector.json"
        if manifest.exists():
            files.append(manifest)
    servers = CONNECTORS / "servers"
    if servers.is_dir():
        files.extend(sorted(f for f in servers.iterdir() if f.is_file()))
    return files


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 16), b""):
            h.update(block)
    return h.hexdigest()


def render() -> str:
    lines = [
        f"{sha256_of(f)}  {f.relative_to(CONNECTORS).as_posix()}"
        for f in covered_files()
    ]
    return "\n".join(lines) + "\n"


def main() -> int:
    if "--check" in sys.argv:
        if not SUMS_PATH.exists():
            print("[FAIL] connectors/SHA256SUMS 不存在：运行 python tools/generate_connector_checksums.py 生成。")
            return 1
        expected = render()
        actual = SUMS_PATH.read_text(encoding="utf-8")
        if actual != expected:
            print("[FAIL] connectors/SHA256SUMS 与目录不一致：连接器清单或内置服务器已变更，重新生成并提交。")
            return 1
        print(f"[OK] 连接器完整性清单一致（{len(covered_files())} 个文件）。")
        return 0
    SUMS_PATH.write_text(render(), encoding="utf-8")
    print(f"[OK] 已生成 connectors/SHA256SUMS（{len(covered_files())} 个文件）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())

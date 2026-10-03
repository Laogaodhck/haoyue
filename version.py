#!/usr/bin/env python3
"""Haoyue 统一版本号管理。

统一版本号（major.minor.patch）同时存在于以下版本源，必须始终一致：
  - haoyue_cli/Ui/Banner.cs                         CLI 横幅与版本输出的展示来源
  - haoyue_desktop/package.json                     桌面端应用版本（electron-builder 安装包版本）
  - packaging/npm/staging/packages/*/package.json   npm 包快照（含 optionalDependencies 中的平台子包版本）

用法：
  python version.py                    查看各版本源当前版本与一致性
  python version.py current            输出统一版本号（供 CI 读取）
  python version.py check [--tag TAG]  校验一致性，可选校验与 git tag（vX.Y.Z）相符；不一致时退出码 1
  python version.py set X.Y.Z          将全部版本源同步为 X.Y.Z

发布流程示例（详见 RELEASING.md）：
  python version.py set 1.4.0
  git commit -am "chore: 发布 v1.4.0" && git tag v1.4.0 && git push origin main --tags
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# 中文 Windows 控制台/管道默认可能是 GBK，统一强制 UTF-8，保证任意终端可读。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

REPO_ROOT = Path(__file__).resolve().parent
BANNER_FILE = REPO_ROOT / "haoyue_cli" / "Ui" / "Banner.cs"
DESKTOP_PACKAGE = REPO_ROOT / "haoyue_desktop" / "package.json"
STAGING_PACKAGES_ROOT = REPO_ROOT / "packaging" / "npm" / "staging" / "packages"

VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+$")
BANNER_RE = re.compile(r'(public const string Version = ")([^"]+)(")')
PACKAGE_VERSION_RE = re.compile(r'("version"\s*:\s*")([^"]+)(")')
OPTIONAL_DEP_RE = re.compile(r'("haoyue-cli-[a-z0-9-]+"\s*:\s*")([^"]+)(")')


class VersionError(RuntimeError):
    pass


def read_text(path: Path) -> str:
    # newline="" 关闭换行翻译，逐字节保留原始换行符（仓库统一 LF，禁止自动转 CRLF）。
    with path.open("r", encoding="utf-8", newline="") as handle:
        return handle.read()


def write_text(path: Path, text: str) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        handle.write(text)


def version_files() -> list[Path]:
    files = [BANNER_FILE, DESKTOP_PACKAGE]
    if STAGING_PACKAGES_ROOT.is_dir():
        files.extend(sorted(STAGING_PACKAGES_ROOT.glob("*/package.json")))
    return files


def read_file_version(path: Path) -> str:
    text = read_text(path)
    pattern = BANNER_RE if path == BANNER_FILE else PACKAGE_VERSION_RE
    match = pattern.search(text)
    if not match:
        raise VersionError(f"未能在文件中找到版本号: {path.relative_to(REPO_ROOT)}")
    return match.group(2)


def read_optional_dependency_versions(path: Path) -> list[str]:
    if path == BANNER_FILE:
        return []
    text = read_text(path)
    return [match.group(2) for match in OPTIONAL_DEP_RE.finditer(text)]


def read_versions() -> dict[Path, str]:
    return {path: read_file_version(path) for path in version_files()}


def unified_version() -> str:
    versions = read_versions()
    unique = sorted(set(versions.values()))
    if len(unique) != 1:
        raise VersionError(
            "版本号不一致：" + "、".join(f"{version}" for version in unique)
        )
    return unique[0]


def write_file_version(path: Path, version: str) -> bool:
    text = read_text(path)
    if path == BANNER_FILE:
        updated, count = BANNER_RE.subn(rf'\g<1>{version}\g<3>', text, count=1)
    else:
        updated, count = PACKAGE_VERSION_RE.subn(rf'\g<1>{version}\g<3>', text, count=1)
        updated = OPTIONAL_DEP_RE.sub(rf'\g<1>{version}\g<3>', updated)
    if not count:
        raise VersionError(f"未能定位版本号字段: {path.relative_to(REPO_ROOT)}")
    if updated == text:
        return False
    write_text(path, updated)
    return True


def set_all(version: str) -> list[Path]:
    if not VERSION_PATTERN.fullmatch(version):
        raise VersionError(f"版本号必须为 major.minor.patch 格式: {version!r}")
    changed: list[Path] = []
    for path in version_files():
        if write_file_version(path, version):
            changed.append(path)
    for path, current in read_versions().items():
        if current != version:
            raise VersionError(f"写入后校验失败: {path.relative_to(REPO_ROOT)} 仍为 {current}")
    return changed


def report() -> bool:
    versions = read_versions()
    optional_deps = {
        path: values
        for path in versions
        if (values := read_optional_dependency_versions(path))
    }
    all_values = list(versions.values())
    for values in optional_deps.values():
        all_values.extend(values)
    consistent = len(set(all_values)) == 1

    if consistent:
        print(f"Haoyue 统一版本: {all_values[0]}")
    else:
        print("Haoyue 版本号不一致：")
    for path, version in versions.items():
        mark = "✓" if version == all_values[0] else "✗"
        print(f"  {mark} {str(path.relative_to(REPO_ROOT)):<47} {version}")
        for value in optional_deps.get(path, []):
            dep_mark = "✓" if value == all_values[0] else "✗"
            print(f"    {dep_mark} optionalDependencies <- {value}")
    return consistent


def command_check(tag: str | None) -> int:
    consistent = report()
    if not consistent:
        print("提示：运行 `python version.py set X.Y.Z` 可一键同步全部版本源。", file=sys.stderr)
        return 1
    if tag:
        normalized = tag[1:] if tag.startswith("v") else tag
        if not VERSION_PATTERN.fullmatch(normalized):
            print(f"tag 格式不合法（应为 vX.Y.Z）: {tag}", file=sys.stderr)
            return 1
        current = unified_version()
        if normalized != current:
            print(
                f"tag 与版本号不符: tag={tag}，版本源={current}。"
                f"请先 `python version.py set {normalized}` 并提交后再打 tag。",
                file=sys.stderr,
            )
            return 1
        print(f"tag {tag} 与统一版本号一致。")
    return 0


def command_set(version: str) -> int:
    changed = set_all(version)
    if changed:
        print(f"已同步 {len(changed)} 个版本源至 {version}:")
        for path in changed:
            print(f"  - {path.relative_to(REPO_ROOT)}")
    else:
        print(f"全部版本源已为 {version}，无需修改。")
    print("下一步: git commit && git tag v" + version)
    return 0


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Haoyue 统一版本号管理。")
    parser.add_argument(
        "command",
        nargs="?",
        default="show",
        choices=["show", "current", "check", "set"],
        help="show(默认)=查看报告 / current=输出统一版本 / check=校验 / set=同步",
    )
    parser.add_argument("value", nargs="?", help="set 命令的目标版本号，如 1.4.0")
    parser.add_argument("--tag", help="check 命令可选：同时校验与 git tag（vX.Y.Z）相符。")
    return parser.parse_args()


def main() -> int:
    args = parse_arguments()
    try:
        if args.command == "show":
            return 0 if report() else 1
        if args.command == "current":
            print(unified_version())
            return 0
        if args.command == "check":
            return command_check(args.tag)
        if args.command == "set":
            if not args.value:
                print("用法: python version.py set X.Y.Z", file=sys.stderr)
                return 2
            return command_set(args.value)
    except VersionError as error:
        print(f"错误: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

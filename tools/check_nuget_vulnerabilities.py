#!/usr/bin/env python3
"""Fail the build when `dotnet list package --vulnerable` reports any advisory.

`dotnet list package --vulnerable` always exits 0, so CI previously printed the
report and moved on. This wrapper consumes its JSON output (stdin) and exits 1
when at least one package carries a vulnerability entry.
"""
from __future__ import annotations

import json
import sys


def main() -> int:
    raw = sys.stdin.read()
    if not raw.strip():
        print("nuget-vulnerability: empty input", file=sys.stderr)
        return 1
    try:
        report = json.loads(raw)
    except json.JSONDecodeError as exc:
        print(f"nuget-vulnerability: cannot parse JSON output: {exc}", file=sys.stderr)
        return 1

    findings: list[str] = []
    for project in report.get("projects", []):
        for framework in project.get("frameworks", []):
            packages = framework.get("topLevelPackages", []) + framework.get("transitivePackages", [])
            for package in packages:
                advisories = package.get("vulnerabilities") or []
                if not advisories:
                    continue
                severities = ",".join(sorted({a.get("severity", "unknown") for a in advisories}))
                findings.append(
                    f"{project.get('path')} [{framework.get('framework')}] "
                    f"{package.get('id')} {package.get('resolvedVersion')} ({severities})"
                )

    if findings:
        print("发现已知漏洞依赖（--vulnerable）：", file=sys.stderr)
        for line in findings:
            print(f"  - {line}", file=sys.stderr)
        return 1
    print("nuget-vulnerability: no vulnerable packages")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

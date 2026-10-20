"""pytest 装配：把内置本地 MCP 服务器作为模块加载。

服务器是单文件脚本（无包结构、以 `python haoyue_local_server.py` 启动），因此按
文件路径加载，而不是依赖 import 路径。
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import pytest

SERVER_DIR = Path(__file__).resolve().parents[1]
SERVER_PATH = SERVER_DIR / "haoyue_local_server.py"


@pytest.fixture(scope="session")
def server():
    spec = importlib.util.spec_from_file_location("haoyue_local_server_under_test", SERVER_PATH)
    assert spec and spec.loader, f"无法加载 {SERVER_PATH}"
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module

"""内置本地 MCP 服务器（connectors/servers/haoyue_local_server.py）回归测试。

覆盖范围（对应端到端审查报告「连接器执行面缺测试」项）：
- PowerShell/AppleScript 参数转义：命令注入与语法破坏（曾因 `{[json.dumps(x)]}`
  渲染成 set 字面量导致剪贴板/通知在 Windows 上 100% 失败）
- http_request 的 scheme 白名单（file:// 任意文件读取）
- ICS/RSS 本地文件解析（折行、排序、limit、格式识别、外部实体）
- stdio 协议的 UTF-8 固定（Windows 管道默认编码是 ANSI 代码页）
- JSON-RPC 主循环：initialize / tools/list / tools/call / 未知方法 / 坏帧
"""

from __future__ import annotations

import io
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import pytest

SERVER_PATH = Path(__file__).resolve().parents[1] / "haoyue_local_server.py"


# --------------------------------------------------------------------------- #
# 测试替身
# --------------------------------------------------------------------------- #

class Completed:
    def __init__(self, returncode: int = 0, stdout: bytes = b"", stderr: bytes = b""):
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


@pytest.fixture
def captured_runs(monkeypatch, server):
    """拦截 subprocess.run，返回其结果对象列表（不真正执行任何外部命令）。"""
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(list(cmd))
        return Completed()

    monkeypatch.setattr(server.subprocess, "run", fake_run)
    return calls


def _script_of(cmd: list[str]) -> str:
    assert cmd[:3] == ["powershell", "-NoProfile", "-Command"], cmd
    return cmd[3]


# --------------------------------------------------------------------------- #
# 参数转义
# --------------------------------------------------------------------------- #

def test_ps_quote_keeps_payload_inside_single_quoted_literal(server):
    payload = "'; Remove-Item C:\\ -Recurse -Force; '"
    quoted = server._ps_quote(payload)

    assert quoted.startswith("'") and quoted.endswith("'")
    # 内部单引号必须双写，PowerShell 才会把它当作字面量的一部分。
    inner = quoted[1:-1]
    assert inner.replace("''", "'") == payload
    assert "'''" in quoted


def test_ps_quote_handles_empty_and_unicode(server):
    assert server._ps_quote("") == "''"
    assert server._ps_quote("中文 '标题'") == "'中文 ''标题'''"


def test_clipboard_set_windows_builds_valid_powershell(server, monkeypatch, captured_runs, tmp_path):
    monkeypatch.setattr(server.sys, "platform", "win32")
    monkeypatch.setattr(tempfile, "tempdir", str(tmp_path))

    result = server.clipboard_set({"text": "剪贴板内容 'quoted'"})

    assert result.startswith("已写入剪贴板")
    script = _script_of(captured_runs[0])
    # 旧实现生成的是 `['C:\...\tmp.txt']`（set 字面量），PowerShell 直接报语法错。
    assert script.startswith("Get-Content -Raw -Encoding UTF8 '")
    assert script.endswith("' | Set-Clipboard")
    assert "[[" not in script and "['" not in script
    # 临时文件用完即删。
    leak = script.split("'")[1]
    assert not os.path.exists(leak)


def test_clipboard_set_windows_raises_on_failure(server, monkeypatch):
    monkeypatch.setattr(server.sys, "platform", "win32")
    monkeypatch.setattr(
        server.subprocess, "run",
        lambda cmd, **kwargs: Completed(returncode=1, stderr=b"boom"),
    )
    with pytest.raises(RuntimeError, match="boom"):
        server.clipboard_set({"text": "x"})


def test_notify_windows_quotes_title_and_body(server, monkeypatch, captured_runs):
    monkeypatch.setattr(server.sys, "platform", "win32")

    assert server.notify_send({"title": "$(calc)", "body": "a'b"}) == "通知已发送"
    script = _script_of(captured_runs[0])
    assert "ShowBalloonTip(5000, '$(calc)', 'a''b'," in script
    # 未加引号的 $(...) 会被 PowerShell 当子表达式执行。
    assert "5000, $(" not in script


def test_notify_linux_passes_arguments_without_shell(server, monkeypatch, captured_runs):
    monkeypatch.setattr(server.sys, "platform", "linux")
    server.notify_send({"title": "标题; rm -rf /", "body": "正文"})
    assert captured_runs[0] == ["notify-send", "标题; rm -rf /", "正文"]


# --------------------------------------------------------------------------- #
# http_request：scheme 白名单与响应处理
# --------------------------------------------------------------------------- #

@pytest.mark.parametrize("url", [
    "file:///C:/Windows/win.ini",
    "file:///etc/passwd",
    "ftp://example.com/x",
    "data:text/plain,hello",
    "gopher://example.com/",
    "jar:file:///c:/x.jar!/a",
    "",
])
def test_http_request_rejects_non_http_schemes(server, monkeypatch, url):
    def explode(*args, **kwargs):
        raise AssertionError("非 http(s) 不应触达 urlopen")

    monkeypatch.setattr(server.urllib.request, "urlopen", explode)
    with pytest.raises(ValueError, match="仅支持 http/https"):
        server.http_request({"url": url})


class FakeResponse:
    def __init__(self, body: bytes, status: int = 200):
        self._body = body
        self.status = status

    def read(self) -> bytes:
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def test_http_request_pretty_prints_json(server, monkeypatch):
    seen = {}

    def fake_urlopen(req, timeout=None):
        seen["method"] = req.get_method()
        seen["url"] = req.full_url
        seen["timeout"] = timeout
        return FakeResponse(json.dumps({"ok": True, "n": 2}).encode())

    monkeypatch.setattr(server.urllib.request, "urlopen", fake_urlopen)
    out = server.http_request({"url": "https://example.com/api"})

    assert out.startswith("HTTP 200")
    assert '"ok": true' in out
    assert seen == {"method": "GET", "url": "https://example.com/api", "timeout": server.HTTP_TIMEOUT}


def test_http_request_truncates_large_body(server, monkeypatch):
    monkeypatch.setattr(
        server.urllib.request, "urlopen",
        lambda req, timeout=None: FakeResponse(b"x" * (server.MAX_OUTPUT_CHARS + 500)),
    )
    out = server.http_request({"url": "http://example.com/big"})
    assert "截断" in out
    assert len(out) < server.MAX_OUTPUT_CHARS + 200


# --------------------------------------------------------------------------- #
# ICS / RSS 本地文件
# --------------------------------------------------------------------------- #

ICS_SAMPLE = """BEGIN:VCALENDAR
VERSION:2.0
BEGIN:VEVENT
DTSTART:20261021T090000
SUMMARY:晚到的会
LOCATION:会议室 A
END:VEVENT
BEGIN:VEVENT
DTSTART:20261019T100000
SUMMARY:折行标
 题续行
END:VEVENT
END:VCALENDAR
"""


def test_ics_events_reads_local_file_sorted_and_unfolded(server, tmp_path):
    ics = tmp_path / "calendar.ics"
    ics.write_text(ICS_SAMPLE, encoding="utf-8")

    out = server.ics_events({"source": str(ics)})

    assert "共 2 个事件" in out
    first, second = out.splitlines()[1], out.splitlines()[2]
    assert "20261019T100000" in first
    assert "折行标题续行" in first  # RFC 5545 折行已还原
    assert "20261021T090000" in second and "会议室 A" in second


def test_ics_events_accepts_file_url(server, tmp_path):
    ics = tmp_path / "calendar.ics"
    ics.write_text(ICS_SAMPLE, encoding="utf-8")
    out = server.ics_events({"source": ics.as_uri()})
    assert "共 2 个事件" in out


def test_ics_events_honours_limit(server, tmp_path):
    ics = tmp_path / "many.ics"
    ics.write_text(ICS_SAMPLE, encoding="utf-8")
    out = server.ics_events({"source": str(ics), "limit": 1})
    assert "显示前 1 个" in out
    assert len(out.splitlines()) == 2


RSS_SAMPLE = """<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0"><channel>
  <title>示例源</title>
  <item><title>第一篇</title><link>https://example.com/1</link>
        <pubDate>Mon, 19 Oct 2026 08:00:00 GMT</pubDate>
        <description>摘要一</description></item>
  <item><title>第二篇</title><link>https://example.com/2</link></item>
</channel></rss>
"""

ATOM_SAMPLE = """<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <entry><title>Atom 条目</title><link href="https://example.com/a"/>
         <updated>2026-10-20T08:00:00Z</updated>
         <summary>atom 摘要</summary></entry>
</feed>
"""


def test_rss_read_parses_rss2_local_file(server, tmp_path):
    feed = tmp_path / "feed.xml"
    feed.write_text(RSS_SAMPLE, encoding="utf-8")

    out = server.rss_read({"url": str(feed)})
    assert "共 2 条" in out
    assert "第一篇" in out and "https://example.com/1" in out
    assert "Mon, 19 Oct 2026 08:00:00 GMT" in out  # 日期原样透传


def test_rss_read_parses_atom(server, tmp_path):
    feed = tmp_path / "atom.xml"
    feed.write_text(ATOM_SAMPLE, encoding="utf-8")
    out = server.rss_read({"url": str(feed), "limit": 1})
    assert "Atom 条目" in out and "https://example.com/a" in out


def test_rss_read_rejects_unknown_format(server, tmp_path):
    feed = tmp_path / "weird.xml"
    feed.write_text("<html><body>nope</body></html>", encoding="utf-8")
    with pytest.raises(RuntimeError, match="无法识别的订阅格式"):
        server.rss_read({"url": str(feed)})


def test_rss_read_does_not_expand_external_entities(server, tmp_path):
    feed = tmp_path / "xxe.xml"
    feed.write_text(
        '<?xml version="1.0"?>\n'
        '<!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>\n'
        "<rss><channel><item><title>&xxe;</title></item></channel></rss>",
        encoding="utf-8",
    )
    with pytest.raises(Exception) as err:
        server.rss_read({"url": str(feed)})
    assert "root:" not in str(err.value)


# --------------------------------------------------------------------------- #
# MCP stdio 协议
# --------------------------------------------------------------------------- #

def test_tool_catalog_has_unique_names_and_schemas(server):
    names = [t["name"] for t in server.TOOLS]
    assert len(names) == len(set(names)) == len(server.HANDLERS)
    assert set(names) == set(server.HANDLERS)
    for tool in server.TOOLS:
        assert tool["description"]
        assert tool["inputSchema"]["type"] == "object"


def test_dispatch_wraps_tool_errors(server):
    result = server._dispatch("http_request", {"url": "file:///etc/passwd"})
    assert result["isError"] is True
    assert "仅支持 http/https" in result["content"][0]["text"]

    unknown = server._dispatch("nope", {})
    assert unknown["isError"] is True and "未知工具" in unknown["content"][0]["text"]


def _run_main(server, monkeypatch, lines: list[str]) -> list[dict]:
    stdout = io.StringIO()
    monkeypatch.setattr(server.sys, "stdin", io.StringIO("\n".join(lines) + "\n"))
    monkeypatch.setattr(server.sys, "stdout", stdout)
    assert server.main() == 0
    return [json.loads(line) for line in stdout.getvalue().splitlines() if line.strip()]


def test_protocol_handles_initialize_list_call_and_errors(server, monkeypatch):
    replies = _run_main(server, monkeypatch, [
        json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize"}),
        json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}),
        json.dumps({"jsonrpc": "2.0", "id": 3, "method": "prompts/list"}),
        "{not json",
        json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}),
        json.dumps({"jsonrpc": "2.0", "id": 4, "method": "tools/call",
                    "params": {"name": "http_request", "arguments": {"url": "file:///etc/passwd"}}}),
    ])

    by_id = {r.get("id"): r for r in replies}
    assert len(replies) == 5  # 通知不产生应答
    assert by_id[1]["result"]["protocolVersion"] == server.PROTOCOL_VERSION
    assert len(by_id[2]["result"]["tools"]) == len(server.TOOLS)
    # prompts/resources 未实现：必须回 -32601，客户端才能立即降级而不是等超时。
    assert by_id[3]["error"]["code"] == -32601
    assert by_id[None]["error"]["code"] == -32700
    assert by_id[4]["result"]["isError"] is True


def test_stdio_is_utf8_even_when_pythonioencoding_is_ansi():
    """Windows 管道默认按系统 ANSI 代码页收发；客户端（.NET）固定 UTF-8。
    含中文的请求/应答必须能无损往返，否则表现为「连上就掉线」。"""
    payload = json.dumps({
        "jsonrpc": "2.0", "id": 1, "method": "tools/call",
        "params": {"name": "http_request", "arguments": {"url": "file:///etc/passwd"}},
    }, ensure_ascii=False)
    env = dict(os.environ, PYTHONIOENCODING="cp936", PYTHONUTF8="0")

    proc = subprocess.run(
        [sys.executable, str(SERVER_PATH)],
        input=(payload + "\n").encode("utf-8"),
        capture_output=True,
        env=env,
        timeout=60,
    )

    assert proc.returncode == 0, proc.stderr.decode("utf-8", "replace")
    reply = json.loads(proc.stdout.decode("utf-8"))
    assert "仅支持 http/https" in reply["result"]["content"][0]["text"]

"""haoyue 内置本地连接器服务器（单文件、纯标准库、离线优先）。

一个 stdio MCP 服务器同时承载六个「无需第三方 npm 包」的高频连接器：
  clipboard / notify / ics / rss / http / email

协议：MCP stdio 传输（按行分隔的 JSON-RPC 2.0）。实现保持最小化：
initialize / tools/list / tools/call / ping，其余方法静默忽略。

安全约定：
- 所有网络访问仅由显式工具调用触发，不常驻后台。
- email 凭据来自环境变量（安装器以 secret: 前缀写入），不落日志。
- http_request 仅在调用方显式传 URL 时发起，超时强制 30 秒。
"""

from __future__ import annotations

import io
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from email.header import decode_header, make_header
from email.parser import BytesParser
from email.policy import default as default_policy
from email.utils import formatdate, parseaddr
from urllib.parse import urlparse

SERVER_NAME = "haoyue-local"
SERVER_VERSION = "1.0.0"
PROTOCOL_VERSION = "2024-11-05"
HTTP_TIMEOUT = 30
MAX_OUTPUT_CHARS = 20_000


# --------------------------------------------------------------------------- #
# 工具定义
# --------------------------------------------------------------------------- #

def _str_prop(description: str) -> dict:
    return {"type": "string", "description": description}


TOOLS = [
    {
        "name": "clipboard_get",
        "description": "读取系统剪贴板文本内容。",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "clipboard_set",
        "description": "把文本写入系统剪贴板。",
        "inputSchema": {
            "type": "object",
            "properties": {"text": _str_prop("要写入剪贴板的文本")},
            "required": ["text"],
        },
    },
    {
        "name": "notify_send",
        "description": "发送一条桌面系统通知。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "title": _str_prop("通知标题"),
                "body": _str_prop("通知正文"),
            },
            "required": ["title"],
        },
    },
    {
        "name": "ics_events",
        "description": "读取 iCalendar（.ics）文件或 URL，返回日程事件列表（按开始时间升序）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "source": _str_prop("ics 文件路径或 http(s) URL"),
                "limit": {"type": "integer", "description": "最多返回的事件数，默认 20"},
            },
            "required": ["source"],
        },
    },
    {
        "name": "rss_read",
        "description": "读取 RSS 2.0 或 Atom 源，返回最新条目（标题/链接/日期/摘要）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "url": _str_prop("RSS/Atom 源 URL"),
                "limit": {"type": "integer", "description": "最多返回的条目数，默认 10"},
            },
            "required": ["url"],
        },
    },
    {
        "name": "http_request",
        "description": "通用 HTTP API 调用器：向指定 URL 发起请求并返回状态码与响应体（JSON 自动格式化）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "url": _str_prop("完整请求 URL"),
                "method": {"type": "string", "description": "HTTP 方法，默认 GET"},
                "headers": {"type": "object", "description": "请求头键值对", "additionalProperties": {"type": "string"}},
                "body": _str_prop("请求体（字符串原样发送）"),
                "timeout_seconds": {"type": "integer", "description": "超时秒数，默认 30，上限 120"},
            },
            "required": ["url"],
        },
    },
    {
        "name": "email_list",
        "description": "通过 IMAP 列出收件箱最新邮件（需在安装连接器时配置 IMAP_HOST/IMAP_USER/IMAP_PASSWORD）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "folder": _str_prop("邮箱文件夹，默认 INBOX"),
                "limit": {"type": "integer", "description": "最多返回的邮件数，默认 10"},
            },
        },
    },
    {
        "name": "email_send",
        "description": "通过 SMTP 发送邮件（需在安装连接器时配置 SMTP_HOST/SMTP_USER/SMTP_PASSWORD）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "to": _str_prop("收件人地址，多个用逗号分隔"),
                "subject": _str_prop("邮件主题"),
                "body": _str_prop("邮件正文（纯文本）"),
                "cc": _str_prop("抄送地址，可选，逗号分隔"),
            },
            "required": ["to", "subject", "body"],
        },
    },
]


# --------------------------------------------------------------------------- #
# 剪贴板
# --------------------------------------------------------------------------- #

def clipboard_get(_: dict) -> str:
    if sys.platform == "win32":
        out = subprocess.run(
            ["powershell", "-NoProfile", "-Command", "Get-Clipboard"],
            capture_output=True, timeout=15, check=False)
        if out.returncode != 0:
            raise RuntimeError(out.stderr.decode("utf-8", "replace").strip() or "Get-Clipboard 失败")
        return out.stdout.decode("utf-8", "replace").rstrip("\r\n")
    for cmd in (["pbpaste"], ["xclip", "-selection", "clipboard", "-o"], ["wl-paste"]):
        try:
            out = subprocess.run(cmd, capture_output=True, timeout=15, check=True)
            return out.stdout.decode("utf-8", "replace")
        except (FileNotFoundError, subprocess.CalledProcessError):
            continue
    raise RuntimeError("未找到可用的剪贴板工具（pbpaste/xclip/wl-paste）")


def clipboard_set(args: dict) -> str:
    text = args.get("text", "")
    if sys.platform == "win32":
        # PowerShell stdin 编码不可靠：经 UTF-8 临时文件中转最稳。
        import tempfile
        with tempfile.NamedTemporaryFile("w", suffix=".txt", encoding="utf-8",
                                         delete=False) as tmp:
            tmp.write(text)
            tmp_path = tmp.name
        try:
            script = f"Get-Content -Raw -Encoding UTF8 {[json.dumps(tmp_path)]} | Set-Clipboard"
            out = subprocess.run(["powershell", "-NoProfile", "-Command", script],
                                 capture_output=True, timeout=15, check=False)
            if out.returncode != 0:
                raise RuntimeError(
                    out.stderr.decode("utf-8", "replace").strip() or "Set-Clipboard 失败")
        finally:
            try:
                os.unlink(tmp_path)
            except OSError:
                pass
    else:
        for cmd in (
            ["pbcopy"],
            ["xclip", "-selection", "clipboard"],
            ["wl-copy"],
        ):
            try:
                out = subprocess.run(cmd, input=text.encode("utf-8"),
                                     capture_output=True, timeout=15, check=True)
                break
            except (FileNotFoundError, subprocess.CalledProcessError):
                continue
        else:
            raise RuntimeError("未找到可用的剪贴板工具（pbcopy/xclip/wl-copy）")
    return f"已写入剪贴板（{len(text)} 字符）"


# --------------------------------------------------------------------------- #
# 系统通知
# --------------------------------------------------------------------------- #

def _notify_windows(title: str, body: str) -> None:
    # NotifyIcon 气泡：无需第三方依赖，2.5 秒后自行退出。
    ps = f"""
Add-Type -AssemblyName System.Windows.Forms
$n = New-Object System.Windows.Forms.NotifyIcon
$n.Icon = [System.Drawing.SystemIcons]::Information
$n.Visible = $true
$n.ShowBalloonTip(5000, {[json.dumps(title)]}, {[json.dumps(body)]}, [System.Windows.Forms.ToolTipIcon]::Info)
Start-Sleep -Milliseconds 2500
$n.Dispose()
"""
    out = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                         capture_output=True, timeout=20, check=False)
    if out.returncode != 0:
        raise RuntimeError("通知发送失败：" + out.stderr.decode("utf-8", "replace").strip())


def notify_send(args: dict) -> str:
    title, body = args.get("title", ""), args.get("body", "")
    if sys.platform == "win32":
        _notify_windows(title, body)
    elif sys.platform == "darwin":
        script = f'display notification {[json.dumps(body)]} with title {[json.dumps(title)]}'
        subprocess.run(["osascript", "-e", script], check=True, timeout=15,
                       capture_output=True)
    else:
        subprocess.run(["notify-send", title, body], check=True, timeout=15,
                       capture_output=True)
    return "通知已发送"


# --------------------------------------------------------------------------- #
# ICS / RSS
# --------------------------------------------------------------------------- #

def _fetch_text(source: str) -> str:
    parsed = urlparse(source)
    if parsed.scheme in ("http", "https"):
        req = urllib.request.Request(source, headers={"User-Agent": "haoyue-local/1.0"})
        with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT) as resp:
            return resp.read().decode("utf-8", "replace")
    if parsed.scheme == "file":
        source = parsed.path
    with io.open(source, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def _unfold_ics(text: str) -> str:
    # RFC 5545 折行：CRLF 后跟空格/制表符属于上一行。
    return re.sub(r"\r?\n[ \t]", "", text)


def ics_events(args: dict) -> str:
    source = args["source"]
    limit = int(args.get("limit") or 20)
    text = _unfold_ics(_fetch_text(source))
    events = []
    for block in text.split("BEGIN:VEVENT")[1:]:
        end = block.find("END:VEVENT")
        if end < 0:
            continue
        block = block[:end]
        fields = dict(
            line.split(":", 1) for line in block.splitlines() if ":" in line)
        dtstart = next((v for k, v in fields.items() if k.split(";")[0] == "DTSTART"), "")
        summary = next((v for k, v in fields.items() if k.split(";")[0] == "SUMMARY"), "(无标题)")
        location = next((v for k, v in fields.items() if k.split(";")[0] == "LOCATION"), "")
        events.append({"start": dtstart, "summary": summary, "location": location})
    events.sort(key=lambda e: e["start"])
    lines = [f"- {e['start']}  {e['summary']}" + (f"（{e['location']}）" if e["location"] else "")
             for e in events[:limit]]
    return f"共 {len(events)} 个事件，显示前 {min(limit, len(events))} 个：\n" + "\n".join(lines)


def rss_read(args: dict) -> str:
    url = args["url"]
    limit = int(args.get("limit") or 10)
    root = ET.fromstring(_fetch_text(url))
    items = []
    if root.tag == "rss":  # RSS 2.0
        for item in root.iter("item"):
            items.append({
                "title": (item.findtext("title") or "").strip(),
                "link": (item.findtext("link") or "").strip(),
                "date": (item.findtext("pubDate") or "").strip(),
                "summary": ((item.findtext("description") or "")
                            .strip()[:200]),
            })
    elif root.tag.endswith("feed"):  # Atom
        ns = root.tag.split("}")[0].lstrip("{") + "}"
        for entry in root.iter(f"{ns}entry"):
            link_el = entry.find(f"{ns}link")
            items.append({
                "title": (entry.findtext(f"{ns}title") or "").strip(),
                "link": (link_el.get("href") if link_el is not None else "").strip(),
                "date": (entry.findtext(f"{ns}updated") or "").strip(),
                "summary": ((entry.findtext(f"{ns}summary") or
                             entry.findtext(f"{ns}content") or "").strip()[:200]),
            })
    else:
        raise RuntimeError("无法识别的订阅格式（期望 RSS 2.0 或 Atom）")
    lines = [f"- {i['title']}\n  {i['link']}" + (f"\n  {i['date']}" if i["date"] else "")
             for i in items[:limit]]
    return f"共 {len(items)} 条，显示前 {min(limit, len(items))} 条：\n" + "\n".join(lines)


# --------------------------------------------------------------------------- #
# HTTP API
# --------------------------------------------------------------------------- #

def http_request(args: dict) -> str:
    url = args["url"]
    method = (args.get("method") or "GET").upper()
    headers = {str(k): str(v) for k, v in (args.get("headers") or {}).items()}
    body = args.get("body")
    timeout = min(int(args.get("timeout_seconds") or HTTP_TIMEOUT), 120)
    req = urllib.request.Request(
        url, data=body.encode("utf-8") if body is not None else None,
        headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            status = resp.status
            raw = resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as err:
        status = err.code
        raw = err.read().decode("utf-8", "replace")
    if len(raw) > MAX_OUTPUT_CHARS:
        raw = raw[:MAX_OUTPUT_CHARS] + f"\n…（截断，共 {len(raw)} 字符）"
    try:
        pretty = json.dumps(json.loads(raw), ensure_ascii=False, indent=2)
        raw = pretty if len(pretty) <= MAX_OUTPUT_CHARS else raw
    except (json.JSONDecodeError, ValueError):
        pass
    return f"HTTP {status}\n{raw}"


# --------------------------------------------------------------------------- #
# 邮件（IMAP / SMTP）
# --------------------------------------------------------------------------- #

def _require_env(*keys: str) -> dict:
    missing = [k for k in keys if not os.environ.get(k)]
    if missing:
        raise RuntimeError(
            "缺少邮件配置环境变量：" + ", ".join(missing)
            + "。请在连接器设置中填写（值以加密形式存储）。")
    return {k: os.environ[k] for k in keys}


def _decode_mime(value) -> str:
    try:
        return str(make_header(decode_header(value or "")))
    except Exception:  # noqa: BLE001 — 解码失败按原文返回
        return value or ""


def email_list(args: dict) -> str:
    import imaplib
    cfg = _require_env("IMAP_HOST", "IMAP_USER", "IMAP_PASSWORD")
    folder = args.get("folder") or "INBOX"
    limit = min(int(args.get("limit") or 10), 50)
    port = int(os.environ.get("IMAP_PORT") or 993)
    conn = imaplib.IMAP4_SSL(cfg["IMAP_HOST"], port)
    try:
        conn.login(cfg["IMAP_USER"], cfg["IMAP_PASSWORD"])
        conn.select(folder, readonly=True)
        _, data = conn.search(None, "ALL")
        ids = data[0].split()[-limit:][::-1]
        lines = []
        for mid in ids:
            _, msg_data = conn.fetch(mid, "(RFC822)")
            msg = BytesParser(policy=default_policy).parsebytes(msg_data[0][1])
            sender = _decode_mime(msg.get("From", ""))
            subject = _decode_mime(msg.get("Subject", ""))
            date = msg.get("Date", "")
            lines.append(f"- {date}\n  发件人：{parseaddr(sender)[1]}\n  主题：{subject}")
        return f"收件箱（{folder}）最新 {len(lines)} 封：\n" + "\n".join(lines)
    finally:
        try:
            conn.logout()
        except Exception:  # noqa: BLE001
            pass


def email_send(args: dict) -> str:
    import smtplib
    from email.message import EmailMessage
    cfg = _require_env("SMTP_HOST", "SMTP_USER", "SMTP_PASSWORD")
    port = int(os.environ.get("SMTP_PORT") or 587)
    recipients = [parseaddr(a.strip())[1] for a in args["to"].split(",") if a.strip()]
    msg = EmailMessage()
    msg["From"] = cfg["SMTP_USER"]
    msg["To"] = ", ".join(recipients)
    msg["Subject"] = args["subject"]
    if args.get("cc"):
        msg["Cc"] = args["cc"]
    msg.set_content(args["body"])
    msg["Date"] = formatdate(localtime=True)
    with smtplib.SMTP(cfg["SMTP_HOST"], port, timeout=HTTP_TIMEOUT) as server:
        server.starttls()
        server.login(cfg["SMTP_USER"], cfg["SMTP_PASSWORD"])
        server.send_message(msg)
    return f"邮件已发送至 {', '.join(recipients)}"


# --------------------------------------------------------------------------- #
# MCP stdio 主循环
# --------------------------------------------------------------------------- #

HANDLERS = {
    "clipboard_get": clipboard_get,
    "clipboard_set": clipboard_set,
    "notify_send": notify_send,
    "ics_events": ics_events,
    "rss_read": rss_read,
    "http_request": http_request,
    "email_list": email_list,
    "email_send": email_send,
}


def _write(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _dispatch(name: str, args: dict) -> dict:
    handler = HANDLERS.get(name)
    if handler is None:
        return {"isError": True, "content": [{"type": "text", "text": f"未知工具：{name}"}]}
    try:
        text = handler(args or {})
        if len(text) > MAX_OUTPUT_CHARS:
            text = text[:MAX_OUTPUT_CHARS] + "…（截断）"
        return {"content": [{"type": "text", "text": text}]}
    except Exception as err:  # noqa: BLE001 — 工具错误按 MCP 约定回传文本
        return {"isError": True, "content": [{"type": "text", "text": f"{type(err).__name__}: {err}"}]}


def main() -> int:
    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        try:
            msg = json.loads(raw)
        except json.JSONDecodeError:
            _write({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error"}})
            continue
        method = msg.get("method", "")
        msg_id = msg.get("id")
        if method == "initialize":
            _write({"jsonrpc": "2.0", "id": msg_id, "result": {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {"tools": {}},
                "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
            }})
        elif method == "tools/list":
            _write({"jsonrpc": "2.0", "id": msg_id, "result": {"tools": TOOLS}})
        elif method == "tools/call":
            params = msg.get("params") or {}
            result = _dispatch(params.get("name", ""), params.get("arguments") or {})
            _write({"jsonrpc": "2.0", "id": msg_id, "result": result})
        elif method == "ping":
            _write({"jsonrpc": "2.0", "id": msg_id, "result": {}})
        elif msg_id is not None:
            # 未知方法必须回错误而不是静默：客户端对未应答请求要等满 60s 超时
            # （prompts/list + resources/list 两次超时曾让连接固定多花 120 秒）。
            # 标准 -32601 让支持方按"服务器不提供该能力"立即降级。
            _write({"jsonrpc": "2.0", "id": msg_id, "error": {
                "code": -32601, "message": f"Method not supported: {method}"}})
        # 通知（无 id）静默忽略。
    return 0


if __name__ == "__main__":
    sys.exit(main())

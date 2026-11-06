"""浩玥 GitHub CLI 连接器服务器（单文件、纯标准库、离线）。

安全约定：
- 只暴露白名单工具，不接受任意 gh 参数数组，也不提供 ``gh api`` 透传；
- 参数在 Python 侧校验后以**列表**传给 subprocess，绝不经过 shell、绝不做命令拼接；
- 复用本机已登录的 gh 凭据，不读取、不落盘、不输出 token；所有输出统一脱敏、截断；
- 未安装 / 未登录 / 超时 / 404 / 速率限制都翻译成可执行的中文提示。
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys

SERVER_NAME = "haoyue-github-cli"
SERVER_VERSION = "1.0.0"
PROTOCOL_VERSION = "2024-11-05"
DEFAULT_TIMEOUT = 60
MAX_TIMEOUT = 120
MAX_OUTPUT_CHARS = 20000
MAX_TITLE = 300
MAX_BODY = 20000
MAX_LINE = 200

INSTALL_HINT = (
    "未检测到 GitHub CLI（gh）。安装：https://cli.github.com/ ，"
    "安装后运行 `gh auth login` 完成登录。"
)
LOGIN_HINT = (
    "GitHub CLI 未登录或凭据失效：请在终端运行 `gh auth login`"
    "（或使用设置面板的登录引导）后重试。"
)

_TOKEN_RE = re.compile(r"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})")
_REPO_RE = re.compile(r"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)?$")
_OWNER_RE = re.compile(r"^[A-Za-z0-9_.-]+$")
_REF_RE = re.compile(r"^[A-Za-z0-9._/-]+$")
_ACCOUNT_RE = re.compile(r"Logged in to \S+ (?:account|as) ([A-Za-z0-9-]+)", re.I)
_HOST_RE = re.compile(r"Logged in to (\S+)", re.I)
_SCOPE_RE = re.compile(r"Token scopes:\s*(.+)", re.I)
_AUTH_FAILURE_RE = re.compile(
    r"not logged in|gh auth login|authentication|http 401|requires authentication", re.I
)

ISSUE_STATES = ("open", "closed", "all")
PR_STATES = ("open", "closed", "merged", "all")


def _redact(text: str) -> str:
    return _TOKEN_RE.sub("gh***", text or "")


def _truncate(text: str) -> str:
    text = text.strip()
    if len(text) <= MAX_OUTPUT_CHARS:
        return text
    return text[:MAX_OUTPUT_CHARS] + "\n…（输出已截断）"


def _line(value: object, field: str, *, required: bool = False, max_len: int = MAX_LINE) -> str | None:
    """校验单行参数：拒绝换行/NUL、拒绝以 - 开头的选项注入。"""
    if value is None:
        if required:
            raise ValueError(f"缺少参数 {field}")
        return None
    if not isinstance(value, str):
        raise ValueError(f"参数 {field} 必须是字符串")
    text = value.strip()
    if required and not text:
        raise ValueError(f"缺少参数 {field}")
    if not text:
        return None
    if len(text) > max_len:
        raise ValueError(f"参数 {field} 超过 {max_len} 字符上限")
    if "\0" in text or "\n" in text or "\r" in text:
        raise ValueError(f"参数 {field} 不能包含换行或空字符")
    if text.startswith("-"):
        raise ValueError(f"参数 {field} 不能以 - 开头")
    return text


def _body(value: object, *, required: bool = False) -> str:
    if value is None:
        if required:
            raise ValueError("缺少参数 body")
        return ""
    if not isinstance(value, str):
        raise ValueError("参数 body 必须是字符串")
    if required and not value.strip():
        raise ValueError("缺少参数 body")
    if len(value) > MAX_BODY:
        raise ValueError(f"参数 body 超过 {MAX_BODY} 字符上限")
    if "\0" in value:
        raise ValueError("参数 body 不能包含空字符")
    return value


def _repo(value: object, *, required: bool = True) -> str | None:
    text = _line(value, "repo", required=required, max_len=100)
    if text is None:
        return None
    if not _REPO_RE.match(text):
        raise ValueError("repo 只支持 owner/name（各自仅含字母、数字、下划线、点或连字符）")
    return text


def _owner(value: object) -> str | None:
    text = _line(value, "owner", max_len=100)
    if text is None:
        return None
    if not _OWNER_RE.match(text):
        raise ValueError("owner 仅允许字母、数字、下划线、点或连字符")
    return text


def _number(value: object) -> int:
    if isinstance(value, bool):
        raise ValueError("number 必须是正整数")
    try:
        number = int(value)
    except (TypeError, ValueError) as err:
        raise ValueError("number 必须是正整数") from err
    if number <= 0 or number > 2**31 - 1:
        raise ValueError("number 超出合法范围")
    return number


def _state(value: object, allowed: tuple[str, ...]) -> str:
    text = _line(value, "state", max_len=20) or "open"
    text = text.lower()
    if text not in allowed:
        raise ValueError(f"state 只支持 {'/'.join(allowed)}")
    return text


def _limit(value: object) -> int:
    try:
        number = int(value)
    except (TypeError, ValueError):
        number = 30
    return max(1, min(100, number))


def _ref(value: object, field: str) -> str | None:
    text = _line(value, field)
    if text is None:
        return None
    if not _REF_RE.match(text):
        raise ValueError(f"参数 {field} 只支持字母、数字、点、下划线、斜杠或连字符")
    return text


def _run(args: list[str], timeout: int = DEFAULT_TIMEOUT) -> subprocess.CompletedProcess[str]:
    binary = os.environ.get("GH_BIN") or "gh"
    resolved = shutil.which(binary)
    if resolved is None:
        raise RuntimeError(INSTALL_HINT)

    env = dict(os.environ)
    env.update(
        {
            "GH_PROMPT_DISABLED": "1",
            "GH_PAGER": "cat",
            "PAGER": "cat",
            "NO_COLOR": "1",
            "GH_NO_UPDATE_NOTIFIER": "1",
        }
    )
    timeout = max(5, min(int(timeout), MAX_TIMEOUT))
    try:
        return subprocess.run(
            [resolved, *args],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            env=env,
            check=False,
        )
    except FileNotFoundError as err:
        raise RuntimeError(INSTALL_HINT) from err
    except subprocess.TimeoutExpired as err:
        raise RuntimeError(f"gh 命令超过 {timeout} 秒未返回，已终止。") from err


def _fail(completed: subprocess.CompletedProcess[str]) -> None:
    text = _redact(((completed.stderr or "") + "\n" + (completed.stdout or "")).strip())
    lowered = text.lower()
    if _AUTH_FAILURE_RE.search(lowered):
        raise RuntimeError(LOGIN_HINT)
    if "http 404" in lowered or "not found" in lowered:
        raise RuntimeError(f"未找到仓库/Issue/PR（GitHub 返回 404）：{text or '无输出'}")
    if "rate limit" in lowered or "http 403" in lowered or "abuse" in lowered:
        raise RuntimeError(f"GitHub API 拒绝或触发速率限制：{text or 'HTTP 403'}")
    raise RuntimeError(f"gh 退出码 {completed.returncode}：{text or '无输出'}")


def _json_output(completed: subprocess.CompletedProcess[str]) -> str:
    if completed.returncode != 0:
        _fail(completed)
    raw = (completed.stdout or "").strip()
    if not raw:
        return "（无数据）"
    try:
        return json.dumps(json.loads(raw), ensure_ascii=False, indent=2)
    except ValueError:
        return _truncate(raw)


def _parse_auth(text: str) -> tuple[str | None, list[str], list[str]]:
    accounts = _ACCOUNT_RE.findall(text)
    hosts = _unique(_HOST_RE.findall(text))
    scopes: list[str] = []
    match = _SCOPE_RE.search(text)
    if match:
        scopes = _unique(re.findall(r"'([^']+)'", match.group(1)))
    return (accounts[0] if accounts else None, hosts, scopes)


def _unique(items: list[str]) -> list[str]:
    seen: list[str] = []
    for item in items:
        if item not in seen:
            seen.append(item)
    return seen


# ------------------------------------------------------------------ 工具实现


def _gh_auth_status(_args: dict) -> str:
    version = _run(["--version"], timeout=15)
    first = (version.stdout or "").strip().splitlines()
    version_line = first[0] if first else "版本未知"

    status = _run(["auth", "status"], timeout=15)
    output = _redact(((status.stdout or "") + "\n" + (status.stderr or "")).strip())
    account, hosts, scopes = _parse_auth(output)

    lines = [f"GitHub CLI：{version_line}"]
    if status.returncode == 0 and account:
        lines.append(f"登录状态：已登录 {', '.join(hosts) or 'github.com'}（账号 {account}）")
    elif status.returncode == 0:
        lines.append("登录状态：已登录")
    else:
        lines.append("登录状态：未登录或凭据失效")
        lines.append(LOGIN_HINT)
    if scopes:
        lines.append("Token scopes：" + ", ".join(scopes))
    if status.returncode != 0 and output:
        lines.append("gh auth status 输出：\n" + _truncate(output))
    return "\n".join(lines)


def _gh_repo_view(args: dict) -> str:
    repo = _repo(args.get("repo"), required=False)
    cmd = ["repo", "view"]
    if repo:
        cmd.append(repo)
    cmd += [
        "--json",
        "nameWithOwner,description,url,defaultBranchRef,visibility,isPrivate,"
        "isFork,stargazerCount,forkCount,pushedAt",
    ]
    return _json_output(_run(cmd))


def _gh_repo_list(args: dict) -> str:
    owner = _owner(args.get("owner"))
    cmd = ["repo", "list"]
    if owner:
        cmd.append(owner)
    cmd += [
        "--limit",
        str(_limit(args.get("limit"))),
        "--json",
        "nameWithOwner,description,visibility,isFork,updatedAt",
    ]
    return _json_output(_run(cmd))


def _gh_issue_list(args: dict) -> str:
    repo = _repo(args.get("repo"))
    cmd = [
        "issue",
        "list",
        "--repo",
        repo,
        "--state",
        _state(args.get("state"), ISSUE_STATES),
        "--limit",
        str(_limit(args.get("limit"))),
    ]
    label = _line(args.get("label"), "label")
    assignee = _line(args.get("assignee"), "assignee")
    search = _line(args.get("search"), "search", max_len=MAX_LINE)
    if label:
        cmd += ["--label", label]
    if assignee:
        cmd += ["--assignee", assignee]
    if search:
        cmd += ["--search", search]
    cmd += ["--json", "number,title,state,author,labels,createdAt,updatedAt,url"]
    return _json_output(_run(cmd))


def _gh_issue_view(args: dict) -> str:
    repo = _repo(args.get("repo"))
    cmd = [
        "issue",
        "view",
        str(_number(args.get("number"))),
        "--repo",
        repo,
        "--json",
        "number,title,state,author,labels,createdAt,updatedAt,body,url",
    ]
    return _json_output(_run(cmd))


def _gh_pr_list(args: dict) -> str:
    repo = _repo(args.get("repo"))
    cmd = [
        "pr",
        "list",
        "--repo",
        repo,
        "--state",
        _state(args.get("state"), PR_STATES),
        "--limit",
        str(_limit(args.get("limit"))),
        "--json",
        "number,title,state,isDraft,author,headRefName,baseRefName,createdAt,updatedAt,url",
    ]
    return _json_output(_run(cmd))


def _gh_pr_view(args: dict) -> str:
    repo = _repo(args.get("repo"))
    if args.get("number") is not None:
        target = str(_number(args.get("number")))
    else:
        target = _ref(args.get("branch"), "branch")
        if not target:
            raise ValueError("缺少参数：number 或 branch 必须提供一个")
    cmd = [
        "pr",
        "view",
        target,
        "--repo",
        repo,
        "--json",
        "number,title,state,isDraft,author,headRefName,baseRefName,mergeable,"
        "mergeStateStatus,createdAt,updatedAt,url,body,additions,deletions,changedFiles",
    ]
    return _json_output(_run(cmd))


def _gh_pr_checks(args: dict) -> str:
    repo = _repo(args.get("repo"))
    cmd = ["pr", "checks", str(_number(args.get("number"))), "--repo", repo]
    completed = _run(cmd, timeout=90)
    text = _truncate((completed.stdout or "").strip())
    if completed.returncode != 0 and not text:
        _fail(completed)
    if completed.returncode != 0:
        text += f"\n（gh 退出码 {completed.returncode}：存在失败、待处理或缺失的检查）"
    return text or "（该 PR 没有检查项）"


def _gh_issue_create(args: dict) -> str:
    repo = _repo(args.get("repo"))
    title = _line(args.get("title"), "title", required=True, max_len=MAX_TITLE)
    body = _body(args.get("body"))
    cmd = ["issue", "create", "--repo", repo, "--title", title, "--body", body]
    label = _line(args.get("label"), "label")
    assignee = _line(args.get("assignee"), "assignee")
    if label:
        cmd += ["--label", label]
    if assignee:
        cmd += ["--assignee", assignee]
    completed = _run(cmd)
    if completed.returncode != 0:
        _fail(completed)
    return _truncate(completed.stdout or "已创建 Issue")


def _gh_issue_comment(args: dict) -> str:
    repo = _repo(args.get("repo"))
    number = _number(args.get("number"))
    body = _body(args.get("body"), required=True)
    cmd = ["issue", "comment", str(number), "--repo", repo, "--body", body]
    completed = _run(cmd)
    if completed.returncode != 0:
        _fail(completed)
    return _truncate(completed.stdout or "已发表评论")


def _gh_pr_create(args: dict) -> str:
    repo = _repo(args.get("repo"))
    title = _line(args.get("title"), "title", required=True, max_len=MAX_TITLE)
    body = _body(args.get("body"))
    cmd = ["pr", "create", "--repo", repo, "--title", title, "--body", body]
    base = _ref(args.get("base"), "base")
    head = _ref(args.get("head"), "head")
    if base:
        cmd += ["--base", base]
    if head:
        cmd += ["--head", head]
    if bool(args.get("draft")):
        cmd.append("--draft")
    completed = _run(cmd, timeout=120)
    if completed.returncode != 0:
        _fail(completed)
    return _truncate(completed.stdout or "已创建 Pull Request")


# ------------------------------------------------------------------ MCP 工具声明


def _schema(properties: dict[str, dict], required: list[str] | None = None) -> dict:
    schema: dict = {"type": "object", "properties": properties, "additionalProperties": False}
    if required:
        schema["required"] = required
    return schema


_REPO_PROP = {"type": "string", "description": "仓库 owner/name（如 octocat/Hello-World）"}
_NUMBER_PROP = {"type": "integer", "description": "Issue / PR 编号（正整数）"}

TOOLS = [
    {
        "name": "gh_auth_status",
        "description": "查看本机 gh 版本、登录账号与 token scopes（不返回令牌）。",
        "inputSchema": _schema({}),
    },
    {
        "name": "gh_repo_view",
        "description": "查看仓库概览（名称、描述、可见性、默认分支、星标与 fork 数）。",
        "inputSchema": _schema({"repo": {**_REPO_PROP, "description": "可选；留空时使用当前目录仓库"}}),
    },
    {
        "name": "gh_repo_list",
        "description": "列出用户或组织的仓库。",
        "inputSchema": _schema({
            "owner": {"type": "string", "description": "可选；用户或组织名，留空为当前登录账号"},
            "limit": {"type": "integer", "description": "返回条数，1–100，默认 30"},
        }),
    },
    {
        "name": "gh_issue_list",
        "description": "列出仓库 Issue，可按状态、标签、负责人与关键词过滤。",
        "inputSchema": _schema({
            "repo": _REPO_PROP,
            "state": {"type": "string", "enum": list(ISSUE_STATES)},
            "limit": {"type": "integer", "description": "返回条数，1–100，默认 30"},
            "label": {"type": "string", "description": "标签名"},
            "assignee": {"type": "string", "description": "负责人登录名"},
            "search": {"type": "string", "description": "搜索关键词"},
        }, ["repo"]),
    },
    {
        "name": "gh_issue_view",
        "description": "查看单个 Issue 的标题、状态、作者、标签、正文与链接。",
        "inputSchema": _schema({"repo": _REPO_PROP, "number": _NUMBER_PROP}, ["repo", "number"]),
    },
    {
        "name": "gh_pr_list",
        "description": "列出仓库 Pull Request。",
        "inputSchema": _schema({
            "repo": _REPO_PROP,
            "state": {"type": "string", "enum": list(PR_STATES)},
            "limit": {"type": "integer", "description": "返回条数，1–100，默认 30"},
        }, ["repo"]),
    },
    {
        "name": "gh_pr_view",
        "description": "查看单个 PR（编号或分支），包含合并状态、增删行数与正文。",
        "inputSchema": _schema({
            "repo": _REPO_PROP,
            "number": _NUMBER_PROP,
            "branch": {"type": "string", "description": "PR 源分支名；与 number 二选一"},
        }, ["repo"]),
    },
    {
        "name": "gh_pr_checks",
        "description": "查看 PR 的 CI 检查状态。",
        "inputSchema": _schema({"repo": _REPO_PROP, "number": _NUMBER_PROP}, ["repo", "number"]),
    },
    {
        "name": "gh_issue_create",
        "description": "在指定仓库创建 Issue（写操作）。",
        "inputSchema": _schema({
            "repo": _REPO_PROP,
            "title": {"type": "string", "description": f"Issue 标题，≤{MAX_TITLE} 字符"},
            "body": {"type": "string", "description": f"Issue 正文，≤{MAX_BODY} 字符"},
            "label": {"type": "string", "description": "单个标签名"},
            "assignee": {"type": "string", "description": "负责人登录名"},
        }, ["repo", "title"]),
    },
    {
        "name": "gh_issue_comment",
        "description": "在 Issue（或 PR）下发表评论（写操作）。",
        "inputSchema": _schema({
            "repo": _REPO_PROP,
            "number": _NUMBER_PROP,
            "body": {"type": "string", "description": f"评论正文，≤{MAX_BODY} 字符"},
        }, ["repo", "number", "body"]),
    },
    {
        "name": "gh_pr_create",
        "description": "创建 Pull Request（写操作）。",
        "inputSchema": _schema({
            "repo": _REPO_PROP,
            "title": {"type": "string", "description": f"PR 标题，≤{MAX_TITLE} 字符"},
            "body": {"type": "string", "description": f"PR 正文，≤{MAX_BODY} 字符"},
            "base": {"type": "string", "description": "目标分支，默认仓库默认分支"},
            "head": {"type": "string", "description": "源分支，默认当前分支"},
            "draft": {"type": "boolean", "description": "是否创建为草稿 PR"},
        }, ["repo", "title"]),
    },
]

HANDLERS = {
    "gh_auth_status": _gh_auth_status,
    "gh_repo_view": _gh_repo_view,
    "gh_repo_list": _gh_repo_list,
    "gh_issue_list": _gh_issue_list,
    "gh_issue_view": _gh_issue_view,
    "gh_pr_list": _gh_pr_list,
    "gh_pr_view": _gh_pr_view,
    "gh_pr_checks": _gh_pr_checks,
    "gh_issue_create": _gh_issue_create,
    "gh_issue_comment": _gh_issue_comment,
    "gh_pr_create": _gh_pr_create,
}


# ------------------------------------------------------------------ MCP stdio 主循环


def _text_result(text: str) -> dict:
    return {"content": [{"type": "text", "text": _truncate(_redact(text))}]}


def _error_result(message: str) -> dict:
    return {"content": [{"type": "text", "text": _redact(message)}], "isError": True}


def _handle(request: dict) -> dict | None:
    method = request.get("method")
    request_id = request.get("id")

    if method == "initialize":
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "result": {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
            },
        }
    if method == "notifications/initialized":
        return None
    if method == "ping":
        return {"jsonrpc": "2.0", "id": request_id, "result": {}}
    if method == "tools/list":
        return {"jsonrpc": "2.0", "id": request_id, "result": {"tools": TOOLS}}
    if method == "tools/call":
        params = request.get("params") or {}
        name = params.get("name")
        handler = HANDLERS.get(name)
        if handler is None:
            return {
                "jsonrpc": "2.0",
                "id": request_id,
                "result": _error_result(f"未知工具：{name}（仅支持白名单内的 gh 封装）"),
            }
        arguments = params.get("arguments") or {}
        if not isinstance(arguments, dict):
            return {
                "jsonrpc": "2.0",
                "id": request_id,
                "result": _error_result("arguments 必须是对象"),
            }
        try:
            return {"jsonrpc": "2.0", "id": request_id, "result": _text_result(handler(arguments))}
        except (ValueError, RuntimeError) as err:
            return {"jsonrpc": "2.0", "id": request_id, "result": _error_result(str(err))}
        except Exception as err:  # noqa: BLE001 - 兜底，避免把进程 traceback 返给模型
            return {
                "jsonrpc": "2.0",
                "id": request_id,
                "result": _error_result(f"执行 gh 工具失败：{_redact(str(err))}"),
            }

    if request_id is None:
        return None
    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "error": {"code": -32601, "message": f"Unknown method: {method}"},
    }


def main() -> None:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")

    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        try:
            request = json.loads(raw)
        except json.JSONDecodeError:
            print(
                json.dumps(
                    {
                        "jsonrpc": "2.0",
                        "id": None,
                        "error": {"code": -32700, "message": "Parse error"},
                    },
                    ensure_ascii=False,
                ),
                flush=True,
            )
            continue
        if not isinstance(request, dict):
            continue
        response = _handle(request)
        if response is not None:
            print(json.dumps(response, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()

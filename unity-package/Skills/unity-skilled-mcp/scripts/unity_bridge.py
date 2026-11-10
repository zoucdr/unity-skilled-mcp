#!/usr/bin/env python3
"""Unity MCP 轻量客户端，仅使用 Python 标准库。

与 ``scripts/unity_bridge.js`` 同契约：同一套 INI 读取、discovery 校验、工程/实例
绑定、数量与字节上限、目录白名单、不重试与退出码。子命令、选项、输出形态一致；
库函数同时提供 snake_case 与 camelCase 两种名字，便于按语言习惯调用。

不读脚本源码即用：``--help`` 与技能 ``SKILL.md`` 已覆盖全部接口。
"""

import argparse
import configparser
import contextvars
from datetime import datetime
from http.client import HTTPException
import json
import math
import os
import re
import sys
import uuid
from urllib import error, parse, request as urlrequest

DEFAULT_MAX_DETAILS = 32
MAX_RESPONSE_BYTES = 65536

EXIT_OK = 0
EXIT_PARAM = 2
EXIT_CONNECTION = 3
EXIT_HTTP = 4
EXIT_FORMAT = 5
EXIT_MCP = 6
EXIT_INTERRUPTED = 130

_QUIET = contextvars.ContextVar("unity_bridge_quiet", default=False)
_WORKFLOW = contextvars.ContextVar("unity_bridge_workflow", default=None)

_BAD_URL_CHARS = re.compile(r"[\s\u0000-\u001f\u007f\\#]")
_BAD_ROUTE_CHARS = re.compile(r"[\s\u0000-\u001f\u007f\\#]")
_BARE_ROUTE = re.compile(
    r"^[A-Za-z0-9._~%!$&'()*+,;=@\-]+"
    r"(?:/[A-Za-z0-9._~%!$&'()*+,;=@\-]*)*"
    r"(?:\?[A-Za-z0-9._~%!$&'()*+,;=:@/?\-]*)?$"
)
_URI = re.compile(r"^[A-Za-z][A-Za-z0-9+.\-]*:\S+$")


class ClientError(Exception):
    """与 CLI 退出码一致的客户端异常；``code`` 即退出码。"""

    def __init__(self, message, code=EXIT_PARAM):
        super().__init__(message)
        self.message = message
        self.code = code

    def __str__(self):
        return self.message


class _NoRedirect(urlrequest.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # 重定向也可能重复提交，交给调用方核实后显式重放。
        return None


def is_object(value):
    return isinstance(value, dict)


def valid_details_limit(value):
    return type(value) is int and 0 <= value <= 9007199254740991


def checked_url(value):
    """拒绝带凭据、片段、空白、反斜杠或非法端口之 URL；不跟随协议相对地址。"""
    try:
        if not isinstance(value, str) or not re.match(r"^https?://", value, re.I):
            raise ValueError()
        if _BAD_URL_CHARS.search(value):
            raise ValueError()
        authority = value.split("/")[2].split("?")[0]
        if not authority or "@" in authority or authority.endswith(":"):
            raise ValueError()
        parts = parse.urlsplit(value)
        if parts.scheme not in ("http", "https") or not parts.hostname:
            raise ValueError()
        if parts.username is not None or parts.password is not None:
            raise ValueError()
        if parts.port is not None and parts.port < 1:
            raise ValueError()
    except (ValueError, TypeError, IndexError):
        raise ClientError("URL 必须是有效的绝对 http/https 地址，不含凭据、片段或反斜杠。", EXIT_PARAM)
    return parts


def origin(value):
    parts = checked_url(value)
    port = parts.port or (443 if parts.scheme == "https" else 80)
    return (parts.scheme, (parts.hostname or "").lower(), port)


def http(value, timeout, method="GET", payload=None, headers=None, max_bytes=None):
    """单次 HTTP 操作：不共享连接、不跟随重定向、不重试。"""
    checked_url(value)
    headers = dict(headers or {})
    data = None
    if payload is not None:
        try:
            data = json.dumps(payload, ensure_ascii=False, allow_nan=False).encode("utf-8")
        except (TypeError, ValueError) as exc:
            raise ClientError("请求参数无效：{}；未重试。".format(exc), EXIT_PARAM)
        if not any(key.lower() == "content-type" for key in headers):
            headers["Content-Type"] = "application/json; charset=utf-8"
    req = urlrequest.Request(value, data=data, headers=headers, method=method)
    # 空 ProxyHandler：不读 HTTP(S)_PROXY，避免本机请求被代理改写（与 unity_bridge.js 一致）。
    opener = urlrequest.build_opener(urlrequest.ProxyHandler({}), _NoRedirect())
    try:
        with opener.open(req, timeout=timeout) as response:
            body = response.read() if max_bytes is None else response.read(max_bytes + 1)
    except error.HTTPError as exc:
        try:
            raw = exc.read()
        except Exception:  # noqa: BLE001 - 读取正文失败不应掩盖原状态码
            raw = b""
        try:
            exc.close()
        except Exception:  # noqa: BLE001
            pass
        text = raw.decode("utf-8", "replace")
        failure = ClientError("HTTP {}：{}；未重试。".format(exc.code, text), EXIT_HTTP)
        failure.status = exc.code
        try:
            failure.body = json.loads(text)
        except ValueError:
            failure.body = text
        raise failure
    except (error.URLError, OSError, TimeoutError, HTTPException) as exc:
        reason = getattr(exc, "reason", exc)
        raise ClientError("连接或 TLS/超时失败：{}；未重试，写操作结果可能未知。".format(reason),
                          EXIT_CONNECTION)
    if max_bytes is not None and len(body) > max_bytes:
        raise ClientError("详情响应超过 65536 字节；未重试。", EXIT_FORMAT)
    try:
        text = body.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise ClientError("响应不是 UTF-8 文本。", EXIT_FORMAT)
    try:
        return json.loads(text)
    except ValueError:
        return text


# ---------------------------------------------------------------------------
# 连接配置与发现
# ---------------------------------------------------------------------------

def read_ini(filename):
    config = configparser.ConfigParser(interpolation=None, strict=True)
    try:
        with open(os.path.expanduser(filename), encoding="utf-8-sig") as stream:
            config.read_file(stream)
    except (OSError, UnicodeError, configparser.Error) as exc:
        raise ClientError("无法读取连接配置 {}：{}；请在 Unity 中启动服务。".format(filename, exc),
                          EXIT_PARAM)
    return config


ALIAS_SECTION = "unity3d-skilled-mcp"
PROJECT_SECTION_PREFIX = "project:"


def _section_config(config, name):
    # DEFAULT 节由 configparser 自动合并；键名大小写不敏感，取值去首尾空白。
    return {key.lower(): (value or "").strip() for key, value in config[name].items()}


def _project_entries(config):
    # 多工程共用一份 INI：每个 [project:工程名] 节一个候选。
    return [(name, _section_config(config, name)) for name in config.sections()
            if name.lower().startswith(PROJECT_SECTION_PREFIX)]


def _entry_name(name, config):
    # 工程名优先取 project_name 字段；否则退化为节名去掉前缀与 #哈希 后缀。
    explicit = config.get("project_name", "").strip()
    if explicit:
        return explicit
    return name[len(PROJECT_SECTION_PREFIX):].split("#", 1)[0].strip()


def _describe_entry(name, config):
    return "{}（端口 {}，pid {}，节 {}）".format(
        _entry_name(name, config), config.get("port", "?"), config.get("process_id", "?"), name)


def _select_connection(entries, selection):
    # 选择次序：--instance-id → --project/UNITY_MCP_PROJECT → --expected-project/cwd → 唯一工程节。
    # 歧义时报错列出候选，不猜测、不重放。
    candidates = "；".join(_describe_entry(name, config) for name, config in entries)
    instance_id = selection.get("instanceId")
    if instance_id:
        matches = [item for item in entries if item[1].get("instance_id") == instance_id]
        if len(matches) == 1:
            return matches[0]
        raise ClientError("未找到实例 {}；候选：{}。".format(instance_id, candidates), EXIT_PARAM)
    project = (selection.get("project") or "").strip()
    if project:
        needle = project.lower()
        matches = [item for item in entries if _entry_name(*item).lower() == needle]
        if len(matches) == 1:
            return matches[0]
        raise ClientError('工程名 "{}" 匹配 {} 个；候选：{}。'.format(project, len(matches), candidates),
                          EXIT_PARAM)
    expected = selection.get("expectedProject")
    if expected:
        matches = [item for item in entries if item[1].get("project_path")
                   and project_key(item[1]["project_path"]) == project_key(expected)]
        if len(matches) == 1:
            return matches[0]
    if len(entries) == 1:
        return entries[0]
    raise ClientError("连接配置含 {} 个工程，请用 --project 或 --expected-project 指定；候选：{}。".format(
        len(entries), candidates), EXIT_PARAM)


def log_info(message):
    if not _QUIET.get():
        print(message, file=sys.stderr)


def discover(ini, timeout, selection=None):
    """读取 INI 并校验 /discovery；任何不一致都在发送业务请求前失败。"""
    selection = selection or {}
    sections = read_ini(ini)
    entries = _project_entries(sections)
    if not entries:
        # 旧版 INI 只有别名节；无工程节时按别名兜底（保持旧客户端行为）。
        if not sections.has_section(ALIAS_SECTION):
            raise ClientError(
                "无法读取连接配置 {}：INI 缺少 unity3d-skilled-mcp 节；请在 Unity 中启动服务。".format(ini),
                EXIT_PARAM)
        entry_name, entry = ALIAS_SECTION, _section_config(sections, ALIAS_SECTION)
    else:
        entry_name, entry = _select_connection(entries, selection)
    ini_limit = None
    try:
        if "max_details" in entry:
            raw = entry["max_details"]
            digits = raw[1:] if raw.startswith("+") else raw
            if not digits or not digits.isascii() or not digits.isdigit():
                raise ValueError("INI max_details 必须为非负整数，0 表示不限数量")
            ini_limit = int(raw)
            if not valid_details_limit(ini_limit):
                raise ValueError("INI max_details 必须为非负安全整数")
        base = entry.get("base_url", "").rstrip("/")
        port = None
        if "port" in entry:
            raw = entry["port"]
            digits = raw[1:] if raw.startswith("+") else raw
            if raw.startswith("-") or not digits.isascii() or not digits.isdigit():
                raise ValueError("INI port 无效")
            port = int(raw)
            if not 1 <= port <= 65535:
                raise ValueError("INI port 无效")
        if not base:
            if port is None:
                raise ValueError("INI 缺少有效 base_url 或 port")
            base = "http://localhost:{}".format(port)
        parts = checked_url(base)
        if parts.path or parts.query:
            raise ValueError("base_url 不能带路径或查询参数")
        if port is not None and origin(base)[2] != port:
            raise ValueError("INI 的 base_url 与 port 不一致")
    except (ValueError, ClientError) as exc:
        raise ClientError("无法读取连接配置 {}：{}；请在 Unity 中启动服务。".format(ini, exc),
                          EXIT_PARAM)

    info = http(base + "/discovery", timeout)
    if not is_object(info):
        raise ClientError("discovery 响应必须是 JSON 对象。", EXIT_FORMAT)
    try:
        if origin(info["base_url"]) != origin(base) or info["port"] != origin(base)[2]:
            raise ValueError("discovery 与 INI 端口/地址不一致")
        for key in ("tools_url", "schema_url_template"):
            if origin(info[key]) != origin(base):
                raise ValueError("discovery 端点不在同一服务")
        for key in ("details_url", "schemas_url"):
            if key in info and origin(info[key]) != origin(base):
                raise ValueError("discovery 端点不在同一服务")
        if "{name}" not in info["schema_url_template"]:
            raise ValueError("schema URL 缺少 {name}")
        if not isinstance(info.get("instance_id"), str) or not info["instance_id"].strip():
            raise ValueError("instance_id 缺失")
        for key in ("project_path", "updated_at"):
            if not isinstance(info[key], str):
                raise ValueError("discovery 字段类型错误：" + key)
        if type(info["process_id"]) is not int:
            raise ValueError("discovery process_id 类型错误")
        if "max_details" in info and not valid_details_limit(info["max_details"]):
            raise ValueError("discovery max_details 必须为非负整数")
    except (KeyError, TypeError, ValueError, ClientError) as exc:
        raise ClientError("无效 discovery：{}".format(exc), EXIT_FORMAT)
    # 陈旧节保护：INI 节记录的实例与目标服务不一致（端口已被其它 Unity 实例占用）时拒绝，避免误连。
    recorded_instance = entry.get("instance_id")
    if recorded_instance and info["instance_id"] != recorded_instance:
        raise ClientError("INI 节 {} 记录的实例已失效（端口被其它实例占用）；请重启目标 Unity 服务刷新。".format(entry_name),
                          EXIT_PARAM)
    if ini_limit is not None and "max_details" in info and ini_limit != info["max_details"]:
        raise ClientError("INI max_details 与 discovery 不一致；请重启目标 Unity 服务以发布启动配置。",
                          EXIT_PARAM)
    # 老服务缺字段时使用 INI；两处都缺失才采用默认值，不抬高服务显式限制。
    info.setdefault("max_details", ini_limit if ini_limit is not None else DEFAULT_MAX_DETAILS)
    log_info("连接信息：" + json.dumps(info, ensure_ascii=False))
    return info


# ---------------------------------------------------------------------------
# 工程与实例绑定
# ---------------------------------------------------------------------------

def expected_project_path(value=None):
    if value is not None:
        if not isinstance(value, str) or not value.strip():
            raise ClientError("expected_project 不能为空。", EXIT_PARAM)
        return os.path.abspath(os.path.expanduser(value))
    current = os.getcwd()
    while True:
        if (os.path.exists(os.path.join(current, "Assets"))
                and os.path.exists(os.path.join(current, "ProjectSettings"))):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


def project_key(value):
    normalized = os.path.abspath(value)
    return normalized.lower() if os.name == "nt" else normalized


def request_id(value):
    if not isinstance(value, str) or not value.strip() or len(value) > 256:
        raise ClientError("request_id 必须为 1-256 个字符。", EXIT_PARAM)
    return value


def query_path(identifier, instance):
    return ("/requests/" + parse.quote(identifier, safe="")
            + "?instance_id=" + parse.quote(instance, safe=""))


def bind(info, args, required):
    client = args["client"]
    expected = client.expected_project
    connection = origin(info["base_url"])
    if client.connection is not None and client.connection != connection:
        print("警告：Unity 连接地址已变更。", file=sys.stderr)
    client.connection = connection
    if (client.instance_id and client.instance_id != info["instance_id"]
            and args["command"] not in ("discover", "status")):
        print("警告：Unity 实例已变更。", file=sys.stderr)
        raise ClientError("实例漂移；显式 discover 重新确认，禁止自动重放。", EXIT_PARAM)
    matches = bool(expected) and project_key(expected) == project_key(info["project_path"])
    if not matches:
        print("警告：" + ("Unity 工程不匹配。" if expected else "未确定预期 Unity 工程。"),
              file=sys.stderr)
        if required:
            raise ClientError("请明确 --expected-project 并确认目标工程；未执行。", EXIT_PARAM)
        return {}
    if client.instance_id and client.instance_id != info["instance_id"]:
        print("警告：Unity 实例已变更。", file=sys.stderr)
    if args["command"] != "status":
        client.instance_id = info["instance_id"]
    return {
        "X-Mcp-Expected-Project": parse.quote(expected, safe=""),
        "X-Mcp-Instance-Id": info["instance_id"],
    }


# ---------------------------------------------------------------------------
# Git Bash / MSYS 路径还原
# ---------------------------------------------------------------------------

def _msys_root():
    exe = os.environ.get("EXEPATH")
    if not exe:
        return None
    root = re.sub(r"[/\\]+$", "", exe.replace("\\", "/"))
    root = re.sub(r"/usr/bin$", "", root, flags=re.I)
    root = re.sub(r"/bin$", "", root, flags=re.I)
    return root if re.match(r"^[A-Za-z]:", root) else None


def normalize_request_target(target):
    """把 shell 转换过的本地路径还原为 /ROUTE；非法输入在网络前失败。"""
    if not isinstance(target, str) or not target:
        return target
    if re.match(r"^https?://", target, re.I):
        return target
    if target.startswith("/"):
        if target.startswith("//") or _BAD_ROUTE_CHARS.search(target):
            raise ClientError("相对路由只能以单个 / 开头，且不含空白、反斜杠或片段。", EXIT_PARAM)
        return target
    if re.match(r"^[A-Za-z]:[\\/]", target):
        normalized = target.replace("\\", "/")
        root = _msys_root()
        if root and normalized.lower().startswith(root.lower() + "/"):
            route = normalized[len(root):]
            if not _BAD_ROUTE_CHARS.search(route):
                return route
        raise ClientError(
            "请求地址看起来是 Git Bash 自动转换的本地路径（" + target
            + "）；请改用省略前导斜杠的路由名、绝对 http/https URL，或设置 MSYS2_ARG_CONV_EXCL=\"*\"。",
            EXIT_PARAM)
    if _BARE_ROUTE.match(target):
        return "/" + target
    return target


# ---------------------------------------------------------------------------
# 载荷构造
# ---------------------------------------------------------------------------

def input_json(args):
    if args.get("json_text") is None and args.get("json_file") is None and not args.get("json_stdin"):
        return None
    try:
        if args.get("json_file") is not None:
            with open(args["json_file"], encoding="utf-8-sig") as stream:
                text = stream.read()
        elif args.get("json_stdin"):
            text = sys.stdin.buffer.read().decode("utf-8-sig")
        else:
            text = args["json_text"]
        return json.loads(text, parse_constant=_invalid_constant)
    except (OSError, UnicodeError, ValueError) as exc:
        raise ClientError("无法读取 UTF-8 JSON 输入：{}".format(exc), EXIT_PARAM)


def _invalid_constant(value):
    raise ValueError("JSON 不允许 " + value)


def response_details_limit(result, limit):
    # 旧响应省略该字段时继承 discovery 的有效限制；显式发布则必须一致。
    return ("max_details" not in result
            or (valid_details_limit(result["max_details"]) and result["max_details"] == limit))


def selection_value(value):
    return (isinstance(value, str) and bool(value.strip()) and value == value.strip()
            and value.lower() != "all"
            and not any(ord(char) < 32 or ord(char) == 127 or char == "*" for char in value))


def details_payload(args):
    if (args.get("tag") is not None) == bool(input_json_present(args)):
        raise ClientError("details 必须选择 --tag 或一种 --members 输入，不能同时使用。", EXIT_PARAM)
    if args.get("tag") is not None:
        if not selection_value(args["tag"]):
            raise ClientError("tag 不能为空或 all/*；从 groups 获取实际标签。", EXIT_PARAM)
        return {"tag": args["tag"]}
    members = input_json(args)
    if not isinstance(members, list) or not members:
        raise ClientError("members 必须是非空 JSON 数组。", EXIT_PARAM)
    unique = {}
    for member in members:
        if not is_object(member) or member.get("kind") not in ("tool", "prompt", "resource"):
            raise ClientError("成员 kind 仅允许 tool、prompt、resource。", EXIT_PARAM)
        key = "uri" if member["kind"] == "resource" else "name"
        if set(member) != {"kind", key} or not selection_value(member[key]):
            raise ClientError("成员仅允许 kind 和 {}，值不能为空或 all/*。".format(key), EXIT_PARAM)
        if key == "uri" and not _URI.match(member["uri"]):
            raise ClientError("资源 uri 必须是带 scheme 的完整 URI；不读取资源内容。", EXIT_PARAM)
        unique[(member["kind"], member[key])] = member
    return {"members": list(unique.values())}


def input_json_present(args):
    return (args.get("json_text") is not None or args.get("json_file") is not None
            or bool(args.get("json_stdin")))


def schema_names(args):
    if args.get("targets"):
        names = args["targets"]
        if input_json_present(args):
            raise ClientError("schemas 的名称列表与 --names 输入只能二选一。", EXIT_PARAM)
    else:
        names = input_json(args)
        if not isinstance(names, list) or not names:
            raise ClientError("names 必须是非空 JSON 字符串数组。", EXIT_PARAM)
    unique = []
    seen = set()
    for name in names:
        if not selection_value(name):
            raise ClientError("工具名必须是非空字符串，禁止 all/* 或控制字符。", EXIT_PARAM)
        if name not in seen:
            seen.add(name)
            unique.append(name)
    return unique


def find_query(value):
    if (not isinstance(value, str) or not value.strip() or value != value.strip()
            or len(value) > 128 or any(ord(char) < 32 or ord(char) == 127 for char in value)):
        raise ClientError("检索关键字须为 1-128 个非控制字符，且不含首尾空白。", EXIT_PARAM)
    return value


# ---------------------------------------------------------------------------
# JSON-RPC 与目录
# ---------------------------------------------------------------------------

def rpc_result(envelope, rpc_id, expected_array=None):
    if (not is_object(envelope) or envelope.get("jsonrpc") != "2.0"
            or envelope.get("id") != rpc_id):
        raise ClientError("无效 JSON-RPC 响应。", EXIT_FORMAT)
    if "error" in envelope:
        raise ClientError("JSON-RPC error：" + json.dumps(envelope["error"], ensure_ascii=False),
                          EXIT_MCP)
    result = envelope.get("result")
    if not is_object(result):
        raise ClientError("JSON-RPC 响应缺少 result 对象。", EXIT_FORMAT)
    if result.get("isError"):
        raise ClientError("MCP isError：" + json.dumps(result, ensure_ascii=False), EXIT_MCP)
    if expected_array and not isinstance(result.get(expected_array), list):
        raise ClientError("JSON-RPC 响应缺少 {} 数组。".format(expected_array), EXIT_FORMAT)
    return envelope


def rpc_call(info, args, method, params, rpc_id, expected_array=None):
    envelope = {"jsonrpc": "2.0", "id": rpc_id, "method": method, "params": params}
    result = http(info["base_url"].rstrip("/") + "/mcp", args["timeout"], "POST", envelope,
                  args.get("binding") or {})
    return rpc_result(result, rpc_id, expected_array)


def directory(info, args, kind):
    url = info["tools_url"] if kind == "tools" else info["base_url"].rstrip("/") + "/" + kind
    result = http(url, args["timeout"])
    if not is_object(result) or not isinstance(result.get(kind), list):
        raise ClientError(kind + " 响应缺少目录列表。", EXIT_FORMAT)
    return result[kind]


# ---------------------------------------------------------------------------
# 执行
# ---------------------------------------------------------------------------

def execute(args):
    """CLI 与库共用的唯一执行路径。"""
    if args["command"] == "request":
        # 路由规范化在 discovery 之前完成；非法输入不产生任何网络请求。
        args["target"] = normalize_request_target(args["target"])
    payload = None
    headers = {}
    if args["command"] in ("call", "request"):
        payload = input_json(args)
        if args["command"] == "call":
            if not input_json_present(args):
                payload = {}
            if not is_object(payload):
                raise ClientError("call 参数必须是 JSON 对象。", EXIT_PARAM)
        else:
            for header in args["headers"]:
                name, sep, value = header.partition(":")
                if not sep or not name.strip() or "\n" in header or "\r" in header:
                    raise ClientError("请求头格式应为 NAME:VALUE，不能包含换行。", EXIT_PARAM)
                name = name.strip()
                if not re.match(r"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$", name):
                    raise ClientError("请求头格式无效。", EXIT_PARAM)
                if name.lower() in ("x-mcp-expected-project", "x-mcp-instance-id"):
                    raise ClientError("绑定头仅由客户端管理，禁止手动注入。", EXIT_PARAM)
                headers[name] = value.strip()
    if args["command"] == "details":
        payload = details_payload(args)
    if args["command"] == "schemas":
        payload = {"tools": schema_names(args)}
    # 名称/关键字/URI 的格式校验在 discovery 之前完成，非法输入不产生任何网络请求。
    if args["command"] == "find":
        payload = {"query": find_query(args["target"])}
    if args["command"] in ("prompt", "resource"):
        if args["command"] == "prompt":
            if not selection_value(args["target"]):
                raise ClientError("提示词名称无效；从 list --kind prompts 选取。", EXIT_PARAM)
            payload = {"name": args["target"]}
        else:
            if not selection_value(args["target"]) or not _URI.match(args["target"]):
                raise ClientError("资源 URI 必须是带 scheme 的完整 URI。", EXIT_PARAM)
            payload = {"uri": args["target"]}

    # 多工程时按下述次序选择目标工程；歧义即报错，不猜测。
    client = args.get("client")
    info = discover(args["ini"], args["timeout"], {
        "instanceId": getattr(client, "instance_id", None),
        "project": getattr(client, "project", None),
        "expectedProject": getattr(client, "expected_project", None),
    })
    base = info["base_url"].rstrip("/")
    same_origin = False
    if args["command"] == "request":
        if args["target"].startswith("/"):
            args["target"] = base + args["target"]
        same_origin = origin(args["target"]) == origin(base)
    args["binding"] = bind(info, args,
                           args["command"] in ("call", "prompt", "resource") or same_origin)

    if args["command"] == "discover":
        return info
    if args["command"] == "status":
        identifier = request_id(args["target"])
        instance = args.get("status_instance") or args["client"].instance_id
        if not instance:
            raise ClientError("status 需要 --instance-id 或已有绑定实例。", EXIT_PARAM)
        return http(base + query_path(identifier, instance), args["timeout"])
    if args["command"] == "groups":
        result = http(base + "/groups", args["timeout"])
        limit = info["max_details"]
        # v2 契约用 tags 索引；服务尚未域重载时仍返回旧 groups 数组，过渡期两者皆接受。
        has_index = is_object(result) and (is_object(result.get("namespaces"))
                                           or is_object(result.get("tags")))
        has_legacy = is_object(result) and isinstance(result.get("groups"), list)
        if (not is_object(result) or not (has_index or has_legacy)
                or not response_details_limit(result, limit)
                or result.get("max_response_bytes") != MAX_RESPONSE_BYTES):
            raise ClientError("groups 响应格式或限制无效。", EXIT_FORMAT)
        log_info("组详情数量限制：" + ("不限数量" if limit == 0 else str(limit))
                 + "；仍受 65536 字节与禁止全集规则约束。")
        return result
    if args["command"] == "list":
        kind = args["kind"]
        url = info["tools_url"] if kind == "tools" else base + "/" + kind
        result = http(url, args["timeout"])
        if not is_object(result) or not isinstance(result.get(kind), list):
            raise ClientError(kind + " 响应缺少目录列表。", EXIT_FORMAT)
        return result
    if args["command"] == "details":
        limit = info["max_details"]
        if limit > 0 and payload.get("members") and len(payload["members"]) > limit:
            raise ClientError("去重后最多选择 {} 个成员（discovery max_details）；未发送详情请求。"
                              .format(limit), EXIT_PARAM)
        result = http(base + "/capabilities/details", args["timeout"], "POST", payload,
                      max_bytes=MAX_RESPONSE_BYTES)
        if (not is_object(result) or not isinstance(result.get("items"), list)
                or type(result.get("count")) is not int or result["count"] != len(result["items"])
                or result["count"] < 1 or (limit > 0 and result["count"] > limit)
                or not response_details_limit(result, limit)
                or not all(is_object(item) for item in result["items"])):
            raise ClientError("details 响应格式或成员数量无效。", EXIT_FORMAT)
        return result
    if args["command"] == "schema":
        name = parse.quote(args["target"], safe="")
        url = info["schema_url_template"].split("{name}")[0] + name \
            + info["schema_url_template"].split("{name}", 1)[1]
        result = http(url, args["timeout"])
        if (not is_object(result) or result.get("name") != args["target"]
                or not is_object(result.get("inputSchema"))):
            raise ClientError("schema 响应缺少匹配名称或完整 inputSchema。", EXIT_FORMAT)
        return result
    if args["command"] == "schemas":
        limit = info["max_details"]
        names = payload["tools"]
        if limit > 0 and len(names) > limit:
            raise ClientError("去重后最多一次获取 {} 个 Schema（discovery max_details）；未发送请求。"
                              .format(limit), EXIT_PARAM)
        url = info.get("schemas_url") or base + "/capabilities/schemas"
        result = http(url, args["timeout"], "POST", payload, max_bytes=MAX_RESPONSE_BYTES)
        if (not is_object(result) or not isinstance(result.get("items"), list)
                or type(result.get("count")) is not int or result["count"] != len(result["items"])
                or result["count"] < 1 or (limit > 0 and result["count"] > limit)
                or not response_details_limit(result, limit)):
            raise ClientError("schemas 响应格式或数量无效。", EXIT_FORMAT)
        matched = len(result["items"]) == len(names) and all(
            is_object(item) and item.get("name") == names[index]
            and is_object(item.get("inputSchema"))
            for index, item in enumerate(result["items"]))
        if not matched:
            raise ClientError("schemas 响应的工具名顺序或 inputSchema 与请求不一致。", EXIT_FORMAT)
        return result
    if args["command"] == "find":
        query = payload["query"]
        entries = directory(info, args, args["kind"])
        needle = query.lower()
        matches = []
        for item in entries:
            if not is_object(item):
                continue
            values = [item.get("name"), item.get("uri"), item.get("description")]
            if isinstance(item.get("tags"), list):
                values += item["tags"]
            if any(isinstance(value, str) and needle in value.lower() for value in values):
                matches.append(item)
        return {"kind": args["kind"], "query": query, "matches": matches,
                "count": len(matches), "total": len(entries)}
    if args["command"] == "prompt":
        name = payload["name"]
        if not any(is_object(item) and item.get("name") == name
                   for item in directory(info, args, "prompts")):
            raise ClientError("提示词不在 /prompts 目录中：{}；先运行 list --kind prompts。"
                              .format(name), EXIT_PARAM)
        return rpc_call(info, args, "prompts/get", {"name": name}, 1, "messages")
    if args["command"] == "resource":
        uri = payload["uri"]
        if not any(is_object(item) and item.get("uri") == uri
                   for item in directory(info, args, "resources")):
            raise ClientError("资源 URI 不在 /resources 目录中，禁止读取未注册 URI：" + uri, EXIT_PARAM)
        return rpc_call(info, args, "resources/read", {"uri": uri}, 1, "contents")
    if args["command"] == "call":
        identifier = request_id(args.get("request_id") or uuid.uuid4().hex)
        args["request_id"] = identifier
        return rpc_call(info, args, "tools/call",
                        {"name": args["target"], "arguments": payload, "request_id": identifier}, 1)

    target = args["target"]
    if target.startswith("/") and not target.startswith("//"):
        target = info["base_url"].rstrip("/") + target
    if same_origin and is_object(payload) and payload.get("method") == "tools/call":
        params = payload.get("params")
        if not is_object(params):
            raise ClientError("tools/call 需要 params。", EXIT_PARAM)
        params["request_id"] = request_id(params.get("request_id") or uuid.uuid4().hex)
        args["request_id"] = params["request_id"]
    merged = dict(headers)
    if same_origin:
        merged.update(args["binding"])
    return http(target, args["timeout"], args["method"], payload, merged)


# ---------------------------------------------------------------------------
# 库 API
# ---------------------------------------------------------------------------

def result_text(result):
    content = result.get("content") if is_object(result) else None
    if not isinstance(content, list):
        return ""
    return "".join(item["text"] for item in content
                   if is_object(item) and isinstance(item.get("text"), str))


def result_json(result):
    text = result_text(result)
    if not text:
        raise ClientError("工具结果不含文本内容。", EXIT_FORMAT)
    try:
        return json.loads(text)
    except ValueError:
        raise ClientError("工具结果文本不是 JSON：" + text[:200], EXIT_FORMAT)


def _record_call(entry):
    workflow = _WORKFLOW.get()
    if workflow is not None:
        workflow.calls.append(entry)


class _Workflow:
    """工作流记录器：只记录调用与耗时，不重试、不回滚、不自动重放。"""

    def __init__(self, name, description):
        self.name = name
        self.description = description
        self.calls = []
        self.started_at = datetime.now()
        self.duration_ms = None

    def summary(self):
        succeeded = sum(1 for item in self.calls if item["ok"])
        return {
            "name": self.name,
            "description": self.description,
            "calls": len(self.calls),
            "succeeded": succeeded,
            "failed": len(self.calls) - succeeded,
            "duration_ms": self.duration_ms,
            "steps": list(self.calls),
        }


class Client:
    """一次构造、多次调用；锁定实例，不缓存端口。"""

    def __init__(self, ini=None, timeout=None, verbose=False,
                 expected_project=None, instance_id=None, project=None):
        self.ini = ini or os.path.join(os.path.expanduser("~"), ".unity-mcp", "skill.ini")
        self.timeout = 15 if timeout is None else timeout
        self.verbose = bool(verbose)
        self.expected_project = expected_project_path(expected_project)
        self.instance_id = instance_id
        # 多工程 INI 下按工程名选择目标；未给时回落到环境变量 UNITY_MCP_PROJECT。
        self.project = (project or os.environ.get("UNITY_MCP_PROJECT") or "").strip() or None
        self.connection = None
        if instance_id is not None and (not isinstance(instance_id, str) or not instance_id.strip()):
            raise ClientError("instance_id 不能为空。", EXIT_PARAM)

    def _args(self, command, **overrides):
        args = {
            "command": command, "client": self,
            "ini": self.ini, "timeout": self.timeout,
            "kind": "tools", "headers": [], "method": "GET", "targets": [],
            "json_text": None, "json_file": None, "json_stdin": False,
            "tag": None, "target": None, "request_id": None, "status_instance": None,
            "binding": {},
        }
        args.update(overrides)
        if command == "call":
            args["request_id"] = request_id(args.get("request_id") or uuid.uuid4().hex)
        return args

    def _exec(self, command, **overrides):
        args = self._args(command, **overrides)
        token = _QUIET.set(not self.verbose)
        try:
            return execute(args)
        except ClientError as exc:
            identifier = args.get("request_id")
            if identifier:
                instance = (args.get("binding") or {}).get("X-Mcp-Instance-Id") \
                    or self.instance_id or ""
                detail = {"request_id": identifier, "instance_id": instance,
                          "query_path": query_path(identifier, instance)}
                exc.request_id = identifier
                exc.instance_id = instance
                exc.query_path = detail["query_path"]
                exc.message = exc.message + "；" + json.dumps(detail, ensure_ascii=False)
            raise
        finally:
            _QUIET.reset(token)

    def discover(self):
        return self._exec("discover")

    def groups(self):
        return self._exec("groups")

    def list(self, kind="tools"):
        return self._exec("list", kind=kind)

    def find(self, query, kind="tools"):
        return self._exec("find", target=query, kind=kind)

    def schema(self, name):
        return self._exec("schema", target=name)

    def schemas(self, names):
        return self._exec("schemas", targets=list(names) if isinstance(names, (list, tuple))
                          else [names])

    def details(self, tag=None, members=None):
        if (tag is None) == (members is None):
            raise ClientError("details 需要 tag 或 members，且只能选一种。", EXIT_PARAM)
        if tag is not None:
            return self._exec("details", tag=tag)
        return self._exec("details", json_text=json_source_value(members))

    def prompt(self, name):
        return self._exec("prompt", target=name)

    def resource(self, uri):
        return self._exec("resource", target=uri)

    def request_status(self, identifier, instance_id=None):
        return self._exec("status", target=request_id(identifier), status_instance=instance_id)

    def call(self, name, arguments=None, request_id=None):
        return self._exec("call", target=name, request_id=request_id,
                          json_text=json_source_value({} if arguments is None else arguments))

    def request(self, target, method="GET", headers=None, data=None):
        overrides = {"target": target, "method": method or "GET"}
        if isinstance(headers, (list, tuple)):
            overrides["headers"] = list(headers)
        if data is not None:
            overrides["json_text"] = json_source_value(data)
        return self._exec("request", **overrides)

    def call_skill(self, name, arguments=None, request_id=None):
        """调用工具并返回 MCP ``result`` 对象（不是完整 JSON-RPC 信封）。"""
        started = datetime.now()
        try:
            result = self.call(name, arguments, request_id)["result"]
        except ClientError as exc:
            _record_call({"name": name, "arguments": arguments, "ok": False,
                          "elapsed_ms": _elapsed_ms(started),
                          "error": {"code": exc.code, "message": str(exc)}})
            raise
        _record_call({"name": name, "arguments": arguments, "ok": True,
                      "result": result, "elapsed_ms": _elapsed_ms(started)})
        return result

    def call_skill_text(self, name, arguments=None):
        return result_text(self.call_skill(name, arguments))

    def call_skill_json(self, name, arguments=None):
        return result_json(self.call_skill(name, arguments))


def _elapsed_ms(started):
    return int((datetime.now() - started).total_seconds() * 1000)


def json_source_value(value):
    # 先序列化成文本再交给 input_json，从而继承其有限数/UTF-8 校验。
    try:
        text = json.dumps({} if value is None else value, ensure_ascii=False, allow_nan=False)
    except (TypeError, ValueError) as exc:
        raise ClientError("参数无法序列化为 JSON：{}".format(exc), EXIT_PARAM)
    if not isinstance(text, str):
        raise ClientError("参数无法序列化为 JSON。", EXIT_PARAM)
    return text


_default_client = None


def get_client():
    global _default_client
    if _default_client is None:
        _default_client = Client()
    return _default_client


def configure(**options):
    global _default_client
    _default_client = Client(**options)
    return _default_client


def workflow_context(name, description="", fn=None, **options):
    """把一组程序化调用归入命名工作流；块内 call_skill 自动记录。"""
    if callable(description):
        options = fn if isinstance(fn, dict) else options
        fn = description
        description = ""
    if not callable(fn):
        raise ClientError("workflowContext 需要回调函数。", EXIT_PARAM)
    workflow = _Workflow(name, description or "")
    started = datetime.now()
    token = _WORKFLOW.set(workflow)
    try:
        return fn(workflow)
    finally:
        _WORKFLOW.reset(token)
        workflow.duration_ms = _elapsed_ms(started)
        if options.get("verbose"):
            print("工作流 {}：{} 次调用，耗时 {} ms".format(
                workflow.name, len(workflow.calls), workflow.duration_ms), file=sys.stderr)


def connection_info():
    return get_client().discover()


def get_groups():
    return get_client().groups()


def list_capabilities(kind="tools"):
    return get_client().list(kind)


def find_capabilities(query, kind="tools"):
    return get_client().find(query, kind)


def get_details(**options):
    return get_client().details(**options)


def get_schema(name):
    return get_client().schema(name)


def get_schemas(names):
    return get_client().schemas(names)


def call_tool(name, arguments=None, request_id=None):
    return get_client().call(name, arguments, request_id)


def call_skill(name, arguments=None, request_id=None):
    return get_client().call_skill(name, arguments, request_id)


def call_skill_text(name, arguments=None):
    return get_client().call_skill_text(name, arguments)


def call_skill_json(name, arguments=None):
    return get_client().call_skill_json(name, arguments)


def read_prompt(name):
    return get_client().prompt(name)


def read_resource(uri):
    return get_client().resource(uri)


def request_route(target, **options):
    return get_client().request(target, **options)


def request_status(identifier, instance_id=None):
    return get_client().request_status(identifier, instance_id)


# camelCase 别名：与 unity_bridge.js 同名，便于跨语言沿用同一套调用习惯。
connectionInfo = connection_info
getGroups = get_groups
listCapabilities = list_capabilities
findCapabilities = find_capabilities
getDetails = get_details
getSchema = get_schema
getSchemas = get_schemas
callTool = call_tool
callSkill = call_skill
callSkillText = call_skill_text
callSkillJson = call_skill_json
readPrompt = read_prompt
readResource = read_resource
requestRoute = request_route
requestStatus = request_status
resultText = result_text
resultJson = result_json
normalizeRequestTarget = normalize_request_target
workflowContext = workflow_context
getClient = get_client

COMMANDS = ("discover", "groups", "list", "find", "details", "schema", "schemas",
            "call", "prompt", "resource", "request", "status")


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _add_binding_flags(parser):
    # 置 SUPPRESS 默认值：主命令与子命令两处声明同名选项时不互相覆盖。
    parser.add_argument("--expected-project", dest="expected_project", metavar="PATH",
                        default=argparse.SUPPRESS,
                        help="显式指定 Unity 工程根；否则从 cwd 向上寻找")
    parser.add_argument("--project", dest="project", metavar="NAME",
                        default=argparse.SUPPRESS,
                        help="按工程名选择目标（多工程共用 INI 时使用）")
    parser.add_argument("--instance-id", dest="instance_id", metavar="ID",
                        default=argparse.SUPPRESS,
                        help="锁定实例；跨进程保持绑定用")
    parser.add_argument("--verbose", action="store_true", default=argparse.SUPPRESS,
                        help="打印完整连接信息与限制")


def _common_flags():
    common = argparse.ArgumentParser(add_help=False, allow_abbrev=False)
    _add_binding_flags(common)
    return common


def _json_options(parser, prefix, required=False):
    group = parser.add_mutually_exclusive_group(required=required)
    group.add_argument("--" + prefix, dest="json_text", metavar="JSON", help="内联 JSON")
    group.add_argument("--" + prefix + "-file", dest="json_file", metavar="FILE",
                       help="UTF-8 JSON 文件（支持 BOM）")
    group.add_argument("--" + prefix + "-stdin", dest="json_stdin", action="store_true",
                       help="从标准输入读取 UTF-8 JSON")


def build_parser():
    parser = argparse.ArgumentParser(
        prog="unity_bridge.py", allow_abbrev=False,
        description="Unity MCP 标准库客户端（与 unity_bridge.js 同契约）；每次读取 INI 并验证 discovery，不重试、不跟随重定向。",
        epilog=("退出码：0 成功；2 参数/配置；3 连接/TLS/超时；4 HTTP；5 响应格式；6 MCP 执行失败；130 中断。"
                " 接口说明见 SKILL.md，勿读脚本源码。"))
    parser.add_argument("--ini", default=os.path.join(os.path.expanduser("~"), ".unity-mcp", "skill.ini"),
                        help="连接 INI 路径，默认 ~/.unity-mcp/skill.ini")
    parser.add_argument("--timeout", type=float, default=15,
                        help="每次 HTTP 操作超时秒数，默认 15，须为正有限数")
    parser.add_argument("--version", action="version", version="unity_bridge.py")
    # 与 unity_bridge.js 一致：绑定类选项置于子命令前后皆可。
    _add_binding_flags(parser)
    common = _common_flags()
    sub = parser.add_subparsers(dest="command", metavar="命令")

    sub.add_parser("discover", parents=[common], allow_abbrev=False,
                   help="显式重新确认工程及实例绑定；不重放操作")
    sub.add_parser("groups", parents=[common], allow_abbrev=False,
                   help="读取命名空间/tag 索引及数量限制；禁止猜测标签")
    listing = sub.add_parser("list", parents=[common], allow_abbrev=False,
                             help="仅列目录摘要，不拉详情")
    listing.add_argument("--kind", choices=["tools", "prompts", "resources"], default="tools",
                         help="目录类型，默认 tools")
    sub.add_parser("details", parents=[common], allow_abbrev=False,
                   help="按 tag 或动态成员取详情；上限由 discovery max_details 决定")
    schema = sub.add_parser("schema", parents=[common], allow_abbrev=False,
                            help="读取一个工具的完整 schema")
    schema.add_argument("target", metavar="NAME", help="list 返回的工具名")
    schemas = sub.add_parser("schemas", parents=[common], allow_abbrev=False,
                             help="一次读取多个工具的完整 schema")
    schemas.add_argument("targets", nargs="*", metavar="NAME", help="工具名，可多个")
    _json_options(schemas, "names")
    call = sub.add_parser("call", parents=[common], allow_abbrev=False,
                          help="调用一个工具；先 list/schema，绝不自动重试")
    call.add_argument("target", metavar="NAME", help="工具名")
    call.add_argument("--request-id", dest="request_id", metavar="ID",
                      help="显式请求 ID（1-256 字符）；失败凭 status 查询，不重放")
    _json_options(call, "args")
    finding = sub.add_parser("find", parents=[common], allow_abbrev=False,
                             help="在目录摘要内本地关键字过滤，不拉详情")
    finding.add_argument("target", metavar="QUERY", help="检索关键字，1-128 字符")
    finding.add_argument("--kind", choices=["tools", "prompts", "resources"], default="tools",
                         help="目录类型，默认 tools")
    prompt = sub.add_parser("prompt", parents=[common], allow_abbrev=False,
                            help="读取 prompts/get 正文；名称须来自 list --kind prompts")
    prompt.add_argument("target", metavar="NAME", help="提示词名称")
    resource = sub.add_parser("resource", parents=[common], allow_abbrev=False,
                              help="读取 resources/read 内容；拒绝未注册 URI")
    resource.add_argument("target", metavar="URI", help="资源 URI")
    status = sub.add_parser("status", parents=[common], allow_abbrev=False,
                            help="查询 request_id 状态；结论落盘，重启后仍可查")
    status.add_argument("target", metavar="REQUEST_ID", help="请求 ID")
    generic = sub.add_parser("request", parents=[common], allow_abbrev=False,
                             help="请求自定义路由，或用户明确授权的绝对 http/https URL")
    generic.add_argument("target", metavar="TARGET", help="/ROUTE、裸路由名或绝对 URL")
    generic.add_argument("--method", choices=["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"],
                         default="GET", help="HTTP 方法，默认 GET")
    generic.add_argument("--header", action="append", default=[], metavar="NAME:VALUE",
                         help="额外请求头，可重复；不支持跳过 TLS 验证")
    _json_options(generic, "data")

    # details 同时接受 --tag 与 members 三选一，故单独声明以避免与互斥组冲突。
    details = sub.choices["details"]
    details.add_argument("--tag", metavar="TAG", help="groups 返回的一个实际标签")
    _json_options(details, "members")
    return parser


def _args_from_namespace(ns):
    client = Client(ini=ns.ini, timeout=ns.timeout, verbose=getattr(ns, "verbose", False),
                    expected_project=getattr(ns, "expected_project", None),
                    instance_id=getattr(ns, "instance_id", None),
                    project=getattr(ns, "project", None))
    args = {
        "command": ns.command, "client": client,
        "ini": client.ini, "timeout": client.timeout,
        "kind": getattr(ns, "kind", "tools"),
        "headers": list(getattr(ns, "header", []) or []),
        "method": getattr(ns, "method", "GET"),
        "targets": list(getattr(ns, "targets", []) or []),
        "json_text": getattr(ns, "json_text", None),
        "json_file": getattr(ns, "json_file", None),
        "json_stdin": bool(getattr(ns, "json_stdin", False)),
        "tag": getattr(ns, "tag", None),
        "target": getattr(ns, "target", None),
        "request_id": getattr(ns, "request_id", None),
        "status_instance": getattr(ns, "instance_id", None),
        "binding": {},
    }
    return args


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    parser = build_parser()
    namespace = parser.parse_args(argv)
    if not namespace.command:
        parser.print_help()
        return EXIT_PARAM
    if not math.isfinite(namespace.timeout) or namespace.timeout <= 0:
        print("错误：--timeout 必须为正有限数。", file=sys.stderr)
        return EXIT_PARAM
    args = _args_from_namespace(namespace)
    if args["command"] == "call" and args["request_id"] is not None:
        args["request_id"] = request_id(args["request_id"])
    token = _QUIET.set(False)
    try:
        result = execute(args)
        if isinstance(result, str):
            print(result)
        else:
            print(json.dumps(result, ensure_ascii=False, indent=2))
        return EXIT_OK
    except ClientError as exc:
        print("错误：" + str(exc), file=sys.stderr)
        return exc.code
    except (OSError, ValueError, UnicodeError) as exc:
        print("错误：{}；未重试。".format(exc), file=sys.stderr)
        return EXIT_PARAM
    except KeyboardInterrupt:
        print("已中断；未重试，写操作结果可能未知。", file=sys.stderr)
        return EXIT_INTERRUPTED
    finally:
        _QUIET.reset(token)


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Unity MCP 轻量客户端，仅使用 Python 标准库。"""

import argparse
import configparser
from http.client import HTTPException
import json
import math
import sys
from pathlib import Path
from urllib import error, parse, request


class ClientError(Exception):
    def __init__(self, message, code):
        super().__init__(message)
        self.code = code


class NoRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # 重定向也可能重复提交，交给用户核实后显式调用。
        return None


def checked_url(value):
    try:
        parts = parse.urlsplit(value)
        if (parts.scheme not in ("http", "https") or not parts.hostname
                or parts.username is not None or parts.password is not None
                or parts.fragment or any(c.isspace() for c in value)):
            raise ValueError()
        port = parts.port
        if port is not None and not 1 <= port <= 65535:
            raise ValueError()
    except (ValueError, TypeError):
        raise ClientError("URL 必须是有效的绝对 http/https 地址，不含凭据或片段。", 2)
    return parts


def origin(url):
    parts = checked_url(url)
    return parts.scheme, parts.hostname, parts.port or (443 if parts.scheme == "https" else 80)


def http(url, timeout, method="GET", payload=None, headers=None, max_bytes=None):
    checked_url(url)
    headers = dict(headers or {})
    data = None
    if payload is not None:
        data = json.dumps(payload, ensure_ascii=False, allow_nan=False).encode("utf-8")
        if not any(k.lower() == "content-type" for k in headers):
            headers["Content-Type"] = "application/json; charset=utf-8"
    req = request.Request(url, data=data, headers=headers, method=method)
    try:
        with request.build_opener(NoRedirect()).open(req, timeout=timeout) as response:
            data = response.read() if max_bytes is None else response.read(max_bytes + 1)
            if max_bytes is not None and len(data) > max_bytes:
                raise ClientError("详情响应超过 65536 字节；未重试。", 5)
            text = data.decode("utf-8-sig")
    except error.HTTPError as exc:
        exc.close()
        raise ClientError("HTTP {}：请求失败或重定向被拒绝；未重试。".format(exc.code), 4)
    except (error.URLError, OSError, TimeoutError, HTTPException) as exc:
        raise ClientError("连接或 TLS/超时失败：{}；未重试，写操作结果可能未知。".format(exc), 3)
    except UnicodeError:
        raise ClientError("响应不是 UTF-8 文本。", 5)
    try:
        return json.loads(text)
    except ValueError:
        return text


def discover(ini, timeout):
    config = configparser.ConfigParser(interpolation=None)
    try:
        with Path(ini).expanduser().open(encoding="utf-8-sig") as stream:
            config.read_file(stream)
        section = config["unity3d-skilled-mcp"]
        ini_limit = None
        if "max_details" in section:
            value = section["max_details"]
            digits = value[1:] if value.startswith("+") else value
            if not digits or not digits.isascii() or not digits.isdigit():
                raise ValueError("INI max_details 必须为非负整数，0 表示不限数量")
            ini_limit = int(value)
            if not valid_details_limit(ini_limit):
                raise ValueError("INI max_details 必须为非负安全整数")
        base = section.get("base_url", "").rstrip("/")
        port = section.getint("port", fallback=None)
        if not base:
            if port is None or not 1 <= port <= 65535:
                raise ValueError("INI 缺少有效 base_url 或 port")
            base = "http://localhost:{}".format(port)
        parts = checked_url(base)
        if parts.path or parts.query:
            raise ValueError("base_url 不能带路径或查询参数")
        if port is not None and origin(base)[2] != port:
            raise ValueError("INI 的 base_url 与 port 不一致")
    except (OSError, UnicodeError, configparser.Error, KeyError, ValueError) as exc:
        raise ClientError("无法读取连接配置 {}：{}；请在 Unity 中启动服务。".format(ini, exc), 2)
    info = http(base + "/discovery", timeout)
    if not isinstance(info, dict):
        raise ClientError("discovery 响应必须是 JSON 对象。", 5)
    try:
        if origin(info["base_url"]) != origin(base) or info["port"] != origin(base)[2]:
            raise ValueError("discovery 与 INI 端口/地址不一致")
        for key in ("tools_url", "schema_url_template"):
            if origin(info[key]) != origin(base):
                raise ValueError("discovery 端点不在同一服务")
        if "{name}" not in info["schema_url_template"]:
            raise ValueError("schema URL 缺少 {name}")
        for key in ("project_path", "updated_at"):
            if not isinstance(info[key], str):
                raise ValueError("discovery 字段类型错误：" + key)
        if type(info["process_id"]) is not int:
            raise ValueError("discovery process_id 类型错误")
        if "max_details" in info and not valid_details_limit(info["max_details"]):
            raise ValueError("discovery max_details 必须为非负整数")
    except (KeyError, TypeError, ValueError, ClientError) as exc:
        raise ClientError("无效 discovery：{}".format(exc), 5)
    if ini_limit is not None and "max_details" in info and ini_limit != info["max_details"]:
        raise ClientError("INI max_details 与 discovery 不一致；请重启目标 Unity 服务以发布启动配置。", 2)
    # 老服务缺字段时使用 INI；两处都缺失才采用默认值，不抬高服务显式限制。
    info.setdefault("max_details", ini_limit if ini_limit is not None else DEFAULT_MAX_DETAILS)
    print("连接信息：" + json.dumps(info, ensure_ascii=False), file=sys.stderr)
    return info


def details_limit(info):
    """运行期限制只来自服务端发布；兼容缺少字段的旧服务，不抬高显式限制。"""
    value = info.get("max_details", DEFAULT_MAX_DETAILS)
    if not valid_details_limit(value):
        raise ClientError("discovery max_details 必须为非负整数", 5)
    return value


def input_json(args):
    try:
        if args.json_file is not None:
            text = Path(args.json_file).read_text(encoding="utf-8-sig")
        elif args.json_stdin:
            text = sys.stdin.buffer.read().decode("utf-8-sig")
        else:
            text = args.json_text
        if text is None:
            return None
        return json.loads(text, parse_constant=lambda value: invalid_constant(value))
    except (OSError, UnicodeError, ValueError) as exc:
        raise ClientError("无法读取 UTF-8 JSON 输入：{}".format(exc), 2)


def invalid_constant(value):
    raise ValueError("JSON 不允许 " + value)


DEFAULT_MAX_DETAILS = 32
MAX_RESPONSE_BYTES = 65536


def valid_details_limit(value):
    return type(value) is int and 0 <= value <= 9007199254740991


def response_details_limit(result, limit):
    # 旧响应省略该字段时继承 discovery 的有效限制；显式发布则必须一致。
    return ("max_details" not in result
            or (valid_details_limit(result["max_details"]) and result["max_details"] == limit))


def selection_value(value):
    return (isinstance(value, str) and bool(value.strip()) and value == value.strip()
            and value.lower() != "all"
            and not any(ord(c) < 32 or ord(c) == 127 or c == "*" for c in value))


def details_payload(args):
    has_members = args.json_text is not None or args.json_file is not None or args.json_stdin
    if (args.tag is not None) == bool(has_members):
        raise ClientError("details 必须选择 --tag 或一种 --members 输入，不能同时使用。", 2)
    if args.tag is not None:
        if len(args.tag) != 1 or not selection_value(args.tag[0]):
            raise ClientError("tag 只能指定一次，不能为空或 all/*；从 groups 获取实际标签。", 2)
        return {"tag": args.tag[0]}
    members = input_json(args)
    if not isinstance(members, list) or not members:
        raise ClientError("members 必须是非空 JSON 数组。", 2)
    unique = {}
    for member in members:
        if not isinstance(member, dict) or member.get("kind") not in ("tool", "prompt", "resource"):
            raise ClientError("成员 kind 仅允许 tool、prompt、resource。", 2)
        key = "uri" if member["kind"] == "resource" else "name"
        if set(member) != {"kind", key} or not selection_value(member[key]):
            raise ClientError("成员仅允许 kind 和 {}，值不能为空或 all/*。".format(key), 2)
        if key == "uri":
            scheme, sep, rest = member[key].partition(":")
            if (not sep or not rest or not scheme or not scheme[0].isascii() or not scheme[0].isalpha()
                    or any(not c.isascii() or not (c.isalnum() or c in "+.-") for c in scheme)
                    or any(c.isspace() for c in member[key])):
                raise ClientError("资源 uri 必须是带 scheme 的完整 URI；不读取资源内容。", 2)
        unique[(member["kind"], member[key])] = member
    return {"members": list(unique.values())}


def add_json_options(parser, prefix, required=False):
    group = parser.add_mutually_exclusive_group(required=required)
    group.add_argument("--" + prefix, dest="json_text", help="内联 JSON")
    group.add_argument("--" + prefix + "-file", dest="json_file", help="UTF-8 JSON 文件（支持 BOM）")
    group.add_argument("--" + prefix + "-stdin", dest="json_stdin", action="store_true", help="从标准输入读取 UTF-8 JSON")
    return group


def build_parser():
    parser = argparse.ArgumentParser(
        allow_abbrev=False,
        description="Unity MCP 标准库客户端；每次读取 INI 并验证 discovery，不重试、不跟随重定向。",
        epilog="退出码：0 成功；2 参数/配置；3 连接/TLS/超时；4 HTTP；5 响应格式；6 MCP 执行失败。")
    parser.add_argument("--ini", default=str(Path.home() / ".unity-mcp" / "skill.ini"), help="连接 INI 路径，默认 ~/.unity-mcp/skill.ini")
    parser.add_argument("--timeout", type=float, default=15, help="每次 HTTP 操作超时秒数，默认 15，须为正有限数")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("discover", help="读取 INI，HTTP 验证并输出连接信息")
    sub.add_parser("groups", help="读取固定 tag 分组及数量限制；禁止猜测标签")
    listing = sub.add_parser("list", help="仅列出启用项的目录摘要，不拉取详情")
    listing.add_argument("--kind", choices=["tools", "prompts", "resources"], default="tools", help="目录类型，默认 tools")
    details = sub.add_parser("details", help="按 tag 或本次动态集合获取详情；去重成员上限由 discovery max_details 决定（默认32，0不限数量），最多65536字节，禁止全量选择")
    selection = add_json_options(details, "members", required=True)
    selection.add_argument("--tag", action="append", help="groups 返回的一个实际标签，与 members 输入互斥")
    schema = sub.add_parser("schema", help="读取一个工具的完整 MCP schema")
    schema.add_argument("name", help="list 返回的工具名称")
    call = sub.add_parser("call", help="调用一个工具；先 list，再 schema；绝不自动重试")
    call.add_argument("name", help="工具名称")
    add_json_options(call, "args")
    generic = sub.add_parser("request", help="请求 Unity 自定义路由，或用户明确授权的绝对 http/https URL")
    generic.add_argument("target", help="以 / 开头的服务路由，或绝对 http/https URL")
    generic.add_argument("--method", choices=["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], default="GET", help="HTTP 方法，默认 GET")
    generic.add_argument("--header", action="append", default=[], metavar="NAME:VALUE", help="额外请求头，可重复；不支持跳过 TLS 验证")
    add_json_options(generic, "data")
    for command_parser in sub.choices.values():
        command_parser.allow_abbrev = False
    return parser


def execute(args):
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        raise ClientError("--timeout 必须为正有限数。", 2)
    payload = None
    headers = {}
    if args.command in ("call", "request"):
        payload = input_json(args)
        if args.command == "call":
            if payload is None and args.json_text is None and args.json_file is None and not args.json_stdin:
                payload = {}
            if not isinstance(payload, dict):
                raise ClientError("call 参数必须是 JSON 对象。", 2)
        else:
            for header in args.header:
                name, sep, value = header.partition(":")
                if not sep or not name.strip() or "\n" in header or "\r" in header:
                    raise ClientError("请求头格式应为 NAME:VALUE，不能包含换行。", 2)
                headers[name.strip()] = value.strip()
    if args.command == "details":
        payload = details_payload(args)
    info = discover(args.ini, args.timeout)
    if args.command == "discover":
        return info
    if args.command in ("groups", "list", "details"):
        base = info["base_url"].rstrip("/")
    if args.command == "groups":
        result = http(base + "/groups", args.timeout)
        limit = details_limit(info)
        if (not isinstance(result, dict) or not isinstance(result.get("groups"), list)
                or not response_details_limit(result, limit)
                or result.get("max_response_bytes") != MAX_RESPONSE_BYTES):
            raise ClientError("groups 响应格式或限制无效。", 5)
        print("组详情数量限制：{}；仍受 65536 字节与禁止全集规则约束。".format(
            "不限数量" if limit == 0 else limit), file=sys.stderr)
        return result
    if args.command == "list":
        result = http(info["tools_url"] if args.kind == "tools" else base + "/" + args.kind, args.timeout)
        if not isinstance(result, dict) or not isinstance(result.get(args.kind), list):
            raise ClientError(args.kind + " 响应缺少目录列表。", 5)
        return result
    if args.command == "details":
        limit = details_limit(info)
        if limit > 0 and "members" in payload and len(payload["members"]) > limit:
            raise ClientError("去重后最多选择 {} 个成员（discovery max_details）；未发送详情请求。".format(limit), 2)
        result = http(base + "/capabilities/details", args.timeout, "POST", payload, max_bytes=MAX_RESPONSE_BYTES)
        if (not isinstance(result, dict) or not isinstance(result.get("items"), list)
                or type(result.get("count")) is not int or result["count"] != len(result["items"])
                or result["count"] < 1 or (limit > 0 and result["count"] > limit)
                or not response_details_limit(result, limit)
                or not all(isinstance(item, dict) for item in result["items"])):
            raise ClientError("details 响应格式或成员数量无效。", 5)
        return result
    if args.command == "schema":
        result = http(info["schema_url_template"].replace("{name}", parse.quote(args.name, safe="")), args.timeout)
        if not isinstance(result, dict) or result.get("name") != args.name or not isinstance(result.get("inputSchema"), dict):
            raise ClientError("schema 响应缺少匹配名称或完整 inputSchema。", 5)
        return result
    if args.command == "call":
        envelope = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": args.name, "arguments": payload}}
        result = http(info["base_url"].rstrip("/") + "/mcp", args.timeout, "POST", envelope)
        if not isinstance(result, dict) or result.get("jsonrpc") != "2.0" or result.get("id") != 1:
            raise ClientError("无效 JSON-RPC 响应。", 5)
        if "error" in result:
            raise ClientError("JSON-RPC error：" + json.dumps(result["error"], ensure_ascii=False), 6)
        if not isinstance(result.get("result"), dict):
            raise ClientError("JSON-RPC 响应缺少 result 对象。", 5)
        if result["result"].get("isError"):
            raise ClientError("MCP isError：" + json.dumps(result["result"], ensure_ascii=False), 6)
        return result
    target = args.target
    if target.startswith("/") and not target.startswith("//"):
        target = info["base_url"].rstrip("/") + target
    checked_url(target)
    return http(target, args.timeout, args.method, payload, headers)


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    args = build_parser().parse_args(argv)
    try:
        result = execute(args)
        print(json.dumps(result, ensure_ascii=False, indent=2) if not isinstance(result, str) else result)
        return 0
    except ClientError as exc:
        print("错误：" + str(exc), file=sys.stderr)
        return exc.code
    except (OSError, ValueError, UnicodeError) as exc:
        print("错误：{}；未重试。".format(exc), file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("已中断；未重试，写操作结果可能未知。", file=sys.stderr)
        return 130


if __name__ == "__main__":
    sys.exit(main())
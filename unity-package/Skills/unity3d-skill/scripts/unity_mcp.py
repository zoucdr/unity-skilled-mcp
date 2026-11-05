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
        for key in ("details_url", "schemas_url"):
            if key in info and origin(info[key]) != origin(base):
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


def schema_names(args):
    """多 Schema 请求的名称列表：位置参数或 --names 三选一；去重后保持顺序。"""
    has_json = args.json_text is not None or args.json_file is not None or args.json_stdin
    if args.names_pos:
        if has_json:
            raise ClientError("schemas 的名称列表与 --names 输入只能二选一。", 2)
        values = args.names_pos
    else:
        values = input_json(args)
        if not isinstance(values, list) or not values:
            raise ClientError("names 必须是非空 JSON 字符串数组。", 2)
    unique = []
    seen = set()
    for name in values:
        if not selection_value(name):
            raise ClientError("工具名必须是非空字符串，禁止 all/* 或控制字符。", 2)
        if name not in seen:
            seen.add(name)
            unique.append(name)
    return unique


def find_query(value):
    if (not isinstance(value, str) or not value.strip() or value != value.strip()
            or len(value) > 128 or any(ord(c) < 32 or ord(c) == 127 for c in value)):
        raise ClientError("检索关键字须为 1-128 个非控制字符，且不含首尾空白。", 2)
    return value


def calls_payload(args):
    value = input_json(args)
    if not isinstance(value, list) or not value:
        raise ClientError("calls 必须是非空 JSON 数组。", 2)
    steps = []
    for index, item in enumerate(value):
        if (not isinstance(item, dict) or not selection_value(item.get("name"))
                or set(item) - {"name", "arguments"}
                or ("arguments" in item and not isinstance(item["arguments"], dict))):
            raise ClientError("第 {} 个调用只能是 {{name, arguments?}}，arguments 必须是 JSON 对象。".format(index + 1), 2)
        steps.append({"name": item["name"], "arguments": item.get("arguments") or {}})
    return steps


def rpc_result(envelope, rpc_id, expected_array=None):
    if (not isinstance(envelope, dict) or envelope.get("jsonrpc") != "2.0"
            or envelope.get("id") != rpc_id):
        raise ClientError("无效 JSON-RPC 响应。", 5)
    if "error" in envelope:
        raise ClientError("JSON-RPC error：" + json.dumps(envelope["error"], ensure_ascii=False), 6)
    result = envelope.get("result")
    if not isinstance(result, dict):
        raise ClientError("JSON-RPC 响应缺少 result 对象。", 5)
    if result.get("isError"):
        raise ClientError("MCP isError：" + json.dumps(result, ensure_ascii=False), 6)
    if expected_array and not isinstance(result.get(expected_array), list):
        raise ClientError("JSON-RPC 响应缺少 {} 数组。".format(expected_array), 5)
    return envelope


def rpc_call(info, args, method, params, rpc_id, expected_array=None):
    envelope = {"jsonrpc": "2.0", "id": rpc_id, "method": method, "params": params}
    result = http(info["base_url"].rstrip("/") + "/mcp", args.timeout, "POST", envelope)
    return rpc_result(result, rpc_id, expected_array)


def directory(info, args, kind):
    url = info["tools_url"] if kind == "tools" else info["base_url"].rstrip("/") + "/" + kind
    result = http(url, args.timeout)
    if not isinstance(result, dict) or not isinstance(result.get(kind), list):
        raise ClientError(kind + " 响应缺少目录列表。", 5)
    return result[kind]


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
    schemas = sub.add_parser("schemas", help="一次读取多个工具的完整 MCP schema；去重上限由 discovery max_details 决定（默认32，0不限数量），最多65536字节，禁止全量选择")
    schemas.add_argument("names_pos", nargs="*", metavar="NAME", help="工具名称，可传多个")
    add_json_options(schemas, "names")
    call = sub.add_parser("call", help="调用一个工具；先 list，再 schema；绝不自动重试")
    call.add_argument("name", help="工具名称")
    add_json_options(call, "args")
    finding = sub.add_parser("find", help="在目录摘要中按名称/描述/标签/URI 做本地关键字过滤，不拉取详情")
    finding.add_argument("target", metavar="QUERY", help="检索关键字，1-128 字符")
    finding.add_argument("--kind", choices=["tools", "prompts", "resources"], default="tools", help="目录类型，默认 tools")
    prompt = sub.add_parser("prompt", help="名称来自 list --kind prompts；读取 prompts/get 消息正文")
    prompt.add_argument("target", metavar="NAME", help="提示词名称")
    res = sub.add_parser("resource", help="URI 来自 list --kind resources；读取 resources/read 内容，拒绝未注册 URI")
    res.add_argument("target", metavar="URI", help="资源 URI")
    calls = sub.add_parser("calls", help="顺序批量调用工具；失败即停并保留已执行步骤，上限由 max_details 决定")
    add_json_options(calls, "calls", required=True)
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
    if args.command == "schemas":
        payload = {"tools": schema_names(args)}
    if args.command == "calls":
        payload = calls_payload(args)
    # 名称/关键字/URI 的格式校验在 discovery 之前完成，非法输入不产生任何网络请求。
    if args.command == "find":
        payload = {"query": find_query(args.target)}
    if args.command == "prompt":
        if not selection_value(args.target):
            raise ClientError("提示词名称无效；从 list --kind prompts 选取。", 2)
        payload = {"name": args.target}
    if args.command == "resource":
        uri = args.target
        scheme, sep, rest = uri.partition(":")
        valid_scheme = (sep and rest and scheme and scheme[0].isascii() and scheme[0].isalpha()
                        and all(c.isascii() and (c.isalnum() or c in "+.-") for c in scheme)
                        and not any(c.isspace() for c in uri))
        if not selection_value(uri) or not valid_scheme:
            raise ClientError("资源 URI 必须是带 scheme 的完整 URI。", 2)
        payload = {"uri": uri}
    info = discover(args.ini, args.timeout)
    if args.command == "discover":
        return info
    if args.command in ("groups", "list", "details", "schemas"):
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
    if args.command == "schemas":
        limit = details_limit(info)
        names = payload["tools"]
        if limit > 0 and len(names) > limit:
            raise ClientError("去重后最多一次获取 {} 个 Schema（discovery max_details）；未发送请求。".format(limit), 2)
        result = http(info.get("schemas_url") or base + "/capabilities/schemas",
                      args.timeout, "POST", payload, max_bytes=MAX_RESPONSE_BYTES)
        if (not isinstance(result, dict) or not isinstance(result.get("items"), list)
                or type(result.get("count")) is not int or result["count"] != len(result["items"])
                or result["count"] < 1 or (limit > 0 and result["count"] > limit)
                or not response_details_limit(result, limit)):
            raise ClientError("schemas 响应格式或数量无效。", 5)
        matched = len(result["items"]) == len(names) and all(
            isinstance(item, dict) and item.get("name") == names[index] and isinstance(item.get("inputSchema"), dict)
            for index, item in enumerate(result["items"]))
        if not matched:
            raise ClientError("schemas 响应的工具名顺序或 inputSchema 与请求不一致。", 5)
        return result
    if args.command == "find":
        query = payload["query"]
        entries = directory(info, args, args.kind)
        needle = query.lower()
        matches = []
        for item in entries:
            if not isinstance(item, dict):
                continue
            values = [item.get("name"), item.get("uri"), item.get("description")]
            values += item.get("tags") if isinstance(item.get("tags"), list) else []
            if any(isinstance(v, str) and needle in v.lower() for v in values):
                matches.append(item)
        return {"kind": args.kind, "query": query, "matches": matches,
                "count": len(matches), "total": len(entries)}
    if args.command == "prompt":
        name = payload["name"]
        if not any(isinstance(item, dict) and item.get("name") == name
                   for item in directory(info, args, "prompts")):
            raise ClientError("提示词不在 /prompts 目录中：{}；先运行 list --kind prompts。".format(name), 2)
        return rpc_call(info, args, "prompts/get", {"name": name}, 1, "messages")
    if args.command == "resource":
        uri = payload["uri"]
        if not any(isinstance(item, dict) and item.get("uri") == uri
                   for item in directory(info, args, "resources")):
            raise ClientError("资源 URI 不在 /resources 目录中，禁止读取未注册 URI：" + uri, 2)
        return rpc_call(info, args, "resources/read", {"uri": uri}, 1, "contents")
    if args.command == "calls":
        limit = details_limit(info)
        if limit > 0 and len(payload) > limit:
            raise ClientError("一次最多 {} 个调用（discovery max_details）；未发送任何调用。".format(limit), 2)
        if len(json.dumps(payload, ensure_ascii=False).encode("utf-8")) > MAX_RESPONSE_BYTES:
            raise ClientError("批量调用请求体超过 65536 字节；未发送任何调用。", 2)
        steps = []
        exit_code = 0
        stopped_at = None
        for index, step in enumerate(payload):
            try:
                envelope = rpc_call(info, args, "tools/call",
                                    {"name": step["name"], "arguments": step["arguments"]}, index + 1)
                steps.append({"index": index, "name": step["name"], "ok": True, "result": envelope["result"]})
            except ClientError as exc:
                steps.append({"index": index, "name": step["name"], "ok": False,
                              "error": {"code": exc.code, "message": str(exc)}})
                exit_code = exc.code
                stopped_at = index
                break
        print("批量调用：已执行 {}/{} 步{}".format(
            len(steps), len(payload), "，全部完成。" if stopped_at is None else " 后停止；已生效的写操作不可盲目重放。"),
            file=sys.stderr)
        return {"steps": steps, "executed": len(steps), "total": len(payload),
                "stopped_at": stopped_at, "exit_code": exit_code}
    if args.command == "call":
        return rpc_call(info, args, "tools/call", {"name": args.name, "arguments": payload}, 1)
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
        # calls 即使中途失败也输出已执行步骤，用退出码区分「全部成功」与「已停止」。
        if args.command == "calls" and isinstance(result, dict):
            return int(result.get("exit_code", 0))
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
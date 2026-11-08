#!/usr/bin/env python3
"""unity_bridge.py 回归测试：仅用标准库。运行 ``python tests/test_unity_bridge.py``。"""

import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "scripts" / "unity_bridge.py"
sys.path.insert(0, str(ROOT / "scripts"))

import unity_bridge  # noqa: E402

PROJECT = str((ROOT / "工程目录").resolve())
INSTANCE = "instance-1"


class Fixture:
    """最小 Unity 服务：记录每次请求，可按用例切换响应模式。"""

    def __init__(self):
        self.requests = []
        self.mode = "ok"
        self.discovery_overrides = {}
        self.limit = 8
        self.schema_calls = 0
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *args):
                pass

            def _handle(self):
                length = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(length) if length else b""
                entry = {
                    "method": self.command,
                    "url": self.path,
                    "headers": {key.lower(): value for key, value in self.headers.items()},
                    "body": json.loads(body.decode("utf-8")) if body else None,
                }
                fixture.requests.append(entry)
                payload = fixture.route(entry)
                data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            do_GET = _handle
            do_POST = _handle
            do_PUT = _handle
            do_DELETE = _handle

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        self.base = "http://127.0.0.1:{}".format(self.port)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

        self.dir = tempfile.mkdtemp(prefix="unity-mcp-py-")
        self.ini = os.path.join(self.dir, "skill.ini")
        Path(self.ini).write_text(
            "[unity3d-skilled-mcp]\nbase_url={}\nport={}\nproject_path={}\nmax_details={}\n".format(
                self.base, self.port, PROJECT, self.limit),
            encoding="utf-8")

    def close(self):
        self.server.shutdown()
        self.server.server_close()

    def discovery(self):
        info = {
            "base_url": self.base, "port": self.port,
            "tools_url": self.base + "/tools",
            "schema_url_template": self.base + "/tools/{name}/schema",
            "details_url": self.base + "/capabilities/details",
            "schemas_url": self.base + "/capabilities/schemas",
            "project_path": PROJECT, "instance_id": INSTANCE,
            "process_id": 4321, "updated_at": "now", "max_details": self.limit,
            "skill_dir": str(ROOT), "unknown_extension": "kept",
        }
        info.update(self.discovery_overrides)
        return info

    def route(self, entry):
        url = entry["url"]
        limit = self.discovery_overrides.get("max_details", self.limit)
        if url == "/discovery":
            return self.discovery()
        if url == "/tools":
            return {"tools": [{"name": "demo.echo", "description": "回显", "tags": ["demo"]}],
                    "count": 1}
        if url == "/tools/demo.echo/schema":
            return {"name": "demo.echo", "description": "回显",
                    "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}}}
        if url == "/groups":
            return {"namespaces": {"_": ["get_prompt"]}, "tags": {"demo": ["demo"]},
                    "labels": {}, "prompts": [], "resources": [],
                    "max_details": limit, "max_response_bytes": 65536}
        if url == "/prompts":
            return {"prompts": [{"name": "hello", "description": "问候", "tags": []}], "count": 1}
        if url == "/resources":
            return {"resources": [{"uri": "file://a.yaml", "name": "a", "description": "d",
                                   "mimeType": "text/yaml", "tags": []}], "count": 1}
        if url == "/capabilities/details":
            body = entry["body"]
            members = body.get("members") or (
                [{"kind": "tool", "name": "demo.echo"}] if "tag" in body else [])
            return {"items": [{"kind": "tool", "name": item["name"]} for item in members],
                    "count": len(members), "max_details": limit}
        if url == "/capabilities/schemas":
            names = entry["body"]["tools"]
            return {"items": [{"name": name, "description": "", "inputSchema": {"type": "object"}}
                              for name in names],
                    "count": len(names), "max_details": limit}
        if url.startswith("/requests/"):
            return {"request_id": url.split("/requests/")[1].split("?")[0],
                    "status": "completed", "persisted": True}
        if url == "/mcp":
            method = entry["body"]["method"]
            if method == "prompts/get":
                return {"jsonrpc": "2.0", "id": 1, "result": {"messages": [{"role": "user"}]}}
            if method == "resources/read":
                return {"jsonrpc": "2.0", "id": 1, "result": {"contents": [{"text": "内容"}]}}
            if self.mode == "rpc-error":
                return {"jsonrpc": "2.0", "id": 1, "error": {"code": -32602, "message": "坏参数"}}
            if self.mode == "is-error":
                return {"jsonrpc": "2.0", "id": 1, "result": {"isError": True, "content": []}}
            params = entry["body"]["params"]
            return {"jsonrpc": "2.0", "id": 1, "result": {
                "content": [{"type": "text",
                             "text": json.dumps({"echo": params["arguments"],
                                                 "request_id": params["request_id"]},
                                                ensure_ascii=False)}]}}
        return {"route": url}


class Base(unittest.TestCase):
    def setUp(self):
        self.fx = Fixture()
        self.client = unity_bridge.Client(ini=self.fx.ini, expected_project=PROJECT)
        self.addCleanup(self.fx.close)

    def cli(self, *argv, **kwargs):
        proc = subprocess.run([sys.executable, str(SCRIPT)] + list(argv),
                              capture_output=True, text=True,
                              encoding="utf-8", errors="replace", **kwargs)
        return proc

    def posts(self):
        return [entry for entry in self.fx.requests if entry["method"] == "POST"]


class TestDiscover(Base):
    def test_保留扩展字段且忽略旧脚本语言(self):
        Path(self.fx.ini).write_text(
            "[unity3d-skilled-mcp]\nbase_url={}\nport={}\nproject_path={}\nmax_details={}\n"
            "script_language=py\n".format(self.fx.base, self.fx.port, PROJECT, self.fx.limit),
            encoding="utf-8")
        info = self.client.discover()
        self.assertEqual("kept", info["unknown_extension"])
        self.assertEqual(INSTANCE, info["instance_id"])

    def test_缺少实例标识即拒绝(self):
        self.fx.discovery_overrides = {"instance_id": ""}
        with self.assertRaises(unity_bridge.ClientError) as ctx:
            self.client.discover()
        self.assertEqual(unity_bridge.EXIT_FORMAT, ctx.exception.code)

    def test_上限不一致要求重启(self):
        self.fx.discovery_overrides = {"max_details": 99}
        with self.assertRaises(unity_bridge.ClientError) as ctx:
            self.client.discover()
        self.assertEqual(unity_bridge.EXIT_PARAM, ctx.exception.code)

    def test_跨源端点拒绝(self):
        self.fx.discovery_overrides = {"tools_url": "http://evil.example/tools"}
        with self.assertRaises(unity_bridge.ClientError) as ctx:
            self.client.discover()
        self.assertEqual(unity_bridge.EXIT_FORMAT, ctx.exception.code)


class TestBinding(Base):
    def test_绑定头与工程编码正确(self):
        self.client.call("demo.echo", {"text": "hi"})
        call = self.posts()[0]
        self.assertEqual(unity_bridge.parse.quote(PROJECT, safe=""),
                         call["headers"]["x-mcp-expected-project"])
        self.assertEqual(INSTANCE, call["headers"]["x-mcp-instance-id"])
        self.assertTrue(call["body"]["params"]["request_id"])

    def test_错工程拒绝且不发送请求(self):
        client = unity_bridge.Client(ini=self.fx.ini, expected_project=os.path.dirname(PROJECT))
        with self.assertRaises(unity_bridge.ClientError) as ctx:
            client.call("demo.echo", {})
        self.assertEqual(unity_bridge.EXIT_PARAM, ctx.exception.code)
        self.assertEqual([], self.posts())

    def test_实例漂移拒绝且不重放(self):
        self.client.discover()
        self.client.instance_id = "old-instance"
        with self.assertRaises(unity_bridge.ClientError) as ctx:
            self.client.call("demo.echo", {})
        self.assertEqual(unity_bridge.EXIT_PARAM, ctx.exception.code)
        self.assertIn("实例漂移", str(ctx.exception))
        self.assertEqual([], self.posts())

    def test_显式请求ID与落盘查询信息(self):
        envelope = self.client.call("demo.echo", {}, request_id="fixed-1")
        text = envelope["result"]["content"][0]["text"]
        self.assertEqual("fixed-1", json.loads(text)["request_id"])
        self.assertEqual("fixed-1", self.posts()[0]["body"]["params"]["request_id"])

    def test_目录查询允许错工程(self):
        client = unity_bridge.Client(ini=self.fx.ini, expected_project=os.path.dirname(PROJECT))
        tools = client.list("tools")
        self.assertEqual(1, tools["count"])


class TestStatus(Base):
    def test_带实例查询状态(self):
        result = self.client.request_status("abc", INSTANCE)
        self.assertEqual("completed", result["status"])
        self.assertIn("/requests/abc?instance_id=instance-1", self.fx.requests[-1]["url"])

    def test_无实例拒绝(self):
        with self.assertRaises(unity_bridge.ClientError) as ctx:
            self.client.request_status("abc")
        self.assertEqual(unity_bridge.EXIT_PARAM, ctx.exception.code)


class TestPayloadLimits(Base):
    def test_schemas_去重保序且单次请求(self):
        result = self.client.schemas(["a", "b", "a"])
        self.assertEqual(["a", "b"], [item["name"] for item in result["items"]])
        self.assertEqual(1, len(self.posts()))

    def test_schemas_超上限在请求前拒绝(self):
        names = ["t{}".format(index) for index in range(9)]
        with self.assertRaises(unity_bridge.ClientError) as ctx:
            self.client.schemas(names)
        self.assertEqual(unity_bridge.EXIT_PARAM, ctx.exception.code)
        self.assertEqual([], self.posts())

    def test_max_details_为零不限数量(self):
        self.fx.discovery_overrides = {"max_details": 0}
        Path(self.fx.ini).write_text(
            "[unity3d-skilled-mcp]\nbase_url={}\nport={}\nproject_path={}\nmax_details=0\n"
            .format(self.fx.base, self.fx.port, PROJECT), encoding="utf-8")
        result = self.client.schemas(["t{}".format(index) for index in range(40)])
        self.assertEqual(40, result["count"])

    def test_details_互斥与成员去重(self):
        with self.assertRaises(unity_bridge.ClientError):
            self.client.details(tag="demo", members=[])
        result = self.client.details(members=[{"kind": "tool", "name": "a"},
                                             {"kind": "tool", "name": "a"}])
        self.assertEqual(1, result["count"])

    def test_详情成员字段校验(self):
        for member in ({"kind": "tool", "name": "a", "extra": 1},
                       {"kind": "tool", "name": "all"},
                       {"kind": "resource", "uri": "no-scheme"}):
            with self.assertRaises(unity_bridge.ClientError):
                self.client.details(members=[member])
        self.assertEqual([], self.posts())


class TestDirectories(Base):
    def test_find_只取摘要并本地过滤(self):
        result = self.client.find("echo")
        self.assertEqual(1, result["count"])
        self.assertEqual(1, result["total"])
        self.assertEqual([], self.posts())

    def test_prompt_与_resource_先验目录(self):
        self.assertEqual(1, len(self.client.prompt("hello")["result"]["messages"]))
        self.assertEqual("内容", self.client.resource("file://a.yaml")["result"]["contents"][0]["text"])
        with self.assertRaises(unity_bridge.ClientError):
            self.client.prompt("unknown")

    def test_groups_接受命名空间与标签索引(self):
        result = self.client.groups()
        self.assertIn("namespaces", result)
        self.assertEqual(["demo"], result["tags"]["demo"])


class TestErrors(Base):
    def test_JSON_RPC_error_与_isError_映射为6(self):
        for mode in ("rpc-error", "is-error"):
            self.fx.mode = mode
            with self.assertRaises(unity_bridge.ClientError) as ctx:
                self.client.call("demo.echo", {})
            self.assertEqual(unity_bridge.EXIT_MCP, ctx.exception.code)

    def test_参数非法在网络前失败(self):
        with self.assertRaises(unity_bridge.ClientError):
            self.client.call("demo.echo", [1])
        with self.assertRaises(unity_bridge.ClientError):
            self.client.find(" ")
        self.assertEqual([], self.fx.requests)

    def test_连接失败返回3(self):
        Path(self.fx.ini).write_text(
            "[unity3d-skilled-mcp]\nbase_url=http://127.0.0.1:1\nport=1\nproject_path={}\n"
            .format(PROJECT), encoding="utf-8")
        with self.assertRaises(unity_bridge.ClientError) as ctx:
            unity_bridge.Client(ini=self.fx.ini, expected_project=PROJECT).discover()
        self.assertEqual(unity_bridge.EXIT_CONNECTION, ctx.exception.code)


class TestNormalizeTarget(unittest.TestCase):
    def test_三种形态(self):
        self.assertEqual("/groups", unity_bridge.normalize_request_target("/groups"))
        self.assertEqual("/groups", unity_bridge.normalize_request_target("groups"))
        self.assertEqual("http://localhost:8000/groups",
                         unity_bridge.normalize_request_target("http://localhost:8000/groups"))

    def test_拒绝协议相对与非法字符(self):
        for value in ("//host/x", "/a b", "/a#b"):
            with self.assertRaises(unity_bridge.ClientError):
                unity_bridge.normalize_request_target(value)

    def test_其他协议留给URL校验拒绝(self):
        self.assertEqual("file:///etc/passwd",
                         unity_bridge.normalize_request_target("file:///etc/passwd"))
        with self.assertRaises(unity_bridge.ClientError):
            unity_bridge.checked_url("file:///etc/passwd")

    def test_还原MSYS路径(self):
        previous = os.environ.get("EXEPATH")
        os.environ["EXEPATH"] = "C:/Program Files/Git/bin"
        try:
            self.assertEqual("/groups",
                             unity_bridge.normalize_request_target(
                                 "C:\\Program Files\\Git\\groups"))
        finally:
            if previous is None:
                os.environ.pop("EXEPATH", None)
            else:
                os.environ["EXEPATH"] = previous


class TestLibraryApi(Base):
    def test_与JS导出名一一对应(self):
        js_names = [
            "Client", "ClientError", "configure", "getClient",
            "connectionInfo", "getGroups", "listCapabilities", "findCapabilities", "getDetails",
            "getSchema", "getSchemas", "callTool", "callSkill", "callSkillText", "callSkillJson",
            "readPrompt", "readResource", "requestRoute", "requestStatus", "workflowContext",
            "resultText", "resultJson", "normalizeRequestTarget",
            "DEFAULT_MAX_DETAILS", "MAX_RESPONSE_BYTES",
        ]
        for name in js_names:
            self.assertTrue(hasattr(unity_bridge, name), "缺少与 JS 同名的导出：" + name)

    def test_工作流记录成功与失败(self):
        unity_bridge.configure(ini=self.fx.ini, expected_project=PROJECT)

        def run(workflow):
            self.assertIsInstance(unity_bridge.call_skill("demo.echo", {"text": "a"}), dict)
            with self.assertRaises(unity_bridge.ClientError):
                unity_bridge.call_skill("demo.echo", [1])
            return workflow.summary()

        summary = unity_bridge.workflow_context("probe", "探测", run)
        self.assertEqual(("probe", 2, 1, 1),
                         (summary["name"], summary["calls"], summary["succeeded"], summary["failed"]))

    def test_result_文本与JSON(self):
        result = {"content": [{"type": "text", "text": "{\"a\":1}"}, {"type": "text", "text": ""}]}
        self.assertEqual('{"a":1}', unity_bridge.result_text(result))
        self.assertEqual({"a": 1}, unity_bridge.result_json(result))

    def test_默认客户端可配置(self):
        client = unity_bridge.configure(ini=self.fx.ini, expected_project=PROJECT, timeout=7)
        self.assertEqual(7, client.timeout)
        self.assertEqual(INSTANCE, unity_bridge.get_client().discover()["instance_id"])


class TestCli(Base):
    def test_输出为JSON且退出码0(self):
        proc = self.cli("--ini", self.fx.ini, "--expected-project", PROJECT, "list")
        self.assertEqual(0, proc.returncode, proc.stderr)
        self.assertEqual(1, json.loads(proc.stdout)["count"])

    def test_帮助与未知命令(self):
        self.assertEqual(0, self.cli("--help").returncode)
        self.assertEqual(0, self.cli("call", "--help").returncode)
        self.assertEqual(unity_bridge.EXIT_PARAM, self.cli("bogus").returncode)
        self.assertEqual(unity_bridge.EXIT_PARAM, self.cli("--timeout", "0", "discover").returncode)

    def test_call_的三种JSON输入(self):
        path = os.path.join(self.fx.dir, "args.json")
        Path(path).write_text("\ufeff{\"text\": \"文件\"}", encoding="utf-8")
        base = ["--ini", self.fx.ini, "--expected-project", PROJECT, "call", "demo.echo"]
        for extra in (["--args", "{\"text\":\"内联\"}"], ["--args-file", path]):
            proc = self.cli(*(base + extra))
            self.assertEqual(0, proc.returncode, proc.stderr)
            self.assertIn("request_id", proc.stdout)
        proc = self.cli(*(base + ["--args-stdin"]), input="{\"text\":\"标准输入\"}")
        self.assertEqual(0, proc.returncode, proc.stderr)

    def test_退出码与错误不重试(self):
        proc = self.cli("--ini", self.fx.ini, "--expected-project", PROJECT,
                        "call", "demo.echo", "--args", "not-json")
        self.assertEqual(unity_bridge.EXIT_PARAM, proc.returncode)
        self.assertIn("错误：", proc.stderr)
        self.assertEqual([], self.posts())

    def test_status_与_instance_id(self):
        proc = self.cli("--ini", self.fx.ini, "--expected-project", PROJECT,
                        "status", "abc", "--instance-id", INSTANCE)
        self.assertEqual(0, proc.returncode, proc.stderr)
        self.assertEqual("completed", json.loads(proc.stdout)["status"])

    def test_details_的_tag_与_members(self):
        base = ["--ini", self.fx.ini, "--expected-project", PROJECT, "details"]
        self.assertEqual(0, self.cli(*(base + ["--tag", "demo"])).returncode)
        proc = self.cli(*(base + ["--members", '[{"kind":"tool","name":"a"}]']))
        self.assertEqual(0, proc.returncode, proc.stderr)
        self.assertEqual(unity_bridge.EXIT_PARAM,
                         self.cli(*(base + ["--tag", "demo", "--members", "[]"])).returncode)


if __name__ == "__main__":
    unittest.main(verbosity=2)

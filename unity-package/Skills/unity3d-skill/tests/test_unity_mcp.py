import importlib.util
import contextlib
import http.server
import io
import json
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "unity_mcp", Path(__file__).resolve().parents[1] / "scripts" / "unity_mcp.py")
client = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(client)


DETAIL_CASES = json.loads((Path(__file__).parent / "details_cases.json").read_text(encoding="utf-8"))


class State:
    port = 0
    calls = []
    requests = []
    response_mode = "ok"
    details_status = 200
    details_response = None
    discovery = {"max_details": 32}
    groups_response = None
    bom = False


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_args):
        pass

    def send_json(self, value, status=200):
        body = (("\ufeff" if State.bom else "") + json.dumps(value, ensure_ascii=False)).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        State.requests.append(("GET", self.path))
        if self.path == "/groups":
            self.send_json(State.groups_response if State.groups_response is not None else {
                "groups": [{"tag": "hierarchy-management", "label": "层级管理",
                            "counts": {"tools": 2, "prompts": 1, "resources": 1}}],
                "max_details": State.discovery.get("max_details", 32), "max_response_bytes": 65536})
        elif self.path == "/prompts":
            self.send_json({"prompts": [{"name": "foo", "description": "提示词", "tags": ["hierarchy-management"]}], "count": 1})
        elif self.path == "/resources":
            self.send_json({"resources": [{"uri": "res://foo", "name": "foo", "description": "资源定义",
                                         "mimeType": "text/plain", "tags": ["hierarchy-management"]}], "count": 1})
        elif self.path == "/discovery":
            base = "http://127.0.0.1:{}".format(State.port)
            self.send_json({"base_url": base, "port": State.port,
                            "project_path": "测试项目", "process_id": 123,
                            "updated_at": "now", "tools_url": base + "/tools",
                            "schema_url_template": base + "/tools/{name}/schema", **State.discovery})
        elif self.path == "/tools":
            self.send_json({"tools": [{"name": "demo", "description": "示例工具"}], "count": 1})
        elif self.path == "/tools/demo/schema":
            self.send_json({"name": "demo", "description": "完整说明",
                            "inputSchema": {"type": "object", "properties": {"值": {"type": "string"}}}})
        elif self.path == "/custom":
            self.send_json({"custom": True})
        else:
            self.send_json({"error": "not found"}, 404)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length).decode("utf-8"))
        State.calls.append((self.path, body))
        State.requests.append(("POST", self.path))
        if self.path == "/capabilities/details":
            if State.details_status != 200:
                self.send_json({"error": "详情选择被拒绝"}, State.details_status)
                return
            if State.details_response is not None:
                self.send_json(State.details_response)
                return
            members = body.get("members", [{"kind": "tool", "name": "hierarchy_search"}])
            items = []
            for member in members:
                item = dict(member, description="注册定义")
                if member["kind"] == "tool":
                    item["inputSchema"] = {"type": "object"}
                elif member["kind"] == "prompt":
                    item["arguments"] = []
                else:
                    item["mimeType"] = "text/plain"
                items.append(item)
            self.send_json({"items": items, "count": len(items), "max_details": State.discovery.get("max_details", 32)})
            return
        if State.response_mode == "rpc_error":
            result = {"jsonrpc": "2.0", "id": body["id"], "error": {"code": -1, "message": "测试错误"}}
        elif State.response_mode == "is_error":
            result = {"jsonrpc": "2.0", "id": body["id"], "result": {"isError": True, "content": []}}
        else:
            result = {"jsonrpc": "2.0", "id": body["id"], "result": {"content": [{"type": "text", "text": "完成"}]}}
        self.send_json(result)


class ClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        State.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.ini = Path(self.temp.name) / "unity.ini"
        self.write_ini(State.port)
        State.calls.clear()
        State.requests.clear()
        State.response_mode = "ok"
        State.details_status = 200
        State.details_response = None
        State.discovery = {"max_details": 32}
        State.groups_response = None
        State.bom = False

    def tearDown(self):
        self.temp.cleanup()

    @contextlib.contextmanager
    def configured(self, discovery):
        """临时模拟 Unity 启动时发布的发现元数据。"""
        previous = State.discovery
        State.discovery = dict(discovery)
        try:
            yield
        finally:
            State.discovery = previous

    def write_ini(self, port):
        self.ini.write_text("[unity3d-skilled-mcp]\nbase_url=http://127.0.0.1:{}\nport={}\n".format(port, port), encoding="utf-8")

    def run_cli(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = client.main(["--ini", str(self.ini), *args])
        return code, out.getvalue(), err.getvalue()

    def test_ini_port_change_is_read_each_invocation(self):
        self.assertEqual(self.run_cli("discover")[0], 0)
        other = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        try:
            previous = State.port
            State.port = other.server_address[1]
            other_thread = threading.Thread(target=other.serve_forever, daemon=True)
            other_thread.start()
            self.write_ini(State.port)
            code, output, _ = self.run_cli("discover")
            self.assertEqual(code, 0)
            self.assertIn('"port": {}'.format(State.port), output)
            self.assertNotEqual(previous, State.port)
            other.shutdown()
            other.server_close()
            other_thread.join()
            State.port = previous
            self.write_ini(previous)
        finally:
            other.shutdown()
            other.server_close()
            State.port = self.server.server_address[1]
            self.write_ini(State.port)

    def test_list_and_full_schema(self):
        code, output, _ = self.run_cli("list")
        self.assertEqual(code, 0)
        self.assertIn('"name": "demo"', output)
        self.assertNotIn("inputSchema", output)
        explicit = self.run_cli("list", "--kind", "tools")
        self.assertEqual(explicit[0], 0)
        self.assertEqual(json.loads(explicit[1]), json.loads(output))
        self.assertEqual(State.requests, [("GET", "/discovery"), ("GET", "/tools")] * 2)
        code, output, _ = self.run_cli("schema", "demo")
        self.assertEqual(code, 0)
        self.assertIn('"inputSchema"', output)
        self.assertIn("值", output)

    def test_call_once_and_utf8_file(self):
        args_file = Path(self.temp.name) / "args.json"
        args_file.write_text('{"文本":"你好"}', encoding="utf-8")
        code, output, _ = self.run_cli("call", "demo", "--args-file", str(args_file))
        self.assertEqual(code, 0)
        self.assertIn("完成", output)
        self.assertEqual(len(State.calls), 1)
        self.assertEqual(State.calls[0][1]["params"]["arguments"], {"文本": "你好"})

    def test_json_rpc_and_is_error_fail_without_retry(self):
        for mode in ("rpc_error", "is_error"):
            State.response_mode = mode
            code, _, _ = self.run_cli("call", "demo", "--args", "{}")
            self.assertEqual(code, 6)
        self.assertEqual(len(State.calls), 2)

    def test_unknown_schema_http_error(self):
        code, _, _ = self.run_cli("schema", "unknown")
        self.assertEqual(code, 4)

    def test_generic_http_route(self):
        code, output, _ = self.run_cli("request", "/custom")
        self.assertEqual(code, 0)
        self.assertIn('"custom": true', output)

    def test_absolute_http_url(self):
        code, output, _ = self.run_cli("request", "http://127.0.0.1:{}/custom".format(State.port))
        self.assertEqual(code, 0)
        self.assertIn('"custom": true', output)

    def test_stdin_and_bom_input(self):
        stream = io.TextIOWrapper(io.BytesIO(b'\xef\xbb\xbf{"value":1}'), encoding="utf-8")
        with patch("sys.stdin", stream):
            self.assertEqual(self.run_cli("call", "demo", "--args-stdin")[0], 0)
        self.assertEqual(State.calls[0][1]["params"]["arguments"], {"value": 1})

    def test_connection_failure_and_timeout_never_retry(self):
        for exc in (client.error.URLError("offline"), TimeoutError("timeout"), client.HTTPException("broken response")):
            with patch.object(client, "discover", return_value={"base_url": "http://localhost:1"}), patch.object(client.request.OpenerDirector, "open", side_effect=exc) as opened:
                code, _, err = self.run_cli("call", "demo")
            self.assertEqual(code, 3)
            self.assertEqual(opened.call_count, 1)
            self.assertNotIn("Traceback", err)

    def test_missing_ini_and_port_fallback(self):
        self.ini.unlink()
        self.assertEqual(self.run_cli("discover")[0], 2)
        self.ini.write_text("[unity3d-skilled-mcp]\nport={}\n".format(State.port), encoding="utf-8-sig")
        # localhost 与模拟服务回报的 127.0.0.1 不同，直接检查发出的地址。
        with patch.object(client, "http", return_value={}) as queried:
            self.run_cli("discover")
        self.assertEqual(queried.call_args.args[0], "http://localhost:{}/discovery".format(State.port))

    def test_discovery_has_no_mcp_endpoint_and_call_uses_base_url(self):
        code, output, _ = self.run_cli("discover")
        self.assertEqual(code, 0)
        self.assertNotIn("mcp_url", output)
        self.assertEqual(self.run_cli("call", "demo")[0], 0)
        self.assertEqual(State.calls[-1][0], "/mcp")

    def test_invalid_discovery_and_response(self):
        with patch.object(client, "http", return_value="invalid"):
            self.assertEqual(self.run_cli("discover")[0], 5)
        with patch.object(client, "discover", return_value={"base_url": "http://localhost:1"}), patch.object(client, "http", return_value={"jsonrpc": "2.0", "id": 1}):
            self.assertEqual(self.run_cli("call", "demo")[0], 5)

    def test_redirect_post_not_replayed(self):
        failure = client.error.HTTPError("http://localhost:1/mcp", 307, "redirect", {}, None)
        with patch.object(client, "discover", return_value={"base_url": "http://localhost:1"}), patch.object(client.request.OpenerDirector, "open", side_effect=failure) as opened:
            self.assertEqual(self.run_cli("call", "demo")[0], 4)
            self.assertEqual(opened.call_count, 1)
        self.assertIsNone(client.NoRedirect().redirect_request(None, None, 307, "redirect", {}, "http://localhost:2/mcp"))

    def test_invalid_input_has_no_traceback(self):
        code, _, err = self.run_cli("call", "demo", "--args", "{")
        self.assertEqual(code, 2)
        self.assertNotIn("Traceback", err)
        self.assertEqual(len(State.calls), 0)

    def test_groups_and_catalogs_without_mcp_list(self):
        code, output, err = self.run_cli("groups")
        self.assertEqual(code, 0, err)
        groups = json.loads(output)
        self.assertEqual(groups["groups"][0]["counts"], {"tools": 2, "prompts": 1, "resources": 1})
        self.assertEqual(groups["max_details"], 32)
        self.assertEqual(groups["max_response_bytes"], 65536)
        for kind in ("tools", "prompts", "resources"):
            code, output, err = self.run_cli("list", "--kind", kind)
            self.assertEqual(code, 0, err)
            self.assertEqual(len(json.loads(output)[kind]), 1)
            self.assertNotIn("inputSchema", output)
            self.assertNotIn("contents", output)
        self.assertEqual([url for _, url in State.requests], ["/discovery", "/groups", "/discovery", "/tools", "/discovery", "/prompts", "/discovery", "/resources"])

    def test_fixed_tag_once(self):
        tag = json.loads(self.run_cli("groups")[1])["groups"][0]["tag"]
        code, output, err = self.run_cli("--timeout", "2", "details", "--tag", tag)
        self.assertEqual(code, 0, err)
        self.assertEqual(json.loads(output)["items"][0]["inputSchema"], {"type": "object"})
        self.assertEqual(State.calls, [("/capabilities/details", {"tag": tag})])
        self.assertEqual([url for _, url in State.requests], ["/discovery", "/groups", "/discovery", "/capabilities/details"])

    def test_mixed_members_deduplicate_without_resource_read(self):
        mixed = DETAIL_CASES["mixed"]
        members = [mixed[0], mixed[1], mixed[0], mixed[2], {"kind": "tool", "name": "foo"}]
        expected = [mixed[0], mixed[1], mixed[2], {"kind": "tool", "name": "foo"}]
        code, output, err = self.run_cli("details", "--members", json.dumps(members))
        self.assertEqual(code, 0, err)
        self.assertEqual(json.loads(output)["count"], 4)
        self.assertNotIn("contents", output)
        self.assertEqual(State.calls, [("/capabilities/details", {"members": expected})])
        self.assertEqual(State.requests, [("GET", "/discovery"), ("POST", "/capabilities/details")])

    def test_members_file_and_stdin_bom(self):
        State.bom = True
        members = DETAIL_CASES["mixed"]
        text = "\ufeff" + json.dumps(members, ensure_ascii=False)
        filename = Path(self.temp.name) / "选择 集合.json"
        filename.write_text(text, encoding="utf-8")
        self.assertEqual(self.run_cli("details", "--members-file", str(filename))[0], 0)
        stream = io.TextIOWrapper(io.BytesIO(text.encode("utf-8")), encoding="utf-8")
        with patch("sys.stdin", stream):
            self.assertEqual(self.run_cli("details", "--members-stdin")[0], 0)
        self.assertEqual(State.calls, [("/capabilities/details", {"members": members})] * 2)

    def test_resource_uri_preserved(self):
        for uri in DETAIL_CASES["uris"]:
            members = [{"kind": "resource", "uri": uri}]
            code, _, err = self.run_cli("details", "--members", json.dumps(members, ensure_ascii=False))
            self.assertEqual(code, 0, err)
            self.assertEqual(State.calls[-1][1], {"members": members})
        self.assertTrue(all(url in ("/discovery", "/capabilities/details") for _, url in State.requests))

    def test_invalid_details_before_network(self):
        for tag in DETAIL_CASES["invalid_tags"]:
            with self.subTest(tag=tag):
                self.assertEqual(self.run_cli("details", "--tag", tag)[0], 2)
        for members in DETAIL_CASES["invalid_members"]:
            with self.subTest(members=members):
                code, _, err = self.run_cli("details", "--members", json.dumps(members))
                self.assertEqual(code, 2, err)
                self.assertNotIn("Traceback", err)
        for args in [("--members-file", "missing.json"), ("--members", "{"), ("--members", "NaN"), ("--tag", "x", "--tag", "y")]:
            self.assertEqual(self.run_cli("details", *args)[0], 2)
        stream = io.TextIOWrapper(io.BytesIO(bytes([0xc3, 0x28])), encoding="utf-8")
        with patch("sys.stdin", stream):
            self.assertEqual(self.run_cli("details", "--members-stdin")[0], 2)
        self.assertEqual(State.requests, [])

    def test_details_parser_mutual_exclusion_and_unknown_options(self):
        cases = [
            ["details"], ["details", "--all"], ["details", "--tag", "x", "--members", "[]"],
            ["details", "--members", "[]", "--members-stdin"], ["details", "--members-stdin=true"],
            ["details", "--members-stdin", "--unknown"], ["details", "--kind", "tools"],
            ["details", "--tag"], ["details", "--members"], ["details", "--members-file"],
            ["list", "--kind", "all"], ["list", "--kind", "*"], ["list", "--kind", "tool"],
            ["details", "--ta", "hierarchy-management"], ["list", "--ki", "tools"],
        ]
        for args in cases:
            with self.subTest(args=args), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as raised:
                client.build_parser().parse_args(args)
            self.assertEqual(raised.exception.code, 2)
        self.assertEqual(State.requests, [])

    def test_member_limit_comes_from_discovery(self):
        def members(count):
            return [{"kind": "tool", "name": "tool_{}".format(i)} for i in range(count)]

        # 默认 32：去重后 8 项允许，33 项在网络前拒绝。
        self.assertEqual(self.run_cli("details", "--members", json.dumps(members(8) * 2))[0], 0)
        self.assertEqual(State.calls[-1][1], {"members": members(8)})
        self.assertEqual(self.run_cli("details", "--members", json.dumps(members(1)))[0], 0)
        before = len(State.requests)
        self.assertEqual(self.run_cli("details", "--members", json.dumps(members(33)))[0], 2)
        # 只在发详情请求前做了 discovery 校验，绝不 POST 超量成员。
        self.assertEqual(State.requests[before:], [("GET", "/discovery")])

        # 服务端指派 12 时允许 9 项。
        with self.configured({"max_details": 12}):
            self.assertEqual(self.run_cli("details", "--members", json.dumps(members(9)))[0], 0)

        # 服务端指派 2 时拒绝 3 项。
        with self.configured({"max_details": 2}):
            before = len(State.requests)
            self.assertEqual(self.run_cli("details", "--members", json.dumps(members(3)))[0], 2)
            self.assertEqual(State.requests[before:], [("GET", "/discovery")])

        # 0 表示不限数量。
        with self.configured({"max_details": 0}):
            self.assertEqual(self.run_cli("details", "--members", json.dumps(members(40)))[0], 0)

        # 旧服务显式 8 仍被尊重，不由客户端抬高。
        with self.configured({"max_details": 8}):
            self.assertEqual(self.run_cli("details", "--members", json.dumps(members(9)))[0], 2)

    def test_details_rejected_group_full_set_unknown_disabled_no_retry(self):
        for status, args in [
            (400, ["--tag", "oversized-group"]),
            (400, ["--members", json.dumps(DETAIL_CASES["mixed"])]),
            (404, ["--tag", "unknown-group"]),
            (404, ["--members", '[{"kind":"tool","name":"disabled"}]']),
        ]:
            State.details_status = status
            before = len(State.requests)
            code, _, err = self.run_cli("details", *args)
            self.assertEqual(code, 4)
            self.assertIn("HTTP {}".format(status), err)
            self.assertEqual(State.requests[before:], [("GET", "/discovery"), ("POST", "/capabilities/details")])

    def test_details_timeout_and_disconnect_no_retry(self):
        for exc in (client.error.URLError("offline"), TimeoutError("timeout"), client.HTTPException("broken response")):
            with patch.object(client, "discover", return_value={"base_url": "http://localhost:1"}), patch.object(client.request.OpenerDirector, "open", side_effect=exc) as opened:
                code, _, err = self.run_cli("details", "--tag", "hierarchy-management")
            self.assertEqual(code, 3)
            self.assertEqual(opened.call_count, 1)
            self.assertNotIn("Traceback", err)

    def test_details_response_count_and_limit_validation(self):
        for value in [{}, {"items": [], "count": 0, "max_details": 32},
                      {"items": [{}], "count": 2, "max_details": 32},
                      {"items": [{}] * 33, "count": 33, "max_details": 32},
                      {"items": [{}], "count": 1, "max_details": 9},
                      {"items": [None], "count": 1, "max_details": 32}]:
            State.details_response = value
            self.assertEqual(self.run_cli("details", "--tag", "hierarchy-management")[0], 5)

    def test_details_response_exact_byte_limit_and_unicode_overflow(self):
        response = {"items": [{"kind": "resource", "uri": "res://foo", "description": ""}], "count": 1, "max_details": 32}
        overhead = len(json.dumps(response, ensure_ascii=False).encode("utf-8"))
        response["items"][0]["description"] = "a" * (65536 - overhead)
        State.details_response = response
        self.assertEqual(self.run_cli("details", "--members", '[{"kind":"resource","uri":"res://foo"}]')[0], 0)
        response["items"][0]["description"] += "a"
        self.assertEqual(self.run_cli("details", "--tag", "hierarchy-management")[0], 5)
        response["items"][0]["description"] = "中" * 22000
        self.assertEqual(self.run_cli("details", "--tag", "hierarchy-management")[0], 5)
        self.assertEqual(len(State.calls), 3)


if __name__ == "__main__":
    unittest.main()

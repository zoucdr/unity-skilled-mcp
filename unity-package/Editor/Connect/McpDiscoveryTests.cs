using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;

namespace UniMcp
{
    // 无 NUnit 或测试包依赖；也可通过 -executeMethod UniMcp.McpDiscoveryTests.RunAll 执行。
    public static class McpDiscoveryTests
    {
        [MenuItem("Tools/Unity MCP/Run Discovery Self Tests")]
        public static void RunAll()
        {
            TestTools();
            TestCapabilityDetails();
            TestConfiguredDetailLimits();
            TestToolTags();
            TestBuiltInSchemas();
            TestConnectionInfo();
            TestSkillInstallPrompt();
            UnityEngine.Debug.Log("[UniMcp] 服务发现自检通过");
        }

        private static void TestSkillInstallPrompt()
        {
            string path = UniMcp.Gui.McpServiceStatusWindow.SkillFolderPath;
            Check(!string.IsNullOrEmpty(path) && Directory.Exists(path), "技能目录可通过 GUID 解析为实际路径");
            Check(File.Exists(Path.Combine(path, "SKILL.md")), "技能目录包含 SKILL.md");
            string prompt = UniMcp.Gui.McpServiceStatusWindow.BuildSkillInstallPrompt();
            Check(prompt.Contains(path, StringComparison.Ordinal), "安装提示词包含技能绝对路径");
            Check(prompt.Contains(".unity-mcp/skill.ini", StringComparison.Ordinal), "安装提示词说明 INI 位置");
            Check(prompt.Contains("SKILL.md", StringComparison.Ordinal), "安装提示词指向 SKILL.md");
            Check(!prompt.Contains("mcp_url", StringComparison.Ordinal), "安装提示词不暴露 MCP 端点");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("服务发现自检失败: " + message);
        }

        private static string Response(McpDiscoverySnapshot snapshot, string path, int status = 200)
        {
            Check(snapshot.TryGetResponse(path, out int actualStatus, out string json), "未匹配路由 " + path);
            Check(actualStatus == status, "状态码不符合预期 " + path);
            return json;
        }

        private static void TestTools()
        {
            var schema = new JsonClass {
                { "type", new JsonData("object") },
                { "properties", new JsonClass {
                    { "calls", new JsonClass {
                        { "type", new JsonData("array") },
                        { "description", new JsonData("完整参数说明") },
                        { "items", new JsonClass {
                            { "type", new JsonData("object") },
                            { "properties", new JsonClass {
                                { "func", new JsonClass { { "type", new JsonData("string") } } }
                            } },
                            { "required", new JsonArray { new JsonData("func") } }
                        } }
                    } }
                } },
                { "required", new JsonArray { new JsonData("calls") } }
            };
            string description = "完整说明\n" + new string('长', 190);
            var snapshot = new McpDiscoverySnapshot(new[] {
                new ToolInfo { name = "z_tool", description = description, inputSchema = schema },
                new ToolInfo { name = "a_tool", description = " 首行\n\t次行 " }
            });
            var listing = Json.Parse(Response(snapshot, "/tools"));
            Check(listing["count"].AsInt == 2, "工具数量");
            Check(listing["tools"][0]["name"].Value == "a_tool", "按名称排序");
            Check(listing["tools"][0].ToObject().Count == 3, "摘要只包含 name/description/tags");
            Check(listing["tools"][0]["description"].Value == "首行 次行", "摘要空白归一化");
            Check(listing["tools"][1]["description"].Value.Length <= 160, "摘要长度限制");
            Check(Response(snapshot, "/mcp/tools") == listing.ToString(), "工具列表别名");
            var detail = Json.Parse(Response(snapshot, "/tools/z_tool/schema"));
            Check(detail.ToObject().Count == 3, "完整 MCP 工具字段");
            Check(detail["description"].Value == description, "完整工具说明不截断");
            Check(detail["inputSchema"].ToString() == schema.ToString(), "完整保留 required/properties/items/description");
            Check(Response(snapshot, "/mcp/tools/z_tool/schema") == detail.ToString(), "schema 别名");
            Check(Response(snapshot, "/tools/%7A_tool/schema") == detail.ToString(), "URL 编码名称");
            var empty = Json.Parse(Response(snapshot, "/tools/a_tool/schema"));
            Check(empty["inputSchema"]["type"].Value == "object", "无参数工具使用 object");
            Check(empty["inputSchema"]["properties"] is JsonClass && empty["inputSchema"]["properties"].Count == 0, "无参数 schema 含空 properties 对象");
            Response(snapshot, "/tools/unknown/schema", 404);
            Response(snapshot, "/mcp/tools/unknown/schema", 404);
            Response(snapshot, "/tools//schema", 404);
            Check(!snapshot.TryGetResponse("/tools/schema", out _, out _), "短路径不发生越界");
            Check(!snapshot.TryGetResponse("/mcp", out _, out _), "不接管现有 MCP 入口");
            Check(McpDiscoverySnapshot.Summarize(new string('a', 158) + "\U0001F600xx").Length == 159, "截断不拆分代理对");
            Check(Json.Parse(Response(new McpDiscoverySnapshot(new ToolInfo[0]), "/tools"))["count"].AsInt == 0, "空注册表");
            schema.Add("changed", new JsonData(true));
            Check(Response(snapshot, "/tools/z_tool/schema") == detail.ToString(), "快照不受源 schema 后续修改影响");
            Task.Run(() => {
                for (int i = 0; i < 100; i++) Response(snapshot, "/tools/z_tool/schema");
            }).GetAwaiter().GetResult();
        }

        private static JsonNode Details(McpDiscoverySnapshot snapshot, string body, int expected = 200)
        {
            string json = snapshot.GetDetails(body, out int status);
            Check(status == expected, "详情状态码 " + expected + " != " + status + ": " + json);
            var result = Json.Parse(json);
            Check(Encoding.UTF8.GetByteCount(json) <= 65536, "成功及错误响应 UTF-8 字节上限");
            if (expected == 200)
            {
                Check(result["max_details"].AsInt == snapshot.MaxDetails, "详情限制元数据与快照一致");
            }
            else Check(!string.IsNullOrEmpty(result["error"].Value), "JSON 错误文案");
            return result;
        }

        [CapabilityTags("Hierarchy Management", "shared", "shared")]
        private abstract class TaggedDefinition { }

        [CapabilityTags(new[] { "legacy" })]
        private abstract class ArrayTaggedDefinition { }

        private static void TestToolTags()
        {
            var tags = McpDiscoverySnapshot.GetTags(typeof(TaggedDefinition), "uncategorized");
            var group = new ToolNameAttribute("tagged", "Original Group", "原分类");
            var snapshot = new McpDiscoverySnapshot(new[] {
                new ToolInfo { name = "tagged" }, new ToolInfo { name = "async_call" }
            }, toolGroups: new System.Collections.Generic.Dictionary<string, ToolNameAttribute> { { "tagged", group } },
                toolTags: new System.Collections.Generic.Dictionary<string, string[]> { { "tagged", tags } });
            var detail = Details(snapshot, "{\"tag\":\"shared\"}");
            Check(detail["count"].AsInt == 1 && detail["items"][0]["tags"].Count == 2, "工具实现类显式标签覆盖默认分类");
            Details(snapshot, "{\"tag\":\"original-group\"}", 404);
            Check(Details(snapshot, "{\"tag\":\"system\"}")["items"][0]["name"].Value == "async_call", "系统工具保留 system 标签");
            tags[0] = "mutated";
            Check(Details(snapshot, "{\"tag\":\"shared\"}").ToString() == detail.ToString(), "工具标签数组发布后隔离");
            var listing = Json.Parse(Response(snapshot, "/groups"));
            Check(listing["groups"].Count == 3, "多标签目录去重且不包含被覆盖的默认组");
            Check(McpDiscoverySnapshot.GetTags(typeof(ArrayTaggedDefinition), "prompts")[0] == "legacy", "统一标签保留数组构造兼容");
            var attribute = new CapabilityTagsAttribute("first");
            var copy = attribute.Tags;
            copy[0] = "changed";
            Check(attribute.Tags[0] == "first", "属性标签对外返回副本");
        }

        private static void TestCapabilityDetails()
        {
            var prompt = new ConfigurablePrompt("foo", new string('提', 180), "提示正文");
            prompt.SetKeys(new System.Collections.Generic.List<ConfigurableMethodKey> {
                new ConfigurableMethodKey { key = "subject", desc = "主题", optional = false }
            });
            var resource = new ConfigurableResource("foo", new string('资', 180), "res://foo", "text/plain");
            var tools = new[] {
                new ToolInfo { name = "hierarchy_search", description = "层级搜索" },
                new ToolInfo { name = "hierarchy_apply", description = "层级修改" }
            };
            var groups = new System.Collections.Generic.Dictionary<string, ToolNameAttribute> {
                { "hierarchy_search", new ToolNameAttribute("hierarchy_search", "Hierarchy Management", "层级管理") },
                { "hierarchy_apply", new ToolNameAttribute("hierarchy_apply", "Hierarchy Management", "层级管理") }
            };
            var snapshot = new McpDiscoverySnapshot(tools, new[] { prompt }, new[] { resource }, groups);
            var groupListing = Json.Parse(Response(snapshot, "/groups"));
            Check(groupListing["max_details"].AsInt == 32 && groupListing["max_response_bytes"].AsInt == 65536, "组目录默认数量及独立字节限制");
            Check(groupListing["groups"][0]["tag"].Value == "hierarchy-management", "稳定英文标签");
            Check(groupListing["groups"][0]["counts"]["tools"].AsInt == 2, "组成员数量");
            Check(groupListing["groups"][0].ToObject().Count == 3, "组目录不复制详情");
            foreach (string path in new[] { "/groups", "/tools", "/prompts", "/resources" })
                Check(Response(snapshot, path) == Response(snapshot, "/mcp" + path), "摘要别名 " + path);
            var prompts = Json.Parse(Response(snapshot, "/prompts"));
            Check(prompts["count"].AsInt == 1 && prompts["prompts"][0].Count == 3, "提示词目录仅摘要和标签");
            Check(prompts["prompts"][0]["description"].Value.Length <= 160, "提示词短摘要");
            var resources = Json.Parse(Response(snapshot, "/resources"));
            Check(resources["count"].AsInt == 1 && resources["resources"][0].Count == 5, "资源目录字段");
            Check(resources["resources"][0]["tags"][0].Value == "resources", "资源默认标签");
            Check(prompts["prompts"][0]["tags"][0].Value == "prompts", "提示词默认标签");
            Check(McpDiscoverySnapshot.GetTags(typeof(TaggedDefinition), "prompts").Length == 2, "统一可选标签去重");
            Check(new ToolNameAttribute("default").GroupNameEnglish == "Uncategorized", "默认英文分组不依赖语言");
            Check(McpDiscoverySnapshot.IsDetailsPath("/capabilities/details") && McpDiscoverySnapshot.IsDetailsPath("/mcp/capabilities/details"), "POST 别名");
            Check(!McpDiscoverySnapshot.IsDetailsPath("/mcp/capabilities/details/all"), "无 all 子路由");

            var tagged = Details(snapshot, "{\"tag\":\"hierarchy-management\"}");
            Check(tagged["count"].AsInt == 2, "合法标签覆盖全部工具但非全部三类时允许");
            string mixed = "{\"members\":[{\"kind\":\"tool\",\"name\":\"hierarchy_search\"},{\"kind\":\"prompt\",\"name\":\"foo\"},{\"kind\":\"resource\",\"uri\":\"res://foo\"}]}";
            var detail = Details(snapshot, mixed);
            Check(detail["count"].AsInt == 3, "混合动态成员");
            Check(detail["items"][0]["inputSchema"]["type"].Value == "object", "详情无参数 object");
            Check(detail["items"][1]["prompt_text"].Value == "提示正文" && detail["items"][1]["arguments"][0]["required"].AsBool, "提示正文及参数");
            Check(detail["items"][2].Count == 6 && detail["items"][2]["uri"].Value == "res://foo", "资源只有注册定义，不读取正文");
            prompt.SetPromptText("已改动");
            resource.SetDescription("已改动");
            Check(Details(snapshot, mixed).ToString() == detail.ToString(), "提示词资源快照不可变");
            string one = "{\"kind\":\"tool\",\"name\":\"hierarchy_search\"}";
            Check(Details(snapshot, "{\"members\":[" + string.Join(",", System.Linq.Enumerable.Repeat(one, 12)) + "]}")["count"].AsInt == 1, "先去重再限制成员数");
            foreach (string invalid in new[] {
                "", "{}", "[]", "null", "{\"members\":[]}", "{\"tag\":\"\"}", "{\"tag\":\" \"}",
                "{\"tag\":\"all\"}", "{\"tag\":\"*\"}", "{\"all\":\"true\"}",
                "{\"tag\":\"prompts\",\"members\":[]}", "{\"tag\":\"prompts\",\"extra\":\"x\"}",
                "{\"tag\":false}", "{\"tag\":123}", "{\"tag\":null}", "{\"members\":\"x\"}",
                "{\"tag\":\"prompts\",\"tag\":\"resources\"}", "{tag:\"prompts\"}",
                "{\"tag\":\"prompts\",}", "{\"tag\":\"prompts\"", "{\"tag\":\"prompts\"}x",
                "{\"members\":[{}]}", "{\"members\":[{\"kind\":\"all\",\"name\":\"x\"}]}",
                "{\"members\":[{\"kind\":\"tool\",\"name\":\"*\"}]}",
                "{\"members\":[{\"kind\":\"tool\",\"name\":\"all\"}]}",
                "{\"members\":[{\"kind\":\"tool\",\"name\":\"\"}]}",
                "{\"members\":[{\"kind\":\"resource\",\"name\":\"foo\"}]}",
                "{\"members\":[{\"kind\":\"tool\",\"name\":\"hierarchy_search\",\"extra\":\"x\"}]}",
                "{\"members\":[{\"kind\":\"tool\",\"name\":\"hierarchy_search\"},]}"
            }) Details(snapshot, invalid, 400);
            Details(snapshot, "{\"tag\":\"missing\"}", 404);
            foreach (string kind in new[] { "tool", "prompt", "resource" })
                Details(snapshot, "{\"members\":[{\"kind\":\"" + kind + "\",\"" + (kind == "resource" ? "uri" : "name") + "\":\"missing\"}]}", 404);
            string allTools = "{\"members\":[" + one + ",{\"kind\":\"tool\",\"name\":\"hierarchy_apply\"}]}";
            Check(Details(snapshot, allTools)["count"].AsInt == 2, "动态选择全部工具但非三类总全集时允许");
            string all = mixed.Replace("\"members\":[", "\"members\":[{\"kind\":\"tool\",\"name\":\"hierarchy_apply\"},");
            Check(Details(snapshot, all, 400)["error"].Value.Contains("三类"), "跨类型全量明确拒绝");
            var onlyTools = new McpDiscoverySnapshot(tools, toolGroups: groups);
            Details(onlyTools, "{\"tag\":\"hierarchy-management\"}", 400);
            Details(onlyTools, allTools, 400);
            Details(onlyTools, "{\"members\":[" + one + "]}");
            var singleton = new McpDiscoverySnapshot(new[] { tools[0] }, toolGroups: groups);
            Details(singleton, "{\"tag\":\"hierarchy-management\"}");
            Details(singleton, "{\"members\":[" + one + "]}");
            // 未启用成员不进入快照，与未知成员使用相同的404。
            Response(singleton, "/tools/hierarchy_apply/schema", 404);
            Details(singleton, "{\"members\":[{\"kind\":\"tool\",\"name\":\"hierarchy_apply\"}]}", 404);
            Details(singleton, "{\"members\":[{\"kind\":\"prompt\",\"name\":\"foo\"}]}", 404);
            Details(singleton, "{\"members\":[{\"kind\":\"resource\",\"uri\":\"res://foo\"}]}", 404);
            Check(snapshot.ResolveResource("resource://foo", out bool registered)["uri"].Value == "res://foo" && registered, "资源名称别名只解析注册定义");
            var disabledResource = new ConfigurableResource("disabled", "禁用资源", "file:///disabled.txt", "text/plain");
            var filteredResources = new McpDiscoverySnapshot(tools, registeredResources: new[] { disabledResource });
            Check(filteredResources.ResolveResource("file:///disabled.txt", out registered) == null && registered, "禁用注册URI禁止落入直接读取回退");
            Check(filteredResources.ResolveResource("resource://disabled", out registered) == null && registered, "禁用注册名称不返回定义");
            Check(filteredResources.ResolveResource("file:///unregistered.txt", out registered) == null && !registered, "保留原有未注册URI单项读取路径");
            Task.Run(() => {
                Check(snapshot.GetPrompt("foo")["prompt_text"].Value == "提示正文", "后台仅使用提示词快照");
                Check(snapshot.ResolveResource("resource://foo", out _)["description"].Value != "已改动", "后台仅使用资源快照");
            }).GetAwaiter().GetResult();

            var many = new System.Collections.Generic.List<ToolInfo>();
            var members = new JsonArray();
            for (int i = 0; i < 10; i++) many.Add(new ToolInfo { name = "tool_" + i });
            var large = new McpDiscoverySnapshot(many, maxDetails: 8);
            for (int i = 0; i < 8; i++) members.Add(new JsonClass { { "kind", new JsonData("tool") }, { "name", new JsonData("tool_" + i) } });
            Check(Details(large, new JsonClass { { "members", members } }.ToString())["count"].AsInt == 8, "允许8项");
            members.Add(new JsonClass { { "kind", new JsonData("tool") }, { "name", new JsonData("tool_8") } });
            Details(large, new JsonClass { { "members", members } }.ToString(), 400);
            Check(Details(large, "{\"tag\":\"system\"}", 400)["error"].Value.Contains("动态 members"), "标签超限指引动态集合");
            TestDetailByteLimits();
            Task.Run(() => { for (int i = 0; i < 30; i++) Details(snapshot, mixed); }).GetAwaiter().GetResult();
        }

        private static string SelectTools(int count)
        {
            var members = new JsonArray();
            for (int i = 0; i < count; i++)
                members.Add(new JsonClass { { "kind", new JsonData("tool") }, { "name", new JsonData("limit_" + i) } });
            return new JsonClass { { "members", members } }.ToString();
        }

        private static void TestConfiguredDetailLimits()
        {
            var tools = new System.Collections.Generic.List<ToolInfo>();
            var groups = new System.Collections.Generic.Dictionary<string, ToolNameAttribute>();
            for (int i = 0; i < 50; i++)
            {
                string name = "limit_" + i;
                tools.Add(new ToolInfo { name = name });
                groups.Add(name, new ToolNameAttribute(name, i < 40 ? "Subset" : "Remainder", "测试组"));
            }
            var defaults = new McpDiscoverySnapshot(tools);
            Check(defaults.MaxDetails == 32, "默认32项");
            Check(Details(defaults, SelectTools(32))["count"].AsInt == 32, "默认允许32项非全集");
            Details(defaults, SelectTools(33), 400);
            var two = new McpDiscoverySnapshot(tools, toolGroups: groups, maxDetails: 2);
            Details(two, SelectTools(2));
            Check(Details(two, SelectTools(3), 400)["error"].Value.Contains("超过2"), "自定义2项拒绝3项并提示实际值");
            Check(Details(two, "{\"tag\":\"subset\"}", 400)["error"].Value.Contains("超过2"), "标签同样应用自定义限制");
            var twelve = new McpDiscoverySnapshot(tools, maxDetails: 12);
            Check(Details(twelve, SelectTools(9))["count"].AsInt == 9, "设置12允许9项且不触发全集规则");
            Details(twelve, SelectTools(12));
            Details(twelve, SelectTools(13), 400);
            var unlimited = new McpDiscoverySnapshot(tools, toolGroups: groups, maxDetails: 0);
            Check(Details(unlimited, SelectTools(40))["count"].AsInt == 40, "0允许超过32项");
            Check(Details(unlimited, "{\"tag\":\"subset\"}")["count"].AsInt == 40, "0允许超过32项的非全集标签");
            Check(Details(unlimited, SelectTools(50), 400)["error"].Value.Contains("全量"), "0仍拒绝动态全集");
            Check(Details(new McpDiscoverySnapshot(tools, maxDetails: 0), "{\"tag\":\"system\"}", 400)["error"].Value.Contains("全量"), "0仍拒绝标签全集");
            Details(unlimited, new string(' ', 65537), 413);
            var oversized = new McpDiscoverySnapshot(new[] { new ToolInfo { name = "limit_0", description = new string('x', 65536) } }, maxDetails: 0);
            Details(oversized, SelectTools(1), 413);
            foreach (int value in new[] { -1, int.MinValue })
            {
                var negative = new McpDiscoverySnapshot(tools, maxDetails: value);
                Check(negative.MaxDetails == 32, "负数归默认32且最小整数不溢出");
                Details(negative, SelectTools(32));
                Details(negative, SelectTools(33), 400);
            }
            var maximum = new McpDiscoverySnapshot(tools, maxDetails: int.MaxValue);
            Check(maximum.MaxDetails == int.MaxValue, "无额外人为上限且最大整数不溢出");
            Details(maximum, SelectTools(49));

            string directory = Path.Combine(Path.GetTempPath(), "unity-mcp-limits-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                foreach (int value in new[] { 32, 2, 12, 0, -1, int.MinValue, int.MaxValue })
                {
                    int expected = value < 0 ? 32 : value;
                    var snapshot = new McpDiscoverySnapshot(tools, maxDetails: value);
                    var info = new McpConnectionInfo(new[] { "http://localhost:8000/" }, "E:/工程", 1234, DateTime.UtcNow, maxDetails: value);
                    string path = Path.Combine(directory, "connection.ini");
                    info.WriteIni(path);
                    string ini = File.ReadAllText(path, Encoding.UTF8);
                    Check(ini.StartsWith("[unity3d-skilled-mcp]\n", StringComparison.Ordinal)
                        && ini.Contains("\nmax_details=" + expected.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"), "INI发布实际数量");
                    Check(Json.Parse(info.ToJson())["max_details"].AsInt == expected, "discovery发布实际数量");
                    Check(Json.Parse(Response(snapshot, "/groups"))["max_details"].AsInt == expected, "groups与INI数量一致");
                    Check(Details(snapshot, SelectTools(1))["max_details"].AsInt == expected, "details与INI数量一致，0不改写");
                }
                string expectedIni = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-mcp", "skill.ini");
                Check(string.Equals(McpService.DefaultIniPath, expectedIni, StringComparison.Ordinal), "INI默认发布到 ~/.unity-mcp/skill.ini");
                string nested = Path.Combine(directory, "nested", ".unity-mcp", "skill.ini");
                new McpConnectionInfo(new[] { "http://localhost:8000/" }, "E:/工程", 1234, DateTime.UtcNow).WriteIni(nested);
                Check(File.Exists(nested), "INI写入自动创建发布目录");
                var plain = new McpConnectionInfo(new[] { "http://localhost:8000/" }, "E:/工程", 1234, DateTime.UtcNow);
                string plainPath = Path.Combine(directory, "no-mcp.ini");
                plain.WriteIni(plainPath);
                Check(!File.ReadAllText(plainPath, Encoding.UTF8).Contains("mcp_url", StringComparison.Ordinal)
                    && !plain.ToJson().Contains("mcp_url", StringComparison.Ordinal), "INI与discovery均不发布MCP端点");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void TestDetailByteLimits()
        {
            string selection = "{\"members\":[{\"kind\":\"tool\",\"name\":\"large\"}]}";
            Func<string, McpDiscoverySnapshot> create = text => new McpDiscoverySnapshot(new[] { new ToolInfo { name = "large", description = text } });
            string baseline = create("").GetDetails(selection, out _);
            int remaining = 65536 - Encoding.UTF8.GetByteCount(baseline);
            Details(create(new string('x', remaining)), selection);
            Details(create(new string('x', remaining + 1)), selection, 413);
            Details(create(new string('中', remaining / 3 + 1)), selection, 413);
            var empty = new McpDiscoverySnapshot(new ToolInfo[0]);
            Details(empty, new string(' ', 65537), 413);
            Details(empty, "{\"members\":[]}", 400);
            string unknownPrefix = "{\"members\":[{\"kind\":\"tool\",\"name\":\"";
            string unknownSuffix = "\"}]}";
            string longUnknown = unknownPrefix + new string('x', 65536 - Encoding.UTF8.GetByteCount(unknownPrefix + unknownSuffix)) + unknownSuffix;
            Details(empty, longUnknown, 404);
            using (var input = new MemoryStream(new byte[65536]))
                Check(McpService.ReadCapabilityBodyAsync(input, System.Threading.CancellationToken.None).GetAwaiter().GetResult().Length == 65536, "正文允许65536字节");
            bool rejected = false;
            using (var input = new MemoryStream(new byte[65537]))
            {
                try { McpService.ReadCapabilityBodyAsync(input, System.Threading.CancellationToken.None).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
            }
            Check(rejected, "流式正文读取拒绝65537字节");
            rejected = false;
            using (var input = new MemoryStream(new byte[] { 0xff }))
            {
                try { McpService.ReadCapabilityBodyAsync(input, System.Threading.CancellationToken.None).GetAwaiter().GetResult(); }
                catch (DecoderFallbackException) { rejected = true; }
            }
            Check(rejected, "拒绝无效 UTF-8");
        }

        private static void TestBuiltInSchemas()
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(McpService);
            // 不调用单例构造器，避免测试启动监听、写用户 INI 或更改服务状态。
            var service = (McpService)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            type.GetField("mainThreadId", flags).SetValue(service, System.Threading.Thread.CurrentThread.ManagedThreadId);
            type.GetField("mcpToolInstanceCache", flags).SetValue(service, new System.Collections.Generic.Dictionary<string, UniMcp.Executer.McpTool>());
            var infos = new System.Collections.Generic.Dictionary<string, ToolInfo>();
            type.GetField("toolInfos", flags).SetValue(service, infos);
            foreach (string method in new[] { "AddAsyncMcpTool", "AddBatchMcpTool", "AddSyncMcpTool", "AddGetPromptDetailTool", "AddSearchMcpTool" })
                type.GetMethod(method, flags).Invoke(service, null);
            var snapshot = new McpDiscoverySnapshot(infos.Values);
            type.GetField("discoverySnapshot", flags).SetValue(service, snapshot);
            var systemGroup = Json.Parse(Response(snapshot, "/groups"))["groups"][0];
            Check(systemGroup["tag"].Value == "system" && systemGroup["counts"]["tools"].AsInt == 5, "五个系统工具归入 system");
            foreach (string method in new[] { "HandleToolsList", "HandlePromptsList", "HandleResourcesList" })
            {
                var response = Json.Parse((string)type.GetMethod(method, flags).Invoke(service, new object[] { "1" }));
                Check(response["error"]["code"].AsInt == -32602 && response["error"]["message"].Value.Contains("/capabilities/details"), "旧列表明确拒绝并引导 HTTP 分组发现: " + method);
            }
            Check(type.GetMethod("BuildPromptDetailResult", flags).Invoke(service, new object[] { "disabled" }) == null, "get_prompt 拒绝快照外提示词");
            var missingPrompt = Json.Parse((string)type.GetMethod("HandlePromptsGet", flags).Invoke(service, new object[] {
                "1", new JsonClass { { "name", new JsonData("disabled") } }
            }));
            Check(missingPrompt["error"]["code"].AsInt == -32602, "prompts/get 拒绝快照外提示词");
            foreach (string name in new[] { "async_call", "batch_call", "sync_call", "get_prompt", "search_tool" })
            {
                var schema = Json.Parse(Response(snapshot, "/tools/" + name + "/schema"))["inputSchema"];
                Check(schema["type"].Value == "object" && schema["properties"].Count > 0 && schema["required"].Count > 0, "内置工具完整 schema: " + name);
            }
            var batch = Json.Parse(Response(snapshot, "/tools/batch_call/schema"))["inputSchema"];
            Check(batch["properties"]["args"]["items"]["required"].Count == 2, "批量工具嵌套必填项");
            type.GetField("availableTools", flags).SetValue(service, new System.Collections.Generic.Dictionary<string, IToolMethod>());
            type.GetField("availablePrompts", flags).SetValue(service, new System.Collections.Generic.Dictionary<string, IPrompts>());
            type.GetField("availableResources", flags).SetValue(service, new System.Collections.Generic.Dictionary<string, IRes>());
            type.GetField("isRunning", flags).SetValue(service, true);
            type.GetField("startedDiscoveryMaxDetails", flags).SetValue(service, 12);
            type.GetField("discoveryLanguage", flags).SetValue(service, McpLocalSettings.Instance.CurrentLanguage);
            type.GetField("discoveryDirty", flags).SetValue(service, 1);
            type.GetMethod("UpdateDiscoverySnapshot", flags).Invoke(service, null);
            var filtered = (McpDiscoverySnapshot)type.GetField("discoverySnapshot", flags).GetValue(service);
            foreach (string name in infos.Keys)
                Response(filtered, "/tools/" + name + "/schema", McpLocalSettings.Instance.IsToolEnabled(name) ? 200 : 404);
            var update = type.GetMethod("UpdateDiscoverySnapshot", flags);
            var snapshotField = type.GetField("discoverySnapshot", flags);
            update.Invoke(service, null);
            Check(ReferenceEquals(filtered, snapshotField.GetValue(service)), "无变化时复用快照");
            foreach (string field in new[] { "discoveryLanguage", "discoveryDisabledTools" })
            {
                var before = snapshotField.GetValue(service);
                type.GetField(field, flags).SetValue(service, "自检强制旧指纹");
                update.Invoke(service, null);
                Check(!ReferenceEquals(before, snapshotField.GetValue(service)), "语言或三类禁用指纹变化重建: " + field);
            }
            var previous = snapshotField.GetValue(service);
            type.GetField("discoveryDirty", flags).SetValue(service, 1);
            Task.Run(() => update.Invoke(service, null)).GetAwaiter().GetResult();
            Check(ReferenceEquals(previous, snapshotField.GetValue(service)), "后台线程不能构建或替换快照");
            update.Invoke(service, null);
            Check(!ReferenceEquals(previous, snapshotField.GetValue(service)), "Discover 失效标记由主线程重建");
            Check(((McpDiscoverySnapshot)snapshotField.GetValue(service)).MaxDetails == 12, "运行快照使用启动值");
            var settings = McpLocalSettings.Instance;
            var settingField = typeof(McpLocalSettings).GetField("_discoveryMaxDetails", flags);
            int savedValue = (int)settingField.GetValue(settings);
            try
            {
                // 只模拟保存后的内存值，不保存测试值到用户项目设置。
                foreach (int value in new[] { 2, 0, -1, int.MinValue, int.MaxValue })
                {
                    settingField.SetValue(settings, value);
                    Check(settings.DiscoveryMaxDetails == (value < 0 ? 32 : value), "项目设置读取归一化且不设人为上限");
                    type.GetField("discoveryDirty", flags).SetValue(service, 1);
                    update.Invoke(service, null);
                    var running = (McpDiscoverySnapshot)snapshotField.GetValue(service);
                    Check(running.MaxDetails == 12 && Json.Parse(Response(running, "/groups"))["max_details"].AsInt == 12,
                        "设置变化后强制重建仍沿用启动值，必须重启才生效");
                }
            }
            finally { settingField.SetValue(settings, savedValue); }
        }

        private static void TestConnectionInfo()
        {
            var time = new DateTime(2026, 10, 29, 1, 2, 3, DateTimeKind.Utc);
            var info = new McpConnectionInfo(new[] { "http://127.0.0.1:8000/", "http://localhost:8000/", "http://+:8000/" }, "E:/工程", 1234, time);
            Check(info.BaseUrl == "http://localhost:8000", "优先显式 localhost 绑定");
            Check(info.ScriptLanguage == "js", "客户端默认 JavaScript");
            Check(info.MaxDetails == 32 && info.ToIni().Contains("\nmax_details=32\n")
                && Json.Parse(info.ToJson())["max_details"].AsInt == 32, "连接默认32及INI/discovery默认值一致");
            Check(info.ToIni().Contains("script_language=js\n"), "INI 发布默认 JS");
            Check(Json.Parse(info.ToJson())["script_language"].Value == "js", "HTTP 发布默认 JS");
            var python = new McpConnectionInfo(new[] { "http://localhost:8000/" }, "E:/工程", 1234, time, "python");
            Check(python.ToIni().Contains("script_language=python\n"), "INI 发布 Python");
            Check(Json.Parse(python.ToJson())["script_language"].Value == "python", "HTTP 发布 Python");
            Check(McpLocalSettings.NormalizeScriptLanguage(null) == "js", "旧配置默认 JS");
            Check(McpLocalSettings.NormalizeScriptLanguage("invalid") == "js", "无效配置回退 JS");
            var loopback = new McpConnectionInfo(new[] { "http://127.0.0.1:8001/" }, "E:/工程", 1234, time);
            Check(loopback.BaseUrl == "http://127.0.0.1:8001", "仅 IPv4 绑定时发布可用地址");
            var wildcard = new McpConnectionInfo(new[] { "http://+:8002/" }, "E:/工程", 1234, time);
            Check(wildcard.BaseUrl == "http://localhost:8002", "禁止发布监听通配符");
            var metadata = Json.Parse(info.ToJson());
            Check(metadata["port"].AsInt == 8000 && metadata["process_id"].AsInt == 1234, "数字元数据");
            Check(metadata["project_path"].Value == "E:/工程", "工程路径元数据");
            Check(metadata["updated_at"].Value.EndsWith("Z", StringComparison.Ordinal), "UTC ISO8601 时间");
            Check(metadata["tools_url"].Value == info.BaseUrl + "/tools", "工具列表 URL");
            Check(metadata["schema_url_template"].Value == info.BaseUrl + "/tools/{name}/schema", "schema URL 模板");
            Check(info.ToIni().StartsWith("[unity3d-skilled-mcp]\nbase_url=" + info.BaseUrl + "\n", StringComparison.Ordinal), "INI 契约");
            string directory = Path.Combine(Path.GetTempPath(), "unity-mcp-discovery-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "connection.ini");
                info.WriteIni(path);
                Check(File.ReadAllText(path, Encoding.UTF8) == info.ToIni(), "首次写入 UTF-8 INI");
                byte[] bytes = File.ReadAllBytes(path);
                Check(!(bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf), "UTF-8 无 BOM");
                loopback.WriteIni(path);
                Check(File.ReadAllText(path, Encoding.UTF8) == loopback.ToIni(), "端口重启原子替换");
                Check(Directory.GetFiles(directory).Length == 1, "成功后无临时文件残留");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    bool failed = false;
                    try { info.WriteIni(path); }
                    catch (IOException) { failed = true; }
                    catch (UnauthorizedAccessException) { failed = true; }
                    Check(failed, "锁定目标写入必须失败");
                }
                Check(File.ReadAllText(path, Encoding.UTF8) == loopback.ToIni(), "替换失败保留旧 INI");
                Check(Directory.GetFiles(directory).Length == 1, "失败后清理临时文件");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
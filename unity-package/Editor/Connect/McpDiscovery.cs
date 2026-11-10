using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;

namespace UniMcp
{
    public partial class McpService
    {
        private volatile McpDiscoverySnapshot discoverySnapshot = new McpDiscoverySnapshot(new ToolInfo[0]);
        private volatile string discoveryJson = "{}";
        private int startedDiscoveryMaxDetails = McpDiscoverySnapshot.DefaultMaxDetails;
        private int discoveryDirty = 1;
        private string discoveryLanguage;
        private string discoveryDisabledTools;
        private volatile bool discoveryDescriptions;

        // EditorApplication.update 在主线程执行，HTTP 请求不触发工具反射或设置读取。
        private void UpdateDiscoverySnapshot()
        {
            if (!isRunning || !IsMainThread()) return;
            var settings = McpLocalSettings.Instance;
            string disabled = new JsonClass {
                { "tools", new JsonData(string.Join("\n", settings.GetDisabledTools().OrderBy(n => n, StringComparer.Ordinal))) },
                { "prompts", new JsonData(string.Join("\n", availablePrompts.Keys.Where(n => !settings.IsPromptEnabled(n)).OrderBy(n => n, StringComparer.Ordinal))) },
                { "resources", new JsonData(string.Join("\n", availableResources.Values.Where(r => !settings.IsResourceEnabled(r.Name)).Select(r => r.Url).OrderBy(n => n, StringComparer.Ordinal))) }
            }.ToString();
            if (System.Threading.Interlocked.Exchange(ref discoveryDirty, 0) == 0
                && discoveryLanguage == settings.CurrentLanguage
                && discoveryDisabledTools == disabled
                && discoveryDescriptions == settings.EnableDescriptions) return;

            try
            {
                if (discoveryLanguage != settings.CurrentLanguage)
                {
                    RefreshToolInfos();
                }
                var tools = new List<ToolInfo>();
                var groups = new Dictionary<string, ToolNameAttribute>(StringComparer.Ordinal);
                var toolTags = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var spec in ToolCatalog.All)
                {
                    if (!settings.IsToolEnabled(spec.Name)) continue;
                    tools.Add(ToolCatalog.CreateInfo(spec, true));
                    groups[spec.Name] = new ToolNameAttribute(spec.Name, spec.GroupEn, spec.GroupZh);
                    toolTags[spec.Name] = spec.Tags;
                }
                foreach (var info in toolInfos.Values)
                {
                    if (!settings.IsToolEnabled(info.name)) continue;
                    if (ToolCatalog.Find(info.name) != null) continue;
                    if (mcpToolInstanceCache.ContainsKey(info.name)) tools.Add(info);
                }
                discoverySnapshot = new McpDiscoverySnapshot(tools,
                    availablePrompts.Values.Where(p => settings.IsPromptEnabled(p.Name)),
                    availableResources.Values.Where(r => settings.IsResourceEnabled(r.Name)), groups, toolTags, availableResources.Values,
                    startedDiscoveryMaxDetails);
                discoveryLanguage = settings.CurrentLanguage;
                discoveryDisabledTools = disabled;
                discoveryDescriptions = settings.EnableDescriptions;
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Exchange(ref discoveryDirty, 1);
                LogWarning("[UniMcp] 更新服务发现快照失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 技能连接信息固定发布到 ~/.unity-mcp/skill.ini，由技能侧作为端口发现的唯一起点。
        /// </summary>
        internal static string DefaultIniPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".unity-mcp", "skill.ini");
            }
        }

        private void PublishConnectionInfo()
        {
            // 技能源目录供技能侧直接定位 SKILL.md 与 unity_bridge.js；解析失败时为 null（INI 仍照常发布）。
            string skillDir = null;
            try { skillDir = UniMcp.Gui.McpServiceStatusWindow.SkillFolderPath; }
            catch (Exception ex) { LogWarning("[UniMcp] 解析技能目录失败（INI 不发布 skill_dir）: " + ex.Message); }
            var info = new McpConnectionInfo(listener.Prefixes.Cast<string>(),
                Path.GetDirectoryName(UnityEngine.Application.dataPath),
                System.Diagnostics.Process.GetCurrentProcess().Id, DateTime.UtcNow,
                maxDetails: startedDiscoveryMaxDetails, skillDir: skillDir,
                unityVersion: UnityEngine.Application.unityVersion,
                renderPipeline: McpConnectionInfo.DetectRenderPipeline());
            discoveryJson = info.ToJson();
            try
            {
                info.WriteIni(DefaultIniPath);
            }
            catch (Exception ex)
            {
                LogWarning("[UniMcp] 写入服务发现 INI 失败（服务继续运行）: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task<bool> TryHandleCapabilityDetailsAsync(
            System.Net.HttpListenerRequest request, System.Net.HttpListenerResponse response,
            string path, System.Threading.CancellationToken cancellationToken)
        {
            bool schemas = McpDiscoverySnapshot.IsSchemasPath(path);
            if (!schemas && !McpDiscoverySnapshot.IsDetailsPath(path)) return false;
            string hint = schemas ? "请指定 tools 名称数组。" : "请指定 tag 或 members。";
            int status = 400;
            string json;
            if (request.HttpMethod != "POST")
                json = McpDiscoverySnapshot.Error("此入口仅接受 POST JSON，" + hint);
            else if (request.ContentLength64 > McpDiscoverySnapshot.MaxRequestBytes)
            {
                status = 413;
                json = McpDiscoverySnapshot.Error("请求正文超过65536字节。");
            }
            else if (!string.IsNullOrEmpty(request.Headers["Content-Encoding"])
                && !string.Equals(request.Headers["Content-Encoding"], "identity", StringComparison.OrdinalIgnoreCase))
                json = McpDiscoverySnapshot.Error("仅接受未压缩的 UTF-8 JSON 正文。");
            else
            {
                try
                {
                    string body = await ReadCapabilityBodyAsync(request.InputStream, cancellationToken);
                    var snapshot = discoverySnapshot;
                    json = schemas ? snapshot.GetSchemas(body, out status) : snapshot.GetDetails(body, out status);
                }
                catch (InvalidDataException)
                {
                    status = 413;
                    json = McpDiscoverySnapshot.Error("请求正文超过65536字节。");
                }
                catch (OperationCanceledException)
                {
                    json = McpDiscoverySnapshot.Error("请求正文读取超时或已取消。");
                }
                catch (DecoderFallbackException)
                {
                    json = McpDiscoverySnapshot.Error("请求正文必须使用有效 UTF-8 编码。");
                }
                catch (IOException)
                {
                    json = McpDiscoverySnapshot.Error("无法读取请求正文。");
                }
            }
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentEncoding = Encoding.UTF8;
            response.KeepAlive = false;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
            return true;
        }

        internal static async System.Threading.Tasks.Task<string> ReadCapabilityBodyAsync(
            Stream input, System.Threading.CancellationToken cancellationToken)
        {
            using (var timeout = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var buffer = new MemoryStream())
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                // HttpListener 在部分运行时不响应 ReadAsync 的取消，关闭输入以中断慢请求。
                using (timeout.Token.Register(() => input.Close()))
                {
                    var block = new byte[4096];
                    try
                    {
                        while (true)
                        {
                            int read = await input.ReadAsync(block, 0,
                                Math.Min(block.Length, McpDiscoverySnapshot.MaxRequestBytes + 1 - (int)buffer.Length), timeout.Token);
                            timeout.Token.ThrowIfCancellationRequested();
                            if (read == 0) break;
                            buffer.Write(block, 0, read);
                            if (buffer.Length > McpDiscoverySnapshot.MaxRequestBytes) throw new InvalidDataException();
                        }
                    }
                    catch (Exception) when (timeout.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(timeout.Token);
                    }
                }
                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
        }

        private bool TryGetDiscoveryResponse(string path, out int statusCode, out string json)
        {
            if (path == "/discovery")
            {
                statusCode = 200;
                json = discoveryJson;
                return true;
            }
            return discoverySnapshot.TryGetResponse(path, out statusCode, out json);
        }
    }

    // 构造仅在主线程执行。发布后不保留能力实例或可变 JsonNode，后台只读字符串。
    internal sealed class McpDiscoverySnapshot
    {
        internal const int DefaultMaxDetails = 32;
        internal readonly int MaxDetails;
        internal const int MaxResponseBytes = 65536;
        internal const int MaxRequestBytes = 65536;

        internal static int NormalizeMaxDetails(int value)
        {
            return value < 0 ? DefaultMaxDetails : value;
        }

        private sealed class Capability
        {
            internal readonly string Kind;
            internal readonly string Detail;
            internal readonly string[] Tags;
            internal Capability(string kind, string detail, string[] tags)
            {
                Kind = kind;
                Detail = detail;
                Tags = (string[])tags.Clone();
            }
        }

        private readonly Dictionary<string, Capability> capabilities = new Dictionary<string, Capability>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> schemas = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> catalogs = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> registeredResourceUris = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> resourceNames = new Dictionary<string, string>(StringComparer.Ordinal);
        internal int Count => schemas.Count;

        internal McpDiscoverySnapshot(IEnumerable<ToolInfo> tools, IEnumerable<IPrompts> prompts = null,
            IEnumerable<IRes> resources = null, IDictionary<string, ToolNameAttribute> toolGroups = null,
            IDictionary<string, string[]> toolTags = null, IEnumerable<IRes> registeredResources = null,
            int maxDetails = DefaultMaxDetails)
        {
            MaxDetails = NormalizeMaxDetails(maxDetails);
            var enabledResources = (resources ?? new IRes[0]).ToArray();
            foreach (var resource in registeredResources ?? enabledResources)
            {
                registeredResourceUris.Add(resource.Url);
                if (!string.IsNullOrEmpty(resource.Name) && !resourceNames.ContainsKey(resource.Name))
                    resourceNames.Add(resource.Name, resource.Url);
            }
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var summaries = new JsonArray();
            foreach (var tool in tools.OrderBy(t => t.name, StringComparer.Ordinal))
            {
                ToolNameAttribute group = null;
                if (toolGroups != null) toolGroups.TryGetValue(tool.name, out group);
                string tag = group == null ? "system" : Slug(group.GroupNameEnglish);
                labels[tag] = group == null ? L.T("System", "系统") : group.GroupName;
                string[] tags = { tag };
                if (group != null && toolTags != null && toolTags.TryGetValue(tool.name, out var explicitTags)
                    && explicitTags != null && explicitTags.Length > 0)
                    tags = (string[])explicitTags.Clone();
                var full = new JsonClass {
                    { "name", new JsonData(tool.name) },
                    { "description", new JsonData(tool.description ?? "") },
                    { "inputSchema", tool.inputSchema ?? new JsonClass {
                        { "type", new JsonData("object") }, { "properties", new JsonClass() }
                    } }
                };
                // 旧单项 schema 的三个字段不变。
                schemas.Add(tool.name, full.ToString());
                full.Add("kind", new JsonData("tool"));
                full.Add("tags", TagsJson(tags));
                Add("tool", tool.name, full, tags);
                summaries.Add(new JsonClass {
                    { "name", new JsonData(tool.name) },
                    { "description", new JsonData(Summarize(tool.description)) },
                    { "tags", TagsJson(tags) }
                });
            }
            catalogs.Add("/tools", Catalog("tools", summaries));

            summaries = new JsonArray();
            foreach (var prompt in (prompts ?? new IPrompts[0]).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var tags = GetTags(prompt.GetType(), "prompts");
                var arguments = new JsonArray();
                foreach (var key in prompt.Keys ?? new MethodKey[0])
                    arguments.Add(new JsonClass {
                        { "name", new JsonData(key.Key) }, { "description", new JsonData(key.Desc ?? "") },
                        { "required", new JsonData(!key.Optional) }
                    });
                Add("prompt", prompt.Name, new JsonClass {
                    { "kind", new JsonData("prompt") }, { "name", new JsonData(prompt.Name) },
                    { "description", new JsonData(prompt.Description ?? "") }, { "arguments", arguments },
                    { "prompt_text", new JsonData(prompt.PromptText ?? "") }, { "tags", TagsJson(tags) }
                }, tags);
                summaries.Add(new JsonClass {
                    { "name", new JsonData(prompt.Name) }, { "description", new JsonData(Summarize(prompt.Description)) },
                    { "tags", TagsJson(tags) }
                });
            }
            catalogs.Add("/prompts", Catalog("prompts", summaries));

            summaries = new JsonArray();
            var resourceLabels = new List<string>();
            foreach (var resource in enabledResources.OrderBy(r => r.Url, StringComparer.Ordinal))
            {
                var tags = GetTags(resource.GetType(), "resources");
                // /groups 只发布名字：URL 属于详情层，简目里既长又容易被当成可直接访问的地址。
                resourceLabels.Add(string.IsNullOrEmpty(resource.Name) ? resource.Url : resource.Name);
                // 只访问注册定义，绝不读取 URL 对应的正文。
                var full = new JsonClass {
                    { "uri", new JsonData(resource.Url) }, { "name", new JsonData(resource.Name ?? "") },
                    { "description", new JsonData(resource.Description ?? "") },
                    { "mimeType", new JsonData(resource.MimeType ?? "application/octet-stream") },
                    { "tags", TagsJson(tags) }
                };
                full.Add("kind", new JsonData("resource"));
                Add("resource", resource.Url, full, tags);
                full.Remove("kind");
                full["description"] = new JsonData(Summarize(resource.Description));
                summaries.Add(full);
            }
            catalogs.Add("/resources", Catalog("resources", summaries));

            // 目录层自带能力树：工具名按 prefix/action 拆开，前缀只出现一次。
            // 客户端按 prefix + "." + action 还原全名；RootNamespace 表示工具名本身无前缀。
            var namespaces = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var pair in capabilities)
            {
                if (pair.Value.Kind != "tool") continue;
                string toolName = pair.Key.Substring("tool:".Length);
                string prefix = NamespaceOf(toolName);
                List<string> actions;
                if (!namespaces.TryGetValue(prefix, out actions))
                    namespaces[prefix] = actions = new List<string>();
                actions.Add(ActionOf(toolName));
            }
            var namespacesJson = new JsonClass();
            foreach (var prefix in namespaces.Keys.OrderBy(p => p, StringComparer.Ordinal))
            {
                var actions = new JsonArray();
                foreach (var action in namespaces[prefix].OrderBy(a => a, StringComparer.Ordinal))
                    actions.Add(new JsonData(action));
                namespacesJson.Add(prefix, actions);
            }

            // tag 索引：整个 prefix 命中记 prefix；仅部分工具命中（例如单个 action 显式打了 tag）记 "@全名"。
            // 零例外时输出仍是最紧凑的 tag -> [prefix...]，且不依赖「同 prefix 内 tags 一致」这一假设。
            var toolsByTag = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var pair in capabilities)
            {
                if (pair.Value.Kind != "tool") continue;
                foreach (var tag in pair.Value.Tags)
                {
                    HashSet<string> members;
                    if (!toolsByTag.TryGetValue(tag, out members))
                        toolsByTag[tag] = members = new HashSet<string>(StringComparer.Ordinal);
                    members.Add(pair.Key.Substring("tool:".Length));
                }
            }
            // prompts / resources 这类组没有工具，但仍须作为 details --tag 的合法键存在。
            foreach (var tag in capabilities.Values.SelectMany(c => c.Tags).Distinct(StringComparer.Ordinal))
                if (!toolsByTag.ContainsKey(tag)) toolsByTag[tag] = new HashSet<string>(StringComparer.Ordinal);

            var tagsJson = new JsonClass();
            foreach (var tag in toolsByTag.Keys.OrderBy(t => t, StringComparer.Ordinal))
            {
                var hit = toolsByTag[tag];
                var wholePrefixes = new List<string>();
                var singleTools = new List<string>();
                foreach (var prefix in namespaces.Keys)
                {
                    var all = namespaces[prefix]
                        .Select(a => prefix == RootNamespace ? a : prefix + "." + a).ToArray();
                    if (all.All(hit.Contains)) wholePrefixes.Add(prefix);
                    else foreach (var name in all.Where(hit.Contains)) singleTools.Add("@" + name);
                }
                var refs = new JsonArray();
                foreach (var p in wholePrefixes.OrderBy(x => x, StringComparer.Ordinal)) refs.Add(new JsonData(p));
                foreach (var n in singleTools.OrderBy(x => x, StringComparer.Ordinal)) refs.Add(new JsonData(n));
                tagsJson.Add(tag, refs);
            }

            // 中文分组名是目录里唯一无法由树推导的信息，只保留与 tag 不同的项。
            var labelsJson = new JsonClass();
            foreach (var pair in labels.Where(p => p.Value != p.Key).OrderBy(p => p.Key, StringComparer.Ordinal))
                labelsJson.Add(pair.Key, new JsonData(pair.Value));

            var promptNames = new JsonArray();
            foreach (var name in capabilities.Keys
                .Where(k => k.StartsWith("prompt:", StringComparison.Ordinal))
                .Select(k => k.Substring("prompt:".Length)).OrderBy(n => n, StringComparer.Ordinal))
                promptNames.Add(new JsonData(name));
            // 资源同理只列名字（缺名时退回 URL），详情与正文仍按 /resources 目录里的 URI 取。
            var resourceNameList = new JsonArray();
            foreach (var name in resourceLabels
                .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
                resourceNameList.Add(new JsonData(name));

            catalogs.Add("/groups", new JsonClass {
                { "namespaces", namespacesJson }, { "tags", tagsJson }, { "labels", labelsJson },
                { "prompts", promptNames }, { "resources", resourceNameList },
                { "max_details", new JsonData(MaxDetails) },
                { "max_response_bytes", new JsonData(MaxResponseBytes) }
            }.ToString());
        }

        private void Add(string kind, string name, JsonClass detail, string[] tags)
        {
            capabilities.Add(kind + ":" + name, new Capability(kind, detail.ToString(), tags));
        }

        // 无前缀工具（如 get_prompt）的保留命名空间；真实前缀均为字母/数字/连字符，不会冲突。
        internal const string RootNamespace = "_";

        internal static string NamespaceOf(string toolName)
        {
            int dot = string.IsNullOrEmpty(toolName) ? -1 : toolName.IndexOf('.');
            return dot < 0 ? RootNamespace : toolName.Substring(0, dot);
        }

        internal static string ActionOf(string toolName)
        {
            int dot = string.IsNullOrEmpty(toolName) ? -1 : toolName.IndexOf('.');
            return dot < 0 ? toolName : toolName.Substring(dot + 1);
        }

        internal static string Slug(string englishName) => CapabilityTagsAttribute.Slug(englishName);

        internal static string[] GetTags(Type type, string fallback)
        {
            var attribute = (CapabilityTagsAttribute)Attribute.GetCustomAttribute(type, typeof(CapabilityTagsAttribute));
            var tags = CapabilityTagsAttribute.Normalize(attribute == null ? new string[0] : attribute.Tags);
            return tags.Length == 0 ? new[] { fallback } : tags;
        }

        private static JsonArray TagsJson(IEnumerable<string> tags)
        {
            var result = new JsonArray();
            foreach (var tag in tags) result.Add(new JsonData(tag));
            return result;
        }

        private static string Catalog(string kind, JsonArray summaries)
        {
            return new JsonClass { { kind, summaries }, { "count", new JsonData(summaries.Count) } }.ToString();
        }

        internal static string Summarize(string description)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(description ?? "", @"\s+", " ").Trim();
            if (text.Length <= 160) return text;
            int length = 159;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length) + "…";
        }

        internal static string Error(string message)
        {
            return new JsonClass { { "error", new JsonData(message) } }.ToString();
        }

        internal static bool IsDetailsPath(string path)
        {
            return path == "/capabilities/details" || path == "/mcp/capabilities/details";
        }

        // 批量 Schema 与详情分离：只返回 name/description/inputSchema，可直接喂给调用方。
        internal static bool IsSchemasPath(string path)
        {
            return path == "/capabilities/schemas" || path == "/mcp/capabilities/schemas";
        }

        internal bool TryGetResponse(string path, out int statusCode, out string json)
        {
            statusCode = 200;
            if (path.StartsWith("/mcp/", StringComparison.Ordinal)) path = path.Substring(4);
            if (catalogs.TryGetValue(path, out json)) return true;
            const string prefix = "/tools/";
            const string suffix = "/schema";
            if (path.Length < prefix.Length + suffix.Length || !path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
                return false;
            string name = Uri.UnescapeDataString(path.Substring(prefix.Length, path.Length - prefix.Length - suffix.Length));
            if (!schemas.TryGetValue(name, out json))
            {
                statusCode = 404;
                json = Error("工具不存在或已禁用。");
            }
            return true;
        }

        internal bool Contains(string kind, string name)
        {
            return capabilities.ContainsKey(kind + ":" + name);
        }

        internal JsonNode GetPrompt(string name)
        {
            return capabilities.TryGetValue("prompt:" + name, out var prompt) ? Json.Parse(prompt.Detail) : null;
        }

        internal JsonNode ResolveResource(string uri, out bool registered)
        {
            string actualUri = uri;
            if (uri.StartsWith("resource://", StringComparison.Ordinal))
            {
                registered = true;
                if (!resourceNames.TryGetValue(uri.Substring("resource://".Length), out actualUri)) return null;
            }
            else registered = registeredResourceUris.Contains(uri);
            return capabilities.TryGetValue("resource:" + actualUri, out var resource) ? Json.Parse(resource.Detail) : null;
        }

        internal string GetDetails(string body, out int statusCode)
        {
            statusCode = 400;
            if (Encoding.UTF8.GetByteCount(body ?? "") > MaxRequestBytes)
            {
                statusCode = 413;
                return Error("请求正文超过65536字节，请缩小动态 members 集合。");
            }
            JsonClass request;
            try { request = new SelectionJsonReader(body).Read() as JsonClass; }
            catch (FormatException) { return Error("请求必须是严格 JSON 对象，且只能包含 tag 或 members；不允许重复字段。"); }
            if (request == null || request.Count != 1 || (!request.ContainsKey("tag") && !request.ContainsKey("members")))
                return Error("仅接受非空 tag 或非空 members，二者不能同时提供；禁止未知字段和 all 模式。");

            bool byTag = request.ContainsKey("tag");
            var selected = new List<Capability>();
            if (byTag)
            {
                if (!(request["tag"] is JsonData) || InvalidSelector(request["tag"].Value))
                    return Error("tag 必须是非空字符串，禁止 all/*。先 GET /groups 查看标签。");
                selected.AddRange(capabilities.Values.Where(c => c.Tags.Contains(request["tag"].Value)));
                if (selected.Count == 0) { statusCode = 404; return Error("标签不存在或其成员均已禁用。"); }
            }
            else
            {
                if (!(request["members"] is JsonArray members) || members.Count == 0)
                    return Error("members 必须是非空数组，数量限制见 max_details（0 表示不限制数量）。");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < members.Count; i++)
                {
                    if (!(members[i] is JsonClass member) || member.Count != 2 || !(member["kind"] is JsonData))
                        return Error("成员只能包含 kind 和 name（tool/prompt）或 uri（resource），禁止未知字段。");
                    string kind = member["kind"].Value;
                    if (kind != "tool" && kind != "prompt" && kind != "resource")
                        return Error("kind 仅支持 tool、prompt、resource，禁止 all/*。");
                    string field = kind == "resource" ? "uri" : "name";
                    if (!member.ContainsKey(field) || !(member[field] is JsonData) || InvalidSelector(member[field].Value))
                        return Error("成员必须提供非空 " + field + "，禁止 all/* 和未知字段。");
                    string selector = member[field].Value;
                    if (kind == "resource" && selector.StartsWith("resource://", StringComparison.Ordinal))
                    {
                        // /groups 只发布资源名，允许把名字写成 resource://<名称> 直接取详情。
                        if (!resourceNames.TryGetValue(selector.Substring("resource://".Length), out var resolvedUri))
                        {
                            statusCode = 404;
                            return Error("成员不存在或已禁用。");
                        }
                        selector = resolvedUri;
                    }
                    string key = kind + ":" + selector;
                    if (!capabilities.TryGetValue(key, out var capability))
                    {
                        statusCode = 404;
                        return Error("成员不存在或已禁用。");
                    }
                    if (seen.Add(key)) selected.Add(capability);
                }
            }
            if (MaxDetails > 0 && selected.Count > MaxDetails)
                return Error(byTag
                    ? "标签解析后超过" + MaxDetails.ToString(CultureInfo.InvariantCulture) + "个去重成员；请改用动态 members 缩小范围，不会静默截断。"
                    : "超过" + MaxDetails.ToString(CultureInfo.InvariantCulture) + "个去重成员，请缩小动态 members 集合。");
            if (selected.Count > 1 && selected.Count == capabilities.Count)
                return Error("本次集合覆盖全部可用的三类能力，已拒绝全量详情；请缩小范围或使用单项入口。");

            // 先计字节再拼接，避免为超大定义分配整个响应；保留嵌套 schema 原文。
            string prefix = "{\"items\":[";
            string suffix = "],\"count\":" + selected.Count.ToString(CultureInfo.InvariantCulture)
                + ",\"max_details\":" + MaxDetails.ToString(CultureInfo.InvariantCulture) + "}";
            long bytes = (long)Encoding.UTF8.GetByteCount(prefix + suffix) + selected.Count - 1;
            foreach (var capability in selected) bytes += Encoding.UTF8.GetByteCount(capability.Detail);
            if (bytes > MaxResponseBytes)
            {
                statusCode = 413;
                return Error("详情响应超过65536个 UTF-8 字节，请缩小范围；单个超大工具可使用 /tools/{name}/schema，提示词可使用 prompts/get。");
            }
            statusCode = 200;
            return prefix + string.Join(",", selected.Select(c => c.Detail)) + suffix;
        }

        // 一次请求返回多个工具的完整 MCP Schema；与详情共用数量、字节和禁止全集规则。
        internal string GetSchemas(string body, out int statusCode)
        {
            statusCode = 400;
            if (Encoding.UTF8.GetByteCount(body ?? "") > MaxRequestBytes)
            {
                statusCode = 413;
                return Error("请求正文超过65536字节，请减少本次请求的工具数量。");
            }
            JsonClass request;
            try { request = new SelectionJsonReader(body).Read() as JsonClass; }
            catch (FormatException) { return Error("请求必须是严格 JSON 对象，且只能包含 tools 字符串数组；不允许重复字段。"); }
            if (request == null || request.Count != 1 || !request.ContainsKey("tools"))
                return Error("仅接受非空 tools 数组，禁止未知字段和 all 模式。");
            if (!(request["tools"] is JsonArray names) || names.Count == 0)
                return Error("tools 必须是非空字符串数组，数量限制见 max_details（0 表示不限制数量）。");

            var selected = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                if (!(names[i] is JsonData) || InvalidSelector(names[i].Value))
                    return Error("tools 只能包含非空字符串名称，禁止 all/* 和未知字段。");
                string name = names[i].Value;
                if (!schemas.ContainsKey(name))
                {
                    statusCode = 404;
                    return Error("工具不存在或已禁用，请从 /tools 目录原样传入名称。");
                }
                if (seen.Add(name)) selected.Add(name);
            }
            if (MaxDetails > 0 && selected.Count > MaxDetails)
                return Error("超过" + MaxDetails.ToString(CultureInfo.InvariantCulture) + "个去重工具，请分批获取 Schema。");
            if (selected.Count > 1 && selected.Count == schemas.Count)
                return Error("本次请求覆盖全部已启用工具，已拒绝全量 Schema；请缩小范围或使用 /tools/{name}/schema。");

            // 先计字节再拼接；单个 schema 原文不被改写。
            string prefix = "{\"items\":[";
            string suffix = "],\"count\":" + selected.Count.ToString(CultureInfo.InvariantCulture)
                + ",\"max_details\":" + MaxDetails.ToString(CultureInfo.InvariantCulture) + "}";
            long bytes = (long)Encoding.UTF8.GetByteCount(prefix + suffix) + selected.Count - 1;
            foreach (string name in selected) bytes += Encoding.UTF8.GetByteCount(schemas[name]);
            if (bytes > MaxResponseBytes)
            {
                statusCode = 413;
                return Error("Schema 响应超过65536个 UTF-8 字节，请减少工具数量；单个超大工具可使用 /tools/{name}/schema。");
            }
            statusCode = 200;
            return prefix + string.Join(",", selected.Select(name => schemas[name])) + suffix;
        }

        private static bool InvalidSelector(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || value.Contains("*")
                || value.IndexOfAny(ControlChars) >= 0;
        }

        private static readonly char[] ControlChars = Enumerable.Range(0, 32).Select(i => (char)i).Concat(new[] { '\u007f' }).ToArray();

        // 仅解析选择器需要的对象、数组、字符串；不使用会接受非标准 JSON 的通用解析器。
        private sealed class SelectionJsonReader
        {
            private readonly string text;
            private int position;
            internal SelectionJsonReader(string text) { this.text = text ?? ""; }
            internal JsonNode Read()
            {
                var value = Value(0);
                White();
                if (position != text.Length) throw new FormatException();
                return value;
            }
            private void White()
            {
                while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n')) position++;
            }
            private bool Take(char c)
            {
                White();
                if (position >= text.Length || text[position] != c) return false;
                position++;
                return true;
            }
            private JsonNode Value(int depth)
            {
                if (depth > 3) throw new FormatException();
                White();
                if (position < text.Length && text[position] == '"') return new JsonData(Text());
                if (Take('{'))
                {
                    var obj = new JsonClass();
                    if (Take('}')) return obj;
                    do
                    {
                        string key = Text();
                        if (obj.ContainsKey(key) || !Take(':')) throw new FormatException();
                        obj.Add(key, Value(depth + 1));
                        if (Take('}')) return obj;
                    } while (Take(','));
                    throw new FormatException();
                }
                if (Take('['))
                {
                    var array = new JsonArray();
                    if (Take(']')) return array;
                    do
                    {
                        array.Add(Value(depth + 1));
                        if (Take(']')) return array;
                    } while (Take(','));
                }
                throw new FormatException();
            }
            private string Text()
            {
                if (!Take('"')) throw new FormatException();
                var result = new StringBuilder();
                while (position < text.Length)
                {
                    char c = text[position++];
                    if (c == '"') return result.ToString();
                    if (c < 32) throw new FormatException();
                    if (c != '\\') { result.Append(c); continue; }
                    if (position == text.Length) throw new FormatException();
                    c = text[position++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': result.Append(c); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            if (position + 4 > text.Length || !ushort.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) throw new FormatException();
                            result.Append((char)code);
                            position += 4;
                            break;
                        default: throw new FormatException();
                    }
                }
                throw new FormatException();
            }
        }
    }

    internal sealed class McpConnectionInfo
    {
        internal string BaseUrl { get; }
        internal int Port { get; }
        internal string ProjectPath { get; }
        internal int ProcessId { get; }
        internal string UpdatedAt { get; }
        internal int MaxDetails { get; }
        /// <summary>技能源目录绝对路径（unity-package/Skills/unity-skilled-mcp）；未导入时为 null。</summary>
        internal string SkillDir { get; }
        /// <summary>Unity 编辑器版本，服务启动成功时捕获。</summary>
        internal string UnityVersion { get; }
        /// <summary>当前生效的渲染管线：Built-in / URP / HDRP / 自定义管线资源类型全名。</summary>
        internal string RenderPipeline { get; }
        /// <summary>工程根文件夹名，用于多工程 INI 的节名与技能侧 --project 选择。</summary>
        internal string ProjectName { get; }

        // 实例标识存在 SessionState 里：跨域重载存活，编辑器进程退出即失效。
        // 这样脚本重编译、进出 Play Mode 都不会改变绑定，客户端无需重新发现。
        private const string InstanceIdKey = "UniMcp.InstanceId";

        private static string instanceId;
        private static bool instanceIdRestored;

        /// <summary>
        /// 本编辑器会话的实例标识，用于执行绑定的 X-Mcp-Instance-Id 校验。
        /// 域重载后保持不变（SessionState 持久化），只有编辑器进程退出重开才更换，
        /// 因此热重载不会让已连接的客户端产生"实例漂移"、被迫重新拉取工具与详情。
        /// 主线程首次访问时主动恢复：McpService 自动启动与本类 InitializeOnLoadMethod
        /// 的先后顺序不定，若不主动恢复，服务启动写 INI 时会落盘一个临时新 guid，
        /// 与 SessionState 的稳定值不一致，客户端就会误判实例漂移、重走发现流程。
        /// </summary>
        internal static string InstanceId
        {
            get
            {
                if (!instanceIdRestored && UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread())
                {
                    RestoreInstanceId();
                }
                return instanceId ?? (instanceId = Guid.NewGuid().ToString("N"));
            }
        }

        /// <summary>
        /// 当前域的标识，每次域重载重新生成；用于判定落盘任务是否属于"已经死掉的域"。
        /// 与 InstanceId 分开是因为：热重载不该打断客户端绑定，但确实会杀死正在执行的调用。
        /// </summary>
        internal static readonly string DomainId = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 每次域加载后（主线程）从 SessionState 取回实例标识；只恢复一次。
        /// 既由 InitializeOnLoad 调用，也由 InstanceId getter 在主线程首次访问时调用，
        /// 避免两者先后顺序不定导致的临时新标识落盘。后台线程不触碰编辑器 API。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RestoreInstanceId()
        {
            if (instanceIdRestored) return;
            instanceIdRestored = true;
            string stored = SessionState.GetString(InstanceIdKey, string.Empty);
            if (string.IsNullOrEmpty(stored))
            {
                stored = Guid.NewGuid().ToString("N");
                SessionState.SetString(InstanceIdKey, stored);
            }
            instanceId = stored;
        }

        internal McpConnectionInfo(IEnumerable<string> boundPrefixes, string projectPath, int processId, DateTime updatedAt,
            int maxDetails = McpDiscoverySnapshot.DefaultMaxDetails, string skillDir = null,
            string unityVersion = null, string renderPipeline = null)
        {
            var prefixes = boundPrefixes.ToArray();
            // 优先选择显式成功绑定的 localhost，其次 127.0.0.1；通配监听只发布回环地址。
            string prefix = prefixes.FirstOrDefault(p => p.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase))
                ?? prefixes.FirstOrDefault(p => p.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase));
            if (prefix == null)
            {
                string wildcard = prefixes.First(p => p.StartsWith("http://+:", StringComparison.Ordinal) || p.StartsWith("http://*:", StringComparison.Ordinal));
                prefix = wildcard.Replace("http://+:", "http://localhost:").Replace("http://*:", "http://localhost:");
            }
            var uri = new Uri(prefix);
            BaseUrl = uri.GetLeftPart(UriPartial.Authority);
            Port = uri.Port;
            ProjectPath = projectPath;
            ProjectName = ProjectNameOf(projectPath);
            ProcessId = processId;
            UpdatedAt = updatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            MaxDetails = McpDiscoverySnapshot.NormalizeMaxDetails(maxDetails);
            SkillDir = string.IsNullOrEmpty(skillDir) ? null : skillDir;
            UnityVersion = string.IsNullOrEmpty(unityVersion) ? UnityEngine.Application.unityVersion : unityVersion;
            RenderPipeline = string.IsNullOrEmpty(renderPipeline) ? DetectRenderPipeline() : renderPipeline;
        }

        /// <summary>
        /// 当前生效的渲染管线（GraphicsSettings.currentRenderPipeline 已含 QualitySettings 覆盖）。
        /// 内置管线返回 Built-in，URP/HDRP 归一化为简称，自定义 SRP 返回管线资源类型全名。
        /// 读取失败不影响服务启动，仅记为 unknown。仅在主线程、服务启动成功时调用。
        /// </summary>
        internal static string DetectRenderPipeline()
        {
            try
            {
                var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                if (pipeline == null) return "Built-in";
                Type type = pipeline.GetType();
                string name = type.FullName ?? type.Name;
                if (name.IndexOf("UniversalRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf(".Universal.", StringComparison.OrdinalIgnoreCase) >= 0) return "URP";
                if (name.IndexOf("HDRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf(".HighDefinition.", StringComparison.OrdinalIgnoreCase) >= 0) return "HDRP";
                return name;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[UniMcp] 读取渲染管线类型失败（INI 记为 unknown）: " + ex.Message);
                return "unknown";
            }
        }

        internal string ToJson()
        {
            return new JsonClass {
                { "base_url", new JsonData(BaseUrl) },
                { "port", new JsonData(Port) },
                { "project_path", new JsonData(ProjectPath) },
                { "unity_version", new JsonData(UnityVersion) },
                { "render_pipeline", new JsonData(RenderPipeline) },
                { "instance_id", new JsonData(InstanceId) },
                { "process_id", new JsonData(ProcessId) },
                { "updated_at", new JsonData(UpdatedAt) },
                { "groups_url", new JsonData(BaseUrl + "/groups") },
                { "prompts_url", new JsonData(BaseUrl + "/prompts") },
                { "resources_url", new JsonData(BaseUrl + "/resources") },
                { "details_url", new JsonData(BaseUrl + "/capabilities/details") },
                { "schemas_url", new JsonData(BaseUrl + "/capabilities/schemas") },
                { "max_details", new JsonData(MaxDetails) },
                { "max_response_bytes", new JsonData(McpDiscoverySnapshot.MaxResponseBytes) },
                { "skill_dir", new JsonData(SkillDir ?? string.Empty) },
                { "tools_url", new JsonData(BaseUrl + "/tools") },
                { "schema_url_template", new JsonData(BaseUrl + "/tools/{name}/schema") }
            }.ToString();
        }

        /// <summary>兼容别名节名：始终记录最近启动的实例，供旧客户端按名字读取。</summary>
        internal const string AliasSection = "unity3d-skilled-mcp";
        /// <summary>按工程分组的节名前缀；节名形如 project:工程名（同名冲突时追加 #路径哈希）。</summary>
        internal const string ProjectSectionPrefix = "project:";
        // 跨进程写入互斥体名：多个 Unity 实例同时改写同一份 skill.ini 时串行化。
        private const string IniMutexName = "UniMcp.SkillIni";

        /// <summary>别名节：不指定工程时的兜底，等价于“最近启动的实例”。</summary>
        internal string ToIni()
        {
            return "[" + AliasSection + "]\n" + SectionBody();
        }

        /// <summary>按工程分组的节；sectionName 由 MergeIni 依工程名与冲突情况决定。</summary>
        internal string ToProjectIni(string sectionName)
        {
            return "[" + sectionName + "]\n" + SectionBody();
        }

        // 别名节与工程节字段顺序保持一致；project_name / instance_id 供多工程选择与陈旧节判定。
        private string SectionBody()
        {
            return "base_url=" + BaseUrl + "\n"
                + "port=" + Port.ToString(CultureInfo.InvariantCulture) + "\n"
                + "project_path=" + IniValue(ProjectPath) + "\n"
                + "project_name=" + IniValue(ProjectName) + "\n"
                + "instance_id=" + InstanceId + "\n"
                + "unity_version=" + IniValue(UnityVersion) + "\n"
                + "render_pipeline=" + IniValue(RenderPipeline) + "\n"
                + "skill_dir=" + IniValue(SkillDir) + "\n"
                + "process_id=" + ProcessId.ToString(CultureInfo.InvariantCulture) + "\n"
                + "updated_at=" + UpdatedAt + "\n"
                + "max_details=" + MaxDetails.ToString(CultureInfo.InvariantCulture) + "\n";
        }

        // 防止合法但包含换行的值注入额外 INI 项。
        private static string IniValue(string value)
        {
            return (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
        }

        /// <summary>
        /// 多工程共用一份 skill.ini：读取旧文件 → 删同 project_path 的旧节与进程已亡的陈旧节
        /// → 重写别名节与本工程节 → 原样保留其它未知节 → 原子替换。全程持命名互斥体，避免多实例互相覆盖。
        /// </summary>
        internal void WriteIni(string path)
        {
            // 目录按需创建（默认发布目录 ~/.unity-mcp 首次启动时可能不存在）。
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            System.Threading.Mutex mutex = null;
            bool acquired = false;
            try
            {
                try { mutex = new System.Threading.Mutex(false, IniMutexName); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("[UniMcp] 创建 INI 写入互斥体失败（退化为无锁写入）: " + ex.Message); }
                if (mutex != null)
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (System.Threading.AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("获取 skill.ini 写入互斥体超时");
                }
                // 读取失败（如目标被独占）直接抛出，不覆盖旧文件。
                string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
                WriteAtomic(path, MergeIni(existing));
            }
            finally
            {
                if (mutex != null)
                {
                    if (acquired) { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } }
                    mutex.Dispose();
                }
            }
        }

        private static void WriteAtomic(string path, string content)
        {
            // 同目录临时文件保证同卷原子替换；失败时保留旧文件，不使用先删后移的降级路径。
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private string MergeIni(string existing)
        {
            var blocks = ParseIni(existing);
            string ourKey = ProjectKey(ProjectPath);
            var preamble = new List<IniBlock>();
            var kept = new List<IniBlock>();
            foreach (var block in blocks)
            {
                if (block.Name == null) { preamble.Add(block); continue; }
                // 别名节稍后按本实例重写。
                if (string.Equals(block.Name, AliasSection, StringComparison.OrdinalIgnoreCase)) continue;
                if (block.Name.StartsWith(ProjectSectionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string otherPath = IniField(block, "project_path");
                    // 同一工程的旧节（含改端口前）直接替换。
                    if (otherPath != null && string.Equals(ProjectKey(otherPath), ourKey, StringComparison.Ordinal)) continue;
                    // 进程已亡的陈旧节直接清理。
                    if (!IsProcessAlive(IniField(block, "process_id"))) continue;
                }
                kept.Add(block);
            }
            string baseName = ProjectSectionPrefix + SanitizeSectionName(ProjectName);
            bool nameTaken = kept.Any(b => string.Equals(b.Name, baseName, StringComparison.OrdinalIgnoreCase));
            string sectionName = nameTaken ? baseName + "#" + PathHash(ProjectPath) : baseName;

            var parts = new List<string>();
            foreach (var block in preamble)
            {
                string body = string.Join("\n", block.Lines).Trim('\n');
                if (body.Trim().Length > 0) parts.Add(body);
            }
            parts.Add(ToIni().TrimEnd('\n'));
            parts.Add(ToProjectIni(sectionName).TrimEnd('\n'));
            foreach (var block in kept)
                parts.Add(("[" + block.Name + "]\n" + string.Join("\n", block.Lines)).Trim('\n'));
            return string.Join("\n\n", parts) + "\n";
        }

        // 极简 INI 解析：仅按节切块并保留原始行，未知节与注释原样回写。
        private sealed class IniBlock
        {
            internal string Name;
            internal readonly List<string> Lines = new List<string>();
        }

        private static List<IniBlock> ParseIni(string text)
        {
            var blocks = new List<IniBlock>();
            var current = new IniBlock();
            if (!string.IsNullOrEmpty(text))
            {
                foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    string trimmed = raw.Trim();
                    if (trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']'
                        && trimmed.IndexOf('=') < 0)
                    {
                        blocks.Add(current);
                        current = new IniBlock { Name = trimmed.Substring(1, trimmed.Length - 2).Trim() };
                    }
                    else
                    {
                        current.Lines.Add(raw);
                    }
                }
            }
            blocks.Add(current);
            return blocks;
        }

        private static string IniField(IniBlock block, string key)
        {
            foreach (string line in block.Lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';') continue;
                int equals = trimmed.IndexOf('=');
                int colon = trimmed.IndexOf(':');
                int separator = equals < 0 ? colon : colon < 0 ? equals : Math.Min(equals, colon);
                if (separator <= 0) continue;
                if (string.Equals(trimmed.Substring(0, separator).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(separator + 1).Trim();
            }
            return null;
        }

        // 进程已亡的工程节视为陈旧：仅在确定进程不存在时清理，判断不了则保留。
        private static bool IsProcessAlive(string processIdText)
        {
            if (string.IsNullOrEmpty(processIdText)) return true;
            int pid;
            if (!int.TryParse(processIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) || pid <= 0)
                return true;
            try
            {
                using (System.Diagnostics.Process.GetProcessById(pid)) { }
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (Exception) { return true; }
        }

        // 工程根文件夹名；取不到时用固定名，避免空节名。
        private static string ProjectNameOf(string projectPath)
        {
            string trimmed = (projectPath ?? string.Empty).TrimEnd('/', '\\');
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? "unity-project" : name;
        }

        // 节名里不允许出现 [ ] # = 与控制字符（# 留给同名冲突的路径哈希后缀）。
        private static string SanitizeSectionName(string name)
        {
            var builder = new StringBuilder((name ?? string.Empty).Length);
            foreach (char c in name ?? string.Empty)
                builder.Append(c == '[' || c == ']' || c == '#' || c == '=' || char.IsControl(c) ? '_' : c);
            return builder.ToString();
        }

        // Windows 路径大小写不敏感；统一分隔符并归一大小写后作工程键。
        private static string ProjectKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string normalized = path.Replace('\\', '/').TrimEnd('/');
            return Path.DirectorySeparatorChar == '\\' ? normalized.ToLowerInvariant() : normalized;
        }

        // 同名工程在不同路径时用其工程键前 8 位十六进制哈希区分节名。
        private static string PathHash(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(ProjectKey(path)));
                var builder = new StringBuilder(8);
                for (int i = 0; i < 4; i++) builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }
    }
}
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
                    AddAsyncMcpTool();
                    AddBatchMcpTool();
                    AddSyncMcpTool();
                    AddGetPromptDetailTool();
                    AddSearchMcpTool();
                }
                var tools = new List<ToolInfo>();
                foreach (var info in toolInfos.Values)
                {
                    if (!settings.IsToolEnabled(info.name)) continue;
                    if (availableTools.TryGetValue(info.name, out var instance))
                    {
                        tools.Add(new ToolInfo {
                            name = info.name,
                            description = string.IsNullOrEmpty(instance.Description) ? info.description : instance.Description,
                            inputSchema = RebuildInputSchema(instance, true)
                        });
                    }
                    else if (mcpToolInstanceCache.ContainsKey(info.name)) tools.Add(info);
                }
                var groups = availableTools.ToDictionary(pair => pair.Key, pair =>
                    (ToolNameAttribute)Attribute.GetCustomAttribute(pair.Value.GetType(), typeof(ToolNameAttribute))
                    ?? new ToolNameAttribute(pair.Key), StringComparer.Ordinal);
                var toolTags = availableTools.ToDictionary(pair => pair.Key, pair =>
                    McpDiscoverySnapshot.GetTags(pair.Value.GetType(), McpDiscoverySnapshot.Slug(groups[pair.Key].GroupNameEnglish)),
                    StringComparer.Ordinal);
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
            var info = new McpConnectionInfo(listener.Prefixes.Cast<string>(),
                Path.GetDirectoryName(UnityEngine.Application.dataPath),
                System.Diagnostics.Process.GetCurrentProcess().Id, DateTime.UtcNow,
                McpLocalSettings.Instance.ScriptLanguage, startedDiscoveryMaxDetails);
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
            if (!McpDiscoverySnapshot.IsDetailsPath(path)) return false;
            int status = 400;
            string json;
            if (request.HttpMethod != "POST")
                json = McpDiscoverySnapshot.Error("此入口仅接受 POST JSON，请指定 tag 或 members。");
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
                    json = snapshot.GetDetails(body, out status);
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
            foreach (var resource in enabledResources.OrderBy(r => r.Url, StringComparer.Ordinal))
            {
                var tags = GetTags(resource.GetType(), "resources");
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

            var groups = new JsonArray();
            foreach (var tag in capabilities.Values.SelectMany(c => c.Tags).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
            {
                var members = capabilities.Values.Where(c => c.Tags.Contains(tag)).ToArray();
                groups.Add(new JsonClass {
                    { "tag", new JsonData(tag) }, { "label", new JsonData(labels.TryGetValue(tag, out var label) ? label : tag) },
                    { "counts", new JsonClass {
                        { "tools", new JsonData(members.Count(c => c.Kind == "tool")) },
                        { "prompts", new JsonData(members.Count(c => c.Kind == "prompt")) },
                        { "resources", new JsonData(members.Count(c => c.Kind == "resource")) }
                    } }
                });
            }
            catalogs.Add("/groups", new JsonClass {
                { "groups", groups }, { "max_details", new JsonData(MaxDetails) },
                { "max_response_bytes", new JsonData(MaxResponseBytes) }
            }.ToString());
        }

        private void Add(string kind, string name, JsonClass detail, string[] tags)
        {
            capabilities.Add(kind + ":" + name, new Capability(kind, detail.ToString(), tags));
        }

        internal static string Slug(string englishName)
        {
            string slug = System.Text.RegularExpressions.Regex.Replace((englishName ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return string.IsNullOrEmpty(slug) ? "uncategorized" : slug;
        }

        internal static string[] GetTags(Type type, string fallback)
        {
            var attribute = (CapabilityTagsAttribute)Attribute.GetCustomAttribute(type, typeof(CapabilityTagsAttribute));
            var tags = (attribute == null ? new string[0] : attribute.Tags)
                .Where(t => !string.IsNullOrWhiteSpace(t)).Select(Slug)
                .Where(t => t != "all").Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray();
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
                    string key = kind + ":" + member[field].Value;
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

        private static bool InvalidSelector(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || value.Contains("*");
        }

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
        internal string ScriptLanguage { get; }
        internal int MaxDetails { get; }

        internal McpConnectionInfo(IEnumerable<string> boundPrefixes, string projectPath, int processId, DateTime updatedAt,
            string scriptLanguage = "js", int maxDetails = McpDiscoverySnapshot.DefaultMaxDetails)
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
            ProcessId = processId;
            UpdatedAt = updatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            ScriptLanguage = McpLocalSettings.NormalizeScriptLanguage(scriptLanguage);
            MaxDetails = McpDiscoverySnapshot.NormalizeMaxDetails(maxDetails);
        }

        internal string ToJson()
        {
            return new JsonClass {
                { "base_url", new JsonData(BaseUrl) },
                { "port", new JsonData(Port) },
                { "project_path", new JsonData(ProjectPath) },
                { "process_id", new JsonData(ProcessId) },
                { "updated_at", new JsonData(UpdatedAt) },
                { "script_language", new JsonData(ScriptLanguage) },
                { "groups_url", new JsonData(BaseUrl + "/groups") },
                { "prompts_url", new JsonData(BaseUrl + "/prompts") },
                { "resources_url", new JsonData(BaseUrl + "/resources") },
                { "details_url", new JsonData(BaseUrl + "/capabilities/details") },
                { "max_details", new JsonData(MaxDetails) },
                { "max_response_bytes", new JsonData(McpDiscoverySnapshot.MaxResponseBytes) },
                { "tools_url", new JsonData(BaseUrl + "/tools") },
                { "schema_url_template", new JsonData(BaseUrl + "/tools/{name}/schema") }
            }.ToString();
        }

        internal string ToIni()
        {
            // 防止合法但包含换行的工程路径注入额外 INI 项。
            string projectPath = ProjectPath.Replace("\r", "\\r").Replace("\n", "\\n");
            return "[unity3d-skilled-mcp]\n"
                + "base_url=" + BaseUrl + "\n"
                + "port=" + Port.ToString(CultureInfo.InvariantCulture) + "\n"
                + "project_path=" + projectPath + "\n"
                + "process_id=" + ProcessId.ToString(CultureInfo.InvariantCulture) + "\n"
                + "updated_at=" + UpdatedAt + "\n"
                + "script_language=" + ScriptLanguage + "\n"
                + "max_details=" + MaxDetails.ToString(CultureInfo.InvariantCulture) + "\n";
        }

        internal void WriteIni(string path)
        {
            // 目录按需创建（默认发布目录 ~/.unity-mcp 首次启动时可能不存在）。
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // 同目录临时文件保证同卷原子替换；失败时保留旧文件，不使用先删后移的降级路径。
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(ToIni());
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
    }
}
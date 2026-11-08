using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UniMcp;
using UniMcp.Models;

namespace UniMcp.Tools
{
    /// <summary>
    /// Built-in management surface for the three MCP primitives: tools, prompts
    /// and resources.
    ///
    /// MCP clients can already *consume* the three primitives (tools/list,
    /// prompts/list, resources/list). This module closes the loop by letting the
    /// agent *inspect and change* the server's own capability registry:
    ///   - discover what exists and whether each item is enabled,
    ///   - flip items on/off (the switch lives in McpLocalSettings),
    ///   - create / update / delete configured prompts and resources
    ///     (persisted in McpSettings.asset),
    ///   - read and write the shared settings that shape what is published.
    ///
    /// Every mutating action calls McpService.RefreshDiscovery() so a connected
    /// client sees the change on its next list call (list_changed semantics).
    /// All state here is editor-local: nothing is written outside ProjectSettings
    /// and UserSettings.
    /// </summary>
    [ToolModule("capability", GroupEn = "System", GroupZh = "系统工具")]
    public class CapabilityTool : ToolModuleBase
    {
        public override string Description => L.T(
            "Inspect and change this MCP server's own capability registry: tools, prompts and resources. Use action='list' to discover, 'get' to read one item, 'set' to enable/disable, 'save' to create or update a configured prompt/resource, 'remove' to delete one, 'settings' to read or write shared settings.",
            "查看并修改本 MCP 服务自身的能力注册表：工具、提示词与资源。action='list' 发现，'get' 读取单项，'set' 启停，'save' 新增或更新可配置的提示词/资源，'remove' 删除，'settings' 读写通用设置。");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T(
                    "Operation to perform.", "要执行的操作。"))
                    .SetEnumValues("list", "get", "set", "save", "remove", "settings")
                    .SetDefault("list"),

                new MethodStr("kind", L.T(
                    "Capability kind: tool, prompt or resource.", "能力类别：tool、prompt 或 resource。"))
                    .SetEnumValues("tool", "prompt", "resource").SetDefault("tool"),

                new MethodStr("name", L.T(
                    "Target name. Tools use the full 'group.action' name; prompts use their name; resources accept name or URI.",
                    "目标名称。工具用完整的 '分组.动作' 名；提示词用其名称；资源可传名称或 URI。"))
                    .AddExamples("texgen.render", "get_tool"),

                new MethodBool("enabled", L.T(
                    "Enable (true) or disable (false) the target. Used by action='set'.",
                    "启用（true）或禁用（false）目标。用于 action='set'。")).SetDefault(true),

                new MethodStr("filter", L.T(
                    "Optional substring filter applied to names in action='list'.",
                    "action='list' 时按名称子串过滤，可省略。"))
                    .AddExample("tex"),

                new MethodStr("description", L.T(
                    "Description for the prompt/resource being saved.",
                    "要保存的提示词/资源的描述。")),

                new MethodStr("prompt_text", L.T(
                    "Prompt body text. Required when saving a prompt.",
                    "提示词正文。保存提示词时必填。"))
                    .AddExample("Draw a magic orb using texgen.render ..."),

                new MethodStr("url", L.T(
                    "Resource URL for the resource being saved, e.g. https://... or a project-relative path.",
                    "要保存资源的 URL，如 https://... 或工程相对路径。"))
                    .AddExample("https://example.com/guide.md"),

                new MethodStr("mime_type", L.T(
                    "MIME type for the resource. Defaults to application/octet-stream.",
                    "资源的 MIME 类型。默认 application/octet-stream。"))
                    .SetDefault("application/octet-stream"),

                new MethodStr("argument_names", L.T(
                    "Comma-separated argument names when saving a prompt (optional).",
                    "保存提示词时的参数名列表，逗号分隔（可选）。"))
                    .AddExample("style,size"),

                new MethodStr("argument_types", L.T(
                    "Comma-separated argument types matching argument_names, e.g. string,integer.",
                    "与 argument_names 对应的参数类型，如 string,integer。"))
                    .AddExample("string,integer"),

                new MethodStr("argument_required", L.T(
                    "Comma-separated 0/1 flags matching argument_names marking each as required.",
                    "与 argument_names 对应的 0/1 标记，标出哪些参数必填。"))
                    .AddExample("0,1"),

                new MethodBool("enable_descriptions", L.T(
                    "Shared setting: publish descriptions on list calls. Used by action='settings'.",
                    "通用设置：列表调用是否返回描述。用于 action='settings'。")),

                new MethodStr("language", L.T(
                    "Shared setting: UI/server language, English or Chinese.",
                    "通用设置：界面/服务语言，English 或 Chinese。"))
                    .SetEnumValues("English", "Chinese"),

                new MethodInt("max_details", L.T(
                    "Shared setting: detail budget per discover request (0 = unlimited).",
                    "通用设置：每次发现请求的详情上限（0 表示不限）。"))
                    .SetRange(0, 512),

                new MethodStr("confirm", L.T(
                    "Safety confirmation. Pass 'DISABLE_ALL' to disable every tool at once.",
                    "安全确认。一次性禁用全部工具需传 'DISABLE_ALL'。"))
                    .AddExample("DISABLE_ALL")
            };
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------

        [ToolAction("list",
            DescriptionEn = "List capabilities of one kind (tool, prompt or resource) with their enabled state.",
            DescriptionZh = "列出某一类能力（tool、prompt 或 resource）及其启用状态。",
            Parameters = new[] { "kind", "filter" },
            Inject = new[] { "action=list" })]
        private object ActionList(JsonClass args)
        {
            string kind = NormalizeKind(args["kind"]?.Value);
            string filter = args["filter"]?.Value;
            var settings = McpLocalSettings.Instance;

            var items = new JsonArray();
            switch (kind)
            {
                case "prompt":
                    foreach (var prompt in McpService.GetAllPrompts().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (prompt == null || !Matches(prompt.Name, filter)) continue;
                        items.Add(Item(prompt.Name, prompt.Description, settings.IsPromptEnabled(prompt.Name)));
                    }
                    break;
                case "resource":
                    foreach (var resource in McpService.GetAllResources().OrderBy(r => r.Url, StringComparer.Ordinal))
                    {
                        if (resource == null) continue;
                        string label = string.IsNullOrEmpty(resource.Name) ? resource.Url : resource.Name;
                        if (!Matches(label, filter) && !Matches(resource.Url, filter)) continue;
                        var item = Item(label, resource.Description, settings.IsResourceEnabled(resource.Name));
                        item.Add("url", new JsonData(resource.Url ?? ""));
                        item.Add("mime_type", new JsonData(resource.MimeType ?? ""));
                        items.Add(item);
                    }
                    break;
                default:
                    foreach (var spec in McpService.GetAllToolSpecs().OrderBy(s => s.Name, StringComparer.Ordinal))
                    {
                        if (!Matches(spec.Name, filter)) continue;
                        var item = Item(spec.Name, spec.Description, settings.IsToolEnabled(spec.Name));
                        item.Add("group", new JsonData(spec.GroupName ?? ""));
                        items.Add(item);
                    }
                    foreach (var info in McpService.GetBuiltinToolInfos())
                    {
                        if (!Matches(info.name, filter)) continue;
                        var item = Item(info.name, info.description, settings.IsToolEnabled(info.name));
                        item.Add("group", new JsonData("builtin"));
                        items.Add(item);
                    }
                    break;
            }

            return Response.Success(
                L.T($"Listed {items.Count} {kind}(s).", $"已列出 {items.Count} 个 {kind}。"),
                new JsonClass { { "kind", new JsonData(kind) }, { "count", new JsonData(items.Count) }, { "items", items } });
        }

        [ToolAction("get",
            DescriptionEn = "Read one capability's details, including full prompt body or resource URI.",
            DescriptionZh = "读取单项能力详情，含提示词正文或资源 URI。",
            Parameters = new[] { "kind", "name" },
            Inject = new[] { "action=get" })]
        private object ActionGet(JsonClass args)
        {
            string kind = NormalizeKind(args["kind"]?.Value);
            string name = args["name"]?.Value;
            if (string.IsNullOrWhiteSpace(name))
                return Response.Error(L.T("'name' is required for action='get'.", "action='get' 需要 'name'。"));

            var settings = McpLocalSettings.Instance;
            var detail = new JsonClass { { "kind", new JsonData(kind) }, { "name", new JsonData(name) } };

            switch (kind)
            {
                case "prompt":
                {
                    var prompt = McpService.GetAllPrompts()
                        .FirstOrDefault(p => p != null && string.Equals(p.Name, name, StringComparison.Ordinal));
                    if (prompt == null)
                        return Response.Error(L.T($"Prompt not found: {name}", $"未找到提示词：{name}"));
                    detail.Add("description", new JsonData(prompt.Description ?? ""));
                    detail.Add("prompt_text", new JsonData(prompt.PromptText ?? ""));
                    detail.Add("enabled", new JsonData(settings.IsPromptEnabled(prompt.Name)));
                    var arguments = new JsonArray();
                    foreach (var key in prompt.Keys ?? new MethodKey[0])
                    {
                        arguments.Add(new JsonClass {
                            { "name", new JsonData(key.Key) },
                            { "type", new JsonData(key.Type ?? "string") },
                            { "required", new JsonData(!key.Optional) }
                        });
                    }
                    detail.Add("arguments", arguments);
                    break;
                }
                case "resource":
                {
                    var resource = McpService.GetAllResources().FirstOrDefault(r =>
                        r != null && (string.Equals(r.Name, name, StringComparison.Ordinal) ||
                                      string.Equals(r.Url, name, StringComparison.Ordinal)));
                    if (resource == null)
                        return Response.Error(L.T($"Resource not found: {name}", $"未找到资源：{name}"));
                    detail.Add("description", new JsonData(resource.Description ?? ""));
                    detail.Add("url", new JsonData(resource.Url ?? ""));
                    detail.Add("mime_type", new JsonData(resource.MimeType ?? ""));
                    detail.Add("enabled", new JsonData(settings.IsResourceEnabled(resource.Name)));
                    break;
                }
                default:
                {
                    var spec = McpService.GetAllToolSpecs().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
                    if (spec != null)
                    {
                        detail.Add("name", new JsonData(spec.Name));
                        detail.Add("description", new JsonData(spec.Description ?? ""));
                        detail.Add("group", new JsonData(spec.GroupName ?? ""));
                        detail.Add("enabled", new JsonData(settings.IsToolEnabled(spec.Name)));
                        var parameters = new JsonArray();
                        foreach (var key in spec.Parameters)
                        {
                            parameters.Add(new JsonClass {
                                { "name", new JsonData(key.Key) },
                                { "type", new JsonData(key.Type ?? "string") },
                                { "required", new JsonData(spec.Required.Contains(key.Key)) }
                            });
                        }
                        detail.Add("parameters", parameters);
                        break;
                    }
                    var info = McpService.GetBuiltinToolInfos().FirstOrDefault(i => string.Equals(i.name, name, StringComparison.Ordinal));
                    if (info == null)
                        return Response.Error(L.T($"Tool not found: {name}", $"未找到工具：{name}"));
                    detail.Add("description", new JsonData(info.description ?? ""));
                    detail.Add("group", new JsonData("builtin"));
                    detail.Add("enabled", new JsonData(settings.IsToolEnabled(info.name)));
                    break;
                }
            }

            return Response.Success(L.T($"Detail of {name}.", $"{name} 的详情。"), detail);
        }

        [ToolAction("set",
            DescriptionEn = "Enable or disable one capability, or a whole group when name is omitted for tools.",
            DescriptionZh = "启用或禁用某项能力；工具在省略 name 时可作用于整组。",
            Parameters = new[] { "kind", "name", "enabled", "confirm" },
            Inject = new[] { "action=set" })]
        private object ActionSet(JsonClass args)
        {
            string kind = NormalizeKind(args["kind"]?.Value);
            bool enabled = args["enabled"] == null || args["enabled"].AsBoolDefault(true);
            string name = args["name"]?.Value;
            var settings = McpLocalSettings.Instance;

            List<string> targets;
            switch (kind)
            {
                case "prompt":
                {
                    var names = McpService.GetAllPrompts().Select(p => p?.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
                    targets = Resolve(names, name);
                    if (targets.Count == 0) return Response.Error(L.T($"Prompt not found: {name}", $"未找到提示词：{name}"));
                    settings.SetPromptsEnabled(targets, enabled);
                    break;
                }
                case "resource":
                {
                    var names = McpService.GetAllResources().Select(r => r?.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
                    targets = Resolve(names, name);
                    if (targets.Count == 0) return Response.Error(L.T($"Resource not found: {name}", $"未找到资源：{name}"));
                    settings.SetResourcesEnabled(targets, enabled);
                    break;
                }
                default:
                {
                    var names = McpService.GetAllToolSpecs().Select(s => s.Name)
                        .Concat(McpService.GetBuiltinToolInfos().Select(i => i.name))
                        .ToList();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        if (enabled)
                            return Response.Error(L.T(
                                "Enabling every tool at once is ambiguous; pass a 'name' or a group prefix such as 'texgen'.",
                                "一次性启用全部工具过于宽泛；请传入 'name' 或分组前缀（如 'texgen'）。"));
                        // 声明式目录 + 3 个内置门面（get_prompt / get_resource / get_tool）。
                        if (!ConfirmAll(ToolCatalog.Count + 3, args["confirm"]?.Value))
                            return Response.Error(L.T(
                                "Refused: disabling every tool requires confirm='DISABLE_ALL'.",
                                "已拒绝：禁用全部工具需传 confirm='DISABLE_ALL'。"));
                        // 始终为恢复通道留一扇门。
                        targets = names.Where(n => !string.Equals(n, "capability.set", StringComparison.Ordinal)).ToList();
                        settings.SetToolsEnabled(targets, false);
                    }
                    else
                    {
                        targets = Resolve(names, name, groupAware: true);
                        if (targets.Count == 0) return Response.Error(L.T($"Tool not found: {name}", $"未找到工具：{name}"));
                        // 自锁保护：capability.set 是经 MCP 恢复一切开关的唯一入口，
                        // 禁用它将使客户端再也无法重新启用任何工具。
                        if (!enabled && targets.Contains("capability.set"))
                            return Response.Error(L.T(
                                "Refused: 'capability.set' is the recovery lever for every switch and cannot disable itself. Disable other capability actions individually if needed.",
                                "已拒绝：'capability.set' 是恢复所有开关的唯一入口，不能禁用自身。如需收窄，请单独禁用其他 capability 动作。"));
                        settings.SetToolsEnabled(targets, enabled);
                    }
                    break;
                }
            }

            McpService.RefreshDiscovery();
            var affected = new JsonArray();
            foreach (var target in targets) affected.Add(new JsonData(target));
            return Response.Success(
                L.T($"{(enabled ? "Enabled" : "Disabled")} {targets.Count} {kind}(s).",
                    $"已{(enabled ? "启用" : "禁用")} {targets.Count} 个 {kind}。"),
                new JsonClass {
                    { "kind", new JsonData(kind) },
                    { "enabled", new JsonData(enabled) },
                    { "affected", affected }
                });
        }

        [ToolAction("save",
            DescriptionEn = "Create or update a configured prompt or resource (persisted to McpSettings.asset).",
            DescriptionZh = "新增或更新一个可配置的提示词或资源（写入 McpSettings.asset）。",
            Parameters = new[] {
                "kind", "name", "description", "prompt_text", "url", "mime_type",
                "argument_names", "argument_types", "argument_required"
            },
            Inject = new[] { "action=save" })]
        private object ActionSave(JsonClass args)
        {
            string kind = NormalizeKind(args["kind"]?.Value);
            string name = args["name"]?.Value;
            if (string.IsNullOrWhiteSpace(name))
                return Response.Error(L.T("'name' is required for action='save'.", "action='save' 需要 'name'。"));
            if (kind == "tool")
                return Response.Error(L.T(
                    "Tools are discovered from code and cannot be saved here; use action='set' to enable or disable one.",
                    "工具由代码发现，不能在此保存；请用 action='set' 启停。"));

            var settings = McpSettings.Instance;
            if (kind == "prompt")
            {
                string text = args["prompt_text"]?.Value;
                if (string.IsNullOrWhiteSpace(text))
                    return Response.Error(L.T(
                        "'prompt_text' is required when saving a prompt.",
                        "保存提示词时 'prompt_text' 必填。"));

                var prompt = new ConfigurablePrompt(name, args["description"]?.Value ?? "", text);
                prompt.SetKeys(BuildKeys(args));
                settings.AddConfigurablePrompt(prompt);
                settings.SaveSettings();
                // 用户显式保存即为想使用它，自动启用。
                McpLocalSettings.Instance.SetPromptEnabled(name, true);
                McpService.RefreshDiscovery();
                return Response.Success(L.T($"Prompt '{name}' saved.", $"提示词 '{name}' 已保存。"),
                    new JsonClass { { "kind", new JsonData("prompt") }, { "name", new JsonData(name) } });
            }

            string url = args["url"]?.Value;
            if (string.IsNullOrWhiteSpace(url))
                return Response.Error(L.T(
                    "'url' is required when saving a resource.",
                    "保存资源时 'url' 必填。"));

            string mime = args["mime_type"]?.Value;
            var resource = new ConfigurableResource(name, args["description"]?.Value ?? "", url,
                string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime);
            settings.AddConfigurableResource(resource);
            settings.SaveSettings();
            McpLocalSettings.Instance.SetResourceEnabled(name, true);
            McpService.RefreshDiscovery();
            return Response.Success(L.T($"Resource '{name}' saved.", $"资源 '{name}' 已保存。"),
                new JsonClass { { "kind", new JsonData("resource") }, { "name", new JsonData(name) }, { "url", new JsonData(url) } });
        }

        [ToolAction("remove",
            DescriptionEn = "Delete a configured prompt or resource. Code-defined tools cannot be removed, only disabled.",
            DescriptionZh = "删除一个可配置的提示词或资源。代码定义的级工具不能删除，只能禁用。",
            Parameters = new[] { "kind", "name" },
            Inject = new[] { "action=remove" })]
        private object ActionRemove(JsonClass args)
        {
            string kind = NormalizeKind(args["kind"]?.Value);
            string name = args["name"]?.Value;
            if (string.IsNullOrWhiteSpace(name))
                return Response.Error(L.T("'name' is required for action='remove'.", "action='remove' 需要 'name'。"));
            if (kind == "tool")
                return Response.Error(L.T(
                    "Tools are discovered from code and cannot be removed; use action='set' to disable one.",
                    "工具由代码发现，不能删除；请用 action='set' 禁用。"));

            var settings = McpSettings.Instance;
            if (kind == "prompt")
            {
                bool exists = settings.GetConfigurablePrompts().Any(p => p != null && p.Name == name);
                if (!exists)
                    return Response.Error(L.T(
                        $"No configured prompt named '{name}'. Only configured prompts can be removed.",
                        $"没有名为 '{name}' 的配置提示词。只能删除可配置的提示词。"));
                settings.RemoveConfigurablePrompt(name);
            }
            else
            {
                bool exists = settings.GetConfigurableResources().Any(r => r != null && r.Name == name);
                if (!exists)
                    return Response.Error(L.T(
                        $"No configured resource named '{name}'. Only configured resources can be removed.",
                        $"没有名为 '{name}' 的配置资源。只能删除可配置的资源。"));
                settings.RemoveConfigurableResource(name);
            }

            settings.SaveSettings();
            // 清掉可能残留的禁用记录，避免同名项下次保存后被"记忆"为禁用。
            McpLocalSettings.Instance.SetPromptEnabled(name, true);
            McpLocalSettings.Instance.SetResourceEnabled(name, true);
            McpService.RefreshDiscovery();
            return Response.Success(L.T($"{kind} '{name}' removed.", $"{kind} '{name}' 已删除。"),
                new JsonClass { { "kind", new JsonData(kind) }, { "name", new JsonData(name) } });
        }

        [ToolAction("settings",
            DescriptionEn = "Read or write shared server settings: enable_descriptions, language, max_details.",
            DescriptionZh = "读取或写入通用服务设置：enable_descriptions、language、max_details。",
            Parameters = new[] { "enable_descriptions", "language", "max_details" },
            Inject = new[] { "action=settings" })]
        private object ActionSettings(JsonClass args)
        {
            var settings = McpLocalSettings.Instance;
            bool changed = false;

            if (args["enable_descriptions"] != null)
            {
                settings.EnableDescriptions = args["enable_descriptions"].AsBoolDefault(true);
                changed = true;
            }
            if (args["language"] != null)
            {
                string language = args["language"].Value;
                if (string.Equals(language, "English", StringComparison.OrdinalIgnoreCase)) language = "English";
                else if (string.Equals(language, "Chinese", StringComparison.OrdinalIgnoreCase)) language = "Chinese";
                else if (!string.IsNullOrWhiteSpace(language))
                    return Response.Error(L.T(
                        "language must be 'English' or 'Chinese'.",
                        "language 必须是 'English' 或 'Chinese'。"));
                if (!string.IsNullOrWhiteSpace(language))
                {
                    settings.CurrentLanguage = language;
                    changed = true;
                }
            }
            if (args["max_details"] != null)
            {
                settings.DiscoveryMaxDetails = args["max_details"].AsIntDefault(settings.DiscoveryMaxDetails);
                changed = true;
            }

            if (changed)
            {
                settings.SaveSettings();
                McpService.RefreshDiscovery();
            }

            var snapshot = new JsonClass {
                { "enable_descriptions", new JsonData(settings.EnableDescriptions) },
                { "language", new JsonData(settings.CurrentLanguage ?? "") },
                { "max_details", new JsonData(settings.DiscoveryMaxDetails) },
                { "port", new JsonData(settings.McpServerPort) },
                { "server_running", new JsonData(settings.McpOpenState) },
                { "tool_count", new JsonData(McpService.GetToolCount()) },
                { "enabled_tool_count", new JsonData(McpService.GetEnabledToolCount()) },
                { "disabled_tool_count", new JsonData(settings.GetDisabledTools().Count) }
            };
            return Response.Success(
                changed ? L.T("Settings updated.", "设置已更新。") : L.T("Current settings.", "当前设置。"),
                snapshot);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string NormalizeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return "tool";
            string value = kind.Trim().ToLowerInvariant();
            if (value == "prompts") return "prompt";
            if (value == "resources") return "resource";
            if (value == "tools") return "tool";
            return value;
        }

        private static bool Matches(string value, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static JsonClass Item(string name, string description, bool enabled)
        {
            return new JsonClass {
                { "name", new JsonData(name ?? "") },
                { "description", new JsonData(description ?? "") },
                { "enabled", new JsonData(enabled) }
            };
        }

        /// <summary>
        /// Resolve the names a 'set' should touch. An exact name wins; otherwise,
        /// for tools, a value that is not a full name may still match a group
        /// prefix so "texgen" toggles every texgen.* tool at once.
        /// </summary>
        private static List<string> Resolve(List<string> names, string requested, bool groupAware = false)
        {
            if (string.IsNullOrWhiteSpace(requested)) return new List<string>();
            string value = requested.Trim();

            if (names.Contains(value)) return new List<string> { value };
            if (!groupAware) return new List<string>();

            string prefix = value + ".";
            return names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        private static bool ConfirmAll(int count, string confirm)
        {
            if (count <= 0) return false;
            return string.Equals(confirm, "DISABLE_ALL", StringComparison.Ordinal);
        }

        private static List<ConfigurableMethodKey> BuildKeys(JsonClass args)
        {
            var keys = new List<ConfigurableMethodKey>();
            string rawNames = args["argument_names"]?.Value;
            if (string.IsNullOrWhiteSpace(rawNames)) return keys;

            string[] names = Split(rawNames);
            string[] types = Split(args["argument_types"]?.Value);
            string[] required = Split(args["argument_required"]?.Value);

            for (int i = 0; i < names.Length; i++)
            {
                string key = names[i].Trim();
                if (string.IsNullOrEmpty(key)) continue;
                keys.Add(new ConfigurableMethodKey
                {
                    key = key,
                    desc = "",
                    optional = i >= required.Length || required[i].Trim() != "1",
                    type = i < types.Length && !string.IsNullOrWhiteSpace(types[i]) ? types[i].Trim() : "string"
                });
            }
            return keys;
        }

        private static string[] Split(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new string[0];
            return value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}

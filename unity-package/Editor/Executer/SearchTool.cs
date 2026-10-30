using System;
using System.Collections.Generic;
using System.Linq;
using UniMcp.Models;

namespace UniMcp.Executer
{
    /// <summary>
    /// 搜索/查询工具：获取当前 MCP 支持的所有工具列表及二级工具详情。
    /// 支持两种查询类型：
    ///   - type = "list"       → 返回所有工具的名称与描述概览
    ///   - type = "detail"     → 返回指定工具的完整详情（含参数键与预览）
    /// </summary>
    public sealed class SearchTool : McpTool
    {
        public override string ToolName => "search_tool";

        public override void HandleCommand(JsonNode ctx, Action<JsonNode> callback)
        {
            try
            {
                string type = ctx?["type"]?.Value;

                if (string.IsNullOrWhiteSpace(type))
                {
                    callback(Response.Error("Required parameter 'type' is missing. Use 'list' or 'detail'."));
                    return;
                }

                switch (type)
                {
                    case "list":
                        HandleList(callback);
                        break;
                    case "detail":
                        HandleDetail(ctx, callback);
                        break;
                    default:
                        callback(Response.Error($"Unknown type '{type}'. Supported types: 'list', 'detail'."));
                        break;
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[SearchTool] Command execution failed: {ex.Message}");
                callback(Response.Error($"Internal error: {ex.Message}"));
            }
        }

        /// <summary>
        /// 列出所有已注册的工具名称与描述。
        /// </summary>
        private void HandleList(Action<JsonNode> callback)
        {
            var allMethods = ToolsCall.GetAllRegisteredMethods();
            var list = new List<Dictionary<string, object>>();

            foreach (var kvp in allMethods.OrderBy(k => k.Key))
            {
                if (!McpLocalSettings.Instance.IsToolEnabled(kvp.Key)) continue;
                var entry = new Dictionary<string, object>
                {
                    ["name"] = kvp.Key,
                    ["description"] = kvp.Value.Description ?? ""
                };
                list.Add(entry);
            }

            var data = new Dictionary<string, object>
            {
                ["tools"] = list,
                ["count"] = list.Count
            };

            callback(Response.Success($"Found {list.Count} tools.", data));
        }

        /// <summary>
        /// 获取指定工具的详情，包括参数键列表和状态树预览。
        /// </summary>
        private void HandleDetail(JsonNode ctx, Action<JsonNode> callback)
        {
            string name = ctx?["name"]?.Value;

            if (string.IsNullOrWhiteSpace(name))
            {
                callback(Response.Error("Required parameter 'name' is missing for detail query."));
                return;
            }

            if (!McpLocalSettings.Instance.IsToolEnabled(name))
            {
                callback(Response.Error($"Tool '{name}' not found or disabled."));
                return;
            }

            var method = ToolsCall.GetToolMethodStatic(name);
            if (method == null)
            {
                callback(Response.Error($"Tool '{name}' not found. Use type='list' to see all available tools."));
                return;
            }

            bool includePreview = ctx["preview"]?.AsBool ?? false;

            var detail = new Dictionary<string, object>
            {
                ["name"] = name,
                ["description"] = method.Description ?? ""
            };

            // 参数键列表
            if (method.Keys != null && method.Keys.Length > 0)
            {
                var keysList = new List<Dictionary<string, object>>();
                foreach (var key in method.Keys)
                {
                    var k = new Dictionary<string, object>
                    {
                        ["key"] = key.Key,
                        ["description"] = key.Desc ?? "",
                        ["type"] = key.Type ?? "string",
                        ["optional"] = key.Optional
                    };

                    if (key.DefaultValue != null)
                        k["default"] = key.DefaultValue;

                    if (key.Examples != null && key.Examples.Count > 0)
                        k["examples"] = key.Examples.ToArray();

                    if (key.EnumValues != null && key.EnumValues.Count > 0)
                        k["enum"] = key.EnumValues.ToArray();

                    keysList.Add(k);
                }
                detail["keys"] = keysList;
            }

            // 状态树预览（可选）
            if (includePreview)
            {
                try
                {
                    detail["preview"] = method.Preview() ?? "";
                }
                catch (Exception ex)
                {
                    detail["preview"] = $"Preview generation failed: {ex.Message}";
                }
            }

            callback(Response.Success($"Tool '{name}' detail retrieved.", detail));
        }
    }
}

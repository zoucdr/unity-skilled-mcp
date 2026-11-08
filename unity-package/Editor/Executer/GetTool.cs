using System;
using System.Collections.Generic;
using System.Linq;
using UniMcp.Models;

namespace UniMcp.Executer
{
    /// <summary>
    /// 工具定义门面：与 get_prompt / get_resource 同组，供只会 tools/call 的客户端
    /// 查询工具目录与单个工具定义（等价于 tools/list 与 tools/get 的兜底）。
    /// 支持两种查询类型：
    ///   - type = "list"       → 返回所有工具的名称与描述概览
    ///   - type = "detail"     → 返回指定工具的完整详情（含参数键与 Schema）
    /// </summary>
    public sealed class GetTool : McpTool
    {
        public override string ToolName => "get_tool";

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
                McpLogger.LogError($"[GetTool] Command execution failed: {ex.Message}");
                callback(Response.Error($"Internal error: {ex.Message}"));
            }
        }

        /// <summary>
        /// 列出所有已注册的工具名称与描述。
        /// </summary>
        private void HandleList(Action<JsonNode> callback)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var spec in ToolCatalog.All)
            {
                if (!McpLocalSettings.Instance.IsToolEnabled(spec.Name)) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = spec.Name,
                    ["description"] = spec.Description ?? "",
                    ["group"] = spec.GroupName,
                    ["parameters"] = spec.Parameters.Select(k => k.Key).ToArray(),
                });
            }
            callback(Response.Success("Found " + list.Count + " tools.", new Dictionary<string, object>
            {
                ["tools"] = list,
                ["count"] = list.Count
            }));
        }

        /// <summary>
        /// 获取指定工具的详情，包括参数键列表与状态树预览。
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

            var spec = ToolCatalog.Find(name);
            if (spec == null)
            {
                callback(Response.Error($"Tool '{name}' not found. Use type='list' to see all available tools."));
                return;
            }

            var detail = new Dictionary<string, object>
            {
                ["name"] = spec.Name,
                ["description"] = spec.Description ?? "",
                ["group"] = spec.GroupName,
                ["owner"] = spec.SourceClass,
                ["action"] = spec.Action,
                ["schema"] = ToolSchema.Build(spec, true).ToString(),
            };

            var keysList = new List<Dictionary<string, object>>();
            foreach (var key in spec.Parameters)
            {
                var k = new Dictionary<string, object>
                {
                    ["key"] = key.Key,
                    ["description"] = key.Desc ?? "",
                    ["type"] = key.Type ?? "string",
                    ["optional"] = !spec.Required.Contains(key.Key)
                };
                if (key.DefaultValue != null) k["default"] = key.DefaultValue;
                if (key.Examples != null && key.Examples.Count > 0) k["examples"] = key.Examples.ToArray();
                if (key.EnumValues != null && key.EnumValues.Count > 0) k["enum"] = key.EnumValues.ToArray();
                keysList.Add(k);
            }
            detail["keys"] = keysList;

            callback(Response.Success($"Tool '{name}' detail retrieved.", detail));
        }
    }
}

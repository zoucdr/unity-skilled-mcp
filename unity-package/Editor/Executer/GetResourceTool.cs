using System;
using System.Threading.Tasks;
using UniMcp;
using UniMcp.Executer;
using UniMcp.Models;

namespace UniMcp.Prompts
{
    /// <summary>
    /// 系统自带工具：按 URI（或资源名）读取一个已启用资源的正文。
    /// 与 MCP 原生 resources/read 共用同一读取实现，供只会 tools/call 的 MCP 客户端使用。
    /// </summary>
    public sealed class GetResourceTool : McpTool
    {
        public override string ToolName => "get_resource";

        public override void HandleCommand(JsonNode ctx, Action<JsonNode> callback)
        {
            string uri = ctx?["uri"]?.Value;
            if (string.IsNullOrWhiteSpace(uri))
            {
                callback(Response.Error(L.T(
                    "Parameter 'uri' is required (a resource name can be passed as resource://<name>)",
                    "参数 'uri' 必填（资源名可写成 resource://<名称>）")));
                return;
            }

            // 读取可能含 HTTP 下载：不阻塞 Unity 主线程，完成后再回调。
            _ = ReadAsync(uri, callback);
        }

        private static async Task ReadAsync(string uri, Action<JsonNode> callback)
        {
            try
            {
                JsonNode contents = await McpService.Instance.ReadResourceContentsForTool(uri);
                var result = new JsonClass {
                    { "uri", new JsonData(uri) },
                    { "contents", contents }
                };
                callback(Response.Success(L.T("Resource content retrieved.", "已获取资源内容。"), result));
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[UniMcp] get_resource failed: {ex.Message}");
                callback(Response.Error(ex.Message));
            }
        }
    }
}

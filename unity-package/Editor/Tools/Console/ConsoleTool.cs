// Real single-class tool module.
// One domain = one class = one script. Handlers are discovered from
// [ToolModule] / [ToolAction] / [ToolParam] by ToolCatalog reflection.
//
// console_read (action=get|get_full|clear) and console_write
// (action=error|warning|log|assert|exception) share the same "action"
// discriminator, so they are genuinely merged into this one class with a
// unified Keys set and a unified state tree. No nested implementation types.
//
using System.Collections.Generic;
using System;
using UniMcp.Models;
using UniMcp;
using UnityEngine;

namespace UniMcp.Tools
{
    [ToolModule("console", GroupEn = "Development Tools", GroupZh = "开发工具", LegacyName = "console")]
    [ToolName("console", "Development Tools", "开发工具")]
    public sealed class ConsoleTool : ToolModuleBase
    {
        public override string Description => L.T(
            "Console tool: read/clear Unity Editor console log entries and write log messages to the console",
            "控制台工具：读取/清除 Unity 编辑器控制台日志条目，并向控制台写入日志消息");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                // Action type - enumeration covering read (get/get_full/clear) and write (error/warning/log/assert/exception)
                new MethodStr("action", L.T("Action type", "操作类型"), false)
                    .SetEnumValues("get", "get_full", "clear", "error", "warning", "log", "assert", "exception"),

                // --- Read keys ---
                // Message type list
                new MethodStr("types", L.T("Message type list, defaults to all types", "消息类型列表，默认为所有类型"))
                    .AddExample("error,warning"),

                // Maximum number of messages to return
                new MethodInt("count", L.T("Maximum number of messages to return, returns all if not set", "返回的最大消息数量，未设置则返回全部"))
                    .SetRange(1, 1000),

                // Text filter
                new MethodStr("filterText", L.T("Text filter, filters logs containing specified text", "文本过滤器，过滤包含指定文本的日志"))
                    .AddExample("Error"),

                // Output format
                new MethodStr("format", L.T("Output format, defaults to detailed", "输出格式，默认为详细"))
                    .SetEnumValues("plain", "detailed", "json"),

                // --- Write keys ---
                // Log message content - required for write actions
                new MethodStr("message", L.T("Log message content to write", "要写入的日志消息内容"), false)
                    .AddExample("This is a test message"),

                // Log tag
                new MethodStr("tag", L.T("Log tag for categorization and filtering", "用于分类和过滤的日志标签"))
                    .AddExample("System"),

                // Context object
                new MethodStr("context", L.T("Context object name, used to locate related GameObject in console", "上下文对象名称，用于在控制台中定位相关游戏对象"))
                    .AddExample("Player"),

                // Assert condition
                new MethodStr("condition", L.T("Assert condition expression (only for assert type)", "断言条件表达式（仅用于断言类型）"))
                    .AddExample("value != null")
            };
        }

        [ToolAction("get", Parameters = new string[] { "types", "format" }, Inject = new string[] { "action=get" })]
        public object HandleGetAllWithoutFilter(JsonClass args)
        {
            return GetConsoleEntriesInternal(args, null, null, includeStacktrace: false, "all log entries (no filter, no stacktrace)");
        }

        [ToolAction("get_with_filter", Parameters = new string[] { "types", "filterText", "format" }, Inject = new string[] { "action=get" })]
        public object HandleGetAllWithFilter(JsonClass args)
        {
            string filterText = args["filterText"]?.Value;
            return GetConsoleEntriesInternal(args, null, filterText, includeStacktrace: false, "all log entries (filtered by '" + filterText + "', no stacktrace)");
        }

        [ToolAction("get_partial", Parameters = new string[] { "types", "count", "format" }, Inject = new string[] { "action=get" })]
        public object HandleGetPartialWithoutFilter(JsonClass args)
        {
            int count = args["count"].AsIntDefault(10);
            return GetConsoleEntriesInternal(args, count, null, includeStacktrace: false, $"{count} log entries (no filter, no stacktrace)");
        }

        [ToolAction("get_partial_with_filter", Parameters = new string[] { "types", "filterText", "format" }, Inject = new string[] { "action=get" })]

     /// 处理获取部分控制台日志（有过滤，不包含堆栈跟踪）的操作
        /// </summary>
        public object HandleGetPartialWithFilter(JsonClass args)
        {
            int count = args["count"].AsIntDefault(10);
            string filterText = args["filterText"]?.Value;
            return GetConsoleEntriesInternal(args, count, filterText, false, $"{count} log entries (filtered by '{filterText}', no stacktrace)");
        }

        // ----- Read branches with stacktrace -----

        [ToolAction("get_full", Parameters = new string[] { "types", "format" }, Inject = new string[] { "action=get_full" })]
        /// <summary>
        /// 处理获取全部控制台日志（无过滤，包含堆栈跟踪）的操作
        /// </summary>
        public object HandleGetFullAllWithoutFilter(JsonClass args)
        {
            return GetConsoleEntriesInternal(args, null, null, true, "all log entries (no filter, with stacktrace)");
        }

        [ToolAction("get_full_with_filter", Parameters = new string[] { "types", "filterText", "format" }, Inject = new string[] { "action=get_full" })]
        /// <summary>
        /// 处理获取全部控制台日志（有过滤，包含堆栈跟踪）的操作
        /// </summary>
        public object HandleGetFullAllWithFilter(JsonClass args)
        {
            string filterText = args["filterText"]?.Value;
            return GetConsoleEntriesInternal(args, null, filterText, true, $"all log entries (filtered by '{filterText}', with stacktrace)");
        }

        [ToolAction("get_full_partial", Parameters = new string[] { "types", "count", "format" }, Inject = new string[] { "action=get_full" })]
        /// <summary>
        /// 处理获取部分控制台日志（无过滤，包含堆栈跟踪）的操作
        /// </summary>
        public object HandleGetFullPartialWithoutFilter(JsonClass args)
        {
            int count = args["count"].AsIntDefault(10);
            return GetConsoleEntriesInternal(args, count, null, true, $"{count} log entries (no filter, with stacktrace)");
        }

        [ToolAction("get_full_partial_with_filter", Parameters = new string[] { "types", "filterText", "format" }, Inject = new string[] { "action=get_full" })]
        /// <summary>
        /// 处理获取部分控制台日志（有过滤，包含堆栈跟踪）的操作
        /// </summary>
        public object HandleGetFullPartialWithFilter(JsonClass args)
        {
            int count = args["count"].AsIntDefault(10);
            string filterText = args["filterText"]?.Value;
            return GetConsoleEntriesInternal(args, count, filterText, true, $"{count} log entries (filtered by '{filterText}', with stacktrace)");
        }

        /// <summary>
        /// 统一的控制台日志获取逻辑
        /// </summary>
        private object GetConsoleEntriesInternal(JsonClass args, int? count, string filterText, bool includeStacktrace, string description)
        {
            // 检查 ConsoleController 是否已正确初始化
            if (!ConsoleUtils.AreReflectionMembersInitialized())
            {
                McpLogger.LogError(
                    "[ReadConsole] GetConsoleEntriesInternal called but ConsoleController reflection members are not initialized."
                );
                return Response.Error(
                    "ConsoleController failed to initialize due to reflection errors. Cannot access console logs."
                );
            }

            try
            {
                // 提取参数
                var types = ExtractTypes(args);
                string format = ExtractFormat(args);

                McpLogger.Log($"[ReadConsole] Getting {description}");

                // 使用 ConsoleController 获取控制台条目
                var entries = ConsoleUtils.GetConsoleEntries(types, count, filterText, format, includeStacktrace);
                return Response.Success(
                    $"Retrieved {entries.Count} log entries ({description}).",
                    entries
                );
            }
            catch (Exception e)
            {
                McpLogger.LogError($"[ReadConsole] GetConsoleEntriesInternal failed: {e}");
                return Response.Error($"Internal error processing console entries: {e.Message}");
            }
        }

        [ToolAction("clear", Inject = new string[] { "action=clear" })]
        /// <summary>
        /// 处理清空控制台的操作
        /// </summary>
        public object HandleClearAction(JsonClass args)
        {
            // 检查 ConsoleController 是否已正确初始化
            if (!ConsoleUtils.AreReflectionMembersInitialized())
            {
                McpLogger.LogError(
                    "[ReadConsole] HandleClearAction called but ConsoleController reflection members are not initialized."
                );
                return Response.Error(
                    "ConsoleController failed to initialize due to reflection errors. Cannot clear console logs."
                );
            }

            try
            {
                McpLogger.Log("[ReadConsole] Clearing console logs");
                ConsoleUtils.ClearConsole();
                return Response.Success("Console cleared successfully.");
            }
            catch (Exception e)
            {
                McpLogger.LogError($"[ReadConsole] Clear action failed: {e}");
                return Response.Error($"Internal error processing clear action: {e.Message}");
            }
        }

        /// <summary>
        /// 处理未知操作的回调方法（读取类动作）
        /// </summary>
        private object HandleUnknownAction(JsonClass args)
        {
            string action = args["action"]?.Value;
            if (string.IsNullOrEmpty(action)) action = "null";
            return Response.Error($"Unknown action: '{action}' for console. Valid read actions are 'get', 'get_full', 'clear'; valid write actions are 'error', 'warning', 'log', 'assert', 'exception'.");
        }

        /// <summary>
        /// 提取消息类型参数
        /// </summary>
        private List<string> ExtractTypes(JsonClass args)
        {
            var typesArray = args["types"] as JsonArray;
            List<string> types;

            if (typesArray != null && typesArray.Count > 0)
            {
                types = new List<string>();
                foreach (JsonNode typeNode in typesArray.Childs)
                {
                    types.Add(typeNode.Value.ToLower());
                }
            }
            else
            {
                types = new List<string> { "error", "warning", "log" };
            }

            if (types.Contains("all"))
            {
                types = new List<string> { "error", "warning", "log" };
            }

            return types;
        }

        /// <summary>
        /// 提取格式参数
        /// </summary>
        private string ExtractFormat(JsonClass args)
        {
            string format = args["format"]?.Value;
            if (string.IsNullOrEmpty(format)) format = "detailed";
            return format.ToLower();
        }

        // ================= Write action handlers =================

        [ToolAction("error", Parameters = new string[] { "message", "tag", "context" }, Required = new string[] { "message" }, Inject = new string[] { "action=error" })]
        /// <summary>
        /// 处理写入错误日志的操作
        /// </summary>
        public object HandleWriteError(JsonClass args)
        {
            try
            {
                string message = ExtractMessage(args);
                string tag = ExtractTag(args);
                UnityEngine.Object context = ExtractContext(args);

                string formattedMessage = FormatMessage(message, tag, "ERROR");

                McpLogger.Log($"[ConsoleWrite] Writing error log: {formattedMessage}");

                if (context != null)
                {
                    Debug.LogError(formattedMessage, context);
                }
                else
                {
                    Debug.LogError(formattedMessage);
                }

                return Response.Success($"Error log written successfully: {formattedMessage}");
            }
            catch (Exception e)
            {
                LogError($"[ConsoleWrite] Failed to write error log: {e.Message}");
                return Response.Error($"Failed to write error log: {e.Message}");
            }
        }

        [ToolAction("warning", Parameters = new string[] { "message", "tag", "context" }, Required = new string[] { "message" }, Inject = new string[] { "action=warning" })]
        /// <summary>
        /// 处理写入警告日志的操作
        /// </summary>
        public object HandleWriteWarning(JsonClass args)
        {
            try
            {
                string message = ExtractMessage(args);
                string tag = ExtractTag(args);
                UnityEngine.Object context = ExtractContext(args);

                string formattedMessage = FormatMessage(message, tag, "WARNING");

                McpLogger.Log($"[ConsoleWrite] Writing warning log: {formattedMessage}");

                if (context != null)
                {
                    Debug.LogWarning(formattedMessage, context);
                }
                else
                {
                    Debug.LogWarning(formattedMessage);
                }

                return Response.Success($"Warning log written successfully: {formattedMessage}");
            }
            catch (Exception e)
            {
                LogError($"[ConsoleWrite] Failed to write warning log: {e.Message}");
                return Response.Error($"Failed to write warning log: {e.Message}");
            }
        }

        [ToolAction("log", Parameters = new string[] { "message", "tag", "context" }, Required = new string[] { "message" }, Inject = new string[] { "action=log" })]
        /// <summary>
        /// 处理写入普通日志的操作
        /// </summary>
        public object HandleWriteLog(JsonClass args)
        {
            try
            {
                string message = ExtractMessage(args);
                string tag = ExtractTag(args);
                UnityEngine.Object context = ExtractContext(args);

                string formattedMessage = FormatMessage(message, tag, "LOG");

                McpLogger.Log($"[ConsoleWrite] Writing log: {formattedMessage}");

                if (context != null)
                {
                    Debug.Log(formattedMessage, context);
                }
                else
                {
                    Debug.Log(formattedMessage);
                }

                return Response.Success($"Log written successfully: {formattedMessage}");
            }
            catch (Exception e)
            {
                LogError($"[ConsoleWrite] Failed to write log: {e.Message}");
                return Response.Error($"Failed to write log: {e.Message}");
            }
        }

        [ToolAction("assert", Parameters = new string[] { "message", "tag", "context", "condition" }, Required = new string[] { "message" }, Inject = new string[] { "action=assert" })]
        /// <summary>
        /// 处理写入断言日志的操作
        /// </summary>
        public object HandleWriteAssert(JsonClass args)
        {
            try
            {
                string message = ExtractMessage(args);
                string tag = ExtractTag(args);
                string condition = ExtractCondition(args);
                UnityEngine.Object context = ExtractContext(args);

                string formattedMessage = FormatAssertMessage(message, tag, condition);

                McpLogger.Log($"[ConsoleWrite] Writing assert log: {formattedMessage}");

                if (context != null)
                {
                    Debug.LogAssertion(formattedMessage, context);
                }
                else
                {
                    Debug.LogAssertion(formattedMessage);
                }

                return Response.Success($"Assertion log written successfully: {formattedMessage}");
            }
            catch (Exception e)
            {
                LogError($"[ConsoleWrite] Failed to write assertion log: {e.Message}");
                return Response.Error($"Failed to write assertion log: {e.Message}");
            }
        }

        [ToolAction("exception", Parameters = new string[] { "message", "tag", "context" }, Required = new string[] { "message" }, Inject = new string[] { "action=exception" })]
        /// <summary>
        /// 处理写入异常日志的操作
        /// </summary>
        public object HandleWriteException(JsonClass args)
        {
            try
            {
                string message = ExtractMessage(args);
                string tag = ExtractTag(args);
                UnityEngine.Object context = ExtractContext(args);

                string formattedMessage = FormatMessage(message, tag, "EXCEPTION");

                McpLogger.Log($"[ConsoleWrite] Writing exception log: {formattedMessage}");

                // 创建一个简单的异常对象用于日志
                var exception = new System.Exception(formattedMessage);

                if (context != null)
                {
                    Debug.LogException(exception, context);
                }
                else
                {
                    Debug.LogException(exception);
                }

                return Response.Success($"Exception log written successfully: {formattedMessage}");
            }
            catch (Exception e)
            {
                LogError($"[ConsoleWrite] Failed to write exception log: {e.Message}");
                return Response.Error($"Failed to write exception log: {e.Message}");
            }
        }

        /// <summary>
        /// 提取消息参数
        /// </summary>
        private string ExtractMessage(JsonClass args)
        {
            string message = args["message"]?.Value;
            if (string.IsNullOrEmpty(message))
            {
                throw new ArgumentException("Message parameter is required and cannot be empty.");
            }
            return message;
        }

        /// <summary>
        /// 提取标签参数
        /// </summary>
        private string ExtractTag(JsonClass args)
        {
            return args["tag"]?.Value;
        }

        /// <summary>
        /// 提取条件参数（用于断言）
        /// </summary>
        private string ExtractCondition(JsonClass args)
        {
            return args["condition"]?.Value;
        }

        /// <summary>
        /// 提取上下文对象
        /// </summary>
        private UnityEngine.Object ExtractContext(JsonClass args)
        {
            string contextName = args["context"]?.Value;
            if (string.IsNullOrEmpty(contextName))
            {
                return null;
            }

            try
            {
                // 尝试找到GameObject
                GameObject go = GameObject.Find(contextName);
                if (go != null)
                {
                    return go;
                }

                // 尝试通过Resources加载资源
                UnityEngine.Object asset = Resources.Load(contextName);
                if (asset != null)
                {
                    return asset;
                }

                McpLogger.Log($"[ConsoleWrite] Context object '{contextName}' not found, using null context");
                return null;
            }
            catch (Exception e)
            {
                McpLogger.Log($"[ConsoleWrite] Failed to find context object '{contextName}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 格式化日志消息
        /// </summary>
        private string FormatMessage(string message, string tag, string logType)
        {
            if (!string.IsNullOrEmpty(tag))
            {
                return $"[{tag}][{logType}] {message}";
            }
            return $"[MCP][{logType}] {message}";
        }

        /// <summary>
        /// 格式化断言日志消息
        /// </summary>
        private string FormatAssertMessage(string message, string tag, string condition)
        {
            string baseMessage = FormatMessage(message, tag, "ASSERT");

            if (!string.IsNullOrEmpty(condition))
            {
                return $"{baseMessage} | Condition: {condition}";
            }

            return baseMessage;
        }
    }
}

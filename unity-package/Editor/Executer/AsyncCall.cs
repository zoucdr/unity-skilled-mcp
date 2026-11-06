using System;
using System.Collections.Generic;
using UniMcp.Models;

namespace UniMcp.Executer
{
    /// <summary>
    /// Fire-and-poll facade for long-running MCP tools.
    ///
    /// type=in     start <func> with <args> under <id>
    /// type=out    read the conclusion of <id> (works after a Unity restart)
    /// type=list   list persisted conclusions
    /// type=remove delete one conclusion
    /// type=clear  delete all conclusions
    ///
    /// Every conclusion is written to AsyncCallResultStore (a local .asset in
    /// ProjectSettings) as soon as the invoked tool completes. Reading a result
    /// never deletes it, so the same id stays queryable across restarts.
    ///
    /// <func> is any name from the tool catalog (a "group.action" name discovered
    /// through /tools).
    /// </summary>
    public class AsyncCall : McpTool
    {
        // In-memory view of tasks started in this domain; the persisted store is
        // the authoritative record and also survives restarts.
        private static readonly HashSet<string> running = new HashSet<string>(StringComparer.Ordinal);

        public override string ToolName => "async_call";

        public override void HandleCommand(JsonNode ctx, Action<JsonNode> callback)
        {
            try
            {
                var cmd = ctx as JsonClass ?? new JsonClass();
                var store = AsyncCallResultStore.Instance;
                store.EnsureOrphansMarked();

                string type = cmd["type"]?.Value;
                if (string.IsNullOrWhiteSpace(type)) type = "out";

                string id = cmd["id"]?.Value;

                switch (type)
                {
                    case "list":
                        ListResults(store, callback);
                        return;
                    case "clear":
                        callback(Ok("Cleared " + store.Clear() + " persisted conclusion(s)."));
                        return;
                    case "remove":
                        if (string.IsNullOrWhiteSpace(id)) { Error(callback, "'id' is required for type='remove'."); return; }
                        callback(store.Remove(id)
                            ? Ok("Removed conclusion '" + id + "'.")
                            : Ok("No conclusion found for id '" + id + "'."));
                        return;
                }

                if (string.IsNullOrWhiteSpace(id)) { Error(callback, "'id' is required."); return; }

                if (type == "in") Start(store, id, cmd, callback);
                else if (type == "out") Retrieve(store, id, callback);
                else Error(callback, "Invalid 'type': '" + type + "'. Use in, out, list, remove or clear.");
            }
            catch (Exception e)
            {
                McpLogger.LogError("[AsyncCall] " + e);
                Error(callback, "Internal error: " + e.Message);
            }
        }

        private void Start(AsyncCallResultStore store, string id, JsonClass cmd, Action<JsonNode> callback)
        {
            string func = cmd["func"]?.Value;
            if (string.IsNullOrWhiteSpace(func))
            {
                Error(callback, "'func' is required when type='in'. Discover tool names via /tools.");
                return;
            }

            if (running.Contains(id))
            {
                Error(callback, "Task '" + id + "' is already running in this Unity session.");
                return;
            }

            var argsNode = cmd["args"]?.AsObject;
            var args = argsNode ?? new JsonClass();
            string argsText = argsNode != null ? argsNode.ToString() : "{}";

            var spec = ToolCatalog.Find(func);
            if (spec == null)
            {
                Error(callback, "Unknown func '" + func + "'. Use /tools to discover " +
                                ToolCatalog.Count + " available group.action tools.");
                return;
            }

            store.MarkRunning(id, func, argsText);
            running.Add(id);

            bool finished = false;
            Action<JsonNode> onComplete = result =>
            {
                finished = true;
                running.Remove(id);
                store.Complete(id, result != null ? result.ToString() : string.Empty, ExtractError(result));
            };

            try
            {
                new ToolCall(spec).HandleCommand(args, onComplete);
            }
            catch (Exception e)
            {
                running.Remove(id);
                store.Complete(id, string.Empty, e.Message);
                Error(callback, "Failed to start '" + func + "': " + e.Message);
                return;
            }

            callback(Ok(finished
                ? "Task '" + id + "' finished; conclusion persisted. Read it with type='out'."
                : "Task '" + id + "' started; poll with type='out'. The conclusion is persisted on completion.",
                new JsonClass
                {
                    { "id", new JsonData(id) },
                    { "func", new JsonData(func) },
                    { "status", new JsonData(finished ? "completed" : "running") },
                    { "persisted", new JsonData(true) }
                }));
        }

        private static void Retrieve(AsyncCallResultStore store, string id, Action<JsonNode> callback)
        {
            var entry = store.Get(id);
            if (entry == null)
            {
                Error(callback, "No conclusion for id '" + id + "'. Use type='list' to see persisted ids.");
                return;
            }

            var payload = new JsonClass
            {
                { "success", new JsonData(true) },
                { "id", new JsonData(entry.id ?? string.Empty) },
                { "func", new JsonData(entry.func ?? string.Empty) },
                { "status", new JsonData(entry.status ?? string.Empty) },
                { "persisted", new JsonData(true) },
                { "started_at", new JsonData(entry.startedAt ?? string.Empty) },
                { "completed_at", new JsonData(entry.completedAt ?? string.Empty) }
            };

            if (!string.IsNullOrEmpty(entry.error)) payload.Add("error", new JsonData(entry.error));

            if (entry.status == "running")
                payload.Add("message", new JsonData("Task is still in progress; poll again."));
            else if (entry.status == "interrupted")
                payload.Add("message", new JsonData("Task was interrupted by a Unity reload/restart; re-run it with type='in'."));
            else if (!string.IsNullOrEmpty(entry.resultJson))
            {
                try { payload.Add("result", Json.Parse(entry.resultJson)); }
                catch { payload.Add("result", new JsonData(entry.resultJson)); }
            }
            else
                payload.Add("message", new JsonData("Task completed without a return value."));

            callback(payload);
        }

        private static void ListResults(AsyncCallResultStore store, Action<JsonNode> callback)
        {
            var items = new JsonArray();
            foreach (var entry in store.All())
            {
                items.Add(new JsonClass
                {
                    { "id", new JsonData(entry.id ?? string.Empty) },
                    { "func", new JsonData(entry.func ?? string.Empty) },
                    { "status", new JsonData(entry.status ?? string.Empty) },
                    { "started_at", new JsonData(entry.startedAt ?? string.Empty) },
                    { "completed_at", new JsonData(entry.completedAt ?? string.Empty) }
                });
            }
            callback(new JsonClass
            {
                { "success", new JsonData(true) },
                { "count", new JsonData(items.Count) },
                { "items", items }
            });
        }

        private static string ExtractError(JsonNode result)
        {
            var obj = result as JsonClass;
            if (obj == null) return string.Empty;
            var success = obj["success"];
            if (success != null && string.Equals(success.Value, "false", StringComparison.OrdinalIgnoreCase))
                return obj["error"]?.Value ?? string.Empty;
            return string.Empty;
        }

        private static JsonClass Ok(string message, JsonNode data = null)
        {
            return Response.Success(message, data);
        }

        private static void Error(Action<JsonNode> callback, string message)
        {
            callback(Response.Error(message));
        }
    }
}

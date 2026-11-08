using System;
using System.Collections.Generic;
using UniMcp.Executer;
using UniMcp.Models;
using UnityEngine;

namespace UniMcp
{
    /// <summary>
    /// Executes one concrete tool. A fresh instance is created per call, so there is
    /// no shared mutable dispatcher state and concurrent calls cannot interfere.
    /// </summary>
    public sealed class ToolCall : McpTool
    {
        private readonly ToolSpec _spec;

        public ToolCall(ToolSpec spec) { _spec = spec; }

        public override string ToolName => _spec.Name;

        public override void HandleCommand(JsonNode ctx, Action<JsonNode> callback)
        {
            if (_spec.Owner is UniMcp.Tools.IStrictToolModule)
            {
                try
                {
                    if (!(ctx is JsonClass input)) throw new ArgumentException("参数必须是JSON对象");
                    UniMcp.Tools.ExtensionSafety.ValidateArguments(_spec, input);
                }
                catch (ArgumentException e)
                {
                    callback(Response.Error(e.Message));
                    return;
                }
            }
            var args = ctx as JsonClass ?? new JsonClass();
            foreach (var pair in _spec.Injections)
            {
                if (!args.ContainsKey(pair.Key))
                    args.Add(pair.Key, new JsonData(pair.Value == null ? string.Empty : pair.Value.ToString()));
            }

            var state = new ToolContext(args, new Dictionary<string, object>());
            state.RegistComplete(callback);

            try
            {
                // A targeted tool resolves its Unity object once, before the handler
                // runs. The resolved object is stored under "_resolved_targets".
                if (_spec.NeedsTarget && _spec.Owner is ITargetedTool targeted)
                {
                    object target = targeted.ResolveTarget(state);
                    if (target == null || target is JsonClass)
                    {
                        state.Complete(Json.FromObject(target ?? Response.Error("Target could not be resolved.")));
                        return;
                    }
                    state.SetObjectReference("_resolved_targets", target);
                }

                if (_spec.Background)
                {
                    RunInBackground(args, state);
                    return;
                }

                object result = _spec.JsonHandler != null
                    ? _spec.JsonHandler(args)
                    : (_spec.ContextHandler != null ? _spec.ContextHandler(state) : null);

                if (result != null && !ReferenceEquals(result, state))
                    state.Complete(Json.FromObject(result));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                state.Complete(Response.Error("Tool '" + _spec.Name + "' failed: " + e.Message));
            }
        }

        /// <summary>
        /// Execute a CPU-bound, Unity-API-free handler on the thread pool, then complete
        /// the call on the editor main thread. Nothing is serialised on the worker.
        /// </summary>
        private void RunInBackground(JsonClass args, ToolContext state)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                object result = null;
                string errorMessage = null;
                try
                {
                    result = _spec.JsonHandler != null
                        ? _spec.JsonHandler(args)
                        : (_spec.ContextHandler != null ? _spec.ContextHandler(state) : null);
                }
                catch (Exception e)
                {
                    errorMessage = e.Message;
                    Debug.LogException(e);
                }

                CoroutineRunner.Enqueue(() =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(errorMessage))
                        {
                            state.Complete(Response.Error("Tool '" + _spec.Name + "' failed: " + errorMessage));
                            return;
                        }
                        // A handler that returned its own context owns a coroutine (AsyncReturn);
                        // Background and AsyncReturn are mutually exclusive, so this is defensive only.
                        if (result == null || ReferenceEquals(result, state)) return;
                        state.Complete(Json.FromObject(result));
                    }
                    catch (Exception e)
                    {
                        // Never leave the request pending: ProcessQueue swallows exceptions,
                        // which would otherwise hang the MCP call forever.
                        Debug.LogException(e);
                        state.Complete(Response.Error("Tool '" + _spec.Name + "' result marshal failed: " + e.Message));
                    }
                });
            });
        }
    }
}

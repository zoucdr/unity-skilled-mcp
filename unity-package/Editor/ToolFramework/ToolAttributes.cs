using System;
using System.Collections.Generic;

namespace UniMcp
{
    /// <summary>
    /// Marks one tool module (a domain such as console, animator or hierarchy).
    /// The catalog discovers modules by this attribute and scans the module type
    /// plus its nested implementation types for [ToolAction] methods.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ToolModuleAttribute : Attribute
    {
        public string Prefix { get; private set; }
        public string GroupEn { get; set; }
        public string GroupZh { get; set; }

        /// <summary>
        /// Extra capability tags shared by every action in this module.
        /// Merged with the default group / prefix tags, never replaces them.
        /// Example: [ToolModule("texgen", GroupEn = "Assets", Tags = new[] { "procedural" })]
        /// </summary>
        public string[] Tags { get; set; }

        public ToolModuleAttribute(string prefix)
        {
            Prefix = (prefix ?? string.Empty).Trim();
            GroupEn = "Uncategorized";
            GroupZh = "未分组";
        }
    }

    /// <summary>
    /// Declares one MCP tool on a handler method. Multiple actions may share a
    /// method, therefore AllowMultiple is enabled. The framework binds the method
    /// by reflection; no separate declaration file is needed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class ToolActionAttribute : Attribute
    {
        public string Action { get; private set; }
        public string DescriptionEn { get; set; }
        public string DescriptionZh { get; set; }

        /// <summary>
        /// Argument names for this action, resolved against the owner's Keys.
        /// Kept on the action (not on the method) so one handler can serve
        /// several actions with different schemas.
        /// </summary>
        public string[] Parameters { get; set; }

        /// <summary>Explicit required parameter names. When empty, MethodKey.Optional decides.</summary>
        public string[] Required { get; set; }

        /// <summary>Legacy field injections written as "key=value".</summary>
        public string[] Inject { get; set; }

        /// <summary>
        /// Extra capability tags for this single action only, on top of the module tags.
        /// Example: [ToolAction("render", Tags = new[] { "screen-capture" })]
        /// </summary>
        public string[] Tags { get; set; }

        /// <summary>
        /// Run this action's handler on a thread-pool worker instead of the editor main thread.
        /// Only valid for handlers that never touch Unity APIs directly; use
        /// CoroutineRunner.Enqueue to marshal any Unity work back to the main thread.
        /// Mutually exclusive with ToolContext.AsyncReturn.
        /// </summary>
        public bool Background { get; set; }

        /// <summary>
        /// True when the shortened parameter list was used. In that form each entry
        /// is "name" (optional) or "name=require" (required), and Required is
        /// authoritative even when it ends up empty.
        /// </summary>
        public bool UsesShortParameterList { get; private set; }

        public ToolActionAttribute(string action)
        {
            Action = (action ?? string.Empty).Trim();
        }

        /// <summary>
        /// Shortened form: [ToolAction("action", "name", "other=require")].
        /// A plain name is optional; the "=require" suffix marks it required.
        /// </summary>
        public ToolActionAttribute(string action, params string[] parameters) : this(action)
        {
            if (parameters == null || parameters.Length == 0) return;

            var names = new List<string>();
            var required = new List<string>();
            foreach (var entry in parameters)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                string token = entry.Trim();
                int split = token.IndexOf('=');
                if (split <= 0)
                {
                    names.Add(token);
                    continue;
                }

                string name = token.Substring(0, split).Trim();
                string flag = token.Substring(split + 1).Trim();
                if (name.Length == 0) continue;

                names.Add(name);
                if (IsRequiredFlag(flag)) required.Add(name);
            }

            Parameters = names.ToArray();
            Required = required.ToArray();
            UsesShortParameterList = true;
        }

        private static bool IsRequiredFlag(string flag)
        {
            return string.Equals(flag, "require", StringComparison.OrdinalIgnoreCase)
                || string.Equals(flag, "required", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Declares one argument of a tool. May be placed on the handler method (for
    /// handlers that take JsonClass/ToolContext) or directly on a strongly
    /// typed parameter, in which case reflection binds and converts the value.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter,
        AllowMultiple = true, Inherited = false)]
    public sealed class ToolParamAttribute : Attribute
    {
        public string Name { get; private set; }
        public bool Required { get; set; }

        public ToolParamAttribute(string name)
        {
            Name = (name ?? string.Empty).Trim();
        }
    }
}

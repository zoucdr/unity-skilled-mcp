using System;
using System.Collections.Generic;

namespace UniMcp
{
    /// <summary>
    /// One concrete MCP tool. Unlike the old state-tree model, a spec has a fixed
    /// name, its own parameter subset, its own description and a directly bound
    /// handler. No runtime routing and no "action" discriminator are involved.
    /// </summary>
    public sealed class ToolSpec
    {
        public string Name;
        public string Action;
        public string Prefix;
        public string SourceClass;
        public string DescriptionEn;
        public string DescriptionZh;
        public string GroupEn;
        public string GroupZh;
        public string[] Tags = new string[0];
        public MethodKey[] Parameters = new MethodKey[0];
        public string[] Required = new string[0];
        public KeyValuePair<string, object>[] Injections = new KeyValuePair<string, object>[0];
        public Func<JsonClass, object> JsonHandler;
        public Func<ToolContext, object> ContextHandler;
        /// <summary>The instance that owns the reflected handler (or null for a static handler).</summary>
        public object Owner;
        /// <summary>True when the owner is an ITargetedTool that must resolve a target first.</summary>
        public bool NeedsTarget;

        public string Description => L.T(DescriptionEn, DescriptionZh);
        public string GroupName => L.T(GroupEn, GroupZh);
    }
}

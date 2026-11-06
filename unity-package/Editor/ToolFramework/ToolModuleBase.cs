using System;
using UnityEngine;

namespace UniMcp
{
    /// <summary>
    /// Base class for every tool module.
    ///
    /// A module is a single real top-level class marked with [ToolModule]; its
    /// handlers are marked with [ToolAction] and are bound directly by
    /// ToolCatalog. There is no state tree and no runtime routing: each tool name
    /// maps to exactly one method.
    /// </summary>
    public abstract class ToolModuleBase
    {
        private MethodKey[] _keys;

        /// <summary>Localized module description.</summary>
        public abstract string Description { get; }

        /// <summary>
        /// Parameter keys supported by this module. Built once from CreateKeys and
        /// used to resolve each tool's own argument subset and inputSchema.
        /// </summary>
        public MethodKey[] Keys
        {
            get
            {
                if (_keys == null)
                {
                    _keys = CreateKeys();
                }
                return _keys;
            }
        }

        /// <summary>Declare the module's parameter keys.</summary>
        protected abstract MethodKey[] CreateKeys();

        public virtual void LogWarning(string message)
        {
            Debug.LogWarning(message);
        }

        public virtual void LogError(string message)
        {
            Debug.LogError(message);
        }

        public virtual void LogException(Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    /// <summary>
    /// Implemented by modules whose handlers operate on a resolved Unity object.
    /// ToolCall resolves the target once before invoking the handler and stores
    /// it under "_resolved_targets" in the context.
    /// </summary>
    public interface ITargetedTool
    {
        /// <summary>
        /// Resolve the target object(s) from the context. Returns the resolved
        /// object, or a Response error describing why resolution failed.
        /// </summary>
        object ResolveTarget(ToolContext ctx);
    }
}

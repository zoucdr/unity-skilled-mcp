using System;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using UniMcp.Models;

namespace UniMcp
{
    /// <summary>Which pool of objects a target is resolved from.</summary>
    public enum TargetScope
    {
        /// <summary>Asset first, then the active scene hierarchy.</summary>
        Any,
        /// <summary>Active scene hierarchy only.</summary>
        Hierarchy,
        /// <summary>Project assets only.</summary>
        Project
    }

    /// <summary>
    /// Resolves a Unity object from the "instance_id" / "path" arguments.
    /// Replaces the former per-scope selector classes; one method covers every
    /// scope, so there is no state tree and no per-type selector instance.
    /// </summary>
    public static class ToolTargetResolver
    {
        /// <summary>
        /// Resolve a single object of type T. Returns the object, or a Response
        /// error (as JsonClass) when it cannot be found.
        /// </summary>
        public static object Resolve<T>(ToolContext ctx, TargetScope scope = TargetScope.Any)
            where T : UnityEngine.Object
        {
            if (ctx == null)
                return Response.Error("No execution context.");

            ctx.TryGetValue("instance_id", out object idObj);
            ctx.TryGetValue("path", out object pathObj);

            bool hasId = idObj != null && !string.IsNullOrEmpty(idObj.ToString());
            bool hasPath = pathObj != null && !string.IsNullOrEmpty(pathObj.ToString());

            if (!hasId && !hasPath)
                return Response.Error("Either 'instance_id' or 'path' must be provided.");

            // instance_id takes precedence.
            if (hasId)
            {
                if (!int.TryParse(idObj.ToString(), out int instanceId))
                    return Response.Error($"Invalid instance_id: '{idObj}'. It must be an integer.");

                var byId = EditorUtility.InstanceIDToObject(instanceId);
                if (byId == null)
                    return Response.Error($"No object found for instance_id '{instanceId}'.");

                if (!InScope(byId, scope))
                    return Response.Error(ScopeError(instanceId, scope));

                if (byId is T typedId) return typedId;
                return Response.Error($"Object '{instanceId}' is a {byId.GetType().Name}, not {typeof(T).Name}.");
            }

            string path = pathObj.ToString();

            if (scope != TargetScope.Hierarchy)
            {
                T asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null) return asset;
            }

            if (scope != TargetScope.Project)
            {
                T hier = FindInHierarchy<T>(path);
                if (hier != null) return hier;
            }

            return Response.Error($"No {typeof(T).Name} found at path '{path}'.");
        }

        /// <summary>Resolve a single GameObject/Component of type T, or null.</summary>
        public static T ResolveOrNull<T>(ToolContext ctx, TargetScope scope = TargetScope.Any)
            where T : UnityEngine.Object
        {
            object result = Resolve<T>(ctx, scope);
            return result as T;
        }

        private static bool InScope(UnityEngine.Object obj, TargetScope scope)
        {
            if (obj == null) return false;

            switch (scope)
            {
                case TargetScope.Hierarchy:
                    return IsInActiveScene(obj);
                case TargetScope.Project:
                    return AssetDatabase.Contains(obj);
                default:
                    return true;
            }
        }

        private static string ScopeError(int instanceId, TargetScope scope)
        {
            return scope == TargetScope.Hierarchy
                ? $"Object '{instanceId}' is not in the active scene hierarchy."
                : $"Object '{instanceId}' is not a project asset.";
        }

        private static bool IsInActiveScene(UnityEngine.Object obj)
        {
            var active = SceneManager.GetActiveScene();

            if (obj is GameObject go) return go.scene == active;
            if (obj is Component c) return c.gameObject.scene == active;
            return false;
        }

        private static T FindInHierarchy<T>(string path) where T : UnityEngine.Object
        {
            GameObject go = GameObject.Find(path);
            if (go == null) return null;

            if (go is T direct) return direct;
            if (typeof(Component).IsAssignableFrom(typeof(T))) return go.GetComponent<T>();
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UniMcp
{
    /// <summary>
    /// The single source of truth for MCP tools.
    ///
    /// Tools are discovered purely by reflection: [ToolModule] marks a domain
    /// class, [ToolAction] marks a handler method and [ToolParam] declares that
    /// tool's arguments. The catalog binds the method to a delegate, resolves
    /// each argument's MethodKey from the owner's Keys array and builds an exact
    /// inputSchema. No generated declaration files and no state tree are used.
    /// </summary>
    public static class ToolCatalog
    {
        private static readonly object Gate = new object();
        private static List<ToolSpec> _all = new List<ToolSpec>();
        private static Dictionary<string, ToolSpec> _byName =
            new Dictionary<string, ToolSpec>(StringComparer.Ordinal);

        private const BindingFlags MethodFlags = BindingFlags.Public | BindingFlags.NonPublic |
                                                 BindingFlags.Instance | BindingFlags.Static |
                                                 BindingFlags.DeclaredOnly;

        public static IReadOnlyList<ToolSpec> All => _all;
        public static int Count => _all.Count;

        public static List<string> Names => _all.Select(s => s.Name).ToList();

        public static ToolSpec Find(string name)
        {
            ToolSpec spec;
            return _byName.TryGetValue(name ?? string.Empty, out spec) ? spec : null;
        }

        public static ToolInfo CreateInfo(ToolSpec spec, bool withDescriptions = true)
        {
            return new ToolInfo
            {
                name = spec.Name,
                description = spec.Description,
                inputSchema = ToolSchema.Build(spec, withDescriptions),
            };
        }

        /// <summary>Re-scan module classes. Called on service start and language change.</summary>
        public static List<ToolSpec> Reload()
        {
            lock (Gate)
            {
                var collected = new List<ToolSpec>();
                var index = new Dictionary<string, ToolSpec>(StringComparer.Ordinal);
                int modules = 0;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }

                    foreach (var type in types)
                    {
                        if (type == null || type.IsInterface || type.IsGenericTypeDefinition) continue;
                        var module = type.GetCustomAttributes(typeof(ToolModuleAttribute), false)
                                         .Cast<ToolModuleAttribute>().FirstOrDefault();
                        if (module == null || string.IsNullOrWhiteSpace(module.Prefix)) continue;
                        modules++;

                        // One real top-level class owns its handlers directly. No nested
                        // implementation types, no state tree.
                        if (type.IsAbstract || type.IsInterface) continue;
                        var ownerType = type;
                        {
                            var owners = new Dictionary<Type, object>();
                            foreach (var method in ownerType.GetMethods(MethodFlags))
                            {
                                var actions = method.GetCustomAttributes(typeof(ToolActionAttribute), false)
                                                    .Cast<ToolActionAttribute>();
                                foreach (var action in actions)
                                {
                                    try
                                    {
                                        object owner = null;
                                        if (!method.IsStatic)
                                        {
                                            if (!owners.TryGetValue(ownerType, out owner))
                                            {
                                                owner = Activator.CreateInstance(ownerType, true);
                                                owners[ownerType] = owner;
                                            }
                                        }

                                        var spec = BuildSpec(module, type, ownerType, owner, method, action);
                                        if (string.IsNullOrWhiteSpace(spec.Name) || index.ContainsKey(spec.Name))
                                        {
                                            Debug.LogWarning("[ToolCatalog] duplicate or invalid tool name skipped: " + spec.Name);
                                            continue;
                                        }
                                        index[spec.Name] = spec;
                                        collected.Add(spec);
                                    }
                                    catch (Exception e)
                                    {
                                        Debug.LogError("[ToolCatalog] failed to bind " + ownerType.FullName + "." +
                                                       method.Name + ": " + e.Message);
                                    }
                                }
                            }
                        }
                    }
                }

                collected.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                _all = collected;
                _byName = index;
                Debug.Log("[ToolCatalog] registered " + collected.Count + " tools from " + modules + " modules.");
                return collected;
            }
        }

        private static ToolSpec BuildSpec(ToolModuleAttribute module, Type moduleType, Type ownerType,
            object owner, MethodInfo method, ToolActionAttribute action)
        {
            var keyIndex = new Dictionary<string, MethodKey>(StringComparer.Ordinal);
            var keysProp = ownerType.GetProperty("Keys", BindingFlags.Public | BindingFlags.Instance |
                                                         BindingFlags.FlattenHierarchy);
            var keys = (owner != null && keysProp != null)
                ? keysProp.GetValue(owner, null) as MethodKey[] : null;
            foreach (var key in keys ?? new MethodKey[0])
            {
                if (key != null && !string.IsNullOrEmpty(key.Key) && !keyIndex.ContainsKey(key.Key))
                    keyIndex[key.Key] = key;
            }

            // [ToolAction].Parameters is authoritative; [ToolParam] on the method
            // or on strongly typed parameters is the declarative alternative.
            var names = (action.Parameters != null)
                ? action.Parameters.ToList()
                : method.GetCustomAttributes(typeof(ToolParamAttribute), false)
                        .Cast<ToolParamAttribute>().Select(p => p.Name).ToList();
            if (action.Parameters == null)
            {
                foreach (var p in method.GetParameters())
                {
                    var pa = p.GetCustomAttributes(typeof(ToolParamAttribute), false)
                              .Cast<ToolParamAttribute>().FirstOrDefault();
                    if (pa != null && !names.Contains(pa.Name)) names.Add(pa.Name);
                }
            }

            var resolved = new List<MethodKey>();
            foreach (var name in names)
            {
                MethodKey key;
                if (keyIndex.TryGetValue(name, out key)) resolved.Add(key);
            }
            // In the shortened form ("name" / "name=require") the list itself decides
            // optionality, including "nothing is required"; otherwise an explicit
            // Required array wins and finally MethodKey.Optional decides.
            string[] required;
            if (action.UsesShortParameterList || (action.Required != null && action.Required.Length > 0))
                required = (action.Required ?? new string[0])
                    .Where(r => resolved.Any(k => k.Key == r)).ToArray();
            else
                required = resolved.Where(k => !k.Optional).Select(k => k.Key).ToArray();

            var injections = new List<KeyValuePair<string, object>>();
            foreach (var entry in action.Inject ?? new string[0])
            {
                int split = entry == null ? -1 : entry.IndexOf('=');
                if (split <= 0) continue;
                injections.Add(new KeyValuePair<string, object>(entry.Substring(0, split), entry.Substring(split + 1)));
            }

            var ps = method.GetParameters();
            Func<JsonClass, object> jsonHandler = null;
            Func<ToolContext, object> contextHandler = null;
            if (ps.Length == 1 && ps[0].ParameterType == typeof(ToolContext))
            {
                contextHandler = c => Invoke(method, owner, new object[] { c });
            }
            else if (ps.Length == 1 && ps[0].ParameterType == typeof(JsonClass))
            {
                jsonHandler = a => Invoke(method, owner, new object[] { a });
            }
            else
            {
                jsonHandler = a => Invoke(method, owner, ConvertArguments(method, a));
            }

            string paramText = resolved.Count == 0 ? "none" : string.Join(", ", resolved.Select(k => k.Key).ToArray());
            string descEn = action.DescriptionEn ?? (Title(action.Action) + " (" + module.GroupEn + "). Parameters: " + paramText + ".");
            string descZh = action.DescriptionZh ?? (module.GroupZh + "：" + action.Action + "。参数：" + paramText + "。");

            return new ToolSpec
            {
                Name = module.Prefix + "." + action.Action,
                Action = action.Action,
                Prefix = module.Prefix,
                LegacyName = module.LegacyName ?? string.Empty,
                SourceClass = moduleType.Name,
                DescriptionEn = descEn,
                DescriptionZh = descZh,
                GroupEn = module.GroupEn,
                GroupZh = module.GroupZh,
                Tags = new[] { Slug(module.GroupEn), Slug(module.Prefix) }
                    .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray(),
                Parameters = resolved.ToArray(),
                Required = required,
                Injections = injections.ToArray(),
                JsonHandler = jsonHandler,
                ContextHandler = contextHandler,
                Owner = owner,
                NeedsTarget = owner is ITargetedTool,
            };
        }

        private static object Invoke(MethodInfo method, object owner, object[] argv)
        {
            try { return method.Invoke(owner, argv); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        /// <summary>Binds strongly typed handler arguments from the JSON object by reflection.</summary>
        private static object[] ConvertArguments(MethodInfo method, JsonClass args)
        {
            var ps = method.GetParameters();
            var values = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                var attr = p.GetCustomAttributes(typeof(ToolParamAttribute), false)
                            .Cast<ToolParamAttribute>().FirstOrDefault();
                string name = attr != null ? attr.Name : p.Name;
                var node = (args != null && !string.IsNullOrEmpty(name) && args.ContainsKey(name))
                    ? args[name] : null;
                values[i] = ConvertValue(node, p.ParameterType, args);
            }
            return values;
        }

        private static object ConvertValue(JsonNode node, Type type, JsonClass args)
        {
            if (type == typeof(JsonClass)) return args;
            if (type == typeof(ToolContext)) return null;
            if (node == null) return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(JsonNode) || type == typeof(object)) return node;
            if (type == typeof(string)) return node.Value;
            if (type == typeof(bool)) return node.AsBoolDefault(false);
            if (type == typeof(int)) return node.AsIntDefault(0);
            if (type == typeof(long)) return (long)node.AsIntDefault(0);
            if (type == typeof(float)) return node.AsFloatDefault(0f);
            if (type == typeof(double)) return (double)node.AsFloatDefault(0f);
            return node.Value;
        }

        private static string Title(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Action";
            return char.ToUpperInvariant(value[0]) + value.Substring(1).Replace('_', ' ');
        }

        private static string Slug(string value)
        {
            var slug = Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return string.IsNullOrEmpty(slug) ? "uncategorized" : slug;
        }
    }
}

using System;
using System.Linq;
using System.Reflection;
using UniMcp.Tools;

namespace UniMcp
{
    // 只验证声明和输入边界；不将这些测试当作Unity场景功能测试。
    public static class WorkflowContractTests
    {
        public static string Run()
        {
            Type[] types = { typeof(SceneWorkflowTool), typeof(AssetWorkflowTool), typeof(AnimationWorkflowTool), typeof(UIWorkflowTool),
                typeof(EditorWorkflowTool), typeof(RuntimeWorkflowTool), typeof(ProjectWorkflowTool), typeof(SystemWorkflowTool),
                typeof(PhysicsWorkflowTool), typeof(RenderingWorkflowTool), typeof(AudioWorkflowTool), typeof(OptionalRuntimeTool) };
            string[] prefixes = { "scene", "asset", "animation", "ui", "editor", "runtime", "project", "system", "physics", "rendering", "audio", "optional_runtime" };
            int[] counts = { 9, 7, 8, 4, 7, 6, 6, 7, 9, 10, 5, 4 };
            int checks = 0, total = 0, skipped = 0;
            var groups = new System.Collections.Generic.HashSet<string>();
            var names = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < types.Length; i++)
            {
                var owner = (ToolModuleBase)Activator.CreateInstance(types[i]);
                Check(owner is IStrictToolModule, "缺少严格校验标记", ref checks);
                var module = (ToolModuleAttribute)types[i].GetCustomAttributes(typeof(ToolModuleAttribute), false).Single(); groups.Add(module.GroupEn);
                Check(module.Prefix == prefixes[i], "模块前缀不符: " + types[i].Name, ref checks);
                int actions = 0;
                foreach (var method in types[i].GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                foreach (ToolActionAttribute action in method.GetCustomAttributes(typeof(ToolActionAttribute), false))
                {
                    actions++; total++;
                    Check(names.Add(module.Prefix + "." + action.Action), "动作名称重复", ref checks);
                    var parameters = action.Parameters ?? new string[0];
                    Check(parameters.All(p => owner.Keys.Any(k => k.Key == p)), "参数未声明", ref checks);
                    var spec = BuildSpec(types[i], owner, method, action);
                    Check(spec.Name == prefixes[i] + "." + action.Action && ReferenceEquals(spec.Owner, owner), "目录绑定丢失名称或实际模块", ref checks);
                    var schema = ToolSchema.Build(spec, false);
                    Check(schema["additionalProperties"].ToString() == "false", "schema未拒绝未知字段", ref checks);
                    Reject(() => ExtensionSafety.ValidateArguments(spec, (JsonClass)Json.Parse("{\"unexpected\":true}")), ref checks);
                    foreach (var required in spec.Required)
                        Check(parameters.Contains(required), "必填参数不在schema中", ref checks);
                    if (spec.Required.Length > 0) Reject(() => ExtensionSafety.ValidateArguments(spec, new JsonClass()), ref checks);
                    else ExtensionSafety.ValidateArguments(spec, new JsonClass());
                }
                Check(actions == counts[i], "动作数量变化: " + types[i].Name, ref checks);
            }
            Check(total == 82 && groups.Count == 10, "总动作/分组数量不符", ref checks);
            CheckRegistrations(ref checks);
            CheckSharedPrefixes(ref checks, ref skipped);
            var numeric = new ToolSpec { Parameters = new[] { new MethodInt("id", "ID") }, Required = new[] { "id" } };
            ExtensionSafety.ValidateArguments(numeric, (JsonClass)Json.Parse("{\"id\":1}")); checks++;
            Reject(() => ExtensionSafety.ValidateArguments(numeric, (JsonClass)Json.Parse("{\"id\":\"1\"}")), ref checks);
            Reject(() => ExtensionSafety.ValidateArguments(numeric, (JsonClass)Json.Parse("{\"id\":1.5}")), ref checks);
            Reject(() => ExtensionSafety.Flag((JsonClass)Json.Parse("{\"v\":\"true\"}"), "v"), ref checks);
            Reject(() => ExtensionSafety.Number((JsonClass)Json.Parse("{\"v\":-1}"), "v", 0, 0, 1), ref checks);
            foreach (string path in new[] { "../secret.json", "Assets/../secret.json", "C:/secret.json", "Assets/a:stream.json", "Assets//a.json" })
            {
                if (UnityRuntimeAvailable()) Reject(() => ExtensionSafety.AssetPath(path, ".json", false), ref checks);
                else skipped++;
            }
            JsonNode response = null;
            numeric.Owner = new SceneWorkflowTool(); numeric.JsonHandler = a => throw new Exception("校验失败时不应执行handler");
            new ToolCall(numeric).HandleCommand(new JsonClass(), r => response = r);
            Check(response != null && response["success"].Value == "false", "实际调用入口未拒绝缺失参数", ref checks);
            response = null;
            new ToolCall(numeric).HandleCommand(new JsonArray(), r => response = r);
            Check(response != null && response["success"].Value == "false", "实际调用入口未拒绝数组", ref checks);
            return "PASS " + checks + "项契约断言；跳过 " + skipped + " 项需Unity运行时的路径或旧模块绑定断言；12模块、82动作、10核心组；不包含Unity原生功能执行";
        }

        private const BindingFlags ActionFlags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static ToolSpec BuildSpec(Type type, object owner, MethodInfo method, ToolActionAttribute action)
        {
            var module = (ToolModuleAttribute)type.GetCustomAttributes(typeof(ToolModuleAttribute), false).Single();
            return (ToolSpec)typeof(ToolCatalog).GetMethod("BuildSpec", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { module, type, type, owner, method, action });
        }

        private static void CheckRegistrations(ref int checks)
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;
                    var module = (ToolModuleAttribute)type.GetCustomAttributes(typeof(ToolModuleAttribute), false).FirstOrDefault();
                    if (module == null) continue;
                    foreach (var method in type.GetMethods(ActionFlags))
                    foreach (ToolActionAttribute action in method.GetCustomAttributes(typeof(ToolActionAttribute), false))
                        Check(names.Add(module.Prefix + "." + action.Action), "注册名冲突: " + module.Prefix + "." + action.Action, ref checks);
                }
            }
        }

        private static void CheckSharedPrefixes(ref int checks, ref int skipped)
        {
            string[] prefixes = { "scene", "ui", "editor", "project", "audio" };
            Type[] moduleTypes;
            try { moduleTypes = typeof(SceneWorkflowTool).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { moduleTypes = e.Types.Where(t => t != null).ToArray(); }
            foreach (string prefix in prefixes)
            {
                bool strictFound = false, legacyFound = false;
                foreach (var type in moduleTypes)
                {
                    var module = (ToolModuleAttribute)type.GetCustomAttributes(typeof(ToolModuleAttribute), false).FirstOrDefault();
                    if (module == null || module.Prefix != prefix) continue;
                    var owner = Activator.CreateInstance(type, true);
                    bool strict = owner is IStrictToolModule;
                    strictFound |= strict; legacyFound |= !strict;
                    foreach (var method in type.GetMethods(ActionFlags))
                    foreach (ToolActionAttribute action in method.GetCustomAttributes(typeof(ToolActionAttribute), false))
                    {
                        ToolSpec spec;
                        if (!strict && !UnityRuntimeAvailable())
                        {
                            // 旧模块Keys依赖Unity本地设置；独立运行仅验证Owner分派，不伪造原生绑定通过。
                            skipped++;
                            spec = new ToolSpec { Name = prefix + "." + action.Action, Prefix = prefix, Owner = owner,
                                Parameters = new MethodKey[0], Required = new string[0],
                                Injections = new System.Collections.Generic.KeyValuePair<string, object>[0] };
                        }
                        else spec = BuildSpec(type, owner, method, action);
                        Check(ReferenceEquals(spec.Owner, owner), "同前缀丢失实际模块", ref checks);
                        Check(((JsonClass)ToolSchema.Build(spec, false)).ContainsKey("additionalProperties") == strict,
                            "同前缀schema校验串扰: " + spec.Name, ref checks);
                        // 替换执行体，验证真实调用入口，不执行Unity原生读写。
                        bool invoked = false;
                        spec.ContextHandler = null;
                        spec.JsonHandler = a => { invoked = true; return null; };
                        JsonNode response = null;
                        new ToolCall(spec).HandleCommand((JsonClass)Json.Parse("{\"unexpected\":true}"), r => response = r);
                        Check(invoked == !strict, "同前缀参数校验串扰: " + spec.Name, ref checks);
                        if (strict) Check(response != null && response["success"].Value == "false", "严格模块未拒绝未知参数", ref checks);
                    }
                }
                Check(strictFound && legacyFound, "未覆盖新旧模块共享前缀: " + prefix, ref checks);
            }
        }

        private static bool UnityRuntimeAvailable()
        {
            // 独立进程不能调用Unity ECall；由运行器显式声明，避免把环境限制误判为通过。
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UNMCP_CONTRACT_NO_UNITY"));
        }
        private static void Check(bool condition, string message, ref int checks) { if (!condition) throw new Exception(message); checks++; }
        private static void Reject(Action action, ref int checks) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception("预期拒绝非法输入但调用成功"); }
    }
}

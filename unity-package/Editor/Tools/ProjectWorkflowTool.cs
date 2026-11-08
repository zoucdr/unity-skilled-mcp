using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("project", GroupEn = "Project", GroupZh = "项目工程",
        Tags = new[] { "project-settings", "build-pipeline", "asset-database", "file-io" })]
    public sealed class ProjectWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "构建场景、PlayerSettings、质量、宏、asmdef与项目体检";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodArr("scenes", "Assets下场景路径数组"), new MethodBool("dry_run", "只预演，默认true"),
            new MethodStr("company", "公司名"), new MethodStr("product", "产品名"), new MethodStr("version", "版本号"),
            new MethodStr("identifier", "应用标识符"), new MethodStr("target_group", "BuildTargetGroup名称"),
            new MethodInt("quality_index", "质量档索引"), new MethodInt("v_sync", "垂直同步0至4"), new MethodInt("anti_aliasing", "抗锯齿0/2/4/8"),
            new MethodArr("defines", "完整宏列表，最多256项"), new MethodStr("asset_path", "asmdef资源路径") };

        [ToolAction("build_scenes", "scenes", "dry_run")]
        private object Scenes(JsonClass a) => ExtensionSafety.Run(() => {
            bool dry = ExtensionSafety.Flag(a, "dry_run", true); var before = EditorBuildSettings.scenes.Select(s => new { s.path, s.enabled }).ToArray();
            string[] paths = null;
            if (a.ContainsKey("scenes")) {
                paths = ExtensionSafety.Strings(a, "scenes").Select(p => ExtensionSafety.AssetPath(p, ".unity")).ToArray();
                if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new ArgumentException("场景路径重复");
                if (!dry) { RecordSettings("EditorBuildSettings"); EditorBuildSettings.scenes = paths.Select(p => new EditorBuildSettingsScene(p, true)).ToArray(); DirtySettings("EditorBuildSettings"); }
            }
            return ExtensionSafety.Ok(new { before, proposed = paths, dry_run = dry });
        });

        [ToolAction("player_settings", "company", "product", "version", "identifier", "target_group", "dry_run")]
        private object Player(JsonClass a) => ExtensionSafety.Run(() => {
            var group = Group(a); bool dry = ExtensionSafety.Flag(a, "dry_run", true);
            string company = ExtensionSafety.Text(a, "company", PlayerSettings.companyName), product = ExtensionSafety.Text(a, "product", PlayerSettings.productName), version = ExtensionSafety.Text(a, "version", PlayerSettings.bundleVersion);
            string identifier = ExtensionSafety.Text(a, "identifier", PlayerSettings.GetApplicationIdentifier(group));
            if (a.ContainsKey("identifier") && !System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)+$")) throw new ArgumentException("应用标识符格式不合法");
            var before = new { company = PlayerSettings.companyName, product = PlayerSettings.productName, version = PlayerSettings.bundleVersion, identifier = PlayerSettings.GetApplicationIdentifier(group) };
            if (!dry && new[] { "company", "product", "version", "identifier" }.Any(a.ContainsKey)) {
                RecordSettings("ProjectSettings"); PlayerSettings.companyName = company; PlayerSettings.productName = product; PlayerSettings.bundleVersion = version;
                if (a.ContainsKey("identifier")) PlayerSettings.SetApplicationIdentifier(group, identifier);
                DirtySettings("ProjectSettings");
            }
            return ExtensionSafety.Ok(new { before, proposed = new { company, product, version, identifier }, target_group = group.ToString(), dry_run = dry });
        });

        [ToolAction("quality", "quality_index", "v_sync", "anti_aliasing", "dry_run")]
        private object Quality(JsonClass a) => ExtensionSafety.Run(() => {
            int index = ExtensionSafety.Integer(a, "quality_index", QualitySettings.GetQualityLevel(), 0, QualitySettings.names.Length - 1);
            int vsync = ExtensionSafety.Integer(a, "v_sync", QualitySettings.vSyncCount, 0, 4), aa = ExtensionSafety.Integer(a, "anti_aliasing", QualitySettings.antiAliasing, 0, 8);
            if (aa != 0 && aa != 2 && aa != 4 && aa != 8) throw new ArgumentException("anti_aliasing只允许0/2/4/8");
            bool dry = ExtensionSafety.Flag(a, "dry_run", true); var before = new { index = QualitySettings.GetQualityLevel(), QualitySettings.vSyncCount, QualitySettings.antiAliasing };
            if (!dry && new[] { "quality_index", "v_sync", "anti_aliasing" }.Any(a.ContainsKey)) {
                RecordSettings("QualitySettings");
                if (a.ContainsKey("quality_index")) QualitySettings.SetQualityLevel(index, true);
                if (a.ContainsKey("v_sync")) QualitySettings.vSyncCount = vsync;
                if (a.ContainsKey("anti_aliasing")) QualitySettings.antiAliasing = aa;
                DirtySettings("QualitySettings");
            }
            return ExtensionSafety.Ok(new { names = QualitySettings.names, before, proposed = new { index, v_sync = a.ContainsKey("v_sync") ? (int?)vsync : null, anti_aliasing = a.ContainsKey("anti_aliasing") ? (int?)aa : null }, dry_run = dry });
        });

        [ToolAction("defines", "target_group", "defines", "dry_run")]
        private object Defines(JsonClass a) => ExtensionSafety.Run(() => {
            var group = Group(a); string before = PlayerSettings.GetScriptingDefineSymbolsForGroup(group); bool dry = ExtensionSafety.Flag(a, "dry_run", true); string[] values = null;
            if (a.ContainsKey("defines")) {
                var arr = a["defines"] as JsonArray;
                if (arr == null) throw new ArgumentException("defines必须是数组");
                values = arr.Count == 0 ? new string[0] : ExtensionSafety.Strings(a, "defines");
                if (values.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[A-Za-z_][A-Za-z0-9_]*$")) || values.Distinct().Count() != values.Length) throw new ArgumentException("宏必须是合法且不重复的标识符");
                if (!dry) { RecordSettings("ProjectSettings"); PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", values)); DirtySettings("ProjectSettings"); }
            }
            return ExtensionSafety.Ok(new { before, proposed = values, dry_run = dry, triggers_domain_reload = values != null && !dry });
        });

        [ToolAction("asmdef_validate", "asset_path=require")]
        private object Asmdef(JsonClass a) => ExtensionSafety.Run(() => {
            string path = ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "asset_path"), ".asmdef");
            var root = Json.Parse(File.ReadAllText(path)) as JsonClass; if (root == null) throw new ArgumentException("asmdef必须是JSON对象");
            string name = ExtensionSafety.Text(root, "name"); var issues = new System.Collections.Generic.List<string>();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_.-]*$")) issues.Add("程序集名称不合法");
            var refs = root["references"] as JsonArray;
            if (root.ContainsKey("references") && refs == null) issues.Add("references必须是数组");
            var all = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            var names = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in all) {
                try { var json = Json.Parse(File.ReadAllText(file)); string n = json["name"].Value; if (names.ContainsKey(n)) issues.Add("重复程序集名称: " + n); else names[n] = file; } catch (Exception e) { issues.Add(file + ": " + e.Message); }
            }
            if (refs != null) for (int i = 0; i < refs.Count; i++) {
                string r = refs[i].Value;
                if (r == name) issues.Add("程序集自引用");
                if (r.StartsWith("GUID:", StringComparison.Ordinal)) { if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(r.Substring(5)))) issues.Add("引用GUID不存在: " + r); }
                else if (!names.ContainsKey(r)) issues.Add("未找到引用程序集: " + r);
            }
            return ExtensionSafety.Ok(new { name, valid = issues.Count == 0, issues });
        });

        [ToolAction("health")]
        private object Health(JsonClass a) => ExtensionSafety.Run(() => {
            var missingScenes = EditorBuildSettings.scenes.Where(s => s.enabled && !File.Exists(s.path)).Select(s => s.path).ToArray();
            var assemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();
            return ExtensionSafety.Ok(new { unity_version = Application.unityVersion, EditorApplication.isCompiling, EditorApplication.isUpdating,
                missing_build_scenes = missingScenes, enabled_build_scenes = EditorBuildSettings.scenes.Count(s => s.enabled),
                compilation_failed = EditorUtility.scriptCompilationFailed,
                assemblies = assemblies.Select(x => new { x.name, source_count = x.sourceFiles.Length, references = x.assemblyReferences.Select(r => r.name).ToArray() }).ToArray(),
                quality_levels = QualitySettings.names, product = PlayerSettings.productName, active_target = EditorUserBuildSettings.activeBuildTarget.ToString() });
        });

        private static BuildTargetGroup Group(JsonClass a) {
            if (!a.ContainsKey("target_group")) return BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            BuildTargetGroup group; if (!Enum.TryParse(ExtensionSafety.Text(a, "target_group"), out group) || !Enum.IsDefined(typeof(BuildTargetGroup), group) || group == BuildTargetGroup.Unknown) throw new ArgumentException("无效target_group"); return group;
        }
        private static void RecordSettings(string name) { var objects = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/" + name + ".asset"); if (objects.Length == 0) throw new InvalidOperationException("无法获取设置对象，未修改: " + name); Undo.RecordObjects(objects, "MCP 项目配置"); }
        private static void DirtySettings(string name) { foreach (var obj in AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/" + name + ".asset")) ExtensionSafety.Dirty(obj); }
    }
}

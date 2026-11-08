using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("asset", GroupEn = "Assets", GroupZh = "资源资产",
        Tags = new[] { "asset-database", "dependency-analysis", "build-pipeline", "validation" })]
    public sealed class AssetWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "依赖反查、导入规则、资源校验、Addressables与AssetBundle";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodStr("asset_path", "Assets下资源路径"), new MethodStr("folder", "Assets下扫描目录，默认Assets"),
            new MethodInt("limit", "最多检查资源数量，1至10000，默认1000"), new MethodBool("recursive", "递归依赖，默认true"),
            new MethodBool("dry_run", "预演，默认true"), new MethodBool("readable", "CPU可读"),
            new MethodInt("max_size", "纹理最大尺寸，32至16384的2次幂"), new MethodBool("mipmap", "生成mipmap"),
            new MethodStr("bundle", "AssetBundle名，小写字母数字下划线和横线"),
            new MethodStr("address", "Addressables地址"), new MethodStr("group", "已有Addressables组名"),
            new MethodStr("output", "Assets下已有空输出目录"), new MethodStr("target", "BuildTarget枚举名称") };

        [ToolAction("dependencies", "asset_path=require", "recursive")]
        private object Dependencies(JsonClass a) => ExtensionSafety.Run(() => ExtensionSafety.Ok(AssetDatabase.GetDependencies(ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "asset_path")), ExtensionSafety.Flag(a, "recursive", true)))) ;

        [ToolAction("reverse_dependencies", "asset_path=require", "folder", "limit", "recursive")]
        private object Reverse(JsonClass a) => ExtensionSafety.Run(() => {
            var path = ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "asset_path")); bool recursive = ExtensionSafety.Flag(a, "recursive", true);
            var paths = Candidates(a); var matches = paths.Item1.Where(p => p != path && AssetDatabase.GetDependencies(p, recursive).Contains(path)).ToArray();
            return ExtensionSafety.Ok(new { matches, checked_count = paths.Item1.Length, total = paths.Item2, truncated = paths.Item2 > paths.Item1.Length });
        });

        [ToolAction("texture_rule", "asset_path=require", "readable", "max_size", "mipmap", "dry_run")]
        private object TextureRule(JsonClass a) => ExtensionSafety.Run(() => {
            var path = ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "asset_path")); var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (!importer) throw new ArgumentException("资源不是纹理");
            int size = ExtensionSafety.Integer(a, "max_size", importer.maxTextureSize, 32, 16384);
            if ((size & (size - 1)) != 0) throw new ArgumentException("max_size必须是2次幂");
            bool readable = ExtensionSafety.Flag(a, "readable", importer.isReadable), mipmap = ExtensionSafety.Flag(a, "mipmap", importer.mipmapEnabled), dry = ExtensionSafety.Flag(a, "dry_run", true);
            var before = new { importer.maxTextureSize, importer.isReadable, importer.mipmapEnabled };
            if (!dry) { Undo.RecordObject(importer, "MCP 纹理导入规则"); importer.maxTextureSize = size; importer.isReadable = readable; importer.mipmapEnabled = mipmap; EditorUtility.SetDirty(importer); importer.SaveAndReimport(); }
            return ExtensionSafety.Ok(new { dry_run = dry, before, after = new { maxTextureSize = size, isReadable = readable, mipmapEnabled = mipmap }, reimport_undo_requires_reimport = true });
        });

        [ToolAction("validate", "folder", "limit")]
        private object Validate(JsonClass a) => ExtensionSafety.Run(() => {
            var paths = Candidates(a); var issues = new List<object>();
            foreach (var path in paths.Item1) {
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (!asset) { issues.Add(new { path, issue = "load_failed" }); continue; }
                if (asset is Material material && (!material.shader || material.shader.name == "Hidden/InternalErrorShader")) issues.Add(new { path, issue = "missing_shader" });
                if (asset is GameObject go) foreach (var issue in SceneWorkflowTool.FindMissing(go)) issues.Add(new { path, detail = issue });
            }
            return ExtensionSafety.Ok(new { issues, checked_count = paths.Item1.Length, total = paths.Item2, truncated = paths.Item2 > paths.Item1.Length });
        });

        [ToolAction("bundle_assign", "asset_path=require", "bundle=require", "dry_run")]
        private object Bundle(JsonClass a) => ExtensionSafety.Run(() => {
            var path = ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "asset_path")); string name = ExtensionSafety.Text(a, "bundle");
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9_-]{0,127}$")) throw new ArgumentException("bundle名称不合法");
            var importer = AssetImporter.GetAtPath(path); if (!importer) throw new ArgumentException("找不到导入器"); bool dry = ExtensionSafety.Flag(a, "dry_run", true); string before = importer.assetBundleName;
            if (!dry) { Undo.RecordObject(importer, "MCP 设置AssetBundle"); importer.SetAssetBundleNameAndVariant(name, ""); EditorUtility.SetDirty(importer); AssetDatabase.WriteImportSettingsIfDirty(path); }
            return ExtensionSafety.Ok(new { dry_run = dry, before, bundle = name });
        });

        [ToolAction("bundle_build", "output=require", "target=require", "dry_run")]
        private object Build(JsonClass a) => ExtensionSafety.Run(() => {
            string output = ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "output"));
            if (!Directory.Exists(output) || Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("输出目录必须已存在且为空");
            BuildTarget target;
            if (!Enum.TryParse(ExtensionSafety.Text(a, "target"), out target) || !Enum.IsDefined(typeof(BuildTarget), target) || target == BuildTarget.NoTarget) throw new ArgumentException("无效BuildTarget");
            bool dry = ExtensionSafety.Flag(a, "dry_run", true); var bundles = AssetDatabase.GetAllAssetBundleNames();
            if (!dry && !BuildPipeline.BuildAssetBundles(output, BuildAssetBundleOptions.None, target)) throw new InvalidOperationException("AssetBundle构建失败");
            return ExtensionSafety.Ok(new { dry_run = dry, output, bundles, target = target.ToString(), undo_supported = false });
        });

        [ToolAction("addressables_entry", "asset_path=require", "address", "group", "dry_run")]
        private object Address(JsonClass a) => ExtensionSafety.Run(() => {
            var type = ExtensionSafety.OptionalType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject", "com.unity.addressables");
            var property = type.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
            var settings = property?.GetValue(null) as UnityEngine.Object;
            if (!settings) throw new InvalidOperationException("Addressables尚未创建Settings；不会自动创建或安装");
            string path = ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "asset_path")); string guid = AssetDatabase.AssetPathToGUID(path);
            var settingsType = settings.GetType();
            var find = settingsType.GetMethod("FindAssetEntry", new[] { typeof(string), typeof(bool) });
            if (find == null) throw new NotSupportedException("Addressables FindAssetEntry API不兼容");
            var entry = find.Invoke(settings, new object[] { guid, false }); bool dry = ExtensionSafety.Flag(a, "dry_run", true);
            bool write = a.ContainsKey("address") || a.ContainsKey("group");
            string address = write ? ExtensionSafety.Text(a, "address") : null;
            object group = null;
            if (write) {
                string groupName = ExtensionSafety.Text(a, "group");
                var method = settingsType.GetMethod("FindGroup", new[] { typeof(string) });
                if (method == null) throw new NotSupportedException("Addressables FindGroup API不兼容");
                group = method.Invoke(settings, new object[] { groupName });
                if (group == null) throw new ArgumentException("Addressables组不存在");
                var groups = settingsType.GetProperty("groups")?.GetValue(settings) as System.Collections.IEnumerable;
                if (groups == null) throw new NotSupportedException("Addressables groups API不兼容");
                foreach (var g in groups) {
                    if (g == null) continue;
                    var entries = g.GetType().GetProperty("entries")?.GetValue(g) as System.Collections.IEnumerable;
                    if (entries == null) continue;
                    foreach (var e in entries) if ((string)e.GetType().GetProperty("address")?.GetValue(e) == address && (string)e.GetType().GetProperty("guid")?.GetValue(e) != guid) throw new ArgumentException("Addressables地址重复");
                }
                var create = settingsType.GetMethods().FirstOrDefault(m => m.Name == "CreateOrMoveEntry" && m.GetParameters().Length == 4);
                if (create == null) throw new NotSupportedException("Addressables CreateOrMoveEntry API不兼容");
                if (!dry) {
                    Undo.RecordObject(settings, "MCP Addressables条目"); Undo.RecordObject((UnityEngine.Object)group, "MCP Addressables条目");
                    var oldGroup = entry?.GetType().GetProperty("parentGroup")?.GetValue(entry) as UnityEngine.Object;
                    if (oldGroup) Undo.RecordObject(oldGroup, "MCP Addressables旧分组");
                    entry = create.Invoke(settings, new[] { (object)guid, group, false, true });
                    var setAddress = entry?.GetType().GetMethod("SetAddress", new[] { typeof(string), typeof(bool) });
                    if (setAddress == null) throw new NotSupportedException("Addressables SetAddress API不兼容");
                    setAddress.Invoke(entry, new object[] { address, true });
                    ExtensionSafety.Dirty(settings); ExtensionSafety.Dirty((UnityEngine.Object)group); if (oldGroup) ExtensionSafety.Dirty(oldGroup);
                }
            }
            return ExtensionSafety.Ok(new { guid, exists = entry != null, dry_run = dry, address = write ? address : entry?.GetType().GetProperty("address")?.GetValue(entry), group = group == null ? null : ((UnityEngine.Object)group).name });
        });

        private static Tuple<string[], int> Candidates(JsonClass a)
        {
            string folder = ExtensionSafety.Text(a, "folder", "Assets"); if (folder != "Assets") ExtensionSafety.AssetPath(folder);
            if (!AssetDatabase.IsValidFolder(folder)) throw new ArgumentException("folder不是资源目录");
            int limit = ExtensionSafety.Integer(a, "limit", 1000, 1, 10000);
            var all = AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Where(p => !AssetDatabase.IsValidFolder(p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            return Tuple.Create(all.Take(limit).ToArray(), all.Length);
        }
    }
}

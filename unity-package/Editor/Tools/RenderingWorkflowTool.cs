using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("rendering", GroupEn = "Rendering", GroupZh = "渲染与光照")]
    public sealed class RenderingWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "光照、Lightmap、Probe、Volume与渲染管线检查和配置";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodInt("instance_id", "目标对象ID"), new MethodFloat("intensity", "非负强度"), new MethodFloat("range", "光源范围，大于0"),
            new MethodVector("color", "RGB颜色，每项0至1"), new MethodStr("light_type", "光源类型").SetEnumValues("Point", "Spot", "Directional", "Area"),
            new MethodStr("asset_path", "Assets下资源路径"), new MethodBool("use_builtin", "恢复内置管线"),
            new MethodFloat("weight", "Volume权重0至1"), new MethodBool("is_global", "全局Volume"),
            new MethodInt("lightmap_index", "Lightmap索引"), new MethodBool("confirm", "确认执行不可撤销操作") };

        [ToolAction("light_set", "instance_id=require", "intensity", "range", "color", "light_type")]
        private object LightSet(JsonClass a) => ExtensionSafety.Run(() => {
            var light = ExtensionSafety.Component<Light>(a);
            float intensity = ExtensionSafety.Number(a, "intensity", light.intensity, 0, 1e8f), range = ExtensionSafety.Number(a, "range", light.range, 0.0001f, 1e7f);
            Color color = light.color;
            if (a.ContainsKey("color")) { var c = ExtensionSafety.Vector(a, "color"); if (c.x < 0 || c.y < 0 || c.z < 0 || c.x > 1 || c.y > 1 || c.z > 1) throw new ArgumentException("color每项应在0至1"); color = new Color(c.x, c.y, c.z); }
            LightType type = light.type;
            if (a.ContainsKey("light_type") && (!Enum.TryParse(ExtensionSafety.Text(a, "light_type"), out type) || !Enum.IsDefined(typeof(LightType), type))) throw new ArgumentException("无效光源类型");
            Undo.RecordObject(light, "MCP 修改光源"); light.intensity = intensity; light.range = range; light.color = color; light.type = type; ExtensionSafety.Dirty(light);
            return ExtensionSafety.Ok(new { intensity, range, type = type.ToString() });
        });

        [ToolAction("lightmaps")]
        private object Maps(JsonClass a) => ExtensionSafety.Run(() => ExtensionSafety.Ok(new {
            mode = LightmapSettings.lightmapsMode.ToString(), baking = Lightmapping.isRunning,
            maps = LightmapSettings.lightmaps.Select((m, i) => new { index = i, color = AssetDatabase.GetAssetPath(m.lightmapColor), direction = AssetDatabase.GetAssetPath(m.lightmapDir), shadow = AssetDatabase.GetAssetPath(m.shadowMask) }).ToArray()
        }));

        [ToolAction("lightmap_assign", "instance_id=require", "lightmap_index=require")]
        private object Assign(JsonClass a) => ExtensionSafety.Run(() => {
            var r = ExtensionSafety.Component<Renderer>(a); int index = ExtensionSafety.Integer(a, "lightmap_index", -1, -1, LightmapSettings.lightmaps.Length - 1);
            Undo.RecordObject(r, "MCP 绑定Lightmap"); r.lightmapIndex = index; ExtensionSafety.Dirty(r); return ExtensionSafety.Ok(index);
        });

        [ToolAction("bake_start", "confirm=require")]
        private object Bake(JsonClass a) => ExtensionSafety.Run(() => {
            if (!ExtensionSafety.Flag(a, "confirm") || Application.isPlaying) throw new InvalidOperationException("需要编辑模式且confirm=true；烘焙不可Undo");
            if (Lightmapping.isRunning) throw new InvalidOperationException("已有烘焙任务");
            if (!Lightmapping.BakeAsync()) throw new InvalidOperationException("Unity未接受烘焙请求");
            return ExtensionSafety.Ok(new { started = true, undo_supported = false });
        });

        [ToolAction("bake_cancel")]
        private object Cancel(JsonClass a) => ExtensionSafety.Run(() => { bool running = Lightmapping.isRunning; if (running) Lightmapping.Cancel(); return ExtensionSafety.Ok(new { cancellation_requested = running }); });

        [ToolAction("probe_inspect", "instance_id=require")]
        private object Probe(JsonClass a) => ExtensionSafety.Run(() => {
            var go = ExtensionSafety.Target(a); var reflection = go.GetComponent<ReflectionProbe>(); var group = go.GetComponent<LightProbeGroup>();
            if (!reflection && !group) throw new ArgumentException("缺少ReflectionProbe或LightProbeGroup");
            return ExtensionSafety.Ok(new { reflection = reflection ? new { mode = reflection.mode.ToString(), reflection.resolution, size = SceneWorkflowTool.V(reflection.size), texture = AssetDatabase.GetAssetPath(reflection.bakedTexture) } : null,
                positions = group ? group.probePositions.Select(SceneWorkflowTool.V).ToArray() : null });
        });

        [ToolAction("probe_render", "instance_id=require")]
        private object ProbeRender(JsonClass a) => ExtensionSafety.Run(() => {
            var probe = ExtensionSafety.Component<ReflectionProbe>(a);
            if (probe.mode != ReflectionProbeMode.Realtime) throw new ArgumentException("仅支持Realtime反射探针");
            return ExtensionSafety.Ok(new { render_id = probe.RenderProbe(), undo_supported = false });
        });

        [ToolAction("pipeline_get")]
        private object Pipeline(JsonClass a) => ExtensionSafety.Run(() => ExtensionSafety.Ok(new {
            graphics = AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline), quality = AssetDatabase.GetAssetPath(QualitySettings.renderPipeline),
            effective = GraphicsSettings.currentRenderPipeline ? GraphicsSettings.currentRenderPipeline.GetType().FullName : "Built-in" }));

        [ToolAction("pipeline_set", "asset_path", "use_builtin", "confirm=require")]
        private object PipelineSet(JsonClass a) => ExtensionSafety.Run(() => {
            if (!ExtensionSafety.Flag(a, "confirm")) throw new ArgumentException("需要confirm=true");
            bool builtin = ExtensionSafety.Flag(a, "use_builtin");
            if (builtin && a.ContainsKey("asset_path")) throw new ArgumentException("use_builtin与asset_path互斥");
            var asset = builtin ? null : ExtensionSafety.Asset<RenderPipelineAsset>(a);
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            Undo.RecordObjects(settings, "MCP 设置渲染管线"); GraphicsSettings.defaultRenderPipeline = asset;
            foreach (var o in settings) ExtensionSafety.Dirty(o);
            return Pipeline(a);
        });

        [ToolAction("volume_set", "instance_id=require", "weight", "is_global", "asset_path")]
        private object Volume(JsonClass a) => ExtensionSafety.Run(() => {
            var type = ExtensionSafety.OptionalType("UnityEngine.Rendering.Volume", "com.unity.render-pipelines.core");
            var component = ExtensionSafety.Target(a).GetComponent(type);
            if (!component) throw new ArgumentException("目标没有Volume组件");
            float weight = ExtensionSafety.Number(a, "weight", 1, 0, 1); bool global = ExtensionSafety.Flag(a, "is_global", true);
            var weightField = type.GetField("weight"); var globalProperty = type.GetProperty("isGlobal"); var globalField = type.GetField("isGlobal"); var profileField = type.GetField("sharedProfile");
            if (weightField == null || (globalProperty == null && globalField == null) || profileField == null) throw new NotSupportedException("Volume API版本不兼容");
            UnityEngine.Object profile = null;
            if (a.ContainsKey("asset_path")) { profile = ExtensionSafety.Asset<UnityEngine.Object>(a); if (!profileField.FieldType.IsInstanceOfType(profile)) throw new ArgumentException("资源不是VolumeProfile"); }
            Undo.RecordObject(component, "MCP 设置Volume");
            if (a.ContainsKey("weight")) weightField.SetValue(component, weight);
            if (a.ContainsKey("is_global")) { if (globalProperty != null) globalProperty.SetValue(component, global); else globalField.SetValue(component, global); }
            if (profile) profileField.SetValue(component, profile);
            ExtensionSafety.Dirty(component); return ExtensionSafety.Ok(new { weight = weightField.GetValue(component), is_global = globalProperty != null ? globalProperty.GetValue(component) : globalField.GetValue(component), profile = AssetDatabase.GetAssetPath(profileField.GetValue(component) as UnityEngine.Object) });
        });
    }
}

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("animation", GroupEn = "Animation", GroupZh = "动画与特效")]
    public sealed class AnimationWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "Timeline/Playable绑定、采样、Avatar与约束";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodInt("instance_id", "目标对象ID"), new MethodInt("source_id", "约束或绑定来源对象ID"),
            new MethodStr("asset_path", "动画/Playable/Avatar资源路径"), new MethodFloat("time", "采样时间秒"),
            new MethodFloat("weight", "约束权重0至1"), new MethodBool("active", "约束启用"),
            new MethodStr("constraint", "约束类型").SetEnumValues("position", "rotation", "scale", "parent", "aim"),
            new MethodStr("stream", "Playable输出流名"), new MethodStr("operation", "播放控制").SetEnumValues("play", "pause", "stop", "evaluate") };

        [ToolAction("sample_clip", "instance_id=require", "asset_path=require", "time=require")]
        private object Sample(JsonClass a) => ExtensionSafety.Run(() => {
            if (Application.isPlaying || AnimationMode.InAnimationMode()) throw new InvalidOperationException("需要编辑模式且未进入Animation预览");
            var go = ExtensionSafety.Target(a); var clip = ExtensionSafety.Asset<AnimationClip>(a); float time = ExtensionSafety.Number(a, "time", 0, 0, clip.length);
            AnimationMode.StartAnimationMode();
            try {
                AnimationMode.BeginSampling();
                try { AnimationMode.SampleAnimationClip(go, clip, time); } finally { AnimationMode.EndSampling(); }
                return ExtensionSafety.Ok(new { time, transforms = go.GetComponentsInChildren<Transform>(true).Select(t => new { instance_id = t.gameObject.GetInstanceID(), position = SceneWorkflowTool.V(t.localPosition), rotation = SceneWorkflowTool.V(t.localEulerAngles), scale = SceneWorkflowTool.V(t.localScale) }).ToArray(), restored = true });
            } finally { AnimationMode.StopAnimationMode(); }
        });

        [ToolAction("director_inspect", "instance_id=require")]
        private object Inspect(JsonClass a) => ExtensionSafety.Run(() => {
            var d = ExtensionSafety.Component<PlayableDirector>(a);
            return ExtensionSafety.Ok(new { asset = AssetDatabase.GetAssetPath(d.playableAsset), d.time, d.duration, state = d.state.ToString(),
                outputs = d.playableAsset ? d.playableAsset.outputs.Select(o => new { stream = o.streamName, type = o.outputTargetType?.FullName, binding_id = d.GetGenericBinding(o.sourceObject) ? d.GetGenericBinding(o.sourceObject).GetInstanceID() : 0 }).ToArray() : null });
        });

        [ToolAction("director_asset", "instance_id=require", "asset_path=require")]
        private object SetAsset(JsonClass a) => ExtensionSafety.Run(() => {
            var d = ExtensionSafety.Component<PlayableDirector>(a); var asset = ExtensionSafety.Asset<PlayableAsset>(a);
            Undo.RecordObject(d, "MCP 设置Playable资源"); d.playableAsset = asset; ExtensionSafety.Dirty(d); return Inspect(a);
        });

        [ToolAction("director_bind", "instance_id=require", "stream=require", "source_id=require")]
        private object Bind(JsonClass a) => ExtensionSafety.Run(() => {
            var d = ExtensionSafety.Component<PlayableDirector>(a); if (!d.playableAsset) throw new ArgumentException("Director无Playable资源");
            string stream = ExtensionSafety.Text(a, "stream"); var matches = d.playableAsset.outputs.Where(o => o.streamName == stream).ToArray();
            if (matches.Length != 1) throw new ArgumentException("stream不存在或不唯一");
            var go = ExtensionSafety.Target(a, "source_id"); var output = matches[0]; UnityEngine.Object binding = go;
            if (output.outputTargetType == null) throw new ArgumentException("该输出不接受绑定");
            if (!output.outputTargetType.IsInstanceOfType(binding)) binding = go.GetComponent(output.outputTargetType);
            if (!binding || !output.outputTargetType.IsInstanceOfType(binding)) throw new ArgumentException("绑定类型不匹配: " + output.outputTargetType.FullName);
            Undo.RecordObject(d, "MCP 绑定Playable输出"); d.SetGenericBinding(output.sourceObject, binding); ExtensionSafety.Dirty(d); return Inspect(a);
        });

        [ToolAction("director_control", "instance_id=require", "operation=require", "time")]
        private object Control(JsonClass a) => ExtensionSafety.Run(() => {
            if (!Application.isPlaying) throw new InvalidOperationException("播放控制仅限运行模式，编辑器采样请用sample_clip");
            var d = ExtensionSafety.Component<PlayableDirector>(a); string operation = ExtensionSafety.Text(a, "operation");
            if (!new[] { "play", "pause", "stop", "evaluate" }.Contains(operation)) throw new ArgumentException("未知operation");
            double time = ExtensionSafety.Number(a, "time", (float)d.time, 0, float.MaxValue);
            if (a.ContainsKey("time")) d.time = time;
            switch (operation) { case "play": d.Play(); break; case "pause": d.Pause(); break; case "stop": d.Stop(); break; case "evaluate": d.Evaluate(); break; }
            return Inspect(a);
        });

        [ToolAction("timeline_tracks", "asset_path=require")]
        private object Tracks(JsonClass a) => ExtensionSafety.Run(() => {
            var type = ExtensionSafety.OptionalType("UnityEngine.Timeline.TimelineAsset", "com.unity.timeline"); var asset = ExtensionSafety.Asset<PlayableAsset>(a);
            if (!type.IsInstanceOfType(asset)) throw new ArgumentException("资源不是TimelineAsset");
            var method = type.GetMethod("GetOutputTracks", Type.EmptyTypes); if (method == null) throw new NotSupportedException("Timeline API不兼容");
            var tracks = method.Invoke(asset, null) as System.Collections.IEnumerable; var result = new System.Collections.Generic.List<object>();
            foreach (var track in tracks) {
                var clips = track.GetType().GetMethod("GetClips", Type.EmptyTypes)?.Invoke(track, null) as System.Collections.IEnumerable;
                var list = new System.Collections.Generic.List<object>();
                if (clips != null) foreach (var clip in clips) list.Add(new { name = clip.GetType().GetProperty("displayName")?.GetValue(clip), start = clip.GetType().GetProperty("start")?.GetValue(clip), duration = clip.GetType().GetProperty("duration")?.GetValue(clip) });
                result.Add(new { name = ((UnityEngine.Object)track).name, type = track.GetType().FullName, clips = list });
            }
            return ExtensionSafety.Ok(result);
        });

        [ToolAction("avatar", "instance_id=require", "asset_path")]
        private object Avatar(JsonClass a) => ExtensionSafety.Run(() => {
            var animator = ExtensionSafety.Component<Animator>(a);
            if (a.ContainsKey("asset_path")) { var avatar = ExtensionSafety.Asset<Avatar>(a); if (!avatar.isValid) throw new ArgumentException("Avatar无效"); Undo.RecordObject(animator, "MCP 设置Avatar"); animator.avatar = avatar; ExtensionSafety.Dirty(animator); }
            var v = animator.avatar; return ExtensionSafety.Ok(new { assigned = v != null, valid = v && v.isValid, human = v && v.isHuman, asset = AssetDatabase.GetAssetPath(v) });
        });

        [ToolAction("constraint_set", "instance_id=require", "source_id=require", "constraint=require", "weight", "active")]
        private object Constraint(JsonClass a) => ExtensionSafety.Run(() => {
            var go = ExtensionSafety.Target(a); var source = ExtensionSafety.Target(a, "source_id");
            if (go == source || source.transform.IsChildOf(go.transform)) throw new ArgumentException("约束源不能是自身或后代");
            float weight = ExtensionSafety.Number(a, "weight", 1, 0, 1); bool active = ExtensionSafety.Flag(a, "active", true); Type type;
            switch (ExtensionSafety.Text(a, "constraint")) { case "position": type = typeof(PositionConstraint); break; case "rotation": type = typeof(RotationConstraint); break; case "scale": type = typeof(ScaleConstraint); break; case "parent": type = typeof(ParentConstraint); break; case "aim": type = typeof(AimConstraint); break; default: throw new ArgumentException("未知约束类型"); }
            var component = go.GetComponent(type) ?? Undo.AddComponent(go, type); var constraint = (IConstraint)component;
            Undo.RecordObject(component, "MCP 设置约束");
            var sources = new System.Collections.Generic.List<ConstraintSource>(); constraint.GetSources(sources);
            int index = sources.FindIndex(x => x.sourceTransform == source.transform); var entry = new ConstraintSource { sourceTransform = source.transform, weight = weight };
            if (index < 0) constraint.AddSource(entry); else constraint.SetSource(index, entry);
            constraint.constraintActive = active; ExtensionSafety.Dirty(component); return ExtensionSafety.Ok(new { component_id = component.GetInstanceID(), constraint.sourceCount });
        });
    }
}

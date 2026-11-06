using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("audio", GroupEn = "Assets", GroupZh = "资源资产")]
    public sealed class AudioWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "音频源、监听器、混音器暴露参数与快照过渡";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodInt("instance_id", "AudioSource对象ID"), new MethodStr("asset_path", "音频/混音器/快照资源路径"),
            new MethodStr("group_path", "AudioMixerGroup资源路径"), new MethodFloat("volume", "音量0至1"), new MethodFloat("pitch", "音高-3至3"),
            new MethodBool("loop", "循环"), new MethodFloat("spatial_blend", "空间混合0至1"),
            new MethodStr("operation", "播放操作").SetEnumValues("play", "pause", "stop"),
            new MethodStr("parameter", "混音器暴露参数名"), new MethodFloat("value", "混音器参数值"), new MethodFloat("seconds", "快照过渡秒数，默认0") };

        [ToolAction("source", "instance_id=require", "asset_path", "group_path", "volume", "pitch", "loop", "spatial_blend")]
        private object Source(JsonClass a) => ExtensionSafety.Run(() => {
            var source = ExtensionSafety.Component<AudioSource>(a);
            var clip = a.ContainsKey("asset_path") ? ExtensionSafety.Asset<AudioClip>(a) : source.clip;
            var group = a.ContainsKey("group_path") ? ExtensionSafety.Asset<AudioMixerGroup>(a, "group_path") : source.outputAudioMixerGroup;
            float volume = ExtensionSafety.Number(a, "volume", source.volume, 0, 1), pitch = ExtensionSafety.Number(a, "pitch", source.pitch, -3, 3), spatial = ExtensionSafety.Number(a, "spatial_blend", source.spatialBlend, 0, 1);
            bool loop = ExtensionSafety.Flag(a, "loop", source.loop);
            if (new[] { "asset_path", "group_path", "volume", "pitch", "loop", "spatial_blend" }.Any(a.ContainsKey)) {
                Undo.RecordObject(source, "MCP 设置音频源"); source.clip = clip; source.outputAudioMixerGroup = group; source.volume = volume; source.pitch = pitch; source.spatialBlend = spatial; source.loop = loop; ExtensionSafety.Dirty(source);
            }
            return ExtensionSafety.Ok(new { clip = AssetDatabase.GetAssetPath(clip), group = AssetDatabase.GetAssetPath(group), source.volume, source.pitch, source.loop, source.spatialBlend, source.isPlaying });
        });

        [ToolAction("source_control", "instance_id=require", "operation=require")]
        private object Control(JsonClass a) => ExtensionSafety.Run(() => {
            if (!Application.isPlaying) throw new InvalidOperationException("播放控制仅限运行模式"); var source = ExtensionSafety.Component<AudioSource>(a);
            switch (ExtensionSafety.Text(a, "operation")) { case "play": if (!source.clip) throw new ArgumentException("没有AudioClip"); source.Play(); break; case "pause": source.Pause(); break; case "stop": source.Stop(); break; default: throw new ArgumentException("未知operation"); }
            return ExtensionSafety.Ok(new { source.isPlaying, undo_supported = false });
        });

        [ToolAction("listeners")]
        private object Listeners(JsonClass a) => ExtensionSafety.Run(() => {
            var listeners = Resources.FindObjectsOfTypeAll<AudioListener>().Where(x => !EditorUtility.IsPersistent(x) && x.gameObject.scene.IsValid()).ToArray();
            int active = listeners.Count(x => x.isActiveAndEnabled);
            return ExtensionSafety.Ok(new { active_count = active, valid = active == 1, listeners = listeners.Select(x => new { id = x.gameObject.GetInstanceID(), x.name, x.enabled, x.isActiveAndEnabled }).ToArray(), AudioListener.volume, AudioListener.pause });
        });

        [ToolAction("mixer_parameter", "asset_path=require", "parameter=require", "value")]
        private object Mixer(JsonClass a) => ExtensionSafety.Run(() => {
            var mixer = ExtensionSafety.Asset<AudioMixer>(a); string name = ExtensionSafety.Text(a, "parameter"); float value;
            if (!mixer.GetFloat(name, out value)) throw new ArgumentException("暴露参数不存在");
            if (a.ContainsKey("value")) {
                if (!Application.isPlaying) throw new InvalidOperationException("混音器暴露参数写入仅限运行模式");
                value = ExtensionSafety.Number(a, "value"); if (!mixer.SetFloat(name, value)) throw new InvalidOperationException("设置混音器参数失败");
            }
            return ExtensionSafety.Ok(new { parameter = name, value, undo_supported = false });
        });

        [ToolAction("snapshot_transition", "asset_path=require", "seconds")]
        private object Snapshot(JsonClass a) => ExtensionSafety.Run(() => {
            if (!Application.isPlaying) throw new InvalidOperationException("快照过渡仅限运行模式");
            var snapshot = ExtensionSafety.Asset<AudioMixerSnapshot>(a); float seconds = ExtensionSafety.Number(a, "seconds", 0, 0, 3600); snapshot.TransitionTo(seconds);
            return ExtensionSafety.Ok(new { snapshot = snapshot.name, seconds, undo_supported = false });
        });
    }
}

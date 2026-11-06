using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("optional_runtime", GroupEn = "Runtime", GroupZh = "运行与调试")]
    public sealed class OptionalRuntimeTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "Netcode与XR可选包只读适配；缺包明确报错，不自动安装";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodInt("instance_id", "包含NetworkObject的场景对象ID"), new MethodStr("feature", "XR布尔特征名，如primaryButton、triggerButton、isTracked") };

        [ToolAction("netcode_status")]
        private object Network(JsonClass a) => ExtensionSafety.Run(() => {
            var type = ExtensionSafety.OptionalType("Unity.Netcode.NetworkManager", "com.unity.netcode.gameobjects");
            var manager = type.GetProperty("Singleton", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (manager == null) throw new InvalidOperationException("当前场景没有活动NetworkManager");
            var fields = new[] { "IsListening", "IsServer", "IsClient", "IsHost", "IsConnectedClient", "LocalClientId" };
            var result = new Dictionary<string, object>();
            foreach (var name in fields) { var property = type.GetProperty(name); if (property == null) throw new NotSupportedException("Netcode API缺少: " + name); result[name] = property.GetValue(manager); }
            return ExtensionSafety.Ok(result);
        });

        [ToolAction("network_object", "instance_id=require")]
        private object NetworkObject(JsonClass a) => ExtensionSafety.Run(() => {
            var type = ExtensionSafety.OptionalType("Unity.Netcode.NetworkObject", "com.unity.netcode.gameobjects");
            var component = ExtensionSafety.Target(a).GetComponent(type); if (!component) throw new ArgumentException("目标无NetworkObject");
            var result = new Dictionary<string, object>();
            foreach (var name in new[] { "NetworkObjectId", "OwnerClientId", "IsSpawned", "IsOwner", "IsPlayerObject" }) {
                var property = type.GetProperty(name); if (property == null) throw new NotSupportedException("Netcode API缺少: " + name); result[name] = property.GetValue(component);
            }
            return ExtensionSafety.Ok(result);
        });

        [ToolAction("xr_loaders")]
        private object Loaders(JsonClass a) => ExtensionSafety.Run(() => {
            var type = ExtensionSafety.OptionalType("UnityEngine.XR.Management.XRGeneralSettings", "com.unity.xr.management");
            var settings = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (settings == null) throw new InvalidOperationException("XRGeneralSettings未配置");
            var manager = type.GetProperty("Manager")?.GetValue(settings); if (manager == null) throw new InvalidOperationException("XRManager未配置");
            var loaders = manager.GetType().GetProperty("activeLoaders")?.GetValue(manager) as IEnumerable;
            if (loaders == null) throw new NotSupportedException("XR Management API不兼容");
            var list = new List<object>(); foreach (var loader in loaders) list.Add(new { name = ((UnityEngine.Object)loader).name, type = loader.GetType().FullName });
            return ExtensionSafety.Ok(new { loaders = list, initialized = manager.GetType().GetProperty("isInitializationComplete")?.GetValue(manager), active = (manager.GetType().GetProperty("activeLoader")?.GetValue(manager) as UnityEngine.Object)?.name });
        });

        [ToolAction("xr_devices", "feature")]
        private object Devices(JsonClass a) => ExtensionSafety.Run(() => {
            ExtensionSafety.OptionalType("UnityEngine.XR.Management.XRGeneralSettings", "com.unity.xr.management");
            var devices = new List<UnityEngine.XR.InputDevice>(); UnityEngine.XR.InputDevices.GetDevices(devices);
            string feature = a.ContainsKey("feature") ? ExtensionSafety.Text(a, "feature") : null;
            return ExtensionSafety.Ok(devices.Select(d => {
                bool value = false; bool available = feature != null && d.TryGetFeatureValue(new UnityEngine.XR.InputFeatureUsage<bool>(feature), out value);
                return new { d.name, d.manufacturer, d.isValid, characteristics = d.characteristics.ToString(), feature, available, value };
            }).ToArray());
        });
    }
}

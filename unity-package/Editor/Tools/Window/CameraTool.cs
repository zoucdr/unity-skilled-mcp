using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;
using UniMcp.Utils;

namespace UniMcp.Tools
{
    /// <summary>
    /// 场景相机管理与截图，并在安装 Cinemachine 时提供虚拟相机操作。
    /// 与 gameplay（Game View 截图）和 window（Scene View 截图）互补：本工具围绕场景中的 Camera 组件工作。
    /// 对应方法名: manage_camera
    /// </summary>
    [ToolModule("camera", GroupEn = "Editor", GroupZh = "编辑器",
        Tags = new[] { "screenshot", "cinemachine" })]
    public class CameraTool : ToolModuleBase
    {
        private static readonly string[] VirtualCameraTypes =
        {
            "Cinemachine.CinemachineVirtualCamera",
            "Unity.Cinemachine.CinemachineCamera",
            "Cinemachine.CinemachineFreeLook"
        };

        private static readonly string[] BrainTypes =
        {
            "Cinemachine.CinemachineBrain",
            "Unity.Cinemachine.CinemachineBrain"
        };

        public override string Description => L.T(
            "Create, configure and capture scene cameras; inspect and drive Cinemachine virtual cameras when installed",
            "创建、配置与截图场景相机；安装 Cinemachine 时可检查并驱动虚拟相机");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T("Operation type", "操作类型"), false)
                    .SetEnumValues(
                        "create", "list", "get", "set", "look_at", "screenshot", "screenshot_multiview",
                        "cm_status", "cm_create", "cm_set", "cm_brain"),

                new MethodStr("name", L.T("Camera GameObject name, or a scene path / instance ID to locate it", "相机对象名称，或用于定位的场景路径 / instanceID"))
                    .AddExample("Main Camera"),

                new MethodVector("position", L.T("World position used by create or look_at", "create 或 look_at 使用的世界坐标"), true, 3)
                    .AddExample(0f, 1f, -10f),

                new MethodVector("rotation", L.T("World Euler rotation in degrees", "世界欧拉旋转角度"), true, 3)
                    .AddExample(0f, 0f, 0f),

                new MethodVector("look_at", L.T("World position the camera looks at", "相机朝向的世界坐标"), true, 3)
                    .AddExample(0f, 1f, 0f),

                new MethodStr("target", L.T("GameObject path used as the look-at or Cinemachine follow target", "用作朝向目标或 Cinemachine 跟随目标的对象路径"))
                    .AddExample("Player"),

                new MethodStr("follow", L.T("GameObject path used as the Cinemachine follow target", "Cinemachine 跟随目标的对象路径"))
                    .AddExample("Player"),

                new MethodFloat("fov", L.T("Field of view in degrees", "视场角（度）"))
                    .SetRange(1f, 179f),

                new MethodFloat("near", L.T("Near clip plane", "近裁剪面"))
                    .SetRange(0.001f, 1000f),

                new MethodFloat("far", L.T("Far clip plane", "远裁剪面"))
                    .SetRange(0.01f, 100000f),

                new MethodBool("orthographic", L.T("Use an orthographic projection", "使用正交投影")),

                new MethodFloat("ortho_size", L.T("Orthographic size", "正交尺寸"))
                    .SetRange(0.01f, 10000f),

                new MethodFloat("depth", L.T("Camera depth, lower renders first", "相机深度，较小的先渲染"))
                    .SetRange(-1000f, 1000f),

                new MethodStr("clear_flags", L.T("Clear flags: Skybox, SolidColor, Depth, Nothing", "清除标志：Skybox、SolidColor、Depth、Nothing"))
                    .SetEnumValues("Skybox", "SolidColor", "Depth", "Nothing"),

                new MethodStr("background", L.T("Background color as #RRGGBB or #RRGGBBAA", "背景色，格式 #RRGGBB 或 #RRGGBBAA"))
                    .AddExample("#1E1E1EFF"),

                new MethodInt("culling_mask", L.T("Culling mask layer bitmask", "剔除遮罩层位掩码"))
                    .SetRange(0, 2147483647),

                new MethodInt("width", L.T("Capture width in pixels", "截图宽度（像素）"))
                    .SetRange(16, 8192),

                new MethodInt("height", L.T("Capture height in pixels", "截图高度（像素）"))
                    .SetRange(16, 8192),

                new MethodStr("save_path", L.T("Capture output path", "截图输出路径"))
                    .AddExample("Assets/Screenshots/camera.png"),

                new MethodStr("folder", L.T("Output folder used by screenshot_multiview", "screenshot_multiview 使用的输出目录"))
                    .AddExample("Assets/Screenshots"),

                new MethodBool("all_cameras", L.T("Capture every enabled scene camera with screenshot_multiview", "screenshot_multiview 时截取所有启用的场景相机"))
                    .SetDefault(false),

                new MethodFloat("priority", L.T("Cinemachine virtual camera priority; the highest wins", "Cinemachine 虚拟相机优先级，最高者生效"))
                    .SetRange(-100f, 1000f),

                new MethodBool("enabled", L.T("Enable or disable the camera component", "启用或禁用相机组件")),

                new MethodArr("cameras", L.T("Camera names captured by screenshot_multiview", "screenshot_multiview 要截取的相机名称列表"), true, "string")
                    .AddExample("Main Camera", "Top View")
            };
        }

        [ToolAction("create", Parameters = new string[]
        {
            "name", "position", "rotation", "look_at", "target", "fov", "near", "far", "orthographic", "ortho_size",
            "depth", "clear_flags", "background", "culling_mask", "enabled"
        }, Inject = new string[] { "action=create" })]
        private object CreateCamera(JsonClass args)
        {
            string name = (args["name"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = "Camera";
            }
            try
            {
                GameObject gameObject = new GameObject(name);
                Undo.RegisterCreatedObjectUndo((UnityEngine.Object)(object)gameObject, "Create Camera");
                Camera camera = gameObject.AddComponent<Camera>();
                ApplyTransform(((Component)camera).transform, args);
                ApplyCameraSettings(camera, args);
                Vector3? position = GameObjectUtils.ParseVector3(args["position"] as JsonArray);
                Vector3? lookAtPoint = GameObjectUtils.ParseVector3(args["look_at"] as JsonArray);
                if (!position.HasValue && !lookAtPoint.HasValue)
                {
                    ((Component)camera).transform.localPosition = new Vector3(0f, 1f, -10f);
                }
                if (lookAtPoint.HasValue)
                {
                    Transform transform = ((Component)camera).transform;
                    Vector3 val = lookAtPoint.Value - ((Component)camera).transform.position;
                    transform.rotation = Quaternion.LookRotation(val.normalized);
                }
                string target = (args["target"]?.Value ?? "").Trim();
                if (!string.IsNullOrEmpty(target))
                {
                    GameObject targetObject = GameObjectUtils.FindObjectByIdOrPath(target);
                    if ((UnityEngine.Object)(object)targetObject == (UnityEngine.Object)null)
                    {
                        return Response.Error("Look-at target not found: " + target);
                    }
                    Vector3 direction = targetObject.transform.position - ((Component)camera).transform.position;
                    if (direction.sqrMagnitude > 0.0001f)
                    {
                        ((Component)camera).transform.rotation = Quaternion.LookRotation(direction.normalized);
                    }
                }
                Selection.activeGameObject = gameObject;
                EditorSceneDirty();
                return Response.Success("Camera created.", DescribeCamera(camera));
            }
            catch (Exception ex)
            {
                return Response.Error("Failed to create camera: " + ex.Message);
            }
        }

        [ToolAction("list", Inject = new string[] { "action=list" })]
        private object ListCameras(JsonClass args)
        {
            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            var items = new List<object>();
            for (int i = 0; i < cameras.Length && i < 200; i++)
                items.Add(DescribeCamera(cameras[i]));

            return Response.Success("Cameras listed.", new { count = items.Count, items = items });
        }

        [ToolAction("get", Parameters = new string[] { "name" }, Required = new string[] { "name" }, Inject = new string[] { "action=get" })]
        private object GetCamera(JsonClass args)
        {
            Camera camera;
            var error = ResolveCamera(args, out camera);
            if (error != null)
                return error;

            return Response.Success("Camera info retrieved.", DescribeCamera(camera));
        }

        [ToolAction("set", Parameters = new string[] { "name", "position", "rotation", "fov", "near", "far", "orthographic", "ortho_size", "depth", "clear_flags", "background", "culling_mask", "enabled" }, Inject = new string[] { "action=set" })]
        private object SetCamera(JsonClass args)
        {
            Camera camera;
            var error = ResolveCamera(args, out camera);
            if (error != null)
                return error;

            try
            {
                Undo.RecordObject(camera, "Set Camera");
                ApplyCameraSettings(camera, args);
                ApplyTransform(camera.transform, args);
                EditorUtility.SetDirty(camera);
                EditorSceneDirty();

                return Response.Success("Camera updated.", DescribeCamera(camera));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to update camera: " + e.Message);
            }
        }

        [ToolAction("look_at", Parameters = new string[] { "name", "look_at", "target" }, Inject = new string[] { "action=look_at" })]
        private object LookAt(JsonClass args)
        {
            Camera camera;
            var error = ResolveCamera(args, out camera);
            if (error != null)
                return error;

            var worked = false;
            Vector3 direction = Vector3.forward;

            var targetPath = (args["target"]?.Value ?? "").Trim();
            if (!string.IsNullOrEmpty(targetPath))
            {
                var target = GameObjectUtils.FindObjectByIdOrPath(targetPath);
                if (target == null)
                    return Response.Error("Target not found: " + targetPath);
                direction = target.transform.position - camera.transform.position;
                worked = true;
            }
            else
            {
                var point = GameObjectUtils.ParseVector3(args["look_at"] as JsonArray);
                if (!point.HasValue)
                    return Response.Error("Provide 'target' or 'look_at' for look_at.");

                direction = point.Value - camera.transform.position;
                worked = true;
            }

            if (!worked || direction.sqrMagnitude < 0.000001f)
                return Response.Error("Look direction is degenerate; move the camera or choose another target.");

            Undo.RecordObject(camera.transform, "Camera Look At");
            camera.transform.rotation = Quaternion.LookRotation(direction.normalized);
            EditorUtility.SetDirty(camera);
            EditorSceneDirty();

            return Response.Success("Camera aimed.", DescribeCamera(camera));
        }

        [ToolAction("screenshot", Parameters = new string[] { "name", "width", "height", "save_path" }, Inject = new string[] { "action=screenshot" })]
        private object Screenshot(JsonClass args)
        {
            Camera camera;
            var error = ResolveCamera(args, out camera);
            if (error != null)
                return error;

            var savePath = args["save_path"]?.Value;
            if (string.IsNullOrWhiteSpace(savePath))
                savePath = "Assets/Screenshots/camera.png";

            var width = args["width"].AsIntDefault(1920);
            var height = args["height"].AsIntDefault(1080);

            var result = ScreenCaptureUtil.CaptureFromCamera(camera, ToFullPath(savePath), width, height);
            if (!result.success)
                return Response.Error("Camera capture failed: " + result.error);

            return Response.Success("Camera capture saved.", new
            {
                path = savePath,
                width = result.width,
                height = result.height,
                size = result.size
            });
        }

        [ToolAction("screenshot_multiview", Parameters = new string[] { "width", "height", "folder", "all_cameras", "cameras" }, Inject = new string[] { "action=screenshot_multiview" })]
        private object ScreenshotMultiView(JsonClass args)
        {
            var folder = args["folder"]?.Value;
            if (string.IsNullOrWhiteSpace(folder))
                folder = "Assets/Screenshots";

            folder = NormalizeAssetPath(folder);
            var width = args["width"].AsIntDefault(1280);
            var height = args["height"].AsIntDefault(720);

            var targets = new List<Camera>();
            var requestedNames = ReadStringArray(args, "cameras");
            var allCameras = args["all_cameras"].AsBoolDefault(false);

            if (requestedNames.Count > 0)
            {
                for (int i = 0; i < requestedNames.Count; i++)
                {
                    var camera = FindCameraByName(requestedNames[i]);
                    if (camera != null)
                        targets.Add(camera);
                }
            }
            else
            {
                var cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (!allCameras && !cameras[i].enabled)
                        continue;
                    if (!cameras[i].gameObject.activeInHierarchy)
                        continue;
                    targets.Add(cameras[i]);
                }
            }

            if (targets.Count == 0)
                return Response.Error("No cameras selected for capture.");

            var captured = new List<object>();
            for (int i = 0; i < targets.Count && i < 32; i++)
            {
                var camera = targets[i];
                var fileName = SanitizeFileName(camera.gameObject.name) + "_" + i + ".png";
                var assetPath = folder.TrimEnd('/') + "/" + fileName;
                var result = ScreenCaptureUtil.CaptureFromCamera(camera, ToFullPath(assetPath), width, height);

                captured.Add(new
                {
                    camera = camera.gameObject.name,
                    path = assetPath,
                    success = result.success,
                    error = result.error,
                    width = result.width,
                    height = result.height
                });
            }

            return Response.Success("Multi-view capture finished.", new { count = captured.Count, items = captured });
        }

        // ---------------- Cinemachine ----------------

        [ToolAction("cm_status", Inject = new string[] { "action=cm_status" })]
        private object CinemachineStatus(JsonClass args)
        {
            var type = OptionalApi.FindAnyType(VirtualCameraTypes);
            if (type == null)
                return Response.Error("Cinemachine is not installed. Add 'com.unity.cinemachine' to the project to use virtual cameras.");

            var brains = UnityEngine.Object.FindObjectsOfType(OptionalApi.FindAnyType(BrainTypes) ?? type);
            var virtualCameras = FindVirtualCameras(type);
            var items = new List<object>();
            for (int i = 0; i < virtualCameras.Count && i < 100; i++)
            {
                var vcam = virtualCameras[i];
                items.Add(new
                {
                    game_object = ((Component)vcam).gameObject.name,
                    type = vcam.GetType().Name,
                    priority = ReadPriority(vcam),
                    follow = ReadTargetPath(vcam, "Follow", "m_Follow"),
                    look_at = ReadTargetPath(vcam, "LookAt", "m_LookAt"),
                    fov = ReadLensField(vcam, "FieldOfView")
                });
            }

            return Response.Success("Cinemachine status retrieved.", new
            {
                installed = true,
                virtual_camera_count = items.Count,
                virtual_cameras = items,
                brain_count = brains != null ? brains.Length : 0
            });
        }

        [ToolAction("cm_create", Parameters = new string[] { "name", "position", "rotation", "target", "follow", "fov", "priority", "enabled" }, Inject = new string[] { "action=cm_create" })]
        private object CinemachineCreate(JsonClass args)
        {
            var type = OptionalApi.FindAnyType(VirtualCameraTypes);
            if (type == null)
                return Response.Error("Cinemachine is not installed. Add 'com.unity.cinemachine' to the project to use virtual cameras.");

            var name = (args["name"]?.Value ?? "Virtual Camera").Trim();
            if (string.IsNullOrEmpty(name))
                name = "Virtual Camera";

            try
            {
                var gameObject = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(gameObject, "Create Virtual Camera");

                var vcam = gameObject.AddComponent(type);
                if (vcam == null)
                    return Response.Error("Failed to add " + type.Name + " to " + name);

                ApplyTransform(gameObject.transform, args);
                ApplyCinemachineSettings(vcam, args);

                var position = GameObjectUtils.ParseVector3(args["position"] as JsonArray);
                if (position.HasValue)
                    gameObject.transform.position = position.Value;

                Selection.activeGameObject = gameObject;
                EditorSceneDirty();

                return Response.Success("Virtual camera created.", new
                {
                    game_object = gameObject.name,
                    type = type.Name,
                    priority = ReadPriority(vcam),
                    follow = ReadTargetPath(vcam, "Follow", "m_Follow"),
                    look_at = ReadTargetPath(vcam, "LookAt", "m_LookAt"),
                    fov = ReadLensField(vcam, "FieldOfView")
                });
            }
            catch (Exception e)
            {
                return Response.Error("Failed to create virtual camera: " + e.Message);
            }
        }

        [ToolAction("cm_set", Parameters = new string[] { "name", "target", "follow", "fov", "priority", "enabled" }, Inject = new string[] { "action=cm_set" })]
        private object CinemachineSet(JsonClass args)
        {
            var type = OptionalApi.FindAnyType(VirtualCameraTypes);
            if (type == null)
                return Response.Error("Cinemachine is not installed. Add 'com.unity.cinemachine' to the project to use virtual cameras.");

            var name = (args["name"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'name' is required for cm_set.");

            var virtualCameras = FindVirtualCameras(type);
            object target = null;
            for (int i = 0; i < virtualCameras.Count; i++)
            {
                var component = (Component)virtualCameras[i];
                if (string.Equals(component.gameObject.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    target = virtualCameras[i];
                    break;
                }
            }

            if (target == null)
                return Response.Error("Virtual camera not found: " + name);

            try
            {
                Undo.RecordObject((UnityEngine.Object)target, "Set Virtual Camera");
                var applied = ApplyCinemachineSettings(target, args);
                EditorUtility.SetDirty((UnityEngine.Object)target);
                EditorSceneDirty();

                return Response.Success("Virtual camera updated.", new
                {
                    game_object = name,
                    applied = applied,
                    priority = ReadPriority(target),
                    follow = ReadTargetPath(target, "Follow", "m_Follow"),
                    look_at = ReadTargetPath(target, "LookAt", "m_LookAt"),
                    fov = ReadLensField(target, "FieldOfView")
                });
            }
            catch (Exception e)
            {
                return Response.Error("Failed to update virtual camera: " + e.Message);
            }
        }

        [ToolAction("cm_brain", Inject = new string[] { "action=cm_brain" })]
        private object CinemachineBrain(JsonClass args)
        {
            var brainType = OptionalApi.FindAnyType(BrainTypes);
            if (brainType == null)
                return Response.Error("Cinemachine is not installed. Add 'com.unity.cinemachine' to the project to inspect the brain.");

            var brains = UnityEngine.Object.FindObjectsOfType(brainType);
            if (brains == null || brains.Length == 0)
                return Response.Error("No CinemachineBrain found in the open scenes.");

            var items = new List<object>();
            for (int i = 0; i < brains.Length; i++)
            {
                var brain = brains[i];
                var active = OptionalApi.Get(brain, "ActiveVirtualCamera");
                var activeComponent = active as Component;

                items.Add(new
                {
                    game_object = ((Component)brain).gameObject.name,
                    active_virtual_camera = activeComponent != null ? activeComponent.gameObject.name : null,
                    is_blending = OptionalApi.Get(brain, "IsBlending")
                });
            }

            return Response.Success("Cinemachine brain info retrieved.", new { count = items.Count, items = items });
        }

        private List<object> FindVirtualCameras(Type type)
        {
            var result = new List<object>();
            var found = UnityEngine.Object.FindObjectsOfType(type);
            for (int i = 0; i < found.Length; i++)
                result.Add(found[i]);
            return result;
        }

        private List<string> ApplyCinemachineSettings(object vcam, JsonClass args)
        {
            var applied = new List<string>();

            if (args["priority"] != null)
            {
                var node = args["priority"];
                if (node.type == JsonNodeType.Float || node.type == JsonNodeType.Integer)
                {
                    var priority = node.AsFloat;
                    if (OptionalApi.Set(vcam, "Priority", priority) || OptionalApi.Set(vcam, "m_Priority", Mathf.RoundToInt(priority)))
                        applied.Add("priority");
                }
            }

            var follow = (args["follow"]?.Value ?? args["target"]?.Value ?? "").Trim();
            if (!string.IsNullOrEmpty(follow))
            {
                var followObject = GameObjectUtils.FindObjectByIdOrPath(follow);
                if (followObject != null && OptionalApi.Set(vcam, "Follow", followObject.transform))
                    applied.Add("follow");
            }

            var lookAt = (args["target"]?.Value ?? "").Trim();
            if (!string.IsNullOrEmpty(lookAt))
            {
                var lookObject = GameObjectUtils.FindObjectByIdOrPath(lookAt);
                if (lookObject != null && OptionalApi.Set(vcam, "LookAt", lookObject.transform))
                    applied.Add("look_at");
            }

            if (args["fov"] != null)
            {
                var node = args["fov"];
                if ((node.type == JsonNodeType.Float || node.type == JsonNodeType.Integer) && WriteLensField(vcam, "FieldOfView", node.AsFloat))
                    applied.Add("fov");
            }

            if (args["enabled"] != null)
            {
                var behaviour = vcam as Behaviour;
                if (behaviour != null)
                {
                    behaviour.enabled = args["enabled"].AsBoolDefault(true);
                    applied.Add("enabled");
                }
            }

            return applied;
        }

        private static float ReadPriority(object vcam)
        {
            var value = OptionalApi.Get(vcam, "Priority") ?? OptionalApi.Get(vcam, "m_Priority");
            if (value == null)
                return 0f;

            try
            {
                return Convert.ToSingle(value);
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private static string ReadTargetPath(object vcam, string propertyName, string fieldName)
        {
            var value = OptionalApi.Get(vcam, propertyName) ?? OptionalApi.Get(vcam, fieldName);
            var transform = value as Transform;
            return transform != null ? transform.gameObject.name : null;
        }

        private static object GetLens(object vcam)
        {
            return OptionalApi.Get(vcam, "Lens") ?? OptionalApi.Get(vcam, "m_Lens");
        }

        private static float? ReadLensField(object vcam, string field)
        {
            var lens = GetLens(vcam);
            if (lens == null)
                return null;

            var value = OptionalApi.Get(lens, field);
            if (value == null)
                return null;

            try
            {
                return Convert.ToSingle(value);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool WriteLensField(object vcam, string field, float value)
        {
            var lens = GetLens(vcam);
            if (lens == null)
                return false;

            if (!OptionalApi.Set(lens, field, value))
                return false;

            // LensSettings 可能是结构体：写回组件字段，保证修改生效
            OptionalApi.Set(vcam, "Lens", lens);
            OptionalApi.Set(vcam, "m_Lens", lens);
            return true;
        }

        // ---------------- 辅助 ----------------

        private object ResolveCamera(JsonClass args, out Camera camera)
        {
            camera = null;
            var name = (args["name"]?.Value ?? "").Trim();

            if (string.IsNullOrEmpty(name))
            {
                camera = Camera.main;
                if (camera == null)
                {
                    var any = UnityEngine.Object.FindObjectOfType<Camera>();
                    camera = any;
                }
            }
            else
            {
                camera = FindCameraByName(name);
            }

            if (camera == null)
                return Response.Error("Camera not found. Provide 'name' with the camera name or scene path.");

            return null;
        }

        private static Camera FindCameraByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                if (string.Equals(cameras[i].gameObject.name, name, StringComparison.OrdinalIgnoreCase))
                    return cameras[i];
            }

            var resolved = GameObjectUtils.FindObjectByIdOrPath(name);
            return resolved != null ? resolved.GetComponent<Camera>() : null;
        }

        private static void ApplyTransform(Transform transform, JsonClass args)
        {
            var position = GameObjectUtils.ParseVector3(args["position"] as JsonArray);
            if (position.HasValue)
                transform.position = position.Value;

            var rotation = GameObjectUtils.ParseVector3(args["rotation"] as JsonArray);
            if (rotation.HasValue)
                transform.eulerAngles = rotation.Value;
        }

        private static void ApplyCameraSettings(Camera camera, JsonClass args)
        {
            if (args["fov"] != null && HasNumber(args["fov"]))
                camera.fieldOfView = args["fov"].AsFloat;

            if (args["near"] != null && HasNumber(args["near"]))
                camera.nearClipPlane = args["near"].AsFloat;

            if (args["far"] != null && HasNumber(args["far"]))
                camera.farClipPlane = args["far"].AsFloat;

            if (args["orthographic"] != null)
            {
                var node = args["orthographic"];
                if (node.type == JsonNodeType.Boolean)
                    camera.orthographic = node.AsBool;
            }

            if (args["ortho_size"] != null && HasNumber(args["ortho_size"]))
                camera.orthographicSize = args["ortho_size"].AsFloat;

            if (args["depth"] != null && HasNumber(args["depth"]))
                camera.depth = args["depth"].AsFloat;

            if (args["culling_mask"] != null && HasNumber(args["culling_mask"]))
                camera.cullingMask = args["culling_mask"].AsInt;

            if (args["enabled"] != null)
            {
                var node = args["enabled"];
                if (node.type == JsonNodeType.Boolean)
                    camera.enabled = node.AsBool;
            }

            var clearFlags = args["clear_flags"]?.Value;
            if (!string.IsNullOrWhiteSpace(clearFlags))
            {
                try
                {
                    camera.clearFlags = (CameraClearFlags)Enum.Parse(typeof(CameraClearFlags), clearFlags.Trim(), true);
                }
                catch (Exception)
                {
                    // 非法枚举值保持原设置
                }
            }

            var background = args["background"]?.Value;
            if (!string.IsNullOrWhiteSpace(background))
            {
                Color color;
                if (ColorUtility.TryParseHtmlString(background.Trim(), out color))
                    camera.backgroundColor = color;
            }
        }

        private static bool HasNumber(JsonNode node)
        {
            return node != null && (node.type == JsonNodeType.Float || node.type == JsonNodeType.Integer);
        }

        private static object DescribeCamera(Camera camera)
        {
            return new
            {
                game_object = camera.gameObject.name,
                path = BuildTransformPath(camera.transform),
                enabled = camera.enabled,
                position = new[] { camera.transform.position.x, camera.transform.position.y, camera.transform.position.z },
                rotation = new[] { camera.transform.eulerAngles.x, camera.transform.eulerAngles.y, camera.transform.eulerAngles.z },
                fov = camera.fieldOfView,
                near = camera.nearClipPlane,
                far = camera.farClipPlane,
                orthographic = camera.orthographic,
                ortho_size = camera.orthographicSize,
                depth = camera.depth,
                clear_flags = camera.clearFlags.ToString(),
                background = "#" + ColorUtility.ToHtmlStringRGBA(camera.backgroundColor),
                culling_mask = camera.cullingMask,
                is_main = camera.CompareTag("MainCamera")
            };
        }

        private static string BuildTransformPath(Transform transform)
        {
            if (transform == null)
                return null;

            var parts = new List<string>();
            var current = transform;
            while (current != null)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }

            return string.Join("/", parts.ToArray());
        }

        private static List<string> ReadStringArray(JsonClass args, string key)
        {
            var result = new List<string>();
            var array = args[key] as JsonArray;
            if (array == null)
                return result;

            for (int i = 0; i < array.Count; i++)
            {
                var value = array[i]?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value.Trim());
            }

            return result;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "camera";

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                var c = name[i];
                builder.Append(Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c);
            }

            return builder.ToString();
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Assets/Screenshots";

            path = path.Trim().Replace('\\', '/');
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return path.TrimEnd('/');

            return "Assets/" + path.TrimStart('/').TrimEnd('/');
        }

        private static string ToFullPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return assetPath;

            if (Path.IsPathRooted(assetPath))
                return assetPath;

            return Path.Combine(Directory.GetCurrentDirectory(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void EditorSceneDirty()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}

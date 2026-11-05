using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    /// <summary>
    /// 视觉特效编辑：LineRenderer / TrailRenderer 直接操作，VFX Graph（VisualEffect）通过反射驱动。
    /// 传统粒子系统请使用 edit_particle_system；本工具补的是线渲染、拖尾与 VFX Graph。
    /// 对应方法名: edit_effects
    /// </summary>
    [ToolName("edit_effects", "Effects", "特效")]
    public class EditEffects : StateMethodBase
    {
        private const string VisualEffectType = "UnityEngine.VFX.VisualEffect";

        public override string Description => L.T(
            "Create and edit LineRenderer and TrailRenderer effects, and drive VFX Graph components (parameters, events, playback)",
            "创建与编辑 LineRenderer、TrailRenderer 特效，并驱动 VFX Graph 组件（参数、事件、播放控制）");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T("Operation type", "操作类型"), false)
                    .SetEnumValues(
                        "create_line", "set_line_points", "set_line_style", "get_line_info",
                        "create_trail", "set_trail_style", "get_trail_info",
                        "vfx_list", "vfx_info", "vfx_set", "vfx_event", "vfx_control"),

                new MethodStr("name", L.T("GameObject name created by create_line / create_trail, or the target for other actions", "create_line / create_trail 创建的对象名，或其他操作的目标对象名"))
                    .AddExamples("Laser", "Trail"),

                new MethodStr("target", L.T("GameObject path or instance ID holding the LineRenderer, TrailRenderer or VisualEffect", "持有 LineRenderer、TrailRenderer 或 VisualEffect 的对象路径或 instanceID")),

                new MethodArr("points", L.T("Line points written as an array of [x, y, z] arrays", "线点位，格式为 [x, y, z] 数组的数组"), true, "array")
                    .AddExample("[0,0,0]", "[1,0,0]"),

                new MethodStr("mode", L.T("Point mode: replace, add, or clear", "点位模式：replace、add、clear"))
                    .SetEnumValues("replace", "add", "clear")
                    .SetDefault("replace"),

                new MethodColor("color", L.T("Primary color as #RRGGBB / #RRGGBBAA or [r, g, b, a]", "主颜色，格式 #RRGGBB / #RRGGBBAA 或 [r, g, b, a]"))
                    .SetDefault("#FFFFFFFF"),

                new MethodColor("start_color", L.T("Gradient start color", "渐变起始颜色")),
                new MethodColor("end_color", L.T("Gradient end color", "渐变结束颜色")),

                new MethodFloat("width", L.T("Line or trail width", "线或拖尾宽度"))
                    .SetRange(0f, 1000f),

                new MethodFloat("width_multiplier", L.T("Width multiplier", "宽度倍率"))
                    .SetRange(0f, 100f),

                new MethodStr("material", L.T("Material asset path assigned to the renderer", "赋给渲染器的材质资源路径"))
                    .AddExample("Assets/Materials/Line.mat"),

                new MethodBool("loop", L.T("Close the line loop", "闭合线条")),

                new MethodBool("world_space", L.T("Use world space positions", "使用世界空间坐标"))
                    .SetDefault(true),

                new MethodStr("texture_mode", L.T("Line texture mode: Stretch, Tile, DistributePerSegment, RepeatPerSegment", "线条纹理模式：Stretch、Tile、DistributePerSegment、RepeatPerSegment"))
                    .SetEnumValues("Stretch", "Tile", "DistributePerSegment", "RepeatPerSegment"),

                new MethodInt("corner_vertices", L.T("Number of corner vertices", "拐角顶点数量"))
                    .SetRange(0, 8),

                new MethodFloat("time", L.T("Trail lifetime in seconds", "拖尾存活时间（秒）"))
                    .SetRange(0.001f, 600f),

                new MethodFloat("min_vertex_distance", L.T("Minimum distance between trail vertices", "拖尾顶点最小间距"))
                    .SetRange(0f, 100f),

                new MethodBool("emitting", L.T("Enable trail emission", "启用拖尾发射")),

                new MethodBool("autodestruct", L.T("Destroy the GameObject when the trail finishes", "拖尾结束后销毁对象")),

                new MethodStr("parameter", L.T("VFX exposed property name", "VFX 暴露属性名"))
                    .AddExample("SpawnRate"),

                new MethodStr("value_type", L.T("VFX value type: float, int, bool, vector3, color", "VFX 值类型：float、int、bool、vector3、color"))
                    .SetEnumValues("float", "int", "bool", "vector3", "color")
                    .SetDefault("float"),

                new MethodFloat("value", L.T("Numeric value for VFX parameters", "VFX 参数的数值"))
                    .SetRange(-1000000f, 1000000f),

                new MethodBool("bool_value", L.T("Boolean value for VFX parameters", "VFX 参数的布尔值"))
                    .SetDefault(false),

                new MethodVector("vector", L.T("Vector3 value for VFX parameters", "VFX 参数的 Vector3 值"), true, 3),

                new MethodStr("event_name", L.T("VFX event name sent by vfx_event", "vfx_event 发送的 VFX 事件名"))
                    .AddExample("OnSpawn"),

                new MethodInt("event_id", L.T("VFX event id used when event_name is empty", "event_name 为空时使用的 VFX 事件 id"))
                    .SetRange(0, 1000),

                new MethodStr("control", L.T("VFX playback control: play, stop, pause, reinit", "VFX 播放控制：play、stop、pause、reinit"))
                    .SetEnumValues("play", "stop", "pause", "reinit")
                    .SetDefault("play")
            };
        }

        protected override StateTree CreateStateTree()
        {
            return StateTreeBuilder
                .Create()
                .Key("action")
                    .Leaf("create_line", CreateLine)
                    .Leaf("set_line_points", SetLinePoints)
                    .Leaf("set_line_style", SetLineStyle)
                    .Leaf("get_line_info", GetLineInfo)
                    .Leaf("create_trail", CreateTrail)
                    .Leaf("set_trail_style", SetTrailStyle)
                    .Leaf("get_trail_info", GetTrailInfo)
                    .Leaf("vfx_list", VfxList)
                    .Leaf("vfx_info", VfxInfo)
                    .Leaf("vfx_set", VfxSet)
                    .Leaf("vfx_event", VfxEvent)
                    .Leaf("vfx_control", VfxControl)
                .Build();
        }

        // ---------------- LineRenderer ----------------

        private object CreateLine(JsonClass args)
        {
            var name = (args["name"]?.Value ?? "Line").Trim();
            if (string.IsNullOrEmpty(name))
                name = "Line";

            try
            {
                var gameObject = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(gameObject, "Create Line");

                var line = gameObject.AddComponent<LineRenderer>();
                line.useWorldSpace = args["world_space"].AsBoolDefault(true);
                line.loop = args["loop"].AsBoolDefault(false);

                var points = ReadPoints(args, "points");
                if (points.Count == 0)
                {
                    points.Add(Vector3.zero);
                    points.Add(Vector3.right);
                }

                line.positionCount = points.Count;
                line.SetPositions(points.ToArray());

                ApplyLineStyle(line, args);
                Selection.activeGameObject = gameObject;
                EditorSceneDirty();

                return Response.Success("LineRenderer created.", DescribeLine(line));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to create LineRenderer: " + e.Message);
            }
        }

        private object SetLinePoints(JsonClass args)
        {
            LineRenderer line;
            var error = ResolveLine(args, out line);
            if (error != null)
                return error;

            var mode = (args["mode"]?.Value ?? "replace").Trim().ToLowerInvariant();

            try
            {
                Undo.RecordObject(line, "Set Line Points");

                if (mode == "clear")
                {
                    line.positionCount = 0;
                }
                else
                {
                    var points = ReadPoints(args, "points");
                    if (points.Count == 0)
                        return Response.Error("'points' is required unless mode is clear.");

                    if (mode == "add")
                    {
                        var existing = new List<Vector3>(line.positionCount);
                        for (int i = 0; i < line.positionCount; i++)
                            existing.Add(line.GetPosition(i));
                        existing.AddRange(points);
                        line.positionCount = existing.Count;
                        line.SetPositions(existing.ToArray());
                    }
                    else
                    {
                        line.positionCount = points.Count;
                        line.SetPositions(points.ToArray());
                    }
                }

                EditorUtility.SetDirty(line);
                EditorSceneDirty();
                return Response.Success("Line points updated.", DescribeLine(line));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to set line points: " + e.Message);
            }
        }

        private object SetLineStyle(JsonClass args)
        {
            LineRenderer line;
            var error = ResolveLine(args, out line);
            if (error != null)
                return error;

            try
            {
                Undo.RecordObject(line, "Set Line Style");
                ApplyLineStyle(line, args);
                EditorUtility.SetDirty(line);
                EditorSceneDirty();
                return Response.Success("Line style updated.", DescribeLine(line));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to set line style: " + e.Message);
            }
        }

        private object GetLineInfo(JsonClass args)
        {
            LineRenderer line;
            var error = ResolveLine(args, out line);
            if (error != null)
                return error;

            return Response.Success("LineRenderer info retrieved.", DescribeLine(line));
        }

        private static void ApplyLineStyle(LineRenderer line, JsonClass args)
        {
            if (args["width"] != null && IsNumber(args["width"]))
                line.widthMultiplier = args["width"].AsFloat;

            if (args["width_multiplier"] != null && IsNumber(args["width_multiplier"]))
                line.widthMultiplier = args["width_multiplier"].AsFloat;

            if (args["loop"] != null && args["loop"].type == JsonNodeType.Boolean)
                line.loop = args["loop"].AsBool;

            if (args["world_space"] != null && args["world_space"].type == JsonNodeType.Boolean)
                line.useWorldSpace = args["world_space"].AsBool;

            if (args["corner_vertices"] != null && IsNumber(args["corner_vertices"]))
                line.numCornerVertices = Mathf.Clamp(args["corner_vertices"].AsInt, 0, 8);

            var textureMode = args["texture_mode"]?.Value;
            if (!string.IsNullOrWhiteSpace(textureMode))
            {
                try
                {
                    line.textureMode = (LineTextureMode)Enum.Parse(typeof(LineTextureMode), textureMode.Trim(), true);
                }
                catch (Exception)
                {
                    // 非法枚举值忽略
                }
            }

            var startColor = ReadColor(args, "start_color");
            var endColor = ReadColor(args, "end_color");
            var mainColor = ReadColor(args, "color");

            if (mainColor.HasValue)
            {
                line.startColor = mainColor.Value;
                line.endColor = mainColor.Value;
            }

            if (startColor.HasValue)
                line.startColor = startColor.Value;
            if (endColor.HasValue)
                line.endColor = endColor.Value;

            AssignMaterial(line, args["material"]?.Value);
        }

        private object DescribeLine(LineRenderer line)
        {
            var positions = new List<float[]>();
            for (int i = 0; i < line.positionCount && i < 200; i++)
            {
                var p = line.GetPosition(i);
                positions.Add(new[] { p.x, p.y, p.z });
            }

            var material = line.sharedMaterial;

            return new
            {
                game_object = line.gameObject.name,
                position_count = line.positionCount,
                positions = positions,
                loop = line.loop,
                use_world_space = line.useWorldSpace,
                width = line.widthMultiplier,
                start_color = "#" + ColorUtility.ToHtmlStringRGBA(line.startColor),
                end_color = "#" + ColorUtility.ToHtmlStringRGBA(line.endColor),
                texture_mode = line.textureMode.ToString(),
                material = material != null ? AssetDatabase.GetAssetPath(material) : null
            };
        }

        // ---------------- TrailRenderer ----------------

        private object CreateTrail(JsonClass args)
        {
            var name = (args["name"]?.Value ?? "Trail").Trim();
            if (string.IsNullOrEmpty(name))
                name = "Trail";

            try
            {
                var gameObject = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(gameObject, "Create Trail");

                var trail = gameObject.AddComponent<TrailRenderer>();
                ApplyTrailStyle(trail, args);

                Selection.activeGameObject = gameObject;
                EditorSceneDirty();

                return Response.Success("TrailRenderer created.", DescribeTrail(trail));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to create TrailRenderer: " + e.Message);
            }
        }

        private object SetTrailStyle(JsonClass args)
        {
            TrailRenderer trail;
            var error = ResolveTrail(args, out trail);
            if (error != null)
                return error;

            try
            {
                Undo.RecordObject(trail, "Set Trail Style");
                ApplyTrailStyle(trail, args);
                EditorUtility.SetDirty(trail);
                EditorSceneDirty();
                return Response.Success("Trail style updated.", DescribeTrail(trail));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to set trail style: " + e.Message);
            }
        }

        private object GetTrailInfo(JsonClass args)
        {
            TrailRenderer trail;
            var error = ResolveTrail(args, out trail);
            if (error != null)
                return error;

            return Response.Success("TrailRenderer info retrieved.", DescribeTrail(trail));
        }

        private static void ApplyTrailStyle(TrailRenderer trail, JsonClass args)
        {
            if (args["time"] != null && IsNumber(args["time"]))
                trail.time = args["time"].AsFloat;

            if (args["min_vertex_distance"] != null && IsNumber(args["min_vertex_distance"]))
                trail.minVertexDistance = args["min_vertex_distance"].AsFloat;

            if (args["width"] != null && IsNumber(args["width"]))
            {
                trail.startWidth = args["width"].AsFloat;
                trail.endWidth = args["width"].AsFloat;
            }

            if (args["width_multiplier"] != null && IsNumber(args["width_multiplier"]))
                trail.widthMultiplier = args["width_multiplier"].AsFloat;

            if (args["emitting"] != null && args["emitting"].type == JsonNodeType.Boolean)
                trail.emitting = args["emitting"].AsBool;

            if (args["autodestruct"] != null && args["autodestruct"].type == JsonNodeType.Boolean)
                trail.autodestruct = args["autodestruct"].AsBool;

            var startColor = ReadColor(args, "start_color");
            var endColor = ReadColor(args, "end_color");
            var mainColor = ReadColor(args, "color");

            if (mainColor.HasValue)
            {
                trail.startColor = mainColor.Value;
                trail.endColor = mainColor.Value;
            }

            if (startColor.HasValue)
                trail.startColor = startColor.Value;
            if (endColor.HasValue)
                trail.endColor = endColor.Value;

            AssignMaterial(trail, args["material"]?.Value);
        }

        private object DescribeTrail(TrailRenderer trail)
        {
            var material = trail.sharedMaterial;
            return new
            {
                game_object = trail.gameObject.name,
                time = trail.time,
                min_vertex_distance = trail.minVertexDistance,
                start_width = trail.startWidth,
                end_width = trail.endWidth,
                width_multiplier = trail.widthMultiplier,
                emitting = trail.emitting,
                autodestruct = trail.autodestruct,
                position_count = trail.positionCount,
                start_color = "#" + ColorUtility.ToHtmlStringRGBA(trail.startColor),
                end_color = "#" + ColorUtility.ToHtmlStringRGBA(trail.endColor),
                material = material != null ? AssetDatabase.GetAssetPath(material) : null
            };
        }

        // ---------------- VFX Graph ----------------

        private object VfxList(JsonClass args)
        {
            Type type;
            var error = ResolveVfxType(out type);
            if (error != null)
                return error;

            var components = UnityEngine.Object.FindObjectsOfType(type);
            var items = new List<object>();
            for (int i = 0; i < components.Length && i < 100; i++)
            {
                var component = components[i] as Component;
                if (component != null)
                    items.Add(DescribeVfx(component));
            }

            return Response.Success("VFX components listed.", new { count = items.Count, items = items });
        }

        private object VfxInfo(JsonClass args)
        {
            Component component;
            var error = ResolveVfxComponent(args, out component);
            if (error != null)
                return error;

            return Response.Success("VFX info retrieved.", DescribeVfx(component));
        }

        private object VfxSet(JsonClass args)
        {
            Component component;
            var error = ResolveVfxComponent(args, out component);
            if (error != null)
                return error;

            var parameter = (args["parameter"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(parameter))
                return Response.Error("'parameter' is required for vfx_set.");

            var valueType = (args["value_type"]?.Value ?? "float").Trim().ToLowerInvariant();

            try
            {
                object value;
                string method;

                switch (valueType)
                {
                    case "int":
                    case "integer":
                        method = "SetInt";
                        value = args["value"].AsIntDefault(0);
                        break;
                    case "bool":
                    case "boolean":
                        method = "SetBool";
                        value = args["bool_value"].AsBoolDefault(false);
                        break;
                    case "vector3":
                    case "vector":
                        method = "SetVector3";
                        var vector = GameObjectUtils.ParseVector3(args["vector"] as JsonArray);
                        if (!vector.HasValue)
                            return Response.Error("'vector' is required when value_type is vector3.");
                        value = vector.Value;
                        break;
                    case "color":
                        method = "SetColor";
                        var color = ReadColor(args, "color");
                        if (!color.HasValue)
                            return Response.Error("'color' is required when value_type is color.");
                        value = color.Value;
                        break;
                    default:
                        method = "SetFloat";
                        value = args["value"].AsFloatDefault(0f);
                        break;
                }

                OptionalApi.Call(component, method, parameter, value);
                EditorUtility.SetDirty(component);

                return Response.Success("VFX parameter set.", new
                {
                    game_object = component.gameObject.name,
                    parameter = parameter,
                    value_type = valueType,
                    value = value?.ToString()
                });
            }
            catch (Exception e)
            {
                return Response.Error("Failed to set VFX parameter: " + OptionalApi.Unwrap(e));
            }
        }

        private object VfxEvent(JsonClass args)
        {
            Component component;
            var error = ResolveVfxComponent(args, out component);
            if (error != null)
                return error;

            var eventName = (args["event_name"]?.Value ?? "").Trim();

            try
            {
                if (!string.IsNullOrEmpty(eventName))
                {
                    OptionalApi.Call(component, "SendEvent", eventName);
                    return Response.Success("VFX event sent.", new { game_object = component.gameObject.name, event_name = eventName });
                }

                var eventId = args["event_id"].AsIntDefault(0);
                OptionalApi.Call(component, "SendEvent", eventId);
                return Response.Success("VFX event sent.", new { game_object = component.gameObject.name, event_id = eventId });
            }
            catch (Exception e)
            {
                return Response.Error("Failed to send VFX event: " + OptionalApi.Unwrap(e));
            }
        }

        private object VfxControl(JsonClass args)
        {
            Component component;
            var error = ResolveVfxComponent(args, out component);
            if (error != null)
                return error;

            var control = (args["control"]?.Value ?? "play").Trim().ToLowerInvariant();
            string method;
            switch (control)
            {
                case "stop":
                    method = "Stop";
                    break;
                case "pause":
                    method = "Pause";
                    break;
                case "reinit":
                    method = "Reinit";
                    break;
                default:
                    method = "Play";
                    break;
            }

            try
            {
                OptionalApi.Call(component, method);
                return Response.Success("VFX control executed: " + method + ".", DescribeVfx(component));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to control VFX: " + OptionalApi.Unwrap(e));
            }
        }

        private object ResolveVfxType(out Type type)
        {
            type = OptionalApi.FindType(VisualEffectType);
            if (type == null)
                return Response.Error("VFX Graph is not available. Install 'com.unity.visualeffectgraph' to use VisualEffect components.");

            return null;
        }

        private object ResolveVfxComponent(JsonClass args, out Component component)
        {
            component = null;
            Type type;
            var error = ResolveVfxType(out type);
            if (error != null)
                return error;

            var target = (args["target"]?.Value ?? args["name"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(target))
            {
                var any = UnityEngine.Object.FindObjectOfType(type);
                component = any as Component;
            }
            else
            {
                var gameObject = GameObjectUtils.FindObjectByIdOrPath(target);
                if (gameObject != null)
                    component = gameObject.GetComponent(type);
            }

            if (component == null)
                return Response.Error("VisualEffect component not found. Provide 'target' with the GameObject path or instance ID.");

            return null;
        }

        private static object DescribeVfx(Component component)
        {
            var asset = OptionalApi.Get(component, "visualEffectAsset") as UnityEngine.Object;
            return new
            {
                game_object = component.gameObject.name,
                type = component.GetType().Name,
                asset = asset != null ? AssetDatabase.GetAssetPath(asset) : null,
                play_rate = OptionalApi.Get(component, "playRate"),
                start_seed = OptionalApi.Get(component, "startSeed"),
                alive_particle_count = OptionalApi.Get(component, "aliveParticleCount"),
                pause = OptionalApi.Get(component, "pause")
            };
        }

        // ---------------- 辅助 ----------------

        private object ResolveLine(JsonClass args, out LineRenderer line)
        {
            line = null;
            var gameObject = ResolveGameObject(args);
            if (gameObject != null)
                line = gameObject.GetComponent<LineRenderer>();

            if (line == null)
                return Response.Error("LineRenderer not found. Provide 'target' or 'name' with the GameObject path.");

            return null;
        }

        private object ResolveTrail(JsonClass args, out TrailRenderer trail)
        {
            trail = null;
            var gameObject = ResolveGameObject(args);
            if (gameObject != null)
                trail = gameObject.GetComponent<TrailRenderer>();

            if (trail == null)
                return Response.Error("TrailRenderer not found. Provide 'target' or 'name' with the GameObject path.");

            return null;
        }

        private static GameObject ResolveGameObject(JsonClass args)
        {
            var target = (args["target"]?.Value ?? args["name"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(target))
                return null;

            return GameObjectUtils.FindObjectByIdOrPath(target);
        }

        private static List<Vector3> ReadPoints(JsonClass args, string key)
        {
            var result = new List<Vector3>();
            var array = args[key] as JsonArray;
            if (array == null)
                return result;

            for (int i = 0; i < array.Count; i++)
            {
                var point = GameObjectUtils.ParseVector3(array[i] as JsonArray);
                if (point.HasValue)
                    result.Add(point.Value);
            }

            return result;
        }

        private static Color? ReadColor(JsonClass args, string key)
        {
            var node = args[key];
            if (node == null)
                return null;

            var array = node as JsonArray;
            if (array != null)
            {
                if (array.Count >= 3)
                {
                    var r = array[0].AsFloatDefault(1f);
                    var g = array[1].AsFloatDefault(1f);
                    var b = array[2].AsFloatDefault(1f);
                    var a = array.Count >= 4 ? array[3].AsFloatDefault(1f) : 1f;
                    return new Color(r, g, b, a);
                }

                return null;
            }

            var text = node.Value;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            Color parsed;
            if (ColorUtility.TryParseHtmlString(text.Trim(), out parsed))
                return parsed;

            return null;
        }

        private static void AssignMaterial(Renderer renderer, string materialPath)
        {
            if (renderer == null)
                return;

            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                var path = materialPath.Trim().Replace('\\', '/');
                if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    path = "Assets/" + path.TrimStart('/');

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material != null)
                {
                    renderer.sharedMaterial = material;
                    return;
                }
            }

            if (renderer.sharedMaterial == null)
            {
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (shader != null)
                    renderer.sharedMaterial = new Material(shader);
            }
        }

        private static bool IsNumber(JsonNode node)
        {
            return node != null && (node.type == JsonNodeType.Float || node.type == JsonNodeType.Integer);
        }

        private static void EditorSceneDirty()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}

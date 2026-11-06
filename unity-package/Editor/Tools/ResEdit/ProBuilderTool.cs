using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    /// <summary>
    /// ProBuilder 建模：通过反射访问 ProBuilder 包，未安装时给出明确提示，且不影响包本身编译。
    /// 对应方法名: edit_probuilder
    /// </summary>
    [ToolModule("probuilder", GroupEn = "Modeling", GroupZh = "建模")]
    public class ProBuilderTool : ToolModuleBase
    {
        private const string MeshType = "UnityEngine.ProBuilder.ProBuilderMesh";
        private const string ShapeGeneratorType = "UnityEngine.ProBuilder.ShapeGenerator";

        public override string Description => L.T(
            "Create ProBuilder shapes and edit their topology (extrude, delete faces) when com.unity.probuilder is installed",
            "在安装了 com.unity.probuilder 时创建 ProBuilder 形状并编辑拓扑（挤出、删除面）");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T("Operation type", "操作类型"), false)
                    .SetEnumValues("status", "create_shape", "get_mesh_info", "extrude_faces", "delete_faces", "set_smoothing"),

                new MethodStr("name", L.T("GameObject name for the created shape", "创建的形状对象名"))
                    .AddExample("PBCube"),

                new MethodStr("shape", L.T("ProBuilder shape: Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Prism, Arch, Stairs, Door", "ProBuilder 形状：Cube、Cylinder、Sphere、Plane、Cone、Torus、Pipe、Prism、Arch、Stairs、Door"))
                    .AddExamples("Cube", "Cylinder")
                    .SetDefault("Cube"),

                new MethodStr("target", L.T("GameObject path or instance ID holding the ProBuilderMesh", "持有 ProBuilderMesh 的对象路径或 instanceID")),

                new MethodVector("position", L.T("World position", "世界坐标"), true, 3),
                new MethodVector("rotation", L.T("World Euler rotation", "世界欧拉旋转"), true, 3),
                new MethodVector("size", L.T("Local scale applied to the created shape", "创建形状时应用的本地缩放"), true, 3),

                new MethodStr("pivot", L.T("Pivot placement: Center, FirstVertex", "轴心位置：Center、FirstVertex"))
                    .SetEnumValues("Center", "FirstVertex")
                    .SetDefault("Center"),

                new MethodStr("extrude_method", L.T("Extrude method: FaceNormal, IndividualFaces, VertexNormal", "挤出方式：FaceNormal、IndividualFaces、VertexNormal"))
                    .SetEnumValues("FaceNormal", "IndividualFaces", "VertexNormal")
                    .SetDefault("FaceNormal"),

                new MethodFloat("distance", L.T("Extrude distance", "挤出距离"))
                    .SetRange(-100f, 100f),

                new MethodArr("face_indices", L.T("Face indices to modify; omit to target every face", "要修改的面索引；省略时作用于全部面"), true, "integer"),

                new MethodBool("smoothing", L.T("Enable smooth shading for the selected faces", "为选中面启用平滑着色"))
                    .SetDefault(true)
            };
        }

        [ToolAction("status", Inject = new string[] { "action=status" })]
        private object Status(JsonClass args)
        {
            Type meshType = OptionalApi.FindType("UnityEngine.ProBuilder.ProBuilderMesh");
            Type generatorType = OptionalApi.FindType("UnityEngine.ProBuilder.ShapeGenerator");
            bool available = meshType != null && generatorType != null;
            string version = ReadPackageVersion("com.unity.probuilder");
            return Response.Success(available ? "ProBuilder is available." : "ProBuilder is not installed.", new
            {
                installed = available,
                version = version,
                hint = (available ? null : "Add 'com.unity.probuilder' from Package Manager to enable these actions.")
            });
        }

        [ToolAction("create_shape", Parameters = new string[] { "name", "shape", "position", "rotation", "size", "pivot" }, Inject = new string[] { "action=create_shape" })]
        private object CreateShape(JsonClass args)
        {
            Type meshType;
            Type generatorType;
            var error = ResolveTypes(out meshType, out generatorType);
            if (error != null)
                return error;

            var shapeName = (args["shape"]?.Value ?? "Cube").Trim();
            var shapeType = OptionalApi.FindType("UnityEngine.ProBuilder.ShapeType");
            var pivotType = OptionalApi.FindType("UnityEngine.ProBuilder.PivotLocation");
            if (shapeType == null || pivotType == null)
                return Response.Error("ProBuilder shape enums were not found; the package version may be incompatible.");

            var shape = OptionalApi.ParseEnum(shapeType, shapeName);
            if (shape == null)
                return Response.Error("Unknown ProBuilder shape: " + shapeName);

            var pivot = OptionalApi.ParseEnum(pivotType, (args["pivot"]?.Value ?? "Center").Trim());
            if (pivot == null)
                pivot = OptionalApi.ParseEnum(pivotType, "Center");

            try
            {
                var mesh = OptionalApi.CallStatic(generatorType, "CreateShape", pivot, shape);
                if (mesh == null)
                    return Response.Error("ProBuilder failed to create the shape.");

                var component = mesh as Component;
                if (component == null)
                    return Response.Error("ProBuilder returned an unexpected object type.");

                var gameObject = component.gameObject;
                Undo.RegisterCreatedObjectUndo(gameObject, "Create ProBuilder Shape");

                var name = (args["name"]?.Value ?? "").Trim();
                if (!string.IsNullOrEmpty(name))
                    gameObject.name = name;

                var position = GameObjectUtils.ParseVector3(args["position"] as JsonArray);
                if (position.HasValue)
                    gameObject.transform.position = position.Value;

                var rotation = GameObjectUtils.ParseVector3(args["rotation"] as JsonArray);
                if (rotation.HasValue)
                    gameObject.transform.eulerAngles = rotation.Value;

                var size = GameObjectUtils.ParseVector3(args["size"] as JsonArray);
                if (size.HasValue)
                    gameObject.transform.localScale = size.Value;

                EditorSceneDirty();
                Selection.activeGameObject = gameObject;

                return Response.Success("ProBuilder shape created.", DescribeMesh(mesh, gameObject));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to create ProBuilder shape: " + OptionalApi.Unwrap(e));
            }
        }

        [ToolAction("get_mesh_info", Parameters = new string[] { "name", "target" }, Inject = new string[] { "action=get_mesh_info" })]
        private object GetMeshInfo(JsonClass args)
        {
            object mesh;
            GameObject gameObject;
            var error = ResolveMesh(args, out mesh, out gameObject);
            if (error != null)
                return error;

            return Response.Success("ProBuilder mesh info retrieved.", DescribeMesh(mesh, gameObject));
        }

        [ToolAction("extrude_faces", Parameters = new string[] { "name", "target", "extrude_method", "distance", "face_indices" }, Inject = new string[] { "action=extrude_faces" })]
        private object ExtrudeFaces(JsonClass args)
        {
            object mesh;
            GameObject gameObject;
            var error = ResolveMesh(args, out mesh, out gameObject);
            if (error != null)
                return error;

            var distance = args["distance"].AsFloatDefault(0.2f);
            if (Mathf.Approximately(distance, 0f))
                return Response.Error("'distance' must not be zero for extrude_faces.");

            var methodName = (args["extrude_method"]?.Value ?? "FaceNormal").Trim();
            var extrudeMethodType = OptionalApi.FindType("UnityEngine.ProBuilder.ExtrudeMethod");
            if (extrudeMethodType == null)
                return Response.Error("ExtrudeMethod was not found; the ProBuilder version may be incompatible.");

            var method = OptionalApi.ParseEnum(extrudeMethodType, methodName);
            if (method == null)
                return Response.Error("Unknown extrude method: " + methodName);

            try
            {
                var faces = ReadFaces(mesh, args);
                if (faces == null || faces.Count == 0)
                    return Response.Error("No faces selected for extrusion.");

                // ProBuilder 的挤出在不同版本里可能是实例方法，也可能是 MeshOperations 的扩展方法
                object ignored;
                if (!OptionalApi.TryCall(mesh, "Extrude", out ignored, faces, method, distance))
                {
                    var operationsType = OptionalApi.FindType("UnityEngine.ProBuilder.MeshOperations.ExtrudeElements");
                    if (operationsType == null)
                        return Response.Error("Extrude is unavailable in this ProBuilder version.");

                    OptionalApi.CallStatic(operationsType, "Extrude", mesh, faces, method, distance);
                }

                OptionalApi.Call(mesh, "ToMesh");
                OptionalApi.Call(mesh, "Refresh");
                EditorUtility.SetDirty((UnityEngine.Object)mesh);
                EditorSceneDirty();

                return Response.Success("Faces extruded.", DescribeMesh(mesh, gameObject));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to extrude faces: " + OptionalApi.Unwrap(e));
            }
        }

        [ToolAction("delete_faces", Parameters = new string[] { "name", "target", "face_indices" }, Inject = new string[] { "action=delete_faces" })]
        private object DeleteFaces(JsonClass args)
        {
            object mesh;
            GameObject gameObject;
            var error = ResolveMesh(args, out mesh, out gameObject);
            if (error != null)
                return error;

            try
            {
                var faces = ReadFaces(mesh, args);
                if (faces == null || faces.Count == 0)
                    return Response.Error("No faces selected for deletion.");

                object ignored;
                if (!OptionalApi.TryCall(mesh, "DeleteFaces", out ignored, faces))
                {
                    var deleteType = OptionalApi.FindType("UnityEngine.ProBuilder.MeshOperations.DeleteElements");
                    if (deleteType == null)
                        return Response.Error("DeleteFaces is unavailable in this ProBuilder version.");

                    OptionalApi.CallStatic(deleteType, "DeleteFaces", mesh, faces);
                }

                OptionalApi.Call(mesh, "ToMesh");
                OptionalApi.Call(mesh, "Refresh");
                EditorUtility.SetDirty((UnityEngine.Object)mesh);
                EditorSceneDirty();

                return Response.Success("Faces deleted.", DescribeMesh(mesh, gameObject));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to delete faces: " + OptionalApi.Unwrap(e));
            }
        }

        [ToolAction("set_smoothing", Parameters = new string[] { "name", "target", "face_indices", "smoothing" }, Inject = new string[] { "action=set_smoothing" })]
        private object SetSmoothing(JsonClass args)
        {
            object mesh;
            GameObject gameObject;
            var error = ResolveMesh(args, out mesh, out gameObject);
            if (error != null)
                return error;

            var smoothing = args["smoothing"].AsBoolDefault(true);

            try
            {
                var faces = ReadFaces(mesh, args);
                if (faces == null || faces.Count == 0)
                    return Response.Error("No faces selected for smoothing.");

                for (int i = 0; i < faces.Count; i++)
                    OptionalApi.Set(faces[i], "smoothingGroup", smoothing ? 1 : 0);

                OptionalApi.Call(mesh, "ToMesh");
                OptionalApi.Call(mesh, "Refresh");
                EditorUtility.SetDirty((UnityEngine.Object)mesh);
                EditorSceneDirty();

                return Response.Success("Smoothing updated.", DescribeMesh(mesh, gameObject));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to set smoothing: " + OptionalApi.Unwrap(e));
            }
        }

        // ---------------- 辅助 ----------------

        private object ResolveTypes(out Type meshType, out Type generatorType)
        {
            meshType = OptionalApi.FindType(MeshType);
            generatorType = OptionalApi.FindType(ShapeGeneratorType);

            if (meshType == null || generatorType == null)
                return Response.Error("ProBuilder is not installed. Add 'com.unity.probuilder' from Package Manager first.");

            return null;
        }

        private object ResolveMesh(JsonClass args, out object mesh, out GameObject gameObject)
        {
            mesh = null;
            gameObject = null;

            Type meshType;
            Type generatorType;
            var error = ResolveTypes(out meshType, out generatorType);
            if (error != null)
                return error;

            var target = (args["target"]?.Value ?? args["name"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(target))
                return Response.Error("'target' is required to locate a ProBuilder mesh.");

            gameObject = GameObjectUtils.FindObjectByIdOrPath(target);
            if (gameObject == null)
                return Response.Error("GameObject not found: " + target);

            mesh = gameObject.GetComponent(meshType);
            if (mesh == null)
                return Response.Error("GameObject has no ProBuilderMesh: " + target);

            return null;
        }

        /// <summary>按 face_indices 或全部面构造 ProBuilder Face 列表（用 IList&lt;Face&gt; 反射传递给包 API）。</summary>
        private static IList ReadFaces(object mesh, JsonClass args)
        {
            var faceType = OptionalApi.FindType("UnityEngine.ProBuilder.Face");
            if (faceType == null)
                return null;

            var allFaces = OptionalApi.Get(mesh, "faces") as IEnumerable;
            if (allFaces == null)
                return null;

            var listType = typeof(List<>).MakeGenericType(faceType);
            var list = (IList)Activator.CreateInstance(listType);

            var indices = ReadIntArray(args, "face_indices");
            var indexSet = new HashSet<int>(indices);

            var index = 0;
            foreach (var face in allFaces)
            {
                if (indexSet.Count == 0 || indexSet.Contains(index))
                    list.Add(face);

                index++;
                if (index > 4096)
                    break;
            }

            return list;
        }

        private static object DescribeMesh(object mesh, GameObject gameObject)
        {
            return new
            {
                game_object = gameObject != null ? gameObject.name : null,
                vertex_count = ReadInt(mesh, "vertexCount"),
                face_count = ReadInt(mesh, "faceCount"),
                edge_count = ReadInt(mesh, "edgeCount"),
                triangle_count = ReadInt(mesh, "triangleCount"),
                position = gameObject != null
                    ? new[] { gameObject.transform.position.x, gameObject.transform.position.y, gameObject.transform.position.z }
                    : null
            };
        }

        private static int ReadInt(object target, string name)
        {
            var value = OptionalApi.Get(target, name);
            if (value == null)
                return 0;

            try
            {
                return Convert.ToInt32(value);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static List<int> ReadIntArray(JsonClass args, string key)
        {
            var result = new List<int>();
            var array = args[key] as JsonArray;
            if (array == null)
                return result;

            for (int i = 0; i < array.Count; i++)
            {
                var node = array[i];
                if (node == null)
                    continue;

                int value;
                if (int.TryParse(node.Value, out value))
                    result.Add(value);
            }

            return result;
        }

        private static string ReadPackageVersion(string packageName)
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + packageName);
                return info != null ? info.version : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void EditorSceneDirty()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}

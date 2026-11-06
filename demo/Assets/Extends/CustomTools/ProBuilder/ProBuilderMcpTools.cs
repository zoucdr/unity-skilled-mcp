/*-*-* Copyright (c) uframe@zoucdr
 * Description: ProBuilder MCP 工具（UniMcp ToolModuleBase 框架）。
 * 由原 ProBuilder HTTP 技能服务的路由直接移植，业务逻辑保持一致。
 *_*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
using UniMcp;
using UniMcp.Models;
using static ProBuilder.SkilledMcp.ProBuilderMcpUtil;

namespace ProBuilder.SkilledMcp
{
    /// <summary>
    /// ProBuilder 网格创建与编辑工具：创建形状、挤出、删除面、倒角、桥接、连接/细分边、
    /// 翻转法线、合并对象、布尔运算、设置面材质/轴心、查询网格信息。
    /// </summary>
    [ToolModule("probuilder", GroupEn = "Assets", GroupZh = "资源资产")]
    public class ProBuilderMcpTools : ToolModuleBase
    {
        public override string Description => L.T(
            "ProBuilder mesh creation and editing: create primitives and polygon shapes, extrude/delete faces, bevel/bridge/connect/subdivide edges, flip normals, merge objects, CSG boolean, set face material or pivot, and inspect mesh info.",
            "ProBuilder 网格创建与编辑：创建图元与多边形形状、挤出面、删除面、边倒角/桥接/连接/细分、翻转法线、合并对象、CSG 布尔运算、设置面材质或轴心、查询网格信息。");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("gameObject", L.T("Target GameObject name or hierarchy path", "目标对象名称或层级路径")),
                new MethodInt("instanceId", L.T("Target GameObject instance ID (alternative to gameObject)", "目标对象 instanceId（与 gameObject 二选一）")),

                new MethodStr("shapeType", L.T("Primitive shape type: Cube, Cylinder, Sphere, Plane, Prism, Cone, Stair, Door, Pipe, Arch, Sprite, Torus", "图元类型：Cube/Cylinder/Sphere/Plane/Prism/Cone/Stair/Door/Pipe/Arch/Sprite/Torus"))
                    .SetEnumValues("Cube", "Cylinder", "Sphere", "Plane", "Prism", "Cone", "Stair", "Door", "Pipe", "Arch", "Sprite", "Torus"),
                new MethodStr("name", L.T("Object name (default 'ProBuilder {shapeType}')", "对象名称（缺省 'ProBuilder {shapeType}'）")),
                new MethodStr("parent", L.T("Parent object name or hierarchy path", "父对象名称或层级路径")),
                new MethodVector("position", L.T("Position", "位置"), true, 3),
                new MethodVector("rotation", L.T("Euler rotation", "欧拉角旋转"), true, 3),
                new MethodVector("scale", L.T("Scale", "缩放"), true, 3),
                new MethodVector("size", L.T("Shape width/height/depth applied by scaling vertices", "形状宽高深（通过缩放顶点实现）"), true, 3),
                new MethodBool("isLocalSpace", L.T("Interpret position/rotation/scale in parent local space", "按父对象局部坐标解释 position/rotation/scale"))
                    .SetDefault(false),

                new MethodArr("points", L.T("Polygon vertices [[x,z],[x,z],...] (at least 3) or 'x,z;x,z;...'", "多边形顶点 [[x,z],[x,z],...]（至少 3 点）或 'x,z;x,z;...'"), true, "number"),
                new MethodFloat("height", L.T("Extrusion height along Y", "沿 Y 轴拉伸高度"))
                    .SetDefault(1f),
                new MethodBool("flipNormals", L.T("Flip normals (faces point inward)", "翻转法线（面朝内）"))
                    .SetDefault(false),

                new MethodArr("faceIndices", L.T("Face index array, e.g. [0,2,4]", "面索引数组，如 [0,2,4]"), true, "integer"),
                new MethodStr("faceDirection", L.T("Faces by direction: up/down/left/right/forward/back", "按方向选面：up/down/left/right/forward/back"))
                    .SetEnumValues("up", "down", "left", "right", "forward", "back"),

                new MethodFloat("distance", L.T("Extrude distance", "挤出距离"))
                    .SetDefault(0.5f),
                new MethodStr("extrudeMethod", L.T("Extrude method: IndividualFaces, FaceNormal, VertexNormal", "挤出方式：IndividualFaces/FaceNormal/VertexNormal"))
                    .SetEnumValues("IndividualFaces", "FaceNormal", "VertexNormal")
                    .SetDefault("FaceNormal"),

                new MethodArr("edges", L.T("Edge list [[a,b],[c,d],...] or 'a-b,c-d'", "边列表 [[a,b],[c,d],...] 或 'a-b,c-d'"), true, "integer"),
                new MethodFloat("amount", L.T("Bevel amount 0.001-0.999 (0.05-0.2 recommended)", "倒角强度 0.001~0.999（建议 0.05~0.2）"))
                    .SetDefault(0.1f)
                    .SetRange(0.001f, 0.999f),
                new MethodArr("edgeA", L.T("First edge [a,b] or 'a-b'", "第一条边 [a,b] 或 'a-b'"), true, "integer"),
                new MethodArr("edgeB", L.T("Second edge [a,b] or 'a-b'", "第二条边 [a,b] 或 'a-b'"), true, "integer"),
                new MethodBool("allowNonManifold", L.T("Allow creating non-manifold geometry", "允许创建非流形几何"))
                    .SetDefault(false),
                new MethodInt("subdivisions", L.T("Subdivisions per edge (1 splits the edge in two)", "每条边细分数（1=一分为二）"))
                    .SetDefault(1)
                    .SetRange(1, 100),

                new MethodArr("gameObjects", L.T("Object names to merge; the first is the target", "待合并对象名称数组，第一个为合并目标"), true, "string"),
                new MethodArr("instanceIds", L.T("Object instance IDs to merge (alternative to gameObjects)", "待合并对象 instanceId 数组（与 gameObjects 二选一）"), true, "integer"),
                new MethodBool("deleteSourceObjects", L.T("Delete source objects after merging (or after boolean)", "合并（或布尔运算）后删除源对象"))
                    .SetDefault(true),

                new MethodStr("materialPath", L.T("Material asset path (Assets/Materials/Grass.mat) or material name", "材质资源路径（Assets/Materials/Grass.mat）或材质名称")),
                new MethodStr("pivotLocation", L.T("Pivot mode: Center, FirstVertex, Custom", "轴心模式：Center/FirstVertex/Custom"))
                    .SetEnumValues("Center", "FirstVertex", "Custom")
                    .SetDefault("Center"),
                new MethodVector("customPosition", L.T("World position for pivotLocation=Custom", "pivotLocation=Custom 时的世界坐标"), true, 3),

                new MethodStr("target", L.T("Main object for boolean operations (alternative to instanceId)", "布尔运算主对象（与 instanceId 二选一）")),
                new MethodStr("tool", L.T("Boolean tool object name (alternative to toolInstanceId)", "布尔工具对象名称（与 toolInstanceId 二选一）")),
                new MethodInt("toolInstanceId", L.T("Boolean tool object instance ID", "布尔工具对象 instanceId")),
                new MethodStr("operation", L.T("Boolean operation: subtract, union, intersect", "布尔运算：subtract（差集打孔）/union（并集）/intersect（交集）"))
                    .SetEnumValues("subtract", "union", "intersect")
                    .SetDefault("subtract"),

                new MethodStr("detail", L.T("Detail level: summary, full", "详情级别：summary（含面方向摘要）/full（逐面详情）"))
                    .SetEnumValues("summary", "full")
                    .SetDefault("summary"),
                new MethodBool("includeVertexPositions", L.T("Include per-face vertex positions in full mode", "full 模式包含每面顶点坐标"))
                    .SetDefault(false),
                new MethodBool("includeEdges", L.T("Include per-face edges in full mode", "full 模式包含每面边信息"))
                    .SetDefault(true),
                new MethodInt("maxFacesToShow", L.T("Max faces listed in full mode (-1 lists all)", "full 模式面详情上限（-1 表示全部）"))
                    .SetDefault(20)
            };
        }

        #region 创建

        [ToolAction("createShape", "shapeType=require", "name", "parent", "position", "rotation", "scale", "size", "isLocalSpace")]
        private object CreateShape(JsonClass args)
        {
            try
            {
                string shapeTypeStr = GetTrimmedString(args, "shapeType");
                if (string.IsNullOrEmpty(shapeTypeStr))
                    return Response.Error(L.T(
                        "Parameter 'shapeType' is required (Cube/Cylinder/Sphere/Plane/Prism/Cone/Stair/Door/Pipe/Arch/Sprite/Torus).",
                        "参数 'shapeType' 必填 (Cube/Cylinder/Sphere/Plane/Prism/Cone/Stair/Door/Pipe/Arch/Sprite/Torus)"));
                if (!Enum.TryParse<ShapeType>(shapeTypeStr, true, out var shapeType))
                    return Response.Error(L.T($"Invalid shapeType '{shapeTypeStr}'.", $"无效 shapeType '{shapeTypeStr}'"));

                string name = GetString(args, "name");
                var parent = FindParent(args, out var pErr);
                if (pErr != null) return Response.Error(pErr);

                Vector3 position = GetVector3(args, "position") ?? Vector3.zero;
                Vector3 rotation = GetVector3(args, "rotation") ?? Vector3.zero;
                Vector3 scale = GetVector3(args, "scale") ?? Vector3.one;
                Vector3? size = GetVector3(args, "size");
                bool isLocalSpace = args["isLocalSpace"].AsBoolDefault(false);

                var pb = ShapeGenerator.CreateShape(shapeType, PivotLocation.Center);
                if (pb == null)
                    return Response.Error(L.T($"Failed to create ProBuilder shape '{shapeTypeStr}'.", $"创建 ProBuilder 形状 '{shapeTypeStr}' 失败"));

                var go = pb.gameObject;
                go.name = string.IsNullOrEmpty(name) ? $"ProBuilder {shapeType}" : name;
                if (parent != null) go.transform.SetParent(parent.transform, false);

                if (isLocalSpace)
                {
                    go.transform.localPosition = position;
                    go.transform.localEulerAngles = rotation;
                    go.transform.localScale = scale;
                }
                else
                {
                    go.transform.position = position;
                    go.transform.eulerAngles = rotation;
                    go.transform.localScale = scale;
                }

                // 按 size 缩放顶点（size 与默认 1,1,1 不同时）
                if (size.HasValue && size.Value != Vector3.one)
                {
                    var sz = size.Value;
                    var meshFilter = go.GetComponent<MeshFilter>();
                    var bounds = meshFilter != null && meshFilter.sharedMesh != null
                        ? meshFilter.sharedMesh.bounds : new Bounds();
                    var currentSize = bounds.size;
                    var scaleFactors = new Vector3(
                        currentSize.x > 0 ? sz.x / currentSize.x : 1f,
                        currentSize.y > 0 ? sz.y / currentSize.y : 1f,
                        currentSize.z > 0 ? sz.z / currentSize.z : 1f);
                    var posArr = pb.positions;
                    var newPos = new Vector3[posArr.Count];
                    for (int i = 0; i < posArr.Count; i++)
                        newPos[i] = Vector3.Scale(posArr[i], scaleFactors);
                    pb.positions = newPos;
                    pb.ToMesh();
                    pb.Refresh();
                }

                EditorUtility.SetDirty(go);
                RepaintEditor();

                return Response.Success(L.T("ProBuilder shape created.", "ProBuilder 形状创建成功。"), new
                {
                    gameObjectName = go.name,
                    instanceId = go.GetInstanceID(),
                    shapeType = shapeType.ToString(),
                    position = FormatVector3(go.transform.position),
                    rotation = FormatVector3(go.transform.eulerAngles),
                    scale = FormatVector3(go.transform.localScale),
                    faceCount = pb.faceCount,
                    vertexCount = pb.vertexCount,
                    edgeCount = pb.edgeCount
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Failed to create shape: {ex.Message}", $"创建形状失败: {ex.Message}"));
            }
        }

        [ToolAction("createPolyShape", "points=require", "height", "name", "parent", "position", "rotation", "flipNormals", "isLocalSpace")]
        private object CreatePolyShape(JsonClass args)
        {
            try
            {
                var pointList = GetFloatPairs(args, "points");
                if (pointList == null || pointList.Count < 3)
                    return Response.Error(L.T(
                        "Parameter 'points' is required with at least 3 points, formatted [[x,z],[x,z],...] or 'x,z;x,z;...'.",
                        "参数 'points' 必填，至少 3 个点，格式 [[x,z],[x,z],...] 或 'x,z;x,z;...'"));
                foreach (var pt in pointList)
                {
                    if (pt.Length < 2)
                        return Response.Error(L.T("Each point must contain [x,z].", "每个点必须包含 [x,z] 两个坐标"));
                }

                float height = args["height"].AsFloatDefault(1f);
                string name = GetString(args, "name");
                var parent = FindParent(args, out var pErr);
                if (pErr != null) return Response.Error(pErr);
                Vector3 position = GetVector3(args, "position") ?? Vector3.zero;
                Vector3 rotation = GetVector3(args, "rotation") ?? Vector3.zero;
                bool flipNormals = args["flipNormals"].AsBoolDefault(false);
                bool isLocalSpace = args["isLocalSpace"].AsBoolDefault(false);

                var points3D = pointList.Select(p => new Vector3(p[0], 0f, p[1])).ToArray();

                var go = new GameObject();
                var pb = go.AddComponent<ProBuilderMesh>();
                if (pb == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return Response.Error(L.T("Failed to create the ProBuilderMesh component.", "创建 ProBuilderMesh 组件失败"));
                }

                ActionResult result;
                try
                {
                    result = pb.CreateShapeFromPolygon(points3D, height, flipNormals);
                }
                catch (Exception ex)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return Response.Error(L.T($"Failed to create polygon shape: {ex.Message}", $"创建多边形形状失败: {ex.Message}"));
                }

                if (result.status != ActionResult.Status.Success)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return Response.Error(L.T($"Failed to create polygon shape: {result.notification}", $"创建多边形形状失败: {result.notification}"));
                }

                go.name = string.IsNullOrEmpty(name) ? "ProBuilder PolyShape" : name;
                if (parent != null) go.transform.SetParent(parent.transform, false);
                if (isLocalSpace)
                {
                    go.transform.localPosition = position;
                    go.transform.localEulerAngles = rotation;
                }
                else
                {
                    go.transform.position = position;
                    go.transform.eulerAngles = rotation;
                }

                EditorUtility.SetDirty(go);
                RepaintEditor();

                var meshFilter = go.GetComponent<MeshFilter>();
                var bounds = meshFilter != null && meshFilter.sharedMesh != null
                    ? meshFilter.sharedMesh.bounds : new Bounds();

                var inputPoints = new List<object>();
                for (int i = 0; i < pointList.Count; i++)
                    inputPoints.Add(new { index = i, x = pointList[i][0], z = pointList[i][1] });

                return Response.Success(L.T("Polygon shape created.", "多边形形状创建成功。"), new
                {
                    gameObjectName = go.name,
                    instanceId = go.GetInstanceID(),
                    position = FormatVector3(go.transform.position),
                    rotation = FormatVector3(go.transform.eulerAngles),
                    pointCount = pointList.Count,
                    height,
                    flipNormals,
                    boundsSize = FormatVector3(bounds.size),
                    faceCount = pb.faceCount,
                    vertexCount = pb.vertexCount,
                    edgeCount = pb.edgeCount,
                    inputPoints
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Failed to create polygon shape: {ex.Message}", $"创建多边形形状失败: {ex.Message}"));
            }
        }

        #endregion

        #region 面编辑

        [ToolAction("extrude", "gameObject", "instanceId", "faceIndices", "faceDirection", "distance", "extrudeMethod")]
        private object Extrude(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                var (faceIndices, selectionMethod, selErr) = ResolveFaceSelection(args, pb);
                if (selErr != null) return Response.Error(selErr);

                float distance = args["distance"].AsFloatDefault(0.5f);
                string methodStr = GetTrimmedString(args, "extrudeMethod") ?? "FaceNormal";
                if (!Enum.TryParse<ExtrudeMethod>(methodStr, true, out var extrudeMethod))
                    return Response.Error(L.T(
                        $"Invalid extrudeMethod '{methodStr}' (IndividualFaces/FaceNormal/VertexNormal).",
                        $"无效 extrudeMethod '{methodStr}' (IndividualFaces/FaceNormal/VertexNormal)"));

                var faces = pb.faces;
                if (faces.Count == 0)
                    return Response.Error(L.T("The mesh has no faces to edit.", "网格没有可操作的面"));

                var facesToExtrude = faceIndices.Select(i => faces[i]).ToArray();
                Face[] newFaces;
                try
                {
                    newFaces = pb.Extrude(facesToExtrude, extrudeMethod, distance);
                }
                catch (Exception ex)
                {
                    return Response.Error(L.T($"Extrude failed: {ex.Message}", $"挤出失败: {ex.Message}"));
                }

                if (newFaces == null || newFaces.Length == 0)
                    return Response.Error(L.T(
                        "Extrude failed: no new faces were created; the operation may not be valid for this mesh.",
                        "挤出失败: 未创建任何新面，该操作可能对当前网格配置无效"));

                pb.ToMesh();
                pb.Refresh();
                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(go);
                RepaintEditor();

                var allFaces = pb.faces;
                var newFaceIndices = new List<int>();
                for (int i = 0; i < allFaces.Count; i++)
                {
                    if (newFaces.Contains(allFaces[i]))
                        newFaceIndices.Add(i);
                }

                return Response.Success(L.T("Faces extruded.", "面挤出成功。"), new
                {
                    extrudedFaceCount = facesToExtrude.Length,
                    selectionMethod,
                    extrudedFaceIndices = faceIndices,
                    extrudeMethod = extrudeMethod.ToString(),
                    distance,
                    newFacesCreated = newFaces.Length,
                    newFaceIndices = newFaceIndices.ToArray(),
                    totalFaceCount = pb.faceCount,
                    totalVertexCount = pb.vertexCount,
                    totalEdgeCount = pb.edgeCount
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Extrude failed: {ex.Message}", $"挤出失败: {ex.Message}"));
            }
        }

        [ToolAction("deleteFaces", "gameObject", "instanceId", "faceIndices", "faceDirection")]
        private object DeleteFaces(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                var (faceIndices, selectionMethod, selErr) = ResolveFaceSelection(args, pb);
                if (selErr != null) return Response.Error(selErr);

                var faces = pb.faces;
                if (faces.Count == 0)
                    return Response.Error(L.T("The mesh has no faces to edit.", "网格没有可操作的面"));

                var uniqueIndices = faceIndices.Distinct().ToArray();
                var invalid = uniqueIndices.Where(i => i < 0 || i >= faces.Count).ToList();
                if (invalid.Count > 0)
                    return Response.Error(L.T(
                        $"Invalid face indices: {string.Join(", ", invalid)}. Valid range is 0 to {faces.Count - 1}.",
                        $"无效的面索引: {string.Join(", ", invalid)}。有效范围 0 到 {faces.Count - 1}"));
                if (uniqueIndices.Length >= faces.Count)
                    return Response.Error(L.T(
                        "Cannot delete every face of the mesh; keep at least one face.",
                        "不能删除网格的所有面，至少保留一个面"));

                int origFaces = pb.faceCount;
                int origVerts = pb.vertexCount;
                var facesToDelete = uniqueIndices.Select(i => faces[i]).ToArray();
                try
                {
                    pb.DeleteFaces(facesToDelete);
                }
                catch (Exception ex)
                {
                    return Response.Error(L.T($"Failed to delete faces: {ex.Message}", $"删除面失败: {ex.Message}"));
                }

                pb.ToMesh();
                pb.Refresh();
                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(go);
                RepaintEditor();

                return Response.Success(L.T("Faces deleted.", "面删除成功。"), new
                {
                    deletedFaceCount = uniqueIndices.Length,
                    selectionMethod,
                    deletedFaceIndices = uniqueIndices,
                    facesRemoved = origFaces - pb.faceCount,
                    verticesRemoved = origVerts - pb.vertexCount,
                    totalFaceCount = pb.faceCount,
                    totalVertexCount = pb.vertexCount,
                    totalEdgeCount = pb.edgeCount
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Failed to delete faces: {ex.Message}", $"删除面失败: {ex.Message}"));
            }
        }

        [ToolAction("flipNormals", "gameObject", "instanceId", "faceIndices", "faceDirection")]
        private object FlipNormals(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                var faces = pb.faces;
                if (faces.Count == 0)
                    return Response.Error(L.T("The mesh has no faces to edit.", "网格没有可操作的面"));

                int[] resolvedIndices;
                string selectionMethod;
                var faceIndices = GetIntArray(args, "faceIndices");
                string faceDirectionStr = GetTrimmedString(args, "faceDirection");

                if (faceIndices != null && faceIndices.Length > 0)
                {
                    resolvedIndices = faceIndices;
                    selectionMethod = "by index";
                }
                else if (!string.IsNullOrEmpty(faceDirectionStr))
                {
                    var sel = SelectFacesByDirection(pb, faceDirectionStr, out var selErr);
                    if (selErr != null) return Response.Error(selErr);
                    resolvedIndices = sel;
                    selectionMethod = $"by direction '{faceDirectionStr}'";
                }
                else
                {
                    resolvedIndices = Enumerable.Range(0, faces.Count).ToArray();
                    selectionMethod = "all faces";
                }

                var invalid = resolvedIndices.Where(i => i < 0 || i >= faces.Count).ToList();
                if (invalid.Count > 0)
                    return Response.Error(L.T(
                        $"Invalid face indices: {string.Join(", ", invalid)}. Valid range is 0 to {faces.Count - 1}.",
                        $"无效的面索引: {string.Join(", ", invalid)}。有效范围 0 到 {faces.Count - 1}"));

                var facesToFlip = resolvedIndices.Select(i => faces[i]).ToArray();
                try
                {
                    foreach (var face in facesToFlip)
                        face.Reverse();
                }
                catch (Exception ex)
                {
                    return Response.Error(L.T($"Failed to flip normals: {ex.Message}", $"翻转法线失败: {ex.Message}"));
                }

                pb.ToMesh();
                pb.Refresh();
                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(go);
                RepaintEditor();

                int[] shownIndices = resolvedIndices.Length <= 20 ? resolvedIndices : null;

                return Response.Success(L.T("Normals flipped.", "法线翻转成功。"), new
                {
                    facesFlipped = resolvedIndices.Length,
                    selectionMethod,
                    faceIndices = shownIndices,
                    totalFaceCount = pb.faceCount,
                    totalVertexCount = pb.vertexCount
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Failed to flip normals: {ex.Message}", $"翻转法线失败: {ex.Message}"));
            }
        }

        [ToolAction("setFaceMaterial", "materialPath=require", "gameObject", "instanceId", "faceIndices", "faceDirection")]
        private object SetFaceMaterial(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                string materialPath = GetString(args, "materialPath");
                if (string.IsNullOrEmpty(materialPath))
                    return Response.Error(L.T(
                        "Parameter 'materialPath' is required (material asset path or name).",
                        "参数 'materialPath' 必填（材质资源路径或名称）"));

                var (faceIndices, selectionMethod, selErr) = ResolveFaceSelection(args, pb);
                if (selErr != null) return Response.Error(selErr);

                Material material = null;
                if (materialPath.StartsWith("Assets/"))
                    material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    var guids = AssetDatabase.FindAssets($"t:Material {materialPath}");
                    if (guids.Length > 0)
                        material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guids[0]));
                }
                if (material == null)
                    return Response.Error(L.T($"Material '{materialPath}' was not found.", $"未找到材质 '{materialPath}'"));

                var faces = pb.faces;
                if (faces.Count == 0)
                    return Response.Error(L.T("The mesh has no faces to edit.", "网格没有可操作的面"));

                var invalid = faceIndices.Where(i => i < 0 || i >= faces.Count).ToList();
                if (invalid.Count > 0)
                    return Response.Error(L.T(
                        $"Invalid face indices: {string.Join(", ", invalid)}. Valid range is 0 to {faces.Count - 1}.",
                        $"无效的面索引: {string.Join(", ", invalid)}。有效范围 0 到 {faces.Count - 1}"));

                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer == null)
                    return Response.Error(L.T(
                        "The GameObject has no MeshRenderer component.",
                        "GameObject 上没有 MeshRenderer 组件"));

                var materials = renderer.sharedMaterials.ToList();
                int materialIndex = materials.IndexOf(material);
                if (materialIndex < 0)
                {
                    materialIndex = materials.Count;
                    materials.Add(material);
                    renderer.sharedMaterials = materials.ToArray();
                }

                var selectedFaces = faceIndices.Select(i => faces[i]).ToArray();
                foreach (var face in selectedFaces)
                    face.submeshIndex = materialIndex;

                pb.ToMesh();
                pb.Refresh();
                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(renderer);
                EditorUtility.SetDirty(go);
                RepaintEditor();

                var meshMaterials = new List<object>();
                for (int i = 0; i < renderer.sharedMaterials.Length; i++)
                {
                    var m = renderer.sharedMaterials[i];
                    meshMaterials.Add(new { index = i, name = m != null ? m.name : "null" });
                }

                return Response.Success(L.T("Face material set.", "面材质设置成功。"), new
                {
                    materialName = material.name,
                    materialIndex,
                    selectionMethod,
                    facesUpdated = faceIndices,
                    meshMaterials
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Failed to set face material: {ex.Message}", $"设置面材质失败: {ex.Message}"));
            }
        }

        #endregion

        #region 边编辑

        [ToolAction("bevel", "edges=require", "gameObject", "instanceId", "amount")]
        private object BevelEdges(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                var edgeList = GetEdgePairs(args, "edges");
                if (edgeList == null || edgeList.Count == 0)
                    return Response.Error(L.T(
                        "Parameter 'edges' is required, formatted [[a,b],[c,d],...] or 'a-b,c-d'.",
                        "参数 'edges' 必填，格式 [[a,b],[c,d],...] 或 'a-b,c-d'"));

                float amount = Mathf.Clamp(args["amount"].AsFloatDefault(0.1f), 0.001f, 0.999f);
                int vertexCount = pb.vertexCount;

                var edges = new List<Edge>();
                foreach (var edgeDef in edgeList)
                {
                    if (edgeDef.Length < 2)
                        return Response.Error(L.T("Each edge must contain 2 vertex indices [a,b].", "每条边必须包含 2 个顶点索引 [a,b]"));
                    if (edgeDef[0] < 0 || edgeDef[0] >= vertexCount)
                        return Response.Error(L.T(
                            $"Vertex index {edgeDef[0]} is out of range 0 to {vertexCount - 1}.",
                            $"顶点索引 {edgeDef[0]} 超出范围 0 到 {vertexCount - 1}"));
                    if (edgeDef[1] < 0 || edgeDef[1] >= vertexCount)
                        return Response.Error(L.T(
                            $"Vertex index {edgeDef[1]} is out of range 0 to {vertexCount - 1}.",
                            $"顶点索引 {edgeDef[1]} 超出范围 0 到 {vertexCount - 1}"));
                    edges.Add(new Edge(edgeDef[0], edgeDef[1]));
                }

                List<Face> newFaces;
                try
                {
                    newFaces = Bevel.BevelEdges(pb, edges, amount);
                }
                catch (Exception ex)
                {
                    return Response.Error(L.T($"Bevel failed: {ex.Message}", $"倒角失败: {ex.Message}"));
                }

                if (newFaces == null || newFaces.Count == 0)
                    return Response.Error(L.T(
                        "Bevel failed: no new faces were created; the edges may not support beveling or are already fully beveled.",
                        "倒角失败: 未创建任何新面，边可能不适合倒角或已达到最大倒角"));

                pb.ToMesh();
                pb.Refresh();
                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(go);
                RepaintEditor();

                return Response.Success(L.T("Edges beveled.", "边倒角成功。"), new
                {
                    edgesBeveled = edges.Count,
                    bevelAmount = amount,
                    newFacesCreated = newFaces.Count,
                    totalFaceCount = pb.faceCount,
                    totalVertexCount = pb.vertexCount,
                    totalEdgeCount = pb.edgeCount
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Bevel failed: {ex.Message}", $"倒角失败: {ex.Message}"));
            }
        }

        [ToolAction("bridge", "edgeA=require", "edgeB=require", "gameObject", "instanceId", "allowNonManifold")]
        private object Bridge(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                var edgeA = GetIntPair(args, "edgeA");
                var edgeB = GetIntPair(args, "edgeB");
                if (edgeA == null || edgeB == null)
                    return Response.Error(L.T(
                        "Parameters 'edgeA' and 'edgeB' are required, formatted [a,b] or 'a-b'.",
                        "参数 'edgeA' 和 'edgeB' 必填，格式 [a,b] 或 'a-b'"));
                bool allowNonManifold = args["allowNonManifold"].AsBoolDefault(false);

                int originalFaceCount = pb.faceCount;
                Face newFace;
                try
                {
                    newFace = pb.Bridge(new Edge(edgeA[0], edgeA[1]), new Edge(edgeB[0], edgeB[1]), allowNonManifold);
                }
                catch (Exception ex)
                {
                    return Response.Error(L.T($"Bridge failed: {ex.Message}", $"桥接失败: {ex.Message}"));
                }

                if (newFace == null)
                    return Response.Error(L.T(
                        "Bridge failed: no face could be created between the given edges; make sure they are valid and unconnected.",
                        "桥接失败: 无法在指定边之间创建面，请确认边有效且未连接"));

                pb.ToMesh();
                pb.Refresh();
                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(go);
                RepaintEditor();

                var faces = pb.faces;
                int newFaceIndex = -1;
                for (int i = 0; i < faces.Count; i++)
                {
                    if (faces[i] == newFace) { newFaceIndex = i; break; }
                }

                return Response.Success(L.T("Edges bridged.", "边桥接成功。"), new
                {
                    edgeA,
                    edgeB,
                    newFaceIndex,
                    allowNonManifold,
                    faceCountBefore = originalFaceCount,
                    faceCountAfter = pb.faceCount,
                    facesAdded = pb.faceCount - originalFaceCount,
                    totalVertexCount = pb.vertexCount,
                    totalEdgeCount = pb.edgeCount
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Bridge failed: {ex.Message}", $"桥接失败: {ex.Message}"));
            }
        }

        [ToolAction("connectEdges", "gameObject", "instanceId", "edges", "faceDirection")]
        private object ConnectEdges(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                var (edgesToConnect, selectionMethod, selErr) = ResolveEdgeSelection(args, pb);
                if (selErr != null) return Response.Error(selErr);
                if (edgesToConnect.Count < 2)
                    return Response.Error(L.T("At least 2 edges are required to connect.", "至少需要 2 条边才能连接"));

                int origFaces = pb.faceCount;
                int origEdges = pb.edgeCount;
                Face[] newFaces;
                Edge[] newEdges;
                try
                {
                    var result = ConnectElements.Connect(pb, edgesToConnect);
                    newFaces = result.item1;
                    newEdges = result.item2;
                }
                catch (Exception ex)
                {
                    return Response.Error(L.T($"Failed to connect edges: {ex.Message}", $"连接边失败: {ex.Message}"));
                }

                if ((newFaces == null || newFaces.Length == 0) && (newEdges == null || newEdges.Length == 0))
                    return Response.Error(L.T(
                        "Connect failed: no new geometry was created; the edges may not support connecting.",
                        "连接失败: 未创建任何新几何，边可能不适合连接"));

                pb.ToMesh();
                pb.Refresh();
                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(go);
                RepaintEditor();

                return Response.Success(L.T("Edges connected.", "边连接成功。"), new
                {
                    selectionMethod,
                    edgesConnected = edgesToConnect.Count,
                    newFacesCreated = newFaces?.Length ?? 0,
                    newEdgesCreated = newEdges?.Length ?? 0,
                    faceCountBefore = origFaces,
                    faceCountAfter = pb.faceCount,
                    facesAdded = pb.faceCount - origFaces,
                    edgeCountBefore = origEdges,
                    edgeCountAfter = pb.edgeCount,
                    edgesAdded = pb.edgeCount - origEdges,
                    totalVertexCount = pb.vertexCount
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Failed to connect edges: {ex.Message}", $"连接边失败: {ex.Message}"));
            }
        }

        [ToolAction("subdivideEdges", "gameObject", "instanceId", "edges", "faceDirection", "subdivisions")]
        private object SubdivideEdges(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                int subdivisions = args["subdivisions"].AsIntDefault(1);
                if (subdivisions < 1)
                    return Response.Error(L.T("'subdivisions' must be at least 1.", "subdivisions 至少为 1"));

                var (edgesToSubdivide, selectionMethod, selErr) = ResolveEdgeSelection(args, pb);
                if (selErr != null) return Response.Error(selErr);
                if (edgesToSubdivide.Count == 0)
                    return Response.Error(L.T("No edges found to subdivide.", "未找到可细分的边"));

                int origVerts = pb.vertexCount;
                int origEdges = pb.edgeCount;
                List<Edge> newEdges;
                try
                {
                    newEdges = pb.AppendVerticesToEdge(edgesToSubdivide, subdivisions);
                }
                catch (Exception ex)
                {
                    return Response.Error(L.T($"Failed to subdivide edges: {ex.Message}", $"细分边失败: {ex.Message}"));
                }

                if (newEdges == null || newEdges.Count == 0)
                    return Response.Error(L.T("Subdivide failed: no new edges were created.", "细分失败: 未创建任何新边"));

                pb.ToMesh();
                pb.Refresh();
                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(go);
                RepaintEditor();

                return Response.Success(L.T("Edges subdivided.", "边细分成功。"), new
                {
                    selectionMethod,
                    edgesSubdivided = edgesToSubdivide.Count,
                    subdivisionsPerEdge = subdivisions,
                    newEdgesCreated = newEdges.Count,
                    vertexCountBefore = origVerts,
                    vertexCountAfter = pb.vertexCount,
                    verticesAdded = pb.vertexCount - origVerts,
                    edgeCountBefore = origEdges,
                    edgeCountAfter = pb.edgeCount,
                    edgesAdded = pb.edgeCount - origEdges,
                    totalFaceCount = pb.faceCount
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Failed to subdivide edges: {ex.Message}", $"细分边失败: {ex.Message}"));
            }
        }

        #endregion

        #region 对象操作

        [ToolAction("mergeObjects", "gameObjects", "instanceIds", "deleteSourceObjects")]
        private object MergeObjects(JsonClass args)
        {
            try
            {
                var targets = ResolveTargets(args, out var tErr);
                if (tErr != null) return Response.Error(tErr);
                if (targets.Count < 2)
                    return Response.Error(L.T("At least 2 GameObjects are required to merge.", "至少需要 2 个 GameObject 才能合并"));
                bool deleteSourceObjects = args["deleteSourceObjects"].AsBoolDefault(true);

                var meshes = new List<ProBuilderMesh>();
                var gameObjects = new List<GameObject>();
                for (int i = 0; i < targets.Count; i++)
                {
                    var go = targets[i];
                    var pb = go.GetComponent<ProBuilderMesh>();
                    if (pb == null)
                        return Response.Error(L.T(
                            $"GameObject '{go.name}' (index {i}) has no ProBuilderMesh component.",
                            $"GameObject '{go.name}' (索引 {i}) 没有 ProBuilderMesh 组件"));
                    meshes.Add(pb);
                    gameObjects.Add(go);
                }

                var targetMesh = meshes[0];
                var targetGo = gameObjects[0];
                var originalNames = gameObjects.Select(g => g.name).ToList();
                int totalFacesBefore = meshes.Sum(m => m.faceCount);
                int totalVertsBefore = meshes.Sum(m => m.vertexCount);

                List<ProBuilderMesh> resultMeshes;
                try
                {
                    resultMeshes = CombineMeshes.Combine(meshes, targetMesh);
                }
                catch (Exception ex)
                {
                    return Response.Error(L.T($"Merge failed: {ex.Message}", $"合并失败: {ex.Message}"));
                }
                if (resultMeshes == null || resultMeshes.Count == 0)
                    return Response.Error(L.T("Merge failed: no mesh was returned.", "合并失败: 未返回任何网格"));

                int deletedCount = 0;
                if (deleteSourceObjects)
                {
                    for (int i = 1; i < gameObjects.Count; i++)
                    {
                        if (gameObjects[i] != null)
                        {
                            UnityEngine.Object.DestroyImmediate(gameObjects[i]);
                            deletedCount++;
                        }
                    }
                }

                foreach (var mesh in resultMeshes)
                {
                    mesh.ToMesh();
                    mesh.Refresh();
                    EditorUtility.SetDirty(mesh);
                    EditorUtility.SetDirty(mesh.gameObject);
                }
                RepaintEditor();

                var sources = new List<object>();
                for (int i = 0; i < originalNames.Count; i++)
                {
                    sources.Add(new
                    {
                        index = i,
                        name = originalNames[i],
                        status = i == 0 ? "target" : (deleteSourceObjects ? "deleted" : "kept")
                    });
                }

                var data = new JsonClass();
                data.Add("mergedMeshCount", new JsonData(meshes.Count));
                data.Add("resultMeshCount", new JsonData(resultMeshes.Count));
                data.Add("targetObjectName", new JsonData(targetGo.name));
                data.Add("targetInstanceId", new JsonData(targetGo.GetInstanceID()));
                data.Add("objectsDeleted", new JsonData(deletedCount));
                data.Add("totalFacesBefore", new JsonData(totalFacesBefore));
                data.Add("totalFacesAfter", new JsonData(resultMeshes.Sum(m => m.faceCount)));
                data.Add("totalVerticesBefore", new JsonData(totalVertsBefore));
                data.Add("totalVerticesAfter", new JsonData(resultMeshes.Sum(m => m.vertexCount)));
                data.Add("sourceObjects", Json.FromObject(sources));

                if (resultMeshes.Count > 1)
                {
                    var additional = new List<object>();
                    for (int i = 1; i < resultMeshes.Count; i++)
                    {
                        additional.Add(new
                        {
                            name = resultMeshes[i].gameObject.name,
                            instanceId = resultMeshes[i].gameObject.GetInstanceID()
                        });
                    }
                    data.Add("additionalMeshes", Json.FromObject(additional));
                }

                return Response.Success(L.T("Meshes merged.", "网格合并成功。"), data);
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Merge failed: {ex.Message}", $"合并失败: {ex.Message}"));
            }
        }

        [ToolAction("setPivot", "gameObject", "instanceId", "pivotLocation", "customPosition")]
        private object SetPivot(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                string pivotLocationStr = GetTrimmedString(args, "pivotLocation") ?? "Center";
                Vector3? customPosition = GetVector3(args, "customPosition");

                var oldPivot = go.transform.position;
                Vector3 newPivotWorld;

                switch (pivotLocationStr.ToLower())
                {
                    case "center":
                    {
                        var meshFilter = go.GetComponent<MeshFilter>();
                        if (meshFilter == null || meshFilter.sharedMesh == null)
                            return Response.Error(L.T(
                                "The GameObject has no mesh (MeshFilter).",
                                "GameObject 上没有网格 (MeshFilter)"));
                        newPivotWorld = go.transform.TransformPoint(meshFilter.sharedMesh.bounds.center);
                        break;
                    }
                    case "firstvertex":
                    {
                        var positions = pb.positions;
                        if (positions == null || positions.Count == 0)
                            return Response.Error(L.T("The mesh has no vertices.", "网格没有顶点"));
                        newPivotWorld = go.transform.TransformPoint(positions[0]);
                        break;
                    }
                    case "custom":
                    {
                        if (!customPosition.HasValue)
                            return Response.Error(L.T(
                                "customPosition is required when pivotLocation=Custom.",
                                "pivotLocation=Custom 时必须提供 customPosition"));
                        newPivotWorld = customPosition.Value;
                        break;
                    }
                    default:
                        return Response.Error(L.T(
                            $"Invalid pivotLocation '{pivotLocationStr}' (Center/FirstVertex/Custom).",
                            $"无效 pivotLocation '{pivotLocationStr}' (Center/FirstVertex/Custom)"));
                }

                var offset = go.transform.InverseTransformPoint(newPivotWorld);
                try
                {
                    var vertexPositions = pb.positions.ToArray();
                    for (int i = 0; i < vertexPositions.Length; i++)
                        vertexPositions[i] -= offset;
                    pb.positions = vertexPositions;
                    go.transform.position = newPivotWorld;
                    pb.ToMesh();
                    pb.Refresh();
                }
                catch (Exception ex)
                {
                    return Response.Error(L.T($"Failed to set pivot: {ex.Message}", $"设置轴心失败: {ex.Message}"));
                }

                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(go);
                RepaintEditor();

                return Response.Success(L.T("Pivot set.", "轴心设置成功。"), new
                {
                    pivotLocation = pivotLocationStr,
                    oldPivot = FormatVector3(oldPivot),
                    newPivot = FormatVector3(newPivotWorld),
                    offsetApplied = FormatVector3(offset),
                    gameObjectName = go.name,
                    newPosition = FormatVector3(go.transform.position)
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Failed to set pivot: {ex.Message}", $"设置轴心失败: {ex.Message}"));
            }
        }

        [ToolAction("boolean", "target", "instanceId", "tool", "toolInstanceId", "operation", "deleteSourceObjects")]
        private object Boolean(JsonClass args)
        {
            try
            {
                var target = FindTarget(args, out var fErr, "target");
                if (fErr != null) return Response.Error(fErr);
                var targetPb = target.GetComponent<ProBuilderMesh>();
                if (targetPb == null)
                    return Response.Error(L.T(
                        $"GameObject '{target.name}' has no ProBuilderMesh component (instanceId={target.GetInstanceID()}).",
                        $"GameObject '{target.name}' 上没有 ProBuilderMesh 组件（instanceId={target.GetInstanceID()}）"));

                var tool = FindTool(args, out var tErr);
                if (tErr != null) return Response.Error(tErr);
                var toolPb = tool.GetComponent<ProBuilderMesh>();
                if (toolPb == null)
                    return Response.Error(L.T(
                        $"GameObject '{tool.name}' has no ProBuilderMesh component (instanceId={tool.GetInstanceID()}).",
                        $"GameObject '{tool.name}' 上没有 ProBuilderMesh 组件（instanceId={tool.GetInstanceID()}）"));

                string operation = GetTrimmedString(args, "operation") ?? "subtract";
                string methodName;
                switch (operation.ToLower())
                {
                    case "union": methodName = "MenuUnion"; break;
                    case "intersect":
                    case "intersection": methodName = "MenuIntersect"; break;
                    case "subtract":
                    case "subtraction":
                    default: methodName = "MenuSubtract"; break;
                }

                bool deleteSourceObjects = args["deleteSourceObjects"].AsBoolDefault(true);

                // 反射调用 ProBuilder 实验性布尔工具（CSG 类为 internal，无法直接引用）
                Assembly asm = Assembly.Load("Unity.ProBuilder.Editor");
                if (asm == null)
                    return Response.Error(L.T("Could not load the Unity.ProBuilder.Editor assembly.", "无法加载 Unity.ProBuilder.Editor 程序集"));
                var editorType = asm.GetType("UnityEditor.ProBuilder.BooleanEditor", false);
                if (editorType == null)
                    return Response.Error(L.T(
                        "BooleanEditor was not found; make sure PROBUILDER_EXPERIMENTAL_FEATURES is enabled in Player Settings.",
                        "未找到 BooleanEditor，请确认已在 Player Settings 开启 PROBUILDER_EXPERIMENTAL_FEATURES"));
                var method = editorType.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(ProBuilderMesh), typeof(ProBuilderMesh) }, null);
                if (method == null)
                    return Response.Error(L.T(
                        $"Boolean method {methodName} was not found.",
                        $"未找到布尔运算方法 {methodName}"));

                ActionResult result;
                try
                {
                    result = (ActionResult)method.Invoke(null, new object[] { targetPb, toolPb });
                }
                catch (TargetInvocationException tie)
                {
                    return Response.Error(L.T(
                        $"Boolean operation threw: {(tie.InnerException != null ? tie.InnerException.Message : tie.Message)}",
                        $"布尔运算异常: {(tie.InnerException != null ? tie.InnerException.Message : tie.Message)}"));
                }

                if (result == null || result.status != ActionResult.Status.Success)
                    return Response.Error(L.T(
                        $"Boolean operation failed: {(result != null ? result.notification : "unknown error")}",
                        $"布尔运算失败: {(result != null ? result.notification : "未知错误")}"));

                // BooleanEditor 会将结果对象设为当前选中
                var resultGo = Selection.activeGameObject;
                if (resultGo == null)
                    return Response.Error(L.T("Boolean operation returned no result object.", "布尔运算未返回结果对象"));
                resultGo.name = $"{target.name}_{operation}_{tool.name}";

                int deletedCount = 0;
                if (deleteSourceObjects)
                {
                    if (target != null && target != resultGo) { UnityEngine.Object.DestroyImmediate(target); deletedCount++; }
                    if (tool != null && tool != resultGo) { UnityEngine.Object.DestroyImmediate(tool); deletedCount++; }
                }

                var resultPb = resultGo.GetComponent<ProBuilderMesh>();
                int faceCount = resultPb != null ? resultPb.faceCount : 0;
                int vertexCount = resultPb != null ? resultPb.vertexCount : 0;
                int edgeCount = resultPb != null ? resultPb.edgeCount : 0;

                EditorUtility.SetDirty(resultGo);
                RepaintEditor();

                return Response.Success(L.T("Boolean operation completed.", "布尔运算完成。"), new
                {
                    gameObjectName = resultGo.name,
                    instanceId = resultGo.GetInstanceID(),
                    operation,
                    objectsDeleted = deletedCount,
                    faceCount,
                    vertexCount,
                    edgeCount
                });
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Boolean operation failed: {ex.Message}", $"布尔运算失败: {ex.Message}"));
            }
        }

        #endregion

        #region 查询

        [ToolAction("getMeshInfo", "gameObject", "instanceId", "detail", "includeVertexPositions", "includeEdges", "maxFacesToShow")]
        private object GetMeshInfo(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var fErr);
                if (fErr != null) return Response.Error(fErr);
                var pb = GetMesh(go, out var mErr);
                if (mErr != null) return Response.Error(mErr);

                string detailStr = GetTrimmedString(args, "detail") ?? "summary";
                bool full = detailStr.Equals("full", StringComparison.OrdinalIgnoreCase);
                bool includeVertexPositions = args["includeVertexPositions"].AsBoolDefault(false);
                bool includeEdges = args["includeEdges"].AsBoolDefault(true);
                int maxFacesToShow = args["maxFacesToShow"].AsIntDefault(20);

                var data = new JsonClass();
                data.Add("gameObjectName", new JsonData(go.name));
                data.Add("instanceId", new JsonData(go.GetInstanceID()));
                data.Add("faceCount", new JsonData(pb.faceCount));
                data.Add("vertexCount", new JsonData(pb.vertexCount));
                data.Add("edgeCount", new JsonData(pb.edgeCount));
                data.Add("triangleCount", new JsonData(pb.triangleCount));

                var meshFilter = go.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    var b = meshFilter.sharedMesh.bounds;
                    var bounds = new JsonClass();
                    bounds.Add("center", new JsonData(FormatVector3(b.center)));
                    bounds.Add("size", new JsonData(FormatVector3(b.size)));
                    bounds.Add("min", new JsonData(FormatVector3(b.min)));
                    bounds.Add("max", new JsonData(FormatVector3(b.max)));
                    data.Add("bounds", bounds);
                }

                // 面方向摘要
                var directionSummary = GetFaceDirectionSummary(pb, out var otherFaces);
                var faces = pb.faces;
                var positions = pb.positions;
                var directionArray = new JsonArray();
                foreach (var kvp in directionSummary)
                {
                    if (kvp.Value.Count == 0) continue;
                    string center = "";
                    if (kvp.Value[0] < faces.Count)
                        center = FormatVector3(GetFaceCenter(faces[kvp.Value[0]], positions));

                    var item = new JsonClass();
                    item.Add("direction", new JsonData(kvp.Key));
                    item.Add("faceIndices", Json.FromObject(kvp.Value.ToArray()));
                    item.Add("firstFaceCenter", new JsonData(center));
                    directionArray.Add(item);
                }
                if (otherFaces.Count > 0)
                {
                    var item = new JsonClass();
                    item.Add("direction", new JsonData("other"));
                    item.Add("faceIndices", Json.FromObject(otherFaces.ToArray()));
                    item.Add("firstFaceCenter", new JsonData(""));
                    directionArray.Add(item);
                }
                data.Add("faceDirections", directionArray);

                if (full)
                {
                    int faceCount = faces.Count;
                    int facesToShow = maxFacesToShow < 0 ? faceCount : System.Math.Min(maxFacesToShow, faceCount);
                    data.Add("facesShown", new JsonData(facesToShow));
                    data.Add("facesTotal", new JsonData(faceCount));

                    var faceArray = new JsonArray();
                    for (int i = 0; i < facesToShow; i++)
                    {
                        var face = faces[i];
                        var faceVertices = face.distinctIndexes;
                        var center = Vector3.zero;
                        foreach (var vi in faceVertices) center += positions[vi];
                        if (faceVertices.Count > 0) center /= faceVertices.Count;

                        var item = new JsonClass();
                        item.Add("index", new JsonData(i));
                        item.Add("vertexCount", new JsonData(faceVertices.Count));
                        item.Add("triangleCount", new JsonData(face.indexes.Count / 3));
                        item.Add("center", new JsonData(FormatVector3(center)));

                        if (includeVertexPositions)
                        {
                            var vertices = new JsonArray();
                            for (int vi = 0; vi < faceVertices.Count; vi++)
                            {
                                var vItem = new JsonClass();
                                vItem.Add("index", new JsonData(faceVertices[vi]));
                                vItem.Add("position", new JsonData(FormatVector3(positions[faceVertices[vi]])));
                                vertices.Add(vItem);
                            }
                            item.Add("vertices", vertices);
                        }

                        if (includeEdges)
                        {
                            var faceEdges = face.edges;
                            var edgesArray = new JsonArray();
                            for (int ei = 0; ei < faceEdges.Count; ei++)
                            {
                                var e = faceEdges[ei];
                                var eItem = new JsonClass();
                                eItem.Add("vertexA", new JsonData(e.a));
                                eItem.Add("vertexB", new JsonData(e.b));
                                eItem.Add("positionA", new JsonData(FormatVector3(positions[e.a])));
                                eItem.Add("positionB", new JsonData(FormatVector3(positions[e.b])));
                                edgesArray.Add(eItem);
                            }
                            item.Add("edges", edgesArray);
                        }

                        faceArray.Add(item);
                    }
                    data.Add("faces", faceArray);

                    if (includeEdges)
                    {
                        var allEdges = pb.faces.SelectMany(f => f.edges).Distinct().ToList();
                        data.Add("uniqueEdgeCount", new JsonData(allEdges.Count));
                    }
                }

                return Response.Success(L.T("ProBuilder mesh info retrieved.", "ProBuilder 网格信息查询成功。"), data);
            }
            catch (Exception ex)
            {
                return Response.Error(L.T($"Failed to query mesh info: {ex.Message}", $"查询网格信息失败: {ex.Message}"));
            }
        }

        #endregion
    }
}

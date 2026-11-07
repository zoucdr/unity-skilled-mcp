/*-*-* Copyright (c) uframe@zoucdr
 * Description: ProBuilder UV 工具（UniMcp ToolModuleBase 框架）。
 * 把合并后的网格按连通部件重排为互不重叠的 UV 图集，供程序化贴图按区域绘制。
 *_*/
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UniMcp;
using UniMcp.Models;
using static ProBuilder.SkilledMcp.ProBuilderMcpUtil;

namespace ProBuilder.SkilledMcp
{
    /// <summary>
    /// ProBuilder UV 编辑：按几何连通部件生成图集布局并查询 UV 岛。
    /// 与 ProBuilderMcpTools 共用 probuilder 前缀，动作名互不重叠。
    /// </summary>
    [ToolModule("probuilder", GroupEn = "Assets", GroupZh = "资源资产")]
    public class ProBuilderUvTools : ToolModuleBase
    {
        public override string Description => L.T(
            "ProBuilder UV authoring: lay a merged mesh out into a non-overlapping UV atlas (one cell per connected part, box or planar projection, vertices rebuilt to remove UV seams) and inspect UV islands, pixel rectangles and overlap.",
            "ProBuilder UV 编辑：把合并后的网格重排为互不重叠的 UV 图集（每个连通部件一格，盒式/平面投影，重建顶点消除 UV 接缝冲突），并查询 UV 岛、像素矩形与重叠情况。");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("gameObject", L.T("Target GameObject name or hierarchy path", "目标对象名称或层级路径")),
                new MethodInt("instanceId", L.T("Target GameObject instance ID (alternative to gameObject)", "目标对象 instanceId（与 gameObject 二选一）")),

                new MethodInt("columns", L.T("Atlas column count; rows are derived unless set", "图集列数；未指定 rows 时自动推导行数"))
                    .SetDefault(4).SetRange(1, 16),
                new MethodInt("rows", L.T("Atlas row count; 0 = derive from island count", "图集行数；0 = 按部件数自动推导"))
                    .SetDefault(0).SetRange(0, 16),
                new MethodFloat("margin", L.T("Gap around each cell as a fraction of cell size (0-0.3)", "每格四周留白占格子尺寸的比例（0~0.3）"))
                    .SetDefault(0.08f).SetRange(0f, 0.3f),
                new MethodStr("projection", L.T("Box projection (6 axis groups per part) or single planar projection", "盒式投影（每部件 6 个轴向组）或单一平面投影"))
                    .SetEnumValues("box", "planar").SetDefault("box"),
                new MethodBool("bake", L.T("Detach ProBuilderMesh after writing UVs so later refreshes cannot overwrite them", "写入 UV 后移除 ProBuilderMesh，避免后续刷新覆盖 UV"))
                    .SetDefault(true),
                new MethodStr("mesh_path", L.T("Save the rebuilt mesh as an .asset at this path; empty = Assets/Generated/Meshes/<name>_UvAtlas.asset", "把重建后的 Mesh 存为该路径的 .asset；留空 = Assets/Generated/Meshes/<名称>_UvAtlas.asset")),
                new MethodBool("save_mesh", L.T("Persist the rebuilt mesh as an asset (recommended); false keeps it scene-embedded only", "把重建后的 Mesh 持久化为资源（推荐）；false 则只作为场景内嵌对象"))
                    .SetDefault(true),
                new MethodInt("width", L.T("Reference texture width for pixel rectangles", "用于换算像素矩形的贴图宽度"))
                    .SetDefault(512).SetRange(1, 2048),
                new MethodInt("height", L.T("Reference texture height for pixel rectangles", "用于换算像素矩形的贴图高度"))
                    .SetDefault(512).SetRange(1, 2048)
            };
        }

        /// <summary>
        /// 把目标网格的 UV 重排成一张互不重叠的图集：每个几何连通部件占一格。
        /// </summary>
        [ToolAction("uv_atlas",
            DescriptionEn = "Rebuild UVs as a non-overlapping atlas: one cell per connected part, box/planar projection, seam-free vertex rebuild. The rebuilt mesh is saved as an .asset by default so UVs survive scene reloads. Returns cell + per-face rectangles for procedural texture layers.",
            DescriptionZh = "把 UV 重建为互不重叠的图集：每个连通部件一格，盒式/平面投影，重建顶点消除接缝冲突；默认把重建后的 Mesh 存为 .asset，保证场景重载后 UV 不丢。返回格子及每个轴向面的矩形，供程序化贴图逐面绘制。",
            Parameters = new[] { "gameObject", "instanceId", "columns", "rows", "margin", "projection", "bake", "mesh_path", "save_mesh", "width", "height" },
            Required = new string[0])]
        private object UvAtlas(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var findError);
                if (findError != null) return Response.Error(findError);

                var filter = go.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    return Response.Error(L.T("Target has no MeshFilter/sharedMesh.", "目标没有 MeshFilter/sharedMesh。"));

                bool bake = args["bake"].AsBoolDefault(true);
                // Only bake=false needs face.manualUV. A previously baked object has no
                // ProBuilderMesh, but its MeshFilter can still be re-atlased.
                ProBuilderMesh pb = null;
                if (!bake)
                {
                    pb = GetMesh(go, out var meshError);
                    if (meshError != null) return Response.Error(meshError);
                }

                int columns = Mathf.Clamp(args["columns"].AsIntDefault(4), 1, 16);
                int rows = Mathf.Clamp(args["rows"].AsIntDefault(0), 0, 16);
                float margin = Mathf.Clamp(args["margin"].AsFloatDefault(0.08f), 0f, 0.3f);
                string projection = GetTrimmedString(args, "projection") ?? "box";
                if (!projection.Equals("box", StringComparison.OrdinalIgnoreCase) &&
                    !projection.Equals("planar", StringComparison.OrdinalIgnoreCase))
                    return Response.Error(L.T("'projection' must be box or planar.", "'projection' 只能是 box 或 planar。"));
                bool box = projection.Equals("box", StringComparison.OrdinalIgnoreCase);
                bool saveMesh = args["save_mesh"].AsBoolDefault(true);
                string meshPathArg = GetTrimmedString(args, "mesh_path");
                string bakeWarning = "";
                int width = Mathf.Clamp(args["width"].AsIntDefault(512), 1, 2048);
                int height = Mathf.Clamp(args["height"].AsIntDefault(512), 1, 2048);

                PbUvBuildResult build;
                try
                {
                    build = PbUvAtlas.Build(filter.sharedMesh, columns, rows, margin, box);
                }
                catch (Exception e)
                {
                    return Response.Error(L.T("UV atlas failed: ", "UV 图集构建失败：") + e.Message);
                }

                // A MeshFilter pointing at a plain runtime Mesh is only saved as an
                // embedded scene object. Saving it as an .asset makes the UV layout
                // reusable across scenes/prefabs and immune to ProBuilder's own
                // OnDestroy cleanup of MeshFilter.sharedMesh.
                Mesh bakedMesh = build.Mesh;
                string meshAssetPath = null;
                string meshGuid = "";
                if (saveMesh)
                {
                    try
                    {
                        meshAssetPath = NormalizeMeshAssetPath(meshPathArg, go.name);
                        EnsureAssetFolder(meshAssetPath);
                        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
                        if (existing != null)
                        {
                            // Keep the GUID stable so already-saved scenes/materials keep their reference.
                            EditorUtility.CopySerialized(build.Mesh, existing);
                            EditorUtility.SetDirty(existing);
                            bakedMesh = existing;
                        }
                        else
                        {
                            AssetDatabase.CreateAsset(build.Mesh, meshAssetPath);
                        }
                        AssetDatabase.SaveAssets();
                        bakedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath) ?? bakedMesh;
                        meshGuid = AssetDatabase.AssetPathToGUID(meshAssetPath);
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Object.DestroyImmediate(build.Mesh);
                        return Response.Error(L.T("Failed to persist mesh asset: ", "Mesh 资源保存失败：") + e.Message);
                    }
                }

                filter.sharedMesh = bakedMesh;

                // 重建后的 Mesh 只有一个 submesh，材质槽也收敛为一个。
                // setFaceMaterial 会把目标材质追加到数组末尾，故保留最后一个非空材质。
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null && renderer.sharedMaterials != null && renderer.sharedMaterials.Length > 1)
                {
                    Material chosen = null;
                    for (int i = renderer.sharedMaterials.Length - 1; i >= 0; i--)
                    {
                        if (renderer.sharedMaterials[i] != null)
                        {
                            chosen = renderer.sharedMaterials[i];
                            break;
                        }
                    }
                    renderer.sharedMaterials = new[] { chosen };
                }

                if (bake)
                {
                    // ProBuilder 会在 ToMesh/Refresh 时用 Auto UV 覆盖 UV0。
                    // 移除组件前必须禁止其 OnDestroy 清理 sharedMesh（见 ProBuilderMeshFunction.OnDestroy）。
                    var component = go.GetComponent<ProBuilderMesh>();
                    if (component != null)
                    {
                        component.preserveMeshAssetOnDestroy = true;
                        UnityEngine.Object.DestroyImmediate(component);
                    }
                    // ProBuilder 把 MeshFilter 标成 DontSave；detach 后恢复，确保场景序列化该引用。
                    filter.hideFlags = HideFlags.None;
                    filter.sharedMesh = bakedMesh;
                }
                else
                {
                    foreach (var face in pb.faces) face.manualUV = true;
                    EditorUtility.SetDirty(pb);
                    // 保留 ProBuilderMesh 时，UV 只写在 sharedMesh.uv 上；
                    // 后续 ProBuilder 的 ToMesh/Refresh 仍可能重建网格并覆盖 UV，故默认 bake=true。
                    bakeWarning = L.T(
                        "bake=false keeps ProBuilderMesh; a later ToMesh/Refresh may overwrite the atlas. Prefer bake=true.",
                        "bake=false 保留了 ProBuilderMesh；后续 ToMesh/Refresh 可能覆盖图集，建议用 bake=true。");
                }
                if (!saveMesh)
                {
                    bakeWarning = (string.IsNullOrEmpty(bakeWarning) ? "" : bakeWarning + " ") + L.T(
                        "save_mesh=false keeps the mesh scene-embedded only: it cannot be shared with other scenes/prefabs and is lost if the scene is not saved.",
                        "save_mesh=false 时 Mesh 仅内嵌于当前场景：不能跨场景/预制体复用，场景未保存则会丢失。");
                }

                EditorUtility.SetDirty(filter);
                EditorUtility.SetDirty(go);
                EditorSceneManager.MarkSceneDirty(go.scene);
                RepaintEditor();

                var cells = new List<object>();
                foreach (var cell in build.Cells)
                {
                    var faces = new List<object>();
                    for (int f = 0; f < cell.FaceRects.Count; f++)
                    {
                        Vector4 fr = cell.FaceRects[f];
                        faces.Add(new
                        {
                            axis_group = cell.FaceGroups[f],
                            rect = new[] { F(fr.x), F(fr.y), F(fr.z), F(fr.w) },
                            rect_px = new[]
                            {
                                Mathf.RoundToInt(fr.x * width),
                                Mathf.RoundToInt(fr.y * height),
                                Mathf.RoundToInt((fr.z - fr.x) * width),
                                Mathf.RoundToInt((fr.w - fr.y) * height)
                            }
                        });
                    }
                    cells.Add(new
                    {
                        id = cell.Id,
                        column = cell.Column,
                        row = cell.Row,
                        rect = new[] { F(cell.Rect.x), F(cell.Rect.y), F(cell.Rect.z), F(cell.Rect.w) },
                        rect_px = new[]
                        {
                            Mathf.RoundToInt(cell.Rect.x * width),
                            Mathf.RoundToInt(cell.Rect.y * height),
                            Mathf.RoundToInt((cell.Rect.z - cell.Rect.x) * width),
                            Mathf.RoundToInt((cell.Rect.w - cell.Rect.y) * height)
                        },
                        face_rects = faces,
                        triangles = cell.Triangles,
                        vertices = cell.Vertices,
                        bounds_min = new[] { F(cell.BoundsMin.x), F(cell.BoundsMin.y), F(cell.BoundsMin.z) },
                        bounds_max = new[] { F(cell.BoundsMax.x), F(cell.BoundsMax.y), F(cell.BoundsMax.z) }
                    });
                }

                var data = new JsonClass();
                data.Add("gameObjectName", new JsonData(go.name));
                data.Add("instanceId", new JsonData(go.GetInstanceID()));
                data.Add("projection", new JsonData(box ? "box" : "planar"));
                data.Add("columns", new JsonData(columns));
                data.Add("rows", new JsonData(build.Rows));
                data.Add("margin", new JsonData(margin));
                data.Add("islandCount", new JsonData(build.Cells.Count));
                data.Add("vertexCountBefore", new JsonData(build.SourceVertices));
                data.Add("vertexCountAfter", new JsonData(build.Mesh.vertexCount));
                data.Add("triangleCount", new JsonData(bakedMesh.triangles.Length / 3));
                data.Add("baked", new JsonData(bake));
                data.Add("meshPersisted", new JsonData(saveMesh));
                data.Add("meshAssetPath", new JsonData(meshAssetPath ?? ""));
                data.Add("meshGuid", new JsonData(meshGuid));
                data.Add("meshIsAsset", new JsonData((AssetDatabase.GetAssetPath(bakedMesh) ?? "")
                    .EndsWith(".asset", StringComparison.OrdinalIgnoreCase)));
                if (!string.IsNullOrEmpty(bakeWarning)) data.Add("warning", new JsonData(bakeWarning));
                data.Add("cells", Json.FromObject(cells));
                data.Add("hint", new JsonData(L.T(
                    "Paint per face_rect (one rectangle per axis group) instead of stretching a single motif across the whole cell; otherwise box projection clips the motif into six repeated fragments. The rebuilt mesh is an .asset unless save_mesh=false.",
                    "请按 face_rect（每个轴向面一个矩形）逐面绘制，不要把一整幅图案拉满整格：盒式投影会把它切成六块重复碎片。除非 save_mesh=false，重建后的 Mesh 已是 .asset。")));

                return Response.Success(L.T("UV atlas built.", "UV 图集已生成。"), data);
            }
            catch (Exception ex)
            {
                return Response.Error(L.T("UV atlas failed: ", "UV 图集构建失败：") + ex.Message);
            }
        }

        /// <summary>
        /// 只读查询当前 UV：连通岛、像素矩形、覆盖率与跨岛重叠（用于确认贴图不会串色）。
        /// </summary>
        [ToolAction("uv_info",
            DescriptionEn = "Inspect current UV0: connected islands, UV/pixel rectangles, coverage ratio and cross-island overlap.",
            DescriptionZh = "查询当前 UV0：连通岛、UV/像素矩形、覆盖率与跨岛重叠像素。",
            Parameters = new[] { "gameObject", "instanceId", "width", "height" },
            Required = new string[0])]
        private object UvInfo(JsonClass args)
        {
            try
            {
                var go = FindTarget(args, out var findError);
                if (findError != null) return Response.Error(findError);
                var filter = go.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    return Response.Error(L.T("Target has no MeshFilter/sharedMesh.", "目标没有 MeshFilter/sharedMesh。"));

                int width = Mathf.Clamp(args["width"].AsIntDefault(512), 1, 2048);
                int height = Mathf.Clamp(args["height"].AsIntDefault(512), 1, 2048);

                PbUvReport report;
                try { report = PbUvAtlas.Inspect(filter.sharedMesh, width, height); }
                catch (Exception e)
                {
                    return Response.Error(L.T("UV inspection failed: ", "UV 检查失败：") + e.Message);
                }

                var islands = new List<object>();
                foreach (var island in report.Islands)
                {
                    islands.Add(new
                    {
                        id = island.Id,
                        rect = new[] { F(island.UvMin.x), F(island.UvMin.y), F(island.UvMax.x), F(island.UvMax.y) },
                        rect_px = new[] { island.PxX, island.PxY, island.PxW, island.PxH },
                        triangles = island.Triangles,
                        covered_pixels = island.CoveredPixels
                    });
                }

                string inspectAssetPath = AssetDatabase.GetAssetPath(filter.sharedMesh) ?? "";
                var data = new JsonClass();
                data.Add("gameObjectName", new JsonData(go.name));
                data.Add("meshAssetPath", new JsonData(inspectAssetPath));
                data.Add("meshIsAsset", new JsonData(inspectAssetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)));
                data.Add("hasProBuilderMesh", new JsonData(go.GetComponent<ProBuilderMesh>() != null));
                data.Add("vertexCount", new JsonData(report.VertexCount));
                data.Add("triangleCount", new JsonData(report.TriangleCount));
                data.Add("islandCount", new JsonData(report.Islands.Count));
                data.Add("coverageRatio", new JsonData(report.CoverageRatio));
                data.Add("overlapPixels", new JsonData(report.OverlapPixels));
                data.Add("outOfRange", new JsonData(report.OutOfRange));
                data.Add("uvMin", new JsonData("(" + F(report.UvMin.x) + ", " + F(report.UvMin.y) + ")"));
                data.Add("uvMax", new JsonData("(" + F(report.UvMax.x) + ", " + F(report.UvMax.y) + ")"));
                data.Add("islands", Json.FromObject(islands));
                return Response.Success(L.T("UV layout inspected.", "UV 布局已检查。"), data);
            }
            catch (Exception ex)
            {
                return Response.Error(L.T("UV inspection failed: ", "UV 检查失败：") + ex.Message);
            }
        }

        private static float F(float value)
        {
            return Mathf.Round(value * 10000f) / 10000f;
        }

        private static string NormalizeMeshAssetPath(string requested, string goName)
        {
            string path = string.IsNullOrEmpty(requested)
                ? "Assets/Generated/Meshes/" + SafeAssetName(goName) + "_UvAtlas.asset"
                : requested.Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException(L.T(
                    "mesh_path must be inside Assets/ (e.g. Assets/Meshes/Tank.asset).",
                    "mesh_path 必须位于 Assets/ 下（例如 Assets/Meshes/Tank.asset）。"));
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) path += ".asset";
            return path;
        }

        /// <summary>Create folders via AssetDatabase (no full Refresh, which can drop the MCP response).</summary>
        private static void EnsureAssetFolder(string assetPath)
        {
            int slash = assetPath.LastIndexOf('/');
            if (slash <= 0) return;
            string folder = assetPath.Substring(0, slash);
            if (AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
            if (!AssetDatabase.IsValidFolder(folder))
                throw new ArgumentException("Cannot create asset folder: " + folder);
        }

        private static string SafeAssetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Mesh";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }
    }

    internal sealed class PbUvCell
    {
        public int Id;
        public int Column;
        public int Row;
        public Vector4 Rect;       // x=u0, y=v0, z=u1, w=v1
        public int Triangles;
        public int Vertices;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        /// <summary>Per-axis-group rectangles actually used by this cell (box projection splits a cell into up to 6).</summary>
        public readonly List<Vector4> FaceRects = new List<Vector4>();
        /// <summary>Axis group id 0..5 matching FaceRects (axis*2 + sign).</summary>
        public readonly List<int> FaceGroups = new List<int>();
    }

    internal sealed class PbUvIslandInfo
    {
        public int Id;
        public Vector2 UvMin;
        public Vector2 UvMax;
        public int Triangles;
        public int CoveredPixels;
        public int PxX, PxY, PxW, PxH;
    }

    internal sealed class PbUvBuildResult
    {
        public Mesh Mesh;
        public List<PbUvCell> Cells = new List<PbUvCell>();
        public int Rows;
        public int SourceVertices;
    }

    internal sealed class PbUvReport
    {
        public List<PbUvIslandInfo> Islands = new List<PbUvIslandInfo>();
        public int VertexCount;
        public int TriangleCount;
        public float CoverageRatio;
        public int OverlapPixels;
        public bool OutOfRange;
        public Vector2 UvMin;
        public Vector2 UvMax;
    }

    /// <summary>
    /// 纯数学实现：并查集求几何连通部件 → 每格分配图集矩形 → 按轴向投影 → 重建顶点。
    /// 不依赖 ProBuilder 内部 API，重建后的 Mesh 可被任何读取 Mesh.uv 的工具使用。
    /// </summary>
    internal static class PbUvAtlas
    {
        private const int GroupCount = 6;   // ±X, ±Y, ±Z
        private const float Pad = 0.04f;    // 轴向子矩形内部留白比例

        private sealed class Dsu
        {
            private readonly int[] _parent;
            public Dsu(int n)
            {
                _parent = new int[n];
                for (int i = 0; i < n; i++) _parent[i] = i;
            }
            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }
                return x;
            }
            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) _parent[rb] = ra;
            }
        }

        private sealed class Island
        {
            public readonly List<int> Tris = new List<int>();
            public readonly HashSet<int> Verts = new HashSet<int>();
            public Vector3 Min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            public Vector3 Max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        }

        private sealed class Group
        {
            public int Axis;
            public int Sign;
            public Vector2 Min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            public Vector2 Max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            public Vector4 Rect;
            public bool Valid;
        }

        public static PbUvBuildResult Build(Mesh src, int columns, int rowsRequested, float margin, bool box)
        {
            if (src == null) throw new ArgumentException("Mesh is null.");
            Vector3[] positions = src.vertices;
            Vector3[] normals = src.normals;
            int[] tris = AllTriangles(src);
            if (positions.Length == 0 || tris.Length == 0)
                throw new ArgumentException("Mesh has no vertices or triangles.");
            if (normals == null || normals.Length != positions.Length)
            {
                normals = new Vector3[positions.Length];
                for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            }

            // ---- 1. 几何连通部件：同位置顶点视为同一点，再按三角形边连接 ----
            var dsu = new Dsu(positions.Length);
            var firstAt = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < positions.Length; i++)
            {
                string key = PositionKey(positions[i]);
                int first;
                if (firstAt.TryGetValue(key, out first)) dsu.Union(i, first);
                else firstAt[key] = i;
            }
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                dsu.Union(tris[i], tris[i + 1]);
                dsu.Union(tris[i + 1], tris[i + 2]);
            }

            int triangleCount = tris.Length / 3;
            var rootToIsland = new Dictionary<int, int>();
            var islands = new List<Island>();
            var triIsland = new int[triangleCount];
            for (int t = 0; t < triangleCount; t++)
            {
                int root = dsu.Find(tris[t * 3]);
                int id;
                if (!rootToIsland.TryGetValue(root, out id))
                {
                    id = islands.Count;
                    rootToIsland[root] = id;
                    islands.Add(new Island());
                }
                triIsland[t] = id;
                var island = islands[id];
                island.Tris.Add(t);
                for (int k = 0; k < 3; k++)
                {
                    int vi = tris[t * 3 + k];
                    if (island.Verts.Add(vi))
                    {
                        Vector3 p = positions[vi];
                        island.Min = Vector3.Min(island.Min, p);
                        island.Max = Vector3.Max(island.Max, p);
                    }
                }
            }
            islands = islands.Where(i => i.Tris.Count > 0).ToList();
            // 重新映射（过滤后 id 仍连续）
            var remap = new Dictionary<int, int>();
            for (int i = 0; i < islands.Count; i++) remap[i] = i;
            for (int t = 0; t < triangleCount; t++)
                triIsland[t] = remap.ContainsKey(triIsland[t]) ? remap[triIsland[t]] : triIsland[t];

            // ---- 2. 图集格子 ----
            int rows = Mathf.Max(1, Mathf.CeilToInt((float)islands.Count / columns));
            if (rowsRequested > 0) rows = Mathf.Max(rows, rowsRequested);
            float cellW = 1f / columns;
            float cellH = 1f / rows;

            // ---- 3. 每个三角形归入轴向组，并统计每组的投影范围 ----
            int[] triGroup = new int[triangleCount];
            var groups = new Group[islands.Count * (box ? GroupCount : 1)];
            int groupsPerIsland = box ? GroupCount : 1;
            for (int i = 0; i < groups.Length; i++) groups[i] = new Group();

            for (int t = 0; t < triangleCount; t++)
            {
                int isl = triIsland[t];
                int axis = 1, sign = 0;
                if (box)
                {
                    Vector3 a = positions[tris[t * 3]];
                    Vector3 b = positions[tris[t * 3 + 1]];
                    Vector3 c = positions[tris[t * 3 + 2]];
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
                    if (ax >= ay && ax >= az) { axis = 0; sign = n.x < 0f ? 1 : 0; }
                    else if (ay >= az) { axis = 1; sign = n.y < 0f ? 1 : 0; }
                    else { axis = 2; sign = n.z < 0f ? 1 : 0; }
                }
                int g = axis * 2 + sign;
                if (!box) g = 0;
                triGroup[t] = g;
                var group = groups[isl * groupsPerIsland + (box ? g : 0)];
                group.Axis = axis;
                group.Sign = sign;
                group.Valid = true;
                for (int k = 0; k < 3; k++)
                {
                    Vector2 q = Project(positions[tris[t * 3 + k]], axis, sign);
                    group.Min = Vector2.Min(group.Min, q);
                    group.Max = Vector2.Max(group.Max, q);
                }
            }

            // ---- 4. 每格内部按轴向组切子矩形 ----
            for (int id = 0; id < islands.Count; id++)
            {
                int col = id % columns;
                int row = id / columns;
                float u0 = col * cellW + cellW * margin;
                float u1 = (col + 1) * cellW - cellW * margin;
                float v0 = row * cellH + cellH * margin;
                float v1 = (row + 1) * cellH - cellH * margin;
                if (u1 <= u0 || v1 <= v0)
                    throw new ArgumentException("margin leaves no room inside an atlas cell.");

                for (int g = 0; g < groupsPerIsland; g++)
                {
                    int gx = g % 3;
                    int gy = g / 3;
                    float sw = (u1 - u0) / 3f;
                    float sh = (v1 - v0) / (groupsPerIsland > 3 ? 2f : 1f);
                    float su0 = u0 + gx * sw + sw * Pad;
                    float su1 = u0 + (gx + 1) * sw - sw * Pad;
                    float sv0 = v0 + gy * sh + sh * Pad;
                    float sv1 = v0 + (gy + 1) * sh - sh * Pad;
                    groups[id * groupsPerIsland + g].Rect = new Vector4(su0, sv0, su1, sv1);
                }
            }

            // ---- 5. 重建顶点：同一 (原顶点, 轴向组) 复用新顶点，跨组自动分裂 ----
            var newPositions = new List<Vector3>();
            var newNormals = new List<Vector3>();
            var newUvs = new List<Vector2>();
            var newTris = new List<int>();
            var keyToNew = new Dictionary<long, int>();
            var cellVertexSets = new HashSet<int>[islands.Count];
            for (int i = 0; i < cellVertexSets.Length; i++) cellVertexSets[i] = new HashSet<int>();

            for (int t = 0; t < triangleCount; t++)
            {
                int isl = triIsland[t];
                int g = box ? triGroup[t] : 0;
                var group = groups[isl * groupsPerIsland + g];
                for (int k = 0; k < 3; k++)
                {
                    int vi = tris[t * 3 + k];
                    long key = (long)vi * 8L + g;
                    int ni;
                    if (!keyToNew.TryGetValue(key, out ni))
                    {
                        ni = newPositions.Count;
                        newPositions.Add(positions[vi]);
                        newNormals.Add(normals[vi]);
                        newUvs.Add(MapUv(positions[vi], group));
                        keyToNew[key] = ni;
                        cellVertexSets[isl].Add(ni);
                    }
                    newTris.Add(ni);
                }
            }

            var mesh = new Mesh();
            mesh.name = (string.IsNullOrEmpty(src.name) ? "PbUvAtlas" : src.name) + "_UvAtlas";
            if (newPositions.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(newPositions);
            mesh.SetNormals(newNormals);
            mesh.SetUVs(0, newUvs);
            mesh.SetTriangles(newTris, 0);
            mesh.RecalculateBounds();

            var result = new PbUvBuildResult
            {
                Mesh = mesh,
                Rows = rows,
                SourceVertices = positions.Length
            };
            for (int id = 0; id < islands.Count; id++)
            {
                int col = id % columns;
                int row = id / columns;
                float u0 = col * cellW + cellW * margin;
                float u1 = (col + 1) * cellW - cellW * margin;
                float v0 = row * cellH + cellH * margin;
                float v1 = (row + 1) * cellH - cellH * margin;
                var cell = new PbUvCell
                {
                    Id = id,
                    Column = col,
                    Row = row,
                    Rect = new Vector4(u0, v0, u1, v1),
                    Triangles = islands[id].Tris.Count,
                    Vertices = cellVertexSets[id].Count,
                    BoundsMin = islands[id].Min,
                    BoundsMax = islands[id].Max
                };
                // Only axis groups that actually have triangles get a rectangle.
                // Callers should paint per face_rect instead of stretching one
                // drawing across the whole cell (which produces clipped, repeated motifs).
                for (int g = 0; g < groupsPerIsland; g++)
                {
                    Group group = groups[id * groupsPerIsland + g];
                    if (!group.Valid) continue;
                    cell.FaceGroups.Add(box ? g : 0);
                    cell.FaceRects.Add(group.Rect);
                }
                result.Cells.Add(cell);
            }
            return result;
        }

        /// <summary>
        /// 只读分析 UV0：按 (顶点索引, UV) 共享边做连通岛，栅格化统计覆盖与跨岛重叠。
        /// </summary>
        public static PbUvReport Inspect(Mesh mesh, int width, int height)
        {
            Vector2[] uv = mesh.uv;
            int[] tris = AllTriangles(mesh);
            if (uv == null || uv.Length == 0) throw new ArgumentException("Mesh has no UV0.");
            if (uv.Length < mesh.vertexCount) throw new ArgumentException("UV0 is shorter than the vertex array.");
            int triangleCount = tris.Length / 3;

            var edgeOwner = new Dictionary<string, int>(StringComparer.Ordinal);
            var dsu = new Dsu(triangleCount);
            for (int t = 0; t < triangleCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int i0 = tris[t * 3 + e];
                    int i1 = tris[t * 3 + (e + 1) % 3];
                    string key = EdgeKey(i0, uv[i0], i1, uv[i1]);
                    int other;
                    if (edgeOwner.TryGetValue(key, out other)) dsu.Union(t, other);
                    else edgeOwner[key] = t;
                }
            }

            var rootToIsland = new Dictionary<int, int>();
            var triIsland = new int[triangleCount];
            var islands = new List<PbUvIslandInfo>();
            for (int t = 0; t < triangleCount; t++)
            {
                int root = dsu.Find(t);
                int id;
                if (!rootToIsland.TryGetValue(root, out id))
                {
                    id = islands.Count;
                    rootToIsland[root] = id;
                    islands.Add(new PbUvIslandInfo
                    {
                        Id = id,
                        UvMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity),
                        UvMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity)
                    });
                }
                triIsland[t] = id;
                var island = islands[id];
                island.Triangles++;
                for (int k = 0; k < 3; k++)
                {
                    Vector2 q = uv[tris[t * 3 + k]];
                    island.UvMin = Vector2.Min(island.UvMin, q);
                    island.UvMax = Vector2.Max(island.UvMax, q);
                }
            }

            // 栅格化：统计覆盖率与跨岛重叠（像素级）
            int rw = Mathf.Clamp(width, 1, 512);
            int rh = Mathf.Clamp(height, 1, 512);
            int[] owner = new int[rw * rh];
            bool[] multi = new bool[rw * rh];
            for (int i = 0; i < owner.Length; i++) owner[i] = -1;

            Vector2 uvMinAll = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 uvMaxAll = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int t = 0; t < triangleCount; t++)
            {
                Vector2 a = uv[tris[t * 3]], b = uv[tris[t * 3 + 1]], c = uv[tris[t * 3 + 2]];
                uvMinAll = Vector2.Min(uvMinAll, Vector2.Min(a, Vector2.Min(b, c)));
                uvMaxAll = Vector2.Max(uvMaxAll, Vector2.Max(a, Vector2.Max(b, c)));
                Rasterize(owner, multi, rw, rh, a, b, c, triIsland[t]);
            }

            int covered = 0, overlap = 0;
            for (int i = 0; i < owner.Length; i++)
            {
                if (owner[i] >= 0) covered++;
                if (multi[i]) overlap++;
            }

            var report = new PbUvReport
            {
                VertexCount = mesh.vertexCount,
                TriangleCount = triangleCount,
                CoverageRatio = (float)covered / Mathf.Max(1, owner.Length),
                OverlapPixels = overlap,
                OutOfRange = uvMinAll.x < -1e-4f || uvMinAll.y < -1e-4f ||
                             uvMaxAll.x > 1.0001f || uvMaxAll.y > 1.0001f,
                UvMin = uvMinAll,
                UvMax = uvMaxAll,
                Islands = islands
            };
            foreach (var island in islands)
            {
                int x = Mathf.Clamp(Mathf.FloorToInt(island.UvMin.x * width), 0, width);
                int y = Mathf.Clamp(Mathf.FloorToInt(island.UvMin.y * height), 0, height);
                int x2 = Mathf.Clamp(Mathf.CeilToInt(island.UvMax.x * width), 0, width);
                int y2 = Mathf.Clamp(Mathf.CeilToInt(island.UvMax.y * height), 0, height);
                island.PxX = x;
                island.PxY = y;
                island.PxW = Mathf.Max(0, x2 - x);
                island.PxH = Mathf.Max(0, y2 - y);
            }
            return report;
        }

        private static void Rasterize(int[] owner, bool[] multi, int w, int h,
            Vector2 a, Vector2 b, Vector2 c, int id)
        {
            float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
            float maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
            int x0 = Mathf.Clamp(Mathf.FloorToInt(minX * w), 0, w - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(maxX * w), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(minY * h), 0, h - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(maxY * h), 0, h - 1);
            float area = Cross(b - a, c - a);
            if (Mathf.Abs(area) < 1e-12f) return;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Vector2 p = new Vector2((x + 0.5f) / w, (y + 0.5f) / h);
                    float w0 = Cross(b - a, p - a) / area;
                    float w1 = Cross(c - b, p - b) / area;
                    float w2 = Cross(a - c, p - c) / area;
                    if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                    int idx = y * w + x;
                    if (owner[idx] < 0) owner[idx] = id;
                    else if (owner[idx] != id) multi[idx] = true;
                }
            }
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static string EdgeKey(int i0, Vector2 uv0, int i1, Vector2 uv1)
        {
            bool swap = i0 > i1 || (i0 == i1 && (uv0.x > uv1.x ||
                (Mathf.Approximately(uv0.x, uv1.x) && uv0.y > uv1.y)));
            if (swap)
            {
                int ti = i0; i0 = i1; i1 = ti;
                Vector2 tv = uv0; uv0 = uv1; uv1 = tv;
            }
            return i0.ToString(CultureInfo.InvariantCulture) + ":" + Q(uv0.x) + "," + Q(uv0.y) +
                   "|" + i1.ToString(CultureInfo.InvariantCulture) + ":" + Q(uv1.x) + "," + Q(uv1.y);
        }

        private static string Q(float value)
        {
            return Mathf.Round(value * 100000f).ToString(CultureInfo.InvariantCulture);
        }

        private static string PositionKey(Vector3 p)
        {
            return Mathf.RoundToInt(p.x * 10000f).ToString(CultureInfo.InvariantCulture) + "_" +
                   Mathf.RoundToInt(p.y * 10000f).ToString(CultureInfo.InvariantCulture) + "_" +
                   Mathf.RoundToInt(p.z * 10000f).ToString(CultureInfo.InvariantCulture);
        }

        private static int[] AllTriangles(Mesh mesh)
        {
            var list = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) list.AddRange(mesh.GetTriangles(s));
            return list.ToArray();
        }

        private static Vector2 Project(Vector3 p, int axis, int sign)
        {
            Vector2 q;
            switch (axis)
            {
                case 0: q = new Vector2(p.z, p.y); break;
                case 1: q = new Vector2(p.x, p.z); break;
                default: q = new Vector2(p.x, p.y); break;
            }
            if (sign != 0) q.x = -q.x;
            return q;
        }

        private static Vector2 MapUv(Vector3 p, Group group)
        {
            Vector4 r = group.Rect;
            Vector2 q = Project(p, group.Axis, group.Sign);
            float spanX = Mathf.Max(1e-6f, group.Max.x - group.Min.x);
            float spanY = Mathf.Max(1e-6f, group.Max.y - group.Min.y);
            float nx = Mathf.Clamp01((q.x - group.Min.x) / spanX);
            float ny = Mathf.Clamp01((q.y - group.Min.y) / spanY);
            return new Vector2(
                Mathf.Lerp(r.x, r.z, nx),
                Mathf.Lerp(r.y, r.w, ny));
        }
    }
}

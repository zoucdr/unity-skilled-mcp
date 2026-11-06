/*-*-* Copyright (c) uframe@zoucdr
 * Description: ProBuilder MCP 工具共享辅助方法（UniMcp 框架）。
 * 由原 ProBuilder HTTP 技能服务的共享逻辑直接移植，行为保持一致。
 *_*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UniMcp;

namespace ProBuilder.SkilledMcp
{
    /// <summary>
    /// ProBuilder MCP 工具的共享解析/查找/选择逻辑。
    /// </summary>
    internal static class ProBuilderMcpUtil
    {
        #region 参数读取

        /// <summary>读取原始字符串值（保留空格），键缺失返回 null。</summary>
        public static string GetString(JsonClass args, string key)
        {
            if (args == null || !args.TryGetValue(key, out JsonNode node) || node == null)
                return null;
            return node.Value;
        }

        /// <summary>读取并去除首尾空白。</summary>
        public static string GetTrimmedString(JsonClass args, string key)
        {
            string value = GetString(args, key);
            return value == null ? null : value.Trim();
        }

        /// <summary>读取 Vector3，支持 [x,y,z] 数组或 "x,y,z" 字符串。</summary>
        public static Vector3? GetVector3(JsonClass args, string key)
        {
            if (args == null || !args.TryGetValue(key, out JsonNode node) || node == null)
                return null;

            if (node is JsonArray array && array.Count >= 3)
            {
                return new Vector3(
                    array[0].AsFloatDefault(0f),
                    array[1].AsFloatDefault(0f),
                    array[2].AsFloatDefault(0f));
            }

            return ParseVector3String(node.Value);
        }

        private static Vector3? ParseVector3String(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            string s = raw.Replace('[', ' ').Replace(']', ' ').Replace(',', ' ');
            var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 &&
                float.TryParse(parts[0], out float x) &&
                float.TryParse(parts[1], out float y) &&
                float.TryParse(parts[2], out float z))
            {
                return new Vector3(x, y, z);
            }
            return null;
        }

        /// <summary>读取 int 数组（JSON 数组 [1,2,3]）。</summary>
        public static int[] GetIntArray(JsonClass args, string key)
        {
            if (args == null || !args.TryGetValue(key, out JsonNode node) || !(node is JsonArray array))
                return null;
            if (array.Count == 0)
                return null;

            var result = new int[array.Count];
            for (int i = 0; i < array.Count; i++)
                result[i] = array[i].AsIntDefault(0);
            return result;
        }

        /// <summary>读取单个顶点对：[a,b] 或 "a-b"/"a,b"。</summary>
        public static int[] GetIntPair(JsonClass args, string key)
        {
            if (args == null || !args.TryGetValue(key, out JsonNode node) || node == null)
                return null;

            if (node is JsonArray array && array.Count >= 2)
                return new[] { array[0].AsIntDefault(0), array[1].AsIntDefault(0) };

            return ParseIntPair(node.Value);
        }

        /// <summary>读取边列表：[[a,b],[c,d],...] 或 "a-b,c-d"。</summary>
        public static List<int[]> GetEdgePairs(JsonClass args, string key)
        {
            if (args == null || !args.TryGetValue(key, out JsonNode node) || node == null)
                return null;

            if (node is JsonArray array)
            {
                var parsed = new List<int[]>();
                for (int i = 0; i < array.Count; i++)
                {
                    var item = array[i];
                    if (item is JsonArray inner && inner.Count >= 2)
                    {
                        parsed.Add(new[]
                        {
                            inner[0].AsIntDefault(0),
                            inner[1].AsIntDefault(0)
                        });
                    }
                    else if (item != null)
                    {
                        var pair = ParseIntPair(item.Value);
                        if (pair != null) parsed.Add(pair);
                    }
                }
                return parsed.Count > 0 ? parsed : null;
            }

            return ParseIntPairList(node.Value);
        }

        /// <summary>读取 2D 点列表：[[x,z],[x,z],...] 或 "x,z;x,z;..."。</summary>
        public static List<float[]> GetFloatPairs(JsonClass args, string key)
        {
            if (args == null || !args.TryGetValue(key, out JsonNode node) || node == null)
                return null;

            if (node is JsonArray array)
            {
                var parsed = new List<float[]>();
                for (int i = 0; i < array.Count; i++)
                {
                    var item = array[i];
                    if (item is JsonArray inner && inner.Count >= 2)
                    {
                        parsed.Add(new[]
                        {
                            inner[0].AsFloatDefault(0f),
                            inner[1].AsFloatDefault(0f)
                        });
                    }
                    else if (item != null)
                    {
                        var pair = ParseFloatPair(item.Value);
                        if (pair != null) parsed.Add(pair);
                    }
                }
                return parsed.Count > 0 ? parsed : null;
            }

            return ParseFloatPairList(node.Value);
        }

        private static int[] ParseIntPair(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var nums = Regex.Matches(value, @"-?\d+");
            if (nums.Count >= 2)
                return new[] { int.Parse(nums[0].Value), int.Parse(nums[1].Value) };
            return null;
        }

        private static List<int[]> ParseIntPairList(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim();
            var result = new List<int[]>();

            if (raw.StartsWith("["))
            {
                var pairs = Regex.Matches(raw, @"\[\s*(-?\d+)\s*,\s*(-?\d+)\s*\]");
                foreach (Match m in pairs)
                    result.Add(new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value) });
                return result.Count > 0 ? result : null;
            }

            var parts = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var nums = Regex.Matches(part.Trim(), @"-?\d+");
                if (nums.Count >= 2)
                    result.Add(new[] { int.Parse(nums[0].Value), int.Parse(nums[1].Value) });
            }
            return result.Count > 0 ? result : null;
        }

        private static float[] ParseFloatPair(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var nums = Regex.Matches(value, @"-?[\d\.]+");
            if (nums.Count >= 2)
                return new[] { float.Parse(nums[0].Value), float.Parse(nums[1].Value) };
            return null;
        }

        private static List<float[]> ParseFloatPairList(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim();
            var result = new List<float[]>();

            if (raw.StartsWith("["))
            {
                var pairs = Regex.Matches(raw, @"\[\s*(-?[\d\.]+)\s*,\s*(-?[\d\.]+)\s*\]");
                foreach (Match m in pairs)
                    result.Add(new[] { float.Parse(m.Groups[1].Value), float.Parse(m.Groups[2].Value) });
                return result.Count > 0 ? result : null;
            }

            var groups = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var group in groups)
            {
                var nums = Regex.Matches(group.Trim(), @"-?[\d\.]+");
                if (nums.Count >= 2)
                    result.Add(new[] { float.Parse(nums[0].Value), float.Parse(nums[1].Value) });
            }
            return result.Count > 0 ? result : null;
        }

        #endregion

        #region 对象查找

        /// <summary>按名称或层级路径查找对象（含全场景同名兜底）。</summary>
        private static GameObject FindByNameOrPath(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var go = GameObjectUtils.FindObjectByIdOrPath(name);
            if (go != null) return go;

            go = GameObject.Find(name);
            if (go != null) return go;

            var all = UnityEngine.Object.FindObjectsOfType<GameObject>();
            return all.FirstOrDefault(g => g.name == name);
        }

        /// <summary>查找目标 GameObject：优先 instanceId，其次 gameObject 名称/层级路径。</summary>
        public static GameObject FindTarget(JsonClass args, out string error)
            => FindTarget(args, out error, "gameObject");

        /// <summary>
        /// 查找目标 GameObject，名称键可指定（布尔运算的文档参数名为 target），
        /// 同时兼容 gameObject 键。
        /// </summary>
        public static GameObject FindTarget(JsonClass args, out string error, params string[] nameKeys)
        {
            error = null;

            if (args != null && args.TryGetValue("instanceId", out JsonNode idNode) && idNode != null && !idNode.IsNull())
            {
                int id = idNode.AsIntDefault(0);
                var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (go == null)
                {
                    error = L.T(
                        $"GameObject with instanceId {id} was not found (the scene may have been reloaded).",
                        $"未找到 instanceId 为 {id} 的 GameObject（场景可能已重载）");
                    return null;
                }
                return go;
            }

            string name = null;
            if (nameKeys != null)
            {
                foreach (var key in nameKeys)
                {
                    name = GetString(args, key);
                    if (!string.IsNullOrEmpty(name)) break;
                }
            }
            if (string.IsNullOrEmpty(name)) name = GetString(args, "gameObject");

            if (!string.IsNullOrEmpty(name))
            {
                var go = FindByNameOrPath(name);
                if (go == null)
                {
                    error = L.T($"GameObject named '{name}' was not found.", $"未找到名为 '{name}' 的 GameObject");
                    return null;
                }
                return go;
            }

            error = L.T(
                "No target GameObject specified: provide 'instanceId' or 'gameObject' (name or hierarchy path).",
                "未指定目标 GameObject：请提供 'instanceId' 或 'gameObject'（名称或层级路径）");
            return null;
        }

        /// <summary>查找父对象（可选）。</summary>
        public static GameObject FindParent(JsonClass args, out string error)
        {
            error = null;
            string parent = GetString(args, "parent");
            if (string.IsNullOrEmpty(parent)) return null;

            var go = FindByNameOrPath(parent);
            if (go == null)
                error = L.T($"Parent object '{parent}' was not found.", $"未找到父对象 '{parent}'");
            return go;
        }

        /// <summary>查找布尔运算工具对象：优先 toolInstanceId，其次 tool 名称。</summary>
        public static GameObject FindTool(JsonClass args, out string error)
        {
            error = null;

            if (args != null && args.TryGetValue("toolInstanceId", out JsonNode idNode) && idNode != null && !idNode.IsNull())
            {
                int id = idNode.AsIntDefault(0);
                var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (go == null)
                {
                    error = L.T(
                        $"GameObject with toolInstanceId {id} was not found (the scene may have been reloaded).",
                        $"未找到 toolInstanceId 为 {id} 的 GameObject（场景可能已重载）");
                    return null;
                }
                return go;
            }

            string name = GetString(args, "tool");
            if (!string.IsNullOrEmpty(name))
            {
                var go = FindByNameOrPath(name);
                if (go == null)
                {
                    error = L.T(
                        $"Boolean tool object named '{name}' was not found.",
                        $"未找到名为 '{name}' 的布尔工具对象");
                    return null;
                }
                return go;
            }

            error = L.T(
                "No boolean tool object specified: provide 'tool' (name) or 'toolInstanceId'.",
                "未指定布尔工具对象：请提供 'tool'（名称）或 'toolInstanceId'");
            return null;
        }

        /// <summary>解析合并目标列表：instanceIds 数组 / gameObjects 名称数组 / 单个 gameObject。</summary>
        public static List<GameObject> ResolveTargets(JsonClass args, out string error)
        {
            error = null;
            var result = new List<GameObject>();

            if (args != null && args.TryGetValue("instanceIds", out JsonNode idsNode) &&
                idsNode is JsonArray idsArray && idsArray.Count > 0)
            {
                for (int i = 0; i < idsArray.Count; i++)
                {
                    int id = idsArray[i].AsIntDefault(0);
                    var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                    if (go == null)
                    {
                        error = L.T(
                            $"GameObject with instanceId {id} was not found.",
                            $"未找到 instanceId 为 {id} 的 GameObject");
                        return null;
                    }
                    result.Add(go);
                }
                return result;
            }

            if (args != null && args.TryGetValue("gameObjects", out JsonNode namesNode) &&
                namesNode is JsonArray namesArray && namesArray.Count > 0)
            {
                for (int i = 0; i < namesArray.Count; i++)
                {
                    string n = namesArray[i]?.Value;
                    if (string.IsNullOrEmpty(n))
                    {
                        error = L.T("'gameObjects' must contain object names.", "'gameObjects' 必须包含对象名称");
                        return null;
                    }
                    var go = FindByNameOrPath(n);
                    if (go == null)
                    {
                        error = L.T($"GameObject named '{n}' was not found.", $"未找到名为 '{n}' 的 GameObject");
                        return null;
                    }
                    result.Add(go);
                }
                return result;
            }

            string single = GetString(args, "gameObject");
            if (!string.IsNullOrEmpty(single))
            {
                var go = FindTarget(args, out error);
                if (go != null) result.Add(go);
                return result;
            }

            error = L.T(
                "No merge target specified: provide 'gameObjects' (name array) or 'instanceIds' (integer array).",
                "未指定合并目标：请提供 'gameObjects'（名称数组）或 'instanceIds'（整数数组）");
            return null;
        }

        /// <summary>获取 ProBuilderMesh 组件，缺失时给出错误信息。</summary>
        public static ProBuilderMesh GetMesh(GameObject go, out string error)
        {
            error = null;
            if (go == null)
            {
                error = L.T("Target GameObject is null.", "目标 GameObject 为空");
                return null;
            }

            var pb = go.GetComponent<ProBuilderMesh>();
            if (pb == null)
            {
                error = L.T(
                    $"GameObject '{go.name}' has no ProBuilderMesh component (instanceId={go.GetInstanceID()}).",
                    $"GameObject '{go.name}' 上没有 ProBuilderMesh 组件（instanceId={go.GetInstanceID()}）");
            }
            return pb;
        }

        #endregion

        #region 面/边选择

        /// <summary>解析面选择：faceIndices 或 faceDirection（二选一）。</summary>
        public static (int[] indices, string method, string error) ResolveFaceSelection(JsonClass args, ProBuilderMesh pb)
        {
            var faceIndices = GetIntArray(args, "faceIndices");
            string faceDirectionStr = GetTrimmedString(args, "faceDirection");

            if (faceIndices != null && faceIndices.Length > 0)
            {
                var faces = pb.faces;
                var invalid = faceIndices.Where(i => i < 0 || i >= faces.Count).ToList();
                if (invalid.Count > 0)
                    return (null, null, L.T(
                        $"Invalid face indices: {string.Join(", ", invalid)}. Valid range is 0 to {faces.Count - 1}.",
                        $"无效的面索引: {string.Join(", ", invalid)}。有效范围 0 到 {faces.Count - 1}"));
                return (faceIndices, "by index", null);
            }

            if (!string.IsNullOrEmpty(faceDirectionStr))
            {
                var sel = SelectFacesByDirection(pb, faceDirectionStr, out var selErr);
                if (selErr != null) return (null, null, selErr);
                if (sel.Length == 0)
                    return (null, null, L.T(
                        $"No faces face '{faceDirectionStr}'.",
                        $"没有朝向 '{faceDirectionStr}' 的面"));
                return (sel, $"by direction '{faceDirectionStr}'", null);
            }

            return (null, null, L.T(
                "Either faceIndices or faceDirection must be provided.",
                "必须提供 faceIndices 或 faceDirection 之一"));
        }

        /// <summary>解析边选择：edges 或 faceDirection（二选一）。</summary>
        public static (List<Edge> edges, string method, string error) ResolveEdgeSelection(JsonClass args, ProBuilderMesh pb)
        {
            var pairList = GetEdgePairs(args, "edges");
            string faceDirectionStr = GetTrimmedString(args, "faceDirection");

            if (pairList != null && pairList.Count > 0)
            {
                int vertexCount = pb.vertexCount;
                var edges = new List<Edge>();
                foreach (var pair in pairList)
                {
                    if (pair[0] < 0 || pair[0] >= vertexCount)
                        return (null, null, L.T(
                            $"Vertex index {pair[0]} is out of range 0 to {vertexCount - 1}.",
                            $"顶点索引 {pair[0]} 超出范围 0 到 {vertexCount - 1}"));
                    if (pair[1] < 0 || pair[1] >= vertexCount)
                        return (null, null, L.T(
                            $"Vertex index {pair[1]} is out of range 0 to {vertexCount - 1}.",
                            $"顶点索引 {pair[1]} 超出范围 0 到 {vertexCount - 1}"));
                    edges.Add(new Edge(pair[0], pair[1]));
                }
                return (edges, "by vertex indices", null);
            }

            if (!string.IsNullOrEmpty(faceDirectionStr))
            {
                var sel = SelectFacesByDirection(pb, faceDirectionStr, out var selErr);
                if (selErr != null) return (null, null, selErr);

                var faces = pb.faces;
                var edges = new List<Edge>();
                foreach (var faceIndex in sel)
                    edges.AddRange(faces[faceIndex].edges);
                edges = edges.Distinct().ToList();

                if (edges.Count == 0)
                    return (null, null, L.T(
                        $"No faces face '{faceDirectionStr}'.",
                        $"没有朝向 '{faceDirectionStr}' 的面"));
                return (edges, $"from faces facing '{faceDirectionStr}'", null);
            }

            return (null, null, L.T(
                "Either edges or faceDirection must be provided.",
                "必须提供 edges 或 faceDirection 之一"));
        }

        /// <summary>方向选面：按面的世界空间法线与方向点积筛选（阈值 0.5）。</summary>
        public static int[] SelectFacesByDirection(ProBuilderMesh pb, string direction, out string error)
        {
            error = null;
            var dirVector = DirectionVector(direction);
            if (!dirVector.HasValue)
            {
                error = L.T(
                    $"Invalid direction '{direction}' (Up/Down/Left/Right/Forward/Back).",
                    $"无效方向 '{direction}' (Up/Down/Left/Right/Forward/Back)");
                return new int[0];
            }

            var summary = GetFaceDirectionSummary(pb, out _);
            var target = summary.FirstOrDefault(kvp => kvp.Key.Equals(direction, StringComparison.OrdinalIgnoreCase));
            if (target.Value != null && target.Value.Count > 0)
                return target.Value.ToArray();

            var faces = pb.faces;
            var positions = pb.positions;
            var result = new List<int>();
            for (int i = 0; i < faces.Count; i++)
            {
                var n = FaceWorldNormal(pb, faces[i], positions);
                if (Vector3.Dot(n, dirVector.Value) > 0.5f)
                    result.Add(i);
            }
            if (result.Count == 0)
                error = L.T(
                    $"No faces face '{direction}' (use get_mesh_info to inspect faceDirections first).",
                    $"没有朝向 '{direction}' 的面（可先用 get_mesh_info 查看 faceDirections）");
            return result.ToArray();
        }

        /// <summary>面方向摘要：按 6 个方向分组，未命中的归入 otherFaces。</summary>
        public static Dictionary<string, List<int>> GetFaceDirectionSummary(ProBuilderMesh pb, out List<int> otherFaces)
        {
            var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            result["Up"] = new List<int>();
            result["Down"] = new List<int>();
            result["Left"] = new List<int>();
            result["Right"] = new List<int>();
            result["Forward"] = new List<int>();
            result["Back"] = new List<int>();
            otherFaces = new List<int>();

            var faces = pb.faces;
            var positions = pb.positions;
            var dirs = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase)
            {
                ["Up"] = Vector3.up,
                ["Down"] = Vector3.down,
                ["Left"] = Vector3.left,
                ["Right"] = Vector3.right,
                ["Forward"] = Vector3.forward,
                ["Back"] = Vector3.back
            };

            for (int i = 0; i < faces.Count; i++)
            {
                var n = FaceWorldNormal(pb, faces[i], positions);
                float bestDot = 0.5f;
                string bestDir = null;
                foreach (var kvp in dirs)
                {
                    float dot = Vector3.Dot(n, kvp.Value);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        bestDir = kvp.Key;
                    }
                }
                if (bestDir != null)
                    result[bestDir].Add(i);
                else
                    otherFaces.Add(i);
            }
            return result;
        }

        /// <summary>计算面中心（模型空间）。</summary>
        public static Vector3 GetFaceCenter(Face face, IList<Vector3> positions)
        {
            var idxs = face.distinctIndexes;
            var center = Vector3.zero;
            foreach (var i in idxs) center += positions[i];
            if (idxs.Count > 0) center /= idxs.Count;
            return center;
        }

        /// <summary>计算面的世界空间单位法线（取第一个三角形，考虑顶点绕序）。</summary>
        public static Vector3 FaceWorldNormal(ProBuilderMesh pb, Face face, IList<Vector3> positions)
        {
            var idxs = face.indexes;
            if (idxs.Count < 3) return Vector3.up;
            var a = pb.transform.TransformPoint(positions[idxs[0]]);
            var b = pb.transform.TransformPoint(positions[idxs[1]]);
            var c = pb.transform.TransformPoint(positions[idxs[2]]);
            var n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 0.000001f) return Vector3.up;
            return n.normalized;
        }

        /// <summary>方向字符串 → 方向向量。</summary>
        public static Vector3? DirectionVector(string direction)
        {
            if (string.IsNullOrEmpty(direction)) return null;
            switch (direction.ToLower())
            {
                case "up": return Vector3.up;
                case "down": return Vector3.down;
                case "left": return Vector3.left;
                case "right": return Vector3.right;
                case "forward": return Vector3.forward;
                case "back": return Vector3.back;
                default: return null;
            }
        }

        #endregion

        #region 工具方法

        /// <summary>格式化 Vector3 为 "(x, y, z)" 字符串。</summary>
        public static string FormatVector3(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";

        /// <summary>重绘编辑器窗口。</summary>
        public static void RepaintEditor()
        {
            SceneView.RepaintAll();
            EditorApplication.RepaintHierarchyWindow();
            EditorApplication.RepaintProjectWindow();
        }

        #endregion
    }
}

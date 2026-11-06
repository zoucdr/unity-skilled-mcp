using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    public interface IStrictToolModule { }

    internal static class ExtensionSafety
    {
        internal static object Run(Func<object> action)
        {
            try { return action(); }
            catch (Exception e) { return Response.Error(e.GetBaseException().Message); }
        }

        internal static void ValidateArguments(ToolSpec spec, JsonClass args)
        {
            var names = spec.Parameters.Select(k => k.Key).ToArray();
            foreach (System.Collections.Generic.KeyValuePair<string, JsonNode> pair in args)
                if (!names.Contains(pair.Key)) throw new ArgumentException("未知参数: " + pair.Key);
            foreach (string key in spec.Required ?? new string[0])
                if (!args.ContainsKey(key)) throw new ArgumentException("缺少必填参数: " + key);
            foreach (var key in spec.Parameters.Where(k => args.ContainsKey(k.Key))) {
                if (key is MethodVector) Vector(args, key.Key);
                else if (key is MethodInt) Integer(args, key.Key);
                else if (key is MethodFloat) Number(args, key.Key);
                else if (key is MethodBool) Flag(args, key.Key);
                else if (key is MethodStr) {
                    string value = Text(args, key.Key);
                    if (key.EnumValues != null && key.EnumValues.Count > 0 && !key.EnumValues.Contains(value)) throw new ArgumentException("参数枚举值无效: " + key.Key);
                }
                else if (key is MethodArr arr) {
                    var values = args[key.Key] as JsonArray; if (values == null) throw new ArgumentException(key.Key + "必须是数组");
                    for (int i = 0; i < values.Count; i++) {
                        var item = new JsonClass(); item.Add("value", values[i]);
                        if (arr.ItemType == "integer") Integer(item, "value");
                        else if (arr.ItemType == "string") Text(item, "value");
                    }
                }
                else if (key is MethodObj && !(args[key.Key] is JsonClass)) throw new ArgumentException(key.Key + "必须是对象");
            }
        }

        internal static string Text(JsonClass a, string key, string fallback = null)
        {
            if (!a.ContainsKey(key))
            {
                if (fallback != null) return fallback;
                throw new ArgumentException("缺少参数: " + key);
            }
            var node = a[key];
            if (node == null || ((node as JsonData)?.ParsedAsString != true && !node.ToString().StartsWith("\"", StringComparison.Ordinal)) || string.IsNullOrWhiteSpace(node.Value))
                throw new ArgumentException(key + " 必须是非空字符串");
            return node.Value;
        }

        internal static float Number(JsonClass a, string key, float fallback = 0, float min = float.MinValue, float max = float.MaxValue)
        {
            if (!a.ContainsKey(key)) return fallback;
            if ((a[key] as JsonData)?.ParsedAsString == true) throw new ArgumentException(key + "不能是字符串");
            float value;
            if (!float.TryParse(a[key].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
                throw new ArgumentException(key + " 必须是范围内的有限数字 [" + min + ", " + max + "]");
            return value;
        }

        internal static int Integer(JsonClass a, string key, int fallback = 0, int min = int.MinValue, int max = int.MaxValue)
        {
            if (!a.ContainsKey(key)) return fallback;
            if ((a[key] as JsonData)?.ParsedAsString == true) throw new ArgumentException(key + "不能是字符串");
            int value;
            if (!int.TryParse(a[key].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max)
                throw new ArgumentException(key + " 必须是范围内的整数");
            return value;
        }

        internal static bool Flag(JsonClass a, string key, bool fallback = false)
        {
            if (!a.ContainsKey(key)) return fallback;
            if ((a[key] as JsonData)?.ParsedAsString == true) throw new ArgumentException(key + "不能是字符串");
            var raw = a[key].ToString();
            if (raw != "true" && raw != "false") throw new ArgumentException(key + " 必须是布尔值");
            return raw == "true";
        }

        internal static Vector3 Vector(JsonClass a, string key)
        {
            var values = a[key] as JsonArray;
            if (values == null || values.Count != 3) throw new ArgumentException(key + " 必须包含3个有限数字");
            for (int i = 0; i < 3; i++) if ((values[i] as JsonData)?.ParsedAsString == true) throw new ArgumentException(key + " 必须包含3个有限数字");
            var o = new JsonClass();
            for (int i = 0; i < 3; i++) o.Add(i.ToString(), values[i]);
            return new Vector3(Number(o, "0"), Number(o, "1"), Number(o, "2"));
        }

        internal static string[] Strings(JsonClass a, string key, int max = 256)
        {
            var array = a[key] as JsonArray;
            if (array == null || array.Count == 0 || array.Count > max) throw new ArgumentException(key + " 必须是非空数组，最大长度 " + max);
            var result = new string[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                var o = new JsonClass(); o.Add("value", array[i]); result[i] = Text(o, "value");
            }
            return result;
        }

        internal static GameObject Target(JsonClass a, string key = "instance_id")
        {
            if (!a.ContainsKey(key)) throw new ArgumentException("缺少参数: " + key);
            var obj = EditorUtility.InstanceIDToObject(Integer(a, key));
            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (!go || EditorUtility.IsPersistent(go) || !go.scene.IsValid() || !go.scene.isLoaded)
                throw new ArgumentException(key + " 必须指向已加载场景中的对象");
            return go;
        }

        internal static T Component<T>(JsonClass a) where T : Component
        {
            var c = Target(a).GetComponent<T>();
            if (!c) throw new ArgumentException("目标缺少组件 " + typeof(T).Name);
            return c;
        }

        internal static void Dirty(UnityEngine.Object obj)
        {
            EditorUtility.SetDirty(obj);
            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go && go.scene.IsValid() && !Application.isPlaying)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
        }

        internal static string AssetPath(string path, string extension = null, bool exists = true)
        {
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || path.Split('/').Any(p => p == ".." || p == "." || p.Length == 0) || path.IndexOf(':') >= 0)
                throw new ArgumentException("路径必须位于 Assets 下，禁止绝对路径与路径穿越");
            if (extension != null && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("文件扩展名必须为 " + extension);
            string root = Path.GetFullPath(Application.dataPath);
            string full = Path.GetFullPath(Path.Combine(root, path.Substring(7)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("路径越界");
            for (string p = full; !string.IsNullOrEmpty(p) && p.Length >= root.Length; p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("不允许符号链接或目录联接路径");
            if (exists && !File.Exists(full) && !Directory.Exists(full)) throw new ArgumentException("路径不存在: " + path);
            if (!exists && !Directory.Exists(Path.GetDirectoryName(full))) throw new ArgumentException("父目录不存在");
            return path;
        }

        internal static T Asset<T>(JsonClass a, string key = "asset_path") where T : UnityEngine.Object
        {
            var path = AssetPath(Text(a, key));
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (!asset) throw new ArgumentException("资源不存在或类型不符: " + path);
            return asset;
        }

        internal static Type OptionalType(string name, string package)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType(name, false)).FirstOrDefault(x => x != null);
            if (type == null) throw new InvalidOperationException("缺少可选包或API: " + package + " (" + name + ")；不会自动安装");
            return type;
        }

        internal static object Ok(object data) { return Response.Success("操作完成", data); }
    }
}

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UniMcp.Executer;

namespace UniMcp
{
    /// <summary>
    /// 可配置的资源类，实现IRes接口
    /// 用于在编辑器中配置资源，支持Unity Object和URL链接
    /// </summary>
    [System.Serializable]
    public class ConfigurableResource : IRes, IMcpSubSettings
    {
        [SerializeField]
        private string _name = "";
        
        [SerializeField]
        private string _description = "";
        
        [SerializeField]
        private string _url = "";
        
        [SerializeField]
        private string _mimeType = "application/octet-stream";
        
        [SerializeField]
        private Object _unityObject;
        
        [SerializeField]
        private ResourceSourceType _sourceType = ResourceSourceType.Url;

        [SerializeField]
        private string _cachedUrl = ""; // 缓存的URL，用于UnityObject类型，避免在后台线程调用AssetDatabase

        [SerializeField]
        private string _folder = "";      // 选项列表：Assets 下的文件夹路径，如 "Assets/Textures"

        [SerializeField]
        private string _typeFilter = "";  // 选项列表：t:Texture2D / Texture2D / png,jpg / 空=全部

        [SerializeField]
        private bool _recursive = true;   // 选项列表：是否包含子文件夹

        public string Name => _name;
        public string Description => _description;
        public string MimeType => _mimeType;
        
        public string Url
        {
            get
            {
                if (_sourceType == ResourceSourceType.UnityObject && _unityObject != null)
                {
                    // 如果已有缓存的URL，直接返回（避免在后台线程调用AssetDatabase）
                    if (!string.IsNullOrEmpty(_cachedUrl))
                    {
                        return _cachedUrl;
                    }
                    
                    // 只有在主线程时才计算URL
                    if (System.Threading.Thread.CurrentThread.ManagedThreadId == 1)
                    {
                        // 将Unity Object转换为file:// URL
                        string assetPath = UnityEditor.AssetDatabase.GetAssetPath(_unityObject);
                        if (!string.IsNullOrEmpty(assetPath))
                        {
                            // 转换为绝对路径
                            string fullPath = System.IO.Path.Combine(
                                System.IO.Path.GetDirectoryName(Application.dataPath),
                                assetPath
                            );
                            _cachedUrl = $"file:///{fullPath.Replace("\\", "/")}";
                            return _cachedUrl;
                        }
                    }
                    else
                    {
                        // 后台线程：返回缓存的URL，如果缓存为空则返回空字符串
                        return _cachedUrl ?? "";
                    }
                }
                return _url;
            }
        }

        // IMcpSubSettings实现
        string IMcpSubSettings.Name => $"ConfigurableResource_{_name}";

        public Object UnityObject
        {
            get => _unityObject;
            set
            {
                _unityObject = value;
                // 当设置UnityObject时，立即计算并缓存URL（必须在主线程中调用）
                if (value != null && System.Threading.Thread.CurrentThread.ManagedThreadId == 1)
                {
                    string assetPath = UnityEditor.AssetDatabase.GetAssetPath(value);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        string fullPath = System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(Application.dataPath),
                            assetPath
                        );
                        _cachedUrl = $"file:///{fullPath.Replace("\\", "/")}";
                    }
                    else
                    {
                        _cachedUrl = "";
                    }
                }
                else if (value == null)
                {
                    _cachedUrl = "";
                }
            }
        }

        public ResourceSourceType SourceType
        {
            get => _sourceType;
            set => _sourceType = value;
        }

        /// <summary>选项列表：Assets 下的文件夹路径（允许绝对路径，自动折算为 Assets 相对路径）。</summary>
        public string Folder { get => _folder; set => _folder = value ?? ""; }

        /// <summary>选项列表：类型筛选。支持 t:Type / Type / 扩展名列表（png,jpg / *.png）/ 空=全部。</summary>
        public string TypeFilter { get => _typeFilter; set => _typeFilter = value ?? ""; }

        /// <summary>选项列表：是否包含子文件夹。</summary>
        public bool Recursive { get => _recursive; set => _recursive = value; }

        public void SetFolder(string folder) => _folder = folder ?? "";
        public void SetTypeFilter(string filter) => _typeFilter = filter ?? "";
        public void SetRecursive(bool recursive) => _recursive = recursive;

        public ConfigurableResource()
        {
        }

        public ConfigurableResource(string name, string description, string url, string mimeType = "application/octet-stream")
        {
            _name = name;
            _description = description;
            _url = url;
            _mimeType = mimeType;
            _sourceType = ResourceSourceType.Url;
        }

        /// <summary>
        /// 选项列表：按文件夹 + 类型筛选枚举出一组资源定义（只注册定义，不读取正文）。
        /// 依赖 AssetDatabase / Application.dataPath，必须在主线程调用；发现流程已保证主线程。
        /// 组内条目名以「组名_文件名」保证唯一，扩展名能推断 MIME 时优先用推断值，
        /// 否则回落到配置项的 MimeType。
        /// </summary>
        public List<ResItem> EnumerateFolderResources()
        {
            var result = new List<ResItem>();
            string folder = NormalizeFolder(_folder);
            if (string.IsNullOrEmpty(folder) || !UnityEditor.AssetDatabase.IsValidFolder(folder))
                return result;

            string findFilter;
            HashSet<string> extensions;
            ParseTypeFilter(_typeFilter, out findFilter, out extensions);

            string[] guids;
            try
            {
                guids = UnityEditor.AssetDatabase.FindAssets(findFilter, new[] { folder });
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[UniMcp] 文件夹资源筛选失败 {folder} ({_typeFilter}): {ex.Message}");
                return result;
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? "";
            var usedNames = new HashSet<string>();
            foreach (var guid in guids)
            {
                string assetPath = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) continue;
                // FindAssets 会把子文件夹本身也返回，排除。
                if (UnityEditor.AssetDatabase.IsValidFolder(assetPath)) continue;
                if (!_recursive && !IsDirectChild(folder, assetPath)) continue;

                string ext = Path.GetExtension(assetPath) ?? "";
                if (extensions != null && extensions.Count > 0
                    && !extensions.Contains(ext.TrimStart('.').ToLowerInvariant())) continue;

                string fullPath = Path.Combine(projectRoot, assetPath).Replace('\\', '/');
                string baseName = Path.GetFileNameWithoutExtension(assetPath);
                string itemName = UniqueName(usedNames, _name + "_" + baseName);
                string mime = MimeTypeForExtension(ext) ?? _mimeType;
                string desc = string.IsNullOrEmpty(_description) ? assetPath : _description + " · " + assetPath;
                result.Add(new ResItem("file://" + fullPath, itemName, desc, mime));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Url, b.Url));
            return result;
        }

        /// <summary>把用户填的路径规范成 Assets 相对路径；非法输入返回空串。</summary>
        private static string NormalizeFolder(string raw)
        {
            string s = (raw ?? "").Trim().Replace('\\', '/');
            while (s.EndsWith("/")) s = s.Substring(0, s.Length - 1);
            // 允许粘贴绝对路径 / 含 Assets 片段的路径
            int assetsIndex = s.IndexOf("/Assets/", System.StringComparison.OrdinalIgnoreCase);
            if (assetsIndex >= 0) s = s.Substring(assetsIndex + 1);
            if (!s.StartsWith("Assets", System.StringComparison.OrdinalIgnoreCase)) return "";
            return s;
        }

        /// <summary>
        /// 解析类型筛选：含 ':' 原样交给 FindAssets（t:/l:/b: 等 Unity 语法）；
        /// 纯大写开头的标识符当作 t:类型；其余按扩展名列表，FindAssets 不做类型过滤。
        /// </summary>
        private static void ParseTypeFilter(string raw, out string findFilter, out HashSet<string> extensions)
        {
            findFilter = "";
            extensions = null;
            string s = (raw ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return;

            if (s.IndexOf(':') >= 0)
            {
                findFilter = s;
                return;
            }
            if (char.IsUpper(s[0]) && s.IndexOf('.') < 0 && s.IndexOf('*') < 0
                && s.IndexOf(',') < 0 && s.IndexOf(';') < 0 && s.IndexOf(' ') < 0)
            {
                findFilter = "t:" + s;
                return;
            }

            extensions = new HashSet<string>();
            foreach (var part in s.Split(new[] { ',', ';', ' ', '|' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                string e = part.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
                if (e.Length > 0) extensions.Add(e);
            }
        }

        private static bool IsDirectChild(string folder, string assetPath)
        {
            string prefix = folder.TrimEnd('/') + "/";
            if (!assetPath.StartsWith(prefix, System.StringComparison.Ordinal)) return false;
            return assetPath.Substring(prefix.Length).IndexOf('/') < 0;
        }

        private static string UniqueName(HashSet<string> used, string name)
        {
            string candidate = name;
            int n = 2;
            while (!used.Add(candidate)) candidate = name + "_" + (n++);
            return candidate;
        }

        /// <summary>按扩展名推断 MIME；无法推断时返回 null，由调用方回落到组级 MIME。</summary>
        internal static string MimeTypeForExtension(string ext)
        {
            switch ((ext ?? "").TrimStart('.').ToLowerInvariant())
            {
                case "png": return "image/png";
                case "jpg": case "jpeg": return "image/jpeg";
                case "gif": return "image/gif";
                case "svg": return "image/svg+xml";
                case "json": return "application/json";
                case "yaml": case "yml": return "application/yaml";
                case "xml": return "text/xml";
                case "txt": case "md": case "cs": case "js": case "shader": return "text/plain";
                case "html": case "htm": return "text/html";
                case "css": return "text/css";
                case "csv": return "text/csv";
                case "pdf": return "application/pdf";
                default: return null;
            }
        }

        public void SetName(string name) => _name = name;
        public void SetDescription(string description) => _description = description;
        public void SetUrl(string url) => _url = url;
        public void SetMimeType(string mimeType) => _mimeType = mimeType;

        /// <summary>
        /// 确保URL已计算（用于UnityObject类型，必须在主线程中调用）
        /// </summary>
        public void EnsureUrlCalculated()
        {
            if (_sourceType == ResourceSourceType.UnityObject && _unityObject != null && string.IsNullOrEmpty(_cachedUrl))
            {
                // 检查是否在主线程
                if (System.Threading.Thread.CurrentThread.ManagedThreadId == 1)
                {
                    string assetPath = UnityEditor.AssetDatabase.GetAssetPath(_unityObject);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        string fullPath = System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(Application.dataPath),
                            assetPath
                        );
                        _cachedUrl = $"file:///{fullPath.Replace("\\", "/")}";
                    }
                }
            }
        }
    }

    /// <summary>
    /// 资源来源类型
    /// </summary>
    public enum ResourceSourceType
    {
        Url,         // URL链接
        UnityObject, // Unity对象
        FolderList   // 选项列表：指定文件夹 + 类型筛选，展开成一组资源
    }
}

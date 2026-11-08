using System;
using System.Collections.Generic;
using System.Linq;
using UniMcp.Utils;

namespace UniMcp
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class CapabilityTagsAttribute : Attribute
    {
        private readonly string[] tags;
        public string[] Tags => (string[])tags.Clone();

        public CapabilityTagsAttribute(params string[] tags)
        {
            this.tags = tags == null ? new string[0] : (string[])tags.Clone();
        }

        /// <summary>
        /// 所有能力（工具 / 提示词 / 资源）共用的 tag 规范化入口：slug 化、去空、
        /// 去保留字 all、去重、按序号排序。传入顺序不限，返回独立副本。
        /// </summary>
        internal static string[] Normalize(params IEnumerable<string>[] sources)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            if (sources == null) return new string[0];
            foreach (var source in sources)
            {
                if (source == null) continue;
                foreach (var raw in source)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var slug = Slug(raw);
                    if (slug != "all") result.Add(slug);
                }
            }
            return result.ToArray();
        }

        internal static string Slug(string value)
        {
            var slug = System.Text.RegularExpressions.Regex.Replace(
                (value ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return string.IsNullOrEmpty(slug) ? "uncategorized" : slug;
        }
    }

    /// <summary>
    /// 用于标记工具方法类的名称和分组的特性
    /// 优先级高于通过类名自动转换的snake_case命名
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class ToolNameAttribute : Attribute
    {
        /// <summary>
        /// 工具方法的名称
        /// </summary>
        public string ToolName { get; }

        /// <summary>
        /// 工具方法的分组名称（根据当前语言返回）
        /// </summary>
        public string GroupName 
        { 
            get 
            {
                // 如果中文组名为空，直接返回英文组名
                if (string.IsNullOrWhiteSpace(_groupNameChinese))
                    return _groupNameEnglish;
                
                // 否则使用 L.T 进行多语言切换
                return L.T(_groupNameEnglish, _groupNameChinese);
            }
        }

        public string GroupNameEnglish => _groupNameEnglish;

        private readonly string _groupNameEnglish;
        private readonly string _groupNameChinese;

        /// <summary>
        /// 初始化 ToolNameAttribute
        /// </summary>
        /// <param name="toolName">工具方法的名称，通常使用snake_case格式</param>
        /// <param name="groupNameEnglish">工具方法的英文分组名称，如"Scene"、"Assets"等，为空则使用默认分组</param>
        /// <param name="groupNameChinese">工具方法的中文分组名称，如"场景层级"、"资源资产"等，为空则忽略多语言切换</param>
        public ToolNameAttribute(string toolName, string groupNameEnglish = null, string groupNameChinese = null)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                throw new ArgumentException("Tool name cannot be null or empty", nameof(toolName));

            ToolName = toolName;
            _groupNameEnglish = string.IsNullOrWhiteSpace(groupNameEnglish)
                ? "Uncategorized"
                : groupNameEnglish;
            _groupNameChinese = string.IsNullOrWhiteSpace(groupNameEnglish) && string.IsNullOrWhiteSpace(groupNameChinese)
                ? "未分组" : groupNameChinese;
        }
    }
}

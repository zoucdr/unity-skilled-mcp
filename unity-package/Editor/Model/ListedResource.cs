namespace UniMcp
{
    /// <summary>
    /// IResList 展开后的单项资源：把组级 MimeType 补齐成条目级，
    /// 并保留来源类名与分组名，供设置面板按组展示、按项开关。
    /// 只在发现流程内构造，对外仍以 IRes 暴露。
    /// </summary>
    internal sealed class ListedResource : IRes
    {
        public string Url { get; }
        public string Name { get; }
        public string Description { get; }
        public string MimeType { get; }

        /// <summary>提供本组资源的 IResList 实现类名（面板归组、"?"定位脚本用）。</summary>
        public string SourceTypeName { get; }

        /// <summary>IResList.GroupName；实现方留空时回落到类名。</summary>
        public string GroupName { get; }

        public ListedResource(ResItem item, string mimeType, string sourceTypeName, string groupName)
        {
            Url = item?.Url;
            Name = item?.Name;
            Description = item?.Description;
            MimeType = mimeType;
            SourceTypeName = sourceTypeName;
            GroupName = groupName;
        }
    }
}

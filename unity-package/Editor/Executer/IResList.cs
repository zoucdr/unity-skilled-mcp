using System.Collections.Generic;

namespace UniMcp
{
    /// <summary>
    /// 一组同类资源的提供者：实现本接口即可把一个批次资源一次性发布给 MCP。
    /// 与 IRes 的差别在粒度——IRes 一个类只发布一个资源（类名即资源身份），
    /// IResList 一个类发布整组（组名 + 逐项条目），适合"某目录下每个文件一个资源"
    /// 这类数量随工程变化的场景，无需为每一项写一个类。
    /// 条目只描述注册定义；正文仍由 Url 决定，不在此处提供。
    /// </summary>
    public interface IResList
    {
        /// <summary>分组名：设置面板与日志按此归拢本组资源；为空时回落到实现类名。</summary>
        string GroupName { get; }

        /// <summary>组内资源的公共 MIME 类型；条目自身未给出时继承此值。</summary>
        string MimeType { get; }

        /// <summary>
        /// 逐项列出本组资源。每次发现都会重新调用，应反映当下工程状态；
        /// 允许返回空集合（本组本次无资源），不可返回 null。
        /// </summary>
        IEnumerable<ResItem> GetResources();
    }

    /// <summary>
    /// 资源条目：组内单项的轻量定义。Url 与 Name 必填，Description / MimeType 可留空
    /// （MimeType 留空时继承 IResList.MimeType）。
    /// </summary>
    public class ResItem
    {
        public string Url { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string MimeType { get; set; }

        public ResItem()
        {
        }

        public ResItem(string url, string name, string description = null, string mimeType = null)
        {
            Url = url;
            Name = name;
            Description = description;
            MimeType = mimeType;
        }
    }
}

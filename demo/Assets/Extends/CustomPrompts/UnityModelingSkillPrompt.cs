using System.IO;
using UnityEngine;
using UniMcp;

namespace UniMcp.Prompts
{
    /// <summary>
    /// 提示词：告知 AI unity-modeling 技能的绝对路径，按需直接读取，无需安装。
    /// 路径在运行时由当前 Unity 工程的父目录拼接 sub_skills/unity-modeling 动态得到。
    /// </summary>
    public class UnityModelingSkillPrompt : IPrompts
    {
        private const string SkillName = "unity-modeling";

        public string Name => "unity_modeling_skill";

        public string Description => L.T(
            "Where the unity-modeling skill lives and how to read it",
            "unity-modeling 技能所在位置及读取方式");

        public string PromptText
        {
            get
            {
                var path = ResolveSkillPath().Replace('\\', '/');
                return L.T(
                    "You are connected to a Unity Editor project that provides the unity-modeling skill. " +
                    "Its absolute path is: " + path + "\n\n" +
                    "No installation needed. When you need this skill, read SKILL.md at that path directly. " +
                    "Read references/semantics.md before starting: it records measured pitfalls that contradict source comments. " +
                    "Run examples/wooden_crate.js as the reference pipeline, and use scripts/model.js, scripts/tex.js, scripts/ui.js and scripts/verify.js for modeling, texturing, UI-as-texture and delivery checks. " +
                    "unity-modeling builds parts with ProBuilder, merges them into a single mesh, rebuilds a non-overlapping UV atlas, textures by atlas region or derives a full PBR set with TexGen, then verifies before delivery.",
                    "你已通过本 MCP 连接到一个提供 unity-modeling 技能的 Unity 项目。" +
                    "其绝对路径为：" + path + "\n\n" +
                    "无需安装。需要使用时直接读取该路径下的 SKILL.md 即可。" +
                    "开工前先读 references/semantics.md：其中记录的实测结论与源码注释相矛盾，不看会重复踩坑。" +
                    "样例流程参考 examples/wooden_crate.js；建模、贴图、UI 当贴图与交付校验分别使用 scripts/model.js、scripts/tex.js、scripts/ui.js、scripts/verify.js。" +
                    "unity-modeling 以 ProBuilder 建分件、合并为单 mesh、重建非重叠 UV 图集，再用 TexGen 按分区贴图或派生整套 PBR，交付前跑校验。");
            }
        }

        public MethodKey[] Keys => null;

        // 从当前 Unity 工程根解析技能包绝对路径：<工程根父目录>/sub_skills/unity-modeling。
        // 不依赖进程工作目录；目录尚未就绪时仍返回预期路径，便于 AI 知晓目标位置。
        private static string ResolveSkillPath()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var parent = Path.GetDirectoryName(projectRoot);
            return Path.GetFullPath(Path.Combine(parent ?? projectRoot, "sub_skills", SkillName));
        }
    }
}

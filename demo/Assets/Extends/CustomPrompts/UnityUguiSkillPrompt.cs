using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UniMcp;

namespace UniMcp.Prompts
{
    /// <summary>
    /// 提示词：告知 AI unity-ugui 技能的绝对路径与使用方法，按需直接读取，无需安装。
    /// 与 UnityModelingSkillPrompt 并列；由 McpService 反射扫描 IPrompts 自动注册，
    /// 无需在 ProjectSettings/McpSettings.asset 的 configurablePrompts 中另行登记。
    /// 路径在运行时由当前 Unity 工程解析；换工程/技能目录未就位时会给出明确提示。
    /// </summary>
    public class UnityUguiSkillPrompt : IPrompts
    {
        private const string SkillName = "unity-ugui";

        public string Name => "unity_ugui_skill";

        public string Description => L.T(
            "Where the unity-ugui skill lives and how to turn a UI mockup into a built UGUI",
            "unity-ugui 技能所在位置，以及从 UI 效果图搭建 UGUI 的读取与执行方式");

        public string PromptText
        {
            get
            {
                var path = ResolveSkillPath().Replace('\\', '/');
                // 当前打开的不是技能所在仓库时，路径会落到别处；明确告知 AI，避免拿着假路径读文件。
                var missing = !File.Exists(Path.Combine(path, "SKILL.md"));
                var notice = missing
                    ? L.T(
                        "\n\nNOTE: No SKILL.md was found at the path above (the Editor is probably not open on the repository that owns sub_skills). " +
                        "Ask for the real skill location, or set the UNITY_UGUI_SKILL_DIR environment variable, before reading any file. " +
                        "Do not assume the demo-only MCP extensions (uisourcegen.*, ugui.generate, ui_rule.*, figma.*) exist in the current project.",
                        "\n\n注意：上述路径下未找到 SKILL.md（当前 Unity 打开的可能不是包含 sub_skills 的那个工程）。" +
                        "读文件前请先确认技能实际位置，或设置环境变量 UNITY_UGUI_SKILL_DIR 显式指定。" +
                        "也不要假定 demo 私有扩展 uisourcegen.* / ugui.generate / ui_rule.* / figma.* 在当前工程可用。")
                    : "";

                return L.T(
                    "You are connected to a Unity Editor project that provides the unity-ugui skill. " +
                    "Its absolute path is: " + path + "\n\n" +
                    "No installation needed. When you need this skill, read SKILL.md at that path directly.\n" +
                    "What it does: turn one UI mockup image into a built UGUI in Unity - sliced sprites with 9-slice borders, " +
                    "a correct hierarchy, and real interactive controls. A single UI Spec (templates/ui_spec.schema.json, " +
                    "rect=[left,top,w,h], top-left origin) is the only source of truth; the skill dispatches it to the " +
                    "UISourceGen art module, the demo UGUI creation module, the unity-package layout module and the ui_rule module, " +
                    "then verifies numerically before delivery.\n\n" +
                    "Read before starting: references/contract.md (Spec contract), references/tools.md (AUTHORITATIVE tool names and parameters - " +
                    "names in source comments and the official settings prompt such as figma_manage / ugui_layout / ui_rule_manage are stale aliases), " +
                    "references/semantics.md (34 measured pitfalls), prompts/vision_to_spec.md (vision-to-Spec prompt), " +
                    "references/pipeline.md (stages, degradation, recovery).\n\n" +
                    "Run: node scripts/check.js -> node scripts/pipeline.js --spec <ui_spec.json> --dry-run -> " +
                    "node scripts/pipeline.js --spec <ui_spec.json> --project <project root> --instance <instance_id> --image <mockup.png>. " +
                    "See examples/login.spec.json and examples/run_login.js for an end-to-end case. " +
                    "Outputs go to Assets/AI-Generate/UGUI/<screen name>/. " +
                    "Stage-level reruns use --stage assets|build|layout|verify.\n\n" +
                    "Three pitfalls that break the chain: (1) capabilities change with the project - run scripts/capabilities.js first, " +
                    "and state any degradation explicitly in the report; (2) ugui.do_layout vector parameters require THREE numbers " +
                    "[x,y,0] per its vector3 schema, and path is required; (3) 'correct controls' means serialized references are non-null " +
                    "(Button.targetGraphic, Toggle.graphic, Slider.fillRect/handleRect, InputField.textComponent/placeholder, " +
                    "ScrollRect.content/viewport) - ugui.create_component only draws fake Sliders, so the main path is scripts/build.js via code.execute. " +
                    "9-slice borders must be written to TextureImporter.spriteBorder, not left as layout.json metadata.\n\n" +
                    "Do NOT reuse the coordinate semantics of the sister skill unity-modeling: this skill is UGUI semantics " +
                    "(mockup top-left origin y-down, RectTransform bottom-left origin y-up).",
                    "你已通过本 MCP 连接到一个提供 unity-ugui 技能的 Unity 项目。" +
                    "其绝对路径为：" + path + "\n\n" +
                    "无需安装。需要使用时直接读取该路径下的 SKILL.md。\n" +
                    "它解决的问题：给一张 UI 效果图，在 Unity 里建好「图切好、UGUI 搭好、控件正确」的可交互界面 —— " +
                    "含带九宫格的切图 Sprite、正确层级、真实可交互控件。" +
                    "以 UI Spec（templates/ui_spec.schema.json，rect=[left,top,w,h]，左上原点）为唯一真值源，" +
                    "分派给 UISourceGen 图资源生产、demo UGUI 创建模块、unity-package 布局模块与 ui_rule 规则模块，交付前做数值验收。\n\n" +
                    "开工必读：references/contract.md（Spec 契约）、references/tools.md（**权威工具名与参数**，" +
                    "源码注释及官方设置面板提示词里的 figma_manage / ugui_layout / ui_rule_manage 全是过时别名）、" +
                    "references/semantics.md（34 条实测铁律）、prompts/vision_to_spec.md（看图出 Spec 的提示词）、" +
                    "references/pipeline.md（阶段、降级、故障恢复）。\n\n" +
                    "执行顺序：node scripts/check.js → node scripts/pipeline.js --spec <ui_spec.json> --dry-run → " +
                    "node scripts/pipeline.js --spec <ui_spec.json> --project <工程根> --instance <instance_id> --image <效果图.png>。" +
                    "端到端样例见 examples/login.spec.json 与 examples/run_login.js。" +
                    "产物落在 Assets/AI-Generate/UGUI/<界面名>/；断点续跑用 --stage assets|build|layout|verify。\n\n" +
                    "三个最容易踩的坑：(1) 能力随工程变 —— 先跑 scripts/capabilities.js 再选路径，降级必须在报告里明写；" +
                    "(2) ugui.do_layout 的向量参数按 vector3 schema 必须给 3 个数 [x,y,0]，且 path 必填；" +
                    "(3)「控件正确」= 序列化引用非 null（Button.targetGraphic、Toggle.graphic、Slider.fillRect/handleRect、" +
                    "InputField.textComponent/placeholder、ScrollRect.content/viewport）—— " +
                    "ugui.create_component 的 Slider 只是两张 Image，主路径请用 scripts/build.js 走 code.execute 建真组件。" +
                    "九宫格必须写进 TextureImporter.spriteBorder，不能只留在 layout.json 的 slice 元数据里。\n\n" +
                    "不要套用姊妹技能 unity-modeling 的坐标语义：本技能是 UGUI 语义" +
                    "（效果图左上原点 y 向下、RectTransform 左下原点 y 向上）。") + notice;
            }
        }

        public MethodKey[] Keys => null;

        /// <summary>
        /// 解析技能包绝对路径。优先环境变量显式覆盖，其次按工程相对位置由近及远上溯，
        /// 命中条件是目录内存在 SKILL.md。与 UnityModelingSkillPrompt 的
        /// &lt;工程根父目录&gt;/sub_skills/&lt;技能名&gt; 布局保持兼容。
        /// 目录未就位时仍返回预期路径，便于 AI 知晓目标位置（配合 PromptText 的缺失提示）。
        /// </summary>
        private static string ResolveSkillPath()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var candidates = new List<string>();

            var envDir = System.Environment.GetEnvironmentVariable("UNITY_UGUI_SKILL_DIR");
            if (!string.IsNullOrEmpty(envDir)) candidates.Add(envDir);

            candidates.Add(Path.Combine(projectRoot, "sub_skills", SkillName));

            var parent = Path.GetDirectoryName(projectRoot);
            if (!string.IsNullOrEmpty(parent)) candidates.Add(Path.Combine(parent, "sub_skills", SkillName));

            var dir = new DirectoryInfo(projectRoot);
            while (dir != null)
            {
                var c = Path.Combine(dir.FullName, "sub_skills", SkillName);
                if (!candidates.Contains(c)) candidates.Add(c);
                dir = dir.Parent;
            }

            foreach (var c in candidates)
            {
                string full;
                try { full = Path.GetFullPath(c); }
                catch { continue; }
                if (File.Exists(Path.Combine(full, "SKILL.md"))) return full;
            }

            return Path.GetFullPath(Path.Combine(parent ?? projectRoot, "sub_skills", SkillName));
        }
    }
}

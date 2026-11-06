using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using UniMcp.Executer;
using UniMcp.Tools;
using UniMcp;
using Object = UnityEngine.Object;

namespace UniMcp.Gui
{
    /// <summary>
    /// MCP连接管理GUI类，提供所有绘制功能的静态方法
    /// 用于在ProjectSettings中显示MCP设置
    /// </summary>
    public static class McpServiceGUI
    {
        // 工具方法列表相关变量
        private static Dictionary<string, bool> methodFoldouts = new Dictionary<string, bool>();
        private static Dictionary<string, bool> groupFoldouts = new Dictionary<string, bool>(); // 分组折叠状态
        private static Vector2 methodsScrollPosition;
        private static int groupIndex = 0; // 分组序号

        // 标签页相关变量
        private static int selectedTab = 0; // 0=工具, 1=资源, 2=提示词
        private static Vector2 resourcesScrollPosition;
        private static Vector2 promptsScrollPosition;
        private static Dictionary<int, bool> resourceFoldouts = new Dictionary<int, bool>();
        private static Dictionary<int, bool> promptFoldouts = new Dictionary<int, bool>();
        // 代码实现的 IPrompts / IRes 折叠状态（与可配置项区分，避免键冲突）
        private static Dictionary<string, bool> codePromptFoldouts = new Dictionary<string, bool>();
        private static Dictionary<string, bool> codeResourceFoldouts = new Dictionary<string, bool>();
        // 服务未启动时 availablePrompts/availableResources 可能为空，这里保证至少自动发现一次
        private static bool codeDiscoveryTried = false;

        // MIME类型下拉框使用的常用资源格式
        private static readonly string[] commonMimeTypes = new string[]
        {
            "application/octet-stream", // 默认值
            "text/plain",
            "text/html",
            "text/css",
            "text/csv",
            "text/markdown",
            "application/json",
            "application/xml",
            "application/yaml",
            "application/javascript",
            "application/typescript",
            "application/pdf",
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/bmp",
            "image/webp",
            "image/svg+xml",
            "image/tiff",
            "audio/mpeg",
            "audio/wav",
            "audio/ogg",
            "audio/flac",
            "video/mp4",
            "video/webm",
            "video/quicktime",
            "application/zip",
            "application/gzip",
            "application/x-tar"
        };


        /// <summary>
        /// 绘制完整的MCP设置GUI
        /// </summary>
        public static void DrawGUI()
        {
            // 使用垂直布局管理整个窗口，确保充分利用空间
            EditorGUILayout.BeginVertical(GUILayout.ExpandHeight(true));

            // 美化标题区域 - 添加背景框和渐变效果
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            // 标题行 - 使用更大的字体和更好的样式
            EditorGUILayout.BeginHorizontal();
            
            // 标题样式
            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.2f, 0.4f, 0.8f) }
            };
            EditorGUILayout.LabelField("⚡ Unity Skilled MCP Service", titleStyle, GUILayout.ExpandWidth(true));

            // 日志级别下拉菜单 - 美化样式
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(L.T("Log Level", "日志级别"), EditorStyles.miniLabel, GUILayout.Width(60));
            var currentLogLevel = McpLogger.GetLogLevel();
            var newLogLevel = (McpLogger.LogLevel)EditorGUILayout.EnumPopup(currentLogLevel, GUILayout.Width(100));
            if (newLogLevel != currentLogLevel)
            {
                McpLogger.SetLogLevel(newLogLevel);
            }
            EditorGUILayout.EndVertical();

            // 语言切换下拉菜单 - 美化样式
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(L.T("Language", "语言"), EditorStyles.miniLabel, GUILayout.Width(60));
            var currentLanguage = McpService.GetLocalSettings().CurrentLanguage;
            if (string.IsNullOrEmpty(currentLanguage))
            {
                currentLanguage = "中文"; // 默认语言
            }
            string[] languages = new string[] { "中文", "English" };
            int currentIndex = currentLanguage == "English" ? 1 : 0;
            int newIndex = EditorGUILayout.Popup(currentIndex, languages, GUILayout.Width(80));
            if (newIndex != currentIndex)
            {
                string newLanguage = languages[newIndex];
                McpService.GetLocalSettings().CurrentLanguage = newLanguage;
                Debug.Log($"[McpServiceGUI] {L.T("Language switched to", "语言已切换为")}: {newLanguage}");
                
                // 刷新列表：重新发现工具、资源和提示词
                RefreshAllLists();
                
                // 强制刷新 ProjectSettings 窗口
                EditorApplication.delayCall += () =>
                {
                    // 获取当前活动的 EditorWindow
                    var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                    foreach (var window in windows)
                    {
                        // 刷新 ProjectSettings 窗口和其他可能的窗口
                        window.Repaint();
                    }
                    
                    // 重新发现工具以更新描述文本
                    McpService.RefreshDiscovery();
                };
            }
            EditorGUILayout.EndVertical();

            // 描述开关 - 美化样式
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(L.T("Descriptions", "描述信息"), EditorStyles.miniLabel, GUILayout.Width(70));
            var localSettings = McpService.GetLocalSettings();
            bool enableDescriptions = localSettings.EnableDescriptions;
            
            // 根据状态设置不同颜色
            Color descToggleOriginalBg = GUI.backgroundColor;
            if (enableDescriptions)
            {
                GUI.backgroundColor = new Color(0.4f, 0.9f, 0.5f, 0.8f); // 绿色 - 启用
            }
            else
            {
                GUI.backgroundColor = new Color(0.9f, 0.6f, 0.3f, 0.8f); // 橙色 - 禁用
            }
            
            bool newEnableDescriptions = EditorGUILayout.Toggle(enableDescriptions, GUILayout.Width(20));
            GUI.backgroundColor = descToggleOriginalBg;
            
            if (newEnableDescriptions != enableDescriptions)
            {
                localSettings.EnableDescriptions = newEnableDescriptions;
                
                // 清除相关缓存，让下次请求返回新格式
                McpService.ClearToolsListCache();
                
                string statusText = newEnableDescriptions ? 
                    L.T("Descriptions enabled. Tools/Resources/Prompts will include full descriptions and parameter details.", 
                        "描述已启用。工具/资源/提示词将包含完整描述和参数说明。") :
                    L.T("Descriptions disabled. Only essential info will be sent. Use Skills/Rules for guidance.", 
                        "描述已禁用。仅发送必要信息。请通过Skill/规则文件获取使用指导。");
                
                Debug.Log($"[McpServiceGUI] {statusText}");
                
                // 显示提示对话框
                EditorUtility.DisplayDialog(
                    L.T("Description Toggle", "描述开关"), 
                    statusText, 
                    L.T("OK", "确定"));
            }
            EditorGUILayout.EndVertical();

            // 状态窗口按钮 - 美化按钮样式
            GUIStyle statusButtonStyle = new GUIStyle(EditorStyles.miniButton)
            {
                padding = new RectOffset(8, 8, 4, 4),
                fontSize = 11
            };
            Color originalBgColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 0.8f); // 淡蓝色
            
            if (GUILayout.Button(L.T("📊 Status Window", "📊 状态窗口"), statusButtonStyle, GUILayout.Width(110), GUILayout.Height(22)))
            {
                McpServiceStatusWindow.ShowWindow();
            }
            
            GUI.backgroundColor = originalBgColor;
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8);

            // 绘制标签页
            DrawTabs();

            EditorGUILayout.Space(4);

            // 根据选中的标签页绘制相应内容
            switch (selectedTab)
            {
                case 0:
                    DrawMethodsList();
                    break;
                case 1:
                    DrawResourcesList();
                    break;
                case 2:
                    DrawPromptsList();
                    break;
            }

            // 结束主垂直布局
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 绘制标签页
        /// </summary>
        private static void DrawTabs()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUIStyle tabStyle = new GUIStyle(EditorStyles.toolbarButton)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(12, 12, 4, 4)
            };

            Color originalColor = GUI.backgroundColor;

            // 工具标签
            if (selectedTab == 0)
                GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 0.8f);
            if (GUILayout.Button(L.T("🔧 Tools", "🔧 工具"), tabStyle))
                selectedTab = 0;
            GUI.backgroundColor = originalColor;

            // 资源标签
            if (selectedTab == 1)
                GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 0.8f);
            if (GUILayout.Button(L.T("📦 Resources", "📦 资源"), tabStyle))
                selectedTab = 1;
            GUI.backgroundColor = originalColor;

            // 提示词标签
            if (selectedTab == 2)
                GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 0.8f);
            if (GUILayout.Button(L.T("💬 Prompts", "💬 提示词"), tabStyle))
                selectedTab = 2;
            GUI.backgroundColor = originalColor;

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 动态计算工具方法列表的可用高度
        /// </summary>
        private static float CalculateAvailableMethodsHeight()
        {
            // 使用固定高度，因为在ProjectSettings中不需要动态计算
            float windowHeight = 800f;

            // 估算已占用的空间
            float usedHeight = 0f;

            // 标题和按钮区域高度 (约 50px)
            usedHeight += 50f;

            // 工具方法列表标题和间距 (约 50px)
            usedHeight += 50f;

            // 窗口边距和滚动条等 (约 30px)
            usedHeight += 30f;

            // 计算剩余可用高度，至少保留 150px
            float availableHeight = Mathf.Max(windowHeight - usedHeight, 500f);

            return availableHeight;
        }

        /// <summary>
        /// 绘制工具树：分组 → 工具模块（前缀）→ 内含子函数 → 各自参数。
        /// 一步到位展示“一个模块内含哪些函数、每个函数有哪些参数”，
        /// 数据源为声明式 ToolCatalog（一个动作即一个工具），另附内置门面工具。
        /// </summary>
        private static void DrawMethodsList()
        {
            // 使用更美观的背景框
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));

            var specs = McpService.GetAllToolSpecs();
            var builtins = McpService.GetBuiltinToolInfos();

            // 汇总所有可切换的工具名（声明式目录 + 内置门面）
            var allToolNames = new List<string>();
            foreach (var spec in specs) allToolNames.Add(spec.Name);
            foreach (var info in builtins) allToolNames.Add(info.name);

            // 美化标题栏：添加背景色和更好的布局
            Rect headerRect = EditorGUILayout.BeginHorizontal(EditorStyles.toolbar, GUILayout.Height(28));

            Color headerBgColor = new Color(0.25f, 0.25f, 0.3f, 0.3f);
            EditorGUI.DrawRect(headerRect, headerBgColor);

            GUIStyle headerTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.95f) },
                padding = new RectOffset(8, 0, 4, 0)
            };

            // 工具暴露模式下拉框：全部开启(纯MCP) / 全部关闭(技能方式) / 选择性开启(混合)
            string[] toolModeOptions = new string[]
            {
                L.T("All On (Pure MCP)", "全部开启"),
                L.T("All Off (Skill Mode)", "全部关闭"),
                L.T("Selective (Hybrid)", "选择性开启")
            };
            var localSettings = McpService.GetLocalSettings();
            bool allEnabled = allToolNames.Count > 0 && allToolNames.All(n => localSettings.IsToolEnabled(n));
            bool allDisabled = allToolNames.Count > 0 && allToolNames.All(n => !localSettings.IsToolEnabled(n));
            int currentMode = allEnabled ? 0 : (allDisabled ? 1 : 2);
            int newMode = EditorGUILayout.Popup(currentMode, toolModeOptions, headerTitleStyle, GUILayout.Width(220));
            if (newMode != currentMode)
            {
                if (newMode == 0)
                {
                    localSettings.SetToolsEnabled(allToolNames, true);
                    Debug.Log($"[McpServiceGUI] {L.T("Tools mode", "工具模式")}: {L.T("All On (Pure MCP)", "全部开启 (纯MCP)")}");
                }
                else if (newMode == 1)
                {
                    localSettings.SetToolsEnabled(allToolNames, false);
                    Debug.Log($"[McpServiceGUI] {L.T("Tools mode", "工具模式")}: {L.T("All Off (Skill Mode)", "全部关闭 (技能方式)")}");
                }
                // newMode == 2: Hybrid, no bulk change
            }

            EditorGUILayout.Space(8);

            // 工具信息按钮 - 美化样式
            GUIStyle toolInfoButtonStyle = new GUIStyle(EditorStyles.miniButton)
            {
                padding = new RectOffset(10, 10, 4, 4),
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };
            Color toolInfoOriginalColor = GUI.backgroundColor;

            int totalToolCount = allToolNames.Count;
            int enabledToolCount = allToolNames.Count(n => localSettings.IsToolEnabled(n));
            string toolButtonText = enabledToolCount == totalToolCount ?
                $"✅ {L.T("Tools", "工具")}({enabledToolCount})" :
                $"⚠️ {L.T("Tools", "工具")}({enabledToolCount}/{totalToolCount})";

            if (enabledToolCount == totalToolCount)
            {
                GUI.backgroundColor = new Color(0.4f, 0.9f, 0.5f, 0.8f);
            }
            else if (enabledToolCount > 0)
            {
                GUI.backgroundColor = new Color(1f, 0.85f, 0.4f, 0.8f);
            }
            else
            {
                GUI.backgroundColor = new Color(1f, 0.5f, 0.5f, 0.8f);
            }

            if (GUILayout.Button(toolButtonText, toolInfoButtonStyle, GUILayout.Width(100), GUILayout.Height(22)))
            {
                ShowToolDebugInfo();
            }

            GUI.backgroundColor = toolInfoOriginalColor;

            // 调试窗口按钮 - 美化样式
            GUIStyle titleDebugButtonStyle = new GUIStyle(EditorStyles.miniButton)
            {
                padding = new RectOffset(10, 10, 4, 4),
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };
            Color titleOriginalColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.5f, 0.7f, 1f, 0.8f);

            if (GUILayout.Button(L.T("🐛 Debug Window", "🐛 调试窗口"), titleDebugButtonStyle, GUILayout.Width(110), GUILayout.Height(22)))
            {
                McpDebugWindow.ShowWindow();
            }

            GUI.backgroundColor = titleOriginalColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // 滚动视图填充剩余空间
            methodsScrollPosition = EditorGUILayout.BeginScrollView(methodsScrollPosition,
                GUILayout.ExpandHeight(true));

            groupIndex = 0;

            // 一级：按分组（GroupName）归类；二级：模块（前缀）；三级：子函数
            var groups = specs.GroupBy(s => s.GroupName)
                              .OrderBy(g => g.Key, StringComparer.Ordinal)
                              .ToList();

            foreach (var group in groups)
            {
                string groupName = group.Key;
                var groupSpecs = group.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

                if (!groupFoldouts.ContainsKey(groupName)) groupFoldouts[groupName] = false;

                bool groupAllEnabled = groupSpecs.All(s => localSettings.IsToolEnabled(s.Name));
                bool groupAnyEnabled = groupSpecs.Any(s => localSettings.IsToolEnabled(s.Name));
                int groupEnabledCount = groupSpecs.Count(s => localSettings.IsToolEnabled(s.Name));
                int groupTotalCount = groupSpecs.Count;

                groupIndex++;

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();

                // 分组开关：切换该分组下所有工具
                Color originalBackgroundColor = GUI.backgroundColor;
                GUI.backgroundColor = groupAllEnabled ? new Color(0.4f, 0.9f, 0.5f, 0.6f)
                    : (groupAnyEnabled ? new Color(1f, 0.9f, 0.4f, 0.6f) : new Color(0.9f, 0.4f, 0.4f, 0.6f));
                bool newGroupToggleState = EditorGUILayout.Toggle(groupAllEnabled, GUILayout.Width(18));
                GUI.backgroundColor = originalBackgroundColor;

                if (newGroupToggleState != groupAllEnabled)
                {
                    localSettings.SetToolsEnabled(groupSpecs.Select(s => s.Name), newGroupToggleState);
                    string statusText = newGroupToggleState ? L.T("enabled", "启用") : L.T("disabled", "禁用");
                    Debug.Log($"[McpServiceGUI] {L.T("Tool group", "工具组")} '{groupName}' {L.T("all tools", "所有工具")}{statusText}");
                }

                GUIStyle groupFoldoutStyle = new GUIStyle(EditorStyles.foldout)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 13
                };
                Color groupColor = groupAllEnabled ? new Color(0.2f, 0.7f, 0.3f)
                    : (groupAnyEnabled ? new Color(0.8f, 0.7f, 0.2f) : new Color(0.9f, 0.3f, 0.3f));
                groupFoldoutStyle.normal.textColor = groupColor;
                groupFoldoutStyle.onNormal.textColor = groupColor;
                groupFoldoutStyle.focused.textColor = groupColor;
                groupFoldoutStyle.onFocused.textColor = groupColor;

                string groupStatusIcon = groupAllEnabled ? "✅" : (groupAnyEnabled ? "⚠️" : "❌");
                string groupCountText = groupAllEnabled ? $"{groupTotalCount}" : $"{groupEnabledCount}/{groupTotalCount}";

                groupFoldouts[groupName] = EditorGUILayout.Foldout(
                    groupFoldouts[groupName],
                    $"{groupStatusIcon} {groupIndex}. {groupName} ({groupCountText})",
                    true, groupFoldoutStyle);

                EditorGUILayout.EndHorizontal();

                if (groupFoldouts[groupName])
                {
                    EditorGUI.indentLevel++;

                    // 二级：按模块前缀分组
                    var modules = groupSpecs.GroupBy(s => s.Prefix)
                                            .OrderBy(m => m.Key, StringComparer.Ordinal)
                                            .ToList();
                    foreach (var module in modules)
                    {
                        DrawModuleNode(module.Key, module.ToList(), localSettings);
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(8);
            }

            // 内置门面工具（async_call / get_prompt / get_resource / search_tool）单独一组
            if (builtins.Count > 0)
            {
                DrawBuiltinGroup(builtins, localSettings);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 绘制一个工具模块（前缀）节点：模块 → 内含子函数 → 各自参数。
        /// </summary>
        private static void DrawModuleNode(string prefix, List<ToolSpec> specs, McpLocalSettings localSettings)
        {
            string moduleKey = "module::" + prefix;
            if (!groupFoldouts.ContainsKey(moduleKey)) groupFoldouts[moduleKey] = false;

            bool allEnabled = specs.All(s => localSettings.IsToolEnabled(s.Name));
            bool anyEnabled = specs.Any(s => localSettings.IsToolEnabled(s.Name));
            string sourceClass = specs.Count > 0 ? specs[0].SourceClass : prefix;

            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.BeginHorizontal();

            Color originalBackgroundColor = GUI.backgroundColor;
            GUI.backgroundColor = allEnabled ? new Color(0.4f, 0.9f, 0.5f, 0.6f)
                : (anyEnabled ? new Color(1f, 0.9f, 0.4f, 0.6f) : new Color(0.9f, 0.4f, 0.4f, 0.6f));
            bool newToggle = EditorGUILayout.Toggle(allEnabled, GUILayout.Width(18));
            GUI.backgroundColor = originalBackgroundColor;

            if (newToggle != allEnabled)
            {
                localSettings.SetToolsEnabled(specs.Select(s => s.Name), newToggle);
            }

            GUIStyle moduleStyle = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12
            };
            Color moduleColor = allEnabled ? new Color(0.6f, 0.85f, 0.65f)
                : (anyEnabled ? new Color(0.9f, 0.8f, 0.45f) : new Color(0.9f, 0.55f, 0.55f));
            moduleStyle.normal.textColor = moduleColor;
            moduleStyle.onNormal.textColor = moduleColor;
            moduleStyle.focused.textColor = moduleColor;
            moduleStyle.onFocused.textColor = moduleColor;

            groupFoldouts[moduleKey] = EditorGUILayout.Foldout(
                groupFoldouts[moduleKey],
                $"📦 {prefix}  ·  {sourceClass}  ({L.T("functions", "函数")}: {specs.Count})",
                true, moduleStyle);

            EditorGUILayout.EndHorizontal();

            if (groupFoldouts[moduleKey])
            {
                EditorGUI.indentLevel++;
                foreach (var spec in specs)
                {
                    DrawSpecNode(spec, localSettings);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 绘制单个子函数节点：名称 + 描述 + 参数（必填/可选、类型、枚举、示例、默认值）。
        /// </summary>
        private static void DrawSpecNode(ToolSpec spec, McpLocalSettings localSettings)
        {
            string key = "spec::" + spec.Name;
            if (!methodFoldouts.ContainsKey(key)) methodFoldouts[key] = false;

            bool enabled = localSettings.IsToolEnabled(spec.Name);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();

            Color toggleOriginalBg = GUI.backgroundColor;
            GUI.backgroundColor = enabled ? new Color(0.4f, 0.9f, 0.5f, 0.5f) : new Color(0.9f, 0.4f, 0.4f, 0.5f);
            bool newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(18));
            GUI.backgroundColor = toggleOriginalBg;

            if (newEnabled != enabled)
            {
                localSettings.SetToolEnabled(spec.Name, newEnabled);
                string statusText = newEnabled ? L.T("enabled", "启用") : L.T("disabled", "禁用");
                Debug.Log($"[McpServiceGUI] {L.T("Tool", "工具")} '{spec.Name}' {L.T("status changed to", "状态已更改为")}: {statusText}");
            }

            GUIStyle specStyle = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 11
            };
            if (enabled)
            {
                specStyle.normal.textColor = new Color(0.7f, 0.9f, 0.7f);
                specStyle.onNormal.textColor = new Color(0.7f, 0.9f, 0.7f);
            }
            else
            {
                specStyle.normal.textColor = new Color(0.9f, 0.6f, 0.6f);
                specStyle.onNormal.textColor = new Color(0.9f, 0.6f, 0.6f);
            }
            specStyle.focused.textColor = specStyle.normal.textColor;
            specStyle.onFocused.textColor = specStyle.normal.textColor;

            string paramCount = spec.Parameters == null ? "0" : spec.Parameters.Length.ToString();
            methodFoldouts[key] = EditorGUILayout.Foldout(
                methodFoldouts[key],
                $"⚙ {spec.Name}  ({L.T("params", "参数")}: {paramCount})",
                true, specStyle);

            GUILayout.FlexibleSpace();

            GUIStyle helpButtonStyle = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(0, 0, 0, 0)
            };
            Color helpButtonOriginalBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.6f, 0.8f, 1f, 0.7f);
            if (GUILayout.Button("?", helpButtonStyle, GUILayout.Width(20), GUILayout.Height(18)))
            {
                PingSourceScript(spec.SourceClass);
            }
            GUI.backgroundColor = helpButtonOriginalBg;

            GUIStyle debugButtonStyle = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(0, 0, 0, 0)
            };
            Color debugOriginalBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.5f, 0.7f, 0.95f, 0.8f);
            if (GUILayout.Button("D", debugButtonStyle, GUILayout.Width(20), GUILayout.Height(18)))
            {
                try
                {
                    McpDebugWindow.ShowWindowWithContent(GenerateSpecExampleJson(spec));
                }
                catch (Exception e)
                {
                    Debug.LogError($"[McpServiceGUI] {L.T("Error generating debug example", "生成调试示例时发生错误")}: {e}");
                }
            }
            GUI.backgroundColor = debugOriginalBg;

            EditorGUILayout.EndHorizontal();

            if (methodFoldouts[key])
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                GUIStyle descStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
                {
                    fontSize = 10,
                    fontStyle = FontStyle.Italic,
                    normal = { textColor = new Color(0.75f, 0.75f, 0.8f) },
                    padding = new RectOffset(4, 4, 2, 2)
                };
                EditorGUILayout.LabelField(spec.Description ?? "", descStyle);
                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField(L.T("📋 Parameters", "📋 参数信息"), EditorStyles.boldLabel);
                DrawParameterTable(spec.Parameters, spec.Required);

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 绘制参数表：必填/可选标记、名称、类型、描述、枚举、示例、默认值。
        /// </summary>
        private static void DrawParameterTable(MethodKey[] keys, string[] required)
        {
            if (keys == null || keys.Length == 0)
            {
                GUIStyle noParamsStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    fontSize = 10,
                    fontStyle = FontStyle.Italic
                };
                EditorGUILayout.LabelField(L.T("📭 No Parameters", "📭 无参数"), noParamsStyle);
                return;
            }

            var requiredSet = new HashSet<string>(required ?? new string[0], StringComparer.Ordinal);

            foreach (var key in keys)
            {
                bool isRequired = requiredSet.Contains(key.Key);

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();

                Color originalKeyColor = GUI.color;
                GUI.color = isRequired ? new Color(0.3f, 0.8f, 0.4f) : new Color(1f, 0.7f, 0.3f);
                EditorGUILayout.LabelField(isRequired ? "●" : "○", EditorStyles.miniLabel, GUILayout.Width(12));
                GUI.color = originalKeyColor;

                GUIStyle keyStyle = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = 10 };
                EditorGUILayout.SelectableLabel(key.Key, keyStyle, GUILayout.Width(130), GUILayout.Height(EditorGUIUtility.singleLineHeight));

                string type = string.IsNullOrEmpty(key.Type) ? key.GetType().Name : key.Type;
                GUIStyle typeStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = 9 };
                EditorGUILayout.LabelField($"[{type}]", typeStyle, GUILayout.Width(70));

                GUIStyle descStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 9,
                    fontStyle = FontStyle.Italic,
                    normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
                };
                EditorGUILayout.LabelField(key.Desc ?? "", descStyle, GUILayout.ExpandWidth(true));
                EditorGUILayout.EndHorizontal();

                // 枚举 / 示例 / 默认值
                if (key.EnumValues != null && key.EnumValues.Count > 0)
                {
                    GUIStyle metaStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        fontSize = 9,
                        normal = { textColor = new Color(0.6f, 0.8f, 1f) },
                        wordWrap = true
                    };
                    EditorGUILayout.LabelField("enum: " + string.Join(" | ", key.EnumValues), metaStyle);
                }

                if (key.Examples != null && key.Examples.Count > 0)
                {
                    GUIStyle metaStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        fontSize = 9,
                        normal = { textColor = new Color(0.7f, 0.9f, 0.7f) },
                        wordWrap = true
                    };
                    EditorGUILayout.LabelField("examples: " + string.Join(", ", key.Examples), metaStyle);
                }

                if (key.DefaultValue != null)
                {
                    GUIStyle metaStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        fontSize = 9,
                        normal = { textColor = new Color(0.9f, 0.8f, 0.6f) },
                        wordWrap = true
                    };
                    EditorGUILayout.LabelField("default: " + key.DefaultValue, metaStyle);
                }

                EditorGUILayout.EndVertical();
            }
        }

        /// <summary>
        /// 绘制内置门面工具分组（async_call / get_prompt / get_resource / search_tool）。
        /// </summary>
        private static void DrawBuiltinGroup(IReadOnlyList<ToolInfo> builtins, McpLocalSettings localSettings)
        {
            const string groupName = "builtin";
            if (!groupFoldouts.ContainsKey(groupName)) groupFoldouts[groupName] = false;

            bool allEnabled = builtins.All(i => localSettings.IsToolEnabled(i.name));
            bool anyEnabled = builtins.Any(i => localSettings.IsToolEnabled(i.name));

            groupIndex++;
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();

            Color originalBackgroundColor = GUI.backgroundColor;
            GUI.backgroundColor = allEnabled ? new Color(0.4f, 0.9f, 0.5f, 0.6f)
                : (anyEnabled ? new Color(1f, 0.9f, 0.4f, 0.6f) : new Color(0.9f, 0.4f, 0.4f, 0.6f));
            bool newToggle = EditorGUILayout.Toggle(allEnabled, GUILayout.Width(18));
            GUI.backgroundColor = originalBackgroundColor;

            if (newToggle != allEnabled)
            {
                localSettings.SetToolsEnabled(builtins.Select(i => i.name), newToggle);
            }

            GUIStyle style = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold, fontSize = 13 };
            Color color = allEnabled ? new Color(0.2f, 0.7f, 0.3f) : (anyEnabled ? new Color(0.8f, 0.7f, 0.2f) : new Color(0.9f, 0.3f, 0.3f));
            style.normal.textColor = color;
            style.onNormal.textColor = color;
            style.focused.textColor = color;
            style.onFocused.textColor = color;

            string icon = allEnabled ? "✅" : (anyEnabled ? "⚠️" : "❌");
            groupFoldouts[groupName] = EditorGUILayout.Foldout(
                groupFoldouts[groupName],
                $"{icon} {groupIndex}. {L.T("Built-in Facades", "内置门面工具")} ({builtins.Count})",
                true, style);

            EditorGUILayout.EndHorizontal();

            if (groupFoldouts[groupName])
            {
                EditorGUI.indentLevel++;
                foreach (var info in builtins)
                {
                    DrawBuiltinNode(info, localSettings);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        /// <summary>
        /// 绘制单个内置门面工具节点：描述 + 输入 schema 属性。
        /// </summary>
        private static void DrawBuiltinNode(ToolInfo info, McpLocalSettings localSettings)
        {
            string key = "builtin::" + info.name;
            if (!methodFoldouts.ContainsKey(key)) methodFoldouts[key] = false;

            bool enabled = localSettings.IsToolEnabled(info.name);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();

            Color toggleOriginalBg = GUI.backgroundColor;
            GUI.backgroundColor = enabled ? new Color(0.4f, 0.9f, 0.5f, 0.5f) : new Color(0.9f, 0.4f, 0.4f, 0.5f);
            bool newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(18));
            GUI.backgroundColor = toggleOriginalBg;

            if (newEnabled != enabled)
            {
                localSettings.SetToolEnabled(info.name, newEnabled);
            }

            GUIStyle specStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold, fontSize = 11 };
            Color specColor = enabled ? new Color(0.7f, 0.9f, 0.7f) : new Color(0.9f, 0.6f, 0.6f);
            specStyle.normal.textColor = specColor;
            specStyle.onNormal.textColor = specColor;
            specStyle.focused.textColor = specColor;
            specStyle.onFocused.textColor = specColor;

            methodFoldouts[key] = EditorGUILayout.Foldout(methodFoldouts[key], $"⚙ {info.name}", true, specStyle);
            EditorGUILayout.EndHorizontal();

            if (methodFoldouts[key])
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                GUIStyle descStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
                {
                    fontSize = 10,
                    fontStyle = FontStyle.Italic,
                    normal = { textColor = new Color(0.75f, 0.75f, 0.8f) }
                };
                EditorGUILayout.LabelField(info.description ?? "", descStyle);
                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField(L.T("📋 Input Schema", "📋 输入参数"), EditorStyles.boldLabel);
                GUIStyle schemaStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 9,
                    normal = { textColor = new Color(0.6f, 0.8f, 1f) },
                    wordWrap = true
                };
                EditorGUILayout.SelectableLabel(info.inputSchema?.ToString() ?? "{}", schemaStyle,
                    GUILayout.ExpandWidth(true), GUILayout.Height(EditorGUIUtility.singleLineHeight * 6));

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 由工具规格生成调试调用示例 JSON：{"func": "分组.动作", "args": {...}}。
        /// </summary>
        private static string GenerateSpecExampleJson(ToolSpec spec)
        {
            try
            {
                var args = new JsonClass();
                foreach (var key in spec.Parameters ?? new MethodKey[0])
                {
                    JsonNode exampleValue = GenerateExampleValueFromKey(key);
                    if (exampleValue != null) args[key.Key] = exampleValue;
                }

                var call = new JsonClass
                {
                    { "func", new JsonData(spec.Name) },
                    { "args", args }
                };
                return call.ToPrettyString();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[McpServiceGUI] {L.T("Failed to generate example JSON, using basic template", "生成示例JSON失败，使用基础模板")}: {e.Message}");
                var basicCall = new JsonClass
                {
                    { "func", new JsonData(spec.Name) },
                    { "args", new JsonClass() }
                };
                return basicCall.ToPrettyString();
            }
        }

        /// <summary>
        /// 在 Project 窗口中定位到指定类所在的脚本文件。
        /// </summary>
        private static void PingSourceScript(string className)
        {
            if (string.IsNullOrEmpty(className)) return;

            var script = FindScriptByClassName(className);
            if (script != null)
            {
                EditorGUIUtility.PingObject(script);
                return;
            }

            Debug.LogWarning($"[McpServiceGUI] {L.T("Unable to find script in Project window", "无法在Project窗口中找到脚本")}: {className}.cs");
        }

        private static MonoScript FindScriptByClassName(string className)
        {
            string scriptName = className + ".cs";
            foreach (string guid in AssetDatabase.FindAssets(className + " t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(scriptName))
                {
                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                    if (script != null) return script;
                }
            }
            return null;
        }

        /// <summary>
        /// 根据 MethodKey 的真实信息生成示例值
        /// </summary>
        /// <param name="key">MethodKey 对象</param>
        /// <returns>示例值</returns>
        private static JsonNode GenerateExampleValueFromKey(MethodKey key)
        {
            // 1. 优先使用 DefaultValue（但排除空字符串和无效对象）
            if (key.DefaultValue != null)
            {
                // 如果是空字符串，忽略这个默认值
                if (key.DefaultValue is string str && string.IsNullOrEmpty(str))
                {
                    // 跳过，继续尝试其他来源
                }
                // 如果是有效的基本类型或数组，使用它
                else if (IsValidDefaultValue(key.DefaultValue))
                {
                    JsonNode converted = ConvertToJsonNode(key.DefaultValue);
                    if (converted != null)
                        return converted;
                }
            }

            // 2. 如果有 EnumValues，使用第一个枚举值
            if (key.EnumValues != null && key.EnumValues.Count > 0)
            {
                return new JsonData(key.EnumValues[0]);
            }

            // 3. 如果有 Examples，使用第一个示例值
            if (key.Examples != null && key.Examples.Count > 0)
            {
                string example = key.Examples[0];
                
                // 检查是否看起来像 JSON（以 { 或 [ 开头）
                string trimmedExample = example.Trim();
                if (trimmedExample.StartsWith("{") || trimmedExample.StartsWith("["))
                {
                    // 尝试解析为 JSON
                    try
                    {
                        JsonNode parsed = Json.Parse(example);
                        if (parsed != null)
                            return parsed;
                    }
                    catch
                    {
                        // JSON 解析失败，作为字符串返回
                    }
                }
                
                // 不是 JSON 格式或解析失败，作为字符串返回
                return new JsonData(example);
            }

            // 4. 根据类型生成默认值
            return GenerateDefaultValueByType(key);
        }

        /// <summary>
        /// 检查 DefaultValue 是否是有效的值类型
        /// </summary>
        private static bool IsValidDefaultValue(object value)
        {
            if (value == null)
                return false;

            // 接受的类型：基本类型、数组、字符串
            Type type = value.GetType();
            return type.IsPrimitive || type.IsArray || type == typeof(string) || type == typeof(decimal);
        }

        /// <summary>
        /// 将对象转换为 JsonNode
        /// </summary>
        private static JsonNode ConvertToJsonNode(object value)
        {
            if (value == null)
                return null;

            // 处理数组类型
            if (value is Array array)
            {
                var jsonArray = new JsonArray();
                foreach (var item in array)
                {
                    if (item is float f)
                        jsonArray.Add(new JsonData(f));
                    else if (item is int i)
                        jsonArray.Add(new JsonData(i));
                    else if (item is double d)
                        jsonArray.Add(new JsonData(d));
                    else if (item is string s)
                        jsonArray.Add(new JsonData(s));
                    else if (item is bool b)
                        jsonArray.Add(new JsonData(b));
                    else
                        jsonArray.Add(new JsonData(item.ToString()));
                }
                return jsonArray;
            }

            // 处理基本类型
            if (value is string str)
                return new JsonData(str);
            if (value is int intVal)
                return new JsonData(intVal);
            if (value is float floatVal)
                return new JsonData(floatVal);
            if (value is double doubleVal)
                return new JsonData(doubleVal);
            if (value is bool boolVal)
                return new JsonData(boolVal);

            // 其他类型尝试序列化
            try
            {
                return Json.Parse(Json.FromObject(value));
            }
            catch
            {
                return new JsonData(value.ToString());
            }
        }

        /// <summary>
        /// 根据 MethodKey 类型生成默认值
        /// </summary>
        private static JsonNode GenerateDefaultValueByType(MethodKey key)
        {
            string typeName = key.GetType().Name;
            string keyType = key.Type?.ToLower() ?? "";

            // 根据 MethodKey 的具体类型生成默认值
            if (typeName == "MethodBool")
            {
                return new JsonData(false);
            }
            else if (typeName == "MethodInt")
            {
                return new JsonData(0);
            }
            else if (typeName == "MethodFloat")
            {
                return new JsonData(0.0f);
            }
            else if (typeName == "MethodVector")
            {
                // Vector 默认为 [0, 0, 0]
                var arr = new JsonArray();
                arr.Add(new JsonData(0));
                arr.Add(new JsonData(0));
                arr.Add(new JsonData(0));
                return arr;
            }
            else if (typeName == "MethodArr")
            {
                return new JsonArray();
            }
            else if (typeName == "MethodObj")
            {
                return new JsonClass();
            }
            else if (keyType == "string")
            {
                return new JsonData("");
            }
            else if (keyType == "number" || keyType == "integer")
            {
                return new JsonData(0);
            }
            else if (keyType == "boolean")
            {
                return new JsonData(false);
            }
            else if (keyType == "array")
            {
                return new JsonArray();
            }
            else if (keyType == "object")
            {
                return new JsonClass();
            }

            // 默认返回空字符串
            return new JsonData("");
        }

        /// <summary>
        /// 显示工具调试信息
        /// </summary>
        private static void ShowToolDebugInfo()
        {
            var allToolNames = McpService.GetAllToolNames();
            int totalToolCount = McpService.GetToolCount();

            // 筛选出启用的工具
            var enabledToolNames = allToolNames.Where(toolName =>
                McpService.GetLocalSettings().IsToolEnabled(toolName)).ToList();
            int enabledToolCount = enabledToolNames.Count;

            string message = $"{L.T("MCP Tool Debug Information", "MCP工具调试信息")}:\n\n";
            message += $"{L.T("Total registered tools", "已注册工具总数")}: {totalToolCount}\n";
            message += $"{L.T("Enabled tools count", "已启用工具数量")}: {enabledToolCount}\n\n";

            if (enabledToolCount > 0)
            {
                message += $"{L.T("Enabled tools", "已启用的工具")}:\n";
                foreach (var toolName in enabledToolNames)
                {
                    message += $"• {toolName}\n";
                }
            }
            else
            {
                message += $"⚠️ {L.T("No tools enabled!", "没有启用任何工具！")}\n\n";
                message += $"{L.T("Possible reasons", "可能的原因")}:\n";
                message += $"1. {L.T("All tools have been manually disabled", "所有工具都被手动禁用了")}\n";
                message += $"2. {L.T("Tool configuration has issues", "工具配置设置有问题")}\n";
                message += $"3. {L.T("Need to rediscover tools", "需要重新发现工具")}\n";
            }

            if (totalToolCount > enabledToolCount)
            {
                message += $"\n💡 {L.T("Tip", "提示")}: {L.T("There are", "还有")} {totalToolCount - enabledToolCount} {L.T("tools disabled", "个工具被禁用")}";
            }

            message += $"\n\n{L.T("Click 'Rediscover' button to rescan tools.", "点击'重新发现'按钮重新扫描工具。")}";

            if (EditorUtility.DisplayDialog(
                L.T("MCP Tool Debug", "MCP工具调试"), 
                message, 
                L.T("Rediscover", "重新发现"), 
                L.T("Close", "关闭")))
            {
                Debug.Log($"[McpServiceGUI] {L.T("Starting tool rediscovery...", "开始重新发现工具...")}");
                McpService.RefreshDiscovery();

                // 重新获取工具信息
                var newAllToolNames = McpService.GetAllToolNames();
                int newTotalToolCount = McpService.GetToolCount();

                var newEnabledToolNames = newAllToolNames.Where(toolName =>
                    McpService.GetLocalSettings().IsToolEnabled(toolName)).ToList();
                int newEnabledToolCount = newEnabledToolNames.Count;

                string resultMessage = $"{L.T("Rediscovery completed!", "重新发现完成!")}\n\n";
                resultMessage += $"{L.T("Total tools found", "发现工具总数")}: {newTotalToolCount}\n";
                resultMessage += $"{L.T("Enabled tools count", "启用工具数量")}: {newEnabledToolCount}\n\n";

                if (newEnabledToolCount > 0)
                {
                    resultMessage += $"{L.T("Enabled tools", "启用的工具")}:\n";
                    foreach (var toolName in newEnabledToolNames)
                    {
                        resultMessage += $"• {toolName}\n";
                    }
                }

                EditorUtility.DisplayDialog(
                    L.T("Tool Rediscovery Result", "工具重新发现结果"), 
                    resultMessage, 
                    L.T("OK", "确定"));
            }
        }

        /// <summary>
        /// 绘制资源列表配置界面
        /// </summary>
        private static void DrawResourcesList()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));

            // 标题栏
            Rect headerRect = EditorGUILayout.BeginHorizontal(EditorStyles.toolbar, GUILayout.Height(28));
            Color headerBgColor = new Color(0.25f, 0.25f, 0.3f, 0.3f);
            EditorGUI.DrawRect(headerRect, headerBgColor);

            GUIStyle headerTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.95f) },
                padding = new RectOffset(8, 0, 4, 0)
            };
            EditorGUILayout.LabelField(L.T("📦 Configurable Resources", "📦 可配置资源"), headerTitleStyle, GUILayout.ExpandWidth(true));

            // 添加资源按钮
            GUIStyle addButtonStyle = new GUIStyle(EditorStyles.miniButton)
            {
                padding = new RectOffset(10, 10, 4, 4),
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };
            Color addButtonColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.4f, 0.9f, 0.5f, 0.8f);

            if (GUILayout.Button(L.T("➕ Add Resource", "➕ 添加资源"), addButtonStyle, GUILayout.Width(110), GUILayout.Height(22)))
            {
                var settings = McpSettings.Instance;
                var newResource = new UniMcp.ConfigurableResource(
                    L.T("New Resource", "新资源"), 
                    L.T("Resource Description", "资源描述"), 
                    "https://example.com/resource");
                settings.AddConfigurableResource(newResource);
                settings.SaveSettings();
                // 让新资源立即进入发布列表
                McpService.RefreshDiscovery();
            }

            GUI.backgroundColor = addButtonColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // 滚动视图
            resourcesScrollPosition = EditorGUILayout.BeginScrollView(resourcesScrollPosition, GUILayout.ExpandHeight(true));

            var settings2 = McpSettings.Instance;
            var resources = settings2.GetConfigurableResources();

            if (resources == null || resources.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    L.T("No configured resources. Click the \"Add Resource\" button above to add a new resource.", 
                        "暂无配置的资源。点击上方\"添加资源\"按钮添加新资源。"), 
                    MessageType.Info);
            }
            else
            {
                for (int i = 0; i < resources.Count; i++)
                {
                    var resource = resources[i];
                    if (resource == null) continue;

                    EditorGUILayout.BeginVertical("box");

                    // 确保折叠状态存在
                    if (!resourceFoldouts.ContainsKey(i))
                        resourceFoldouts[i] = false;

                    // 资源标题行
                    EditorGUILayout.BeginHorizontal();

                    // 获取资源启用状态
                    bool resourceEnabled = McpService.GetLocalSettings().IsResourceEnabled(resource.Name);

                    // 开关
                    Color toggleOriginalBg = GUI.backgroundColor;
                    if (resourceEnabled)
                    {
                        GUI.backgroundColor = new Color(0.4f, 0.9f, 0.5f, 0.5f);
                    }
                    else
                    {
                        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f, 0.5f);
                    }
                    Rect toggleRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, GUILayout.Width(22));
                    bool newResourceEnabled = EditorGUI.Toggle(toggleRect, resourceEnabled);
                    GUI.backgroundColor = toggleOriginalBg;

                    // 处理资源开关状态变化
                    if (newResourceEnabled != resourceEnabled)
                    {
                        McpService.GetLocalSettings().SetResourceEnabled(resource.Name, newResourceEnabled);
                        // 重新发现资源（同时清 prompts/resources 列表缓存）
                        McpService.RefreshDiscovery();
                        string statusText = newResourceEnabled ? L.T("enabled", "启用") : L.T("disabled", "禁用");
                        Debug.Log($"[McpServiceGUI] {L.T("Resource", "资源")} '{resource.Name}' {L.T("status changed to", "状态已更改为")}: {statusText}");
                    }

                    // 根据启用状态设置文字颜色
                    GUIStyle foldoutStyle = new GUIStyle(EditorStyles.foldout)
                    {
                        fontStyle = FontStyle.Bold,
                        fontSize = 11
                    };
                    if (resourceEnabled)
                    {
                        foldoutStyle.normal.textColor = new Color(0.7f, 0.9f, 0.7f);
                        foldoutStyle.onNormal.textColor = new Color(0.7f, 0.9f, 0.7f);
                    }
                    else
                    {
                        foldoutStyle.normal.textColor = new Color(0.9f, 0.6f, 0.6f);
                        foldoutStyle.onNormal.textColor = new Color(0.9f, 0.6f, 0.6f);
                    }
                    foldoutStyle.focused.textColor = foldoutStyle.normal.textColor;
                    foldoutStyle.onFocused.textColor = foldoutStyle.normal.textColor;

                    resourceFoldouts[i] = EditorGUILayout.Foldout(resourceFoldouts[i], $"📦 {resource.Name}", true, foldoutStyle);

                    // 删除按钮
                    GUIStyle deleteButtonStyle = new GUIStyle(EditorStyles.miniButtonRight)
                    {
                        fontSize = 10,
                        padding = new RectOffset(4, 4, 2, 2)
                    };
                    Color deleteButtonColor = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(1f, 0.5f, 0.5f, 0.8f);

                    if (GUILayout.Button(L.T("Delete", "删除"), deleteButtonStyle, GUILayout.Width(60)))
                    {
                        if (EditorUtility.DisplayDialog(
                            L.T("Confirm Delete", "确认删除"), 
                            $"{L.T("Are you sure you want to delete resource", "确定要删除资源")} '{resource.Name}' {L.T("?", "吗？")}", 
                            L.T("Delete", "删除"), 
                            L.T("Cancel", "取消")))
                        {
                            settings2.RemoveConfigurableResource(resource.Name);
                            settings2.SaveSettings();
                            resourceFoldouts.Remove(i);
                            // 重新发现资源（同时清 prompts/resources 列表缓存）
                            McpService.RefreshDiscovery();
                            break;
                        }
                    }

                    GUI.backgroundColor = deleteButtonColor;
                    EditorGUILayout.EndHorizontal();

                    // 资源详情
                    if (resourceFoldouts[i])
                    {
                        EditorGUI.indentLevel++;

                        // 名称
                        EditorGUI.BeginChangeCheck();
                        string newName = EditorGUILayout.TextField(L.T("Name", "名称"), resource.Name);
                        if (EditorGUI.EndChangeCheck() && !string.IsNullOrEmpty(newName))
                        {
                            resource.SetName(newName);
                            settings2.SaveSettings();
                        }

                        // 描述
                        EditorGUI.BeginChangeCheck();
                        string newDesc = EditorGUILayout.TextField(L.T("Description", "描述"), resource.Description);
                        if (EditorGUI.EndChangeCheck())
                        {
                            resource.SetDescription(newDesc);
                            settings2.SaveSettings();
                        }

                        // 来源类型
                        EditorGUI.BeginChangeCheck();
                        UniMcp.ResourceSourceType newSourceType = (UniMcp.ResourceSourceType)EditorGUILayout.EnumPopup(L.T("Source Type", "来源类型"), resource.SourceType);
                        if (EditorGUI.EndChangeCheck())
                        {
                            resource.SourceType = newSourceType;
                            settings2.SaveSettings();
                        }

                        // 根据来源类型显示不同字段
                        if (resource.SourceType == UniMcp.ResourceSourceType.Url)
                        {
                            EditorGUI.BeginChangeCheck();
                            string newUrl = EditorGUILayout.TextField("URL", resource.Url);
                            if (EditorGUI.EndChangeCheck())
                            {
                                resource.SetUrl(newUrl);
                                settings2.SaveSettings();
                            }
                        }
                        else // UnityObject
                        {
                            EditorGUI.BeginChangeCheck();
                            Object newObject = EditorGUILayout.ObjectField(L.T("Unity Object", "Unity对象"), resource.UnityObject, typeof(Object), false);
                            if (EditorGUI.EndChangeCheck())
                            {
                                resource.UnityObject = newObject;
                                settings2.SaveSettings();
                            }

                            // 显示转换后的URL
                            if (resource.UnityObject != null)
                            {
                                EditorGUI.BeginDisabledGroup(true);
                                EditorGUILayout.TextField("URL", resource.Url);
                                EditorGUI.EndDisabledGroup();
                            }
                        }

                        // MIME类型 - 纯下拉选择（常用资源格式）
                        // 若当前值不在常用列表中，则临时置顶保留自定义值，避免打开面板即被覆盖。
                        string[] mimeOptions = commonMimeTypes;
                        int selectedIndex = Array.IndexOf(mimeOptions, resource.MimeType);
                        if (selectedIndex < 0)
                        {
                            mimeOptions = new string[commonMimeTypes.Length + 1];
                            mimeOptions[0] = string.IsNullOrEmpty(resource.MimeType)
                                ? commonMimeTypes[0]
                                : resource.MimeType;
                            Array.Copy(commonMimeTypes, 0, mimeOptions, 1, commonMimeTypes.Length);
                            selectedIndex = 0;
                        }

                        EditorGUI.BeginChangeCheck();
                        int newSelectedIndex = EditorGUILayout.Popup(
                            L.T("MIME Type", "MIME类型"), selectedIndex, mimeOptions);
                        if (EditorGUI.EndChangeCheck() && newSelectedIndex != selectedIndex)
                        {
                            resource.SetMimeType(mimeOptions[newSelectedIndex]);
                            settings2.SaveSettings();
                        }

                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(4);
                }
            }

            // 代码实现的 IRes 实现类列表（可查看详情并单独开关）
            DrawCodeResourcesList();

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 绘制提示词列表配置界面
        /// </summary>
        private static void DrawPromptsList()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));

            // 标题栏
            Rect headerRect = EditorGUILayout.BeginHorizontal(EditorStyles.toolbar, GUILayout.Height(28));
            Color headerBgColor = new Color(0.25f, 0.25f, 0.3f, 0.3f);
            EditorGUI.DrawRect(headerRect, headerBgColor);

            GUIStyle headerTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.95f) },
                padding = new RectOffset(8, 0, 4, 0)
            };
            EditorGUILayout.LabelField(L.T("💬 Configurable Prompts", "💬 可配置提示词"), headerTitleStyle, GUILayout.ExpandWidth(true));

            // 添加提示词按钮
            GUIStyle addButtonStyle = new GUIStyle(EditorStyles.miniButton)
            {
                padding = new RectOffset(10, 10, 4, 4),
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };
            Color addButtonColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.4f, 0.9f, 0.5f, 0.8f);

            if (GUILayout.Button(L.T("➕ Add Prompt", "➕ 添加提示词"), addButtonStyle, GUILayout.Width(110), GUILayout.Height(22)))
            {
                var settings = McpSettings.Instance;
                var newPrompt = new UniMcp.ConfigurablePrompt(
                    L.T("New Prompt", "新提示词"), 
                    L.T("Prompt Description", "提示词描述"), 
                    L.T("Prompt Content", "提示词内容"));
                settings.AddConfigurablePrompt(newPrompt);
                settings.SaveSettings();
                // 让新提示词立即进入发布列表
                McpService.RefreshDiscovery();
            }

            GUI.backgroundColor = addButtonColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // 滚动视图
            promptsScrollPosition = EditorGUILayout.BeginScrollView(promptsScrollPosition, GUILayout.ExpandHeight(true));

            var settings2 = McpSettings.Instance;
            var prompts = settings2.GetConfigurablePrompts();

            if (prompts == null || prompts.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    L.T("No configured prompts. Click the \"Add Prompt\" button above to add a new prompt.", 
                        "暂无配置的提示词。点击上方\"添加提示词\"按钮添加新提示词。"), 
                    MessageType.Info);
            }
            else
            {
                for (int i = 0; i < prompts.Count; i++)
                {
                    var prompt = prompts[i];
                    if (prompt == null) continue;

                    EditorGUILayout.BeginVertical("box");

                    // 确保折叠状态存在
                    if (!promptFoldouts.ContainsKey(i))
                        promptFoldouts[i] = false;

                    // 提示词标题行
                    EditorGUILayout.BeginHorizontal();

                    // 获取提示词启用状态
                    bool promptEnabled = McpService.GetLocalSettings().IsPromptEnabled(prompt.Name);

                    // 开关
                    Color toggleOriginalBg = GUI.backgroundColor;
                    if (promptEnabled)
                    {
                        GUI.backgroundColor = new Color(0.4f, 0.9f, 0.5f, 0.5f);
                    }
                    else
                    {
                        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f, 0.5f);
                    }
                    Rect toggleRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, GUILayout.Width(22));
                    bool newPromptEnabled = EditorGUI.Toggle(toggleRect, promptEnabled);
                    GUI.backgroundColor = toggleOriginalBg;

                    // 处理提示词开关状态变化
                    if (newPromptEnabled != promptEnabled)
                    {
                        McpService.GetLocalSettings().SetPromptEnabled(prompt.Name, newPromptEnabled);
                        // 重新发现提示词（同时清 prompts/resources 列表缓存）
                        McpService.RefreshDiscovery();
                        string statusText = newPromptEnabled ? L.T("enabled", "启用") : L.T("disabled", "禁用");
                        Debug.Log($"[McpServiceGUI] {L.T("Prompt", "提示词")} '{prompt.Name}' {L.T("status changed to", "状态已更改为")}: {statusText}");
                    }

                    // 根据启用状态设置文字颜色
                    GUIStyle foldoutStyle = new GUIStyle(EditorStyles.foldout)
                    {
                        fontStyle = FontStyle.Bold,
                        fontSize = 11
                    };
                    if (promptEnabled)
                    {
                        foldoutStyle.normal.textColor = new Color(0.7f, 0.9f, 0.7f);
                        foldoutStyle.onNormal.textColor = new Color(0.7f, 0.9f, 0.7f);
                    }
                    else
                    {
                        foldoutStyle.normal.textColor = new Color(0.9f, 0.6f, 0.6f);
                        foldoutStyle.onNormal.textColor = new Color(0.9f, 0.6f, 0.6f);
                    }
                    foldoutStyle.focused.textColor = foldoutStyle.normal.textColor;
                    foldoutStyle.onFocused.textColor = foldoutStyle.normal.textColor;

                    promptFoldouts[i] = EditorGUILayout.Foldout(promptFoldouts[i], $"💬 {prompt.Name}", true, foldoutStyle);

                    // 删除按钮
                    GUIStyle deleteButtonStyle = new GUIStyle(EditorStyles.miniButtonRight)
                    {
                        fontSize = 10,
                        padding = new RectOffset(4, 4, 2, 2)
                    };
                    Color deleteButtonColor = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(1f, 0.5f, 0.5f, 0.8f);

                    if (GUILayout.Button(L.T("Delete", "删除"), deleteButtonStyle, GUILayout.Width(60)))
                    {
                        if (EditorUtility.DisplayDialog(
                            L.T("Confirm Delete", "确认删除"), 
                            $"{L.T("Are you sure you want to delete prompt", "确定要删除提示词")} '{prompt.Name}' {L.T("?", "吗？")}", 
                            L.T("Delete", "删除"), 
                            L.T("Cancel", "取消")))
                        {
                            settings2.RemoveConfigurablePrompt(prompt.Name);
                            settings2.SaveSettings();
                            promptFoldouts.Remove(i);
                            // 重新发现提示词（同时清 prompts/resources 列表缓存）
                            McpService.RefreshDiscovery();
                            break;
                        }
                    }

                    GUI.backgroundColor = deleteButtonColor;
                    EditorGUILayout.EndHorizontal();

                    // 提示词详情
                    if (promptFoldouts[i])
                    {
                        EditorGUI.indentLevel++;

                        // 名称
                        EditorGUI.BeginChangeCheck();
                        string newName = EditorGUILayout.TextField(L.T("Name", "名称"), prompt.Name);
                        if (EditorGUI.EndChangeCheck() && !string.IsNullOrEmpty(newName))
                        {
                            prompt.SetName(newName);
                            settings2.SaveSettings();
                        }

                        // 描述
                        EditorGUI.BeginChangeCheck();
                        string newDesc = EditorGUILayout.TextField(L.T("Description", "描述"), prompt.Description);
                        if (EditorGUI.EndChangeCheck())
                        {
                            prompt.SetDescription(newDesc);
                            settings2.SaveSettings();
                        }

                        // 提示词文本
                        EditorGUI.BeginChangeCheck();
                        string newPromptText = EditorGUILayout.TextArea(prompt.PromptText, GUILayout.Height(100));
                        if (EditorGUI.EndChangeCheck())
                        {
                            prompt.SetPromptText(newPromptText);
                            settings2.SaveSettings();
                        }

                        // 参数列表（只读显示，不提供添加/删除功能）
                        System.Collections.Generic.List<UniMcp.ConfigurableMethodKey> keys = prompt.GetKeys();
                        if (keys != null && keys.Count > 0)
                        {
                            EditorGUILayout.Space(4);
                            EditorGUILayout.LabelField(L.T("Parameter Information (Read-only)", "参数信息（只读）"), EditorStyles.boldLabel);
                            EditorGUILayout.HelpBox(
                                L.T("Parameter information for configuration-based prompts is read-only. To modify parameters, implement the IPrompts interface in code.", 
                                    "配置方式的提示词参数信息为只读显示。如需修改参数，请通过代码实现IPrompts接口。"), 
                                MessageType.Info);
                            
                            for (int j = 0; j < keys.Count; j++)
                            {
                                var key = keys[j];
                                if (key == null) continue;

                                EditorGUILayout.BeginVertical("box");
                                EditorGUILayout.LabelField($"{L.T("Parameter", "参数")} {j + 1}: {key.key}", EditorStyles.miniBoldLabel);
                                EditorGUILayout.LabelField($"{L.T("Type", "类型")}: {key.type}, {L.T("Optional", "可选")}: {key.optional}", EditorStyles.miniLabel);
                                EditorGUILayout.LabelField($"{L.T("Description", "描述")}: {key.desc}", EditorStyles.miniLabel);
                                
                                if (key.examples != null && key.examples.Count > 0)
                                {
                                    EditorGUILayout.LabelField($"{L.T("Examples", "示例")}: {string.Join(", ", key.examples)}", EditorStyles.miniLabel);
                                }
                                
                                if (key.enumValues != null && key.enumValues.Count > 0)
                                {
                                    EditorGUILayout.LabelField($"{L.T("Enum Values", "枚举值")}: {string.Join(", ", key.enumValues)}", EditorStyles.miniLabel);
                                }
                                
                                EditorGUILayout.EndVertical();
                                EditorGUILayout.Space(2);
                            }
                        }

                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(4);
                }
            }

            // 代码实现的 IPrompts 实现类列表（可查看详情并单独开关）
            DrawCodePromptsList();

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 绘制代码实现的 IRes 实现类列表。
        /// 与"可配置资源"并列：此处只读展示实现类的名称/URL/描述/MIME 类型，并可单独开关其在 MCP 发布列表中的可见性。
        /// 数据源为 McpService.GetAllResources() 中剔除 ConfigurableResource 后的实例（即经反射发现的实现类）。
        /// </summary>
        private static void DrawCodeResourcesList()
        {
            var allResources = McpService.GetAllResources();
            // 服务未启动（或首次打开面板）时 availableResources 可能为空，尝试自动发现一次，
            // 确保代码实现的 IRes 即便在 MCP 关闭时也能在面板中列出。
            if (!codeDiscoveryTried && allResources.Count == 0)
            {
                codeDiscoveryTried = true;
                McpService.RefreshDiscovery();
                allResources = McpService.GetAllResources();
            }
            var codeResources = allResources.Where(r => !(r is ConfigurableResource)).ToList();

            EditorGUILayout.Space(10);

            GUIStyle sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.35f, 0.6f, 0.95f) }
            };
            EditorGUILayout.LabelField(
                L.T("💻 Code-implemented Resources (IRes)", "💻 代码实现的资源 (IRes)"),
                sectionTitleStyle);

            if (codeResources.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    L.T("No code-implemented IRes found. Implement the IRes interface in a script to add one here.",
                        "未找到代码实现的 IRes。在脚本中实现 IRes 接口即可在此添加。"),
                    MessageType.Info);
                return;
            }

            var localSettings = McpService.GetLocalSettings();

            for (int i = 0; i < codeResources.Count; i++)
            {
                var resource = codeResources[i];
                if (resource == null) continue;

                string key = "coderesource::" + i;
                if (!codeResourceFoldouts.ContainsKey(key)) codeResourceFoldouts[key] = false;

                string className = resource.GetType().Name;
                bool enabled = localSettings.IsResourceEnabled(resource.Name);

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();

                // 开关：控制该实现类资源在 MCP 发布列表中的可见性
                Color toggleOriginalBg = GUI.backgroundColor;
                GUI.backgroundColor = enabled ? new Color(0.4f, 0.9f, 0.5f, 0.5f) : new Color(0.9f, 0.4f, 0.4f, 0.5f);
                Rect toggleRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, GUILayout.Width(22));
                bool newEnabled = EditorGUI.Toggle(toggleRect, enabled);
                GUI.backgroundColor = toggleOriginalBg;

                if (newEnabled != enabled)
                {
                    localSettings.SetResourceEnabled(resource.Name, newEnabled);
                    McpService.RefreshDiscovery();
                    string statusText = newEnabled ? L.T("enabled", "启用") : L.T("disabled", "禁用");
                    Debug.Log($"[McpServiceGUI] {L.T("Code resource", "代码资源")} '{resource.Name}' {L.T("status changed to", "状态已更改为")}: {statusText}");
                }

                // 实现类标签：置于标题左侧，避免与折叠标题文本（长 Name）重叠
                GUIStyle classTagStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 9,
                    normal = { textColor = new Color(0.6f, 0.8f, 1f) }
                };
                EditorGUILayout.LabelField($"class: {className}", classTagStyle, GUILayout.Width(150));

                // 标题（折叠）
                GUIStyle foldoutStyle = new GUIStyle(EditorStyles.foldout)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 11
                };
                foldoutStyle.normal.textColor = enabled ? new Color(0.7f, 0.9f, 0.7f) : new Color(0.9f, 0.6f, 0.6f);
                foldoutStyle.onNormal.textColor = foldoutStyle.normal.textColor;
                foldoutStyle.focused.textColor = foldoutStyle.normal.textColor;
                foldoutStyle.onFocused.textColor = foldoutStyle.normal.textColor;

                codeResourceFoldouts[key] = EditorGUILayout.Foldout(codeResourceFoldouts[key], $"📦 {resource.Name}", true, foldoutStyle);

                GUILayout.FlexibleSpace();

                // 定位实现脚本按钮
                GUIStyle helpButtonStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    padding = new RectOffset(0, 0, 0, 0)
                };
                Color helpButtonOriginalBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.6f, 0.8f, 1f, 0.7f);
                if (GUILayout.Button("?", helpButtonStyle, GUILayout.Width(20), GUILayout.Height(18)))
                {
                    PingSourceScript(className);
                }
                GUI.backgroundColor = helpButtonOriginalBg;

                EditorGUILayout.EndHorizontal();

                if (codeResourceFoldouts[key])
                {
                    EditorGUI.indentLevel++;

                    GUIStyle miniWrap = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
                    {
                        fontSize = 10,
                        normal = { textColor = new Color(0.75f, 0.75f, 0.8f) }
                    };
                    EditorGUILayout.LabelField(L.T("URL", "地址") + ": " + (resource.Url ?? ""), miniWrap);
                    EditorGUILayout.LabelField(L.T("Description", "描述") + ": " + (resource.Description ?? ""), miniWrap);
                    EditorGUILayout.LabelField(L.T("MIME Type", "MIME类型") + ": " + (resource.MimeType ?? ""), EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(L.T("Implementation Class", "实现类") + ": " + resource.GetType().FullName, EditorStyles.miniLabel);

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }
        }

        /// <summary>
        /// 绘制代码实现的 IPrompts 实现类列表。
        /// 与"可配置提示词"并列：此处只读展示实现类的名称/描述/参数/正文，并可单独开关其在 MCP 发布列表中的可见性。
        /// 数据源为 McpService.GetAllPrompts() 中剔除 ConfigurablePrompt 后的实例（即经反射发现的实现类）。
        /// </summary>
        private static void DrawCodePromptsList()
        {
            var allPrompts = McpService.GetAllPrompts();
            // 服务未启动（或首次打开面板）时 availablePrompts 可能为空，尝试自动发现一次，
            // 确保代码实现的 IPrompts 即便在 MCP 关闭时也能在面板中列出。
            if (!codeDiscoveryTried && allPrompts.Count == 0)
            {
                codeDiscoveryTried = true;
                McpService.RefreshDiscovery();
                allPrompts = McpService.GetAllPrompts();
            }
            var codePrompts = allPrompts.Where(p => !(p is ConfigurablePrompt)).ToList();

            EditorGUILayout.Space(10);

            GUIStyle sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.35f, 0.6f, 0.95f) }
            };
            EditorGUILayout.LabelField(
                L.T("💻 Code-implemented Prompts (IPrompts)", "💻 代码实现的提示词 (IPrompts)"),
                sectionTitleStyle);

            if (codePrompts.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    L.T("No code-implemented IPrompts found. Implement the IPrompts interface in a script to add one here.",
                        "未找到代码实现的 IPrompts。在脚本中实现 IPrompts 接口即可在此添加。"),
                    MessageType.Info);
                return;
            }

            var localSettings = McpService.GetLocalSettings();

            for (int i = 0; i < codePrompts.Count; i++)
            {
                var prompt = codePrompts[i];
                if (prompt == null) continue;

                string key = "codeprompt::" + i;
                if (!codePromptFoldouts.ContainsKey(key)) codePromptFoldouts[key] = false;

                string className = prompt.GetType().Name;
                bool enabled = localSettings.IsPromptEnabled(prompt.Name);

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();

                // 开关：控制该实现类提示词在 MCP 发布列表中的可见性
                Color toggleOriginalBg = GUI.backgroundColor;
                GUI.backgroundColor = enabled ? new Color(0.4f, 0.9f, 0.5f, 0.5f) : new Color(0.9f, 0.4f, 0.4f, 0.5f);
                Rect toggleRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, GUILayout.Width(22));
                bool newEnabled = EditorGUI.Toggle(toggleRect, enabled);
                GUI.backgroundColor = toggleOriginalBg;

                if (newEnabled != enabled)
                {
                    localSettings.SetPromptEnabled(prompt.Name, newEnabled);
                    McpService.RefreshDiscovery();
                    string statusText = newEnabled ? L.T("enabled", "启用") : L.T("disabled", "禁用");
                    Debug.Log($"[McpServiceGUI] {L.T("Code prompt", "代码提示词")} '{prompt.Name}' {L.T("status changed to", "状态已更改为")}: {statusText}");
                }

                // 实现类标签：置于标题左侧，避免与折叠标题文本（长 Name）重叠
                GUIStyle classTagStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 9,
                    normal = { textColor = new Color(0.6f, 0.8f, 1f) }
                };
                EditorGUILayout.LabelField($"class: {className}", classTagStyle, GUILayout.Width(150));

                // 标题（折叠）
                GUIStyle foldoutStyle = new GUIStyle(EditorStyles.foldout)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 11
                };
                foldoutStyle.normal.textColor = enabled ? new Color(0.7f, 0.9f, 0.7f) : new Color(0.9f, 0.6f, 0.6f);
                foldoutStyle.onNormal.textColor = foldoutStyle.normal.textColor;
                foldoutStyle.focused.textColor = foldoutStyle.normal.textColor;
                foldoutStyle.onFocused.textColor = foldoutStyle.normal.textColor;

                codePromptFoldouts[key] = EditorGUILayout.Foldout(codePromptFoldouts[key], $"💬 {prompt.Name}", true, foldoutStyle);

                GUILayout.FlexibleSpace();

                // 定位实现脚本按钮
                GUIStyle helpButtonStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    padding = new RectOffset(0, 0, 0, 0)
                };
                Color helpButtonOriginalBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.6f, 0.8f, 1f, 0.7f);
                if (GUILayout.Button("?", helpButtonStyle, GUILayout.Width(20), GUILayout.Height(18)))
                {
                    PingSourceScript(className);
                }
                GUI.backgroundColor = helpButtonOriginalBg;

                EditorGUILayout.EndHorizontal();

                if (codePromptFoldouts[key])
                {
                    EditorGUI.indentLevel++;

                    GUIStyle descStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
                    {
                        fontSize = 10,
                        fontStyle = FontStyle.Italic,
                        normal = { textColor = new Color(0.75f, 0.75f, 0.8f) }
                    };
                    EditorGUILayout.LabelField(L.T("Description", "描述") + ": " + (prompt.Description ?? ""), descStyle);
                    EditorGUILayout.LabelField(L.T("Implementation Class", "实现类") + ": " + prompt.GetType().FullName, EditorStyles.miniLabel);

                    // 提示词正文（只读）
                    EditorGUILayout.LabelField(L.T("Prompt Text", "提示词正文"), EditorStyles.boldLabel);
                    EditorGUILayout.SelectableLabel(
                        prompt.PromptText ?? "",
                        new GUIStyle(EditorStyles.wordWrappedMiniLabel) { fontSize = 10 },
                        GUILayout.ExpandWidth(true),
                        GUILayout.Height(EditorGUIUtility.singleLineHeight * 4));

                    // 参数（只读）
                    var keys = prompt.Keys;
                    if (keys != null && keys.Length > 0)
                    {
                        EditorGUILayout.Space(4);
                        EditorGUILayout.LabelField(L.T("📋 Arguments (Read-only)", "📋 参数（只读）"), EditorStyles.boldLabel);
                        foreach (var k in keys)
                        {
                            if (k == null) continue;
                            EditorGUILayout.BeginVertical("box");
                            EditorGUILayout.LabelField(
                                $"{k.Key}  [{k.Type}]  {(k.Optional ? "(optional)" : "(required)")}",
                                EditorStyles.miniBoldLabel);
                            EditorGUILayout.LabelField($"{L.T("Description", "描述")}: {k.Desc}", EditorStyles.miniLabel);
                            if (k.Examples != null && k.Examples.Count > 0)
                                EditorGUILayout.LabelField($"{L.T("Examples", "示例")}: {string.Join(", ", k.Examples)}", EditorStyles.miniLabel);
                            if (k.EnumValues != null && k.EnumValues.Count > 0)
                                EditorGUILayout.LabelField($"{L.T("Enum Values", "枚举值")}: {string.Join(", ", k.EnumValues)}", EditorStyles.miniLabel);
                            EditorGUILayout.EndVertical();
                            EditorGUILayout.Space(2);
                        }
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }
        }

        /// <summary>
        /// 刷新所有列表（工具、资源、提示词）
        /// </summary>
        private static void RefreshAllLists()
        {
            Debug.Log("[McpServiceGUI] Refreshing all lists (tools, resources, prompts)...");
            
            // 清除折叠状态，让界面重新渲染
            methodFoldouts.Clear();
            groupFoldouts.Clear();
            resourceFoldouts.Clear();
            promptFoldouts.Clear();
            codePromptFoldouts.Clear();
            codeResourceFoldouts.Clear();
            codeDiscoveryTried = false;
            
            // 重新发现工具（这会同时刷新工具、资源和提示词）
            McpService.RefreshDiscovery();
            
            Debug.Log("[McpServiceGUI] All lists refreshed.");
        }

    }
}
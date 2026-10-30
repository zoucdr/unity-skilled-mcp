using UnityEngine;
using UnityEditor;
using UniMcp.Utils;
using UniMcp.Executer;

namespace UniMcp.Gui
{
    /// <summary>
    /// MCP设置提供器，用于在Unity的ProjectSettings窗口中显示MCP设置
    /// </summary>
    public static class McpSettingsProvider
    {
        private static string lastLanguage = "";
        
        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            // 监听语言变化
            EditorApplication.update += CheckLanguageChange;
        }
        
        private static void CheckLanguageChange()
        {
            string currentLanguage = McpLocalSettings.Instance.CurrentLanguage;
            if (lastLanguage != currentLanguage)
            {
                lastLanguage = currentLanguage;
                Debug.Log($"[McpSettingsProvider] Language changed to: {currentLanguage}");
                
                // 清除工具缓存，让工具实例重新创建
                ToolsCall.ClearRegisteredMethods();
                
                // 刷新ProjectSettings窗口
                EditorApplication.delayCall += () =>
                {
                    var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                    foreach (var window in windows)
                    {
                        if (window.GetType().Name == "ProjectSettingsWindow")
                        {
                            window.Repaint();
                        }
                    }
                };
            }
        }
        [SettingsProvider]
        public static SettingsProvider CreateMcpSettingsProvider()
        {
            var provider = new SettingsProvider("Project/MCP", SettingsScope.Project)
            {
                label = "MCP",
                guiHandler = (searchContext) =>
                {
                    DrawMcpSettings();
                },
                keywords = new[] { "MCP", "Settings", "Configuration", "Debug", "Bridge", "Server" }
            };

            return provider;
        }

        private static void DrawMcpSettings()
        {
            var settings = McpSettings.Instance;

            // 美化标题区域
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            // 标题样式
            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.2f, 0.5f, 0.9f) },
                padding = new RectOffset(0, 0, 8, 8)
            };
            
            // 美化帮助信息
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUIStyle helpTextStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                wordWrap = true,
                normal = { textColor = new Color(0.85f, 0.85f, 0.9f) },
                padding = new RectOffset(8, 8, 6, 6)
            };
            
            string helpText = L.IsChinese()
                ? "🚀 Unity Skilled MCP是一个强大的Unity扩展工具，提供了智能的UI生成、代码管理和项目优化功能。\n" +
                  "💡 通过与AI模型的深度集成，Unity Skilled MCP能够帮助开发者快速创建高质量的Unity项目。"
                : "🚀 Unity Skilled MCP is a powerful Unity extension tool that provides intelligent UI generation, code management and project optimization features.\n" +
                  "💡 Through deep integration with AI models, Unity Skilled MCP helps developers quickly create high-quality Unity projects.";
            
            EditorGUILayout.LabelField(helpText, helpTextStyle);
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8);

            // 绘制完整的MCP管理GUI
            McpServiceGUI.DrawGUI();

            // 自动保存
            if (GUI.changed)
            {
                settings.SaveSettings();
            }
        }
    }
}

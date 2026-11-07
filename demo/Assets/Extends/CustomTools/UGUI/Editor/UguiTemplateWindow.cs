using UnityEngine;
using UnityEditor;
using UniMcp.Ugui;

namespace UniMcp.Ugui
{
    // ============================================================================
    // UGUI 模板生成器窗口 + 菜单（菜单执行一键模板化，对齐 figma 的 McpRulesWindow 范式）
    // ============================================================================
    public class UguiTemplateWindow : EditorWindow
    {
        private static readonly string[] TEMPLATES = { "dashboard", "login", "settings", "cardGrid", "statPanel" };
        private int sel = 0;
        private string rootName = "";

        [MenuItem("Window/UGUI/Template Generator")]
        public static void ShowWindow()
        {
            var w = GetWindow<UguiTemplateWindow>("UGUI Templates");
            w.minSize = new Vector2(360, 220);
            w.Show();
        }

        // 菜单一键：GameObject/UGUI/Generate/<模板>
        [MenuItem("GameObject/UGUI/Generate/Dashboard")]
        public static void GenDashboard() { UguiGenerator.Build("dashboard"); }
        [MenuItem("GameObject/UGUI/Generate/Login")]
        public static void GenLogin() { UguiGenerator.Build("login"); }
        [MenuItem("GameObject/UGUI/Generate/Settings")]
        public static void GenSettings() { UguiGenerator.Build("settings"); }
        [MenuItem("GameObject/UGUI/Generate/Card Grid")]
        public static void GenCardGrid() { UguiGenerator.Build("cardGrid"); }
        [MenuItem("GameObject/UGUI/Generate/Stat Panel")]
        public static void GenStatPanel() { UguiGenerator.Build("statPanel"); }

        // 创建可编辑主题资产
        [MenuItem("Assets/Create/UGUI/Theme")]
        public static void CreateTheme()
        {
            var t = UguiTheme.Default();
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/UguiTheme.asset");
            AssetDatabase.CreateAsset(t, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = t;
            EditorGUIUtility.PingObject(t);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("UGUI 一键模板生成", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("选择模板后点 Generate，即在场景根生成一套 UGUI（UnityEngine.UI）层级。坐标/配色由 UguiTheme 设计令牌驱动。", MessageType.Info);
            sel = EditorGUILayout.Popup("模板 Template", sel, TEMPLATES);
            rootName = EditorGUILayout.TextField("根节点名 Root", rootName);
            EditorGUILayout.Space(10);
            if (GUILayout.Button("Generate  一键生成", GUILayout.Height(36)))
            {
                string rn = string.IsNullOrEmpty(rootName) ? null : rootName;
                var go = UguiGenerator.Build(TEMPLATES[sel], rn);
                if (go != null)
                {
                    Debug.Log("[Ugui] 已生成模板 " + TEMPLATES[sel] + " -> " + go.name);
                    EditorGUIUtility.PingObject(go);
                }
            }
            EditorGUILayout.Space(6);
            if (GUILayout.Button("Create Theme Asset  创建设计令牌"))
            {
                CreateTheme();
            }
        }
    }
}

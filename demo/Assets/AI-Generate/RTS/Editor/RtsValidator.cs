using System.IO;
using System.Text;
using IronFront;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IronFront.EditorTools
{
    /// <summary>验收断言：场景结构、组件引用、贴图与材质是否齐备。</summary>
    public static class RtsValidator
    {
        [MenuItem("Tools/Iron Front/Validate Scene")]
        public static void Validate()
        {
            StringBuilder sb = new StringBuilder();
            int fail = 0;

            sb.AppendLine("=== IRON FRONT VALIDATION ===");

            if (!File.Exists(RtsSceneBuilder.ScenePath))
            {
                sb.AppendLine("[FAIL] scene file missing: " + RtsSceneBuilder.ScenePath);
                Debug.LogError(sb.ToString());
                return;
            }
            sb.AppendLine("[ OK ] scene file exists");

            Scene scene = EditorSceneManager.OpenScene(RtsSceneBuilder.ScenePath, OpenSceneMode.Additive);

            try
            {
                Camera cam = null;
                RtsBootstrap boot = null;
                Light light = null;

                GameObject[] roots = scene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    GameObject go = roots[i];
                    if (cam == null) cam = go.GetComponentInChildren<Camera>(true);
                    if (light == null) light = go.GetComponentInChildren<Light>(true);
                    if (boot == null) boot = go.GetComponentInChildren<RtsBootstrap>(true);
                }

                fail += Check(sb, cam != null, "main camera present");
                fail += Check(sb, light != null, "directional light present");
                fail += Check(sb, boot != null, "RtsBootstrap present");

                if (boot != null)
                {
                    fail += Check(sb, boot.Cam != null, "bootstrap.Cam assigned");
                    fail += Check(sb, boot.Game != null, "bootstrap.Game assigned");
                    fail += Check(sb, boot.GetComponent<RtsGame>() != null, "RtsGame component");
                    fail += Check(sb, boot.GetComponent<RtsInput>() != null, "RtsInput component");
                    fail += Check(sb, boot.GetComponent<RtsAI>() != null, "RtsAI component");
                    fail += Check(sb, boot.GetComponent<RtsCheats>() != null, "RtsCheats component");
                    fail += Check(sb, boot.GetComponent<RtsBuildPlacer>() != null, "RtsBuildPlacer component");
                }

                if (cam != null)
                {
                    fail += Check(sb, cam.GetComponent<RtsCamera>() != null, "RtsCamera on camera");
                }

                string[] textures =
                {
                    "Assets/Resources/RTS/T_Ground.png",
                    "Assets/Resources/RTS/T_Rock.png",
                    "Assets/Resources/RTS/T_Metal.png",
                    "Assets/Resources/RTS/T_Crystal.png"
                };

                for (int i = 0; i < textures.Length; i++)
                {
                    fail += Check(sb, File.Exists(textures[i]), "texture " + Path.GetFileName(textures[i]));
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            sb.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL (" + fail + ")");

            try
            {
                File.WriteAllText("Assets/AI-Generate/RTS/validation.txt", sb.ToString());
                AssetDatabase.Refresh();
            }
            catch { }

            if (fail == 0) Debug.Log(sb.ToString());
            else Debug.LogError(sb.ToString());
        }

        private static int Check(StringBuilder sb, bool ok, string label)
        {
            sb.AppendLine((ok ? "[ OK ] " : "[FAIL] ") + label);
            return ok ? 0 : 1;
        }

        [MenuItem("Tools/Iron Front/Report Play State")]
        public static void ReportPlayState()
        {
            RtsCheats cheats = Object.FindObjectOfType<RtsCheats>();
            if (cheats == null)
            {
                Debug.LogWarning("[IRON FRONT] RtsCheats not found (not in play mode?)");
                return;
            }
            Debug.Log("[IRON FRONT REPORT] " + cheats.Report());
        }
    }
}

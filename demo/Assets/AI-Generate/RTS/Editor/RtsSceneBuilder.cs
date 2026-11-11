using System.IO;
using IronFront;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IronFront.EditorTools
{
    /// <summary>编辑器装配：新建 RTS 场景（additive，不动当前场景），放光照/相机/系统对象。</summary>
    public static class RtsSceneBuilder
    {
        public const string ScenePath = "Assets/AI-Generate/RTS/Scenes/RTS_Main.unity";

        [MenuItem("Tools/Iron Front/Build Scene")]
        public static void BuildMenu()
        {
            Build();
        }

        public static void Build()
        {
            string dir = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

            BuildLighting();
            Camera cam = BuildCamera();
            BuildSystems(cam);

            EditorSceneManager.MarkSceneDirty(scene);
            bool ok = EditorSceneManager.SaveScene(scene, ScenePath);
            EditorSceneManager.CloseScene(scene, true);

            Debug.Log("[IRON FRONT] scene built: " + ScenePath + " saved=" + ok);
        }

        private static void BuildLighting()
        {
            GameObject lightGo = new GameObject("Directional Light");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.78f;
            lightGo.transform.rotation = Quaternion.Euler(38f, -30f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.47f, 0.5f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.62f, 0.66f, 0.7f);
            RenderSettings.fogStartDistance = 120f;
            RenderSettings.fogEndDistance = 320f;
        }

        private static Camera BuildCamera()
        {
            GameObject camGo = new GameObject("Main Camera");
            Camera cam = camGo.AddComponent<Camera>();
            camGo.tag = "MainCamera";
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 400f;
            cam.clearFlags = CameraClearFlags.Skybox;

            camGo.AddComponent<AudioListener>();

            float h = 34f;
            float back = h / Mathf.Tan(55f * Mathf.Deg2Rad);
            camGo.transform.position = new Vector3(-26f, h, -30f - back);
            camGo.transform.rotation = Quaternion.Euler(55f, 0f, 0f);

            RtsCamera rc = camGo.AddComponent<RtsCamera>();
            rc.HomeTarget = new Vector3(-26f, 0f, -30f);
            return cam;
        }

        private static void BuildSystems(Camera cam)
        {
            GameObject sys = new GameObject("RTS_Systems");

            RtsGame game = sys.AddComponent<RtsGame>();
            sys.AddComponent<RtsBuildPlacer>();
            sys.AddComponent<RtsInput>();
            sys.AddComponent<RtsAI>();
            sys.AddComponent<RtsCheats>();

            RtsBootstrap boot = sys.AddComponent<RtsBootstrap>();
            boot.Cam = cam;
            boot.Game = game;
        }

        [MenuItem("Tools/Iron Front/Open RTS Scene")]
        public static void OpenScene()
        {
            if (!File.Exists(ScenePath))
            {
                Debug.LogError("[IRON FRONT] scene not built yet: " + ScenePath);
                return;
            }
            EditorSceneManager.SaveOpenScenes();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
    }
}

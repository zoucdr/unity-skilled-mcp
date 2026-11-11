using UnityEngine;

namespace IronFront
{
    /// <summary>场景入口：把各系统引用接好，并构建 HUD。所有引用在运行时补齐，不依赖场景手工拖拽。</summary>
    public class RtsBootstrap : MonoBehaviour
    {
        public Camera Cam;
        public RtsGame Game;
        public RtsInput InputCtl;
        public RtsCamera CamCtl;
        public RtsBuildPlacer Placer;
        public RtsHUD Hud;
        public RtsWorldOverlay Overlay;
        public RtsAI AI;
        public RtsCheats Cheats;

        private void Awake()
        {
            if (Cam == null) Cam = Camera.main;

            Game = GetComponent<RtsGame>();
            if (Game == null) Game = gameObject.AddComponent<RtsGame>();
            RtsGame.Bind(Game);

            Game.Root = transform;
            Game.UnitRoot = FindOrCreate(transform, "Units");
            Game.BuildingRoot = FindOrCreate(transform, "Buildings");
            Game.NodeRoot = FindOrCreate(transform, "Nodes");

            RtsMap.Build(Game);

            if (Placer == null) Placer = GetComponent<RtsBuildPlacer>();
            if (Placer == null) Placer = gameObject.AddComponent<RtsBuildPlacer>();

            if (InputCtl == null) InputCtl = GetComponent<RtsInput>();
            if (InputCtl == null) InputCtl = gameObject.AddComponent<RtsInput>();
            InputCtl.Cam = Cam;
            InputCtl.Placer = Placer;

            if (CamCtl == null && Cam != null) CamCtl = Cam.GetComponent<RtsCamera>();
            if (AI == null) AI = GetComponent<RtsAI>();
            if (Cheats == null) Cheats = GetComponent<RtsCheats>();
            if (Cheats == null) Cheats = gameObject.AddComponent<RtsCheats>();

            if (Hud == null) Hud = GetComponent<RtsHUD>();
            if (Hud == null) Hud = gameObject.AddComponent<RtsHUD>();
            Hud.Cam = Cam;
            Hud.Placer = Placer;
            RtsHudBuilder.Build(Hud, transform);

            if (Overlay == null) Overlay = GetComponent<RtsWorldOverlay>();
            if (Overlay == null) Overlay = gameObject.AddComponent<RtsWorldOverlay>();
            Overlay.Init(Cam, InputCtl);

            Debug.Log("[IRON FRONT] bootstrap ready. units=" + Game.Units.Count + " buildings=" + Game.Buildings.Count);
        }

        private static Transform FindOrCreate(Transform parent, string name)
        {
            Transform t = parent.Find(name);
            if (t != null) return t;

            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }
    }
}

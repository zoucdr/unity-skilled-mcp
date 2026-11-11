using UnityEngine;

namespace IronFront
{
    /// <summary>材质缓存 + 程序化低模工厂。所有几何用基础图元拼装，零外部模型依赖。</summary>
    public static class RtsArt
    {
        private static Shader _standard;
        private static Material _teamBlue;
        private static Material _teamRed;
        private static Material _teamNeutral;
        private static Material _metal;
        private static Material _dark;
        private static Material _ground;
        private static Material _backdrop;
        private static Material _crystal;
        private static Material _ghostOk;
        private static Material _ghostBad;

        public static Shader Sh
        {
            get
            {
                if (_standard == null)
                {
                    _standard = Shader.Find("Standard");
                    if (_standard == null) _standard = Shader.Find("Legacy Shaders/Diffuse");
                }
                return _standard;
            }
        }

        private static Material Make(string name, Color c, float metallic, float smooth)
        {
            Material m = new Material(Sh);
            m.name = name;
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
            return m;
        }

        public static Material TeamMat(Team t)
        {
            if (t == Team.Blue)
            {
                if (_teamBlue == null) _teamBlue = Make("M_TeamBlue", RtsDefs.TeamColor(Team.Blue), 0.15f, 0.3f);
                return _teamBlue;
            }
            if (t == Team.Red)
            {
                if (_teamRed == null) _teamRed = Make("M_TeamRed", RtsDefs.TeamColor(Team.Red), 0.15f, 0.3f);
                return _teamRed;
            }
            if (_teamNeutral == null) _teamNeutral = Make("M_TeamNeutral", RtsDefs.TeamColor(Team.Neutral), 0.1f, 0.25f);
            return _teamNeutral;
        }

        public static Material Metal
        {
            get
            {
                if (_metal == null)
                {
                    Texture2D tex = Resources.Load<Texture2D>("RTS/T_Metal");
                    if (tex != null) _metal = MakeTex("M_Metal", tex, 0.35f, 0.4f);
                    else _metal = Make("M_Metal", new Color(0.42f, 0.46f, 0.5f), 0.35f, 0.4f);
                }
                return _metal;
            }
        }

        public static Material Dark
        {
            get
            {
                if (_dark == null) _dark = Make("M_Dark", new Color(0.13f, 0.14f, 0.16f), 0.1f, 0.2f);
                return _dark;
            }
        }

        public static Material Ground
        {
            get
            {
                if (_ground == null)
                {
                    Texture2D tex = Resources.Load<Texture2D>("RTS/T_Ground");
                    if (tex != null)
                    {
                        _ground = MakeTex("M_Ground", tex, 0.05f, 0.18f);
                        if (_ground.HasProperty("_MainTex")) _ground.mainTextureScale = new Vector2(8f, 8f);
                    }
                    else _ground = Make("M_Ground", new Color(0.24f, 0.36f, 0.2f), 0.05f, 0.2f);
                }
                return _ground;
            }
        }

        /// <summary>地图外大底盘：比可玩区更暗，配合雾色让边界不露天空。</summary>
        public static Material Backdrop
        {
            get
            {
                if (_backdrop == null)
                {
                    Texture2D tex = Resources.Load<Texture2D>("RTS/T_Ground");
                    if (tex != null)
                    {
                        _backdrop = MakeTex("M_Backdrop", tex, 0f, 0.1f);
                        _backdrop.mainTextureScale = new Vector2(60f, 60f);
                        if (_backdrop.HasProperty("_Color")) _backdrop.SetColor("_Color", new Color(0.42f, 0.48f, 0.4f));
                    }
                    else _backdrop = Make("M_Backdrop", new Color(0.1f, 0.12f, 0.1f), 0f, 0.1f);
                }
                return _backdrop;
            }
        }

        public static Material Crystal
        {
            get
            {
                if (_crystal == null)
                {
                    Texture2D tex = Resources.Load<Texture2D>("RTS/T_Crystal");
                    if (tex != null) _crystal = MakeTex("M_Crystal", tex, 0.2f, 0.75f);
                    else _crystal = Make("M_Crystal", new Color(0.24f, 0.62f, 0.85f), 0.2f, 0.7f);
                }
                return _crystal;
            }
        }

        public static Material Rock
        {
            get
            {
                Texture2D tex = Resources.Load<Texture2D>("RTS/T_Rock");
                if (tex != null) return MakeTex("M_Rock", tex, 0.1f, 0.22f);
                return Make("M_Rock", new Color(0.42f, 0.43f, 0.45f), 0.1f, 0.2f);
            }
        }

        private static Material MakeTex(string name, Texture2D tex, float metallic, float smooth)
        {
            Material m = Make(name, Color.white, metallic, smooth);
            if (m.HasProperty("_MainTex")) m.mainTexture = tex;
            return m;
        }

        public static Material Ghost(bool ok)
        {
            if (ok)
            {
                if (_ghostOk == null) _ghostOk = Make("M_GhostOk", new Color(0.2f, 0.9f, 0.35f, 0.45f), 0f, 0.1f);
                return _ghostOk;
            }
            if (_ghostBad == null) _ghostBad = Make("M_GhostBad", new Color(0.95f, 0.25f, 0.2f, 0.45f), 0f, 0.1f);
            return _ghostBad;
        }

        // ---------- 几何工具 ----------

        public static GameObject Part(Transform parent, PrimitiveType prim, string name, Vector3 localPos, Vector3 localScale, Material mat, Quaternion? rot = null)
        {
            GameObject go = GameObject.CreatePrimitive(prim);
            go.name = name;
            Collider c = go.GetComponent<Collider>();
            if (c != null) Object.DestroyImmediate(c);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            go.transform.localRotation = rot.HasValue ? rot.Value : Quaternion.identity;
            Renderer r = go.GetComponent<Renderer>();
            if (r != null && mat != null) r.sharedMaterial = mat;
            return go;
        }

        public static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            return Part(parent, PrimitiveType.Cube, name, pos, size, mat);
        }

        public static GameObject Cyl(Transform parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            return Part(parent, PrimitiveType.Cylinder, name, pos, size, mat);
        }

        // ---------- 单位造型 ----------

        public static void BuildUnitVisual(Transform root, UnitKind kind, Team team)
        {
            Material teamMat = TeamMat(team);
            Material metal = Metal;
            Material dark = Dark;

            if (kind == UnitKind.Engineer)
            {
                Box(root, "torso", new Vector3(0f, 0.62f, 0f), new Vector3(0.56f, 0.72f, 0.38f), teamMat);
                Box(root, "head", new Vector3(0f, 1.09f, 0f), new Vector3(0.36f, 0.34f, 0.34f), dark);
                Box(root, "pack", new Vector3(0f, 0.68f, -0.28f), new Vector3(0.46f, 0.46f, 0.18f), metal);
                Box(root, "tool", new Vector3(0.3f, 0.72f, 0.14f), new Vector3(0.06f, 0.5f, 0.06f), metal);
                Box(root, "legL", new Vector3(-0.16f, 0.2f, 0f), new Vector3(0.16f, 0.4f, 0.2f), dark);
                Box(root, "legR", new Vector3(0.16f, 0.2f, 0f), new Vector3(0.16f, 0.4f, 0.2f), dark);
            }
            else if (kind == UnitKind.Rifleman)
            {
                Box(root, "torso", new Vector3(0f, 0.66f, 0f), new Vector3(0.5f, 0.78f, 0.34f), teamMat);
                Box(root, "head", new Vector3(0f, 1.16f, 0f), new Vector3(0.34f, 0.32f, 0.32f), dark);
                Box(root, "gun", new Vector3(0.2f, 0.78f, 0.36f), new Vector3(0.07f, 0.07f, 0.62f), metal);
                Box(root, "legL", new Vector3(-0.15f, 0.21f, 0f), new Vector3(0.15f, 0.42f, 0.19f), dark);
                Box(root, "legR", new Vector3(0.15f, 0.21f, 0f), new Vector3(0.15f, 0.42f, 0.19f), dark);
            }
            else
            {
                Box(root, "hull", new Vector3(0f, 0.55f, 0f), new Vector3(2.5f, 0.62f, 1.55f), teamMat);
                Box(root, "trackL", new Vector3(-1.05f, 0.3f, 0f), new Vector3(0.42f, 0.5f, 1.7f), dark);
                Box(root, "trackR", new Vector3(1.05f, 0.3f, 0f), new Vector3(0.42f, 0.5f, 1.7f), dark);
                Box(root, "turret", new Vector3(0f, 1.06f, -0.05f), new Vector3(1.3f, 0.52f, 1.2f), teamMat);
                Box(root, "barrel", new Vector3(0f, 1.08f, 0.92f), new Vector3(0.16f, 0.16f, 1.5f), metal);
                Box(root, "hatch", new Vector3(-0.32f, 1.34f, -0.2f), new Vector3(0.34f, 0.1f, 0.34f), metal);
            }
        }

        // ---------- 建筑造型 ----------

        public static void BuildBuildingVisual(Transform root, BuildingKind kind, Team team)
        {
            Material teamMat = TeamMat(team);
            Material metal = Metal;
            Material dark = Dark;

            if (kind == BuildingKind.HQ)
            {
                Box(root, "base", new Vector3(0f, 0.5f, 0f), new Vector3(8f, 1f, 8f), dark);
                Box(root, "body", new Vector3(0f, 2.6f, 0f), new Vector3(6f, 3.2f, 6f), teamMat);
                Box(root, "trim", new Vector3(0f, 4.32f, 0f), new Vector3(6.4f, 0.24f, 6.4f), metal);
                Cyl(root, "mast", new Vector3(0f, 5.6f, 0f), new Vector3(0.3f, 1.3f, 0.3f), metal);
                Cyl(root, "radar", new Vector3(0f, 6.42f, 0f), new Vector3(1.6f, 0.09f, 1.6f), metal);
                Box(root, "door", new Vector3(0f, 1.35f, 3.05f), new Vector3(1.8f, 1.7f, 0.2f), dark);
                Box(root, "winL", new Vector3(-1.7f, 3.1f, 3.02f), new Vector3(1.2f, 0.7f, 0.1f), metal);
                Box(root, "winR", new Vector3(1.7f, 3.1f, 3.02f), new Vector3(1.2f, 0.7f, 0.1f), metal);
            }
            else if (kind == BuildingKind.Barracks)
            {
                Box(root, "base", new Vector3(0f, 0.35f, 0f), new Vector3(6f, 0.7f, 5f), dark);
                Box(root, "body", new Vector3(0f, 1.95f, 0f), new Vector3(5f, 2.5f, 4f), teamMat);
                Box(root, "roof", new Vector3(0f, 3.28f, 0f), new Vector3(5.4f, 0.24f, 4.4f), metal);
                Box(root, "ramp", new Vector3(0f, 0.28f, 2.55f), new Vector3(2.2f, 0.16f, 1.2f), metal);
                Box(root, "door", new Vector3(0f, 1.25f, 2.02f), new Vector3(2f, 1.6f, 0.14f), dark);
                Cyl(root, "barrelL", new Vector3(-1.9f, 0.55f, 1.7f), new Vector3(0.7f, 0.55f, 0.7f), metal);
                Cyl(root, "barrelR", new Vector3(-1.2f, 0.55f, 2.1f), new Vector3(0.7f, 0.55f, 0.7f), metal);
            }
            else
            {
                Cyl(root, "base", new Vector3(0f, 0.4f, 0f), new Vector3(2.6f, 0.4f, 2.6f), dark);
                Box(root, "post", new Vector3(0f, 1.3f, 0f), new Vector3(0.9f, 1.8f, 0.9f), metal);
                Box(root, "head", new Vector3(0f, 2.35f, 0f), new Vector3(1.9f, 0.9f, 1.9f), teamMat);
                Part(root, PrimitiveType.Cylinder, "barrel", new Vector3(0f, 2.42f, 1.05f),
                    new Vector3(0.26f, 1.0f, 0.26f), metal, Quaternion.Euler(90f, 0f, 0f));
                Box(root, "shield", new Vector3(0f, 2.35f, 0.98f), new Vector3(1.5f, 0.7f, 0.14f), dark);
            }
        }

        /// <summary>给整体挂一个与外观匹配的碰撞体（简化：一个 BoxCollider）。</summary>
        public static BoxCollider AddHitbox(GameObject go, Vector3 center, Vector3 size)
        {
            BoxCollider bc = go.GetComponent<BoxCollider>();
            if (bc == null) bc = go.AddComponent<BoxCollider>();
            bc.center = center;
            bc.size = size;
            return bc;
        }
    }
}

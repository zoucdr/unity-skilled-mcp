using UnityEngine;

namespace IronFront
{
    /// <summary>程序化地图：地面、边界、装饰、双方基地与矿脉。运行时生成，保证重开一致。</summary>
    public static class RtsMap
    {
        public const float Half = 40f;

        public static void Build(RtsGame g)
        {
            if (g == null) return;

            BuildGround(g);
            BuildBackdrop(g);
            BuildProps(g);

            RtsBuilding blue = BuildBase(g, Team.Blue, new Vector3(-26f, 0f, -26f));
            RtsBuilding red = BuildBase(g, Team.Red, new Vector3(26f, 0f, 26f));

            g.PlayerHQ = blue;
            g.EnemyHQ = red;
        }

        private static void BuildGround(RtsGame g)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(g.transform, false);
            ground.transform.localScale = new Vector3(Half * 2f / 10f, 1f, Half * 2f / 10f);
            Renderer r = ground.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = RtsArt.Ground;

            AddWall(g, new Vector3(0f, 1.5f, Half + 1f), new Vector3(Half * 2f + 4f, 3f, 2f));
            AddWall(g, new Vector3(0f, 1.5f, -Half - 1f), new Vector3(Half * 2f + 4f, 3f, 2f));
            AddWall(g, new Vector3(Half + 1f, 1.5f, 0f), new Vector3(2f, 3f, Half * 2f + 4f));
            AddWall(g, new Vector3(-Half - 1f, 1.5f, 0f), new Vector3(2f, 3f, Half * 2f + 4f));
        }

        /// <summary>地图外延展的大底盘：避免相机拉远/贴边时视野露出天空盒。</summary>
        private static void BuildBackdrop(RtsGame g)
        {
            GameObject bd = GameObject.CreatePrimitive(PrimitiveType.Plane);
            bd.name = "Backdrop";
            bd.transform.SetParent(g.transform, false);
            bd.transform.localPosition = new Vector3(0f, -0.55f, 0f);
            bd.transform.localScale = new Vector3(64f, 1f, 64f); // 640 x 640

            Collider c = bd.GetComponent<Collider>();
            if (c != null) Object.Destroy(c);

            Renderer r = bd.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = RtsArt.Backdrop;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.16f, 0.19f, 0.17f);
            RenderSettings.fogStartDistance = 95f;
            RenderSettings.fogEndDistance = 260f;
        }

        private static void AddWall(RtsGame g, Vector3 pos, Vector3 size)
        {
            GameObject wall = new GameObject("Boundary");
            wall.transform.SetParent(g.transform, false);
            wall.transform.localPosition = pos;
            BoxCollider bc = wall.AddComponent<BoxCollider>();
            bc.size = size;
        }

        private static void BuildProps(RtsGame g)
        {
            Random.InitState(20261111);

            GameObject root = new GameObject("Props");
            root.transform.SetParent(g.transform, false);

            Material rockMat = RtsArt.Rock;
            Material darkMat = RtsArt.Dark;

            int placed = 0;
            int guard = 0;

            while (placed < 40 && guard < 400)
            {
                guard++;
                float x = Random.Range(-36f, 36f);
                float z = Random.Range(-36f, 36f);

                if (NearBase(new Vector3(x, 0f, z), 13f)) continue;
                if (NearNode(g, new Vector3(x, 0f, z), 6f)) continue;

                GameObject rock = new GameObject("Rock_" + placed);
                rock.transform.SetParent(root.transform, false);
                rock.transform.localPosition = new Vector3(x, 0f, z);
                rock.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

                float sx = Random.Range(1.2f, 2.8f);
                float sy = Random.Range(0.8f, 2.0f);
                float sz = Random.Range(1.2f, 2.8f);

                GameObject main = RtsArt.Box(rock.transform, "main",
                    new Vector3(0f, sy * 0.5f, 0f), new Vector3(sx, sy, sz), rockMat);
                main.transform.localRotation = Quaternion.Euler(Random.Range(-8f, 8f), Random.Range(0f, 90f), Random.Range(-6f, 6f));

                if (Random.value > 0.45f)
                {
                    RtsArt.Box(rock.transform, "chip",
                        new Vector3(Random.Range(-0.9f, 0.9f), sy * 0.35f, Random.Range(-0.9f, 0.9f)),
                        new Vector3(sx * 0.45f, sy * 0.55f, sz * 0.45f), rockMat);
                }

                if (Random.value > 0.7f)
                {
                    RtsArt.Cyl(rock.transform, "stump",
                        new Vector3(Random.Range(-1.4f, 1.4f), 0.7f, Random.Range(-1.4f, 1.4f)),
                        new Vector3(0.35f, 0.7f, 0.35f), darkMat);
                }

                placed++;
            }
        }

        private static bool NearBase(Vector3 p, float dist)
        {
            if (RtsUtil.Flat(p, new Vector3(-26f, 0f, -26f)) < dist) return true;
            if (RtsUtil.Flat(p, new Vector3(26f, 0f, 26f)) < dist) return true;
            return false;
        }

        private static bool NearNode(RtsGame g, Vector3 p, float dist)
        {
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                if (g.Nodes[i] == null) continue;
                if (RtsUtil.Flat(p, g.Nodes[i].transform.position) < dist) return true;
            }
            return false;
        }

        private static RtsBuilding BuildBase(RtsGame g, Team team, Vector3 center)
        {
            Vector3 toCenter = new Vector3(-center.x, 0f, -center.z);
            if (toCenter.sqrMagnitude < 0.01f) toCenter = Vector3.forward;
            toCenter.Normalize();

            Quaternion rot = Quaternion.LookRotation(toCenter, Vector3.up);

            RtsBuilding hq = RtsPool.SpawnBuilding(BuildingKind.HQ, team, center, g.BuildingRoot, true);
            hq.transform.rotation = rot;
            hq.Rally = center + toCenter * 8f;

            Vector3 side = Quaternion.Euler(0f, 90f, 0f) * toCenter;

            Vector3 nodeA = center + toCenter * 8f + side * 4.5f;
            Vector3 nodeB = center + side * 11f + toCenter * 3f;

            RtsPool.SpawnNode(nodeA, 3000, g.NodeRoot);
            RtsPool.SpawnNode(nodeB, 2000, g.NodeRoot);

            for (int i = 0; i < 3; i++)
            {
                Vector3 p = center + toCenter * 6.5f + side * ((i - 1) * 1.8f);
                RtsUnit u = RtsPool.SpawnUnit(UnitKind.Engineer, team, p, g.UnitRoot);
                u.transform.rotation = rot;
                u.IssueHarvest(null);
            }

            return hq;
        }
    }
}

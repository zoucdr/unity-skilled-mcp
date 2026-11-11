using UnityEngine;

namespace IronFront
{
    /// <summary>统一的生成入口：负责挂组件、建外观、注册到全局。</summary>
    public static class RtsPool
    {
        public static RtsUnit SpawnUnit(UnitKind kind, Team team, Vector3 pos, Transform parent)
        {
            GameObject go = new GameObject("U_" + kind + "_" + team);
            if (parent != null) go.transform.SetParent(parent, false);

            RtsUnit u = go.AddComponent<RtsUnit>();
            u.Init(kind, team, pos);
            return u;
        }

        public static RtsBuilding SpawnBuilding(BuildingKind kind, Team team, Vector3 pos, Transform parent, bool finished)
        {
            GameObject go = new GameObject("B_" + kind + "_" + team);
            if (parent != null) go.transform.SetParent(parent, false);

            RtsBuilding b = go.AddComponent<RtsBuilding>();
            b.Init(kind, team, pos, finished);
            return b;
        }

        public static RtsResourceNode SpawnNode(Vector3 pos, int amount, Transform parent)
        {
            GameObject go = new GameObject("N_Crystal");
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(pos.x, 0f, pos.z);

            RtsResourceNode n = go.AddComponent<RtsResourceNode>();
            n.Init(amount);
            if (RtsGame.I != null) RtsGame.I.Register(n);

            BoxCollider bc = go.GetComponent<BoxCollider>();
            if (bc == null) bc = go.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, 0.7f, 0f);
            bc.size = new Vector3(2.6f, 1.6f, 2.6f);

            return n;
        }
    }
}

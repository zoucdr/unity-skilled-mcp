using System.Collections.Generic;
using UnityEngine;

namespace IronFront
{
    /// <summary>敌方 AI：采矿 → 建兵营 → 造兵 → 定期成波进攻 → 遇袭回防。</summary>
    public class RtsAI : MonoBehaviour
    {
        public float WaveInterval = 40f;

        private float _think;
        private float _wave;

        private void Update()
        {
            RtsGame g = RtsGame.I;
            if (g == null || g.IsOver || g.State == GameState.Paused) return;

            _wave += Time.deltaTime;
            _think -= Time.deltaTime;
            if (_think > 0f) return;
            _think = 0.5f;

            Think();
        }

        private void Think()
        {
            RtsGame g = RtsGame.I;
            if (g.EnemyHQ == null || !g.EnemyHQ.IsAlive) return;

            KeepMining();

            RtsBuilding barracks = FindBarracks();
            if (barracks == null) TryBuildBarracks();
            else if (!barracks.UnderConstruction) TryProduce(barracks);

            if (g.Elapsed > 12f && _wave >= WaveInterval)
            {
                if (LaunchAttack()) _wave = 0f;
            }

            DefendIfNeeded();
        }

        private void KeepMining()
        {
            RtsGame g = RtsGame.I;
            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u == null || !u.IsAlive || u.Team != Team.Red) continue;
                if (u.Kind != UnitKind.Engineer) continue;
                if (u.State == UnitState.Idle && u.Carrying == 0) u.IssueHarvest(null);
            }
        }

        private RtsBuilding FindBarracks()
        {
            RtsGame g = RtsGame.I;
            for (int i = 0; i < g.Buildings.Count; i++)
            {
                RtsBuilding b = g.Buildings[i];
                if (b != null && b.IsAlive && b.Team == Team.Red && b.Kind == BuildingKind.Barracks) return b;
            }
            return null;
        }

        private void TryBuildBarracks()
        {
            RtsGame g = RtsGame.I;
            BuildingDef d = RtsDefs.B(BuildingKind.Barracks);
            if (!g.CanAffordOf(Team.Red, d.Cost, 0)) return;

            RtsUnit eng = FindEngineer();
            if (eng == null) return;

            Vector3 pos = g.EnemyHQ.transform.position + new Vector3(-9f, 0f, -7f);
            if (!g.SpendOf(Team.Red, d.Cost, 0)) return;

            RtsBuilding b = RtsPool.SpawnBuilding(BuildingKind.Barracks, Team.Red, pos, g.BuildingRoot, false);
            eng.IssueBuild(b);
        }

        private void TryProduce(RtsBuilding barracks)
        {
            RtsGame g = RtsGame.I;
            if (barracks.Queue.Count >= 2) return;

            int army = CountArmy();
            UnitKind want = UnitKind.Rifleman;
            if (g.EnemyCrystal > 640 && army >= 3) want = UnitKind.Tank;

            UnitDef ud = RtsDefs.U(want);
            if (g.CanAffordOf(Team.Red, ud.Cost, ud.Supply)) barracks.Enqueue(want);
        }

        private int CountArmy()
        {
            RtsGame g = RtsGame.I;
            int n = 0;
            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u == null || !u.IsAlive || u.Team != Team.Red) continue;
                if (u.Kind != UnitKind.Engineer) n++;
            }
            return n;
        }

        private bool LaunchAttack()
        {
            RtsGame g = RtsGame.I;
            RtsBuilding target = g.PlayerHQ;
            if (target == null || !target.IsAlive) return false;

            List<RtsUnit> army = Army();
            if (army.Count < 2) return false;

            for (int i = 0; i < army.Count; i++) army[i].IssueAttack(target);
            return true;
        }

        private void DefendIfNeeded()
        {
            RtsGame g = RtsGame.I;
            if (g.EnemyHQ == null) return;

            Vector3 hq = g.EnemyHQ.transform.position;
            RtsUnit intruder = null;
            float best = 22f;

            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u == null || !u.IsAlive || u.Team != Team.Blue) continue;
                float d = RtsUtil.Flat(u.transform.position, hq);
                if (d < best) { best = d; intruder = u; }
            }

            if (intruder == null) return;

            List<RtsUnit> army = Army();
            for (int i = 0; i < army.Count; i++)
            {
                if (army[i].State == UnitState.Idle || army[i].State == UnitState.Move)
                    army[i].IssueAttack(intruder);
            }
        }

        private List<RtsUnit> Army()
        {
            RtsGame g = RtsGame.I;
            List<RtsUnit> list = new List<RtsUnit>();
            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u == null || !u.IsAlive || u.Team != Team.Red) continue;
                if (u.Kind == UnitKind.Engineer) continue;
                list.Add(u);
            }
            return list;
        }

        private RtsUnit FindEngineer()
        {
            RtsGame g = RtsGame.I;
            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u != null && u.IsAlive && u.Team == Team.Red && u.Kind == UnitKind.Engineer) return u;
            }
            return null;
        }
    }
}

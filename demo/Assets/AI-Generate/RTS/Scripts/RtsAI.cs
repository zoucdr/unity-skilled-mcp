using System.Collections.Generic;
using UnityEngine;

namespace IronFront
{
    /// <summary>敌方 AI：采矿 → 建兵营 → 造兵 → 定期成波进攻 → 遇袭回防。</summary>
    public class RtsAI : MonoBehaviour
    {
        public float WaveInterval = 90f;
        /// <summary>首波进攻的最早时间；低于它 AI 只造兵不出门，给玩家组织防守的窗口。</summary>
        public float FirstWaveAt = 70f;
        /// <summary>每次进攻投入的兵力比例，其余留守基地，避免被反打直接空门。</summary>
        public float CommitRatio = 0.6f;
        /// <summary>两次下单之间的最小间隔，模拟 AI 的决策延迟，避免瞬间攒满队列。</summary>
        public float ProduceCooldown = 6f;

        private float _think;
        private float _wave;
        private float _produceCd;

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

            if (g.Elapsed > FirstWaveAt && _wave >= WaveInterval)
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
            if (_produceCd > 0f) { _produceCd -= 0.5f; return; }

            int army = CountArmy();
            UnitKind want = UnitKind.Rifleman;
            if (g.EnemyCrystal > 640 && army >= 3) want = UnitKind.Tank;

            UnitDef ud = RtsDefs.U(want);
            if (g.CanAffordOf(Team.Red, ud.Cost, ud.Supply))
            {
                if (barracks.Enqueue(want)) _produceCd = ProduceCooldown;
            }
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

            // 只派前线的一部分，其余留守，避免一次梭哈被反打就空门。
            Vector3 tp = target.transform.position;
            army.Sort((a, b) => RtsUtil.Flat(a.transform.position, tp)
                .CompareTo(RtsUtil.Flat(b.transform.position, tp)));

            int send = Mathf.Max(2, Mathf.CeilToInt(army.Count * Mathf.Clamp01(CommitRatio)));
            if (send > army.Count) send = army.Count;

            for (int i = 0; i < send; i++) army[i].IssueAttack(target);
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

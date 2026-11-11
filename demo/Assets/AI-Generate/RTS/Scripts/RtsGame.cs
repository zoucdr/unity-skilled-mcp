using System;
using System.Collections.Generic;
using UnityEngine;

namespace IronFront
{
    /// <summary>全局状态：晶体、人口、单位注册表、胜负与暂停。</summary>
    public class RtsGame : MonoBehaviour
    {
        public static RtsGame I { get; private set; }

        public const int CrystalMax = 9999;
        public const int SupplyMax = 60;

        public int Crystal = 1200;
        public int EnemyCrystal = 1200;
        public int SupplyCap;
        public int SupplyUsed;
        public int EnemySupplyCap;
        public int EnemySupplyUsed;
        public int Kills;
        public int Losses;
        public float Elapsed;
        public GameState State = GameState.Running;

        public RtsBuilding PlayerHQ;
        public RtsBuilding EnemyHQ;

        public readonly List<RtsUnit> Units = new List<RtsUnit>();
        public readonly List<RtsBuilding> Buildings = new List<RtsBuilding>();
        public readonly List<RtsResourceNode> Nodes = new List<RtsResourceNode>();
        public readonly List<RtsSelectable> Selection = new List<RtsSelectable>();

        public Transform Root;
        public Transform UnitRoot;
        public Transform BuildingRoot;
        public Transform NodeRoot;

        public event Action EconomyChanged;
        public event Action SelectionChanged;
        public event Action<GameState> GameOver;

        private float _sepTimer;

        public bool IsOver
        {
            get { return State == GameState.Won || State == GameState.Lost; }
        }

        private void Awake()
        {
            if (I == null) I = this;
            if (Root == null) Root = transform;
        }

        private void OnEnable()
        {
            // 域重载（脚本热编译等）会清空静态字段但保留组件，此处自愈，避免全局单例失效。
            if (I == null) I = this;
        }

        private void OnDestroy()
        {
            if (I == this) I = null;
        }

        /// <summary>让第一个创建的实例胜出，避免 Awake 顺序不确定导致状态分离。</summary>
        public static void Bind(RtsGame g)
        {
            if (I == null) I = g;
        }

        private void Update()
        {
            if (I == null)
            {
                I = this;
                Debug.LogWarning("[IRON FRONT] RtsGame.I 为空，已自动重绑。");
            }

            if (State == GameState.Running) Elapsed += Time.deltaTime;

            _sepTimer += Time.deltaTime;
            if (_sepTimer >= 0.1f)
            {
                _sepTimer = 0f;
                TickSeparation();
            }
        }

        // ---------- 经济 ----------

        public void RaiseEconomy()
        {
            if (EconomyChanged != null) EconomyChanged();
        }

        public int CrystalOf(Team t)
        {
            return t == Team.Red ? EnemyCrystal : Crystal;
        }

        public void AddCrystalOf(Team t, int amount)
        {
            if (t == Team.Red) EnemyCrystal = Mathf.Clamp(EnemyCrystal + amount, 0, CrystalMax);
            else Crystal = Mathf.Clamp(Crystal + amount, 0, CrystalMax);
            RaiseEconomy();
        }

        public void AddCrystal(int amount)
        {
            AddCrystalOf(Team.Blue, amount);
        }

        public bool CanAffordOf(Team t, int cost, int supply)
        {
            int c = t == Team.Red ? EnemyCrystal : Crystal;
            int used = t == Team.Red ? EnemySupplyUsed : SupplyUsed;
            int cap = t == Team.Red ? EnemySupplyCap : SupplyCap;
            if (c < cost) return false;
            if (used + supply > cap) return false;
            return true;
        }

        public bool CanAfford(int cost, int supply)
        {
            return CanAffordOf(Team.Blue, cost, supply);
        }

        public bool SpendOf(Team t, int cost, int supply)
        {
            if (!CanAffordOf(t, cost, supply)) return false;
            if (t == Team.Red) { EnemyCrystal -= cost; EnemySupplyUsed += supply; }
            else { Crystal -= cost; SupplyUsed += supply; }
            RaiseEconomy();
            return true;
        }

        public bool Spend(int cost, int supply)
        {
            return SpendOf(Team.Blue, cost, supply);
        }

        public void RefundOf(Team t, int cost, int supply)
        {
            if (t == Team.Red)
            {
                EnemyCrystal = Mathf.Clamp(EnemyCrystal + cost, 0, CrystalMax);
                EnemySupplyUsed = Mathf.Max(0, EnemySupplyUsed - supply);
            }
            else
            {
                Crystal = Mathf.Clamp(Crystal + cost, 0, CrystalMax);
                SupplyUsed = Mathf.Max(0, SupplyUsed - supply);
            }
            RaiseEconomy();
        }

        public void Refund(int cost, int supply)
        {
            RefundOf(Team.Blue, cost, supply);
        }

        public void ReleaseSupplyOf(Team t, int supply)
        {
            if (t == Team.Red) EnemySupplyUsed = Mathf.Max(0, EnemySupplyUsed - supply);
            else SupplyUsed = Mathf.Max(0, SupplyUsed - supply);
            RaiseEconomy();
        }

        public void ReleaseSupply(int supply)
        {
            ReleaseSupplyOf(Team.Blue, supply);
        }

        public void AddSupplyCapOf(Team t, int amount)
        {
            if (t == Team.Red) EnemySupplyCap = Mathf.Clamp(EnemySupplyCap + amount, 0, SupplyMax);
            else SupplyCap = Mathf.Clamp(SupplyCap + amount, 0, SupplyMax);
            RaiseEconomy();
        }

        public void AddSupplyCap(int amount)
        {
            AddSupplyCapOf(Team.Blue, amount);
        }

        public void OccupySupply(Team t, int supply)
        {
            if (t == Team.Red) EnemySupplyUsed += supply;
            else SupplyUsed += supply;
            RaiseEconomy();
        }

        // ---------- 选择 ----------

        public void SetSelection(List<RtsSelectable> items)
        {
            for (int i = 0; i < Selection.Count; i++)
            {
                if (Selection[i] != null) Selection[i].SetSelected(false);
            }
            Selection.Clear();
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] == null) continue;
                    Selection.Add(items[i]);
                    items[i].SetSelected(true);
                }
            }
            if (SelectionChanged != null) SelectionChanged();
        }

        public void ClearSelection()
        {
            SetSelection(null);
        }

        public RtsUnit FirstSelectedUnit
        {
            get
            {
                for (int i = 0; i < Selection.Count; i++)
                {
                    RtsUnit u = Selection[i] as RtsUnit;
                    if (u != null) return u;
                }
                return null;
            }
        }

        // ---------- 注册 ----------

        public void Register(RtsUnit u)
        {
            if (!Units.Contains(u)) Units.Add(u);
        }

        public void Unregister(RtsUnit u)
        {
            Units.Remove(u);
            Selection.Remove(u);
            ReleaseSupplyOf(u.Team, u.Def != null ? u.Def.Supply : 1);
        }

        public void Register(RtsBuilding b)
        {
            if (!Buildings.Contains(b)) Buildings.Add(b);
        }

        public void Unregister(RtsBuilding b)
        {
            Buildings.Remove(b);
            Selection.Remove(b);
        }

        public void Register(RtsResourceNode n)
        {
            if (!Nodes.Contains(n)) Nodes.Add(n);
        }

        public void Unregister(RtsResourceNode n)
        {
            Nodes.Remove(n);
        }

        // ---------- 查询 ----------

        public static RtsSelectable FindNearestEnemy(Vector3 pos, float range, Team mine, bool includeBuildings)
        {
            RtsGame g = I;
            if (g == null) return null;

            RtsSelectable best = null;
            float bestD = range * range;

            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u == null || !u.IsAlive || u.Team == mine || u.Team == Team.Neutral) continue;
                float d = (u.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = u; }
            }

            if (includeBuildings)
            {
                for (int i = 0; i < g.Buildings.Count; i++)
                {
                    RtsBuilding b = g.Buildings[i];
                    if (b == null || !b.IsAlive || b.Team == mine || b.Team == Team.Neutral) continue;
                    float d = (b.transform.position - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = b; }
                }
            }

            return best;
        }

        public static RtsResourceNode FindNearestNode(Vector3 pos)
        {
            RtsGame g = I;
            if (g == null) return null;

            RtsResourceNode best = null;
            float bestD = float.MaxValue;

            for (int i = 0; i < g.Nodes.Count; i++)
            {
                RtsResourceNode n = g.Nodes[i];
                if (n == null || n.Depleted) continue;
                float d = (n.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = n; }
            }

            return best;
        }

        public static RtsBuilding FindNearestHQ(Vector3 pos, Team team)
        {
            RtsGame g = I;
            if (g == null) return null;

            RtsBuilding best = null;
            float bestD = float.MaxValue;

            for (int i = 0; i < g.Buildings.Count; i++)
            {
                RtsBuilding b = g.Buildings[i];
                if (b == null || !b.IsAlive || b.Team != team || b.Kind != BuildingKind.HQ) continue;
                float d = (b.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = b; }
            }

            return best;
        }

        public static List<RtsUnit> UnitsOf(Team team)
        {
            List<RtsUnit> list = new List<RtsUnit>();
            RtsGame g = I;
            if (g == null) return list;
            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u != null && u.IsAlive && u.Team == team) list.Add(u);
            }
            return list;
        }

        // ---------- 单位分离，避免完全重叠 ----------

        private void TickSeparation()
        {
            int n = Units.Count;
            for (int i = 0; i < n; i++)
            {
                RtsUnit a = Units[i];
                if (a == null || !a.IsAlive) continue;
                Vector3 push = Vector3.zero;

                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    RtsUnit b = Units[j];
                    if (b == null || !b.IsAlive) continue;

                    Vector3 d = a.transform.position - b.transform.position;
                    d.y = 0f;
                    float minDist = a.Def.Radius + b.Def.Radius + 0.35f;
                    float dist = d.magnitude;
                    if (dist < 0.0001f)
                    {
                        d = new Vector3(0.13f, 0f, 0.17f);
                        dist = d.magnitude;
                    }
                    if (dist < minDist)
                    {
                        push += d.normalized * ((minDist - dist) * 0.55f);
                    }
                }

                if (push.sqrMagnitude > 0.000001f)
                {
                    a.Nudge(push * 0.35f);
                }
            }
        }

        // ---------- 暂停与结算 ----------

        public void TogglePause()
        {
            if (IsOver) return;
            SetPaused(State != GameState.Paused);
        }

        public void SetPaused(bool paused)
        {
            if (IsOver) return;
            State = paused ? GameState.Paused : GameState.Running;
            Time.timeScale = paused ? 0f : 1f;
        }

        public void EndGame(GameState result)
        {
            if (IsOver) return;
            State = result;
            if (GameOver != null) GameOver(result);
        }

        public void RestartLevel()
        {
            Time.timeScale = 1f;
            if (Application.isEditor)
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            }
        }
    }
}

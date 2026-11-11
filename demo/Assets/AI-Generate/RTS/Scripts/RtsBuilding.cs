using System.Collections.Generic;
using UnityEngine;

namespace IronFront
{
    /// <summary>建筑：建造进度、生产队列、炮塔自动开火。</summary>
    public class RtsBuilding : RtsSelectable
    {
        public BuildingKind Kind;
        public BuildingDef Def;

        public bool UnderConstruction;
        public float BuildProgress;
        public RtsUnit Builder;

        public readonly List<UnitKind> Queue = new List<UnitKind>();
        public float ProduceTimer;
        public Vector3 Rally;

        private Transform _visual;
        private float _cd;
        private float _think;
        private float _flash;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _mpb;

        public override string DisplayName
        {
            get { return Def != null ? Def.Name : "BUILDING"; }
        }

        public override float BarHeight
        {
            get { return (Def != null ? Def.Height : 3f) + 0.9f; }
        }

        public override bool IsAlive
        {
            get { return Hp > 0f; }
        }

        public bool Producing
        {
            get { return Queue.Count > 0; }
        }

        public float ProduceRatio
        {
            get
            {
                if (Queue.Count == 0) return 0f;
                UnitDef ud = RtsDefs.U(Queue[0]);
                return Mathf.Clamp01(ProduceTimer / Mathf.Max(0.01f, ud.BuildTime));
            }
        }

        public void Init(BuildingKind kind, Team team, Vector3 pos, bool finished)
        {
            Kind = kind;
            Def = RtsDefs.B(kind);
            Team = team;
            MaxHp = Def.Hp;
            Hp = finished ? Def.Hp : Def.Hp * 0.1f;
            BuildProgress = finished ? 1f : 0f;
            UnderConstruction = !finished;
            Queue.Clear();
            ProduceTimer = 0f;
            _cd = 0f;
            _think = 0f;
            _flash = 0f;

            transform.position = new Vector3(pos.x, 0f, pos.z);

            EnsureVisual();
            EnsureHitbox();
            CacheRenderers();

            _visual.localScale = new Vector3(1f, finished ? 1f : 0.1f, 1f);
            Rally = transform.position + transform.forward * (Def.Radius + 3.2f);

            if (RtsGame.I != null)
            {
                RtsGame.I.Register(this);
                if (finished) RtsGame.I.AddSupplyCapOf(team, Def.Supply);
            }
        }

        private void EnsureVisual()
        {
            if (_visual != null) return;
            GameObject v = new GameObject("visual");
            v.transform.SetParent(transform, false);
            _visual = v.transform;
            RtsArt.BuildBuildingVisual(_visual, Kind, Team);
        }

        private void EnsureHitbox()
        {
            BoxCollider bc = gameObject.GetComponent<BoxCollider>();
            if (bc == null) bc = gameObject.AddComponent<BoxCollider>();
            float r = Def.Radius;
            float h = Def.Height;
            bc.center = new Vector3(0f, h * 0.5f, 0f);
            bc.size = new Vector3(r * 2f, h, r * 2f);
        }

        private void CacheRenderers()
        {
            _renderers = GetComponentsInChildren<Renderer>();
            _mpb = new MaterialPropertyBlock();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (_cd > 0f) _cd -= dt;
            if (_flash > 0f)
            {
                _flash -= dt;
                if (_flash <= 0f) ApplyFlash(false);
            }

            if (!IsAlive) return;
            if (UnderConstruction) return;

            TickProduction(dt);
            TickTurret(dt);
        }

        // ---------- 建造 ----------

        public void Work(float dt, RtsUnit builder)
        {
            if (!UnderConstruction || !IsAlive) return;
            if (Def.BuildTime <= 0f)
            {
                FinishConstruction();
                return;
            }

            Builder = builder;
            BuildProgress = Mathf.Clamp01(BuildProgress + dt / Def.BuildTime);
            Hp = Def.Hp * Mathf.Lerp(0.1f, 1f, BuildProgress);
            if (_visual != null) _visual.localScale = new Vector3(1f, Mathf.Lerp(0.1f, 1f, BuildProgress), 1f);

            if (BuildProgress >= 1f) FinishConstruction();
        }

        private void FinishConstruction()
        {
            UnderConstruction = false;
            BuildProgress = 1f;
            Hp = MaxHp;
            if (_visual != null) _visual.localScale = Vector3.one;

            if (RtsGame.I != null) RtsGame.I.AddSupplyCapOf(Team, Def.Supply);
            RtsFx.Ring(transform.position, new Color(0.4f, 1f, 0.6f), Def.Radius, 0.6f);
        }

        // ---------- 生产 ----------

        public bool Enqueue(UnitKind kind)
        {
            if (!IsAlive || UnderConstruction) return false;
            if (Queue.Count >= 5) return false;

            UnitDef ud = RtsDefs.U(kind);
            if (ud.Producer != Kind) return false;
            if (RtsGame.I == null) return false;

            // 人口在单位真正生成时由 RtsUnit.Init 占用，这里只做校验与扣钱，避免重复计数
            if (!RtsGame.I.CanAffordOf(Team, ud.Cost, ud.Supply)) return false;
            RtsGame.I.SpendOf(Team, ud.Cost, 0);

            Queue.Add(kind);
            return true;
        }

        public void CancelQueue()
        {
            if (Queue.Count == 0) return;
            UnitKind k = Queue[Queue.Count - 1];
            Queue.RemoveAt(Queue.Count - 1);
            UnitDef ud = RtsDefs.U(k);
            if (RtsGame.I != null) RtsGame.I.RefundOf(Team, ud.Cost, 0);
        }

        private void TickProduction(float dt)
        {
            if (Queue.Count == 0)
            {
                ProduceTimer = 0f;
                return;
            }

            UnitDef ud = RtsDefs.U(Queue[0]);
            ProduceTimer += dt;
            if (ProduceTimer < ud.BuildTime) return;

            ProduceTimer = 0f;
            UnitKind k = Queue[0];
            Queue.RemoveAt(0);
            SpawnProduced(k);
        }

        private void SpawnProduced(UnitKind kind)
        {
            Vector3 dir = Rally - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
            dir.Normalize();

            Vector3 pos = transform.position + dir * (Def.Radius + 1.6f);
            pos.y = 0f;

            RtsUnit u = RtsPool.SpawnUnit(kind, Team, pos, RtsGame.I != null ? RtsGame.I.UnitRoot : null);
            if (Vector3.Distance(Rally, transform.position) > 0.5f) u.IssueMove(Rally);
        }

        // ---------- 炮塔 ----------

        private void TickTurret(float dt)
        {
            if (Def.Damage <= 0f) return;

            _think -= dt;
            if (_think > 0f) return;
            _think = 0.3f;

            if (_cd > 0f) return;

            RtsSelectable e = RtsGame.FindNearestEnemy(transform.position, Def.Range, Team, true);
            if (e == null) return;

            _cd = Def.Cooldown;
            Vector3 to = e.transform.position + Vector3.up * 0.8f;
            Vector3 dir = e.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f) dir.Normalize();

            Vector3 from = transform.position + Vector3.up * (Def.Height * 0.7f) + dir * 1.2f;
            e.TakeDamage(Def.Damage * Random.Range(0.9f, 1.1f), this);
            RtsFx.Tracer(from, to, Team);
        }

        // ---------- 受击 ----------

        public override void TakeDamage(float amount, RtsSelectable source)
        {
            if (!IsAlive) return;

            Hp -= amount;
            _flash = 0.09f;
            ApplyFlash(true);

            if (Hp <= 0f) Die();
        }

        private void ApplyFlash(bool on)
        {
            if (_renderers == null) return;
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                if (r == null) continue;
                if (on)
                {
                    r.GetPropertyBlock(_mpb);
                    _mpb.SetColor("_Color", Color.white);
                    r.SetPropertyBlock(_mpb);
                }
                else
                {
                    r.SetPropertyBlock(null);
                }
            }
        }

        private void Die()
        {
            Hp = 0f;
            RtsFx.Explosion(transform.position, Def.Radius * 1.4f);

            if (UnderConstruction && Kind != BuildingKind.HQ && RtsGame.I != null)
            {
                RtsGame.I.RefundOf(Team, Mathf.RoundToInt(Def.Cost * 0.5f), 0);
            }

            if (Kind == BuildingKind.HQ && RtsGame.I != null)
            {
                RtsGame.I.EndGame(Team == Team.Red ? GameState.Won : GameState.Lost);
            }

            Queue.Clear();
            if (RtsGame.I != null) RtsGame.I.Unregister(this);
            Destroy(gameObject, 0.6f);
        }

        public override string InfoLine()
        {
            string s = DisplayName + "  HP " + Mathf.CeilToInt(Hp) + "/" + Mathf.CeilToInt(MaxHp);
            if (UnderConstruction) s += "  [BUILD " + Mathf.RoundToInt(BuildProgress * 100f) + "%]";
            else if (Queue.Count > 0) s += "  [QUEUE " + Queue.Count + "  " + Mathf.RoundToInt(ProduceRatio * 100f) + "%]";
            return s;
        }
    }
}

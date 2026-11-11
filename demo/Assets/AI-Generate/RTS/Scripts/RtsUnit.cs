using UnityEngine;

namespace IronFront
{
    /// <summary>单位：移动、自动索敌、开火、采集、建造。</summary>
    public class RtsUnit : RtsSelectable
    {
        public UnitKind Kind;
        public UnitDef Def;
        public UnitState State = UnitState.Idle;

        public RtsSelectable Target;
        public RtsResourceNode Node;
        public RtsBuilding BuildSite;
        public Vector3 MoveGoal;
        public bool HasMoveGoal;
        public int Carrying;
        public Vector3 Anchor;
        public bool AttackOrdered;

        private float _cd;
        private float _harvestTimer;
        private float _think;
        private float _flash;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _mpb;
        private Transform _visual;

        public override string DisplayName
        {
            get { return Def != null ? Def.Name : "UNIT"; }
        }

        public override float BarHeight
        {
            get { return (Def != null ? Def.Height : 1.3f) + 0.55f; }
        }

        public override bool IsAlive
        {
            get { return Hp > 0f && State != UnitState.Dead; }
        }

        public int CarryMax
        {
            get { return 24; }
        }

        public void Init(UnitKind kind, Team team, Vector3 pos)
        {
            Kind = kind;
            Def = RtsDefs.U(kind);
            Team = team;
            MaxHp = Def.Hp;
            Hp = Def.Hp;
            State = UnitState.Idle;
            Target = null;
            Node = null;
            BuildSite = null;
            Carrying = 0;
            HasMoveGoal = false;
            _cd = 0f;
            _harvestTimer = 0f;
            _think = Random.Range(0f, 0.25f);
            _flash = 0f;

            transform.position = new Vector3(pos.x, 0f, pos.z);
            transform.localScale = Vector3.one;
            Anchor = transform.position;

            EnsureVisual();
            EnsureHitbox();
            CacheRenderers();

            if (RtsGame.I != null)
            {
                RtsGame.I.Register(this);
                RtsGame.I.OccupySupply(team, Def.Supply);
            }
        }

        private void EnsureVisual()
        {
            if (_visual != null) return;
            GameObject v = new GameObject("visual");
            v.transform.SetParent(transform, false);
            _visual = v.transform;
            RtsArt.BuildUnitVisual(_visual, Kind, Team);
        }

        private void EnsureHitbox()
        {
            BoxCollider bc = gameObject.GetComponent<BoxCollider>();
            if (bc == null) bc = gameObject.AddComponent<BoxCollider>();
            float h = Def.Height;
            float r = Def.Radius;
            bc.center = new Vector3(0f, h * 0.5f, 0f);
            bc.size = new Vector3(r * 2.4f, h, r * 2.4f);
        }

        private void CacheRenderers()
        {
            _renderers = GetComponentsInChildren<Renderer>();
            _mpb = new MaterialPropertyBlock();
        }

        // ---------- 命令 ----------

        public void IssueMove(Vector3 pos)
        {
            if (!IsAlive) return;
            MoveGoal = new Vector3(pos.x, 0f, pos.z);
            HasMoveGoal = true;
            Target = null;
            AttackOrdered = false;
            BuildSite = null;
            State = UnitState.Move;
        }

        public void IssueAttack(RtsSelectable target)
        {
            IssueAttack(target, true);
        }

        /// <summary>ordered=true 表示玩家/AI 的明确攻击指令，不受牵引距离限制；自动索敌则受限。</summary>
        public void IssueAttack(RtsSelectable target, bool ordered)
        {
            if (!IsAlive || target == null) return;
            Target = target;
            State = UnitState.Attack;
            HasMoveGoal = false;
            AttackOrdered = ordered;
            Anchor = transform.position;
        }

        public void IssueHarvest(RtsResourceNode node)
        {
            if (!IsAlive) return;
            Node = node != null ? node : RtsGame.FindNearestNode(transform.position);
            Target = null;
            AttackOrdered = false;
            State = UnitState.ToNode;
        }

        public void IssueBuild(RtsBuilding site)
        {
            if (!IsAlive || site == null) return;
            BuildSite = site;
            Target = null;
            AttackOrdered = false;
            State = UnitState.Build;
        }

        public void Stop()
        {
            Target = null;
            HasMoveGoal = false;
            AttackOrdered = false;
            if (State != UnitState.Harvesting) State = UnitState.Idle;
        }

        public void Nudge(Vector3 delta)
        {
            if (!IsAlive) return;
            transform.position += delta;
            transform.position = new Vector3(transform.position.x, 0f, transform.position.z);
        }

        // ---------- 主循环 ----------

        private void Update()
        {
            if (!IsAlive) return;

            float dt = Time.deltaTime;
            if (_cd > 0f) _cd -= dt;
            if (_flash > 0f)
            {
                _flash -= dt;
                if (_flash <= 0f) ApplyFlash(false);
            }

            switch (State)
            {
                case UnitState.Idle:
                    DoIdle(dt);
                    break;
                case UnitState.Move:
                    DoMove(dt);
                    break;
                case UnitState.Attack:
                    DoAttack(dt);
                    break;
                case UnitState.ToNode:
                    DoToNode(dt);
                    break;
                case UnitState.Harvesting:
                    DoHarvest(dt);
                    break;
                case UnitState.ToBase:
                    DoToBase(dt);
                    break;
                case UnitState.Build:
                    DoBuild(dt);
                    break;
            }
        }

        private void DoIdle(float dt)
        {
            _think -= dt;
            if (_think > 0f) return;
            _think = 0.25f;

            RtsSelectable e = RtsGame.FindNearestEnemy(transform.position, Def.Sight, Team, true);
            if (e != null) IssueAttack(e, false);
        }

        private void DoMove(float dt)
        {
            if (!HasMoveGoal) { State = UnitState.Idle; return; }
            if (MoveTo(MoveGoal, dt))
            {
                HasMoveGoal = false;
                Anchor = transform.position;
                State = UnitState.Idle;
            }
        }

        private void DoAttack(float dt)
        {
            if (Target == null || !Target.IsAlive)
            {
                Target = null;
                AttackOrdered = false;
                State = UnitState.Idle;
                return;
            }

            Vector3 tp = Target.transform.position;
            float dist = Flat(transform.position, tp);

            if (dist > Def.Range)
            {
                // 只有自动索敌才受牵引距离限制；明确指令必须能打到远距离目标。
                if (!AttackOrdered && Flat(transform.position, Anchor) > 26f)
                {
                    AttackOrdered = false;
                    State = UnitState.Idle;
                    return;
                }
                MoveTo(tp, dt);
                return;
            }

            Face(tp, dt);
            TryFire();
        }

        private void DoToNode(float dt)
        {
            if (Node == null || Node.Depleted)
            {
                Node = RtsGame.FindNearestNode(transform.position);
                if (Node == null) { State = UnitState.Idle; return; }
            }

            if (MoveTo(Node.transform.position, dt, Node.Radius + 0.6f))
            {
                State = UnitState.Harvesting;
                _harvestTimer = 0f;
            }
        }

        private void DoHarvest(float dt)
        {
            if (Node == null || Node.Depleted)
            {
                GoDeliver();
                return;
            }

            Face(Node.transform.position, dt);
            _harvestTimer += dt;

            if (_harvestTimer >= 1.2f)
            {
                _harvestTimer -= 1.2f;
                int got = Node.Extract(8);
                Carrying += got;
                if (Carrying >= CarryMax || Node.Depleted) GoDeliver();
            }
        }

        private void GoDeliver()
        {
            if (Carrying <= 0) Carrying = 0;
            State = UnitState.ToBase;
        }

        private void DoToBase(float dt)
        {
            RtsBuilding hq = RtsGame.FindNearestHQ(transform.position, Team);
            if (hq == null)
            {
                State = UnitState.Idle;
                return;
            }

            float arrive = hq.Def.Radius + 1.4f;
            if (MoveTo(hq.transform.position, dt, arrive))
            {
                if (Carrying > 0 && RtsGame.I != null)
                {
                    RtsGame.I.AddCrystalOf(Team, Carrying);
                    Carrying = 0;
                }
                if (Node != null && !Node.Depleted) State = UnitState.ToNode;
                else
                {
                    Node = RtsGame.FindNearestNode(transform.position);
                    State = Node != null ? UnitState.ToNode : UnitState.Idle;
                }
            }
        }

        private void DoBuild(float dt)
        {
            if (BuildSite == null || !BuildSite.IsAlive)
            {
                BuildSite = null;
                State = UnitState.Idle;
                return;
            }

            float arrive = BuildSite.Def.Radius + 2.2f;
            if (MoveTo(BuildSite.transform.position, dt, arrive))
            {
                Face(BuildSite.transform.position, dt);
                BuildSite.Work(dt, this);
            }
        }

        // ---------- 辅助 ----------

        private bool MoveTo(Vector3 goal, float dt)
        {
            return MoveTo(goal, dt, 0.55f);
        }

        private bool MoveTo(Vector3 goal, float dt, float arriveDist)
        {
            Vector3 p = transform.position;
            Vector3 d = new Vector3(goal.x - p.x, 0f, goal.z - p.z);
            float dist = d.magnitude;

            if (dist <= arriveDist) return true;

            Vector3 dir = d / dist;
            float step = Def.Speed * dt;
            if (step > dist - arriveDist) step = dist - arriveDist;

            p += dir * step;
            p.y = 0f;
            transform.position = p;

            Quaternion look = Quaternion.LookRotation(dir, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, Mathf.Clamp01(dt * 9f));
            return false;
        }

        private void Face(Vector3 target, float dt)
        {
            Vector3 d = new Vector3(target.x - transform.position.x, 0f, target.z - transform.position.z);
            if (d.sqrMagnitude < 0.0001f) return;
            Quaternion look = Quaternion.LookRotation(d.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, Mathf.Clamp01(dt * 10f));
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private void TryFire()
        {
            if (_cd > 0f || Target == null) return;
            _cd = Def.Cooldown;

            float dmg = Def.Damage * Random.Range(0.9f, 1.1f);
            Vector3 from = transform.position + Vector3.up * (Def.Height * 0.72f) + transform.forward * 0.35f;
            Vector3 to = Target.transform.position + Vector3.up * (Target.BarHeight * 0.55f);

            Target.TakeDamage(dmg, this);
            RtsFx.Tracer(from, to, Team);
        }

        // ---------- 受击与死亡 ----------

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
            State = UnitState.Dead;
            Hp = 0f;
            RtsFx.Explosion(transform.position, Def.Radius * 2.2f);

            if (RtsGame.I != null)
            {
                if (Team == Team.Red) RtsGame.I.Kills++;
                else RtsGame.I.Losses++;
                RtsGame.I.Unregister(this);
            }

            Destroy(gameObject, 0.25f);
        }

        public override string InfoLine()
        {
            string extra = "";
            if (Kind == UnitKind.Engineer)
            {
                if (State == UnitState.Harvesting || State == UnitState.ToNode || State == UnitState.ToBase)
                    extra = "  [" + (Carrying > 0 ? "CARRY " + Carrying : "HARVESTING") + "]";
                else if (State == UnitState.Build) extra = "  [BUILDING]";
            }
            return DisplayName + "  HP " + Mathf.CeilToInt(Hp) + "/" + Mathf.CeilToInt(MaxHp) + extra;
        }
    }
}

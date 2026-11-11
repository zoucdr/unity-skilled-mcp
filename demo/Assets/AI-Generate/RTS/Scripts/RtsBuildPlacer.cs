using System.Collections.Generic;
using UnityEngine;

namespace IronFront
{
    /// <summary>建造放置：半透明预览 + 合法性检测 + 确认后派工兵施工。</summary>
    public class RtsBuildPlacer : MonoBehaviour
    {
        public bool Active;
        public BuildingKind Kind;

        private GameObject _ghost;
        private Renderer _ghostR;
        private bool _valid;
        private Vector3 _pos;

        public bool IsValid { get { return _valid; } }
        public Vector3 Position { get { return _pos; } }

        public void Begin(BuildingKind kind)
        {
            if (RtsGame.I == null || RtsGame.I.IsOver) return;

            BuildingDef d = RtsDefs.B(kind);
            if (!RtsGame.I.CanAfford(d.Cost, 0))
            {
                RtsHUD.Toast("NOT ENOUGH CRYSTAL");
                return;
            }

            Cancel();
            Active = true;
            Kind = kind;

            _ghost = new GameObject("ghost_" + kind);
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Collider bc = box.GetComponent<Collider>();
            if (bc != null) Object.Destroy(bc);
            box.transform.SetParent(_ghost.transform, false);
            box.transform.localPosition = new Vector3(0f, d.Height * 0.5f, 0f);
            box.transform.localScale = new Vector3(d.Radius * 2f, d.Height, d.Radius * 2f);
            _ghostR = box.GetComponent<Renderer>();
            if (_ghostR != null) _ghostR.sharedMaterial = RtsArt.Ghost(true);

            RtsHUD.Toast("LEFT CLICK TO PLACE / RIGHT CLICK TO CANCEL");
        }

        public void Cancel()
        {
            Active = false;
            if (_ghost != null) Object.Destroy(_ghost);
            _ghost = null;
            _ghostR = null;
        }

        private void Update()
        {
            if (!Active) return;

            Camera cam = Camera.main;
            Vector3 ground;
            if (cam == null || !RtsUtil.ScreenToGround(cam, Input.mousePosition, out ground)) return;

            _pos = ground;
            _valid = CanPlace(ground);

            if (_ghost != null) _ghost.transform.position = ground;
            if (_ghostR != null) _ghostR.sharedMaterial = RtsArt.Ghost(_valid);
        }

        /// <summary>落点是否合法（不越界、不压建筑与矿点）。建造与 AI 选址共用。</summary>
        public bool CanPlace(Vector3 p)
        {
            return CanPlace(Kind, p);
        }

        /// <summary>按指定建筑类型判断落点是否合法，便于 AI 与测试在不进入建造模式时选址。</summary>
        public bool CanPlace(BuildingKind kind, Vector3 p)
        {
            BuildingDef d = RtsDefs.B(kind);
            if (Mathf.Abs(p.x) > 38f || Mathf.Abs(p.z) > 38f) return false;

            RtsGame g = RtsGame.I;
            if (g == null) return false;

            for (int i = 0; i < g.Buildings.Count; i++)
            {
                RtsBuilding b = g.Buildings[i];
                if (b == null || !b.IsAlive) continue;
                if (RtsUtil.Flat(p, b.transform.position) < d.Radius + b.Def.Radius + 1.5f) return false;
            }

            for (int i = 0; i < g.Nodes.Count; i++)
            {
                RtsResourceNode n = g.Nodes[i];
                if (n == null || n.Depleted) continue;
                if (RtsUtil.Flat(p, n.transform.position) < d.Radius + n.Radius + 2f) return false;
            }

            return true;
        }

        public void Confirm()
        {
            ConfirmInternal(false);
        }

        /// <summary>
        /// 确认放置。fresh=true 时用当前鼠标位置重新校验，避免 Update 与鼠标事件帧序不一致
        /// 导致「点到的位置」和「预览的位置」错开一帧（快速点击时尤其明显）。
        /// </summary>
        private void ConfirmInternal(bool useCachedPosition)
        {
            if (!Active) return;

            if (!useCachedPosition)
            {
                Camera cam = Camera.main;
                Vector3 ground;
                if (cam != null && RtsUtil.ScreenToGround(cam, Input.mousePosition, out ground))
                {
                    _pos = ground;
                    _valid = CanPlace(ground);
                }
            }

            if (!_valid)
            {
                RtsHUD.Toast("INVALID POSITION");
                return;
            }

            BuildingDef d = RtsDefs.B(Kind);
            if (RtsGame.I == null || !RtsGame.I.Spend(d.Cost, 0))
            {
                RtsHUD.Toast("NOT ENOUGH CRYSTAL");
                return;
            }

            RtsBuilding b = RtsPool.SpawnBuilding(Kind, Team.Blue, _pos,
                RtsGame.I.BuildingRoot, false);

            RtsUnit engineer = null;
            List<RtsUnit> units = RtsInput.SelectedUnits();
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].Kind == UnitKind.Engineer) { engineer = units[i]; break; }
            }
            if (engineer == null) engineer = FindNearestEngineer(_pos);

            if (engineer == null)
            {
                RtsGame.I.Refund(d.Cost, 0);
                if (b != null) Object.Destroy(b.gameObject);
                RtsHUD.Toast("NO ENGINEER AVAILABLE");
                Cancel();
                return;
            }

            engineer.IssueBuild(b);
            RtsHUD.Toast(d.Name + " UNDER CONSTRUCTION");
            Cancel();
        }

        /// <summary>以指定落点直接确认建造（脱离鼠标的等价入口，供测试与 AI 复用）。</summary>
        public bool ConfirmAt(Vector3 pos)
        {
            if (!Active) return false;
            _pos = pos;
            _valid = CanPlace(pos);
            if (!_valid) return false;
            ConfirmInternal(true);
            return true;
        }

        private static RtsUnit FindNearestEngineer(Vector3 pos)
        {
            RtsGame g = RtsGame.I;
            if (g == null) return null;

            RtsUnit best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u == null || !u.IsAlive || u.Team != Team.Blue) continue;
                if (u.Kind != UnitKind.Engineer) continue;
                float dd = RtsUtil.Flat(pos, u.transform.position);
                if (dd < bestD) { bestD = dd; best = u; }
            }
            return best;
        }
    }
}

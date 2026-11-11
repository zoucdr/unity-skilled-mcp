using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace IronFront
{
    /// <summary>鼠标交互：点选、框选、右键命令、建造放置确认。</summary>
    public class RtsInput : MonoBehaviour
    {
        public Camera Cam;
        public RtsBuildPlacer Placer;

        public Rect DragRect;
        public bool IsBoxSelecting;

        private Vector3 _dragStart;
        private bool _mouseDown;
        private bool _dragMoved;

        private void Awake()
        {
            if (Cam == null) Cam = Camera.main;
        }

        private void Update()
        {
            if (Cam == null) Cam = Camera.main;
            if (Cam == null || RtsGame.I == null) return;
            if (RtsGame.I.IsOver) return;
            if (RtsGame.I.State == GameState.Paused) return;

            if (Placer != null && Placer.Active)
            {
                HandlePlacement();
                return;
            }

            HandleLeftMouse();
            HandleRightMouse();
        }

        // ---------- 建造放置 ----------

        private void HandlePlacement()
        {
            if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))
            {
                Placer.Cancel();
                return;
            }
            if (Input.GetMouseButtonDown(0))
            {
                Placer.Confirm();
            }
        }

        // ---------- 左键：点选与框选 ----------

        private static bool PointerOverUI()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void HandleLeftMouse()
        {
            if (Input.GetMouseButtonDown(0) && PointerOverUI()) return;

            if (Input.GetMouseButtonDown(0))
            {
                _mouseDown = true;
                _dragMoved = false;
                _dragStart = Input.mousePosition;
                DragRect = new Rect(_dragStart.x, _dragStart.y, 0f, 0f);
                IsBoxSelecting = false;
            }

            if (_mouseDown && Input.GetMouseButton(0))
            {
                Vector3 cur = Input.mousePosition;
                if (!_dragMoved && (cur - _dragStart).magnitude > 8f)
                {
                    _dragMoved = true;
                    IsBoxSelecting = true;
                }
                if (_dragMoved)
                {
                    float x = Mathf.Min(_dragStart.x, cur.x);
                    float y = Mathf.Min(_dragStart.y, cur.y);
                    float w = Mathf.Abs(cur.x - _dragStart.x);
                    float h = Mathf.Abs(cur.y - _dragStart.y);
                    DragRect = new Rect(x, y, w, h);
                }
            }

            if (_mouseDown && Input.GetMouseButtonUp(0))
            {
                _mouseDown = false;
                IsBoxSelecting = false;

                if (_dragMoved) DoBoxSelect(DragRect);
                else DoClickSelect(Input.mousePosition);

                DragRect = new Rect(0f, 0f, 0f, 0f);
            }
        }

        private void DoClickSelect(Vector3 screenPos)
        {
            RtsSelectable hit = Pick(screenPos);

            if (hit != null)
            {
                bool additive = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                List<RtsSelectable> list = new List<RtsSelectable>();
                if (additive)
                {
                    list.AddRange(RtsGame.I.Selection);
                    if (list.Contains(hit)) list.Remove(hit);
                    else list.Add(hit);
                }
                else
                {
                    list.Add(hit);
                }
                RtsGame.I.SetSelection(list);
                return;
            }

            if (!Input.GetKey(KeyCode.LeftShift)) RtsGame.I.ClearSelection();
        }

        private void DoBoxSelect(Rect rect)
        {
            List<RtsSelectable> list = new List<RtsSelectable>();
            if (Input.GetKey(KeyCode.LeftShift)) list.AddRange(RtsGame.I.Selection);

            for (int i = 0; i < RtsGame.I.Units.Count; i++)
            {
                RtsUnit u = RtsGame.I.Units[i];
                if (u == null || !u.IsAlive || u.Team != Team.Blue) continue;
                Vector3 sp = Cam.WorldToScreenPoint(u.transform.position + Vector3.up * 0.6f);
                if (sp.z < 0f) continue;
                if (rect.Contains(new Vector2(sp.x, sp.y)) && !list.Contains(u)) list.Add(u);
            }

            RtsGame.I.SetSelection(list);
        }

        private RtsSelectable Pick(Vector3 screenPos)
        {
            Ray ray = Cam.ScreenPointToRay(screenPos);
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, 500f)) return null;
            return hit.collider.GetComponentInParent<RtsSelectable>();
        }

        // ---------- 右键：命令分派 ----------

        private void HandleRightMouse()
        {
            if (!Input.GetMouseButtonDown(1)) return;
            if (PointerOverUI()) return;
            if (RtsGame.I.Selection.Count == 0) return;

            Ray ray = Cam.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            bool hasHit = Physics.Raycast(ray, out hit, 500f);

            if (hasHit)
            {
                RtsSelectable sel = hit.collider.GetComponentInParent<RtsSelectable>();
                if (sel != null && sel.Team != Team.Blue && sel.IsAlive)
                {
                    IssueAttack(sel);
                    RtsFx.Ring(sel.transform.position, new Color(1f, 0.35f, 0.3f), 1.6f, 0.45f);
                    return;
                }

                RtsResourceNode node = hit.collider.GetComponentInParent<RtsResourceNode>();
                if (node != null)
                {
                    IssueHarvest(node);
                    RtsFx.Ring(node.transform.position, new Color(0.4f, 0.85f, 1f), 2f, 0.45f);
                    return;
                }

                RtsBuilding b = sel as RtsBuilding;
                if (b != null && b.Team == Team.Blue)
                {
                    b.Rally = new Vector3(hit.point.x, 0f, hit.point.z);
                    RtsFx.Ring(b.Rally, new Color(0.9f, 0.9f, 0.4f), 1.2f, 0.4f);
                    return;
                }
            }

            Vector3 ground;
            if (RtsUtil.ScreenToGround(Cam, Input.mousePosition, out ground))
            {
                IssueMove(ground);
                RtsFx.Ring(ground, new Color(0.4f, 1f, 0.55f), 1.2f, 0.4f);
            }
        }

        private void IssueMove(Vector3 ground)
        {
            List<RtsUnit> units = SelectedUnits();
            if (units.Count == 0) return;

            List<Vector3> pts = RtsUtil.Spread(ground, units.Count, 1.6f);
            for (int i = 0; i < units.Count; i++)
            {
                units[i].IssueMove(pts[i]);
            }
        }

        private void IssueAttack(RtsSelectable target)
        {
            List<RtsUnit> units = SelectedUnits();
            for (int i = 0; i < units.Count; i++) units[i].IssueAttack(target);
        }

        private void IssueHarvest(RtsResourceNode node)
        {
            List<RtsUnit> units = SelectedUnits();
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].Kind == UnitKind.Engineer) units[i].IssueHarvest(node);
                else units[i].IssueMove(node.transform.position);
            }
        }

        public static List<RtsUnit> SelectedUnits()
        {
            List<RtsUnit> list = new List<RtsUnit>();
            if (RtsGame.I == null) return list;
            for (int i = 0; i < RtsGame.I.Selection.Count; i++)
            {
                RtsUnit u = RtsGame.I.Selection[i] as RtsUnit;
                if (u != null && u.IsAlive) list.Add(u);
            }
            return list;
        }
    }
}

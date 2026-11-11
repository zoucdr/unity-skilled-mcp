using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace IronFront
{
    /// <summary>测试与试玩辅助接口：给资源、生成单位、强制摧毁、导出状态。</summary>
    public class RtsCheats : MonoBehaviour
    {
        public static RtsCheats I;

        private void Awake()
        {
            if (I == null) I = this;
        }

        private void OnEnable()
        {
            if (I == null) I = this;
        }

        private void OnDestroy()
        {
            if (I == this) I = null;
        }

        public void GiveCrystal(int amount)
        {
            if (RtsGame.I != null) RtsGame.I.AddCrystal(amount);
        }

        public void GiveSupply(int amount)
        {
            if (RtsGame.I != null) RtsGame.I.AddSupplyCap(amount);
        }

        public void SpawnUnit(int kind, int team, float x, float z)
        {
            if (RtsGame.I == null) return;
            RtsPool.SpawnUnit((UnitKind)kind, (Team)team, new Vector3(x, 0f, z), RtsGame.I.UnitRoot);
        }

        public void SpawnBuilding(int kind, int team, float x, float z, bool finished)
        {
            if (RtsGame.I == null) return;
            RtsPool.SpawnBuilding((BuildingKind)kind, (Team)team, new Vector3(x, 0f, z),
                RtsGame.I.BuildingRoot, finished);
        }

        public void DamageHQ(int team, float amount)
        {
            if (RtsGame.I == null) return;
            RtsBuilding hq = team == 2 ? RtsGame.I.EnemyHQ : RtsGame.I.PlayerHQ;
            if (hq != null) hq.TakeDamage(amount, null);
        }

        public void KillHQ(int team)
        {
            DamageHQ(team, 99999f);
        }

        public void KillAll(int team)
        {
            RtsGame g = RtsGame.I;
            if (g == null) return;

            for (int i = g.Units.Count - 1; i >= 0; i--)
            {
                RtsUnit u = g.Units[i];
                if (u != null && u.Team == (Team)team) u.TakeDamage(99999f, null);
            }
        }

        public void SelectAllBlue()
        {
            RtsGame g = RtsGame.I;
            if (g == null) return;

            System.Collections.Generic.List<RtsSelectable> list =
                new System.Collections.Generic.List<RtsSelectable>();

            for (int i = 0; i < g.Units.Count; i++)
            {
                if (g.Units[i] != null && g.Units[i].Team == Team.Blue) list.Add(g.Units[i]);
            }
            g.SetSelection(list);
        }

        /// <summary>选中玩家总部（等价于点选 HQ）。</summary>
        public string SelectHQ()
        {
            RtsGame g = RtsGame.I;
            if (g == null || g.PlayerHQ == null) return "{\"error\":\"no_hq\"}";
            System.Collections.Generic.List<RtsSelectable> list =
                new System.Collections.Generic.List<RtsSelectable>();
            list.Add(g.PlayerHQ);
            g.SetSelection(list);
            return "{\"selected\":" + g.Selection.Count + "}";
        }

        /// <summary>触发命令面板按钮回调（与 UGUI Button.onClick 同一入口）。</summary>
        public string ClickCommand(int idx)
        {
            if (RtsHUD.I == null) return "{\"error\":\"no_hud\"}";
            RtsGame g = RtsGame.I;
            int before = g != null ? g.Crystal : -1;
            RtsHUD.I.CommandFromButton(idx);
            return "{\"command\":" + idx
                + ",\"crystalBefore\":" + before
                + ",\"crystalAfter\":" + (g != null ? g.Crystal : -1) + "}";
        }

        /// <summary>走真实建造流程落一座建筑（Begin → 校验 → Confirm），返回诊断串。</summary>
        public string BuildAt(int kind, float x, float z)
        {
            RtsBuildPlacer placer = FindObjectOfType<RtsBuildPlacer>();
            if (placer == null) return "{\"error\":\"no_placer\"}";
            if (RtsGame.I == null) return "{\"error\":\"no_game\"}";

            placer.Begin((BuildingKind)kind);
            if (!placer.Active) return "{\"ok\":false,\"reason\":\"begin_rejected\"}";

            int before = RtsGame.I.Crystal;
            bool ok = placer.ConfirmAt(new Vector3(x, 0f, z));
            placer.Cancel();

            return "{\"ok\":" + (ok ? "true" : "false")
                + ",\"valid\":" + (placer.IsValid ? "true" : "false")
                + ",\"crystalBefore\":" + before
                + ",\"crystalAfter\":" + RtsGame.I.Crystal
                + ",\"buildings\":" + RtsGame.I.Buildings.Count + "}";
        }

        /// <summary>在指定点生成单位并回报数量，供战斗用例布置。</summary>
        public string SpawnAt(int kind, int team, float x, float z)
        {
            if (RtsGame.I == null) return "{\"error\":\"no_game\"}";
            RtsUnit u = RtsPool.SpawnUnit((UnitKind)kind, (Team)team,
                new Vector3(x, 0f, z), RtsGame.I.UnitRoot);
            return "{\"spawned\":" + (u != null ? "true" : "false")
                + ",\"units\":" + RtsGame.I.Units.Count + "}";
        }

        /// <summary>关键引用与状态探针，用于自动化断言。</summary>
        /// <summary>输入链路探针：读出 Unity 侧真实 Input 状态，用于验证外部注入是否生效。</summary>
        public string InputProbe()
        {
            RtsGame g = RtsGame.I;
            Vector3 mp = Input.mousePosition;
            string s = "{";
            s += "\"mouse\":[" + mp.x.ToString("F1") + "," + mp.y.ToString("F1") + "]";
            s += ",\"screen\":[" + Screen.width + "," + Screen.height + "]";
            s += ",\"lmb\":" + (Input.GetMouseButton(0) ? "true" : "false");
            s += ",\"rmb\":" + (Input.GetMouseButton(1) ? "true" : "false");
            s += ",\"focused\":" + (Application.isFocused ? "true" : "false");
            s += ",\"keys\":\"";
            if (Input.GetKey(KeyCode.W)) s += "W";
            if (Input.GetKey(KeyCode.A)) s += "A";
            if (Input.GetKey(KeyCode.S)) s += "S";
            if (Input.GetKey(KeyCode.D)) s += "D";
            if (Input.GetKey(KeyCode.Space)) s += "SP";
            s += "\"";
            s += ",\"scroll\":" + Input.mouseScrollDelta.y.ToString("F1");

            UnityEngine.EventSystems.EventSystem es = UnityEngine.EventSystems.EventSystem.current;
            s += ",\"evsys\":" + (es != null ? "true" : "false");
            s += ",\"overUI\":" + (es != null && es.IsPointerOverGameObject() ? "true" : "false");
            if (es != null)
            {
                var pd = new UnityEngine.EventSystems.PointerEventData(es);
                pd.position = mp;
                var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(pd, hits);
                s += ",\"uiHit\":\"" + (hits.Count > 0 && hits[0].gameObject != null ? hits[0].gameObject.name : "") + "\"";
                s += ",\"selected\":" + (es.currentSelectedGameObject != null ? "\"" + es.currentSelectedGameObject.name + "\"" : "null");
            }
            s += ",\"sel\":" + (g != null ? g.Selection.Count : -1);
            s += "}";
            return s;
        }

        /// <summary>
        /// 把 HUD 按钮、单位、资源点、双方总部的屏幕坐标导出为 GameView 左上原点像素，
        /// 供外部真实输入注入（SendInput）精确定位，避免靠猜坐标点空。
        /// </summary>
        public string UiPoints()
        {
            RtsGame g = RtsGame.I;
            Camera cam = Camera.main;
            float h = Screen.height;
            StringBuilder sb = new StringBuilder(2048);

            sb.Append("{\"screen\":[").Append(Screen.width).Append(",").Append(Screen.height).Append("]");

            sb.Append(",\"buttons\":[");
            RtsHUD hud = RtsHUD.I != null ? RtsHUD.I : FindObjectOfType<RtsHUD>();
            bool first = true;
            if (hud != null && hud.CmdButtons != null)
            {
                Vector3[] c = new Vector3[4];
                for (int i = 0; i < hud.CmdButtons.Length; i++)
                {
                    Button btn = hud.CmdButtons[i];
                    if (!first) sb.Append(",");
                    first = false;
                    if (btn == null) { sb.Append("null"); continue; }

                    RectTransform rt = btn.transform as RectTransform;
                    rt.GetWorldCorners(c);
                    float cx = (c[0].x + c[2].x) * 0.5f;
                    float cy = (c[0].y + c[2].y) * 0.5f;
                    sb.Append("{\"idx\":").Append(i)
                      .Append(",\"name\":\"").Append(btn.name).Append("\"")
                      .Append(",\"center\":[").Append(cx.ToString("F0")).Append(",").Append((h - cy).ToString("F0")).Append("]")
                      .Append(",\"size\":[").Append((c[2].x - c[0].x).ToString("F0")).Append(",").Append((c[2].y - c[0].y).ToString("F0")).Append("]")
                      .Append(",\"interactable\":").Append(btn.interactable ? "true" : "false")
                      .Append("}");
                }
            }
            sb.Append("]");

            if (g != null && cam != null)
            {
                sb.Append(",\"units\":[");
                first = true;
                for (int i = 0; i < g.Units.Count; i++)
                {
                    RtsUnit u = g.Units[i];
                    if (u == null || !u.IsAlive) continue;
                    Vector3 sp = cam.WorldToScreenPoint(u.transform.position + Vector3.up * 0.6f);
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"team\":\"").Append(u.Team == Team.Blue ? "blue" : "red").Append("\"")
                      .Append(",\"kind\":\"").Append(u.Kind).Append("\"")
                      .Append(",\"screen\":[").Append(sp.x.ToString("F0")).Append(",").Append((h - sp.y).ToString("F0")).Append("]")
                      .Append(",\"behind\":").Append(sp.z < 0f ? "true" : "false")
                      .Append(",\"pos\":[").Append(u.transform.position.x.ToString("F1")).Append(",").Append(u.transform.position.z.ToString("F1")).Append("]")
                      .Append("}");
                }
                sb.Append("]");

                sb.Append(",\"nodes\":[");
                first = true;
                for (int i = 0; i < g.Nodes.Count; i++)
                {
                    RtsResourceNode n = g.Nodes[i];
                    if (n == null || n.Depleted) continue;
                    Vector3 sp = cam.WorldToScreenPoint(n.transform.position + Vector3.up * 0.5f);
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"screen\":[").Append(sp.x.ToString("F0")).Append(",").Append((h - sp.y).ToString("F0")).Append("]")
                      .Append(",\"pos\":[").Append(n.transform.position.x.ToString("F1")).Append(",").Append(n.transform.position.z.ToString("F1")).Append("]")
                      .Append(",\"left\":").Append(n.Remaining)
                      .Append("}");
                }
                sb.Append("]");

                AppendScreenPoint(sb, "hq", cam, g.PlayerHQ, h);
                AppendScreenPoint(sb, "enemyHq", cam, g.EnemyHQ, h);

                RtsBuilding barracks = null;
                for (int i = 0; i < g.Buildings.Count; i++)
                {
                    RtsBuilding b = g.Buildings[i];
                    if (b != null && b.IsAlive && b.Team == Team.Blue
                        && b.Kind == BuildingKind.Barracks) { barracks = b; break; }
                }
                AppendScreenPoint(sb, "barracks", cam, barracks, h);

                sb.Append(",\"camera\":{\"height\":").Append(cam.transform.position.y.ToString("F1"))
                  .Append(",\"pos\":[").Append(cam.transform.position.x.ToString("F1")).Append(",")
                  .Append(cam.transform.position.z.ToString("F1")).Append("]}");
            }

            sb.Append("}");
            return sb.ToString();
        }

        private static void AppendScreenPoint(StringBuilder sb, string key, Camera cam, RtsBuilding b, float h)
        {
            sb.Append(",\"").Append(key).Append("\":");
            if (b == null || !b.IsAlive || cam == null) { sb.Append("null"); return; }
            Vector3 sp = cam.WorldToScreenPoint(b.transform.position + Vector3.up * 1.5f);
            sb.Append("[")
              .Append(sp.x.ToString("F0")).Append(",").Append((h - sp.y).ToString("F0"))
              .Append("]");
        }

        /// <summary>
        /// 在玩家总部周围螺旋搜索一个合法建造落点，返回世界坐标与 GameView 屏幕坐标。
        /// 供自动试玩与手动调试定位可放置位置，避免点在全是非法区的地方。
        /// </summary>
        public string FindSpot(int kind)
        {
            RtsGame g = RtsGame.I;
            Camera cam = Camera.main;
            if (g == null || g.PlayerHQ == null || cam == null) return "{\"error\":\"no_ctx\"}";

            RtsBuildPlacer placer = FindObjectOfType<RtsBuildPlacer>();
            if (placer == null) return "{\"error\":\"no_placer\"}";

            Vector3 c = g.PlayerHQ.transform.position;
            float h = Screen.height;

            for (float r = 7f; r <= 28f; r += 1.5f)
            {
                for (int a = 0; a < 24; a++)
                {
                    float ang = a * Mathf.PI * 2f / 24f;
                    Vector3 pos = c + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
                    if (!placer.CanPlace((BuildingKind)kind, pos)) continue;

                    Vector3 sp = cam.WorldToScreenPoint(pos);
                    if (sp.z < 0f) continue;
                    if (sp.x < 24f || sp.x > Screen.width - 24f) continue;
                    if (sp.y < 90f || sp.y > h - 140f) continue;

                    return "{\"ok\":true,\"world\":[" + pos.x.ToString("F1") + "," + pos.z.ToString("F1")
                        + "],\"screen\":[" + sp.x.ToString("F0") + "," + (h - sp.y).ToString("F0") + "]}";
                }
            }
            return "{\"ok\":false}";
        }

        /// <summary>选中构成：总数、工兵数、战斗单位数、建筑数。</summary>
        public string SelectionInfo()
        {
            RtsGame g = RtsGame.I;
            if (g == null) return "{\"error\":\"no_game\"}";

            int eng = 0, army = 0, bld = 0;
            for (int i = 0; i < g.Selection.Count; i++)
            {
                RtsSelectable s = g.Selection[i];
                if (s == null) continue;
                RtsUnit u = s as RtsUnit;
                if (u != null)
                {
                    if (u.Kind == UnitKind.Engineer) eng++;
                    else army++;
                }
                else bld++;
            }

            return "{\"total\":" + g.Selection.Count + ",\"engineers\":" + eng
                + ",\"army\":" + army + ",\"buildings\":" + bld + "}";
        }

        public string Probe()
        {
            RtsGame g = RtsGame.I;
            RtsBuildPlacer p = FindObjectOfType<RtsBuildPlacer>();
            RtsHUD h = FindObjectOfType<RtsHUD>();
            RtsWorldOverlay o = FindObjectOfType<RtsWorldOverlay>();

            string s = "{";
            s += "\"game\":" + (g != null ? "true" : "false");
            s += ",\"units\":" + (g != null ? g.Units.Count : -1);
            s += ",\"buildings\":" + (g != null ? g.Buildings.Count : -1);
            s += ",\"nodes\":" + (g != null ? g.Nodes.Count : -1);
            s += ",\"placer\":" + (p != null ? "true" : "false");
            s += ",\"placerActive\":" + (p != null && p.Active ? "true" : "false");
            s += ",\"hud\":" + (h != null ? "true" : "false");
            s += ",\"overlay\":" + (o != null ? "true" : "false");
            s += ",\"state\":\"" + (g != null ? g.State.ToString() : "?") + "\"";
            s += "}";
            return s;
        }

        /// <summary>紧凑状态报告，便于从编辑器侧断言。</summary>
        public string Report()
        {
            RtsGame g = RtsGame.I;
            if (g == null) return "{\"error\":\"no_game\"}";

            int blueUnits = 0;
            int redUnits = 0;
            int blueEng = 0;
            float blueHp = 0f;
            float redHp = 0f;
            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u == null || !u.IsAlive) continue;
                if (u.Team == Team.Blue) { blueUnits++; blueHp += u.Hp; }
                else if (u.Team == Team.Red) { redUnits++; redHp += u.Hp; }
                if (u.Team == Team.Blue && u.Kind == UnitKind.Engineer) blueEng++;
            }

            int blueBuildings = 0;
            int redBuildings = 0;
            int redBarracks = 0;
            float hqHp = -1f;
            float enemyHqHp = -1f;

            for (int i = 0; i < g.Buildings.Count; i++)
            {
                RtsBuilding b = g.Buildings[i];
                if (b == null || !b.IsAlive) continue;
                if (b.Team == Team.Blue) blueBuildings++;
                if (b.Team == Team.Red)
                {
                    redBuildings++;
                    if (b.Kind == BuildingKind.Barracks) redBarracks++;
                }
            }

            if (g.PlayerHQ != null) hqHp = g.PlayerHQ.Hp;
            if (g.EnemyHQ != null) enemyHqHp = g.EnemyHQ.Hp;

            float redNearHq = -1f;
            if (g.PlayerHQ != null)
            {
                redNearHq = 999f;
                for (int i = 0; i < g.Units.Count; i++)
                {
                    RtsUnit u = g.Units[i];
                    if (u == null || !u.IsAlive || u.Team != Team.Red) continue;
                    float dd = RtsUtil.Flat(u.transform.position, g.PlayerHQ.transform.position);
                    if (dd < redNearHq) redNearHq = dd;
                }
                if (redNearHq > 900f) redNearHq = -1f;
            }

            string s = "{";
            s += "\"state\":\"" + g.State + "\"";
            s += ",\"crystal\":" + g.Crystal;
            s += ",\"enemyCrystal\":" + g.EnemyCrystal;
            s += ",\"supplyUsed\":" + g.SupplyUsed + ",\"supplyCap\":" + g.SupplyCap;
            s += ",\"enemySupplyUsed\":" + g.EnemySupplyUsed + ",\"enemySupplyCap\":" + g.EnemySupplyCap;
            s += ",\"elapsed\":" + g.Elapsed.ToString("0.0");
            s += ",\"blueUnits\":" + blueUnits;
            s += ",\"redUnits\":" + redUnits;
            s += ",\"blueHp\":" + blueHp.ToString("0");
            s += ",\"redHp\":" + redHp.ToString("0");
            s += ",\"blueEngineers\":" + blueEng;
            s += ",\"blueBuildings\":" + blueBuildings;
            s += ",\"redBuildings\":" + redBuildings;
            s += ",\"redBarracks\":" + redBarracks;
            s += ",\"hqHp\":" + hqHp.ToString("0");
            s += ",\"enemyHqHp\":" + enemyHqHp.ToString("0");
            s += ",\"kills\":" + g.Kills;
            s += ",\"losses\":" + g.Losses;
            s += ",\"redNearHq\":" + redNearHq.ToString("0.0");
            s += ",\"selected\":" + g.Selection.Count;
            s += "}";
            return s;
        }
    }
}

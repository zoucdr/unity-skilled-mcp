using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace IronFront
{
    /// <summary>HUD：资源条、小地图、选中信息、命令面板、提示、帮助与结算。全部运行时构建。</summary>
    public class RtsHUD : MonoBehaviour
    {
        public static RtsHUD I;

        public Camera Cam;
        public RtsBuildPlacer Placer;
        public Canvas Canvas;

        public Text CrystalText;
        public Text SupplyText;
        public Text TimerText;
        public Text SelectionText;
        public Text ToastText;
        public Image ToastBg;
        public Text PauseText;
        public Image SelectionSwatch;

        public Button[] CmdButtons = new Button[5];
        public Text[] CmdTitles = new Text[5];
        public Text[] CmdCosts = new Text[5];

        public GameObject EndPanel;
        public Text EndTitle;
        public Text EndStats;
        public GameObject HelpPanel;

        public RawImage MiniMap;

        private float _refresh;
        private float _toastTimer;
        private float _mmTimer;
        private Texture2D _mmTex;
        private Color32[] _mmPixels;

        private static readonly string[] CmdTitleText =
            { "ENGINEER", "RIFLEMAN", "TANK", "BARRACKS", "TURRET" };

        private static readonly int[] CmdCostValue = { 60, 90, 320, 350, 220 };

        private static readonly string[] CmdKey = { "1", "2", "3", "4", "5" };

        private void Awake()
        {
            I = this;
        }

        private void OnEnable()
        {
            if (I == null) I = this;
        }

        private void Start()
        {
            if (RtsGame.I != null)
            {
                RtsGame.I.GameOver += OnGameOver;
                RtsGame.I.SelectionChanged += OnSelectionChanged;
            }
            if (ToastText != null) ToastText.text = "";
            if (PauseText != null) PauseText.gameObject.SetActive(false);
            if (EndPanel != null) EndPanel.SetActive(false);
            if (HelpPanel != null) HelpPanel.SetActive(false);
            BuildMiniMapTexture();
        }

        private void OnDestroy()
        {
            if (RtsGame.I != null)
            {
                RtsGame.I.GameOver -= OnGameOver;
                RtsGame.I.SelectionChanged -= OnSelectionChanged;
            }
            if (I == this) I = null;
        }

        public static void Toast(string msg)
        {
            if (I == null)
            {
                Debug.Log("[HUD] " + msg);
                return;
            }
            I.ShowToast(msg);
        }

        public void ShowToast(string msg)
        {
            if (ToastText == null) return;
            ToastText.text = msg;
            _toastTimer = 2.6f;
        }

        // ---------- 输入热键 ----------

        private void Update()
        {
            if (RtsGame.I == null) return;

            if (Input.GetKeyDown(KeyCode.Space)) RtsGame.I.TogglePause();

            if (Input.GetKeyDown(KeyCode.F1) && HelpPanel != null)
            {
                HelpPanel.SetActive(!HelpPanel.activeSelf);
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (HelpPanel != null && HelpPanel.activeSelf) HelpPanel.SetActive(false);
                else if (Placer != null && Placer.Active) Placer.Cancel();
                else RtsGame.I.ClearSelection();
            }

            for (int i = 0; i < 5; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) OnCommand(i);
            }

            float dt = Time.unscaledDeltaTime;

            if (_toastTimer > 0f)
            {
                _toastTimer -= dt;
                if (ToastText != null)
                {
                    Color c = ToastText.color;
                    c.a = Mathf.Clamp01(_toastTimer / 0.5f);
                    ToastText.color = c;
                    if (_toastTimer <= 0f) ToastText.text = "";
                }
            }

            if (ToastBg != null)
            {
                bool show = _toastTimer > 0f;
                if (ToastBg.enabled != show) ToastBg.enabled = show;
            }

            if (PauseText != null) PauseText.gameObject.SetActive(RtsGame.I.State == GameState.Paused);

            _refresh += dt;
            if (_refresh >= 0.1f)
            {
                _refresh = 0f;
                Refresh();
            }

            _mmTimer += dt;
            if (_mmTimer >= 0.2f)
            {
                _mmTimer = 0f;
                RefreshMiniMap();
            }
        }

        // ---------- 刷新 ----------

        private void Refresh()
        {
            RtsGame g = RtsGame.I;
            if (g == null) return;

            if (CrystalText != null) CrystalText.text = g.Crystal.ToString();
            if (SupplyText != null) SupplyText.text = g.SupplyUsed + " / " + g.SupplyCap;
            if (TimerText != null) TimerText.text = RtsUtil.MmSs(g.Elapsed);

            RefreshSelection();
            RefreshButtons();
        }

        private void RefreshSelection()
        {
            RtsGame g = RtsGame.I;
            if (SelectionText == null) return;

            if (g.Selection.Count == 0)
            {
                SelectionText.text = "NO SELECTION\n\nLeft click / drag to select.\nRight click to command.";
                if (SelectionSwatch != null) SelectionSwatch.color = new Color(0.2f, 0.24f, 0.3f, 0.9f);
                return;
            }

            RtsSelectable first = g.Selection[0];
            if (first == null)
            {
                SelectionText.text = "";
                return;
            }

            string s = first.InfoLine();
            if (g.Selection.Count > 1) s += "\n+ " + (g.Selection.Count - 1) + " more selected";

            RtsUnit u = first as RtsUnit;
            if (u != null && u.Def != null)
            {
                s += "\nDMG " + Mathf.RoundToInt(u.Def.Damage) + "   RNG " + u.Def.Range.ToString("0.0")
                     + "   SPD " + u.Def.Speed.ToString("0.0");
            }

            RtsBuilding b = first as RtsBuilding;
            if (b != null && b.Def != null)
            {
                s += "\nSUPPLY +" + b.Def.Supply + "   SIGHT " + Mathf.RoundToInt(b.Def.Sight);
            }

            SelectionText.text = s;

            if (SelectionSwatch != null) SelectionSwatch.color = RtsDefs.TeamColor(first.Team);
        }

        private void RefreshButtons()
        {
            for (int i = 0; i < CmdButtons.Length; i++)
            {
                if (CmdButtons[i] == null) continue;
                bool ok = CanCommand(i);
                CmdButtons[i].interactable = ok;

                if (CmdTitles[i] != null)
                    CmdTitles[i].color = ok ? new Color(0.92f, 0.95f, 1f) : new Color(0.45f, 0.48f, 0.52f);
                if (CmdCosts[i] != null)
                    CmdCosts[i].color = ok ? new Color(1f, 0.85f, 0.35f) : new Color(0.42f, 0.36f, 0.3f);
            }
        }

        private bool CanCommand(int idx)
        {
            RtsGame g = RtsGame.I;
            if (g == null || g.IsOver) return false;
            if (Placer != null && Placer.Active) return false;

            if (idx <= 2)
            {
                RtsBuilding producer = FindProducer(idx);
                if (producer == null || producer.UnderConstruction) return false;
                if (producer.Queue.Count >= 5) return false;

                UnitKind kind = idx == 0 ? UnitKind.Engineer : (idx == 1 ? UnitKind.Rifleman : UnitKind.Tank);
                UnitDef ud = RtsDefs.U(kind);
                return g.CanAfford(ud.Cost, ud.Supply);
            }

            if (!HasEngineerSelected()) return false;
            BuildingKind bk = idx == 3 ? BuildingKind.Barracks : BuildingKind.Turret;
            return g.CanAfford(RtsDefs.B(bk).Cost, 0);
        }

        private RtsBuilding FindProducer(int idx)
        {
            RtsGame g = RtsGame.I;
            BuildingKind want = idx == 0 ? BuildingKind.HQ : BuildingKind.Barracks;
            for (int i = 0; i < g.Selection.Count; i++)
            {
                RtsBuilding b = g.Selection[i] as RtsBuilding;
                if (b != null && b.Team == Team.Blue && b.Kind == want && b.IsAlive) return b;
            }
            return null;
        }

        private static bool HasEngineerSelected()
        {
            List<RtsUnit> units = RtsInput.SelectedUnits();
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].Kind == UnitKind.Engineer) return true;
            }
            return false;
        }

        // ---------- 命令 ----------

        public void CommandFromButton(int idx)
        {
            OnCommand(idx);
        }

        private void OnCommand(int idx)
        {
            RtsGame g = RtsGame.I;
            if (g == null || g.IsOver) return;
            if (Placer != null && Placer.Active) return;

            if (idx <= 2)
            {
                RtsBuilding producer = FindProducer(idx);
                if (producer == null)
                {
                    Toast("SELECT HQ / BARRACKS FIRST");
                    return;
                }
                if (producer.UnderConstruction)
                {
                    Toast("STILL UNDER CONSTRUCTION");
                    return;
                }
                UnitKind kind = idx == 0 ? UnitKind.Engineer : (idx == 1 ? UnitKind.Rifleman : UnitKind.Tank);
                UnitDef ud = RtsDefs.U(kind);

                if (!g.CanAfford(ud.Cost, ud.Supply))
                {
                    if (g.Crystal < ud.Cost) Toast("NOT ENOUGH CRYSTAL");
                    else Toast("SUPPLY BLOCKED");
                    return;
                }
                if (producer.Enqueue(kind)) Toast(ud.Name + " QUEUED");
                return;
            }

            BuildingKind bk = idx == 3 ? BuildingKind.Barracks : BuildingKind.Turret;
            if (!HasEngineerSelected())
            {
                Toast("SELECT AN ENGINEER FIRST");
                return;
            }
            if (Placer != null) Placer.Begin(bk);
        }

        // ---------- 结算 ----------

        private void OnSelectionChanged()
        {
            RefreshSelection();
            RefreshButtons();
        }

        private void OnGameOver(GameState state)
        {
            if (EndPanel == null) return;
            EndPanel.SetActive(true);

            bool win = state == GameState.Won;
            if (EndTitle != null)
            {
                EndTitle.text = win ? "VICTORY" : "DEFEAT";
                EndTitle.color = win ? new Color(0.45f, 0.95f, 0.55f) : new Color(0.95f, 0.35f, 0.3f);
            }
            if (EndStats != null)
            {
                RtsGame g = RtsGame.I;
                EndStats.text = "TIME  " + RtsUtil.MmSs(g.Elapsed)
                                + "\nKILLS  " + g.Kills
                                + "\nLOSSES  " + g.Losses
                                + "\nCRYSTAL  " + g.Crystal;
            }
        }

        // ---------- 小地图 ----------

        private void BuildMiniMapTexture()
        {
            _mmTex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            _mmTex.filterMode = FilterMode.Point;
            _mmPixels = new Color32[128 * 128];
            if (MiniMap != null) MiniMap.texture = _mmTex;
            RefreshMiniMap();
        }

        private void RefreshMiniMap()
        {
            if (_mmTex == null || _mmPixels == null || RtsGame.I == null) return;

            Color32 bg = new Color32(26, 34, 26, 255);
            for (int i = 0; i < _mmPixels.Length; i++) _mmPixels[i] = bg;

            RtsGame g = RtsGame.I;

            for (int i = 0; i < g.Nodes.Count; i++)
            {
                RtsResourceNode n = g.Nodes[i];
                if (n == null || n.Depleted) continue;
                Plot(n.transform.position, new Color32(90, 200, 255, 255), 2);
            }

            for (int i = 0; i < g.Buildings.Count; i++)
            {
                RtsBuilding b = g.Buildings[i];
                if (b == null || !b.IsAlive) continue;
                Color32 c = b.Team == Team.Blue ? new Color32(70, 140, 240, 255) : new Color32(220, 80, 60, 255);
                Plot(b.transform.position, c, 3);
            }

            for (int i = 0; i < g.Units.Count; i++)
            {
                RtsUnit u = g.Units[i];
                if (u == null || !u.IsAlive) continue;
                Color32 c = u.Team == Team.Blue ? new Color32(130, 190, 255, 255) : new Color32(255, 130, 100, 255);
                Plot(u.transform.position, c, 1);
            }

            _mmTex.SetPixels32(_mmPixels);
            _mmTex.Apply(false);
        }

        private void Plot(Vector3 world, Color32 c, int size)
        {
            int x = Mathf.RoundToInt((world.x + 40f) / 80f * 127f);
            int y = Mathf.RoundToInt((world.z + 40f) / 80f * 127f);

            for (int dy = -size; dy <= size; dy++)
            {
                for (int dx = -size; dx <= size; dx++)
                {
                    int px = x + dx;
                    int py = y + dy;
                    if (px < 0 || px > 127 || py < 0 || py > 127) continue;
                    _mmPixels[py * 128 + px] = c;
                }
            }
        }
    }
}

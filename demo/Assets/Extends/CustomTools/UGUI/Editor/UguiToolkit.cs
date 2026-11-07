using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using System.Collections.Generic;

namespace UniMcp.Ugui
{
    // ============================================================================
    // 设计令牌（Design Tokens）—— 对应 figma 的变量 / 组件库定义
    // 仿 ComponentDefineObject 做成可编辑 ScriptableObject，便于在 Inspector 调色。
    // ============================================================================
    [CreateAssetMenu(fileName = "UguiTheme", menuName = "UGUI/Theme")]
    public class UguiTheme : ScriptableObject
    {
        public Color bg, panel, panelSolid, cyan, cyanDim, valueCol, textCol, textDim,
                    green, orange, yellow, barCol, lineCol, buttonCol, red;
        public int fTitle = 26, fH1 = 30, fH2 = 22, fBody = 16, fSmall = 13, fKpi = 40;
        public float sSpace = 8, mSpace = 16, lSpace = 24;

        public static UguiTheme Default()
        {
            var t = CreateInstance<UguiTheme>();
            t.bg = Hex("#070B14");
            t.panel = Hex("rgba(14,26,43,0.62)");
            t.panelSolid = Hex("rgba(11,20,34,0.92)");
            t.cyan = Hex("#00D8FF");
            t.cyanDim = Hex("#0AA6C8");
            t.valueCol = Hex("#5FE9FF");
            t.textCol = Hex("#9FB3C8");
            t.textDim = Hex("#6B7E92");
            t.green = Hex("#36E27A");
            t.orange = Hex("#FF8A3C");
            t.yellow = Hex("#FFC53C");
            t.barCol = Hex("rgba(25,198,232,0.9)");
            t.lineCol = Hex("rgba(0,216,255,0.18)");
            t.buttonCol = Hex("rgba(14,58,85,0.95)");
            t.red = Hex("#FF5470");
            return t;
        }

        public static Color Hex(string s, float a = -1f)
        {
            s = s.Trim();
            if (s.StartsWith("#"))
            {
                string h = s.Substring(1);
                float r, g, b, al = 1f;
                if (h.Length == 3) { r = P(h[0]) ; g = P(h[1]); b = P(h[2]); }
                else if (h.Length == 6) { r = P2(h.Substring(0,2)); g = P2(h.Substring(2,2)); b = P2(h.Substring(4,2)); }
                else if (h.Length == 8) { r = P2(h.Substring(0,2)); g = P2(h.Substring(2,2)); b = P2(h.Substring(4,2)); al = P2(h.Substring(6,2)); }
                else { r = g = b = 1f; }
                return new Color(r, g, b, a < 0 ? al : a);
            }
            if (s.StartsWith("rgba") || s.StartsWith("rgb"))
            {
                string[] p = s.Substring(s.IndexOf('(') + 1).TrimEnd(')').Split(',');
                float r = float.Parse(p[0]) / 255f, g = float.Parse(p[1]) / 255f, b = float.Parse(p[2]) / 255f;
                float al = p.Length > 3 ? float.Parse(p[3]) : 1f;
                return new Color(r, g, b, a < 0 ? al : a);
            }
            return Color.white;
        }
        static float P(char c) { return int.Parse("" + c + c, System.Globalization.NumberStyles.HexNumber) / 255f; }
        static float P2(string h) { return int.Parse(h, System.Globalization.NumberStyles.HexNumber) / 255f; }
    }

    // ============================================================================
    // UGUI 生成器（纯 Unity 实现，坐标逻辑与已验证的 Node 工具一致）
    // 约定：元素用「画布绝对左上坐标 + 尺寸」描述；坐标换算为相对父对象中心的局部偏移。
    // ============================================================================
    public static class UguiGenerator
    {
        class N { public GameObject go; public float cx, cy; }
        static Dictionary<string, N> map = new Dictionary<string, N>();
        static UguiTheme th;
        static float W, H;

        public static void Init(UguiTheme theme = null) { th = theme ?? UguiTheme.Default(); map.Clear(); }

        // --- 根 Canvas ---
        public static GameObject Begin(string rootName, float w, float h)
        {
            Init();
            W = w; H = h;
            var go = new GameObject(rootName);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(w, h);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            Undo.RegisterCreatedObjectUndo(go, "Create UGUI " + rootName);
            map[rootName] = new N { go = go, cx = w / 2f, cy = h / 2f };
            Selection.activeGameObject = go;
            return go;
        }

        // --- 通用生成（核心）---
        static GameObject Spawn(string parent, string name, string ctrl, float x, float y, float w, float h)
        {
            if (!map.ContainsKey(parent)) { Debug.LogError("[Ugui] 父对象不存在: " + parent); return null; }
            var pn = map[parent];
            var go = new GameObject(name);
            go.transform.SetParent(pn.go.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            float cx = x + w / 2f, cy = y + h / 2f;
            rt.anchoredPosition = new Vector2(cx - pn.cx, pn.cy - cy); // 顶左原点 → UGUI(+上)
            if (ctrl == "image" || ctrl == "panel") go.AddComponent<Image>();
            else if (ctrl == "text") go.AddComponent<Text>();
            map[name] = new N { go = go, cx = cx, cy = cy };
            return go;
        }

        static void SetColor(GameObject go, Color c) { var img = go.GetComponent<Image>(); if (img) img.color = c; }
        static void SetText(GameObject go, string t, int size, Color c, TextAnchor align)
        {
            var tx = go.GetComponent<Text>(); if (!tx) return;
            tx.text = t; tx.fontSize = size; tx.color = c; tx.alignment = align;
            tx.horizontalOverflow = HorizontalWrapMode.Overflow; tx.verticalOverflow = VerticalWrapMode.Overflow;
        }
        static void Outline(GameObject go, Color c, float d = 1.5f)
        {
            var o = go.AddComponent<Outline>(); o.effectColor = c; o.effectDistance = new Vector2(d, d);
        }
        static void Shadow(GameObject go, Color c, float d = 2f)
        {
            var s = go.AddComponent<Shadow>(); s.effectColor = c; s.effectDistance = new Vector2(d, d);
        }
        static Rect Inside(Rect r, float ox, float oy, float w, float h) { return new Rect(r.x + ox, r.y + oy, w, h); }

        // --- 组件库 ---
        public static GameObject Panel(string parent, string name, Rect r, Color? color = null, Color? outline = null)
        {
            var go = Spawn(parent, name, "image", r.x, r.y, r.width, r.height);
            SetColor(go, color ?? th.panel);
            if (outline.HasValue) Outline(go, outline.Value);
            return go;
        }
        public static GameObject Text(string parent, string name, Rect r, string t, int size, Color c, TextAnchor align = TextAnchor.MiddleLeft, bool shadow = false)
        {
            var go = Spawn(parent, name, "text", r.x, r.y, r.width, r.height);
            SetText(go, t, size, c, align);
            if (shadow) Shadow(go, c);
            return go;
        }
        public static GameObject Button(string parent, string name, Rect r, string label, Color? bg = null, Color? outline = null)
        {
            var go = Spawn(parent, name, "image", r.x, r.y, r.width, r.height);
            SetColor(go, bg ?? th.buttonCol);
            Outline(go, outline ?? th.cyan);
            go.AddComponent<Button>();
            Text(name, name + "_Txt", new Rect(r.x, r.y + (r.height - 30) / 2, r.width, 30), label, 18, th.valueCol, TextAnchor.MiddleCenter);
            return go;
        }
        public static GameObject KpiCard(string parent, string name, Rect r, string label, string value)
        {
            Panel(parent, name, r, th.panel, th.cyan);
            Text(name, name + "_L", Inside(r, th.sSpace, th.sSpace, r.width - 36, 22), label, th.fSmall, th.textCol);
            Text(name, name + "_V", Inside(r, th.sSpace, th.sSpace + 26, r.width - 36, 56), value, th.fKpi, th.valueCol, TextAnchor.MiddleLeft, true);
            return map[name].go;
        }
        public static GameObject BarChart(string parent, string name, Rect r, string title, float[] vals)
        {
            Panel(parent, name, r, th.panel, th.cyan);
            Text(name, name + "_T", Inside(r, th.sSpace, th.sSpace, r.width - 24, 22), title, th.fSmall, th.cyan);
            float baseY = r.y + r.height - th.sSpace;
            float slot = (r.width - th.sSpace * 2) / vals.Length;
            float bw = Mathf.Min(28, slot - 8);
            for (int i = 0; i < vals.Length; i++)
            {
                float bx = r.x + th.sSpace + i * slot + (slot - bw) / 2;
                var go = Spawn(name, name + "_B" + i, "image", bx, baseY - vals[i], bw, vals[i]);
                var img = go.GetComponent<Image>();
                img.color = th.barCol; img.type = Image.Type.Filled; img.fillMethod = Image.FillMethod.Vertical; img.fillAmount = 1f;
            }
            return map[name].go;
        }
        public static GameObject RingChart(string parent, string name, Rect r, string title, float percent)
        {
            Panel(parent, name, r, th.panel, th.cyan);
            Text(name, name + "_T", Inside(r, th.sSpace, th.sSpace, r.width - 24, 22), title, th.fSmall, th.cyan);
            float d = Mathf.Min(r.width, r.height) - 90;
            float ox = r.x + (r.width - d) / 2, oy = r.y + (r.height - d) / 2 + 6;
            var ring = Spawn(name, name + "_Ring", "image", ox, oy, d, d);
            var ri = ring.GetComponent<Image>();
            ri.type = Image.Type.Filled; ri.fillMethod = Image.FillMethod.Radial360; ri.fillClockwise = true; ri.fillAmount = percent / 100f; ri.color = th.cyan;
            float hd = d * 0.66f;
            var hole = Spawn(name, name + "_Hole", "image", ox + (d - hd) / 2, oy + (d - hd) / 2, hd, hd);
            hole.GetComponent<Image>().color = th.panelSolid;
            Text(name, name + "_V", Inside(r, 0, r.height / 2 - 18, r.width, 36), Mathf.RoundToInt(percent) + "%", th.fH1, th.valueCol, TextAnchor.MiddleCenter, true);
            return map[name].go;
        }
        public static GameObject Alert(string parent, string name, Rect r, string title, string[] lines, Color? color = null)
        {
            Color c = color ?? th.orange;
            Panel(parent, name, r, UguiTheme.Hex("rgba(26,14,4,0.9)"), c);
            var ico = Spawn(name, name + "_Ico", "image", r.x + th.sSpace, r.y + th.sSpace, 44, 44);
            ico.GetComponent<Image>().color = th.yellow;
            Text(name, name + "_Bang", new Rect(r.x + th.sSpace + 4, r.y + th.sSpace + 4, 36, 36), "!", 30, UguiTheme.Hex("#1A0E04"), TextAnchor.MiddleCenter);
            Text(name, name + "_T", Inside(r, th.sSpace + 60, th.sSpace, r.width - 100, 24), title, 18, c);
            for (int i = 0; i < lines.Length; i++)
                Text(name, name + "_L" + i, Inside(r, th.sSpace, th.sSpace + 60 + i * 22, r.width - 36, 20), lines[i], th.fSmall, th.textCol);
            return map[name].go;
        }
        // 扩展控件
        public static GameObject ProgressBar(string parent, string name, Rect r, float percent, Color? color = null)
        {
            var bg = Spawn(parent, name, "image", r.x, r.y, r.width, r.height);
            var bi = bg.GetComponent<Image>(); bi.color = th.panelSolid; bi.type = Image.Type.Filled; bi.fillMethod = Image.FillMethod.Horizontal; bi.fillAmount = 1f;
            var fill = Spawn(name, name + "_Fill", "image", r.x, r.y, r.width, r.height);
            var fi = fill.GetComponent<Image>(); fi.color = color ?? th.cyan; fi.type = Image.Type.Filled; fi.fillMethod = Image.FillMethod.Horizontal; fi.fillAmount = percent / 100f;
            return map[name].go;
        }
        public static GameObject Toggle(string parent, string name, Rect r, string label, bool on = true)
        {
            var go = Spawn(parent, name, "image", r.x, r.y, r.width, r.height);
            var img = go.GetComponent<Image>();
            img.color = on ? th.cyanDim : th.panelSolid; img.type = Image.Type.Filled; img.fillMethod = Image.FillMethod.Horizontal; img.fillAmount = 1f;
            go.AddComponent<Toggle>();
            float knobX = on ? r.x + r.width - r.height : r.x;
            var knob = Spawn(name, name + "_Knob", "image", knobX, r.y, r.height, r.height);
            knob.GetComponent<Image>().color = on ? th.cyan : th.textCol;
            Text(parent, name + "_Lbl", new Rect(r.x - 160, r.y, 150, r.height), label, th.fBody, th.textCol, TextAnchor.MiddleRight);
            return map[name].go;
        }
        public static GameObject Slider(string parent, string name, Rect r, float value, Color? color = null)
        {
            var go = Spawn(parent, name, "image", r.x, r.y, r.width, r.height);
            var img = go.GetComponent<Image>(); img.color = th.panelSolid; img.type = Image.Type.Filled; img.fillMethod = Image.FillMethod.Horizontal; img.fillAmount = 1f;
            var fill = Spawn(name, name + "_Fill", "image", r.x, r.y, r.width, r.height);
            var fi = fill.GetComponent<Image>(); fi.color = color ?? th.cyan; fi.type = Image.Type.Filled; fi.fillMethod = Image.FillMethod.Horizontal; fi.fillAmount = value;
            float hx = r.x + (r.width - r.height) * value;
            var handle = Spawn(name, name + "_Handle", "image", hx, r.y - 4, r.height + 8, r.height + 8);
            handle.GetComponent<Image>().color = th.valueCol;
            return map[name].go;
        }
        public static GameObject InputField(string parent, string name, Rect r, string placeholder)
        {
            var go = Spawn(parent, name, "image", r.x, r.y, r.width, r.height);
            var img = go.GetComponent<Image>(); img.color = th.panelSolid; img.type = Image.Type.Filled; img.fillMethod = Image.FillMethod.Horizontal; img.fillAmount = 1f;
            go.AddComponent<InputField>();
            Spawn(name, name + "_Txt", "text", r.x + 10, r.y, r.width - 20, r.height);
            Text(name, name + "_Ph", new Rect(r.x + 10, r.y, r.width - 20, r.height), placeholder, th.fBody, th.textDim, TextAnchor.MiddleLeft);
            return map[name].go;
        }
        public static GameObject Card(string parent, string name, Rect r, string title, string body, float? percent = null)
        {
            Panel(parent, name, r, th.panel, th.cyan);
            Text(name, name + "_T", Inside(r, th.sSpace, th.sSpace, r.width - 24, 24), title, th.fH2, th.cyan);
            Text(name, name + "_B", Inside(r, th.sSpace, th.sSpace + 32, r.width - 24, r.height - 64), body, th.fBody, th.textCol);
            if (percent.HasValue) ProgressBar(name, name + "_P", Inside(r, th.sSpace, r.height - 24, r.width - 24, 10), percent.Value);
            return map[name].go;
        }

        // --- 整屏模板 ---
        public static GameObject Build(string template, string rootName = null, float w = 1280, float h = 720)
        {
            if (rootName == null) rootName = "UI_" + char.ToUpper(template[0]) + template.Substring(1);
            Begin(rootName, w, h);
            switch (template)
            {
                case "dashboard": return TplDashboard();
                case "login": return TplLogin();
                case "settings": return TplSettings();
                case "cardGrid": return TplCardGrid();
                case "statPanel": return TplStatPanel();
                default: Debug.LogError("[Ugui] 未知模板: " + template); return map[rootName].go;
            }
        }

        static GameObject TplDashboard()
        {
            Panel("UI_Dashboard", "Bg", new Rect(0, 0, W, H), th.bg);
            Panel("UI_Dashboard", "TitleBar", new Rect(20, 16, W - 40, 56), th.panelSolid, th.cyan);
            Text("TitleBar", "Title", new Rect(40, 24, 460, 40), "CYBER COMMAND CENTER", th.fTitle, th.cyan, TextAnchor.MiddleLeft, true);
            Text("TitleBar", "Status", new Rect(820, 30, 220, 28), "● SYSTEMS NOMINAL", 18, th.green);
            Text("TitleBar", "Clock", new Rect(1040, 30, 200, 28), "2026-11-07 21:53", 16, th.textCol, TextAnchor.MiddleRight);
            string[] kpis = { "CPU LOAD|42%", "MEMORY|68%", "NETWORK I/O|1.24 Gb/s" };
            for (int i = 0; i < kpis.Length; i++)
            {
                var p = kpis[i].Split('|');
                KpiCard("UI_Dashboard", "KPI_" + p[0].Replace(" ", "").Replace("/", ""), new Rect(40, 96 + i * 132, 300, 120), p[0], p[1]);
            }
            BarChart("UI_Dashboard", "BarPanel", new Rect(40, 496, 300, 200), "THROUGHPUT", new float[] { 60, 110, 80, 145, 95, 125 });
            RingChart("UI_Dashboard", "RingPanel", new Rect(940, 96, 300, 240), "SYSTEM LOAD", 60);
            Button("UI_Dashboard", "BtnSettings", new Rect(940, 352, 300, 56), "SYSTEM SETTINGS");
            Button("UI_Dashboard", "BtnDiag", new Rect(940, 420, 300, 56), "RUN DIAGNOSTICS");
            Alert("UI_Dashboard", "AlertPanel", new Rect(940, 496, 300, 200), "WARNING", new string[] { "Anomaly node detected", "in cluster-07." });
            var fl = Spawn("UI_Dashboard", "FooterLine", "image", 40, 706, W - 80, 2); fl.GetComponent<Image>().color = th.lineCol;
            return map["UI_Dashboard"].go;
        }
        static GameObject TplLogin()
        {
            Panel("UI_Login", "Bg", new Rect(0, 0, W, H), th.bg);
            float pw = 420, px = (W - pw) / 2;
            Panel("UI_Login", "LoginCard", new Rect(px, 160, pw, 400), th.panel, th.cyan);
            Text("LoginCard", "Title", new Rect(px, 188, pw, 44), "SIGN IN", 34, th.cyan, TextAnchor.MiddleCenter, true);
            InputField("LoginCard", "User", new Rect(px + 40, 270, pw - 80, 46), "Username");
            InputField("LoginCard", "Pass", new Rect(px + 40, 336, pw - 80, 46), "Password");
            Toggle("LoginCard", "Remember", new Rect(px + 40, 404, 60, 28), "Remember me", true);
            Button("LoginCard", "LoginBtn", new Rect(px + 40, 460, pw - 80, 52), "LOGIN", th.cyanDim);
            Text("UI_Login", "Footer", new Rect(0, H - 40, W, 24), "© 2026 Cyber Systems · v2.1", 13, th.textDim, TextAnchor.MiddleCenter);
            return map["UI_Login"].go;
        }
        static GameObject TplSettings()
        {
            Panel("UI_Settings", "Bg", new Rect(0, 0, W, H), th.bg);
            Panel("UI_Settings", "Header", new Rect(40, 24, W - 80, 64), th.panelSolid, th.cyan);
            Text("Header", "Title", new Rect(60, 40, 400, 32), "SETTINGS", 28, th.cyan);
            float listX = 60, listY = 130, rowH = 70, w = W - 120;
            Toggle("UI_Settings", "T1", new Rect(listX, listY, 60, 28), "Auto Update", true);
            Toggle("UI_Settings", "T2", new Rect(listX, listY + rowH, 60, 28), "Dark Mode", true);
            Slider("UI_Settings", "S1", new Rect(listX, listY + rowH * 2 + 14, w - 200, 24), 0.7f);
            Text("UI_Settings", "S1Lbl", new Rect(listX + w - 180, listY + rowH * 2, 160, 24), "Volume", 16, th.textCol, TextAnchor.MiddleRight);
            ProgressBar("UI_Settings", "PB", new Rect(listX, listY + rowH * 3 + 14, w, 14), 82);
            Button("UI_Settings", "Save", new Rect(W / 2 - 110, H - 90, 220, 56), "SAVE CHANGES");
            return map["UI_Settings"].go;
        }
        static GameObject TplCardGrid()
        {
            Panel("UI_CardGrid", "Bg", new Rect(0, 0, W, H), th.bg);
            Text("UI_CardGrid", "Title", new Rect(40, 24, W - 80, 40), "MODULE OVERVIEW", 28, th.cyan, TextAnchor.MiddleLeft, true);
            var cards = new (string t, string b, float p)[]
            {
                ("SENSOR GRID","142 nodes online",92),("POWER CORE","Output 3.4 MW",76),("COOLING","Nominal 22°C",64),
                ("NETWORK","1.2 Gb/s uplink",88),("SECURITY","2 alerts active",40),("STORAGE","68% used",68),
            };
            float cols = 3, cw = 380, ch = 150, gx = 48, gy = 176, pad = 24;
            for (int i = 0; i < cards.Length; i++)
            {
                int col = i % 3, row = i / 3;
                Card("UI_CardGrid", "Card" + i, new Rect(gx + col * (cw + pad), gy + row * (ch + pad), cw, ch), cards[i].t, cards[i].b, cards[i].p);
            }
            return map["UI_CardGrid"].go;
        }
        static GameObject TplStatPanel()
        {
            Panel("UI_StatPanel", "Bg", new Rect(0, 0, W, H), th.bg);
            Panel("UI_StatPanel", "Main", new Rect(120, 80, W - 240, H - 160), th.panel, th.cyan);
            Text("Main", "Title", new Rect(144, 96, 400, 36), "SYSTEM TELEMETRY", 26, th.cyan);
            RingChart("Main", "Ring", new Rect(144, 150, 220, 220), "CPU", 73);
            RingChart("Main", "Ring2", new Rect(390, 150, 220, 220), "MEM", 61);
            BarChart("Main", "Bars", new Rect(144, 400, 560, 200), "THROUGHPUT", new float[] { 120, 160, 90, 200, 140, 175 });
            Alert("Main", "Alert", new Rect(764, 150, 300, 200), "NOTICE", new string[] { "Routine maintenance", "scheduled 02:00." }, th.yellow);
            return map["UI_StatPanel"].go;
        }

        // --- 供 MCP / 外部调用的扩展入口 ---
        public static string EnsureRoot(string name = "UIRoot")
        {
            if (map.Count == 0) Begin(name, 1280, 720);
            foreach (var k in map.Keys) return k;
            return name;
        }
        public static GameObject AddControl(string control, string parent, string name, Rect r, string label = "", float value = 0, Color? col = null)
        {
            switch (control)
            {
                case "panel": return Panel(parent, name, r, col ?? th.panel, th.cyan);
                case "image": { var g = Spawn(parent, name, "image", r.x, r.y, r.width, r.height); SetColor(g, col ?? th.cyan); return g; }
                case "text": return Text(parent, name, r, label, th.fBody, col ?? th.textCol);
                case "button": return Button(parent, name, r, label);
                case "kpi": return KpiCard(parent, name, r, label, value.ToString("F0"));
                case "barchart": return BarChart(parent, name, r, label, new float[] { 60, 110, 80, 145, 95, 125 });
                case "ring": return RingChart(parent, name, r, label, value);
                case "alert": return Alert(parent, name, r, label, new string[] { "Notice" });
                case "progress": return ProgressBar(parent, name, r, value, col);
                case "toggle": return Toggle(parent, name, r, label, value > 0.5f);
                case "slider": return Slider(parent, name, r, value, col);
                case "input": return InputField(parent, name, r, label);
                case "card": return Card(parent, name, r, label, label, value);
                default: Debug.LogError("[Ugui] 未知控件: " + control); return null;
            }
        }
    }
}

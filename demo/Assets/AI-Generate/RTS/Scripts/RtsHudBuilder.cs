using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace IronFront
{
    /// <summary>HUD 构建器：运行时创建全部 UGUI 控件，避免场景手工拖引用。</summary>
    public static class RtsHudBuilder
    {
        private static Font _font;

        private static readonly Vector2 TL = new Vector2(0f, 1f);
        private static readonly Vector2 TR = new Vector2(1f, 1f);
        private static readonly Vector2 BL = new Vector2(0f, 0f);
        private static readonly Vector2 BR = new Vector2(1f, 0f);
        private static readonly Vector2 CT = new Vector2(0.5f, 1f);
        private static readonly Vector2 CC = new Vector2(0.5f, 0.5f);

        public static Font Font
        {
            get
            {
                if (_font != null) return _font;
                try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch { _font = null; }
                if (_font == null)
                {
                    try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                    catch { _font = null; }
                }
                if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Arial", 16);
                return _font;
            }
        }

        public static void Build(RtsHUD hud, Transform parent)
        {
            if (Object.FindObjectOfType<EventSystem>() == null)
            {
                GameObject es = new GameObject("EventSystem",
                    typeof(EventSystem), typeof(StandaloneInputModule));
                es.transform.SetParent(parent, false);
            }

            GameObject canvasGo = new GameObject("HUD_Canvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(parent, false);

            Canvas cv = canvasGo.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 10;

            CanvasScaler cs = canvasGo.GetComponent<CanvasScaler>();
            cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            cs.referenceResolution = new Vector2(1920f, 1080f);
            cs.matchWidthOrHeight = 0.5f;

            hud.Canvas = cv;
            Transform root = canvasGo.transform;

            BuildTopBar(hud, root);
            BuildMiniMap(hud, root);
            BuildSelectionPanel(hud, root);
            BuildCommandPanel(hud, root);
            BuildToast(hud, root);
            BuildPause(hud, root);
            BuildHelp(hud, root);
            BuildEndPanel(hud, root);
        }

        // ---------- 顶部资源条 ----------

        private static void BuildTopBar(RtsHUD hud, Transform root)
        {
            GameObject bar = Panel(root, "TopBar", TL, TL, new Vector2(16f, -16f), new Vector2(430f, 62f),
                new Color(0.05f, 0.07f, 0.09f, 0.88f));

            Panel(bar.transform, "crystalIcon", TL, TL, new Vector2(12f, -13f), new Vector2(36f, 36f),
                new Color(0.22f, 0.68f, 0.92f, 1f));

            hud.CrystalText = Label(bar.transform, "crystalValue", "1200", 24, TextAnchor.MiddleLeft,
                new Color(0.7f, 0.92f, 1f), new Vector2(58f, -13f), new Vector2(140f, 36f), true);

            Panel(bar.transform, "supplyIcon", TL, TL, new Vector2(206f, -15f), new Vector2(30f, 30f),
                new Color(0.85f, 0.72f, 0.32f, 1f));

            hud.SupplyText = Label(bar.transform, "supplyValue", "3 / 20", 22, TextAnchor.MiddleLeft,
                new Color(1f, 0.9f, 0.65f), new Vector2(244f, -15f), new Vector2(120f, 30f), true);

            hud.TimerText = Label(bar.transform, "timerValue", "0:00", 20, TextAnchor.MiddleRight,
                new Color(0.8f, 0.86f, 0.95f), new Vector2(330f, -15f), new Vector2(88f, 30f), false);
        }

        // ---------- 小地图 ----------

        private static void BuildMiniMap(RtsHUD hud, Transform root)
        {
            GameObject frame = Panel(root, "MiniMapFrame", TR, TR, new Vector2(-16f, -16f), new Vector2(248f, 248f),
                new Color(0.05f, 0.07f, 0.09f, 0.9f));

            GameObject mm = new GameObject("MiniMap", typeof(RectTransform), typeof(RawImage));
            mm.transform.SetParent(frame.transform, false);
            RectTransform rt = mm.GetComponent<RectTransform>();
            rt.anchorMin = TL;
            rt.anchorMax = TL;
            rt.pivot = TL;
            rt.anchoredPosition = new Vector2(4f, -4f);
            rt.sizeDelta = new Vector2(240f, 240f);

            RawImage raw = mm.GetComponent<RawImage>();
            raw.color = Color.white;
            raw.raycastTarget = false;
            hud.MiniMap = raw;
        }

        // ---------- 选中信息 ----------

        private static void BuildSelectionPanel(RtsHUD hud, Transform root)
        {
            GameObject p = Panel(root, "SelectionPanel", BL, BL, new Vector2(16f, 16f), new Vector2(560f, 244f),
                new Color(0.05f, 0.07f, 0.09f, 0.88f));

            Label(p.transform, "selTitle", "SELECTION", 15, TextAnchor.MiddleLeft,
                new Color(0.55f, 0.62f, 0.7f), new Vector2(16f, -8f), new Vector2(200f, 22f), true);

            GameObject swatch = Panel(p.transform, "swatch", TL, TL, new Vector2(16f, -34f), new Vector2(72f, 72f),
                new Color(0.2f, 0.24f, 0.3f, 0.9f));
            hud.SelectionSwatch = swatch.GetComponent<Image>();

            hud.SelectionText = Label(p.transform, "selText", "NO SELECTION", 18, TextAnchor.UpperLeft,
                new Color(0.9f, 0.94f, 1f), new Vector2(102f, -34f), new Vector2(440f, 190f), false);
        }

        // ---------- 命令面板 ----------

        private static void BuildCommandPanel(RtsHUD hud, Transform root)
        {
            GameObject p = Panel(root, "CommandPanel", BR, BR, new Vector2(-16f, 16f), new Vector2(560f, 244f),
                new Color(0.05f, 0.07f, 0.09f, 0.88f));

            Label(p.transform, "cmdTitle", "COMMAND", 15, TextAnchor.MiddleLeft,
                new Color(0.55f, 0.62f, 0.7f), new Vector2(16f, -8f), new Vector2(200f, 22f), true);

            Color[] iconColors =
            {
                new Color(0.3f, 0.75f, 0.45f),
                new Color(0.35f, 0.62f, 0.9f),
                new Color(0.75f, 0.45f, 0.3f),
                new Color(0.55f, 0.5f, 0.85f),
                new Color(0.8f, 0.7f, 0.32f)
            };

            for (int i = 0; i < 5; i++)
            {
                int col = i % 3;
                int row = i / 3;
                Vector2 pos = new Vector2(16f + col * 180f, -34f - row * 106f);
                Vector2 size = new Vector2(168f, 96f);

                Button b = MakeButton(p.transform, "cmd_" + i, pos, size,
                    RtsHUDCommandTitle(i), RtsHUDCommandCost(i).ToString(), (i + 1).ToString(), i, hud, iconColors[i]);

                hud.CmdButtons[i] = b;
                hud.CmdTitles[i] = b.transform.Find("title").GetComponent<Text>();
                hud.CmdCosts[i] = b.transform.Find("cost").GetComponent<Text>();
            }
        }

        private static string RtsHUDCommandTitle(int i)
        {
            string[] t = { "ENGINEER", "RIFLEMAN", "TANK", "BARRACKS", "TURRET" };
            return t[i];
        }

        private static int RtsHUDCommandCost(int i)
        {
            int[] c = { 60, 90, 320, 350, 220 };
            return c[i];
        }

        // ---------- 提示与暂停 ----------

        private static void BuildToast(RtsHUD hud, Transform root)
        {
            GameObject p = Panel(root, "Toast", CT, CT, new Vector2(0f, -96f), new Vector2(620f, 42f),
                new Color(0.06f, 0.09f, 0.13f, 0.82f));

            hud.ToastBg = p.GetComponent<Image>();
            if (hud.ToastBg != null) hud.ToastBg.enabled = false;

            hud.ToastText = Label(p.transform, "toastText", "", 20, TextAnchor.MiddleCenter,
                new Color(0.95f, 0.98f, 1f), Vector2.zero, new Vector2(600f, 40f), false);
            RectTransform rt = hud.ToastText.rectTransform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void BuildPause(RtsHUD hud, Transform root)
        {
            GameObject p = Panel(root, "Pause", CC, CC, Vector2.zero, new Vector2(460f, 90f),
                new Color(0f, 0f, 0f, 0.55f));

            hud.PauseText = Label(p.transform, "pauseText", "PAUSED  (SPACE TO RESUME)", 30, TextAnchor.MiddleCenter,
                new Color(1f, 0.92f, 0.55f), Vector2.zero, new Vector2(440f, 80f), true);
            RectTransform rt = hud.PauseText.rectTransform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            hud.PauseText.gameObject.SetActive(false);
        }

        // ---------- 帮助 ----------

        private static void BuildHelp(RtsHUD hud, Transform root)
        {
            GameObject p = Panel(root, "Help", CC, CC, Vector2.zero, new Vector2(900f, 560f),
                new Color(0.04f, 0.06f, 0.09f, 0.96f));

            Label(p.transform, "helpTitle", "IRON FRONT — CONTROLS", 30, TextAnchor.MiddleCenter,
                new Color(0.85f, 0.92f, 1f), new Vector2(0f, -28f), new Vector2(860f, 44f), true);

            string body =
                "LEFT CLICK            select unit / building\n" +
                "LEFT DRAG             box select friendly units\n" +
                "SHIFT + CLICK         add / remove from selection\n" +
                "RIGHT CLICK           move / attack / harvest\n" +
                "RIGHT CLICK ON OWN HQ set rally point\n\n" +
                "1  PRODUCE ENGINEER        (HQ selected)\n" +
                "2  PRODUCE RIFLEMAN        (Barracks selected)\n" +
                "3  PRODUCE TANK            (Barracks selected)\n" +
                "4  BUILD BARRACKS          (Engineer selected)\n" +
                "5  BUILD TURRET            (Engineer selected)\n\n" +
                "W A S D / ARROWS      pan camera\n" +
                "MOUSE WHEEL           zoom\n" +
                "MIDDLE DRAG           pan camera\n" +
                "HOME                  center on HQ\n" +
                "SPACE                 pause / resume\n" +
                "F1                    toggle this panel\n" +
                "ESC                   cancel placement / clear selection\n\n" +
                "GOAL  destroy the red headquarters.\n" +
                "Engineers harvest crystal and construct buildings.";

            Label(p.transform, "helpBody", body, 19, TextAnchor.UpperLeft,
                new Color(0.82f, 0.88f, 0.96f), new Vector2(40f, -76f), new Vector2(820f, 460f), false);

            hud.HelpPanel = p;
            p.SetActive(false);
        }

        // ---------- 结算 ----------

        private static void BuildEndPanel(RtsHUD hud, Transform root)
        {
            GameObject p = Panel(root, "EndPanel", CC, CC, Vector2.zero, new Vector2(620f, 420f),
                new Color(0.03f, 0.05f, 0.07f, 0.97f));

            hud.EndTitle = Label(p.transform, "endTitle", "VICTORY", 46, TextAnchor.MiddleCenter,
                new Color(0.45f, 0.95f, 0.55f), new Vector2(0f, -40f), new Vector2(560f, 64f), true);

            hud.EndStats = Label(p.transform, "endStats", "", 22, TextAnchor.UpperCenter,
                new Color(0.85f, 0.9f, 0.96f), new Vector2(0f, -130f), new Vector2(520f, 160f), false);

            Button restart = MakeButton(p.transform, "restart", new Vector2(220f, -320f), new Vector2(180f, 60f),
                "RESTART", "", "", -1, hud, new Color(0.28f, 0.55f, 0.35f));
            restart.onClick.RemoveAllListeners();
            restart.onClick.AddListener(delegate
            {
                if (RtsGame.I != null) RtsGame.I.RestartLevel();
            });

            hud.EndPanel = p;
            p.SetActive(false);
        }

        // ---------- 基础控件 ----------

        private static GameObject Panel(Transform parent, string name, Vector2 anchor, Vector2 pivot,
            Vector2 pos, Vector2 size, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            Image img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;

            // 大面板套九宫格皮肤（小色块如色板/图标保持纯色）
            if (size.x >= 96f && size.y >= 54f && RtsUiSkin.ApplyPanel(img))
            {
                img.color = new Color(1f, 1f, 1f, color.a);
            }

            return go;
        }

        private static Text Label(Transform parent, string name, string text, int fontSize,
            TextAnchor align, Color color, Vector2 pos, Vector2 size, bool bold)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = TL;
            rt.anchorMax = TL;
            rt.pivot = TL;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            Text t = go.GetComponent<Text>();
            t.font = Font;
            t.fontSize = fontSize;
            t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            t.text = text;
            t.color = color;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        private static Button MakeButton(Transform parent, string name, Vector2 pos, Vector2 size,
            string title, string cost, string key, int idx, RtsHUD hud, Color iconColor)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = TL;
            rt.anchorMax = TL;
            rt.pivot = TL;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            Image bg = go.GetComponent<Image>();
            bg.color = RtsUiSkin.ApplyButton(bg)
                ? Color.white
                : new Color(0.14f, 0.18f, 0.23f, 1f);

            Button b = go.GetComponent<Button>();
            b.targetGraphic = bg;

            ColorBlock cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.3f, 1.35f, 1f);
            cb.pressedColor = new Color(0.8f, 0.85f, 0.9f, 1f);
            cb.selectedColor = Color.white;
            cb.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            b.colors = cb;
            b.transition = Selectable.Transition.ColorTint;

            GameObject iconGo = Panel(go.transform, "icon", TL, TL,
                new Vector2(10f, -10f), new Vector2(34f, 34f), iconColor);
            Image iconImg = iconGo.GetComponent<Image>();
            Sprite iconSprite = RtsUiSkin.Icon(idx);
            if (iconSprite != null)
            {
                iconImg.sprite = iconSprite;
                iconImg.preserveAspect = true;
                iconImg.color = Color.white;
            }

            Label(go.transform, "title", title, 17, TextAnchor.UpperLeft,
                new Color(0.92f, 0.95f, 1f), new Vector2(10f, -48f), new Vector2(148f, 22f), true);
            Label(go.transform, "cost", cost, 16, TextAnchor.UpperLeft,
                new Color(1f, 0.85f, 0.35f), new Vector2(10f, -70f), new Vector2(92f, 22f), false);
            Label(go.transform, "key", key, 15, TextAnchor.UpperRight,
                new Color(0.55f, 0.62f, 0.7f), new Vector2(122f, -70f), new Vector2(36f, 22f), true);

            if (idx >= 0)
            {
                int captured = idx;
                b.onClick.AddListener(delegate { hud.CommandFromButton(captured); });
            }

            return b;
        }
    }
}

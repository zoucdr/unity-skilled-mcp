using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace IronFront
{
    /// <summary>世界空间 UI 叠加：血条、框选矩形。全部用屏幕坐标投影，不建 World Space Canvas。</summary>
    public class RtsWorldOverlay : MonoBehaviour
    {
        public Camera Cam;
        public RtsInput InputCtl;
        public Canvas Canvas;
        public Image SelectionBox;
        public Image[] Bars = new Image[160];
        public Image[] Fills = new Image[160];

        private const int MaxBars = 160;

        public void Init(Camera cam, RtsInput input)
        {
            Cam = cam;
            InputCtl = input;
            BuildUI();
        }

        private void BuildUI()
        {
            GameObject canvasGo = new GameObject("WorldOverlay_Canvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);

            Canvas cv = canvasGo.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 5;
            Canvas = cv;

            CanvasScaler cs = canvasGo.GetComponent<CanvasScaler>();
            cs.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            cs.scaleFactor = 1f;

            Transform root = canvasGo.transform;

            GameObject box = NewImage(root, "SelectionBox", new Color(0.35f, 0.65f, 1f, 0.18f));
            SelectionBox = box.GetComponent<Image>();
            SelectionBox.rectTransform.pivot = new Vector2(0f, 0f);
            SelectionBox.gameObject.SetActive(false);

            for (int i = 0; i < MaxBars; i++)
            {
                GameObject bg = NewImage(root, "hp_bg_" + i, new Color(0.06f, 0.08f, 0.1f, 0.85f));
                Image bgImg = bg.GetComponent<Image>();
                bgImg.rectTransform.sizeDelta = new Vector2(42f, 7f);
                bgImg.gameObject.SetActive(false);

                GameObject fill = NewImage(bg.transform, "hp_fill", new Color(0.35f, 0.95f, 0.45f, 1f));
                Image fillImg = fill.GetComponent<Image>();
                fillImg.rectTransform.anchorMin = new Vector2(0f, 0.5f);
                fillImg.rectTransform.anchorMax = new Vector2(0f, 0.5f);
                fillImg.rectTransform.pivot = new Vector2(0f, 0.5f);
                fillImg.rectTransform.anchoredPosition = new Vector2(1f, 0f);
                fillImg.rectTransform.sizeDelta = new Vector2(40f, 5f);

                Bars[i] = bgImg;
                Fills[i] = fillImg;
            }
        }

        private static GameObject NewImage(Transform parent, string name, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(42f, 7f);

            Image img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return go;
        }

        private void LateUpdate()
        {
            if (Cam == null || RtsGame.I == null) return;

            DrawBars();
            DrawSelectionBox();
        }

        private void DrawBars()
        {
            RtsGame g = RtsGame.I;
            int slot = 0;

            for (int i = 0; i < g.Units.Count && slot < MaxBars; i++)
            {
                RtsUnit u = g.Units[i];
                if (u == null || !u.IsAlive) continue;
                slot = DrawOne(u, slot);
            }

            for (int i = 0; i < g.Buildings.Count && slot < MaxBars; i++)
            {
                RtsBuilding b = g.Buildings[i];
                if (b == null || !b.IsAlive) continue;
                slot = DrawOne(b, slot);
            }

            for (int i = slot; i < MaxBars; i++)
            {
                if (Bars[i] != null && Bars[i].gameObject.activeSelf) Bars[i].gameObject.SetActive(false);
            }
        }

        private int DrawOne(RtsSelectable target, int slot)
        {
            Vector3 world = target.transform.position + Vector3.up * target.BarHeight;
            Vector3 sp = Cam.WorldToScreenPoint(world);

            bool show = sp.z > 0f && (target.IsSelected || target.HpRatio < 0.995f);
            Image bar = Bars[slot];
            if (bar == null) return slot;

            if (!show)
            {
                if (bar.gameObject.activeSelf) bar.gameObject.SetActive(false);
                return slot;
            }

            if (!bar.gameObject.activeSelf) bar.gameObject.SetActive(true);
            bar.rectTransform.position = new Vector3(sp.x, sp.y, 0f);

            Image fill = Fills[slot];
            if (fill != null)
            {
                float w = 40f * Mathf.Clamp01(target.HpRatio);
                fill.rectTransform.sizeDelta = new Vector2(w, 5f);
                fill.color = target.Team == Team.Red
                    ? new Color(0.95f, 0.35f, 0.3f, 1f)
                    : new Color(0.35f, 0.95f, 0.45f, 1f);
            }

            return slot + 1;
        }

        private void DrawSelectionBox()
        {
            if (SelectionBox == null) return;

            if (InputCtl == null || !InputCtl.IsBoxSelecting)
            {
                if (SelectionBox.gameObject.activeSelf) SelectionBox.gameObject.SetActive(false);
                return;
            }

            Rect r = InputCtl.DragRect;
            if (r.width < 2f || r.height < 2f)
            {
                SelectionBox.gameObject.SetActive(false);
                return;
            }

            SelectionBox.gameObject.SetActive(true);
            SelectionBox.rectTransform.position = new Vector3(r.x, r.y, 0f);
            SelectionBox.rectTransform.sizeDelta = new Vector2(r.width, r.height);
        }
    }
}

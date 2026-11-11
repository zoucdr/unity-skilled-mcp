using UnityEngine;
using UnityEngine.UI;

namespace IronFront
{
    /// <summary>UI 皮肤：读取 Resources 下的程序化九宫格图与图标，运行时构建带 border 的 Sprite。缺图时整条链路自动退回纯色。</summary>
    public static class RtsUiSkin
    {
        private const string Dir = "RTS/UI/";

        private static bool _loaded;
        private static Sprite _panel;
        private static Sprite _button;
        private static Sprite[] _icons;

        private static readonly string[] IconNames =
        {
            "Icon_Engineer", "Icon_Rifleman", "Icon_Tank", "Icon_Barracks", "Icon_Turret"
        };

        private static Sprite Load(string name, float borderRatio)
        {
            Texture2D t = Resources.Load<Texture2D>(Dir + name + "/" + name);
            if (t == null) return null;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;

            Vector4 border = Vector4.zero;
            if (borderRatio > 0f)
            {
                float b = Mathf.Round(Mathf.Min(t.width, t.height) * borderRatio);
                border = new Vector4(b, b, b, b);
            }

            return Sprite.Create(t, new Rect(0f, 0f, t.width, t.height), new Vector2(0.5f, 0.5f),
                100f, 0u, SpriteMeshType.FullRect, border);
        }

        private static void Ensure()
        {
            if (_loaded) return;
            _loaded = true;
            _panel = Load("T_Panel", 0.13f);
            _button = Load("T_Button", 0.15f);

            _icons = new Sprite[IconNames.Length];
            for (int i = 0; i < IconNames.Length; i++) _icons[i] = Load(IconNames[i], 0f);
        }

        public static Sprite Panel
        {
            get { Ensure(); return _panel; }
        }

        public static Sprite Button
        {
            get { Ensure(); return _button; }
        }

        public static Sprite Icon(int index)
        {
            Ensure();
            if (_icons == null || index < 0 || index >= _icons.Length) return null;
            return _icons[index];
        }

        /// <summary>给 Image 套九宫格皮肤；贴图缺失时保留原色。</summary>
        public static bool ApplyPanel(Image img)
        {
            if (img == null) return false;
            Sprite s = Panel;
            if (s == null) return false;
            img.sprite = s;
            img.type = Image.Type.Sliced;
            img.preserveAspect = false;
            return true;
        }

        public static bool ApplyButton(Image img)
        {
            if (img == null) return false;
            Sprite s = Button;
            if (s == null) return false;
            img.sprite = s;
            img.type = Image.Type.Sliced;
            img.preserveAspect = false;
            return true;
        }
    }
}

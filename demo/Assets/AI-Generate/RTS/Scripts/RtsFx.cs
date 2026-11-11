using UnityEngine;

namespace IronFront
{
    /// <summary>轻量特效：曳光、命中火花、命令落点波纹、爆炸。全部运行时生成，无预制体。</summary>
    public static class RtsFx
    {
        private static Material _lineBlue;
        private static Material _lineRed;
        private static Material _lineGreen;

        private static Material LineMat(Color c)
        {
            if (c.b > 0.8f && c.r < 0.6f)
            {
                if (_lineBlue == null) _lineBlue = MakeLine("M_LineBlue", c);
                return _lineBlue;
            }
            if (c.r > 0.8f && c.g < 0.6f)
            {
                if (_lineRed == null) _lineRed = MakeLine("M_LineRed", c);
                return _lineRed;
            }
            if (_lineGreen == null) _lineGreen = MakeLine("M_LineGreen", c);
            return _lineGreen;
        }

        private static Material MakeLine(string name, Color c)
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            Material m = new Material(sh);
            m.name = name;
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            return m;
        }

        public static void Tracer(Vector3 a, Vector3 b, Team team)
        {
            GameObject go = new GameObject("fx_tracer");
            LineRenderer lr = go.AddComponent<LineRenderer>();
            Color c = team == Team.Red ? new Color(1f, 0.66f, 0.28f) : new Color(0.55f, 0.85f, 1f);
            lr.positionCount = 2;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            lr.startWidth = 0.075f;
            lr.endWidth = 0.02f;
            lr.material = LineMat(c);
            lr.startColor = c;
            lr.endColor = new Color(c.r, c.g, c.b, 0.15f);
            lr.useWorldSpace = true;
            Object.Destroy(go, 0.09f);

            GameObject spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            spark.name = "fx_spark";
            Collider sc = spark.GetComponent<Collider>();
            if (sc != null) Object.Destroy(sc);
            spark.transform.position = b;
            spark.transform.localScale = Vector3.one * 0.22f;
            Renderer r = spark.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = LineMat(c);
            Object.Destroy(spark, 0.1f);
        }

        public static void Ring(Vector3 pos, Color color, float radius, float duration)
        {
            GameObject go = new GameObject("fx_ring");
            go.transform.position = pos + Vector3.up * 0.05f;
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.positionCount = 32;
            for (int i = 0; i < 32; i++)
            {
                float a = i / 32f * Mathf.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
            }
            lr.startWidth = 0.09f;
            lr.endWidth = 0.09f;
            lr.material = LineMat(color);
            lr.startColor = color;
            lr.endColor = color;

            RtsFxRing ring = go.AddComponent<RtsFxRing>();
            ring.Life = duration;
        }

        public static void Explosion(Vector3 pos, float scale)
        {
            for (int i = 0; i < 6; i++)
            {
                GameObject p = GameObject.CreatePrimitive(PrimitiveType.Cube);
                p.name = "fx_debris";
                Collider c = p.GetComponent<Collider>();
                if (c != null) Object.Destroy(c);
                p.transform.position = pos + new Vector3(Random.Range(-0.4f, 0.4f), Random.Range(0.2f, 0.9f), Random.Range(-0.4f, 0.4f)) * scale;
                p.transform.localScale = Vector3.one * Random.Range(0.12f, 0.28f) * scale;
                Renderer r = p.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = LineMat(new Color(1f, 0.55f, 0.2f));
                RtsFxDebris d = p.AddComponent<RtsFxDebris>();
                d.Vel = new Vector3(Random.Range(-2.5f, 2.5f), Random.Range(2f, 4.5f), Random.Range(-2.5f, 2.5f));
            }
        }
    }

    public class RtsFxRing : MonoBehaviour
    {
        public float Life = 0.5f;
        private float _t;

        private void Update()
        {
            _t += Time.deltaTime;
            float k = Mathf.Clamp01(_t / Mathf.Max(0.01f, Life));
            transform.localScale = Vector3.one * Mathf.Lerp(1f, 1.6f, k);
            LineRenderer lr = GetComponent<LineRenderer>();
            if (lr != null)
            {
                Color c = lr.startColor;
                c.a = 1f - k;
                lr.startColor = c;
                lr.endColor = c;
            }
            if (_t >= Life) Destroy(gameObject);
        }
    }

    public class RtsFxDebris : MonoBehaviour
    {
        public Vector3 Vel;
        private float _t;

        private void Update()
        {
            _t += Time.deltaTime;
            Vel.y -= 12f * Time.deltaTime;
            transform.position += Vel * Time.deltaTime;
            if (_t > 0.9f) Destroy(gameObject);
        }
    }
}

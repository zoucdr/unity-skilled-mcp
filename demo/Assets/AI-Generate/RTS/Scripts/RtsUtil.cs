using System.Collections.Generic;
using UnityEngine;

namespace IronFront
{
    public static class RtsUtil
    {
        public static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>屏幕点投射到 y=0 平面，避免依赖地面 Collider。</summary>
        public static bool ScreenToGround(Camera cam, Vector3 screenPos, out Vector3 world)
        {
            world = Vector3.zero;
            if (cam == null) return false;

            Ray r = cam.ScreenPointToRay(screenPos);
            if (Mathf.Abs(r.direction.y) < 0.00001f) return false;

            float t = -r.origin.y / r.direction.y;
            if (t <= 0f) return false;

            world = r.origin + r.direction * t;
            world.y = 0f;
            return true;
        }

        /// <summary>黄金角散开，让多单位不挤在同一个点。</summary>
        public static List<Vector3> Spread(Vector3 center, int n, float spacing)
        {
            List<Vector3> pts = new List<Vector3>();
            if (n <= 0) return pts;
            if (n == 1)
            {
                pts.Add(center);
                return pts;
            }

            float r = spacing * Mathf.Sqrt(n) * 0.55f;
            for (int i = 0; i < n; i++)
            {
                float ang = i * 2.39996323f;
                float rad = r * Mathf.Sqrt((i + 0.5f) / n);
                pts.Add(center + new Vector3(Mathf.Cos(ang) * rad, 0f, Mathf.Sin(ang) * rad));
            }
            return pts;
        }

        public static string MmSs(float seconds)
        {
            int s = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return (s / 60) + ":" + (s % 60).ToString("00");
        }
    }
}

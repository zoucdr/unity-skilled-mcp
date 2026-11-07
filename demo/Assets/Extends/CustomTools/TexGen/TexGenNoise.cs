using UnityEngine;

namespace UniMcp.TexGen
{
    /// <summary>
    /// Deterministic, dependency-free noise used by procedural fills.
    /// Every function is a pure function of its coordinates and seed, so the same
    /// instruction list always rasterises to the same pixels on any machine.
    ///
    /// The <c>*T</c> overloads take a lattice period. A non-zero period makes the
    /// hash wrap at that many cells, which is what turns a noise field into a
    /// seamlessly tiling one: the left edge and the right edge share lattice
    /// points, so the border is continuous instead of merely similar.
    /// </summary>
    public static class TexNoise
    {
        private const uint K1 = 0x9E3779B1u;
        private const uint K2 = 0x85EBCA6Bu;
        private const uint K3 = 0xC2B2AE35u;
        private const uint K4 = 0x27D4EB2Fu;

        /// <summary>32-bit integer hash of a lattice point.</summary>
        public static int HashInt(int x, int y, int seed)
        {
            uint h = unchecked((uint)seed);
            h ^= unchecked((uint)x) * K1;
            h = (h ^ (h >> 15)) * K2;
            h ^= unchecked((uint)y) * K3;
            h = (h ^ (h >> 13)) * K4;
            h ^= h >> 16;
            return unchecked((int)h);
        }

        /// <summary>Hash mapped to [0, 1].</summary>
        public static float Hash01(int x, int y, int seed)
        {
            uint h = unchecked((uint)HashInt(x, y, seed));
            return (h & 0x00FFFFFFu) / 16777215f;
        }

        /// <summary>Smoothstep for lattice interpolation.</summary>
        public static float Smooth(float t)
        {
            return t * t * (3f - 2f * t);
        }

        /// <summary>Positive modulo; a non-positive period means "do not wrap".</summary>
        public static int WrapIndex(int value, int period)
        {
            if (period <= 0) return value;
            int m = value % period;
            return m < 0 ? m + period : m;
        }

        /// <summary>Value noise in [0, 1].</summary>
        public static float Value(float x, float y, int seed)
        {
            return ValueT(x, y, seed, 0, 0);
        }

        /// <summary>Value noise whose lattice wraps every <paramref name="periodX"/> x <paramref name="periodY"/> cells.</summary>
        public static float ValueT(float x, float y, int seed, int periodX, int periodY)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float xf = x - xi;
            float yf = y - yi;
            float u = Smooth(xf);
            float v = Smooth(yf);

            int x0 = WrapIndex(xi, periodX);
            int x1 = WrapIndex(xi + 1, periodX);
            int y0 = WrapIndex(yi, periodY);
            int y1 = WrapIndex(yi + 1, periodY);

            float a = Hash01(x0, y0, seed);
            float b = Hash01(x1, y0, seed);
            float c = Hash01(x0, y1, seed);
            float d = Hash01(x1, y1, seed);

            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        /// <summary>Classic gradient (Perlin-style) noise in [0, 1].</summary>
        public static float Perlin(float x, float y, int seed)
        {
            return PerlinT(x, y, seed, 0, 0);
        }

        /// <summary>Gradient noise with a wrapping lattice.</summary>
        public static float PerlinT(float x, float y, int seed, int periodX, int periodY)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float xf = x - xi;
            float yf = y - yi;
            float u = Smooth(xf);
            float v = Smooth(yf);

            int x0 = WrapIndex(xi, periodX);
            int x1 = WrapIndex(xi + 1, periodX);
            int y0 = WrapIndex(yi, periodY);
            int y1 = WrapIndex(yi + 1, periodY);

            float n00 = Grad(HashInt(x0, y0, seed), xf, yf);
            float n10 = Grad(HashInt(x1, y0, seed), xf - 1f, yf);
            float n01 = Grad(HashInt(x0, y1, seed), xf, yf - 1f);
            float n11 = Grad(HashInt(x1, y1, seed), xf - 1f, yf - 1f);

            float n = Mathf.Lerp(Mathf.Lerp(n00, n10, u), Mathf.Lerp(n01, n11, u), v);
            return Mathf.Clamp01(n * 0.7071f + 0.5f);
        }

        private static float Grad(int hash, float x, float y)
        {
            // Eight evenly spaced gradient directions.
            switch (hash & 7)
            {
                case 0: return x + y;
                case 1: return x - y;
                case 2: return -x + y;
                case 3: return -x - y;
                case 4: return x;
                case 5: return -x;
                case 6: return y;
                default: return -y;
            }
        }

        /// <summary>Fractal sum of value noise in [0, 1].</summary>
        public static float Fbm(float x, float y, int seed, int octaves,
            float lacunarity = 2f, float gain = 0.5f)
        {
            return FbmT(x, y, seed, octaves, 0, 0, lacunarity, gain);
        }

        /// <summary>Fractal value noise that stays periodic under the given lattice periods.</summary>
        public static float FbmT(float x, float y, int seed, int octaves, int periodX, int periodY,
            float lacunarity = 2f, float gain = 0.5f)
        {
            int oct = Mathf.Clamp(octaves, 1, 10);
            float sum = 0f, amp = 1f, norm = 0f;
            float fx = x, fy = y;
            int px = periodX, py = periodY;
            for (int i = 0; i < oct; i++)
            {
                sum += amp * ValueT(fx, fy, unchecked(seed + i * 131), px, py);
                norm += amp;
                fx *= lacunarity;
                fy *= lacunarity;
                if (px > 0) px = Mathf.Max(1, Mathf.RoundToInt(px * lacunarity));
                if (py > 0) py = Mathf.Max(1, Mathf.RoundToInt(py * lacunarity));
                amp *= gain;
            }
            return norm > 0f ? Mathf.Clamp01(sum / norm) : 0f;
        }

        /// <summary>Fractal sum of gradient noise in [0, 1].</summary>
        public static float FbmPerlin(float x, float y, int seed, int octaves,
            float lacunarity = 2f, float gain = 0.5f)
        {
            int oct = Mathf.Clamp(octaves, 1, 10);
            float sum = 0f, amp = 1f, norm = 0f;
            float fx = x, fy = y;
            for (int i = 0; i < oct; i++)
            {
                sum += amp * Perlin(fx, fy, unchecked(seed + i * 197));
                norm += amp;
                fx *= lacunarity;
                fy *= lacunarity;
                amp *= gain;
            }
            return norm > 0f ? Mathf.Clamp01(sum / norm) : 0f;
        }

        /// <summary>Fractal gradient noise that stays periodic under the given lattice periods.</summary>
        public static float FbmPerlinT(float x, float y, int seed, int octaves, int periodX, int periodY,
            float lacunarity = 2f, float gain = 0.5f)
        {
            int oct = Mathf.Clamp(octaves, 1, 10);
            float sum = 0f, amp = 1f, norm = 0f;
            float fx = x, fy = y;
            int px = periodX, py = periodY;
            for (int i = 0; i < oct; i++)
            {
                sum += amp * PerlinT(fx, fy, unchecked(seed + i * 197), px, py);
                norm += amp;
                fx *= lacunarity;
                fy *= lacunarity;
                if (px > 0) px = Mathf.Max(1, Mathf.RoundToInt(px * lacunarity));
                if (py > 0) py = Mathf.Max(1, Mathf.RoundToInt(py * lacunarity));
                amp *= gain;
            }
            return norm > 0f ? Mathf.Clamp01(sum / norm) : 0f;
        }

        /// <summary>Ridged fractal sum in [0, 1] (billowy, vein-like).</summary>
        public static float Ridged(float x, float y, int seed, int octaves)
        {
            return RidgedT(x, y, seed, octaves, 0, 0);
        }

        /// <summary>Ridged fractal with a wrapping lattice.</summary>
        public static float RidgedT(float x, float y, int seed, int octaves, int periodX, int periodY)
        {
            int oct = Mathf.Clamp(octaves, 1, 10);
            float sum = 0f, amp = 1f, norm = 0f;
            float fx = x, fy = y;
            int px = periodX, py = periodY;
            for (int i = 0; i < oct; i++)
            {
                float v = 1f - Mathf.Abs(ValueT(fx, fy, unchecked(seed + i * 313), px, py) * 2f - 1f);
                sum += amp * v * v;
                norm += amp;
                fx *= 2f;
                fy *= 2f;
                if (px > 0) px = Mathf.Max(1, px * 2);
                if (py > 0) py = Mathf.Max(1, py * 2);
                amp *= 0.5f;
            }
            return norm > 0f ? Mathf.Clamp01(sum / norm) : 0f;
        }

        /// <summary>Cellular (Worley) noise: distance to the nearest feature point, in [0, 1].</summary>
        public static float Worley(float x, float y, int seed)
        {
            return WorleyT(x, y, seed, 0, 0);
        }

        /// <summary>Cellular noise with a wrapping cell lattice.</summary>
        public static float WorleyT(float x, float y, int seed, int periodX, int periodY)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float best = float.MaxValue;

            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    int cx = xi + ox;
                    int cy = yi + oy;
                    int wx = WrapIndex(cx, periodX);
                    int wy = WrapIndex(cy, periodY);
                    float px = Hash01(wx, wy, unchecked(seed * 7 + 1));
                    float py = Hash01(wx, wy, unchecked(seed * 7 + 2));
                    float dx = cx + px - x;
                    float dy = cy + py - y;
                    float d = dx * dx + dy * dy;
                    if (d < best) best = d;
                }
            }

            return Mathf.Clamp01(Mathf.Sqrt(best));
        }

        /// <summary>Second-nearest cellular distance minus nearest; high on cell borders.</summary>
        public static float WorleyEdgeT(float x, float y, int seed, int periodX, int periodY)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float best = float.MaxValue;
            float second = float.MaxValue;

            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    int cx = xi + ox;
                    int cy = yi + oy;
                    int wx = WrapIndex(cx, periodX);
                    int wy = WrapIndex(cy, periodY);
                    float px = Hash01(wx, wy, unchecked(seed * 7 + 1));
                    float py = Hash01(wx, wy, unchecked(seed * 7 + 2));
                    float dx = cx + px - x;
                    float dy = cy + py - y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d < best) { second = best; best = d; }
                    else if (d < second) { second = d; }
                }
            }

            return Mathf.Clamp01(second - best);
        }
    }
}

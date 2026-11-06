using UnityEngine;

namespace UniMcp.TexGen
{
    /// <summary>
    /// Deterministic, dependency-free noise used by procedural fills.
    /// Every function is a pure function of its coordinates and seed, so the same
    /// instruction list always rasterises to the same pixels on any machine.
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

        /// <summary>Value noise in [0, 1].</summary>
        public static float Value(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float xf = x - xi;
            float yf = y - yi;
            float u = Smooth(xf);
            float v = Smooth(yf);

            float a = Hash01(xi, yi, seed);
            float b = Hash01(xi + 1, yi, seed);
            float c = Hash01(xi, yi + 1, seed);
            float d = Hash01(xi + 1, yi + 1, seed);

            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        /// <summary>Classic gradient (Perlin-style) noise in [0, 1].</summary>
        public static float Perlin(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float xf = x - xi;
            float yf = y - yi;
            float u = Smooth(xf);
            float v = Smooth(yf);

            float n00 = Grad(HashInt(xi, yi, seed), xf, yf);
            float n10 = Grad(HashInt(xi + 1, yi, seed), xf - 1f, yf);
            float n01 = Grad(HashInt(xi, yi + 1, seed), xf, yf - 1f);
            float n11 = Grad(HashInt(xi + 1, yi + 1, seed), xf - 1f, yf - 1f);

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
            int oct = Mathf.Clamp(octaves, 1, 10);
            float sum = 0f, amp = 1f, norm = 0f;
            float fx = x, fy = y;
            for (int i = 0; i < oct; i++)
            {
                sum += amp * Value(fx, fy, unchecked(seed + i * 131));
                norm += amp;
                fx *= lacunarity;
                fy *= lacunarity;
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

        /// <summary>Ridged fractal sum in [0, 1] (billowy, vein-like).</summary>
        public static float Ridged(float x, float y, int seed, int octaves)
        {
            int oct = Mathf.Clamp(octaves, 1, 10);
            float sum = 0f, amp = 1f, norm = 0f;
            float fx = x, fy = y;
            for (int i = 0; i < oct; i++)
            {
                float v = 1f - Mathf.Abs(Value(fx, fy, unchecked(seed + i * 313)) * 2f - 1f);
                sum += amp * v * v;
                norm += amp;
                fx *= 2f;
                fy *= 2f;
                amp *= 0.5f;
            }
            return norm > 0f ? Mathf.Clamp01(sum / norm) : 0f;
        }

        /// <summary>Cellular (Worley) noise: distance to the nearest feature point, in [0, 1].</summary>
        public static float Worley(float x, float y, int seed)
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
                    float px = Hash01(cx, cy, unchecked(seed * 7 + 1));
                    float py = Hash01(cx, cy, unchecked(seed * 7 + 2));
                    float dx = cx + px - x;
                    float dy = cy + py - y;
                    float d = dx * dx + dy * dy;
                    if (d < best) best = d;
                }
            }

            return Mathf.Clamp01(Mathf.Sqrt(best));
        }
    }
}

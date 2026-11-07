using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UniMcp.TexGen
{
    /// <summary>One paintable area of a mesh's UV space (a submesh, an island, or the whole mesh).</summary>
    public sealed class TgUvRegion
    {
        public int Id;
        public string Name;
        public int Triangles;
        public Vector2 UvMin;
        public Vector2 UvMax;

        /// <summary>Rectangle in texgen canvas pixels, derived from UvMin/UvMax. Ready to use in layer geometry.</summary>
        public int PxX;
        public int PxY;
        public int PxW;
        public int PxH;
        public int CoveredPixels;
    }

    /// <summary>Rasterised UV coverage of a mesh at a fixed canvas size.</summary>
    public sealed class TgUvLayout
    {
        public int Width;
        public int Height;

        /// <summary>Row-major, y=0 is the first texgen canvas row. -1 = not covered by any triangle.</summary>
        public int[] Mask;

        public readonly List<TgUvRegion> Regions = new List<TgUvRegion>();
        public readonly List<string> Warnings = new List<string>();

        public int VertexCount;
        public int TriangleCount;
        public int SubmeshCount;
        public int CoveredPixels;
        public Vector2 UvMin;
        public Vector2 UvMax;
        public bool OutOfRange;

        public float CoverageRatio => (float)CoveredPixels / Mathf.Max(1, Width * Height);
    }

    /// <summary>
    /// UV-space analysis and composition for texgen.
    ///
    /// Pure CPU: reads a Unity Mesh, rasterises its UV triangles into a coverage
    /// mask, groups them into paintable regions, and composites one rendered
    /// instruction list per region. The existing TgRasterizer is untouched —
    /// each region still renders through the ordinary 2D canvas pipeline.
    /// </summary>
    public static class TgMeshUv
    {
        public const int MaxDimension = 2048;

        private struct Tri
        {
            public int Submesh;
            public int A, B, C;
        }

        /// <summary>
        /// Analyse a mesh and rasterise its UV triangles.
        /// Pixel (x, y) maps from UV (u, v) as x = u * width, y = v * height,
        /// matching the row-major canvas used by TgRasterizer (first row first).
        /// </summary>
        public static TgUvLayout Analyze(Mesh mesh, int width, int height, string groupBy)
        {
            if (mesh == null) throw new ArgumentException("Mesh is null.");
            width = Mathf.Clamp(width, 1, MaxDimension);
            height = Mathf.Clamp(height, 1, MaxDimension);

            Vector2[] uv;
            try
            {
                uv = mesh.uv;
            }
            catch (Exception e)
            {
                throw new ArgumentException("Cannot read mesh UVs: " + e.Message +
                    " (enable Read/Write on the model importer if this is an imported mesh)");
            }

            var layout = new TgUvLayout
            {
                Width = width,
                Height = height,
                VertexCount = mesh.vertexCount,
                SubmeshCount = mesh.subMeshCount,
                Mask = new int[width * height]
            };
            for (int i = 0; i < layout.Mask.Length; i++) layout.Mask[i] = -1;

            if (uv == null || uv.Length == 0)
                throw new ArgumentException("Mesh has no UV channel 0. Generate UVs in the model importer first.");
            if (uv.Length < mesh.vertexCount)
                throw new ArgumentException("UV array is shorter than the vertex array (" +
                    uv.Length + " < " + mesh.vertexCount + ").");

            var tris = new List<Tri>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] indices;
                try { indices = mesh.GetTriangles(s); }
                catch (Exception e) { throw new ArgumentException("Cannot read submesh " + s + ": " + e.Message); }

                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    tris.Add(new Tri { Submesh = s, A = indices[i], B = indices[i + 1], C = indices[i + 2] });
                }
            }
            layout.TriangleCount = tris.Count;
            if (tris.Count == 0) throw new ArgumentException("Mesh has no triangles.");

            string mode = string.IsNullOrEmpty(groupBy) ? "submesh" : groupBy.ToLowerInvariant();
            if (mode != "submesh" && mode != "island" && mode != "all")
                throw new ArgumentException("group_by must be submesh, island or all.");

            int[] regionOfTri = BuildRegions(tris, uv, mode, layout.SubmeshCount, out int regionCount);
            string prefix = mode == "submesh" ? "submesh_" : mode == "island" ? "island_" : "uv_";

            var uvMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var uvMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            var regionMin = new Vector2[regionCount];
            var regionMax = new Vector2[regionCount];
            var regionTris = new int[regionCount];
            for (int r = 0; r < regionCount; r++)
            {
                regionMin[r] = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                regionMax[r] = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            }

            for (int t = 0; t < tris.Count; t++)
            {
                int r = regionOfTri[t];
                regionTris[r]++;
                Tri tri = tris[t];
                Accumulate(uv[tri.A], ref regionMin[r], ref regionMax[r], ref uvMin, ref uvMax);
                Accumulate(uv[tri.B], ref regionMin[r], ref regionMax[r], ref uvMin, ref uvMax);
                Accumulate(uv[tri.C], ref regionMin[r], ref regionMax[r], ref uvMin, ref uvMax);

                RasterizeTriangle(layout.Mask, width, height,
                    uv[tri.A], uv[tri.B], uv[tri.C], r);
            }

            layout.UvMin = uvMin;
            layout.UvMax = uvMax;
            layout.OutOfRange = uvMin.x < -1e-4f || uvMin.y < -1e-4f || uvMax.x > 1.0001f || uvMax.y > 1.0001f;
            if (layout.OutOfRange)
                layout.Warnings.Add("UVs extend outside [0,1]; out-of-range triangles are clipped. " +
                    "Use a tiled/wrap material or re-unwrap the model.");

            var coveredPerRegion = new int[regionCount];
            for (int i = 0; i < layout.Mask.Length; i++)
            {
                int r = layout.Mask[i];
                if (r >= 0) { coveredPerRegion[r]++; layout.CoveredPixels++; }
            }

            for (int r = 0; r < regionCount; r++)
            {
                Vector2 mn = regionMin[r];
                Vector2 mx = regionMax[r];
                if (float.IsInfinity(mn.x)) { mn = Vector2.zero; mx = Vector2.zero; }

                int px = Mathf.Clamp(Mathf.FloorToInt(mn.x * width), 0, width - 1);
                int py = Mathf.Clamp(Mathf.FloorToInt(mn.y * height), 0, height - 1);
                int px2 = Mathf.Clamp(Mathf.CeilToInt(mx.x * width), 0, width);
                int py2 = Mathf.Clamp(Mathf.CeilToInt(mx.y * height), 0, height);

                layout.Regions.Add(new TgUvRegion
                {
                    Id = r,
                    Name = prefix + r,
                    Triangles = regionTris[r],
                    UvMin = mn,
                    UvMax = mx,
                    PxX = px,
                    PxY = py,
                    PxW = Mathf.Max(1, px2 - px),
                    PxH = Mathf.Max(1, py2 - py),
                    CoveredPixels = coveredPerRegion[r]
                });
            }

            if (layout.CoveredPixels == 0)
                layout.Warnings.Add("No pixel was covered. Check the UV channel and increase width/height.");

            return layout;
        }

        private static void Accumulate(Vector2 p, ref Vector2 rMin, ref Vector2 rMax,
            ref Vector2 gMin, ref Vector2 gMax)
        {
            rMin = Vector2.Min(rMin, p);
            rMax = Vector2.Max(rMax, p);
            gMin = Vector2.Min(gMin, p);
            gMax = Vector2.Max(gMax, p);
        }

        /// <summary>Assign a region id to every triangle.</summary>
        private static int[] BuildRegions(List<Tri> tris, Vector2[] uv, string mode,
            int submeshCount, out int regionCount)
        {
            var ids = new int[tris.Count];

            if (mode == "all")
            {
                regionCount = 1;
                return ids;
            }

            if (mode == "submesh")
            {
                regionCount = Mathf.Max(1, submeshCount);
                for (int t = 0; t < tris.Count; t++) ids[t] = tris[t].Submesh;
                return ids;
            }

            // UV-connected islands: union triangles sharing an edge whose two
            // endpoints have the same vertex index AND the same UV (seams split).
            var dsu = new DisjointSet(tris.Count);
            var edges = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int t = 0; t < tris.Count; t++)
            {
                Tri tri = tris[t];
                UnionEdge(edges, dsu, t, tri.A, uv[tri.A], tri.B, uv[tri.B]);
                UnionEdge(edges, dsu, t, tri.B, uv[tri.B], tri.C, uv[tri.C]);
                UnionEdge(edges, dsu, t, tri.C, uv[tri.C], tri.A, uv[tri.A]);
            }

            var remap = new Dictionary<int, int>();
            for (int t = 0; t < tris.Count; t++)
            {
                int root = dsu.Find(t);
                if (!remap.ContainsKey(root)) remap[root] = remap.Count;
                ids[t] = remap[root];
            }
            regionCount = Mathf.Max(1, remap.Count);
            return ids;
        }

        private static void UnionEdge(Dictionary<string, int> edges, DisjointSet dsu,
            int tri, int i0, Vector2 uv0, int i1, Vector2 uv1)
        {
            string key = EdgeKey(i0, uv0, i1, uv1);
            int other;
            if (edges.TryGetValue(key, out other)) dsu.Union(tri, other);
            else edges[key] = tri;
        }

        private static string EdgeKey(int i0, Vector2 uv0, int i1, Vector2 uv1)
        {
            bool swap = i0 > i1 || (i0 == i1 && (uv0.x > uv1.x ||
                (Mathf.Approximately(uv0.x, uv1.x) && uv0.y > uv1.y)));
            if (swap)
            {
                int ti = i0; i0 = i1; i1 = ti;
                Vector2 tv = uv0; uv0 = uv1; uv1 = tv;
            }
            return i0.ToString(CultureInfo.InvariantCulture) + ":" + Q(uv0.x) + "," + Q(uv0.y) +
                   "|" + i1.ToString(CultureInfo.InvariantCulture) + ":" + Q(uv1.x) + "," + Q(uv1.y);
        }

        private static int Q(float v)
        {
            return Mathf.RoundToInt(v * 10000f);
        }

        private sealed class DisjointSet
        {
            private readonly int[] _parent;
            private readonly int[] _rank;

            public DisjointSet(int n)
            {
                _parent = new int[n];
                _rank = new int[n];
                for (int i = 0; i < n; i++) _parent[i] = i;
            }

            public int Find(int x)
            {
                int root = x;
                while (_parent[root] != root) root = _parent[root];
                while (_parent[x] != root)
                {
                    int next = _parent[x];
                    _parent[x] = root;
                    x = next;
                }
                return root;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra == rb) return;
                if (_rank[ra] < _rank[rb]) { int t = ra; ra = rb; rb = t; }
                _parent[rb] = ra;
                if (_rank[ra] == _rank[rb]) _rank[ra]++;
            }
        }

        /// <summary>Point-in-triangle test in UV space (inclusive edges, tolerant of degenerate triangles).</summary>
        public static bool TriangleContains(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(d) < 1e-9f) return false;
            float l1 = ((b.y - c.y) * (p.x - c.x) + (c.x - b.x) * (p.y - c.y)) / d;
            float l2 = ((c.y - a.y) * (p.x - c.x) + (a.x - c.x) * (p.y - c.y)) / d;
            float l3 = 1f - l1 - l2;
            const float eps = 1e-6f;
            return l1 >= -eps && l2 >= -eps && l3 >= -eps;
        }

        private static void RasterizeTriangle(int[] mask, int w, int h,
            Vector2 a, Vector2 b, Vector2 c, int region)
        {
            float minU = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            float maxU = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float minV = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
            float maxV = Mathf.Max(a.y, Mathf.Max(b.y, c.y));

            int x0 = Mathf.Max(0, Mathf.FloorToInt(minU * w));
            int x1 = Mathf.Min(w - 1, Mathf.CeilToInt(maxU * w));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(minV * h));
            int y1 = Mathf.Min(h - 1, Mathf.CeilToInt(maxV * h));
            if (x1 < x0 || y1 < y0) return;

            for (int y = y0; y <= y1; y++)
            {
                float v = (y + 0.5f) / h;
                for (int x = x0; x <= x1; x++)
                {
                    float u = (x + 0.5f) / w;
                    if (TriangleContains(a, b, c, new Vector2(u, v)))
                        mask[y * w + x] = region;
                }
            }
        }

        /// <summary>
        /// Composite one rendered canvas per region through the UV mask, then
        /// dilate edge colours outward by 'padding' pixels so mipmaps and
        /// bilinear filtering never sample the empty background at UV seams.
        /// </summary>
        public static Texture2D Compose(TgUvLayout layout, Dictionary<int, Color32[]> regionPixels,
            Color background, int padding, bool fillNearest = false)
        {
            int w = layout.Width, h = layout.Height;
            int n = w * h;
            var outBuf = new Color32[n];
            var covered = new bool[n];
            Color32 bg = new Color32(
                (byte)Mathf.RoundToInt(Mathf.Clamp01(background.r) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(background.g) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(background.b) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(background.a) * 255f));

            for (int i = 0; i < n; i++)
            {
                int r = layout.Mask[i];
                Color32[] src;
                if (r >= 0 && regionPixels.TryGetValue(r, out src) && src != null && src.Length == n)
                {
                    outBuf[i] = src[i];
                    covered[i] = true;
                }
                else
                {
                    outBuf[i] = bg;
                }
            }

            padding = Mathf.Clamp(padding, 0, 64);
            if (padding > 0) Dilate(outBuf, covered, w, h, padding);

            // "nearest" extends the nearest island colour into every empty pixel.
            // Without it the atlas shows the silhouette of each UV island as a
            // repeated half-moon fragment against a flat background. Those empty
            // pixels are never sampled by the mesh, so filling them is safe and
            // also removes dark halos at mip levels.
            if (fillNearest) FillFromNearest(outBuf, covered, w, h);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.SetPixels32(outBuf);
            tex.Apply(false, false);
            tex.name = "TexGen_ForMesh";
            return tex;
        }

        /// <summary>Multi-source BFS: fill every uncovered pixel with the colour of its nearest covered pixel (O(w*h)).</summary>
        private static void FillFromNearest(Color32[] buf, bool[] covered, int w, int h)
        {
            int n = w * h;
            var visited = (bool[])covered.Clone();
            var queue = new int[n];
            int head = 0, tail = 0;
            for (int i = 0; i < n; i++) if (visited[i]) queue[tail++] = i;

            while (head < tail)
            {
                int i = queue[head++];
                Color32 c = buf[i];
                int x = i % w;
                int y = i / w;
                if (x > 0 && !visited[i - 1]) { visited[i - 1] = true; buf[i - 1] = c; queue[tail++] = i - 1; }
                if (x < w - 1 && !visited[i + 1]) { visited[i + 1] = true; buf[i + 1] = c; queue[tail++] = i + 1; }
                if (y > 0 && !visited[i - w]) { visited[i - w] = true; buf[i - w] = c; queue[tail++] = i - w; }
                if (y < h - 1 && !visited[i + w]) { visited[i + w] = true; buf[i + w] = c; queue[tail++] = i + w; }
            }
        }

        private static void Dilate(Color32[] buf, bool[] covered, int w, int h, int passes)
        {
            for (int pass = 0; pass < passes; pass++)
            {
                var next = (bool[])covered.Clone();
                bool changed = false;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (covered[i]) continue;

                        int count = 0;
                        int sr = 0, sg = 0, sb = 0, sa = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= h) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                if (nx < 0 || nx >= w) continue;
                                int j = ny * w + nx;
                                if (!covered[j]) continue;
                                Color32 c = buf[j];
                                sr += c.r; sg += c.g; sb += c.b; sa += c.a;
                                count++;
                            }
                        }

                        if (count == 0) continue;
                        buf[i] = new Color32(
                            (byte)(sr / count), (byte)(sg / count),
                            (byte)(sb / count), (byte)(sa / count));
                        next[i] = true;
                        changed = true;
                    }
                }

                if (!changed) break;
                covered = next;
            }
        }
    }
}

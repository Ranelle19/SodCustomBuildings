using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>One storey of a building as the generator sees it: inside cells (by address type) and outer edges.</summary>
    public sealed class StoreyPlan
    {
        public readonly Dictionary<(int x, int y), string> Cells = new();
        public readonly HashSet<(int x, int y, char d)> Windows = new();
        public readonly HashSet<(int x, int y, char d)> Entrances = new();
        public readonly HashSet<(int x, int y, char d)> Main = new();
    }

    /// <summary>
    /// "Neon arcade" exterior made from a building's floor plans. Ported from a Python prototype that was rendered and
    /// checked (all faces wind outwards, unique UVs per facade). Built in Unity space (left-handed, Y up, plan row y → −Z),
    /// written as OBJ with X mirrored like the plugin's model export. Facades get their own atlas area so the window map
    /// can tell every window apart.
    /// </summary>
    public static partial class ExteriorGenerator
    {
        // Ground = street level in the model. The game draws the ground floor's outside walls itself (from the floor plan,
        // with the base building's wall material); its own models start one storey up (y ≈ 5.3). So ours do too.
        public const float Cell = 1.8f, Storey = 5.4f, Ground = 0f, Skin = 0.1f, Parapet = 1.0f;
        public const int P = 32, ParPx = 18;

        internal static readonly Color[] Neon =
        {
            Color.FromArgb(255, 46, 170), Color.FromArgb(40, 235, 255), Color.FromArgb(175, 90, 255), Color.FromArgb(255, 214, 64),
        };
        internal static readonly Color Brick = Color.FromArgb(124, 64, 84), Mortar = Color.FromArgb(70, 40, 60);   // the game's light and fog darken it a lot
        internal static readonly Color Trim = Color.FromArgb(36, 36, 44), Glass = Color.FromArgb(14, 26, 40);

        private static float X(float x) => (x - 10.5f) * Cell;
        private static float Z(float y) => (10.5f - y) * Cell;

        private static readonly (char d, int dx, int dy)[] Dirs = { ('N', 0, -1), ('S', 0, 1), ('W', -1, 0), ('E', 1, 0) };

        private static (float x, float y, float z) Normal(char d) => d switch
        {
            'N' => (0, 0, 1), 'S' => (0, 0, -1), 'W' => (-1, 0, 0), _ => (1, 0, 0),
        };

        // ------------------------------------------------------------------ outline

        private readonly record struct Run(char D, int Line, int A0, int A1)
        {
            public int Length => A1 - A0 + 1;
        }

        private static List<Run> Outline(IReadOnlyDictionary<(int x, int y), string> cells)
        {
            var edges = new SortedDictionary<(char, int), List<int>>();
            foreach (var (x, y) in cells.Keys)
                foreach (var (d, dx, dy) in Dirs)
                    if (!cells.ContainsKey((x + dx, y + dy)))
                    {
                        var key = d == 'N' || d == 'S' ? (d, y) : (d, x);
                        if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<int>();
                        list.Add(d == 'N' || d == 'S' ? x : y);
                    }
            var runs = new List<Run>();
            foreach (var kv in edges)
            {
                var al = kv.Value.OrderBy(a => a).ToList();
                int s = al[0], p = al[0];
                for (int i = 1; i <= al.Count; i++)
                {
                    if (i < al.Count && al[i] == p + 1) { p = al[i]; continue; }
                    runs.Add(new Run(kv.Key.Item1, kv.Key.Item2, s, p));
                    if (i < al.Count) s = p = al[i];
                }
            }
            return runs;
        }

        private static List<(int x, int y)> RunCells(Run r)
        {
            var list = new List<(int, int)>();
            for (int a = r.A0; a <= r.A1; a++) list.Add(r.D == 'N' || r.D == 'S' ? (a, r.Line) : (r.Line, a));
            return list;
        }

        /// <summary>Cells of a run left to right as seen from outside.</summary>
        private static List<(int x, int y)> Ordered(Run r)
        {
            var c = RunCells(r);
            if (r.D == 'N' || r.D == 'E') c.Reverse();
            return c;
        }

        /// <summary>Bottom-left and bottom-right (X, Z) of a cell's outer edge seen from outside, pushed out by Skin.</summary>
        private static ((float x, float z) a, (float x, float z) b) EdgePoints((int x, int y) c, char d)
        {
            var (x, y) = c;
            (float, float) a, b; (float, float) off;
            switch (d)
            {
                case 'N': a = (X(x + 1), Z(y)); b = (X(x), Z(y)); off = (0, Skin); break;
                case 'S': a = (X(x), Z(y + 1)); b = (X(x + 1), Z(y + 1)); off = (0, -Skin); break;
                case 'W': a = (X(x), Z(y)); b = (X(x), Z(y + 1)); off = (-Skin, 0); break;
                default: a = (X(x + 1), Z(y + 1)); b = (X(x + 1), Z(y)); off = (Skin, 0); break;
            }
            return ((a.Item1 + off.Item1, a.Item2 + off.Item2), (b.Item1 + off.Item1, b.Item2 + off.Item2));
        }

        /// <summary>
        /// Walls sit Skin outside the plan edge, so at an outside corner two walls would stop Skin short of each other and
        /// leave a see-through slit. The first and last cell of a run reach Skin further; at inside corners that overlap
        /// is hidden behind the other wall.
        /// </summary>
        private static ((float x, float z) a, (float x, float z) b) CloseCorners((float x, float z) a, (float x, float z) b, bool first, bool last)
        {
            float tx = b.x - a.x, tz = b.z - a.z, len = MathF.Sqrt(tx * tx + tz * tz);
            if (len < 1e-5f) return (a, b);
            tx = tx / len * Skin; tz = tz / len * Skin;
            if (first) a = (a.x - tx, a.z - tz);
            if (last) b = (b.x + tx, b.z + tz);
            return (a, b);
        }

        // ------------------------------------------------------------------ mesh + atlas

        private sealed class MeshB
        {
            public readonly List<(float x, float y, float z)> V = new(), N = new();
            public readonly List<(float u, float v)> Uv = new();
            public readonly List<(int a, int b, int c)> Tris = new();

            /// <summary>Corners as seen from the front; uv = (u0, v0, u1, v1) in atlas pixels, v0 = top row.</summary>
            public void Quad((float, float, float) bl, (float, float, float) br, (float, float, float) tr, (float, float, float) tl,
                             (float, float, float) normal, (float u0, float v0, float u1, float v1) uv)
            {
                int i = V.Count;
                V.Add(bl); V.Add(br); V.Add(tr); V.Add(tl);
                for (int k = 0; k < 4; k++) N.Add(normal);
                Uv.Add((uv.u0, uv.v1)); Uv.Add((uv.u1, uv.v1)); Uv.Add((uv.u1, uv.v0)); Uv.Add((uv.u0, uv.v0));
                Tris.Add((i, i + 3, i + 2)); Tris.Add((i, i + 2, i + 1));   // clockwise from the front (Unity)
            }
        }

        private sealed class Item
        {
            public int W, H, X, Y;
        }

        private sealed class Atlas
        {
            public int Width = 2048, Height;
            private readonly List<Item> _items = new();
            public Item Add(int w, int h) { var it = new Item { W = w, H = h }; _items.Add(it); return it; }
            public void Pack(int pad = 2)
            {
                int x = 0, y = 0, rowh = 0;
                foreach (var it in _items.OrderByDescending(i => i.H))   // stable, like Python's sorted()
                {
                    int w = it.W + 2 * pad, h = it.H + 2 * pad;
                    if (x + w > Width) { x = 0; y += rowh; rowh = 0; }
                    it.X = x + pad; it.Y = y + pad;
                    x += w; rowh = Math.Max(rowh, h);
                }
                Height = 1;
                while (Height < y + rowh) Height *= 2;
            }
        }

        // ------------------------------------------------------------------ pixels

        internal sealed class Img
        {
            public readonly int W, H;
            public readonly byte[] Px;   // RGB
            public Img(int w, int h) { W = w; H = h; Px = new byte[w * h * 3]; }

            public void Rect(int x0, int y0, int x1, int y1, Color c)
            {
                x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(W, x1); y1 = Math.Min(H, y1);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) Set(x, y, c);
            }

            public void Set(int x, int y, Color c)
            {
                if (x < 0 || y < 0 || x >= W || y >= H) return;
                int i = (y * W + x) * 3;
                Px[i] = c.R; Px[i + 1] = c.G; Px[i + 2] = c.B;
            }

            public Color Get(int x, int y)
            {
                int i = (y * W + x) * 3;
                return Color.FromArgb(Px[i], Px[i + 1], Px[i + 2]);
            }

            /// <summary>Reads a PNG (any size; returns null if it can't).</summary>
            public static Img Load(string path)
            {
                try
                {
                    using var src = new Bitmap(path);
                    using var bmp = src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format24bppRgb);
                    var img = new Img(bmp.Width, bmp.Height);
                    var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    var row = new byte[data.Stride];
                    for (int y = 0; y < img.H; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                        for (int x = 0; x < img.W; x++)
                        {
                            int i = (y * img.W + x) * 3;
                            img.Px[i] = row[x * 3 + 2]; img.Px[i + 1] = row[x * 3 + 1]; img.Px[i + 2] = row[x * 3];
                        }
                    }
                    bmp.UnlockBits(data);
                    return img;
                }
                catch { return null; }
            }

            /// <summary>Copies a rectangle from another image (same place in both).</summary>
            public void CopyRect(Img from, int x0, int y0, int w, int h)
            {
                for (int y = y0; y < y0 + h; y++)
                {
                    if (y < 0 || y >= H || y >= from.H) continue;
                    for (int x = x0; x < x0 + w; x++)
                    {
                        if (x < 0 || x >= W || x >= from.W) continue;
                        int i = (y * W + x) * 3, j = (y * from.W + x) * 3;
                        Px[i] = from.Px[j]; Px[i + 1] = from.Px[j + 1]; Px[i + 2] = from.Px[j + 2];
                    }
                }
            }

            public void Save(string path)
            {
                using var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
                var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                var row = new byte[data.Stride];
                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        int i = (y * W + x) * 3;
                        row[x * 3] = Px[i + 2]; row[x * 3 + 1] = Px[i + 1]; row[x * 3 + 2] = Px[i];   // BGR
                    }
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
                }
                bmp.UnlockBits(data);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        internal static Color Clamp(int r, int g, int b) => Color.FromArgb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
        internal static Color Pale(Color c) => Clamp(c.R / 2 + 120, c.G / 2 + 120, c.B / 2 + 120);

        private static void PaintBrick(Img img, int ox, int oy, int w, int h, Random rng)
        {
            img.Rect(ox, oy, ox + w, oy + h, Mortar);
            const int bh = 4, bw = 10;
            for (int r = 0; r < h; r += bh)
            {
                int shift = (r / bh) % 2 * (bw / 2);
                for (int c = -bw; c < w; c += bw)
                {
                    int x0 = Math.Max(ox, ox + c + shift + 1), x1 = Math.Min(ox + w, ox + c + shift + bw);
                    if (x1 <= x0) continue;
                    int v = rng.Next(-8, 9);
                    img.Rect(x0, oy + r, x1, oy + Math.Min(h, r + bh - 1), Clamp(Brick.R + v, Brick.G + v, Brick.B + v));
                }
            }
        }

        /// <summary>Darker column on tile boundaries (every 3 cells).</summary>
        private static void Pilasters(Img img, int ox, int oy, int L, int h, int firstA, int step)
        {
            for (int i = 0; i <= L; i++)
            {
                int a = firstA + i * step;
                if (((a % 3) + 3) % 3 != 0) continue;
                int x = ox + i * P;
                img.Rect(Math.Max(ox, x - 2), oy, Math.Min(ox + L * P, x + 2), oy + h, Color.FromArgb(40, 22, 36));
            }
        }

        private static void PaintGlass(Img img, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                float t = (y - y0) / (float)Math.Max(1, y1 - y0);
                img.Rect(x0, y, x1, y + 1, Clamp((int)(Glass.R + 30 * (1 - t)), (int)(Glass.G + 40 * (1 - t)), (int)(Glass.B + 50 * (1 - t))));
            }
            for (int k = 0; k < (x1 - x0) / 2; k++)
            {
                int y = y0 + 2 + k;
                if (y < y1 - 1) img.Rect(Math.Min(x1 - 1, x0 + 2 + k * 2), y, Math.Min(x1, x0 + 4 + k * 2), y + 1, Color.FromArgb(70, 100, 120));
            }
        }

        /// <summary>Anti-aliased text as a 0..1 mask, shrunk until it fits.</summary>
        internal static float[,] TextMask(string text, int w, int h, int maxSize)
        {
            var mask = new float[h, w];
            if (string.IsNullOrEmpty(text) || w <= 0 || h <= 0) return mask;
            using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int size = Math.Max(6, maxSize);
            Font font = null;
            SizeF m = default;
            while (true)
            {
                font?.Dispose();
                font = new Font("Arial", size, FontStyle.Bold, GraphicsUnit.Pixel);
                m = g.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic);
                if (m.Width <= w - 6 || size <= 6) break;
                size--;
            }
            using (font)
            using (var brush = new SolidBrush(Color.White))
                g.DrawString(text, font, brush, (w - m.Width) / 2f, (h - m.Height) / 2f, StringFormat.GenericTypographic);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < w; x++) mask[y, x] = row[x * 3 + 1] / 255f;
            }
            bmp.UnlockBits(data);
            return mask;
        }

        internal static float[,] Blur(float[,] m, int r)
        {
            int h = m.GetLength(0), w = m.GetLength(1);
            var a = (float[,])m.Clone();
            for (int pass = 0; pass < 2; pass++)
            {
                var b = new float[h, w];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float s = 0; int n = 0;
                        for (int k = -r; k <= r; k++) { int xx = x + k; if (xx >= 0 && xx < w) { s += a[y, xx]; n++; } }
                        b[y, x] = s / n;
                    }
                var c = new float[h, w];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float s = 0; int n = 0;
                        for (int k = -r; k <= r; k++) { int yy = y + k; if (yy >= 0 && yy < h) { s += b[yy, x]; n++; } }
                        c[y, x] = s / n;
                    }
                a = c;
            }
            return a;
        }

        // ------------------------------------------------------------------ generator

        public sealed class Result
        {
            public string ObjText;
            public int Width, Height, Triangles, Windows;
            public float WallTopY;
            public string FrontSide = "";
            public List<ExteriorPanel> Panels = new();
            internal Img Diffuse, Lit, Unlit, WindowMap;
        }

        /// <param name="neon">false = plain brick with windows and doors and blank signs, as a base to paint on.</param>
        public static Result Generate(IReadOnlyList<StoreyPlan> storeys, string signText, bool neon = true, int seed = 7, bool terraceWalls = true)
        {
            if (storeys.Count == 0 || storeys.Any(s => s.Cells.Count == 0)) throw new ArgumentException("Every storey needs at least one cell inside the building.");
            var rng = new Random(seed);
            var mesh = new MeshB();
            var detail = new MeshB();
            var atlas = new Atlas();

            var facades = new List<(int s, Run run, Item it)>();
            for (int s = 1; s < storeys.Count; s++)   // not the ground floor: the game builds that one
                foreach (var run in Outline(storeys[s].Cells))
                    facades.Add((s, run, atlas.Add(run.Length * P, 3 * P)));
            var top = storeys[^1];
            var topOutline = Outline(top.Cells);
            var parapets = topOutline.Select(r => (run: r, it: atlas.Add(r.Length * P, ParPx), s: storeys.Count)).ToList();
            // low walls around balconies, terraces and setbacks: outer edges of a storey that the storey above doesn't cover
            for (int s = 1; s < storeys.Count && terraceWalls; s++)
                foreach (var run in Outline(storeys[s - 1].Cells))
                {
                    int a0 = int.MinValue;
                    for (int a = run.A0; a <= run.A1 + 1; a++)
                    {
                        var c = run.D == 'N' || run.D == 'S' ? (a, run.Line) : (run.Line, a);
                        bool open = a <= run.A1 && !storeys[s].Cells.ContainsKey(c);
                        if (open && a0 == int.MinValue) a0 = a;
                        if (!open && a0 != int.MinValue)
                        {
                            var seg = new Run(run.D, run.Line, a0, a - 1);
                            parapets.Add((seg, atlas.Add(seg.Length * P, ParPx), s));
                            a0 = int.MinValue;
                        }
                    }
                }
            var roof = atlas.Add(64, 64);
            char sd = storeys[0].Main.Count > 0 ? storeys[0].Main.First().d : 'N';
            var sideRuns = topOutline.Where(r => r.D == sd).ToList();
            if (sideRuns.Count == 0) { sd = topOutline[0].D; sideRuns = topOutline.Where(r => r.D == sd).ToList(); }
            var signRun = sideRuns.OrderByDescending(r => r.Length).First();
            int signCells = Math.Max(4, (int)(signRun.Length * 0.7));
            signCells = Math.Min(signCells, signRun.Length);
            var signFront = atlas.Add(signCells * P, 64);
            var signBack = atlas.Add(64, 64);
            var awning = atlas.Add(96, 24);
            int bladeStoreys = Math.Max(1, storeys.Count - 1);
            var blade = atlas.Add(P, bladeStoreys * 3 * P);
            var box = atlas.Add(32, 32);
            var swatches = atlas.Add(64, 16);   // colours for balcony railings, bay window sides, ledge tops
            atlas.Pack();
            int W = atlas.Width, H = atlas.Height;

            // where everything sits in the textures, for the exterior painter
            var panels = new List<ExteriorPanel>();
            ExteriorPanel Panel(string kind, Item it, string side = "", int storey = 0, int line = 0, int from = 0, int to = 0)
            {
                var p = new ExteriorPanel { Kind = kind, Side = side, Storey = storey, Line = line, From = from, To = to, X = it.X, Y = it.Y, W = it.W, H = it.H };
                panels.Add(p);
                return p;
            }
            foreach (var (s, run, it) in facades) Panel("Wall", it, run.D.ToString(), s, run.Line, run.A0, run.A1);
            foreach (var (run, it, ps) in parapets) Panel("Parapet", it, run.D.ToString(), ps, run.Line, run.A0, run.A1);
            Panel("Sign", signFront, sd.ToString());
            Panel("SignBack", signBack, sd.ToString());
            if (storeys.Count >= 2) Panel("Blade", blade, signRun.D.ToString());
            if (storeys[0].Entrances.Count > 0) Panel("Awning", awning);
            Panel("Roof", roof);
            Panel("Box", box);
            Panel("ShapeColours", swatches);

            var diffuse = new Img(W, H); var lit = new Img(W, H); var unlit = new Img(W, H); var winmap = new Img(W, H);
            int windows = 0;

            void NeonLine(int x0, int y0, int x1, int y1, Color c)
            {
                if (!neon) return;
                diffuse.Rect(x0, y0, x1, y1, Pale(c)); unlit.Rect(x0, y0, x1, y1, c); lit.Rect(x0, y0, x1, y1, c);
            }

            void Stamp(float[,] mask, int ox, int oy, Color diffuseC, Color neonC, float threshold = 90 / 255f)
            {
                int h = mask.GetLength(0), w = mask.GetLength(1);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (mask[y, x] > threshold)
                        {
                            diffuse.Set(ox + x, oy + y, diffuseC); lit.Set(ox + x, oy + y, neonC); unlit.Set(ox + x, oy + y, neonC);
                        }
            }

            // ---- facades
            for (int fi = 0; fi < facades.Count; fi++)
            {
                var (s, run, it) = facades[fi];
                int ox = it.X, oy = it.Y;
                char d = run.D;
                var plan = storeys[s];
                var cells = Ordered(run);
                int L = cells.Count;
                PaintBrick(diffuse, ox, oy, L * P, 3 * P, rng);
                var along = cells.Select(c => d == 'N' || d == 'S' ? c.x : c.y).ToList();
                bool up = along[^1] >= along[0];
                Pilasters(diffuse, ox, oy, L, 3 * P, along[0] + (up ? 0 : 1), up ? 1 : -1);
                float y0w = Ground + s * Storey - (s == 1 ? 0.1f : 0f), y1w = y0w + Storey + (s == 1 ? 0.1f : 0f);   // overlap the game's ground floor a little, like its own models
                var nrm = Normal(d);

                for (int i = 0; i < L; i++)
                {
                    var c = cells[i];
                    int px0 = ox + i * P, px1 = px0 + P;
                    var (a, b) = EdgePoints(c, d);
                    (a, b) = CloseCorners(a, b, i == 0, i == L - 1);
                    mesh.Quad((a.x, y0w, a.z), (b.x, y0w, b.z), (b.x, y1w, b.z), (a.x, y1w, a.z), nrm, (px0, oy, px1, oy + 3 * P));
                    var edge = (c.x, c.y, d);
                    plan.Cells.TryGetValue(c, out var kind);
                    kind ??= "";
                    if (s == 0)
                    {
                        diffuse.Rect(px0, oy + 3 * P - 4, px1, oy + 3 * P, Trim);   // plinth
                        if (plan.Entrances.Contains(edge))
                        {
                            diffuse.Rect(px0 + 3, oy + 26, px1 - 3, oy + 3 * P - 4, Trim);
                            PaintGlass(diffuse, px0 + 6, oy + 30, px1 - 6, oy + 3 * P - 6);
                            diffuse.Rect(px0 + P / 2 - 1, oy + 30, px0 + P / 2 + 1, oy + 3 * P - 6, Trim);
                            lit.Rect(px0 + 6, oy + 30, px1 - 6, oy + 3 * P - 6, Color.FromArgb(120, 96, 60));
                            NeonLine(px0 + 2, oy + 22, px1 - 2, oy + 24, Neon[(fi + i) % 4]);
                        }
                        else if (kind == "Retail" || kind == "Eatery" || plan.Windows.Contains(edge))
                        {
                            diffuse.Rect(px0, oy + 18, px1, oy + 3 * P - 4, Trim);
                            int gx0 = px0 + 2, gy0 = oy + 30, gx1 = px1 - 2, gy1 = oy + 3 * P - 10;
                            PaintGlass(diffuse, gx0, gy0, gx1, gy1);
                            winmap.Rect(gx0, gy0, gx1, gy1, Color.White);
                            windows++;
                            var shades = new[] { Color.FromArgb(150, 120, 70), Color.FromArgb(120, 140, 150), Color.FromArgb(160, 110, 140) };
                            lit.Rect(gx0, gy0, gx1, gy1, shades[rng.Next(shades.Length)]);
                            NeonLine(px0, oy + 20, px1, oy + 23, Neon[(fi + (kind == "Retail" ? 0 : 1)) % 4]);
                        }
                    }
                    else
                    {
                        if (plan.Entrances.Contains(edge))
                        {
                            // a door onto a balcony or terrace
                            diffuse.Rect(px0 + 4, oy + 22, px1 - 4, oy + 3 * P, Trim);
                            PaintGlass(diffuse, px0 + 7, oy + 26, px1 - 7, oy + 3 * P - 2);
                            diffuse.Rect(px0 + P / 2 - 1, oy + 26, px0 + P / 2 + 1, oy + 3 * P - 2, Trim);
                            lit.Rect(px0 + 7, oy + 26, px1 - 7, oy + 3 * P - 2, Color.FromArgb(120, 96, 60));
                        }
                        else if (plan.Windows.Contains(edge))
                        {
                            int gx0 = px0 + 5, gy0 = oy + 26, gx1 = px1 - 5, gy1 = oy + 66;
                            diffuse.Rect(gx0 - 2, gy0 - 2, gx1 + 2, gy1 + 3, Trim);
                            PaintGlass(diffuse, gx0, gy0, gx1, gy1);
                            winmap.Rect(gx0, gy0, gx1, gy1, Color.White);
                            windows++;
                            if (rng.NextDouble() < 0.7)
                            {
                                var warm = new[] { Color.FromArgb(255, 196, 110), Color.FromArgb(240, 170, 90), Color.FromArgb(200, 220, 255), Color.FromArgb(255, 120, 190) };
                                lit.Rect(gx0, gy0, gx1, gy1, warm[rng.Next(warm.Length)]);
                            }
                        }
                        NeonLine(px0, oy + 3 * P - 3, px1, oy + 3 * P - 1, Neon[s % 2]);   // storey band
                    }
                }

                if (s == 0)
                {
                    // a neon word on the fascia over each run of shop windows
                    int i = 0;
                    while (i < L)
                    {
                        plan.Cells.TryGetValue(cells[i], out var k);
                        if (neon && (k == "Retail" || k == "Eatery") && !plan.Entrances.Contains((cells[i].x, cells[i].y, d)))
                        {
                            int j = i;
                            while (j + 1 < L && plan.Cells.TryGetValue(cells[j + 1], out var k2) && k2 == k &&
                                   !plan.Entrances.Contains((cells[j + 1].x, cells[j + 1].y, d))) j++;
                            if (j - i + 1 >= 2)
                            {
                                var words = k == "Eatery" ? new[] { "FOOD", "EAT", "DINER" } : new[] { "SHOP", "SALE", "OPEN", "24H", "DEALS" };
                                var word = words[rng.Next(words.Length)];
                                int bw = (j - i + 1) * P;
                                var col = Neon[(fi + i) % 4];
                                int x0b = ox + i * P, y0b = oy + 2;
                                diffuse.Rect(x0b + 2, y0b, x0b + bw - 2, y0b + 16, Color.FromArgb(24, 16, 30));
                                Stamp(TextMask(word, bw, 16, 15), x0b, y0b, Pale(col), col);
                            }
                            i = j + 1;
                        }
                        else i++;
                    }
                }
                else
                {
                    NeonLine(ox, oy, ox + 3, oy + 3 * P, Neon[1]);
                    NeonLine(ox + L * P - 3, oy, ox + L * P, oy + 3 * P, Neon[1]);
                }
            }

            // ---- parapets (outer + inner faces + caps) and roofs
            float ytop = Ground + storeys.Count * Storey;
            foreach (var (run, it, ps) in parapets)
            {
                float ybase = Ground + ps * Storey;
                int ox = it.X, oy = it.Y;
                char d = run.D;
                var cells = Ordered(run);
                int L = cells.Count;
                PaintBrick(diffuse, ox, oy, L * P, ParPx, rng);
                NeonLine(ox, oy, ox + L * P, oy + 2, Neon[0]);
                var (nx, _, nz) = Normal(d);
                for (int i = 0; i < L; i++)
                {
                    var (a, b) = EdgePoints(cells[i], d);
                    (a, b) = CloseCorners(a, b, i == 0, i == L - 1);
                    var uv = (ox + i * P, oy, ox + (i + 1) * P, oy + ParPx);
                    mesh.Quad((a.x, ybase, a.z), (b.x, ybase, b.z), (b.x, ybase + Parapet, b.z), (a.x, ybase + Parapet, a.z), Normal(d), uv);
                    const float t = 0.25f;
                    var ia = (x: a.x - nx * t, z: a.z - nz * t);
                    var ib = (x: b.x - nx * t, z: b.z - nz * t);
                    mesh.Quad((ib.x, ybase, ib.z), (ia.x, ybase, ia.z), (ia.x, ybase + Parapet, ia.z), (ib.x, ybase + Parapet, ib.z), (-nx, 0, -nz), uv);
                    mesh.Quad((ib.x, ybase + Parapet, ib.z), (ia.x, ybase + Parapet, ia.z), (a.x, ybase + Parapet, a.z), (b.x, ybase + Parapet, b.z), (0, 1, 0),
                              (ox + i * P, oy, ox + (i + 1) * P, oy + 2));
                }
            }
            diffuse.Rect(roof.X, roof.Y, roof.X + 64, roof.Y + 64, Color.FromArgb(40, 38, 44));
            for (int k = 0; k < 64; k += 8) diffuse.Rect(roof.X, roof.Y + k, roof.X + 64, roof.Y + k + 1, Color.FromArgb(34, 32, 38));
            for (int s = 0; s < storeys.Count; s++)
            {
                var above = s + 1 < storeys.Count ? storeys[s + 1].Cells : new Dictionary<(int x, int y), string>();
                float yr = Ground + (s + 1) * Storey;
                foreach (var (x, y) in storeys[s].Cells.Keys)
                {
                    if (above.ContainsKey((x, y))) continue;
                    mesh.Quad((X(x), yr, Z(y + 1)), (X(x + 1), yr, Z(y + 1)), (X(x + 1), yr, Z(y)), (X(x), yr, Z(y)), (0, 1, 0),
                              (roof.X, roof.Y, roof.X + 64, roof.Y + 64));
                }
                // underside where this storey sticks out over open air (e.g. the ceiling of a balcony below)
                if (s + 1 < storeys.Count)
                    foreach (var (x, y) in storeys[s + 1].Cells.Keys)
                    {
                        if (storeys[s].Cells.ContainsKey((x, y))) continue;
                        mesh.Quad((X(x), yr, Z(y)), (X(x + 1), yr, Z(y)), (X(x + 1), yr, Z(y + 1)), (X(x), yr, Z(y + 1)), (0, -1, 0),
                                  (roof.X, roof.Y, roof.X + 64, roof.Y + 64));
                    }
            }

            // ---- rooftop sign on the main entrance side
            int sx = signFront.X, sy = signFront.Y, sw = signCells * P;
            diffuse.Rect(sx, sy, sx + sw, sy + 64, Color.FromArgb(20, 14, 26));
            diffuse.Rect(sx, sy, sx + sw, sy + 3, Trim);
            diffuse.Rect(sx, sy + 61, sx + sw, sy + 64, Trim);
            var tm = TextMask(neon ? signText : "", sw, 64, 44);
            var glow = Blur(tm, 3);
            var pink = Neon[0];
            foreach (var buf in new[] { unlit, lit })
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < sw; x++)
                    {
                        float gl = glow[y, x] * 0.8f;
                        var o = buf.Get(sx + x, sy + y);
                        var c = Color.FromArgb(Math.Max(o.R, (int)(pink.R * gl)), Math.Max(o.G, (int)(pink.G * gl)), Math.Max(o.B, (int)(pink.B * gl)));
                        if (tm[y, x] > 100 / 255f) c = Color.FromArgb(255, 200, 235);
                        buf.Set(sx + x, sy + y, c);
                    }
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < sw; x++)
                    if (tm[y, x] > 100 / 255f) diffuse.Set(sx + x, sy + y, Color.FromArgb(255, 170, 220));
            NeonLine(sx, sy + 3, sx + sw, sy + 5, Neon[1]);
            NeonLine(sx, sy + 59, sx + sw, sy + 61, Neon[1]);
            diffuse.Rect(signBack.X, signBack.Y, signBack.X + 64, signBack.Y + 64, Color.FromArgb(26, 22, 30));
            {
                var cells = Ordered(signRun);
                int L = cells.Count, first = (L - signCells) / 2;
                char d = signRun.D;
                var (a, _) = EdgePoints(cells[first], d);
                var (_, b) = EdgePoints(cells[first + signCells - 1], d);
                var (nx, _, nz) = Normal(d);
                const float back = 0.6f;
                float ax = a.x - nx * back, az = a.z - nz * back, bx = b.x - nx * back, bz = b.z - nz * back;
                float y0s = ytop + 0.6f, y1s = ytop + 0.6f + 3.6f;
                detail.Quad((ax, y0s, az), (bx, y0s, bz), (bx, y1s, bz), (ax, y1s, az), Normal(d), (sx, sy, sx + sw, sy + 64));
                const float t = 0.15f;
                detail.Quad((bx - nx * t, y0s, bz - nz * t), (ax - nx * t, y0s, az - nz * t), (ax - nx * t, y1s, az - nz * t), (bx - nx * t, y1s, bz - nz * t),
                            (-nx, 0, -nz), (signBack.X, signBack.Y, signBack.X + 64, signBack.Y + 64));
                foreach (var f in new[] { 0.15f, 0.85f })
                {
                    float px = ax + (bx - ax) * f, pz = az + (bz - az) * f;
                    float tx = bx - ax, tz = bz - az, ln = MathF.Sqrt(tx * tx + tz * tz);
                    tx = tx / ln * 0.12f; tz = tz / ln * 0.12f;
                    detail.Quad((px - tx, ytop, pz - tz), (px + tx, ytop, pz + tz), (px + tx, y0s, pz + tz), (px - tx, y0s, pz - tz), Normal(d),
                                (signBack.X, signBack.Y, signBack.X + 8, signBack.Y + 64));
                }
            }

            // ---- awnings over entrances
            for (int k = 0; k < 96; k += 12)
            {
                diffuse.Rect(awning.X + k, awning.Y, awning.X + k + 6, awning.Y + 24, Color.FromArgb(90, 20, 70));
                diffuse.Rect(awning.X + k + 6, awning.Y, awning.X + k + 12, awning.Y + 24, Color.FromArgb(30, 16, 34));
            }
            if (neon)
            {
                diffuse.Rect(awning.X, awning.Y + 21, awning.X + 96, awning.Y + 24, Color.FromArgb(255, 120, 210));
                unlit.Rect(awning.X, awning.Y + 21, awning.X + 96, awning.Y + 24, Neon[0]);
                lit.Rect(awning.X, awning.Y + 21, awning.X + 96, awning.Y + 24, Neon[0]);
            }
            foreach (var (x, y, d) in storeys[0].Entrances.OrderBy(e => e.x).ThenBy(e => e.y).ThenBy(e => e.d))
            {
                var (a, b) = EdgePoints((x, y), d);
                var (nx, _, nz) = Normal(d);
                const float outD = 1.3f;
                float yA = Ground + 3.7f, yB = Ground + 3.1f;
                detail.Quad((a.x + nx * outD, yB, a.z + nz * outD), (b.x + nx * outD, yB, b.z + nz * outD), (b.x, yA, b.z), (a.x, yA, a.z),
                            (nx * 0.4f, 0.9f, nz * 0.4f), (awning.X, awning.Y, awning.X + 32, awning.Y + 24));
                detail.Quad((a.x + nx * outD, yB - 0.35f, a.z + nz * outD), (b.x + nx * outD, yB - 0.35f, b.z + nz * outD),
                            (b.x + nx * outD, yB, b.z + nz * outD), (a.x + nx * outD, yB, a.z + nz * outD), Normal(d),
                            (awning.X, awning.Y + 18, awning.X + 32, awning.Y + 24));
            }

            // ---- blade sign: sticks out of the sign side near its left end, storeys 1..top
            if (storeys.Count >= 2)
            {
                int blx = blade.X, bly = blade.Y, bh = bladeStoreys * 3 * P;
                diffuse.Rect(blx, bly, blx + P, bly + bh, Color.FromArgb(22, 14, 28));
                var word = (signText ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "SHOP";
                word = word.Substring(0, Math.Min(word.Length, Math.Max(3, bh / 22)));
                int cellH = bh / Math.Max(1, word.Length);
                for (int li = 0; li < word.Length && neon; li++)
                    Stamp(TextMask(word[li].ToString(), P, cellH, Math.Min(P - 4, cellH - 4)), blx, bly + li * cellH, Color.FromArgb(150, 240, 255), Neon[1]);
                foreach (var buf in neon ? new[] { diffuse, lit, unlit } : Array.Empty<Img>())
                {
                    var c = buf == diffuse ? Color.FromArgb(255, 120, 210) : Neon[0];
                    buf.Rect(blx, bly, blx + 2, bly + bh, c);
                    buf.Rect(blx + P - 2, bly, blx + P, bly + bh, c);
                }
                var cells = Ordered(signRun);
                var cc = cells.Count > 2 ? cells[1] : cells[0];
                var (a, b) = EdgePoints(cc, signRun.D);
                var (nx, _, nz) = Normal(signRun.D);
                float mx = (a.x + b.x) / 2, mz = (a.z + b.z) / 2;
                const float outB = 1.5f, th = 0.18f;
                float tx = b.x - a.x, tz = b.z - a.z, ln = MathF.Sqrt(tx * tx + tz * tz);
                tx = tx / ln * th; tz = tz / ln * th;
                float y0b = Ground + Storey + 0.4f, y1b = Ground + storeys.Count * Storey - 0.4f;
                var uvb = (blx, bly, blx + P, bly + bh);
                (float, float) p0 = (mx - tx, mz - tz), p1 = (mx - tx + nx * outB, mz - tz + nz * outB);
                (float, float) q0 = (mx + tx, mz + tz), q1 = (mx + tx + nx * outB, mz + tz + nz * outB);
                // seen from the −t side the wall is on the viewer's left; from the +t side it's on the right
                detail.Quad((p0.Item1, y0b, p0.Item2), (p1.Item1, y0b, p1.Item2), (p1.Item1, y1b, p1.Item2), (p0.Item1, y1b, p0.Item2), (-tx / th, 0, -tz / th), uvb);
                detail.Quad((q1.Item1, y0b, q1.Item2), (q0.Item1, y0b, q0.Item2), (q0.Item1, y1b, q0.Item2), (q1.Item1, y1b, q1.Item2), (tx / th, 0, tz / th), uvb);
                detail.Quad((p1.Item1, y0b, p1.Item2), (q1.Item1, y0b, q1.Item2), (q1.Item1, y1b, q1.Item2), (p1.Item1, y1b, p1.Item2), Normal(signRun.D),
                            (blx, bly, blx + 2, bly + bh));
            }

            // ---- rooftop boxes (vents / AC units)
            diffuse.Rect(box.X, box.Y, box.X + 32, box.Y + 32, Color.FromArgb(70, 72, 80));
            for (int k = 4; k < 32; k += 6) diffuse.Rect(box.X + 3, box.Y + k, box.X + 29, box.Y + k + 2, Color.FromArgb(48, 50, 56));
            var topCells = top.Cells.Keys.OrderBy(c => c.x).ThenBy(c => c.y).ToList();
            var cands = topCells.Where(c => new[] { -2, 0, 2 }.All(dx => new[] { -2, 0, 2 }.All(dy => top.Cells.ContainsKey((c.x + dx, c.y + dy))))).ToList();
            var rng2 = new Random(topCells.Count);
            for (int n = 0; n < Math.Min(3, cands.Count); n++)
            {
                var c = cands[rng2.Next(cands.Count)];
                cands.Remove(c);
                float hgt = new[] { 1.0f, 1.4f, 1.8f }[rng2.Next(3)];
                int sz = rng2.Next(1, 3);
                float x0b = X(c.x), x1b = X(c.x + sz), z0b = Z(c.y + sz), z1b = Z(c.y), yb = ytop, yt = ytop + hgt;
                var uvx = (box.X, box.Y, box.X + 32, box.Y + 32);
                detail.Quad((x1b, yb, z1b), (x0b, yb, z1b), (x0b, yt, z1b), (x1b, yt, z1b), (0, 0, 1), uvx);
                detail.Quad((x0b, yb, z0b), (x1b, yb, z0b), (x1b, yt, z0b), (x0b, yt, z0b), (0, 0, -1), uvx);
                detail.Quad((x0b, yb, z1b), (x0b, yb, z0b), (x0b, yt, z0b), (x0b, yt, z1b), (-1, 0, 0), uvx);
                detail.Quad((x1b, yb, z0b), (x1b, yb, z1b), (x1b, yt, z1b), (x1b, yt, z0b), (1, 0, 0), uvx);
                detail.Quad((x0b, yt, z0b), (x1b, yt, z0b), (x1b, yt, z1b), (x0b, yt, z1b), (0, 1, 0), uvx);
            }

            // ---- colours used by the shapes (balconies, bay windows…): trim, metal, glass, concrete
            diffuse.Rect(swatches.X, swatches.Y, swatches.X + 16, swatches.Y + 16, Trim);
            diffuse.Rect(swatches.X + 16, swatches.Y, swatches.X + 32, swatches.Y + 16, Color.FromArgb(70, 72, 80));
            PaintGlass(diffuse, swatches.X + 32, swatches.Y, swatches.X + 48, swatches.Y + 16);
            diffuse.Rect(swatches.X + 48, swatches.Y, swatches.X + 64, swatches.Y + 16, Color.FromArgb(96, 92, 100));

            return new Result
            {
                ObjText = WriteObj(mesh, detail, W, H),
                Width = W, Height = H,
                Triangles = mesh.Tris.Count + detail.Tris.Count,
                Windows = windows,
                WallTopY = ytop,
                FrontSide = sd.ToString(),
                Panels = panels,
                Diffuse = diffuse, Lit = lit, Unlit = unlit, WindowMap = winmap,
            };
        }

        /// <summary>OBJ in the plugin's export convention: X mirrored and winding reversed. "Base" is the shell the game
        /// measures (windows, height); "Details" holds signs, awnings and roof boxes.</summary>
        private static string WriteObj(MeshB shell, MeshB details, int W, int H)
        {
            var sb = new StringBuilder();
            sb.Append("# CustomBuildings generated exterior (x mirrored like the plugin's model export)\nmtllib ")
              .Append(ModPaths.ExteriorMtlFile).Append('\n');
            int baseIndex = 1;
            foreach (var (name, m) in new[] { ("Base", shell), ("Details", details) })
                AppendObject(sb, name, m, W, H, ref baseIndex);
            return sb.ToString();
        }

        private static void AppendObject(StringBuilder sb, string name, MeshB m, int W, int H, ref int baseIndex)
        {
            var inv = CultureInfo.InvariantCulture;
            {
                if (m.V.Count == 0) return;
                sb.Append("o ").Append(name).Append("\nusemtl exterior\n");
                foreach (var (x, y, z) in m.V) sb.Append("v ").Append((-x).ToString("0.####", inv)).Append(' ').Append(y.ToString("0.####", inv)).Append(' ').Append(z.ToString("0.####", inv)).Append('\n');
                foreach (var (u, v) in m.Uv) sb.Append("vt ").Append((u / W).ToString("0.######", inv)).Append(' ').Append((1 - v / H).ToString("0.######", inv)).Append('\n');
                foreach (var (x, y, z) in m.N) sb.Append("vn ").Append((-x).ToString("0.####", inv)).Append(' ').Append(y.ToString("0.####", inv)).Append(' ').Append(z.ToString("0.####", inv)).Append('\n');
                foreach (var (a, b, c) in m.Tris)
                {
                    int A = a + baseIndex, B = b + baseIndex, C = c + baseIndex;
                    sb.Append("f ").Append(A).Append('/').Append(A).Append('/').Append(A).Append(' ')
                      .Append(C).Append('/').Append(C).Append('/').Append(C).Append(' ')
                      .Append(B).Append('/').Append(B).Append('/').Append(B).Append('\n');
                }
                baseIndex += m.V.Count;
            }
        }

        /// <summary>
        /// Keeps painted areas when the exterior is made again: every wall, sign or roof piece that still exists with the
        /// same size gets its old pixels back (all four textures). Returns how many pieces were kept.
        /// </summary>
        public static int CarryOver(Result r, string oldFolder, ExteriorInfo old)
        {
            if (old?.Panels == null || old.Panels.Count == 0) return 0;
            var imgs = new[] { old.Diffuse, old.Lit, old.Unlit, old.WindowMap }.Select(f => Img.Load(Path.Combine(oldFolder, f))).ToArray();
            if (imgs.Any(i => i == null)) return 0;
            var oldByKey = old.Panels.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
            var targets = new[] { r.Diffuse, r.Lit, r.Unlit, r.WindowMap };
            int kept = 0;
            foreach (var p in r.Panels)
            {
                if (!oldByKey.TryGetValue(p.Key, out var o)) continue;
                for (int k = 0; k < 4; k++)
                {
                    for (int y = 0; y < p.H; y++)
                        for (int x = 0; x < p.W; x++)
                        {
                            if (o.X + x >= imgs[k].W || o.Y + y >= imgs[k].H) continue;
                            targets[k].Set(p.X + x, p.Y + y, imgs[k].Get(o.X + x, o.Y + y));
                        }
                }
                kept++;
            }
            r.Windows = CountWindows(r.WindowMap.Px, r.Width, r.Height, 3);
            return kept;
        }

        /// <summary>Number of separate white areas in a window map (RGB bytes, stride = bytes per pixel).</summary>
        public static int CountWindows(byte[] px, int w, int h, int bpp)
        {
            var seen = new bool[w * h];
            var stack = new Stack<int>();
            int n = 0;
            bool White(int i) => px[i * bpp] > 127 || px[i * bpp + 1] > 127 || px[i * bpp + 2] > 127;
            for (int i = 0; i < w * h; i++)
            {
                if (seen[i] || !White(i)) continue;
                n++;
                seen[i] = true;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int c = stack.Pop(), x = c % w, y = c / w;
                    void Try(int xx, int yy)
                    {
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) return;
                        int j = yy * w + xx;
                        if (seen[j] || !White(j)) return;
                        seen[j] = true;
                        stack.Push(j);
                    }
                    Try(x - 1, y); Try(x + 1, y); Try(x, y - 1); Try(x, y + 1);
                }
            }
            return n;
        }

        public static void Write(Result r, string folder, ExteriorInfo info)
        {
            info.FrontSide = r.FrontSide;
            info.GroundY = Ground;
            info.TextureWidth = r.Width;
            info.TextureHeight = r.Height;
            info.Panels = r.Panels;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ModPaths.ExteriorObjFile), r.ObjText);
            info.Shapes = (info.Shapes ?? new List<ExteriorShape>()).Where(sh => ShapeWallExists(info, sh)).ToList();
            WriteShapes(folder, info);
            File.WriteAllText(Path.Combine(folder, ModPaths.ExteriorMtlFile),
                $"newmtl exterior\nKd 1 1 1\nmap_Kd {info.Diffuse}\nmap_Ke {info.Lit}\n");
            r.Diffuse.Save(Path.Combine(folder, info.Diffuse));
            r.Lit.Save(Path.Combine(folder, info.Lit));
            r.Unlit.Save(Path.Combine(folder, info.Unlit));
            r.WindowMap.Save(Path.Combine(folder, info.WindowMap));
            File.WriteAllText(Path.Combine(folder, ModPaths.ExteriorInfoFile), JsonSerializer.Serialize(info, Json.Options));
        }

        // ------------------------------------------------------------------ plans from floor files

        private static readonly HashSet<string> OutdoorTypes = new(StringComparer.Ordinal)
        {
            "Outside", "Rooftop", "VentedRooftop", "StreetFrontage", "Yard", "FathomsYard", "Park", "Path",
        };

        /// <summary>Reads one storey from a floor plan: inside cells, window/door walls on the outline, entrance tile pairs.</summary>
        public static StoreyPlan PlanFrom(FloorDoc doc)
        {
            var p = new StoreyPlan();
            if (doc == null) return p;
            for (int a = 0; a < doc.Addresses.Count; a++)
            {
                var type = doc.AddressPreset(a);
                if (OutdoorTypes.Contains(type)) continue;
                // every cell of an indoor address counts, whatever its tile type: stairwells are "none, but indoors",
                // and older edits could leave inside cells at "none"
                foreach (var n in doc.NodesIn(a, 0)) p.Cells[(n.X, n.Y)] = type;
            }

            // walls on outer edges: windows, and doors (those are entrances too)
            for (int a = 0; a < doc.Addresses.Count; a++)
                foreach (var n in doc.NodesIn(a, 0))
                {
                    if (!p.Cells.ContainsKey((n.X, n.Y)) || n.Node["w_d"] is not System.Text.Json.Nodes.JsonArray walls) continue;
                    foreach (var w in walls.OfType<System.Text.Json.Nodes.JsonObject>())
                    {
                        var dir = FloorDoc.DirOf(w);
                        var (d, dx, dy) = dir switch { WallDir.Up => ('N', 0, -1), WallDir.Down => ('S', 0, 1), WallDir.Left => ('W', -1, 0), _ => ('E', 1, 0) };
                        if (p.Cells.ContainsKey((n.X + dx, n.Y + dy))) continue;
                        var kind = GameNames.KindOf(FloorDoc.Str(w, "p_n"));
                        if (kind == GameNames.WallKind.Window) p.Windows.Add((n.X, n.Y, d));
                        else if (kind == GameNames.WallKind.Door || kind == GameNames.WallKind.Entrance) p.Entrances.Add((n.X, n.Y, d));
                    }
                }

            // entrance tile pairs: an inside tile and the tile next to it outside, both flagged
            int npt = Math.Max(1, doc.NodesPerTile);
            var flags = new Dictionary<(int, int), (bool e, bool m)>();
            foreach (var t in doc.Tiles.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                var (tx, ty) = FloorDoc.Coord(t);
                bool m = FloorDoc.Bool(t, "m_e"), e = FloorDoc.Bool(t, "i_e") || m;
                if (e) flags[(tx, ty)] = (e, m);
            }
            foreach (var kv in flags)
                foreach (var (d, dx, dy) in Dirs)
                {
                    var (tx, ty) = kv.Key;
                    if (!flags.TryGetValue((tx + dx, ty + dy), out var other)) continue;
                    // cells of this tile on the side facing the other tile
                    for (int k = 0; k < npt; k++)
                    {
                        int cx = tx * npt + (dx == 0 ? k : dx > 0 ? npt - 1 : 0);
                        int cy = ty * npt + (dy == 0 ? k : dy > 0 ? npt - 1 : 0);
                        if (!p.Cells.ContainsKey((cx, cy)) || p.Cells.ContainsKey((cx + dx, cy + dy))) continue;
                        p.Entrances.Add((cx, cy, d));
                        if (kv.Value.m || other.m) p.Main.Add((cx, cy, d));
                    }
                }
            return p;
        }

        /// <summary>
        /// Adds window walls to a floor plan where the outline has none: on upper storeys one window in the middle of every
        /// tile, on the ground floor shop windows along shops and restaurants. Walls are written on both sides, like the
        /// game's floors. Returns how many edges changed. Needs the wall names from the game export.
        /// </summary>
        public static int AddWindows(FloorDoc doc, bool ground)
        {
            string small = WallIdByName("WindowMediumRectangle"), large = WallIdByName("WindowLargeRectangle");
            if (small == null || large == null) return 0;
            var plan = PlanFrom(doc);
            int changed = 0;
            foreach (var run in Outline(plan.Cells))
                foreach (var c in RunCells(run))
                {
                    var edge = (c.x, c.y, run.D);
                    if (plan.Windows.Contains(edge) || plan.Entrances.Contains(edge)) continue;
                    var dir = run.D switch { 'N' => WallDir.Up, 'S' => WallDir.Down, 'W' => WallDir.Left, _ => WallDir.Right };
                    string id;
                    if (ground)
                    {
                        plan.Cells.TryGetValue(c, out var kind);
                        if (kind != "Retail" && kind != "Eatery") continue;
                        id = large;
                    }
                    else
                    {
                        int along = run.D == 'N' || run.D == 'S' ? c.x : c.y;
                        if (((along % 3) + 3) % 3 != 1) continue;
                        id = small;
                    }
                    int a = doc.AddressAt(c.x, c.y);
                    if (a < 0) continue;
                    bool any = false;
                    for (int v = 0; v < doc.VariationCount(a); v++)
                        any |= doc.SetWall(a, v, c.x, c.y, dir, id, mirror: true);
                    if (any) changed++;
                }
            return changed;
        }

        private static string WallIdByName(string name) =>
            GameNames.Walls.FirstOrDefault(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)).Key;
    }
}

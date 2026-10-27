using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>
    /// 3D shapes on the walls (ledges, pillars, balconies, bay windows, window/door frames). They are boxes that stick out
    /// of a wall; their front shows the wall's own painted texture behind them, so what you paint there is what the shape
    /// looks like. Written as the "Shapes" part of exterior.obj.
    /// </summary>
    public static partial class ExteriorGenerator
    {
        public static readonly string[] ShapeKinds = { "Ledge", "Pillar", "Balcony", "Bay", "Frame" };

        public static float DefaultDepth(string kind) => kind switch
        {
            "Pillar" => 0.4f,
            "Balcony" => 1.5f,
            "Bay" => 1.0f,
            "Frame" => 0.2f,
            _ => 0.3f,
        };

        private const int SwTrim = 0, SwMetal = 1, SwGlass = 2, SwConcrete = 3, SwWall = -1;

        /// <summary>Model height range of a wall or parapet panel.</summary>
        public static (float y0, float y1) PanelRange(ExteriorInfo info, ExteriorPanel p)
        {
            float y0 = info.GroundY + p.Storey * info.StoreyHeight;
            return p.Kind == "Parapet" ? (y0, y0 + Parapet) : (y0, y0 + info.StoreyHeight);
        }

        private static bool IsWallPanel(ExteriorPanel p) => p.Kind == "Wall" || p.Kind == "Parapet";

        private static bool SameWall(ExteriorPanel p, ExteriorShape s) =>
            IsWallPanel(p) && p.Side == s.Side && p.Line == s.Line && p.From == s.From && p.To == s.To;

        public static bool ShapeWallExists(ExteriorInfo info, ExteriorShape s) => info.Panels.Any(p => SameWall(p, s));

        private sealed class WallCtx
        {
            public ExteriorInfo Info;
            public List<(ExteriorPanel p, float y0, float y1)> Panels;
            public float Ax, Az, Tx, Tz, Nx, Nz, Length;
            public ExteriorPanel Swatches;
            public const float PxPerUnit = P / Cell;

            public (float x, float y, float z) Pt(float u, float y, float d) => (Ax + Tx * u + Nx * d, y, Az + Tz * u + Nz * d);
        }

        private static WallCtx Ctx(ExteriorInfo info, ExteriorShape s)
        {
            var panels = info.Panels.Where(p => SameWall(p, s)).Select(p => { var (lo, hi) = PanelRange(info, p); return (p, lo, hi); })
                .OrderBy(x => x.lo).ToList();
            if (panels.Count == 0 || s.Side.Length == 0) return null;
            var run = new Run(s.Side[0], s.Line, s.From, s.To);
            var cells = Ordered(run);
            var (a, _) = EdgePoints(cells[0], run.D);
            var (_, b) = EdgePoints(cells[^1], run.D);
            float dx = b.x - a.x, dz = b.z - a.z, len = MathF.Sqrt(dx * dx + dz * dz);
            var (nx, _, nz) = Normal(run.D);
            return new WallCtx
            {
                Info = info, Panels = panels, Ax = a.x, Az = a.z, Tx = dx / len, Tz = dz / len, Nx = nx, Nz = nz, Length = len,
                Swatches = info.Panels.FirstOrDefault(p => p.Kind == "ShapeColours"),
            };
        }

        /// <summary>Atlas rectangle (u0, vTop, u1, vBottom) of the wall texture behind [ua..ub] × [ya..yb] (inside one panel).</summary>
        private static (float, float, float, float) WallUv(WallCtx c, ExteriorPanel p, float r0, float r1, float ua, float ub, float ya, float yb)
        {
            float maxU = p.W / WallCtx.PxPerUnit;
            ua = Math.Clamp(ua, 0, maxU); ub = Math.Clamp(ub, 0, maxU);
            float Vy(float y) => p.Y + (r1 - Math.Clamp(y, r0, r1)) / (r1 - r0) * p.H;
            return (p.X + ua * WallCtx.PxPerUnit, Vy(yb), p.X + ub * WallCtx.PxPerUnit, Vy(ya));
        }

        private static (float, float, float, float)? SwatchUv(WallCtx c, int k)
        {
            if (k < 0 || c.Swatches == null) return null;
            var s = c.Swatches;
            return (s.X + 16 * k + 3, s.Y + 3, s.X + 16 * k + 13, s.Y + 13);
        }

        /// <summary>
        /// A box on the wall: u along the wall from its left end, y height, d out of the wall. Faces facing the wall are
        /// left out unless <paramref name="back"/>. Each colour index is a swatch, or SwWall to use the wall texture there.
        /// </summary>
        private static void Box(MeshB m, WallCtx c, float u0, float u1, float y0, float y1, float d0, float d1,
                                int front = SwWall, int sides = SwWall, int caps = SwWall, bool back = false)
        {
            if (u1 - u0 < 0.001f || y1 - y0 < 0.001f || d1 - d0 < 0.001f) return;
            var n = (c.Nx, 0f, c.Nz);
            var t = (c.Tx, 0f, c.Tz);
            var mt = (-c.Tx, 0f, -c.Tz);
            var mn = (-c.Nx, 0f, -c.Nz);
            float onePx = 1f / WallCtx.PxPerUnit;

            // the wall panels this box's height covers (outside the wall's range, the nearest panel is stretched)
            float wy0 = c.Panels[0].y0, wy1 = c.Panels[^1].y1;
            var segs = new List<(float ya, float yb, ExteriorPanel p, float r0, float r1)>();
            foreach (var (p, r0, r1) in c.Panels)
            {
                float ya = Math.Max(y0, r0), yb = Math.Min(y1, r1);
                if (p == c.Panels[0].p) ya = y0;
                if (p == c.Panels[^1].p) yb = y1;
                if (yb - ya > 0.0005f) segs.Add((ya, yb, p, r0, r1));
            }
            if (segs.Count == 0) return;
            var (topY, topP, topR0, topR1) = (segs[^1].yb, segs[^1].p, segs[^1].r0, segs[^1].r1);
            var (botY, botP, botR0, botR1) = (segs[0].ya, segs[0].p, segs[0].r0, segs[0].r1);

            foreach (var (ya, yb, p, r0, r1) in segs)
            {
                var fuv = SwatchUv(c, front) ?? WallUv(c, p, r0, r1, u0, u1, ya, yb);
                m.Quad(c.Pt(u0, ya, d1), c.Pt(u1, ya, d1), c.Pt(u1, yb, d1), c.Pt(u0, yb, d1), n, fuv);
                if (back)
                    m.Quad(c.Pt(u1, ya, d0), c.Pt(u0, ya, d0), c.Pt(u0, yb, d0), c.Pt(u1, yb, d0), mn, fuv);
                var luv = SwatchUv(c, sides) ?? WallUv(c, p, r0, r1, u0, u0 + onePx, ya, yb);
                var ruv = SwatchUv(c, sides) ?? WallUv(c, p, r0, r1, u1 - onePx, u1, ya, yb);
                m.Quad(c.Pt(u0, ya, d0), c.Pt(u0, ya, d1), c.Pt(u0, yb, d1), c.Pt(u0, yb, d0), mt, luv);
                m.Quad(c.Pt(u1, ya, d1), c.Pt(u1, ya, d0), c.Pt(u1, yb, d0), c.Pt(u1, yb, d1), t, ruv);
            }
            float pxY(float r0, float r1, ExteriorPanel p) => (r1 - r0) / p.H;
            var tuv = SwatchUv(c, caps) ?? WallUv(c, topP, topR0, topR1, u0, u1, topY - pxY(topR0, topR1, topP), topY);
            var buv = SwatchUv(c, caps) ?? WallUv(c, botP, botR0, botR1, u0, u1, botY, botY + pxY(botR0, botR1, botP));
            m.Quad(c.Pt(u0, y1, d1), c.Pt(u1, y1, d1), c.Pt(u1, y1, d0), c.Pt(u0, y1, d0), (0f, 1f, 0f), tuv);
            m.Quad(c.Pt(u0, y0, d0), c.Pt(u1, y0, d0), c.Pt(u1, y0, d1), c.Pt(u0, y0, d1), (0f, -1f, 0f), buv);
        }

        private static void BuildShape(MeshB m, ExteriorInfo info, ExteriorShape s)
        {
            var c = Ctx(info, s);
            if (c == null) return;
            float u0 = Math.Clamp(Math.Min(s.U0, s.U1), 0, c.Length), u1 = Math.Clamp(Math.Max(s.U0, s.U1), 0, c.Length);
            float y0 = Math.Min(s.Y0, s.Y1), y1 = Math.Max(s.Y0, s.Y1);
            float d = Math.Clamp(s.Depth, 0.02f, 6f);
            switch (s.Kind)
            {
                case "Bay":
                    Box(m, c, u0, u1, y0, y1, 0, d, SwWall, SwGlass, SwConcrete);
                    break;

                case "Balcony":
                {
                    float slab = Math.Min(0.3f, (y1 - y0) * 0.3f);
                    Box(m, c, u0, u1, y0, y0 + slab, 0, d, SwConcrete, SwConcrete, SwConcrete);
                    float railTop = y1, rail = 0.08f, bar = 0.05f, yb = y0 + slab;
                    if (railTop - yb < 0.2f) break;
                    // top rails: front and both sides
                    Box(m, c, u0, u1, railTop - rail, railTop, d - rail, d, SwMetal, SwMetal, SwMetal, back: true);
                    Box(m, c, u0, u0 + rail, railTop - rail, railTop, 0, d - rail, SwMetal, SwMetal, SwMetal);
                    Box(m, c, u1 - rail, u1, railTop - rail, railTop, 0, d - rail, SwMetal, SwMetal, SwMetal);
                    // bars
                    int nFront = Math.Max(2, (int)((u1 - u0) / 0.3f));
                    for (int i = 0; i <= nFront; i++)
                    {
                        float u = u0 + (u1 - u0 - bar) * i / nFront;
                        Box(m, c, u, u + bar, yb, railTop - rail, d - rail + 0.015f, d - 0.015f, SwMetal, SwMetal, SwMetal, back: true);
                    }
                    int nSide = Math.Max(1, (int)(d / 0.3f));
                    for (int i = 1; i < nSide; i++)
                    {
                        float dd = d * i / nSide;
                        Box(m, c, u0 + 0.015f, u0 + rail - 0.015f, yb, railTop - rail, dd, dd + bar, SwMetal, SwMetal, SwMetal, back: true);
                        Box(m, c, u1 - rail + 0.015f, u1 - 0.015f, yb, railTop - rail, dd, dd + bar, SwMetal, SwMetal, SwMetal, back: true);
                    }
                    break;
                }

                case "Frame":
                {
                    // a surround that sticks out, so the window or door behind it looks set into the wall
                    float fw = Math.Min(0.17f, Math.Min(u1 - u0, y1 - y0) / 3);
                    Box(m, c, u0, u0 + fw, y0, y1, 0, d);
                    Box(m, c, u1 - fw, u1, y0, y1, 0, d);
                    Box(m, c, u0 + fw, u1 - fw, y1 - fw, y1, 0, d);
                    Box(m, c, u0 - 0.1f, u1 + 0.1f, y0 - 0.06f, y0 + fw * 0.6f, 0, d + 0.08f);   // sill
                    break;
                }

                default:   // Ledge, Pillar
                    Box(m, c, u0, u1, y0, y1, 0, d);
                    break;
            }
        }

        /// <summary>Rewrites the "Shapes" part of exterior.obj from info.Shapes (the rest of the file stays as it is).</summary>
        public static void WriteShapes(string folder, ExteriorInfo info)
        {
            var path = Path.Combine(folder, ModPaths.ExteriorObjFile);
            if (!File.Exists(path)) return;
            var kept = new List<string>();
            int vCount = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("o Shapes", StringComparison.Ordinal)) break;
                kept.Add(line);
                if (line.StartsWith("v ", StringComparison.Ordinal)) vCount++;
            }
            var sb = new StringBuilder();
            foreach (var l in kept) sb.Append(l).Append('\n');
            var m = new MeshB();
            foreach (var s in info.Shapes ?? new List<ExteriorShape>()) BuildShape(m, info, s);
            int w = info.TextureWidth > 0 ? info.TextureWidth : 2048, h = info.TextureHeight > 0 ? info.TextureHeight : 1024;
            int baseIndex = vCount + 1;
            AppendObject(sb, "Shapes", m, w, h, ref baseIndex);
            File.WriteAllText(path, sb.ToString());
        }
    }
}

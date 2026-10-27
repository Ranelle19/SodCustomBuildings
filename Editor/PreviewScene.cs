using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace BuildingEditor
{
    /// <summary>One flat polygon in the 3D preview. World axes: X = cell x, Z = cell y, Y = up.</summary>
    public sealed class Face
    {
        public Vector3[] P;
        public Color Color;
        public int Level;
        public bool Outline;
    }

    /// <summary>One storey of the building in the preview, and the floor plan shown for it.</summary>
    public sealed class FloorSlot
    {
        public int Level;                 // 0 = ground, negative = basements
        public string Layout;             // e.g. "Layout 2"
        public List<string> Candidates = new();
        public int Chosen;
        public FloorDoc Doc;              // resolved plan, or null when missing
        public string ChosenName => Candidates.Count == 0 ? "" : Candidates[Math.Clamp(Chosen, 0, Candidates.Count - 1)];
    }

    public sealed class SceneOptions
    {
        public float LevelHeight = 3f;
        public float WallHeight = 2.6f;
        public float Spread;              // extra gap between storeys
        public bool Walls = true;
        public bool OuterWalls = true;
        /// <summary>False draws outer edges as low "dollhouse" walls so you can see into the rooms.</summary>
        public bool OuterWallsFull;
        public bool RandomVariations;
        public int Seed = 1;
    }

    /// <summary>Turns floor plans into faces for the preview. Only reads the documents; never changes them.</summary>
    public static class PreviewScene
    {
        public static float LevelY(int level, SceneOptions o) => level * (o.LevelHeight + o.Spread);

        public static List<Face> Build(IEnumerable<FloorSlot> slots, SceneOptions o)
        {
            var faces = new List<Face>();
            var list = slots.Where(s => s.Doc != null).ToList();
            var levels = new HashSet<int>(list.Select(s => s.Level));
            foreach (var s in list) AddFloor(faces, s.Doc, s.Level, o, levels.Contains(s.Level + 1));
            return faces;
        }

        private struct Cell
        {
            public int A, R;
            public JsonObject Node;
        }

        private static void AddFloor(List<Face> faces, FloorDoc doc, int level, SceneOptions o, bool storeyAbove)
        {
            float y0 = LevelY(level, o);
            var rnd = new Random(o.Seed * 7919 + level * 131);

            // which variation of each address is shown
            var chosenVar = new int[doc.Addresses.Count];
            for (int a = 0; a < doc.Addresses.Count; a++)
            {
                int count = doc.VariationCount(a);
                chosenVar[a] = o.RandomVariations && count > 1 ? rnd.Next(count) : 0;
            }

            // occupancy
            var cells = new Dictionary<(int x, int y), Cell>();
            for (int a = 0; a < doc.Addresses.Count; a++)
                foreach (var n in doc.NodesIn(a, chosenVar[a]))
                    cells.TryAdd((n.X, n.Y), new Cell { A = a, R = n.R, Node = n.Node });

            // floor: one quad per cell (large faces would sort badly in this painter's-algorithm renderer)
            foreach (var kv in cells)
            {
                var (x, y) = kv.Key;
                int type = TileType(kv.Value);
                if (type == 0 || type == 3) continue; // 0 = open, 3 = ceiling only: no floor to draw
                var col = Shade(doc.AddressColor(kv.Value.A), kv.Value.R);
                if (type == 4) col = Blend(col, Color.Gray, 0.5f);
                faces.Add(Quad(new Vector3(x, y0, y), new Vector3(x + 1, y0, y),
                               new Vector3(x + 1, y0, y + 1), new Vector3(x, y0, y + 1), col, level, false));
            }

            // walls from the plan (deduplicated: both sides of a shared edge usually carry the same wall)
            var edges = new HashSet<(int x, int y, bool vertical)>();
            if (o.Walls)
            {
                for (int a = 0; a < doc.Addresses.Count; a++)
                    foreach (var n in doc.NodesIn(a, chosenVar[a]))
                    {
                        if (n.Node["w_d"] is not JsonArray walls) continue;
                        foreach (var w in walls.OfType<JsonObject>())
                        {
                            var dir = FloorDoc.DirOf(w);
                            var key = EdgeKey(n.X, n.Y, dir);
                            if (!edges.Add(key)) continue;
                            AddWall(faces, n.X, n.Y, dir, FloorDoc.Str(w, "p_n"), y0, level, o);
                        }
                    }
            }

            // outer walls where the plan has an edge with nothing beyond it and no wall drawn
            if (o.OuterWalls)
            {
                foreach (var (x, y) in cells.Keys)
                    foreach (WallDir d in Enum.GetValues(typeof(WallDir)))
                    {
                        var (dx, dy) = Step(d);
                        if (cells.ContainsKey((x + dx, y + dy))) continue;
                        if (!edges.Add(EdgeKey(x, y, d))) continue;
                        var (p, q) = EdgePoints(x, y, d);
                        AddPanel(faces, p, q, y0, y0 + o.WallHeight * (o.OuterWallsFull ? 1f : 0.35f), Color.FromArgb(120, 124, 132), level);
                    }
            }

            // entrances, stairs, lifts
            int npt = Math.Max(1, doc.NodesPerTile);
            foreach (var t in doc.Tiles.OfType<JsonObject>())
            {
                var (tx, ty) = FloorDoc.Coord(t);
                float x0 = tx * npt, z0 = ty * npt, x1 = x0 + npt, z1 = z0 + npt;
                float lift = y0 + 0.03f;

                if (FloorDoc.Bool(t, "m_e") || FloorDoc.Bool(t, "i_e"))
                {
                    var col = FloorDoc.Bool(t, "m_e") ? Color.Gold : Color.LimeGreen;
                    float inset = npt * 0.25f;
                    faces.Add(Quad(new Vector3(x0 + inset, lift, z0 + inset), new Vector3(x1 - inset, lift, z0 + inset),
                                   new Vector3(x1 - inset, lift, z1 - inset), new Vector3(x0 + inset, lift, z1 - inset), col, level, true));
                }

                if (FloorDoc.Bool(t, "s_t"))
                    AddStairs(faces, x0, z0, npt, y0, storeyAbove ? y0 + o.LevelHeight + o.Spread : y0 + o.WallHeight * 0.5f,
                              FloorDoc.Int(t, "s_r"), level);

                if (FloorDoc.Bool(t, "e_l"))
                {
                    float m = npt * 0.2f;
                    AddBox(faces, x0 + m, z0 + m, x1 - m, z1 - m, y0, y0 + o.LevelHeight + o.Spread, Color.FromArgb(150, 147, 112, 219), level);
                }
            }
        }

        private static int TileType(Cell c) => FloorDoc.Int(c.Node, "f_t", 1);

        // ------------------------------------------------------------------ geometry helpers

        private static (int dx, int dy) Step(WallDir d) => d switch
        {
            WallDir.Up => (0, -1), WallDir.Right => (1, 0), WallDir.Down => (0, 1), _ => (-1, 0),
        };

        /// <summary>Same key for an edge whichever cell it's seen from.</summary>
        private static (int, int, bool) EdgeKey(int x, int y, WallDir d) => d switch
        {
            WallDir.Up => (x, y, false),
            WallDir.Down => (x, y + 1, false),
            WallDir.Left => (x, y, true),
            _ => (x + 1, y, true),
        };

        private static (Vector2 p, Vector2 q) EdgePoints(int x, int y, WallDir d) => d switch
        {
            WallDir.Up => (new Vector2(x, y), new Vector2(x + 1, y)),
            WallDir.Down => (new Vector2(x, y + 1), new Vector2(x + 1, y + 1)),
            WallDir.Left => (new Vector2(x, y), new Vector2(x, y + 1)),
            _ => (new Vector2(x + 1, y), new Vector2(x + 1, y + 1)),
        };

        private static void AddWall(List<Face> faces, int x, int y, WallDir d, string preset, float y0, int level, SceneOptions o)
        {
            var (p, q) = EdgePoints(x, y, d);
            float h = o.WallHeight;
            var kind = GameNames.KindOf(preset);
            var wallCol = Color.FromArgb(214, 210, 200);

            if (kind == GameNames.WallKind.Door || kind == GameNames.WallKind.Entrance)
            {
                var doorCol = kind == GameNames.WallKind.Entrance ? Color.LimeGreen : Color.Orange;
                AddPanel(faces, p, q, y0, y0 + h * 0.78f, doorCol, level);
                AddPanel(faces, p, q, y0 + h * 0.78f, y0 + h, wallCol, level);
            }
            else if (kind == GameNames.WallKind.Window)
            {
                AddPanel(faces, p, q, y0, y0 + h * 0.35f, wallCol, level);
                AddPanel(faces, p, q, y0 + h * 0.35f, y0 + h * 0.8f, Color.FromArgb(140, 90, 180, 255), level);
                AddPanel(faces, p, q, y0 + h * 0.8f, y0 + h, wallCol, level);
            }
            else if (kind == GameNames.WallKind.Opening)
            {
                // an opening: draw only a thin header so it reads as a gap
                AddPanel(faces, p, q, y0 + h * 0.9f, y0 + h, wallCol, level);
            }
            else if (kind == GameNames.WallKind.Railing)
                AddPanel(faces, p, q, y0, y0 + h * 0.35f, Color.Tan, level);
            else
                AddPanel(faces, p, q, y0, y0 + h, wallCol, level);
        }

        private static void AddPanel(List<Face> faces, Vector2 p, Vector2 q, float yLow, float yHigh, Color col, int level)
        {
            faces.Add(Quad(new Vector3(p.X, yLow, p.Y), new Vector3(q.X, yLow, q.Y),
                           new Vector3(q.X, yHigh, q.Y), new Vector3(p.X, yHigh, p.Y), col, level, true));
        }

        private static void AddBox(List<Face> faces, float x0, float z0, float x1, float z1, float yLow, float yHigh, Color col, int level)
        {
            AddPanel(faces, new Vector2(x0, z0), new Vector2(x1, z0), yLow, yHigh, col, level);
            AddPanel(faces, new Vector2(x1, z0), new Vector2(x1, z1), yLow, yHigh, col, level);
            AddPanel(faces, new Vector2(x1, z1), new Vector2(x0, z1), yLow, yHigh, col, level);
            AddPanel(faces, new Vector2(x0, z1), new Vector2(x0, z0), yLow, yHigh, col, level);
        }

        /// <summary>
        /// A ramp across the tile that climbs to the next storey, split into one-cell pieces so it sorts correctly.
        /// Rotation 0 climbs toward -y (up on the 2D canvas), 90 toward +x, 180 toward +y, 270 toward -x.
        /// </summary>
        private static void AddStairs(List<Face> faces, float x0, float z0, int size, float yLow, float yHigh, int rotation, int level)
        {
            var col = Color.FromArgb(176, 132, 86);
            int rot = ((rotation % 360) + 360) % 360;
            float x1 = x0 + size, z1 = z0 + size;

            // u = across the stairs, v = along the climb (0 = bottom)
            Vector3 P(float u, float v, float h) => rot switch
            {
                90 => new Vector3(x0 + v, h, z0 + u),
                180 => new Vector3(x0 + u, h, z0 + v),
                270 => new Vector3(x1 - v, h, z0 + u),
                _ => new Vector3(x0 + u, h, z1 - v),
            };

            for (int j = 0; j < size; j++)
            {
                float h0 = yLow + (yHigh - yLow) * j / size, h1 = yLow + (yHigh - yLow) * (j + 1) / size;
                for (int i = 0; i < size; i++)
                    faces.Add(Quad(P(i, j, h0), P(i + 1, j, h0), P(i + 1, j + 1, h1), P(i, j + 1, h1), col, level, true));
            }
        }

        private static Face Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, int level, bool outline) =>
            new() { P = new[] { a, b, c, d }, Color = col, Level = level, Outline = outline };

        // ------------------------------------------------------------------ colours (match the 2D canvas)

        internal static Color Shade(Color c, int room)
        {
            float[] f = { 0f, 0.28f, -0.22f, 0.45f, -0.38f, 0.15f, -0.1f, 0.35f };
            float k = f[((room % f.Length) + f.Length) % f.Length];
            return k >= 0 ? Blend(c, Color.White, k) : Blend(c, Color.Black, -k);
        }

        internal static Color Blend(Color a, Color b, float t) => Color.FromArgb(a.A,
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}

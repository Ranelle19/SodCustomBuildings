using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuildingEditor
{
    /// <summary>A node (one grid cell) inside a room of an address variation.</summary>
    public sealed class NodeRef
    {
        public int A, V, R;
        public JsonObject Room;
        public JsonObject Node;
        public int X, Y;
    }

    public enum WallDir { Up = 0, Right = 1, Down = 2, Left = 3 }

    /// <summary>
    /// A floor plan in the game's own FloorSaveData JSON format, edited in place so every field we don't understand is
    /// kept exactly as it was.
    ///
    ///   floorName, size{x,y}, defaultFloorHeight, defaultCeilingHeight
    ///   a_d[]  addresses:  p_n (AddressPreset), e_c (editor colour), vs[] layout variations
    ///          vs[]: r_d[] rooms: id, l (layout name), n_d[] nodes: f_c{x,y}, f_h, f_t (FloorTileType), f_r, w_d[] walls: w_o{x,y}, p_n
    ///   t_d[]  tiles: f_c{x,y}, i_e, m_e, s_t, s_r, e_l, e_r
    /// </summary>
    public sealed class FloorDoc
    {
        public JsonObject Root { get; private set; }
        public bool ReadOnly;
        public bool Dirty;

        /// <summary>Grid size in nodes, and how many nodes make up one tile (worked out from the data, see Analyze).</summary>
        public int Width { get; private set; } = 1;
        public int Height { get; private set; } = 1;
        public int NodesPerTile { get; private set; } = 1;
        public int NodesPerTileOverride;
        /// <summary>Magnitude used in wall offsets (w_o); read from the data.</summary>
        public double WallOffset { get; private set; } = 0.5;

        private JsonObject _nodeTemplate;
        private readonly LinkedList<string> _undo = new();
        private readonly Stack<string> _redo = new();
        private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

        public FloorDoc(JsonObject root)
        {
            Root = root;
            Analyze();
        }

        public static FloorDoc Parse(string json)
        {
            var node = JsonNode.Parse(json.TrimStart('﻿')) as JsonObject;
            if (node == null) throw new FormatException("The floor file is not a JSON object.");
            return new FloorDoc(node);
        }

        public string ToJson() => Root.ToJsonString(Compact);
        public FloorDoc Clone() => Parse(ToJson());

        // ------------------------------------------------------------------ basic fields

        public string Name
        {
            get => Str(Root, "floorName");
            set => Root["floorName"] = value;
        }

        public int DefaultFloorHeight
        {
            get => Int(Root, "defaultFloorHeight");
            set => Root["defaultFloorHeight"] = value;
        }

        public int DefaultCeilingHeight
        {
            get => Int(Root, "defaultCeilingHeight");
            set => Root["defaultCeilingHeight"] = value;
        }

        public string SizeText
        {
            get
            {
                var s = Root["size"] as JsonObject;
                return s == null ? "?" : $"{Num(s, "x")} x {Num(s, "y")}";
            }
        }

        public JsonArray Addresses => Arr(Root, "a_d");
        public JsonArray Tiles => Arr(Root, "t_d");
        public JsonObject Address(int a) => (JsonObject)Addresses[a];
        public JsonArray Variations(int a) => Arr(Address(a), "vs");
        public JsonArray Rooms(int a, int v) => Arr((JsonObject)Variations(a)[v], "r_d");
        public static JsonArray NodesOf(JsonObject room) => Arr(room, "n_d");

        public int VariationCount(int a) => a >= 0 && a < Addresses.Count ? Variations(a).Count : 0;
        public int RoomCount(int a, int v) => v >= 0 && v < VariationCount(a) ? Rooms(a, v).Count : 0;

        public string AddressPreset(int a) => Str(Address(a), "p_n");
        public void SetAddressPreset(int a, string preset) { Address(a)["p_n"] = preset; Dirty = true; }

        public Color AddressColor(int a)
        {
            var c = Address(a)["e_c"] as JsonObject;
            if (c == null) return Palette(a);
            int R(string k) => Math.Clamp((int)Math.Round(Num(c, k) * 255), 0, 255);
            var col = Color.FromArgb(255, R("r"), R("g"), R("b"));
            return col.R + col.G + col.B < 30 ? Palette(a) : col;
        }

        public void SetAddressColor(int a, Color col)
        {
            Address(a)["e_c"] = new JsonObject
            {
                ["r"] = Math.Round(col.R / 255.0, 4), ["g"] = Math.Round(col.G / 255.0, 4),
                ["b"] = Math.Round(col.B / 255.0, 4), ["a"] = 1.0
            };
            Dirty = true;
        }

        public static Color Palette(int i)
        {
            Color[] p = { Color.SteelBlue, Color.IndianRed, Color.SeaGreen, Color.Goldenrod, Color.MediumPurple, Color.Teal,
                          Color.Chocolate, Color.OliveDrab, Color.PaleVioletRed, Color.CadetBlue, Color.Peru, Color.SlateBlue };
            return p[((i % p.Length) + p.Length) % p.Length];
        }

        public JsonObject Room(int a, int v, int r) => (JsonObject)Rooms(a, v)[r];

        /// <summary>Game addresses start with a room named "Null". Shops keep it empty; the area outside the building lives in one.</summary>
        public const string NullRoom = "Null";
        public bool IsNullRoom(int a, int v, int r) => RoomLayout(a, v, r) == NullRoom;

        /// <summary>First room that isn't the Null room, or -1.</summary>
        public int FirstRealRoom(int a, int v)
        {
            for (int r = 0; r < RoomCount(a, v); r++)
                if (!IsNullRoom(a, v, r)) return r;
            return -1;
        }
        public int RoomId(int a, int v, int r) => Int(Room(a, v, r), "id");
        public string RoomLayout(int a, int v, int r) => Str(Room(a, v, r), "l");
        public void SetRoomLayout(int a, int v, int r, string l) { Room(a, v, r)["l"] = l; Dirty = true; }

        // ------------------------------------------------------------------ json helpers

        public static JsonArray Arr(JsonObject o, string k)
        {
            if (o[k] is JsonArray a) return a;
            var n = new JsonArray();
            o[k] = n;
            return n;
        }

        public static string Str(JsonObject o, string k, string def = "")
        {
            if (o?[k] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            return def;
        }

        public static double Num(JsonObject o, string k, double def = 0)
        {
            if (o?[k] is JsonValue v)
            {
                if (v.TryGetValue<double>(out var d)) return d;
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<float>(out var f)) return f;
            }
            return def;
        }

        public static int Int(JsonObject o, string k, int def = 0)
        {
            if (o?[k] is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<long>(out var l)) return (int)l;
                if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
            }
            return def;
        }

        public static bool Bool(JsonObject o, string k)
        {
            if (o?[k] is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<int>(out var i)) return i != 0;
            }
            return false;
        }

        public static (int x, int y) Coord(JsonObject node)
        {
            var c = node["f_c"] as JsonObject;
            return ((int)Math.Round(Num(c, "x")), (int)Math.Round(Num(c, "y")));
        }

        private static JsonObject MakeCoord(int x, int y) => new() { ["x"] = x, ["y"] = y };
        private static JsonObject CloneObj(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString());

        // ------------------------------------------------------------------ analysis

        /// <summary>Works out grid size, nodes per tile and wall offset size from the data itself.</summary>
        public void Analyze()
        {
            int mnx = -1, mny = -1, mtx = -1, mty = -1;
            var offsets = new Dictionary<double, int>();
            for (int a = 0; a < Addresses.Count; a++)
                for (int v = 0; v < VariationCount(a); v++)
                    foreach (var n in NodesIn(a, v))
                    {
                        mnx = Math.Max(mnx, n.X);
                        mny = Math.Max(mny, n.Y);
                        if (n.Node["w_d"] is JsonArray walls)
                            foreach (var w in walls.OfType<JsonObject>())
                            {
                                var o = w["w_o"] as JsonObject;
                                var m = Math.Round(Math.Max(Math.Abs(Num(o, "x")), Math.Abs(Num(o, "y"))), 3);
                                if (m > 0) offsets[m] = offsets.TryGetValue(m, out var c) ? c + 1 : 1;
                            }
                    }
            foreach (var t in Tiles.OfType<JsonObject>())
            {
                var (x, y) = Coord(t);
                mtx = Math.Max(mtx, x);
                mty = Math.Max(mty, y);
            }

            var size = Root["size"] as JsonObject;
            int sx = (int)Math.Round(Num(size, "x")), sy = (int)Math.Round(Num(size, "y"));

            int r = 1;
            if (mtx >= 0 && mnx >= 0) r = Math.Max(1, (int)Math.Round((mnx + 1.0) / (mtx + 1.0)));
            else if (sx > 0 && mnx >= 0) r = Math.Max(1, (int)Math.Round((mnx + 1.0) / sx));
            if (NodesPerTileOverride > 0) r = NodesPerTileOverride;
            NodesPerTile = r;

            Width = Math.Max(mnx + 1, (mtx + 1) * r);
            Height = Math.Max(mny + 1, (mty + 1) * r);
            if (Width <= 0) Width = Math.Max(1, sx) * r;
            if (Height <= 0) Height = Math.Max(1, sy) * r;
            // room to paint one tile beyond the current edge
            Width += r;
            Height += r;

            if (offsets.Count > 0) WallOffset = offsets.OrderByDescending(kv => kv.Value).First().Key;
        }

        // ------------------------------------------------------------------ queries

        public IEnumerable<NodeRef> NodesIn(int a, int v)
        {
            if (a < 0 || a >= Addresses.Count || v < 0 || v >= VariationCount(a)) yield break;
            var rooms = Rooms(a, v);
            for (int r = 0; r < rooms.Count; r++)
            {
                if (rooms[r] is not JsonObject room) continue;
                foreach (var n in NodesOf(room).OfType<JsonObject>())
                {
                    var (x, y) = Coord(n);
                    yield return new NodeRef { A = a, V = v, R = r, Room = room, Node = n, X = x, Y = y };
                }
            }
        }

        public NodeRef Find(int a, int v, int x, int y) => NodesIn(a, v).FirstOrDefault(n => n.X == x && n.Y == y);

        /// <summary>The address that owns a cell (looks in every variation), or -1.</summary>
        public int AddressAt(int x, int y)
        {
            for (int a = 0; a < Addresses.Count; a++)
                for (int v = 0; v < VariationCount(a); v++)
                    if (Find(a, v, x, y) != null) return a;
            return -1;
        }

        public JsonObject TileAt(int tx, int ty) =>
            Tiles.OfType<JsonObject>().FirstOrDefault(t => Coord(t) == (tx, ty));

        public (int tx, int ty) TileOf(int x, int y) => (FloorDiv(x, NodesPerTile), FloorDiv(y, NodesPerTile));
        private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

        public int NextRoomId()
        {
            int max = 0;
            for (int a = 0; a < Addresses.Count; a++)
                for (int v = 0; v < VariationCount(a); v++)
                    for (int r = 0; r < RoomCount(a, v); r++)
                        max = Math.Max(max, RoomId(a, v, r));
            return max + 1;
        }

        // ------------------------------------------------------------------ undo

        public void PushUndo()
        {
            _undo.AddLast(ToJson());
            if (_undo.Count > 100) _undo.RemoveFirst();
            _redo.Clear();
        }

        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            _redo.Push(ToJson());
            Root = (JsonObject)JsonNode.Parse(_undo.Last.Value);
            _undo.RemoveLast();
            Dirty = true;
            Analyze();
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            _undo.AddLast(ToJson());
            Root = (JsonObject)JsonNode.Parse(_redo.Pop());
            Dirty = true;
            Analyze();
            return true;
        }

        public void ReplaceAll(string json)
        {
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null) throw new FormatException("Not a JSON object.");
            PushUndo();
            Root = node;
            Dirty = true;
            Analyze();
        }

        // ------------------------------------------------------------------ editing: nodes

        private JsonObject NodeTemplate()
        {
            JsonObject t = _nodeTemplate;
            if (t == null)
                for (int a = 0; a < Addresses.Count && t == null; a++)
                    if (AddressPreset(a) != "Outside") t = NodesIn(a, 0).FirstOrDefault()?.Node;
            JsonObject n = t != null
                ? CloneObj(t)
                : new JsonObject { ["f_c"] = MakeCoord(0, 0), ["f_h"] = 0, ["f_t"] = 1, ["f_r"] = "", ["w_d"] = new JsonArray() };
            n["w_d"] = new JsonArray();
            return n;
        }

        /// <summary>Move a cell to another room of the same address variation.</summary>
        public bool PaintRoom(int a, int v, int x, int y, int targetRoom)
        {
            var n = Find(a, v, x, y);
            if (n == null || n.R == targetRoom || targetRoom < 0 || targetRoom >= RoomCount(a, v)) return false;
            NodesOf(n.Room).Remove(n.Node);
            NodesOf(Room(a, v, targetRoom)).Add(n.Node);
            Dirty = true;
            return true;
        }

        /// <summary>Give a cell to an address (taking it from whoever owned it). It is added to every variation.</summary>
        public bool PaintAddress(int x, int y, int targetA, int currentV, int currentRoom)
        {
            if (targetA < 0 || targetA >= Addresses.Count || x < 0 || y < 0) return false;
            int oldA = AddressAt(x, y);
            if (oldA == targetA) return false;

            JsonObject template = null;
            if (oldA >= 0)
                for (int v = 0; v < VariationCount(oldA); v++)
                {
                    var n = Find(oldA, v, x, y);
                    if (n == null) continue;
                    template ??= n.Node;
                    NodesOf(n.Room).Remove(n.Node);
                }

            var node = template != null ? CloneObj(template) : NodeTemplate();
            node["f_c"] = MakeCoord(x, y);
            node["w_d"] = new JsonArray();
            // Cells inside a building always have a floor; the "Outside" ring never does. A template copied from the other
            // side (e.g. painting a shop over the outside ring's cells) would get that wrong.
            bool outside = AddressPreset(targetA) == "Outside";
            if (outside) node["f_t"] = 0;
            else if (Int(node, "f_t") == 0) node["f_t"] = 1;
            if (!outside && Int(node, "f_h") < 0) node["f_h"] = 0;

            if (VariationCount(targetA) == 0) Variations(targetA).Add(new JsonObject { ["r_d"] = new JsonArray() });
            for (int v = 0; v < VariationCount(targetA); v++)
            {
                if (RoomCount(targetA, v) == 0) AddRoom(targetA, v);
                int r = v == currentV && currentRoom >= 0 && currentRoom < RoomCount(targetA, v)
                    ? currentRoom
                    : NearestRoom(targetA, v, x, y);
                NodesOf(Room(targetA, v, r)).Add(CloneObj(node));
            }

            var (tx, ty) = TileOf(x, y);
            EnsureTile(tx, ty);
            Dirty = true;
            return true;
        }

        private int NearestRoom(int a, int v, int x, int y)
        {
            int best = Math.Max(0, FirstRealRoom(a, v)), bestD = int.MaxValue;
            foreach (var n in NodesIn(a, v))
            {
                if (IsNullRoom(a, v, n.R)) continue;
                int d = Math.Abs(n.X - x) + Math.Abs(n.Y - y);
                if (d < bestD) { bestD = d; best = n.R; }
            }
            return best;
        }

        public bool Erase(int x, int y)
        {
            bool any = false;
            for (int a = 0; a < Addresses.Count; a++)
                for (int v = 0; v < VariationCount(a); v++)
                {
                    var n = Find(a, v, x, y);
                    if (n == null) continue;
                    if (AddressPreset(a) != "Outside") _nodeTemplate ??= CloneObj(n.Node);
                    NodesOf(n.Room).Remove(n.Node);
                    any = true;
                }
            if (any) Dirty = true;
            return any;
        }

        public bool SetTileType(int x, int y, int type)
        {
            bool any = false;
            for (int a = 0; a < Addresses.Count; a++)
                for (int v = 0; v < VariationCount(a); v++)
                {
                    var n = Find(a, v, x, y);
                    if (n == null || Int(n.Node, "f_t") == type) continue;
                    n.Node["f_t"] = type;
                    any = true;
                }
            if (any) Dirty = true;
            return any;
        }

        public bool SetNodeHeight(int x, int y, int h)
        {
            bool any = false;
            for (int a = 0; a < Addresses.Count; a++)
                for (int v = 0; v < VariationCount(a); v++)
                {
                    var n = Find(a, v, x, y);
                    if (n == null || Int(n.Node, "f_h") == h) continue;
                    n.Node["f_h"] = h;
                    any = true;
                }
            if (any) Dirty = true;
            return any;
        }

        // ------------------------------------------------------------------ editing: walls

        public static WallDir DirOf(JsonObject wall)
        {
            var o = wall["w_o"] as JsonObject;
            double x = Num(o, "x"), y = Num(o, "y");
            if (Math.Abs(x) > Math.Abs(y)) return x > 0 ? WallDir.Right : WallDir.Left;
            return y > 0 ? WallDir.Down : WallDir.Up;
        }

        private JsonObject Offset(WallDir d) => d switch
        {
            WallDir.Up => new JsonObject { ["x"] = 0.0, ["y"] = -WallOffset },
            WallDir.Right => new JsonObject { ["x"] = WallOffset, ["y"] = 0.0 },
            WallDir.Down => new JsonObject { ["x"] = 0.0, ["y"] = WallOffset },
            _ => new JsonObject { ["x"] = -WallOffset, ["y"] = 0.0 },
        };

        private static WallDir Opposite(WallDir d) => (WallDir)(((int)d + 2) % 4);
        private static (int dx, int dy) Step(WallDir d) => d switch
        {
            WallDir.Up => (0, -1), WallDir.Right => (1, 0), WallDir.Down => (0, 1), _ => (-1, 0),
        };

        private bool SetWallOn(JsonObject node, WallDir d, string preset)
        {
            var walls = Arr(node, "w_d");
            bool changed = false;
            foreach (var w in walls.OfType<JsonObject>().Where(w => DirOf(w) == d).ToList())
            {
                walls.Remove(w);
                changed = true;
            }
            if (!string.IsNullOrEmpty(preset))
            {
                walls.Add(new JsonObject { ["w_o"] = Offset(d), ["p_n"] = preset });
                changed = true;
            }
            return changed;
        }

        /// <summary>Set (or with preset null, remove) the wall on one side of a cell. Optionally mirrors it onto the neighbour.</summary>
        public bool SetWall(int a, int v, int x, int y, WallDir d, string preset, bool mirror)
        {
            var n = Find(a, v, x, y);
            if (n == null) return false;
            bool changed = SetWallOn(n.Node, d, preset);

            if (mirror)
            {
                var (dx, dy) = Step(d);
                var same = Find(a, v, x + dx, y + dy);
                if (same != null) changed |= SetWallOn(same.Node, Opposite(d), preset);
                else
                {
                    int other = AddressAt(x + dx, y + dy);
                    if (other >= 0)
                        for (int ov = 0; ov < VariationCount(other); ov++)
                        {
                            var on = Find(other, ov, x + dx, y + dy);
                            if (on != null) changed |= SetWallOn(on.Node, Opposite(d), preset);
                        }
                }
            }
            if (changed) Dirty = true;
            return changed;
        }

        // ------------------------------------------------------------------ editing: tiles

        public JsonObject EnsureTile(int tx, int ty)
        {
            var t = TileAt(tx, ty);
            if (t != null) return t;
            var first = Tiles.OfType<JsonObject>().FirstOrDefault();
            if (first != null)
            {
                t = CloneObj(first);
                foreach (var k in t.Select(kv => kv.Key).ToList())
                {
                    if (t[k] is JsonValue val && val.TryGetValue<bool>(out _)) t[k] = false;
                    else if (k == "s_r" || k == "e_r") t[k] = 0;
                }
            }
            else
                t = new JsonObject { ["i_e"] = false, ["m_e"] = false, ["s_t"] = false, ["s_r"] = 0, ["e_l"] = false, ["e_r"] = 0 };
            t["f_c"] = MakeCoord(tx, ty);
            Tiles.Add(t);
            Dirty = true;
            return t;
        }

        /// <summary>key: i_e (entrance), m_e (main entrance), s_t (stairwell), e_l (elevator). Rotation goes to s_r / e_r.</summary>
        public bool SetTileFlag(int tx, int ty, string key, bool value, int rotation)
        {
            if (tx < 0 || ty < 0) return false;
            var t = value ? EnsureTile(tx, ty) : TileAt(tx, ty);
            if (t == null) return false;
            t[key] = value;
            if (key == "s_t") t["s_r"] = rotation;
            if (key == "e_l") t["e_r"] = rotation;
            Dirty = true;
            return true;
        }

        // ------------------------------------------------------------------ stairs

        /// <summary>
        /// Side of a stairwell tile people walk in and out on, from its rotation. Worked out from all game floors: the other
        /// three sides always have walls, this one never has; all cells of the tile are tile type 4 and there are no walls
        /// inside the tile. Rotation 0 → bottom (S), 90 → right (E), 180 → top (N), 270 → left (W).
        /// </summary>
        public static WallDir StairOpenSide(int rotation) => ((((rotation % 360) + 360) % 360) / 90) switch
        {
            1 => WallDir.Right, 2 => WallDir.Up, 3 => WallDir.Left, _ => WallDir.Down,
        };

        /// <summary>
        /// Step heights (node f_h) of a stairs tile by rotation, rows top to bottom, as in every regular game stairwell
        /// (e.g. all Tenement floors). Without them the stairs are flat for the game's path finding and people get stuck.
        /// </summary>
        private static readonly Dictionary<int, int[,]> StairHeights = new()
        {
            [0] = new[,] { { 33, 28, 24 }, { 39, 0, 20 }, { 51, 0, 7 } },
            [90] = new[,] { { 24, 20, 7 }, { 28, 0, 0 }, { 33, 39, 51 } },
            [180] = new[,] { { 7, 0, 51 }, { 20, 0, 39 }, { 24, 28, 33 } },
            [270] = new[,] { { 51, 39, 33 }, { 0, 0, 28 }, { 7, 20, 24 } },
        };

        private bool StairsFlat(int tx, int ty) =>
            TileCells(tx, ty).All(c => { int a = AddressAt(c.x, c.y); return a < 0 || Int(Find(a, 0, c.x, c.y)?.Node, "f_h", 0) == 0; });

        public IEnumerable<(int tx, int ty, bool stairs, int rot)> StairTiles() =>
            Tiles.OfType<JsonObject>()
                 .Where(t => Bool(t, "s_t") || Bool(t, "e_l"))
                 .Select(t => { var (tx, ty) = Coord(t); return (tx, ty, Bool(t, "s_t"), Int(t, Bool(t, "s_t") ? "s_r" : "e_r")); });

        private IEnumerable<(int x, int y)> TileCells(int tx, int ty)
        {
            int n = Math.Max(1, NodesPerTile);
            for (int y = ty * n; y < ty * n + n; y++)
                for (int x = tx * n; x < tx * n + n; x++) yield return (x, y);
        }

        private IEnumerable<(WallDir d, string preset)> WallsOf(int x, int y)
        {
            int a = AddressAt(x, y);
            var n = a < 0 ? null : Find(a, 0, x, y);
            if (n == null || n.Node["w_d"] is not JsonArray walls) yield break;
            foreach (var w in walls.OfType<JsonObject>()) yield return (DirOf(w), Str(w, "p_n"));
        }

        /// <summary>What's wrong with the stairwells and lift shafts on this floor (things that make people get stuck).</summary>
        public List<string> StairProblems()
        {
            var list = new List<string>();
            int n = Math.Max(1, NodesPerTile);
            foreach (var (tx, ty, stairs, rot) in StairTiles())
            {
                string what = (stairs ? "stairs" : "lift") + $" on tile {tx},{ty}";
                var cells = TileCells(tx, ty).ToList();
                var missing = cells.Where(c => AddressAt(c.x, c.y) < 0).ToList();
                if (missing.Count > 0) list.Add($"{what}: {missing.Count} cell(s) of the tile belong to no address.");
                var wrongType = cells.Where(c => AddressAt(c.x, c.y) >= 0 && Int(Find(AddressAt(c.x, c.y), 0, c.x, c.y)?.Node, "f_t", 1) != 4).ToList();
                if (wrongType.Count > 0)
                    list.Add($"{what}: {wrongType.Count} cell(s) aren't tile type 4 (\"none, but indoors\"); a floor or ceiling there blocks the stairwell.");
                var addrs = cells.Select(c => AddressAt(c.x, c.y)).Where(a => a >= 0).Distinct().ToList();
                if (addrs.Count > 1) list.Add($"{what}: the tile is split between {addrs.Count} addresses; it should be one hallway.");
                int inner = 0;
                foreach (var (x, y) in cells)
                    foreach (var (d, _) in WallsOf(x, y))
                    {
                        var (dx, dy) = Step(d);
                        if (cells.Contains((x + dx, y + dy))) inner++;
                    }
                if (inner > 0) list.Add($"{what}: {inner} wall(s) inside the tile.");
                if (!stairs) continue;
                var tileObj = TileAt(tx, ty);
                if (Bool(tileObj, "e_l"))
                    list.Add($"{what}: there is also a lift on the stairs tile. The game only does that in the Hotel's double stairwell; " +
                             "in a normal stairwell the lift blocks the top of the stairs and people get stuck there. Remove the lift (Entrances & stairs, right-click) or give it a tile of its own.");
                if (n == 3 && StairsFlat(tx, ty))
                    list.Add($"{what}: the steps have no heights (all cells at height 0), so for the game the stairs are flat and people get stuck on them.");
                var open = StairOpenSide(rot);
                var (ox, oy) = Step(open);
                int blocked = 0, nowhere = 0;
                foreach (var (x, y) in cells)
                {
                    bool border = open switch { WallDir.Up => y % n == 0, WallDir.Down => y % n == n - 1, WallDir.Left => x % n == 0, _ => x % n == n - 1 };
                    if (!border) continue;
                    if (WallsOf(x, y).Any(w => w.d == open)) blocked++;
                    int na = AddressAt(x + ox, y + oy);
                    if (na < 0 || OutdoorTypes.Contains(AddressPreset(na))) nowhere++;
                }
                var sideName = open switch { WallDir.Up => "top", WallDir.Down => "bottom", WallDir.Left => "left", _ => "right" };
                if (blocked > 0) list.Add($"{what}: the way in/out is the {sideName} side (rotation {rot}), but it has {blocked} wall(s).");
                if (nowhere > 0) list.Add($"{what}: the way in/out is the {sideName} side (rotation {rot}), but that leads outside or nowhere. Turn the stairs (rotation) or move them.");
            }
            return list;
        }

        /// <summary>
        /// Makes stairwell and lift tiles like the game's: tile type 4 on all cells, no walls inside, the way in/out open and
        /// walls on the other three sides of stairs. Returns how many things changed.
        /// </summary>
        public int FixStairs(string wallPreset)
        {
            int changed = 0;
            int n = Math.Max(1, NodesPerTile);
            foreach (var (tx, ty, stairs, rot) in StairTiles().ToList())
            {
                var cells = TileCells(tx, ty).ToList();
                foreach (var (x, y) in cells)
                {
                    if (AddressAt(x, y) < 0) continue;
                    if (SetTileType(x, y, 4)) changed++;
                }
                int norm = (((rot % 360) + 360) % 360) / 90 * 90;
                if (stairs && n == 3 && StairsFlat(tx, ty) && StairHeights.TryGetValue(norm, out var heights))
                    foreach (var (x, y) in cells)
                        if (AddressAt(x, y) >= 0 && SetNodeHeight(x, y, heights[y - ty * n, x - tx * n])) changed++;
                var open = StairOpenSide(rot);
                foreach (var (x, y) in cells)
                {
                    int a = AddressAt(x, y);
                    if (a < 0) continue;
                    foreach (WallDir d in Enum.GetValues(typeof(WallDir)))
                    {
                        var (dx, dy) = Step(d);
                        bool inside = cells.Contains((x + dx, y + dy));
                        bool has = WallsOf(x, y).Any(w => w.d == d);
                        if (inside || (stairs && d == open))
                        {
                            if (has) for (int v = 0; v < VariationCount(a); v++) changed += SetWall(a, v, x, y, d, null, true) ? 1 : 0;
                        }
                        else if (stairs && !has && !string.IsNullOrEmpty(wallPreset))
                            for (int v = 0; v < VariationCount(a); v++) changed += SetWall(a, v, x, y, d, wallPreset, true) ? 1 : 0;
                    }
                }
            }
            if (changed > 0) Dirty = true;
            return changed;
        }

        // ------------------------------------------------------------------ entrances

        /// <summary>
        /// Entrance tile pairs (an outside tile and the inside tile next to it, both flagged) where the shared edge has no
        /// door. People then walk to the entrance and get stuck on a wall. Game floors always have a door on the middle cell.
        /// </summary>
        public List<(int x, int y, WallDir d)> BlockedEntrances()
        {
            var list = new List<(int, int, WallDir)>();
            int n = Math.Max(1, NodesPerTile);
            var flagged = Tiles.OfType<JsonObject>().Where(t => Bool(t, "i_e") || Bool(t, "m_e")).Select(Coord).ToHashSet();
            foreach (var (tx, ty) in flagged)
                foreach (WallDir d in Enum.GetValues(typeof(WallDir)))
                {
                    var (dx, dy) = Step(d);
                    if (!flagged.Contains((tx + dx, ty + dy))) continue;
                    // the inside tile of the pair is the one whose address isn't outdoors
                    int mid = n / 2;
                    int cx = tx * n + (dx == 0 ? mid : dx > 0 ? n - 1 : 0), cy = ty * n + (dy == 0 ? mid : dy > 0 ? n - 1 : 0);
                    int a = AddressAt(cx, cy), b = AddressAt(cx + dx, cy + dy);
                    if (a < 0 || b < 0 || OutdoorTypes.Contains(AddressPreset(a)) || !OutdoorTypes.Contains(AddressPreset(b))) continue;
                    bool door = false;
                    for (int k = 0; k < n && !door; k++)
                    {
                        int x = tx * n + (dx == 0 ? k : dx > 0 ? n - 1 : 0), y = ty * n + (dy == 0 ? k : dy > 0 ? n - 1 : 0);
                        door = WallsOf(x, y).Any(w => w.d == d && (GameNames.KindOf(w.preset) is GameNames.WallKind.Door or GameNames.WallKind.Entrance));
                    }
                    if (!door) list.Add((cx, cy, d));
                }
            return list;
        }

        /// <summary>Puts a door on the middle cell of every entrance that has none. Returns how many.</summary>
        public int FixEntrances(string doorPreset)
        {
            int fixedCount = 0;
            foreach (var (x, y, d) in BlockedEntrances())
            {
                int a = AddressAt(x, y);
                bool any = false;
                for (int v = 0; v < VariationCount(a); v++) any |= SetWall(a, v, x, y, d, doorPreset, true);
                if (any) fixedCount++;
            }
            return fixedCount;
        }

        /// <summary>Rooms of hallway addresses that are much bigger than the game's (one ceiling light per room).</summary>
        public List<string> BigHallways()
        {
            var list = new List<string>();
            for (int a = 0; a < Addresses.Count; a++)
            {
                var type = AddressPreset(a);
                if (!type.EndsWith("Lobby", StringComparison.Ordinal)) continue;
                for (int r = 0; r < RoomCount(a, 0); r++)
                {
                    if (IsNullRoom(a, 0, r)) continue;
                    int cells = NodesOf(Room(a, 0, r)).Count;
                    if (cells > 27)
                        list.Add($"address {a + 1} ({type}) has a hallway room of {cells} cells. The game lights each room with one ceiling light and keeps " +
                                 "hallway rooms to about 15 cells, so it will be dark. Split it: Add room, paint part of it, and put Divider walls between the rooms.");
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ editing: balconies

        /// <summary>Address types that are open air (no building wall around them).</summary>
        public static readonly HashSet<string> OutdoorTypes = new(StringComparer.Ordinal)
        {
            "Outside", "Rooftop", "VentedRooftop", "StreetFrontage", "Yard", "FathomsYard", "Park", "Path",
        };

        public const string BalconyAddress = "Rooftop", BalconyRoom = "Rooftop";

        /// <summary>
        /// Turns the cells in [x0..x1] × [y0..y1] into a walkable balcony, the way the game's own floors make roof terraces
        /// (e.g. MixedIndustrial_ThirdFloor01): a "Rooftop" address, floor without ceiling, a door into the building and
        /// <paramref name="railing"/> on the open edges. Returns null when it worked, otherwise why not.
        /// </summary>
        public string MakeBalcony(int x0, int y0, int x1, int y1, string railing, string door, string wall)
        {
            if (x1 < x0) (x0, x1) = (x1, x0);
            if (y1 < y0) (y0, y1) = (y1, y0);
            var cells = new HashSet<(int x, int y)>();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int a = AddressAt(x, y);
                    if (a < 0) continue;
                    var type = AddressPreset(a);
                    if (type == "Outside") return "A balcony has to be inside the building (not on the pink outside ring).";
                    if (OutdoorTypes.Contains(type)) continue;   // already open air: leave it alone
                    cells.Add((x, y));
                }
            if (cells.Count == 0) return "There are no building cells there.";

            // no stairs or lifts in it
            foreach (var (x, y) in cells)
            {
                var (tx, ty) = TileOf(x, y);
                var t = TileAt(tx, ty);
                if (t != null && (Bool(t, "s_t") || Bool(t, "e_l")))
                    return "The stairs or lift are in that area. Leave them out of the balcony.";
            }

            bool Open(int x, int y)
            {
                if (cells.Contains((x, y))) return false;
                int a = AddressAt(x, y);
                return a < 0 || OutdoorTypes.Contains(AddressPreset(a));
            }
            (int, int)[] Dirs4 = { (0, -1), (1, 0), (0, 1), (-1, 0) };
            WallDir DirOf4(int dx, int dy) => dx == 0 ? (dy < 0 ? WallDir.Up : WallDir.Down) : (dx < 0 ? WallDir.Left : WallDir.Right);
            if (!cells.Any(c => Dirs4.Any(d => Open(c.x + d.Item1, c.y + d.Item2))))
                return "A balcony has to touch the outside of the building (an outer wall), otherwise it would be a room without a roof.";

            // the Rooftop address (reuse one on this floor)
            int bal = -1;
            for (int a = 0; a < Addresses.Count && bal < 0; a++)
                if (AddressPreset(a) == BalconyAddress) bal = a;
            if (bal < 0)
            {
                bal = AddAddress(BalconyAddress, Color.FromArgb(120, 200, 120));
                SetRoomLayout(bal, 0, FirstRealRoom(bal, 0), BalconyRoom);
            }
            for (int v = 0; v < VariationCount(bal); v++)
                if (FirstRealRoom(bal, v) < 0) AddRoom(bal, v, BalconyRoom);

            var before = cells.ToDictionary(c => c, c => AddressAt(c.x, c.y));
            foreach (var (x, y) in cells) PaintAddress(x, y, bal, 0, FirstRealRoom(bal, 0));
            foreach (var (x, y) in cells) SetTileType(x, y, 2);   // floor, no ceiling: open sky

            // edges: open air → railing, other balcony cells → nothing, building → wall (one gets the door)
            var inner = new List<(int x, int y, WallDir d, int a)>();
            foreach (var (x, y) in cells)
                foreach (var (dx, dy) in Dirs4)
                {
                    int nx = x + dx, ny = y + dy;
                    var d = DirOf4(dx, dy);
                    if (cells.Contains((nx, ny)) || AddressAt(nx, ny) == bal) { SetWallAll(bal, x, y, d, null); continue; }
                    if (Open(nx, ny)) { SetWallAll(bal, x, y, d, railing); continue; }
                    SetWallAll(bal, x, y, d, wall);
                    inner.Add((x, y, d, AddressAt(nx, ny)));
                }

            // the door: on the longest stretch of wall shared with one address, in its middle
            if (inner.Count > 0 && !string.IsNullOrEmpty(door))
            {
                var best = inner.GroupBy(e => e.a).OrderByDescending(g => g.Count())
                                .ThenBy(g => AddressPreset(g.Key) == "Lobby" ? 1 : 0).First()
                                .OrderBy(e => e.d).ThenBy(e => e.x).ThenBy(e => e.y).ToList();
                var mid = best[best.Count / 2];
                SetWallAll(bal, mid.x, mid.y, mid.d, door);
            }

            // tidy up: real rooms that lost all their cells, addresses that are now empty
            foreach (var a in before.Values.Distinct().OrderByDescending(a => a))
            {
                if (a < 0 || a >= Addresses.Count || a == bal) continue;
                bool empty = true;
                for (int v = 0; v < VariationCount(a) && empty; v++) empty = !NodesIn(a, v).Any();
                if (empty) { DeleteAddress(a); if (a < bal) bal--; continue; }
                for (int v = 0; v < VariationCount(a); v++)
                    for (int r = RoomCount(a, v) - 1; r >= 0; r--)
                        if (!IsNullRoom(a, v, r) && !NodesOf(Room(a, v, r)).Any() &&
                            Enumerable.Range(0, RoomCount(a, v)).Count(i => !IsNullRoom(a, v, i)) > 1)
                            DeleteRoom(a, v, r);
            }
            Analyze();
            Dirty = true;
            return null;
        }

        /// <summary>A wall on a cell of address a in every variation, and on the neighbour's side in every address/variation.</summary>
        private void SetWallAll(int a, int x, int y, WallDir d, string preset)
        {
            for (int v = 0; v < VariationCount(a); v++) SetWall(a, v, x, y, d, string.IsNullOrEmpty(preset) ? null : preset, true);
        }

        // ------------------------------------------------------------------ editing: addresses / variations / rooms

        public int AddAddress(string preset, Color color)
        {
            var obj = new JsonObject
            {
                ["p_n"] = preset ?? "",
                ["e_c"] = null,
                ["vs"] = new JsonArray { new JsonObject { ["r_d"] = new JsonArray() } }
            };
            Addresses.Add(obj);
            int a = Addresses.Count - 1;
            SetAddressColor(a, color);
            Rooms(a, 0).Add(new JsonObject { ["id"] = NextRoomId(), ["n_d"] = new JsonArray(), ["l"] = NullRoom });
            AddRoom(a, 0);
            Dirty = true;
            return a;
        }

        public void DeleteAddress(int a)
        {
            Addresses.RemoveAt(a);
            Dirty = true;
        }

        public int AddVariation(int a, int copyFrom)
        {
            var vars = Variations(a);
            JsonObject v = copyFrom >= 0 && copyFrom < vars.Count
                ? CloneObj((JsonObject)vars[copyFrom])
                : new JsonObject { ["r_d"] = new JsonArray() };
            // copies need their own room ids
            int next = NextRoomId();
            foreach (var room in Arr(v, "r_d").OfType<JsonObject>()) room["id"] = next++;
            vars.Add(v);
            Dirty = true;
            return vars.Count - 1;
        }

        public bool DeleteVariation(int a, int v)
        {
            if (VariationCount(a) <= 1) return false;
            Variations(a).RemoveAt(v);
            Dirty = true;
            return true;
        }

        public int AddRoom(int a, int v, string layout = null)
        {
            if (layout == null)
            {
                layout = "";
                for (int r = 0; r < RoomCount(a, v) && layout == ""; r++)
                    if (!IsNullRoom(a, v, r)) layout = RoomLayout(a, v, r);
            }
            var room = new JsonObject { ["id"] = NextRoomId(), ["n_d"] = new JsonArray(), ["l"] = layout };
            Rooms(a, v).Add(room);
            Dirty = true;
            return RoomCount(a, v) - 1;
        }

        /// <summary>Deletes a room; its cells move to another real room. The first Null room and the last real room stay.</summary>
        public bool DeleteRoom(int a, int v, int r)
        {
            if (IsNullRoom(a, v, r) && r == 0) return false;
            int other = -1;
            for (int i = 0; i < RoomCount(a, v) && other < 0; i++)
                if (i != r && !IsNullRoom(a, v, i)) other = i;
            if (other < 0) return false;
            var room = Room(a, v, r);
            var target = Room(a, v, other);
            foreach (var n in NodesOf(room).OfType<JsonObject>().ToList())
            {
                NodesOf(room).Remove(n);
                NodesOf(target).Add(n);
            }
            Rooms(a, v).RemoveAt(r);
            Dirty = true;
            return true;
        }

        /// <summary>Removes every address (keeps size and tiles), for starting a new layout on an existing shape.</summary>
        public void ClearAddresses()
        {
            for (int a = 0; a < Addresses.Count && _nodeTemplate == null; a++)
            {
                if (AddressPreset(a) == "Outside") continue;
                var n = NodesIn(a, 0).FirstOrDefault();
                if (n != null) _nodeTemplate = CloneObj(n.Node);
            }
            Root["a_d"] = new JsonArray();
            Dirty = true;
        }

        // ------------------------------------------------------------------ values for dropdowns

        public void CollectValues(ISet<string> addressPresets, ISet<string> walls, ISet<string> roomLayouts, ISet<string> floorRefs)
        {
            for (int a = 0; a < Addresses.Count; a++)
            {
                var p = AddressPreset(a);
                if (p != "") addressPresets.Add(p);
                for (int v = 0; v < VariationCount(a); v++)
                {
                    for (int r = 0; r < RoomCount(a, v); r++)
                    {
                        var l = RoomLayout(a, v, r);
                        if (l != "") roomLayouts.Add(l);
                    }
                    foreach (var n in NodesIn(a, v))
                    {
                        var fr = Str(n.Node, "f_r");
                        if (fr != "") floorRefs.Add(fr);
                        if (n.Node["w_d"] is JsonArray ws)
                            foreach (var w in ws.OfType<JsonObject>())
                            {
                                var wp = Str(w, "p_n");
                                if (wp != "") walls.Add(wp);
                            }
                    }
                }
            }
        }
    }
}

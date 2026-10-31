using System;
using System.Collections.Generic;
using CustomBuildings.Shared;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace BuildingEditor
{
    public enum FloorTool { Inspect, Room, Address, Erase, TileType, Wall, TileFlag, Balcony, Furniture }

    /// <summary>Draws a floor plan as a grid of nodes and lets the user paint on it.</summary>
    public sealed class FloorCanvas : Control
    {
        public FloorDoc Doc { get; private set; }
        public int CurrentAddress = -1;
        public int CurrentVariation;
        public int CurrentRoom;
        public FloorTool Tool = FloorTool.Room;
        public int TileTypeToPaint = 1;
        public string WallPreset = "";
        public bool MirrorWalls = true;
        public string TileFlagKey = "i_e";
        public int TileRotation;
        public int CellSize = 22;
        public string BalconyRailing = "", BalconyDoor = "7", BalconyWall = "0";
        private bool _balDrag;
        private Point _balStart, _balEnd;
        /// <summary>Addresses or rooms were added or removed (the side panel needs a full refresh).</summary>
        public event Action StructureChanged;

        private const int Pad = 24;
        private static readonly Font NoticeFont = new("Segoe UI", 9f, FontStyle.Bold);
        private Point _hover = new(-1, -1);
        private WallDir _hoverDir;
        private bool _stroke;
        private bool _strokeChanged;

        /// <summary>A cell was clicked with the inspect tool, or right-clicked to pick its address/room.</summary>
        public event Action<int, int> CellPicked;
        /// <summary>The floor changed (after a stroke ends).</summary>
        public event Action Changed;
        public event Action<string> HoverText;

        // ---- furniture tool
        /// <summary>The building's hand-placed furniture (null for game floors: the tool is off then).</summary>
        public List<FurnitureItem> Furniture;
        /// <summary>Footprint and kind of a piece by name (from the game's catalog).</summary>
        public Func<string, CatalogFurniture> FurnitureInfo;
        /// <summary>The piece a click puts down.</summary>
        public string FurnitureToPlace = "";
        public int FurnitureAngle;
        /// <summary>Hand-placed furniture changed.</summary>
        public event Action FurnitureChanged;
        private FurnitureItem _dragItem;
        private Point _dragGrab;

        public FloorCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(28, 30, 34);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 7.5f);
        }

        public void SetDoc(FloorDoc doc)
        {
            Doc = doc;
            CurrentAddress = doc != null && doc.Addresses.Count > 0 ? 0 : -1;
            CurrentVariation = 0;
            CurrentRoom = 0;
            UpdateSize();
            Invalidate();
        }

        public void UpdateSize()
        {
            if (Doc == null) { Size = new Size(10, 10); return; }
            Size = new Size(Doc.Width * CellSize + Pad * 2, Doc.Height * CellSize + Pad * 2);
        }

        public void Zoom(int delta)
        {
            CellSize = Math.Clamp(CellSize + delta, 6, 60);
            UpdateSize();
            Invalidate();
        }

        // ------------------------------------------------------------------ painting

        private Rectangle CellRect(int x, int y) => new(Pad + x * CellSize, Pad + y * CellSize, CellSize, CellSize);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Doc == null) return;
            g.SmoothingMode = SmoothingMode.None;

            int w = Doc.Width, h = Doc.Height, npt = Doc.NodesPerTile;

            // cells
            for (int a = 0; a < Doc.Addresses.Count; a++)
            {
                bool current = a == CurrentAddress;
                int v = current ? Math.Min(CurrentVariation, Math.Max(0, Doc.VariationCount(a) - 1)) : 0;
                var baseCol = Doc.AddressColor(a);
                foreach (var n in Doc.NodesIn(a, v))
                {
                    var col = current ? Shade(baseCol, n.R) : Blend(baseCol, BackColor, 0.55f);
                    using (var b = new SolidBrush(col)) g.FillRectangle(b, CellRect(n.X, n.Y));

                    int type = FloorDoc.Int(n.Node, "f_t", 1);
                    if (type != 1 && CellSize >= 10)
                    {
                        string mark = type switch { 0 => "×", 2 => "F", 3 => "C", 4 => "i", _ => type.ToString() };
                        using var tb = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
                        g.DrawString(mark, Font, tb, CellRect(n.X, n.Y).X + 1, CellRect(n.X, n.Y).Y);
                    }
                }
            }

            // grid lines: thin per node, thicker per tile
            using (var thin = new Pen(Color.FromArgb(45, 255, 255, 255)))
            using (var thick = new Pen(Color.FromArgb(110, 255, 255, 255)))
            {
                for (int x = 0; x <= w; x++)
                    g.DrawLine(x % npt == 0 ? thick : thin, Pad + x * CellSize, Pad, Pad + x * CellSize, Pad + h * CellSize);
                for (int y = 0; y <= h; y++)
                    g.DrawLine(y % npt == 0 ? thick : thin, Pad, Pad + y * CellSize, Pad + w * CellSize, Pad + y * CellSize);
            }

            // selected room outline + room ids
            if (CurrentAddress >= 0 && CurrentAddress < Doc.Addresses.Count)
            {
                using var sel = new Pen(Color.White, 2) { DashStyle = DashStyle.Dot };
                foreach (var n in Doc.NodesIn(CurrentAddress, CurrentVariation))
                {
                    var r = CellRect(n.X, n.Y);
                    if (n.R == CurrentRoom) g.DrawRectangle(sel, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                }
            }

            // walls (current address uses its current variation, others variation 0)
            for (int a = 0; a < Doc.Addresses.Count; a++)
            {
                int v = a == CurrentAddress ? CurrentVariation : 0;
                foreach (var n in Doc.NodesIn(a, v))
                {
                    if (n.Node["w_d"] is not JsonArray walls) continue;
                    foreach (var wall in walls.OfType<JsonObject>())
                        DrawWall(g, n.X, n.Y, FloorDoc.DirOf(wall), FloorDoc.Str(wall, "p_n"), a == CurrentAddress);
                }
            }

            // tile flags
            foreach (var t in Doc.Tiles.OfType<JsonObject>())
            {
                var (tx, ty) = FloorDoc.Coord(t);
                var r = new Rectangle(Pad + tx * npt * CellSize, Pad + ty * npt * CellSize, npt * CellSize, npt * CellSize);
                string label = "";
                if (FloorDoc.Bool(t, "m_e")) label += "MAIN ";
                else if (FloorDoc.Bool(t, "i_e")) label += "ENT ";
                if (FloorDoc.Bool(t, "s_t")) label += "STAIRS" + Arrow(FloorDoc.Int(t, "s_r")) + " ";
                if (FloorDoc.Bool(t, "e_l")) label += "LIFT" + Arrow(FloorDoc.Int(t, "e_r"));
                if (label.Length == 0) continue;
                using var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
                using var fg = new SolidBrush(Color.Yellow);
                var size = g.MeasureString(label.Trim(), Font);
                g.FillRectangle(bg, r.X + 2, r.Y + 2, size.Width, size.Height);
                g.DrawString(label.Trim(), Font, fg, r.X + 2, r.Y + 2);
            }

            // axis labels
            using (var lb = new SolidBrush(Color.FromArgb(140, 255, 255, 255)))
            {
                for (int x = 0; x < w; x += npt) g.DrawString(x.ToString(), Font, lb, Pad + x * CellSize, 4);
                for (int y = 0; y < h; y += npt) g.DrawString(y.ToString(), Font, lb, 2, Pad + y * CellSize);
            }

            // balcony being dragged
            if (_balDrag)
            {
                int bx0 = Math.Min(_balStart.X, _balEnd.X), by0 = Math.Min(_balStart.Y, _balEnd.Y);
                int bx1 = Math.Max(_balStart.X, _balEnd.X), by1 = Math.Max(_balStart.Y, _balEnd.Y);
                var br = Rectangle.Union(CellRect(bx0, by0), CellRect(bx1, by1));
                using var bb = new SolidBrush(Color.FromArgb(90, 120, 220, 120));
                using var bp = new Pen(Color.LightGreen, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                g.FillRectangle(bb, br);
                g.DrawRectangle(bp, br);
            }

            PaintFurniture(g);

            // hover
            if (_hover.X >= 0 && Tool == FloorTool.Furniture)
                PaintFurnitureGhost(g);
            else if (_hover.X >= 0)
            {
                using var hp = new Pen(Color.Orange, 2);
                if (Tool == FloorTool.TileFlag)
                {
                    var (tx, ty) = Doc.TileOf(_hover.X, _hover.Y);
                    g.DrawRectangle(hp, Pad + tx * npt * CellSize, Pad + ty * npt * CellSize, npt * CellSize, npt * CellSize);
                }
                else if (Tool == FloorTool.Wall)
                    DrawEdge(g, hp, _hover.X, _hover.Y, _hoverDir, 3);
                else
                    g.DrawRectangle(hp, CellRect(_hover.X, _hover.Y));
            }

            if (Doc.ReadOnly)
            {
                using var rb = new SolidBrush(Color.FromArgb(200, 255, 180, 80));
                g.DrawString("Game floor: read-only. Use Floor > Copy game floor into building to edit a copy.",
                    NoticeFont, rb, Pad, Pad + h * CellSize + 4);
            }
        }

        private static string Arrow(int rot) => (((rot % 360) + 360) % 360) switch { 90 => "→", 180 => "↓", 270 => "←", _ => "↑" };

        private void DrawWall(Graphics g, int x, int y, WallDir d, string preset, bool current)
        {
            var kind = GameNames.KindOf(preset);
            Color c = kind switch
            {
                GameNames.WallKind.Door => Color.Orange,
                GameNames.WallKind.Window => Color.DeepSkyBlue,
                GameNames.WallKind.Entrance => Color.LimeGreen,
                GameNames.WallKind.Opening => Color.FromArgb(110, 110, 110),
                GameNames.WallKind.Railing => Color.Tan,
                _ => Color.WhiteSmoke,
            };
            if (!current) c = Blend(c, BackColor, 0.4f);
            using var pen = new Pen(c, Math.Max(2, CellSize / 7));
            DrawEdge(g, pen, x, y, d, 1);
        }

        private void DrawEdge(Graphics g, Pen pen, int x, int y, WallDir d, int inset)
        {
            var r = CellRect(x, y);
            switch (d)
            {
                case WallDir.Up: g.DrawLine(pen, r.Left + inset, r.Top + inset, r.Right - inset, r.Top + inset); break;
                case WallDir.Down: g.DrawLine(pen, r.Left + inset, r.Bottom - inset, r.Right - inset, r.Bottom - inset); break;
                case WallDir.Left: g.DrawLine(pen, r.Left + inset, r.Top + inset, r.Left + inset, r.Bottom - inset); break;
                default: g.DrawLine(pen, r.Right - inset, r.Top + inset, r.Right - inset, r.Bottom - inset); break;
            }
        }

        private static Color Shade(Color c, int room)
        {
            // alternate lighter/darker per room so neighbouring rooms are distinguishable
            float[] f = { 0f, 0.28f, -0.22f, 0.45f, -0.38f, 0.15f, -0.1f, 0.35f };
            float k = f[((room % f.Length) + f.Length) % f.Length];
            return k >= 0 ? Blend(c, Color.White, k) : Blend(c, Color.Black, -k);
        }

        private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // ------------------------------------------------------------------ mouse

        private bool HitCell(Point p, out int x, out int y, out WallDir edge)
        {
            x = (int)Math.Floor((p.X - Pad) / (double)CellSize);
            y = (int)Math.Floor((p.Y - Pad) / (double)CellSize);
            double fx = (p.X - Pad) / (double)CellSize - x, fy = (p.Y - Pad) / (double)CellSize - y;
            double up = fy, down = 1 - fy, left = fx, right = 1 - fx;
            double m = Math.Min(Math.Min(up, down), Math.Min(left, right));
            edge = m == up ? WallDir.Up : m == down ? WallDir.Down : m == left ? WallDir.Left : WallDir.Right;
            return Doc != null && x >= 0 && y >= 0 && x < Doc.Width && y < Doc.Height;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (!HitCell(e.Location, out int x, out int y, out var edge)) return;

            if (Tool == FloorTool.Furniture && !Doc.ReadOnly) { FurnitureMouseDown(x, y, e.Button); return; }
            if (e.Button == MouseButtons.Right && Tool != FloorTool.Wall && Tool != FloorTool.TileFlag)
            {
                CellPicked?.Invoke(x, y);   // eyedropper: pick address/room under the cursor
                return;
            }
            if (Tool == FloorTool.Inspect || Doc.ReadOnly)
            {
                CellPicked?.Invoke(x, y);
                return;
            }
            if (Tool == FloorTool.Balcony)
            {
                _balDrag = true;
                _balStart = _balEnd = new Point(x, y);
                Invalidate();
                return;
            }

            Doc.PushUndo();
            _stroke = true;
            _strokeChanged = false;
            Apply(x, y, edge, e.Button == MouseButtons.Right);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Doc == null) return;
            bool inside = HitCell(e.Location, out int x, out int y, out var edge);
            var hover = inside ? new Point(x, y) : new Point(-1, -1);
            if (hover != _hover || edge != _hoverDir)
            {
                _hover = hover;
                _hoverDir = edge;
                Invalidate();
                HoverText?.Invoke(inside ? Describe(x, y) : "");
            }
            if (_balDrag && inside && _balEnd != new Point(x, y)) { _balEnd = new Point(x, y); Invalidate(); }
            if (_dragItem != null && inside)
            {
                int nx = x - _dragGrab.X, ny = y - _dragGrab.Y;
                if (nx != _dragItem.X || ny != _dragItem.Y)
                {
                    var (w, d) = Footprint(_dragItem);
                    if (Fits(nx, ny, w, d, _dragItem, out _, out _)) { _dragItem.X = nx; _dragItem.Y = ny; Invalidate(); }
                }
            }
            if (_stroke && inside && Tool != FloorTool.Wall && Tool != FloorTool.TileFlag)
                Apply(x, y, edge, e.Button == MouseButtons.Right);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragItem != null) { _dragItem = null; FurnitureChanged?.Invoke(); Invalidate(); return; }
            if (_balDrag)
            {
                _balDrag = false;
                Doc.PushUndo();
                var err = Doc.MakeBalcony(_balStart.X, _balStart.Y, _balEnd.X, _balEnd.Y, BalconyRailing, BalconyDoor, BalconyWall);
                if (err != null)
                {
                    Doc.Undo();
                    Invalidate();
                    MessageBox.Show(FindForm(), err, "Balcony");
                    return;
                }
                UpdateSize();
                Invalidate();
                StructureChanged?.Invoke();
                Changed?.Invoke();
                return;
            }
            if (!_stroke) return;
            _stroke = false;
            if (_strokeChanged)
            {
                Doc.Analyze();
                UpdateSize();
                Changed?.Invoke();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                Zoom(e.Delta > 0 ? 3 : -3);
                return;
            }
            base.OnMouseWheel(e);
        }

        private void Apply(int x, int y, WallDir edge, bool right)
        {
            bool changed = false;
            switch (Tool)
            {
                case FloorTool.Room:
                    if (CurrentAddress >= 0) changed = Doc.PaintRoom(CurrentAddress, CurrentVariation, x, y, CurrentRoom);
                    break;
                case FloorTool.Address:
                    if (CurrentAddress >= 0) changed = Doc.PaintAddress(x, y, CurrentAddress, CurrentVariation, CurrentRoom);
                    break;
                case FloorTool.Erase:
                    changed = Doc.Erase(x, y);
                    break;
                case FloorTool.TileType:
                    changed = Doc.SetTileType(x, y, TileTypeToPaint);
                    break;
                case FloorTool.Wall:
                    {
                        int a = CurrentAddress >= 0 && Doc.Find(CurrentAddress, CurrentVariation, x, y) != null ? CurrentAddress : Doc.AddressAt(x, y);
                        if (a < 0) break;
                        int v = a == CurrentAddress ? CurrentVariation : 0;
                        changed = Doc.SetWall(a, v, x, y, edge, right ? null : WallPreset, MirrorWalls);
                        break;
                    }
                case FloorTool.TileFlag:
                    {
                        var (tx, ty) = Doc.TileOf(x, y);
                        changed = Doc.SetTileFlag(tx, ty, TileFlagKey, !right, TileRotation);
                        break;
                    }
            }
            if (changed)
            {
                _strokeChanged = true;
                Invalidate();
            }
        }

        private string Describe(int x, int y)
        {
            var under = FurnitureAt(x, y);
            if (Tool == FloorTool.Furniture && under != null)
                return $"node {x},{y}  {under.Preset} (turned {under.Angle}°): drag to move, right-click or Del to remove, R to turn";
            var (tx, ty) = Doc.TileOf(x, y);
            int a = Doc.AddressAt(x, y);
            var s = $"node {x},{y}  tile {tx},{ty}";
            if (a >= 0)
            {
                int v = a == CurrentAddress ? CurrentVariation : 0;
                var n = Doc.Find(a, v, x, y);
                s += $"  address {a + 1}: {Doc.AddressPreset(a)}";
                if (n != null) s += $"  room id {Doc.RoomId(a, v, n.R)}  type {FloorDoc.Int(n.Node, "f_t")}  height {FloorDoc.Int(n.Node, "f_h")}";
            }
            return s;
        }
    
        // ------------------------------------------------------------------ furniture

        private (int w, int d) Footprint(FurnitureItem it)
        {
            var info = FurnitureInfo?.Invoke(it.Preset);
            return it.Footprint(info?.W ?? 1, info?.D ?? 1);
        }

        private IEnumerable<FurnitureItem> FurnitureOnThisFloor()
        {
            if (Furniture == null || Doc == null) return Enumerable.Empty<FurnitureItem>();
            return Furniture.Where(f => f.Floor == Doc.Name && f.Variation == (f.Address - 1 == CurrentAddress ? CurrentVariation : 0));
        }

        private FurnitureItem FurnitureAt(int x, int y)
        {
            foreach (var it in FurnitureOnThisFloor().Reverse())
            {
                var (w, d) = Footprint(it);
                if (x >= it.X && y >= it.Y && x < it.X + w && y < it.Y + d) return it;
            }
            return null;
        }

        /// <summary>
        /// The footprint fits if all its cells are in one room of one address (the variation shown) and don't overlap
        /// other furniture (wall and ceiling pieces may share cells with floor pieces).
        /// </summary>
        private bool Fits(int x0, int y0, int w, int d, FurnitureItem ignore, out int address, out string why, string preset = null)
        {
            address = -1; why = "";
            if (Doc == null) return false;
            int a = Doc.AddressAt(x0, y0);
            if (a < 0) { why = "not on the floor"; return false; }
            int v = a == CurrentAddress ? CurrentVariation : 0;
            var first = Doc.Find(a, v, x0, y0);
            if (first == null) { why = "not in this layout variation"; return false; }
            for (int x = x0; x < x0 + w; x++)
                for (int y = y0; y < y0 + d; y++)
                {
                    var n = Doc.Find(a, v, x, y);
                    if (n == null || n.R != first.R) { why = "it has to fit inside one room"; return false; }
                }
            var info = FurnitureInfo?.Invoke(preset ?? ignore?.Preset ?? "");
            bool layered = info != null && (info.Wall || info.Ceiling);
            foreach (var it in FurnitureOnThisFloor())
            {
                if (it == ignore || it.Address != a + 1) continue;
                var oi = FurnitureInfo?.Invoke(it.Preset);
                if (layered || (oi != null && (oi.Wall || oi.Ceiling))) continue;
                var (ow, od) = Footprint(it);
                if (x0 < it.X + ow && it.X < x0 + w && y0 < it.Y + od && it.Y < y0 + d) { why = $"it overlaps {it.Preset}"; return false; }
            }
            address = a;
            return true;
        }

        private void FurnitureMouseDown(int x, int y, MouseButtons button)
        {
            if (Furniture == null) return;
            var under = FurnitureAt(x, y);
            if (button == MouseButtons.Right)
            {
                if (under != null) { Furniture.Remove(under); FurnitureChanged?.Invoke(); Invalidate(); }
                else CellPicked?.Invoke(x, y);
                return;
            }
            if (under != null) { _dragItem = under; _dragGrab = new Point(x - under.X, y - under.Y); return; }
            if (string.IsNullOrEmpty(FurnitureToPlace)) { HoverText?.Invoke("Pick a piece of furniture in the list first."); return; }
            var info = FurnitureInfo?.Invoke(FurnitureToPlace);
            var item = new FurnitureItem { Floor = Doc.Name, Preset = FurnitureToPlace, Angle = FurnitureAngle, X = x, Y = y };
            var (w, d) = item.Footprint(info?.W ?? 1, info?.D ?? 1);
            if (!Fits(x, y, w, d, null, out int a, out var why, FurnitureToPlace)) { HoverText?.Invoke($"Can't put {FurnitureToPlace} here: {why}."); return; }
            item.Address = a + 1;
            item.Variation = a == CurrentAddress ? CurrentVariation : 0;
            Furniture.Add(item);
            FurnitureChanged?.Invoke();
            Invalidate();
        }

        /// <summary>R turns the piece under the mouse (or the one about to be placed); Del removes the one under the mouse.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Tool == FloorTool.Furniture && Doc != null && !Doc.ReadOnly && Furniture != null)
            {
                var under = _hover.X >= 0 ? FurnitureAt(_hover.X, _hover.Y) : null;
                if (keyData == Keys.R)
                {
                    if (under != null)
                    {
                        int old = under.Angle;
                        under.Angle = (under.Angle + 90) % 360;
                        var (w, d) = Footprint(under);
                        if (!Fits(under.X, under.Y, w, d, under, out _, out var why)) { under.Angle = old; HoverText?.Invoke($"Can't turn it here: {why}."); }
                        else FurnitureChanged?.Invoke();
                    }
                    else FurnitureAngle = (FurnitureAngle + 90) % 360;
                    Invalidate();
                    return true;
                }
                if ((keyData == Keys.Delete || keyData == Keys.Back) && under != null)
                {
                    Furniture.Remove(under);
                    FurnitureChanged?.Invoke();
                    Invalidate();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void PaintFurniture(Graphics g)
        {
            if (Furniture == null) return;
            foreach (var it in FurnitureOnThisFloor())
            {
                bool current = it.Address - 1 == CurrentAddress;
                var info = FurnitureInfo?.Invoke(it.Preset);
                DrawPiece(g, it.X, it.Y, it, info, current ? 255 : 120, info == null ? Color.IndianRed : null);
            }
        }

        private void PaintFurnitureGhost(Graphics g)
        {
            if (_dragItem != null || Furniture == null) return;
            var under = FurnitureAt(_hover.X, _hover.Y);
            if (under != null)
            {
                var (w, d) = Footprint(under);
                using var hp = new Pen(Color.White, 2);
                g.DrawRectangle(hp, Rectangle.Union(CellRect(under.X, under.Y), CellRect(under.X + w - 1, under.Y + d - 1)));
                return;
            }
            if (string.IsNullOrEmpty(FurnitureToPlace)) { using var hp = new Pen(Color.Orange, 2); g.DrawRectangle(hp, CellRect(_hover.X, _hover.Y)); return; }
            var info = FurnitureInfo?.Invoke(FurnitureToPlace);
            var ghost = new FurnitureItem { Preset = FurnitureToPlace, Angle = FurnitureAngle, X = _hover.X, Y = _hover.Y };
            var (gw, gd) = ghost.Footprint(info?.W ?? 1, info?.D ?? 1);
            bool ok = Fits(_hover.X, _hover.Y, gw, gd, null, out _, out _, FurnitureToPlace);
            DrawPiece(g, _hover.X, _hover.Y, ghost, info, 150, ok ? Color.LightGreen : Color.IndianRed);
        }

        /// <summary>A piece as seen from above: its footprint, its name, and a mark on the side it faces.</summary>
        private void DrawPiece(Graphics g, int x, int y, FurnitureItem it, CatalogFurniture info, int alpha, Color? tint)
        {
            var (w, d) = it.Footprint(info?.W ?? 1, info?.D ?? 1);
            var r = Rectangle.Union(CellRect(x, y), CellRect(x + w - 1, y + d - 1));
            r.Inflate(-2, -2);
            var baseCol = tint ?? (info?.Wall == true ? Color.FromArgb(214, 160, 70) : info?.Ceiling == true ? Color.FromArgb(120, 170, 230) : Color.FromArgb(235, 228, 214));
            using (var fill = new SolidBrush(Color.FromArgb(alpha * 150 / 255, 20, 18, 26))) g.FillRectangle(fill, r);
            using (var pen = new Pen(Color.FromArgb(alpha, baseCol), 2) { DashStyle = info?.Ceiling == true ? DashStyle.Dash : DashStyle.Solid }) g.DrawRectangle(pen, r);
            // facing mark: a small triangle on the front edge (0 = down the plan, then clockwise)
            var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
            float s = Math.Max(3, Math.Min(r.Width, r.Height) / 5f);
            PointF[] tri = (((it.Angle % 360) + 360) % 360) switch
            {
                90 => new[] { new PointF(r.Left + 1, c.Y - s), new PointF(r.Left + 1, c.Y + s), new PointF(r.Left + 1 + s, c.Y) },
                180 => new[] { new PointF(c.X - s, r.Top + 1), new PointF(c.X + s, r.Top + 1), new PointF(c.X, r.Top + 1 + s) },
                270 => new[] { new PointF(r.Right - 1, c.Y - s), new PointF(r.Right - 1, c.Y + s), new PointF(r.Right - 1 - s, c.Y) },
                _ => new[] { new PointF(c.X - s, r.Bottom - 1), new PointF(c.X + s, r.Bottom - 1), new PointF(c.X, r.Bottom - 1 - s) },
            };
            using (var fb = new SolidBrush(Color.FromArgb(alpha, baseCol))) g.FillPolygon(fb, tri);
            if (CellSize >= 12)
            {
                using var tb = new SolidBrush(Color.FromArgb(alpha, Color.White));
                var rf = new RectangleF(r.X + 2, r.Y + 1, r.Width - 4, r.Height - 2);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                g.DrawString(it.Preset, Font, tb, rf, sf);
            }
        }
}
}

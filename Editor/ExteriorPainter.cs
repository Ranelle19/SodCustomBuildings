using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>
    /// Paints a building's own exterior. Each wall is shown as one flat picture with its storeys stacked like on the
    /// building (parapet on top), plus sheets for the rooftop sign, the blade sign and the roof bits. Painting writes into
    /// the four exterior textures: daytime colour, night lights, always-on glow (neon) and the window map.
    /// Sheets can be exported as PNGs, painted in any image editor and imported again.
    /// </summary>
    public sealed class ExteriorPainter : Form
    {
        // ------------------------------------------------------------------ types

        private enum Layer { Day, Neon, Night, Windows }
        private enum Tool { Pencil, Line, Rect, Fill, Picker, Window, ShopWindow, Door, Text, Brick, Picture,
                            ShapeLedge, ShapePillar, ShapeBalcony, ShapeBay, ShapeFrame, ShapeSelect }
        private enum View { Day, Night, Windows }

        private sealed class Place
        {
            public ExteriorPanel P;
            public int X, Y;
            public string Label;
        }

        private sealed class Sheet
        {
            public string Name, FileName;
            public bool IsWall;
            public string Side;
            public int Line, From, To;
            public readonly List<Place> Places = new();
            public int W, H;
            public int[] Map;          // sheet pixel → texture pixel index, -1 = nothing there
            public override string ToString() => Name;
        }

        private sealed class Snapshot
        {
            public Sheet Sheet;
            public int[][] Data;
            public List<ExteriorShape> Shapes;
        }

        private const int Black = unchecked((int)0xFF000000), White = unchecked((int)0xFFFFFFFF), Empty = unchecked((int)0xFF2A2A30), Empty2 = unchecked((int)0xFF323238);
        private const int P = ExteriorGenerator.P;

        // ------------------------------------------------------------------ state

        private readonly Workspace _ws;
        private readonly BuildingEntry _b;
        private readonly string _folder;
        private ExteriorInfo _info;
        private int _tw, _th;
        private int[] _d, _l, _u, _m;    // ARGB: diffuse, lit (night), unlit (always), window map
        private readonly List<Sheet> _sheets = new();
        private Sheet _sheet;
        private Bitmap _comp;
        private bool _dirty;
        private readonly List<Snapshot> _undo = new(), _redo = new();
        private Color _color = ExteriorGenerator.Neon[0];
        private Tool _tool = Tool.Pencil;
        private Layer _layer = Layer.Day;
        private View _view = View.Day;
        private int _zoom = 3;
        private Bitmap _picture;
        private string _pictureName;
        private PreviewForm _preview;
        private readonly Random _rng = new();
        private ExteriorShape _sel;
        private bool _settingDepth;

        // mouse
        private bool _down, _erase;
        private Point _start, _last, _now;

        // ------------------------------------------------------------------ controls

        private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly PaintCanvas _canvas;
        private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(24, 24, 28) };
        private readonly ComboBox _layerBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
        private readonly ComboBox _viewBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly NumericUpDown _size = new() { Minimum = 1, Maximum = 32, Value = 2, Width = 50 };
        private readonly CheckBox _grid = new() { Text = "Grid", Checked = true, AutoSize = true };
        private readonly CheckBox _snap = new() { Text = "Snap to cells", Checked = true, AutoSize = true };
        private readonly CheckBox _windowLights = new() { Text = "Window lights on at night", Checked = true, AutoSize = true };
        private readonly NumericUpDown _depth = new() { Minimum = 0.05m, Maximum = 6, Increment = 0.05m, DecimalPlaces = 2, Value = 0.3m, Width = 60 };
        private readonly CheckBox _showShapes = new() { Text = "Show shapes", Checked = true, AutoSize = true };
        private readonly Panel _swatch = new() { Width = 34, Height = 26, BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
        private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
        private readonly Label _hint = new() { Dock = DockStyle.Bottom, Height = 36, ForeColor = Color.DimGray, Padding = new Padding(6, 2, 6, 0) };
        private readonly Dictionary<Tool, ToolStripButton> _toolButtons = new();

        public static ExteriorInfo ReadInfo(string folder)
        {
            try
            {
                var p = Path.Combine(folder, ModPaths.ExteriorInfoFile);
                return File.Exists(p) ? JsonSerializer.Deserialize<ExteriorInfo>(File.ReadAllText(p), Json.Options) : null;
            }
            catch { return null; }
        }

        public ExteriorPainter(Workspace ws, BuildingEntry b)
        {
            AppIcon.Apply(this);
            _ws = ws;
            _b = b;
            _folder = ModelLoader.ExteriorFolder(ws, b);
            Text = $"Paint exterior: {b.Def.Id}";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1280, 860);
            KeyPreview = true;

            _canvas = new PaintCanvas(this);
            _scroll.Controls.Add(_canvas);

            // ---- tool bar
            var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), ImageScalingSize = new Size(16, 16) };
            void ToolBtn(Tool t, string text, string tip)
            {
                var btn = new ToolStripButton(text) { ToolTipText = tip, CheckOnClick = false, DisplayStyle = ToolStripItemDisplayStyle.Text };
                btn.Click += (s, e) =>
                {
                    if (t == Tool.Picture && _tool == Tool.Picture) PickPicture();
                    else SetTool(t);
                };
                _toolButtons[t] = btn;
                tools.Items.Add(btn);
            }
            ToolBtn(Tool.Pencil, "Pencil", "Paint freehand (B). Right-click erases glow / windows, or picks a colour on the daytime layer.");
            ToolBtn(Tool.Line, "Line", "Straight line (L), e.g. a neon tube.");
            ToolBtn(Tool.Rect, "Rectangle", "Filled rectangle (R).");
            ToolBtn(Tool.Fill, "Fill", "Fill an area of the same colour (F).");
            ToolBtn(Tool.Picker, "Pick colour", "Pick a colour from the picture (I).");
            tools.Items.Add(new ToolStripSeparator());
            ToolBtn(Tool.Window, "Window", "Drag a box: a window with frame and glass (W). The game puts its window lights there.");
            ToolBtn(Tool.ShopWindow, "Shop window", "Drag a box: a big shop window (S).");
            ToolBtn(Tool.Door, "Door", "Drag a box: a glass door (D). Only looks; the real doors come from the entrances in your floor plans.");
            ToolBtn(Tool.Text, "Text", "Drag a box, then type the text (T). On the neon layer it glows.");
            ToolBtn(Tool.Brick, "Bricks", "Drag a box: bricks in the current colour (K).");
            ToolBtn(Tool.Picture, "Picture", "Drag a box: puts a PNG/JPG (a logo, a poster…) there (P). Transparent parts are kept.");
            tools.Items.Add(new ToolStripSeparator());
            var undo = new ToolStripButton("Undo") { ToolTipText = "Ctrl+Z" };
            undo.Click += (s, e) => Undo();
            var redo = new ToolStripButton("Redo") { ToolTipText = "Ctrl+Y" };
            redo.Click += (s, e) => Redo();
            tools.Items.Add(undo);
            tools.Items.Add(redo);
            tools.Items.Add(new ToolStripSeparator());
            var zin = new ToolStripButton("Zoom +") { ToolTipText = "Ctrl + mouse wheel" };
            zin.Click += (s, e) => SetZoom(_zoom + 1);
            var zout = new ToolStripButton("Zoom −");
            zout.Click += (s, e) => SetZoom(_zoom - 1);
            tools.Items.Add(zin);
            tools.Items.Add(zout);

            // ---- shapes bar
            var shapes = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
            shapes.Items.Add(new ToolStripLabel("3D shapes:") { Font = new Font(Font, FontStyle.Bold) });
            var toolsBar = tools;
            tools = shapes;
            ToolBtn(Tool.ShapeLedge, "Ledge", "Drag a box: a band that sticks out (cornice, storey ledge, sill). Its front looks like what you painted there.");
            ToolBtn(Tool.ShapePillar, "Pillar", "Drag a box: a column or pilaster that sticks out, any height.");
            ToolBtn(Tool.ShapeBalcony, "Balcony", "Drag a box from the floor of the balcony up to the top of its railing.");
            ToolBtn(Tool.ShapeBay, "Bay window", "Drag a box: a closed box with glass sides. Paint windows on the wall where you put it; they show on its front.");
            ToolBtn(Tool.ShapeFrame, "Window frame", "Drag a box around a window or door: a surround and sill that stick out, so it looks set into the wall.");
            ToolBtn(Tool.ShapeSelect, "Select", "Click a shape to select it, then change how far it sticks out or press Delete. Right-click deletes.");
            tools = toolsBar;
            var depthHost = new ToolStripControlHost(_depth) { ToolTipText = "How far the shape sticks out of the wall (a cell is 1.8 wide, a storey 5.4 high)" };
            shapes.Items.Add(new ToolStripSeparator());
            shapes.Items.Add(new ToolStripLabel("Sticks out:"));
            shapes.Items.Add(depthHost);
            var del = new ToolStripButton("Delete shape") { ToolTipText = "Delete the selected shape (Delete key)" };
            del.Click += (s, e) => DeleteShape(_sel);
            shapes.Items.Add(del);
            shapes.Items.Add(new ToolStripControlHost(_showShapes));
            shapes.Items.Add(new ToolStripLabel("   Shapes show in game and in 3D after saving.") { ForeColor = Color.DimGray });

            // ---- options row
            var opts = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(4, 4, 4, 0), WrapContents = false };
            Label L(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(8, 7, 2, 0) };
            _layerBox.Items.AddRange(new object[]
            {
                "Wall colour (daytime)",
                "Neon (glows day and night)",
                "Night light (glows only at night)",
                "Window map (where the game lights windows)",
            });
            _layerBox.SelectedIndex = 0;
            _viewBox.Items.AddRange(new object[] { "Day", "Night", "Show window map" });
            _viewBox.SelectedIndex = 0;
            opts.Controls.Add(L("Paint on:"));
            opts.Controls.Add(_layerBox);
            opts.Controls.Add(L("Colour:"));
            opts.Controls.Add(_swatch);
            foreach (var c in Palette())
            {
                var sw = new Panel { Width = 18, Height = 26, BackColor = c, Margin = new Padding(1, 3, 1, 0), Cursor = Cursors.Hand };
                sw.Click += (s, e) => SetColor(((Panel)s).BackColor);
                opts.Controls.Add(sw);
            }
            opts.Controls.Add(L("Size:"));
            opts.Controls.Add(_size);
            opts.Controls.Add(L("View:"));
            opts.Controls.Add(_viewBox);
            foreach (var cb in new[] { _grid, _snap, _windowLights }) { cb.Margin = new Padding(10, 7, 0, 0); opts.Controls.Add(cb); }

            // ---- left: sheets + file buttons
            var left = new Panel { Dock = DockStyle.Left, Width = 250, Padding = new Padding(6) };
            var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 220, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Button Btn(string text, EventHandler click, string tip = null)
            {
                var btn = new Button { Text = text, Width = 232, Height = 28, Margin = new Padding(0, 2, 0, 2) };
                btn.Click += click;
                if (tip != null) new ToolTip().SetToolTip(btn, tip);
                leftButtons.Controls.Add(btn);
                return btn;
            }
            Btn("Save", (s, e) => SaveTextures(), "Saves the textures (Ctrl+S). Start the game and generate a new city to see it.");
            Btn("Save and show in 3D", (s, e) => { if (SaveTextures()) Show3D(); });
            Btn("Export this wall as PNGs…", (s, e) => ExportSheet(_sheet, null),
                "Writes the picture, its night lights, glow and window map, plus a guide with the grid, to paint in another program.");
            Btn("Import PNG into this wall…", (s, e) => ImportSheet(),
                "Name.png = daytime colour, Name_night.png = night lights, Name_glow.png = always glowing, Name_windows.png = window map. " +
                "Other sizes are scaled. Transparent pixels are left alone.");
            Btn("Export all walls to a folder…", (s, e) => ExportAll());
            Btn("Import all from a folder…", (s, e) => ImportAll());
            left.Controls.Add(_list);
            left.Controls.Add(new Label { Text = "Walls and signs", Dock = DockStyle.Top, Height = 22, Font = new Font(Font, FontStyle.Bold) });
            left.Controls.Add(leftButtons);

            Controls.Add(_scroll);
            Controls.Add(_hint);
            Controls.Add(_status);
            Controls.Add(left);
            Controls.Add(opts);
            Controls.Add(shapes);
            Controls.Add(tools);

            // ---- events
            _list.SelectedIndexChanged += (s, e) => { if (_list.SelectedItem is Sheet sh) ShowSheet(sh); };
            _layerBox.SelectedIndexChanged += (s, e) =>
            {
                _layer = (Layer)_layerBox.SelectedIndex;
                // show what's being painted
                var want = _layer == Layer.Windows ? View.Windows : _layer == Layer.Night ? View.Night : _view == View.Windows ? View.Day : _view;
                if (want != _view) _viewBox.SelectedIndex = (int)want;
                UpdateHint();
            };
            _viewBox.SelectedIndexChanged += (s, e) => { _view = (View)_viewBox.SelectedIndex; RebuildComposite(); };
            _grid.CheckedChanged += (s, e) => _canvas.Invalidate();
            _showShapes.CheckedChanged += (s, e) => _canvas.Invalidate();
            _depth.ValueChanged += (s, e) =>
            {
                if (_settingDepth || _sel == null) return;
                PushUndo();
                _sel.Depth = (float)_depth.Value;
                Changed();
            };
            _swatch.Click += (s, e) =>
            {
                using var dlg = new ColorDialog { Color = _color, FullOpen = true };
                if (dlg.ShowDialog(this) == DialogResult.OK) SetColor(dlg.Color);
            };
            FormClosing += (s, e) =>
            {
                if (!_dirty) return;
                var r = MessageBox.Show(this, "Save your painting first?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel || (r == DialogResult.Yes && !SaveTextures())) e.Cancel = true;
            };
            FormClosed += (s, e) => { _comp?.Dispose(); _picture?.Dispose(); };

            SetColor(_color);
            SetTool(Tool.Pencil);
            Load += (s, e) =>
            {
                string err = LoadTextures();
                if (err != null)
                {
                    MessageBox.Show(this, err, Text);
                    BeginInvoke(new Action(Close));
                    return;
                }
                BuildSheets();
                foreach (var sh in _sheets) _list.Items.Add(sh);
                if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            };
        }

        private static IEnumerable<Color> Palette() => ExteriorGenerator.Neon.Concat(new[]
        {
            Color.FromArgb(255, 70, 50), Color.FromArgb(80, 255, 120),
            ExteriorGenerator.Brick, ExteriorGenerator.Mortar, Color.FromArgb(150, 70, 50), Color.FromArgb(120, 116, 110), Color.FromArgb(200, 190, 170),
            ExteriorGenerator.Trim, ExteriorGenerator.Glass, Color.FromArgb(255, 196, 110), Color.FromArgb(200, 220, 255),
            Color.White, Color.Black,
        });

        // ------------------------------------------------------------------ loading / saving

        private string LoadTextures()
        {
            _info = ReadInfo(_folder);
            if (_info == null)
                return "This building has no exterior of its own yet. Make one first with Building > Make exterior (Ctrl+E), " +
                       "then paint it here (pick \"Plain brick\" there for an empty base).";
            _info.Shapes ??= new List<ExteriorShape>();
            if (_info.Panels == null || _info.Panels.Count == 0)
                return "This exterior was made with an older editor, so the painter doesn't know where the walls are. " +
                       "Make it again with Building > Make exterior (Ctrl+E), then open the painter.";
            int[] Read(string file)
            {
                var path = Path.Combine(_folder, file);
                if (!File.Exists(path)) return null;
                using var src = new Bitmap(path);
                if (_tw == 0) { _tw = src.Width; _th = src.Height; }
                using var bmp = new Bitmap(_tw, _th, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(src, new Rectangle(0, 0, _tw, _th));
                }
                return ToArray(bmp);
            }
            try
            {
                _d = Read(_info.Diffuse);
                if (_d == null) return $"{_info.Diffuse} is missing in {_folder}. Make the exterior again.";
                _l = Read(_info.Lit) ?? NewLayer();
                _u = Read(_info.Unlit) ?? NewLayer();
                _m = Read(_info.WindowMap) ?? NewLayer();
            }
            catch (Exception e) { return "Couldn't read the exterior textures:\n" + e.Message; }
            if (_info.TextureWidth > 0 && (_info.TextureWidth != _tw || _info.TextureHeight != _th))
                return $"The textures are {_tw}×{_th} pixels, but the exterior was made for {_info.TextureWidth}×{_info.TextureHeight}. " +
                       "Put the original size back or make the exterior again.";
            return null;
        }

        private int[] NewLayer()
        {
            var a = new int[_tw * _th];
            Array.Fill(a, Black);
            return a;
        }

        private static int[] ToArray(Bitmap bmp)
        {
            var a = new int[bmp.Width * bmp.Height];
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < bmp.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, a, y * bmp.Width, bmp.Width);
            bmp.UnlockBits(data);
            for (int i = 0; i < a.Length; i++) a[i] |= Black;   // no transparency in the textures
            return a;
        }

        private static Bitmap ToBitmap(int[] a, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(a, y * w, data.Scan0 + y * data.Stride, w);
            bmp.UnlockBits(data);
            return bmp;
        }

        private static void SavePng(int[] a, int w, int h, string path)
        {
            using var bmp = ToBitmap(a, w, h);
            using var rgb = bmp.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format24bppRgb);
            var tmp = path + ".tmp";
            rgb.Save(tmp, ImageFormat.Png);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        private bool SaveTextures()
        {
            try
            {
                SavePng(_d, _tw, _th, Path.Combine(_folder, _info.Diffuse));
                SavePng(_l, _tw, _th, Path.Combine(_folder, _info.Lit));
                SavePng(_u, _tw, _th, Path.Combine(_folder, _info.Unlit));
                SavePng(_m, _tw, _th, Path.Combine(_folder, _info.WindowMap));
                var bytes = new byte[_tw * _th * 3];
                for (int i = 0; i < _m.Length; i++) { bytes[i * 3] = (byte)(_m[i] >> 16); bytes[i * 3 + 1] = (byte)(_m[i] >> 8); bytes[i * 3 + 2] = (byte)_m[i]; }
                _info.Windows = ExteriorGenerator.CountWindows(bytes, _tw, _th, 3);
                _info.Painted = true;
                _info.TextureWidth = _tw;
                _info.TextureHeight = _th;
                ExteriorGenerator.WriteShapes(_folder, _info);
                File.WriteAllText(Path.Combine(_folder, ModPaths.ExteriorInfoFile), JsonSerializer.Serialize(_info, Json.Options));
                _dirty = false;
                _status.Text = $"Saved. {_info.Windows} window(s) in the window map, {_info.Shapes.Count} shape(s). Start the game and generate a new city to see it.";
                UpdateTitle();
                return true;
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Couldn't save the textures:\n" + e.Message, Text);
                return false;
            }
        }

        private void Show3D()
        {
            if (_preview != null && !_preview.IsDisposed) { _preview.ReloadModel(); _preview.Activate(); return; }
            foreach (var f in Application.OpenForms.OfType<PreviewForm>())
                if (f.Building == _b) { _preview = f; f.ReloadModel(); f.Activate(); return; }
            _preview = new PreviewForm(_ws, _b);
            _preview.Show(Owner ?? this);
        }

        private void UpdateTitle() => Text = $"Paint exterior: {_b.Def.Id}" + (_dirty ? " *" : "");

        // ------------------------------------------------------------------ sheets

        private static string SideName(string side) => side switch
        {
            "N" => "Top side",
            "S" => "Bottom side",
            "W" => "Left side",
            "E" => "Right side",
            _ => side,
        };

        private void BuildSheets()
        {
            string front = _info.FrontSide ?? "";
            var walls = _info.Panels.Where(p => p.Kind == "Wall" || p.Kind == "Parapet")
                .GroupBy(p => (p.Side, p.Line, p.From, p.To))
                .OrderBy(g => g.Key.Side == front ? 0 : 1)
                .ThenBy(g => "SENW".IndexOf(g.Key.Side, StringComparison.Ordinal))
                .ThenBy(g => g.Key.Line).ThenBy(g => g.Key.From)
                .ToList();
            var perSide = walls.GroupBy(g => g.Key.Side).ToDictionary(g => g.Key, g => g.Count());
            var partNo = new Dictionary<string, int>();
            foreach (var g in walls)
            {
                var (side, line, from, to) = g.Key;
                partNo[side] = partNo.TryGetValue(side, out var n) ? n + 1 : 1;
                var sh = new Sheet
                {
                    Name = SideName(side) + " of the plan" + (side == front ? " (front)" : "") +
                           (perSide[side] > 1 ? $", part {partNo[side]}" : "") + $" · {to - from + 1} cells",
                    FileName = $"wall_{side}{line}_{from}-{to}",
                    IsWall = true, Side = side, Line = line, From = from, To = to,
                };
                int y = 0;
                foreach (var p in g.OrderByDescending(p => p.Kind == "Parapet" ? int.MaxValue : p.Storey))
                {
                    sh.Places.Add(new Place { P = p, X = 0, Y = y, Label = p.Kind == "Parapet" ? "roof edge" : p.Storey == 0 ? "ground floor" : $"storey {p.Storey}" });
                    y += p.H;
                }
                Finish(sh);
            }

            Sheet Misc(string name, string file, params (string kind, string label)[] kinds)
            {
                var sh = new Sheet { Name = name, FileName = file };
                int y = 0;
                foreach (var (kind, label) in kinds)
                    foreach (var p in _info.Panels.Where(p => p.Kind == kind))
                    {
                        sh.Places.Add(new Place { P = p, X = 0, Y = y, Label = label });
                        y += p.H + 8;
                    }
                if (sh.Places.Count == 0) return null;
                Finish(sh);
                return sh;
            }
            Misc("Rooftop sign", "sign", ("Sign", "front"), ("SignBack", "back and posts"));
            Misc("Blade sign (sticks out of the wall)", "blade", ("Blade", "both sides"));
            Misc("Awnings, roof and vents", "roof", ("Awning", "awning (each entrance shows a third)"), ("Roof", "roof (repeats)"), ("Box", "vent boxes"));
            Misc("Shape colours", "shapecolours", ("ShapeColours", "trim · railings · bay window glass · concrete"));
        }

        private void Finish(Sheet sh)
        {
            sh.W = Math.Max(1, sh.Places.Max(p => p.X + p.P.W));
            sh.H = Math.Max(1, sh.Places.Max(p => p.Y + p.P.H));
            sh.Map = new int[sh.W * sh.H];
            Array.Fill(sh.Map, -1);
            foreach (var pl in sh.Places)
                for (int y = 0; y < pl.P.H; y++)
                    for (int x = 0; x < pl.P.W; x++)
                    {
                        int tx = pl.P.X + x, ty = pl.P.Y + y;
                        if (tx < 0 || ty < 0 || tx >= _tw || ty >= _th) continue;
                        sh.Map[(pl.Y + y) * sh.W + pl.X + x] = ty * _tw + tx;
                    }
            _sheets.Add(sh);
        }

        private void ShowSheet(Sheet sh)
        {
            _sheet = sh;
            _comp?.Dispose();
            _comp = new Bitmap(sh.W, sh.H, PixelFormat.Format32bppArgb);
            // fit the zoom to the window the first time a sheet is shown
            int fit = Math.Max(1, Math.Min((_scroll.ClientSize.Width - 40) / sh.W, (_scroll.ClientSize.Height - 40) / sh.H));
            _zoom = Math.Clamp(fit, 1, 8);
            ResizeCanvas();
            RebuildComposite();
        }

        private void SetZoom(int z)
        {
            _zoom = Math.Clamp(z, 1, 16);
            ResizeCanvas();
            _canvas.Invalidate();
        }

        private void ResizeCanvas()
        {
            if (_sheet == null) return;
            _canvas.Size = new Size(_sheet.W * _zoom + 40, _sheet.H * _zoom + 40);
        }

        private Place PlaceAt(int sx, int sy) =>
            _sheet?.Places.FirstOrDefault(p => sx >= p.X && sy >= p.Y && sx < p.X + p.P.W && sy < p.Y + p.P.H);

        // ------------------------------------------------------------------ 3D shapes

        private static readonly Dictionary<Tool, string> ShapeTools = new()
        {
            [Tool.ShapeLedge] = "Ledge", [Tool.ShapePillar] = "Pillar", [Tool.ShapeBalcony] = "Balcony",
            [Tool.ShapeBay] = "Bay", [Tool.ShapeFrame] = "Frame",
        };

        private static string ShapeName(string kind) => kind switch
        {
            "Bay" => "bay window", "Frame" => "window frame", _ => kind.ToLowerInvariant(),
        };

        private static Color ShapeColor(string kind) => kind switch
        {
            "Ledge" => Color.FromArgb(255, 200, 60),
            "Pillar" => Color.FromArgb(255, 140, 60),
            "Balcony" => Color.FromArgb(90, 255, 140),
            "Bay" => Color.FromArgb(90, 200, 255),
            _ => Color.FromArgb(240, 120, 255),
        };

        private bool IsShapeTool => ShapeTools.ContainsKey(_tool) || _tool == Tool.ShapeSelect;

        private const float PxPerUnit = ExteriorGenerator.P / ExteriorGenerator.Cell;

        /// <summary>Model height at a sheet row (wall sheets only).</summary>
        private float SheetToY(Sheet sh, float sy)
        {
            var places = sh.Places.OrderBy(p => p.Y).ToList();
            var pl = places.FirstOrDefault(p => sy >= p.Y && sy <= p.Y + p.P.H) ?? (sy < places[0].Y ? places[0] : places[^1]);
            var (r0, r1) = ExteriorGenerator.PanelRange(_info, pl.P);
            float f = Math.Clamp((sy - pl.Y) / pl.P.H, 0, 1);
            return r1 - f * (r1 - r0);
        }

        /// <summary>Sheet row of a model height (wall sheets only).</summary>
        private float YToSheet(Sheet sh, float y)
        {
            Place best = null;
            float bestDist = float.MaxValue, bestRow = 0;
            foreach (var pl in sh.Places)
            {
                var (r0, r1) = ExteriorGenerator.PanelRange(_info, pl.P);
                float cy = Math.Clamp(y, r0, r1), dist = Math.Abs(cy - y);
                if (dist < bestDist - 1e-4f) { bestDist = dist; best = pl; bestRow = pl.Y + (r1 - cy) / (r1 - r0) * pl.P.H; }
            }
            return best == null ? 0 : bestRow;
        }

        private IEnumerable<ExteriorShape> ShapesOn(Sheet sh) =>
            sh == null || !sh.IsWall ? Enumerable.Empty<ExteriorShape>()
                : _info.Shapes.Where(x => x.Side == sh.Side && x.Line == sh.Line && x.From == sh.From && x.To == sh.To);

        /// <summary>Where a shape sits on its wall sheet (sheet pixels).</summary>
        private RectangleF ShapeRect(Sheet sh, ExteriorShape x)
        {
            float x0 = Math.Min(x.U0, x.U1) * PxPerUnit, x1 = Math.Max(x.U0, x.U1) * PxPerUnit;
            float yTop = YToSheet(sh, Math.Max(x.Y0, x.Y1)), yBot = YToSheet(sh, Math.Min(x.Y0, x.Y1));
            return RectangleF.FromLTRB(x0, yTop, x1, Math.Max(yTop + 1, yBot));
        }

        private ExteriorShape ShapeAt(Point p) =>
            ShapesOn(_sheet).Reverse().FirstOrDefault(x => { var r = ShapeRect(_sheet, x); r.Inflate(1, 1); return r.Contains(p.X + 0.5f, p.Y + 0.5f); });

        private void Select(ExteriorShape x)
        {
            _sel = x;
            if (x != null)
            {
                _settingDepth = true;
                _depth.Value = Math.Clamp((decimal)x.Depth, _depth.Minimum, _depth.Maximum);
                _settingDepth = false;
                _status.Text = $"Selected {ShapeName(x.Kind)}: sticks out {x.Depth:0.##}, " +
                               $"{Math.Abs(x.U1 - x.U0):0.##} wide, {Math.Abs(x.Y1 - x.Y0):0.##} high. Change \"Sticks out\" or press Delete.";
            }
            _canvas.Invalidate();
        }

        private void DeleteShape(ExteriorShape x)
        {
            if (x == null) return;
            PushUndo();
            _info.Shapes.Remove(x);
            Select(null);
            Changed();
        }

        private void AddShape(string kind, Rectangle r)
        {
            if (_sheet == null || !_sheet.IsWall)
            {
                MessageBox.Show(this, "3D shapes go on walls. Pick a wall on the left.", Text);
                return;
            }
            if (r.Width < 2 || r.Height < 2) return;
            var shape = new ExteriorShape
            {
                Kind = kind, Side = _sheet.Side, Line = _sheet.Line, From = _sheet.From, To = _sheet.To,
                U0 = r.Left / PxPerUnit, U1 = r.Right / PxPerUnit,
                Y0 = SheetToY(_sheet, r.Bottom), Y1 = SheetToY(_sheet, r.Top),
                Depth = (float)_depth.Value,
            };
            PushUndo();
            _info.Shapes.Add(shape);
            Select(shape);
            Changed();
        }

        private void DrawShapes(Graphics g)
        {
            if (!_showShapes.Checked) return;
            int z = _zoom;
            using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
            foreach (var x in ShapesOn(_sheet))
            {
                var r = ShapeRect(_sheet, x);
                var sr = new RectangleF(20 + r.X * z, 20 + r.Y * z, r.Width * z, r.Height * z);
                var c = ShapeColor(x.Kind);
                bool sel = x == _sel;
                using (var fill = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(sel ? 120 : 60, c), Color.Transparent))
                    g.FillRectangle(fill, sr);
                using (var pen = new Pen(c, sel ? 3 : 1.5f) { DashStyle = sel ? DashStyle.Solid : DashStyle.Dash })
                    g.DrawRectangle(pen, sr.X, sr.Y, sr.Width, sr.Height);
                var label = $"{ShapeName(x.Kind)} {x.Depth:0.##}";
                var size = g.MeasureString(label, font);
                if (size.Width < sr.Width + 40 || sel)
                {
                    using var back = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                    g.FillRectangle(back, sr.X, sr.Y - size.Height - 1, size.Width + 2, size.Height);
                    using var tb = new SolidBrush(c);
                    g.DrawString(label, font, tb, sr.X + 1, sr.Y - size.Height - 1);
                }
            }
        }

        // ------------------------------------------------------------------ composite

        private static int Rgb(int r, int g, int b) => Black | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

        private int ViewPixel(int i)
        {
            int d = _d[i];
            switch (_view)
            {
                case View.Night:
                {
                    int l = _l[i];
                    return Rgb((int)(((d >> 16) & 255) * 0.22) + ((l >> 16) & 255), (int)(((d >> 8) & 255) * 0.22) + ((l >> 8) & 255),
                               (int)((d & 255) * 0.25) + (l & 255));
                }
                case View.Windows:
                    if ((_m[i] & 0x808080) != 0)
                        return Rgb((((d >> 16) & 255) + 40) / 2, (((d >> 8) & 255) + 230) / 2, ((d & 255) + 255) / 2);
                    return Rgb((int)(((d >> 16) & 255) * 0.6), (int)(((d >> 8) & 255) * 0.6), (int)((d & 255) * 0.6));
                default:
                    return d;
            }
        }

        private void RebuildComposite()
        {
            if (_sheet == null || _comp == null) return;
            var buf = new int[_sheet.W * _sheet.H];
            for (int y = 0; y < _sheet.H; y++)
                for (int x = 0; x < _sheet.W; x++)
                {
                    int k = y * _sheet.W + x, i = _sheet.Map[k];
                    buf[k] = i < 0 ? (((x / 8 + y / 8) & 1) == 0 ? Empty : Empty2) : ViewPixel(i);
                }
            var data = _comp.LockBits(new Rectangle(0, 0, _sheet.W, _sheet.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < _sheet.H; y++) Marshal.Copy(buf, y * _sheet.W, data.Scan0 + y * data.Stride, _sheet.W);
            _comp.UnlockBits(data);
            _canvas.Invalidate();
        }

        // ------------------------------------------------------------------ undo

        private void PushUndo()
        {
            if (_sheet == null) return;
            _undo.Add(Take(_sheet));
            if (_undo.Count > 60) _undo.RemoveAt(0);
            _redo.Clear();
        }

        private Snapshot Take(Sheet sh)
        {
            var idx = sh.Map.Where(i => i >= 0).ToArray();
            var data = new int[4][];
            var layers = new[] { _d, _l, _u, _m };
            for (int k = 0; k < 4; k++)
            {
                data[k] = new int[idx.Length];
                for (int j = 0; j < idx.Length; j++) data[k][j] = layers[k][idx[j]];
            }
            return new Snapshot { Sheet = sh, Data = data, Shapes = _info.Shapes.Select(x => x.Clone()).ToList() };
        }

        private void Restore(Snapshot s)
        {
            var idx = s.Sheet.Map.Where(i => i >= 0).ToArray();
            var layers = new[] { _d, _l, _u, _m };
            for (int k = 0; k < 4; k++)
                for (int j = 0; j < idx.Length; j++) layers[k][idx[j]] = s.Data[k][j];
            if (s.Shapes != null) { _info.Shapes = s.Shapes.Select(x => x.Clone()).ToList(); Select(null); }
        }

        private void Undo() => Step(_undo, _redo);
        private void Redo() => Step(_redo, _undo);

        private void Step(List<Snapshot> from, List<Snapshot> to)
        {
            if (from.Count == 0) return;
            var s = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add(Take(s.Sheet));
            Restore(s);
            if (_sheet != s.Sheet) _list.SelectedItem = s.Sheet;
            Changed();
        }

        private void Changed()
        {
            _dirty = true;
            UpdateTitle();
            RebuildComposite();
        }

        // ------------------------------------------------------------------ pixel writes (sheet coordinates)

        private int Idx(int x, int y) => _sheet == null || x < 0 || y < 0 || x >= _sheet.W || y >= _sheet.H ? -1 : _sheet.Map[y * _sheet.W + x];

        private void SetD(int x, int y, Color c) { int i = Idx(x, y); if (i >= 0) _d[i] = c.ToArgb() | Black; }
        private void SetL(int x, int y, Color c) { int i = Idx(x, y); if (i >= 0) _l[i] = c.ToArgb() | Black; }
        private void SetU(int x, int y, Color c) { int i = Idx(x, y); if (i >= 0) _u[i] = c.ToArgb() | Black; }
        private void SetM(int x, int y, bool on) { int i = Idx(x, y); if (i >= 0) _m[i] = on ? White : Black; }

        /// <summary>Writes one pixel on the chosen layer the way that layer means it.</summary>
        private void Put(int x, int y, Color c, bool erase)
        {
            int i = Idx(x, y);
            if (i < 0) return;
            switch (_layer)
            {
                case Layer.Day:
                    if (!erase) _d[i] = c.ToArgb() | Black;
                    break;
                case Layer.Neon:
                    if (erase) { _l[i] = Black; _u[i] = Black; }
                    else { _d[i] = ExteriorGenerator.Pale(c).ToArgb(); _l[i] = _u[i] = c.ToArgb() | Black; }
                    break;
                case Layer.Night:
                    _l[i] = erase ? Black : c.ToArgb() | Black;
                    break;
                case Layer.Windows:
                    _m[i] = erase ? Black : White;
                    break;
            }
        }

        /// <summary>The value the fill tool compares on the chosen layer.</summary>
        private int Probe(int i) => _layer switch
        {
            Layer.Day => _d[i],
            Layer.Windows => _m[i],
            _ => _l[i],
        };

        private void Dab(int cx, int cy, bool erase)
        {
            int n = (int)_size.Value;
            int r0 = -(n - 1) / 2, r1 = n / 2;
            float rad = n / 2f;
            for (int dy = r0; dy <= r1; dy++)
                for (int dx = r0; dx <= r1; dx++)
                {
                    if (n >= 3)
                    {
                        float fx = dx - (r0 + r1) / 2f, fy = dy - (r0 + r1) / 2f;
                        if (fx * fx + fy * fy > rad * rad + 0.25f) continue;
                    }
                    Put(cx + dx, cy + dy, _color, erase);
                }
        }

        private void LineDabs(Point a, Point b, bool erase)
        {
            int dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y), steps = Math.Max(dx, dy);
            for (int s = 0; s <= steps; s++)
            {
                float t = steps == 0 ? 0 : s / (float)steps;
                Dab((int)Math.Round(a.X + (b.X - a.X) * t), (int)Math.Round(a.Y + (b.Y - a.Y) * t), erase);
            }
        }

        private void FillRect(Rectangle r, bool erase)
        {
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++) Put(x, y, _color, erase);
        }

        private void Flood(int sx, int sy, bool erase)
        {
            int i0 = Idx(sx, sy);
            if (i0 < 0) return;
            int target = Probe(i0);
            var seen = new bool[_sheet.W * _sheet.H];
            var stack = new Stack<(int, int)>();
            stack.Push((sx, sy));
            seen[sy * _sheet.W + sx] = true;
            var todo = new List<(int, int)>();
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                todo.Add((x, y));
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= _sheet.W || ny >= _sheet.H) continue;
                    int k = ny * _sheet.W + nx;
                    if (seen[k]) continue;
                    int i = _sheet.Map[k];
                    if (i < 0 || Probe(i) != target) continue;
                    seen[k] = true;
                    stack.Push((nx, ny));
                }
            }
            foreach (var (x, y) in todo) Put(x, y, _color, erase);
        }

        // ------------------------------------------------------------------ stamps

        private void Glass(Rectangle r)
        {
            var g = ExteriorGenerator.Glass;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                float t = (y - r.Top) / (float)Math.Max(1, r.Height);
                var c = ExteriorGenerator.Clamp((int)(g.R + 30 * (1 - t)), (int)(g.G + 40 * (1 - t)), (int)(g.B + 50 * (1 - t)));
                for (int x = r.Left; x < r.Right; x++) SetD(x, y, c);
            }
            var glint = Color.FromArgb(70, 100, 120);
            for (int k = 0; k < r.Width / 2; k++)
            {
                int y = r.Top + 2 + k, x = r.Left + 2 + k * 2;
                if (y >= r.Bottom - 1 || x >= r.Right - 1) break;
                SetD(x, y, glint); SetD(x + 1, y, glint);
            }
        }

        private static readonly Color[] Warm =
        {
            Color.FromArgb(255, 196, 110), Color.FromArgb(240, 170, 90), Color.FromArgb(200, 220, 255), Color.FromArgb(255, 120, 190),
        };

        private void Each(Rectangle r, Action<int, int> a)
        {
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++) a(x, y);
        }

        private void StampWindow(Rectangle r, bool shop)
        {
            if (r.Width < 4 || r.Height < 4) return;
            var trim = ExteriorGenerator.Trim;
            Each(r, (x, y) => SetD(x, y, trim));
            var glass = Rectangle.Inflate(r, -2, -2);
            if (shop) glass = Rectangle.FromLTRB(r.Left + 2, r.Top + Math.Min(6, r.Height / 4), r.Right - 2, r.Bottom - 3);
            Glass(glass);
            Each(glass, (x, y) => SetM(x, y, true));
            if (_windowLights.Checked)
            {
                var c = shop ? new[] { Color.FromArgb(150, 120, 70), Color.FromArgb(120, 140, 150), Color.FromArgb(160, 110, 140) }[_rng.Next(3)]
                             : Warm[_rng.Next(Warm.Length)];
                Each(glass, (x, y) => SetL(x, y, c));
            }
            else Each(glass, (x, y) => SetL(x, y, Color.Black));
        }

        private void StampDoor(Rectangle r)
        {
            if (r.Width < 6 || r.Height < 6) return;
            var trim = ExteriorGenerator.Trim;
            Each(r, (x, y) => SetD(x, y, trim));
            var glass = Rectangle.FromLTRB(r.Left + 3, r.Top + 4, r.Right - 3, r.Bottom - 1);
            Glass(glass);
            int mid = (r.Left + r.Right) / 2;
            for (int y = glass.Top; y < glass.Bottom; y++) { SetD(mid - 1, y, trim); SetD(mid, y, trim); }
            Each(glass, (x, y) => SetL(x, y, Color.FromArgb(120, 96, 60)));
        }

        private void StampBricks(Rectangle r)
        {
            var c = _color;
            var mortar = Color.FromArgb(c.R * 55 / 100, c.G * 55 / 100, c.B * 55 / 100);
            Each(r, (x, y) => SetD(x, y, mortar));
            const int bh = 4, bw = 10;
            for (int row = 0; row < r.Height; row += bh)
            {
                int shift = (row / bh) % 2 * (bw / 2);
                for (int col = -bw; col < r.Width; col += bw)
                {
                    int x0 = Math.Max(r.Left, r.Left + col + shift + 1), x1 = Math.Min(r.Right, r.Left + col + shift + bw);
                    if (x1 <= x0) continue;
                    int v = _rng.Next(-10, 11);
                    var bc = ExteriorGenerator.Clamp(c.R + v, c.G + v, c.B + v);
                    for (int y = r.Top + row; y < Math.Min(r.Bottom, r.Top + row + bh - 1); y++)
                        for (int x = x0; x < x1; x++) SetD(x, y, bc);
                }
            }
        }

        private void StampText(Rectangle r)
        {
            if (r.Width < 6 || r.Height < 6) return;
            var text = Dialogs.AskText(this, "Text", "Text to put there:", _lastText);
            if (string.IsNullOrWhiteSpace(text)) { _undo.RemoveAt(_undo.Count - 1); return; }
            _lastText = text;
            var mask = ExteriorGenerator.TextMask(text, r.Width, r.Height, Math.Max(6, r.Height - 2));
            const float th = 90 / 255f;
            if (_layer == Layer.Neon)
            {
                var glow = ExteriorGenerator.Blur(mask, Math.Clamp(r.Height / 14, 1, 4));
                for (int y = 0; y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++)
                    {
                        int px = r.Left + x, py = r.Top + y, i = Idx(px, py);
                        if (i < 0) continue;
                        float gl = glow[y, x] * 0.8f;
                        foreach (var layer in new[] { _l, _u })
                        {
                            int o = layer[i];
                            layer[i] = Rgb(Math.Max((o >> 16) & 255, (int)(_color.R * gl)), Math.Max((o >> 8) & 255, (int)(_color.G * gl)), Math.Max(o & 255, (int)(_color.B * gl)));
                        }
                        if (mask[y, x] > th) Put(px, py, _color, false);
                    }
            }
            else
            {
                for (int y = 0; y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++)
                        if (mask[y, x] > th) Put(r.Left + x, r.Top + y, _color, false);
            }
        }

        private string _lastText = "OPEN";

        private bool PickPicture()
        {
            using var dlg = new OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*", Title = "Picture to put on the wall" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            try
            {
                using var src = new Bitmap(dlg.FileName);
                _picture?.Dispose();
                _picture = new Bitmap(src);
                _pictureName = Path.GetFileName(dlg.FileName);
                UpdateHint();
                return true;
            }
            catch (Exception e) { MessageBox.Show(this, "Couldn't read that picture:\n" + e.Message, Text); return false; }
        }

        private static Bitmap Scaled(Bitmap src, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            // whole-number enlargements (pixel art) stay sharp; everything else is smoothed
            bool pixelArt = w % src.Width == 0 && h % src.Height == 0;
            g.InterpolationMode = pixelArt ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe at the edges
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            return bmp;
        }

        private static int Blend(int under, int over, int alpha)
        {
            int Mix(int sh) => (((under >> sh) & 255) * (255 - alpha) + ((over >> sh) & 255) * alpha) / 255;
            return Black | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
        }

        private void StampPicture(Rectangle r)
        {
            if (r.Width < 1 || r.Height < 1) return;
            if (_picture == null && !PickPicture()) { _undo.RemoveAt(_undo.Count - 1); return; }
            using var bmp = Scaled(_picture, r.Width, r.Height);
            var px = ToArrayRaw(bmp);
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    int i = Idx(r.Left + x, r.Top + y);
                    if (i < 0) continue;
                    int c = px[y * r.Width + x], a = (c >> 24) & 255;
                    if (a == 0) continue;
                    switch (_layer)
                    {
                        case Layer.Day: _d[i] = Blend(_d[i], c, a); break;
                        case Layer.Neon:
                            _l[i] = Blend(_l[i], c, a); _u[i] = Blend(_u[i], c, a);
                            _d[i] = Blend(_d[i], ExteriorGenerator.Pale(Color.FromArgb(c | Black)).ToArgb(), a);
                            break;
                        case Layer.Night: _l[i] = Blend(_l[i], c, a); break;
                        case Layer.Windows:
                            int lum = (((c >> 16) & 255) + ((c >> 8) & 255) + (c & 255)) / 3;
                            if (a > 127) _m[i] = lum > 127 ? White : Black;
                            break;
                    }
                }
        }

        private static int[] ToArrayRaw(Bitmap bmp)
        {
            var a = new int[bmp.Width * bmp.Height];
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < bmp.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, a, y * bmp.Width, bmp.Width);
            bmp.UnlockBits(data);
            return a;
        }

        // ------------------------------------------------------------------ export / import

        private static readonly (string suffix, int layer, string what)[] Files =
        {
            ("", 0, "daytime colour"), ("_night", 1, "light at night (windows and neon)"), ("_glow", 2, "always glowing (neon)"), ("_windows", 3, "window map"),
        };

        private int[] LayerArray(int k) => k switch { 0 => _d, 1 => _l, 2 => _u, _ => _m };

        private void WriteSheet(Sheet sh, string folder)
        {
            for (int f = 0; f < Files.Length; f++)
            {
                var arr = LayerArray(Files[f].layer);
                var buf = new int[sh.W * sh.H];
                for (int k = 0; k < buf.Length; k++) buf[k] = sh.Map[k] < 0 ? 0 : arr[sh.Map[k]];   // transparent between pieces
                using var bmp = ToBitmap(buf, sh.W, sh.H);
                bmp.Save(Path.Combine(folder, sh.FileName + Files[f].suffix + ".png"), ImageFormat.Png);
            }
            using var guide = Guide(sh);
            guide.Save(Path.Combine(folder, sh.FileName + "_guide.png"), ImageFormat.Png);
        }

        /// <summary>Transparent overlay with the cell grid, tile lines, storey lines and window outlines, to use as a layer elsewhere.</summary>
        private Bitmap Guide(Sheet sh)
        {
            var buf = new int[sh.W * sh.H];
            int line = unchecked((int)0x90FFFFFF), tile = unchecked((int)0xE0FFD040), storey = unchecked((int)0xFF40E0FF), win = unchecked((int)0xFF40FF80);
            foreach (var pl in sh.Places)
            {
                var p = pl.P;
                bool wall = p.Kind == "Wall" || p.Kind == "Parapet";
                for (int x = 0; x < p.W; x++)
                {
                    buf[pl.Y * sh.W + pl.X + x] = storey;
                    buf[(pl.Y + p.H - 1) * sh.W + pl.X + x] = storey;
                }
                if (!wall) continue;
                foreach (var (cx, isTile) in CellLines(p))
                    for (int y = 1; y < p.H - 1; y++)
                        buf[(pl.Y + y) * sh.W + pl.X + Math.Min(cx, p.W - 1)] = isTile ? tile : line;
            }
            // window outlines
            for (int y = 0; y < sh.H; y++)
                for (int x = 0; x < sh.W; x++)
                {
                    int i = sh.Map[y * sh.W + x];
                    if (i < 0 || (_m[i] & 0x808080) == 0) continue;
                    bool edge = false;
                    foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    {
                        int j = nx < 0 || ny < 0 || nx >= sh.W || ny >= sh.H ? -1 : sh.Map[ny * sh.W + nx];
                        if (j < 0 || (_m[j] & 0x808080) == 0) { edge = true; break; }
                    }
                    if (edge) buf[y * sh.W + x] = win;
                }
            return ToBitmap(buf, sh.W, sh.H);
        }

        /// <summary>X positions (in the panel) of cell lines; tiles are every 3 cells of the floor grid.</summary>
        private static IEnumerable<(int x, bool tile)> CellLines(ExteriorPanel p)
        {
            int cells = p.To - p.From + 1;
            if (cells <= 0 || p.W != cells * P) yield break;
            bool up = p.Side == "S" || p.Side == "W";      // left-to-right as seen from outside
            int firstA = up ? p.From : p.To + 1, step = up ? 1 : -1;
            for (int i = 0; i <= cells; i++)
            {
                int a = firstA + i * step;
                yield return (i * P, ((a % 3) + 3) % 3 == 0);
            }
        }

        private void ExportSheet(Sheet sh, string folder)
        {
            if (sh == null) return;
            if (folder == null)
            {
                using var dlg = new FolderBrowserDialog { Description = "Folder to put the PNGs of this wall in", UseDescriptionForTitle = true };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                folder = dlg.SelectedPath;
            }
            try
            {
                WriteSheet(sh, folder);
                _status.Text = $"Exported {sh.FileName}.png (+ _night, _glow, _windows, _guide) to {folder}.";
                if (MessageBox.Show(this,
                        $"Saved in {folder}:\n\n" +
                        $"{sh.FileName}.png  – the daytime colours (paint this one)\n" +
                        $"{sh.FileName}_night.png  – what lights up at night\n" +
                        $"{sh.FileName}_glow.png  – what always glows (neon)\n" +
                        $"{sh.FileName}_windows.png  – white where the game puts window lights\n" +
                        $"{sh.FileName}_guide.png  – grid and window outlines, as a layer to paint over (don't import it)\n\n" +
                        "Keep the size and the file names, then use Import PNG. Open the folder?", Text, MessageBoxButtons.YesNo) == DialogResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", folder);
            }
            catch (Exception e) { MessageBox.Show(this, "Couldn't export:\n" + e.Message, Text); }
        }

        private void ExportAll()
        {
            using var dlg = new FolderBrowserDialog { Description = "Folder to put the PNGs of all walls and signs in", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                foreach (var sh in _sheets) WriteSheet(sh, dlg.SelectedPath);
                _status.Text = $"Exported {_sheets.Count} sheets to {dlg.SelectedPath}.";
                System.Diagnostics.Process.Start("explorer.exe", dlg.SelectedPath);
            }
            catch (Exception e) { MessageBox.Show(this, "Couldn't export:\n" + e.Message, Text); }
        }

        /// <summary>Which layer a file is for, from its name ending.</summary>
        private static int LayerFor(string path, out bool guide)
        {
            var n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            guide = n.EndsWith("_guide");
            foreach (var f in Files.Skip(1))
                if (n.EndsWith(f.suffix)) return f.layer;
            return 0;
        }

        private static string BaseName(string path)
        {
            var n = Path.GetFileNameWithoutExtension(path);
            foreach (var f in Files.Skip(1).Select(f => f.suffix).Append("_guide"))
                if (n.EndsWith(f, StringComparison.OrdinalIgnoreCase)) return n.Substring(0, n.Length - f.Length);
            return n;
        }

        private bool Import(Sheet sh, string path, List<string> notes)
        {
            int layer = LayerFor(path, out bool guide);
            if (guide) return false;
            Bitmap src;
            try { src = new Bitmap(path); }
            catch (Exception e) { notes.Add($"{Path.GetFileName(path)}: couldn't read it ({e.Message})"); return false; }
            using (src)
            {
                if (src.Width != sh.W || src.Height != sh.H)
                    notes.Add($"{Path.GetFileName(path)} is {src.Width}×{src.Height}, the wall is {sh.W}×{sh.H}: scaled to fit.");
                using var bmp = Scaled(src, sh.W, sh.H);
                var px = ToArrayRaw(bmp);
                var arr = LayerArray(layer);
                for (int k = 0; k < px.Length; k++)
                {
                    int i = sh.Map[k];
                    if (i < 0) continue;
                    int a = (px[k] >> 24) & 255;
                    if (a < 128) continue;
                    arr[i] = layer == 3 ? ((px[k] & 0x808080) != 0 ? White : Black) : px[k] | Black;
                }
            }
            return true;
        }

        private void ImportSheet()
        {
            if (_sheet == null) return;
            using var dlg = new OpenFileDialog
            {
                Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*", Multiselect = true,
                Title = "PNG(s) for this wall: name.png = daytime, name_night.png, name_glow.png, name_windows.png",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            PushUndo();
            var notes = new List<string>();
            int n = dlg.FileNames.Count(f => Import(_sheet, f, notes));
            if (n == 0) { _undo.RemoveAt(_undo.Count - 1); }
            else Changed();
            _status.Text = $"Imported {n} file(s) into {_sheet.Name}.";
            if (notes.Count > 0) Dialogs.ShowList(this, "Import", "Notes:", notes);
        }

        private void ImportAll()
        {
            using var dlg = new FolderBrowserDialog { Description = "Folder with the PNGs (as written by Export all)", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var notes = new List<string>();
            int files = 0;
            var byName = _sheets.ToDictionary(s => s.FileName, StringComparer.OrdinalIgnoreCase);
            var touched = new HashSet<Sheet>();
            foreach (var path in Directory.GetFiles(dlg.SelectedPath, "*.png"))
            {
                if (!byName.TryGetValue(BaseName(path), out var sh)) { notes.Add($"{Path.GetFileName(path)}: no wall with that name here, skipped."); continue; }
                if (touched.Add(sh)) { _undo.Add(Take(sh)); _redo.Clear(); }
                if (Import(sh, path, notes)) files++;
            }
            if (files > 0) Changed();
            _status.Text = $"Imported {files} file(s) into {touched.Count} wall(s)/sign(s).";
            if (notes.Count > 0) Dialogs.ShowList(this, "Import", "Notes:", notes);
        }

        // ------------------------------------------------------------------ tools and mouse

        private void SetTool(Tool t)
        {
            _tool = t;
            foreach (var kv in _toolButtons) kv.Value.Checked = kv.Key == t;
            if (t == Tool.Picture && _picture == null) PickPicture();
            _canvas.Cursor = t == Tool.Picker || t == Tool.ShapeSelect ? Cursors.Hand : Cursors.Cross;
            if (ShapeTools.TryGetValue(t, out var kind))
            {
                _sel = null;
                _settingDepth = true;
                _depth.Value = (decimal)ExteriorGenerator.DefaultDepth(kind);
                _settingDepth = false;
            }
            UpdateHint();
        }

        private void SetColor(Color c)
        {
            _color = Color.FromArgb(255, c.R, c.G, c.B);
            _swatch.BackColor = _color;
        }

        private void UpdateHint()
        {
            if (IsShapeTool)
            {
                _hint.Text = "3D shapes stick out of the wall. Their front shows what's painted on the wall behind them, so paint them like the wall." +
                             (_tool switch
                             {
                                 Tool.ShapeLedge => " Ledge: drag a band across the wall, e.g. along a storey line or under the roof edge.",
                                 Tool.ShapePillar => " Pillar: drag a tall narrow box.",
                                 Tool.ShapeBalcony => " Balcony: drag from the balcony floor up to the top of its railing (about 1.2 high). Paint a door or window behind it.",
                                 Tool.ShapeBay => " Bay window: drag a box, then paint windows on that part of the wall.",
                                 Tool.ShapeFrame => " Window frame: drag a box around a window or door.",
                                 _ => " Click a shape to select it; right-click deletes.",
                             });
                return;
            }
            string layer = _layer switch
            {
                Layer.Day => "Painting the daytime colours. Lights and glow stay as they are.",
                Layer.Neon => "Painting neon: it glows day and night (and looks pale in daylight). Right-click removes glow.",
                Layer.Night => "Painting night light: it only glows at night, like lit windows. Right-click removes it.",
                _ => "Painting the window map: white = a window the game can light at night. Right-click removes. Windows also need glass in the daytime colours.",
            };
            string tool = _tool switch
            {
                Tool.Window => " Window: drag a box over a cell.",
                Tool.ShopWindow => " Shop window: drag a box along the ground floor.",
                Tool.Door => " Door: drag a box over an entrance (doors you can walk through come from the floor plan).",
                Tool.Text => " Text: drag a box, then type.",
                Tool.Brick => " Bricks: drag a box; the colour is the brick colour.",
                Tool.Picture => _picture == null ? " Picture: choose a file, then drag a box." : $" Picture ({_pictureName}): drag a box. Click the Picture button again to pick another.",
                _ => "",
            };
            _hint.Text = layer + tool;
        }

        private bool RectTool => _tool is Tool.Rect or Tool.Window or Tool.ShopWindow or Tool.Door or Tool.Text or Tool.Brick or Tool.Picture
                                 || ShapeTools.ContainsKey(_tool);

        private Point ToSheet(Point screen) => new((int)Math.Floor((screen.X - 20) / (double)_zoom), (int)Math.Floor((screen.Y - 20) / (double)_zoom));

        private Rectangle DragRect()
        {
            int x0 = Math.Min(_start.X, _now.X), x1 = Math.Max(_start.X, _now.X) + 1;
            int y0 = Math.Min(_start.Y, _now.Y), y1 = Math.Max(_start.Y, _now.Y) + 1;
            if (_snap.Checked && _tool != Tool.Picture && _tool != Tool.Text)
            {
                var pl = PlaceAt(_start.X, _start.Y);
                if (pl != null && ShapeTools.ContainsKey(_tool))
                {
                    // shapes: ledges snap to whole cells, the rest to quarter cells
                    int step = _tool == Tool.ShapeLedge ? P : P / 4;
                    x0 = pl.X + (int)Math.Round((x0 - pl.X) / (double)step) * step;
                    x1 = pl.X + (int)Math.Round((x1 - pl.X) / (double)step) * step;
                    if (x1 <= x0) x1 = x0 + step;
                }
                else if (pl != null && (pl.P.Kind == "Wall" || pl.P.Kind == "Parapet"))
                {
                    // snap left/right to cell edges, inset a little so neighbouring windows don't touch
                    int inset = _tool == Tool.Rect || _tool == Tool.Brick ? 0 : _tool == Tool.ShopWindow ? 2 : _tool == Tool.Door ? 3 : 5;
                    x0 = pl.X + (x0 - pl.X) / P * P + inset;
                    x1 = pl.X + ((x1 - pl.X + P - 1) / P) * P - inset;
                }
            }
            return Rectangle.FromLTRB(x0, y0, Math.Max(x0 + 1, x1), Math.Max(y0 + 1, y1));
        }

        internal void CanvasDown(MouseEventArgs e)
        {
            if (_sheet == null) return;
            var p = ToSheet(e.Location);
            _erase = e.Button == MouseButtons.Right;
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
            if (IsShapeTool)
            {
                var hit = ShapeAt(p);
                if (_erase) { if (hit != null) DeleteShape(hit); return; }
                if (_tool == Tool.ShapeSelect) { Select(hit); return; }
            }
            if (_tool == Tool.Picker || (_erase && _layer == Layer.Day && (_tool == Tool.Pencil || _tool == Tool.Fill)))
            {
                Pick(p);
                return;
            }
            _down = true;
            _start = _last = _now = p;
            switch (_tool)
            {
                case Tool.Pencil:
                    PushUndo();
                    Dab(p.X, p.Y, _erase);
                    Changed();
                    break;
                case Tool.Fill:
                    PushUndo();
                    Flood(p.X, p.Y, _erase);
                    Changed();
                    _down = false;
                    break;
            }
        }

        internal void CanvasMove(MouseEventArgs e)
        {
            if (_sheet == null) return;
            var p = ToSheet(e.Location);
            var pl = PlaceAt(p.X, p.Y);
            string where = pl == null ? "" : $"{pl.Label}";
            if (pl != null && (pl.P.Kind == "Wall" || pl.P.Kind == "Parapet") && pl.P.W > 0)
            {
                int cell = (p.X - pl.X) / P;
                where += $", cell {cell + 1} of {pl.P.To - pl.P.From + 1}";
            }
            _status.Text = $"{_sheet.Name}   {where}   x {p.X}, y {p.Y}   zoom {_zoom}×";
            if (!_down) { _now = p; if (_tool != Tool.Fill && _tool != Tool.Picker) _canvas.Invalidate(); return; }
            _now = p;
            if (_tool == Tool.Pencil)
            {
                LineDabs(_last, p, _erase);
                _last = p;
                Changed();
            }
            else _canvas.Invalidate();
        }

        internal void CanvasUp(MouseEventArgs e)
        {
            if (!_down) return;
            _down = false;
            _now = ToSheet(e.Location);
            if (_tool == Tool.Line)
            {
                PushUndo();
                LineDabs(_start, _now, _erase);
                Changed();
            }
            else if (RectTool)
            {
                var r = DragRect();
                if (!ShapeTools.ContainsKey(_tool)) PushUndo();
                if (ShapeTools.TryGetValue(_tool, out var kind))
                {
                    var hit = ShapeAt(_start);
                    if (r.Width * _zoom < 6 && r.Height * _zoom < 6 && hit != null) Select(hit);   // a click selects
                    else AddShape(kind, r);
                    return;
                }
                switch (_tool)
                {
                    case Tool.Rect: FillRect(r, _erase); break;
                    case Tool.Window: StampWindow(r, false); break;
                    case Tool.ShopWindow: StampWindow(r, true); break;
                    case Tool.Door: StampDoor(r); break;
                    case Tool.Text: StampText(r); break;
                    case Tool.Brick: StampBricks(r); break;
                    case Tool.Picture: StampPicture(r); break;
                }
                Changed();
            }
        }

        private void Pick(Point p)
        {
            int i = Idx(p.X, p.Y);
            if (i < 0) return;
            int c = _layer == Layer.Day || _layer == Layer.Windows ? _d[i] : _l[i];
            if (_layer != Layer.Day && _layer != Layer.Windows && (c & 0xFFFFFF) == 0) c = _d[i];
            SetColor(Color.FromArgb(c | Black));
        }

        internal void CanvasWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == 0) return;
            SetZoom(_zoom + (e.Delta > 0 ? 1 : -1));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
                case Keys.Control | Keys.S: SaveTextures(); return true;
                case Keys.Delete when _sel != null && !(ActiveControl is NumericUpDown): DeleteShape(_sel); return true;
                case Keys.Escape when _sel != null: Select(null); return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Add: SetZoom(_zoom + 1); return true;
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract: SetZoom(_zoom - 1); return true;
            }
            if (!(ActiveControl is NumericUpDown || ActiveControl is ComboBox || ActiveControl is TextBoxBase))
            {
                Tool? t = keyData switch
                {
                    Keys.B => Tool.Pencil, Keys.L => Tool.Line, Keys.R => Tool.Rect, Keys.F => Tool.Fill, Keys.I => Tool.Picker,
                    Keys.W => Tool.Window, Keys.S => Tool.ShopWindow, Keys.D => Tool.Door, Keys.T => Tool.Text, Keys.K => Tool.Brick,
                    Keys.P => Tool.Picture, _ => null,
                };
                if (t != null) { SetTool(t.Value); return true; }
                if (keyData == Keys.G) { _grid.Checked = !_grid.Checked; return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------ drawing the canvas

        internal void PaintCanvasTo(Graphics g)
        {
            if (_sheet == null || _comp == null) return;
            int z = _zoom;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_comp, new Rectangle(20, 20, _sheet.W * z, _sheet.H * z));
            g.PixelOffsetMode = PixelOffsetMode.Default;

            if (_grid.Checked)
            {
                using var cellPen = new Pen(Color.FromArgb(70, 255, 255, 255)) { DashStyle = DashStyle.Dot };
                using var tilePen = new Pen(Color.FromArgb(150, 255, 210, 70));
                using var storeyPen = new Pen(Color.FromArgb(200, 70, 220, 255));
                using var font = new Font("Segoe UI", 8f);
                using var back = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
                foreach (var pl in _sheet.Places)
                {
                    var r = new Rectangle(20 + pl.X * z, 20 + pl.Y * z, pl.P.W * z, pl.P.H * z);
                    if (pl.P.Kind == "Wall" || pl.P.Kind == "Parapet")
                        foreach (var (x, tile) in CellLines(pl.P))
                            if (z >= 2 || tile)
                                g.DrawLine(tile ? tilePen : cellPen, r.X + x * z, r.Y, r.X + x * z, r.Bottom);
                    g.DrawRectangle(storeyPen, r.X, r.Y, r.Width - 1, r.Height - 1);
                    var size = g.MeasureString(pl.Label, font);
                    g.FillRectangle(back, r.X + 2, r.Y + 2, size.Width + 2, size.Height);
                    g.DrawString(pl.Label, font, Brushes.White, r.X + 3, r.Y + 2);
                }
            }

            DrawShapes(g);

            // what the current drag will do
            using var prev = new Pen(Color.White) { DashStyle = DashStyle.Dash };
            if (_down && _tool == Tool.Line)
                g.DrawLine(prev, 20 + _start.X * z + z / 2, 20 + _start.Y * z + z / 2, 20 + _now.X * z + z / 2, 20 + _now.Y * z + z / 2);
            else if (_down && RectTool)
            {
                var r = DragRect();
                g.DrawRectangle(prev, 20 + r.X * z, 20 + r.Y * z, r.Width * z - 1, r.Height * z - 1);
            }
            else if (!_down && (_tool == Tool.Pencil || _tool == Tool.Line))
            {
                int n = (int)_size.Value, r0 = -(n - 1) / 2;
                using var pen = new Pen(Color.FromArgb(180, 255, 255, 255));
                g.DrawRectangle(pen, 20 + (_now.X + r0) * z, 20 + (_now.Y + r0) * z, n * z - 1, n * z - 1);
            }
        }

        private sealed class PaintCanvas : Control
        {
            private readonly ExteriorPainter _owner;

            public PaintCanvas(ExteriorPainter owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
                BackColor = Color.FromArgb(24, 24, 28);
                Cursor = Cursors.Cross;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                _owner.PaintCanvasTo(e.Graphics);
            }

            protected override void OnMouseDown(MouseEventArgs e) { Focus(); Capture = true; _owner.CanvasDown(e); base.OnMouseDown(e); }
            protected override void OnMouseMove(MouseEventArgs e) { _owner.CanvasMove(e); base.OnMouseMove(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _owner.CanvasUp(e); Capture = false; base.OnMouseUp(e); }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if ((ModifierKeys & Keys.Control) != 0) { _owner.CanvasWheel(e); return; }
                if (Parent is ScrollableControl sc)
                {
                    var pos = new Point(-sc.AutoScrollPosition.X, -sc.AutoScrollPosition.Y);
                    if ((ModifierKeys & Keys.Shift) != 0) pos.X -= e.Delta; else pos.Y -= e.Delta;
                    sc.AutoScrollPosition = pos;
                    return;
                }
                base.OnMouseWheel(e);
            }
        }
    }
}

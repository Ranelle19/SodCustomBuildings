using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using CustomBuildings.Shared;
using NV = System.Numerics;

namespace BuildingEditor
{
    /// <summary>How a game model is placed over the floor plans; saved per base building in Editor/preview.json.</summary>
    public sealed class ModelAlign
    {
        public int Rotate { get; set; }             // 0, 90, 180, 270
        public bool Mirror { get; set; }
        public float ScalePercent { get; set; } = 100;  // on top of the automatic fit
        public float OffsetX { get; set; }
        public float OffsetZ { get; set; }
        public float StoreyHeight { get; set; }     // 0 = automatic
    }

    /// <summary>
    /// 3D preview of a building (all storeys, optionally inside the game's real exterior model) or of a single floor plan,
    /// built from the editor's current, unsaved state. It never changes anything.
    /// </summary>
    public sealed class PreviewForm : Form
    {
        private readonly Workspace _ws;
        private readonly BuildingEntry _building;      // null in single-floor mode
        private readonly FloorDoc _singleDoc;
        private readonly string _singleName;
        private List<FloorSlot> _slots = new();
        private readonly Dictionary<int, string> _chosenByLevel = new();
        private readonly SceneOptions _opt = new();
        private bool _loading;

        private LoadedModel _model;
        private string _modelPreset;
        private bool _customExterior;
        private string _modelError;
        private ModelAlign _align = new();
        private Dictionary<string, ModelAlign> _alignments;

        private readonly Preview3D _view = new() { Dock = DockStyle.Fill };
        private readonly Label _title = new() { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };
        private readonly ListBox _floors = new() { Dock = DockStyle.Top, Height = 200, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 20 };
        private readonly ComboBox _plan = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _onlySelected = new() { Dock = DockStyle.Top, Text = "Show only the selected storey", Height = 24 };
        private readonly TrackBar _cut = new() { Dock = DockStyle.Top, TickStyle = TickStyle.None, Height = 32 };
        private readonly Label _cutLabel = new() { Dock = DockStyle.Top, Height = 20 };
        private readonly TrackBar _spread = new() { Dock = DockStyle.Top, Minimum = 0, Maximum = 16, TickStyle = TickStyle.None, Height = 32 };
        private readonly CheckBox _walls = new() { Dock = DockStyle.Top, Text = "Walls, doors and windows", Checked = true, Height = 24 };
        private readonly CheckBox _outer = new() { Dock = DockStyle.Top, Text = "Close open outer edges", Checked = true, Height = 24 };
        private readonly CheckBox _outerFull = new() { Dock = DockStyle.Top, Text = "Full-height outer edges (hides rooms)", Height = 24 };
        private readonly CheckBox _randomVars = new() { Dock = DockStyle.Top, Text = "Random address variations", Height = 24 };
        private readonly Label _stats = new() { Dock = DockStyle.Top, Height = 40, ForeColor = Color.DimGray };

        // furniture placing in 3D
        private readonly CheckBox _placeFurn = new() { Dock = DockStyle.Top, Text = "Place furniture here (left click)", Height = 24 };
        private readonly ComboBox _furnPick = new()
        {
            Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        private readonly Label _furnHint = new() { Dock = DockStyle.Top, Height = 64, ForeColor = Color.DimGray,
            Text = "Pick a storey in the list above, then click on its floor. Drag a piece to move it, R turns, right-click or Del removes. Right-drag turns the camera, middle-drag moves it." };
        private int _furnAngle;
        private FurnitureItem _furnDrag;
        private (int x, int y) _furnGrab;
        private (int x, int y, bool ok, FurnitureItem under)? _furnHover;

        // game model
        private readonly CheckBox _showModel = new() { Dock = DockStyle.Top, Text = "Show the game's model", Checked = true, Height = 24 };
        private readonly ComboBox _look = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _night = new() { Dock = DockStyle.Top, Text = "Night (lit windows)", Height = 24 };
        private readonly CheckBox _env = new() { Dock = DockStyle.Top, Text = "Water and pier", Checked = true, Height = 24 };
        private readonly Label _modelStatus = new() { Dock = DockStyle.Top, Height = 48, ForeColor = Color.DimGray };
        private readonly CheckBox _mirror = new() { Text = "Mirror", AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        private readonly NumericUpDown _scale = new() { Minimum = 10, Maximum = 400, Increment = 5, Width = 60, Value = 100 };
        private readonly NumericUpDown _offX = new() { Minimum = -200, Maximum = 200, Increment = 0.5m, DecimalPlaces = 1, Width = 60 };
        private readonly NumericUpDown _offZ = new() { Minimum = -200, Maximum = 200, Increment = 0.5m, DecimalPlaces = 1, Width = 60 };
        private readonly NumericUpDown _storey = new() { Minimum = 0, Maximum = 50, Increment = 0.1m, DecimalPlaces = 2, Width = 60 };
        private Control _alignPanel;

        public PreviewForm(Workspace ws, BuildingEntry building) : this(ws, building, false) { }

        /// <summary>furnish: the furnishing window (catalog on the left, storeys, the 3D view to place furniture in).</summary>
        public PreviewForm(Workspace ws, BuildingEntry building, bool furnish) : this(ws, furnish)
        {
            _building = building;
            Text = furnish ? $"Furnish: {building.Def.Id}" : $"3D preview: {building.Def.Id}";
            _opt.Furniture = building.Def.Furniture ??= new List<FurnitureItem>();
            if (furnish) _showModel.Checked = false;
            LoadModel();
            Rebuild(keepCamera: false);
            if (furnish) FurnishReady();
        }

        /// <summary>What File > Save does in the main window (the furnishing window's Save button and Ctrl+S).</summary>
        public Action SaveRequested;

        /// <summary>The building this furnishing window is for (null for a plain preview).</summary>
        public BuildingEntry Furnishing => _furnish ? _building : null;

        public PreviewForm(Workspace ws, FloorDoc doc, string name) : this(ws, false)
        {
            _singleDoc = doc;
            _singleName = name;
            _opt.Furniture = ws.Buildings.FirstOrDefault(b => b.Floors.Any(f => f.Doc == doc || f.Name == name))?.Def.Furniture;
            Text = $"3D preview: {name}";
            LoadModel();
            Rebuild(keepCamera: false);
        }

        private readonly bool _furnish;

        private PreviewForm(Workspace ws, bool furnish)
        {
            AppIcon.Apply(this);
            _ws = ws;
            _furnish = furnish;
            _opt.FurnitureInfo = n => ws.Catalog.Furniture.FirstOrDefault(f => f.Name == n);
            _opt.HasModel = n => FurnitureModel(n) != null;
            Width = 1300;
            Height = 860;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            ShowInTaskbar = true;
            BuildUi();
        }

        // ------------------------------------------------------------------ UI

        private static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, Dock = DockStyle.Top, Height = 30 };
            b.Click += click;
            return b;
        }

        private static Label Caption(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 20, Padding = new Padding(0, 4, 0, 0) };
        private static Label Heading(string text) => new()
        {
            Text = text, Dock = DockStyle.Top, Height = 26, Padding = new Padding(0, 8, 0, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };
        private static Control Gap(int h = 8) => new Panel { Dock = DockStyle.Top, Height = h };

        private static Control Row(params Control[] items)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false };
            foreach (var c in items) row.Controls.Add(c);
            return row;
        }

        private static Label Small(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 6, 4, 0) };

        private void BuildUi()
        {
            var side = new Panel { Dock = DockStyle.Left, Width = 300, Padding = new Padding(8), AutoScroll = true };

            _look.Items.AddRange(new object[] { "Solid model", "See-through model" });
            _look.SelectedIndex = 0;

            var rotate = new Button { Text = "Rotate 90°", Width = 90, Height = 26 };
            rotate.Click += (s, e) => { _align.Rotate = (_align.Rotate + 90) % 360; AlignChanged(); };
            var resetAlign = new Button { Text = "Reset", Width = 60, Height = 26 };
            resetAlign.Click += (s, e) => { _align = new ModelAlign(); SaveAlign(); LoadAlignControls(); AlignChanged(); };

            var alignPanel = new Panel { Dock = DockStyle.Top, Height = 150 };
            var alignRows = new Control[]
            {
                Row(rotate, _mirror, resetAlign),
                Row(Small("Scale %"), _scale, Small(" Storey height"), _storey),
                Row(Small("Move X"), _offX, Small(" Move Z"), _offZ),
                new Label { Text = "Storey height in game units, 0 = the game's 5.4. Saved per base building.", Dock = DockStyle.Top, Height = 34, ForeColor = Color.DimGray },
            };
            for (int i = alignRows.Length - 1; i >= 0; i--) alignPanel.Controls.Add(alignRows[i]);
            _alignPanel = alignPanel;

            // Dock=Top stacks in reverse add order: add from the bottom up.
            var controls = new Control[]
            {
                _title,
                Caption("Storeys (top first):"), _floors,
                Caption("Floor plan shown for the selected storey:"), _plan,
                Btn("Shuffle floor plans", (s, e) => Shuffle()),
                Gap(),
                _onlySelected,
                _cutLabel, _cut,

                Heading("Game model"),
                _modelStatus,
                _showModel, _look, _night, _env,
                Caption("Line the model up with the floors:"), alignPanel,

                Heading("Floor plans"),
                Caption("Spread storeys apart (hides the model):"), _spread,
                _walls, _outer, _outerFull, _randomVars,
                Btn("New random variations", (s, e) => { _opt.Seed++; Rebuild(true); }),
                Gap(),
                Btn("Refresh from editor", (s, e) => { LoadModel(); Rebuild(true); }),
                Btn("Reset view", (s, e) => _view.ResetView()),
                Gap(),
                _stats,
            };
            for (int i = controls.Length - 1; i >= 0; i--) side.Controls.Add(controls[i]);

            Controls.Add(_view);
            Controls.Add(side);

            _floors.DrawItem += DrawFloorItem;
            SetupFurniture();
            if (_furnish) BuildFurnishUi(side);
            _floors.SelectedIndexChanged += (s, e) => { if (!_loading) { LoadPlanChoices(); ApplyCut(); } };
            _plan.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || SelectedSlot == null) return;
                var slot = SelectedSlot;
                slot.Chosen = Math.Max(0, _plan.SelectedIndex);
                _chosenByLevel[slot.Level] = slot.ChosenName;
                slot.Doc = Resolve(slot.ChosenName);
                RebuildScene(true);
                _floors.Invalidate();
            };
            _onlySelected.CheckedChanged += (s, e) => ApplyCut();
            _cut.ValueChanged += (s, e) => { if (!_loading) ApplyCut(); };
            _spread.ValueChanged += (s, e) => { _opt.Spread = _spread.Value * 0.5f; RebuildScene(true); };
            _walls.CheckedChanged += (s, e) => { _opt.Walls = _walls.Checked; RebuildScene(true); };
            _outer.CheckedChanged += (s, e) => { _opt.OuterWalls = _outer.Checked; RebuildScene(true); };
            _outerFull.CheckedChanged += (s, e) => { _opt.OuterWallsFull = _outerFull.Checked; RebuildScene(true); };
            _randomVars.CheckedChanged += (s, e) => { _opt.RandomVariations = _randomVars.Checked; RebuildScene(true); };

            _showModel.CheckedChanged += (s, e) => RebuildScene(true);
            _look.SelectedIndexChanged += (s, e) => { _view.Look = _look.SelectedIndex == 1 ? ModelLook.SeeThrough : ModelLook.Solid; _view.RefreshScene(); };
            _night.CheckedChanged += (s, e) => { _view.Night = _night.Checked; _view.RefreshScene(); };
            _env.CheckedChanged += (s, e) => { _view.Environment = _env.Checked; _view.RefreshScene(); };
            _mirror.CheckedChanged += (s, e) => { if (_loading) return; _align.Mirror = _mirror.Checked; AlignChanged(); };
            _scale.ValueChanged += (s, e) => { if (_loading) return; _align.ScalePercent = (float)_scale.Value; AlignChanged(); };
            _offX.ValueChanged += (s, e) => { if (_loading) return; _align.OffsetX = (float)_offX.Value; AlignChanged(); };
            _offZ.ValueChanged += (s, e) => { if (_loading) return; _align.OffsetZ = (float)_offZ.Value; AlignChanged(); };
            _storey.ValueChanged += (s, e) => { if (_loading) return; _align.StoreyHeight = (float)_storey.Value; AlignChanged(); };
        }

        // ------------------------------------------------------------------ the furnishing window

        private readonly FlowLayoutPanel _storeyStrip = new() { Dock = DockStyle.Left, Width = 58, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(6, 8, 0, 0) };
        private readonly ListView _catalog = new() { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None };
        private readonly ImageList _thumbs = new() { ImageSize = new Size(96, 96), ColorDepth = ColorDepth.Depth32Bit };
        private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search…" };
        private readonly ComboBox _catFilter = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Queue<ListViewItem> _thumbQueue = new();
        private readonly Timer _thumbTimer = new() { Interval = 30 };
        private readonly List<List<FurnitureItem>> _history = new();
        private int _historyAt = -1;
        private FurnitureItem _furnLast;

        private void BuildFurnishUi(Panel side)
        {
            side.Visible = false;

            // left: the catalog
            var cat = new Panel { Dock = DockStyle.Left, Width = 290, Padding = new Padding(8) };
            _catFilter.Items.AddRange(new object[] { "All", "On the floor", "On walls", "On ceilings", "From mods", "Used in this building" });
            _catFilter.SelectedIndex = 0;
            _thumbs.Images.Add("wait", Placeholder("…"));
            _thumbs.Images.Add("none", Placeholder("no model"));
            _catalog.LargeImageList = _thumbs;
            var catControls = new Control[]
            {
                new Label { Text = "CATALOG · FURNITURE", Dock = DockStyle.Top, Height = 22, Font = new Font(Font, FontStyle.Bold) },
                _search, new Panel { Dock = DockStyle.Top, Height = 4 }, _catFilter, new Panel { Dock = DockStyle.Top, Height = 6 },
            };
            cat.Controls.Add(_catalog);
            for (int i = catControls.Length - 1; i >= 0; i--) cat.Controls.Add(catControls[i]);

            // top: tools
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 5, 6, 0), WrapContents = false };
            Button B(string t, Action a, int w = 0)
            {
                var b = new Button { Text = t, AutoSize = w == 0, Width = w > 0 ? w : 75, Height = 28, Margin = new Padding(2, 0, 2, 0) };
                b.Click += (s, e) => a();
                return b;
            }
            RadioButton R(string t, Action a, bool on = false)
            {
                var r = new RadioButton { Text = t, Appearance = Appearance.Button, AutoSize = true, Height = 28, Checked = on, Margin = new Padding(1, 0, 1, 0), TextAlign = ContentAlignment.MiddleCenter, MinimumSize = new Size(44, 28) };
                r.CheckedChanged += (s, e) => { if (r.Checked) a(); };
                return r;
            }
            var walls = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(12, 0, 0, 0) };
            walls.Controls.Add(new Label { Text = "Walls", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            walls.Controls.Add(R("Full", () => SetWalls(1f), true));
            walls.Controls.Add(R("Low", () => SetWalls(0.3f)));
            walls.Controls.Add(R("Off", () => SetWalls(0f)));
            var model = new CheckBox { Text = "Building model", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
            model.CheckedChanged += (s, e) => { _showModel.Checked = model.Checked; };
            foreach (var c in new Control[]
            {
                new Label { Text = _building?.Def.Id ?? "", AutoSize = true, Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold), Margin = new Padding(0, 6, 14, 0) },
                B("↶ Undo", Undo), B("↷ Redo", Redo),
                B("Turn (R)", () => TurnPiece(_furnHover?.under ?? _furnLast)), B("Remove (Del)", () => RemovePiece(_furnHover?.under ?? _furnLast)),
                walls, model,
                B("Reset view", () => _view.ResetView()), B("Save", () => SaveRequested?.Invoke()),
            }) bar.Controls.Add(c);

            // bottom: what the mouse is over / what happens on click
            _furnHint.Parent?.Controls.Remove(_furnHint);
            _furnHint.Dock = DockStyle.Bottom;
            _furnHint.Height = 26;
            _furnHint.Padding = new Padding(8, 5, 0, 0);
            _furnHint.Text = "Pick a piece on the left, then click in the view. Drag a piece to move it · R turns · Del / right-click removes · right-drag turns the camera · middle-drag moves it · wheel zooms";

            Controls.Add(_storeyStrip);
            Controls.Add(cat);
            Controls.Add(_furnHint);
            Controls.Add(bar);

            _search.TextChanged += (s, e) => FillCatalog();
            _catFilter.SelectedIndexChanged += (s, e) => FillCatalog();
            _catalog.SelectedIndexChanged += (s, e) =>
            {
                if (_catalog.SelectedItems.Count == 0) return;
                _furnPick.Text = (string)_catalog.SelectedItems[0].Tag;
                _furnHover = null;
            };
            _thumbTimer.Tick += (s, e) => NextThumb();
            FormClosed += (s, e) => _thumbTimer.Stop();
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.Y) { Redo(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.S) { SaveRequested?.Invoke(); e.Handled = true; }
            };
        }

        /// <summary>After the building is loaded: storeys, catalog, the first undo step.</summary>
        private void FurnishReady()
        {
            _placeFurn.Checked = true;   // turns on placing in the view
            FillStoreys();
            FillCatalog();
            Remember();
            _thumbTimer.Start();
        }

        private void SetWalls(float scale)
        {
            _opt.Walls = scale > 0;
            _opt.WallScale = scale > 0 ? scale : 1f;
            RebuildFaces();
        }

        private void FillStoreys()
        {
            _storeyStrip.Controls.Clear();
            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[_slots.Count - 1 - i];   // top first, like the storey list
                int listIndex = i;
                var rb = new RadioButton
                {
                    Text = LevelName(slot.Level), Appearance = Appearance.Button, Width = 44, Height = 28, TextAlign = ContentAlignment.MiddleCenter,
                    Margin = new Padding(0, 0, 0, 4), Checked = _floors.SelectedIndex == listIndex,
                };
                new ToolTip().SetToolTip(rb, slot.Doc != null ? slot.ChosenName : "(no floor plan)");
                rb.CheckedChanged += (s, e) =>
                {
                    if (!rb.Checked) return;
                    _floors.SelectedIndex = listIndex;
                    _onlySelected.Checked = false;
                    _cut.Value = Math.Clamp(slot.Level, _cut.Minimum, _cut.Maximum);   // show up to this storey, place on it
                    _furnHover = null;
                };
                _storeyStrip.Controls.Add(rb);
            }
            // start on the ground floor
            var ground = _storeyStrip.Controls.OfType<RadioButton>().FirstOrDefault(r => r.Text == "G") ?? _storeyStrip.Controls.OfType<RadioButton>().LastOrDefault();
            if (ground != null) ground.Checked = true;
        }

        private static Bitmap Placeholder(string text)
        {
            var bmp = new Bitmap(96, 96);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(22, 22, 40));
            using var f = new Font("Segoe UI", 8f);
            TextRenderer.DrawText(g, text, f, new Rectangle(0, 0, 96, 96), Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return bmp;
        }

        private string FurnitureFolder(string name) =>
            _ws.ModDir == null ? null : Path.Combine(_ws.ExportDir, ModPaths.FurnitureModelsDir, ModPaths.ModelFolder(name));

        private void FillCatalog()
        {
            IEnumerable<CatalogFurniture> list = _ws.Catalog.Furniture;
            var q = _search.Text.Trim();
            if (q.Length > 0) list = list.Where(f => f.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (f.Classes ?? new List<string>()).Any(c => c.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            var used = new HashSet<string>((_opt.Furniture ?? new List<FurnitureItem>()).Select(f => f.Preset));
            list = _catFilter.SelectedIndex switch
            {
                1 => list.Where(f => !f.Wall && !f.Ceiling),
                2 => list.Where(f => f.Wall),
                3 => list.Where(f => f.Ceiling),
                4 => list.Where(f => f.FromMod),
                5 => list.Where(f => used.Contains(f.Name)),
                _ => list,
            };
            _catalog.BeginUpdate();
            _catalog.Items.Clear();
            _thumbQueue.Clear();
            foreach (var f in list)
            {
                var item = new ListViewItem($"{f.Name}  {f.W}×{f.D}{(f.Wall ? " · wall" : f.Ceiling ? " · ceiling" : "")}{(f.FromMod ? " · mod" : "")}") { Tag = f.Name };
                item.ImageKey = _thumbs.Images.ContainsKey(f.Name) ? f.Name : "wait";
                _catalog.Items.Add(item);
                if (item.ImageKey == "wait") _thumbQueue.Enqueue(item);
                if (f.Name == PickedPreset) item.Selected = true;
            }
            _catalog.EndUpdate();
            if (_ws.Catalog.Furniture.Count == 0)
                _furnHint.Text = "No furniture listed yet: start the game once with the plugin (and wait at the main menu), then reopen this window.";
        }

        /// <summary>One picture per tick, so the window stays responsive while the catalog fills in.</summary>
        private void NextThumb()
        {
            while (_thumbQueue.Count > 0)
            {
                var item = _thumbQueue.Dequeue();
                if (item.ListView == null) continue;
                var name = (string)item.Tag;
                if (!_thumbs.Images.ContainsKey(name))
                {
                    var img = FurnitureThumbs.Get(FurnitureFolder(name), 96);
                    if (img == null) { item.ImageKey = "none"; return; }
                    _thumbs.Images.Add(name, img);
                }
                item.ImageKey = name;
                return;
            }
        }

        // ---- undo / redo of the building's furniture
        private static List<FurnitureItem> Clone(IEnumerable<FurnitureItem> list) =>
            list.Select(f => new FurnitureItem { Floor = f.Floor, Address = f.Address, Variation = f.Variation, Preset = f.Preset, X = f.X, Y = f.Y, Angle = f.Angle }).ToList();

        private void Remember()
        {
            if (_opt.Furniture == null) return;
            if (_historyAt < _history.Count - 1) _history.RemoveRange(_historyAt + 1, _history.Count - _historyAt - 1);
            _history.Add(Clone(_opt.Furniture));
            _historyAt = _history.Count - 1;
        }

        private void Restore(int at)
        {
            if (_opt.Furniture == null || at < 0 || at >= _history.Count) return;
            _historyAt = at;
            _opt.Furniture.Clear();
            _opt.Furniture.AddRange(Clone(_history[at]));
            _furnHover = null;
            _furnLast = null;
            var owner = FurnitureOwner;
            if (owner != null) owner.Dirty = true;
            FurnitureRules.RaiseChanged(this);
            RebuildFaces();
        }

        private void Undo() => Restore(_historyAt - 1);
        private void Redo() => Restore(_historyAt + 1);

        private void TurnPiece(FurnitureItem it)
        {
            if (it == null) { _furnAngle = (_furnAngle + 90) % 360; _furnHover = null; _furnHint.Text = $"The next piece is turned {_furnAngle}°."; return; }
            int old = it.Angle;
            it.Angle = (it.Angle + 90) % 360;
            var slot = SlotFor(it);
            var (w, d) = FurnitureRules.Footprint(it, _opt.FurnitureInfo(it.Preset));
            string why = "its floor isn't shown";
            if (slot == null || !FurnitureRules.Fits(slot.Doc, _opt.Furniture, it.X, it.Y, w, d, it.Preset, it, VarOf(slot), _opt.FurnitureInfo, out _, out why))
            { it.Angle = old; _furnHint.Text = $"Can't turn it there: {why}."; return; }
            FurnChanged();
        }

        private void RemovePiece(FurnitureItem it)
        {
            if (it == null || _opt.Furniture == null) return;
            _opt.Furniture.Remove(it);
            if (_furnLast == it) _furnLast = null;
            FurnChanged();
        }

        // ------------------------------------------------------------------ furniture in 3D

        private BuildingEntry FurnitureOwner => _building ?? _ws.Buildings.FirstOrDefault(b => b.Floors.Any(f => f.Doc == _singleDoc || f.Name == _singleName));

        private void SetupFurniture()
        {
            foreach (var f in _ws.Catalog.Furniture)
                _furnPick.Items.Add($"{f.Name}  ({f.W}×{f.D}{(f.Wall ? ", wall" : "")}{(f.Ceiling ? ", ceiling" : "")})");
            if (_ws.Catalog.Furniture.Count == 0) _furnPick.Text = "(start the game once with the new plugin to list the furniture)";
            _placeFurn.CheckedChanged += (s, e) =>
            {
                _view.EditMode = _placeFurn.Checked && _opt.Furniture != null;
                if (_placeFurn.Checked && _opt.Furniture == null)
                {
                    _furnHint.Text = "Furniture can only be placed in floors of your own buildings.";
                    _placeFurn.Checked = false;
                }
                if (!_placeFurn.Checked) { _opt.Ghost = null; _opt.Highlight = null; RebuildFaces(); }
            };
            _view.EditDown += FurnDown;
            _view.EditMove += FurnMove;
            _view.EditUp += () => { if (_furnDrag != null) { _furnDrag = null; FurnChanged(); } };
            _view.EditRightClick += (from, dir) =>
            {
                if (!HitCell(from, dir, out var slot, out int x, out int y)) return;
                var under = FurnitureRules.At(slot.Doc, _opt.Furniture, x, y, VarOf(slot), _opt.FurnitureInfo);
                if (under != null) { _opt.Furniture.Remove(under); FurnChanged(); }
            };
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (!_view.EditMode || _furnPick.Focused || _search.Focused) return;
                var under = _furnHover?.under;
                if (e.KeyCode == Keys.R)
                {
                    if (under != null)
                    {
                        int old = under.Angle;
                        under.Angle = (under.Angle + 90) % 360;
                        var slot = SlotFor(under);
                        var (w, d) = FurnitureRules.Footprint(under, _opt.FurnitureInfo(under.Preset));
                        string why = "its floor isn't shown";
                        if (slot == null || !FurnitureRules.Fits(slot.Doc, _opt.Furniture, under.X, under.Y, w, d, under.Preset, under, VarOf(slot), _opt.FurnitureInfo, out _, out why))
                        { under.Angle = old; _furnHint.Text = $"Can't turn it there: {why}."; }
                        else FurnChanged();
                    }
                    else { _furnAngle = (_furnAngle + 90) % 360; _furnHover = null; }
                    e.Handled = true;
                }
                else if ((e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) && under != null)
                {
                    _opt.Furniture.Remove(under);
                    _furnHover = null;
                    FurnChanged();
                    e.Handled = true;
                }
            };
            Action<object> other = src => { if (src != this && !IsDisposed) { if (_furnish) Remember(); RebuildFaces(); } };
            FurnitureRules.Changed += other;
            FormClosed += (s, e) => FurnitureRules.Changed -= other;
        }

        private static Func<int, int> VarOf(FloorSlot slot) =>
            a => slot?.ShownVariation != null && a >= 0 && a < slot.ShownVariation.Length ? slot.ShownVariation[a] : 0;

        private FloorSlot SlotFor(FurnitureItem it) => _slots.FirstOrDefault(s => s.Doc != null && s.Doc.Name == it.Floor);

        private string PickedPreset => (_furnPick.Text ?? "").Split(new[] { "  (" }, StringSplitOptions.None)[0].Trim();

        /// <summary>The cell of the chosen storey's floor under the mouse.</summary>
        private bool HitCell(NV.Vector3 from, NV.Vector3 dir, out FloorSlot slot, out int x, out int y)
        {
            x = y = 0;
            slot = SelectedSlot ?? _slots.FirstOrDefault(s => s.Level == 0 && s.Doc != null) ?? _slots.FirstOrDefault(s => s.Doc != null);
            if (slot?.Doc == null || Math.Abs(dir.Y) < 1e-4f) return false;
            float planeY = PreviewScene.LevelY(slot.Level, _opt);
            float t = (planeY - from.Y) / dir.Y;
            if (t <= 0) return false;
            var p = from + dir * t;
            x = (int)Math.Floor(p.X);
            y = (int)Math.Floor(p.Z);
            return x >= 0 && y >= 0 && x < slot.Doc.Width && y < slot.Doc.Height;
        }

        private bool FurnDown(NV.Vector3 from, NV.Vector3 dir)
        {
            if (_opt.Furniture == null || !HitCell(from, dir, out var slot, out int x, out int y)) return false;
            var vars = VarOf(slot);
            var under = FurnitureRules.At(slot.Doc, _opt.Furniture, x, y, vars, _opt.FurnitureInfo);
            if (under != null) { _furnDrag = under; _furnLast = under; _furnGrab = (x - under.X, y - under.Y); return true; }
            var preset = PickedPreset;
            var info = _opt.FurnitureInfo(preset);
            if (info == null) { _furnHint.Text = "Pick a piece of furniture in the list first."; return true; }
            var item = new FurnitureItem { Floor = slot.Doc.Name, Preset = preset, Angle = _furnAngle, X = x, Y = y };
            var (w, d) = FurnitureRules.Footprint(item, info);
            if (!FurnitureRules.Fits(slot.Doc, _opt.Furniture, x, y, w, d, preset, null, vars, _opt.FurnitureInfo, out int a, out var why))
            { _furnHint.Text = $"Can't put {preset} there: {why}."; return true; }
            item.Address = a + 1;
            item.Variation = vars(a);
            _opt.Furniture.Add(item);
            _furnLast = item;
            FurnChanged();
            return true;
        }

        private void FurnMove(NV.Vector3 from, NV.Vector3 dir, bool dragging)
        {
            if (_opt.Furniture == null) return;
            if (!HitCell(from, dir, out var slot, out int x, out int y))
            {
                if (_opt.Ghost != null || _opt.Highlight != null) { _opt.Ghost = null; _opt.Highlight = null; _furnHover = null; RebuildFaces(); }
                return;
            }
            var vars = VarOf(slot);
            if (dragging && _furnDrag != null)
            {
                int nx = x - _furnGrab.x, ny = y - _furnGrab.y;
                if (nx == _furnDrag.X && ny == _furnDrag.Y) return;
                var (w, d) = FurnitureRules.Footprint(_furnDrag, _opt.FurnitureInfo(_furnDrag.Preset));
                if (FurnitureRules.Fits(slot.Doc, _opt.Furniture, nx, ny, w, d, _furnDrag.Preset, _furnDrag, vars, _opt.FurnitureInfo, out _, out _))
                { _furnDrag.X = nx; _furnDrag.Y = ny; RebuildFaces(); }
                return;
            }
            var under = FurnitureRules.At(slot.Doc, _opt.Furniture, x, y, vars, _opt.FurnitureInfo);
            bool ok = false;
            FurnitureItem ghost = null;
            var info = _opt.FurnitureInfo(PickedPreset);
            if (under == null && info != null)
            {
                ghost = new FurnitureItem { Floor = slot.Doc.Name, Preset = info.Name, Angle = _furnAngle, X = x, Y = y };
                var (w, d) = FurnitureRules.Footprint(ghost, info);
                ok = FurnitureRules.Fits(slot.Doc, _opt.Furniture, x, y, w, d, info.Name, null, vars, _opt.FurnitureInfo, out _, out _);
            }
            var key = (x, y, ok, under);
            if (_furnHover.HasValue && _furnHover.Value.Equals(key) && (_opt.Ghost?.Angle ?? _furnAngle) == _furnAngle) return;
            _furnHover = key;
            _opt.Ghost = ghost;
            _opt.GhostFits = ok;
            _opt.GhostLevel = slot.Level;
            _opt.Highlight = under;
            _furnHint.Text = under != null ? $"{under.Preset} (turned {under.Angle}°): drag to move, R turns, right-click or Del removes."
                                           : ghost != null ? (ok ? $"Click to put {ghost.Preset} here." : $"{ghost.Preset} doesn't fit here.") : "Pick a piece of furniture in the list.";
            RebuildFaces();
        }

        private void FurnChanged()
        {
            if (_furnish) Remember();
            var owner = FurnitureOwner;
            if (owner != null) owner.Dirty = true;
            _furnHover = null;
            FurnitureRules.RaiseChanged(this);
            RebuildFaces();
        }

        /// <summary>Only the floors and furniture again (not the game's model): quick enough while the mouse moves.</summary>
        private void RebuildFaces()
        {
            _view.SetFaces(PreviewScene.Build(_slots, _opt), keepCamera: true);
            _view.SetFurniture(FurnitureInstances());
        }

        // ---- the game's furniture models (exported by the plugin to Export/furniture)
        private readonly Dictionary<string, LoadedModel> _furnModels = new(StringComparer.Ordinal);
        private const float UnitsPerNode = 1.8f;

        private LoadedModel FurnitureModel(string preset)
        {
            if (string.IsNullOrEmpty(preset) || _ws.ModDir == null) return null;
            if (_furnModels.TryGetValue(preset, out var m)) return m;
            var folder = Path.Combine(_ws.ExportDir, ModPaths.FurnitureModelsDir, ModPaths.ModelFolder(preset));
            m = Directory.Exists(folder) ? ModelLoader.Load(folder, out _) : null;
            _furnModels[preset] = m;
            return m;
        }

        /// <summary>
        /// Where each placed piece's model goes: centred on its footprint, standing on its storey's floor, turned like in
        /// the game (the game's angle is the editor's + 180, Unity's +Z is up the plan), in the scene's cell units.
        /// </summary>
        private List<Preview3D.FurnitureInstance> FurnitureInstances()
        {
            var list = new List<Preview3D.FurnitureInstance>();
            if (_opt.Furniture == null || !_opt.ShowFurniture) return list;
            foreach (var slot in _slots)
            {
                if (slot.Doc == null) continue;
                var vars = VarOf(slot);
                float y0 = PreviewScene.LevelY(slot.Level, _opt);
                foreach (var it in FurnitureRules.OnFloor(slot.Doc, _opt.Furniture, vars))
                {
                    var model = FurnitureModel(it.Preset);
                    if (model == null) continue;
                    var (w, d) = FurnitureRules.Footprint(it, _opt.FurnitureInfo(it.Preset));
                    // model in Unity space (un-mirror X), centred, standing on y = 0
                    float cx = -(model.Min.X + model.Max.X) / 2f, cz = (model.Min.Z + model.Max.Z) / 2f;
                    float gameAngle = ((it.Angle + 180) % 360) * MathF.PI / 180f;
                    var xf = NV.Matrix4x4.CreateScale(-1, 1, 1)
                           * NV.Matrix4x4.CreateTranslation(-cx, -model.Min.Y, -cz)
                           * NV.Matrix4x4.CreateScale(FitScale(model, _opt.FurnitureInfo(it.Preset)))
                           * NV.Matrix4x4.CreateRotationY(gameAngle)
                           * NV.Matrix4x4.CreateScale(1 / UnitsPerNode, 1 / UnitsPerNode, -1 / UnitsPerNode)
                           * NV.Matrix4x4.CreateTranslation(it.X + w / 2f, y0, it.Y + d / 2f);
                    list.Add(new Preview3D.FurnitureInstance { Model = model, Transform = xf, Level = slot.Level });
                }
            }
            return list;
        }

        /// <summary>
        /// 1, unless a model is far bigger than its piece's footprint (some mods' models are made at another size and
        /// scaled in the game): then it's shrunk to fit the footprint, so it doesn't fill half the building.
        /// </summary>
        private static float FitScale(LoadedModel model, CatalogFurniture info)
        {
            if (info == null) return 1f;
            float sx = (model.Max.X - model.Min.X) / UnitsPerNode, sz = (model.Max.Z - model.Min.Z) / UnitsPerNode;
            float fit = MathF.Min(Math.Max(1, info.W) * 1.2f / MathF.Max(sx, 1e-3f), Math.Max(1, info.D) * 1.2f / MathF.Max(sz, 1e-3f));
            return fit < 1f / 1.6f ? fit : 1f;
        }

        private FloorSlot SelectedSlot => _floors.SelectedIndex >= 0 && _floors.SelectedIndex < _slots.Count
            ? _slots[_slots.Count - 1 - _floors.SelectedIndex]   // list shows the top storey first
            : null;

        private static string LevelName(int level) => level == 0 ? "G" : level > 0 ? level.ToString() : "B" + (-level);

        private void DrawFloorItem(object sender, DrawItemEventArgs e)
        {
            Theme.DrawItemBackground(e);
            if (e.Index < 0 || e.Index >= _slots.Count) return;
            var s = _slots[_slots.Count - 1 - e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var fore = s.Doc == null ? Color.Firebrick : Theme.ItemFore(e);
            var text = $"{LevelName(s.Level),-3} {(s.Candidates.Count == 0 ? "(no floor plans)" : s.ChosenName)}" +
                       (s.Doc == null && s.Candidates.Count > 0 ? "  (missing)" : "") +
                       (s.Candidates.Count > 1 ? $"  [{s.Chosen + 1}/{s.Candidates.Count}]" : "");
            TextRenderer.DrawText(e.Graphics, text, e.Font, new Point(e.Bounds.X + 4, e.Bounds.Y + 2), fore);
            e.DrawFocusRectangle();
        }

        // ------------------------------------------------------------------ floor plan data

        private FloorDoc Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_singleDoc != null && name == _singleName) return _singleDoc;
            var own = _ws.Buildings.SelectMany(b => b.Floors).FirstOrDefault(f => f.Name == name);
            if (own != null) return own.Doc;
            try { return _ws.LoadGameFloor(name); }
            catch { return null; }
        }

        private List<FloorSlot> BuildSlots()
        {
            var slots = new List<FloorSlot>();
            if (_singleDoc != null)
            {
                slots.Add(new FloorSlot { Level = 0, Layout = "Floor", Candidates = { _singleName } });
                return slots;
            }

            var def = _building.Def;
            var cb = _ws.FindBaseBuilding(def.BasePreset);
            var floorLayouts = def.ReplaceFloorLayouts && def.FloorLayouts.Count > 0 ? def.FloorLayouts : cb?.FloorLayouts ?? new List<FloorLayoutDef>();
            var basementLayouts = def.ReplaceBasementLayouts ? def.BasementLayouts : cb?.BasementLayouts ?? new List<FloorLayoutDef>();

            int level = 0;
            for (int li = 0; li < floorLayouts.Count; li++)
            {
                var layout = floorLayouts[li];
                for (int k = 0; k < Math.Max(1, layout.Floors); k++)
                    slots.Add(MakeSlot(level++, $"Layout {li + 1}", layout, k));
            }
            level = -1;
            for (int li = 0; li < basementLayouts.Count; li++)
            {
                var layout = basementLayouts[li];
                for (int k = 0; k < Math.Max(1, layout.Floors); k++)
                    slots.Add(MakeSlot(level--, $"Basement {li + 1}", layout, k));
            }
            slots.Sort((a, b) => a.Level.CompareTo(b.Level));
            return slots;
        }

        private FloorSlot MakeSlot(int level, string layoutName, FloorLayoutDef layout, int indexInLayout)
        {
            var slot = new FloorSlot { Level = level, Layout = layoutName, Candidates = layout.Blueprints.ToList() };
            if (slot.Candidates.Count > 0)
            {
                // keep the user's pick when refreshing; otherwise cycle through the plans so variety shows
                int kept = _chosenByLevel.TryGetValue(level, out var name) ? slot.Candidates.IndexOf(name) : -1;
                slot.Chosen = kept >= 0 ? kept : indexInLayout % slot.Candidates.Count;
            }
            return slot;
        }

        /// <summary>Re-reads the building and floor plans from the editor (including unsaved edits).</summary>
        private void Rebuild(bool keepCamera)
        {
            if (_building != null && _building.Def.BasePreset != _modelPreset) LoadModel();

            _slots = BuildSlots();
            foreach (var s in _slots) s.Doc = Resolve(s.ChosenName);

            _loading = true;
            int keepSel = _floors.SelectedIndex;
            _floors.Items.Clear();
            for (int i = 0; i < _slots.Count; i++) _floors.Items.Add(i);
            if (_floors.Items.Count > 0) _floors.SelectedIndex = Math.Clamp(keepSel, 0, _floors.Items.Count - 1);

            int min = _slots.Count > 0 ? _slots.Min(s => s.Level) : 0, max = _slots.Count > 0 ? _slots.Max(s => s.Level) : 0;
            bool wasAtTop = _cut.Value == _cut.Maximum;
            _cut.Minimum = min;
            _cut.Maximum = Math.Max(min, max);
            if (!keepCamera || wasAtTop) _cut.Value = _cut.Maximum;
            _cut.Value = Math.Clamp(_cut.Value, _cut.Minimum, _cut.Maximum);

            _title.Text = _building != null ? $"{_building.Def.Id}  (based on {_building.Def.BasePreset})" : _singleName;
            _loading = false;

            LoadPlanChoices();
            RebuildScene(keepCamera);
        }

        private void LoadPlanChoices()
        {
            _loading = true;
            _plan.Items.Clear();
            var s = SelectedSlot;
            if (s != null)
            {
                foreach (var c in s.Candidates) _plan.Items.Add(c);
                if (_plan.Items.Count > 0) _plan.SelectedIndex = Math.Clamp(s.Chosen, 0, _plan.Items.Count - 1);
            }
            _plan.Enabled = _plan.Items.Count > 1;
            _loading = false;
        }

        private void Shuffle()
        {
            var rnd = new Random();
            foreach (var s in _slots.Where(s => s.Candidates.Count > 1))
            {
                s.Chosen = rnd.Next(s.Candidates.Count);
                _chosenByLevel[s.Level] = s.ChosenName;
                s.Doc = Resolve(s.ChosenName);
            }
            LoadPlanChoices();
            RebuildScene(true);
            _floors.Invalidate();
        }

        // ------------------------------------------------------------------ game model

        /// <summary>Reloads the model and its textures (e.g. after painting the exterior).</summary>
        public void ReloadModel()
        {
            LoadModel();
            Rebuild(true);
        }

        public BuildingEntry Building => _building;

        private void LoadModel()
        {
            _view.ClearTextureCache();
            _model = null;
            _modelError = null;
            _modelPreset = _building?.Def.BasePreset;
            _customExterior = false;

            if (_building == null)
            {
                _modelError = "Only available when previewing a whole building.";
            }
            else if (ModelLoader.ExteriorExists(_ws, _building))
            {
                _model = ModelLoader.LoadExterior(ModelLoader.ExteriorFolder(_ws, _building), out _modelError);
                _customExterior = _model != null;
            }
            else
            {
                var folder = ModelLoader.FolderFor(_ws, _modelPreset);
                if (folder == null || !ModelLoader.Exists(_ws, _modelPreset))
                    _modelError = $"No exported model for '{_modelPreset}'. Start the game once with the updated plugin " +
                                  "(ExportModels on) and reopen this window.";
                else
                    _model = ModelLoader.Load(folder, out _modelError);
            }

            _align = GetAlign(_customExterior ? "custom:" + _building.Def.Id : _modelPreset);
            LoadAlignControls();

            bool ok = _model != null;
            foreach (var c in new Control[] { _showModel, _look, _night, _alignPanel }) c.Enabled = ok;
            _modelStatus.ForeColor = ok ? Color.DimGray : Color.Firebrick;
            _modelStatus.Text = ok
                ? $"{(_customExterior ? "Your exterior" : _modelPreset)}: {_model.Meshes.Count} part(s), {_model.TriangleCount:N0} triangles" +
                  (_model.Info?.Warnings.Count > 0 ? $"\r\n{_model.Info.Warnings.Count} export warning(s) in model.json" : "")
                : _modelError;
        }

        private bool ModelVisible => _model != null && _showModel.Checked && _opt.Spread == 0;

        /// <summary>Plan footprint (world X/Z) of the above-ground storeys.</summary>
        private bool PlanBounds(out NV.Vector2 min, out NV.Vector2 max)
        {
            min = new NV.Vector2(float.MaxValue);
            max = new NV.Vector2(float.MinValue);
            bool any = false;
            foreach (var s in _slots.Where(s => s.Level >= 0 && s.Doc != null))
                for (int a = 0; a < s.Doc.Addresses.Count; a++)
                {
                    if (s.Doc.AddressPreset(a) == "Outside") continue;   // the ring around the building
                    foreach (var n in s.Doc.NodesIn(a, 0))
                    {
                        min = NV.Vector2.Min(min, new NV.Vector2(n.X, n.Y));
                        max = NV.Vector2.Max(max, new NV.Vector2(n.X + 1, n.Y + 1));
                        any = true;
                    }
                }
            return any;
        }

        /// <summary>Game units per floor-plan cell: a building's 15×15-cell interior is 27 units wide, a whole 21-cell plot 37.8.</summary>
        private const float UnitsPerCell = 1.8f;

        /// <summary>
        /// Places the model over the floor plans: OBJ → Unity (un-mirror X), centre the footprint, optional mirror and
        /// rotation, scale from game units to cells (times the user's scale), then move onto the plans' centre.
        /// Also works out the storey height that makes the floors line up with the model.
        /// </summary>
        private NV.Matrix4x4 ModelTransform(out float storeyHeight)
        {
            // Where the game puts a floor plan in a building model (worked out from the exported game models and their
            // floors): cell x → Unity X = (x − 10.5)·1.8, cell row y → Unity Z = (10.5 − y)·1.8, street at Y = 0 (the model starts above the ground floor),
            // one storey = 5.4 = 3 cells. The preview works in cells with Z = row, so: un-mirror the OBJ, then map back.
            const float c = 10.5f;
            var toPlan = NV.Matrix4x4.CreateScale(-1, 1, 1)                                   // OBJ -> Unity
                       * NV.Matrix4x4.CreateTranslation(0, -GroundY, 0)
                       * NV.Matrix4x4.CreateScale(1 / UnitsPerCell, 1 / UnitsPerCell, -1 / UnitsPerCell)
                       * NV.Matrix4x4.CreateTranslation(c, 0, c);
            float scale = Math.Max(0.1f, _align.ScalePercent / 100f);
            var user = NV.Matrix4x4.CreateTranslation(-c, 0, -c)
                     * (_align.Mirror ? NV.Matrix4x4.CreateScale(-1, 1, 1) : NV.Matrix4x4.Identity)
                     * NV.Matrix4x4.CreateRotationY(_align.Rotate * MathF.PI / 180f)
                     * NV.Matrix4x4.CreateScale(scale)
                     * NV.Matrix4x4.CreateTranslation(c + _align.OffsetX, 0, c + _align.OffsetZ);
            storeyHeight = _align.StoreyHeight > 0 ? _align.StoreyHeight / UnitsPerCell : StoreyUnits / UnitsPerCell * scale;
            return toPlan * user;
        }

        // Street level. Game models (and ours) start one storey up, at about 5.3: the game draws the ground floor's outside itself.
        private const float GroundY = 0f, StoreyUnits = 5.4f;

        private void AlignChanged()
        {
            SaveAlign();
            RebuildScene(true);
        }

        private void LoadAlignControls()
        {
            _loading = true;
            _mirror.Checked = _align.Mirror;
            _scale.Value = (decimal)Math.Clamp(_align.ScalePercent, 10f, 400f);
            _offX.Value = (decimal)Math.Clamp(_align.OffsetX, -200f, 200f);
            _offZ.Value = (decimal)Math.Clamp(_align.OffsetZ, -200f, 200f);
            _storey.Value = (decimal)Math.Clamp(_align.StoreyHeight, 0f, 50f);
            _loading = false;
        }

        private string AlignFile => _ws.ModDir == null ? null : Path.Combine(_ws.ModDir, "Editor", "preview.json");

        private ModelAlign GetAlign(string preset)
        {
            if (_alignments == null)
            {
                _alignments = new Dictionary<string, ModelAlign>();
                try
                {
                    if (AlignFile != null && File.Exists(AlignFile))
                        _alignments = JsonSerializer.Deserialize<Dictionary<string, ModelAlign>>(File.ReadAllText(AlignFile), Json.Options)
                                      ?? new Dictionary<string, ModelAlign>();
                }
                catch { /* start fresh */ }
            }
            if (string.IsNullOrEmpty(preset)) return new ModelAlign();
            if (!_alignments.TryGetValue(preset, out var a)) _alignments[preset] = a = new ModelAlign();
            return a;
        }

        private void SaveAlign()
        {
            if (string.IsNullOrEmpty(_modelPreset) || AlignFile == null) return;
            _alignments[_modelPreset] = _align;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AlignFile));
                File.WriteAllText(AlignFile, JsonSerializer.Serialize(_alignments, Json.Options));
            }
            catch { /* not important enough to interrupt */ }
        }

        // ------------------------------------------------------------------ scene

        private void RebuildScene(bool keepCamera)
        {
            NV.Matrix4x4 xf = NV.Matrix4x4.Identity;
            if (_model != null)
            {
                xf = ModelTransform(out float storey);
                if (ModelVisible)
                {
                    _opt.LevelHeight = storey;
                    _opt.WallHeight = storey * 0.87f;
                }
                else
                {
                    _opt.LevelHeight = 3f;
                    _opt.WallHeight = 2.6f;
                }
                _storey.Increment = 0.1m;
            }

            var faces = PreviewScene.Build(_slots, _opt);

            // pier under the plans and the model
            NV.Vector2 fmin, fmax;
            if (!PlanBounds(out fmin, out fmax)) { fmin = NV.Vector2.Zero; fmax = NV.Vector2.Zero; }
            if (ModelVisible)
            {
                foreach (var c in new[] { _model.Min, _model.Max, new NV.Vector3(_model.Min.X, 0, _model.Max.Z), new NV.Vector3(_model.Max.X, 0, _model.Min.Z) })
                {
                    var p = NV.Vector3.Transform(c, xf);
                    fmin = NV.Vector2.Min(fmin, new NV.Vector2(p.X, p.Z));
                    fmax = NV.Vector2.Max(fmax, new NV.Vector2(p.X, p.Z));
                }
            }
            _view.FootprintMin = fmin;
            _view.FootprintMax = fmax;

            _view.ShowModel = ModelVisible;
            ApplyCut(refresh: false);
            _view.SetModel(ModelVisible ? _model : null, xf, keepCamera: true);
            _view.SetFaces(faces, keepCamera);   // frames the camera on floors + model
            _view.SetFurniture(FurnitureInstances());

            int missing = _slots.Count(s => s.Doc == null);
            _stats.Text = $"{_slots.Count} storey(s) · {faces.Count:N0} faces" +
                          (ModelVisible ? $" · model {_model.TriangleCount:N0} tris" : "") +
                          (missing > 0 ? $"\r\n{missing} storey(s) have no floor plan to show (red in the list)." : "");
        }

        private void ApplyCut() => ApplyCut(refresh: true);

        private void ApplyCut(bool refresh)
        {
            var s = SelectedSlot;
            int top;
            if (_onlySelected.Checked && s != null)
            {
                _view.MinLevel = s.Level;
                _view.MaxLevel = s.Level;
                top = s.Level;
                _cutLabel.Text = $"Showing only storey {LevelName(s.Level)}";
            }
            else
            {
                _view.MinLevel = int.MinValue;
                _view.MaxLevel = _cut.Value;
                top = _cut.Value;
                _cutLabel.Text = _cut.Value == _cut.Maximum ? "Show storeys up to: all" : $"Show storeys up to: {LevelName(_cut.Value)}";
            }

            // cut the model at the top of the highest storey shown, so the cutaway shows the inside
            bool cutting = _onlySelected.Checked || _cut.Value < _cut.Maximum;
            _view.ModelCutY = cutting ? PreviewScene.LevelY(top + 1, _opt) - 0.05f : float.MaxValue;

            if (refresh) _view.RefreshScene();
        }
    }
}

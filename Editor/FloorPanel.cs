using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>Floor plan editor: tool bar, canvas, and a side panel for addresses, variations and rooms.</summary>
    public sealed class FloorPanel : UserControl
    {
        private readonly Workspace _ws;
        private FloorEntry _entry;          // null for read-only game floors
        private FloorDoc _doc;
        private bool _loading;

        private readonly FloorCanvas _canvas = new();
        private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(28, 30, 34) };
        private readonly Label _hint = new() { Dock = DockStyle.Top, Height = 22, Padding = new Padding(4, 4, 0, 0), ForeColor = Color.DimGray };

        // tool bar
        private readonly FlowLayoutPanel _tools = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), WrapContents = true };
        private readonly ComboBox _tileType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly ComboBox _wallPreset = new()
        {
            DropDownStyle = ComboBoxStyle.DropDown, Width = 220,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        private readonly CheckBox _mirror = new() { Text = "Both sides", Checked = true, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        private readonly ComboBox _flag = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        private readonly ComboBox _rotation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        private readonly Control[] _wallOpts, _typeOpts, _flagOpts, _balconyOpts;
        private readonly ComboBox _balRailing = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        private readonly ComboBox _balDoor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };

        // side panel
        private readonly Label _floorName = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        private readonly NumericUpDown _floorHeight = new() { Minimum = -1000, Maximum = 1000, Width = 70 };
        private readonly NumericUpDown _ceilHeight = new() { Minimum = -1000, Maximum = 1000, Width = 70 };
        private readonly NumericUpDown _npt = new() { Minimum = 0, Maximum = 10, Width = 50 };
        private readonly Label _size = new() { AutoSize = true, ForeColor = Color.DimGray };
        private readonly ListBox _addresses = new() { Height = 140, Dock = DockStyle.Top, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 20 };
        private readonly ComboBox _addrPreset = new()
        {
            Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        private readonly ComboBox _variation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly ListBox _rooms = new() { Height = 120, Dock = DockStyle.Top, IntegralHeight = false };
        private readonly ComboBox _roomLayout = new()
        {
            Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        private readonly TextBox _inspect = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f) };
        private readonly Panel _side = new() { Dock = DockStyle.Right, Width = 320, AutoScroll = true, Padding = new Padding(6) };
        private Control[] _editOnly;
        private readonly ComboBox _place = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        private const string PlaceAny = "(the game picks, like for its own buildings)";

        public event Action Changed;
        public event Action<string> Status;
        public event Action Preview3DRequested;

        public FloorPanel(Workspace ws)
        {
            _ws = ws;
            Dock = DockStyle.Fill;

            _tileType.Items.AddRange(new object[] { "0  none (open)", "1  floor + ceiling", "2  floor only", "3  ceiling only", "4  none, but indoors" });
            _tileType.SelectedIndex = 1;
            _flag.Items.AddRange(new object[] { "Entrance", "Main entrance", "Stairwell", "Elevator" });
            _flag.SelectedIndex = 0;
            _rotation.Items.AddRange(new object[] { "0", "90", "180", "270" });
            _rotation.SelectedIndex = 0;

            _typeOpts = new Control[] { _tileType };
            _wallOpts = new Control[] { _wallPreset, _mirror };
            _flagOpts = new Control[] { _flag, _rotation };
            _balconyOpts = new Control[]
            {
                new Label { Text = "Edge:", AutoSize = true, Margin = new Padding(6, 7, 0, 0) }, _balRailing,
                new Label { Text = "Door:", AutoSize = true, Margin = new Padding(6, 7, 0, 0) }, _balDoor,
            };

            BuildToolbar();
            BuildSide();

            _scroll.Controls.Add(_canvas);
            Controls.Add(_scroll);
            Controls.Add(_side);
            Controls.Add(_hint);
            Controls.Add(_tools);

            _canvas.Changed += () => { RefreshRooms(); UpdateSizeLabel(); Changed?.Invoke(); };
            _canvas.CellPicked += OnCellPicked;
            _canvas.StructureChanged += Reload;
            _canvas.HoverText += t => Status?.Invoke(t);

            SetTool(FloorTool.Room);
        }

        // ------------------------------------------------------------------ UI construction

        private RadioButton ToolButton(string text, FloorTool tool, string tip)
        {
            var rb = new RadioButton { Text = text, Appearance = Appearance.Button, AutoSize = true, Tag = tool, Margin = new Padding(2), MinimumSize = new Size(0, 28), TextAlign = ContentAlignment.MiddleCenter };
            rb.CheckedChanged += (s, e) => { if (rb.Checked) SetTool(tool); };
            new ToolTip().SetToolTip(rb, tip);
            return rb;
        }

        private static Button SmallBtn(string text, EventHandler click, int width = 70)
        {
            var b = new Button { Text = text, Width = width, Height = 26, Margin = new Padding(2) };
            b.Click += click;
            return b;
        }

        private void BuildToolbar()
        {
            _tools.Controls.Add(ToolButton("Inspect", FloorTool.Inspect, "Click a cell to see its data."));
            _tools.Controls.Add(ToolButton("Paint room", FloorTool.Room, "Move cells into the selected room (within the selected address)."));
            _tools.Controls.Add(ToolButton("Paint address", FloorTool.Address, "Give cells to the selected address (apartment, shop, hallway…)."));
            _tools.Controls.Add(ToolButton("Erase", FloorTool.Erase, "Remove cells from the floor."));
            _tools.Controls.Add(ToolButton("Tile type", FloorTool.TileType, "Set floor/ceiling type of cells."));
            _tools.Controls.Add(ToolButton("Walls", FloorTool.Wall, "Left-click a cell edge to place the selected wall/door/window, right-click to remove."));
            _tools.Controls.Add(ToolButton("Entrances & stairs", FloorTool.TileFlag, "Left-click a tile to set the flag, right-click to clear."));
            _tools.Controls.Add(ToolButton("Balcony", FloorTool.Balcony, "Drag over cells along an outer wall: they become a balcony you can walk on, with a door and a railing."));
            _tools.Controls.Add(new Label { Text = " ", Width = 8 });
            foreach (var c in _typeOpts.Concat(_wallOpts).Concat(_flagOpts).Concat(_balconyOpts)) _tools.Controls.Add(c);
            _tools.Controls.Add(new Label { Text = " ", Width = 8 });
            _tools.Controls.Add(SmallBtn("Undo", (s, e) => DoUndo(), 55));
            _tools.Controls.Add(SmallBtn("Redo", (s, e) => DoRedo(), 55));
            _tools.Controls.Add(SmallBtn("−", (s, e) => _canvas.Zoom(-4), 30));
            _tools.Controls.Add(SmallBtn("+", (s, e) => _canvas.Zoom(4), 30));
            _tools.Controls.Add(SmallBtn("3D view", (s, e) => Preview3DRequested?.Invoke(), 70));

            _tileType.SelectedIndexChanged += (s, e) => _canvas.TileTypeToPaint = _tileType.SelectedIndex;
            _wallPreset.TextChanged += (s, e) => _canvas.WallPreset = GameNames.WallIdFromLabel(_wallPreset.Text);
            _mirror.CheckedChanged += (s, e) => _canvas.MirrorWalls = _mirror.Checked;
            _flag.SelectedIndexChanged += (s, e) => _canvas.TileFlagKey = new[] { "i_e", "m_e", "s_t", "e_l" }[Math.Max(0, _flag.SelectedIndex)];
            _rotation.SelectedIndexChanged += (s, e) => _canvas.TileRotation = _rotation.SelectedIndex * 90;
            _balRailing.SelectedIndexChanged += (s, e) => _canvas.BalconyRailing = GameNames.WallIdFromLabel(_balRailing.Text);
            _balDoor.SelectedIndexChanged += (s, e) => _canvas.BalconyDoor = GameNames.WallIdFromLabel(_balDoor.Text);
        }

        /// <summary>Railing and door choices for the balcony tool, from the game's wall list.</summary>
        private void FillBalconyChoices()
        {
            string Keep(ComboBox c) => c.SelectedItem as string;
            var oldR = Keep(_balRailing);
            var oldD = Keep(_balDoor);
            _balRailing.Items.Clear();
            _balDoor.Items.Clear();
            foreach (var id in _ws.WallPresets)
            {
                var kind = GameNames.KindOf(id);
                var name = GameNames.WallName(id) ?? "";
                if (kind == GameNames.WallKind.Railing || name == "NothingWall") _balRailing.Items.Add(GameNames.WallLabel(id));
                if (kind == GameNames.WallKind.Door) _balDoor.Items.Add(GameNames.WallLabel(id));
            }
            if (_balRailing.Items.Count == 0) _balRailing.Items.Add("11  (open edge)");
            if (_balDoor.Items.Count == 0) _balDoor.Items.Add("7  (door)");
            int Pick(ComboBox c, string old, string want)
            {
                if (old != null && c.Items.Contains(old)) return c.Items.IndexOf(old);
                for (int i = 0; i < c.Items.Count; i++)
                    if (((string)c.Items[i]).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return i;
                return 0;
            }
            _balRailing.SelectedIndex = Pick(_balRailing, oldR, "DecoHandrail");
            _balDoor.SelectedIndex = Pick(_balDoor, oldD, "InteriorDoorway");
            _canvas.BalconyWall = _ws.WallPresets.FirstOrDefault(id => GameNames.WallName(id) == "DefaultWalls") ?? "0";
        }

        private static GroupBox Group(string title, int height)
        {
            return new GroupBox { Text = title, Dock = DockStyle.Top, Height = height, Padding = new Padding(6) };
        }

        private void BuildSide()
        {
            // Dock=Top stacks in reverse add order, so add bottom-most first.
            var inspect = Group("Selected cell", 200);
            inspect.Dock = DockStyle.Fill;
            inspect.Controls.Add(_inspect);

            // rooms
            var rooms = Group("Rooms in this address variation", 230);
            var roomBtns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
            roomBtns.Controls.Add(SmallBtn("Add room", (s, e) => AddRoom(), 80));
            roomBtns.Controls.Add(SmallBtn("Delete", (s, e) => DeleteRoom()));
            rooms.Controls.Add(_roomLayout);
            rooms.Controls.Add(new Label { Text = "Room type:", Dock = DockStyle.Top, Height = 18 });
            rooms.Controls.Add(roomBtns);
            rooms.Controls.Add(_rooms);

            // variations
            var vars = Group("Layout variation of this address", 62);
            var varRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            varRow.Controls.Add(_variation);
            varRow.Controls.Add(SmallBtn("Copy", (s, e) => AddVariation(), 55));
            varRow.Controls.Add(SmallBtn("Delete", (s, e) => DeleteVariation(), 60));
            vars.Controls.Add(varRow);

            // addresses
            var addrs = Group("Addresses (apartments, shops, halls)", 300);
            var addrBtns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
            addrBtns.Controls.Add(SmallBtn("Add", (s, e) => AddAddress(), 55));
            addrBtns.Controls.Add(SmallBtn("Delete", (s, e) => DeleteAddress(), 60));
            addrBtns.Controls.Add(SmallBtn("Colour…", (s, e) => PickColour(), 70));
            addrs.Controls.Add(_place);
            addrs.Controls.Add(new Label { Text = "Always make it this kind of place:", Dock = DockStyle.Top, Height = 18 });
            addrs.Controls.Add(_addrPreset);
            addrs.Controls.Add(new Label { Text = "What this address is:", Dock = DockStyle.Top, Height = 18 });
            addrs.Controls.Add(addrBtns);
            addrs.Controls.Add(_addresses);

            // floor
            var floor = Group("Floor", 150);
            var ft = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            ft.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            ft.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ft.Controls.Add(_floorName, 0, 0); ft.SetColumnSpan(_floorName, 2);
            ft.Controls.Add(new Label { Text = "Default floor height:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            ft.Controls.Add(_floorHeight, 1, 1);
            ft.Controls.Add(new Label { Text = "Default ceiling height:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            ft.Controls.Add(_ceilHeight, 1, 2);
            ft.Controls.Add(new Label { Text = "Cells per tile (0 = auto):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
            ft.Controls.Add(_npt, 1, 3);
            ft.Controls.Add(_size, 0, 4); ft.SetColumnSpan(_size, 2);
            floor.Controls.Add(ft);

            _side.Controls.Add(inspect);
            _side.Controls.Add(rooms);
            _side.Controls.Add(vars);
            _side.Controls.Add(addrs);
            _side.Controls.Add(floor);

            _editOnly = new Control[] { _floorHeight, _ceilHeight, addrBtns, _addrPreset, varRow, roomBtns, _roomLayout };

            // events
            _floorHeight.ValueChanged += (s, e) => { if (_loading || _doc.ReadOnly) return; Edit(() => _doc.DefaultFloorHeight = (int)_floorHeight.Value); };
            _ceilHeight.ValueChanged += (s, e) => { if (_loading || _doc.ReadOnly) return; Edit(() => _doc.DefaultCeilingHeight = (int)_ceilHeight.Value); };
            _npt.ValueChanged += (s, e) =>
            {
                if (_loading) return;
                _doc.NodesPerTileOverride = (int)_npt.Value;
                _doc.Analyze();
                _canvas.UpdateSize();
                _canvas.Invalidate();
                UpdateSizeLabel();
            };

            _addresses.DrawItem += DrawAddress;
            _place.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || _entry?.Owner == null || _canvas.CurrentAddress < 0) return;
                var list = _entry.Owner.Def.ReservedPlaces ??= new List<ReservedPlace>();
                int addr = _canvas.CurrentAddress + 1;
                list.RemoveAll(r => r.Floor == _doc.Name && r.Address == addr);
                var text = _place.SelectedItem as string ?? PlaceAny;
                if (_place.SelectedIndex > 0 && !text.StartsWith("—", StringComparison.Ordinal))
                    list.Add(new ReservedPlace { Floor = _doc.Name, Address = addr, Place = text.Split(new[] { "  " }, StringSplitOptions.None)[0] });
                _entry.Owner.Dirty = true;
                Changed?.Invoke();
            };
            _addresses.SelectedIndexChanged += (s, e) =>
            {
                if (_loading) return;
                _canvas.CurrentAddress = _addresses.SelectedIndex;
                _canvas.CurrentVariation = 0;
                _canvas.CurrentRoom = RealRoom();
                RefreshAddressDetails();
                _canvas.Invalidate();
            };
            _addrPreset.TextChanged += (s, e) =>
            {
                if (_loading || _doc.ReadOnly || _canvas.CurrentAddress < 0) return;
                _doc.SetAddressPreset(_canvas.CurrentAddress, GameNames.AddressTypeFromLabel(_addrPreset.Text));
                _addresses.Invalidate();
                Changed?.Invoke();
            };
            _variation.SelectedIndexChanged += (s, e) =>
            {
                if (_loading) return;
                _canvas.CurrentVariation = Math.Max(0, _variation.SelectedIndex);
                _canvas.CurrentRoom = RealRoom();
                RefreshRooms();
                _canvas.Invalidate();
            };
            _rooms.SelectedIndexChanged += (s, e) =>
            {
                if (_loading) return;
                _canvas.CurrentRoom = Math.Max(0, _rooms.SelectedIndex);
                LoadRoomLayout();
                _canvas.Invalidate();
            };
            _roomLayout.TextChanged += (s, e) =>
            {
                if (_loading || _doc.ReadOnly || !HasRoom) return;
                _doc.SetRoomLayout(_canvas.CurrentAddress, _canvas.CurrentVariation, _canvas.CurrentRoom, _roomLayout.Text.Trim());
                UpdateRoomItem(_canvas.CurrentRoom);
                Changed?.Invoke();
            };
        }

        /// <summary>Refreshes one line of the room list without rebuilding it (keeps the selection and typing).</summary>
        private void UpdateRoomItem(int r)
        {
            int a = _canvas.CurrentAddress, v = _canvas.CurrentVariation;
            if (r < 0 || r >= _rooms.Items.Count || r >= _doc.RoomCount(a, v)) return;
            int cells = FloorDoc.NodesOf(_doc.Room(a, v, r)).Count;
            var l = _doc.RoomLayout(a, v, r);
            var text = _doc.IsNullRoom(a, v, r)
                ? $"(Null room · {cells} cells · the game needs it; keep it empty in shops and homes)"
                : $"{(l.Length > 0 ? l : "(no type)")} · {cells} cells · id {_doc.RoomId(a, v, r)}";
            if ((string)_rooms.Items[r] == text) return;
            bool was = _loading;
            _loading = true;
            _rooms.Items[r] = text;
            _rooms.SelectedIndex = r;
            _loading = was;
        }

        // ------------------------------------------------------------------ binding

        public FloorDoc Doc => _doc;
        public FloorEntry Entry => _entry;

        public void Bind(FloorDoc doc, FloorEntry entry)
        {
            _loading = true;
            _doc = doc;
            _entry = entry;
            _canvas.SetDoc(doc);

            FillCombo(_wallPreset, _ws.WallPresets.Select(GameNames.WallLabel));
            if (_wallPreset.Text.Length == 0 && _wallPreset.Items.Count > 0)
            {
                var guess = _wallPreset.Items.Cast<string>().FirstOrDefault(s => s.IndexOf("InteriorDoorway", StringComparison.OrdinalIgnoreCase) >= 0)
                         ?? _wallPreset.Items.Cast<string>().FirstOrDefault(s => s.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0);
                _wallPreset.Text = guess ?? _wallPreset.Items[0].ToString();
            }
            FillCombo(_addrPreset, _ws.AddressPresets.Select(GameNames.AddressTypeLabel));
            FillBalconyChoices();
            FillCombo(_roomLayout, _ws.RoomLayouts);

            _floorName.Text = doc.Name + (doc.ReadOnly ? "  (game floor, read-only)" : "");
            _floorHeight.Value = Math.Clamp(doc.DefaultFloorHeight, -1000, 1000);
            _ceilHeight.Value = Math.Clamp(doc.DefaultCeilingHeight, -1000, 1000);
            _npt.Value = Math.Clamp(doc.NodesPerTileOverride, 0, 10);

            foreach (Control c in _tools.Controls)
                if (c is RadioButton rb && (FloorTool)rb.Tag != FloorTool.Inspect) rb.Enabled = !doc.ReadOnly;
            foreach (var c in _editOnly) c.Enabled = !doc.ReadOnly;
            if (doc.ReadOnly) SetTool(FloorTool.Inspect);

            _loading = false;
            RefreshAddresses();
            UpdateSizeLabel();
            _inspect.Text = "";
        }

        private static void FillCombo(ComboBox cb, System.Collections.Generic.IEnumerable<string> items)
        {
            var text = cb.Text;
            cb.BeginUpdate();
            cb.Items.Clear();
            cb.Items.AddRange(items.Cast<object>().ToArray());
            cb.EndUpdate();
            cb.Text = text;
        }

        private void UpdateSizeLabel()
        {
            if (_doc == null) return;
            _size.Text = $"Size field: {_doc.SizeText} · grid {_doc.Width}×{_doc.Height} cells · {_doc.NodesPerTile} cells per tile";
        }

        private void RefreshAddresses()
        {
            _loading = true;
            _addresses.Items.Clear();
            for (int a = 0; a < _doc.Addresses.Count; a++) _addresses.Items.Add(a);
            if (_canvas.CurrentAddress >= _doc.Addresses.Count) _canvas.CurrentAddress = _doc.Addresses.Count - 1;
            if (_canvas.CurrentAddress < 0 && _doc.Addresses.Count > 0) _canvas.CurrentAddress = 0;
            _addresses.SelectedIndex = _canvas.CurrentAddress;
            _loading = false;
            RefreshAddressDetails();
        }

        private void RefreshAddressDetails()
        {
            _loading = true;
            int a = _canvas.CurrentAddress;
            _addrPreset.Text = a >= 0 ? GameNames.AddressTypeLabel(_doc.AddressPreset(a)) : "";
            _variation.Items.Clear();
            for (int v = 0; v < _doc.VariationCount(a); v++) _variation.Items.Add($"Variation {v + 1}");
            if (_canvas.CurrentVariation >= _variation.Items.Count) _canvas.CurrentVariation = 0;
            if (_variation.Items.Count > 0) _variation.SelectedIndex = _canvas.CurrentVariation;
            FillPlaces(a);
            _loading = false;
            RefreshRooms();
        }

        /// <summary>
        /// Kinds of places (the game's AddressPresets, and ones mods add) that fit this address's type; the chosen one is
        /// stored in the building (ReservedPlaces) and the plugin makes the game pick it for this unit.
        /// </summary>
        private void FillPlaces(int a)
        {
            _place.Items.Clear();
            _place.Items.Add(PlaceAny);
            bool own = _entry?.Owner != null && a >= 0;
            _place.Enabled = own && !_doc.ReadOnly;
            if (!own) { _place.SelectedIndex = 0; return; }
            var type = _doc.AddressPreset(a);
            var places = _ws.Catalog.Places ?? new List<CatalogPlace>();
            string PlaceLabel(CatalogPlace p) => p.Name + (p.OnlyIn.Count > 0 ? $"  (normally only in {string.Join(", ", p.OnlyIn.Take(3))})" : "");
            foreach (var p in places.Where(x => x.Fits.Contains(type)).OrderBy(x => x.Name)) _place.Items.Add(PlaceLabel(p));
            var others = places.Where(x => !x.Fits.Contains(type)).OrderBy(x => x.Name).ToList();
            if (others.Count > 0)
            {
                _place.Items.Add("— these normally go into other address types —");
                foreach (var p in others) _place.Items.Add(PlaceLabel(p));
            }
            var current = _entry.Owner.Def.ReservedPlaces?.FirstOrDefault(r => r.Floor == _doc.Name && r.Address == a + 1);
            int idx = 0;
            if (current != null)
            {
                for (int i = 0; i < _place.Items.Count; i++)
                    if (((string)_place.Items[i]).Split(new[] { "  " }, StringSplitOptions.None)[0] == current.Place) { idx = i; break; }
                if (idx == 0) { _place.Items.Add(current.Place); idx = _place.Items.Count - 1; }
            }
            _place.SelectedIndex = idx;
            if (places.Count == 0) _place.Items[0] = PlaceAny + " – start the game once with plugin 1.4 to list the places";
        }

        private void RefreshRooms()
        {
            _loading = true;
            int a = _canvas.CurrentAddress, v = _canvas.CurrentVariation;
            _rooms.Items.Clear();
            for (int r = 0; r < _doc.RoomCount(a, v); r++)
            {
                int cells = FloorDoc.NodesOf(_doc.Room(a, v, r)).Count;
                var l = _doc.RoomLayout(a, v, r);
                _rooms.Items.Add(_doc.IsNullRoom(a, v, r)
                    ? $"(Null room · {cells} cells · the game needs it; keep it empty in shops and homes)"
                    : $"{(l.Length > 0 ? l : "(no type)")} · {cells} cells · id {_doc.RoomId(a, v, r)}");
            }
            if (_canvas.CurrentRoom >= _rooms.Items.Count) _canvas.CurrentRoom = Math.Max(0, _rooms.Items.Count - 1);
            if (_rooms.Items.Count > 0) _rooms.SelectedIndex = _canvas.CurrentRoom;
            _loading = false;
            LoadRoomLayout();
            _addresses.Invalidate();
        }

        private int RealRoom() => _doc == null ? 0 : Math.Max(0, _doc.FirstRealRoom(_canvas.CurrentAddress, _canvas.CurrentVariation));

        private bool HasRoom => _canvas.CurrentAddress >= 0 && _canvas.CurrentRoom < _doc.RoomCount(_canvas.CurrentAddress, _canvas.CurrentVariation);

        private void LoadRoomLayout()
        {
            _loading = true;
            var types = _canvas.CurrentAddress >= 0 && _ws.Catalog.LayoutRooms.TryGetValue(_doc.AddressPreset(_canvas.CurrentAddress), out var t)
                ? t.Concat(_ws.RoomLayouts.Where(x => !t.Contains(x))) : _ws.RoomLayouts;
            FillCombo(_roomLayout, types.Where(x => x != FloorDoc.NullRoom));
            _roomLayout.Enabled = !_doc.ReadOnly && HasRoom && !_doc.IsNullRoom(_canvas.CurrentAddress, _canvas.CurrentVariation, _canvas.CurrentRoom);
            _roomLayout.Text = HasRoom ? _doc.RoomLayout(_canvas.CurrentAddress, _canvas.CurrentVariation, _canvas.CurrentRoom) : "";
            _loading = false;
        }

        private void DrawAddress(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || _doc == null || e.Index >= _doc.Addresses.Count) return;
            var r = e.Bounds;
            using (var b = new SolidBrush(_doc.AddressColor(e.Index))) e.Graphics.FillRectangle(b, r.X + 3, r.Y + 3, 14, r.Height - 6);
            var preset = _doc.AddressPreset(e.Index);
            int cells = _doc.NodesIn(e.Index, 0).Count();
            TextRenderer.DrawText(e.Graphics, $"{e.Index + 1}. {(preset.Length > 0 ? GameNames.AddressTypeLabel(preset) : "(no type)")}  ·  {cells} cells",
                e.Font, new Point(r.X + 22, r.Y + 2), e.ForeColor);
            e.DrawFocusRectangle();
        }

        // ------------------------------------------------------------------ tools

        private void SetTool(FloorTool tool)
        {
            _canvas.Tool = tool;
            foreach (Control c in _tools.Controls)
                if (c is RadioButton rb && (FloorTool)rb.Tag == tool && !rb.Checked) rb.Checked = true;
            foreach (var c in _typeOpts) c.Visible = tool == FloorTool.TileType;
            foreach (var c in _wallOpts) c.Visible = tool == FloorTool.Wall;
            foreach (var c in _flagOpts) c.Visible = tool == FloorTool.TileFlag;
            foreach (var c in _balconyOpts) c.Visible = tool == FloorTool.Balcony;
            _hint.Text = tool switch
            {
                FloorTool.Inspect => "Click a cell to see its raw data on the right.",
                FloorTool.Room => "Drag to move cells into the selected room. Right-click a cell to select its address and room.",
                FloorTool.Address => "Drag to give cells to the selected address (new cells copy their settings from existing ones). Right-click picks.",
                FloorTool.Erase => "Drag to remove cells from every address.",
                FloorTool.TileType => "Drag to set the floor/ceiling type. Marks: × none, F floor only, C ceiling only, i indoors.",
                FloorTool.Wall => "Click near a cell edge to place the chosen wall/door/window; right-click to remove. Orange = door, blue = window.",
                FloorTool.TileFlag => "Click a tile (thick grid) to set the flag; right-click clears it. Stairs and lifts use the rotation.",
                FloorTool.Balcony => "Drag over cells along an outer wall (e.g. one tile, 3×3). They become an open-air Rooftop terrace with a door into the room next to it. Undo works.",
                _ => "",
            };
            _canvas.Invalidate();
        }

        private void OnCellPicked(int x, int y)
        {
            int a = _doc.AddressAt(x, y);
            var (tx, ty) = _doc.TileOf(x, y);
            var tile = _doc.TileAt(tx, ty);
            string text = $"Cell {x},{y}   tile {tx},{ty}\r\n";
            if (a >= 0)
            {
                int v = a == _canvas.CurrentAddress ? _canvas.CurrentVariation : 0;
                var n = _doc.Find(a, v, x, y) ?? _doc.NodesIn(a, 0).FirstOrDefault(q => q.X == x && q.Y == y);
                if (a != _canvas.CurrentAddress)
                {
                    _canvas.CurrentAddress = a;
                    _canvas.CurrentVariation = 0;
                    RefreshAddresses();
                }
                if (n != null)
                {
                    _canvas.CurrentRoom = n.R;
                    RefreshRooms();
                    text += $"Address {a + 1}: {_doc.AddressPreset(a)}\r\nRoom id {_doc.RoomId(a, n.V, n.R)}\r\n\r\nNode:\r\n{Pretty(n.Node.ToJsonString())}\r\n";
                }
            }
            else text += "(empty)\r\n";
            if (tile != null) text += $"\r\nTile:\r\n{Pretty(tile.ToJsonString())}";
            _inspect.Text = text;
            _canvas.Invalidate();
        }

        private static string Pretty(string json)
        {
            try
            {
                return System.Text.Json.Nodes.JsonNode.Parse(json)
                    .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\r\n");
            }
            catch { return json; }
        }

        private void Edit(Action change)
        {
            _doc.PushUndo();
            change();
            _doc.Dirty = true;
            Changed?.Invoke();
        }

        private void AfterStructureChange()
        {
            _doc.Analyze();
            _canvas.UpdateSize();
            RefreshAddresses();
            _canvas.Invalidate();
            Changed?.Invoke();
        }

        public void DoUndo()
        {
            if (_doc == null || _doc.ReadOnly || !_doc.Undo()) return;
            AfterStructureChange();
        }

        public void DoRedo()
        {
            if (_doc == null || _doc.ReadOnly || !_doc.Redo()) return;
            AfterStructureChange();
        }

        private void AddAddress()
        {
            var label = Dialogs.AskChoice(FindForm(), "New address", "What is it:", _ws.AddressPresets.Select(GameNames.AddressTypeLabel), GameNames.AddressTypeLabel("Retail"));
            if (label == null) return;
            var preset = GameNames.AddressTypeFromLabel(label);
            _doc.PushUndo();
            _canvas.CurrentAddress = _doc.AddAddress(preset, FloorDoc.Palette(_doc.Addresses.Count));
            // name the first room after the first room type this address type uses (e.g. RetailSpace for a shop)
            if (_ws.Catalog.LayoutRooms.TryGetValue(preset, out var roomTypes))
            {
                var first = roomTypes.FirstOrDefault(t => t != FloorDoc.NullRoom);
                int real = _doc.FirstRealRoom(_canvas.CurrentAddress, 0);
                if (first != null && real >= 0) _doc.SetRoomLayout(_canvas.CurrentAddress, 0, real, first);
            }
            _canvas.CurrentVariation = 0;
            _canvas.CurrentRoom = RealRoom();
            SetTool(FloorTool.Address);
            AfterStructureChange();
        }

        private void DeleteAddress()
        {
            int a = _canvas.CurrentAddress;
            if (a < 0) return;
            if (MessageBox.Show(FindForm(), $"Delete address {a + 1} and all its cells?", "Delete address", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _doc.PushUndo();
            _doc.DeleteAddress(a);
            _canvas.CurrentAddress = Math.Min(a, _doc.Addresses.Count - 1);
            AfterStructureChange();
        }

        private void PickColour()
        {
            int a = _canvas.CurrentAddress;
            if (a < 0) return;
            using var dlg = new ColorDialog { Color = _doc.AddressColor(a), FullOpen = true };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            _doc.PushUndo();
            _doc.SetAddressColor(a, dlg.Color);
            _addresses.Invalidate();
            _canvas.Invalidate();
            Changed?.Invoke();
        }

        private void AddVariation()
        {
            int a = _canvas.CurrentAddress;
            if (a < 0) return;
            _doc.PushUndo();
            _canvas.CurrentVariation = _doc.AddVariation(a, _canvas.CurrentVariation);
            _canvas.CurrentRoom = RealRoom();
            AfterStructureChange();
        }

        private void DeleteVariation()
        {
            int a = _canvas.CurrentAddress;
            if (a < 0) return;
            _doc.PushUndo();
            if (!_doc.DeleteVariation(a, _canvas.CurrentVariation)) { MessageBox.Show(FindForm(), "An address needs at least one variation."); return; }
            _canvas.CurrentVariation = 0;
            AfterStructureChange();
        }

        private void AddRoom()
        {
            int a = _canvas.CurrentAddress;
            if (a < 0) { MessageBox.Show(FindForm(), "Add or select an address first."); return; }
            _doc.PushUndo();
            _canvas.CurrentRoom = _doc.AddRoom(a, _canvas.CurrentVariation);
            SetTool(FloorTool.Room);
            AfterStructureChange();
        }

        private void DeleteRoom()
        {
            int a = _canvas.CurrentAddress;
            if (!HasRoom) return;
            _doc.PushUndo();
            if (!_doc.DeleteRoom(a, _canvas.CurrentVariation, _canvas.CurrentRoom)) { MessageBox.Show(FindForm(), "An address needs at least one room."); return; }
            _canvas.CurrentRoom = RealRoom();
            AfterStructureChange();
        }

        /// <summary>Called after the floor was replaced from outside (raw JSON edit).</summary>
        public void Reload()
        {
            if (_doc != null) Bind(_doc, _entry);
        }
    }
}

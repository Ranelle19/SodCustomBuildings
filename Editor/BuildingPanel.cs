using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>Edits one building.json: id, base building, floor layouts and setting overrides.</summary>
    public sealed class BuildingPanel : UserControl
    {
        private readonly Workspace _ws;
        private BuildingEntry _b;
        private bool _loading;

        private readonly TextBox _id = new() { Width = 260 };
        private readonly TextBox _displayName = new() { Width = 260 };
        private readonly ComboBox _base = new()
        {
            Width = 260, DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        private readonly Label _baseInfo = new() { AutoSize = true, ForeColor = Color.DimGray };
        private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        private readonly CheckBox _replace = new() { AutoSize = true, Text = "Replace the base building's layouts" };
        private readonly ListBox _layouts = new() { IntegralHeight = false, Dock = DockStyle.Fill };
        private readonly NumericUpDown _floors = new() { Minimum = 1, Maximum = 200, Width = 70 };
        private readonly ListBox _blueprints = new() { IntegralHeight = false, Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended };
        private readonly Label _sum = new() { AutoSize = true };
        private readonly PropertyGrid _grid = new() { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = false, PropertySort = PropertySort.Categorized };
        private readonly TextBox _names = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };

        /// <summary>Raised whenever the building is edited.</summary>
        public event Action Changed;
        /// <summary>Raised when the user wants to open one of the building's floors.</summary>
        public event Action<string> OpenFloor;
        /// <summary>Raised when the user asks for the 3D preview.</summary>
        public event Action Preview;
        /// <summary>Raised when the user wants to generate an exterior.</summary>
        public event Action MakeExterior;
        public event Action PaintExterior;

        public BuildingPanel(Workspace ws)
        {
            _ws = ws;
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            BuildUi();
        }

        private static Button Btn(string text, EventHandler click, int width = 80)
        {
            var b = new Button { Text = text, Width = width, Height = 28, Margin = new Padding(0, 0, 6, 6) };
            b.Click += click;
            return b;
        }

        private void BuildUi()
        {
            // ---- header
            var head = new TableLayoutPanel { Dock = DockStyle.Top, Height = 138, ColumnCount = 3, RowCount = 4 };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.Controls.Add(new Label { Text = "Id (preset name):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            head.Controls.Add(_id, 1, 0);
            head.Controls.Add(new Label { Text = "Unique name the game uses. Keep a prefix like cb_.", AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left }, 2, 0);
            head.Controls.Add(new Label { Text = "Name in game:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            head.Controls.Add(_displayName, 1, 1);
            head.Controls.Add(new Label { Text = "Shown for this kind of building, e.g. in the city editor. Empty = made from the Id.", AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left }, 2, 1);
            head.Controls.Add(new Label { Text = "Base building:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            head.Controls.Add(_base, 1, 2);
            head.Controls.Add(_baseInfo, 2, 2);
            var headBtns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            headBtns.Controls.Add(Btn("Copy base layouts into this building", (s, e) => CopyBaseLayouts(), 240));
            headBtns.Controls.Add(Btn("3D preview…", (s, e) => Preview?.Invoke(), 110));
            headBtns.Controls.Add(Btn("Make exterior…", (s, e) => MakeExterior?.Invoke(), 120));
            headBtns.Controls.Add(Btn("Paint exterior…", (s, e) => PaintExterior?.Invoke(), 120));
            head.Controls.Add(headBtns, 1, 3);
            head.SetColumnSpan(headBtns, 2);

            // ---- layouts (left)
            var left = new GroupBox { Text = "Floor layouts (bottom floor first)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var lt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
            lt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lt.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            lt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lt.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            lt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lt.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var kindRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            _kind.Items.AddRange(new object[] { "Floors", "Basements" });
            kindRow.Controls.Add(_kind);
            kindRow.Controls.Add(_replace);
            lt.Controls.Add(kindRow, 0, 0);
            lt.Controls.Add(_layouts, 0, 1);

            var layoutBtns = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            layoutBtns.Controls.Add(Btn("Add", (s, e) => AddLayout()));
            layoutBtns.Controls.Add(Btn("Remove", (s, e) => RemoveLayout()));
            layoutBtns.Controls.Add(Btn("Up", (s, e) => MoveLayout(-1), 50));
            layoutBtns.Controls.Add(Btn("Down", (s, e) => MoveLayout(1), 60));
            lt.Controls.Add(layoutBtns, 0, 2);

            var floorsRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            floorsRow.Controls.Add(new Label { Text = "Number of floors with this layout:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            floorsRow.Controls.Add(_floors);
            floorsRow.Controls.Add(new Label { Text = "  Floor plans (the game picks one per floor):", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
            lt.Controls.Add(floorsRow, 0, 3);
            lt.Controls.Add(_blueprints, 0, 4);

            var bpBtns = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            bpBtns.Controls.Add(Btn("Add floor plans…", (s, e) => AddBlueprints(), 120));
            bpBtns.Controls.Add(Btn("Remove", (s, e) => RemoveBlueprints()));
            bpBtns.Controls.Add(Btn("Open", (s, e) => { if (_blueprints.SelectedItem is string n) OpenFloor?.Invoke(n); }));
            lt.Controls.Add(bpBtns, 0, 5);
            lt.Controls.Add(_sum, 0, 6);
            left.Controls.Add(lt);

            // ---- settings (right)
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            var settings = new GroupBox { Text = "Settings (empty = same as the base building)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            settings.Controls.Add(_grid);
            var names = new GroupBox { Text = "Building names, one per line (empty = game default)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            names.Controls.Add(_names);
            right.Controls.Add(settings, 0, 0);
            right.Controls.Add(names, 0, 1);

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            split.Controls.Add(left, 0, 0);
            split.Controls.Add(right, 1, 0);

            Controls.Add(split);
            Controls.Add(head);

            // ---- events
            _id.TextChanged += (s, e) => { if (_loading) return; _b.Def.Id = _id.Text.Trim(); _displayName.PlaceholderText = _b.Def.GameName(); Touch(); };
            _displayName.TextChanged += (s, e) => { if (_loading) return; _b.Def.DisplayName = _displayName.Text.Trim(); Touch(); };
            _base.TextChanged += (s, e) => { if (_loading) return; _b.Def.BasePreset = _base.Text.Trim(); UpdateInfo(); Touch(); };
            _kind.SelectedIndexChanged += (s, e) => { if (!_loading) LoadLayouts(); };
            _replace.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                if (_kind.SelectedIndex == 0) _b.Def.ReplaceFloorLayouts = _replace.Checked; else _b.Def.ReplaceBasementLayouts = _replace.Checked;
                Touch();
            };
            _layouts.SelectedIndexChanged += (s, e) => { if (!_loading) LoadBlueprints(); };
            _floors.ValueChanged += (s, e) =>
            {
                if (_loading || Current == null) return;
                Current.Floors = (int)_floors.Value;
                RefreshLayoutText();
                Touch();
            };
            _blueprints.DoubleClick += (s, e) => { if (_blueprints.SelectedItem is string n) OpenFloor?.Invoke(n); };
            _grid.PropertyValueChanged += (s, e) => Touch();
            _names.TextChanged += (s, e) =>
            {
                if (_loading) return;
                var list = _names.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                _b.Def.Overrides.PossibleNames = list.Count > 0 ? list : null;
                Touch();
            };
        }

        public void Bind(BuildingEntry b)
        {
            _loading = true;
            _b = b;
            _id.Text = b.Def.Id;
            _displayName.Text = b.Def.DisplayName ?? "";
            _displayName.PlaceholderText = new BuildingDef { Id = b.Def.Id }.GameName();
            _base.Items.Clear();
            foreach (var cb in _ws.Catalog.Buildings.Where(c => !c.Custom)) _base.Items.Add(cb.PresetName);
            _base.Text = b.Def.BasePreset;
            _kind.SelectedIndex = 0;
            _grid.SelectedObject = b.Def.Overrides;
            _names.Text = string.Join(Environment.NewLine, b.Def.Overrides.PossibleNames ?? new List<string>());
            _loading = false;
            UpdateInfo();
            LoadLayouts();
        }

        private List<FloorLayoutDef> Layouts => _kind.SelectedIndex == 1 ? _b.Def.BasementLayouts : _b.Def.FloorLayouts;
        private FloorLayoutDef Current => _layouts.SelectedIndex >= 0 && _layouts.SelectedIndex < Layouts.Count ? Layouts[_layouts.SelectedIndex] : null;

        private void Touch()
        {
            _b.Dirty = true;
            UpdateSum();
            Changed?.Invoke();
        }

        private void UpdateInfo()
        {
            var cb = _ws.FindBaseBuilding(_b.Def.BasePreset);
            if (_ws.Catalog.Buildings.Count == 0)
                _baseInfo.Text = "No game catalog yet: run the game once with the plugin installed.";
            else if (cb == null)
                _baseInfo.Text = "Not a known game building.";
            else
                _baseInfo.Text = $"{cb.LayoutStoreys} storeys · {cb.Zone}{(cb.NonEnterable ? " · not enterable" : "")}{(cb.Boundary ? " · boundary" : "")}";
            UpdateSum();
        }

        private void UpdateSum()
        {
            if (_b == null) return;
            var cb = _ws.FindBaseBuilding(_b.Def.BasePreset);
            int sum = _b.Def.FloorLayouts.Sum(l => Math.Max(1, l.Floors));
            if (!_b.Def.ReplaceFloorLayouts || _b.Def.FloorLayouts.Count == 0)
            {
                _sum.Text = "Using the base building's floor layouts.";
                _sum.ForeColor = Color.DimGray;
            }
            else if (ModelLoader.ExteriorExists(_ws, _b))
            {
                _sum.Text = $"Layouts cover {sum} storeys. The building has its own exterior; after changing storeys, make it again.";
                _sum.ForeColor = Color.DimGray;
            }
            else if (cb == null)
            {
                _sum.Text = $"Layouts cover {sum} floor(s).";
                _sum.ForeColor = Color.DimGray;
            }
            else
            {
                bool ok = sum == cb.LayoutStoreys;
                _sum.Text = ok ? $"Layouts cover {sum} storeys, the same as {cb.PresetName}."
                               : $"Layouts cover {sum} storey(s) but {cb.PresetName} uses {cb.LayoutStoreys}. Windows may not line up.";
                _sum.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
            }
        }

        private void LoadLayouts()
        {
            _loading = true;
            _replace.Checked = _kind.SelectedIndex == 1 ? _b.Def.ReplaceBasementLayouts : _b.Def.ReplaceFloorLayouts;
            int keep = _layouts.SelectedIndex;
            _layouts.Items.Clear();
            foreach (var l in Layouts) _layouts.Items.Add(LayoutText(l));
            if (_layouts.Items.Count > 0) _layouts.SelectedIndex = Math.Clamp(keep, 0, _layouts.Items.Count - 1);
            _loading = false;
            LoadBlueprints();
            UpdateSum();
        }

        private static string LayoutText(FloorLayoutDef l) =>
            $"{Math.Max(1, l.Floors)} floor(s): {(l.Blueprints.Count == 0 ? "(no floor plans)" : string.Join(", ", l.Blueprints))}";

        private void RefreshLayoutText()
        {
            int i = _layouts.SelectedIndex;
            if (i < 0) return;
            _loading = true;
            _layouts.Items[i] = LayoutText(Layouts[i]);
            _loading = false;
        }

        private void LoadBlueprints()
        {
            _loading = true;
            _blueprints.Items.Clear();
            var c = Current;
            _floors.Enabled = _blueprints.Enabled = c != null;
            if (c != null)
            {
                _floors.Value = Math.Clamp(c.Floors, 1, 200);
                foreach (var bp in c.Blueprints) _blueprints.Items.Add(bp);
            }
            _loading = false;
        }

        private void CopyBaseLayouts()
        {
            var cb = _ws.FindBaseBuilding(_b.Def.BasePreset);
            if (cb == null) { MessageBox.Show(this, "Pick a base building from the game catalog first."); return; }
            if (_b.Def.FloorLayouts.Count > 0 &&
                MessageBox.Show(this, "Replace this building's floor and basement layouts with the base building's?", "Copy layouts",
                    MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _b.Def.FloorLayouts = cb.FloorLayouts.Select(l => new FloorLayoutDef { Floors = l.Floors, Blueprints = l.Blueprints.ToList() }).ToList();
            _b.Def.BasementLayouts = cb.BasementLayouts.Select(l => new FloorLayoutDef { Floors = l.Floors, Blueprints = l.Blueprints.ToList() }).ToList();
            _b.Def.ReplaceFloorLayouts = true;
            LoadLayouts();
            Touch();
        }

        private void AddLayout()
        {
            Layouts.Add(new FloorLayoutDef { Floors = 1 });
            if (_kind.SelectedIndex == 0) _b.Def.ReplaceFloorLayouts = true; else _b.Def.ReplaceBasementLayouts = true;
            LoadLayouts();
            _layouts.SelectedIndex = _layouts.Items.Count - 1;
            Touch();
        }

        private void RemoveLayout()
        {
            int i = _layouts.SelectedIndex;
            if (i < 0) return;
            Layouts.RemoveAt(i);
            LoadLayouts();
            Touch();
        }

        private void MoveLayout(int delta)
        {
            int i = _layouts.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= Layouts.Count) return;
            (Layouts[i], Layouts[j]) = (Layouts[j], Layouts[i]);
            LoadLayouts();
            _layouts.SelectedIndex = j;
            Touch();
        }

        private void AddBlueprints()
        {
            if (Current == null) { MessageBox.Show(this, "Add or select a layout first."); return; }
            var picked = Dialogs.PickFloors(FindForm(), _ws);
            if (picked.Count == 0) return;
            foreach (var p in picked.Where(p => !Current.Blueprints.Contains(p))) Current.Blueprints.Add(p);
            RefreshLayoutText();
            LoadBlueprints();
            Touch();
        }

        private void RemoveBlueprints()
        {
            if (Current == null) return;
            foreach (var s in _blueprints.SelectedItems.Cast<string>().ToList()) Current.Blueprints.Remove(s);
            RefreshLayoutText();
            LoadBlueprints();
            Touch();
        }

        /// <summary>Adds a floor plan to the selected layout (or the first), used after creating a new floor.</summary>
        public void AddToCurrentLayout(string floorName)
        {
            if (_b == null) return;
            if (_b.Def.FloorLayouts.Count == 0) _b.Def.FloorLayouts.Add(new FloorLayoutDef { Floors = 1 });
            var target = _kind.SelectedIndex == 0 && Current != null ? Current : _b.Def.FloorLayouts[0];
            if (!target.Blueprints.Contains(floorName)) target.Blueprints.Add(floorName);
            _b.Def.ReplaceFloorLayouts = true;
            LoadLayouts();
            Touch();
        }
    }
}

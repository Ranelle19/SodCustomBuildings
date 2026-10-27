using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>
    /// "New shopping building": copies a game building, turns the homes (and optionally offices) on the chosen storeys into
    /// shops and restaurants, and sets up the floor layouts so everything lines up with the model. The game decides which
    /// kind of shop each one becomes when it generates the city.
    /// </summary>
    public sealed class ShoppingWizard : Form
    {
        private readonly Workspace _ws;
        private readonly TextBox _id = new() { Width = 300 };
        private readonly TextBox _name = new() { Width = 300, PlaceholderText = "optional, e.g. Margin Mall" };
        private readonly ComboBox _base = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckedListBox _storeys = new() { Width = 560, Height = 200, CheckOnClick = true, IntegralHeight = false };
        private readonly CheckBox _offices = new() { Text = "Also turn offices into shops", AutoSize = true };
        private readonly TrackBar _restaurants = new() { Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 25, TickFrequency = 25, Width = 260, Value = 25 };
        private readonly Label _restLabel = new() { AutoSize = true };
        private readonly Label _summary = new() { AutoSize = false, Width = 560, Height = 40, ForeColor = Color.DarkGreen };
        private readonly List<Run> _runs = new();
        private bool _loading;

        /// <summary>A run of storeys from one of the base building's layouts.</summary>
        private sealed class Run
        {
            public bool Basement;
            public int Index;            // position in FloorLayouts / BasementLayouts
            public int FirstLevel, Floors;
            public FloorLayoutDef Layout;
            public int MinConvertible, MaxConvertible;
            public override string ToString()
            {
                string where = Basement
                    ? (Floors == 1 ? $"Basement {-FirstLevel}" : $"Basements {-FirstLevel}–{-FirstLevel + Floors - 1}")
                    : Floors == 1 ? LevelName(FirstLevel) : $"{LevelName(FirstLevel)} to {LevelName(FirstLevel + Floors - 1)}";
                string what = MaxConvertible == 0 ? "nothing to turn into shops"
                    : MinConvertible == MaxConvertible ? $"{MaxConvertible} → shops"
                    : $"{MinConvertible}–{MaxConvertible} → shops";
                return $"{where}:  {what}   ({string.Join(", ", Layout.Blueprints)})";
            }
        }

        private static string LevelName(int level) => level == 0 ? "Ground floor" : $"Storey {level}";

        public BuildingEntry Result { get; private set; }

        public ShoppingWizard(Workspace ws)
        {
            AppIcon.Apply(this);
            _ws = ws;
            Text = "New shopping building";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(600, 560);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, AutoSize = false };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Row(string label, Control c)
            {
                t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 0, 0) });
                t.Controls.Add(c);
            }
            void Full(Control c) { t.Controls.Add(c); t.SetColumnSpan(c, 2); }

            Full(new Label
            {
                Text = "Turns the homes in a game building into shops and restaurants. The building keeps its look, stairs, " +
                       "doors and windows. The game picks the kind of each shop (supermarket, chemist, pawn shop…) when it makes a new city.",
                AutoSize = false, Width = 560, Height = 48,
            });
            Row("Id:", _id);
            Row("Building name:", _name);
            Row("Looks like:", _base);
            Full(new Label { Text = "Storeys to fill with shops:", AutoSize = true, Margin = new Padding(0, 10, 0, 2) });
            Full(_storeys);
            Full(_offices);
            var restRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            restRow.Controls.Add(_restaurants);
            restRow.Controls.Add(_restLabel);
            Row("Restaurants:", restRow);
            Full(_summary);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            var ok = new Button { Text = "Create building", Width = 130, Height = 28 };
            var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            Controls.Add(t);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            // defaults
            int n = 1;
            while (_ws.Buildings.Any(b => b.Def.Id.Equals($"cb_Shopping{n}", StringComparison.OrdinalIgnoreCase))) n++;
            _id.Text = $"cb_Shopping{n}";
            foreach (var b in Candidates()) _base.Items.Add(new BaseItem(b));
            int pick = _base.Items.Cast<BaseItem>().ToList().FindIndex(i => i.B.PresetName == "TownhouseShops");
            if (_base.Items.Count > 0) _base.SelectedIndex = Math.Max(0, pick);

            _base.SelectedIndexChanged += (s, e) => LoadRuns();
            _offices.CheckedChanged += (s, e) => LoadRuns();
            _storeys.ItemCheck += (s, e) => { if (!_loading) BeginInvoke(new Action(UpdateSummary)); };
            _restaurants.ValueChanged += (s, e) => UpdateSummary();
            ok.Click += (s, e) => Create();
            LoadRuns();
        }

        private sealed class BaseItem
        {
            public readonly CatalogBuilding B;
            public BaseItem(CatalogBuilding b) { B = b; }
            public override string ToString() => $"{B.PresetName}  ({B.LayoutStoreys} storeys, {B.Zone})";
        }

        private IEnumerable<CatalogBuilding> Candidates() =>
            _ws.Catalog.Buildings.Where(b => !b.Custom && !b.Boundary && !b.NonEnterable && b.FloorLayouts.Count > 0)
                .OrderBy(b => b.LayoutStoreys);

        private CatalogBuilding Base => (_base.SelectedItem as BaseItem)?.B;
        private ShopConverter.Options Opts => new() { Homes = true, Offices = _offices.Checked, RestaurantShare = _restaurants.Value / 100.0 };

        private FloorDoc Game(string name)
        {
            try { return _ws.LoadGameFloor(name); }
            catch { return null; }
        }

        private void LoadRuns()
        {
            _loading = true;
            _runs.Clear();
            _storeys.Items.Clear();
            var b = Base;
            if (b != null)
            {
                var o = Opts;
                void Add(List<FloorLayoutDef> layouts, bool basement)
                {
                    int level = basement ? -1 : 0;
                    for (int i = 0; i < layouts.Count; i++)
                    {
                        var l = layouts[i];
                        int floors = Math.Max(1, l.Floors);
                        var counts = l.Blueprints.Select(bp => Game(bp)).Where(d => d != null).Select(d => ShopConverter.CountConvertible(d, o)).ToList();
                        var run = new Run
                        {
                            Basement = basement, Index = i, Layout = l, Floors = floors,
                            FirstLevel = basement ? level : level,
                            MinConvertible = counts.Count == 0 ? 0 : counts.Min(),
                            MaxConvertible = counts.Count == 0 ? 0 : counts.Max(),
                        };
                        level += basement ? -floors : floors;
                        _runs.Add(run);
                        _storeys.Items.Add(run, run.MaxConvertible > 0 && !basement);
                    }
                }
                Add(b.FloorLayouts, false);
                Add(b.BasementLayouts, true);
            }
            _loading = false;
            UpdateSummary();
        }

        private List<Run> Chosen()
        {
            var list = new List<Run>();
            for (int i = 0; i < _storeys.Items.Count; i++)
                if (_storeys.GetItemChecked(i)) list.Add((Run)_storeys.Items[i]);
            return list;
        }

        private void UpdateSummary()
        {
            _restLabel.Text = _restaurants.Value == 0 ? "none" : $"about {_restaurants.Value}% of them";
            var tally = new ShopConverter.Tally();
            var o = Opts;
            foreach (var r in Chosen())
                for (int k = 0; k < r.Floors; k++)
                {
                    // the game picks one floor plan per storey; estimate with them in turn
                    var doc = Game(r.Layout.Blueprints.Count == 0 ? null : r.Layout.Blueprints[k % r.Layout.Blueprints.Count]);
                    if (doc != null) ShopConverter.Convert(doc.Clone(), o, tally);
                }
            _summary.Text = tally.Total == 0
                ? "Nothing selected would change. Tick some storeys, or pick another building."
                : $"About {tally.Shops} shop(s) and {tally.Restaurants} restaurant(s) on {Chosen().Sum(r => r.Floors)} storey(s).";
        }

        // ------------------------------------------------------------------ create

        private void Create()
        {
            var id = _id.Text.Trim();
            string err = null;
            if (id.Length == 0) err = "Enter an Id.";
            else if (_ws.Buildings.Any(b => b.Def.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) err = "That Id is already used.";
            else if (_ws.Catalog.Buildings.Any(b => b.PresetName.Equals(id, StringComparison.OrdinalIgnoreCase))) err = "That's the name of a game building.";
            else if (Base == null) err = "Pick what the building looks like.";
            else if (Chosen().Count == 0) err = "Tick at least one storey.";
            if (err != null) { MessageBox.Show(this, err, Text); return; }

            var b = Base;
            var o = Opts;
            var tally = new ShopConverter.Tally();
            var entry = new BuildingEntry
            {
                Dirty = true,
                Def = new BuildingDef
                {
                    Id = id,
                    BasePreset = b.PresetName,
                    DisplayName = "Shopping centre",
                    ReplaceFloorLayouts = true,
                    ReplaceBasementLayouts = b.BasementLayouts.Count > 0,
                    Overrides = new BuildingOverrides { Zone = ZoneType.commercial },
                },
            };
            var names = _name.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (names.Count > 0) entry.Def.Overrides.PossibleNames = names;

            var converted = new Dictionary<string, string>();   // game floor -> our copy
            var chosen = Chosen();
            List<FloorLayoutDef> Copy(List<FloorLayoutDef> layouts, bool basement)
            {
                var result = new List<FloorLayoutDef>();
                for (int i = 0; i < layouts.Count; i++)
                {
                    var src = layouts[i];
                    var dst = new FloorLayoutDef { Floors = src.Floors, Blueprints = src.Blueprints.ToList() };
                    if (chosen.Any(r => r.Basement == basement && r.Index == i))
                        for (int k = 0; k < dst.Blueprints.Count; k++)
                        {
                            var bp = dst.Blueprints[k];
                            if (!converted.TryGetValue(bp, out var mine))
                            {
                                mine = null;
                                var game = Game(bp);
                                if (game != null)
                                {
                                    var doc = game.Clone();
                                    doc.ReadOnly = false;
                                    if (ShopConverter.Convert(doc, o, tally) > 0)
                                    {
                                        mine = UniqueFloorName($"{id}_{bp}");
                                        doc.Name = mine;
                                        doc.Dirty = true;
                                        entry.Floors.Add(new FloorEntry { Owner = entry, Doc = doc });
                                    }
                                }
                                converted[bp] = mine;
                            }
                            if (mine != null) dst.Blueprints[k] = mine;
                        }
                    result.Add(dst);
                }
                return result;
            }
            entry.Def.FloorLayouts = Copy(b.FloorLayouts, false);
            entry.Def.BasementLayouts = Copy(b.BasementLayouts, true);

            if (entry.Floors.Count == 0)
            {
                MessageBox.Show(this, "None of the ticked storeys had homes or offices to turn into shops.", Text);
                return;
            }
            Result = entry;
            DialogResult = DialogResult.OK;
            Close();
        }

        private string UniqueFloorName(string name)
        {
            string n = name;
            for (int i = 2; _ws.FloorNameTaken(n) || _pending.Contains(n); i++) n = $"{name}_{i}";
            _pending.Add(n);
            return n;
        }

        private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>"Make exterior…": generates a neon-arcade outside for a building from its floor plans.</summary>
    public sealed class ExteriorDialog : Form
    {
        private readonly Workspace _ws;
        private readonly BuildingEntry _b;
        private readonly TextBox _sign = new() { Width = 300 };
        private readonly NumericUpDown _storeys = new() { Minimum = 1, Maximum = 40, Width = 70 };
        private readonly CheckBox _windows = new() { Text = "Add windows to my floor plans where the outer walls have none", AutoSize = true, Checked = true };
        private readonly ComboBox _style = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _terraceWalls = new() { Text = "Low brick walls around balconies, terraces and setbacks", AutoSize = true, Checked = true };
        private readonly CheckBox _keep = new() { Text = "Keep my painting on walls and signs that stay the same size", AutoSize = true, Checked = true, Visible = false };
        private readonly ExteriorInfo _old;
        private readonly Label _info = new() { AutoSize = false, Width = 520, Height = 70, ForeColor = Color.DimGray };

        public bool ExteriorMade { get; private set; }

        public ExteriorDialog(Workspace ws, BuildingEntry b)
        {
            AppIcon.Apply(this);
            _ws = ws;
            _b = b;
            Text = $"Make exterior: {b.Def.Id}";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 395);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Row(string label, Control c)
            {
                t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 0, 0) });
                t.Controls.Add(c);
            }
            void Full(Control c) { t.Controls.Add(c); t.SetColumnSpan(c, 2); }

            Full(new Label
            {
                Text = "Builds an outside for this building from its floor plans, in a neon-arcade style: brick walls with neon " +
                       "trims, windows on the upper storeys and a lit sign on the roof. The ground floor's outside is built by the game itself " +
                       "(from the floor plan's windows and doors, in the base building's wall material).",
                AutoSize = false, Width = 520, Height = 50,
            });
            _style.Items.AddRange(new object[] { "Neon arcade", "Plain brick (a base to paint yourself)" });
            _style.SelectedIndex = 0;
            Row("Style:", _style);
            Row("Sign text:", _sign);
            var storeyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            storeyRow.Controls.Add(_storeys);
            storeyRow.Controls.Add(new Label { Text = "  (more than now repeats the top floor plan)", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
            Row("Storeys:", storeyRow);
            Full(_windows);
            Full(_terraceWalls);
            Full(_keep);
            Full(_info);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            var ok = new Button { Text = "Make exterior", Width = 130, Height = 28 };
            var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            Controls.Add(t);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            _sign.Text = b.Def.GameName().ToUpperInvariant();
            int now = Layouts().Sum(l => Math.Max(1, l.Floors));
            _storeys.Value = Math.Clamp(now, 1, 40);
            if (GameNames.Walls.Count == 0)
            {
                _windows.Checked = false;
                _windows.Enabled = false;
                _windows.Text += " (needs the wall names: start the game once with the plugin)";
            }
            _old = ExteriorPainter.ReadInfo(ModelLoader.ExteriorFolder(_ws, _b));
            if (_old != null)
            {
                _sign.Text = string.IsNullOrWhiteSpace(_old.SignText) ? _sign.Text : _old.SignText;
                if (_old.Style == "Plain") _style.SelectedIndex = 1;
                _keep.Visible = _old.Painted;
            }
            _info.Text = ModelLoader.ExteriorExists(_ws, _b)
                ? "This building already has an exterior; it will be replaced." +
                  (_old?.Painted == true ? " Untick the box above to also throw away your painting." : "")
                : "The building keeps working without it; the game then uses the base building's look. " +
                  "Afterwards you can paint it with Building > Paint exterior.";
            ok.Click += (s, e) => Make();
        }

        private List<FloorLayoutDef> Layouts()
        {
            var def = _b.Def;
            if (def.ReplaceFloorLayouts && def.FloorLayouts.Count > 0) return def.FloorLayouts;
            return _ws.FindBaseBuilding(def.BasePreset)?.FloorLayouts ?? new List<FloorLayoutDef>();
        }

        private FloorEntry Own(string name) => _ws.Buildings.SelectMany(x => x.Floors).FirstOrDefault(f => f.Name == name);

        private FloorDoc Resolve(string name)
        {
            var own = Own(name);
            if (own != null) return own.Doc;
            try { return _ws.LoadGameFloor(name); } catch { return null; }
        }

        private void Make()
        {
            var def = _b.Def;
            int want = (int)_storeys.Value;

            // storeys: make the layouts add up by repeating the top layout (or trimming from the top)
            if (!def.ReplaceFloorLayouts || def.FloorLayouts.Count == 0)
            {
                var cb = _ws.FindBaseBuilding(def.BasePreset);
                def.FloorLayouts = (cb?.FloorLayouts ?? new List<FloorLayoutDef>())
                    .Select(l => new FloorLayoutDef { Floors = l.Floors, Blueprints = l.Blueprints.ToList() }).ToList();
                def.ReplaceFloorLayouts = true;
            }
            if (def.FloorLayouts.Count == 0) { MessageBox.Show(this, "The building has no floor layouts yet.", Text); return; }
            int have = def.FloorLayouts.Sum(l => Math.Max(1, l.Floors));
            if (want != have)
            {
                var last = def.FloorLayouts[^1];
                int delta = want - have;
                if (Math.Max(1, last.Floors) + delta >= 1) last.Floors = Math.Max(1, last.Floors) + delta;
                else
                {
                    MessageBox.Show(this, $"Can't go below {have - Math.Max(1, last.Floors) + 1} storeys by trimming the top layout. Change the layouts in the building first.", Text);
                    return;
                }
                _b.Dirty = true;
            }

            // one floor plan per storey (the first plan of each layout; the game may pick others)
            var docs = new List<(FloorDoc doc, string name)>();
            foreach (var l in def.FloorLayouts)
                for (int k = 0; k < Math.Max(1, l.Floors); k++)
                {
                    var name = l.Blueprints.Count == 0 ? null : l.Blueprints[k % l.Blueprints.Count];
                    docs.Add((name == null ? null : Resolve(name), name));
                }
            var missing = docs.Where(d => d.doc == null).Select(d => d.name ?? "(empty layout)").Distinct().ToList();
            if (missing.Count > 0) { Dialogs.ShowList(this, "Missing floor plans", "These floor plans weren't found:", missing); return; }

            // windows on the user's own plans
            int added = 0;
            var notOwn = new List<string>();
            if (_windows.Checked)
            {
                var done = new HashSet<(string, bool)>();
                for (int i = 0; i < docs.Count; i++)
                {
                    bool ground = i == 0;
                    if (!done.Add((docs[i].name, ground))) continue;
                    var own = Own(docs[i].name);
                    if (own == null) { notOwn.Add(docs[i].name); continue; }
                    own.Doc.PushUndo();
                    int n = ExteriorGenerator.AddWindows(own.Doc, ground);
                    if (n > 0) { added += n; own.Doc.Dirty = true; }
                }
            }

            var storeys = docs.Select(d => ExteriorGenerator.PlanFrom(d.doc)).ToList();
            var empty = storeys.Select((s, i) => (s, i)).Where(x => x.s.Cells.Count == 0).Select(x => $"storey {x.i}").ToList();
            if (empty.Count > 0) { Dialogs.ShowList(this, "Empty storeys", "These storeys have no cells inside the building:", empty); return; }

            ExteriorGenerator.Result result;
            try
            {
                Cursor = Cursors.WaitCursor;
                result = ExteriorGenerator.Generate(storeys, _sign.Text.Trim().Length > 0 ? _sign.Text.Trim() : def.GameName().ToUpperInvariant(),
                                                    neon: _style.SelectedIndex == 0, terraceWalls: _terraceWalls.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't make the exterior:\n" + ex.Message, Text);
                return;
            }
            finally { Cursor = Cursors.Default; }

            var folder = ModelLoader.ExteriorFolder(_ws, _b);
            int kept = 0;
            if (_keep.Visible && _keep.Checked)
            {
                try { kept = ExteriorGenerator.CarryOver(result, folder, _old); } catch { kept = 0; }
            }
            var info = new ExteriorInfo
            {
                Style = _style.SelectedIndex == 0 ? "NeonArcade" : "Plain",
                Painted = kept > 0,
                Shapes = _old?.Shapes?.Select(x => x.Clone()).ToList() ?? new List<ExteriorShape>(),   // kept on walls that still exist
                Storeys = storeys.Count,
                WallTopY = result.WallTopY,
                SignText = _sign.Text.Trim(),
                Windows = result.Windows,
            };
            try { ExteriorGenerator.Write(result, folder, info); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the exterior:\n" + ex.Message, Text);
                return;
            }

            ExteriorMade = true;
            var msg = $"Made an exterior with {storeys.Count} storeys, {result.Windows} windows and {result.Triangles:N0} triangles.\n\n" +
                      $"Saved in {folder}.";
            if (info.Shapes.Count > 0) msg += $"\n\nKept {info.Shapes.Count} shape(s) (ledges, balconies…).";
            if ((_old?.Shapes?.Count ?? 0) > info.Shapes.Count) msg += $" {_old.Shapes.Count - info.Shapes.Count} were on walls that no longer exist and were removed.";
            if (kept > 0) msg += $"\n\nKept your painting on {kept} wall/sign piece(s).";
            msg += "\n\nTo paint it: Building > Paint exterior (Ctrl+P).";
            if (added > 0) msg += $"\n\nAdded windows on {added} outer wall edge(s) of your floor plans.";
            if (notOwn.Count > 0) msg += $"\n\nThese are game floors, so no windows were added to them: {string.Join(", ", notOwn.Distinct())}.";
            msg += "\n\nSave the building (File > Save) so the floor plans and storeys match the exterior.";
            MessageBox.Show(this, msg, Text);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

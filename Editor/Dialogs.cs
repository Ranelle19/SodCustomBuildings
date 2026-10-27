using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BuildingEditor
{
    /// <summary>Small code-built dialogs.</summary>
    internal static class Dialogs
    {
        private static Form MakeForm(string title, int width, int height)
        {
            return new Form
            {
                Text = title,
                Width = width,
                Height = height,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                Font = new Font("Segoe UI", 9f),
            };
        }

        private static (Button ok, Button cancel) AddButtons(Form f, int y)
        {
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = f.ClientSize.Width - 180, Top = y, Width = 80 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = f.ClientSize.Width - 92, Top = y, Width = 80 };
            f.Controls.Add(ok);
            f.Controls.Add(cancel);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            return (ok, cancel);
        }

        private static Label AddLabel(Form f, string text, int y) =>
            AddControl(f, new Label { Text = text, Left = 12, Top = y + 3, Width = 130, AutoSize = false });

        private static T AddControl<T>(Form f, T c) where T : Control
        {
            f.Controls.Add(c);
            return c;
        }

        /// <summary>Asks for one line of text. Returns null when cancelled.</summary>
        public static string AskText(IWin32Window owner, string title, string label, string initial, Func<string, string> validate = null)
        {
            using var f = MakeForm(title, 460, 150);
            AddLabel(f, label, 16);
            var tb = AddControl(f, new TextBox { Left = 146, Top = 16, Width = 285, Text = initial ?? "" });
            AddButtons(f, 60);
            f.FormClosing += (s, e) =>
            {
                if (f.DialogResult != DialogResult.OK || validate == null) return;
                var err = validate(tb.Text.Trim());
                if (err != null) { MessageBox.Show(f, err, title); e.Cancel = true; }
            };
            return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
        }

        /// <summary>Asks for text, picked from a list or typed. Returns null when cancelled.</summary>
        public static string AskChoice(IWin32Window owner, string title, string label, IEnumerable<string> choices, string initial)
        {
            using var f = MakeForm(title, 460, 150);
            AddLabel(f, label, 16);
            var cb = AddControl(f, new ComboBox
            {
                Left = 146, Top = 16, Width = 285, DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
            });
            cb.Items.AddRange(choices.Cast<object>().ToArray());
            cb.Text = initial ?? "";
            AddButtons(f, 60);
            return f.ShowDialog(owner) == DialogResult.OK ? cb.Text.Trim() : null;
        }

        public sealed class NewBuildingResult { public string Id; public string BasePreset; public bool CopyLayouts; }

        public static NewBuildingResult NewBuilding(IWin32Window owner, Workspace ws)
        {
            using var f = MakeForm("New building", 480, 210);
            AddLabel(f, "Id (unique):", 16);
            var id = AddControl(f, new TextBox { Left = 146, Top = 16, Width = 300, Text = "cb_MyBuilding" });
            AddLabel(f, "Base building:", 52);
            var bp = AddControl(f, new ComboBox
            {
                Left = 146, Top = 52, Width = 300, DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
            });
            foreach (var b in ws.Catalog.Buildings.Where(b => !b.Custom && !b.Boundary))
                bp.Items.Add(b.PresetName);
            if (bp.Items.Count > 0) bp.SelectedIndex = 0;
            var copy = AddControl(f, new CheckBox { Left = 146, Top = 86, Width = 300, Text = "Start with the base building's floor layouts", Checked = true });
            AddButtons(f, 124);
            f.FormClosing += (s, e) =>
            {
                if (f.DialogResult != DialogResult.OK) return;
                string err = null;
                var v = id.Text.Trim();
                if (v.Length == 0) err = "Enter an Id.";
                else if (ws.Buildings.Any(b => string.Equals(b.Def.Id, v, StringComparison.OrdinalIgnoreCase))) err = "That Id is already used.";
                else if (ws.Catalog.Buildings.Any(b => b.PresetName == v)) err = "That's the name of a game building.";
                else if (bp.Text.Trim().Length == 0) err = "Pick a base building.";
                if (err != null) { MessageBox.Show(f, err, f.Text); e.Cancel = true; }
            };
            if (f.ShowDialog(owner) != DialogResult.OK) return null;
            return new NewBuildingResult { Id = id.Text.Trim(), BasePreset = bp.Text.Trim(), CopyLayouts = copy.Checked };
        }

        public sealed class NewFloorResult { public string Name; public string Template; public bool ClearRooms; }

        public static NewFloorResult NewFloor(IWin32Window owner, Workspace ws, BuildingEntry building, string suggestedTemplate)
        {
            using var f = MakeForm($"New floor for {building.Def.Id}", 520, 240);
            AddLabel(f, "Floor name:", 16);
            var name = AddControl(f, new TextBox { Left = 146, Top = 16, Width = 340, Text = building.Def.Id + "_floor" + (building.Floors.Count + 1) });
            AddLabel(f, "Start from:", 52);
            var tpl = AddControl(f, new ComboBox
            {
                Left = 146, Top = 52, Width = 340, DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
            });
            foreach (var n in ws.OwnFloorNames()) tpl.Items.Add(n);
            foreach (var n in ws.GameFloorFiles.Keys) tpl.Items.Add(n);
            tpl.Text = suggestedTemplate ?? (tpl.Items.Count > 0 ? tpl.Items[0].ToString() : "");
            var clear = AddControl(f, new CheckBox
            {
                Left = 146, Top = 86, Width = 340, Height = 40,
                Text = "Remove all addresses and rooms (keep only the floor's shape, tiles and heights)",
            });
            AddButtons(f, 152);
            f.FormClosing += (s, e) =>
            {
                if (f.DialogResult != DialogResult.OK) return;
                string err = null;
                var v = name.Text.Trim();
                if (v.Length == 0) err = "Enter a name.";
                else if (ws.FloorNameTaken(v)) err = "That floor name is already used.";
                else if (tpl.Text.Trim().Length == 0) err = "Pick a floor to start from (a game floor is a good template).";
                if (err != null) { MessageBox.Show(f, err, f.Text); e.Cancel = true; }
            };
            if (f.ShowDialog(owner) != DialogResult.OK) return null;
            return new NewFloorResult { Name = name.Text.Trim(), Template = tpl.Text.Trim(), ClearRooms = clear.Checked };
        }

        /// <summary>Searchable multi-select list of floor names. Own floors are listed first.</summary>
        public static List<string> PickFloors(IWin32Window owner, Workspace ws)
        {
            using var f = MakeForm("Add floors to layout", 460, 520);
            f.FormBorderStyle = FormBorderStyle.Sizable;
            var search = AddControl(f, new TextBox { Left = 12, Top = 12, Width = 420, PlaceholderText = "Search…", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right });
            var list = AddControl(f, new ListBox
            {
                Left = 12, Top = 42, Width = 420, Height = 380, SelectionMode = SelectionMode.MultiExtended,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, IntegralHeight = false,
            });
            var all = ws.OwnFloorNames().Select(n => "★ " + n)
                .Concat(ws.GameFloorFiles.Keys.Union(ws.Catalog.Floors).Where(n => !ws.OwnFloorNames().Contains(n)).OrderBy(n => n))
                .ToList();
            void Fill()
            {
                list.BeginUpdate();
                list.Items.Clear();
                var q = search.Text.Trim();
                foreach (var n in all.Where(n => q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    list.Items.Add(n);
                list.EndUpdate();
            }
            search.TextChanged += (s, e) => Fill();
            Fill();
            var (ok, cancel) = AddButtons(f, 432);
            ok.Anchor = cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            list.DoubleClick += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
            if (f.ShowDialog(owner) != DialogResult.OK) return new List<string>();
            return list.SelectedItems.Cast<string>().Select(n => n.StartsWith("★ ") ? n.Substring(2) : n).ToList();
        }

        /// <summary>Raw JSON editor. Returns the new text, or null when cancelled.</summary>
        public static string EditJson(IWin32Window owner, string title, string json)
        {
            using var f = MakeForm(title, 800, 640);
            f.FormBorderStyle = FormBorderStyle.Sizable;
            var tb = AddControl(f, new TextBox
            {
                Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Left = 12, Top = 12,
                Width = f.ClientSize.Width - 24, Height = f.ClientSize.Height - 64, Font = new Font("Consolas", 9.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = Pretty(json), MaxLength = 0,
            });
            var (ok, cancel) = AddButtons(f, f.ClientSize.Height - 42);
            ok.Anchor = cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            f.FormClosing += (s, e) =>
            {
                if (f.DialogResult != DialogResult.OK) return;
                try { System.Text.Json.Nodes.JsonNode.Parse(tb.Text); }
                catch (Exception ex) { MessageBox.Show(f, "That isn't valid JSON:\n" + ex.Message, title); e.Cancel = true; }
            };
            return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
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

        public static void ShowList(IWin32Window owner, string title, string intro, IEnumerable<string> lines)
        {
            using var f = MakeForm(title, 640, 420);
            f.FormBorderStyle = FormBorderStyle.Sizable;
            AddControl(f, new Label { Text = intro, Left = 12, Top = 10, Width = 600, Height = 36 });
            AddControl(f, new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Left = 12, Top = 48,
                Width = f.ClientSize.Width - 24, Height = f.ClientSize.Height - 100,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = string.Join("\r\n", lines.Select(l => "• " + l)),
            });
            var ok = AddControl(f, new Button
            {
                Text = "OK", DialogResult = DialogResult.OK, Width = 80,
                Left = f.ClientSize.Width - 92, Top = f.ClientSize.Height - 42, Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            });
            f.AcceptButton = ok;
            f.ShowDialog(owner);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CustomBuildings.Shared;
using NV = System.Numerics;

namespace BuildingEditor
{
    /// <summary>
    /// The right-hand panel for one of our wall pieces (tree: My edge pieces). Its settings, and its model: export a
    /// template for Blender, import a model (checked against the game's wall piece), or go back to the game's wall.
    /// Changes are saved with File > Save, like buildings.
    /// </summary>
    public sealed class EdgePanel : UserControl
    {
        private readonly Workspace _ws;
        private readonly PropertyGrid _grid = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true };
        private readonly Label _title = new() { Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(4, 6, 0, 0) };
        private readonly Label _model = new() { Dock = DockStyle.Top, Height = 64, Padding = new Padding(6, 4, 6, 0), ForeColor = Color.DimGray };
        private EdgeEntry _entry;
        private readonly Preview3D _view = new() { Dock = DockStyle.Fill, Environment = false };
        private readonly TextBox _facts = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                                                  BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, Font = new Font("Consolas", 9f) };
        private readonly CheckBox _neighbours = new() { Text = "Wall pieces next to it", Checked = true, AutoSize = true };
        private readonly CheckBox _collision = new() { Text = "Collision", AutoSize = true };
        private readonly CheckBox _seeThrough = new() { Text = "See-through", AutoSize = true };
        private readonly Label _showing = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 4, 0, 0) };

        public event Action Changed;
        /// <summary>"Make a wall piece from this" on one of the game's wall pieces.</summary>
        public event Action<EdgeEntry> MakeFromGame;
        private bool ReadOnly => _entry?.GamePreset != null;
        private Button _import, _game, _make;
        private Label _info;

        public EdgePanel(Workspace ws)
        {
            _ws = ws;
            Dock = DockStyle.Fill;
            var info = _info = new Label
            {
                Dock = DockStyle.Top, Height = 62, Padding = new Padding(6, 0, 6, 0), ForeColor = Color.DimGray,
                Text = "A piece of the city's sea wall that new cities get (Minimum / HardLimit per city; the plugin picks the tiles). It's a copy of the " +
                       "game's wall piece for its Role. Give it its own model to change how it looks: the model is the whole piece, with the pier as " +
                       "part of it. Saved with File > Save.",
            };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4) };
            var export = new Button { Text = "Export for Blender…", Width = 150, Height = 28 };
            var import = new Button { Text = "Import model…", Width = 130, Height = 28 };
            var game = new Button { Text = "Use a game wall…", Width = 150, Height = 28 };
            var make = new Button { Text = "Make a wall piece from this", Width = 200, Height = 28, Visible = false };
            buttons.Controls.AddRange(new Control[] { export, import, game, make });
            _import = import; _game = game; _make = make;
            make.Click += (s, e) => { if (ReadOnly) MakeFromGame?.Invoke(_entry); };

            // settings and facts on the left, the 3D view on the right
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            var leftSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            // splitter positions only once the panel has its size (setting them earlier throws)
            Load += (s, e) => { try { split.SplitterDistance = 360; leftSplit.SplitterDistance = 240; } catch { } };
            leftSplit.Panel1.Controls.Add(_grid);
            var factsBox = new GroupBox { Text = "Measured on the model shown", Dock = DockStyle.Fill, Padding = new Padding(6) };
            factsBox.Controls.Add(_facts);
            leftSplit.Panel2.Controls.Add(factsBox);
            split.Panel1.Controls.Add(leftSplit);
            var viewBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(4, 4, 0, 0) };
            var reset = new Button { Text = "Reset view", Width = 90, Height = 24 };
            viewBar.Controls.AddRange(new Control[] { reset, _neighbours, _collision, _seeThrough, _showing });
            split.Panel2.Controls.Add(_view);
            split.Panel2.Controls.Add(viewBar);
            reset.Click += (s, e) => _view.ResetView();
            foreach (var cb in new[] { _neighbours, _collision }) cb.CheckedChanged += (s, e) => ShowPreview(keepCamera: true);
            _seeThrough.CheckedChanged += (s, e) => { _view.Look = _seeThrough.Checked ? ModelLook.SeeThrough : ModelLook.Solid; _view.RefreshScene(); };

            Controls.Add(split);
            Controls.Add(_model);
            Controls.Add(buttons);
            Controls.Add(info);
            Controls.Add(_title);

            _grid.PropertyValueChanged += (s, e) => Touch();
            export.Click += (s, e) => Export();
            import.Click += (s, e) => Import();
            game.Click += (s, e) =>
            {
                if (_entry == null || ReadOnly) return;
                // which of the game's straight pieces to be a copy of (and look like) from now on
                var options = new List<string>
                {
                    $"{GameEdgePieces.Straight}: the straight sea wall",
                    $"{GameEdgePieces.Platform}: the wall with the square platform",
                };
                int pick = Choose("Use the game's wall", $"Which of the game's wall pieces should {_entry.Def.Id} be?" +
                                  (_entry.Def.Model != null || _entry.PendingObj != null ? " (Its own model is removed when you save.)" : ""), options);
                if (pick < 0) return;
                _entry.Def.Role = pick == 1 ? EdgeRole.Platform : EdgeRole.Straight;
                if (_entry.Def.Model != null || _entry.PendingObj != null) _entry.RemoveModel = true;
                _entry.Def.Model = null;
                _entry.PendingObj = null;
                Touch();
            };
        }

        public void Bind(EdgeEntry e)
        {
            _entry = e;
            bool ro = e?.GamePreset != null;
            if (ro) System.ComponentModel.TypeDescriptor.AddAttributes(e.Def, new System.ComponentModel.ReadOnlyAttribute(true));
            _grid.SelectedObject = e?.Def;
            _import.Visible = _game.Visible = !ro;
            _make.Visible = ro;
            _info.Text = ro
                ? "One of the game's own wall pieces (read-only). Look at it, export it for Blender, or make a wall piece of your own from it."
                : "A piece of the city's sea wall that new cities get (Minimum / HardLimit per city; the plugin picks the tiles). It's a copy of the " +
                  "game's wall piece for its Role. Give it its own model to change how it looks: the model is the whole piece, with the pier as " +
                  "part of it. Saved with File > Save.";
            ShowModel();
            ShowPreview(keepCamera: false);
        }

        private void Touch()
        {
            if (_entry == null || ReadOnly) return;
            _entry.Dirty = true;
            _grid.Refresh();
            ShowModel();
            ShowPreview(keepCamera: true);
            Changed?.Invoke();
        }

        private string StarterFolder => _entry == null ? null : Path.Combine(_ws.ExportDir, "edge", Workspace.SafeName(_entry.Def.Id));
        private bool HasStarter => StarterFolder != null && File.Exists(Path.Combine(StarterFolder, ModPaths.ModelObjFile));
        private string OwnFolder => _entry == null ? null : EdgeModelTools.ModelFolder(_ws.EdgeDir, _entry.Def);
        private bool HasOwn => _entry?.Def.Model != null && _entry.PendingObj == null && Directory.Exists(OwnFolder);

        private void ShowModel()
        {
            if (_entry == null) { _title.Text = ""; _model.Text = ""; return; }
            var d = _entry.Def;
            _title.Text = ReadOnly ? $"{d.Id}  (the game's {d.Role.ToString().ToLowerInvariant()} wall piece, read-only)" : $"{d.Id}  ({d.Role}){(_entry.Dirty ? " *" : "")}";
            if (ReadOnly) { _model.Text = "Model: the game's own."; return; }
            if (_entry.PendingObj != null)
                _model.Text = $"Model: {Path.GetFileName(_entry.PendingObj)}, imported; it's copied in when you save.";
            else if (d.Model != null)
                _model.Text = $"Model: its own ({d.Model.Obj}{(d.Model.Scale != 1 ? $", ×{d.Model.Scale}" : "")}{(d.Model.SwapYZ ? ", Z-up fixed" : "")}" +
                              $"{(d.Model.KeepGameWall ? ", on top of the game's wall" : "")}). The pier, if any, is part of it.";
            else
                _model.Text = "Model: the game's wall" + (d.Pier != null ? ", with the pier the plugin builds (a starter until you import your own model). " +
                              "After a city with it has loaded, \"Export for Blender\" gives you exactly that: the wall with its opening and the pier." : ".");
        }

        // ------------------------------------------------------------------ 3D preview and facts

        /// <summary>
        /// The model the piece has in game now: an import waiting for Save, else its own model, else the starter the plugin
        /// wrote (the game's wall + its pier), else the game's plain wall piece. With the fixes (scale, Z-up) applied.
        /// </summary>
        private (LoadedModel model, string what, float scale, bool swap) CurrentModel(out string error)
        {
            error = null;
            var d = _entry.Def;
            if (_entry.PendingObj != null)
            {
                var m = ModelLoader.Load(Path.GetDirectoryName(_entry.PendingObj), out error, Path.GetFileName(_entry.PendingObj),
                                         EdgeModelTools.MtlOf(_entry.PendingObj) ?? "none.mtl");
                return (m, "imported model (not saved yet)", d.Model?.Scale ?? 1, d.Model?.SwapYZ ?? false);
            }
            if (HasOwn)
                return (ModelLoader.Load(OwnFolder, out error, d.Model.Obj, d.Model.Mtl), "its own model", d.Model.Scale, d.Model.SwapYZ);
            if (HasStarter && !ReadOnly)
                return (ModelLoader.Load(StarterFolder, out error), "as it is in the game (the game's wall + the pier)", 1, false);
            var t = CachedTemplate();
            if (t == null) EdgeModelTools.Template(_ws, d, out error);
            return (t, ReadOnly ? $"the game's wall piece {d.BasePreset}" : $"the game's plain wall piece ({d.BasePreset}); the pier shows here after a city with it has loaded", 1, false);
        }

        private static NV.Matrix4x4 FixMatrix(float scale, bool swap)
        {
            var m = swap ? new NV.Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1) : NV.Matrix4x4.Identity;   // (x, y, z) → (x, z, -y)
            return m * NV.Matrix4x4.CreateScale(scale <= 0 ? 1 : scale);
        }

        private static bool IsCollision(ModelMesh m) => m.Material.StartsWith("collision", StringComparison.OrdinalIgnoreCase);

        private void ShowPreview(bool keepCamera)
        {
            if (_entry == null) { _view.SetModel(null, NV.Matrix4x4.Identity, true); _facts.Text = ""; return; }
            var (model, what, scale, swap) = CurrentModel(out var err);
            if (model == null)
            {
                _view.SetModel(null, NV.Matrix4x4.Identity, true);
                _view.SetFaces(new List<Face>(), true);
                _showing.Text = err ?? "no model";
                _facts.Text = err ?? "";
                return;
            }
            var fix = FixMatrix(scale, swap);
            // one model for the view: this piece (fixed), plus the game's wall piece left and right of it
            var scene = new LoadedModel { Folder = model.Folder };
            foreach (var m in model.Meshes)
            {
                if (IsCollision(m) && !_collision.Checked) continue;
                scene.Meshes.Add(Copy(m, p => NV.Vector3.Transform(p, fix), IsCollision(m) ? Color.Magenta : (Color?)null));
            }
            var template = CachedTemplate();
            float len = template != null ? template.Max.X - template.Min.X : GameEdgePieces.TileLength;
            if (_neighbours.Checked && template != null)
                foreach (var dx in new[] { -len, len })
                    foreach (var m in template.Meshes)
                        scene.Meshes.Add(Copy(m, p => p + new NV.Vector3(dx, 0, 0), null));
            scene.Min = new NV.Vector3(float.MaxValue); scene.Max = new NV.Vector3(float.MinValue);
            foreach (var m in scene.Meshes) foreach (var p in m.Pos) { scene.Min = NV.Vector3.Min(scene.Min, p); scene.Max = NV.Vector3.Max(scene.Max, p); }

            // the sea and the street, as flat faces
            float x0 = scene.Min.X - 20, x1 = scene.Max.X + 20, zSea0 = scene.Min.Z - 40, zSea1 = scene.Max.Z + 40;
            var faces = new List<Face>
            {
                new() { P = new[] { new NV.Vector3(x0, GameEdgePieces.SeaY, zSea0), new NV.Vector3(x1, GameEdgePieces.SeaY, zSea0),
                                    new NV.Vector3(x1, GameEdgePieces.SeaY, zSea1), new NV.Vector3(x0, GameEdgePieces.SeaY, zSea1) },
                        Color = Color.FromArgb(48, 78, 88) },
            };
            if (template != null)
            {
                float zc = template.Max.Z;   // the city side
                faces.Add(new Face
                {
                    P = new[] { new NV.Vector3(x0, GameEdgePieces.StreetY - 0.02f, zc), new NV.Vector3(x1, GameEdgePieces.StreetY - 0.02f, zc),
                                new NV.Vector3(x1, GameEdgePieces.StreetY - 0.02f, zc + 25), new NV.Vector3(x0, GameEdgePieces.StreetY - 0.02f, zc + 25) },
                    Color = Color.FromArgb(96, 96, 92),
                });
            }
            _view.SetModel(scene, NV.Matrix4x4.Identity, keepCamera);
            _view.SetFaces(faces, keepCamera);
            _showing.Text = "Showing: " + what;
            _facts.Text = Facts(model, fix, template, what);
        }

        private readonly Dictionary<string, (DateTime stamp, LoadedModel model)> _templates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The game's wall piece, read once (it's a few MB).</summary>
        private LoadedModel CachedTemplate()
        {
            var folder = ModelLoader.FolderFor(_ws, _entry.Def.BasePreset);
            if (folder == null) return null;
            var obj = Path.Combine(folder, ModPaths.ModelObjFile);
            var stamp = File.Exists(obj) ? File.GetLastWriteTimeUtc(obj) : DateTime.MinValue;
            if (_templates.TryGetValue(folder, out var c) && c.stamp == stamp) return c.model;
            var t = EdgeModelTools.Template(_ws, _entry.Def, out _);
            _templates[folder] = (stamp, t);
            return t;
        }

        private static ModelMesh Copy(ModelMesh m, Func<NV.Vector3, NV.Vector3> f, Color? tint)
        {
            var c = new ModelMesh { Material = m.Material, TexturePath = tint == null ? m.TexturePath : null, EmissionPath = null };
            foreach (var p in m.Pos) c.Pos.Add(f(p));
            c.Nrm.AddRange(m.Nrm);
            c.Uv.AddRange(m.Uv);
            c.Idx.AddRange(m.Idx);
            return c;
        }

        /// <summary>Heights and sizes, worked out from the model (OBJ space, metres; the street is ≈ 0.2, the sea -12).</summary>
        private string Facts(LoadedModel model, NV.Matrix4x4 fix, LoadedModel template, string what)
        {
            var d = _entry.Def;
            var vis = model.Meshes.Where(m => !IsCollision(m)).SelectMany(m => m.Pos).Select(p => NV.Vector3.Transform(p, fix)).ToList();
            var col = model.Meshes.Where(IsCollision).ToList();
            if (vis.Count == 0) return "The model has no visible faces.";
            var min = new NV.Vector3(vis.Min(p => p.X), vis.Min(p => p.Y), vis.Min(p => p.Z));
            var max = new NV.Vector3(vis.Max(p => p.X), vis.Max(p => p.Y), vis.Max(p => p.Z));
            string M(float v) => $"{v,7:0.00} m";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Showing:   {what}");
            sb.AppendLine($"Role:      {d.Role}, copy of {d.BasePreset}");
            if (!ReadOnly) sb.AppendLine($"Per city:  {d.Minimum} to {(d.HardLimit > 0 ? d.HardLimit.ToString() : d.Minimum.ToString())}");
            sb.AppendLine();
            sb.AppendLine("Size");
            sb.AppendLine($"  along the wall  {M(max.X - min.X)}   (one tile: {GameEdgePieces.TileLength:0.0} m)");
            sb.AppendLine($"  across          {M(max.Z - min.Z)}");
            sb.AppendLine($"  height          {M(max.Y - min.Y)}");
            sb.AppendLine();
            sb.AppendLine("Heights");
            sb.AppendLine($"  highest point   {M(max.Y)}");
            if (template != null)
            {
                var street = vis.Where(p => p.Z > template.Max.Z - 0.4f && p.Y > -2 && p.Y < 2).ToList();
                if (street.Count > 0) sb.AppendLine($"  at the street   {M(street.Max(p => p.Y))}   (the street: {GameEdgePieces.StreetY:0.00} m)");
                var outside = vis.Where(p => p.Z < template.Min.Z - 0.5f).ToList();
                var deck = outside.Where(p => p.Y < -3 && p.Y > GameEdgePieces.SeaY).ToList();
                if (deck.Count > 0)
                {
                    // the deck's top: the most common height of points out at sea, below the street
                    var top = deck.GroupBy(p => MathF.Round(p.Y * 10) / 10).OrderByDescending(g => g.Count()).First().Key;
                    sb.AppendLine($"  pier deck       {M(top)}   ({top - GameEdgePieces.SeaY:0.0} m above the sea)");
                    if (top < GameEdgePieces.WadeY + 0.3f) sb.AppendLine($"    below the wading height ({GameEdgePieces.WadeY} m): players walk slowly there!");
                }
                if (outside.Count > 0) sb.AppendLine($"  reaches out     {M(template.Min.Z - min.Z)}   beyond the game's wall");
            }
            sb.AppendLine($"  lowest point    {M(min.Y)}   (the sea: {GameEdgePieces.SeaY:0.0} m{(min.Y > GameEdgePieces.SeaY ? ", doesn't reach it!" : "")})");
            sb.AppendLine($"  wading below    {M(GameEdgePieces.WadeY)}");
            sb.AppendLine();
            sb.AppendLine("Model");
            sb.AppendLine($"  triangles       {model.Meshes.Where(m => !IsCollision(m)).Sum(m => m.Idx.Count / 3),8:N0}");
            sb.AppendLine($"  materials       {model.Meshes.Count(m => !IsCollision(m)),8}");
            sb.AppendLine($"  textures        {model.Meshes.Count(m => m.TexturePath != null),8}{(model.Meshes.Any(m => !IsCollision(m) && m.TexturePath == null) ? "  (some parts plain colour)" : "")}");
            sb.AppendLine($"  collision       {(col.Count > 0 ? $"{col.Sum(m => m.Idx.Count / 3):N0} triangles (invisible)" : "none: the whole model is solid"),8}");
            if (d.Model?.CheckReport?.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Import check");
                foreach (var line in d.Model.CheckReport.Where(l => !l.StartsWith("Ok", StringComparison.Ordinal))) sb.AppendLine("  " + line);
                if (d.Model.CheckReport.All(l => l.StartsWith("Ok", StringComparison.Ordinal) || l.StartsWith("Info", StringComparison.Ordinal))) sb.AppendLine("  all fine");
            }
            return sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        // ------------------------------------------------------------------ export / import

        private void Export()
        {
            if (_entry == null) return;
            var d = _entry.Def;
            var choices = new List<(string label, string folder, bool template)>();
            if (HasOwn) choices.Add(("Its own model (to keep working on it)", OwnFolder, false));
            if (HasStarter && !ReadOnly) choices.Add(("As it is in the game: the wall with its opening and the pier (written by the plugin)", StarterFolder, false));
            var tmpl = EdgeModelTools.Template(_ws, d, out var err);
            if (tmpl != null) choices.Add(($"The game's plain wall piece ({d.BasePreset})", ModelLoader.FolderFor(_ws, d.BasePreset), true));
            if (choices.Count == 0)
            {
                MessageBox.Show(this, err + (d.Pier != null ? "\n\nFor the wall with its pier: generate a city with this wall piece in it once." : ""), "Export");
                return;
            }
            int pick = choices.Count == 1 ? 0 : Choose("Export for Blender", "What do you want to export?", choices.Select(c => c.label).ToList());
            if (pick < 0) return;
            using var dlg = new FolderBrowserDialog { Description = "Folder to put the model in (for Blender)", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var to = Path.Combine(dlg.SelectedPath, d.Id);
                var c = choices[pick];
                var own = c.folder == OwnFolder;
                EdgeModelTools.Export(c.folder, own ? d.Model.Obj : ModPaths.ModelObjFile, own ? d.Model.Mtl : ModPaths.ModelMtlFile, to, d.Id, c.template);
                Process.Start("explorer.exe", to);
            }
            catch (Exception ex) { MessageBox.Show(this, "Export didn't work:\n" + ex.Message, "Export"); }
        }

        private void Import()
        {
            if (_entry == null) return;
            var d = _entry.Def;
            var template = EdgeModelTools.Template(_ws, d, out var err);
            if (template == null) { MessageBox.Show(this, err, "Import"); return; }
            using var open = new OpenFileDialog { Filter = "Wavefront OBJ (*.obj)|*.obj", Title = $"Model for {d.Id}" };
            if (open.ShowDialog(this) != DialogResult.OK) return;
            var obj = open.FileName;
            var imported = ModelLoader.Load(Path.GetDirectoryName(obj), out err, Path.GetFileName(obj), EdgeModelTools.MtlOf(obj) ?? "none.mtl");
            if (imported == null) { MessageBox.Show(this, err, "Import"); return; }
            using var check = new EdgeImportDialog(template, imported, obj, d.Id);
            if (check.ShowDialog(this) != DialogResult.OK) return;
            d.Model = new EdgeModelInfo { Scale = check.UnitScale, SwapYZ = check.Swap, KeepGameWall = check.KeepGameWall, CheckReport = check.Report };
            _entry.PendingObj = obj;
            _entry.RemoveModel = false;
            Touch();
        }

        private int Choose(string title, string question, List<string> options)
        {
            using var f = new Form
            {
                Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(560, 70 + options.Count * 28 + 50), Font = Font,
            };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12) };
            flow.Controls.Add(new Label { Text = question, AutoSize = true, MaximumSize = new Size(530, 0), Margin = new Padding(0, 0, 0, 8) });
            var radios = options.Select((o, i) => new RadioButton { Text = o, AutoSize = true, Checked = i == 0 }).ToList();
            foreach (var r in radios) flow.Controls.Add(r);
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            row.Controls.Add(ok); row.Controls.Add(cancel);
            flow.Controls.Add(row);
            f.Controls.Add(flow);
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog(this) != DialogResult.OK) return -1;
            return radios.FindIndex(r => r.Checked);
        }
    }
}

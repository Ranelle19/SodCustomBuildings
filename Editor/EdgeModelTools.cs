using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows.Forms;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>
    /// A wall piece's own model: exporting a template for Blender, checking an import against it, copying the files.
    /// Everything is compared in OBJ space, the frame described at EdgeModelInfo (the template is the game wall piece
    /// exactly as the plugin exported it, so a model made from it lines up with the game's wall pieces next to it).
    /// </summary>
    public static class EdgeModelTools
    {
        public enum Level { Ok, Warn, Bad, Info }

        public sealed class Check
        {
            public readonly List<(Level level, string text)> Items = new();
            public float SuggestScale = 1;
            public bool SuggestSwap;
            public Vector3 TMin, TMax, IMin, IMax;
            public List<Vector3> TPts = new(), IPts = new();   // samples for drawing
            public bool AnyBad => Items.Any(i => i.level == Level.Bad);
        }

        /// <summary>The template: the game wall piece's export, if it's there and complete.</summary>
        public static LoadedModel Template(Workspace ws, EdgePieceDef piece, out string error)
        {
            error = null;
            var folder = ModelLoader.FolderFor(ws, piece.BasePreset);
            if (folder == null || !File.Exists(Path.Combine(folder, ModPaths.ModelObjFile)))
            {
                error = $"The game's wall piece ({piece.BasePreset}) hasn't been exported yet. Start the game once with plugin 1.0.0 " +
                        "(with ExportModels on), then try again.";
                return null;
            }
            var m = ModelLoader.Load(folder, out error);
            if (m == null) return null;
            if (m.Info != null && !m.Info.OneLod)
            {
                error = $"The export of {piece.BasePreset} is from an older plugin (it has several detail levels on top of each other). " +
                        "Start the game once with plugin 1.0.0; it exports it again.";
                return null;
            }
            return m;
        }

        public static string ModelFolder(string edgeDir, EdgePieceDef piece) => Path.Combine(EdgeStore.PieceDir(edgeDir, piece.Id), EdgeModelInfo.Folder);

        // ------------------------------------------------------------------ export

        /// <summary>Copies the template (or the piece's own model) to a folder for editing in Blender, with a readme.</summary>
        public static void Export(string fromFolder, string objName, string mtlName, string toFolder, string pieceId, bool isTemplate)
        {
            Directory.CreateDirectory(toFolder);
            foreach (var f in Directory.GetFiles(fromFolder))
            {
                var n = Path.GetFileName(f);
                var ext = Path.GetExtension(n).ToLowerInvariant();
                if (ext is ".obj" or ".mtl" or ".png" or ".jpg" or ".jpeg" or ".tga")
                    File.Copy(f, Path.Combine(toFolder, n), true);
            }
            var sb = new StringBuilder();
            sb.AppendLine($"Wall piece model for {pieceId} ({(isTemplate ? "template: the game's own wall piece" : "its current model")})");
            sb.AppendLine();
            sb.AppendLine($"Edit {objName} in Blender (File > Import > Wavefront (.obj)), then export it again as OBJ and import it");
            sb.AppendLine("in the editor (Edge > Wall pieces > Import model…). The editor checks it against the game's wall piece.");
            sb.AppendLine();
            sb.AppendLine("Keep it lined up with the template so it fits the wall pieces next to it:");
            sb.AppendLine("  - don't move or rotate the whole thing; leave the origin where it is");
            sb.AppendLine("  - metres (Blender's default unit scale 1.0)");
            sb.AppendLine("  - Blender's OBJ import/export defaults (Forward -Z, Up Y) both ways");
            sb.AppendLine("  - the two ends of the wall (along the length) must keep the template's shape, so the next piece connects");
            sb.AppendLine("  - the walkway must stay at the template's height where it meets the street");
            sb.AppendLine("  - don't go further toward the city than the template (that's the street's tile)");
            sb.AppendLine("  - down to the water (-12 m) or below, else it floats");
            sb.AppendLine("  - an object named 'collision…' is used only to walk on / bump into (invisible); without one, the whole model is solid");
            sb.AppendLine("  - textures: PNGs next to the .obj, referenced by the .mtl (map_Kd)");
            sb.AppendLine();
            sb.AppendLine("Leave a gap in the railing where the stairs of a pier should start: nothing gets cut out of your model.");
            File.WriteAllText(Path.Combine(toFolder, "README-wall-piece.txt"), sb.ToString());
        }

        // ------------------------------------------------------------------ checking

        public static Vector3 Fix(Vector3 p, float scale, bool swap) => (swap ? new Vector3(p.X, p.Z, -p.Y) : p) * scale;

        public static Check Run(LoadedModel t, LoadedModel imp, float scale, bool swap, string objPath)
        {
            var c = new Check { TMin = t.Min, TMax = t.Max };
            var ip = imp.Meshes.SelectMany(m => m.Pos).Select(p => Fix(p, scale, swap)).ToList();
            var tp = t.Meshes.SelectMany(m => m.Pos).ToList();
            c.IMin = new Vector3(ip.Min(p => p.X), ip.Min(p => p.Y), ip.Min(p => p.Z));
            c.IMax = new Vector3(ip.Max(p => p.X), ip.Max(p => p.Y), ip.Max(p => p.Z));
            c.TPts = Sample(tp, 25000);
            c.IPts = Sample(ip, 25000);
            var te = c.TMax - c.TMin; var ie = c.IMax - c.IMin;
            string M(float v) => $"{v:0.##} m";

            // units and axes (on the raw file)
            var raw = imp.Meshes.SelectMany(m => m.Pos).ToList();
            var re = new Vector3(raw.Max(p => p.X) - raw.Min(p => p.X), raw.Max(p => p.Y) - raw.Min(p => p.Y), raw.Max(p => p.Z) - raw.Min(p => p.Z));
            float ratio = Math.Max(re.X, Math.Max(re.Y, re.Z)) / Math.Max(0.01f, Math.Max(te.X, Math.Max(te.Y, te.Z)));
            if (ratio > 50 && ratio < 200) c.SuggestScale = 0.01f;
            else if (ratio > 500 && ratio < 2000) c.SuggestScale = 0.001f;
            var rs = re * c.SuggestScale;
            if (Math.Abs(rs.Y - te.Z) + Math.Abs(rs.Z - te.Y) < 0.5f * (Math.Abs(rs.Y - te.Y) + Math.Abs(rs.Z - te.Z)) && Math.Abs(te.Y - te.Z) > 2)
                c.SuggestSwap = true;
            if (c.SuggestScale != scale) c.Items.Add((Level.Bad, $"It's about {ratio:0} times the template's size: probably saved in {(c.SuggestScale == 0.01f ? "centimetres" : "millimetres")}. Tick the unit fix."));
            if (c.SuggestSwap != swap) c.Items.Add((Level.Bad, "Height and depth look swapped: probably exported Z-up. Tick the Z-up fix (or export with Up = Y)."));

            // along the wall: one tile, no more, no less
            float over0 = t.Min.X - c.IMin.X, over1 = c.IMax.X - t.Max.X;
            if (over0 > 0.05f || over1 > 0.05f)
                c.Items.Add((Level.Bad, $"Longer than the wall piece: it reaches {M(Math.Max(over0, over1))} into the next one ({M(c.IMin.X)} to {M(c.IMax.X)}, the tile is {M(t.Min.X)} to {M(t.Max.X)})."));
            else if (-over0 > 0.1f || -over1 > 0.1f)
                c.Items.Add((Level.Warn, $"Shorter than the wall piece: gaps of {M(-over0)} and {M(-over1)} at the ends, the sea shows through."));
            else c.Items.Add((Level.Ok, $"Length along the wall fits ({M(ie.X)}, the tile {M(te.X)})."));

            // the two ends: same cross-section as the template, so it connects to its neighbours
            foreach (var (label, tx, ix) in new[] { ("left", t.Min.X, c.IMin.X), ("right", t.Max.X, c.IMax.X) })
            {
                var tEnd = tp.Where(p => Math.Abs(p.X - tx) < 0.3f && p.Y < 3).ToList();
                var iEnd = ip.Where(p => Math.Abs(p.X - tx) < 0.3f && p.Y < 3).ToList();
                if (tEnd.Count == 0) continue;
                if (iEnd.Count == 0) { c.Items.Add((Level.Bad, $"Nothing of it at the {label} end of the tile: the next wall piece won't connect.")); continue; }
                float dTop = iEnd.Max(p => p.Y) - tEnd.Max(p => p.Y);
                float dIn = iEnd.Max(p => p.Z) - tEnd.Max(p => p.Z), dOut = iEnd.Min(p => p.Z) - tEnd.Min(p => p.Z);
                float dLow = iEnd.Min(p => p.Y) - tEnd.Min(p => p.Y);
                var bad = new List<string>();
                if (Math.Abs(dTop) > 0.1f) bad.Add($"walkway/railing {(dTop > 0 ? "higher" : "lower")} by {M(Math.Abs(dTop))}");
                if (Math.Abs(dIn) > 0.1f) bad.Add($"city side {(dIn > 0 ? "further in" : "further out")} by {M(Math.Abs(dIn))}");
                if (Math.Abs(dOut) > 0.15f) bad.Add($"sea side {(dOut < 0 ? "further out" : "further in")} by {M(Math.Abs(dOut))}");
                if (dLow > 0.3f) bad.Add($"bottom higher by {M(dLow)}");
                if (bad.Count == 0) c.Items.Add((Level.Ok, $"The {label} end has the template's shape: it connects to the next wall piece."));
                else c.Items.Add((Level.Warn, $"The {label} end differs from the template ({string.Join(", ", bad)}): there'll be a step or gap to the next wall piece."));
            }

            // the city side: not into the street's tile
            if (c.IMax.Z > t.Max.Z + 0.1f)
                c.Items.Add((Level.Bad, $"It reaches {M(c.IMax.Z - t.Max.Z)} further toward the city than the template: into the street's tile."));
            // street level where the walkway meets the street
            var tStreet = tp.Where(p => p.Z > t.Max.Z - 0.4f && p.Y > -2 && p.Y < 2).ToList();
            var iStreet = ip.Where(p => p.Z > t.Max.Z - 0.4f && p.Y > -2 && p.Y < 2).ToList();
            if (tStreet.Count > 0 && iStreet.Count > 0)
            {
                float d = iStreet.Max(p => p.Y) - tStreet.Max(p => p.Y);
                c.Items.Add(Math.Abs(d) > 0.06f
                    ? (Level.Warn, $"Where it meets the street it's {M(Math.Abs(d))} {(d > 0 ? "higher" : "lower")} than the template: a step there.")
                    : (Level.Ok, "Walkway height at the street fits."));
            }
            // water
            if (c.IMin.Y > GameEdgePieces.SeaY + 0.05f)
                c.Items.Add((Level.Warn, $"Its lowest point is {M(c.IMin.Y)}; the sea is at {M(GameEdgePieces.SeaY)}: it will look like it floats."));
            else c.Items.Add((Level.Ok, "Reaches down into the water."));
            if (c.IMin.Z < t.Min.Z - 0.3f)
                c.Items.Add((Level.Info, $"Reaches {M(t.Min.Z - c.IMin.Z)} further out to sea than the game's wall (that's fine: a dock, stairs…)."));

            // textures and the rest
            var missing = MissingTextures(objPath);
            if (missing.Count > 0) c.Items.Add((Level.Warn, "Textures not found next to the model: " + string.Join(", ", missing)));
            if (imp.Meshes.Any(m => !m.HasUv)) c.Items.Add((Level.Warn, "Some parts have no UVs (texture coordinates): they'll be one flat colour."));
            int tris = imp.TriangleCount;
            if (tris > 300000) c.Items.Add((Level.Warn, $"{tris:N0} triangles: a lot for one wall piece (the game's has about {t.TriangleCount:N0})."));
            else c.Items.Add((Level.Info, $"{tris:N0} triangles in {imp.Meshes.Count} material(s) (the game's piece: {t.TriangleCount:N0})."));
            return c;
        }

        private static List<Vector3> Sample(List<Vector3> pts, int max)
        {
            if (pts.Count <= max) return pts;
            var r = new List<Vector3>(max);
            double step = pts.Count / (double)max;
            for (double i = 0; i < pts.Count; i += step) r.Add(pts[(int)i]);
            return r;
        }

        public static string MtlOf(string objPath)
        {
            foreach (var raw in File.ReadLines(objPath))
            {
                var l = raw.Trim();
                if (l.StartsWith("mtllib ")) return l.Substring(7).Trim();
                if (l.StartsWith("v ")) break;
            }
            var guess = Path.ChangeExtension(Path.GetFileName(objPath), ".mtl");
            return File.Exists(Path.Combine(Path.GetDirectoryName(objPath), guess)) ? guess : null;
        }

        private static IEnumerable<string> TextureRefs(string mtlPath)
        {
            if (mtlPath == null || !File.Exists(mtlPath)) yield break;
            foreach (var raw in File.ReadLines(mtlPath))
            {
                var l = raw.Trim();
                if (!l.StartsWith("map_") && !l.StartsWith("bump ") && !l.StartsWith("norm ")) continue;
                int sp = l.IndexOf(' ');
                if (sp < 0) continue;
                var rest = l.Substring(sp + 1).Trim();
                // options like "-bm 1.0 file.png": the file is the last part
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts[0].StartsWith("-")) rest = parts[^1];
                yield return rest;
            }
        }

        private static List<string> MissingTextures(string objPath)
        {
            var dir = Path.GetDirectoryName(objPath);
            var mtl = MtlOf(objPath);
            if (mtl == null) return new List<string>();
            var mtlPath = Path.Combine(dir, mtl);
            return TextureRefs(mtlPath).Where(t => !File.Exists(Path.IsPathRooted(t) ? t : Path.Combine(dir, t))).Distinct().ToList();
        }

        // ------------------------------------------------------------------ importing

        /// <summary>
        /// Copies the OBJ, its MTL and the textures into Edge\&lt;Id&gt;\model\ (emptied first). Texture paths in the MTL
        /// become plain file names next to it. Returns the model info to store in piece.json.
        /// </summary>
        public static EdgeModelInfo Install(string objPath, string modelFolder, float scale, bool swap, bool keepGameWall, List<string> report)
        {
            var srcDir = Path.GetDirectoryName(objPath);
            if (Directory.Exists(modelFolder)) Directory.Delete(modelFolder, true);
            Directory.CreateDirectory(modelFolder);
            const string objName = "model.obj", mtlName = "model.mtl";
            var mtl = MtlOf(objPath);

            // OBJ with mtllib pointing to our copy
            var lines = File.ReadAllLines(objPath).Select(l => l.TrimStart().StartsWith("mtllib ") ? "mtllib " + mtlName : l);
            File.WriteAllLines(Path.Combine(modelFolder, objName), lines);

            if (mtl != null && File.Exists(Path.Combine(srcDir, mtl)))
            {
                var outLines = new List<string>();
                foreach (var raw in File.ReadAllLines(Path.Combine(srcDir, mtl)))
                {
                    var l = raw.Trim();
                    if (l.StartsWith("map_") || l.StartsWith("bump ") || l.StartsWith("norm "))
                    {
                        var key = l.Substring(0, l.IndexOf(' '));
                        var file = l.Substring(key.Length).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
                        var src = Path.IsPathRooted(file) ? file : Path.Combine(srcDir, file);
                        var name = Path.GetFileName(file);
                        if (File.Exists(src)) File.Copy(src, Path.Combine(modelFolder, name), true);
                        outLines.Add($"{key} {name}");
                    }
                    else outLines.Add(raw);
                }
                File.WriteAllLines(Path.Combine(modelFolder, mtlName), outLines);
            }
            return new EdgeModelInfo { Obj = objName, Mtl = mtlName, Scale = scale, SwapYZ = swap, KeepGameWall = keepGameWall, CheckReport = report };
        }
    }

    /// <summary>Shows the import check: the results, and the model over the template from above and at both ends.</summary>
    public sealed class EdgeImportDialog : Form
    {
        private readonly LoadedModel _t, _imp;
        private readonly string _obj;
        private readonly CheckBox _scale = new() { AutoSize = true, Text = "Units: centimetres → metres (×0.01)" };
        private readonly CheckBox _mm = new() { AutoSize = true, Text = "Units: millimetres → metres (×0.001)" };
        private readonly CheckBox _swap = new() { AutoSize = true, Text = "Saved Z-up: turn to Y-up" };
        private readonly CheckBox _keep = new() { AutoSize = true, Text = "Keep the game's wall too (add mine to it)" };
        private readonly ListView _items = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None };
        private readonly Panel _draw = new DoubleBufferedPanel { Dock = DockStyle.Fill };
        private EdgeModelTools.Check _check;

        public float UnitScale => _mm.Checked ? 0.001f : _scale.Checked ? 0.01f : 1f;
        public bool Swap => _swap.Checked;
        public bool KeepGameWall => _keep.Checked;
        public List<string> Report => _check?.Items.Select(i => $"{i.level}: {i.text}").ToList() ?? new List<string>();

        public EdgeImportDialog(LoadedModel template, LoadedModel imported, string objPath, string pieceId)
        {
            AppIcon.Apply(this);
            _t = template; _imp = imported; _obj = objPath;
            Text = $"Import model for {pieceId}: {Path.GetFileName(objPath)}";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1150, 760);

            _items.Columns.Add("", 1080);
            var fixes = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(6, 6, 0, 0) };
            fixes.Controls.AddRange(new Control[] { _scale, _mm, _swap, _keep });
            var top = new Panel { Dock = DockStyle.Top, Height = 230 };
            top.Controls.Add(_items);
            top.Controls.Add(fixes);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var ok = new Button { Text = "Import", Width = 100, Height = 28 };
            var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
            bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
            bottom.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 6, 16, 0), ForeColor = Color.DimGray,
                Text = "Grey: the game's wall piece (template). Blue: your model. Above: seen from above. Below: the two ends, seen along the wall." });

            Controls.Add(_draw);
            Controls.Add(top);
            Controls.Add(bottom);
            CancelButton = cancel;

            var first = EdgeModelTools.Run(_t, _imp, 1, false, _obj);
            _scale.Checked = first.SuggestScale == 0.01f;
            _mm.Checked = first.SuggestScale == 0.001f;
            _swap.Checked = first.SuggestSwap;
            foreach (var cb in new[] { _scale, _mm, _swap }) cb.CheckedChanged += (s, e) => Rerun();
            _scale.CheckedChanged += (s, e) => { if (_scale.Checked) _mm.Checked = false; };
            _mm.CheckedChanged += (s, e) => { if (_mm.Checked) _scale.Checked = false; };
            _draw.Paint += (s, e) => Draw(e.Graphics, _draw.ClientRectangle);
            ok.Click += (s, e) =>
            {
                if (_check.AnyBad && MessageBox.Show(this, "The check found problems (red). Import anyway?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                DialogResult = DialogResult.OK;
                Close();
            };
            Rerun();
        }

        private void Rerun()
        {
            _check = EdgeModelTools.Run(_t, _imp, UnitScale, Swap, _obj);
            _items.BeginUpdate();
            _items.Items.Clear();
            foreach (var (level, text) in _check.Items)
            {
                var sym = level switch { EdgeModelTools.Level.Ok => "✔", EdgeModelTools.Level.Warn => "⚠", EdgeModelTools.Level.Bad => "✘", _ => "•" };
                var it = new ListViewItem($"{sym}  {text}")
                {
                    ForeColor = level switch { EdgeModelTools.Level.Ok => Color.SeaGreen, EdgeModelTools.Level.Warn => Color.DarkGoldenrod, EdgeModelTools.Level.Bad => Color.Firebrick, _ => Color.DimGray },
                };
                _items.Items.Add(it);
            }
            _items.EndUpdate();
            _draw.Invalidate();
        }

        private void Draw(Graphics g, Rectangle r)
        {
            g.Clear(Color.FromArgb(32, 36, 40));
            if (_check == null) return;
            var c = _check;
            // above: X along, Z across
            var topArea = new Rectangle(r.Left + 10, r.Top + 10, r.Width - 20, (int)(r.Height * 0.48));
            Scatter(g, topArea, c, p => new PointF(p.X, p.Z), "Seen from above (along the wall →)");
            int w = (r.Width - 30) / 2;
            var endY = topArea.Bottom + 14;
            var leftArea = new Rectangle(r.Left + 10, endY, w, r.Bottom - endY - 10);
            var rightArea = new Rectangle(leftArea.Right + 10, endY, w, r.Bottom - endY - 10);
            Scatter(g, leftArea, c, p => new PointF(p.Z, p.Y), "Left end (across ↔, height ↕)", p => Math.Abs(p.X - c.TMin.X) < 0.3f);
            Scatter(g, rightArea, c, p => new PointF(p.Z, p.Y), "Right end", p => Math.Abs(p.X - c.TMax.X) < 0.3f);
        }

        private static void Scatter(Graphics g, Rectangle area, EdgeModelTools.Check c, Func<Vector3, PointF> proj, string title, Func<Vector3, bool> filter = null)
        {
            using (var frame = new Pen(Color.FromArgb(70, 80, 90))) g.DrawRectangle(frame, area);
            using (var f = new Font("Segoe UI", 8.5f)) g.DrawString(title, f, Brushes.Gainsboro, area.Left + 4, area.Top + 2);
            var tp = (filter == null ? c.TPts : c.TPts.Where(filter)).Select(proj).ToList();
            var ip = (filter == null ? c.IPts : c.IPts.Where(filter)).Select(proj).ToList();
            var all = tp.Concat(ip).ToList();
            if (all.Count == 0) return;
            float x0 = all.Min(p => p.X), x1 = all.Max(p => p.X), y0 = all.Min(p => p.Y), y1 = all.Max(p => p.Y);
            float sc = Math.Min((area.Width - 20) / Math.Max(0.1f, x1 - x0), (area.Height - 30) / Math.Max(0.1f, y1 - y0));
            float ox = area.Left + 10 + ((area.Width - 20) - (x1 - x0) * sc) / 2, oy = area.Bottom - 10;
            using var grey = new SolidBrush(Color.FromArgb(150, 170, 170, 170));
            using var blue = new SolidBrush(Color.FromArgb(130, 80, 160, 255));
            foreach (var p in tp) g.FillRectangle(grey, ox + (p.X - x0) * sc, oy - (p.Y - y0) * sc, 1.5f, 1.5f);
            foreach (var p in ip) g.FillRectangle(blue, ox + (p.X - x0) * sc, oy - (p.Y - y0) * sc, 1.5f, 1.5f);
        }

        private sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        }
    }
}

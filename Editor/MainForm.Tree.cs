using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>
    /// The tree's commands: copy, paste, duplicate, rename and delete for buildings, floors and wall pieces, with the
    /// keyboard (while the tree has the focus: Ctrl+C, Ctrl+V, Ctrl+D, F2, Del) and a right-click menu. The game's
    /// floors and wall pieces can be copied (into one of yours), never changed. The clipboard is the editor's own.
    /// </summary>
    public sealed partial class MainForm
    {
        private sealed class Clip
        {
            public string Kind;          // "building", "floor", "edge"
            public string Json;
            public string Name;          // what it was called
            public string ModelObj;      // a wall piece's model, copied along
            public List<(string name, string json)> Floors = new();   // a building's own floors
        }
        private Clip _clip;

        private void SetupTreeCommands()
        {
            _tree.KeyDown += (s, e) =>
            {
                bool handled = true;
                if (e.Control && e.KeyCode == Keys.C) CopySelected();
                else if (e.Control && e.KeyCode == Keys.V) PasteSelected();
                else if (e.Control && e.KeyCode == Keys.D) DuplicateSelected();
                else if (e.KeyCode == Keys.Delete) DeleteSelected();
                else handled = false;
                if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
            };
            _tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                _tree.SelectedNode = e.Node;
                var menu = ContextFor(e.Node);
                if (menu.Items.Count > 0) menu.Show(_tree, e.Location);
            };
        }

        private ContextMenuStrip ContextFor(TreeNode node)
        {
            var m = new ContextMenuStrip();
            void Add(string text, Action a, string keys = null, bool enabled = true)
            {
                var it = new ToolStripMenuItem(text, null, (s, e) => a()) { Enabled = enabled };
                if (keys != null) it.ShortcutKeyDisplayString = keys;
                m.Items.Add(it);
            }
            void Sep() { if (m.Items.Count > 0 && !(m.Items[m.Items.Count - 1] is ToolStripSeparator)) m.Items.Add(new ToolStripSeparator()); }
            bool canPaste = _clip != null;
            string pasteText = _clip == null ? "Paste" : $"Paste {_clip.Kind} '{_clip.Name}'";
            switch (node?.Tag)
            {
                case BuildingEntry:
                    Add("Copy", CopySelected, "Ctrl+C");
                    Add(pasteText, PasteSelected, "Ctrl+V", canPaste);
                    Add("Duplicate", DuplicateSelected, "Ctrl+D");
                    Add("Rename…", RenameSelected, "F2");
                    Add("Delete", DeleteSelected, "Del");
                    Sep();
                    Add("New floor…", NewFloor);
                    Add("3D preview…", PreviewBuilding);
                    Add("Make exterior…", MakeExterior);
                    Add("Paint exterior…", PaintExterior);
                    Add("Export for other mod loaders…", ExportOtherLoaders);
                    break;
                case FloorEntry:
                    Add("Copy", CopySelected, "Ctrl+C");
                    Add(pasteText, PasteSelected, "Ctrl+V", canPaste && _clip.Kind == "floor");
                    Add("Duplicate", DuplicateSelected, "Ctrl+D");
                    Add("Rename…", RenameSelected, "F2");
                    Add("Delete", DeleteSelected, "Del");
                    Sep();
                    Add("3D view of this floor…", PreviewFloor);
                    Add("Fix stairs, lifts and entrances", FixStairsCurrent);
                    Add("Edit raw JSON…", EditRawJson);
                    break;
                case EdgeEntry:
                    Add("Copy", CopySelected, "Ctrl+C");
                    Add(pasteText, PasteSelected, "Ctrl+V", canPaste && _clip.Kind == "edge");
                    Add("Duplicate", DuplicateSelected, "Ctrl+D");
                    Add("Rename…", RenameSelected, "F2");
                    Add("Delete", DeleteSelected, "Del");
                    Sep();
                    Add("New wall piece", NewEdgePiece);
                    break;
                case string s when s.StartsWith("game:"):
                    Add("Copy", CopySelected, "Ctrl+C");
                    Add("Copy into a building…", CopyGameFloor);
                    break;
                case string s when s.StartsWith("gameedge:"):
                    Add("Copy", CopySelected, "Ctrl+C");
                    Add("Make a wall piece from this", () => _edgePanel_MakeFromSelected());
                    break;
                default:
                    var text = node?.Text ?? "";
                    if (text.StartsWith("My buildings"))
                    {
                        Add("New building…", NewBuilding);
                        Add("New shopping building…", NewShoppingBuilding);
                        Add(pasteText, PasteSelected, "Ctrl+V", canPaste && _clip.Kind == "building");
                    }
                    else if (text.StartsWith("My edge pieces"))
                    {
                        Add("New wall piece", NewEdgePiece);
                        Add(pasteText, PasteSelected, "Ctrl+V", canPaste && _clip.Kind == "edge");
                    }
                    break;
            }
            return m;
        }

        private void _edgePanel_MakeFromSelected()
        {
            if (_tree.SelectedNode?.Tag is string s && s.StartsWith("gameedge:"))
                PasteEdge(new EdgePieceDef { Role = EdgeEntry.Game(s.Substring(9)).Def.Role, DisplayName = "" }, null, "BoundaryCoastal");
        }

        // ------------------------------------------------------------------ copy

        private Clip ClipOf(object tag)
        {
            switch (tag)
            {
                case BuildingEntry b:
                    return new Clip
                    {
                        Kind = "building", Name = b.Def.Id, Json = JsonSerializer.Serialize(b.Def, Json.Options),
                        Floors = b.Floors.Select(f => (f.Name, f.Doc.ToJson())).ToList(),
                    };
                case FloorEntry f:
                    return new Clip { Kind = "floor", Name = f.Name, Json = f.Doc.ToJson() };
                case string s when s.StartsWith("game:"):
                    try { return new Clip { Kind = "floor", Name = s.Substring(5), Json = _ws.LoadGameFloor(s.Substring(5)).ToJson() }; }
                    catch (Exception e) { MessageBox.Show(this, "Couldn't read that floor:\n" + e.Message); return null; }
                case EdgeEntry e:
                {
                    string obj = e.PendingObj;
                    if (obj == null && e.Def.Model != null)
                    {
                        var own = Path.Combine(EdgeModelTools.ModelFolder(_ws.EdgeDir, e.Def), e.Def.Model.Obj ?? "model.obj");
                        if (File.Exists(own)) obj = own;
                    }
                    return new Clip { Kind = "edge", Name = e.Def.Id, Json = JsonSerializer.Serialize(e.Def, Json.Options), ModelObj = obj };
                }
                case string s when s.StartsWith("gameedge:"):
                {
                    var g = EdgeEntry.Game(s.Substring(9));
                    return new Clip { Kind = "edge", Name = g.Def.Id, Json = JsonSerializer.Serialize(new EdgePieceDef { Role = g.Def.Role, DisplayName = "" }, Json.Options) };
                }
            }
            return null;
        }

        private void CopySelected()
        {
            var c = ClipOf(_tree.SelectedNode?.Tag);
            if (c == null) return;
            _clip = c;
            _status.Text = $"Copied {c.Kind} '{c.Name}'. Ctrl+V pastes it.";
        }

        private void DuplicateSelected()
        {
            var c = ClipOf(_tree.SelectedNode?.Tag);
            if (c == null) return;
            Paste(c);
        }

        private void PasteSelected()
        {
            if (_clip == null) { _status.Text = "Nothing copied yet (Ctrl+C on a building, floor or wall piece)."; return; }
            Paste(_clip);
        }

        // ------------------------------------------------------------------ paste

        private void Paste(Clip c)
        {
            if (!RequireFolder()) return;
            try
            {
                switch (c.Kind)
                {
                    case "building": PasteBuilding(c); break;
                    case "floor": PasteFloor(c); break;
                    case "edge":
                        PasteEdge(JsonSerializer.Deserialize<EdgePieceDef>(c.Json, Json.Options), c.ModelObj, c.Name);
                        break;
                }
            }
            catch (Exception e) { MessageBox.Show(this, "Paste didn't work:\n" + e.Message, Text); }
        }

        private string FreeBuildingId(string baseId)
        {
            bool Taken(string id) => _ws.Buildings.Any(b => string.Equals(b.Def.Id, id, StringComparison.OrdinalIgnoreCase)) ||
                                     _ws.EdgePieces.Any(e => string.Equals(e.Def.Id, id, StringComparison.OrdinalIgnoreCase));
            var id = baseId + "_copy";
            for (int n = 2; Taken(id); n++) id = $"{baseId}_copy{n}";
            return id;
        }

        private string FreeFloorName(string baseName)
        {
            var name = baseName + "_copy";
            for (int n = 2; _ws.FloorNameTaken(name) || _ws.GameFloorFiles.ContainsKey(name); n++) name = $"{baseName}_copy{n}";
            return name;
        }

        /// <summary>A copy of a building with copies of its floors (renamed, and the layouts pointing to the copies).</summary>
        private void PasteBuilding(Clip c)
        {
            var def = JsonSerializer.Deserialize<BuildingDef>(c.Json, Json.Options);
            var oldId = def.Id;
            def.Id = FreeBuildingId(oldId);
            def.Overrides ??= new BuildingOverrides();
            def.FloorLayouts ??= new List<FloorLayoutDef>();
            def.BasementLayouts ??= new List<FloorLayoutDef>();
            var entry = new BuildingEntry { Def = def, Dirty = true };
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, json) in c.Floors)
            {
                var newName = name.StartsWith(oldId, StringComparison.Ordinal) ? def.Id + name.Substring(oldId.Length) : def.Id + "_" + name;
                for (int n = 2; _ws.FloorNameTaken(newName) || renamed.ContainsValue(newName); n++) newName = $"{def.Id}_{name}_{n}";
                renamed[name] = newName;
                var doc = FloorDoc.Parse(json);
                doc.ReadOnly = false;
                doc.Name = newName;
                doc.Dirty = true;
                entry.Floors.Add(new FloorEntry { Owner = entry, Doc = doc });
            }
            foreach (var l in def.FloorLayouts.Concat(def.BasementLayouts))
                l.Blueprints = l.Blueprints.Select(bp => renamed.TryGetValue(bp, out var n) ? n : bp).ToList();
            if (def.ReservedPlaces != null)
                foreach (var r in def.ReservedPlaces)
                    if (r.Floor != null && renamed.TryGetValue(r.Floor, out var n)) r.Floor = n;
            _ws.Buildings.Add(entry);
            RefreshTree(entry);
            UpdateTitle();
            _status.Text = $"Pasted {def.Id} ({entry.Floors.Count} floor(s) copied along). File > Save keeps it.";
        }

        /// <summary>A floor into the selected building (or the building of the selected floor); asks if unclear.</summary>
        private void PasteFloor(Clip c)
        {
            var b = _tree.SelectedNode?.Tag switch
            {
                BuildingEntry be => be,
                FloorEntry fe => fe.Owner,
                _ => null,
            };
            b ??= _ws.Buildings.Count == 1 ? _ws.Buildings[0] : AskBuilding("Paste floor");
            if (b == null) return;
            var doc = FloorDoc.Parse(c.Json);
            doc.ReadOnly = false;
            doc.Name = FreeFloorName(c.Name);
            doc.Dirty = true;
            var entry = new FloorEntry { Owner = b, Doc = doc };
            b.Floors.Add(entry);
            b.Dirty = true;
            RefreshTree(entry);
            UpdateTitle();
            _status.Text = $"Pasted floor {doc.Name} into {b.Def.Id}. Add it to a floor layout to use it.";
        }

        /// <summary>A new wall piece from a copied one (its own model copied along when you save).</summary>
        private void PasteEdge(EdgePieceDef def, string modelObj, string baseName)
        {
            if (!RequireFolder()) return;
            def.Id = baseName.StartsWith("BoundaryCoastal", StringComparison.OrdinalIgnoreCase) ? NextEdgeId() : FreeBuildingId(baseName);
            if (string.IsNullOrWhiteSpace(def.DisplayName)) def.DisplayName = def.Role == EdgeRole.Platform ? "Platform wall" : "Sea wall";
            var entry = new EdgeEntry { Def = def, Dirty = true };
            if (def.Model != null && modelObj != null && File.Exists(modelObj)) entry.PendingObj = modelObj;
            else def.Model = null;
            _ws.EdgePieces.Add(entry);
            RefreshTree(entry);
            UpdateTitle();
            _status.Text = $"New wall piece {def.Id} ({def.Role}). File > Save keeps it.";
        }

        private string NextEdgeId()
        {
            for (int n = 3; ; n++)
            {
                var id = $"BoundaryCoastal{n:00}";
                if (!_ws.EdgePieces.Any(e => string.Equals(e.Def.Id, id, StringComparison.OrdinalIgnoreCase)) &&
                    !_ws.Buildings.Any(b => string.Equals(b.Def.Id, id, StringComparison.OrdinalIgnoreCase))) return id;
            }
        }

        /// <summary>
        /// Edge > New wall piece from the pier example: the example that comes with the mod (the game's straight wall with an
        /// opening and a pier, built into the editor), as a new wall piece of yours with its own model.
        /// </summary>
        private void NewEdgeFromExample()
        {
            if (!RequireFolder()) return;
            try
            {
                var asm = typeof(MainForm).Assembly;
                var dir = Path.Combine(Path.GetTempPath(), "SodBuildingEditor", "pier-example");
                Directory.CreateDirectory(dir);
                foreach (var name in new[] { "piece.json", "model.obj", "model.mtl", "tex0.png" })
                {
                    using var src = asm.GetManifestResourceStream("example/" + name);
                    if (src == null) throw new Exception("this editor was built without the example (Thunderstore folder missing)");
                    using var dst = File.Create(Path.Combine(dir, name));
                    src.CopyTo(dst);
                }
                var def = JsonSerializer.Deserialize<EdgePieceDef>(File.ReadAllText(Path.Combine(dir, "piece.json")), Json.Options);
                def.Notes = "Made from the pier example.";
                PasteEdge(def, Path.Combine(dir, "model.obj"), "BoundaryCoastal");
            }
            catch (Exception e) { MessageBox.Show(this, "Couldn't make it from the example:\n" + e.Message, Text); }
        }

        // ------------------------------------------------------------------ rename / delete

        private void RenameSelected()
        {
            switch (_tree.SelectedNode?.Tag)
            {
                case FloorEntry: RenameFloor(); break;
                case BuildingEntry b:
                {
                    var id = Dialogs.AskText(this, "Rename building", "New Id:", b.Def.Id,
                        v => v.Length == 0 ? "Enter an Id." :
                             !string.Equals(v, b.Def.Id, StringComparison.OrdinalIgnoreCase) &&
                             (_ws.Buildings.Any(x => string.Equals(x.Def.Id, v, StringComparison.OrdinalIgnoreCase)) ||
                              _ws.EdgePieces.Any(x => string.Equals(x.Def.Id, v, StringComparison.OrdinalIgnoreCase))) ? "That Id is already used." : null);
                    if (id == null || id == b.Def.Id) return;
                    b.Def.Id = id;
                    b.Dirty = true;
                    RefreshTree(b);
                    UpdateTitle();
                    break;
                }
                case EdgeEntry e when e.GamePreset == null:
                {
                    var id = Dialogs.AskText(this, "Rename wall piece", "New Id:", e.Def.Id,
                        v => v.Length == 0 ? "Enter an Id." :
                             GameEdgePieces.All.Contains(v, StringComparer.OrdinalIgnoreCase) ? "That's one of the game's own." :
                             !string.Equals(v, e.Def.Id, StringComparison.OrdinalIgnoreCase) &&
                             (_ws.EdgePieces.Any(x => string.Equals(x.Def.Id, v, StringComparison.OrdinalIgnoreCase)) ||
                              _ws.Buildings.Any(x => string.Equals(x.Def.Id, v, StringComparison.OrdinalIgnoreCase))) ? "That Id is already used." : null);
                    if (id == null || id == e.Def.Id) return;
                    e.Def.Id = id;
                    e.Dirty = true;
                    RefreshTree(e);
                    UpdateTitle();
                    break;
                }
            }
        }

        private void DeleteSelected()
        {
            switch (_tree.SelectedNode?.Tag)
            {
                case FloorEntry: DeleteFloor(); break;
                case BuildingEntry: DeleteBuilding(); break;
                case EdgeEntry e when e.GamePreset == null: DeleteEdgePiece(); break;
                case string s when s.StartsWith("game"):
                    _status.Text = "The game's own floors and wall pieces are read-only; copy them to make one of your own.";
                    break;
            }
        }
    }
}

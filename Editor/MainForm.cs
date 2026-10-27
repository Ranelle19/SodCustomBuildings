using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    public sealed partial class MainForm : Form
    {
        private readonly Workspace _ws = new();
        private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
        private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filter game floors…" };
        private readonly Panel _host = new() { Dock = DockStyle.Fill };
        private readonly Label _welcome = new()
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 11f),
        };
        private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripStatusLabel _hover = new() { TextAlign = ContentAlignment.MiddleRight };
        private readonly BuildingPanel _buildingPanel;
        private readonly FloorPanel _floorPanel;
        private readonly EdgePanel _edgePanel;
        private TreeNode _myRoot, _gameRoot;

        public MainForm()
        {
            AppIcon.Apply(this);
            Text = "Shadows of Doubt Building Editor";
            Width = 1400;
            Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            _buildingPanel = new BuildingPanel(_ws);
            _floorPanel = new FloorPanel(_ws);
            _edgePanel = new EdgePanel(_ws);
            _edgePanel.Changed += OnEdited;
            _buildingPanel.Changed += OnEdited;
            _buildingPanel.OpenFloor += SelectFloorByName;
            _floorPanel.Changed += OnEdited;
            _floorPanel.Status += t => _hover.Text = t;
            _buildingPanel.Preview += PreviewBuilding;
            _buildingPanel.MakeExterior += MakeExterior;
            _buildingPanel.PaintExterior += PaintExterior;
            _floorPanel.Preview3DRequested += PreviewFloor;

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(_tree);
            split.Panel1.Controls.Add(_filter);
            split.Panel2.Controls.Add(_host);
            Controls.Add(split);
            Controls.Add(BuildMenu());
            var statusStrip = new StatusStrip();
            statusStrip.Items.Add(_status);
            statusStrip.Items.Add(_hover);
            Controls.Add(statusStrip);
            Load += (s, e) => { split.SplitterDistance = 280; AutoOpen(); };

            _tree.AfterSelect += (s, e) => ShowSelection();
            SetupTreeCommands();
            _edgePanel.MakeFromGame += g => PasteEdge(new EdgePieceDef { Role = g.Def.Role, DisplayName = "" }, null, "BoundaryCoastal");
            _tree.NodeMouseDoubleClick += (s, e) => { if (e.Node == _gameRoot) e.Node.Toggle(); };
            _filter.TextChanged += (s, e) => FillGameFloors();
            FormClosing += OnClosing;

            ShowWelcome();
        }

        // ------------------------------------------------------------------ menu

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();
            ToolStripMenuItem Item(string text, Action a, Keys keys = Keys.None)
            {
                var i = new ToolStripMenuItem(text, null, (s, e) => a()) { ShortcutKeys = keys };
                return i;
            }

            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(Item("&Game folder and mod setup…", SetupAgain));
            file.DropDownItems.Add(Item("&Open mod folder…", OpenFolder, Keys.Control | Keys.O));
            file.DropDownItems.Add(Item("&Save", Save, Keys.Control | Keys.S));
            file.DropDownItems.Add(Item("&Reload from disk", Reload));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("Show mod folder in Explorer", () => { if (_ws.ModDir != null) Process.Start("explorer.exe", _ws.ModDir); }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", Close));

            var building = new ToolStripMenuItem("&Building");
            building.DropDownItems.Add(Item("New &shopping building…", NewShoppingBuilding, Keys.Control | Keys.M));
            building.DropDownItems.Add(Item("&New building…", NewBuilding, Keys.Control | Keys.N));
            building.DropDownItems.Add(Item("&Duplicate building…", DuplicateBuilding));
            building.DropDownItems.Add(Item("De&lete building", DeleteBuilding));
            building.DropDownItems.Add(new ToolStripSeparator());
            building.DropDownItems.Add(Item("&3D preview…", PreviewBuilding, Keys.Control | Keys.D3));
            building.DropDownItems.Add(Item("Make &exterior…", MakeExterior, Keys.Control | Keys.E));
            building.DropDownItems.Add(Item("&Paint exterior…", PaintExterior, Keys.Control | Keys.P));
            building.DropDownItems.Add(new ToolStripSeparator());
            building.DropDownItems.Add(Item("Export for &other mod loaders…", ExportOtherLoaders));

            var edgeMenu = new ToolStripMenuItem("&Edge");
            edgeMenu.DropDownItems.Add(Item("&New wall piece", NewEdgePiece));
            edgeMenu.DropDownItems.Add(Item("New wall piece from the &pier example", NewEdgeFromExample));
            edgeMenu.DropDownItems.Add(Item("&Delete wall piece", DeleteEdgePiece));

            var floor = new ToolStripMenuItem("F&loor");
            floor.DropDownItems.Add(Item("&New floor…", NewFloor, Keys.Control | Keys.Shift | Keys.N));
            floor.DropDownItems.Add(Item("&Copy game floor into building…", CopyGameFloor));
            floor.DropDownItems.Add(Item("&Rename…", RenameSelected, Keys.F2));
            floor.DropDownItems.Add(Item("&Delete floor", DeleteFloor));
            floor.DropDownItems.Add(new ToolStripSeparator());
            floor.DropDownItems.Add(Item("Fix &stairs, lifts and entrances", FixStairsCurrent));
            floor.DropDownItems.Add(Item("Edit raw &JSON…", EditRawJson));
            floor.DropDownItems.Add(Item("3D &view of this floor…", PreviewFloor));

            var tools = new ToolStripMenuItem("&Tools");
            tools.DropDownItems.Add(Item("&Check for problems", CheckProblems));
            tools.DropDownItems.Add(Item("Fix stairs, lifts and entrances in all my floors", FixStairsAll));
            tools.DropDownItems.Add(Item("Show content &hash (multiplayer check)", ShowHash));

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Item("&How it works", ShowHelp, Keys.F1));

            menu.Items.AddRange(new ToolStripItem[] { file, building, floor, edgeMenu, tools, help });
            return menu;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var focused = FocusedControl();
            if (_host.Controls.Contains(_floorPanel) && !(focused is TextBoxBase) && !(focused is ComboBox))
            {
                if (keyData == (Keys.Control | Keys.Z)) { _floorPanel.DoUndo(); return true; }
                if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { _floorPanel.DoRedo(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private Control FocusedControl()
        {
            Control c = ActiveControl;
            while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
            return c;
        }

        // ------------------------------------------------------------------ folder

        private readonly EditorSettings _settings = EditorSettings.Load();

        /// <summary>
        /// On start: if the game folder is known and the plugin is installed, open its mod folder; otherwise show the setup
        /// (pick the game, install BepInEx and the plugin). The plugin is built into the editor, so the .exe alone is enough.
        /// </summary>
        private void AutoOpen()
        {
            var game = _settings.GameDir;
            if (!GameSetup.IsGameFolder(game) || GameSetup.GetBepInEx(game) == BepInExState.Missing ||
                !GameSetup.PluginOk(GameSetup.GetPlugin(game)))
            {
                if (!RunSetup()) { ShowWelcome(); return; }
                game = _settings.GameDir;
            }
            OpenModDir(GameSetup.PluginDir(game));
        }

        /// <summary>Shows the setup. Returns true when the game is ready to use.</summary>
        private bool RunSetup()
        {
            using var setup = new SetupForm(_settings);
            if (setup.ShowDialog(this) != DialogResult.OK) return false;
            return GameSetup.IsGameFolder(_settings.GameDir);
        }

        private void SetupAgain()
        {
            if (!ConfirmDiscard()) return;
            if (RunSetup()) OpenModDir(GameSetup.PluginDir(_settings.GameDir));
        }

        private void OpenFolder()
        {
            if (!ConfirmDiscard()) return;
            using var dlg = new FolderBrowserDialog
            {
                Description = @"Pick the plugin folder: Shadows of Doubt\BepInEx\plugins\CustomBuildings",
                UseDescriptionForTitle = true,
                SelectedPath = _ws.ModDir ?? (GameSetup.IsGameFolder(_settings.GameDir) ? GameSetup.PluginDir(_settings.GameDir) : ""),
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) OpenModDir(dlg.SelectedPath);
        }

        private void OpenModDir(string dir)
        {
            List<string> problems;
            try { problems = _ws.Load(dir); }
            catch (Exception e) { MessageBox.Show(this, "Couldn't open the folder:\n" + e.Message, Text); return; }
            RefreshTree();
            UpdateTitle();
            _status.Text = $"{_ws.Buildings.Count} building(s) · {_ws.GameFloorFiles.Count} game floors · {_ws.Catalog.Buildings.Count} game buildings in catalog";
            ShowWelcome();
            if (problems.Count > 0) Dialogs.ShowList(this, "Some files couldn't be read", "These files were skipped:", problems);
            if (!_ws.HasExport)
                MessageBox.Show(this,
                    "This folder has no Export yet.\n\nStart the game once with the CustomBuildings plugin installed; it writes every game floor plan " +
                    "and a catalog of buildings into the Export folder. The editor uses those as templates and for checks.\n\n" +
                    "You can keep going, but there will be no game floors to copy from.", "No game data exported yet");
        }

        private void Reload()
        {
            if (_ws.ModDir == null || !ConfirmDiscard()) return;
            OpenModDir(_ws.ModDir);
        }

        private void Save()
        {
            if (_ws.ModDir == null) return;
            try { _ws.Save(); }
            catch (Exception e) { MessageBox.Show(this, "Saving failed:\n" + e.Message, Text); return; }
            RefreshTree(_tree.SelectedNode?.Tag);
            UpdateTitle();
            var warnings = _ws.Validate();
            _status.Text = $"Saved. Content hash {ContentHash.Compute(_ws.BuildingsDir)}" + (warnings.Count > 0 ? $" · {warnings.Count} warning(s)" : "");
            if (warnings.Count > 0) Dialogs.ShowList(this, "Saved, with warnings", "Saved. These things may not work in game:", warnings);
        }

        private bool ConfirmDiscard()
        {
            if (!_ws.AnyDirty) return true;
            var r = MessageBox.Show(this, "Save your changes first?", Text, MessageBoxButtons.YesNoCancel);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) Save();
            return true;
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!ConfirmDiscard()) e.Cancel = true;
        }

        // ------------------------------------------------------------------ tree

        private void RefreshTree(object select = null)
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            _myRoot = _tree.Nodes.Add("My buildings");
            foreach (var b in _ws.Buildings.OrderBy(b => b.Def.Id, StringComparer.OrdinalIgnoreCase))
            {
                var bn = _myRoot.Nodes.Add(NodeText(b));
                bn.Tag = b;
                foreach (var f in b.Floors.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    bn.Nodes.Add(new TreeNode(NodeText(f)) { Tag = f });
                bn.Expand();
            }
            _myRoot.Expand();
            var edgeRoot = _tree.Nodes.Add("My edge pieces");
            foreach (var e in _ws.EdgePieces.OrderBy(e => e.Def.Id, StringComparer.OrdinalIgnoreCase))
                edgeRoot.Nodes.Add(new TreeNode(NodeText(e)) { Tag = e });
            edgeRoot.Expand();
            var gameEdge = _tree.Nodes.Add("Game edge pieces (read-only)");
            foreach (var p in GameEdgePieces.All) gameEdge.Nodes.Add(new TreeNode(p) { Tag = "gameedge:" + p, ForeColor = Color.DimGray });
            _gameRoot = _tree.Nodes.Add("Game floors (read-only)");
            FillGameFloors(false);
            _tree.EndUpdate();
            if (select != null) SelectTag(select);
        }

        private void FillGameFloors() => FillGameFloors(true);

        private void FillGameFloors(bool update)
        {
            if (_gameRoot == null) return;
            if (update) _tree.BeginUpdate();
            _gameRoot.Nodes.Clear();
            var q = _filter.Text.Trim();
            foreach (var n in _ws.GameFloorFiles.Keys.Where(n => q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase)))
                _gameRoot.Nodes.Add(new TreeNode(n) { Tag = "game:" + n, ForeColor = Color.DimGray });
            _gameRoot.Text = $"Game floors (read-only, {_ws.GameFloorFiles.Count})";
            if (q.Length > 0) _gameRoot.Expand();
            if (update) _tree.EndUpdate();
        }

        private static string NodeText(BuildingEntry b) => b.Def.Id + (b.Dirty ? " *" : "");
        private static string NodeText(FloorEntry f) => f.Name + (f.Doc.Dirty ? " *" : "");
        private static string NodeText(EdgeEntry e) => $"{e.Def.Id} ({e.Def.Role}{(e.Def.Model != null || e.PendingObj != null ? ", own model" : "")})" + (e.Dirty ? " *" : "");

        private bool SelectTag(object tag)
        {
            foreach (var n in AllNodes(_tree.Nodes))
                if (Equals(n.Tag, tag)) { _tree.SelectedNode = n; n.EnsureVisible(); return true; }
            return false;
        }

        private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                yield return n;
                foreach (var c in AllNodes(n.Nodes)) yield return c;
            }
        }

        private void SelectFloorByName(string name)
        {
            var own = _ws.Buildings.SelectMany(b => b.Floors).FirstOrDefault(f => f.Name == name);
            if (own != null) { SelectTag(own); return; }
            if (_ws.GameFloorFiles.ContainsKey(name))
            {
                _filter.Text = "";
                SelectTag("game:" + name);
                return;
            }
            MessageBox.Show(this, $"'{name}' isn't one of your floors or an exported game floor.", Text);
        }

        private void ShowSelection()
        {
            var tag = _tree.SelectedNode?.Tag;
            switch (tag)
            {
                case BuildingEntry b:
                    Host(_buildingPanel);
                    _buildingPanel.Bind(b);
                    break;
                case FloorEntry f:
                    Host(_floorPanel);
                    _floorPanel.Bind(f.Doc, f);
                    break;
                case EdgeEntry e:
                    Host(_edgePanel);
                    _edgePanel.Bind(e);
                    break;
                case string ge when ge.StartsWith("gameedge:"):
                    Host(_edgePanel);
                    _edgePanel.Bind(EdgeEntry.Game(ge.Substring(9)));
                    break;
                case string s when s.StartsWith("game:"):
                    try
                    {
                        var doc = _ws.LoadGameFloor(s.Substring(5));
                        Host(_floorPanel);
                        _floorPanel.Bind(doc, null);
                    }
                    catch (Exception e) { MessageBox.Show(this, "Couldn't read that floor:\n" + e.Message, Text); }
                    break;
                default:
                    ShowWelcome();
                    break;
            }
        }

        private void Host(Control c)
        {
            if (_host.Controls.Count == 1 && _host.Controls[0] == c) return;
            _host.Controls.Clear();
            _host.Controls.Add(c);
        }

        private void ShowWelcome()
        {
            _welcome.Text = _ws.ModDir == null
                ? "File > Open mod folder…\r\n\r\nPick Shadows of Doubt\\BepInEx\\plugins\\CustomBuildings"
                : "Building > New shopping building… (Ctrl+M) makes a building full of shops in one step.\r\n" +
                  "Building > New building… starts an empty one. Or pick a building or floor on the left.\r\n\r\n" +
                  "Tip: copy a game floor into your building and change it, rather than drawing from nothing.";
            Host(_welcome);
        }

        private void OnEdited()
        {
            var n = _tree.SelectedNode;
            if (n?.Tag is BuildingEntry b) n.Text = NodeText(b);
            else if (n?.Tag is FloorEntry f) n.Text = NodeText(f);
            else if (n?.Tag is EdgeEntry ee) n.Text = NodeText(ee);
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            Text = "Shadows of Doubt Building Editor" + (_ws.ModDir != null ? " — " + _ws.ModDir : "") + (_ws.AnyDirty ? " *" : "");
        }

        // ------------------------------------------------------------------ buildings

        private BuildingEntry SelectedBuilding()
        {
            return _tree.SelectedNode?.Tag switch
            {
                BuildingEntry b => b,
                FloorEntry f => f.Owner,
                _ => null,
            };
        }

        private BuildingEntry AskBuilding(string why)
        {
            var b = SelectedBuilding();
            if (b != null) return b;
            if (_ws.Buildings.Count == 0) { MessageBox.Show(this, "Create a building first (Building > New building…)."); return null; }
            if (_ws.Buildings.Count == 1) return _ws.Buildings[0];
            var id = Dialogs.AskChoice(this, why, "Building:", _ws.Buildings.Select(x => x.Def.Id), _ws.Buildings[0].Def.Id);
            return id == null ? null : _ws.Buildings.FirstOrDefault(x => x.Def.Id == id);
        }

        private bool RequireFolder()
        {
            if (_ws.ModDir != null) return true;
            MessageBox.Show(this, "Open the mod folder first (File > Open mod folder…).", Text);
            return false;
        }

        private void NewShoppingBuilding()
        {
            if (!RequireFolder()) return;
            if (_ws.GameFloorFiles.Count == 0)
            {
                MessageBox.Show(this, "The game's floors haven't been exported yet. Start the game once with the plugin, then try again.", Text);
                return;
            }
            using var wiz = new ShoppingWizard(_ws);
            if (wiz.ShowDialog(this) != DialogResult.OK || wiz.Result == null) return;
            var b = wiz.Result;
            _ws.Buildings.Add(b);
            RefreshTree(b);
            UpdateTitle();
            if (MessageBox.Show(this,
                    $"Created {b.Def.Id} with {b.Floors.Count} shop floor plan(s).\n\n" +
                    "Next: File > Save, start the game and generate a NEW city (buildings only appear in new cities).\n\n" +
                    "Open the 3D preview now?", "Shopping building created", MessageBoxButtons.YesNo) == DialogResult.Yes)
                new PreviewForm(_ws, b).Show(this);
        }

        private void NewBuilding()
        {
            if (!RequireFolder()) return;
            var r = Dialogs.NewBuilding(this, _ws);
            if (r == null) return;
            var def = new BuildingDef { Id = r.Id, BasePreset = r.BasePreset };
            var cb = _ws.FindBaseBuilding(r.BasePreset);
            if (r.CopyLayouts && cb != null)
            {
                def.FloorLayouts = cb.FloorLayouts.Select(l => new FloorLayoutDef { Floors = l.Floors, Blueprints = l.Blueprints.ToList() }).ToList();
                def.ReplaceFloorLayouts = true;
            }
            var entry = new BuildingEntry { Def = def, Dirty = true };
            _ws.Buildings.Add(entry);
            RefreshTree(entry);
            UpdateTitle();
        }

        private void DuplicateBuilding()
        {
            var b = SelectedBuilding();
            if (b == null) { MessageBox.Show(this, "Select a building first."); return; }
            var id = Dialogs.AskText(this, "Duplicate building", "New Id:", b.Def.Id + "_copy",
                v => v.Length == 0 ? "Enter an Id." :
                     _ws.Buildings.Any(x => string.Equals(x.Def.Id, v, StringComparison.OrdinalIgnoreCase)) ? "That Id is already used." : null);
            if (id == null) return;
            var def = JsonSerializer.Deserialize<BuildingDef>(JsonSerializer.Serialize(b.Def, Json.Options), Json.Options);
            def.Id = id;
            def.Overrides ??= new BuildingOverrides();
            var entry = new BuildingEntry { Def = def, Dirty = true };
            _ws.Buildings.Add(entry);
            RefreshTree(entry);
            UpdateTitle();
        }

        private void DeleteBuilding()
        {
            var b = SelectedBuilding();
            if (b == null) return;
            if (MessageBox.Show(this, $"Delete {b.Def.Id} and its {b.Floors.Count} floor(s)? This happens on disk when you save.",
                    "Delete building", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _ws.DeleteBuilding(b);
            RefreshTree();
            ShowWelcome();
            UpdateTitle();
        }

        // ------------------------------------------------------------------ floors

        private void NewFloor()
        {
            if (!RequireFolder()) return;
            var b = AskBuilding("New floor");
            if (b == null) return;
            string suggested = _tree.SelectedNode?.Tag is string s && s.StartsWith("game:") ? s.Substring(5)
                             : _tree.SelectedNode?.Tag is FloorEntry fe ? fe.Name : null;
            var r = Dialogs.NewFloor(this, _ws, b, suggested);
            if (r == null) return;

            FloorDoc template = _ws.Buildings.SelectMany(x => x.Floors).FirstOrDefault(f => f.Name == r.Template)?.Doc;
            try { template ??= _ws.LoadGameFloor(r.Template); }
            catch (Exception e) { MessageBox.Show(this, "Couldn't read the template:\n" + e.Message); return; }
            if (template == null) { MessageBox.Show(this, $"'{r.Template}' wasn't found."); return; }

            var doc = template.Clone();
            doc.ReadOnly = false;
            doc.Name = r.Name;
            if (r.ClearRooms) doc.ClearAddresses();
            doc.Dirty = true;
            AddFloor(b, doc);
        }

        private void CopyGameFloor()
        {
            if (!RequireFolder()) return;
            if (!(_tree.SelectedNode?.Tag is string s && s.StartsWith("game:")))
            {
                MessageBox.Show(this, "Select a game floor in the list on the left first.");
                return;
            }
            var gameName = s.Substring(5);
            var b = _ws.Buildings.Count == 1 ? _ws.Buildings[0] : null;
            if (b == null)
            {
                if (_ws.Buildings.Count == 0) { MessageBox.Show(this, "Create a building first (Building > New building…)."); return; }
                var id = Dialogs.AskChoice(this, "Copy game floor", "Into building:", _ws.Buildings.Select(x => x.Def.Id), _ws.Buildings[0].Def.Id);
                b = _ws.Buildings.FirstOrDefault(x => x.Def.Id == id);
                if (b == null) return;
            }
            var name = Dialogs.AskText(this, "Copy game floor", "New floor name:", b.Def.Id + "_" + gameName,
                v => v.Length == 0 ? "Enter a name." : _ws.FloorNameTaken(v) ? "That name is already used." : null);
            if (name == null) return;
            var doc = _ws.LoadGameFloor(gameName).Clone();
            doc.ReadOnly = false;
            doc.Name = name;
            doc.Dirty = true;
            AddFloor(b, doc);
        }

        private void AddFloor(BuildingEntry b, FloorDoc doc)
        {
            var entry = new FloorEntry { Owner = b, Doc = doc };
            b.Floors.Add(entry);
            b.Dirty = true;
            if (MessageBox.Show(this, $"Use '{doc.Name}' in {b.Def.Id}'s floor layout now?\n\n" +
                                      "Yes adds it to the first floor layout. You can move it in the building's layout list later.",
                    "Add to layout", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                if (b.Def.FloorLayouts.Count == 0) b.Def.FloorLayouts.Add(new FloorLayoutDef { Floors = 1 });
                b.Def.FloorLayouts[0].Blueprints.Add(doc.Name);
                b.Def.ReplaceFloorLayouts = true;
            }
            RefreshTree(entry);
            UpdateTitle();
        }

        private void RenameFloor()
        {
            if (!(_tree.SelectedNode?.Tag is FloorEntry f)) { MessageBox.Show(this, "Select one of your floors first."); return; }
            var name = Dialogs.AskText(this, "Rename floor", "New name:", f.Name,
                v => v.Length == 0 ? "Enter a name." : v != f.Name && _ws.FloorNameTaken(v, f) ? "That name is already used." : null);
            if (name == null || name == f.Name) return;
            _ws.RenameFloor(f, name);
            RefreshTree(f);
            UpdateTitle();
        }

        private void DeleteFloor()
        {
            if (!(_tree.SelectedNode?.Tag is FloorEntry f)) { MessageBox.Show(this, "Select one of your floors first."); return; }
            if (MessageBox.Show(this, $"Delete floor {f.Name}? Layouts that use it will need fixing. This happens on disk when you save.",
                    "Delete floor", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _ws.DeleteFloor(f);
            RefreshTree(f.Owner);
            UpdateTitle();
        }

        private void EditRawJson()
        {
            if (!(_tree.SelectedNode?.Tag is FloorEntry f)) { MessageBox.Show(this, "Select one of your floors first."); return; }
            var json = Dialogs.EditJson(this, $"Raw JSON: {f.Name}", f.Doc.ToJson());
            if (json == null) return;
            var keepName = f.Name;
            try
            {
                f.Doc.ReplaceAll(json);
                f.Doc.Name = keepName;   // the file name decides the floor's name
            }
            catch (Exception e) { MessageBox.Show(this, e.Message); return; }
            _floorPanel.Reload();
            OnEdited();
        }

        // ------------------------------------------------------------------ 3D preview

        private void PreviewBuilding()
        {
            var b = SelectedBuilding();
            if (b == null) { MessageBox.Show(this, "Select a building (or one of its floors) first."); return; }
            new PreviewForm(_ws, b).Show(this);
        }

        private void MakeExterior()
        {
            if (!RequireFolder()) return;
            var b = SelectedBuilding();
            if (b == null) { MessageBox.Show(this, "Select a building (or one of its floors) first."); return; }
            using var dlg = new ExteriorDialog(_ws, b);
            if (dlg.ShowDialog(this) != DialogResult.OK || !dlg.ExteriorMade) return;
            RefreshTree(_tree.SelectedNode?.Tag ?? b);
            UpdateTitle();
            new PreviewForm(_ws, b).Show(this);
        }

        // ------------------------------------------------------------------ the city edge (tree: My edge pieces)

        /// <summary>Edge > New wall piece: a copy of the game's straight wall piece with the starter pier.</summary>
        private void NewEdgePiece()
        {
            if (!RequireFolder()) return;
            string id;
            for (int n = 3; ; n++)
            {
                id = $"BoundaryCoastal{n:00}";
                var taken = id;
                if (!_ws.EdgePieces.Any(e => string.Equals(e.Def.Id, taken, StringComparison.OrdinalIgnoreCase)) &&
                    !_ws.Buildings.Any(b => string.Equals(b.Def.Id, taken, StringComparison.OrdinalIgnoreCase))) break;
            }
            var entry = new EdgeEntry { Def = new EdgePieceDef { Id = id, Pier = new PierDef { Name = id + " pier" } }, Dirty = true };
            _ws.EdgePieces.Add(entry);
            RefreshTree(entry);
            UpdateTitle();
        }

        private void DeleteEdgePiece()
        {
            if (!(_tree.SelectedNode?.Tag is EdgeEntry e)) { MessageBox.Show(this, "Select one of your edge pieces first."); return; }
            if (MessageBox.Show(this, $"Delete {e.Def.Id} (and its own model, if any)? This happens on disk when you save.", "Delete wall piece",
                    MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _ws.RemoveEdge(e);
            RefreshTree();
            UpdateTitle();
        }

        private void ExportOtherLoaders()
        {
            if (!RequireFolder()) return;
            var b = SelectedBuilding();
            if (b == null) { MessageBox.Show(this, "Select a building (or one of its floors) first."); return; }
            if (_ws.AnyDirty && MessageBox.Show(this, "Unsaved changes are exported as they are in the editor now. Go on?", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            using var dlg = new FolderBrowserDialog { Description = "Folder to write the packages into", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var r = OtherLoadersExport.Export(_ws, b, dlg.SelectedPath);
                var lines = new List<string>
                {
                    "Asset Bundle Loader (with Community Case Loader + DDSLoader): " + r.AblFolder,
                    "   a ready content-only mod; copy its plugins folder into BepInEx, or zip the folder for Thunderstore.",
                    "UniversalObjectLoader: " + r.UolFolder,
                    "   JSON and floor plans for a modder's own plugin (see its README).",
                    "",
                };
                lines.AddRange(r.Notes);
                Dialogs.ShowList(this, "Exported", "Written:", lines);
                Process.Start("explorer.exe", dlg.SelectedPath);
            }
            catch (Exception e) { MessageBox.Show(this, "Export failed:\n" + e.Message, Text); }
        }

        private void PaintExterior()
        {
            if (!RequireFolder()) return;
            var b = SelectedBuilding();
            if (b == null) { MessageBox.Show(this, "Select a building (or one of its floors) first."); return; }
            var open = Application.OpenForms.OfType<ExteriorPainter>().FirstOrDefault(f => f.Tag == b);
            if (open != null) { open.Activate(); return; }
            if (!ModelLoader.ExteriorExists(_ws, b))
            {
                if (MessageBox.Show(this, "This building has no exterior of its own yet. Make one first? " +
                                          "(Choose \"Plain brick\" for an empty base to paint on.)", "Paint exterior",
                                    MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                MakeExterior();
                if (!ModelLoader.ExteriorExists(_ws, b)) return;
            }
            new ExteriorPainter(_ws, b) { Tag = b }.Show(this);
        }

        private void PreviewFloor()
        {
            switch (_tree.SelectedNode?.Tag)
            {
                case FloorEntry f:
                    new PreviewForm(_ws, f.Doc, f.Name).Show(this);
                    break;
                case string s when s.StartsWith("game:"):
                    var name = s.Substring(5);
                    FloorDoc doc;
                    try { doc = _ws.LoadGameFloor(name); }
                    catch (Exception e) { MessageBox.Show(this, "Couldn't read that floor:\n" + e.Message); return; }
                    if (doc != null) new PreviewForm(_ws, doc, name).Show(this);
                    break;
                default:
                    MessageBox.Show(this, "Select a floor first.");
                    break;
            }
        }

        // ------------------------------------------------------------------ tools

        private string DefaultWall() => _ws.WallPresets.FirstOrDefault(id => GameNames.WallName(id) == "DefaultWalls") ?? "0";
        private string DoorWall() => _ws.WallPresets.FirstOrDefault(id => GameNames.WallName(id) == "InteriorDoorway") ?? "7";

        private void FixStairsCurrent()
        {
            if (_tree.SelectedNode?.Tag is not FloorEntry f) { MessageBox.Show(this, "Select one of your floors first."); return; }
            var before = f.Doc.StairProblems();
            before.AddRange(f.Doc.BlockedEntrances().Select(e => $"entrance at cell {e.x},{e.y} without a door"));
            f.Doc.PushUndo();
            int n = f.Doc.FixStairs(DefaultWall()) + f.Doc.FixEntrances(DoorWall());
            _floorPanel.Reload();
            OnEdited();
            var after = f.Doc.StairProblems();
            var msg = $"{n} change(s) on {f.Name}.";
            if (before.Count > 0) msg += "\n\nWas:\n• " + string.Join("\n• ", before);
            if (after.Count > 0) msg += "\n\nStill to fix by hand:\n• " + string.Join("\n• ", after);
            MessageBox.Show(this, msg, "Fix stairs and lifts");
        }

        private void FixStairsAll()
        {
            if (!RequireFolder()) return;
            var lines = new List<string>();
            foreach (var f in _ws.Buildings.SelectMany(b => b.Floors))
            {
                if (f.Doc.StairProblems().Count == 0 && f.Doc.BlockedEntrances().Count == 0) continue;
                f.Doc.PushUndo();
                int n = f.Doc.FixStairs(DefaultWall()) + f.Doc.FixEntrances(DoorWall());
                lines.Add($"{f.Name}: {n} change(s)");
                foreach (var p in f.Doc.StairProblems()) lines.Add($"   still: {p}");
            }
            _floorPanel.Reload();
            OnEdited();
            if (lines.Count == 0) MessageBox.Show(this, "All stairs and lifts already look like the game's.", Text);
            else Dialogs.ShowList(this, "Fix stairs and lifts", "Fixed (File > Save to keep it; Ctrl+Z on a floor undoes it):", lines);
        }

        private void CheckProblems()
        {
            if (!RequireFolder()) return;
            var w = _ws.Validate();
            if (w.Count == 0) MessageBox.Show(this, "No problems found.", Text);
            else Dialogs.ShowList(this, "Problems", "These things may not work in game:", w);
        }

        private void ShowHash()
        {
            if (!RequireFolder()) return;
            var hash = ContentHash.Compute(_ws.BuildingsDir);
            MessageBox.Show(this,
                $"Content hash of the saved files: {hash}\n\n" +
                "In multiplayer every player must have exactly the same Buildings folder. The game log (BepInEx\\LogOutput.log) " +
                "prints the same hash on start, so players can compare." + (_ws.AnyDirty ? "\n\nYou have unsaved changes; save first." : ""),
                "Content hash");
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "1. Install BepInEx and the CustomBuildings plugin, then start the game once. The plugin writes every game floor " +
                "plan and a building catalog to the Export folder.\r\n\r\n" +
                "2. Here: File > Open mod folder (BepInEx\\plugins\\CustomBuildings).\r\n\r\n" +
                "3. Building > New building: pick an Id and a game building whose outside (model) you want to reuse.\r\n\r\n" +
                "4. Select a game floor on the left and use Floor > Copy game floor into building, then change it: paint addresses " +
                "(apartments, shops) and rooms, walls, doors, entrances and stairs.\r\n\r\n" +
                "5. In the building, set which floor plans each floor uses. The total number of floors should match the base model.\r\n\r\n" +
                "6. Save, start the game and generate a new city. New buildings only appear in newly generated cities.\r\n\r\n" +
                "Multiplayer: every player needs the plugin and the same Buildings folder (compare Tools > Show content hash).",
                "How it works");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    public sealed class BuildingEntry
    {
        public BuildingDef Def;
        /// <summary>Folder name on disk when loaded (null for new buildings). Used to rename the folder when the Id changes.</summary>
        public string LoadedDirName;
        public readonly List<FloorEntry> Floors = new();
        public bool Dirty;
    }

    public sealed class FloorEntry
    {
        public BuildingEntry Owner;
        public FloorDoc Doc;
        /// <summary>File name (without .json) when loaded; null for new floors.</summary>
        public string LoadedName;
        public string Name => Doc.Name;
    }

    /// <summary>One of our wall pieces (Edge\&lt;Id&gt;\piece.json), as shown in the tree under "My edge pieces".</summary>
    public sealed class EdgeEntry
    {
        public EdgePieceDef Def;
        /// <summary>Id when read from disk (null for new ones), to move its folder on a rename.</summary>
        public string LoadedId;
        public bool Dirty;
        /// <summary>An imported OBJ waiting for File > Save (copied into Edge\&lt;Id&gt;\model then).</summary>
        public string PendingObj;
        /// <summary>"Use the game's wall" was clicked: its model folder goes on save.</summary>
        public bool RemoveModel;
        /// <summary>Set for the game's own wall pieces (tree: Game edge pieces): shown read-only, never saved.</summary>
        public string GamePreset;

        public static EdgeEntry Game(string preset) => new()
        {
            GamePreset = preset,
            Def = new EdgePieceDef
            {
                Id = preset, DisplayName = preset, Minimum = 0, HardLimit = 0,
                Role = preset == GameEdgePieces.Platform ? EdgeRole.Platform : preset == GameEdgePieces.Corner ? EdgeRole.Corner : EdgeRole.Straight,
            },
        };
    }

    /// <summary>The mod folder: BepInEx\plugins\CustomBuildings with Buildings\ and Export\ inside.</summary>
    public sealed class Workspace
    {
        public string ModDir { get; private set; }
        public Catalog Catalog { get; private set; } = new();
        public readonly List<BuildingEntry> Buildings = new();
        public readonly SortedDictionary<string, string> GameFloorFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FloorDoc> _gameFloorCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _pendingDeletes = new();
        public readonly List<EdgeEntry> EdgePieces = new();
        public string EdgeDir => EdgeStore.Root(ModDir);

        // Values seen in the game's floor plans and the catalog, for dropdowns.
        public readonly SortedSet<string> AddressPresets = new(StringComparer.Ordinal);
        public readonly SortedSet<string> WallPresets = new(StringComparer.Ordinal);
        public readonly SortedSet<string> RoomLayouts = new(StringComparer.Ordinal);
        public readonly SortedSet<string> FloorRefs = new(StringComparer.Ordinal);

        public string BuildingsDir => Path.Combine(ModDir, ModPaths.BuildingsDir);
        public string ExportDir => Path.Combine(ModDir, ModPaths.ExportDir);
        public bool HasExport => File.Exists(Path.Combine(ExportDir, ModPaths.CatalogFile));
        public bool AnyDirty => Buildings.Any(b => b.Dirty || b.Floors.Any(f => f.Doc.Dirty)) || _pendingDeletes.Count > 0 || EdgePieces.Any(e => e.Dirty);

        public List<string> Load(string modDir)
        {
            var problems = new List<string>();
            ModDir = modDir;
            Buildings.Clear();
            EdgePieces.Clear();
            GameFloorFiles.Clear();
            _gameFloorCache.Clear();
            _gameRoomTypes.Clear();
            _pendingDeletes.Clear();
            AddressPresets.Clear(); WallPresets.Clear(); RoomLayouts.Clear(); FloorRefs.Clear();
            Catalog = new Catalog();

            var catalogPath = Path.Combine(ExportDir, ModPaths.CatalogFile);
            if (File.Exists(catalogPath))
            {
                try { Catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(catalogPath), Json.Options) ?? new Catalog(); }
                catch (Exception e) { problems.Add($"catalog.json: {e.Message}"); }
            }
            // Floor files name addresses by layout type (Retail, Apartment…) and walls by id ("7"); see GameNames.
            foreach (var s in Catalog.LayoutConfigurations) AddressPresets.Add(s);
            foreach (var s in Catalog.WallPresetNames.Keys) WallPresets.Add(s);
            GameNames.Walls = Catalog.WallPresetNames ?? new Dictionary<string, string>();
            StairsSourceConverter.Choices.Clear();
            StairsSourceConverter.Choices.Add("");
            foreach (var b in Catalog.Buildings.Where(b => !b.Custom && !b.Boundary && !b.NonEnterable).OrderByDescending(b => b.StairwellHasLift).ThenBy(b => b.PresetName))
                StairsSourceConverter.Choices.Add(b.PresetName + (b.StairwellHasLift ? "  (lift)" : string.IsNullOrEmpty(b.Stairwell) ? "" : "  (no lift)"));

            var floorDir = Path.Combine(ExportDir, ModPaths.ExportFloorsDir);
            if (Directory.Exists(floorDir))
                foreach (var f in Directory.GetFiles(floorDir, "*.json"))
                    GameFloorFiles[Path.GetFileNameWithoutExtension(f)] = f;

            // Learn the values the game actually uses from its own floor plans.
            foreach (var f in GameFloorFiles.Values)
            {
                try
                {
                    var doc = FloorDoc.Parse(File.ReadAllText(f));
                    doc.CollectValues(AddressPresets, WallPresets, RoomLayouts, FloorRefs);
                }
                catch { /* a broken export file is reported when opened */ }
            }

            Directory.CreateDirectory(BuildingsDir);
            EdgeStore.Migrate(ModDir);   // edge things from before the Edge folder (piers.json, wall pieces saved as buildings)
            foreach (var dir in Directory.GetDirectories(BuildingsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(dir, ModPaths.BuildingFile);
                if (!File.Exists(file)) continue;
                var entry = new BuildingEntry { LoadedDirName = Path.GetFileName(dir) };
                try
                {
                    entry.Def = JsonSerializer.Deserialize<BuildingDef>(File.ReadAllText(file), Json.Options) ?? new BuildingDef();
                }
                catch (Exception e)
                {
                    problems.Add($"{file}: {e.Message}");
                    continue;
                }
                entry.Def.Overrides ??= new BuildingOverrides();
                entry.Def.FloorLayouts ??= new List<FloorLayoutDef>();
                entry.Def.BasementLayouts ??= new List<FloorLayoutDef>();

                var fdir = Path.Combine(dir, ModPaths.FloorsDir);
                if (Directory.Exists(fdir))
                    foreach (var f in Directory.GetFiles(fdir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var doc = FloorDoc.Parse(File.ReadAllText(f));
                            var name = Path.GetFileNameWithoutExtension(f);
                            if (doc.Name != name) { doc.Name = name; doc.Dirty = true; }
                            entry.Floors.Add(new FloorEntry { Owner = entry, Doc = doc, LoadedName = name });
                            doc.CollectValues(AddressPresets, WallPresets, RoomLayouts, FloorRefs);
                        }
                        catch (Exception e) { problems.Add($"{f}: {e.Message}"); }
                    }
                Buildings.Add(entry);
            }

            foreach (var (def, _) in EdgeStore.LoadPieces(EdgeDir, problems.Add))
                EdgePieces.Add(new EdgeEntry { Def = def, LoadedId = def.Id });
            return problems;
        }

        public FloorDoc LoadGameFloor(string name)
        {
            if (_gameFloorCache.TryGetValue(name, out var doc)) return doc;
            if (!GameFloorFiles.TryGetValue(name, out var path)) return null;
            doc = FloorDoc.Parse(File.ReadAllText(path));
            doc.ReadOnly = true;
            _gameFloorCache[name] = doc;
            return doc;
        }

        public IEnumerable<string> OwnFloorNames() => Buildings.SelectMany(b => b.Floors).Select(f => f.Name);

        public bool FloorNameTaken(string name, FloorEntry except = null) =>
            GameFloorFiles.ContainsKey(name) ||
            Catalog.Floors.Contains(name, StringComparer.OrdinalIgnoreCase) && !OwnFloorNames().Contains(name, StringComparer.OrdinalIgnoreCase) ||
            Buildings.SelectMany(b => b.Floors).Any(f => f != except && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        public bool FloorExists(string name) =>
            OwnFloorNames().Contains(name, StringComparer.Ordinal) ||
            GameFloorFiles.ContainsKey(name) ||
            Catalog.Floors.Contains(name, StringComparer.Ordinal);

        public CatalogBuilding FindBaseBuilding(string preset) =>
            Catalog.Buildings.FirstOrDefault(b => b.PresetName == preset && !b.Custom);

        // ------------------------------------------------------------------ edits that touch the disk on save

        public void DeleteFloor(FloorEntry f)
        {
            f.Owner.Floors.Remove(f);
            if (f.LoadedName != null && f.Owner.LoadedDirName != null)
                _pendingDeletes.Add(Path.Combine(BuildingsDir, f.Owner.LoadedDirName, ModPaths.FloorsDir, f.LoadedName + ".json"));
            f.Owner.Dirty = true;
        }

        public void DeleteBuilding(BuildingEntry b)
        {
            Buildings.Remove(b);
            if (b.LoadedDirName != null) _pendingDeletes.Add(Path.Combine(BuildingsDir, b.LoadedDirName));
        }

        /// <summary>Renames a floor and updates blueprint references in every building.</summary>
        public void RenameFloor(FloorEntry f, string newName)
        {
            var old = f.Name;
            f.Doc.Name = newName;
            f.Doc.Dirty = true;
            foreach (var b in Buildings)
                foreach (var layout in b.Def.FloorLayouts.Concat(b.Def.BasementLayouts))
                    for (int i = 0; i < layout.Blueprints.Count; i++)
                        if (layout.Blueprints[i] == old) { layout.Blueprints[i] = newName; b.Dirty = true; }
        }

        /// <summary>Removes a wall piece; its folder goes on save.</summary>
        public void RemoveEdge(EdgeEntry e)
        {
            EdgePieces.Remove(e);
            if (e.LoadedId != null) _pendingDeletes.Add(EdgeStore.PieceDir(EdgeDir, e.LoadedId));
        }

        public void Save()
        {
            foreach (var p in _pendingDeletes)
            {
                if (File.Exists(p)) File.Delete(p);
                else if (Directory.Exists(p)) Directory.Delete(p, true);
            }
            _pendingDeletes.Clear();

            foreach (var b in Buildings)
            {
                var dirName = SafeName(b.Def.Id);
                var dir = Path.Combine(BuildingsDir, dirName);
                if (b.LoadedDirName != null && b.LoadedDirName != dirName)
                {
                    var oldDir = Path.Combine(BuildingsDir, b.LoadedDirName);
                    if (Directory.Exists(oldDir) && !Directory.Exists(dir)) Directory.Move(oldDir, dir);
                }
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, ModPaths.BuildingFile), JsonSerializer.Serialize(b.Def, Json.Options));
                b.LoadedDirName = dirName;
                b.Dirty = false;

                var fdir = Path.Combine(dir, ModPaths.FloorsDir);
                Directory.CreateDirectory(fdir);
                foreach (var f in b.Floors)
                {
                    if (f.LoadedName != null && f.LoadedName != f.Name)
                    {
                        var oldFile = Path.Combine(fdir, f.LoadedName + ".json");
                        if (File.Exists(oldFile)) File.Delete(oldFile);
                    }
                    File.WriteAllText(Path.Combine(fdir, SafeName(f.Name) + ".json"), f.Doc.ToJson());
                    f.LoadedName = f.Name;
                    f.Doc.Dirty = false;
                }
            }
            SaveEdge();
        }

        /// <summary>Our wall pieces: renamed folders moved, imported models copied in, removed models deleted.</summary>
        private void SaveEdge()
        {
            foreach (var e in EdgePieces)
            {
                if (!e.Dirty && e.LoadedId != null) continue;
                e.Def.Id = (e.Def.Id ?? "").Trim();
                if (e.LoadedId != null && !string.Equals(e.LoadedId, e.Def.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var from = EdgeStore.PieceDir(EdgeDir, e.LoadedId);
                    var to = EdgeStore.PieceDir(EdgeDir, e.Def.Id);
                    if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
                }
                var modelFolder = EdgeModelTools.ModelFolder(EdgeDir, e.Def);
                if (e.PendingObj != null)
                {
                    var m = e.Def.Model ?? new EdgeModelInfo();
                    e.Def.Model = EdgeModelTools.Install(e.PendingObj, modelFolder, m.Scale, m.SwapYZ, m.KeepGameWall, m.CheckReport);
                    e.PendingObj = null;
                }
                else if (e.RemoveModel && e.Def.Model == null && Directory.Exists(modelFolder)) Directory.Delete(modelFolder, true);
                e.RemoveModel = false;
                EdgeStore.SavePiece(EdgeDir, e.Def);
                e.LoadedId = e.Def.Id;
                e.Dirty = false;
            }
        }

        public static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        // ------------------------------------------------------------------ checks

        public List<string> Validate()
        {
            var w = new List<string>();
            foreach (var g in Buildings.GroupBy(b => b.Def.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                w.Add($"Building Id '{g.Key}' is used more than once.");
            foreach (var g in OwnFloorNames().GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                w.Add($"Floor name '{g.Key}' is used more than once.");
            foreach (var n in OwnFloorNames().Where(n => GameFloorFiles.ContainsKey(n)))
                w.Add($"Floor '{n}' has the same name as a game floor. Rename it (prefix it with your building id).");

            // our wall pieces
            foreach (var g in EdgePieces.GroupBy(e => (e.Def.Id ?? "").Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                w.Add($"Wall piece Id '{g.Key}' is used more than once.");
            foreach (var e in EdgePieces)
            {
                var id = (e.Def.Id ?? "").Trim();
                if (id.Length == 0) w.Add("A wall piece has no Id.");
                else if (GameEdgePieces.All.Contains(id, StringComparer.OrdinalIgnoreCase)) w.Add($"Wall piece {id}: that's one of the game's own; pick another Id.");
                else if (Buildings.Any(b => string.Equals(b.Def.Id, id, StringComparison.OrdinalIgnoreCase))) w.Add($"Wall piece {id}: one of your buildings has that Id.");
                if (e.Def.HardLimit > 0 && e.Def.Minimum > e.Def.HardLimit) w.Add($"Wall piece {id}: Minimum is more than HardLimit.");
                if (e.Def.Minimum < 1) w.Add($"Wall piece {id}: Minimum 0, so new cities may not get it.");
            }

            foreach (var b in Buildings)
            {
                var d = b.Def;
                if (string.IsNullOrWhiteSpace(d.Id)) w.Add("A building has no Id.");
                if (Catalog.Buildings.Count > 0 && FindBaseBuilding(d.BasePreset) == null)
                    w.Add($"{d.Id}: base building '{d.BasePreset}' isn't in the game's catalog.");
                if (Catalog.Buildings.Any(c => !c.Custom && c.PresetName == d.Id))
                    w.Add($"{d.Id}: the Id is the same as a game building. Pick a unique Id.");

                CheckLayouts(w, d, d.FloorLayouts, d.ReplaceFloorLayouts, "floor");
                CheckLayouts(w, d, d.BasementLayouts, d.ReplaceBasementLayouts, "basement");
                foreach (var f in b.Floors) CheckRooms(w, f.Doc);
                foreach (var f in b.Floors)
                {
                    foreach (var p in f.Doc.StairProblems()) w.Add($"{f.Name}: {p} (Floor > Fix stairs, lifts and entrances)");
                    foreach (var (x, y, _) in f.Doc.BlockedEntrances())
                        w.Add($"{f.Name}: the entrance at cell {x},{y} has no door, so people get stuck there (Floor > Fix stairs, lifts and entrances).");
                    foreach (var p in f.Doc.BigHallways()) w.Add($"{f.Name}: {p}");
                }
                CheckStairsLineUp(w, b);
                foreach (var r in d.ReservedPlaces ?? new List<ReservedPlace>())
                {
                    var f = b.Floors.FirstOrDefault(x => x.Name == r.Floor);
                    if (f == null) { w.Add($"{d.Id}: a unit on floor '{r.Floor}' is set to always be {r.Place}, but the building has no floor of that name (renamed?)."); continue; }
                    if (r.Address < 1 || r.Address > f.Doc.Addresses.Count) { w.Add($"{d.Id}: {r.Floor} address {r.Address} is set to always be {r.Place}, but that address doesn't exist."); continue; }
                    if (Catalog.Places.Count > 0 && !Catalog.Places.Any(p => p.Name == r.Place))
                        w.Add($"{d.Id}: {r.Floor} address {r.Address} is set to always be '{r.Place}', which the game didn't list (a place from a mod that isn't installed?).");
                }

                var cb = FindBaseBuilding(d.BasePreset);
                if (cb != null && d.ReplaceFloorLayouts && d.FloorLayouts.Count > 0 && !ModelLoader.ExteriorExists(this, b))
                {
                    int sum = d.FloorLayouts.Sum(l => Math.Max(1, l.Floors));
                    if (sum != cb.LayoutStoreys)
                        w.Add($"{d.Id}: layouts cover {sum} storey(s) but {d.BasePreset} uses {cb.LayoutStoreys}.");
                }
            }
            return w;
        }

        /// <summary>Stairs and lifts have to be on the same tile, turned the same way, on every storey they connect.</summary>
        private void CheckStairsLineUp(List<string> w, BuildingEntry b)
        {
            var d = b.Def;
            var layouts = d.ReplaceFloorLayouts && d.FloorLayouts.Count > 0 ? d.FloorLayouts : FindBaseBuilding(d.BasePreset)?.FloorLayouts;
            if (layouts == null) return;
            FloorDoc Resolve(string name)
            {
                var own = b.Floors.FirstOrDefault(f => f.Name == name) ?? Buildings.SelectMany(x => x.Floors).FirstOrDefault(f => f.Name == name);
                if (own != null) return own.Doc;
                try { return LoadGameFloor(name); } catch { return null; }
            }
            var plans = new List<(int storey, string name, HashSet<(int, int, bool, int)> tiles)>();
            int storey = 0;
            foreach (var l in layouts)
                for (int k = 0; k < Math.Max(1, l.Floors); k++, storey++)
                    foreach (var name in l.Blueprints.Distinct())
                    {
                        var doc = Resolve(name);
                        if (doc != null) plans.Add((storey, name, doc.StairTiles().ToHashSet()));
                    }
            static string Desc((int tx, int ty, bool stairs, int rot) t) => $"{(t.stairs ? "stairs" : "lift")} on tile {t.tx},{t.ty}" + (t.stairs ? $" turned {t.rot}" : "");
            var told = new HashSet<string>();
            foreach (var lower in plans)
                foreach (var upper in plans.Where(p => p.storey == lower.storey + 1))
                {
                    foreach (var t in lower.tiles.Where(t => !upper.tiles.Contains(t)))
                    {
                        var msg = $"{d.Id}: {lower.name} (storey {lower.storey}) has {Desc(t)}, but {upper.name} above has no matching one. People can get stuck there.";
                        if (told.Add(msg)) w.Add(msg);
                    }
                    foreach (var t in upper.tiles.Where(t => !lower.tiles.Contains(t)))
                    {
                        var msg = $"{d.Id}: {upper.name} (storey {upper.storey}) has {Desc(t)}, but {lower.name} below has no matching one.";
                        if (told.Add(msg)) w.Add(msg);
                    }
                }
        }

        /// <summary>
        /// Room types must be ones the game knows, and fit the address: a shop uses RetailSpace, PrivateOffice, Bathroom…
        /// (the catalog's LayoutRooms lists them per address type; game floors are used as a second source).
        /// </summary>
        private void CheckRooms(List<string> w, FloorDoc doc)
        {
            var known = new HashSet<string>(Catalog.RoomTypePresets.Concat(RoomLayouts), StringComparer.Ordinal) { FloorDoc.NullRoom };
            for (int a = 0; a < doc.Addresses.Count; a++)
            {
                var type = doc.AddressPreset(a);
                var label = GameNames.AddressTypeLabel(type);
                if (type.Length == 0) { w.Add($"{doc.Name}: address {a + 1} has no type."); continue; }
                if (!FloorDoc.OutdoorTypes.Contains(type))
                {
                    var open = doc.NodesIn(a, 0).Where(n => FloorDoc.Int(n.Node, "f_t", 1) == 0).Select(n => $"{n.X},{n.Y}").ToList();
                    if (open.Count > 0)
                        w.Add($"{doc.Name}: address {a + 1} ({label}) has {open.Count} cell(s) with tile type 0 (no floor, no ceiling, " +
                              $"open air): {string.Join(" ", open.Take(12))}{(open.Count > 12 ? " …" : "")}. Inside cells should be 1 " +
                              "(or 4 on stairs). Fix with the Tile type tool.");
                }
                if (Catalog.LayoutConfigurations.Count > 0 && !Catalog.LayoutConfigurations.Contains(type))
                    w.Add($"{doc.Name}: address {a + 1} has the type '{type}', which the game doesn't know.");
                Catalog.LayoutRooms.TryGetValue(type, out var allowed);
                var usedByGame = GameRoomTypes(type);
                for (int v = 0; v < doc.VariationCount(a); v++)
                {
                    int nulls = 0;
                    for (int r = 0; r < doc.RoomCount(a, v); r++)
                    {
                        var l = doc.RoomLayout(a, v, r);
                        int cells = FloorDoc.NodesOf(doc.Room(a, v, r)).Count;
                        if (l == FloorDoc.NullRoom) { nulls++; continue; }
                        string where = $"{doc.Name}: address {a + 1} ({label})";
                        if (l.Length == 0) w.Add($"{where} has a room with no type ({cells} cells).");
                        else if (!known.Contains(l)) w.Add($"{where} has a room type '{l}' that the game doesn't know.");
                        else
                        {
                            var ok = new HashSet<string>(usedByGame, StringComparer.Ordinal);
                            if (allowed != null) ok.UnionWith(allowed);
                            if (ok.Count > 0 && !ok.Contains(l))
                                w.Add($"{where}: '{l}' isn't a room this kind of address uses. It uses: " +
                                      string.Join(", ", ok.Where(x => x != FloorDoc.NullRoom).OrderBy(x => x)) + ".");
                        }
                    }
                    if (nulls == 0 && (type == "Retail" || type == "Eatery")) w.Add($"{doc.Name}: address {a + 1} ({label}) has no Null room. Game addresses always start with one.");
                }
                if (type == "Retail" || type == "Eatery")
                {
                    var rooms = Enumerable.Range(0, doc.RoomCount(a, 0)).Select(r => doc.RoomLayout(a, 0, r)).ToList();
                    string main = type == "Retail" ? "RetailSpace" : "Eatery";
                    if (!rooms.Contains(main)) w.Add($"{doc.Name}: address {a + 1} ({label}) has no '{main}' room.");
                    if (type == "Eatery" && !rooms.Contains("Kitchen")) w.Add($"{doc.Name}: address {a + 1} (restaurant) has no Kitchen.");
                }
            }
        }

        private readonly Dictionary<string, HashSet<string>> _gameRoomTypes = new(StringComparer.Ordinal);

        /// <summary>Room types the game's own floors use for this address type.</summary>
        private HashSet<string> GameRoomTypes(string addressType)
        {
            if (_gameRoomTypes.Count == 0)
                foreach (var name in GameFloorFiles.Keys)
                {
                    FloorDoc g;
                    try { g = LoadGameFloor(name); } catch { continue; }
                    if (g == null) continue;
                    for (int a = 0; a < g.Addresses.Count; a++)
                    {
                        var t = g.AddressPreset(a);
                        if (!_gameRoomTypes.TryGetValue(t, out var set)) _gameRoomTypes[t] = set = new HashSet<string>(StringComparer.Ordinal);
                        for (int v = 0; v < g.VariationCount(a); v++)
                            for (int r = 0; r < g.RoomCount(a, v); r++) set.Add(g.RoomLayout(a, v, r));
                    }
                }
            return _gameRoomTypes.TryGetValue(addressType, out var s) ? s : new HashSet<string>();
        }

        private void CheckLayouts(List<string> w, BuildingDef d, List<FloorLayoutDef> layouts, bool replace, string what)
        {
            if (!replace) return;
            for (int i = 0; i < layouts.Count; i++)
            {
                if (layouts[i].Blueprints.Count == 0)
                    w.Add($"{d.Id}: {what} layout {i + 1} has no floors.");
                foreach (var bp in layouts[i].Blueprints.Where(bp => !FloorExists(bp)))
                    w.Add($"{d.Id}: {what} layout {i + 1} uses '{bp}', which doesn't exist.");
            }
        }
    }
}

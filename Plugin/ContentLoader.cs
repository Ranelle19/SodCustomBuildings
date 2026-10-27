using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CustomBuildings
{
    using CustomBuildings.Shared;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Reads building.json + floor files from disk once, then registers them with the game:
    /// floors as TextAssets (AssetLoader.allFloorData + CityData.floorData), buildings as clones of a vanilla BuildingPreset
    /// (AssetLoader.allBuildingData). Registration is idempotent and runs from several hooks, because the game's
    /// asset loading is asynchronous and we don't want to depend on one exact moment.
    /// </summary>
    internal static class ContentLoader
    {
        private class FloorFile
        {
            public string Name;
            public string Json;
            public string Source;
        }

        private static readonly List<BuildingDef> Defs = new();
        internal static IReadOnlyList<BuildingDef> Definitions => Defs;
        /// <summary>Folder each building was read from (for its exterior).</summary>
        private static readonly Dictionary<string, string> DefDirs = new(StringComparer.Ordinal);
        private static readonly SortedDictionary<string, FloorFile> Floors = new(StringComparer.Ordinal);

        // Our live game objects, kept here so they're never collected and so we can tell them apart from vanilla.
        internal static readonly Dictionary<string, TextAsset> FloorAssets = new(StringComparer.Ordinal);
        internal static readonly Dictionary<string, BuildingPreset> Buildings = new(StringComparer.Ordinal);

        /// <summary>The folder a building or wall piece was read from.</summary>
        internal static string DirOf(string id) => DefDirs.TryGetValue(id, out var d) ? d : null;

        /// <summary>Our registered preset for an Id (null if not registered or collected).</summary>
        internal static BuildingPreset FindOurPreset(string id)
            => Buildings.TryGetValue(id, out var p) && p != null && !p.WasCollected ? p : null;
        private static readonly HashSet<string> WarnedMissingBase = new();

        public static string ContentHash { get; private set; } = "";

        /// <summary>Set once the game has run CityData.ParseFloorData itself. Before that we only add TextAssets, never
        /// dictionary entries, so the game's own parse can't hit a duplicate key.</summary>
        public static bool FloorDataParsed;

        public static bool IsOurFloor(string name) => name != null && Floors.ContainsKey(name);
        public static bool IsOurBuilding(string name) => name != null && Defs.Any(d => d.Id == name);

        // ---------------------------------------------------------------- disk

        public static void ReadFiles()
        {
            Defs.Clear();
            DefDirs.Clear();
            Floors.Clear();
            var root = Path.Combine(Plugin.ModDir, ModPaths.BuildingsDir);
            foreach (var m in EdgeStore.Migrate(Plugin.ModDir)) Plugin.L.LogInfo($"[edge] {m}");   // from before the Edge folder

            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                var buildingFile = Path.Combine(dir, ModPaths.BuildingFile);
                if (File.Exists(buildingFile))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(buildingFile);
                        var def = JsonSerializer.Deserialize<BuildingDef>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), Json.Options);
                        if (def == null || string.IsNullOrWhiteSpace(def.Id))
                            Plugin.L.LogError($"{buildingFile}: missing Id, skipped.");
                        else if (Defs.Any(d => d.Id == def.Id))
                            Plugin.L.LogError($"{buildingFile}: Id '{def.Id}' is used twice, skipped.");
                        else
                        {
                            Defs.Add(def);
                            DefDirs[def.Id] = dir;
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.L.LogError($"{buildingFile}: {e.Message}");
                    }
                }

                var floorDir = Path.Combine(dir, ModPaths.FloorsDir);
                if (!Directory.Exists(floorDir)) continue;
                foreach (var file in Directory.GetFiles(floorDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (Floors.TryGetValue(name, out var existing))
                    {
                        Plugin.L.LogError($"Floor '{name}' exists twice ({existing.Source} and {file}). Floor names must be unique; the second one is skipped.");
                        continue;
                    }
                    try
                    {
                        var bytes = File.ReadAllBytes(file);
                        var json = FixFloorName(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), name, file);
                        Floors[name] = new FloorFile { Name = name, Json = json, Source = file };
                    }
                    catch (Exception e)
                    {
                        Plugin.L.LogError($"{file}: {e.Message}");
                    }
                }
            }

            // the city edge: our wall pieces become buildings in memory (same code path: copy, limits, later their exterior)
            foreach (var (piece, dir) in EdgeStore.LoadPieces(EdgeStore.Root(Plugin.ModDir), err => Plugin.L.LogError("[edge] " + err)))
            {
                if (!piece.Enabled) { Plugin.L.LogInfo($"[edge] {piece.Id} is switched off"); continue; }
                if (GameEdgePieces.All.Contains(piece.Id) || Defs.Any(d => d.Id == piece.Id))
                {
                    Plugin.L.LogError($"[edge] {piece.Id}: that Id is already used (by the game or a building); skipped.");
                    continue;
                }
                var def = piece.ToBuildingDef();
                Defs.Add(def);
                DefDirs[def.Id] = dir;
            }

            Defs.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            ContentHash = Shared.ContentHash.Compute(root);
            Plugin.L.LogInfo($"Read {Defs.Count} building(s) and {Floors.Count} floor(s). Content hash {ContentHash} " +
                             "(every player in a multiplayer game must show the same hash).");
        }

        /// <summary>The game finds a floor by name, so the JSON's floorName must equal the file name.</summary>
        private static string FixFloorName(string json, string name, string file)
        {
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null) throw new Exception("not a JSON object");
            var current = node["floorName"]?.GetValue<string>();
            if (current == name) return json;
            Plugin.L.LogWarning($"{file}: floorName '{current}' does not match the file name; using '{name}'.");
            node["floorName"] = name;
            return node.ToJsonString();
        }

        // ---------------------------------------------------------------- game

        public static void RegisterAll(string from)
        {
            try
            {
                RegisterFloors(from);
                RegisterBuildings(from);
                BuildingNames.Apply(from);
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"Registering custom buildings ({from}) failed: {e}");
            }
        }

        public static void RegisterFloors(string from)
        {
            var loader = AssetLoader.Instance;
            var cityData = CityData.Instance;

            foreach (var f in Floors.Values)
            {
                if (!FloorAssets.TryGetValue(f.Name, out var asset) || asset == null || asset.WasCollected)
                {
                    asset = new TextAsset(f.Json);
                    asset.name = f.Name;
                    asset.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    FloorAssets[f.Name] = asset;
                }

                // 1) the asset list the game parses floors from
                if (loader != null && loader.allFloorData != null)
                {
                    bool present = false, clash = false;
                    foreach (var existing in loader.allFloorData)
                    {
                        if (existing == null || existing.name != f.Name) continue;
                        if (existing.Pointer == asset.Pointer) present = true; else clash = true;
                    }
                    if (clash)
                        Plugin.L.LogWarning($"Floor '{f.Name}' already exists in the game; rename yours (use a prefix like cb_).");
                    else if (!present)
                    {
                        loader.allFloorData.Add(asset);
                        Plugin.Verbose($"[{from}] floor asset {f.Name} added");
                    }
                }

                // 2) the parsed dictionary, only after the game has parsed it (catches floors the parse missed)
                if (FloorDataParsed && cityData != null && cityData.floorData != null && !cityData.floorData.ContainsKey(f.Name))
                {
                    var parsed = JsonUtility.FromJson(f.Json, Il2CppType.Of<FloorSaveData>());
                    var data = parsed?.TryCast<FloorSaveData>();
                    if (data == null)
                    {
                        Plugin.L.LogError($"Floor '{f.Name}' could not be parsed by the game. Check the file in the editor.");
                        continue;
                    }
                    data.floorName = f.Name;
                    cityData.floorData.Add(f.Name, data);
                    Plugin.Verbose($"[{from}] floor data {f.Name} added");
                }
            }
        }

        public static void RegisterBuildings(string from)
        {
            var loader = AssetLoader.Instance;
            if (loader == null || loader.allBuildingData == null) return;

            foreach (var def in Defs)
            {
                if (Buildings.TryGetValue(def.Id, out var done) && done != null && !done.WasCollected)
                {
                    EnsureInList(loader, done);
                    EnsureCached(done, from);
                    continue;
                }

                var basePreset = FindVanillaPreset(def.BasePreset);
                if (basePreset == null)
                {
                    if (WarnedMissingBase.Add(def.Id))
                        Plugin.L.LogWarning($"[{from}] {def.Id}: base building '{def.BasePreset}' not found yet; will retry.");
                    continue;
                }

                var clone = Object.Instantiate(basePreset).Cast<BuildingPreset>();
                clone.name = def.Id;
                clone.presetName = def.Id;
                clone.hideFlags = HideFlags.DontUnloadUnusedAsset;

                if (def.ReplaceFloorLayouts && def.FloorLayouts.Count > 0)
                    clone.floorLayouts = BuildLayouts(def, def.FloorLayouts, clone.floorLayouts, "floor");
                if (def.ReplaceBasementLayouts)
                    clone.basementLayouts = BuildLayouts(def, def.BasementLayouts, clone.basementLayouts, "basement");

                ApplyOverrides(clone, def.Overrides);
                bool ownExterior = DefDirs.TryGetValue(def.Id, out var defDir) && ExteriorLoader.Apply(clone, basePreset, def, defDir);

                Buildings[def.Id] = clone;
                EnsureInList(loader, clone);
                EnsureCached(clone, from);
                Places.ShareLimits(basePreset, clone);

                // Game buildings' layouts don't add up to floorCount (they include roofs, lobby ceilings…), so compare with
                // the base building's own layouts, which are known to fit its model. A building with its own exterior fits itself.
                int floors = 0, baseFloors = 0;
                foreach (var l in clone.floorLayouts) floors += l.floorsWithThisSetting;
                foreach (var l in basePreset.floorLayouts) baseFloors += l.floorsWithThisSetting;
                if (!ownExterior && floors != baseFloors)
                    Plugin.L.LogWarning($"{def.Id}: layouts cover {floors} storey(s) but '{def.BasePreset}' uses {baseFloors}. " +
                                        "Windows and floors may not line up.");
                Plugin.L.LogInfo($"[{from}] Registered building {def.Id} (based on {def.BasePreset}).");
                if (def.Edge != null) EdgePieces.OnRegistered(def, clone, basePreset);
            }
        }

        private static readonly HashSet<string> CacheLogged = new(StringComparer.Ordinal);

        /// <summary>
        /// A saved city finds its buildings' presets by name in the game's preset cache (Toolbox.resourcesCache), which the
        /// game fills once at start-up, before our presets exist. Without this, loading a city that has one of our
        /// buildings fails (NewBuilding.Load: NullReferenceException) and the load never finishes.
        /// </summary>
        internal static void EnsureCached(BuildingPreset preset, string from)
        {
            var tb = Toolbox.Instance;
            if (tb == null || preset == null) return;
            string how = "?";
            try
            {
                if (Find(tb, preset.presetName) is { } f && f.Pointer == preset.Pointer) return;   // already there
                bool replaced = false;
                try { replaced = tb.TryReplaceInResourcesCache(preset); } catch (Exception e) { how = "replace failed: " + e.Message; }
                if (!(Find(tb, preset.presetName) is { } f2 && f2.Pointer == preset.Pointer))
                {
                    tb.ProcessLoadedScriptableObject(preset);
                    how = replaced ? "replaced, then added" : "added";
                }
                else how = "replaced";
                bool ok = Find(tb, preset.presetName) is { } f3 && f3.Pointer == preset.Pointer;
                if (CacheLogged.Add(preset.presetName + how))
                    Plugin.L.LogInfo($"[{from}] {preset.presetName}: put in the game's preset cache ({how}){(ok ? "" : " – but the game still can't find it by name")}.");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[{from}] {preset.presetName}: couldn't put it in the game's preset cache ({how}): {e.GetType().Name}: {e.Message}"); }
        }

        internal static BuildingPreset Find(Toolbox tb, string name)
        {
            try { return tb.LoadDataFromResources<BuildingPreset>(name, out var p) ? p : null; }
            catch { return null; }
        }

        /// <summary>Before a city loads: all our presets in the cache again (in case the game rebuilt it).</summary>
        internal static void EnsureAllCached(string from)
        {
            foreach (var b in Buildings.Values)
                if (b != null && !b.WasCollected) EnsureCached(b, from);
        }

        private static void EnsureInList(AssetLoader loader, BuildingPreset preset)
        {
            BuildingPreset stale = null;
            foreach (var b in loader.allBuildingData)
            {
                if (b == null || b.presetName != preset.presetName) continue;
                if (b.Pointer == preset.Pointer) return;
                stale = b;
            }
            if (stale != null) loader.allBuildingData.Remove(stale);
            loader.allBuildingData.Add(preset);
        }

        private static BuildingPreset FindVanillaPreset(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var loader = AssetLoader.Instance;
            if (loader != null && loader.allBuildingData != null)
            {
                foreach (var b in loader.allBuildingData)
                    if (b != null && (b.presetName == name || b.name == name) && !IsOurBuilding(b.presetName))
                        return b;
            }
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<BuildingPreset>()))
            {
                var b = o?.TryCast<BuildingPreset>();
                if (b != null && (b.presetName == name || b.name == name) && !IsOurBuilding(b.presetName))
                    return b;
            }
            return null;
        }

        /// <summary>
        /// Builds new InteriorFloorSettings. Extra settings (air vents, hidden model parts, ceiling height…) are taken from the
        /// base building's layout at the same position (or its last one), since those belong to the same exterior model.
        /// </summary>
        private static Il2CppSystem.Collections.Generic.List<BuildingPreset.InteriorFloorSetting> BuildLayouts(
            BuildingDef def, List<FloorLayoutDef> defs,
            Il2CppSystem.Collections.Generic.List<BuildingPreset.InteriorFloorSetting> templates, string what)
        {
            var templateList = new List<BuildingPreset.InteriorFloorSetting>();
            if (templates != null)
                foreach (var t in templates) templateList.Add(t);

            var result = new Il2CppSystem.Collections.Generic.List<BuildingPreset.InteriorFloorSetting>();
            for (int i = 0; i < defs.Count; i++)
            {
                var d = defs[i];
                var template = templateList.Count == 0 ? null : templateList[Math.Min(i, templateList.Count - 1)];
                var s = new BuildingPreset.InteriorFloorSetting();
                if (template != null)
                {
                    s.airVentMaximumExtrusion = template.airVentMaximumExtrusion;
                    s.controlRoomVariants = template.controlRoomVariants;
                    s.forceShowModel = template.forceShowModel;
                    s.forceHideModels = template.forceHideModels;
                    s.forceHideModelsInRooms = template.forceHideModelsInRooms;
                    s.forceHideModelsOutside = template.forceHideModelsOutside;
                    s.overrideCeilingHeight = template.overrideCeilingHeight;
                    s.newCeilingHeight = template.newCeilingHeight;
                }
                else
                {
                    s.controlRoomVariants = new Il2CppSystem.Collections.Generic.List<TextAsset>();
                    s.forceHideModels = new Il2CppSystem.Collections.Generic.List<string>();
                    s.forceHideModelsOutside = new Il2CppSystem.Collections.Generic.List<string>();
                }

                s.floorsWithThisSetting = Math.Max(1, d.Floors);
                s.blueprints = new Il2CppSystem.Collections.Generic.List<TextAsset>();
                foreach (var bpName in d.Blueprints)
                {
                    var asset = ResolveFloor(bpName);
                    if (asset != null) s.blueprints.Add(asset);
                    else Plugin.L.LogError($"{def.Id}: {what} layout {i + 1} uses floor '{bpName}', which doesn't exist.");
                }
                if (s.blueprints.Count == 0)
                    Plugin.L.LogError($"{def.Id}: {what} layout {i + 1} has no usable floors; the game may fail to build it.");
                result.Add(s);
            }
            return result;
        }

        private static TextAsset ResolveFloor(string name)
        {
            if (FloorAssets.TryGetValue(name, out var ours) && ours != null) return ours;
            var loader = AssetLoader.Instance;
            if (loader?.allFloorData != null)
                foreach (var t in loader.allFloorData)
                    if (t != null && t.name == name) return t;
            return null;
        }

        private static void ApplyOverrides(BuildingPreset p, BuildingOverrides o)
        {
            if (o == null) return;
            if (o.Disable.HasValue) p.disable = o.Disable.Value;
            if (o.Zone.HasValue) p.displayedZone = (BuildingPreset.ZoneType)(int)o.Zone.Value;
            if (o.DensityMinimum.HasValue) p.densityMinimum = (BuildingPreset.Density)(int)o.DensityMinimum.Value;
            if (o.DensityMaximum.HasValue) p.densityMaximum = (BuildingPreset.Density)(int)o.DensityMaximum.Value;
            if (o.LandValueMinimum.HasValue) p.landValueMinimum = (BuildingPreset.LandValue)(int)o.LandValueMinimum.Value;
            if (o.LandValueMaximum.HasValue) p.landValueMaximum = (BuildingPreset.LandValue)(int)o.LandValueMaximum.Value;
            if (o.Minimum.HasValue) p.minimum = o.Minimum.Value;
            if (o.HardLimit.HasValue) p.hardLimit = o.HardLimit.Value;
            if (o.DesiredRatio.HasValue) p.desiredRatio = o.DesiredRatio.Value;
            if (o.FeatureImportance.HasValue) p.featureImportance = o.FeatureImportance.Value;
            if (o.AllowedInAllDistricts.HasValue) p.allowedInAllDistricts = o.AllowedInAllDistricts.Value;
            if (!string.IsNullOrWhiteSpace(o.StairsFrom))
            {
                var src = FindVanillaPreset(o.StairsFrom.Trim());
                if (src == null) Plugin.L.LogWarning($"{p.presetName}: StairsFrom '{o.StairsFrom}' isn't a game building; keeping the base building's stairs.");
                else
                {
                    p.stairwellRegular = src.stairwellRegular;
                    p.stairwellLarge = src.stairwellLarge;
                    Plugin.L.LogInfo($"{p.presetName}: stairs from {o.StairsFrom} ({src.stairwellRegular?.name}, lift: {(src.stairwellRegular != null && src.stairwellRegular.featuresElevator ? "yes" : "no")}).");
                }
            }
            if (o.PossibleNames != null && o.PossibleNames.Count > 0)
            {
                var names = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var n in o.PossibleNames) names.Add(n);
                p.possibleNames = names;
                p.overrideNaming = true;
            }
        }
    }
}

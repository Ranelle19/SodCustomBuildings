using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace CustomBuildings
{
    using CustomBuildings.Shared;

    /// <summary>
    /// Writes Export/floors/*.json (every floor plan the game ships, in its own format) and Export/catalog.json
    /// (building presets with their layouts, plus the preset names floor plans refer to). The editor reads both.
    /// </summary>
    internal static class Exporter
    {
        private static bool _done;

        public static void ExportOnce()
        {
            if (_done || Plugin.ExportOnStart == null || !Plugin.ExportOnStart.Value) return;
            var loader = AssetLoader.Instance;
            if (loader == null || loader.allFloorData == null || loader.allFloorData.Count == 0 ||
                loader.allBuildingData == null || loader.allBuildingData.Count == 0)
                return;
            _done = true;

            try
            {
                var exportDir = Path.Combine(Plugin.ModDir, ModPaths.ExportDir);
                var floorDir = Path.Combine(exportDir, ModPaths.ExportFloorsDir);
                Directory.CreateDirectory(floorDir);

                var catalog = new Catalog { GameVersion = Application.version };

                int written = 0;
                foreach (var t in loader.allFloorData)
                {
                    if (t == null || string.IsNullOrEmpty(t.name)) continue;
                    catalog.Floors.Add(t.name);
                    if (ContentLoader.IsOurFloor(t.name)) continue;
                    File.WriteAllText(Path.Combine(floorDir, Safe(t.name) + ".json"), t.text);
                    written++;
                }

                foreach (var b in loader.allBuildingData)
                {
                    if (b == null) continue;
                    var cb = new CatalogBuilding
                    {
                        PresetName = string.IsNullOrEmpty(b.presetName) ? b.name : b.presetName,
                        FloorCount = b.floorCount,
                        Zone = b.displayedZone.ToString(),
                        NonEnterable = b.nonEnterable,
                        Boundary = b.boundary,
                        Custom = ContentLoader.IsOurBuilding(b.presetName),
                        Stairwell = b.stairwellRegular?.name ?? "",
                        StairwellHasLift = b.stairwellRegular != null && b.stairwellRegular.featuresElevator,
                        LargeStairwell = b.stairwellLarge?.name ?? "",
                        LargeStairwellHasLift = b.stairwellLarge != null && b.stairwellLarge.featuresElevator,
                        FloorLayouts = Layouts(b.floorLayouts),
                        BasementLayouts = Layouts(b.basementLayouts),
                    };
                    catalog.Buildings.Add(cb);
                }

                catalog.AddressPresets = Names<AddressPreset>(loader);
                catalog.DoorPairPresets = Names<DoorPairPreset>(loader);
                catalog.LayoutConfigurations = Names<LayoutConfiguration>(loader);
                catalog.RoomConfigurations = Names<RoomConfiguration>(loader);
                catalog.RoomTypePresets = Names<RoomTypePreset>(loader);
                FillWallNames(catalog, loader);
                FillPlaces(catalog);
                FillLayoutRooms(catalog, loader);

                catalog.Buildings.Sort((a, b) => string.CompareOrdinal(a.PresetName, b.PresetName));
                catalog.Floors.Sort(StringComparer.Ordinal);

                File.WriteAllText(Path.Combine(exportDir, ModPaths.CatalogFile), JsonSerializer.Serialize(catalog, Json.Options));
                Plugin.L.LogInfo($"Exported {written} vanilla floor(s) and {catalog.Buildings.Count} building(s) to {exportDir}");

                if (Plugin.ExportModels != null && Plugin.ExportModels.Value)
                {
                    try { ModelExporter.ExportAll(exportDir); }
                    catch (Exception e) { Plugin.L.LogError($"Model export failed: {e}"); }
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"Export failed: {e}");
            }
        }

        private static IEnumerable<T> All<T>(AssetLoader loader) where T : ScriptableObject
        {
            var seen = new HashSet<IntPtr>();
            if (loader.allPresets != null)
                foreach (var o in loader.allPresets)
                {
                    var t = o?.TryCast<T>();
                    if (t != null && seen.Add(t.Pointer)) yield return t;
                }
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
            {
                var t = o?.TryCast<T>();
                if (t != null && seen.Add(t.Pointer)) yield return t;
            }
        }

        private static void FillWallNames(Catalog catalog, AssetLoader loader)
        {
            try
            {
                foreach (var d in All<DoorPairPreset>(loader))
                    if (!string.IsNullOrEmpty(d.id) && !catalog.WallPresetNames.ContainsKey(d.id))
                        catalog.WallPresetNames[d.id] = d.name;
            }
            catch (Exception e) { Plugin.L.LogWarning($"Couldn't list wall presets: {e.Message}"); }
        }

        private static void FillLayoutRooms(Catalog catalog, AssetLoader loader)
        {
            try
            {
                foreach (var l in All<LayoutConfiguration>(loader))
                {
                    var name = string.IsNullOrEmpty(l.presetName) ? l.name : l.presetName;
                    if (string.IsNullOrEmpty(name) || catalog.LayoutRooms.ContainsKey(name)) continue;
                    var rooms = new List<string>();
                    if (l.roomLayout != null)
                        foreach (var r in l.roomLayout)
                            if (r != null) rooms.Add(string.IsNullOrEmpty(r.presetName) ? r.name : r.presetName);
                    catalog.LayoutRooms[name] = rooms;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"Couldn't list layout rooms: {e.Message}"); }
        }

        private static List<FloorLayoutDef> Layouts(Il2CppSystem.Collections.Generic.List<BuildingPreset.InteriorFloorSetting> list)
        {
            var result = new List<FloorLayoutDef>();
            if (list == null) return result;
            foreach (var s in list)
            {
                if (s == null) continue;
                var d = new FloorLayoutDef { Floors = s.floorsWithThisSetting };
                if (s.blueprints != null)
                    foreach (var t in s.blueprints)
                        if (t != null) d.Blueprints.Add(t.name);
                result.Add(d);
            }
            return result;
        }

        private static void FillPlaces(Catalog catalog)
        {
            try
            {
                var list = Toolbox.Instance?.allAddressPresets;
                if (list == null) return;
                foreach (var ap in list)
                {
                    if (ap == null) continue;
                    var p = new CatalogPlace
                    {
                        Name = string.IsNullOrEmpty(ap.presetName) ? ap.name : ap.presetName,
                        MinFloor = (int)ap.minMaxFloors.x, MaxFloor = (int)ap.minMaxFloors.y,
                        Company = ap.company != null ? ap.company.name : "",
                    };
                    if (ap.compatible != null) foreach (var c in ap.compatible) if (c != null) p.Fits.Add(c.name);
                    if (ap.limitToBuildings != null) foreach (var b in ap.limitToBuildings) if (b != null) p.OnlyIn.Add(b.presetName);
                    catalog.Places.Add(p);
                }
                catalog.Places.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            }
            catch (Exception e) { Plugin.L.LogWarning($"Couldn't list places: {e.Message}"); }
        }

        private static List<string> Names<T>(AssetLoader loader) where T : ScriptableObject
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            void Add(Il2CppObjectBase o)
            {
                var so = o?.TryCast<T>();
                if (so == null) return;
                var preset = so.TryCast<SoCustomComparison>();
                var n = preset != null && !string.IsNullOrEmpty(preset.presetName) ? preset.presetName : so.name;
                if (!string.IsNullOrEmpty(n)) set.Add(n);
            }
            if (loader.allPresets != null)
                foreach (var o in loader.allPresets) Add(o);
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>())) Add(o);
            return set.ToList();
        }

        private static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}

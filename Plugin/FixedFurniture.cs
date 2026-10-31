using System;
using System.Collections.Generic;
using System.Linq;
using CustomBuildings.Shared;
using HarmonyLib;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// Furniture placed by hand in the editor (BuildingDef.Furniture). A unit of a custom building (floor + address +
    /// variation) that has any gets exactly that furniture: while the game furnishes the unit
    /// (GenerationController.GenerateAddressDecor) it finds no spot for its own furniture there, and afterwards ours is
    /// put in, the way the game places furniture the player buys for their flat (NewRoom.AddFurnitureCustom). The city is
    /// saved with it like any other furniture, so loading needs nothing extra.
    /// </summary>
    internal static class FixedFurniture
    {
        /// <summary>The address the game is furnishing right now, if it has hand-placed furniture.</summary>
        internal static NewAddress Furnishing;
        internal static List<FurnitureItem> Items;

        /// <summary>Hand-placed furniture for this unit, or null.</summary>
        internal static List<FurnitureItem> For(NewAddress ad)
        {
            try
            {
                var floor = ad?.floor;
                var preset = floor?.building?.preset;
                if (preset == null || !ContentLoader.IsOurBuilding(preset.presetName)) return null;
                var def = ContentLoader.Definitions.FirstOrDefault(d => d.Id == preset.presetName);
                if (def?.Furniture == null || def.Furniture.Count == 0) return null;
                int index = Places.AddressIndex(ad);
                if (index < 1) return null;
                var items = def.Furniture.Where(f => f.Floor == floor.floorName && f.Address == index).ToList();
                if (items.Count == 0) return null;
                // the variation the game picked for this unit; items for other variations don't apply
                int v = 0;
                try { v = ad.loadedVarIndex; } catch { }
                var forVar = items.Where(f => f.Variation == Math.Max(0, v)).ToList();
                if (forVar.Count == 0)
                    Plugin.L.LogInfo($"[furniture] {preset.presetName} '{floor.floorName}' address {index}: the game picked layout variation {v + 1}; the placed furniture is for variation(s) {string.Join(", ", items.Select(f => f.Variation + 1).Distinct())}, so the game furnishes this one itself.");
                return forVar.Count > 0 ? forVar : null;
            }
            catch (Exception e) { Plugin.Verbose($"[furniture] {e.Message}"); return null; }
        }

        /// <summary>
        /// The editor counts 0 = facing down the floor plan (+y), turning clockwise as seen from above; the game turns
        /// the same way but 0 faces Unity's +Z, which is up the plan (rows count towards -Z). So the game's angle is the
        /// editor's + 180.
        /// </summary>
        internal static int GameAngle(int editorAngle) => (((editorAngle + 180) % 360) + 360) % 360;

        internal static void Place(NewAddress ad, List<FurnitureItem> items)
        {
            // every node of the unit by its floor-plan position
            var nodes = new Dictionary<(int, int), (NewNode node, NewRoom room)>();
            if (ad.rooms != null)
                foreach (var room in ad.rooms)
                {
                    if (room?.nodes == null) continue;
                    foreach (var n in room.nodes)
                        if (n != null) nodes[((int)n.floorCoord.x, (int)n.floorCoord.y)] = (n, room);
                }

            var tb = Toolbox.Instance;
            int placed = 0;
            var problems = new List<string>();
            foreach (var it in items)
            {
                FurniturePreset preset = null;
                try { tb.LoadDataFromResources<FurniturePreset>(it.Preset, out preset); } catch { }
                if (preset == null)
                    foreach (var f in AssetLoader.Instance.GetAllFurniture())
                        if (f != null && f.name == it.Preset) { preset = f; break; }
                if (preset == null)   // a mod's furniture that isn't in the game's lists
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<FurniturePreset>()))
                    {
                        var f = o?.TryCast<FurniturePreset>();
                        if (f != null && f.name == it.Preset) { preset = f; break; }
                    }
                if (preset == null) { problems.Add($"{it.Preset}: the game has no such furniture"); continue; }

                var cls = preset.classes != null && preset.classes.Count > 0 ? preset.classes[0] : null;
                int w = 1, d = 1;
                if (cls != null)
                {
                    w = Math.Max(1, (int)Math.Round((double)cls.objectSize.x));
                    d = Math.Max(1, (int)Math.Round((double)cls.objectSize.y));
                }
                var (fw, fd) = it.Footprint(w, d);
                if (!nodes.TryGetValue((it.X, it.Y), out var anchor)) { problems.Add($"{it.Preset} at {it.X},{it.Y}: not in this unit"); continue; }
                var covers = new Il2CppSystem.Collections.Generic.List<NewNode>();
                bool ok = true;
                for (int x = it.X; x < it.X + fw && ok; x++)
                    for (int y = it.Y; y < it.Y + fd && ok; y++)
                    {
                        if (nodes.TryGetValue((x, y), out var c) && c.room.Pointer == anchor.room.Pointer) covers.Add(c.node);
                        else ok = false;
                    }
                if (!ok) { problems.Add($"{it.Preset} at {it.X},{it.Y}: its {fw}×{fd} footprint goes outside its room"); continue; }

                try
                {
                    var p = new PlayerApartmentController.FurniturePlacement
                    {
                        preset = preset, anchorNode = anchor.node, coversNodes = covers, angle = GameAngle(it.Angle), offset = Vector3.zero,
                    };
                    var loc = anchor.room.AddFurnitureCustom(p);
                    if (loc != null) placed++;
                    else problems.Add($"{it.Preset} at {it.X},{it.Y}: the game didn't place it");
                }
                catch (Exception e) { problems.Add($"{it.Preset} at {it.X},{it.Y}: {e.GetType().Name}: {e.Message}"); }
            }
            Plugin.L.LogInfo($"[furniture] {ad.floor?.building?.preset?.presetName} '{ad.floor?.floorName}' address {Places.AddressIndex(ad)}: " +
                             $"{placed} of {items.Count} piece(s) placed{(problems.Count > 0 ? " – " + string.Join("; ", problems) : "")}");
        }
    }

    [HarmonyPatch(typeof(GenerationController), nameof(GenerationController.GenerateAddressDecor))]
    internal static class FixedFurnitureDecorPatch
    {
        private static void Prefix(NewAddress ad)
        {
            FixedFurniture.Furnishing = null;
            var items = FixedFurniture.For(ad);
            if (items == null) return;
            FixedFurniture.Furnishing = ad;
            FixedFurniture.Items = items;
        }

        private static void Postfix(NewAddress ad)
        {
            var mine = FixedFurniture.Furnishing;
            FixedFurniture.Furnishing = null;
            if (mine == null || ad == null || mine.Pointer != ad.Pointer) return;
            try { FixedFurniture.Place(ad, FixedFurniture.Items); }
            catch (Exception e) { Plugin.L.LogWarning($"[furniture] {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// <summary>While a unit with hand-placed furniture is furnished, the game finds no place for its own.</summary>
    [HarmonyPatch(typeof(GenerationController), nameof(GenerationController.GetBestFurnitureClusterLocation))]
    internal static class FixedFurnitureNoGamePieces
    {
        private static bool Prefix(NewRoom room, ref FurnitureClusterLocation __result)
        {
            var ad = FixedFurniture.Furnishing;
            if (ad == null || room?.gameLocation == null || room.gameLocation.Pointer != ad.Pointer) return true;
            __result = null;
            return false;
        }
    }
}

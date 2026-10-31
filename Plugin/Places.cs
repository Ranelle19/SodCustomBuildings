using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace CustomBuildings
{
    using CustomBuildings.Shared;

    /// <summary>
    /// Kinds of places (AddressPresets) in custom buildings:
    ///  - places the game limits to certain buildings (AddressPreset.limitToBuildings) also accept a custom building when
    ///    they accept its base building, so a TownhouseShops copy gets the same shops as TownhouseShops;
    ///  - reserved units (BuildingDef.ReservedPlaces): when the game picks what a unit becomes (NewAddress.AssignPurpose),
    ///    the reserved place is made the clear favourite for that one call, then everything is put back.
    /// Works with places other mods add at game start (e.g. NewLife's) as long as they are in Toolbox.allAddressPresets.
    /// </summary>
    internal static class Places
    {
        public static void ShareLimits(BuildingPreset basePreset, BuildingPreset clone)
        {
            try
            {
                var list = Toolbox.Instance?.allAddressPresets;
                if (list == null) return;
                int n = 0;
                foreach (var ap in list)
                {
                    var limit = ap?.limitToBuildings;
                    if (limit == null || limit.Count == 0) continue;
                    bool hasBase = false, hasClone = false;
                    foreach (var b in limit)
                    {
                        if (b == null) continue;
                        if (b.Pointer == basePreset.Pointer) hasBase = true;
                        if (b.Pointer == clone.Pointer || b.presetName == clone.presetName) hasClone = true;
                    }
                    if (hasBase && !hasClone) { limit.Add(clone); n++; }
                }
                if (n > 0) Plugin.L.LogInfo($"{clone.presetName}: {n} kind(s) of place limited to {basePreset.presetName} allowed here too.");
            }
            catch (Exception e) { Plugin.L.LogWarning($"{clone.presetName}: couldn't share place limits: {e.Message}"); }
        }

        internal static AddressPreset Find(string name) =>
            Toolbox.Instance?.allAddressPresets?.ToArray().FirstOrDefault(a => a != null && (a.presetName == name || a.name == name));

        /// <summary>The reservation for this address, if its building is a custom one and one matches.</summary>
        internal static ReservedPlace ReservationFor(NewAddress ad, bool log = true)
        {
            var floor = ad?.floor;
            var building = floor?.building;
            var preset = building?.preset;
            if (preset == null || !ContentLoader.IsOurBuilding(preset.presetName)) return null;
            var def = ContentLoader.Definitions.FirstOrDefault(d => d.Id == preset.presetName);
            if (def?.ReservedPlaces == null || def.ReservedPlaces.Count == 0) return null;
            int index = AddressIndex(ad, out var why);
            var r = def.ReservedPlaces.FirstOrDefault(x => x.Floor == floor.floorName && x.Address == index);
            // floors that have a reservation: say how each of their units was numbered (a few lines per building), so a
            // reservation that doesn't take can be traced
            if (log && def.ReservedPlaces.Any(x => x.Floor == floor.floorName))
                Plugin.L.LogInfo($"[places] {preset.presetName} storey {floor.floor} '{floor.floorName}': unit {ad.preset?.name} is address {(index > 0 ? index.ToString() : "? (" + why + ")")}" +
                                 (r != null ? $", reserved for {r.Place}" : ""));
            Plugin.Verbose($"[places] {preset.presetName} storey {floor.floor} '{floor.floorName}': address {index} ({ad.preset?.name}){(r != null ? " reserved for " + r.Place : "")}");
            return r;
        }

        /// <summary>
        /// Address number as in the floor file / editor (1-based): the position of this address's own data in its floor
        /// plan's address list. (editorID is numbered across the whole city, not per floor.) -1 if it can't be told.
        /// </summary>
        internal static int AddressIndex(NewAddress ad) => AddressIndex(ad, out _);

        internal static int AddressIndex(NewAddress ad, out string why)
        {
            why = "";
            try
            {
                var floor = ad.floor;
                FloorSaveData fsd = null;
                if (floor != null && CityData.Instance?.floorData != null)
                    CityData.Instance.floorData.TryGetValue(floor.floorName, out fsd);
                var list = fsd?.a_d;
                if (list == null) { why = $"floor plan '{floor?.floorName}' not found"; return -1; }

                // 1) the unit still knows its floor-plan entry
                var sd = ad.saveData;
                if (sd != null)
                {
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && list[i].Pointer == sd.Pointer) return i + 1;
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && list[i].p_n == sd.p_n && list[i].e_c == sd.e_c) return i + 1;
                }

                // 2) when the game picks what a unit becomes, it doesn't have that yet: the game makes a floor's units in
                //    the floor plan's order (editorID counts up), so the 2nd shop on the floor is the plan's 2nd shop.
                //    (It may add a lobby of its own, so lobbies can be off by one; shops, flats, offices are exact.)
                var type = ad.preset?.name;
                if (string.IsNullOrEmpty(type)) { why = "unit has no address type yet"; return -1; }
                int k = 0;
                if (floor.addresses != null)
                    foreach (var other in floor.addresses)
                        if (other != null && other.Pointer != ad.Pointer && other.preset != null && other.preset.name == type && other.editorID < ad.editorID) k++;
                int seen = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == null || list[i].p_n != type) continue;
                    if (seen == k) return i + 1;
                    seen++;
                }
                why = $"the floor plan has only {seen} address(es) of type {type}, this is number {k + 1}";
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
            return -1;
        }
    }

    [HarmonyPatch(typeof(NewAddress), nameof(NewAddress.AssignPurpose))]
    internal static class ReservedPlacePatch
    {
        private sealed class State
        {
            public AddressPreset Target;
            public int Score, Penalty;
            public Vector2 Floors;
            public List<BuildingPreset> Limit;
            public bool AddedLayout;
            public ReservedPlace Reservation;
        }

        private static void Prefix(NewAddress __instance, out State __state)
        {
            __state = null;
            try
            {
                var r = Places.ReservationFor(__instance);
                if (r == null) return;
                var target = Places.Find(r.Place);
                if (target == null)
                {
                    Plugin.L.LogWarning($"[places] '{r.Place}' (reserved for {r.Floor} address {r.Address}) isn't a place the game knows; the game picks as usual.");
                    return;
                }
                var st = new State
                {
                    Target = target, Reservation = r, Score = target.baseScore, Penalty = target.baseScoreFrequencyPenalty, Floors = target.minMaxFloors,
                    Limit = target.limitToBuildings?.ToArray().ToList(),
                };
                // make it fit here and the clear favourite, just for this one choice
                target.baseScore = 1000000;
                target.baseScoreFrequencyPenalty = 0;
                target.minMaxFloors = new Vector2(-100, 1000);
                target.limitToBuildings?.Clear();
                var layout = __instance.preset;
                if (layout != null && target.compatible != null && !target.compatible.Contains(layout)) { target.compatible.Add(layout); st.AddedLayout = true; }
                __state = st;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[places] {e.Message}"); }
        }

        private static void Postfix(NewAddress __instance, State __state)
        {
            if (__state == null) return;
            var t = __state.Target;
            try
            {
                t.baseScore = __state.Score;
                t.baseScoreFrequencyPenalty = __state.Penalty;
                t.minMaxFloors = __state.Floors;
                if (__state.Limit != null && t.limitToBuildings != null) foreach (var b in __state.Limit) t.limitToBuildings.Add(b);
                if (__state.AddedLayout) t.compatible.Remove(__instance.preset);
                var got = __instance.addressPreset;
                var r = __state.Reservation;
                if (got != null && got.Pointer == t.Pointer)
                    Plugin.L.LogInfo($"[places] {r.Floor} address {r.Address} is now {r.Place}.");
                else
                    Plugin.L.LogWarning($"[places] {r.Floor} address {r.Address} should be {r.Place}, but the game made it {got?.presetName ?? "nothing"}.");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[places] {e.Message}"); }
        }
    }
}

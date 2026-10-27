using System;
using System.Collections.Generic;
using System.Linq;
using CustomBuildings.Shared;
using HarmonyLib;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// Our own pieces of the city's edge (sea wall), e.g. "BoundaryCoastal03": custom buildings whose base is one of the
    /// game's edge pieces (see Shared/EdgePieces.cs for the whole picture). They are registered like every custom
    /// building (ContentLoader); this class
    ///  - checks and logs them when they are registered (and the game's own edge pieces' limits, for comparison),
    ///  - finds every copy the city generator placed once a city has loaded, and builds what belongs to it
    ///    (OnPlaced: so far a pier; add new kinds of things there).
    /// </summary>
    internal static class EdgePieces
    {
        /// <summary>Our edge pieces by preset name.</summary>
        internal static readonly Dictionary<string, BuildingDef> Ours = new(StringComparer.Ordinal);
        private static bool _loggedGame;

        internal static EdgeRole? RoleOf(NewBuilding b)
            => b?.preset != null && Ours.TryGetValue(b.preset.presetName, out var d) ? d.Edge?.Role : null;

        internal static void OnRegistered(BuildingDef def, BuildingPreset clone, BuildingPreset basePreset)
        {
            Ours[def.Id] = def;
            if (!basePreset.boundary)
                Plugin.L.LogWarning($"[edge] {def.Id}: its base '{basePreset.presetName}' isn't one of the game's edge pieces, so the city generator " +
                                    $"won't put it at the edge. Use {GameEdgePieces.Straight}, {GameEdgePieces.Platform} or {GameEdgePieces.Corner}.");
            Plugin.L.LogInfo($"[edge] {def.Id}: edge piece ({def.Edge.Role}) based on {basePreset.presetName}: boundary {clone.boundary}, " +
                             $"corner {clone.boundaryCorner}, minimum {clone.minimum}, hard limit {clone.hardLimit}" +
                             (def.Edge.Pier != null ? $", with pier '{def.Edge.Pier.Name}'" : ""));

            if (_loggedGame) return;
            _loggedGame = true;
            var loader = AssetLoader.Instance;
            if (loader?.allBuildingData == null) return;
            foreach (var p in loader.allBuildingData)
                if (p != null && p.boundary && !Ours.ContainsKey(p.presetName))
                    Plugin.L.LogInfo($"[edge]   game edge piece {p.presetName}: corner {p.boundaryCorner}, minimum {p.minimum}, hard limit {p.hardLimit}");
        }

        /// <summary>
        /// Called once per loaded city with all edge pieces in it. Logs which of ours the generator used and builds what
        /// belongs to each (buildPier builds a pier at a given piece).
        /// </summary>
        internal static void OnPlaced(List<NewBuilding> pieces, Func<PierDef, NewBuilding, GameObject> buildPier)
        {
            if (Ours.Count == 0) return;
            var placed = pieces.Where(p => p?.preset != null && Ours.ContainsKey(p.preset.presetName)).ToList();
            var counts = pieces.Where(p => p?.preset != null).GroupBy(p => p.preset.presetName).Select(g => $"{g.Key} ×{g.Count()}");
            Plugin.L.LogInfo($"[edge] this city's edge: {string.Join(", ", counts)}");
            if (placed.Count == 0)
            {
                Plugin.L.LogInfo($"[edge] none of our edge pieces ({string.Join(", ", Ours.Keys)}) is in this city. They only appear in cities " +
                                 "generated after they were added; if this one is new, the generator didn't pick them (check minimum / hard limit).");
                return;
            }
            foreach (var p in placed)
            {
                var def = Ours[p.preset.presetName];
                Plugin.L.LogInfo($"[edge] {def.Id} placed on tile {p.cityTile?.cityCoord}, facing {p.facing}, at ({p.transform.position.x:0.#}, {p.transform.position.z:0.#})");
                // ---- things that belong to this piece; add new kinds here
                if (def.Edge?.Model != null)
                {
                    // its own model: the whole piece, the pier (if any) is part of it
                    try { EdgeModel.Apply(p, def.Edge, ContentLoader.DirOf(def.Id)); }
                    catch (Exception e) { Plugin.L.LogWarning($"[edge] {def.Id}: own model: {e.GetType().Name}: {e.Message}"); }
                    continue;
                }
                // no model yet: the game's wall with the starter pier, and that written out as the starter model for Blender
                GameObject pier = null;
                if (def.Edge?.Pier != null && def.Edge.Pier.Enabled)
                {
                    try { pier = buildPier(def.Edge.Pier, p); }
                    catch (Exception e) { Plugin.L.LogWarning($"[edge] {def.Id}: pier: {e.GetType().Name}: {e.Message}"); }
                }
                try { EdgeStarterExport.Export(p, def.Edge, pier); }
                catch (Exception e) { Plugin.L.LogWarning($"[edge] {def.Id}: starter model export: {e.GetType().Name}: {e.Message}"); }
            }
        }
    }

    /// <summary>
    /// Decides which edge tiles of a new city get our wall pieces. The game ignores minimum / hard limit for edge pieces
    /// (it picks evenly among them: a test city got 5 BoundaryCoastal03 with hard limit 1), so we pick ourselves, in the
    /// moment the generator gives a tile its preset (NewBuilding.Setup):
    ///  - each of our pieces gets Minimum..HardLimit tiles, chosen from all straight edge tiles by a random order that only
    ///    depends on the city (same city = same choice);
    ///  - a chosen tile gets ours, whatever straight piece the generator gave it (BoundaryCoastal01 or 02: both fill a tile),
    ///    so wall pieces based on either work;
    ///  - when the generator picked one of ours for a tile that wasn't chosen, it gets one of the game's pieces instead,
    ///    chosen evenly, like the game would have.
    /// Corners are left alone. Loading a saved city doesn't go through here (the save has the pieces in it).
    /// </summary>
    [HarmonyPatch(typeof(NewBuilding), nameof(NewBuilding.Setup))]
    internal static class EdgePlacement
    {
        private static string _planFor;
        private static readonly Dictionary<(int, int), string> Plan = new();   // tile → our piece's Id
        private static System.Random _swapRng;

        private static void Prefix(CityTile newGroundmap, ref BuildingPreset newpreset, bool setupExistingBuilding)
        {
            try
            {
                if (setupExistingBuilding || newpreset == null || newGroundmap == null || !newpreset.boundary || newpreset.boundaryCorner) return;
                if (EdgePieces.Ours.Count == 0) return;
                var city = CityData.Instance;
                if (city == null) return;
                MakePlan(city);

                var c = newGroundmap.cityCoord;
                var name = newpreset.presetName;
                bool isOurs = EdgePieces.Ours.ContainsKey(name);
                if (Plan.TryGetValue((c.x, c.y), out var wantId))
                {
                    if (name == wantId) return;
                    // any straight edge piece can take ours (a normal wall or a platform: both fill one edge tile)
                    var preset = ContentLoader.FindOurPreset(wantId);
                    if (preset == null) return;
                    Plugin.L.LogInfo($"[edge] tile {c}: {wantId} instead of {name}");
                    newpreset = preset;
                    return;
                }
                if (!isOurs) return;
                // the generator picked one of ours on its own: give the tile one of the game's pieces instead
                var game = GamePieces();
                if (game.Count == 0) return;
                var pick = game[_swapRng.Next(game.Count)];
                Plugin.Verbose($"[edge] tile {c}: {pick.presetName} instead of {name} (not chosen for ours)");
                newpreset = pick;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[edge] placement: {e.GetType().Name}: {e.Message}"); }
        }

        private static List<BuildingPreset> GamePieces()
        {
            var list = new List<BuildingPreset>();
            var loader = AssetLoader.Instance;
            if (loader?.allBuildingData != null)
                foreach (var p in loader.allBuildingData)
                    if (p != null && p.boundary && !p.boundaryCorner && !EdgePieces.Ours.ContainsKey(p.presetName)) list.Add(p);
            return list;
        }

        private static void MakePlan(CityData city)
        {
            string key = $"{city.cityName}|{city.citySize}";
            if (_planFor == key) return;
            _planFor = key;
            Plan.Clear();
            int seed = StableHash(key);
            _swapRng = new System.Random(seed ^ 0x5EA);

            int w = Math.Max(0, (int)Math.Round(city.citySize.x)), h = Math.Max(0, (int)Math.Round(city.citySize.y));
            var tiles = new List<(int, int)>();
            for (int x = 1; x < w - 1; x++) { tiles.Add((x, 0)); tiles.Add((x, h - 1)); }
            for (int y = 1; y < h - 1; y++) { tiles.Add((0, y)); tiles.Add((w - 1, y)); }
            var rng = new System.Random(seed);
            var order = tiles.OrderBy(t => rng.Next()).ToList();

            int at = 0;
            var notes = new List<string>();
            foreach (var def in EdgePieces.Ours.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                var e = def.Edge;
                if (e == null || e.Role == EdgeRole.Corner) continue;
                int min = Math.Max(0, e.Minimum), max = e.HardLimit > 0 ? Math.Max(min, e.HardLimit) : min;
                int count = min + (max > min ? rng.Next(max - min + 1) : 0);
                var got = new List<string>();
                for (int i = 0; i < count && at < order.Count; i++, at++) { Plan[order[at]] = def.Id; got.Add($"({order[at].Item1}, {order[at].Item2})"); }
                notes.Add($"{def.Id} ×{got.Count} on {(got.Count == 0 ? "no tile" : string.Join(" ", got))}");
            }
            Plugin.L.LogInfo($"[edge] plan for '{city.cityName}' ({w}×{h} tiles, {tiles.Count} straight edge tiles): {string.Join("; ", notes)}");
        }

        /// <summary>Same number for the same text on every computer (string.GetHashCode isn't).</summary>
        private static int StableHash(string s)
        {
            unchecked
            {
                int h = (int)2166136261;
                foreach (var ch in s) h = (h ^ ch) * 16777619;
                return h & 0x7FFFFFFF;
            }
        }
    }
}

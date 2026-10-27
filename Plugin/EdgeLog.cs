using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// Finds out how the edge of the city (the platform's sides down to the sea) is built, for a pier at water level.
    /// Writes an [edge] report to the log once a city is loaded:
    ///   - city size and boundary, tile size
    ///   - the sea: its renderer, height, material, colliders, the basement water level
    ///   - every building preset marked as boundary / boundary corner
    ///   - every edge piece placed in this city: preset, tile, side, rotation, position, height range, colliders
    /// Ctrl+Shift+9 in game: writes where you stand and what is below you and in front of you (stand at the edge and
    /// look out to sea), so we know the street, edge and water heights there.
    /// </summary>
    [HarmonyPatch(typeof(SessionData), nameof(SessionData.Update))]
    internal static class EdgeLog
    {
        private static int _frames;
        private static bool _done;
        private static string _cityDone;

        private static void Postfix()
        {
            try
            {
                if (Plugin.EdgeReport != null && !Plugin.EdgeReport.Value) return;
                if (Input.GetKeyDown(KeyCode.Alpha9) && Held(KeyCode.LeftControl, KeyCode.RightControl) && Held(KeyCode.LeftShift, KeyCode.RightShift))
                    Probe();

                if (++_frames % 300 != 0) return;   // look every ~5 s
                var city = CityData.Instance;
                if (city == null || !PierBuilder.CityReady()) return;
                string key = city.cityName + "|" + city.citySize;
                if (_done && key == _cityDone) return;
                var pieces = EdgePieces();
                if (pieces.Count == 0) return;   // city not built yet
                _done = true;
                _cityDone = key;
                Report(pieces);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[edge] {e.GetType().Name}: {e.Message}"); }
        }

        private static bool Held(KeyCode a, KeyCode b) => Input.GetKey(a) || Input.GetKey(b);

        internal static List<NewBuilding> EdgePieces()
        {
            var list = new List<NewBuilding>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<NewBuilding>()))
            {
                var b = o?.TryCast<NewBuilding>();
                if (b == null) continue;
                bool edge = b.preset != null && (b.preset.boundary || b.preset.boundaryCorner);
                if (edge) list.Add(b);
            }
            return list;
        }

        private static string V(Vector3 v) => $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##})";

        private static void Report(List<NewBuilding> pieces)
        {
            var sb = new StringBuilder();
            void Line(string s) => sb.Append("[edge] ").AppendLine(s);

            var city = CityData.Instance;
            Line($"===== city edge report (plugin {Plugin.Version}) =====");
            Line($"city '{city.cityName}' size {city.citySize} boundary L {city.boundaryLeft:0.##} R {city.boundaryRight:0.##} U {city.boundaryUp:0.##} D {city.boundaryDown:0.##}");

            var cc = CityControls.Instance;
            if (cc != null)
            {
                Line($"tile size {V(cc.cityTileSize)} multiplier {cc.tileMultiplier}  basementWaterLevel {cc.basementWaterLevel:0.###}");
                if (cc.basementWaterTransform != null) Line($"basement water at {V(cc.basementWaterTransform.position)}");
                if (cc.ships1 != null) Line($"ships at {V(cc.ships1.position)}");
                if (cc.seaMaterial != null) Line($"sea material {cc.seaMaterial.name} shader {cc.seaMaterial.shader?.name}");
                var sea = cc.seaRenderer;
                if (sea != null)
                {
                    var b = sea.bounds;
                    Line($"sea renderer '{sea.gameObject.name}' at {V(sea.transform.position)} scale {V(sea.transform.lossyScale)} bounds {V(b.min)} – {V(b.max)} " +
                         $"layer {sea.gameObject.layer} material {sea.sharedMaterial?.name}");
                }
            }

            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SeaCollider>()))
            {
                var s = o?.TryCast<SeaCollider>();
                if (s == null) continue;
                var cols = s.GetComponents<Collider>();
                string c = string.Join(", ", cols.Select(x => $"{x.GetIl2CppType().Name} {V(x.bounds.min)}–{V(x.bounds.max)} trigger {x.isTrigger}"));
                Line($"SeaCollider '{s.gameObject.name}' at {V(s.transform.position)} layer {s.gameObject.layer}: {c}");
            }

            // the presets the game can use for the edge
            var loader = AssetLoader.Instance;
            if (loader?.allBuildingData != null)
                foreach (var p in loader.allBuildingData)
                    if (p != null && (p.boundary || p.boundaryCorner))
                        Line($"preset {p.presetName}: boundary {p.boundary} corner {p.boundaryCorner} prefab {p.prefab?.name} nonEnterable {p.nonEnterable} floors {p.floorCount}");

            // the tiles
            var tiles = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<CityTile>()).Select(o => o?.TryCast<CityTile>()).Where(t => t != null).ToList();
            if (tiles.Count > 0)
            {
                int minX = tiles.Min(t => t.cityCoord.x), maxX = tiles.Max(t => t.cityCoord.x);
                int minY = tiles.Min(t => t.cityCoord.y), maxY = tiles.Max(t => t.cityCoord.y);
                Line($"{tiles.Count} city tiles, x {minX}..{maxX}, y {minY}..{maxY}");
                var first = tiles.FirstOrDefault(t => t.cityCoord.x == minX + 2 && t.cityCoord.y == minY + 2) ?? tiles[0];
                Line($"tile {first.cityCoord} at {V(first.transform.position)}");
            }

            // a normal building for the street height
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<NewBuilding>()))
            {
                var b = o?.TryCast<NewBuilding>();
                if (b == null || b.preset == null || b.preset.boundary || b.preset.boundaryCorner) continue;
                Line($"normal building {b.preset.presetName} worldPosition {V(b.worldPosition)} transform {V(b.transform.position)} tile {b.cityTile?.cityCoord}");
                break;
            }

            // the pieces
            Line($"{pieces.Count} edge pieces:");
            var count = new Dictionary<string, int>();
            foreach (var b in pieces.OrderBy(p => p.cityTile != null ? p.cityTile.cityCoord.y : 0).ThenBy(p => p.cityTile != null ? p.cityTile.cityCoord.x : 0))
            {
                string name = b.preset?.presetName ?? "?";
                count[name] = count.TryGetValue(name, out var n) ? n + 1 : 1;
                var rs = b.GetComponentsInChildren<Renderer>(true);
                string range = "no renderers";
                if (rs.Length > 0)
                {
                    var bb = rs[0].bounds;
                    foreach (var r in rs) bb.Encapsulate(r.bounds);
                    range = $"bounds {V(bb.min)} – {V(bb.max)}";
                }
                var cols = b.GetComponentsInChildren<Collider>(true);
                string colInfo = cols.Length == 0 ? "no colliders" : $"{cols.Length} colliders ({string.Join(",", cols.Select(c => c.GetIl2CppType().Name).Distinct())})";
                Line($"  {name} tile {b.cityTile?.cityCoord} global {b.globalTileCoords} facing {b.facing} rotations {b.rotations} " +
                     $"corner {b.preset?.boundaryCorner} pos {V(b.transform.position)} rotY {b.transform.eulerAngles.y:0} {range}; {colInfo}; " +
                     $"model {b.buildingModelBase?.name}");
            }
            Line("counts: " + string.Join(", ", count.Select(kv => $"{kv.Key} ×{kv.Value}")));

            var bnd = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<CityBoundaryAndTiles>())?.TryCast<CityBoundaryAndTiles>();
            if (bnd != null)
                Line($"CityBoundaryAndTiles: L {bnd.boundaryLeft:0.##} R {bnd.boundaryRight:0.##} U {bnd.boundaryUp:0.##} D {bnd.boundaryDown:0.##} " +
                     $"tiles {bnd.cityTiles?.Count} tile prefab {bnd.cityTilePrefab?.name}");
            Line("===== end of city edge report (Ctrl+Shift+9 at the edge adds more) =====");
            Plugin.L.LogInfo("\n" + sb);
        }

        /// <summary>Ctrl+Shift+9: where the player is, and what's below them and below points in front of them.</summary>
        private static void Probe()
        {
            var cam = Camera.main;
            if (cam == null) { Plugin.L.LogInfo("[edge] probe: no camera"); return; }
            var sb = new StringBuilder();
            var pos = cam.transform.position;
            var fwd = cam.transform.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();
            sb.AppendLine($"[edge] probe: camera at {V(pos)} looking {V(fwd)}");

            var city = CityData.Instance;
            if (city != null)
                sb.AppendLine($"[edge]   distance to boundary: L {pos.x - city.boundaryLeft:0.#} R {city.boundaryRight - pos.x:0.#} D {pos.z - city.boundaryDown:0.#} U {city.boundaryUp - pos.z:0.#}");

            foreach (var d in new[] { 0f, 3f, 6f, 10f, 15f, 20f, 30f, 45f, 60f })
            {
                var from = pos + fwd * d + Vector3.up * 30f;
                var hits = Physics.RaycastAll(from, Vector3.down, 300f, ~0, QueryTriggerInteraction.Collide);
                var parts = hits.OrderBy(h => h.distance).Take(4)
                                .Select(h => $"{h.point.y:0.##} '{h.collider.gameObject.name}'{(h.collider.isTrigger ? " (trigger)" : "")} layer {h.collider.gameObject.layer}");
                sb.AppendLine($"[edge]   {d,2:0} m ahead: {(hits.Length == 0 ? "nothing below" : string.Join(" | ", parts))}");
            }

            // nearest edge piece
            NewBuilding best = null; float bestD = float.MaxValue;
            foreach (var b in EdgePieces())
            {
                float dd = (b.transform.position - pos).sqrMagnitude;
                if (dd < bestD) { bestD = dd; best = b; }
            }
            if (best != null)
                sb.AppendLine($"[edge]   nearest edge piece {best.preset?.presetName} tile {best.cityTile?.cityCoord} at {V(best.transform.position)} rotY {best.transform.eulerAngles.y:0}, {Mathf.Sqrt(bestD):0.#} m away");

            var cc = CityControls.Instance;
            if (cc?.seaRenderer != null) sb.AppendLine($"[edge]   sea surface top {cc.seaRenderer.bounds.max.y:0.##}");
            if (cc != null) sb.AppendLine($"[edge]   basement water level {cc.basementWaterLevel:0.##} (you are {pos.y - 1.6f - cc.basementWaterLevel:+0.##;-0.##} m above it)");
            try
            {
                var pl = Player.Instance;
                if (pl != null)
                    sb.AppendLine($"[edge]   player feet {pl.transform.position.y:0.##}, speed {pl.currentMovementSpeed:0.##}, walk {pl.desiredWalkSpeed:0.##}, run {pl.desiredRunSpeed:0.##}");
                var st = StatusController.Instance;
                if (st != null)
                {
                    var names = new List<string>();
                    if (st.activeStatuses != null) foreach (var x in st.activeStatuses) if (x != null) names.Add(x.name);
                    sb.AppendLine($"[edge]   movement speed x{st.movementSpeedMultiplier:0.##}, statuses: {(names.Count == 0 ? "none" : string.Join(", ", names))}");
                }
            }
            catch (Exception e) { sb.AppendLine($"[edge]   (player info: {e.Message})"); }
            Plugin.L.LogInfo(sb.ToString().TrimEnd());
        }
    }
}

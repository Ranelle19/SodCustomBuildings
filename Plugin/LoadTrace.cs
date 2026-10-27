using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace CustomBuildings
{
    /// <summary>
    /// Finding where a city load gets stuck: every building the game sets up while a city is made or loaded is noted
    /// ([load] lines: which preset, which tile, how long it took), and while a load runs a line every ~5 s says how far
    /// it got. A building that starts and never finishes is the one the load is stuck on.
    /// </summary>
    [HarmonyPatch(typeof(NewBuilding), nameof(NewBuilding.Setup))]
    internal static class LoadTrace
    {
        internal static int Count;
        internal static string Last = "none yet";
        private static readonly Stopwatch Watch = new();

        [HarmonyPriority(Priority.First)]
        private static void Prefix(CityTile newGroundmap, ref BuildingPreset newpreset, bool setupExistingBuilding)
        {
            try
            {
                Count++;
                string tile = newGroundmap != null ? $"({newGroundmap.cityCoord.x}, {newGroundmap.cityCoord.y})" : "?";
                bool ours = newpreset != null && ContentLoader.IsOurBuilding(newpreset.presetName);
                Last = $"#{Count} {newpreset?.presetName ?? "?"} on tile {tile}{(setupExistingBuilding ? " (from the save)" : "")}{(ours ? " [ours]" : "")}";
                if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value) Plugin.L.LogInfo($"[load] start {Last}");
                Watch.Restart();
            }
            catch (Exception e) { Plugin.Verbose($"[load] {e.Message}"); }
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            try
            {
                if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value) Plugin.L.LogInfo($"[load] done  {Last} in {Watch.ElapsedMilliseconds} ms");
            }
            catch { }
        }
    }

    /// <summary>The "how far did it get" line while a load runs.</summary>
    [HarmonyPatch(typeof(SessionData), nameof(SessionData.Update))]
    internal static class LoadHeartbeat
    {
        private static readonly Stopwatch Since = Stopwatch.StartNew();
        private static int _lastCount = -1, _lastLoads = -1;
        private static readonly Dictionary<string, string> Seen = new();
        private static PropertyInfo[] _props;

        /// <summary>The loader's simple values (step, counters, flags) that changed since the last line.</summary>
        private static string State(CityConstructor cc)
        {
            _props ??= typeof(CityConstructor).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 &&
                            (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string)))
                .ToArray();
            var sb = new StringBuilder();
            foreach (var p in _props)
            {
                string v;
                try { v = p.GetValue(cc)?.ToString() ?? "null"; } catch { continue; }
                if (Seen.TryGetValue(p.Name, out var old) && old == v) continue;
                Seen[p.Name] = v;
                sb.Append(p.Name).Append('=').Append(v).Append("  ");
            }
            return sb.ToString();
        }

        private static bool _cachedForLoad;

        private static void Postfix()
        {
            try
            {
                // a load is starting: make sure the game can find our presets by name (cheap check every frame)
                if (!_cachedForLoad)
                {
                    var c = CityConstructor.Instance;
                    bool active = false;
                    try { active = c != null && c.loadingOperationActive; } catch { }
                    if (active) { _cachedForLoad = true; ContentLoader.EnsureAllCached("load start"); }
                }
                else if (CityConstructor.Instance is { } done)
                {
                    bool idle = false;
                    try { idle = done.isLoaded && !done.loadingOperationActive; } catch { }
                    if (idle) _cachedForLoad = false;   // ready for the next load
                }
                if (!(Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value)) return;   // the rest is a debugging aid
                if (Since.ElapsedMilliseconds < 5000) return;
                Since.Restart();
                var cc = CityConstructor.Instance;
                if (cc == null) return;
                bool loading = false, loaded = false;
                try { loading = cc.loadingOperationActive; loaded = cc.isLoaded; } catch { }
                if (!loading && loaded) { _lastCount = -1; return; }
                bool moved = LoadTrace.Count != _lastCount;
                _lastCount = LoadTrace.Count;
                Plugin.L.LogInfo($"[load] still loading (loading {loading}, loaded {loaded}): {LoadTrace.Count} building(s) set up, {BuildingLoadTrace.Count} building load call(s), last {BuildingLoadTrace.Last}{(moved || BuildingLoadTrace.Count != _lastLoads ? "" : " – nothing new in the last 5 s")}");
                _lastLoads = BuildingLoadTrace.Count;
                var changed = State(cc);
                if (changed.Length > 0) Plugin.L.LogInfo($"[load] loader state: {changed}");
            }
            catch { }
        }
    }
}

namespace CustomBuildings
{
    /// <summary>
    /// A saved city's buildings don't go through NewBuilding.Setup but through its Load… methods: note those too, so the
    /// building a stuck load stops at shows up.
    /// </summary>
    [HarmonyPatch]
    internal static class BuildingLoadTrace
    {
        internal static int Count;
        internal static string Last = "none yet";
        private static readonly Stopwatch Watch = new();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(NewBuilding).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.StartsWith("Load", StringComparison.Ordinal) && !m.IsGenericMethod && !m.IsSpecialName
                            && m.GetParameters().All(p => !p.ParameterType.IsByRef));
        }

        private static string Who(NewBuilding b)
        {
            try
            {
                var t = b.cityTile;
                return $"{b.preset?.presetName ?? "?"} on tile {(t != null ? $"({t.cityCoord.x}, {t.cityCoord.y})" : "?")}";
            }
            catch { return "?"; }
        }

        private static void Prefix(NewBuilding __instance, MethodBase __originalMethod, object[] __args)
        {
            try
            {
                Count++;
                string saved = "";
                if (__originalMethod.Name == "Load" && __args != null && __args.Length > 0 && __args[0] != null)
                {
                    try
                    {
                        var name = __args[0].GetType().GetProperty("preset")?.GetValue(__args[0]) as string;
                        if (name != null)
                        {
                            var tb = Toolbox.Instance;
                            bool found = tb != null && ContentLoader.Find(tb, name) != null;
                            saved = $"(saved as {name}{(ContentLoader.IsOurBuilding(name) ? ", ours" : "")}{(found ? "" : ", NOT FOUND in the game's preset cache")}) ";
                            if (!found)
                                Plugin.L.LogWarning($"[load] the save has a building '{name}' that this game doesn't have{(ContentLoader.IsOurBuilding(name) ? " (one of ours, but the game can't find it by name)" : " (from a mod that isn't installed now?)")}. Loading this city will probably fail.");
                        }
                    }
                    catch { }
                }
                Last = $"#{Count} NewBuilding.{__originalMethod.Name} {saved}{Who(__instance)}";
                if (Count <= 600 && Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value) Plugin.L.LogInfo($"[load] start {Last}");
                Watch.Restart();
            }
            catch { }
        }

        private static void Postfix(NewBuilding __instance, MethodBase __originalMethod)
        {
            try { if (Count <= 600 && Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value) Plugin.L.LogInfo($"[load] done  NewBuilding.{__originalMethod.Name} {Who(__instance)} in {Watch.ElapsedMilliseconds} ms"); }
            catch { }
        }
    }
}

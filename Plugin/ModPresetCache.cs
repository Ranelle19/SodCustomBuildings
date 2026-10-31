using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// The game finds everything a saved city names (building types, room types, places, furniture, objects…) in its
    /// preset cache, Toolbox.resourcesCache, which it fills once at start-up. Presets that mods make later (ours, NewLife's
    /// animal shelter and pet furniture, SOD API's items…) work in a new city but are missing when the city is loaded
    /// again, and the load breaks (NewRoom.SetConfiguration / AddFurniture: NullReferenceException).
    /// Before every load this goes through each kind of preset the cache holds, finds the ones of that kind that exist
    /// in the game but aren't in the cache, and adds them, by name, to the cache only (nothing else is changed, so they
    /// don't start appearing anywhere new).
    /// </summary>
    internal static class ModPresetCache
    {
        internal static void FillAll(string from)
        {
            var tb = Toolbox.Instance;
            if (tb == null) return;
            try
            {
                var cache = tb.resourcesCache;
                if (cache == null) return;
                var added = new List<string>();
                int total = 0;
                foreach (var kv in cache)
                {
                    var type = kv.Key;
                    var inner = kv.Value;
                    if (type == null || inner == null) continue;
                    var names = new List<string>();
                    try
                    {
                        foreach (var o in Resources.FindObjectsOfTypeAll(type))
                        {
                            if (o == null) continue;
                            string name = o.name;
                            if (string.IsNullOrEmpty(name) || name.EndsWith("(Clone)", StringComparison.Ordinal)) continue;
                            if (inner.ContainsKey(name)) continue;
                            var so = o.TryCast<ScriptableObject>();
                            if (so == null) continue;
                            inner.Add(name, so);
                            names.Add(name);
                        }
                    }
                    catch (Exception e) { Plugin.Verbose($"[{from}] preset cache, {type.Name}: {e.Message}"); }
                    if (names.Count == 0) continue;
                    total += names.Count;
                    added.Add($"{type.Name} ×{names.Count} ({string.Join(", ", names.Take(8))}{(names.Count > 8 ? ", …" : "")})");
                }
                if (total > 0)
                    Plugin.L.LogInfo($"[{from}] put {total} preset(s) made by mods into the game's preset cache, so saved cities can find them: {string.Join("; ", added)}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[{from}] couldn't fill the game's preset cache: {e.GetType().Name}: {e.Message}"); }
        }
    }
}

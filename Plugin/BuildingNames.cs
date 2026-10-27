using System;
using System.Collections.Generic;

namespace CustomBuildings
{
    using CustomBuildings.Shared;

    /// <summary>
    /// Gives custom buildings a name in the game's language tables. The game shows a building type by looking up a text
    /// keyed by its preset name. We don't rely on knowing which table that is: wherever the base building has an entry
    /// (exact key, or a key that contains the base's name as a whole part, like "building_townhouseshops"), the same entry
    /// is added for our building with our name, in the same letter case. Existing entries are never overwritten.
    /// </summary>
    internal static class BuildingNames
    {
        private static readonly HashSet<string> Logged = new();

        public static void Apply(string from)
        {
            try
            {
                if (Strings.stringTable == null) return;   // texts not loaded yet
                int added = 0;
                foreach (var def in ContentLoader.Definitions)
                {
                    if (string.IsNullOrEmpty(def.BasePreset) || string.IsNullOrEmpty(def.Id)) continue;
                    int n = AddTo(Strings.stringTable, def) + AddTo(Strings.stringTableENG, def);
                    added += n;
                    if (n > 0 && Logged.Add(def.Id + "|ok"))
                        Plugin.L.LogInfo($"[{from}] Named {def.Id} \"{def.GameName()}\" ({n} text entr{(n == 1 ? "y" : "ies")}).");
                    else if (n == 0 && !HasName(Strings.stringTable, def) && Strings.textFilesLoaded && Logged.Add(def.Id + "|none"))
                        Plugin.L.LogWarning($"[{from}] Couldn't find a name entry for '{def.BasePreset}' to copy, so {def.Id} may show without a name. " +
                                            "Turn on VerboseLogging and send the log.");
                }
                if (added > 0) Plugin.Verbose($"[{from}] Added {added} name entr{(added == 1 ? "y" : "ies")}.");
            }
            catch (Exception e)
            {
                if (Logged.Add("error")) Plugin.L.LogWarning($"Couldn't add building names: {e.Message}");
            }
        }

        private static bool HasName(Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.Dictionary<string, Strings.DisplayString>> table, BuildingDef def)
        {
            if (table == null) return false;
            foreach (var dict in table)
                foreach (var entry in dict.Value)
                    if (MatchKey(entry.Key, def.Id) != null) return true;
            return false;
        }

        private static int AddTo(Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.Dictionary<string, Strings.DisplayString>> table, BuildingDef def)
        {
            if (table == null) return 0;
            int added = 0;
            var name = def.GameName();
            foreach (var dict in table)
            {
                var inner = dict.Value;
                if (inner == null) continue;
                var toAdd = new List<string>();
                foreach (var entry in inner)
                {
                    var ours = MatchKey(entry.Key, def.BasePreset) != null ? Replace(entry.Key, def.BasePreset, def.Id) : null;
                    if (ours != null && !inner.ContainsKey(ours)) toAdd.Add(ours);
                }
                foreach (var key in toAdd)
                {
                    var ds = new Strings.DisplayString { displayStr = name, alternateStr = name };
                    inner[key] = ds;
                    added++;
                    Plugin.Verbose($"  name: [{dict.Key}] {key} = {name}");
                }
            }
            return added;
        }

        /// <summary>The key if it is the preset name, or contains it as a whole part separated by _ . - or space.</summary>
        private static string MatchKey(string key, string preset)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(preset)) return null;
            int i = key.IndexOf(preset, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            bool startOk = i == 0 || IsSep(key[i - 1]);
            int end = i + preset.Length;
            bool endOk = end == key.Length || IsSep(key[end]);
            return startOk && endOk ? key : null;
        }

        private static bool IsSep(char c) => c == '_' || c == '.' || c == '-' || c == ' ';

        /// <summary>Puts our id where the base's name was, in the same letter case (all lower, all upper, or as written).</summary>
        private static string Replace(string key, string basePreset, string id)
        {
            int i = key.IndexOf(basePreset, StringComparison.OrdinalIgnoreCase);
            var found = key.Substring(i, basePreset.Length);
            string ours = found == found.ToLowerInvariant() ? id.ToLowerInvariant()
                        : found == found.ToUpperInvariant() ? id.ToUpperInvariant()
                        : id;
            return key.Substring(0, i) + ours + key.Substring(i + basePreset.Length);
        }
    }
}

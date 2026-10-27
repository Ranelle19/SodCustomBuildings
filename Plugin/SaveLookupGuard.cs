using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// A saved city names its rooms' types (RoomConfiguration) and the game finds
    /// them by name in its preset cache (Toolbox.resourcesCache), which it fills once at start-up. Anything a mod adds
    /// later (our buildings, other mods' places and rooms) isn't in it, and loading such a city then fails with a
    /// NullReferenceException (NewRoom.SetConfiguration) and never finishes. Before each room is loaded this
    /// checks the name; if the cache doesn't have it but the game does (made by a mod), it is put in the cache. If the
    /// game doesn't have it at all (a mod that isn't installed now), the log says so.
    /// </summary>
    internal static class SaveLookupGuard
    {
        private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);   // looked for already, not there

        internal static void Check<T>(object saveData, string what, Func<Toolbox, string, bool> inCache) where T : ScriptableObject
        {
            try
            {
                if (saveData == null) return;
                var name = saveData.GetType().GetProperty("preset")?.GetValue(saveData) as string;
                var tb = Toolbox.Instance;
                if (tb == null) return;
                if (string.IsNullOrEmpty(name))
                {
                    if (typeof(T) == typeof(RoomConfiguration) && Reported.Add(what + ":<empty>"))
                        Plugin.L.LogWarning($"[load] the save has a {what} with no type name; loading this city will probably fail.");
                    return;
                }
                if (Missing.Contains(what + ":" + name) || inCache(tb, name)) return;

                T found = null;
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
                {
                    var t = o?.TryCast<T>();
                    if (t != null && t.name == name) { found = t; break; }
                }
                if (found != null)
                {
                    tb.ProcessLoadedScriptableObject(found);
                    bool ok = inCache(tb, name);
                    if (!ok) Missing.Add(what + ":" + name);   // don't search again for every room
                    if (Reported.Add(what + ":" + name))
                        Plugin.L.LogInfo($"[load] {what} '{name}' (added by a mod) wasn't in the game's preset cache; put it in{(ok ? "" : " – but the game still can't find it")}.");
                }
                else if (Missing.Add(what + ":" + name))
                    Plugin.L.LogWarning($"[load] the save has a {what} '{name}' that the game can't find (made by another mod that isn't installed, or that doesn't register it with the game). Loading this city will probably fail; that mod has to put it in Toolbox's preset cache (Toolbox.ProcessLoadedScriptableObject).");
            }
            catch (Exception e) { Plugin.Verbose($"[load] {what} check: {e.GetType().Name}: {e.Message}"); }
        }

        internal static bool HasRoom(Toolbox tb, string name)
        {
            try { return tb.LoadDataFromResources<RoomConfiguration>(name, out var r) && r != null; } catch { return true; }
        }

    }

    [HarmonyPatch(typeof(NewRoom), nameof(NewRoom.Load))]
    internal static class RoomLoadGuard
    {
        private static void Prefix(object[] __args)
        {
            if (__args != null && __args.Length > 0)
                SaveLookupGuard.Check<RoomConfiguration>(__args[0], "room type", SaveLookupGuard.HasRoom);
        }
    }
}

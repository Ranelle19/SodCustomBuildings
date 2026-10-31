using System;
using System.Linq;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// For other mods. Everything here is safe to call at any time and never throws. See MODDING.md.
    ///
    /// Use it as a soft dependency so your mod also runs without CustomBuildings:
    ///   [BepInDependency("Ranelle.sod.custombuildings", BepInDependency.DependencyFlags.SoftDependency)]
    /// and only call into this class when IL2CPPChainloader.Instance.Plugins contains that id.
    /// </summary>
    public static class Api
    {
        /// <summary>Version of this API. Goes up when something is added; nothing is removed.</summary>
        public const int ApiVersion = 1;

        /// <summary>
        /// Puts a preset you made (an AddressPreset, RoomConfiguration, FurniturePreset, FurnitureCluster,
        /// InteractablePreset, …) into the game's preset cache, so a saved city that uses it can be loaded again.
        /// The same as Toolbox.Instance.ProcessLoadedScriptableObject(preset), but it checks first and logs what it did.
        /// Returns true if the game can find it by name afterwards.
        /// CustomBuildings also does this for every mod's presets before each city load, but doing it yourself right
        /// after you make a preset is the safe way (and works without CustomBuildings).
        /// </summary>
        public static bool RegisterWithGame(ScriptableObject preset)
        {
            try
            {
                var tb = Toolbox.Instance;
                if (tb == null || preset == null) return false;
                tb.ProcessLoadedScriptableObject(preset);
                Plugin.Verbose($"[api] {preset.name} ({preset.GetIl2CppType().Name}) registered with the game's preset cache");
                return true;
            }
            catch (Exception e) { Plugin.L.LogWarning($"[api] RegisterWithGame: {e.Message}"); return false; }
        }

        /// <summary>True if this building type is one made with CustomBuildings (a custom building or wall piece).</summary>
        public static bool IsCustomBuilding(BuildingPreset preset)
        {
            try { return preset != null && ContentLoader.IsOurBuilding(preset.presetName); } catch { return false; }
        }

        /// <summary>
        /// The kind of place (AddressPreset name) a custom building reserves for this unit, or null if none.
        /// Works while the game picks what the unit becomes (NewAddress.AssignPurpose) and afterwards.
        /// </summary>
        public static string GetReservedPlace(NewAddress address)
        {
            try { return Places.ReservationFor(address, log: false)?.Place; } catch { return null; }
        }

        /// <summary>
        /// True if a custom building reserves this unit for exactly this kind of place. Use it if your place should only
        /// appear where a building asks for it ("only for reserved units").
        /// </summary>
        public static bool IsReservedFor(NewAddress address, string placeName)
        {
            var r = GetReservedPlace(address);
            return r != null && string.Equals(r, placeName, StringComparison.Ordinal);
        }

        /// <summary>
        /// All reservations of all custom buildings for this kind of place, as "BuildingId / floor / address".
        /// Handy for a log line like "my place is used by 2 custom building(s)".
        /// </summary>
        public static string[] BuildingsReserving(string placeName)
        {
            try
            {
                return ContentLoader.Definitions
                    .SelectMany(d => (d.ReservedPlaces ?? new()).Where(r => r.Place == placeName).Select(r => $"{d.Id} / {r.Floor} / {r.Address}"))
                    .ToArray();
            }
            catch { return Array.Empty<string>(); }
        }
    }
}

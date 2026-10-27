using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// Keeps custom exteriors matt. Something in the game puts shine back on them after they are placed (the walls then
    /// look white-hot), so every few seconds this finds their materials and sets the matt values again, and logs what it
    /// found the first times (so we can see what changed them). Ctrl+Shift+1..5 (LookTest) pauses it while testing.
    /// </summary>
    [HarmonyPatch(typeof(SessionData), nameof(SessionData.Update))]
    internal static class MattKeeper
    {
        internal static bool Paused;
        private static int _frame, _logged, _scans;
        private static IntPtr _city;
        private static readonly List<Material> Known = new();
        private static readonly HashSet<IntPtr> KnownPtrs = new();

        internal static void Track(Material m)
        {
            if (m != null && KnownPtrs.Add(m.Pointer)) Known.Add(m);
        }

        private static void Postfix()
        {
            if (Paused) return;
            _frame++;
            try
            {
                // a new city: look for our exteriors' materials a few times while it settles, then stop. (Searching every
                // renderer in the scene every 5 s for good was part of the stutter.)
                var city = CityData.Instance;
                var cp = city != null ? city.Pointer : IntPtr.Zero;
                if (cp != _city) { _city = cp; _scans = 0; }
                if (_frame % 300 == 1 && _scans < 6 && cp != IntPtr.Zero && PierBuilder.CityReady()) { _scans++; Scan(); }
                if (_frame % 300 != 0) return;
                for (int i = Known.Count - 1; i >= 0; i--)
                {
                    var m = Known[i];
                    if (m == null || m.WasCollected) { Known.RemoveAt(i); continue; }
                    Enforce(m);
                }
            }
            catch (Exception e) { if (_logged++ < 3) Plugin.L.LogWarning($"[matt] {e.Message}"); }
        }

        private static void Scan()
        {
            var ids = ContentLoader.Definitions.Select(d => d.Id + "_Exterior").ToList();
            if (ids.Count == 0) return;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>()))
            {
                var r = obj?.TryCast<MeshRenderer>();
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && ids.Any(id => m.name.StartsWith(id, StringComparison.Ordinal))) Track(m);
            }
        }

        private static void Enforce(Material m)
        {
            float shine = Math.Clamp(Plugin.ExteriorShine.Value, 0f, 1f);
            float Get(string p) => m.HasProperty(p) ? m.GetFloat(p) : 0f;
            bool glowing = false;
            try { if (Plugin.ExteriorGlow.Value <= 0f && m.HasProperty("_EmissiveColor")) { var c = m.GetColor("_EmissiveColor"); glowing = c.r + c.g + c.b > 1e-4f; } } catch { }
            bool off = glowing || Math.Abs(Get("_MetallicRemapMax")) > 1e-4f || Math.Abs(Get("_Metallic")) > 1e-4f ||
                       Math.Abs(Get("_SmoothnessRemapMax") - shine) > 1e-4f || Math.Abs(Get("_SmoothnessRemapMin")) > 1e-4f;
            if (!off) return;
            if (_logged < 6)
            {
                _logged++;
                Plugin.L.LogInfo($"[matt] {m.name} had metal {Get("_Metallic"):0.###} ({Get("_MetallicRemapMin"):0.###}-{Get("_MetallicRemapMax"):0.###}), " +
                                 $"smoothness {Get("_Smoothness"):0.###} ({Get("_SmoothnessRemapMin"):0.###}-{Get("_SmoothnessRemapMax"):0.###}); set back to matt.");
            }
            ExteriorLoader.MakeMatt(m);
        }
    }
}

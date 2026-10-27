using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// In-game test of the custom exteriors' look, to find out what makes them too bright. Hold Ctrl+Shift and press:
    ///   1 = normal, 2 = no glow (emission off), 3 = black colour (only light and glow left),
    ///   4 = matt (no shine at all), 5 = the base building's own surface maps.
    /// Changes every custom exterior in the city at once and writes a line to the log.
    /// </summary>
    [HarmonyPatch(typeof(SessionData), nameof(SessionData.Update))]
    internal static class LookTest
    {
        private sealed class Saved
        {
            public Color Emissive, BaseColor;
            public float Smoothness, SmoothMin, SmoothMax, Metallic, MetalMin, MetalMax;
            public Texture Mask, Normal, Emission;
        }

        private static readonly Dictionary<IntPtr, Saved> Originals = new();
        internal static Texture BaseMask, BaseNormal;

        private static void Postfix()
        {
            try
            {
                if (!(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return;
                if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return;
                int mode = Input.GetKeyDown(KeyCode.Alpha1) ? 1 : Input.GetKeyDown(KeyCode.Alpha2) ? 2 : Input.GetKeyDown(KeyCode.Alpha3) ? 3
                         : Input.GetKeyDown(KeyCode.Alpha4) ? 4 : Input.GetKeyDown(KeyCode.Alpha5) ? 5
                         : Input.GetKeyDown(KeyCode.Alpha6) ? 6 : Input.GetKeyDown(KeyCode.Alpha7) ? 7 : 0;
                if (mode == 0) return;
                MattKeeper.Paused = mode != 1;
                Apply(mode);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[look] {e.Message}"); }
        }

        private static void Apply(int mode)
        {
            var ids = ContentLoader.Definitions.Select(d => d.Id + "_Exterior").ToList();
            int n = 0;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>()))
            {
                var r = obj?.TryCast<MeshRenderer>();
                if (r == null) continue;
                var m = r.sharedMaterial;
                if (m == null || !ids.Any(id => m.name.StartsWith(id, StringComparison.Ordinal))) continue;
                if (!Originals.TryGetValue(m.Pointer, out var o))
                {
                    o = new Saved
                    {
                        Emissive = m.GetColor("_EmissiveColor"), BaseColor = m.GetColor("_BaseColor"),
                        Smoothness = m.GetFloat("_Smoothness"), SmoothMin = m.GetFloat("_SmoothnessRemapMin"), SmoothMax = m.GetFloat("_SmoothnessRemapMax"),
                        Metallic = m.GetFloat("_Metallic"), MetalMin = m.GetFloat("_MetallicRemapMin"), MetalMax = m.GetFloat("_MetallicRemapMax"),
                        Mask = m.GetTexture("_MaskMap"), Normal = m.GetTexture("_NormalMap"), Emission = m.GetTexture("_EmissiveColorMap"),
                    };
                    Originals[m.Pointer] = o;
                }
                // back to how it was
                m.SetColor("_EmissiveColor", o.Emissive);
                m.SetColor("_BaseColor", o.BaseColor);
                m.SetFloat("_Smoothness", o.Smoothness); m.SetFloat("_SmoothnessRemapMin", o.SmoothMin); m.SetFloat("_SmoothnessRemapMax", o.SmoothMax);
                m.SetFloat("_Metallic", o.Metallic); m.SetFloat("_MetallicRemapMin", o.MetalMin); m.SetFloat("_MetallicRemapMax", o.MetalMax);
                m.SetTexture("_MaskMap", o.Mask); m.SetTexture("_NormalMap", o.Normal); m.SetTexture("_EmissiveColorMap", o.Emission);
                m.EnableKeyword("_MASKMAP");
                m.DisableKeyword("_NORMALMAP");
                switch (mode)
                {
                    case 2: m.SetColor("_EmissiveColor", Color.black); break;
                    case 3: m.SetColor("_BaseColor", Color.black); break;
                    case 4:
                        m.SetFloat("_SmoothnessRemapMin", 0); m.SetFloat("_SmoothnessRemapMax", 0); m.SetFloat("_Smoothness", 0);
                        m.SetFloat("_MetallicRemapMin", 0); m.SetFloat("_MetallicRemapMax", 0); m.SetFloat("_Metallic", 0);
                        break;
                    case 6:
                        var lit = ContentLoader.Definitions.Select(d => ExteriorLoader.LitMapOf(d.Id)).FirstOrDefault(t => m.name.StartsWith(t.id + "_", StringComparison.Ordinal)).tex;
                        if (lit != null) m.SetTexture("_EmissiveColorMap", lit);
                        break;
                    case 7: m.SetColor("_EmissiveColor", new Color(o.Emissive.r * 0.1f, o.Emissive.g * 0.1f, o.Emissive.b * 0.1f, o.Emissive.a)); break;
                    case 5:
                        if (BaseMask != null) m.SetTexture("_MaskMap", BaseMask);
                        if (BaseNormal != null) { m.SetTexture("_NormalMap", BaseNormal); m.EnableKeyword("_NORMALMAP"); }
                        break;
                }
                if (n < 2)
                {
                    var em = m.GetTexture("_EmissiveColorMap");
                    Plugin.L.LogInfo($"[look]   {m.name}: glow map {em?.name} {em?.width}x{em?.height} average {(em != null ? ExteriorLoader.Average(em)?.ToString() : "-")}, " +
                                     $"glow colour {m.GetColor("_EmissiveColor")}, keywords [{string.Join(" ", m.shaderKeywords)}]");
                }
                n++;
            }
            string what = mode switch
            {
                1 => "normal", 2 => "no glow (emission off)", 3 => "black colour", 4 => "matt (no shine)", 5 => "base building's surface maps", 6 => "glow from the exterior's own lit.png instead of the game's copy", 7 => "glow at 10%",
                _ => "?",
            };
            Plugin.L.LogInfo($"[look] {what} on {n} custom exterior part(s)");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// Logs how the game sets up a building's model and glow textures when it places one: once for each custom building and
    /// once for a game building of the same base, so the two can be compared (e.g. why a custom building glows).
    /// </summary>
    [HarmonyPatch(typeof(NewBuilding), nameof(NewBuilding.SetupEmissionTexture))]
    internal static class BuildingDiagnostics
    {
        private static readonly HashSet<string> Done = new();
        private static int _vanillaShown;

        private static string Fmt(Texture t)
        {
            if (t == null) return "none";
            try { return $"{t.name} {t.graphicsFormat}"; } catch { return t.name; }
        }

        private static string Describe(Material m)
        {
            if (m == null) return "no material";
            string C(string p) { try { return m.HasProperty(p) ? m.GetColor(p).ToString() : "-"; } catch { return "?"; } }
            string F(string p) { try { return m.HasProperty(p) ? m.GetFloat(p).ToString("0.###") : "-"; } catch { return "?"; } }
            string T(string p) { try { return m.HasProperty(p) ? Fmt(m.GetTexture(p)) : "-"; } catch { return "?"; } }
            string kw;
            try { kw = string.Join(" ", m.shaderKeywords); } catch { kw = "?"; }
            string avg;
            try { avg = m.HasProperty("_BaseColorMap") && m.GetTexture("_BaseColorMap") != null ? ExteriorLoader.Average(m.GetTexture("_BaseColorMap"))?.ToString() ?? "?" : "-"; } catch { avg = "?"; }
            return $"material {m.name} / {m.shader?.name}; base {T("_BaseColorMap")} (linear avg {avg}) x {C("_BaseColor")}; mask {T("_MaskMap")} metal {F("_Metallic")} ({F("_MetallicRemapMin")}-{F("_MetallicRemapMax")}) " +
                   $"smooth {F("_Smoothness")} ({F("_SmoothnessRemapMin")}-{F("_SmoothnessRemapMax")}) AO {F("_AORemapMin")}-{F("_AORemapMax")}; normal {T("_NormalMap")} x{F("_NormalScale")}; " +
                   $"emissive {T("_EmissiveColorMap")} x {C("_EmissiveColor")} albedoAffects {F("_AlbedoAffectEmissive")}; keywords [{kw}]";
        }

        private static void Postfix(NewBuilding __instance)
        {
            if (Plugin.VerboseLogging == null || !Plugin.VerboseLogging.Value) return;   // a debugging aid from the glow fix
            try
            {
                var preset = __instance.preset;
                if (preset == null) return;
                string name = preset.presetName;
                bool ours = ContentLoader.IsOurBuilding(name);
                string key = ours ? name : "vanilla:" + name;
                // every custom building, plus its base building and the first two other game buildings for comparison
                bool wanted = ours || ContentLoader.Definitions.Any(d => d.BasePreset == name) || _vanillaShown < 2;
                if (!wanted || !Done.Add(key)) return;
                if (!ours) _vanillaShown++;

                var lines = new List<string> { $"[diag] {(ours ? "custom" : "game")} building {name}:" };
                string Tex(Texture t)
                {
                    if (t == null) return "none";
                    var avg = ExteriorLoader.Average(t);
                    var t2 = t.TryCast<Texture2D>();
                    return $"{t.name} {t.width}x{t.height} {(t2 != null ? t2.format.ToString() + " mips " + t2.mipmapCount : t.GetIl2CppType().Name)} avg {avg?.ToString() ?? "?"}";
                }
                lines.Add($"  emissionTextureInstanced: {Tex(__instance.emissionTextureInstanced)}");
                lines.Add($"  emissionTextureUnlit: {Tex(__instance.emissionTextureUnlit)}");
                lines.Add($"  preset maps: unlit {Tex(preset.emissionMapUnlit)}, lit {Tex(preset.emissionMapLit)}, window {Tex(preset.windowMap)}");
                var m = __instance.extMat;
                if (m != null) lines.Add($"  extMat {m.name} shader {m.shader?.name}: emissive map {Tex(m.HasProperty("_EmissiveColorMap") ? m.GetTexture("_EmissiveColorMap") : null)}, " +
                                         $"emissive colour {(m.HasProperty("_EmissiveColor") ? m.GetColor("_EmissiveColor").ToString() : "-")}, base map {(m.HasProperty("_BaseColorMap") ? m.GetTexture("_BaseColorMap")?.name : "-")}");
                void Models(string label, GameObject go)
                {
                    if (go == null) return;
                    foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        var mpb = new MaterialPropertyBlock();
                        r.GetPropertyBlock(mpb);
                        string block = mpb.isEmpty ? "no property block" :
                            $"block emissive map {Tex(mpb.GetTexture("_EmissiveColorMap"))}, emissive colour {mpb.GetColor("_EmissiveColor")}";
                        var sm = r.sharedMaterial;
                        var mf = r.GetComponent<MeshFilter>();
                        string mesh = mf?.sharedMesh == null ? "no mesh" : $"mesh {mf.sharedMesh.name} {mf.sharedMesh.vertexCount} verts, bounds {mf.sharedMesh.bounds.size}, uv sets " +
                            string.Join("", Enumerable.Range(0, 4).Select(k => mf.sharedMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0 + k) ? k.ToString() : "-"));
                        lines.Add($"  {label} {r.gameObject.name} (active {r.gameObject.activeInHierarchy}, enabled {r.enabled}, layer {r.gameObject.layer}, renderingLayers {r.renderingLayerMask}, " +
                                  $"probes {r.lightProbeUsage}, {mesh}): {Describe(sm)}; {block}");
                    }
                }
                Models("base", __instance.buildingModelBase);
                if (__instance.buildingModelsActual != null) foreach (var g in __instance.buildingModelsActual) Models("actual", g);
                if (__instance.buildingModelsLights != null) foreach (var g in __instance.buildingModelsLights) Models("lights", g);
                Plugin.L.LogInfo(string.Join("\n", lines));
            }
            catch (Exception e) { Plugin.L.LogWarning($"[diag] {e.Message}"); }
        }
    }
}

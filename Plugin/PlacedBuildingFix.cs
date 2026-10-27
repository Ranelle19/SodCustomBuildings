using System;
using HarmonyLib;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// When the game places a building it makes its own copies of the model's materials and sets them up. For custom
    /// exteriors, put the matt settings back on those copies afterwards (they made the walls look white-hot).
    /// </summary>
    [HarmonyPatch]
    internal static class PlacedBuildingFix
    {
        [HarmonyPostfix, HarmonyPatch(typeof(NewBuilding), nameof(NewBuilding.SetupModel))]
        private static void AfterModel(NewBuilding __instance) => Fix(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(NewBuilding), nameof(NewBuilding.SetupEmissionTexture))]
        private static void AfterEmission(NewBuilding __instance) => Fix(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(NewBuilding), nameof(NewBuilding.UpdateColourSchemeAndMaterials))]
        private static void AfterColours(NewBuilding __instance) => Fix(__instance);

        private static void Fix(NewBuilding b)
        {
            try
            {
                if (b?.preset == null || !ContentLoader.IsOurBuilding(b.preset.presetName)) return;
                void Under(GameObject go)
                {
                    if (go == null) return;
                    foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                        foreach (var m in r.sharedMaterials)
                            if (m != null && m.name.Contains("_Exterior")) { ExteriorLoader.MakeMatt(m); MattKeeper.Track(m); }
                }
                Under(b.buildingModelBase);
                if (b.buildingModelsActual != null) foreach (var g in b.buildingModelsActual) Under(g);
                if (b.buildingModelsLights != null) foreach (var g in b.buildingModelsLights) Under(g);
            }
            catch (Exception e) { Plugin.Verbose($"matt fix: {e.Message}"); }
        }
    }
}

using HarmonyLib;

namespace CustomBuildings
{
    // Registration is idempotent, so it is safe to run from more than one place. Toolbox.Start is where other
    // building mods (CitiesV2) register; ParseFloorData is the moment floor plans are read into CityData.floorData.

    [HarmonyPatch(typeof(Toolbox), nameof(Toolbox.Start))]
    internal static class ToolboxStartPatch
    {
        private static void Postfix() => ContentLoader.RegisterAll("Toolbox.Start");
    }

    [HarmonyPatch(typeof(Strings), nameof(Strings.LoadTextFiles))]
    internal static class StringsLoadPatch
    {
        private static void Postfix() => BuildingNames.Apply("Strings.LoadTextFiles");
    }

    [HarmonyPatch(typeof(CityData), nameof(CityData.ParseFloorData))]
    internal static class ParseFloorDataPatch
    {
        // Put our floor TextAssets in AssetLoader.allFloorData before the game parses that list…
        private static void Prefix() => ContentLoader.RegisterFloors("ParseFloorData (before)");

        // …then make sure everything is present afterwards, and export vanilla data for the editor.
        private static void Postfix()
        {
            ContentLoader.FloorDataParsed = true;
            ContentLoader.RegisterAll("ParseFloorData (after)");
            Exporter.ExportOnce();
        }
    }
}

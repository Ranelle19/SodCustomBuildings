using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace CustomBuildings
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "Ranelle.sod.custombuildings";
        private const string OldGuid = "felix.sod.custombuildings";   // before 1.4.1; its settings are carried over once
        public const string Name = "CustomBuildings";
        public const string Version = "1.0.0";   // keep in step with <Version> in CustomBuildings.csproj

        internal static ManualLogSource L;
        internal static string ModDir;
        internal static ConfigEntry<bool> ExportOnStart;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<bool> ExportModels;
        internal static ConfigEntry<int> ModelTextureMaxSize;
        internal static ConfigEntry<int> ExteriorTextureMaxSize;
        internal static ConfigEntry<bool> ExteriorWindowLights;
        internal static ConfigEntry<string> ExteriorWindowMethod;
        internal static ConfigEntry<float> ExteriorShine;
        internal static ConfigEntry<float> ExteriorGlow;
        internal static ConfigEntry<bool> EdgeReport;

        public override void Load()
        {
            L = Log;

            // The mod folder is wherever this DLL sits (BepInEx\plugins\CustomBuildings).
            ModDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (string.IsNullOrEmpty(ModDir))
                ModDir = Path.Combine(Paths.PluginPath, Name);

            // carry the settings over from the old plugin id (felix.sod.custombuildings.cfg), then tidy up the old file
            try
            {
                var oldCfg = Path.Combine(Paths.ConfigPath, OldGuid + ".cfg");
                var newCfg = Path.Combine(Paths.ConfigPath, Guid + ".cfg");
                if (File.Exists(oldCfg) && !string.Equals(oldCfg, newCfg, System.StringComparison.OrdinalIgnoreCase))
                {
                    bool fresh = !File.Exists(newCfg) || new FileInfo(newCfg).Length == 0;
                    if (fresh)
                    {
                        File.Copy(oldCfg, newCfg, overwrite: true);
                        Config.Reload();
                        Log.LogInfo($"Settings carried over from {OldGuid}.cfg");
                    }
                    File.Move(oldCfg, oldCfg + ".old", overwrite: true);
                }
            }
            catch (System.Exception ex) { Log.LogWarning("Couldn't carry over the old settings: " + ex.Message); }

            ExportOnStart = Config.Bind("Export", "ExportOnStart", true,
                "Write every vanilla floor plan and a catalog of buildings/presets to the Export folder when the game loads. " +
                "The editor reads these. Turn off once exported to save a little load time.");
            VerboseLogging = Config.Bind("Debug", "VerboseLogging", false, "Log every floor and building as it is registered.");
            ExportModels = Config.Bind("Export", "ExportModels", true,
                "Also export each game building's 3D model and textures (Export/models) for the editor's 3D preview. " +
                "Done once per game version; delete Export/models to export again.");
            ModelTextureMaxSize = Config.Bind("Export", "ModelTextureMaxSize", 1024,
                "Largest texture size (pixels) written for exported models. Smaller = less disk space.");

            ExteriorTextureMaxSize = Config.Bind("Exterior", "ExteriorTextureMaxSize", 1024,
                "Custom exteriors' textures are shrunk (halved) until they are at most this wide, like the game's own buildings. " +
                "Bigger looks sharper but loads slower. 0 = never shrink.");
            ExteriorWindowLights = Config.Bind("Exterior", "ExteriorWindowLights", true,
                "Let the game work out the windows of custom exteriors from their window map (for lights at night). " +
                "Turn off if loading hangs on a custom building; its windows then won't light up.");

            ExteriorWindowMethod = Config.Bind("Exterior", "ExteriorWindowMethod", "Fast",
                new ConfigDescription("How custom exteriors' windows are found in their window map. Fast = the plugin's own copy of the " +
                                      "game's method (milliseconds, gives the same result). Game = the game's own routine (can take minutes; " +
                                      "its result is cached in BepInEx\\cache\\CustomBuildings).", new AcceptableValueList<string>("Fast", "Game")));

            ExteriorGlow = Config.Bind("Exterior", "ExteriorGlowAmount", 0f,
                "Glow of custom exteriors (lit windows, neon) compared to the game's buildings. 0 = off (default: with glow on, the whole " +
                "building glows). 1 = the same strength as the game's buildings.");
            ExteriorShine = Config.Bind("Exterior", "ExteriorShine", 0f,
                "How shiny custom exteriors are, 0 (matt) to 1 (mirror). The base building's own setting made them look white-hot.");

            EdgeReport = Config.Bind("Debug", "EdgeReport", false,
                "Write an [edge] report about the city's edge and the sea to the log when a city loads, and let Ctrl+Shift+9 log " +
                "the heights where you stand (for building a pier). Turn off when not needed.");

            Directory.CreateDirectory(Path.Combine(ModDir, Shared.ModPaths.BuildingsDir));

            ContentLoader.ReadFiles();
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

            L.LogInfo($"{Name} {Version} loaded from {ModDir}");
        }

        internal static void Verbose(string msg)
        {
            if (VerboseLogging != null && VerboseLogging.Value) L.LogInfo(msg);
        }
    }
}

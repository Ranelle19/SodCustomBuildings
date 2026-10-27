using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BuildingEditor
{
    public enum BepInExState { Missing, WrongKind, Installed, Ready }
    public enum PluginState { Missing, Outdated, Current, NotBundled, Newer, Different }

    /// <summary>Editor settings, kept in %APPDATA%\SodBuildingEditor\settings.json.</summary>
    public sealed class EditorSettings
    {
        public string GameDir { get; set; } = "";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SodBuildingEditor", "settings.json");

        public static EditorSettings Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(FilePath)) ?? new EditorSettings(); }
            catch { }
            return new EditorSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>
    /// Finds the game, checks and installs BepInEx (IL2CPP) and the CustomBuildings plugin. The plugin DLL is built into the
    /// editor (embedded resource), so the editor alone is enough to set someone up.
    /// </summary>
    public static class GameSetup
    {
        public const string GameExe = "Shadows of Doubt.exe";
        public const string SteamAppId = "986130";
        public const string BepInExVersion = "6.0.788";
        public const string BepInExPage = "https://thunderstore.io/c/shadows-of-doubt/p/BepInEx/BepInExPack_IL2CPP/";
        public static readonly string BepInExDownload = $"https://thunderstore.io/package/download/BepInEx/BepInExPack_IL2CPP/{BepInExVersion}/";
        private const string PluginResource = "CustomBuildings.dll";

        public static string PluginDir(string gameDir) => Path.Combine(gameDir, "BepInEx", "plugins", "CustomBuildings");
        public static string PluginDll(string gameDir) => Path.Combine(PluginDir(gameDir), "CustomBuildings.dll");
        public static string LogFile(string gameDir) => Path.Combine(gameDir, "BepInEx", "LogOutput.log");

        public static bool IsGameFolder(string dir) => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, GameExe));

        public static bool IsGameRunning()
        {
            try { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)).Length > 0; }
            catch { return false; }
        }

        // ------------------------------------------------------------------ finding the game

        /// <summary>Game folders found through Steam (all its library folders) and a few usual places.</summary>
        public static List<string> FindGameFolders()
        {
            var found = new List<string>();
            void Try(string dir)
            {
                if (IsGameFolder(dir) && !found.Contains(dir, StringComparer.OrdinalIgnoreCase)) found.Add(dir);
            }

            var steamRoots = new List<string>();
            foreach (var (hive, key, value) in new[]
                     {
                         (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                         (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                         (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                     })
            {
                try
                {
                    using var k = hive.OpenSubKey(key);
                    if (k?.GetValue(value) is string p && Directory.Exists(p)) steamRoots.Add(p.Replace('/', '\\'));
                }
                catch { }
            }

            var libraries = new List<string>(steamRoots);
            foreach (var root in steamRoots)
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                try
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch { }
            }
            foreach (var lib in libraries) Try(Path.Combine(lib, "steamapps", "common", "Shadows of Doubt"));

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                Try(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common", "Shadows of Doubt"));
                Try(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam", "steamapps", "common", "Shadows of Doubt"));
                Try(Path.Combine(drive.RootDirectory.FullName, "Games", "Shadows of Doubt"));
            }
            return found;
        }

        // ------------------------------------------------------------------ BepInEx

        public static BepInExState GetBepInEx(string gameDir)
        {
            var core = Path.Combine(gameDir, "BepInEx", "core");
            if (!Directory.Exists(core)) return BepInExState.Missing;
            bool il2cpp = File.Exists(Path.Combine(core, "BepInEx.Unity.IL2CPP.dll"));
            if (!il2cpp) return BepInExState.WrongKind;   // BepInEx 5 / Mono: doesn't work with this game
            bool loader = File.Exists(Path.Combine(gameDir, "winhttp.dll"));
            if (!loader) return BepInExState.Missing;
            bool ranOnce = File.Exists(Path.Combine(gameDir, "BepInEx", "interop", "Assembly-CSharp.dll"));
            return ranOnce ? BepInExState.Ready : BepInExState.Installed;
        }

        /// <summary>Downloads BepInExPack IL2CPP from Thunderstore and copies its BepInExPack folder into the game folder.</summary>
        public static async Task InstallBepInExAsync(string gameDir, IProgress<string> progress)
        {
            progress?.Report($"Downloading BepInEx {BepInExVersion}…");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SodBuildingEditor/1.0");
            var bytes = await http.GetByteArrayAsync(BepInExDownload);

            progress?.Report("Installing BepInEx…");
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            // the package has its files under "BepInExPack/"; older packs may have them at the top level
            string prefix = zip.Entries.Any(e => e.FullName.StartsWith("BepInExPack/", StringComparison.OrdinalIgnoreCase)) ? "BepInExPack/" : "";
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", "icon.png", "README.md", "CHANGELOG.md" };
            int files = 0;
            string root = Path.GetFullPath(gameDir) + Path.DirectorySeparatorChar;
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = e.FullName.Substring(prefix.Length);
                if (rel.Length == 0 || (prefix.Length == 0 && skip.Contains(rel))) continue;
                var target = Path.GetFullPath(Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never write outside the game folder
                if (rel.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                e.ExtractToFile(target, overwrite: true);
                files++;
            }
            if (files == 0) throw new Exception("The download didn't contain BepInEx files.");
            progress?.Report($"BepInEx installed ({files} files).");
        }

        // ------------------------------------------------------------------ the plugin

        private static byte[] BundledPlugin()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(PluginResource);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        public static PluginState GetPlugin(string gameDir)
        {
            var bundled = BundledPlugin();
            var dll = PluginDll(gameDir);
            if (!File.Exists(dll)) return bundled == null ? PluginState.NotBundled : PluginState.Missing;
            if (bundled == null) return PluginState.Current;   // nothing to compare with: trust what's there
            if (SHA256.HashData(File.ReadAllBytes(dll)).SequenceEqual(SHA256.HashData(bundled))) return PluginState.Current;
            var installed = VersionOf(File.ReadAllBytes(dll));
            var mine = VersionOf(bundled);
            if (installed == null || mine == null) return PluginState.Different;
            int c = installed.CompareTo(mine);
            return c > 0 ? PluginState.Newer : c < 0 ? PluginState.Outdated : PluginState.Different;
        }

        /// <summary>Version of the installed and the bundled plugin (e.g. "0.3.0"), for showing in the setup.</summary>
        public static string InstalledVersion(string gameDir) =>
            File.Exists(PluginDll(gameDir)) ? VersionOf(File.ReadAllBytes(PluginDll(gameDir)))?.ToString(3) : null;
        public static string BundledVersion() { var b = BundledPlugin(); return b == null ? null : VersionOf(b)?.ToString(3); }

        private static Version VersionOf(byte[] dll)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sodcb_" + Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                File.WriteAllBytes(tmp, dll);
                return AssemblyName.GetAssemblyName(tmp).Version;
            }
            catch { return null; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>Good enough to work with: the same plugin, or one newer than the editor's (e.g. built by hand).</summary>
        public static bool PluginOk(PluginState s) => s is PluginState.Current or PluginState.Newer or PluginState.Different;

        public static void InstallPlugin(string gameDir)
        {
            var bundled = BundledPlugin() ?? throw new Exception("This editor was built without the plugin inside.");
            bool fresh = !File.Exists(PluginDll(gameDir));
            Directory.CreateDirectory(Path.Combine(PluginDir(gameDir), CustomBuildings.Shared.ModPaths.BuildingsDir));
            File.WriteAllBytes(PluginDll(gameDir), bundled);
            // a first install gets the example wall piece too, like the Thunderstore package (so everyone in multiplayer
            // has the same Edge folder); an update never brings back one you deleted
            if (fresh) InstallExample(gameDir);
        }

        /// <summary>The example wall piece (BoundaryCoastal03, the sea wall with a pier) into Edge\, unless it's there.</summary>
        public static void InstallExample(string gameDir)
        {
            var dir = Path.Combine(PluginDir(gameDir), CustomBuildings.Shared.EdgeStore.DirName, "BoundaryCoastal03");
            if (Directory.Exists(dir)) return;
            var asm = typeof(GameSetup).Assembly;
            var files = new[] { ("piece.json", ""), ("model.obj", "model"), ("model.mtl", "model"), ("tex0.png", "model") };
            if (files.Any(f => asm.GetManifestResourceInfo("example/" + f.Item1) == null)) return;
            foreach (var (name, sub) in files)
            {
                var to = Path.Combine(dir, sub);
                Directory.CreateDirectory(to);
                using var src = asm.GetManifestResourceStream("example/" + name);
                using var dst = File.Create(Path.Combine(to, name));
                src.CopyTo(dst);
            }
        }

        public static bool HasExport(string gameDir) =>
            File.Exists(Path.Combine(PluginDir(gameDir), CustomBuildings.Shared.ModPaths.ExportDir, CustomBuildings.Shared.ModPaths.CatalogFile));

        public static void StartGame()
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
        }

        public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        // ------------------------------------------------------------------ optional mods

        /// <summary>A Thunderstore mod that isn't needed by CustomBuildings, but is used by "Export for other mod loaders".</summary>
        public sealed class OptionalMod
        {
            public string Author, Name, Version, Title, Why;
            public string[] DllHints = Array.Empty<string>();   // parts of DLL names that show it's installed
            public string[] Needs = Array.Empty<string>();      // Names of other optional mods it depends on
            public string Page => $"https://thunderstore.io/c/shadows-of-doubt/p/{Author}/{Name}/";
            public string Download => $"https://thunderstore.io/package/download/{Author}/{Name}/{Version}/";
            public string FolderName => $"{Author}-{Name}";
        }

        public static readonly OptionalMod[] OptionalMods =
        {
            new() { Author = "Piepieonline", Name = "Asset_Bundle_Loader", Version = "2.1.2", Title = "Asset Bundle Loader",
                    Why = "loads the Asset Bundle Loader export", DllHints = new[] { "AssetBundleLoader" } },
            new() { Author = "Piepieonline", Name = "CommunityCaseLoader", Version = "1.1.0", Title = "Community Case Loader",
                    Why = "finds .sodso.json packages for Asset Bundle Loader", DllHints = new[] { "CommunityCaseLoader", "CaseLoader" } },
            new() { Author = "Piepieonline", Name = "DDSLoader", Version = "1.1.0", Title = "DDSLoader",
                    Why = "loads textures and names for Asset Bundle Loader packages", DllHints = new[] { "DDSLoader" } },
            new() { Author = "Venomaus", Name = "SODCommon", Version = "2.1.4", Title = "SOD Common",
                    Why = "needed by UniversalObjectLoader", DllHints = new[] { "SOD.Common", "SODCommon" } },
            new() { Author = "truedartt", Name = "UniversalObjectLoader", Version = "1.1.0", Title = "UniversalObjectLoader",
                    Why = "loads the UniversalObjectLoader export", DllHints = new[] { "UniversalObjectLoader", "ObjectLoader" },
                    Needs = new[] { "Asset_Bundle_Loader", "SODCommon" } },
        };

        /// <summary>True when the mod is in BepInEx\plugins: its Thunderstore folder (r2modman/our installer) or a DLL with its name.</summary>
        public static bool IsInstalled(string gameDir, OptionalMod mod)
        {
            var plugins = Path.Combine(gameDir, "BepInEx", "plugins");
            if (!Directory.Exists(plugins)) return false;
            try
            {
                foreach (var d in Directory.EnumerateDirectories(plugins))
                {
                    var n = Path.GetFileName(d);
                    if (n.Equals(mod.FolderName, StringComparison.OrdinalIgnoreCase) || n.Equals(mod.Name, StringComparison.OrdinalIgnoreCase))
                        if (Directory.EnumerateFiles(d, "*.dll", SearchOption.AllDirectories).Any()) return true;
                }
                foreach (var f in Directory.EnumerateFiles(plugins, "*.dll", SearchOption.AllDirectories))
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    if (mod.DllHints.Any(h => n.Equals(h, StringComparison.OrdinalIgnoreCase) || n.Replace(".", "").Equals(h.Replace(".", ""), StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Installs the mod (and the optional mods it needs that are missing) into BepInEx\plugins\Author-Name.</summary>
        public static async Task InstallOptionalAsync(string gameDir, OptionalMod mod, IProgress<string> progress)
        {
            foreach (var need in mod.Needs)
            {
                var dep = OptionalMods.FirstOrDefault(m => m.Name == need);
                if (dep != null && !IsInstalled(gameDir, dep)) await InstallOptionalAsync(gameDir, dep, progress);
            }

            progress?.Report($"Downloading {mod.Title} {mod.Version}…");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SodBuildingEditor/1.0");
            var bytes = await http.GetByteArrayAsync(mod.Download);

            progress?.Report($"Installing {mod.Title}…");
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var target = Path.Combine(gameDir, "BepInEx", "plugins", mod.FolderName);
            var config = Path.Combine(gameDir, "BepInEx", "config");
            string root = Path.GetFullPath(Path.Combine(gameDir, "BepInEx")) + Path.DirectorySeparatorChar;
            int files = 0;
            foreach (var e in zip.Entries)
            {
                var rel = e.FullName.Replace('\\', '/');
                if (rel.EndsWith("/")) continue;
                string baseDir = target;
                // Thunderstore packages put their files at the top, under plugins/ or under BepInEx/plugins/; config/ goes to BepInEx\config
                foreach (var p in new[] { "BepInEx/plugins/", "plugins/" })
                    if (rel.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { rel = rel.Substring(p.Length); break; }
                foreach (var p in new[] { "BepInEx/config/", "config/" })
                    if (rel.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { rel = rel.Substring(p.Length); baseDir = config; break; }
                if (rel.Length == 0) continue;
                var path = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never write outside BepInEx
                if (baseDir == config && File.Exists(path)) continue;                    // keep the user's settings
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                e.ExtractToFile(path, overwrite: true);
                files++;
            }
            if (files == 0) throw new Exception($"The {mod.Title} download was empty.");
            progress?.Report($"{mod.Title} installed.");
        }
    }
}

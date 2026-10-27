using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>
    /// Writes a building in the formats of two other mod loaders, for people who use those instead of the CustomBuildings
    /// plugin:
    ///  * Piepieonline's Asset Bundle Loader + Community Case Loader: a content-only Thunderstore package (like
    ///    PiesConstructionCo): murdermanifest.sodso.json, the BuildingPreset as .sodso.json ("copyFrom" the base
    ///    building, floors as "FLOOR:", the exterior as a .sodprefab.json with OBJ meshes and PNG textures), names via DDSLoader.
    ///  * truedartt's UniversalObjectLoader: a BuildingPreset JSON ({"Base__clone": {...}}, floors as TextAsset paths) and
    ///    the floor files, for a modder's own plugin to load with Loader.LoadFromJson (UOL has no folder loader of its own).
    /// What only the CustomBuildings plugin does (reserved places, window detection, keeping the exterior matt and
    /// glow-free at runtime, sharing place limits) is listed in each package's README.
    /// </summary>
    public static class OtherLoadersExport
    {
        private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

        public sealed class Result
        {
            public string AblFolder, UolFolder;
            public readonly List<string> Notes = new();
        }

        public static Result Export(Workspace ws, BuildingEntry b, string outDir)
        {
            var r = new Result();
            var def = b.Def;
            var layouts = def.ReplaceFloorLayouts && def.FloorLayouts.Count > 0
                ? def.FloorLayouts
                : ws.FindBaseBuilding(def.BasePreset)?.FloorLayouts ?? new List<FloorLayoutDef>();
            if (layouts.Count == 0) throw new Exception("The building has no floor layouts.");

            // every floor plan the layouts use: own ones from the editor, game ones copied from the export
            var floors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in layouts.SelectMany(l => l.Blueprints).Distinct())
            {
                var own = ws.Buildings.SelectMany(x => x.Floors).FirstOrDefault(f => f.Name == name);
                if (own != null) { floors[name] = own.Doc.ToJson(); continue; }
                if (ws.GameFloorFiles.TryGetValue(name, out var path)) { floors[name] = File.ReadAllText(path); continue; }
                r.Notes.Add($"Floor plan '{name}' wasn't found and is left out.");
            }

            var extDir = ModelLoader.ExteriorFolder(ws, b);
            var extInfo = ExteriorPainter.ReadInfo(extDir);
            bool hasExterior = extInfo != null && File.Exists(Path.Combine(extDir, extInfo.Obj));

            r.AblFolder = WriteAbl(ws, b, layouts, floors, extDir, hasExterior ? extInfo : null, Path.Combine(outDir, Safe(def.Id) + "-AssetBundleLoader"), r.Notes);
            r.UolFolder = WriteUol(ws, b, layouts, floors, Path.Combine(outDir, Safe(def.Id) + "-UniversalObjectLoader"), r.Notes);
            if (def.ReservedPlaces?.Count > 0)
                r.Notes.Add("Units reserved for a kind of place only work with the CustomBuildings plugin; in these packages the game picks as usual.");
            return r;
        }

        private static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());

        // ------------------------------------------------------------------ shared bits

        /// <summary>BuildingPreset fields from the building's overrides (the game's own field names).</summary>
        private static void Overrides(JsonObject o, BuildingDef def, Workspace ws, Func<string, string, JsonNode> reference)
        {
            var ov = def.Overrides;
            if (ov == null) return;
            if (ov.Disable.HasValue) o["disable"] = ov.Disable.Value;
            if (ov.Zone.HasValue) o["displayedZone"] = (int)ov.Zone.Value;
            if (ov.DensityMinimum.HasValue) o["densityMinimum"] = (int)ov.DensityMinimum.Value;
            if (ov.DensityMaximum.HasValue) o["densityMaximum"] = (int)ov.DensityMaximum.Value;
            if (ov.LandValueMinimum.HasValue) o["landValueMinimum"] = (int)ov.LandValueMinimum.Value;
            if (ov.LandValueMaximum.HasValue) o["landValueMaximum"] = (int)ov.LandValueMaximum.Value;
            if (ov.Minimum.HasValue) o["minimum"] = ov.Minimum.Value;
            if (ov.HardLimit.HasValue) o["hardLimit"] = ov.HardLimit.Value;
            if (ov.DesiredRatio.HasValue) o["desiredRatio"] = ov.DesiredRatio.Value;
            if (ov.FeatureImportance.HasValue) o["featureImportance"] = ov.FeatureImportance.Value;
            if (ov.AllowedInAllDistricts.HasValue) o["allowedInAllDistricts"] = ov.AllowedInAllDistricts.Value;
            var names = ov.PossibleNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
            if (names.Count == 0 && !string.IsNullOrWhiteSpace(def.DisplayName)) names.Add(def.DisplayName.Trim());
            if (names.Count > 0)
            {
                o["overrideNaming"] = true;
                o["possibleNames"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray());
            }
            if (!string.IsNullOrWhiteSpace(ov.StairsFrom))
            {
                var cb = ws.Catalog.Buildings.FirstOrDefault(x => x.PresetName == ov.StairsFrom.Trim());
                if (cb != null && cb.Stairwell.Length > 0) o["stairwellRegular"] = reference("StairwellPreset", cb.Stairwell);
                if (cb != null && cb.LargeStairwell.Length > 0) o["stairwellLarge"] = reference("StairwellPreset", cb.LargeStairwell);
            }
        }

        private static JsonObject Layout(FloorLayoutDef l, Func<string, JsonNode> blueprint, bool ground) => new()
        {
            ["floorsWithThisSetting"] = Math.Max(1, l.Floors),
            ["blueprints"] = new JsonArray(l.Blueprints.Select(blueprint).ToArray()),
            ["airVentMaximumExtrusion"] = ground ? 0 : 1,
            ["controlRoomVariants"] = new JsonArray(),
            ["forceShowModel"] = 0,
            ["forceHideModels"] = new JsonArray(),
            ["forceHideModelsInRooms"] = new JsonArray(),
            ["forceHideModelsOutside"] = new JsonArray(),
            ["overrideCeilingHeight"] = 0,
            ["newCeilingHeight"] = 51,
        };

        private static void Write(string path, JsonNode node) => File.WriteAllText(path, node.ToJsonString(Pretty));

        // ------------------------------------------------------------------ Asset Bundle Loader + Community Case Loader

        private static string WriteAbl(Workspace ws, BuildingEntry b, List<FloorLayoutDef> layouts, Dictionary<string, string> floors,
                                       string extDir, ExteriorInfo ext, string root, List<string> notes)
        {
            var def = b.Def;
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var mod = Path.Combine(root, "plugins", Safe(def.Id));
            Directory.CreateDirectory(Path.Combine(mod, "floors"));

            foreach (var kv in floors) File.WriteAllText(Path.Combine(mod, "floors", kv.Key + ".json"), kv.Value);

            var preset = new JsonObject
            {
                ["presetName"] = def.Id,
                ["name"] = def.Id,
                ["fileType"] = "BuildingPreset",
                ["type"] = "BuildingPreset",
                ["copyFrom"] = "REF:BuildingPreset|" + def.BasePreset,
            };
            preset["floorLayouts"] = new JsonArray(layouts.Select((l, i) => (JsonNode)Layout(l, n => "FLOOR:floors/" + n, i == 0)).ToArray());
            if (def.ReplaceBasementLayouts)
                preset["basementLayouts"] = new JsonArray(def.BasementLayouts.Select(l => (JsonNode)Layout(l, n => "FLOOR:floors/" + n, false)).ToArray());
            Overrides(preset, def, ws, (type, name) => "REF:" + type + "|" + name);

            if (ext != null)
            {
                var exOut = Path.Combine(mod, "exterior");
                Directory.CreateDirectory(exOut);
                foreach (var f in new[] { ext.Diffuse, ext.Lit, ext.Unlit, ext.WindowMap })
                    if (File.Exists(Path.Combine(extDir, f))) File.Copy(Path.Combine(extDir, f), Path.Combine(exOut, f), true);
                var parts = SplitObj(Path.Combine(extDir, ext.Obj));
                foreach (var kv in parts) File.WriteAllText(Path.Combine(exOut, kv.Key.ToLowerInvariant() + ".obj"), kv.Value);

                JsonObject Material() => new()
                {
                    ["name"] = def.Id + "_Exterior",
                    ["copyFrom"] = "REF:BuildingPreset|" + def.BasePreset,
                    ["clearTextures"] = new JsonArray("_NormalMap", "_MaskMap", "_DetailMap"),
                    ["textures"] = new JsonObject { ["_BaseColorMap"] = ext.Diffuse, ["_MainTex"] = ext.Diffuse },
                    // matt and without glow: with the base building's settings our walls looked white-hot in game
                    ["colors"] = new JsonObject { ["_EmissiveColor"] = new JsonArray(0, 0, 0, 1) },
                    ["floats"] = new JsonObject
                    {
                        ["_Metallic"] = 0, ["_MetallicRemapMin"] = 0, ["_MetallicRemapMax"] = 0,
                        ["_Smoothness"] = 0, ["_SmoothnessRemapMin"] = 0, ["_SmoothnessRemapMax"] = 0,
                    },
                };
                var children = new JsonArray();
                foreach (var name in parts.Keys)
                {
                    var comps = new JsonArray(new JsonObject { ["type"] = "MeshRenderer", ["mesh"] = name.ToLowerInvariant() + ".obj", ["material"] = Material() });
                    if (name == "Base") comps.Add(new JsonObject { ["type"] = "MeshCollider" });
                    children.Add(new JsonObject { ["name"] = name, ["tag"] = "BuildingModelLights", ["layer"] = 27, ["components"] = comps });
                }
                Write(Path.Combine(exOut, "exterior.sodprefab.json"), new JsonObject
                {
                    ["name"] = def.Id + "_Model",
                    ["prefabType"] = "building",
                    ["tag"] = "BuildingModel",
                    ["layer"] = 27,
                    ["defaultColliders"] = false,
                    ["children"] = children,
                });
                string Tex(string f) => "TEXTURE:exterior/" + Path.GetFileNameWithoutExtension(f);
                preset["prefab"] = "PREFAB:exterior/exterior";
                preset["tex"] = Tex(ext.Diffuse);
                preset["emissionMapLit"] = Tex(ext.Lit);
                preset["emissionMapUnlit"] = Tex(ext.Unlit);
                preset["windowMap"] = Tex(ext.WindowMap);
                preset["addonMap"] = null;
                preset["floorCount"] = ext.Storeys;
                preset["featuresSmokestack"] = false;
                preset["cableLinkPoints"] = new JsonArray();
                preset["sideSignPoints"] = new JsonArray();
                notes.Add("Asset Bundle Loader: the exterior is included as an OBJ prefab. Its window lights depend on the game working out " +
                          "the windows itself, which may be slow or not happen; the CustomBuildings plugin does that part for its own copy.");
            }

            Write(Path.Combine(mod, Safe(def.Id) + ".sodso.json"), preset);
            Write(Path.Combine(mod, "murdermanifest.sodso.json"), new JsonObject
            {
                ["enabled"] = true,
                ["fileOrder"] = new JsonArray("REF:" + Safe(def.Id)),
                ["loadBefore"] = "",
                ["version"] = 1,
            });

            // building name for the game's text tables (DDSLoader)
            var strings = Path.Combine(mod, "DDSContent", "Strings", "English");
            Directory.CreateDirectory(strings);
            File.WriteAllText(Path.Combine(strings, "names.rooms.csv"), $"\"{def.Id.ToLowerInvariant()}\",,\"{def.GameName().Replace("\"", "'")}\",,,,\n", new UTF8Encoding(false));

            Write(Path.Combine(root, "manifest.json"), new JsonObject
            {
                ["name"] = Safe(def.Id),
                ["version_number"] = "1.0.0",
                ["website_url"] = "",
                ["description"] = $"{def.GameName()}: a new building for Shadows of Doubt (made with SodBuildingEditor).",
                ["dependencies"] = new JsonArray(
                    "BepInEx-BepInExPack_IL2CPP-6.0.788",
                    "Piepieonline-Asset_Bundle_Loader-2.1.2",
                    "Piepieonline-CommunityCaseLoader-1.1.0",
                    "Piepieonline-DDSLoader-1.1.0"),
            });
            WriteIcon(Path.Combine(root, "icon.png"), ext != null ? Path.Combine(extDir, ext.Diffuse) : null);
            File.WriteAllText(Path.Combine(root, "README.md"),
$@"# {def.GameName()}

A new building for Shadows of Doubt, based on {def.BasePreset}. Content only: it is loaded by Piepieonline's Asset Bundle
Loader and Community Case Loader (and DDSLoader for its name); no CustomBuildings plugin needed.

## Install

Copy the `plugins` folder into `BepInEx` (so you get `BepInEx\plugins\{Safe(def.Id)}\murdermanifest.sodso.json`), with the
mods in `manifest.json` installed. Generate a new city.

## Differences from the CustomBuildings plugin

- Units reserved for a particular kind of place aren't supported; the game picks what each unit becomes.
- The exterior's window lights rely on the game; the plugin works the windows out itself.
- The plugin also lets shops that the game limits to {def.BasePreset} into this building; here they may not appear.
- Don't install this and the same building through the CustomBuildings plugin at the same time.
");
            return root;
        }

        /// <summary>Splits exterior.obj ("o Base", "o Details", "o Shapes") into one OBJ per part, renumbering the indices.</summary>
        private static Dictionary<string, string> SplitObj(string path)
        {
            var v = new List<string>(); var vt = new List<string>(); var vn = new List<string>();
            var faces = new Dictionary<string, List<string[]>>();
            string cur = "Base";
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("o ")) { cur = line.Substring(2).Trim(); continue; }
                if (line.StartsWith("v ")) v.Add(line);
                else if (line.StartsWith("vt ")) vt.Add(line);
                else if (line.StartsWith("vn ")) vn.Add(line);
                else if (line.StartsWith("f "))
                {
                    if (!faces.TryGetValue(cur, out var list)) faces[cur] = list = new List<string[]>();
                    list.Add(line.Substring(2).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }
            }
            var result = new Dictionary<string, string>();
            foreach (var kv in faces)
            {
                var sb = new StringBuilder($"# {kv.Key}, from a SodBuildingEditor exterior (x mirrored, Asset Bundle Loader convention)\n");
                var mapV = new Dictionary<int, int>(); var mapT = new Dictionary<int, int>(); var mapN = new Dictionary<int, int>();
                var outV = new List<string>(); var outT = new List<string>(); var outN = new List<string>();
                int Map(Dictionary<int, int> map, List<string> src, List<string> dst, string idx)
                {
                    if (string.IsNullOrEmpty(idx)) return 0;
                    int i = int.Parse(idx, CultureInfo.InvariantCulture) - 1;
                    if (!map.TryGetValue(i, out var j)) { j = dst.Count + 1; dst.Add(src[i]); map[i] = j; }
                    return j;
                }
                var fl = new StringBuilder();
                foreach (var f in kv.Value)
                {
                    fl.Append('f');
                    foreach (var tok in f)
                    {
                        var p = tok.Split('/');
                        int a = Map(mapV, v, outV, p[0]);
                        int t = p.Length > 1 ? Map(mapT, vt, outT, p[1]) : 0;
                        int n = p.Length > 2 ? Map(mapN, vn, outN, p[2]) : 0;
                        fl.Append(' ').Append(a).Append('/').Append(t > 0 ? t.ToString() : "").Append('/').Append(n > 0 ? n.ToString() : "");
                    }
                    fl.Append('\n');
                }
                foreach (var l in outV) sb.Append(l).Append('\n');
                foreach (var l in outT) sb.Append(l).Append('\n');
                foreach (var l in outN) sb.Append(l).Append('\n');
                sb.Append(fl);
                result[kv.Key] = sb.ToString();
            }
            return result;
        }

        /// <summary>Thunderstore wants a 256×256 icon: a crop of the exterior's colours, or a plain one.</summary>
        private static void WriteIcon(string path, string diffuse)
        {
            using var bmp = new Bitmap(256, 256, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(60, 30, 45));
                if (diffuse != null && File.Exists(diffuse))
                {
                    using var src = new Bitmap(diffuse);
                    int s = Math.Min(src.Height, Math.Min(src.Width, 192));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.DrawImage(src, new Rectangle(0, 0, 256, 256), new Rectangle(0, 0, s, s), GraphicsUnit.Pixel);
                }
            }
            bmp.Save(path, ImageFormat.Png);
        }

        // ------------------------------------------------------------------ UniversalObjectLoader

        private static string WriteUol(Workspace ws, BuildingEntry b, List<FloorLayoutDef> layouts, Dictionary<string, string> floors, string root, List<string> notes)
        {
            var def = b.Def;
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(Path.Combine(root, "floors"));
            foreach (var kv in floors) File.WriteAllText(Path.Combine(root, "floors", kv.Key + ".json"), kv.Value);

            var fields = new JsonObject
            {
                ["presetName"] = def.Id,
                ["name"] = def.Id,
                ["floorLayouts"] = new JsonArray(layouts.Select((l, i) => (JsonNode)Layout(l, n => "floors/" + n + ".json", i == 0)).ToArray()),
            };
            if (def.ReplaceBasementLayouts)
                fields["basementLayouts"] = new JsonArray(def.BasementLayouts.Select(l => (JsonNode)Layout(l, n => "floors/" + n + ".json", false)).ToArray());
            Overrides(fields, def, ws, (type, name) => name);
            Write(Path.Combine(root, Safe(def.Id) + ".json"), new JsonObject { [def.BasePreset + "__clone"] = fields });

            File.WriteAllText(Path.Combine(root, "README.md"),
$@"# {def.GameName()} for UniversalObjectLoader

UniversalObjectLoader (truedartt) is a library: it has no folder it loads by itself, so a plugin has to load this file.
`{Safe(def.Id)}.json` is a BuildingPreset cloned from {def.BasePreset} (`""{def.BasePreset}__clone""`), with the floor plans
as TextAsset paths (`floors/…json`). The base building's outside model is kept (UOL can't load OBJ models or PNG textures).

```csharp
using static ObjectLoader.Loader;
// in your plugin, e.g. a postfix on CityData.ParseFloorData, with this folder next to your DLL:
SetPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), ""{Safe(def.Id)}""));
var preset = LoadFromJson<BuildingPreset>(Path.Combine(dir, ""{Safe(def.Id)}.json""));
AssetLoader.Instance.allBuildingData.Add(preset);
// the game reads floor plans by name from CityData.floorData; add each file under floors/ there too:
foreach (var file in Directory.GetFiles(Path.Combine(dir, ""floors""), ""*.json""))
    CityData.Instance.floorData[Path.GetFileNameWithoutExtension(file)] =
        JsonUtility.FromJson<FloorSaveData>(File.ReadAllText(file));
```

Not included compared with the CustomBuildings plugin: the custom exterior, building names in the text tables, reserved
places, window detection.
");
            notes.Add("UniversalObjectLoader: only a BuildingPreset JSON and the floor plans (it's a library; a modder's plugin has to load them). The base building's look is kept.");
            return root;
        }
    }
}

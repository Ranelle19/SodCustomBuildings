// Shared between the in-game plugin and the editor. No Unity / game references here.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomBuildings.Shared
{
    /// <summary>
    /// One custom building, stored as Buildings/&lt;Id&gt;/building.json inside the mod folder.
    /// Floor plans used by it live in Buildings/&lt;Id&gt;/floors/&lt;FloorName&gt;.json (the game's own FloorSaveData format).
    /// </summary>
    public class BuildingDef
    {
        public const int CurrentFormat = 1;

        public int Format { get; set; } = CurrentFormat;

        /// <summary>Unique preset name the game will use. Prefix it (e.g. "cb_") so it never clashes with vanilla.</summary>
        public string Id { get; set; } = "cb_NewBuilding";

        /// <summary>Vanilla BuildingPreset (presetName) to clone: exterior model, textures, windows, stairwells…</summary>
        public string BasePreset { get; set; } = "";

        /// <summary>
        /// Set (in memory only) when this building is one of our city wall pieces from the Edge folder; see
        /// Shared/EdgePieces.cs. Never saved in building.json.
        /// </summary>
        [JsonIgnore]
        public EdgePieceDef Edge { get; set; }

        /// <summary>Name the game shows for this kind of building (city editor, map), e.g. "Shopping centre".</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>DisplayName, or a readable version of the Id ("cb_ShoppingCenter" → "Shopping Center").</summary>
        public string GameName()
        {
            if (!string.IsNullOrWhiteSpace(DisplayName)) return DisplayName.Trim();
            var id = Id ?? "";
            if (id.StartsWith("cb_", StringComparison.OrdinalIgnoreCase)) id = id.Substring(3);
            var sb = new StringBuilder();
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                if (c == '_' || c == '-') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(id[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            var s = sb.ToString().Trim();
            return s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : "Custom building";
        }

        /// <summary>When true the floor layouts below replace the base building's layouts. When false the base layouts are kept.</summary>
        public bool ReplaceFloorLayouts { get; set; } = true;
        public List<FloorLayoutDef> FloorLayouts { get; set; } = new();

        /// <summary>Units that always become a particular kind of place (an AddressPreset, e.g. "PawnShop" or a mod's own).</summary>
        public List<ReservedPlace> ReservedPlaces { get; set; } = new();

        public bool ReplaceBasementLayouts { get; set; } = false;
        public List<FloorLayoutDef> BasementLayouts { get; set; } = new();

        /// <summary>Optional changes on top of the cloned preset. Null = keep the base building's value.</summary>
        public BuildingOverrides Overrides { get; set; } = new();
    }

    /// <summary>Mirrors BuildingPreset.InteriorFloorSetting: a run of floors that pick from a set of blueprints.</summary>
    public class FloorLayoutDef
    {
        /// <summary>How many floors use this setting (InteriorFloorSetting.floorsWithThisSetting).</summary>
        public int Floors { get; set; } = 1;

        /// <summary>
        /// Floor plan names. A name is looked up first among this building's own floors, then among every floor the game knows
        /// (vanilla and other mods). The game picks one of them per floor.
        /// </summary>
        public List<string> Blueprints { get; set; } = new();
    }

    public class BuildingOverrides
    {
        [Category("General"), Description("Turn the building off without deleting it.")]
        public bool? Disable { get; set; }

        [Category("General"), Description("Zone shown on the map (BuildingPreset.displayedZone).")]
        public ZoneType? Zone { get; set; }

        [Category("Placement"), Description("Lowest city density this building can appear in.")]
        public Density? DensityMinimum { get; set; }

        [Category("Placement"), Description("Highest city density this building can appear in.")]
        public Density? DensityMaximum { get; set; }

        [Category("Placement"), Description("Lowest land value this building can appear on.")]
        public LandValue? LandValueMinimum { get; set; }

        [Category("Placement"), Description("Highest land value this building can appear on.")]
        public LandValue? LandValueMaximum { get; set; }

        [Category("Placement"), Description("Minimum number of this building in a city.")]
        public int? Minimum { get; set; }

        [Category("Placement"), Description("Hard maximum number of this building in a city.")]
        public int? HardLimit { get; set; }

        [Category("Placement"), Description("How common the building is compared with others.")]
        public float? DesiredRatio { get; set; }

        [Category("Placement"), Description("Priority when the generator places buildings.")]
        public int? FeatureImportance { get; set; }

        [Category("Placement"), Description("Allow the building in every district type.")]
        public bool? AllowedInAllDistricts { get; set; }

        [Category("Stairs and lift"), TypeConverter(typeof(StairsSourceConverter)),
         Description("Use the stairwell of another game building. Stairs and lift come together from the building's stairwell " +
                     "(placing Stairwell in the floor plan is enough; the lift comes with it). Empty = the base building's. " +
                     "Only needed to get a different look; the editor marks which have a lift.")]
        public string StairsFrom { get; set; }

        /// <summary>If set, the building is named from this list (BuildingPreset.possibleNames + overrideNaming).</summary>
        [Browsable(false)]
        public List<string> PossibleNames { get; set; }
    }

    /// <summary>One unit of a floor plan that the game should always turn into the given kind of place.</summary>
    public class ReservedPlace
    {
        /// <summary>Floor plan name (file name).</summary>
        public string Floor { get; set; } = "";
        /// <summary>Address number as the editor shows it (1 = the first address of the floor file).</summary>
        public int Address { get; set; }
        /// <summary>AddressPreset name (catalog Places).</summary>
        public string Place { get; set; } = "";
    }

    /// <summary>A kind of place the game can give a unit (AddressPreset), incl. ones mods add at game start.</summary>
    public class CatalogPlace
    {
        public string Name { get; set; } = "";
        /// <summary>Address types (LayoutConfiguration, the p_n in floor files) it can go into.</summary>
        public List<string> Fits { get; set; } = new();
        /// <summary>Only in these buildings (empty = any).</summary>
        public List<string> OnlyIn { get; set; } = new();
        public int MinFloor { get; set; }
        public int MaxFloor { get; set; }
        public string Company { get; set; } = "";
    }

    /// <summary>Dropdown for StairsFrom: game buildings, "(lift)" after those whose stairs have one. Filled by the editor.</summary>
    public class StairsSourceConverter : StringConverter
    {
        public static List<string> Choices { get; } = new();
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => false;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context) => new(Choices);
        public override object ConvertFrom(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value)
        {
            // "OneFIfthAve  (lift)" → "OneFIfthAve"
            if (value is string s) { int i = s.IndexOf("  (", StringComparison.Ordinal); s = (i >= 0 ? s.Substring(0, i) : s).Trim(); return s.Length == 0 ? null : s; }
            return base.ConvertFrom(context, culture, value);
        }
    }

    // Same order and values as the game's BuildingPreset enums.
    public enum ZoneType { residential = 0, commercial = 1, industrial = 2, municipal = 3, publicProperty = 4, privateProperty = 5 }
    public enum Density { low = 0, medium = 1, high = 2, veryHigh = 3 }
    public enum LandValue { veryLow = 0, low = 1, medium = 2, high = 3, veryHigh = 4 }

    /// <summary>Written by the plugin to Export/catalog.json so the editor knows what the game contains.</summary>
    public class Catalog
    {
        public string GameVersion { get; set; } = "";
        public List<CatalogBuilding> Buildings { get; set; } = new();
        public List<string> Floors { get; set; } = new();
        public List<string> AddressPresets { get; set; } = new();
        public List<string> DoorPairPresets { get; set; } = new();
        /// <summary>Wall/door/window presets by the id floor files use (w_d[].p_n is this id, not the name).</summary>
        public Dictionary<string, string> WallPresetNames { get; set; } = new();
        /// <summary>For each address type (LayoutConfiguration, the a_d[].p_n in floor files): the room types it uses.</summary>
        public Dictionary<string, List<string>> LayoutRooms { get; set; } = new();
        public List<string> LayoutConfigurations { get; set; } = new();
        public List<string> RoomConfigurations { get; set; } = new();
        public List<string> RoomTypePresets { get; set; } = new();
        /// <summary>Every kind of place the game knows when a city is made (Toolbox.allAddressPresets), with where it fits.</summary>
        public List<CatalogPlace> Places { get; set; } = new();
    }

    /// <summary>
    /// Written next to each exported building model (Export/models/&lt;preset&gt;/model.json). The model itself is model.obj +
    /// model.mtl + PNG textures, in the usual OBJ convention (X mirrored from Unity, so Blender shows it the right way round).
    /// </summary>
    public class ModelInfo
    {
        /// <summary>Raised when the exporter changes, so older (incomplete) exports are redone.</summary>
        public const int CurrentExportFormat = 3;
        public int ExportFormat { get; set; }
        /// <summary>
        /// Only one detail level per LOD group was exported (the most detailed one that could be read). Edge pieces need
        /// it: their most detailed level can't be read and the others overlap. Exports without it are redone for them.
        /// </summary>
        public bool OneLod { get; set; }
        /// <summary>Which detail level that was, per LOD group (0 = the most detailed).</summary>
        public List<int> LodLevels { get; set; } = new();
        public string Preset { get; set; } = "";
        public string GameVersion { get; set; } = "";
        public int FloorCount { get; set; }
        public float MeshHeight { get; set; }
        public float BuildingHeight { get; set; }
        /// <summary>Bounds in OBJ space.</summary>
        public float[] Min { get; set; } = new float[3];
        public float[] Max { get; set; } = new float[3];
        public List<ModelPart> Parts { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        /// <summary>The building's window map, add-on map and unlit emission map (full size), to learn their format.</summary>
        public string WindowMap { get; set; }
        public string AddonMap { get; set; }
        public string EmissionUnlit { get; set; }
        /// <summary>What the game made of the window map: windows per storey and side, and a few sample windows.</summary>
        public List<string> WindowSummary { get; set; } = new();
    }

    public class ModelPart
    {
        /// <summary>Object name in the OBJ (the child GameObject's name).</summary>
        public string Name { get; set; } = "";
        public int Vertices { get; set; }
        public int Triangles { get; set; }
        /// <summary>True for the main building shell (the "Base" child that uses the preset's textures).</summary>
        public bool IsBase { get; set; }
    }

    /// <summary>
    /// A generated exterior: Buildings/&lt;id&gt;/exterior/exterior.json next to exterior.obj/.mtl and four PNGs that share one UV
    /// layout (diffuse, lit = night with windows on, unlit = neon only, windows = window mask). The OBJ uses the same
    /// convention as the plugin's model export (X mirrored), so the editor's preview and loader treat both the same.
    /// </summary>
    public class ExteriorInfo
    {
        public const int CurrentFormat = 1;
        public int Format { get; set; } = CurrentFormat;
        public string Style { get; set; } = "NeonArcade";
        public int Storeys { get; set; }
        public float CellSize { get; set; } = 1.8f;
        public float StoreyHeight { get; set; } = 5.4f;
        /// <summary>Height of the ground floor in a building model (game models start at about 5.3).</summary>
        public float GroundY { get; set; } = 5.4f;
        /// <summary>Top of the walls (without parapet and signs).</summary>
        public float WallTopY { get; set; }
        /// <summary>Flip left/right when the model is placed in game (in case the game's plan → model mapping is mirrored).</summary>
        public bool MirrorX { get; set; }
        public string SignText { get; set; } = "";
        public string Obj { get; set; } = "exterior.obj";
        public string Diffuse { get; set; } = "diffuse.png";
        public string Lit { get; set; } = "lit.png";
        public string Unlit { get; set; } = "unlit.png";
        public string WindowMap { get; set; } = "windows.png";
        public int Windows { get; set; }
        /// <summary>Side of the plan the main entrance (and the rooftop sign) faces: N = plan top, S = bottom, W = left, E = right.</summary>
        public string FrontSide { get; set; } = "";
        /// <summary>Size of the textures and where each wall, sign and roof piece sits in them (for the exterior painter).</summary>
        public int TextureWidth { get; set; }
        public int TextureHeight { get; set; }
        public List<ExteriorPanel> Panels { get; set; } = new();
        /// <summary>Ledges, pillars, balconies, bay windows and window frames added in the painter (the "Shapes" part of the OBJ).</summary>
        public List<ExteriorShape> Shapes { get; set; } = new();
        /// <summary>True once the textures were changed in the exterior painter (or imported).</summary>
        public bool Painted { get; set; }
    }

    /// <summary>
    /// A 3D shape on a wall: a box (or a few) sticking out of it. Placed on a wall (Side/Line/From/To like its panels),
    /// U = distance from the wall's left end as seen from outside, Y = height in the model, both in model units.
    /// </summary>
    public class ExteriorShape
    {
        /// <summary>Ledge, Pillar, Balcony, Bay, Frame.</summary>
        public string Kind { get; set; } = "Ledge";
        public string Side { get; set; } = "";
        public int Line { get; set; }
        public int From { get; set; }
        public int To { get; set; }
        public float U0 { get; set; }
        public float U1 { get; set; }
        public float Y0 { get; set; }
        public float Y1 { get; set; }
        /// <summary>How far it sticks out of the wall.</summary>
        public float Depth { get; set; } = 0.3f;

        public ExteriorShape Clone() => (ExteriorShape)MemberwiseClone();
    }

    /// <summary>One area of the exterior textures: a wall of one storey, a parapet, the sign, the roof…</summary>
    public class ExteriorPanel
    {
        /// <summary>Wall, Parapet, Roof, Sign, SignBack, Awning, Blade, Box.</summary>
        public string Kind { get; set; } = "";
        /// <summary>N, S, W or E for walls and parapets (which side of the floor plan they face).</summary>
        public string Side { get; set; } = "";
        public int Storey { get; set; }
        /// <summary>Plan row (N/S) or column (W/E) of the wall and the cells it spans.</summary>
        public int Line { get; set; }
        public int From { get; set; }
        public int To { get; set; }
        /// <summary>Pixel rectangle in the textures (top-left origin). The left edge is the left end as seen from outside.</summary>
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }

        [JsonIgnore] public string Key => $"{Kind}|{Side}|{Storey}|{Line}|{From}|{To}|{W}|{H}";
    }

    public class CatalogBuilding
    {
        public string PresetName { get; set; } = "";
        public int FloorCount { get; set; }
        public string Zone { get; set; } = "";
        public bool NonEnterable { get; set; }
        public bool Boundary { get; set; }
        public bool Custom { get; set; }
        /// <summary>The building's stairwells (StairwellPreset names) and whether they include a lift.</summary>
        public string Stairwell { get; set; } = "";
        public bool StairwellHasLift { get; set; }
        public string LargeStairwell { get; set; } = "";
        public bool LargeStairwellHasLift { get; set; }
        public List<FloorLayoutDef> FloorLayouts { get; set; } = new();
        public List<FloorLayoutDef> BasementLayouts { get; set; } = new();

        /// <summary>Storeys the game's own layouts for this building cover (what custom layouts should match).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int LayoutStoreys => FloorLayouts.Sum(l => Math.Max(1, l.Floors));
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // enums as text ("residential"); applies to the nullable override fields too
            Converters = { new JsonStringEnumConverter() },
        };
    }

    /// <summary>
    /// Short fingerprint of everything in the Buildings folder. Plugin and editor use this same code, so players can
    /// compare it: in multiplayer every player must have the same hash.
    /// </summary>
    public static class ContentHash
    {
        public static List<string> Files(string buildingsRoot)
        {
            var files = new List<string>();
            if (!Directory.Exists(buildingsRoot)) return files;
            foreach (var dir in Directory.GetDirectories(buildingsRoot))
            {
                var b = Path.Combine(dir, ModPaths.BuildingFile);
                if (File.Exists(b)) files.Add(b);
                var f = Path.Combine(dir, ModPaths.FloorsDir);
                if (Directory.Exists(f)) files.AddRange(Directory.GetFiles(f, "*.json"));
                var ex = Path.Combine(dir, ModPaths.ExteriorDir);
                if (Directory.Exists(ex)) files.AddRange(Directory.GetFiles(ex));
            }
            // the city edge (Edge folder next to Buildings): our wall pieces, their piers and models
            var modDir = Path.GetDirectoryName(Path.GetFullPath(buildingsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (modDir != null) files.AddRange(EdgeStore.HashFiles(EdgeStore.Root(modDir)));
            return files;
        }

        public static string Compute(string buildingsRoot)
        {
            var entries = Files(buildingsRoot)
                .Select(p => (rel: Path.GetRelativePath(buildingsRoot, p).Replace('\\', '/'), path: p))
                .OrderBy(e => e.rel, StringComparer.Ordinal);
            using var sha = SHA256.Create();
            foreach (var (rel, path) in entries)
            {
                var name = Encoding.UTF8.GetBytes(rel + "\n");
                var data = File.ReadAllBytes(path);
                sha.TransformBlock(name, 0, name.Length, null, 0);
                sha.TransformBlock(data, 0, data.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash).Substring(0, 12);
        }
    }

    /// <summary>Folder layout of the mod, relative to BepInEx/plugins/CustomBuildings.</summary>
    public static class ModPaths
    {
        public const string BuildingsDir = "Buildings";
        public const string BuildingFile = "building.json";
        public const string FloorsDir = "floors";
        public const string ExportDir = "Export";
        public const string ExportFloorsDir = "floors";
        public const string CatalogFile = "catalog.json";
        public const string ModelsDir = "models";
        public const string ModelInfoFile = "model.json";
        public const string ModelObjFile = "model.obj";
        public const string ModelMtlFile = "model.mtl";
        public const string ExteriorDir = "exterior";
        public const string ExteriorInfoFile = "exterior.json";
        public const string ExteriorObjFile = "exterior.obj";
        public const string ExteriorMtlFile = "exterior.mtl";

        /// <summary>Folder name under Export/models for a building preset (plugin and editor must agree).</summary>
        public static string ModelFolder(string preset)
        {
            var sb = new StringBuilder(preset.Length);
            foreach (var c in preset) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}

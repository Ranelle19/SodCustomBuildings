// Shared between the in-game plugin and the editor. No Unity / game references here.
//
// THE CITY EDGE — everything about the sea wall around the city lives in the mod's Edge folder, apart from buildings:
//
//   BepInEx\plugins\CustomBuildings\Edge\
//     <Id>\piece.json            our own wall pieces (EdgePieceDef below), e.g. Edge\BoundaryCoastal03\piece.json
//     <Id>\model\                the piece's own wall model (OBJ + MTL + PNGs), see EdgeModelInfo
//
//  * The game builds the edge from its own wall pieces, which are ordinary building types marked "boundary":
//    BoundaryCoastal01 (straight), BoundaryCoastal02 (with a square platform), BoundaryCorner01 (corner); see
//    GameEdgePieces. Our wall piece is a copy of the one for its Role, registered under its own Id, so the city
//    generator can place it; Minimum / HardLimit say how many each new city gets.
//  * In the plugin, a wall piece becomes an in-memory BuildingDef (ToBuildingDef) and goes through the same code as
//    buildings (copying the preset, limits, later its own exterior). BuildingDef.Edge points back to the EdgePieceDef.
//  * Things built at every placed copy (in game, once a city has loaded) are properties of EdgePieceDef:
//      - Pier: a pier (PierDef, Shared/Piers.cs) built at that piece; edited in Edge > Piers.
//    To add another kind (a crane, a boat, stairs, a kiosk…): add a property to EdgePieceDef, then handle it in the
//    plugin's EdgePieces.OnPlaced (Plugin/EdgePieces.cs), which gets every placed copy with its side of the city.
//  * Everything in the Edge folder is part of the multiplayer content hash (ContentHash in BuildingDef.cs).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomBuildings.Shared
{
    public enum EdgeRole { Straight, Platform, Corner }

    /// <summary>One of our own pieces of the city's sea wall, stored as Edge\&lt;Id&gt;\piece.json.</summary>
    public class EdgePieceDef
    {
        [Browsable(false)]
        public int Format { get; set; } = 1;

        [Category("1. Piece"), Description("Name of this wall piece in the game. Must not be one of the game's (BoundaryCoastal01, BoundaryCoastal02, BoundaryCorner01).")]
        public string Id { get; set; } = "BoundaryCoastal03";

        [Category("1. Piece"), Description("Name shown in the game.")]
        public string DisplayName { get; set; } = "Pier wall";

        [Category("1. Piece"), Description("Untick to keep it in the folder but not give it to the game.")]
        public bool Enabled { get; set; } = true;

        [Category("1. Piece"), Description("Straight = a normal stretch of wall, Platform = with a square platform, Corner = a corner. " +
                                           "It's a copy of the game's piece of that kind and takes its place in the city.")]
        public EdgeRole Role { get; set; } = EdgeRole.Straight;

        [Category("2. How many"), Description("At least this many in every new city (the plugin picks the tiles; the game itself ignores limits on edge pieces).")]
        public int Minimum { get; set; } = 1;

        [Category("2. How many"), Description("At most this many in a city; each city gets a random number from Minimum to HardLimit. 0 = exactly Minimum.")]
        public int HardLimit { get; set; } = 1;

        /// <summary>
        /// The starter pier: built by the plugin while the piece has no model of its own (and written into the starter
        /// model the editor exports for Blender). Once a model is imported, the pier is part of the model.
        /// </summary>
        [Browsable(false)]
        public PierDef Pier { get; set; }

        /// <summary>Its own wall model (imported OBJ), or null = looks like the game's wall piece.</summary>
        [Browsable(false)]
        public EdgeModelInfo Model { get; set; }



        [Category("3. Notes"), Description("Free notes, for you.")]
        public string Notes { get; set; } = "";

        /// <summary>The game building this piece copies.</summary>
        [JsonIgnore, Browsable(false)]
        public string BasePreset => GameEdgePieces.BaseFor(Role);

        /// <summary>As a building for the plugin's building code (never saved as a building).</summary>
        public BuildingDef ToBuildingDef() => new()
        {
            Id = Id,
            BasePreset = BasePreset,
            DisplayName = DisplayName,
            Edge = this,
            Overrides = new BuildingOverrides { Minimum = Minimum, HardLimit = HardLimit },
        };

        public EdgePieceDef Clone()
        {
            var c = (EdgePieceDef)MemberwiseClone();
            c.Pier = Pier?.Clone();
            c.Model = Model?.Clone();
            return c;
        }

        public override string ToString() => $"{Id} ({Role}{(Enabled ? "" : ", off")}{(Pier != null ? ", pier" : "")})";
    }

    /// <summary>
    /// A wall piece's own model, in Edge\&lt;Id&gt;\model\. THE FRAME: the same as the template the editor exports
    /// (Export template…), i.e. the game wall piece's own model space as our OBJ exports write it: origin in the middle
    /// of the edge tile at height 0 (street ≈ 0.2), Y up, the wall runs along X from -18.9 to +18.9 (37.8 m, one tile),
    /// and Z goes across it; the sea is on the side the template's wall face is on. OBJ files we write have X mirrored
    /// (the usual Unity → OBJ conversion); the plugin undoes that when loading. Import checks it against the template.
    /// </summary>
    public class EdgeModelInfo
    {
        public string Obj { get; set; } = "model.obj";
        public string Mtl { get; set; } = "model.mtl";
        /// <summary>Applied when loading: unit fix (cm → m is 0.01) found by the import check.</summary>
        public float Scale { get; set; } = 1;
        /// <summary>The file was saved Z-up (a Blender export setting); turned back to Y-up when loading.</summary>
        public bool SwapYZ { get; set; }
        /// <summary>Also keep the game's wall (add the model to it) instead of replacing it.</summary>
        public bool KeepGameWall { get; set; }
        /// <summary>What the import check said, for the record.</summary>
        public List<string> CheckReport { get; set; } = new();

        public const string Folder = "model";

        public EdgeModelInfo Clone()
        {
            var c = (EdgeModelInfo)MemberwiseClone();
            c.CheckReport = new List<string>(CheckReport ?? new List<string>());
            return c;
        }
    }

    /// <summary>The game's own edge pieces (measured in game, plugin 1.4.2 edge report).</summary>
    public static class GameEdgePieces
    {
        public const string Straight = "BoundaryCoastal01";
        public const string Platform = "BoundaryCoastal02";
        public const string Corner = "BoundaryCorner01";

        /// <summary>Length of one edge tile along the wall (the city's tile size).</summary>
        public const float TileLength = 37.8f;
        /// <summary>The walkway is at street level (~0.2 m); the sea is 12 m below it; the game makes you wade below -9.6 m.</summary>
        public const float StreetY = 0.2f, SeaY = -12f, WadeY = -9.6f;

        public static string BaseFor(EdgeRole role) => role switch
        {
            EdgeRole.Platform => Platform, EdgeRole.Corner => Corner, _ => Straight,
        };

        public static readonly IReadOnlyList<string> All = new[] { Straight, Platform, Corner };
    }

    /// <summary>Reading and writing the Edge folder.</summary>
    public static class EdgeStore
    {
        public const string DirName = "Edge";
        public const string PieceFile = "piece.json";

        public static string Root(string modDir) => Path.Combine(modDir, DirName);

        /// <summary>All wall pieces with the folder each was read from.</summary>
        public static List<(EdgePieceDef def, string dir)> LoadPieces(string edgeRoot, Action<string> error = null)
        {
            var list = new List<(EdgePieceDef, string)>();
            if (!Directory.Exists(edgeRoot)) return list;
            foreach (var dir in Directory.GetDirectories(edgeRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var f = Path.Combine(dir, PieceFile);
                if (!File.Exists(f)) continue;
                try
                {
                    var def = JsonSerializer.Deserialize<EdgePieceDef>(File.ReadAllText(f, Encoding.UTF8).TrimStart('﻿'), Json.Options);
                    if (def == null || string.IsNullOrWhiteSpace(def.Id)) { error?.Invoke($"{f}: missing Id, skipped."); continue; }
                    list.Add((def, dir));
                }
                catch (Exception e) { error?.Invoke($"{f}: {e.Message}"); }
            }
            return list;
        }

        public static string PieceDir(string edgeRoot, string id) => Path.Combine(edgeRoot, Safe(id));

        public static void SavePiece(string edgeRoot, EdgePieceDef def)
        {
            var dir = PieceDir(edgeRoot, def.Id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, PieceFile), JsonSerializer.Serialize(def, Json.Options), new UTF8Encoding(false));
        }

        /// <summary>Removes the piece's folder (its piece.json and exterior).</summary>
        public static void DeletePiece(string edgeRoot, string id)
        {
            var dir = PieceDir(edgeRoot, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        /// <summary>
        /// Tidies up after earlier versions: removes the old piers.json (piers on a side of every city), and moves wall pieces
        /// saved as buildings (plugin 1.7.0: building.json with "Edge") → Edge\&lt;Id&gt;\piece.json. Returns what it moved.
        /// </summary>
        public static List<string> Migrate(string modDir)
        {
            var moved = new List<string>();
            var buildings = Path.Combine(modDir, ModPaths.BuildingsDir);
            var edge = Root(modDir);
            try
            {
                // piers on a side of every city (piers.json, plugin 1.5–1.8) were replaced by piers on our wall pieces
                foreach (var old in new[] { Path.Combine(buildings, "piers.json"), Path.Combine(edge, "piers.json") })
                    if (File.Exists(old)) { File.Delete(old); moved.Add($"removed the old piers ({old})"); }
                if (!Directory.Exists(buildings)) return moved;
                foreach (var dir in Directory.GetDirectories(buildings))
                {
                    var bf = Path.Combine(dir, ModPaths.BuildingFile);
                    if (!File.Exists(bf)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(bf, Encoding.UTF8).TrimStart('﻿'),
                        new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    var r = doc.RootElement;
                    if (!TryGet(r, "Edge", out var e) || e.ValueKind != JsonValueKind.Object) continue;
                    var def = JsonSerializer.Deserialize<EdgePieceDef>(e.GetRawText(), Json.Options) ?? new EdgePieceDef();
                    if (TryGet(r, "Id", out var id)) def.Id = id.GetString();
                    if (TryGet(r, "DisplayName", out var dn) && dn.ValueKind == JsonValueKind.String) def.DisplayName = dn.GetString();
                    if (TryGet(r, "Overrides", out var o) && o.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGet(o, "Minimum", out var mn) && mn.ValueKind == JsonValueKind.Number) def.Minimum = mn.GetInt32();
                        if (TryGet(o, "HardLimit", out var hl) && hl.ValueKind == JsonValueKind.Number) def.HardLimit = hl.GetInt32();
                    }
                    doc.Dispose();
                    if (Directory.Exists(PieceDir(edge, def.Id))) continue;   // already moved
                    SavePiece(edge, def);
                    var ext = Path.Combine(dir, ModPaths.ExteriorDir);
                    if (Directory.Exists(ext)) Directory.Move(ext, Path.Combine(PieceDir(edge, def.Id), ModPaths.ExteriorDir));
                    Directory.Delete(dir, true);
                    moved.Add($"moved {def.Id} → Edge");
                }
            }
            catch (Exception ex) { moved.Add("(moving old edge files failed: " + ex.Message + ")"); }
            return moved;
        }

        private static bool TryGet(JsonElement e, string name, out JsonElement value)
        {
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
            value = default;
            return false;
        }

        private static string Safe(string id)
        {
            var sb = new StringBuilder();
            foreach (var c in id ?? "") sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            return sb.Length == 0 ? "_" : sb.ToString();
        }

        /// <summary>Every file in the Edge folder, for the content hash.</summary>
        public static IEnumerable<string> HashFiles(string edgeRoot)
            => Directory.Exists(edgeRoot) ? Directory.GetFiles(edgeRoot, "*", SearchOption.AllDirectories) : Array.Empty<string>();
    }
}

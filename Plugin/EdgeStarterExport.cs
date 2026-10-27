using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CustomBuildings.Shared;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// Writes one of our wall pieces exactly as it stands in the game, before it has a model of its own: the game's wall
    /// (the detail level that can be read, with the opening cut for the stairs) plus the starter pier, as one OBJ with
    /// its MTL and PNGs, in Export\edge\&lt;Id&gt;\. That's the "as it is in the game" choice of the editor's Export for
    /// Blender, so the pier becomes part of the piece's own model. The frame is the one in EdgeModelInfo (the game
    /// model's own space, X mirrored like all our OBJ exports), so importing it back puts everything where it was.
    /// Written again when the game version, the plugin version or the pier's settings change.
    /// </summary>
    internal static class EdgeStarterExport
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly HashSet<string> DoneThisRun = new(StringComparer.OrdinalIgnoreCase);

        private sealed class Stamp
        {
            public string GameVersion { get; set; } = "";
            public string Plugin { get; set; } = "";
            public string Pier { get; set; } = "";
        }

        internal static void Export(NewBuilding piece, EdgePieceDef def, GameObject pier)
        {
            if (piece == null || def == null || DoneThisRun.Contains(def.Id)) return;
            var outDir = Path.Combine(CustomBuildings.Plugin.ModDir, ModPaths.ExportDir, "edge", Safe(def.Id));
            var stamp = new Stamp
            {
                GameVersion = Application.version,
                Plugin = CustomBuildings.Plugin.Version,
                Pier = def.Pier == null ? "" : JsonSerializer.Serialize(def.Pier, Json.Options),
            };
            var stampFile = Path.Combine(outDir, "starter.json");
            try
            {
                if (File.Exists(stampFile) && File.Exists(Path.Combine(outDir, ModPaths.ModelObjFile)))
                {
                    var old = JsonSerializer.Deserialize<Stamp>(File.ReadAllText(stampFile), Json.Options);
                    if (old != null && old.GameVersion == stamp.GameVersion && old.Plugin == stamp.Plugin && old.Pier == stamp.Pier)
                    {
                        DoneThisRun.Add(def.Id);
                        return;
                    }
                }
            }
            catch { }

            // the game model's root under the piece: everything is written in its space
            Transform root = null;
            var prefabName = piece.preset.prefab != null ? piece.preset.prefab.name : null;
            for (int i = 0; i < piece.transform.childCount && root == null; i++)
            {
                var c = piece.transform.GetChild(i);
                if (prefabName != null && c.name.StartsWith(prefabName, StringComparison.Ordinal)) root = c;
            }
            root ??= piece.transform;
            var toRoot = root.worldToLocalMatrix;

            // the wall: one detail level per LOD group (the most detailed one that can be read: the cut one)
            var skip = ModelExporter.PickLods(root.gameObject, new ModelInfo());
            var renderers = new List<MeshRenderer>();
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                if (r != null && !skip.Contains(r.Pointer)) renderers.Add(r);
            if (pier != null) renderers.AddRange(pier.GetComponentsInChildren<MeshRenderer>(true).Where(r => r != null && r.gameObject.activeInHierarchy));

            Directory.CreateDirectory(outDir);
            foreach (var f in Directory.GetFiles(outDir)) File.Delete(f);
            var obj = new StringBuilder();
            var mtl = new StringBuilder();
            obj.AppendLine($"# {def.Id} as it stands in Shadows of Doubt {Application.version} (the game's wall + the starter pier), CustomBuildings {CustomBuildings.Plugin.Version}");
            obj.AppendLine("# Frame: the wall piece's own model space, X mirrored (see README-wall-piece.txt). Don't move the whole thing.");
            obj.AppendLine($"mtllib {ModPaths.ModelMtlFile}");
            var texFiles = new Dictionary<IntPtr, string>();
            var matNames = new Dictionary<IntPtr, string>();
            var info = new ModelInfo();
            int vBase = 1, parts = 0, tris = 0;

            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf?.sharedMesh;
                if (mesh == null) continue;
                ModelExporter.MeshData data;
                try { data = ModelExporter.ReadMesh(mesh); }
                catch { continue; }   // the unreadable detail level: its cut copy is the one we keep
                if (data == null || data.Pos.Length == 0) continue;
                bool isPier = pier != null && r.transform.IsChildOf(pier.transform);
                var m = toRoot * r.transform.localToWorldMatrix;
                obj.AppendLine($"o {(isPier ? "Pier_" : "Wall_")}{Clean(r.gameObject.name)}_{parts++}");
                foreach (var v in data.Pos)
                {
                    var p = m.MultiplyPoint3x4(v);
                    obj.Append("v ").Append(F(-p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
                }
                bool hasUv = data.Uv != null && data.Uv.Length == data.Pos.Length;
                bool hasN = data.Nrm != null && data.Nrm.Length == data.Pos.Length;
                if (hasUv) foreach (var t in data.Uv) obj.Append("vt ").Append(F(t.x)).Append(' ').Append(F(t.y)).Append('\n');
                if (hasN)
                    foreach (var n in data.Nrm)
                    {
                        var q = m.MultiplyVector(n).normalized;
                        obj.Append("vn ").Append(F(-q.x)).Append(' ').Append(F(q.y)).Append(' ').Append(F(q.z)).Append('\n');
                    }
                var mats = r.sharedMaterials;
                for (int s = 0; s < data.Tris.Count; s++)
                {
                    var mat = s < mats.Length ? mats[s] : r.sharedMaterial;
                    obj.AppendLine($"usemtl {MaterialName(mat, outDir, mtl, matNames, texFiles, info)}");
                    var t = data.Tris[s];
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        obj.Append('f');
                        foreach (var idx in new[] { t[i], t[i + 2], t[i + 1] })   // X mirrored: reverse the winding
                        {
                            int k = vBase + idx;
                            obj.Append(' ').Append(k);
                            if (hasUv || hasN) obj.Append('/').Append(hasUv ? k.ToString(Inv) : "");
                            if (hasN) obj.Append('/').Append(k);
                        }
                        obj.Append('\n');
                        tris++;
                    }
                }
                vBase += data.Pos.Length;
            }
            if (parts == 0) { CustomBuildings.Plugin.L.LogInfo($"[edge] {def.Id}: nothing readable to write as the starter model"); return; }

            // what you walk on and bump into, as invisible "collision" objects (the wall's collider with its opening, the
            // pier's smooth stair ramps and railing walls): with them the imported model collides exactly like this one
            int cols = 0;
            var colliders = root.GetComponentsInChildren<Collider>(true).ToList();
            if (pier != null) colliders.AddRange(pier.GetComponentsInChildren<Collider>(true));
            foreach (var c in colliders)
            {
                if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy) continue;
                var tri = new List<Vector3>();
                var box = c.TryCast<BoxCollider>();
                var mc = c.TryCast<MeshCollider>();
                if (box != null) tri.AddRange(BoxTriangles(box));
                else if (mc?.sharedMesh != null)
                {
                    ModelExporter.MeshData md;
                    try { md = ModelExporter.ReadMesh(mc.sharedMesh); } catch { continue; }
                    var cm = mc.transform.localToWorldMatrix;
                    foreach (var t in md.Tris)
                        for (int i = 0; i + 2 < t.Length; i += 3)
                        {
                            tri.Add(cm.MultiplyPoint3x4(md.Pos[t[i]])); tri.Add(cm.MultiplyPoint3x4(md.Pos[t[i + 1]])); tri.Add(cm.MultiplyPoint3x4(md.Pos[t[i + 2]]));
                        }
                }
                if (tri.Count == 0) continue;
                obj.AppendLine($"o collision_{cols++}");
                obj.AppendLine("usemtl collision");
                foreach (var w in tri)
                {
                    var p = toRoot.MultiplyPoint3x4(w);
                    obj.Append("v ").Append(F(-p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
                }
                for (int i = 0; i + 2 < tri.Count; i += 3)
                    obj.Append("f ").Append(vBase + i).Append(' ').Append(vBase + i + 2).Append(' ').Append(vBase + i + 1).Append('\n');
                vBase += tri.Count;
            }
            mtl.AppendLine("newmtl collision");
            mtl.AppendLine("Kd 1 0 1");
            mtl.AppendLine();
            File.WriteAllText(Path.Combine(outDir, ModPaths.ModelObjFile), obj.ToString());
            File.WriteAllText(Path.Combine(outDir, ModPaths.ModelMtlFile), mtl.ToString());
            File.WriteAllText(stampFile, JsonSerializer.Serialize(stamp, Json.Options));
            DoneThisRun.Add(def.Id);
            CustomBuildings.Plugin.L.LogInfo($"[edge] {def.Id}: wrote it as it stands (wall + {(pier != null ? "pier" : "no pier")}, {parts} parts, {tris} triangles) " +
                                             $"to {outDir} for the editor's Export for Blender" + (info.Warnings.Count > 0 ? $" ({string.Join("; ", info.Warnings)})" : ""));
        }

        private static string MaterialName(Material mat, string outDir, StringBuilder mtl, Dictionary<IntPtr, string> names,
                                           Dictionary<IntPtr, string> tex, ModelInfo info)
        {
            if (mat == null) return "none";
            if (names.TryGetValue(mat.Pointer, out var n)) return n;
            n = Clean(mat.name) + "_" + names.Count;
            names[mat.Pointer] = n;
            mtl.AppendLine($"newmtl {n}");
            var main = ModelExporter.MainTexture(mat);
            string file = main != null ? ModelExporter.SaveTexture(main, outDir, Clean(main.name) + "_" + tex.Count, 2048, tex, info) : null;
            Color c = Color.white;
            try
            {
                if (mat.HasProperty("_BaseColor")) c = mat.GetColor("_BaseColor");
                else if (mat.HasProperty("_Color")) c = mat.GetColor("_Color");
            }
            catch { }
            mtl.AppendLine($"Kd {F(c.r)} {F(c.g)} {F(c.b)}");
            if (file != null) mtl.AppendLine($"map_Kd {file}");
            mtl.AppendLine();
            return n;
        }

        private static string F(float f) => f.ToString("0.#####", Inv);

        /// <summary>A box collider as 12 world-space triangles, faces pointing out (Unity winding).</summary>
        private static IEnumerable<Vector3> BoxTriangles(BoxCollider b)
        {
            var m = b.transform.localToWorldMatrix;
            var c = b.center; var h = b.size / 2;
            var centre = m.MultiplyPoint3x4(c);
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            var res = new List<Vector3>();
            for (int a = 0; a < 3; a++)
                foreach (var sgn in new[] { 1f, -1f })
                {
                    var n = axes[a] * sgn;
                    var u = axes[(a + 1) % 3]; var v = axes[(a + 2) % 3];
                    Vector3 P(float su, float sv) => m.MultiplyPoint3x4(c + Vector3.Scale(n + u * su + v * sv, h));
                    var q = new[] { P(-1, -1), P(1, -1), P(1, 1), P(-1, 1) };
                    var fc = (q[0] + q[2]) / 2;
                    if (Vector3.Dot(Vector3.Cross(q[1] - q[0], q[2] - q[0]), fc - centre) < 0) Array.Reverse(q);
                    res.AddRange(new[] { q[0], q[1], q[2], q[0], q[2], q[3] });
                }
            return res;
        }

        private static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s ?? "") sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return sb.Length == 0 ? "part" : sb.ToString();
        }

        private static string Safe(string id)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return id;
        }
    }
}

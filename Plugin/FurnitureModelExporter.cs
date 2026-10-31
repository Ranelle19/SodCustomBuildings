using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CustomBuildings.Shared;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// Exports every piece of furniture's model (Export/furniture/&lt;name&gt;/model.obj + .mtl + small textures + model.json),
    /// so the editor's 3D view shows the real thing. A few pieces per frame while no city is loading, so the menu doesn't
    /// freeze; pieces already exported for this game version are skipped. Same OBJ convention as the building models
    /// (prefab space, game units, X mirrored).
    /// </summary>
    [HarmonyPatch(typeof(SessionData), nameof(SessionData.Update))]
    internal static class FurnitureModelExporter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// <summary>Furniture's own export version (4: the prefab's own scale is kept, so scaled mod pieces have the right size).</summary>
        internal const int FurnitureFormat = 104;
        private static readonly Queue<FurniturePreset> Todo = new();
        private static readonly HashSet<string> Queued = new(StringComparer.Ordinal);
        private static int _done, _skipped, _failed, _frames;
        private static bool _reported = true;

        /// <summary>Queues every piece not exported yet (called after the export and before each city load, for mods' pieces).</summary>
        internal static void QueueAll()
        {
            try
            {
                if (Plugin.ExportModels != null && !Plugin.ExportModels.Value) return;
                var all = new List<FurniturePreset>();
                try { var l = AssetLoader.Instance?.GetAllFurniture(); if (l != null) foreach (var f in l) all.Add(f); } catch { }
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<FurniturePreset>()))
                {
                    var f = o?.TryCast<FurniturePreset>();
                    if (f != null && !f.name.EndsWith("(Clone)", StringComparison.Ordinal)) all.Add(f);
                }
                foreach (var f in all)
                    if (f != null && f.prefab != null && !string.IsNullOrEmpty(f.name) && Queued.Add(f.name)) Todo.Enqueue(f);
                if (Todo.Count > 0) _reported = false;
            }
            catch (Exception e) { Plugin.L.LogWarning($"Furniture models: {e.Message}"); }
        }

        private static void Postfix()
        {
            if (Todo.Count == 0)
            {
                if (!_reported)
                {
                    _reported = true;
                    Plugin.L.LogInfo($"Furniture models for the editor: exported {_done}, already up to date {_skipped}, failed {_failed}.");
                }
                return;
            }
            if (++_frames % 2 != 0) return;
            try
            {
                var cc = CityConstructor.Instance;
                if (cc != null && cc.loadingOperationActive) return;   // not while a city loads
            }
            catch { }
            var root = Path.Combine(Plugin.ModDir, ModPaths.ExportDir, ModPaths.FurnitureModelsDir);
            for (int i = 0; i < 3 && Todo.Count > 0; i++)
            {
                var f = Todo.Dequeue();
                if (f == null || f.WasCollected || f.prefab == null) continue;
                var outDir = Path.Combine(root, ModPaths.ModelFolder(f.name));
                try
                {
                    if (UpToDate(outDir)) { _skipped++; continue; }
                    if (Failed(outDir)) { _skipped++; continue; }
                    Export(f, outDir);
                    _done++;
                }
                catch (Exception e)
                {
                    _failed++;
                    Plugin.Verbose($"Furniture model {f.name}: {e.Message}");
                    try { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, "export-failed.txt"), $"{Application.version}|{FurnitureFormat}"); } catch { }
                }
            }
        }

        private static bool UpToDate(string outDir)
        {
            var path = Path.Combine(outDir, ModPaths.ModelInfoFile);
            if (!File.Exists(path)) return false;
            try
            {
                var mi = JsonSerializer.Deserialize<ModelInfo>(File.ReadAllText(path), Json.Options);
                return mi != null && mi.GameVersion == Application.version && mi.ExportFormat == FurnitureFormat && mi.Parts.Count > 0;
            }
            catch { return false; }
        }

        private static bool Failed(string outDir)
        {
            var mark = Path.Combine(outDir, "export-failed.txt");
            return File.Exists(mark) && File.ReadAllText(mark).Trim() == $"{Application.version}|{FurnitureFormat}";
        }

        private static string F(float v) => v.ToString("0.#####", Inv);

        private static void Export(FurniturePreset f, string outDir)
        {
            var prefab = f.prefab;
            var info = new ModelInfo { Preset = f.name, GameVersion = Application.version, ExportFormat = FurnitureFormat };
            var obj = new StringBuilder();
            var mtl = new StringBuilder();
            obj.AppendLine($"# {f.name} (furniture) from Shadows of Doubt {Application.version}, exported by CustomBuildings");
            obj.AppendLine($"mtllib {ModPaths.ModelMtlFile}");
            var texFiles = new Dictionary<IntPtr, string>();
            var materials = new Dictionary<string, string>();
            var min = new System.Numerics.Vector3(float.MaxValue);
            var max = new System.Numerics.Vector3(float.MinValue);
            int vBase = 1, part = 0;
            var root = prefab.transform;
            // the game spawns the prefab with its own scale (mods often scale their piece's root), so keep it
            var rs = root.localScale;
            if (Math.Abs(rs.x) < 1e-6f || Math.Abs(rs.y) < 1e-6f || Math.Abs(rs.z) < 1e-6f) rs = Vector3.one;
            var skip = ModelExporter.PickLods(prefab, info);
            bool dirMade = false;

            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                var go = mf.gameObject;
                var renderer = go.GetComponent<MeshRenderer>();
                if (mesh == null || renderer == null || skip.Contains(renderer.Pointer)) continue;
                ModelExporter.MeshData data;
                try { data = ModelExporter.ReadMesh(mesh); }
                catch (Exception e) { info.Warnings.Add($"{go.name}: {e.Message}"); continue; }
                if (data == null || data.Pos.Length == 0) continue;
                if (!dirMade) { Directory.CreateDirectory(outDir); dirMade = true; }

                var local = mf.transform.localToWorldMatrix;
                var toRoot = root.worldToLocalMatrix;
                string partName = "p" + part++;
                obj.AppendLine($"o {partName}");
                foreach (var v in data.Pos)
                {
                    var p = Vector3.Scale(toRoot.MultiplyPoint3x4(local.MultiplyPoint3x4(v)), rs);
                    obj.Append("v ").Append(F(-p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
                    min = System.Numerics.Vector3.Min(min, new System.Numerics.Vector3(-p.x, p.y, p.z));
                    max = System.Numerics.Vector3.Max(max, new System.Numerics.Vector3(-p.x, p.y, p.z));
                }
                bool hasUv = data.Uv != null && data.Uv.Length == data.Pos.Length;
                if (hasUv) foreach (var t in data.Uv) obj.Append("vt ").Append(F(t.x)).Append(' ').Append(F(t.y)).Append('\n');
                bool hasN = data.Nrm != null && data.Nrm.Length == data.Pos.Length;
                if (hasN)
                    foreach (var n in data.Nrm)
                    {
                        var w = toRoot.MultiplyVector(local.MultiplyVector(n));
                        w = new Vector3(w.x / rs.x, w.y / rs.y, w.z / rs.z);   // normals go with the inverse scale
                        float len = MathF.Sqrt(w.x * w.x + w.y * w.y + w.z * w.z);
                        if (len < 1e-8f) len = 1;
                        obj.Append("vn ").Append(F(-w.x / len)).Append(' ').Append(F(w.y / len)).Append(' ').Append(F(w.z / len)).Append('\n');
                    }

                var mats = renderer.sharedMaterials;
                int tris = 0;
                for (int s = 0; s < data.Tris.Count; s++)
                {
                    Material mat = mats != null && mats.Length > 0 ? mats[Math.Min(s, mats.Length - 1)] : null;
                    var texFile = ModelExporter.SaveTexture(ModelExporter.MainTexture(mat), outDir, $"tex{texFiles.Count}", 256, texFiles, info);
                    string colour = "1 1 1";
                    try
                    {
                        if (mat != null && mat.HasProperty("_BaseColor")) { var c = mat.GetColor("_BaseColor"); colour = $"{F(c.r)} {F(c.g)} {F(c.b)}"; }
                    }
                    catch { }
                    var key = (texFile ?? "") + "|" + colour;
                    if (!materials.TryGetValue(key, out var matName))
                    {
                        matName = "m" + materials.Count;
                        materials[key] = matName;
                        mtl.AppendLine($"newmtl {matName}");
                        mtl.AppendLine($"Kd {colour}");
                        if (texFile != null) mtl.AppendLine($"map_Kd {texFile}");
                        mtl.AppendLine();
                    }
                    obj.AppendLine($"usemtl {matName}");
                    var idx = data.Tris[s];
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        obj.Append('f');
                        foreach (var k0 in new[] { idx[i], idx[i + 2], idx[i + 1] })
                        {
                            int k = vBase + k0;
                            obj.Append(' ').Append(k);
                            if (hasUv || hasN) obj.Append('/').Append(hasUv ? k.ToString(Inv) : "");
                            if (hasN) obj.Append('/').Append(k);
                        }
                        obj.Append('\n');
                        tris++;
                    }
                }
                vBase += data.Pos.Length;
                info.Parts.Add(new ModelPart { Name = partName, Vertices = data.Pos.Length, Triangles = tris });
            }
            if (info.Parts.Count == 0) throw new Exception("no readable meshes" + (info.Warnings.Count > 0 ? ": " + string.Join("; ", info.Warnings) : ""));
            info.Min = new[] { min.X, min.Y, min.Z };
            info.Max = new[] { max.X, max.Y, max.Z };
            File.WriteAllText(Path.Combine(outDir, ModPaths.ModelObjFile), obj.ToString());
            File.WriteAllText(Path.Combine(outDir, ModPaths.ModelMtlFile), mtl.ToString());
            File.WriteAllText(Path.Combine(outDir, ModPaths.ModelInfoFile), JsonSerializer.Serialize(info, Json.Options));
        }
    }
}

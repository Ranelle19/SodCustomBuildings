using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>One material's worth of triangles from an exported building model, in OBJ space.</summary>
    public sealed class ModelMesh
    {
        public string Material = "";
        public string TexturePath;      // diffuse PNG, or null
        public string EmissionPath;     // lit-windows PNG, or null
        public readonly List<Vector3> Pos = new();
        public readonly List<Vector3> Nrm = new();
        public readonly List<Vector2> Uv = new();
        public readonly List<int> Idx = new();
        public bool HasNormals => Nrm.Count == Pos.Count && Nrm.Count > 0;
        public bool HasUv => Uv.Count == Pos.Count && Uv.Count > 0;
    }

    public sealed class LoadedModel
    {
        public string Folder;
        public ModelInfo Info;
        public readonly List<ModelMesh> Meshes = new();
        public Vector3 Min, Max;        // OBJ space
        public int TriangleCount => Meshes.Sum(m => m.Idx.Count / 3);
    }

    /// <summary>Reads the model.obj / model.mtl / model.json the plugin exports for each game building.</summary>
    public static class ModelLoader
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string FolderFor(Workspace ws, string preset) =>
            ws?.ModDir == null || string.IsNullOrEmpty(preset)
                ? null
                : Path.Combine(ws.ExportDir, ModPaths.ModelsDir, ModPaths.ModelFolder(preset));

        public static bool Exists(Workspace ws, string preset)
        {
            var dir = FolderFor(ws, preset);
            return dir != null && File.Exists(Path.Combine(dir, ModPaths.ModelObjFile));
        }

        /// <summary>Loads a model, or returns null with a reason.</summary>
        /// <summary>Folder of a building's generated exterior (Buildings/&lt;id&gt;/exterior).</summary>
        public static string ExteriorFolder(Workspace ws, BuildingEntry b) =>
            ws?.ModDir == null || b == null ? null
                : Path.Combine(ws.BuildingsDir, b.LoadedDirName ?? Workspace.SafeName(b.Def.Id), ModPaths.ExteriorDir);

        public static bool ExteriorExists(Workspace ws, BuildingEntry b)
        {
            var dir = ExteriorFolder(ws, b);
            return dir != null && File.Exists(Path.Combine(dir, ModPaths.ExteriorObjFile));
        }

        public static LoadedModel LoadExterior(string folder, out string error) =>
            Load(folder, out error, ModPaths.ExteriorObjFile, ModPaths.ExteriorMtlFile);

        public static LoadedModel Load(string folder, out string error, string objFile = null, string mtlFile = null)
        {
            error = null;
            var objPath = Path.Combine(folder, objFile ?? ModPaths.ModelObjFile);
            if (!File.Exists(objPath)) { error = "No exported model."; return null; }

            var model = new LoadedModel { Folder = folder };
            var infoPath = Path.Combine(folder, ModPaths.ModelInfoFile);
            if (File.Exists(infoPath))
            {
                try { model.Info = JsonSerializer.Deserialize<ModelInfo>(File.ReadAllText(infoPath), Json.Options); }
                catch { /* the OBJ is still usable */ }
            }

            var materials = ReadMtl(Path.Combine(folder, mtlFile ?? ModPaths.ModelMtlFile), folder);

            var v = new List<Vector3>();
            var vt = new List<Vector2>();
            var vn = new List<Vector3>();
            var meshes = new Dictionary<string, ModelMesh>();
            var maps = new Dictionary<ModelMesh, Dictionary<(int, int, int), int>>();
            ModelMesh current = null;

            ModelMesh MeshFor(string mat)
            {
                if (!meshes.TryGetValue(mat, out var m))
                {
                    m = new ModelMesh { Material = mat };
                    if (materials.TryGetValue(mat, out var maps2)) { m.TexturePath = maps2.diffuse; m.EmissionPath = maps2.emission; }
                    meshes[mat] = m;
                    maps[m] = new Dictionary<(int, int, int), int>();
                }
                return m;
            }

            try
            {
                foreach (var raw in File.ReadLines(objPath))
                {
                    var line = raw.Trim();
                    if (line.Length < 2 || line[0] == '#') continue;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    switch (parts[0])
                    {
                        case "v":
                            v.Add(new Vector3(P(parts[1]), P(parts[2]), P(parts[3])));
                            break;
                        case "vt":
                            vt.Add(new Vector2(P(parts[1]), parts.Length > 2 ? P(parts[2]) : 0));
                            break;
                        case "vn":
                            vn.Add(new Vector3(P(parts[1]), P(parts[2]), P(parts[3])));
                            break;
                        case "usemtl":
                            current = MeshFor(parts.Length > 1 ? parts[1] : "");
                            break;
                        case "f":
                            current ??= MeshFor("");
                            var corner = new int[parts.Length - 1];
                            for (int i = 1; i < parts.Length; i++)
                                corner[i - 1] = Vertex(current, maps[current], parts[i], v, vt, vn);
                            for (int i = 1; i + 1 < corner.Length; i++) // fan triangulation
                            {
                                current.Idx.Add(corner[0]);
                                current.Idx.Add(corner[i]);
                                current.Idx.Add(corner[i + 1]);
                            }
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                error = "The model file couldn't be read: " + e.Message;
                return null;
            }

            model.Meshes.AddRange(meshes.Values.Where(m => m.Idx.Count > 0));
            if (model.Meshes.Count == 0) { error = "The model file has no faces."; return null; }

            model.Min = new Vector3(float.MaxValue);
            model.Max = new Vector3(float.MinValue);
            foreach (var m in model.Meshes)
                foreach (var p in m.Pos)
                {
                    model.Min = Vector3.Min(model.Min, p);
                    model.Max = Vector3.Max(model.Max, p);
                }
            return model;
        }

        private static float P(string s) => float.Parse(s, NumberStyles.Float, Inv);

        private static int Vertex(ModelMesh m, Dictionary<(int, int, int), int> map, string token,
                                  List<Vector3> v, List<Vector2> vt, List<Vector3> vn)
        {
            var f = token.Split('/');
            int Ix(int i, int count)
            {
                if (f.Length <= i || f[i].Length == 0) return -1;
                int k = int.Parse(f[i], Inv);
                return k < 0 ? count + k : k - 1; // OBJ is 1-based; negative = relative
            }
            int vi = Ix(0, v.Count), ti = Ix(1, vt.Count), ni = Ix(2, vn.Count);
            var key = (vi, ti, ni);
            if (map.TryGetValue(key, out var idx)) return idx;

            idx = m.Pos.Count;
            m.Pos.Add(v[vi]);
            if (ti >= 0 && ti < vt.Count) m.Uv.Add(vt[ti]); else if (m.Uv.Count > 0) m.Uv.Add(Vector2.Zero);
            if (ni >= 0 && ni < vn.Count) m.Nrm.Add(vn[ni]); else if (m.Nrm.Count > 0) m.Nrm.Add(Vector3.UnitY);
            map[key] = idx;
            return idx;
        }

        private static Dictionary<string, (string diffuse, string emission)> ReadMtl(string path, string folder)
        {
            var result = new Dictionary<string, (string, string)>();
            if (!File.Exists(path)) return result;
            string name = null;
            string diffuse = null, emission = null;
            void Flush()
            {
                if (name != null) result[name] = (diffuse, emission);
            }
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("newmtl "))
                {
                    Flush();
                    name = line.Substring(7).Trim();
                    diffuse = emission = null;
                }
                else if (line.StartsWith("map_Kd ")) diffuse = Existing(folder, line.Substring(7).Trim());
                else if (line.StartsWith("map_Ke ")) emission = Existing(folder, line.Substring(7).Trim());
            }
            Flush();
            return result;
        }

        private static string Existing(string folder, string file)
        {
            var p = Path.Combine(folder, file);
            return File.Exists(p) ? p : null;
        }
    }
}

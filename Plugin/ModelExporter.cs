using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomBuildings
{
    using CustomBuildings.Shared;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Exports each game building's exterior model (its prefab's meshes) and textures to Export/models/&lt;preset&gt;/ as
    /// model.obj + model.mtl + PNGs + model.json, so the editor can show the real building. Game meshes are usually not
    /// CPU-readable, so they are read back from the GPU vertex/index buffers. Nothing in the game is changed, except that a
    /// mesh whose buffers refuse to be read gets the Raw buffer flag added and is read again.
    /// </summary>
    internal static class ModelExporter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal sealed class MeshData
        {
            public Vector3[] Pos;
            public Vector3[] Nrm;   // may be null
            public Vector2[] Uv;    // may be null
            public List<int[]> Tris = new();
        }

        public static void ExportAll(string exportDir)
        {
            var loader = AssetLoader.Instance;
            if (loader?.allBuildingData == null) return;
            var root = Path.Combine(exportDir, ModPaths.ModelsDir);
            Directory.CreateDirectory(root);
            int maxTex = Math.Clamp(Plugin.ModelTextureMaxSize?.Value ?? 1024, 64, 4096);
            int done = 0, skipped = 0, failed = 0;

            foreach (var b in loader.allBuildingData)
            {
                if (b == null || b.prefab == null) continue;
                var name = string.IsNullOrEmpty(b.presetName) ? b.name : b.presetName;
                if (ContentLoader.IsOurBuilding(name)) continue;
                var outDir = Path.Combine(root, ModPaths.ModelFolder(name));
                if (AlreadyExported(outDir, b.boundary)) { skipped++; continue; }
                var failedMark = Path.Combine(outDir, "export-failed.txt");
                if (File.Exists(failedMark) && File.ReadAllText(failedMark).Trim() == $"{Application.version}|{ModelInfo.CurrentExportFormat}" && !b.boundary)
                {
                    skipped++;   // failed before with this game version; don't try (and warn) on every start
                    continue;
                }
                try
                {
                    ExportOne(b, name, outDir, maxTex);
                    done++;
                }
                catch (Exception e)
                {
                    failed++;
                    Plugin.L.LogWarning($"Model export of {name} failed: {e.Message} (the editor's 3D preview has no model for it; " +
                                        "this isn't tried again until the game updates)");
                    try { Directory.CreateDirectory(outDir); File.WriteAllText(failedMark, $"{Application.version}|{ModelInfo.CurrentExportFormat}"); } catch { }
                }
            }
            Plugin.L.LogInfo($"Building models: exported {done}, already up to date {skipped}, failed {failed} ({root}).");
        }

        private static bool AlreadyExported(string outDir, bool needOneLod = false)
        {
            var info = Path.Combine(outDir, ModPaths.ModelInfoFile);
            if (!File.Exists(info)) return false;
            try
            {
                var mi = JsonSerializer.Deserialize<ModelInfo>(File.ReadAllText(info), Json.Options);
                return mi != null && mi.GameVersion == Application.version && mi.ExportFormat == ModelInfo.CurrentExportFormat && mi.Parts.Count > 0
                       && (!needOneLod || mi.OneLod);
            }
            catch { return false; }
        }

        private static void ExportOne(BuildingPreset b, string name, string outDir, int maxTex)
        {
            Directory.CreateDirectory(outDir);
            var info = new ModelInfo
            {
                Preset = name,
                GameVersion = Application.version,
                ExportFormat = ModelInfo.CurrentExportFormat,
                FloorCount = b.floorCount,
                MeshHeight = b.meshHeight,
                BuildingHeight = b.buildingHeight,
            };
            var root = b.prefab.transform;
            var obj = new StringBuilder();
            var mtl = new StringBuilder();
            obj.AppendLine($"# {name} from Shadows of Doubt {Application.version}, exported by CustomBuildings");
            obj.AppendLine($"mtllib {ModPaths.ModelMtlFile}");

            var texFiles = new Dictionary<IntPtr, string>();
            var materials = new Dictionary<string, string>(); // key (tex|emission) -> material name
            var min = new System.Numerics.Vector3(float.MaxValue);
            var max = new System.Numerics.Vector3(float.MinValue);
            int vBase = 1, partIndex = 0;

            var skip = PickLods(b.prefab, info);
            foreach (var mf in b.prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                var go = mf.gameObject;
                var renderer = go.GetComponent<MeshRenderer>();
                if (mesh == null || renderer == null) continue;
                if (skip.Contains(renderer.Pointer)) continue;   // another detail level of the same thing

                MeshData data;
                try { data = ReadMesh(mesh); }
                catch (Exception e)
                {
                    info.Warnings.Add($"{go.name}: couldn't read mesh ({e.Message})");
                    continue;
                }
                if (data == null || data.Pos.Length == 0) continue;

                bool isBase = go.name.IndexOf("Base", StringComparison.OrdinalIgnoreCase) >= 0;
                var local = mf.transform.localToWorldMatrix;
                var toRoot = root.worldToLocalMatrix;
                string partName = Safe(go.name) + "_" + partIndex++;
                obj.AppendLine($"o {partName}");

                foreach (var v in data.Pos)
                {
                    var p = toRoot.MultiplyPoint3x4(local.MultiplyPoint3x4(v));
                    obj.Append("v ").Append(F(-p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
                    min = System.Numerics.Vector3.Min(min, new System.Numerics.Vector3(-p.x, p.y, p.z));
                    max = System.Numerics.Vector3.Max(max, new System.Numerics.Vector3(-p.x, p.y, p.z));
                }
                bool hasUv = data.Uv != null && data.Uv.Length == data.Pos.Length;
                if (hasUv)
                    foreach (var t in data.Uv)
                        obj.Append("vt ").Append(F(t.x)).Append(' ').Append(F(t.y)).Append('\n');
                bool hasN = data.Nrm != null && data.Nrm.Length == data.Pos.Length;
                if (hasN)
                    foreach (var n in data.Nrm)
                    {
                        var w = toRoot.MultiplyVector(local.MultiplyVector(n));
                        float len = MathF.Sqrt(w.x * w.x + w.y * w.y + w.z * w.z);
                        if (len < 1e-8f) len = 1;
                        obj.Append("vn ").Append(F(-w.x / len)).Append(' ').Append(F(w.y / len)).Append(' ').Append(F(w.z / len)).Append('\n');
                    }

                var mats = renderer.sharedMaterials;
                int triCount = 0;
                for (int s = 0; s < data.Tris.Count; s++)
                {
                    Material mat = mats != null && mats.Length > 0 ? mats[Math.Min(s, mats.Length - 1)] : null;
                    var tex = isBase && b.tex != null ? b.tex : MainTexture(mat);
                    var texFile = SaveTexture(tex, outDir, $"tex{texFiles.Count}", maxTex, texFiles, info);
                    string emFile = isBase ? SaveTexture(b.emissionMapLit, outDir, $"lit{texFiles.Count}", maxTex, texFiles, info) : null;
                    var key = (texFile ?? "") + "|" + (emFile ?? "");
                    if (!materials.TryGetValue(key, out var matName))
                    {
                        matName = "m" + materials.Count;
                        materials[key] = matName;
                        mtl.AppendLine($"newmtl {matName}");
                        mtl.AppendLine("Kd 1 1 1");
                        if (texFile != null) mtl.AppendLine($"map_Kd {texFile}");
                        if (emFile != null) mtl.AppendLine($"map_Ke {emFile}");
                        mtl.AppendLine();
                    }
                    obj.AppendLine($"usemtl {matName}");

                    var tris = data.Tris[s];
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        // X is mirrored, so reverse the winding to keep faces pointing outwards
                        obj.Append('f');
                        foreach (var idx in new[] { tris[i], tris[i + 2], tris[i + 1] })
                        {
                            int k = vBase + idx;
                            obj.Append(' ').Append(k);
                            if (hasUv || hasN) obj.Append('/').Append(hasUv ? k.ToString(Inv) : "");
                            if (hasN) obj.Append('/').Append(k);
                        }
                        obj.Append('\n');
                        triCount++;
                    }
                }
                vBase += data.Pos.Length;
                info.Parts.Add(new ModelPart { Name = partName, Vertices = data.Pos.Length, Triangles = triCount, IsBase = isBase });
            }

            if (info.Parts.Count == 0)
            {
                int filters = b.prefab.GetComponentsInChildren<MeshFilter>(true).Length;
                int skinned = b.prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                var why = info.Warnings.Count > 0 ? " " + string.Join("; ", info.Warnings) : "";
                throw new Exception($"no readable meshes in the prefab ({filters} mesh filter(s), {skinned} skinned mesh(es)).{why}");
            }

            // calibration data for generated exteriors: the maps at full size, and the windows the game found in them
            info.WindowMap = SaveTexture(b.windowMap, outDir, "windowmap", 4096, texFiles, info);
            info.AddonMap = SaveTexture(b.addonMap, outDir, "addonmap", 4096, texFiles, info);
            info.EmissionUnlit = SaveTexture(b.emissionMapUnlit, outDir, "unlit", maxTex, texFiles, info);
            try
            {
                if (b.sortedWindows != null)
                {
                    int f = 0;
                    foreach (var fl in b.sortedWindows)
                    {
                        info.WindowSummary.Add($"storey {f}: front {fl.front?.Count ?? 0}, back {fl.back?.Count ?? 0}, left {fl.left?.Count ?? 0}, right {fl.right?.Count ?? 0}");
                        if (f < 2)
                            foreach (var side in new[] { ("front", fl.front), ("back", fl.back), ("left", fl.left), ("right", fl.right) })
                                if (side.Item2 != null && side.Item2.Count > 0)
                                {
                                    var w = side.Item2[0];
                                    info.WindowSummary.Add($"  {side.Item1}[0]: origin {w.originPixel} size {w.rectSize} centre {w.centrePixel} " +
                                                           $"left {w.localMeshPositionLeft} right {w.localMeshPositionRight} floor {w.floor} side {w.side} horizontal {w.horizonal}");
                                }
                        f++;
                    }
                }
                info.WindowSummary.Add($"floorCount {b.floorCount}, meshHeight {b.meshHeight}, buildingHeight {b.buildingHeight}, " +
                                       $"captureMesh {(b.captureMesh != null ? b.captureMesh.name : "none")}, cable points {b.cableLinkPoints?.Count ?? 0}, sign points {b.sideSignPoints?.Count ?? 0}");
            }
            catch (Exception e) { info.Warnings.Add($"window summary: {e.Message}"); }
            info.Min = new[] { min.X, min.Y, min.Z };
            info.Max = new[] { max.X, max.Y, max.Z };
            File.WriteAllText(Path.Combine(outDir, ModPaths.ModelObjFile), obj.ToString());
            File.WriteAllText(Path.Combine(outDir, ModPaths.ModelMtlFile), mtl.ToString());
            File.WriteAllText(Path.Combine(outDir, ModPaths.ModelInfoFile), JsonSerializer.Serialize(info, Json.Options));
            Plugin.Verbose($"Exported model {name}: {info.Parts.Count} part(s), {vBase - 1} vertices");
        }

        private static string F(float f) => f.ToString("0.#####", Inv);

        internal static Texture MainTexture(Material mat)
        {
            if (mat == null) return null;
            try
            {
                if (mat.mainTexture != null) return mat.mainTexture;
                foreach (var prop in new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo" })
                    if (mat.HasProperty(prop) && mat.GetTexture(prop) != null) return mat.GetTexture(prop);
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ textures

        /// <summary>Copies any texture (compressed, non-readable) through a RenderTexture and saves it as PNG.</summary>
        internal static string SaveTexture(Texture tex, string outDir, string baseName, int maxSize, Dictionary<IntPtr, string> cache, ModelInfo info)
        {
            if (tex == null) return null;
            if (cache.TryGetValue(tex.Pointer, out var existing)) return existing;

            int w = tex.width, h = tex.height;
            if (w <= 0 || h <= 0) return null;
            float k = Math.Min(1f, maxSize / (float)Math.Max(w, h));
            int tw = Math.Max(1, (int)(w * k)), th = Math.Max(1, (int)(h * k));

            var prev = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(tw, th, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                copy.Apply();
                var png = ImageConversion.EncodeToPNG(copy);
                var file = baseName + ".png";
                File.WriteAllBytes(Path.Combine(outDir, file), ToManaged(png));
                cache[tex.Pointer] = file;
                return file;
            }
            catch (Exception e)
            {
                info.Warnings.Add($"texture {tex.name}: {e.Message}");
                cache[tex.Pointer] = null;
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) Object.Destroy(copy);
            }
        }

        private static byte[] ToManaged(Il2CppStructArray<byte> a)
        {
            var r = new byte[a.Length];
            for (int i = 0; i < r.Length; i++) r[i] = a[i];
            return r;
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>
        /// For every LOD group: keep the most detailed level whose models can all be read, skip the other levels (they are
        /// the same thing again, less detailed). Returns the renderers to skip.
        /// </summary>
        internal static HashSet<IntPtr> PickLods(GameObject prefab, ModelInfo info)
        {
            var skip = new HashSet<IntPtr>();
            info.OneLod = true;
            foreach (var lg in prefab.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lg.GetLODs();
                int keep = -1;
                for (int i = 0; i < lods.Length && keep < 0; i++)
                {
                    var rs = lods[i].renderers;
                    if (rs == null || rs.Length == 0) continue;
                    bool ok = true;
                    foreach (var r in rs)
                    {
                        var mf = r != null ? r.GetComponent<MeshFilter>() : null;
                        if (mf?.sharedMesh == null) continue;
                        try { ReadMesh(mf.sharedMesh); } catch { ok = false; break; }
                    }
                    if (ok) keep = i;
                }
                info.LodLevels.Add(keep);
                for (int i = 0; i < lods.Length; i++)
                {
                    if (i == keep) continue;
                    var rs = lods[i].renderers;
                    if (rs == null) continue;
                    foreach (var r in rs) if (r != null) skip.Add(r.Pointer);
                }
                if (keep > 0) info.Warnings.Add($"{lg.gameObject.name}: detail level {keep} exported (the more detailed ones can't be read)");
            }
            return skip;
        }

        internal static MeshData ReadMesh(Mesh mesh)
        {
            bool allTriangles = true;
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetSubMesh(s).topology != MeshTopology.Triangles) allTriangles = false;

            if (mesh.isReadable && allTriangles)
            {
                var d = new MeshData { Pos = Copy(mesh.vertices) };
                var n = mesh.normals;
                if (n != null && n.Length == d.Pos.Length) d.Nrm = Copy(n);
                var uv = mesh.uv;
                if (uv != null && uv.Length == d.Pos.Length) d.Uv = Copy(uv);
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var t = mesh.GetTriangles(s);
                    var arr = new int[t.Length];
                    for (int i = 0; i < arr.Length; i++) arr[i] = t[i];
                    d.Tris.Add(arr);
                }
                return d;
            }

            // NEVER change the buffers' flags (vertexBufferTarget / indexBufferTarget) of a model that isn't readable: Unity
            // then rebuilds its buffers from a copy it doesn't have, and the model disappears everywhere it's used (that made
            // the sea wall vanish in plugin 1.7.2). Some models also read back as all zeros; those can't be read at all.
            var g = ReadMeshFromGpu(mesh);
            if (AllSame(g.Pos)) throw new Exception($"mesh {mesh.name}: the graphics card gives back no positions for it, so it can't be read");
            return g;
        }

        private static bool AllSame(Vector3[] p)
        {
            if (p == null || p.Length < 2) return false;
            for (int i = 1; i < p.Length; i++) if (p[i] != p[0]) return false;
            return true;
        }

        private static Vector3[] Copy(Il2CppStructArray<Vector3> a)
        {
            var r = new Vector3[a.Length];
            for (int i = 0; i < r.Length; i++) r[i] = a[i];
            return r;
        }

        private static Vector2[] Copy(Il2CppStructArray<Vector2> a)
        {
            var r = new Vector2[a.Length];
            for (int i = 0; i < r.Length; i++) r[i] = a[i];
            return r;
        }

        private static MeshData ReadMeshFromGpu(Mesh mesh)
        {
            int n = mesh.vertexCount;
            var streams = new Dictionary<int, byte[]>();
            var d = new MeshData();

            var pos = ReadAttribute(mesh, VertexAttribute.Position, streams, n, out int pd);
            if (pos == null || pd < 3) throw new Exception("mesh has no positions");
            d.Pos = new Vector3[n];
            for (int i = 0; i < n; i++) d.Pos[i] = new Vector3(pos[i * pd], pos[i * pd + 1], pos[i * pd + 2]);

            var nrm = ReadAttribute(mesh, VertexAttribute.Normal, streams, n, out int nd);
            if (nrm != null && nd >= 3)
            {
                d.Nrm = new Vector3[n];
                for (int i = 0; i < n; i++) d.Nrm[i] = new Vector3(nrm[i * nd], nrm[i * nd + 1], nrm[i * nd + 2]);
            }

            var uv = ReadAttribute(mesh, VertexAttribute.TexCoord0, streams, n, out int ud);
            if (uv != null && ud >= 2)
            {
                d.Uv = new Vector2[n];
                for (int i = 0; i < n; i++) d.Uv[i] = new Vector2(uv[i * ud], uv[i * ud + 1]);
            }

            byte[] ib;
            var ibuf = mesh.GetIndexBuffer();
            try { ib = ReadBuffer(ibuf); }
            finally { ibuf.Dispose(); }
            bool is32 = mesh.indexFormat == IndexFormat.UInt32;

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var sm = mesh.GetSubMesh(s);
                var idx = new int[sm.indexCount];
                for (int i = 0; i < idx.Length; i++)
                {
                    int at = sm.indexStart + i;
                    int raw = is32 ? BitConverter.ToInt32(ib, at * 4) : BitConverter.ToUInt16(ib, at * 2);
                    idx[i] = raw + sm.baseVertex;
                }
                if (sm.topology == MeshTopology.Quads)
                {
                    var tris = new List<int>(idx.Length / 4 * 6);
                    for (int i = 0; i + 3 < idx.Length; i += 4)
                        tris.AddRange(new[] { idx[i], idx[i + 1], idx[i + 2], idx[i], idx[i + 2], idx[i + 3] });
                    idx = tris.ToArray();
                }
                else if (sm.topology != MeshTopology.Triangles)
                    idx = Array.Empty<int>(); // lines/points: nothing to draw as a surface
                d.Tris.Add(idx);
            }
            return d;
        }

        private static float[] ReadAttribute(Mesh mesh, VertexAttribute attr, Dictionary<int, byte[]> streams, int n, out int dim)
        {
            dim = 0;
            if (!mesh.HasVertexAttribute(attr)) return null;
            int stream = mesh.GetVertexAttributeStream(attr);
            int offset = mesh.GetVertexAttributeOffset(attr);
            dim = mesh.GetVertexAttributeDimension(attr);
            var fmt = mesh.GetVertexAttributeFormat(attr);
            int stride = mesh.GetVertexBufferStride(stream);

            if (!streams.TryGetValue(stream, out var data))
            {
                var buf = mesh.GetVertexBuffer(stream);
                try { data = ReadBuffer(buf); }
                finally { buf.Dispose(); }
                streams[stream] = data;
            }

            int size = Size(fmt);
            var result = new float[n * dim];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < dim; c++)
                {
                    int o = i * stride + offset + c * size;
                    if (o + size <= data.Length) result[i * dim + c] = Decode(data, o, fmt);
                }
            return result;
        }

        private static byte[] ReadBuffer(GraphicsBuffer buf)
        {
            int size = buf.count * buf.stride;
            var arr = new Il2CppStructArray<byte>(size);
            // GraphicsBuffer.GetData(Array) is rebuilt by BepInEx from Unity's C# and calls Array.GetType(), which the
            // game's interop doesn't have ("Method not found"). Call the engine function behind it directly instead:
            // copy `size` elements of 1 byte each, starting at 0 on both sides.
            buf.InternalGetData(arr.Cast<Il2CppSystem.Array>(), 0, 0, size, 1);
            return ToManaged(arr);
        }

        private static int Size(VertexAttributeFormat f) => f switch
        {
            VertexAttributeFormat.Float32 or VertexAttributeFormat.UInt32 or VertexAttributeFormat.SInt32 => 4,
            VertexAttributeFormat.Float16 or VertexAttributeFormat.UNorm16 or VertexAttributeFormat.SNorm16 or
                VertexAttributeFormat.UInt16 or VertexAttributeFormat.SInt16 => 2,
            _ => 1,
        };

        private static float Decode(byte[] d, int o, VertexAttributeFormat f) => f switch
        {
            VertexAttributeFormat.Float32 => BitConverter.ToSingle(d, o),
            VertexAttributeFormat.Float16 => (float)BitConverter.ToHalf(d, o),
            VertexAttributeFormat.UNorm16 => BitConverter.ToUInt16(d, o) / 65535f,
            VertexAttributeFormat.SNorm16 => Math.Max(-1f, BitConverter.ToInt16(d, o) / 32767f),
            VertexAttributeFormat.UNorm8 => d[o] / 255f,
            VertexAttributeFormat.SNorm8 => Math.Max(-1f, (sbyte)d[o] / 127f),
            VertexAttributeFormat.UInt16 => BitConverter.ToUInt16(d, o),
            VertexAttributeFormat.SInt16 => BitConverter.ToInt16(d, o),
            VertexAttributeFormat.UInt32 => BitConverter.ToUInt32(d, o),
            VertexAttributeFormat.SInt32 => BitConverter.ToInt32(d, o),
            VertexAttributeFormat.UInt8 => d[o],
            VertexAttributeFormat.SInt8 => (sbyte)d[o],
            _ => 0f,
        };

        private static string Safe(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}

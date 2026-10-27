using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CustomBuildings.Shared;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomBuildings
{
    /// <summary>
    /// A wall piece's own model (Edge\&lt;Id&gt;\model\, see EdgeModelInfo for the frame): swaps it in for the game's wall
    /// on every placed copy. The model goes under the game model's root, so it sits and turns exactly like the game's
    /// wall did. The game's wall is hidden (its renderers, detail levels and solid colliders) unless KeepGameWall is set.
    /// Every material of the OBJ becomes one mesh with a MeshCollider; objects named "collision…" are only colliders.
    /// </summary>
    internal static class EdgeModel
    {
        private sealed class Part
        {
            public string Material = "";
            public bool CollisionOnly;
            public readonly List<Vector3> V = new(), N = new();
            public readonly List<Vector2> Uv = new();
            public readonly List<int> Tris = new();
            public readonly Dictionary<(int, int, int), int> Map = new();
        }

        private static readonly Dictionary<string, List<Part>> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<IntPtr> Done = new();

        internal static bool HasOwnModel(NewBuilding piece)
            => piece?.preset != null && EdgePieces.Ours.TryGetValue(piece.preset.presetName, out var d) && d.Edge?.Model != null;

        /// <summary>Puts the piece's own model in. Returns true if it did.</summary>
        internal static bool Apply(NewBuilding piece, EdgePieceDef def, string dir)
        {
            var info = def.Model;
            if (info == null || piece == null || Done.Contains(piece.Pointer)) return false;
            var folder = Path.Combine(dir, EdgeModelInfo.Folder);
            var objPath = Path.Combine(folder, info.Obj ?? "model.obj");
            if (!File.Exists(objPath)) { Plugin.L.LogWarning($"[edge] {def.Id}: its model {objPath} is missing; keeping the game's wall"); return false; }

            if (!Cache.TryGetValue(objPath, out var parts))
            {
                parts = ReadObj(objPath, info);
                Cache[objPath] = parts;
            }
            if (parts.Count == 0) { Plugin.L.LogWarning($"[edge] {def.Id}: its model has no faces"); return false; }
            var mats = ReadMtl(Path.Combine(folder, info.Mtl ?? "model.mtl"));

            // where the game's model sits: its prefab copy under the piece
            Transform root = null;
            var prefabName = piece.preset.prefab != null ? piece.preset.prefab.name : null;
            for (int i = 0; i < piece.transform.childCount && root == null; i++)
            {
                var c = piece.transform.GetChild(i);
                if (prefabName != null && c.name.StartsWith(prefabName, StringComparison.Ordinal)) root = c;
            }
            if (root == null && piece.buildingModelBase != null) root = piece.buildingModelBase.transform;
            if (root == null) root = piece.transform;

            var srcRenderer = root.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r != null && r.sharedMaterial != null);
            var srcMat = srcRenderer?.sharedMaterial;
            int layer = srcRenderer != null ? srcRenderer.gameObject.layer : root.gameObject.layer;

            // hide the game's wall
            int hidden = 0, offCols = 0;
            if (!info.KeepGameWall)
            {
                foreach (var lg in piece.GetComponentsInChildren<LODGroup>(true)) lg.enabled = false;
                foreach (var r in piece.GetComponentsInChildren<Renderer>(true)) { if (r.enabled) { r.enabled = false; hidden++; } }
                foreach (var c in piece.GetComponentsInChildren<Collider>(true))
                    if (c.enabled && !c.isTrigger) { c.enabled = false; offCols++; }
            }

            var holder = new GameObject(def.Id + "_OwnModel");
            holder.transform.SetParent(root, false);   // same frame as the game's model (see EdgeModelInfo)
            holder.layer = layer;
            var matCache = new Dictionary<string, Material>();
            int tris = 0;
            foreach (var p in parts)
            {
                var go = new GameObject(p.CollisionOnly ? "Collision" : p.Material);
                go.transform.SetParent(holder.transform, false);
                go.layer = layer;
                var mesh = BuildMesh(p, $"{def.Id}_{p.Material}");
                tris += p.Tris.Count / 3;
                if (!p.CollisionOnly)
                {
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    if (!matCache.TryGetValue(p.Material, out var m)) matCache[p.Material] = m = MakeMaterial(srcMat, mats, p.Material, folder, def.Id);
                    if (m != null) mr.sharedMaterial = m;
                    if (srcRenderer != null) ExteriorLoader.CopyRendererSettings(srcRenderer, mr);
                }
                bool hasCollisionParts = parts.Any(q => q.CollisionOnly);
                if (p.CollisionOnly || !hasCollisionParts)
                    try { go.AddComponent<MeshCollider>().sharedMesh = mesh; } catch (Exception e) { Plugin.Verbose($"[edge] {def.Id}: collider: {e.Message}"); }
            }
            Physics.SyncTransforms();   // the pier measures the wall right after this
            // fell into the sea here: back onto the walkway (city side of the piece, in the game model's own space)
            PierBuilder.AddRescue(def.Id, root.TransformPoint(new Vector3(0, 1.2f, 12.5f)));
            Done.Add(piece.Pointer);
            Plugin.L.LogInfo($"[edge] {def.Id} on tile {piece.cityTile?.cityCoord}: own model in ({parts.Count} parts, {tris} triangles); " +
                             (info.KeepGameWall ? "the game's wall stays" : $"game's wall hidden ({hidden} renderers, {offCols} colliders)"));
            return true;
        }

        internal static void Clear() => Done.Clear();

        // ------------------------------------------------------------------ reading

        /// <summary>OBJ in our export convention (X mirrored, winding reversed): both undone. Plus the import check's fixes.</summary>
        private static List<Part> ReadObj(string path, EdgeModelInfo info)
        {
            var inv = CultureInfo.InvariantCulture;
            var v = new List<Vector3>(); var vt = new List<Vector2>(); var vn = new List<Vector3>();
            var parts = new Dictionary<(string, bool), Part>();
            string mat = "", obj = "";
            float k = info.Scale > 0 ? info.Scale : 1f;
            Vector3 Fix(float x, float y, float z, bool scale)
            {
                var p = info.SwapYZ ? new Vector3(-x, z, -y) : new Vector3(-x, y, z);
                return scale ? p * k : p;
            }
            Part Cur()
            {
                bool col = obj.StartsWith("collision", StringComparison.OrdinalIgnoreCase);
                var key = (col ? "collision" : mat, col);
                if (!parts.TryGetValue(key, out var p)) parts[key] = p = new Part { Material = key.Item1, CollisionOnly = col };
                return p;
            }
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length < 2 || line[0] == '#') continue;
                var s = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (s[0])
                {
                    case "o": case "g": obj = s.Length > 1 ? s[1] : ""; break;
                    case "usemtl": mat = s.Length > 1 ? s[1] : ""; break;
                    case "v": v.Add(Fix(float.Parse(s[1], inv), float.Parse(s[2], inv), float.Parse(s[3], inv), true)); break;
                    case "vt": vt.Add(new Vector2(float.Parse(s[1], inv), s.Length > 2 ? float.Parse(s[2], inv) : 0)); break;
                    case "vn": vn.Add(Fix(float.Parse(s[1], inv), float.Parse(s[2], inv), float.Parse(s[3], inv), false).normalized); break;
                    case "f":
                        var p = Cur();
                        var idx = new int[s.Length - 1];
                        for (int i = 1; i < s.Length; i++) idx[i - 1] = Vertex(p, s[i], v, vt, vn);
                        for (int i = 1; i + 1 < idx.Length; i++) { p.Tris.Add(idx[0]); p.Tris.Add(idx[i + 1]); p.Tris.Add(idx[i]); }   // mirrored back: reverse
                        break;
                }
            }
            return parts.Values.Where(p => p.Tris.Count > 0).ToList();
        }

        private static int Vertex(Part p, string token, List<Vector3> v, List<Vector2> vt, List<Vector3> vn)
        {
            var f = token.Split('/');
            int Ix(int i, int count)
            {
                if (f.Length <= i || f[i].Length == 0) return -1;
                int n = int.Parse(f[i], CultureInfo.InvariantCulture);
                return n < 0 ? count + n : n - 1;
            }
            var key = (Ix(0, v.Count), Ix(1, vt.Count), Ix(2, vn.Count));
            if (p.Map.TryGetValue(key, out var idx)) return idx;
            idx = p.V.Count;
            p.V.Add(v[key.Item1]);
            p.Uv.Add(key.Item2 >= 0 ? vt[key.Item2] : Vector2.zero);
            p.N.Add(key.Item3 >= 0 ? vn[key.Item3] : Vector3.zero);
            p.Map[key] = idx;
            return idx;
        }

        /// <summary>material name → (diffuse texture file, Kd colour)</summary>
        private static Dictionary<string, (string tex, Color kd)> ReadMtl(string path)
        {
            var d = new Dictionary<string, (string, Color)>(StringComparer.Ordinal);
            if (!File.Exists(path)) return d;
            var inv = CultureInfo.InvariantCulture;
            string cur = null;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                var s = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (s.Length < 2) continue;
                if (s[0] == "newmtl") { cur = line.Substring(7).Trim(); d[cur] = (null, Color.white); }
                else if (cur != null && s[0] == "map_Kd") d[cur] = (line.Substring(6).Trim(), d[cur].Item2);
                else if (cur != null && s[0] == "Kd" && s.Length >= 4)
                    d[cur] = (d[cur].Item1, new Color(float.Parse(s[1], inv), float.Parse(s[2], inv), float.Parse(s[3], inv)));
            }
            return d;
        }

        private static Material MakeMaterial(Material src, Dictionary<string, (string tex, Color kd)> mats, string name, string folder, string id)
        {
            Material m = src != null ? new Material(src) : (Shader.Find("HDRP/Lit") is Shader sh ? new Material(sh) : null);
            if (m == null) return null;
            m.name = $"{id}_{name}";
            mats.TryGetValue(name, out var mi);
            Texture2D tex = null;
            if (!string.IsNullOrEmpty(mi.tex))
                try { tex = ExteriorLoader.Texture(folder, mi.tex, srgb: true); } catch (Exception e) { Plugin.L.LogWarning($"[edge] {id}: texture {mi.tex}: {e.Message}"); }
            foreach (var p in new[] { "_BaseColorMap", "_MainTex", "_BaseMap" })
                try { if (m.HasProperty(p)) m.SetTexture(p, tex); } catch { }
            var col = tex != null ? Color.white : (mi.kd == default ? Color.white : mi.kd);
            foreach (var p in new[] { "_BaseColor", "_Color" })
                try { if (m.HasProperty(p)) m.SetColor(p, col); } catch { }
            ExteriorLoader.CleanMaterial(m, id);
            ExteriorLoader.MakeMatt(m);
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return m;
        }

        private static Mesh BuildMesh(Part p, string name)
        {
            var mesh = new Mesh { name = name };
            if (p.V.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            var v = new Il2CppStructArray<Vector3>(p.V.Count);
            for (int i = 0; i < p.V.Count; i++) v[i] = p.V[i];
            mesh.vertices = v;
            var uv = new Il2CppStructArray<Vector2>(p.Uv.Count);
            for (int i = 0; i < p.Uv.Count; i++) uv[i] = p.Uv[i];
            mesh.uv = uv;
            try { mesh.uv2 = uv; mesh.uv3 = uv; mesh.uv4 = uv; } catch { }
            var t = new Il2CppStructArray<int>(p.Tris.Count);
            for (int i = 0; i < p.Tris.Count; i++) t[i] = p.Tris[i];
            mesh.triangles = t;
            bool normals = p.N.Count == p.V.Count && p.N.Any(n => n != Vector3.zero);
            if (normals)
            {
                var n = new Il2CppStructArray<Vector3>(p.N.Count);
                for (int i = 0; i < p.N.Count; i++) n[i] = p.N[i];
                mesh.normals = n;
            }
            else mesh.RecalculateNormals();
            try { mesh.RecalculateTangents(); } catch { }
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return mesh;
        }
    }
}

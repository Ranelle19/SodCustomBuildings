using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomBuildings
{
    /// <summary>
    /// The railing side of the piers:
    ///  - Openings: where the stairs start, the game's railing is cut out of every model and collider that has it there,
    ///    not only the wall piece's own (the visible railing can be a separate model). Checked again every few seconds,
    ///    so a model the game swaps in later (closer detail) gets cut too.
    ///  - Railing copies: a piece of the game's own railing (one post and the bars to the next) is taken from the wall
    ///    next to the pier and repeated along the pier's railings, with the game's material, so it looks the same.
    /// </summary>
    internal static class PierRailing
    {
        internal sealed class Opening
        {
            public string Name;
            public Vector3 Origin, Right, Out;   // origin on the walkway's edge at the middle of the opening
            public float A0, A1, TopY, YHi;
            public float UHi = 0.6f;             // how far out (from the edge) the railing can be
            public Transform Exclude;            // our own pier objects
            public Transform Within;             // only cut models of this wall piece (never people walking by)
            public int Visual, Collider;         // triangles cut so far
            public readonly HashSet<IntPtr> CutRenderers = new();   // renderers showing a cut model
            public bool Forced;
        }

        internal static readonly List<Opening> Openings = new();
        private static readonly HashSet<IntPtr> OurMeshes = new();   // cut copies we made; never cut again

        private static readonly HashSet<(Opening, IntPtr)> Checked = new();   // models already looked at with nothing to cut
        internal static void Clear() { Openings.Clear(); OurMeshes.Clear(); Checked.Clear(); }

        /// <summary>The wall piece's models (also switched-off detail levels), or every model if no piece is set.</summary>
        private static IEnumerable<MeshFilter> Candidates(Opening o)
        {
            if (o.Within != null) return o.Within.GetComponentsInChildren<MeshFilter>(true);
            return UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MeshFilter>()).Select(x => x?.TryCast<MeshFilter>()).Where(x => x != null);
        }

        private const float ULo = -1.8f;

        private static Bounds RegionBounds(Opening o, float aMargin = 0.2f)
        {
            var pts = new List<Vector3>();
            foreach (var a in new[] { o.A0 - aMargin, o.A1 + aMargin })
                foreach (var u in new[] { ULo, o.UHi })
                    foreach (var y in new[] { o.TopY - 0.1f, o.YHi })
                        pts.Add(o.Origin + o.Right * a + o.Out * u + new Vector3(0, y - o.Origin.y, 0));
            var b = new Bounds(pts[0], Vector3.zero);
            foreach (var p in pts) b.Encapsulate(p);
            return b;
        }

        /// <summary>Cuts every model and collider in the opening that isn't cut yet. Returns how many triangles it cut now.</summary>
        internal static int Apply(Opening o, bool log)
        {
            var region = RegionBounds(o);
            int visual = 0, collider = 0;
            var notes = new List<string>();
            var unreadable = new List<Renderer>();
            foreach (var mf in Candidates(o))
            {
                var mesh = mf?.sharedMesh;
                if (mesh == null || OurMeshes.Contains(mesh.Pointer) || Checked.Contains((o, mesh.Pointer))) continue;
                if (o.Exclude != null && mf.transform.IsChildOf(o.Exclude)) continue;
                if (o.Within != null && !mf.transform.IsChildOf(o.Within)) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.bounds.Intersects(region)) continue;
                if (mr.isPartOfStaticBatch) { notes.Add($"{mf.gameObject.name}: static batch, can't be cut"); continue; }
                Mesh cut;
                try { cut = CutMesh(mesh, mf.transform, o, out int n); visual += n; }
                catch (Exception e)
                {
                    Checked.Add((o, mesh.Pointer));   // don't try again (each try reads it back from the graphics card)
                    unreadable.Add(mr);
                    notes.Add($"{mf.gameObject.name}: {e.Message}");
                    continue;
                }
                if (cut == null)
                {
                    Checked.Add((o, mesh.Pointer));   // reading a model back is slow: don't look at it again (that made the game stutter)
                    if (log) notes.Add($"nothing to cut in {Path(mf.transform)} ({Near(mesh, mf.transform, mr, o)})");
                    continue;
                }
                OurMeshes.Add(cut.Pointer);
                mf.sharedMesh = cut;
                o.CutRenderers.Add(mr.Pointer);
                notes.Add($"model {Path(mf.transform)}");
            }
            // The detailed model can't always be read (then its railing can't be cut). Its less detailed versions can: show
            // one of those for this wall piece only, so the opening is really open.
            // (1.8.0 made this optional and off, which brought the railing back into the opening: it stays always on)
            if (unreadable.Count > 0 && o.Within != null) ForceCutDetail(o, notes);

            foreach (var c in Physics.OverlapBox(region.center, region.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
            {
                var mc = c?.TryCast<MeshCollider>();
                var mesh = mc?.sharedMesh;
                if (mesh == null || OurMeshes.Contains(mesh.Pointer) || !mc.enabled || Checked.Contains((o, mesh.Pointer))) continue;
                if (o.Exclude != null && mc.transform.IsChildOf(o.Exclude)) continue;
                if (o.Within != null && !mc.transform.IsChildOf(o.Within)) continue;
                Mesh cut;
                try { cut = CutMesh(mesh, mc.transform, o, out int n); collider += n; }
                catch (Exception e) { notes.Add($"collider {mc.gameObject.name}: {e.Message}"); continue; }
                if (cut == null) { Checked.Add((o, mesh.Pointer)); continue; }
                OurMeshes.Add(cut.Pointer);
                mc.sharedMesh = null;
                mc.sharedMesh = cut;
                notes.Add($"collider {Path(mc.transform)}");
            }
            o.Visual += visual; o.Collider += collider;
            if ((log || visual + collider > 0) && notes.Count > 0)
                Plugin.L.LogInfo($"[pier] {o.Name}: opening in the railing: cut {visual} triangles from models, {collider} from colliders ({string.Join("; ", notes)})");
            return visual + collider;
        }

        /// <summary>
        /// Makes the wall piece's LOD group always show the most detailed level whose model we could cut (the game's most
        /// detailed level can be unreadable). Only this wall piece; a bit less detail on it.
        /// </summary>
        private static void ForceCutDetail(Opening o, List<string> notes)
        {
            if (o.Forced) return;
            foreach (var lg in o.Within.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lg.GetLODs();
                for (int i = 0; i < lods.Length; i++)
                {
                    var rs = lods[i].renderers;
                    if (rs == null) continue;
                    bool cut = false;
                    foreach (var r in rs) if (r != null && o.CutRenderers.Contains(r.Pointer)) cut = true;
                    if (!cut) continue;
                    if (i > 0)
                    {
                        lg.ForceLOD(i);
                        o.Forced = true;
                        notes.Add($"the detailed model can't be read, so this wall piece always shows detail level {i} (cut)");
                    }
                    break;
                }
            }
        }

        /// <summary>Re-checks all openings (models swapped in later).</summary>
        private static float _lastRefresh;
        internal static void Refresh()
        {
            if (Time.realtimeSinceStartup - _lastRefresh < 20f) return;
            _lastRefresh = Time.realtimeSinceStartup;
            foreach (var o in Openings)
                try { Apply(o, false); } catch (Exception e) { Plugin.L.LogWarning($"[pier] {o.Name}: {e.Message}"); }
        }

        private static string V(Vector3 v) => $"({v.x:0.#}, {v.y:0.#}, {v.z:0.#})";

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ------------------------------------------------------------------ cutting

        /// <summary>For the log: what the model has around the opening, when nothing was cut.</summary>
        private static string Near(Mesh mesh, Transform t, MeshRenderer mr, Opening o)
        {
            try
            {
                var d = ModelExporter.ReadMesh(mesh);
                var m = t.localToWorldMatrix;
                var w = d.Pos.Select(p => m.MultiplyPoint3x4(p)).ToList();
                // where the vertices really are, compared with where Unity draws the model
                var wb = new Bounds(w[0], Vector3.zero); foreach (var p in w) wb.Encapsulate(p);
                var lb = new Bounds(d.Pos[0], Vector3.zero); foreach (var p in d.Pos) lb.Encapsulate(p);
                // vertices in the opening's width, whatever their height and distance
                float wu0 = 99, wu1 = -99, wy0 = 99, wy1 = -99; int inWidth = 0;
                foreach (var p in w)
                {
                    float a = Vector3.Dot(p - o.Origin, o.Right);
                    if (a < o.A0 - 1 || a > o.A1 + 1) continue;
                    inWidth++;
                    float u = Vector3.Dot(p - o.Origin, o.Out);
                    wu0 = Math.Min(wu0, u); wu1 = Math.Max(wu1, u); wy0 = Math.Min(wy0, p.y); wy1 = Math.Max(wy1, p.y);
                }
                string extra = $"; read {(mesh.isReadable ? "directly" : "from the GPU")}, mesh {mesh.name} local {V(lb.min)}–{V(lb.max)}, " +
                               $"as placed {V(wb.min)}–{V(wb.max)}, drawn at {V(mr.bounds.min)}–{V(mr.bounds.max)}, scale {V(t.lossyScale)}; " +
                               $"{inWidth} vertices within 1 m of the opening's width: out {wu0:0.##}..{wu1:0.##}, height {wy0:0.##}..{wy1:0.##}";
                int n = 0; float u0 = 99, u1 = -99, y0 = 99, y1 = -99, big = 0;
                foreach (var tris in d.Tris)
                    for (int k = 0; k + 2 < tris.Length; k += 3)
                    {
                        bool any = false;
                        for (int q = 0; q < 3; q++)
                        {
                            var p = w[tris[k + q]];
                            float a = Vector3.Dot(p - o.Origin, o.Right), u = Vector3.Dot(p - o.Origin, o.Out);
                            if (a >= o.A0 && a <= o.A1 && u >= -3 && u <= o.UHi + 2 && p.y >= o.TopY - 1 && p.y <= o.YHi + 1) any = true;
                        }
                        if (!any) continue;
                        n++;
                        float tu0 = 99, tu1 = -99, ty0 = 99, ty1 = -99;
                        for (int q = 0; q < 3; q++)
                        {
                            var p = w[tris[k + q]];
                            float u = Vector3.Dot(p - o.Origin, o.Out);
                            tu0 = Math.Min(tu0, u); tu1 = Math.Max(tu1, u); ty0 = Math.Min(ty0, p.y); ty1 = Math.Max(ty1, p.y);
                        }
                        u0 = Math.Min(u0, tu0); u1 = Math.Max(u1, tu1); y0 = Math.Min(y0, ty0); y1 = Math.Max(y1, ty1);
                        if (ty1 - ty0 > 3 || tu1 - tu0 > 3) big++;
                    }
                return $"{mesh.vertexCount} vertices, {n} triangles near the opening, out {u0:0.##}..{u1:0.##}, height {y0:0.##}..{y1:0.##}, {big} of them large" + extra;
            }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>The mesh without the railing in the opening (triangles split at its two ends), or null if nothing was there.</summary>
        private static Mesh CutMesh(Mesh mesh, Transform t, Opening o, out int n)
        {
            n = 0;
            var data = ModelExporter.ReadMesh(mesh);
            var m = t.localToWorldMatrix;
            var pos = new List<Vector3>(data.Pos);
            var nrm = data.Nrm != null ? new List<Vector3>(data.Nrm) : null;
            var uv = data.Uv != null ? new List<Vector2>(data.Uv) : null;
            var world = data.Pos.Select(p => m.MultiplyPoint3x4(p)).ToList();
            float yLo = o.TopY + 0.3f;
            float A(int i) => Vector3.Dot(world[i] - o.Origin, o.Right);
            bool InStrip(int i)
            {
                float u = Vector3.Dot(world[i] - o.Origin, o.Out), y = world[i].y;
                return u >= ULo && u <= o.UHi && y >= o.TopY - 1.2f && y <= o.YHi;   // posts can reach down into the walkway
            }
            int Lerp(int i, int j, float k)
            {
                pos.Add(Vector3.Lerp(pos[i], pos[j], k));
                world.Add(Vector3.Lerp(world[i], world[j], k));
                nrm?.Add(Vector3.Lerp(nrm[i], nrm[j], k).normalized);
                uv?.Add(Vector2.Lerp(uv[i], uv[j], k));
                return pos.Count - 1;
            }
            List<int> Clip(List<int> poly, float edge, bool keepBelow)
            {
                var res = new List<int>();
                for (int k = 0; k < poly.Count; k++)
                {
                    int i = poly[k], j = poly[(k + 1) % poly.Count];
                    float ai = A(i) - edge, aj = A(j) - edge;
                    bool inI = keepBelow ? ai <= 0 : ai >= 0, inJ = keepBelow ? aj <= 0 : aj >= 0;
                    if (inI) res.Add(i);
                    if (inI != inJ) res.Add(Lerp(i, j, ai / (ai - aj)));
                }
                return res;
            }

            var subs = new List<int[]>();
            foreach (var tris in data.Tris)
            {
                var outT = new List<int>(tris.Length);
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    int i0 = tris[k], i1 = tris[k + 1], i2 = tris[k + 2];
                    bool raised = Math.Max(world[i0].y, Math.Max(world[i1].y, world[i2].y)) > yLo;
                    if (!(raised && InStrip(i0) && InStrip(i1) && InStrip(i2))) { outT.Add(i0); outT.Add(i1); outT.Add(i2); continue; }
                    float x0 = A(i0), x1 = A(i1), x2 = A(i2);
                    if (Math.Max(x0, Math.Max(x1, x2)) <= o.A0 || Math.Min(x0, Math.Min(x1, x2)) >= o.A1) { outT.Add(i0); outT.Add(i1); outT.Add(i2); continue; }
                    n++;
                    var poly = new List<int> { i0, i1, i2 };
                    foreach (var part in new[] { Clip(poly, o.A0, true), Clip(poly, o.A1, false) })
                        for (int q = 1; q + 1 < part.Count; q++) { outT.Add(part[0]); outT.Add(part[q]); outT.Add(part[q + 1]); }
                }
                subs.Add(outT.ToArray());
            }
            if (n == 0) return null;

            var copy = new Mesh { name = mesh.name + "_pier" };
            if (pos.Count > 65000) copy.indexFormat = IndexFormat.UInt32;
            copy.vertices = V3(pos);
            if (nrm != null) copy.normals = V3(nrm);
            if (uv != null) { var u2 = V2(uv); copy.uv = u2; try { copy.uv2 = u2; copy.uv3 = u2; copy.uv4 = u2; } catch { } }
            copy.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) copy.SetTriangles(I(subs[s]), s);
            copy.RecalculateBounds();
            try { copy.RecalculateTangents(); } catch { }
            return copy;
        }

        // ------------------------------------------------------------------ copying the game's railing

        internal sealed class Segment
        {
            public float Length;   // one post plus bars, along the rail
            public float Height;
            public readonly List<Part> Parts = new();
            public MeshRenderer Source;
            public sealed class Part
            {
                public Material Mat;
                public readonly List<Vector3> Pos = new(), Nrm = new();   // x along, y up, z sideways (out to sea)
                public readonly List<Vector2> Uv = new();
                public readonly List<int> Tris = new();
            }
        }

        /// <summary>
        /// Takes one post-to-post piece of the game's railing from the walkway edge between a0 and a1 (along the wall),
        /// in the frame of the opening. Null if no railing with posts is found there.
        /// </summary>
        internal static Segment Extract(Opening o, float a0, float a1, out string why)
        {
            why = null;
            var probe = new Opening { Name = o.Name, Origin = o.Origin, Right = o.Right, Out = o.Out, A0 = a0, A1 = a1, TopY = o.TopY, YHi = o.YHi, UHi = o.UHi, Exclude = o.Exclude };
            var region = RegionBounds(probe, 0);
            var found = new List<(MeshRenderer mr, ModelExporter.MeshData d, List<Vector3> world, Matrix4x4 m)>();
            foreach (var mf in Candidates(o))
            {
                var mesh = mf?.sharedMesh;
                if (mesh == null || (o.Exclude != null && mf.transform.IsChildOf(o.Exclude))) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled || !mr.bounds.Intersects(region) || mr.isPartOfStaticBatch) continue;
                try
                {
                    var d = ModelExporter.ReadMesh(mesh);
                    var m = mf.transform.localToWorldMatrix;
                    found.Add((mr, d, d.Pos.Select(p => m.MultiplyPoint3x4(p)).ToList(), m));
                }
                catch { }
            }
            if (found.Count == 0) { why = "no model there"; return null; }

            float A(Vector3 w) => Vector3.Dot(w - o.Origin, o.Right);
            float U(Vector3 w) => Vector3.Dot(w - o.Origin, o.Out);
            bool In(Vector3 w) { float u = U(w), a = A(w); return u >= ULo && u <= o.UHi && w.y >= o.TopY - 0.6f && w.y <= o.YHi && a >= a0 && a <= a1; }

            // posts: railing triangles that reach from near the ground up to the top rail
            var postA = new List<float>(); var postU = new List<float>();
            float top = o.TopY;
            foreach (var (mr, d, world, _) in found)
                foreach (var tris in d.Tris)
                    for (int k = 0; k + 2 < tris.Length; k += 3)
                    {
                        var w0 = world[tris[k]]; var w1 = world[tris[k + 1]]; var w2 = world[tris[k + 2]];
                        if (!(In(w0) && In(w1) && In(w2))) continue;
                        float ymin = Math.Min(w0.y, Math.Min(w1.y, w2.y)), ymax = Math.Max(w0.y, Math.Max(w1.y, w2.y));
                        if (ymax > o.TopY + 0.3f) top = Math.Max(top, ymax);
                        if (ymin < o.TopY + 0.45f && ymax > o.TopY + 0.7f)
                        {
                            float aw = Math.Max(A(w0), Math.Max(A(w1), A(w2))) - Math.Min(A(w0), Math.Min(A(w1), A(w2)));
                            if (aw < 0.5f) { postA.Add((A(w0) + A(w1) + A(w2)) / 3); postU.Add((U(w0) + U(w1) + U(w2)) / 3); }
                        }
                    }
            postA.Sort();
            var centres = new List<float>();
            foreach (var a in postA)
                if (centres.Count == 0 || a - centres[^1] > 0.4f) centres.Add(a);
                else centres[^1] = (centres[^1] + a) / 2;
            if (centres.Count < 3) { why = $"found {centres.Count} railing posts, need 3"; return null; }
            var gaps = centres.Zip(centres.Skip(1), (p, q) => q - p).OrderBy(x => x).ToList();
            float spacing = gaps[gaps.Count / 2];
            if (spacing < 0.6f || spacing > 8f) { why = $"posts {spacing:0.##} m apart"; return null; }
            // one post in the middle of the piece: cut halfway to its neighbours
            int mid = centres.Count / 2;
            float s0 = centres[mid] - spacing / 2, s1 = centres[mid] + spacing / 2;
            postU.Sort();
            float railU = postU[postU.Count / 2];

            var seg = new Segment { Length = spacing, Height = top - o.TopY };
            foreach (var (mr, d, world, mtx) in found)
            {
                var mats = mr.sharedMaterials;
                for (int sub = 0; sub < d.Tris.Count; sub++)
                {
                    var part = new Segment.Part { Mat = sub < mats.Length ? mats[sub] : mr.sharedMaterial };
                    var tris = d.Tris[sub];
                    // collect railing triangles in [s0, s1], clipped at both ends
                    var wl = new List<Vector3>(); var nl = new List<Vector3>(); var ul = new List<Vector2>();
                    for (int k = 0; k + 2 < tris.Length; k += 3)
                    {
                        int i0 = tris[k], i1 = tris[k + 1], i2 = tris[k + 2];
                        var w0 = world[i0]; var w1 = world[i1]; var w2 = world[i2];
                        if (!(InRail(w0) && InRail(w1) && InRail(w2))) continue;
                        if (Math.Max(w0.y, Math.Max(w1.y, w2.y)) <= o.TopY + 0.3f) continue;
                        var poly = new List<(Vector3 w, Vector3 n, Vector2 uv)> { V(d, world, mtx, i0), V(d, world, mtx, i1), V(d, world, mtx, i2) };
                        poly = ClipA(poly, s0, false, A);
                        poly = ClipA(poly, s1, true, A);
                        for (int q = 1; q + 1 < poly.Count; q++)
                            foreach (var v in new[] { poly[0], poly[q], poly[q + 1] })
                            {
                                part.Tris.Add(part.Pos.Count);
                                part.Pos.Add(new Vector3(A(v.w) - s0, v.w.y - o.TopY, U(v.w) - railU));
                                part.Nrm.Add(new Vector3(Vector3.Dot(v.n, o.Right), v.n.y, Vector3.Dot(v.n, o.Out)));
                                part.Uv.Add(v.uv);
                            }
                    }
                    if (part.Tris.Count > 0) { seg.Parts.Add(part); seg.Source ??= mr; }
                }
            }
            if (seg.Parts.Count == 0) { why = "railing posts found but no railing pieces"; return null; }
            return seg;

            bool InRail(Vector3 w) { float u = U(w); return u >= railU - 0.6f && u <= railU + 0.6f && w.y >= o.TopY - 0.6f && w.y <= o.YHi; }
        }

        private static (Vector3 w, Vector3 n, Vector2 uv) V(ModelExporter.MeshData d, List<Vector3> world, Matrix4x4 m, int i)
            => (world[i], d.Nrm != null ? m.MultiplyVector(d.Nrm[i]).normalized : Vector3.up, d.Uv != null ? d.Uv[i] : Vector2.zero);

        private static List<(Vector3 w, Vector3 n, Vector2 uv)> ClipA(List<(Vector3 w, Vector3 n, Vector2 uv)> poly, float edge, bool keepBelow, Func<Vector3, float> A)
        {
            var res = new List<(Vector3, Vector3, Vector2)>();
            for (int k = 0; k < poly.Count; k++)
            {
                var p = poly[k]; var q = poly[(k + 1) % poly.Count];
                float ap = A(p.w) - edge, aq = A(q.w) - edge;
                bool inP = keepBelow ? ap <= 0 : ap >= 0, inQ = keepBelow ? aq <= 0 : aq >= 0;
                if (inP) res.Add(p);
                if (inP != inQ)
                {
                    float t = ap / (ap - aq);
                    res.Add((Vector3.Lerp(p.w, q.w, t), Vector3.Lerp(p.n, q.n, t).normalized, Vector2.Lerp(p.uv, q.uv, t)));
                }
            }
            return res;
        }

        /// <summary>
        /// Puts copies of the segment along each line (base of the railing, from p0 to p1), stretched a little so a whole
        /// number fits, sloped lines sheared so posts stay upright. One mesh per material, under `parent`.
        /// </summary>
        internal static int Place(Segment seg, Transform parent, IEnumerable<(Vector3 p0, Vector3 p1)> lines)
        {
            var meshes = new Dictionary<Material, (List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t)>();
            int copies = 0;
            foreach (var (p0, p1) in lines)
            {
                var d = p1 - p0;
                var flat = new Vector3(d.x, 0, d.z);
                float len = flat.magnitude;
                if (len < 0.3f) continue;
                var along = flat / len;
                var side = Vector3.Cross(along, Vector3.up);   // the rail's local "out" side; this order keeps the model's handedness (else it's inside out)
                float slope = d.y / len;
                int count = Math.Max(1, (int)Math.Round(len / seg.Length));
                float k = len / (count * seg.Length);
                for (int c = 0; c < count; c++)
                {
                    foreach (var part in seg.Parts)
                    {
                        if (!meshes.TryGetValue(part.Mat, out var mb)) meshes[part.Mat] = mb = (new(), new(), new(), new());
                        int baseIdx = mb.v.Count;
                        for (int i = 0; i < part.Pos.Count; i++)
                        {
                            var lp = part.Pos[i];
                            float x = (c * seg.Length + lp.x) * k;
                            mb.v.Add(p0 + along * x + side * lp.z + Vector3.up * (lp.y + slope * x));
                            var ln = part.Nrm[i];
                            mb.n.Add((along * ln.x + Vector3.up * ln.y + side * ln.z).normalized);
                            mb.uv.Add(part.Uv[i]);
                        }
                        foreach (var ti in part.Tris) mb.t.Add(baseIdx + ti);
                    }
                    copies++;
                }
            }
            foreach (var kv in meshes)
            {
                var go = new GameObject("GameRailing");
                go.transform.SetParent(parent, false);
                go.layer = parent.gameObject.layer;
                var mesh = new Mesh { name = "Pier_GameRailing" };
                if (kv.Value.v.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
                mesh.vertices = V3(kv.Value.v);
                mesh.normals = V3(kv.Value.n);
                var uv = V2(kv.Value.uv);
                mesh.uv = uv;
                try { mesh.uv2 = uv; mesh.uv3 = uv; mesh.uv4 = uv; } catch { }
                mesh.triangles = I(kv.Value.t);
                mesh.RecalculateBounds();
                try { mesh.RecalculateTangents(); } catch { }
                OurMeshes.Add(mesh.Pointer);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = kv.Key;
                if (seg.Source != null) ExteriorLoader.CopyRendererSettings(seg.Source, mr);
            }
            return copies;
        }

        private static Il2CppStructArray<Vector3> V3(List<Vector3> a) { var r = new Il2CppStructArray<Vector3>(a.Count); for (int i = 0; i < a.Count; i++) r[i] = a[i]; return r; }
        private static Il2CppStructArray<Vector2> V2(List<Vector2> a) { var r = new Il2CppStructArray<Vector2>(a.Count); for (int i = 0; i < a.Count; i++) r[i] = a[i]; return r; }
        private static Il2CppStructArray<int> I(int[] a) { var r = new Il2CppStructArray<int>(a.Length); for (int i = 0; i < a.Length; i++) r[i] = a[i]; return r; }
        private static Il2CppStructArray<int> I(List<int> a) { var r = new Il2CppStructArray<int>(a.Count); for (int i = 0; i < a.Count; i++) r[i] = a[i]; return r; }
    }
}

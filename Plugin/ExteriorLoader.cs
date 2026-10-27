using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomBuildings
{
    using CustomBuildings.Shared;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Builds a custom building's outside from Buildings/&lt;id&gt;/exterior (OBJ + PNGs made by the editor) and puts it on the
    /// cloned BuildingPreset in place of the base building's model. The parts are copied from the base building where the
    /// game expects something (material/shader, tags, layers). Anything that fails leaves the base building's model in place.
    /// </summary>
    internal static class ExteriorLoader
    {
        private static GameObject _holder;
        private static readonly Dictionary<string, Part> BaseParts = new();
        private static readonly Dictionary<string, Texture2D> LitMaps = new();
        internal static (string id, Texture2D tex) LitMapOf(string id) => (id, LitMaps.TryGetValue(id, out var t) ? t : null);
        private static readonly Dictionary<string, GameObject> Built = new();

        private sealed class Part
        {
            public string Name;
            public readonly List<Vector3> V = new(), N = new();
            public readonly List<Vector2> Uv = new();
            public readonly List<int> Tris = new();
            public readonly Dictionary<(int, int, int), int> Map = new();
        }

        public static bool Apply(BuildingPreset clone, BuildingPreset basePreset, BuildingDef def, string buildingDir)
        {
            var dir = Path.Combine(buildingDir, ModPaths.ExteriorDir);
            var infoPath = Path.Combine(dir, ModPaths.ExteriorInfoFile);
            if (!File.Exists(infoPath)) return false;

            try
            {
                var info = JsonSerializer.Deserialize<ExteriorInfo>(File.ReadAllText(infoPath), Json.Options) ?? new ExteriorInfo();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                void Step(string what) { Plugin.L.LogInfo($"{def.Id}: {what} ({clock.ElapsedMilliseconds} ms)"); }
                Step("loading its own exterior");

                if (!Built.TryGetValue(def.Id, out var root) || root == null || root.WasCollected)
                {
                    root = Build(def, info, dir, basePreset);
                    Built[def.Id] = root;
                }
                Step("model built");

                var baseMesh = root.transform.Find("Base")?.GetComponent<MeshFilter>()?.sharedMesh;
                clone.prefab = root;
                clone.tex = Texture(dir, info.Diffuse, srgb: true);
                clone.emissionMapLit = Texture(dir, info.Lit);
                LitMaps[def.Id] = clone.emissionMapLit;
                clone.emissionMapUnlit = Texture(dir, info.Unlit);
                clone.windowMap = Texture(dir, info.WindowMap, mask: true);
                Step($"textures loaded ({clone.tex?.width}x{clone.tex?.height}, colour {clone.tex?.graphicsFormat}, glow {clone.emissionMapUnlit?.graphicsFormat}, {clone.emissionMapUnlit?.mipmapCount} mips)");
                clone.addonMap = null;
                if (baseMesh != null) clone.captureMesh = baseMesh;
                clone.cableLinkPoints = new Il2CppSystem.Collections.Generic.List<BuildingPreset.CableLinkPoint>();
                clone.sideSignPoints = new Il2CppSystem.Collections.Generic.List<BuildingPreset.CableLinkPoint>();
                clone.featuresSmokestack = false;
                clone.lightningRodLocalPos = new Vector3(0, info.WallTopY + 1f, 0);
                if (info.Storeys > 0) clone.floorCount = info.Storeys;

                try { clone.CalculateMeshHeight(); }
                catch (Exception e) { Plugin.L.LogWarning($"{def.Id}: couldn't measure the exterior: {e.Message}"); }
                Step($"measured (height {clone.meshHeight:0.#})");

                // window data from the window map (needs the captureMesh's UVs). The game's own routine can be slow on
                // big maps, so the result is cached outside the Buildings folder (which must stay identical for multiplayer).
                clone.sortedWindows = new Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVFloor>();
                int found = 0, floorsWithWindows = 0;
                string cacheFile = WindowCache.FileFor(def.Id, dir, info);
                try
                {
                    if (!Plugin.ExteriorWindowLights.Value)
                        Step("window lights are off in the config (ExteriorWindowLights), skipping the window map");
                    else if (Plugin.ExteriorWindowMethod.Value != "Game" && BaseParts.TryGetValue(def.Id, out var bp) && clone.windowMap != null)
                    {
                        var map = clone.windowMap;
                        var px = map.GetPixels32();
                        var white = new bool[map.width * map.height];
                        for (int i = 0; i < white.Length; i++) { var c = px[i]; white[i] = c.r > 127 || c.g > 127 || c.b > 127; }
                        var wins = WindowFinder.Find(white, map.width, map.height, bp.V, bp.Uv, bp.N, bp.Tris);
                        WindowFinder.Apply(clone, wins);
                        Step($"windows worked out from the window map ({wins.Count})");
                    }
                    else if (WindowCache.TryLoad(cacheFile, clone))
                        Step("window data read from the cache");
                    else
                    {
                        Step("working out the windows from the window map (first start after a change; can take a while)");
                        clone.GenerateWindowData();
                        Step("windows worked out");
                        WindowCache.Save(cacheFile, clone);
                    }
                    if (clone.sortedWindows != null)
                        foreach (var f in clone.sortedWindows)
                        {
                            int n = (f.front?.Count ?? 0) + (f.back?.Count ?? 0) + (f.left?.Count ?? 0) + (f.right?.Count ?? 0);
                            found += n;
                            if (n > 0) floorsWithWindows++;
                        }
                }
                catch (Exception e) { Plugin.L.LogWarning($"{def.Id}: window data from the window map failed: {e.Message}"); }

                Plugin.L.LogInfo($"{def.Id}: using its own exterior ({info.Storeys} storeys, height {clone.meshHeight:0.#}). " +
                                 $"The game found {found} of {info.Windows} windows on {floorsWithWindows} storey(s).");
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogError($"{def.Id}: couldn't load its exterior, using {def.BasePreset}'s look instead: {e}");
                return false;
            }
        }

        // ------------------------------------------------------------------ model

        private static GameObject Build(BuildingDef def, ExteriorInfo info, string dir, BuildingPreset basePreset)
        {
            var parts = ReadObj(Path.Combine(dir, info.Obj), info.MirrorX);
            if (parts.Count == 0) throw new Exception("the exterior model has no faces");
            var basePart = parts.Find(p => p.Name == "Base");
            if (basePart != null) BaseParts[def.Id] = basePart;

            // look like the base building: its "Base" part's material (shader), tag and layer
            var vRoot = basePreset.prefab;
            var vBase = vRoot != null ? vRoot.transform.Find("Base") : null;
            MeshRenderer vRenderer = vBase != null ? vBase.GetComponent<MeshRenderer>() : null;
            if (vRenderer == null && vRoot != null)
            {
                var all = vRoot.GetComponentsInChildren<MeshRenderer>(true);
                if (all != null && all.Length > 0) vRenderer = all[0];
            }

            var diffuse = Texture(dir, info.Diffuse, srgb: true);
            Material mat = null;
            if (vRenderer != null && vRenderer.sharedMaterial != null)
            {
                mat = new Material(vRenderer.sharedMaterial) { name = def.Id + "_Exterior" };
                try { mat.mainTexture = diffuse; } catch { }
                foreach (var p in new[] { "_MainTex", "_BaseColorMap", "_BaseMap" })
                    try { if (mat.HasProperty(p)) mat.SetTexture(p, diffuse); } catch { }
                CleanMaterial(mat, def.Id);
                mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }

            if (_holder == null || _holder.WasCollected)
            {
                // inactive holder: the model templates live here and are never drawn themselves
                _holder = new GameObject("CustomBuildings.Exteriors");
                _holder.SetActive(false);
                Object.DontDestroyOnLoad(_holder);
                _holder.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }

            var root = new GameObject(def.Id + "_Model");
            root.transform.SetParent(_holder.transform, false);
            if (vRoot != null)
            {
                try { root.tag = vRoot.tag; } catch { }
                root.layer = vRoot.layer;
            }
            root.hideFlags = HideFlags.DontUnloadUnusedAsset;

            foreach (var p in parts)
            {
                var go = new GameObject(p.Name);
                go.transform.SetParent(root.transform, false);
                var mesh = BuildMesh(p, def.Id + "_" + p.Name);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                if (mat != null) mr.sharedMaterial = mat;
                if (vRenderer != null) CopyRendererSettings(vRenderer, mr);
                if (p.Name == "Base")   // like the game's models: the shell is solid (raycasts, camera, thrown things)
                    try { go.AddComponent<MeshCollider>().sharedMesh = mesh; } catch (Exception e) { Plugin.Verbose($"{def.Id}: no collider: {e.Message}"); }
                if (vBase != null)
                {
                    try { go.tag = vBase.gameObject.tag; } catch { }
                    go.layer = vBase.gameObject.layer;
                }
                go.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            Plugin.Verbose($"{def.Id}: exterior built from {parts.Count} part(s), {parts.Sum(p => p.Tris.Count) / 3} triangles");
            return root;
        }

        private static readonly string[] SurfaceMapWords = { "normal", "mask", "detail", "bent", "specular", "coat", "height", "tangent", "aniso", "iridescence", "thickness" };

        /// <summary>
        /// The copied material still carries the base building's surface maps, laid out for the base building's UVs. On our
        /// UVs they land in random places (black metal, dents). The mask map (metal, ambient occlusion, smoothness) is
        /// replaced by a flat one with its average values, so the building is as bright and as shiny as the base building
        /// on average; the normal map and other detail maps are removed. Logs what it did.
        /// </summary>
        internal static void CleanMaterial(Material mat, string id)
        {
            var removed = new List<string>();
            string mask = "none";
            try
            {
                foreach (var name in mat.GetTexturePropertyNames())
                {
                    var lower = name.ToLowerInvariant();
                    var tex = mat.GetTexture(name);
                    if (tex == null || !SurfaceMapWords.Any(w => lower.Contains(w))) continue;
                    if (name == "_MaskMap") LookTest.BaseMask = tex;
                    if (name == "_NormalMap") LookTest.BaseNormal = tex;
                    if (name == "_MaskMap")
                    {
                        var avg = Average(tex);
                        if (avg != null)
                        {
                            var flat = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = id + "_FlatMask" };
                            var c = new Color32[16];
                            for (int i = 0; i < 16; i++) c[i] = avg.Value;
                            flat.SetPixels32(c);
                            flat.Apply(false, true);
                            flat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                            mat.SetTexture(name, flat);
                            mask = $"metal {avg.Value.r / 255f:0.00}, occlusion {avg.Value.g / 255f:0.00}, smoothness {avg.Value.a / 255f:0.00}";
                            continue;
                        }
                    }
                    mat.SetTexture(name, null);
                    removed.Add(name);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"{id}: couldn't look at the material's textures: {e.Message}"); }
            void F(string prop, float v) { try { if (mat.HasProperty(prop)) mat.SetFloat(prop, v); } catch { } }
            F("_NormalScale", 0f);
            try
            {
                if (mat.HasProperty("_EmissiveColor") && Math.Abs(Plugin.ExteriorGlow.Value - 1f) > 1e-4f)
                {
                    var c = mat.GetColor("_EmissiveColor");
                    float k = Math.Max(0f, Plugin.ExteriorGlow.Value);
                    mat.SetColor("_EmissiveColor", new Color(c.r * k, c.g * k, c.b * k, c.a));
                }
            }
            catch { }
            MakeMatt(mat);
            foreach (var k in new[] { "_NORMALMAP", "_DETAIL_MAP", "_BENTNORMALMAP", "_SPECULARCOLORMAP", "_NORMALMAP_TANGENT_SPACE", "_HEIGHTMAP" })
                try { mat.DisableKeyword(k); } catch { }

            string Col(string p) { try { return mat.HasProperty(p) ? mat.GetColor(p).ToString() : "-"; } catch { return "?"; } }
            string Num(string p) { try { return mat.HasProperty(p) ? mat.GetFloat(p).ToString("0.###") : "-"; } catch { return "?"; } }
            Plugin.L.LogInfo($"{id}: exterior material '{mat.shader?.name}': removed [{string.Join(", ", removed)}]; mask map → flat ({mask}). " +
                             $"BaseColor {Col("_BaseColor")}, Emissive {Col("_EmissiveColor")} x{Num("_EmissiveIntensity")}, " +
                             $"Metallic {Num("_Metallic")} ({Num("_MetallicRemapMin")}-{Num("_MetallicRemapMax")}), " +
                             $"Smoothness {Num("_Smoothness")} ({Num("_SmoothnessRemapMin")}-{Num("_SmoothnessRemapMax")}), " +
                             $"AO {Num("_AORemapMin")}-{Num("_AORemapMax")}, emission map {mat.GetTexture("_EmissiveColorMap")?.name ?? "none"}.");
        }

        /// <summary>
        /// Tested in game (Ctrl+Shift+4): with the base building's shine settings our walls looked white-hot; fully matt
        /// they look like the game's buildings. So: no metal, and only as much shine as the config asks for. Also applied
        /// again after the game sets a placed building up, because it makes its own copy of the material then.
        /// </summary>
        internal static void MakeMatt(Material mat)
        {
            if (mat == null) return;
            void F(string prop, float v) { try { if (mat.HasProperty(prop)) mat.SetFloat(prop, v); } catch { } }
            float shine = Math.Clamp(Plugin.ExteriorShine.Value, 0f, 1f);
            F("_Metallic", 0f); F("_MetallicRemapMin", 0f); F("_MetallicRemapMax", 0f);
            F("_Smoothness", shine); F("_SmoothnessRemapMin", 0f); F("_SmoothnessRemapMax", shine);
            // No glow unless asked for (it lit up whole walls, see ExteriorGlow).
            try { if (Plugin.ExteriorGlow.Value <= 0f && mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", Color.black); } catch { }
        }

        /// <summary>Average colour of any texture (also compressed, non-readable ones), read as linear data.</summary>
        internal static Color32? Average(Texture tex)
        {
            const int n = 32;
            var prev = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(n, n, TextureFormat.RGBA32, false, true);
                copy.ReadPixels(new Rect(0, 0, n, n), 0, 0);
                copy.Apply();
                var px = copy.GetPixels32();
                long r = 0, g = 0, b = 0, a = 0;
                for (int i = 0; i < n * n; i++) { var c = px[i]; r += c.r; g += c.g; b += c.b; a += c.a; }
                int k = n * n;
                return new Color32((byte)(r / k), (byte)(g / k), (byte)(b / k), (byte)(a / k));
            }
            catch { return null; }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) Object.Destroy(copy);
            }
        }

        /// <summary>
        /// The game's building renderers are set up for its lighting: which lights reach them (rendering layers), light
        /// probes, reflection probes, shadows. A fresh MeshRenderer gets Unity's defaults and is lit by everything, so it
        /// glows. Copy the base building's settings, and log them plus what else sits on the base building's model.
        /// </summary>
        private static bool _loggedRenderer;
        internal static void CopyRendererSettings(MeshRenderer from, MeshRenderer to)
        {
            void Try(Action a) { try { a(); } catch { } }
            Try(() => to.renderingLayerMask = from.renderingLayerMask);
            Try(() => to.shadowCastingMode = from.shadowCastingMode);
            Try(() => to.receiveShadows = from.receiveShadows);
            Try(() => to.lightProbeUsage = from.lightProbeUsage);
            Try(() => to.reflectionProbeUsage = from.reflectionProbeUsage);
            Try(() => to.motionVectorGenerationMode = from.motionVectorGenerationMode);
            Try(() => to.allowOcclusionWhenDynamic = from.allowOcclusionWhenDynamic);
            Try(() => to.staticShadowCaster = from.staticShadowCaster);
            Try(() => to.rendererPriority = from.rendererPriority);
            if (_loggedRenderer) return;
            _loggedRenderer = true;
            try
            {
                var comps = new List<string>();
                var root = from.transform.root;
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                    if (c != null) comps.Add(c.gameObject.name + ":" + c.GetIl2CppType().Name);
                Plugin.L.LogInfo($"Base building renderer '{from.gameObject.name}': renderingLayerMask {from.renderingLayerMask}, shadows {from.shadowCastingMode}/{from.receiveShadows}, " +
                                 $"lightProbes {from.lightProbeUsage}, reflectionProbes {from.reflectionProbeUsage}, lightmapIndex {from.lightmapIndex}, layer {from.gameObject.layer}. " +
                                 $"Model parts: {string.Join(", ", comps.Distinct().Take(40))}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"Couldn't describe the base building's renderer: {e.Message}"); }
        }

        private static Mesh BuildMesh(Part p, string name)
        {
            var mesh = new Mesh { name = name };
            if (p.V.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            var v = new Il2CppStructArray<Vector3>(p.V.Count);
            for (int i = 0; i < p.V.Count; i++) v[i] = p.V[i];
            mesh.vertices = v;
            if (p.Uv.Count == p.V.Count)
            {
                var uv = new Il2CppStructArray<Vector2>(p.Uv.Count);
                for (int i = 0; i < p.Uv.Count; i++) uv[i] = p.Uv[i];
                mesh.uv = uv;
                // The game's materials may read the glow picture through another UV set (its own models carry several).
                // Without one, every pixel reads the same spot and the whole wall glows (Ctrl+Shift+7 test). Give all sets
                // the same layout.
                try { mesh.uv2 = uv; mesh.uv3 = uv; mesh.uv4 = uv; } catch (Exception e) { Plugin.Verbose($"{name}: extra UV sets: {e.Message}"); }
            }
            var t = new Il2CppStructArray<int>(p.Tris.Count);
            for (int i = 0; i < p.Tris.Count; i++) t[i] = p.Tris[i];
            mesh.triangles = t;
            if (p.N.Count == p.V.Count)
            {
                var n = new Il2CppStructArray<Vector3>(p.N.Count);
                for (int i = 0; i < p.N.Count; i++) n[i] = p.N[i];
                mesh.normals = n;
            }
            else mesh.RecalculateNormals();
            if (p.Uv.Count == p.V.Count) try { mesh.RecalculateTangents(); } catch { }   // the game's lit shader wants them
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return mesh;
        }

        /// <summary>
        /// Reads the OBJ (one part per "o" line). The file uses the model-export convention (X mirrored, winding reversed);
        /// this undoes both so the mesh is back in Unity space. MirrorX additionally flips the model left to right.
        /// </summary>
        private static List<Part> ReadObj(string path, bool mirrorX)
        {
            var inv = CultureInfo.InvariantCulture;
            var v = new List<Vector3>(); var vt = new List<Vector2>(); var vn = new List<Vector3>();
            var parts = new List<Part>();
            Part cur = null;
            float sx = mirrorX ? 1f : -1f;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length < 2 || line[0] == '#') continue;
                var s = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (s[0])
                {
                    case "o":
                        cur = new Part { Name = s.Length > 1 ? s[1] : "Part" + parts.Count };
                        parts.Add(cur);
                        break;
                    case "v":
                        v.Add(new Vector3(sx * float.Parse(s[1], inv), float.Parse(s[2], inv), float.Parse(s[3], inv)));
                        break;
                    case "vt":
                        vt.Add(new Vector2(float.Parse(s[1], inv), s.Length > 2 ? float.Parse(s[2], inv) : 0));
                        break;
                    case "vn":
                        vn.Add(new Vector3(sx * float.Parse(s[1], inv), float.Parse(s[2], inv), float.Parse(s[3], inv)));
                        break;
                    case "f":
                        if (cur == null) { cur = new Part { Name = "Base" }; parts.Add(cur); }
                        var idx = new int[s.Length - 1];
                        for (int i = 1; i < s.Length; i++) idx[i - 1] = Vertex(cur, s[i], v, vt, vn);
                        for (int i = 1; i + 1 < idx.Length; i++)
                        {
                            // reversed back to Unity's winding, unless mirrored a second time
                            if (mirrorX) { cur.Tris.Add(idx[0]); cur.Tris.Add(idx[i]); cur.Tris.Add(idx[i + 1]); }
                            else { cur.Tris.Add(idx[0]); cur.Tris.Add(idx[i + 1]); cur.Tris.Add(idx[i]); }
                        }
                        break;
                }
            }
            return parts.Where(p => p.Tris.Count > 0).ToList();
        }

        private static int Vertex(Part p, string token, List<Vector3> v, List<Vector2> vt, List<Vector3> vn)
        {
            var f = token.Split('/');
            int Ix(int i, int count)
            {
                if (f.Length <= i || f[i].Length == 0) return -1;
                int k = int.Parse(f[i], CultureInfo.InvariantCulture);
                return k < 0 ? count + k : k - 1;
            }
            var key = (Ix(0, v.Count), Ix(1, vt.Count), Ix(2, vn.Count));
            if (p.Map.TryGetValue(key, out var idx)) return idx;
            idx = p.V.Count;
            p.V.Add(v[key.Item1]);
            if (key.Item2 >= 0) p.Uv.Add(vt[key.Item2]); else if (p.Uv.Count > 0) p.Uv.Add(Vector2.zero);
            if (key.Item3 >= 0) p.N.Add(vn[key.Item3]); else if (p.N.Count > 0) p.N.Add(Vector3.up);
            p.Map[key] = idx;
            return idx;
        }

        // ------------------------------------------------------------------ textures

        private static readonly Dictionary<string, Texture2D> Textures = new(StringComparer.OrdinalIgnoreCase);

        internal static Texture2D Texture(string dir, string file, bool mask = false, bool srgb = false)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) return null;
            if (Textures.TryGetValue(path, out var t) && t != null && !t.WasCollected) return t;
            Il2CppStructArray<byte> arr = File.ReadAllBytes(path);   // one copy, not byte by byte
            // Colour pictures are sRGB, the window map is data. Say so explicitly: read the wrong way, colours come out
            // several times too bright.
            t = new Texture2D(2, 2, TextureFormat.RGBA32, true, mask);
            if (!ImageConversion.LoadImage(t, arr, false)) throw new Exception($"couldn't read {file}");
            int max = Plugin.ExteriorTextureMaxSize.Value;
            if (max > 0 && t.width > max) t = Downscale(t, max, mask);
            if (srgb) t = AsSrgb(t);
            t.name = Path.GetFileNameWithoutExtension(file);
            t.filterMode = FilterMode.Point;      // crisp pixels, like the game's own textures
            t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Textures[path] = t;
            return t;
        }

        /// <summary>
        /// The game's colour textures are sRGB (RGBA_BC7_SRGB). A PNG loaded by script can end up read as linear data,
        /// which makes every colour several times too bright (the buildings looked like they glowed). Copy it into a
        /// texture that is sRGB for sure.
        /// </summary>
        private static Texture2D AsSrgb(Texture2D src)
        {
            try
            {
                if (src.graphicsFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB) return src;
                var t = new Texture2D(src.width, src.height, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB,
                                      UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain);
                t.SetPixels32(src.GetPixels32());
                t.Apply(true, false);
                t.name = src.name;
                Object.Destroy(src);
                return t;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Couldn't make {src.name} an sRGB texture: {e.Message}");
                return src;
            }
        }

        /// <summary>
        /// Halves the texture until it is at most maxWidth wide (game buildings use 1024 or less). Colours are averaged;
        /// for the window map a pixel stays white when at least half of the pixels it replaces were white. All four textures
        /// shrink by the same factor, so the model's UVs still fit.
        /// </summary>
        private static Texture2D Downscale(Texture2D src, int maxWidth, bool mask)
        {
            int w = src.width, h = src.height, f = 1;
            while (w / f > maxWidth && h / f > 1) f *= 2;
            if (f == 1) return src;
            int nw = w / f, nh = h / f;
            var px = src.GetPixels32();
            var outPx = new Color32[nw * nh];
            int n = f * f;
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0, white = 0;
                    for (int dy = 0; dy < f; dy++)
                    {
                        int row = (y * f + dy) * w + x * f;
                        for (int dx = 0; dx < f; dx++)
                        {
                            var c = px[row + dx];
                            r += c.r; g += c.g; b += c.b; a += c.a;
                            if (c.r > 127 || c.g > 127 || c.b > 127) white++;
                        }
                    }
                    outPx[y * nw + x] = mask
                        ? (white * 2 >= n ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255))
                        : new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            // Same format and mipmaps as the loaded original: the game copies the emission maps into a per-building
            // texture (NewBuilding.SetupEmissionTexture), which only works when they match. A mismatch left that texture
            // blank-white, and the whole building glowed in the emission colour.
            bool mips = src.mipmapCount > 1;
            var t = new Texture2D(nw, nh, src.format, mips, mask);
            t.SetPixels32(outPx);
            t.Apply(mips, false);
            Object.Destroy(src);
            return t;
        }
    }
}

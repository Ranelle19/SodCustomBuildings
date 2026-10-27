using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CustomBuildings.Shared;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomBuildings
{
    /// <summary>
    /// Builds the piers of our wall pieces (EdgePieceDef.Pier) once a city has loaded, at every copy the generator placed: stairs down the outside of the sea wall and a deck
    /// just above the water (shape from Shared.PierLayout, the same code the editor draws with). Where the stairs start,
    /// the game's railing on the wall is cut open (its model and collider are copied for that one wall piece only); if
    /// that isn't possible, steps go up and over it. Everything is logged with [pier].
    /// </summary>
    [HarmonyPatch(typeof(SessionData), nameof(SessionData.Update))]
    internal static class PierBuilder
    {
        private static int _frames;
        private static GameObject _root;
        private static string _builtFor;
        private static readonly List<(string name, Vector3 at)> Rescue = new();
        private static float _seaY = -12f, _lastRescue = -100f;

        private static void Postfix()
        {
            try
            {
                ++_frames;
                if (Rescue.Count > 0 && _frames % 10 == 0) BackOnThePier();
                if (_frames % 300 != 150) return;   // every ~5 s, offset from the edge report
                var city = CityData.Instance;
                if (city == null) return;
                if (!CityReady()) return;
                // cheap check first: same city object as last time → nothing to do (no search through the scene; that,
                // every 5 s, was part of the stutter)
                string key = $"{city.Pointer}|{city.cityName}|{city.citySize}";
                if (_root != null && _builtFor == key) { if (PierRailing.Openings.Count > 0) PierRailing.Refresh(); return; }
                var pieces = EdgeLog.EdgePieces();
                if (pieces.Count == 0) return;
                if (_root != null) UnityEngine.Object.Destroy(_root);
                PierRailing.Clear();
                Rescue.Clear();
                EdgeModel.Clear();
                _root = null;
                _builtFor = key;

                _root = new GameObject("CustomBuildings_Piers");
                // our own wall pieces the city generator placed, and what belongs to them (their model, a pier…)
                EdgePieces.OnPlaced(pieces, (pier, piece) => Build(pier, pieces, piece));
            }
            catch (Exception e) { Plugin.L.LogWarning($"[pier] {e.GetType().Name}: {e.Message}"); }
        }


        /// <summary>
        /// Only once the game has finished making or loading the city. Building the piers (and reading models back from
        /// the graphics card) while it was still loading could hang the game. (No "go ahead anyway" after a while: the game
        /// switches its loading flag off between some steps, so that would start in the middle of a load, or at the menu.)
        /// </summary>
        internal static bool CityReady()
        {
            var cc = CityConstructor.Instance;
            if (cc == null) return true;
            try { return cc.isLoaded && !cc.loadingOperationActive; } catch { return true; }
        }

        /// <summary>Somewhere to put a player who fell into the sea near this piece (its own model's walkway).</summary>
        internal static void AddRescue(string name, Vector3 at)
        {
            Rescue.Add((name, at));
            if (CityControls.Instance?.seaRenderer != null) _seaY = CityControls.Instance.seaRenderer.bounds.max.y;
        }

        /// <summary>
        /// Below the sea the screen goes black and there's no way out, so someone who jumps or falls off a pier is put
        /// back on the nearest pier's deck.
        /// </summary>
        private static void BackOnThePier()
        {
            var pl = Player.Instance;
            if (pl == null) return;
            var p = pl.transform.position;
            if (p.y > _seaY - 0.5f || Time.time - _lastRescue < 2f) return;
            var best = Rescue.OrderBy(r => (new Vector2(r.at.x - p.x, r.at.z - p.z)).sqrMagnitude).First();
            if (new Vector2(best.at.x - p.x, best.at.z - p.z).magnitude > 250f) return;
            _lastRescue = Time.time;
            pl.SetPosition(best.at, pl.transform.rotation);
            Plugin.L.LogInfo($"[pier] fell into the sea at {V(p)}; put back on {best.name}");
        }

        private static string V(Vector3 v) => $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##})";

        // ------------------------------------------------------------------ finding the spot

        /// <param name="forced">build at this edge piece (one of ours the generator placed) instead of the side/position in def</param>
        private static GameObject Build(PierDef def, List<NewBuilding> pieces, NewBuilding forced = null)
        {
            var withTile = pieces.Where(p => p.cityTile != null).ToList();
            int minX = withTile.Min(p => p.cityTile.cityCoord.x), maxX = withTile.Max(p => p.cityTile.cityCoord.x);
            int minY = withTile.Min(p => p.cityTile.cityCoord.y), maxY = withTile.Max(p => p.cityTile.cityCoord.y);
            CitySide? SideOf(NewBuilding b)
            {
                var c = b.cityTile.cityCoord;
                if (c.y == maxY) return CitySide.North;
                if (c.y == minY) return CitySide.South;
                if (c.x == maxX) return CitySide.East;
                if (c.x == minX) return CitySide.West;
                return null;
            }
            if (forced != null)
            {
                var fs = forced.cityTile != null ? SideOf(forced) : null;
                if (fs == null) { Plugin.L.LogWarning($"[pier] {def.Name}: couldn't tell which side of the city {forced.preset.presetName} is on"); return null; }
                if (fs.Value != def.Side) { def = def.Clone(); def.Side = fs.Value; }
            }
            var side = withTile.Where(p => !p.preset.boundaryCorner && SideOf(p) == def.Side).ToList();
            if (side.Count == 0) { Plugin.L.LogWarning($"[pier] {def.Name}: no wall pieces on the {def.Side} side"); return null; }

            Vector3 outDir = def.Side switch
            {
                CitySide.North => Vector3.forward, CitySide.South => Vector3.back, CitySide.East => Vector3.right, _ => Vector3.left,
            };
            var right = new Vector3(outDir.z, 0, -outDir.x);   // to the right, seen from the city looking out to sea
            float tile = CityControls.Instance != null ? CityControls.Instance.cityTileSize.x : 37.8f;
            float Along(Vector3 p) => Vector3.Dot(p, right);

            float lo = side.Min(p => Along(p.transform.position)) - tile / 2, hi = side.Max(p => Along(p.transform.position)) + tile / 2;
            float target = lo + (hi - lo) * Mathf.Clamp(def.Position, 0, 100) / 100f;

            // a normal stretch of wall (BoundaryCoastal01), or one of the square platforms that stick out (BoundaryCoastal02)
            bool platform = def.AttachTo == PierAttach.Platform;
            var platforms = side.Where(p => p.preset.presetName == "BoundaryCoastal02").ToList();
            if (platform && platforms.Count == 0)
            {
                Plugin.L.LogInfo($"[pier] {def.Name}: this city has no platform on the {def.Side} side; the pier goes on the wall instead");
                platform = false;
            }
            var pool = platform ? platforms : side.Where(p => p.preset.presetName == "BoundaryCoastal01").ToList();
            if (pool.Count == 0) pool = side;
            var piece = forced ?? pool.OrderBy(p => Math.Abs(Along(p.transform.position) - target)).First();
            float centre = Along(piece.transform.position);
            if (forced != null)
            {
                platform = EdgePieces.RoleOf(forced) == EdgeRole.Platform;
                target = centre + (Mathf.Clamp(def.Position, 0, 100) / 100f - 0.5f) * tile;   // Position = where along this piece
            }
            if (!platform && piece.preset.presetName == "BoundaryCoastal02")
            {
                Plugin.L.LogInfo($"[pier] {def.Name}: this side has only platforms here, so the pier goes on the nearest platform");
                platform = true;
            }

            var bounds = PieceBounds(piece);
            float inner = Vector3.Dot(bounds.center, outDir) - Vector3.Dot(bounds.extents, Abs(outDir));
            float faceGuess = Vector3.Dot(bounds.center, outDir) + Vector3.Dot(bounds.extents, Abs(outDir));
            float seaY = CityControls.Instance?.seaRenderer != null ? CityControls.Instance.seaRenderer.bounds.max.y : -12f;
            // below the basement water level the game makes you wade (slow); keep the deck above it
            float wadeY = CityControls.Instance != null ? CityControls.Instance.basementWaterLevel : -9.6f;
            float minDeck = wadeY + 0.5f;

            Vector3 OnLine(float along, float outward, float y) => right * along + outDir * outward + Vector3.up * y;
            bool stepsWanted = def.RailingGap == WallRailingGap.StepsOver;

            float aRef = platform ? centre : Mathf.Clamp(target, centre - tile / 2 + 1, centre + tile / 2 - 1);
            float topY = MeasureTop(piece, OnLine(aRef, inner + 1f, 40f), 0.2f);
            // the outer edge of the walkable top (the wall below can be set back, so measure at street level)
            // on a platform, look along several lines and step over things standing on it (the transformer tower)
            float edge = platform
                ? new[] { -4f, -2f, 0f, 2f, 4f }.Select(da => ScanEdge(piece, aRef + da, inner + 0.3f, inner + 80f, topY, right, outDir, 6f))
                                                   .Where(e => !float.IsNaN(e)).DefaultIfEmpty(float.NaN).Max()
                : ScanEdge(piece, aRef, inner + 0.3f, inner + 80f, topY, right, outDir);
            if (float.IsNaN(edge)) { Plugin.L.LogInfo($"[pier] {def.Name}: couldn't find the edge of the top, using the model's outside"); edge = faceGuess; }

            float span0 = centre - tile / 2, span1 = centre + tile / 2, want = target;
            if (platform)
            {
                (span0, span1) = ScanSpan(piece, aRef, edge - 1f, topY, right, outDir);
                want = (span0 + span1) / 2;
                Plugin.L.LogInfo($"[pier] {def.Name}: platform edge {edge:0.##} out, {span1 - span0:0.#} m wide");
            }

            var probe = PierLayout.Build(def, topY, seaY, topY + 1.1f, stepsWanted, minDeck);
            float margin = platform ? 0.3f : 1.0f;
            float minC = span0 + margin - probe.MinA, maxC = span1 - margin - probe.MaxA;
            float halfW = def.StairsWidth / 2;
            float gapAt = minC <= maxC ? Mathf.Clamp(want, minC, maxC) : Mathf.Clamp(want, span0 + halfW + 0.3f, span1 - halfW - 0.3f);
            if (!platform && Math.Abs(gapAt - target) > 0.5f)
                Plugin.L.LogInfo($"[pier] {def.Name}: moved {gapAt - target:+0.#;-0.#} m along the wall so the stairs fit on a normal stretch of wall");
            if (minC > maxC) Plugin.L.LogInfo($"[pier] {def.Name}: the stairs are longer than the {(platform ? "platform" : "wall piece")}, so they reach past its end");

            if (!platform)
            {
                if (def.PreferBay)
                {
                    var bay = FindBay(piece, span0, span1, target, inner, topY, right, outDir, def.StairsWidth + 0.4f, out string profile);
                    Plugin.L.LogInfo($"[pier] {def.Name}: edge of the walkway along the piece (m along: m out from the inner side): {profile}");
                    if (bay.HasValue)
                    {
                        gapAt = bay.Value.centre;
                        Plugin.L.LogInfo($"[pier] {def.Name}: starting from the bay at {bay.Value.a0:0.#}..{bay.Value.a1:0.#} ({bay.Value.a1 - bay.Value.a0:0.#} m wide, {bay.Value.outBy:0.##} m further out)");
                    }
                    else Plugin.L.LogInfo($"[pier] {def.Name}: no bay found on this wall piece; starting at Position");
                }
                topY = MeasureTop(piece, OnLine(gapAt, inner + 1f, 40f), topY);
                var e2 = ScanEdge(piece, gapAt, inner + 0.3f, inner + 20f, topY, right, outDir);
                if (!float.IsNaN(e2)) edge = e2;
            }
            float edgeAt = edge;

            // keep the stairs clear of pillars, lamps and supports that stick out further than the edge where they start
            var foot = PierLayout.Build(def, topY, seaY, topY + 1.1f, stepsWanted, minDeck);
            float clear = ObstacleOut(piece, gapAt + foot.MinA, gapAt + foot.MaxA, topY, edgeAt + 12f, right, outDir);
            float face = float.IsNaN(clear) ? edgeAt : Math.Max(edgeAt, clear + 0.05f);
            if (face - edgeAt > 4f) face = edgeAt + 4f;
            float landingBack = face - edgeAt;

            float railTop = MeasureRailTop(piece, gapAt, edgeAt, topY, right, outDir);
            Plugin.L.LogInfo($"[pier] {def.Name}: {def.Side} side, {(platform ? "platform" : "wall")} piece {piece.preset.presetName} tile {piece.cityTile.cityCoord}; " +
                             $"street {topY:0.##}, edge at {edgeAt:0.##}, stairs at {face:0.##} (clear of what sticks out: {(float.IsNaN(clear) ? "nothing" : clear.ToString("0.##"))}), " +
                             $"railing top {railTop:0.##}, sea {seaY:0.##}, wading below {wadeY:0.##}");

            var origin = OnLine(gapAt, face, 0);   // the pier's (A=0, U=0) at height 0
            var edgeOrigin = OnLine(gapAt, edgeAt, 0);
            Vector3 W(float a, float u, float y) => origin + right * a + outDir * u + Vector3.up * y;

            // open the railing, or build steps over it
            bool ownWall = EdgeModel.HasOwnModel(piece);   // its own model has the opening built in: nothing to cut
            bool stepsOver = def.RailingGap == WallRailingGap.StepsOver && !ownWall;
            var model = PierLayout.Build(def, topY, seaY, railTop, stepsOver, minDeck, landingBack);
            if (ownWall) Plugin.L.LogInfo($"[pier] {def.Name}: the wall piece has its own model, so its railing isn't cut");
            if (!stepsOver && !ownWall)
            {
                var opening = new PierRailing.Opening
                {
                    Name = def.Name, Origin = edgeOrigin, Right = right, Out = outDir, A0 = model.GapA0, A1 = model.GapA1,
                    TopY = topY, YHi = Math.Max(railTop, topY + 1.2f) + 0.4f, Exclude = _root.transform,
                    UHi = landingBack + 0.5f,   // the railing can stand further out than the walkway's edge (up to where the stairs start)
                    Within = piece.transform,
                };
                string why = null;
                try { if (PierRailing.Apply(opening, true) == 0) why = "no railing found there"; }
                catch (Exception e) { why = e.Message; }
                if (why == null) PierRailing.Openings.Add(opening);
                if (why != null)
                {
                    Plugin.L.LogInfo($"[pier] {def.Name}: couldn't cut the railing ({why}); building steps over it instead");
                    stepsOver = true;
                    model = PierLayout.Build(def, topY, seaY, railTop, true, minDeck, landingBack);
                }
            }
            if (!ownWall) OpenBlockingBoxes(piece, origin, right, outDir, model, stepsOver);

            // build it
            var go = new GameObject("Pier_" + def.Name);
            go.transform.SetParent(_root.transform, false);
            go.layer = piece.gameObject.layer;
            var srcRenderer = piece.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r != null && r.sharedMaterial != null);
            var parts = new Dictionary<PierPart, string>
            {
                [PierPart.Deck] = def.DeckColour, [PierPart.Stairs] = def.StairsColour, [PierPart.Post] = def.PostColour,
                [PierPart.Rail] = def.RailColour, [PierPart.Bollard] = def.RailColour, [PierPart.Lamp] = def.RailColour,
            };
            int boxes = 0, colliders = 0;
            bool gameRail = false;
            foreach (var kv in parts)
            {
                var mb = new MeshBuilder();
                foreach (var b in model.Boxes.Where(q => q.Part == kv.Key && q.Visible))
                {
                    mb.AxisBox(W(b.A0, b.U0, b.Y0), W(b.A1, b.U1, b.Y1));
                    boxes++;
                }
                foreach (var r in model.Ramps.Where(q => q.Part == kv.Key && q.Visible))
                    mb.Ramp(W(r.A0, r.U0, r.Y0), W(r.A1, r.U1, r.Y1), r.Width, r.Thick);
                if (mb.Empty) continue;
                var part = new GameObject(kv.Key.ToString());
                part.transform.SetParent(go.transform, false);
                part.layer = go.layer;
                part.AddComponent<MeshFilter>().sharedMesh = mb.ToMesh($"Pier_{def.Name}_{kv.Key}");
                var mr = part.AddComponent<MeshRenderer>();
                mr.sharedMaterial = MakeMaterial(srcRenderer?.sharedMaterial, kv.Value, $"Pier_{kv.Key}");
                if (srcRenderer != null) ExteriorLoader.CopyRendererSettings(srcRenderer, mr);
            }

            // the game's own railing along the pier's railings, copied from the wall beside the pier
            if (def.GameRailing && !ownWall)
            {
                try
                {
                    float sgn = def.StairsGoRight ? 1 : -1;
                    // sample the wall's railing on the side away from the stairs (the deck is 9 m below there, the railing is intact)
                    float sa0 = sgn > 0 ? model.GapA0 - 9f : model.GapA1 + 0.3f, sa1 = sgn > 0 ? model.GapA0 - 0.3f : model.GapA1 + 9f;
                    var sample = new PierRailing.Opening
                    {
                        Name = def.Name, Origin = edgeOrigin, Right = right, Out = outDir, A0 = sa0, A1 = sa1,
                        TopY = topY, YHi = Math.Max(railTop, topY + 1.2f) + 0.4f, Exclude = _root.transform,
                        UHi = landingBack + 0.5f,
                        Within = piece.transform,
                    };
                    var seg = PierRailing.Extract(sample, sa0, sa1, out string whyNot);
                    if (seg != null)
                    {
                        var lines = model.RailLines.Select(l => (W(l.A0, l.U0, l.Y0), W(l.A1, l.U1, l.Y1))).ToList();
                        int n = PierRailing.Place(seg, go.transform, lines);
                        gameRail = true;
                        Plugin.L.LogInfo($"[pier] {def.Name}: railing copied from the wall ({seg.Length:0.##} m per post, {seg.Height:0.##} m high, {seg.Parts.Count} parts), {n} pieces placed");
                    }
                    else Plugin.L.LogInfo($"[pier] {def.Name}: couldn't copy the game's railing ({whyNot}); using simple bars");
                }
                catch (Exception e) { Plugin.L.LogInfo($"[pier] {def.Name}: couldn't copy the game's railing ({e.Message}); using simple bars"); }
            }
            if (gameRail)
            {
                // the simple bars were built above; hide them
                var bars = go.transform.Find(PierPart.Rail.ToString());
                if (bars != null) { bars.gameObject.SetActive(false); UnityEngine.Object.Destroy(bars.gameObject); }   // inactive at once (Destroy waits)
            }

            var cols = new GameObject("Colliders");
            cols.transform.SetParent(go.transform, false);
            cols.layer = go.layer;
            foreach (var b in model.Boxes.Where(q => q.Solid))
            {
                var p0 = W(b.A0, b.U0, b.Y0); var p1 = W(b.A1, b.U1, b.Y1);
                AddBox(cols.transform, (p0 + p1) / 2, Abs(p1 - p0), Quaternion.identity);
                colliders++;
            }
            foreach (var r in model.Ramps.Where(q => q.Solid))
            {
                var p0 = W(r.A0, r.U0, r.Y0); var p1 = W(r.A1, r.U1, r.Y1);
                var fwd = (p1 - p0).normalized;
                var sideways = Vector3.Cross(Vector3.up, fwd).normalized;
                var up = Vector3.Cross(fwd, sideways).normalized;
                var mid = (p0 + p1) / 2 - up * (r.Thick / 2);
                AddBox(cols.transform, mid, new Vector3(r.Width, r.Thick, (p1 - p0).magnitude), Quaternion.LookRotation(fwd, up));
                colliders++;
            }
            Rescue.Add((def.Name, W(model.RescueA, model.RescueU, model.DeckY + 1.2f)));
            _seaY = seaY;
            Plugin.L.LogInfo($"[pier] {def.Name}: built at {V(origin)}: {model.Steps} steps from {model.StartY:0.##} down to the deck at {model.DeckY:0.##}, " +
                             $"reaches {model.MaxU:0.#} m out, {boxes} parts, {colliders} colliders, {(stepsOver ? "steps over the railing" : "railing cut open")}");
            return go;
        }

        private static Vector3 Abs(Vector3 v) => new(Math.Abs(v.x), Math.Abs(v.y), Math.Abs(v.z));

        private static Bounds PieceBounds(NewBuilding piece)
        {
            var rs = piece.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(piece.transform.position, Vector3.one);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        private static bool OnPiece(Collider c, NewBuilding piece) => c != null && !c.isTrigger && c.transform.IsChildOf(piece.transform);

        private static float MeasureTop(NewBuilding piece, Vector3 from, float fallback)
        {
            float best = float.MinValue;
            foreach (var h in Physics.RaycastAll(from, Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore))
                if (OnPiece(h.collider, piece) && h.point.y < 4f && h.point.y > best) best = h.point.y;
            return best > float.MinValue ? best : fallback;
        }

        private static bool Walkable(NewBuilding piece, Vector3 at, float topY)
        {
            foreach (var h in Physics.RaycastAll(new Vector3(at.x, topY + 3f, at.z), Vector3.down, 4.5f, ~0, QueryTriggerInteraction.Ignore))
                if (OnPiece(h.collider, piece) && Math.Abs(h.point.y - topY) < 0.4f) return true;
            return false;
        }

        /// <summary>Walks outward from `from` and returns the last spot where the piece's top is still under foot (NaN if never).</summary>
        private static float ScanEdge(NewBuilding piece, float along, float from, float to, float topY, Vector3 right, Vector3 outDir, float maxGap = 0.6f)
        {
            float last = float.NaN;
            for (float u = from; u <= to; u += 0.2f)
            {
                if (Walkable(piece, right * along + outDir * u, topY)) last = u;
                else if (!float.IsNaN(last) && u - last > maxGap) break;
            }
            return last;
        }

        /// <summary>
        /// The walkway's edge along the whole piece; a bay is a run that sticks out further than most of the edge (between
        /// two pillars). Returns the bay nearest `target` that is at least `minWidth` wide.
        /// </summary>
        private static (float a0, float a1, float centre, float outBy)? FindBay(NewBuilding piece, float span0, float span1, float target, float inner,
                                                                                float topY, Vector3 right, Vector3 outDir, float minWidth, out string profile)
        {
            var samples = new List<(float a, float e)>();
            for (float a = span0 + 0.3f; a <= span1 - 0.3f; a += 0.25f)
                samples.Add((a, ScanEdge(piece, a, inner + 0.3f, inner + 20f, topY, right, outDir)));
            var valid = samples.Where(x => !float.IsNaN(x.e)).Select(x => x.e).OrderBy(x => x).ToList();

            // compact profile for the log: runs of about the same edge
            var parts = new List<string>();
            int i0 = 0;
            for (int i = 1; i <= samples.Count; i++)
            {
                bool end = i == samples.Count;
                if (!end)
                {
                    var p = samples[i - 1].e; var c = samples[i].e;
                    bool same = (float.IsNaN(p) && float.IsNaN(c)) || (!float.IsNaN(p) && !float.IsNaN(c) && Math.Abs(p - c) < 0.2f);
                    if (same) continue;
                }
                var e = samples[i0].e;
                parts.Add($"{samples[i0].a:0.#}..{samples[i - 1].a:0.#}: {(float.IsNaN(e) ? "-" : (e - inner).ToString("0.##"))}");
                i0 = i;
            }
            profile = string.Join(" | ", parts.Take(40));
            if (valid.Count < 4) return null;

            float baseline = valid[valid.Count / 2];
            var bays = new List<(float a0, float a1, float centre, float outBy)>();
            int start = -1, misses = 0; float sum = 0; int n = 0;
            for (int i = 0; i <= samples.Count; i++)
            {
                bool inBay = i < samples.Count && !float.IsNaN(samples[i].e) && samples[i].e > baseline + 0.4f;
                bool gap = i < samples.Count && float.IsNaN(samples[i].e) && start >= 0 && misses < 2;
                if (inBay) { if (start < 0) { start = i; sum = 0; n = 0; } misses = 0; sum += samples[i].e; n++; continue; }
                if (gap) { misses++; continue; }
                if (start >= 0)
                {
                    int last = i - 1 - misses;
                    float a0 = samples[start].a, a1 = samples[last].a;
                    if (a1 - a0 >= minWidth) bays.Add((a0, a1, (a0 + a1) / 2, sum / n - baseline));
                }
                start = -1; misses = 0;
            }
            if (bays.Count == 0) return null;
            return bays.OrderBy(b => Math.Abs(b.centre - target)).First();
        }

        /// <summary>The furthest anything of the piece sticks out (from the sea side) across A in [a0, a1], from just above the street down.</summary>
        private static float ObstacleOut(NewBuilding piece, float a0, float a1, float topY, float fromOut, Vector3 right, Vector3 outDir)
        {
            float best = float.NaN;
            foreach (var dy in new[] { 1.0f, 0.4f, -0.6f, -2f, -4f, -7f })
                for (float a = Math.Min(a0, a1); a <= Math.Max(a0, a1); a += 0.5f)
                {
                    var from = right * a + outDir * fromOut + Vector3.up * (topY + dy);
                    foreach (var h in Physics.RaycastAll(from, -outDir, 25f, ~0, QueryTriggerInteraction.Ignore))
                        if (OnPiece(h.collider, piece))
                        {
                            float u = Vector3.Dot(h.point, outDir);
                            if (float.IsNaN(best) || u > best) best = u;
                        }
                }
            return best;
        }

        /// <summary>How far the top reaches left and right of `along`, a metre inside the edge.</summary>
        private static (float, float) ScanSpan(NewBuilding piece, float along, float u, float topY, Vector3 right, Vector3 outDir)
        {
            float lo = along, hi = along;
            for (float a = along; a > along - 40f; a -= 0.25f) { if (!Walkable(piece, right * a + outDir * u, topY)) break; lo = a; }
            for (float a = along; a < along + 40f; a += 0.25f) { if (!Walkable(piece, right * a + outDir * u, topY)) break; hi = a; }
            return (lo, hi);
        }

        private static float MeasureFace(NewBuilding piece, Vector3 from, Vector3 dir, Vector3 outDir, float fallback)
        {
            float best = float.MinValue;
            foreach (var h in Physics.RaycastAll(from, dir, 60f, ~0, QueryTriggerInteraction.Ignore))
                if (OnPiece(h.collider, piece)) best = Math.Max(best, Vector3.Dot(h.point, outDir));
            return best > float.MinValue && Math.Abs(best - fallback) < 6f ? best : fallback;
        }

        private static float MeasureRailTop(NewBuilding piece, float along, float face, float topY, Vector3 right, Vector3 outDir)
        {
            float best = float.MinValue;
            foreach (var da in new[] { -0.6f, 0f, 0.6f })
                for (float back = 0.05f; back <= 1.3f; back += 0.15f)
                {
                    var from = right * (along + da) + outDir * (face - back) + Vector3.up * (topY + 3f);
                    foreach (var h in Physics.RaycastAll(from, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore))
                        if (OnPiece(h.collider, piece) && h.point.y > topY + 0.3f && h.point.y < topY + 2.5f) best = Math.Max(best, h.point.y);
                }
            return best > float.MinValue ? best : topY + 1.1f;
        }

        // ------------------------------------------------------------------ opening the railing

        /// <summary>
        /// Invisible walls (BoxColliders) on the wall piece that block the way to the stairs: replaced by boxes left and
        /// right of the opening, plus one up to street level so the ground stays walkable.
        /// </summary>
        private static void OpenBlockingBoxes(NewBuilding piece, Vector3 origin, Vector3 right, Vector3 outDir, PierModel model, bool stepsOver)
        {
            float a0 = model.GapA0, a1 = model.GapA1;
            float yWalk = stepsOver ? model.StartY : model.TopY;
            foreach (var bc in piece.GetComponentsInChildren<BoxCollider>(true))
            {
                if (bc == null || bc.isTrigger || !bc.enabled) continue;
                var b = bc.bounds;
                float bA0 = Vector3.Dot(b.min - origin, right), bA1 = Vector3.Dot(b.max - origin, right);
                if (bA0 > bA1) (bA0, bA1) = (bA1, bA0);
                float bU0 = Vector3.Dot(b.min - origin, outDir), bU1 = Vector3.Dot(b.max - origin, outDir);
                if (bU0 > bU1) (bU0, bU1) = (bU1, bU0);
                bool overlaps = bA1 > a0 && bA0 < a1 && bU1 > -2f && bU0 < 1f && b.max.y > yWalk + 0.2f && b.min.y < yWalk + 2.5f;
                Plugin.L.LogInfo($"[pier]   box collider '{bc.gameObject.name}' {V(b.min)} – {V(b.max)}{(overlaps ? " is in the way" : "")}");
                if (!overlaps) continue;
                var e = bc.transform.eulerAngles;
                bool aligned = Math.Abs(Mathf.DeltaAngle(e.x, 0)) < 1 && Math.Abs(Mathf.DeltaAngle(e.z, 0)) < 1 && Math.Abs(Mathf.DeltaAngle(e.y, Mathf.Round(e.y / 90) * 90)) < 1;
                if (!aligned) { Plugin.L.LogInfo("[pier]   it's turned at an angle, so it's left as it is"); continue; }

                var parent = new GameObject("WallColliderParts");
                parent.transform.SetParent(bc.transform.parent, true);
                parent.layer = bc.gameObject.layer;
                Vector3 Pt(float a, float u, float y) => origin + right * a + outDir * u + Vector3.up * y;
                void Part(float pa0, float pa1, float y0, float y1)
                {
                    if (pa1 - pa0 < 0.01f || y1 - y0 < 0.01f) return;
                    var p0 = Pt(pa0, bU0, y0); var p1 = Pt(pa1, bU1, y1);
                    AddBox(parent.transform, (p0 + p1) / 2, Abs(p1 - p0), Quaternion.identity);
                }
                Part(bA0, Math.Max(bA0, a0), b.min.y, b.max.y);
                Part(Math.Min(bA1, a1), bA1, b.min.y, b.max.y);
                Part(Math.Max(bA0, a0), Math.Min(bA1, a1), b.min.y, Math.Min(b.max.y, model.TopY));
                bc.enabled = false;
                Plugin.L.LogInfo("[pier]   replaced it with parts that leave the opening free");
            }
        }

        private static void AddBox(Transform parent, Vector3 centre, Vector3 size, Quaternion rot)
        {
            var g = new GameObject("c");
            g.transform.SetParent(parent, false);
            g.layer = parent.gameObject.layer;
            g.transform.position = centre;
            g.transform.rotation = rot;
            g.AddComponent<BoxCollider>().size = size;
        }

        // ------------------------------------------------------------------ looks

        private static Material MakeMaterial(Material src, string hex, string name)
        {
            var (r, g, b) = PierLayout.Colour(hex);
            Material m;
            if (src != null) m = new Material(src);
            else
            {
                var sh = Shader.Find("HDRP/Lit");
                if (sh == null) return null;
                m = new Material(sh);
            }
            m.name = name;
            foreach (var t in new[] { "_BaseColorMap", "_MaskMap", "_NormalMap", "_EmissiveColorMap", "_DetailMap", "_MainTex" })
                try { if (m.HasProperty(t)) m.SetTexture(t, null); } catch { }
            foreach (var k in new[] { "_NORMALMAP", "_MASKMAP", "_DETAIL_MAP", "_EMISSIVE_COLOR_MAP" }) m.DisableKeyword(k);
            var c = new Color(r, g, b, 1);
            foreach (var p in new[] { "_BaseColor", "_Color" })
                try { if (m.HasProperty(p)) m.SetColor(p, c); } catch { }
            ExteriorLoader.MakeMatt(m);
            try { if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", Color.black); } catch { }
            return m;
        }

        private static Il2CppStructArray<Vector3> ToIl2(Vector3[] a) { var r = new Il2CppStructArray<Vector3>(a.Length); for (int i = 0; i < a.Length; i++) r[i] = a[i]; return r; }
        private static Il2CppStructArray<Vector2> ToIl2(Vector2[] a) { var r = new Il2CppStructArray<Vector2>(a.Length); for (int i = 0; i < a.Length; i++) r[i] = a[i]; return r; }
        private static Il2CppStructArray<int> ToIl2(int[] a) { var r = new Il2CppStructArray<int>(a.Length); for (int i = 0; i < a.Length; i++) r[i] = a[i]; return r; }

        /// <summary>Boxes with flat normals and world-space UVs (1 m per unit), merged into one mesh.</summary>
        private sealed class MeshBuilder
        {
            private readonly List<Vector3> _v = new(), _n = new();
            private readonly List<Vector2> _uv = new();
            private readonly List<int> _t = new();
            public bool Empty => _t.Count == 0;

            public void AxisBox(Vector3 p0, Vector3 p1)
            {
                var mn = Vector3.Min(p0, p1); var mx = Vector3.Max(p0, p1);
                var c = (mn + mx) / 2; var h = (mx - mn) / 2;
                Box(c, Vector3.right * h.x, Vector3.up * h.y, Vector3.forward * h.z);
            }

            /// <summary>Sloped bar whose top surface runs from p0 to p1.</summary>
            public void Ramp(Vector3 p0, Vector3 p1, float width, float thick)
            {
                var fwd = (p1 - p0);
                float len = fwd.magnitude;
                if (len < 0.001f) return;
                fwd /= len;
                var side = Vector3.Cross(Vector3.up, fwd).normalized;
                var up = Vector3.Cross(fwd, side).normalized;
                var c = (p0 + p1) / 2 - up * (thick / 2);
                Box(c, side * (width / 2), up * (thick / 2), fwd * (len / 2));
            }

            /// <summary>Box from its centre and three half-axes.</summary>
            private void Box(Vector3 c, Vector3 x, Vector3 y, Vector3 z)
            {
                void Face(Vector3 n, Vector3 a, Vector3 b)   // face at c+n, spanned by a and b
                {
                    var nn = n.normalized;
                    var cs = new[] { c + n - a - b, c + n + a - b, c + n + a + b, c + n - a + b };
                    // wind so the face points along n
                    if (Vector3.Dot(Vector3.Cross(cs[1] - cs[0], cs[2] - cs[0]), nn) < 0) Array.Reverse(cs);   // Unity: Cross(b-a, c-a) is the front
                    int i0 = _v.Count;
                    foreach (var p in cs)
                    {
                        _v.Add(p); _n.Add(nn);
                        // planar UVs on the face's two widest world axes
                        var ap = new Vector3(Math.Abs(nn.x), Math.Abs(nn.y), Math.Abs(nn.z));
                        _uv.Add(ap.y > 0.7f ? new Vector2(p.x, p.z) : ap.x > ap.z ? new Vector2(p.z, p.y) : new Vector2(p.x, p.y));
                    }
                    _t.AddRange(new[] { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 3 });
                }
                Face(x, y, z); Face(-x, y, z);
                Face(y, x, z); Face(-y, x, z);
                Face(z, x, y); Face(-z, x, y);
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                if (_v.Count > 65000) m.indexFormat = IndexFormat.UInt32;
                m.vertices = ToIl2(_v.ToArray());
                m.normals = ToIl2(_n.ToArray());
                var uv = ToIl2(_uv.ToArray());
                m.uv = uv;
                try { m.uv2 = uv; m.uv3 = uv; m.uv4 = uv; } catch { }
                m.triangles = ToIl2(_t.ToArray());
                m.RecalculateBounds();
                try { m.RecalculateTangents(); } catch { }
                return m;
            }
        }
    }
}

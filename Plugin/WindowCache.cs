using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;
using UnityEngine;

namespace CustomBuildings
{
    using CustomBuildings.Shared;

    /// <summary>
    /// Keeps the window data the game works out from a custom exterior's window map (BuildingPreset.GenerateWindowData),
    /// so it only has to be worked out once per change. Lives in BepInEx\cache, not in the Buildings folder, so it doesn't
    /// change the multiplayer content hash.
    /// </summary>
    internal static class WindowCache
    {
        private sealed class Block
        {
            public float[] O { get; set; }   // originPixel
            public float[] S { get; set; }   // rectSize
            public float[] C { get; set; }   // centrePixel
            public float[] L { get; set; }   // localMeshPositionLeft
            public float[] R { get; set; }   // localMeshPositionRight
            public int F { get; set; }       // floor
            public float[] Side { get; set; }
            public int H { get; set; }       // horizonal
        }

        private sealed class Floor
        {
            public List<Block> Front { get; set; } = new();
            public List<Block> Back { get; set; } = new();
            public List<Block> Left { get; set; } = new();
            public List<Block> Right { get; set; } = new();
        }

        public static string FileFor(string id, string exteriorDir, ExteriorInfo info)
        {
            try
            {
                using var sha = SHA256.Create();
                void Add(string file)
                {
                    var p = Path.Combine(exteriorDir, file ?? "");
                    if (!File.Exists(p)) return;
                    var b = File.ReadAllBytes(p);
                    sha.TransformBlock(b, 0, b.Length, null, 0);
                }
                Add(info.Obj);
                Add(info.WindowMap);
                var extra = System.Text.Encoding.UTF8.GetBytes($"|{info.Storeys}|{Plugin.ExteriorTextureMaxSize.Value}|1");
                sha.TransformFinalBlock(extra, 0, extra.Length);
                var hash = BitConverter.ToString(sha.Hash, 0, 8).Replace("-", "");
                var dir = Path.Combine(Paths.CachePath, Plugin.Name);
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, $"{id}_{hash}.windows.json");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"{id}: window cache unavailable: {e.Message}");
                return null;
            }
        }

        private static float[] V(Vector2 v) => new[] { v.x, v.y };
        private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
        private static Vector2 V2(float[] a) => a != null && a.Length >= 2 ? new Vector2(a[0], a[1]) : Vector2.zero;
        private static Vector3 V3(float[] a) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;

        private static List<Block> Out(Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVBlock> list)
        {
            var r = new List<Block>();
            if (list == null) return r;
            foreach (var b in list)
            {
                if (b == null) continue;
                r.Add(new Block
                {
                    O = V(b.originPixel), S = V(b.rectSize), C = V(b.centrePixel),
                    L = V(b.localMeshPositionLeft), R = V(b.localMeshPositionRight),
                    F = b.floor, Side = V(b.side), H = b.horizonal,
                });
            }
            return r;
        }

        private static Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVBlock> In(List<Block> list)
        {
            var r = new Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVBlock>();
            foreach (var b in list ?? new List<Block>())
            {
                var w = new BuildingPreset.WindowUVBlock
                {
                    originPixel = V2(b.O), rectSize = V2(b.S), centrePixel = V2(b.C),
                    localMeshPositionLeft = V3(b.L), localMeshPositionRight = V3(b.R),
                    floor = b.F, side = V2(b.Side), horizonal = b.H,
                };
                r.Add(w);
            }
            return r;
        }

        public static void Save(string file, BuildingPreset preset)
        {
            if (file == null || preset.sortedWindows == null) return;
            try
            {
                var floors = new List<Floor>();
                foreach (var f in preset.sortedWindows)
                    floors.Add(f == null ? new Floor() : new Floor { Front = Out(f.front), Back = Out(f.back), Left = Out(f.left), Right = Out(f.right) });
                File.WriteAllText(file, JsonSerializer.Serialize(floors));
            }
            catch (Exception e) { Plugin.L.LogWarning($"Couldn't write the window cache: {e.Message}"); }
        }

        public static bool TryLoad(string file, BuildingPreset preset)
        {
            if (file == null || !File.Exists(file)) return false;
            try
            {
                var floors = JsonSerializer.Deserialize<List<Floor>>(File.ReadAllText(file));
                if (floors == null) return false;
                var list = new Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVFloor>();
                foreach (var f in floors)
                    list.Add(new BuildingPreset.WindowUVFloor { front = In(f.Front), back = In(f.Back), left = In(f.Left), right = In(f.Right) });
                preset.sortedWindows = list;
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"Couldn't read the window cache ({e.Message}); working the windows out again.");
                try { File.Delete(file); } catch { }
                return false;
            }
        }
    }
}

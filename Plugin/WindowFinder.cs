using System;
using System.Collections.Generic;
using UnityEngine;

namespace CustomBuildings
{
    /// <summary>
    /// Works out a custom exterior's windows the way BuildingPreset.GenerateWindowData does, but in a few milliseconds
    /// (the game's routine took minutes on our models). Checked against the game's own results for two buildings: all
    /// 246 windows identical. Each white area of the window map is one window:
    ///   originPixel = lowest-left pixel, rectSize = its size, centrePixel = origin + size/2 (pixels, bottom-up),
    ///   localMeshPositionLeft = model position at the centre pixel's UV, …Right = one pixel to the right,
    ///   floor = floor(height / 5.4), side = which way the wall faces (front = +Z, back = −Z, right = +X, left = −X),
    ///   horizonal = index along that side, left to right as seen from outside. sortedWindows[i] holds floor i + 1.
    /// </summary>
    internal static class WindowFinder
    {
        public const float StoreyHeight = 5.4f;

        public sealed class Window
        {
            public (int x, int y) Origin, Size, Centre;
            public Vector3 Left, Right;
            public int Floor;
            public (int x, int y) Side;
        }

        /// <param name="white">window map, bottom-up rows (Unity's pixel order)</param>
        public static List<Window> Find(bool[] white, int w, int h, IList<Vector3> v, IList<Vector2> uv, IList<Vector3> n, IList<int> tris)
        {
            var result = new List<Window>();
            var seen = new bool[w * h];
            var stack = new Stack<int>();
            for (int start = 0; start < white.Length; start++)
            {
                if (!white[start] || seen[start]) continue;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int i = stack.Pop(), x = i % w, y = i / w;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    void Try(int xx, int yy)
                    {
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) return;
                        int j = yy * w + xx;
                        if (seen[j] || !white[j]) return;
                        seen[j] = true;
                        stack.Push(j);
                    }
                    Try(x + 1, y); Try(x - 1, y); Try(x, y + 1); Try(x, y - 1);
                }
                var size = (x: maxX - minX + 1, y: maxY - minY + 1);
                var centre = (x: minX + size.x / 2, y: minY + size.y / 2);
                if (!At(centre.x / (float)w, centre.y / (float)h, v, uv, n, tris, out var left, out var normal)) continue;
                if (!At((centre.x + 1) / (float)w, centre.y / (float)h, v, uv, n, tris, out var right, out _)) right = left;
                (int x, int y) side = normal.z > Math.Abs(normal.x) ? (0, 1)
                                    : -normal.z > Math.Abs(normal.x) ? (0, -1)
                                    : normal.x > 0 ? (1, 0) : (-1, 0);
                result.Add(new Window
                {
                    Origin = (minX, minY), Size = size, Centre = centre, Left = left, Right = right,
                    Floor = (int)Math.Floor(left.y / StoreyHeight), Side = side,
                });
            }
            return result;
        }

        /// <summary>Model position (and the wall's facing) at a UV, from the triangle that covers it.</summary>
        private static bool At(float u, float vv, IList<Vector3> v, IList<Vector2> uv, IList<Vector3> n, IList<int> tris, out Vector3 pos, out Vector3 normal)
        {
            const float eps = -1e-6f;
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                Vector2 A = uv[a], B = uv[b], C = uv[c];
                float den = (B.y - C.y) * (A.x - C.x) + (C.x - B.x) * (A.y - C.y);
                if (Math.Abs(den) < 1e-12f) continue;
                float w0 = ((B.y - C.y) * (u - C.x) + (C.x - B.x) * (vv - C.y)) / den;
                float w1 = ((C.y - A.y) * (u - C.x) + (A.x - C.x) * (vv - C.y)) / den;
                float w2 = 1 - w0 - w1;
                if (w0 < eps || w1 < eps || w2 < eps) continue;
                pos = new Vector3(v[a].x * w0 + v[b].x * w1 + v[c].x * w2, v[a].y * w0 + v[b].y * w1 + v[c].y * w2, v[a].z * w0 + v[b].z * w1 + v[c].z * w2);
                if (n != null && n.Count == v.Count)
                    normal = new Vector3(n[a].x + n[b].x + n[c].x, n[a].y + n[b].y + n[c].y, n[a].z + n[b].z + n[c].z);
                else
                {
                    float ux = v[b].x - v[a].x, uy = v[b].y - v[a].y, uz = v[b].z - v[a].z;
                    float wx = v[c].x - v[a].x, wy = v[c].y - v[a].y, wz = v[c].z - v[a].z;
                    normal = new Vector3(uy * wz - uz * wy, uz * wx - ux * wz, ux * wy - uy * wx);
                }
                return true;
            }
            pos = default;
            normal = default;
            return false;
        }

        /// <summary>Fills preset.sortedWindows. Returns how many windows were placed.</summary>
        public static int Apply(BuildingPreset preset, List<Window> windows)
        {
            int top = 0;
            foreach (var win in windows) top = Math.Max(top, win.Floor);
            var floors = new Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVFloor>();
            int placed = 0;
            for (int f = 1; f <= top; f++)
            {
                var entry = new BuildingPreset.WindowUVFloor
                {
                    front = new Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVBlock>(),
                    back = new Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVBlock>(),
                    left = new Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVBlock>(),
                    right = new Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVBlock>(),
                };
                foreach (var (side, list, order) in new ((int x, int y), Il2CppSystem.Collections.Generic.List<BuildingPreset.WindowUVBlock>, Func<Window, float>)[]
                {
                    ((0, 1), entry.front, w => -w.Left.x),
                    ((0, -1), entry.back, w => w.Left.x),
                    ((-1, 0), entry.left, w => -w.Left.z),
                    ((1, 0), entry.right, w => w.Left.z),
                })
                {
                    var mine = windows.FindAll(w => w.Floor == f && w.Side.x == side.x && w.Side.y == side.y);
                    mine.Sort((p, q) => order(p).CompareTo(order(q)));
                    for (int i = 0; i < mine.Count; i++)
                    {
                        var w = mine[i];
                        list.Add(new BuildingPreset.WindowUVBlock
                        {
                            originPixel = new Vector2(w.Origin.x, w.Origin.y),
                            rectSize = new Vector2(w.Size.x, w.Size.y),
                            centrePixel = new Vector2(w.Centre.x, w.Centre.y),
                            localMeshPositionLeft = w.Left,
                            localMeshPositionRight = w.Right,
                            floor = f,
                            side = new Vector2(side.x, side.y),
                            horizonal = i,
                        });
                        placed++;
                    }
                }
                floors.Add(entry);
            }
            preset.sortedWindows = floors;
            return placed;
        }
    }
}

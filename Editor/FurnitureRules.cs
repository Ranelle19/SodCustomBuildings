using System;
using System.Collections.Generic;
using System.Linq;
using CustomBuildings.Shared;

namespace BuildingEditor
{
    /// <summary>
    /// Where hand-placed furniture may go, shared by the 2D floor editor and the 3D view, and a signal so both show the
    /// same furniture after either one changed it.
    /// </summary>
    public static class FurnitureRules
    {
        /// <summary>Hand-placed furniture changed somewhere (the argument is who changed it, so it can skip itself).</summary>
        public static event Action<object> Changed;
        public static void RaiseChanged(object source) => Changed?.Invoke(source);

        public static (int w, int d) Footprint(FurnitureItem it, CatalogFurniture info) => it.Footprint(info?.W ?? 1, info?.D ?? 1);

        /// <summary>The furniture on this floor plan that belongs to the variation shown for its address.</summary>
        public static IEnumerable<FurnitureItem> OnFloor(FloorDoc doc, List<FurnitureItem> all, Func<int, int> variationOf) =>
            all == null || doc == null ? Enumerable.Empty<FurnitureItem>()
                : all.Where(f => f.Floor == doc.Name && f.Variation == variationOf(f.Address - 1));

        public static FurnitureItem At(FloorDoc doc, List<FurnitureItem> all, int x, int y, Func<int, int> variationOf, Func<string, CatalogFurniture> info)
        {
            foreach (var it in OnFloor(doc, all, variationOf).Reverse())
            {
                var (w, d) = Footprint(it, info?.Invoke(it.Preset));
                if (x >= it.X && y >= it.Y && x < it.X + w && y < it.Y + d) return it;
            }
            return null;
        }

        /// <summary>
        /// A footprint fits if all its cells are in one room of one address (in the variation shown) and it doesn't
        /// overlap other furniture there (wall and ceiling pieces may share cells with floor pieces).
        /// </summary>
        public static bool Fits(FloorDoc doc, List<FurnitureItem> all, int x0, int y0, int w, int d, string preset, FurnitureItem ignore,
                                Func<int, int> variationOf, Func<string, CatalogFurniture> info, out int address, out string why)
        {
            address = -1; why = "";
            if (doc == null) { why = "no floor plan"; return false; }
            int a = doc.AddressAt(x0, y0);
            if (a < 0) { why = "not on the floor"; return false; }
            int v = variationOf(a);
            var first = doc.Find(a, v, x0, y0);
            if (first == null) { why = "not in this layout variation"; return false; }
            for (int x = x0; x < x0 + w; x++)
                for (int y = y0; y < y0 + d; y++)
                {
                    var n = doc.Find(a, v, x, y);
                    if (n == null || n.R != first.R) { why = "it has to fit inside one room"; return false; }
                }
            var mine = info?.Invoke(preset ?? ignore?.Preset ?? "");
            bool layered = mine != null && (mine.Wall || mine.Ceiling);
            foreach (var it in OnFloor(doc, all, variationOf))
            {
                if (it == ignore || it.Address != a + 1) continue;
                var oi = info?.Invoke(it.Preset);
                if (layered || (oi != null && (oi.Wall || oi.Ceiling))) continue;
                var (ow, od) = Footprint(it, oi);
                if (x0 < it.X + ow && it.X < x0 + w && y0 < it.Y + od && it.Y < y0 + d) { why = $"it overlaps {it.Preset}"; return false; }
            }
            address = a;
            return true;
        }
    }
}

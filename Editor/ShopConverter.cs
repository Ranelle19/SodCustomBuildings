using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace BuildingEditor
{
    /// <summary>
    /// Turns homes and offices on a floor plan into shops ("Retail") and restaurants ("Eatery"), keeping their walls, doors
    /// and windows. Only room types change; rooms that don't fit a shop (an apartment's hallway) are merged into the main room
    /// and the walls between them removed. Tested against all of the game's exported floors before being ported here.
    /// </summary>
    public static class ShopConverter
    {
        public const string Shop = "Retail";
        public const string Restaurant = "Eatery";

        public static readonly string[] HomeTypes = { "Apartment", "HotelRoom", "Den" };
        public static readonly string[] OfficeTypes = { "Office", "OfficeHighrise" };

        private static readonly HashSet<string> Main = new() { "LivingRoom", "OfficeSpace", "Den" };
        private static readonly HashSet<string> Join = new() { "Hallway", "DiningRoom", "Reception" };
        private static readonly HashSet<string> Wash = new() { "Bathroom", "ShowerRoom", "PublicBathroomEatery" };
        private static readonly HashSet<string> OfficeRooms = new() { "PrivateOffice", "Bedroom" };

        public sealed class Options
        {
            public bool Homes = true;
            public bool Offices;
            /// <summary>0..1: share of converted addresses that become restaurants (where a kitchen fits).</summary>
            public double RestaurantShare = 0.25;
        }

        /// <summary>Running count across a whole building, so the restaurant share holds over all storeys.</summary>
        public sealed class Tally
        {
            public int Shops, Restaurants;
            public int Total => Shops + Restaurants;
        }

        public static bool IsConvertible(string type, Options o) =>
            (o.Homes && HomeTypes.Contains(type)) || (o.Offices && OfficeTypes.Contains(type));

        /// <summary>How many addresses on this floor would change.</summary>
        public static int CountConvertible(FloorDoc doc, Options o)
        {
            int n = 0;
            for (int a = 0; a < doc.Addresses.Count; a++)
                if (IsConvertible(doc.AddressPreset(a), o)) n++;
            return n;
        }

        /// <summary>Converts in place. Returns how many addresses changed.</summary>
        public static int Convert(FloorDoc doc, Options o, Tally tally)
        {
            int changed = 0;
            for (int a = 0; a < doc.Addresses.Count; a++)
            {
                if (!IsConvertible(doc.AddressPreset(a), o)) continue;

                // restaurant if that keeps us at or under the requested share, and a kitchen fits
                bool wantRestaurant = o.RestaurantShare > 0 &&
                                      tally.Restaurants + 1 <= Math.Round((tally.Total + 1) * o.RestaurantShare) &&
                                      CanBeRestaurant(doc, a);
                var kind = wantRestaurant ? Restaurant : Shop;

                doc.SetAddressPreset(a, kind);
                for (int v = 0; v < doc.VariationCount(a); v++) ConvertVariation(doc, doc.Rooms(a, v), kind);
                if (kind == Restaurant) tally.Restaurants++; else tally.Shops++;
                changed++;
            }
            if (changed > 0) doc.Dirty = true;
            return changed;
        }

        // ------------------------------------------------------------------ one address

        private static string Type(JsonObject room) => FloorDoc.Str(room, "l");
        private static JsonArray Nodes(JsonObject room) => FloorDoc.NodesOf(room);
        private static bool IsNull(JsonObject room) => Type(room) == FloorDoc.NullRoom;

        private static List<JsonObject> RealRooms(JsonArray rooms) =>
            rooms.OfType<JsonObject>().Where(r => !IsNull(r) && Nodes(r).Count > 0).ToList();

        /// <summary>A restaurant needs a main room, a bathroom and one more room for the kitchen, in every variation.</summary>
        private static bool CanBeRestaurant(FloorDoc doc, int a)
        {
            for (int v = 0; v < doc.VariationCount(a); v++)
            {
                var real = RealRooms(doc.Rooms(a, v));
                var others = real.Where(r => !Main.Contains(Type(r)) && !Join.Contains(Type(r)) && !Wash.Contains(Type(r))).ToList();
                if (real.Count < 3 || others.Count == 0) return false;
            }
            return true;
        }

        private static HashSet<(int, int)> Cells(JsonObject room)
        {
            var set = new HashSet<(int, int)>();
            foreach (var n in Nodes(room).OfType<JsonObject>()) set.Add(FloorDoc.Coord(n));
            return set;
        }

        private static readonly (int dx, int dy)[] Steps = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        private static bool Adjacent(HashSet<(int, int)> a, HashSet<(int, int)> b)
        {
            foreach (var (x, y) in a)
                foreach (var (dx, dy) in Steps)
                    if (b.Contains((x + dx, y + dy))) return true;
            return false;
        }

        private static (int dx, int dy) Step(WallDir d) => d switch
        {
            WallDir.Up => (0, -1), WallDir.Right => (1, 0), WallDir.Down => (0, 1), _ => (-1, 0),
        };

        /// <summary>Moves src's cells into dst and removes the walls between them (walls are stored on both sides).</summary>
        private static void Merge(JsonObject src, JsonObject dst)
        {
            var srcCells = Cells(src);
            var dstCells = Cells(dst);
            RemoveWallsToward(src, dstCells);
            RemoveWallsToward(dst, srcCells);
            foreach (var n in Nodes(src).OfType<JsonObject>().ToList())
            {
                Nodes(src).Remove(n);
                Nodes(dst).Add(n);
            }
        }

        private static void RemoveWallsToward(JsonObject room, HashSet<(int, int)> other)
        {
            foreach (var n in Nodes(room).OfType<JsonObject>())
            {
                if (n["w_d"] is not JsonArray walls) continue;
                var (x, y) = FloorDoc.Coord(n);
                foreach (var w in walls.OfType<JsonObject>().ToList())
                {
                    var (dx, dy) = Step(FloorDoc.DirOf(w));
                    if (other.Contains((x + dx, y + dy))) walls.Remove(w);
                }
            }
        }

        private const string SpaceMark = "\u0000space";

        private static void ConvertVariation(FloorDoc doc, JsonArray rooms, string kind)
        {
            // Null rooms that hold cells (spare space inside a home) are folded into the shop like a hallway.
            // Every game shop has exactly one, empty, Null room.
            foreach (var r in rooms.OfType<JsonObject>())
                if (IsNull(r) && Nodes(r).Count > 0) r["l"] = SpaceMark;

            var real = RealRooms(rooms);
            if (real.Count == 0) { RestoreSpace(rooms); return; }

            var mains = real.Where(r => Main.Contains(Type(r))).ToList();
            if (mains.Count == 0) mains = new List<JsonObject> { real.OrderByDescending(r => Nodes(r).Count).First() };
            var main = mains.OrderByDescending(r => Nodes(r).Count).First();

            string mainType = kind == Shop ? "RetailSpace" : "Eatery";
            string storeType = kind == Shop ? "Storeroom" : "Kitchen";
            string washType = kind == Shop ? "Bathroom" : "PublicBathroomEatery";
            const string mergeMark = "\u0000merge";

            var target = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance) { [main] = mainType };
            bool office = false;
            var mainCells = Cells(main);
            foreach (var r in real.Where(r => r != main).OrderByDescending(r => Nodes(r).Count))
            {
                var t = Type(r);
                if ((Join.Contains(t) || t == SpaceMark) && Adjacent(Cells(r), mainCells)) target[r] = mergeMark;
                else if (Wash.Contains(t)) target[r] = washType;
                else if (OfficeRooms.Contains(t) && !office) { target[r] = "PrivateOffice"; office = true; }
                else target[r] = storeType;
            }

            // a restaurant needs a kitchen: use the office if nothing else became one
            if (kind == Restaurant && !target.Values.Contains("Kitchen"))
            {
                var office1 = real.FirstOrDefault(r => r != main && target[r] == "PrivateOffice");
                if (office1 != null) target[office1] = "Kitchen";
            }

            foreach (var r in real)
            {
                if (target[r] == mergeMark) Merge(r, main);
                else r["l"] = target[r];
            }

            // drop rooms left empty by merging
            foreach (var r in rooms.OfType<JsonObject>().Where(r => !IsNull(r) && Nodes(r).Count == 0).ToList())
                if (real.Contains(r)) rooms.Remove(r);

            // exactly one empty Null room, first
            var nulls = rooms.OfType<JsonObject>().Where(IsNull).ToList();
            var keep = nulls.FirstOrDefault() ?? new JsonObject { ["id"] = doc.NextRoomId(), ["n_d"] = new JsonArray(), ["l"] = FloorDoc.NullRoom };
            var others = rooms.OfType<JsonObject>().Where(r => !IsNull(r)).ToList();
            rooms.Clear();
            rooms.Add(keep);
            foreach (var r in others) rooms.Add(r);
        }

        /// <summary>Nothing to convert: put any marked spare space back as it was.</summary>
        private static void RestoreSpace(JsonArray rooms)
        {
            foreach (var r in rooms.OfType<JsonObject>())
                if (Type(r) == SpaceMark) r["l"] = FloorDoc.NullRoom;
        }
    }
}

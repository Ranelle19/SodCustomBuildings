using System;
using System.Collections.Generic;

namespace BuildingEditor
{
    /// <summary>
    /// Plain-language names for the codes in floor files. Walls are stored by id ("7"); the names come from the plugin's
    /// catalog export (WallPresetNames). Address types (a_d[].p_n) are LayoutConfiguration names such as "Retail".
    /// </summary>
    public static class GameNames
    {
        public static Dictionary<string, string> Walls { get; set; } = new();

        public static string WallName(string id) =>
            id != null && Walls.TryGetValue(id, out var n) ? n : null;

        /// <summary>"7  InteriorDoorway", or just "7" while the names aren't exported yet.</summary>
        public static string WallLabel(string id)
        {
            var n = WallName(id);
            return n == null ? id : $"{id}  {n}";
        }

        /// <summary>The id at the start of a label made by WallLabel (or typed by hand).</summary>
        public static string WallIdFromLabel(string label)
        {
            label = (label ?? "").Trim();
            int sp = label.IndexOf(' ');
            return sp < 0 ? label : label.Substring(0, sp);
        }

        public enum WallKind { Wall, Door, Window, Entrance, Opening, Railing }

        public static WallKind KindOf(string id)
        {
            var n = (WallName(id) ?? id ?? "").ToLowerInvariant();
            if (n.Contains("entrance")) return WallKind.Entrance;
            if (n.Contains("door")) return WallKind.Door;
            if (n.Contains("window")) return WallKind.Window;
            if (n.Contains("bannister") || n.Contains("handrail") || n.Contains("fence")) return WallKind.Railing;
            if (n.Contains("nothing") || n.Contains("divider")) return WallKind.Opening;
            return WallKind.Wall;
        }

        private static readonly Dictionary<string, string> AddressTypes = new(StringComparer.Ordinal)
        {
            ["Retail"] = "Shop (the game picks the kind of shop)",
            ["Eatery"] = "Restaurant / café",
            ["Apartment"] = "Home (apartment)",
            ["HotelRoom"] = "Hotel room",
            ["Den"] = "Cheap room (den)",
            ["Lobby"] = "Hallway / stairs",
            ["BasementLobby"] = "Basement hallway",
            ["CorporateLobby"] = "Office lobby",
            ["Outside"] = "Outside (around the building)",
            ["StreetFrontage"] = "Street frontage",
            ["Office"] = "Office",
            ["OfficeHighrise"] = "Office (tower)",
            ["PowerRoom"] = "Power room",
            ["ControlRoom"] = "Security control room",
            ["BuildingBathrooms"] = "Public toilets",
            ["Rooftop"] = "Rooftop",
            ["AmericanDiner"] = "Diner",
            ["RooftopBar"] = "Rooftop bar",
        };

        /// <summary>"Shop (…)  [Retail]" for lists, so both the plain name and the code are visible.</summary>
        public static string AddressTypeLabel(string code) =>
            AddressTypes.TryGetValue(code ?? "", out var n) ? $"{n}  [{code}]" : code ?? "";

        /// <summary>The code from a label made by AddressTypeLabel (or a code typed by hand).</summary>
        public static string AddressTypeFromLabel(string label)
        {
            label = (label ?? "").Trim();
            int open = label.LastIndexOf('['), close = label.LastIndexOf(']');
            return open >= 0 && close > open ? label.Substring(open + 1, close - open - 1) : label;
        }
    }
}

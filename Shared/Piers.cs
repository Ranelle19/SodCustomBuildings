// Shared between the in-game plugin and the editor. No Unity / game references here.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomBuildings.Shared
{
    public enum CitySide { North, East, South, West }
    public enum WallRailingGap { Cut, StepsOver }
    public enum PierAttach { Wall, Platform }

    /// <summary>
    /// A pier at the edge of the city: stairs from the street down the outside of the sea wall to a deck just above
    /// the water. Belongs to one of our wall pieces (EdgePieceDef.Pier) and is built at every copy of it.
    /// </summary>
    public class PierDef
    {
        [Category("1. Where"), Description("Shown in the editor and the log.")]
        public string Name { get; set; } = "Pier";

        [Category("1. Where"), Description("Untick to keep the pier in the file but not build it.")]
        public bool Enabled { get; set; } = true;

        /// <summary>Set by the plugin from the wall piece the pier belongs to.</summary>
        [Browsable(false)]
        public CitySide Side { get; set; } = CitySide.North;

        [Category("1. Where"), Description("Where along its wall piece, 0–100 % (50 = the middle), from the left end as seen from the city looking out to sea. " +
                                           "With PreferBay, the nearest bay between two pillars.")]
        public float Position { get; set; } = 50;

        /// <summary>Follows the wall piece's Role (a Platform piece puts the pier on the platform's outer edge).</summary>
        [Browsable(false)]
        public PierAttach AttachTo { get; set; } = PierAttach.Wall;

        [Category("1. Where"), Description("On a wall: start the stairs from the nearest of the little bays between two pillars, where the walkway " +
                                           "sticks out a bit (Position picks which one). Off = exactly at Position.")]
        public bool PreferBay { get; set; } = true;

        [Category("1. Where"), Description("Which way the stairs go down along the wall, seen from the city looking out to sea.")]
        public bool StairsGoRight { get; set; } = true;

        [Category("2. Size"), Description("How far the deck reaches out to sea beyond the stairs, in metres.")]
        public float Length { get; set; } = 25;

        [Category("2. Size"), Description("Width of the deck, in metres.")]
        public float Width { get; set; } = 6;

        [Category("2. Size"), Description("Width of the stairs, in metres.")]
        public float StairsWidth { get; set; } = 2.0f;

        [Category("2. Size"), Description("A wide platform at the end of the deck (a T shape), this wide in metres. 0 = none, just the deck.")]
        public float HeadWidth { get; set; } = 14;

        [Category("2. Size"), Description("How deep the platform at the end is, in metres.")]
        public float HeadLength { get; set; } = 9;

        [Category("2. Size"), Description("Height of the deck above the water, in metres. The sea is about 12 m below the street. The game slows you down " +
                                          "(wading) below about 2.4 m above the sea, so the deck never goes lower than that.")]
        public float DeckAboveWater { get; set; } = 3.0f;

        [Category("2. Size"), Description("Distance between the posts under the deck, in metres.")]
        public float PostSpacing { get; set; } = 3;

        [Category("3. Details"), Description("Cut = take a piece out of the game's railing on the wall where the stairs start. StepsOver = a few steps up and over it " +
                                             "(also used by itself if the game's railing can't be cut).")]
        public WallRailingGap RailingGap { get; set; } = WallRailingGap.Cut;

        [Category("3. Details"), Description("Use a copy of the game's own railing from the wall (same look). Off, or if it can't be copied: simple bars.")]
        public bool GameRailing { get; set; } = true;

        [Category("3. Details"), Description("Railing around the deck. The stairs always have one.")]
        public bool DeckRailing { get; set; } = true;

        [Category("3. Details"), Description("Mooring posts along the deck.")]
        public bool Bollards { get; set; } = true;

        [Category("3. Details"), Description("A lamp post at the end of the deck (no light, just the post).")]
        public bool LampPost { get; set; } = true;

        [Category("4. Colours"), Description("Deck planks, as #RRGGBB.")]
        public string DeckColour { get; set; } = "#4E3D2C";

        [Category("4. Colours"), Description("Stairs and landings.")]
        public string StairsColour { get; set; } = "#5F5C57";

        [Category("4. Colours"), Description("Posts in the water.")]
        public string PostColour { get; set; } = "#2F2A25";

        [Category("4. Colours"), Description("Railings, bollards and the lamp post.")]
        public string RailColour { get; set; } = "#26292B";

        public PierDef Clone() => (PierDef)MemberwiseClone();
        public override string ToString() => $"{Name} ({Position:0}% along its wall piece)";
    }

    public enum PierPart { Deck, Stairs, Post, Rail, Bollard, Lamp }

    /// <summary>
    /// An axis-aligned box in the pier's own frame: A along the wall (to the right seen from the city), U out to sea from
    /// the wall's face (0 = the face), Y = world height.
    /// </summary>
    public struct PierBox
    {
        public PierPart Part;
        public float A0, A1, U0, U1, Y0, Y1;
        public bool Solid;   // gets a collider
        public bool Visible;
    }

    /// <summary>A sloped bar: top surface runs along the line from (A0,U0,Y0) to (A1,U1,Y1); Width sideways, Thick below.</summary>
    public struct PierRamp
    {
        public PierPart Part;
        public float A0, U0, Y0, A1, U1, Y1, Width, Thick;
        public bool Solid, Visible;
    }

    public sealed class PierModel
    {
        public readonly List<PierBox> Boxes = new();
        public readonly List<PierRamp> Ramps = new();
        /// <summary>Base lines of all railings (A0,U0,Y0 → A1,U1,Y1), for putting the game's railing along them.</summary>
        public readonly List<(float A0, float U0, float Y0, float A1, float U1, float Y1)> RailLines = new();
        public float GapA0, GapA1;          // where the wall railing is opened
        public float LandingBack;           // the walkway's edge is at U = -LandingBack
        public float RescueA, RescueU;      // where someone who fell into the sea is put back (on the deck)
        public float StartY, DeckY, TopY;
        public int Steps;
        public float MinA, MaxA, MaxU;      // footprint, for clamping and drawing
    }

    /// <summary>The pier's shape. The plugin builds it in game, the editor draws it — both from this same code.</summary>
    public static class PierLayout
    {
        public const float Tread = 0.32f, MaxRise = 0.2f, FlightGap = 0.15f, RailHeight = 1.0f;

        /// <param name="topY">top of the wall (street level)</param>
        /// <param name="seaY">water surface</param>
        /// <param name="railTop">top of the game's railing on the wall (for StepsOver)</param>
        /// <param name="stepsOver">build steps over the wall railing instead of using a gap in it</param>
        public static PierModel Build(PierDef d, float topY, float seaY, float railTop, bool stepsOver, float minDeckY = float.MinValue, float landingBack = 0)
        {
            var m = new PierModel();
            float w = Clamp(d.StairsWidth, 0.9f, 4f), g = FlightGap, s = d.StairsGoRight ? 1 : -1;
            float width = Clamp(d.Width, 1.5f, 30f), length = Clamp(d.Length, 0f, 150f);
            float deckY = Math.Max(seaY + Clamp(d.DeckAboveWater, 0.3f, 8f), minDeckY);
            float startY = stepsOver ? Math.Max(topY, railTop) + 0.1f : topY;
            deckY = Math.Min(deckY, startY - 1f);
            m.TopY = topY; m.StartY = startY; m.DeckY = deckY;

            float H = startY - deckY;
            int N = Math.Max(2, (int)Math.Ceiling(H / MaxRise));
            float r = H / N;
            int n1 = N / 2, n2 = N - n1;
            float L1 = n1 * Tread, L2 = n2 * Tread;
            m.Steps = N;
            const float thick = 0.3f;
            float Sa(float a) => s * a;   // along the stairs' direction

            void Box(PierPart p, float a0, float a1, float u0, float u1, float y0, float y1, bool solid = true, bool visible = true)
                => m.Boxes.Add(new PierBox { Part = p, A0 = Math.Min(a0, a1), A1 = Math.Max(a0, a1), U0 = Math.Min(u0, u1), U1 = Math.Max(u0, u1),
                                             Y0 = Math.Min(y0, y1), Y1 = Math.Max(y0, y1), Solid = solid, Visible = visible });
            void Ramp(PierPart p, float a0, float u0, float y0, float a1, float u1, float y1, float wid, float th, bool solid, bool visible)
                => m.Ramps.Add(new PierRamp { Part = p, A0 = a0, U0 = u0, Y0 = y0, A1 = a1, U1 = u1, Y1 = y1, Width = wid, Thick = th, Solid = solid, Visible = visible });

            // a straight railing along A (u fixed) or along U (a fixed): posts + top bar + one invisible wall to stop falling
            void RailA(float a0, float a1, float u, float y)
            {
                if (Math.Abs(a1 - a0) < 0.05f) return;
                m.RailLines.Add((a0, u, y, a1, u, y));
                float lo = Math.Min(a0, a1), hi = Math.Max(a0, a1);
                int posts = Math.Max(1, (int)Math.Ceiling((hi - lo) / 1.5f));
                for (int i = 0; i <= posts; i++) { float a = lo + (hi - lo) * i / posts; Box(PierPart.Rail, a - 0.04f, a + 0.04f, u - 0.04f, u + 0.04f, y, y + RailHeight, solid: false); }
                Box(PierPart.Rail, lo, hi, u - 0.05f, u + 0.05f, y + RailHeight - 0.07f, y + RailHeight, solid: false);
                Box(PierPart.Rail, lo, hi, u - 0.05f, u + 0.05f, y, y + RailHeight + 0.1f, solid: true, visible: false);
            }
            void RailU(float u0, float u1, float a, float y)
            {
                if (Math.Abs(u1 - u0) < 0.05f) return;
                m.RailLines.Add((a, u0, y, a, u1, y));
                float lo = Math.Min(u0, u1), hi = Math.Max(u0, u1);
                int posts = Math.Max(1, (int)Math.Ceiling((hi - lo) / 1.5f));
                for (int i = 0; i <= posts; i++) { float u = lo + (hi - lo) * i / posts; Box(PierPart.Rail, a - 0.04f, a + 0.04f, u - 0.04f, u + 0.04f, y, y + RailHeight, solid: false); }
                Box(PierPart.Rail, a - 0.05f, a + 0.05f, lo, hi, y + RailHeight - 0.07f, y + RailHeight, solid: false);
                Box(PierPart.Rail, a - 0.05f, a + 0.05f, lo, hi, y, y + RailHeight + 0.1f, solid: true, visible: false);
            }
            // a sloped railing along a flight (A direction), at the lane edge u
            void RailSlope(float a0, float y0, float a1, float y1, float u)
            {
                m.RailLines.Add((a0, u, y0, a1, u, y1));
                int posts = Math.Max(1, (int)Math.Ceiling(Math.Abs(a1 - a0) / 1.5f));
                for (int i = 0; i <= posts; i++)
                {
                    float t = (float)i / posts, a = a0 + (a1 - a0) * t, y = y0 + (y1 - y0) * t;
                    Box(PierPart.Rail, a - 0.04f, a + 0.04f, u - 0.04f, u + 0.04f, y, y + RailHeight, solid: false);
                }
                Ramp(PierPart.Rail, a0, u, y0 + RailHeight, a1, u, y1 + RailHeight, 0.08f, 0.07f, false, true);
                Ramp(PierPart.Rail, a0, u, y0 + RailHeight + 0.1f, a1, u, y1 + RailHeight + 0.1f, 0.1f, RailHeight + 0.1f, true, false);
            }
            float Sea(float below) => seaY - below;

            // ---- top landing, outside the wall at the gap
            float half = w / 2;
            // landingBack: the landing reaches back this far to the real edge, when the stairs stand off the wall to clear
            // pillars or a bay that sticks out (U = -landingBack is where the walkway ends)
            float back = Math.Max(0, Math.Min(landingBack, 4f));
            m.LandingBack = back;
            Box(PierPart.Stairs, -half, half, -back, w, startY - thick, startY);
            m.GapA0 = -half - 0.1f; m.GapA1 = half + 0.1f;
            RailU(-back, w, -Sa(half), startY);     // the side away from the stairs
            if (back > 0.05f) RailU(-back, 0, Sa(half), startY);   // and the stairs side, up to where flight 1 starts
            RailA(-half, half, w, startY);          // the sea side
            Box(PierPart.Post, Sa(-half) - 0.15f * s, Sa(-half) + 0.15f * s, w - 0.3f, w, Sea(3), startY - thick, solid: true);

            // steps over the game's railing: up from the wall top on the city side, over the rail, onto the landing
            if (stepsOver && startY > topY + 0.05f)
            {
                int k = Math.Max(1, (int)Math.Ceiling((startY - topY) / MaxRise));
                float rr = (startY - topY) / k;
                for (int i = 1; i <= k; i++)
                    Box(PierPart.Stairs, -half, half, -back - (k - i + 1) * Tread, -back - (k - i) * Tread, topY, topY + i * rr, solid: false);
                Ramp(PierPart.Stairs, 0, -back - k * Tread, topY, 0, -back, startY, w, 0.3f, true, false);
                RailU(-back - k * Tread, -back, -half, topY + 0);   // short side rails
                RailU(-back - k * Tread, -back, half, topY + 0);
            }

            // ---- flight 1: along the wall, lane U 0..w
            for (int i = 1; i <= n1; i++)
                Box(PierPart.Stairs, Sa(half + (i - 1) * Tread), Sa(half + i * Tread), 0, w, startY - i * r - 0.35f, startY - i * r, solid: false);
            float y1 = startY - n1 * r;
            Ramp(PierPart.Stairs, Sa(half), w / 2, startY, Sa(half + L1), w / 2, y1, w, 0.3f, true, false);
            Ramp(PierPart.Stairs, Sa(half), w / 2, startY - 0.3f, Sa(half + L1), w / 2, y1 - 0.3f, w, 0.25f, false, true);   // underside
            RailSlope(Sa(half), startY, Sa(half + L1), y1, w);
            RailSlope(Sa(half), startY, Sa(half + L1), y1, 0);      // wall side too: the wall below the street is set back
            for (float a = half + 2.5f; a < half + L1 - 0.5f; a += 3f)   // legs: the wall below the street can be set back
            {
                float yy = startY - (a - half) / Tread * r - 0.35f;
                Box(PierPart.Post, Sa(a) - 0.12f, Sa(a) + 0.12f, w - 0.32f, w - 0.08f, Sea(3), yy, solid: false);
            }

            // ---- middle landing, both lanes
            float aMid0 = half + L1, aMid1 = half + L1 + w, uOut = 2 * w + g;
            Box(PierPart.Stairs, Sa(aMid0), Sa(aMid1), 0, uOut, y1 - thick, y1);
            RailU(0, uOut, Sa(aMid1), y1);
            RailA(Sa(aMid0), Sa(aMid1), 0, y1);                      // wall side
            RailA(Sa(aMid0), Sa(aMid1), uOut, y1);
            Box(PierPart.Post, Sa(aMid1) - 0.15f, Sa(aMid1) + 0.15f, uOut - 0.3f, uOut, Sea(3), y1 - thick, solid: true);

            // ---- flight 2: back along the wall, outer lane
            for (int j = 1; j <= n2; j++)
                Box(PierPart.Stairs, Sa(aMid0 - (j - 1) * Tread), Sa(aMid0 - j * Tread), w + g, uOut, y1 - j * r - 0.35f, y1 - j * r, solid: false);
            float aEnd = aMid0 - L2;
            Ramp(PierPart.Stairs, Sa(aMid0), w + g + w / 2, y1, Sa(aEnd), w + g + w / 2, deckY, w, 0.3f, true, false);
            Ramp(PierPart.Stairs, Sa(aMid0), w + g + w / 2, y1 - 0.3f, Sa(aEnd), w + g + w / 2, deckY - 0.3f, w, 0.25f, false, true);
            RailSlope(Sa(aMid0), y1, Sa(aEnd), deckY, uOut);
            RailSlope(Sa(aMid0), y1, Sa(aEnd), deckY, w + g);        // inner side, above flight 1
            for (float a = aMid0 - 2.5f; a > aEnd + 1.5f; a -= 3f)
            {
                float yy = y1 - (aMid0 - a) / Tread * r - 0.35f;
                Box(PierPart.Post, Sa(a) - 0.12f, Sa(a) + 0.12f, uOut - 0.32f, uOut - 0.08f, Sea(3), yy, solid: false);
            }
            Box(PierPart.Post, Sa(aEnd + 0.3f) - 0.15f, Sa(aEnd + 0.3f) + 0.15f, uOut - 0.3f, uOut, Sea(3), deckY - 0.4f, solid: true);

            // ---- deck: next to the stairs' foot, from the wall out to sea
            float dA0 = aEnd, dA1 = aEnd - width, uEnd = uOut + length;
            Box(PierPart.Deck, Sa(dA0), Sa(dA1), 0, uEnd, deckY - thick, deckY);
            if (d.DeckRailing)
            {
                RailA(Sa(dA0), Sa(dA1), 0, deckY);       // the end toward the city (the wall is set back below)
                RailU(0, uEnd, Sa(dA1), deckY);          // far side
                RailU(uOut, uEnd, Sa(dA0), deckY);       // stairs side: open only where the stairs arrive (lane w+g..uOut)
                RailU(0, w + g, Sa(dA0), deckY);
            }
            // the platform at the end (T shape), centred on the deck
            float hw = Clamp(d.HeadWidth, 0f, 60f), hl = Clamp(d.HeadLength, 2f, 40f);
            bool head = hw >= width + 0.5f;
            float hA0 = dA0, hA1 = dA1, uHead = uEnd;
            if (head)
            {
                float mid = (dA0 + dA1) / 2;
                hA0 = mid + hw / 2; hA1 = mid - hw / 2;   // hA0 on the stairs side (larger A along s)
                uHead = uEnd + hl;
                Box(PierPart.Deck, Sa(hA0), Sa(hA1), uEnd, uHead, deckY - thick, deckY);
                if (d.DeckRailing)
                {
                    RailA(Sa(hA0), Sa(dA0), uEnd, deckY);     // the head's back edge, beside the deck
                    RailA(Sa(dA1), Sa(hA1), uEnd, deckY);
                    RailU(uEnd, uHead, Sa(hA0), deckY);
                    RailU(uEnd, uHead, Sa(hA1), deckY);
                    RailA(Sa(hA0), Sa(hA1), uHead, deckY);
                }
            }
            else if (d.DeckRailing) RailA(Sa(dA0), Sa(dA1), uEnd, deckY);    // the end
            float spacing = Clamp(d.PostSpacing, 1.5f, 20f);
            void PostRow(float aLo, float aHi, float u0, float u1)   // posts under a rectangle of deck, every `spacing`
            {
                int na = Math.Max(1, (int)Math.Round((aHi - aLo) / spacing)), nu = Math.Max(1, (int)Math.Round((u1 - u0) / spacing));
                for (int i = 0; i <= na; i++)
                    for (int j = 0; j <= nu; j++)
                    {
                        bool edgePost = i == 0 || i == na || j == 0 || j == nu;
                        if (!edgePost && (aHi - aLo) < 7) continue;   // narrow: only along the sides
                        float a = aLo + 0.25f + (aHi - aLo - 0.5f) * i / na, u = u0 + 0.25f + (u1 - u0 - 0.5f) * j / nu;
                        Box(PierPart.Post, Sa(a) - 0.17f, Sa(a) + 0.17f, u - 0.17f, u + 0.17f, Sea(3), deckY - thick, solid: true);
                    }
            }
            PostRow(Math.Min(dA0, dA1), Math.Max(dA0, dA1), 0.5f, uEnd);
            if (head) PostRow(Math.Min(hA0, hA1), Math.Max(hA0, hA1), uEnd, uHead);
            if (d.Bollards)
            {
                for (float u = uOut + 2; u < uEnd - 1; u += 6)
                    Box(PierPart.Bollard, Sa(dA1 + 0.45f) - 0.15f, Sa(dA1 + 0.45f) + 0.15f, u - 0.15f, u + 0.15f, deckY, deckY + 0.5f);
                if (head)
                    for (float a = Math.Min(hA0, hA1) + 1.5f; a < Math.Max(hA0, hA1) - 1; a += 4)
                        Box(PierPart.Bollard, Sa(a) - 0.15f, Sa(a) + 0.15f, uHead - 0.6f, uHead - 0.3f, deckY, deckY + 0.5f);
            }
            if (d.LampPost)
            {
                float la = head ? hA1 + 0.35f : dA1 + 0.35f, lu = (head ? uHead : uEnd) - 0.35f;
                Box(PierPart.Lamp, Sa(la) - 0.07f, Sa(la) + 0.07f, lu - 0.07f, lu + 0.07f, deckY, deckY + 3.6f);
                Box(PierPart.Lamp, Sa(la) - 0.22f, Sa(la) + 0.22f, lu - 0.22f, lu + 0.22f, deckY + 3.6f, deckY + 4.0f, solid: false);
            }

            m.RescueA = Sa((dA0 + dA1) / 2); m.RescueU = uOut + 1.5f;
            m.MinA = float.MaxValue; m.MaxA = float.MinValue; m.MaxU = 0;
            foreach (var b in m.Boxes) { m.MinA = Math.Min(m.MinA, b.A0); m.MaxA = Math.Max(m.MaxA, b.A1); m.MaxU = Math.Max(m.MaxU, b.U1); }
            return m;
        }

        private static float Clamp(float v, float lo, float hi) => float.IsNaN(v) ? lo : Math.Max(lo, Math.Min(hi, v));

        /// <summary>#RRGGBB → 0..1 floats; grey on bad input.</summary>
        public static (float r, float g, float b) Colour(string hex)
        {
            try
            {
                var h = (hex ?? "").Trim().TrimStart('#');
                if (h.Length == 6)
                    return (Convert.ToInt32(h.Substring(0, 2), 16) / 255f, Convert.ToInt32(h.Substring(2, 2), 16) / 255f, Convert.ToInt32(h.Substring(4, 2), 16) / 255f);
            }
            catch { }
            return (0.4f, 0.4f, 0.4f);
        }
    }
}

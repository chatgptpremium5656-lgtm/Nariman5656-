using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // Endless street grid for the distance run. Streets run along X and Z every Block
    // metres; each tile owns the junction at its corner, the two street segments that
    // leave it (north and east) and the block of buildings between them.
    public static class GridCity
    {
        public const float Block = 200;          // street pitch
        public const float Half = 7.3f;          // half the carriageway (2 + 2 lanes)
        public const float Lane = 3.65f;
        public const int Window = 7;             // tiles kept around the car, and the repeat period
        const float Kerb = .5f, Walk = 5.5f;
        // Pitch of the street furniture (lamps and trees) along a pavement.
        const float Pitch = 22f;

        public static int Mod(int a, int n) => ((a % n) + n) % n;

        public static float Hash(int a, int b)
        {
            float s = Mathf.Sin(a * 12.9898f + b * 78.233f + 3.17f) * 43758.5453f;
            return s - Mathf.Floor(s);
        }

        // Signed offset of a point from the nearest street centre line on each axis.
        public static float OffsetX(float x) => Mathf.Repeat(x + Block / 2, Block) - Block / 2;
        public static float OffsetZ(float z) => Mathf.Repeat(z + Block / 2, Block) - Block / 2;
        public static bool OnStreet(Vector3 p) => Mathf.Abs(OffsetX(p.x)) < Half + .6f || Mathf.Abs(OffsetZ(p.z)) < Half + .6f;
        public static float Snap(float v) => Mathf.Round(v / Block) * Block;

        // Downtown sits in the middle of every repeat of the pattern.
        public static float Core(int i, int j)
        {
            int a = Mod(i, Window) - 3, b = Mod(j, Window) - 3;
            return Mathf.Clamp01(1 - Mathf.Sqrt(a * a + b * b) / 3.3f);
        }

        // Right-hand traffic: lane 0 is next to the centre line, lane 1 next to the kerb.
        public static float LaneOffset(int lane) => Lane * (.5f + lane);

        // A lane on the nearest street, pointing the way the car was already heading.
        public static void Recover(Vehicle v)
        {
            Vector3 p = v.transform.position, f = v.transform.forward;
            float ox = OffsetX(p.x), oz = OffsetZ(p.z);
            bool alongZ = Mathf.Abs(ox) <= Mathf.Abs(oz);
            if (alongZ)
            {
                float dir = f.z >= 0 ? 1 : -1;
                v.Place(new Vector3(p.x - ox + dir * LaneOffset(0), .25f, p.z - dir * 8), Quaternion.LookRotation(new Vector3(0, 0, dir)));
            }
            else
            {
                float dir = f.x >= 0 ? 1 : -1;
                v.Place(new Vector3(p.x - dir * 8, .25f, p.z - oz - dir * LaneOffset(0)), Quaternion.LookRotation(new Vector3(dir, 0, 0)));
            }
        }

        // Recover onto the street nearest to a given point.
        public static void RecoverAt(Vehicle v, Vector3 p, Vector3 f)
        {
            float ox = OffsetX(p.x), oz = OffsetZ(p.z);
            bool alongZ = Mathf.Abs(ox) <= Mathf.Abs(oz);
            if (alongZ)
            {
                float dir = f.z >= 0 ? 1 : -1;
                v.Place(new Vector3(p.x - ox + dir * LaneOffset(0), .25f, p.z), Quaternion.LookRotation(new Vector3(0, 0, dir)));
            }
            else
            {
                float dir = f.x >= 0 ? 1 : -1;
                v.Place(new Vector3(p.x, .25f, p.z - oz - dir * LaneOffset(0)), Quaternion.LookRotation(new Vector3(dir, 0, 0)));
            }
        }

        public static void Spawn(Vehicle v) => v.Place(new Vector3(LaneOffset(0), .25f, 30), Quaternion.identity);

        static Transform Box(Transform root, string name, Vector3 centre, Vector3 size, Material m, bool collision = false)
        {
            var t = Art.Box(root, name, centre, size, m, collision);
            return t;
        }

        // block: build the houses between the streets; north / east: build the street leaving the
        // junction that way. The island city's outer ring uses junction-only tiles.
        public static void BuildTile(Transform root, int ti, int tj, bool block = true, bool north = true, bool east = true, int bigPark = 0, bool station = false)
        {
            var steps = BuildTileSteps(root, ti, tj, block, north, east, bigPark, station);
            while (steps.MoveNext()) { }
        }

        // The same, a part at a time (the island city streams its blocks in while the player
        // drives): streets and markings, signals, pavements, street life, then the houses side by side.
        public static IEnumerator<object> BuildTileSteps(Transform root, int ti, int tj, bool block = true, bool north = true, bool east = true, int bigPark = 0, bool station = false)
        {
            int pi = Mod(ti, Window), pj = Mod(tj, Window);
            var rnd = new System.Random(pi * 7919 + pj * 104729 + 17);
            float R(float a, float b) => (float)(a + rnd.NextDouble() * (b - a));
            float B = Block, H = Half;

            // --- carriageways: junction, northbound segment, eastbound segment
            Art.WorldUV(Box(root, "Grid junction", new(0, -.1f, 0), new(2 * H, .2f, 2 * H), Art.Asphalt, true), 5, 1);
            if (north) Art.WorldUV(Box(root, "Grid street NS", new(0, -.1f, B / 2), new(2 * H, .2f, B - 2 * H), Art.Asphalt, true), 5, 1);
            if (east) Art.WorldUV(Box(root, "Grid street EW", new(B / 2, -.1f, 0), new(B - 2 * H, .2f, 2 * H), Art.Asphalt, true), 5, 1);

            // Markings: double centre line, lane dashes, zebras and stop bars.
            float runStart = H + 15, runEnd = B - H - 15, run = runEnd - runStart;
            foreach (float side in new[] {-.16f, .16f})
            {
                if (north) Box(root, "Centre line", new(side, .012f, (runStart + runEnd) / 2), new(.12f, .02f, run), Art.Line);
                if (east) Box(root, "Centre line", new((runStart + runEnd) / 2, .012f, side), new(run, .02f, .12f), Art.Line);
            }
            for (float s = runStart + 3; s < runEnd - 3; s += 12)
                foreach (float lane in new[] {-Lane, Lane})
                {
                    if (north) Box(root, "Lane dash", new(lane, .012f, s), new(.12f, .02f, 5), Art.Line);
                    if (east) Box(root, "Lane dash", new(s, .012f, lane), new(5, .02f, .12f), Art.Line);
                }
            for (int arm = 0; arm < 4; arm++)
            {
                // arm 0: +Z, 1: -Z, 2: +X, 3: -X
                float sign = arm % 2 == 0 ? 1 : -1;
                bool alongZ = arm < 2;
                for (float k = -H + 1; k <= H - 1; k += 1.6f)
                {
                    Vector3 c = alongZ ? new Vector3(k, .013f, sign * 11) : new Vector3(sign * 11, .013f, k);
                    Box(root, "Zebra", c, alongZ ? new Vector3(.8f, .02f, 3.2f) : new Vector3(3.2f, .02f, .8f), Art.Line);
                }
                // Stop bar across the lanes that approach this arm's junction.
                Vector3 bar = alongZ ? new Vector3(-sign * H / 2, .013f, sign * 13.6f) : new Vector3(sign * 13.6f, .013f, sign * H / 2);
                Box(root, "Stop line", bar, alongZ ? new Vector3(H, .02f, .4f) : new Vector3(.4f, .02f, H), Art.Line);
            }
            yield return null;

            // German-style signals: a mast on the right of every approach with an arm
            // over the lanes, one head above each lane and a repeater on the pole.
            for (int k = 0; k < 4; k++)
                Signal(root, k);
            if (!block)
                yield break;
            yield return null;

            // --- the block between the two streets
            float lo = H, hi = B - H, span = hi - lo, mid = B / 2;
            float core = Core(ti, tj);
            float kind = Hash(pi, pj);
            // Only the two big parks (the island city); the endless city keeps its small squares.
            bool park = bigPark > 0 || (!GridWorld.IslandBuilding && kind < .13f && core < .7f);
            bool carPark = !park && !station && kind < .24f && core < .7f;

            // Kerbs, pavements and the ground of the block.
            float w0 = lo + Kerb, w1 = w0 + Walk;
            Kerbside(root, lo, hi, w1);
            Art.WorldUV(Box(root, "Block ground", new(mid, .02f, mid), new(B - 2 * w1, .1f, B - 2 * w1), park || !carPark ? Art.Grass : Art.Concrete, true), 6, 1);

            // Street furniture along the four pavements.
            for (int side = 0; side < 4; side++)
                for (float s = lo + 18; s < hi - 10; s += Pitch)
                {
                    Vector3 edge = side switch
                    {
                        0 => new Vector3(s, 0, w0 + 1.2f),
                        1 => new Vector3(s, 0, B - w0 - 1.2f),
                        2 => new Vector3(w0 + 1.2f, 0, s),
                        _ => new Vector3(B - w0 - 1.2f, 0, s),
                    };
                    // Two lamps for every tree, so the street is properly lit at night.
                    if (((int)((s - lo) / Pitch) + side) % 3 != 1)
                    {
                        Vector3 road = side switch { 0 => Vector3.back, 1 => Vector3.forward, 2 => Vector3.left, _ => Vector3.right };
                        LampPost(root, edge, road);
                        Vector3 along = side < 2 ? Vector3.right : Vector3.forward;
                        LitterBin(root, edge + along * 1.4f);
                    }
                    else
                        Art.Broadleaf(root, edge + new Vector3(0, .14f, 0), R(5.5f, 8f));
                }
            yield return null;

            Details(root, rnd, ti, tj, lo, hi, w0, w1);
            yield return null;

            float m = w1;                  // building line, flush with the pavement
            float length = B - 2 * m;
            if (bigPark > 0)
            {
                BigPark(root, rnd, m, bigPark, ti, tj);
                yield break;
            }
            if (park)
            {
                Box(root, "Park path", new(mid, .08f, mid), new(length, .06f, 3.2f), Art.Shoulder);
                Box(root, "Park path", new(mid, .08f, mid), new(3.2f, .06f, length), Art.Shoulder);
                Box(root, "Pond", new(mid + 40, .08f, mid - 38), new(34, .06f, 24), Art.Glass);
                for (int n = 0; n < 36; n++)
                {
                    Vector3 p = new(R(m + 6, B - m - 6), .07f, R(m + 6, B - m - 6));
                    if (Mathf.Abs(p.x - mid) < 5 || Mathf.Abs(p.z - mid) < 5) continue;
                    if (n % 4 == 0) Art.Pine(root, p, R(9, 15), false);
                    else Art.Broadleaf(root, p, R(7, 11));
                }
                Box(root, "Monument plinth", new(mid, 1.2f, mid), new(3, 2.4f, 3), Art.Concrete, true);
                Box(root, "Monument", new(mid, 4, mid), new(1.2f, 3.2f, 1.2f), Art.Steel);
                yield break;
            }

            if (carPark)
            {
                CarPark(root, rnd, m);
                yield return null;
            }
            // The city's filling station fills the west half of its block, facing the avenue.
            if (station)
            {
                GasStation.Build(root, new Vector3(m, .07f, mid), Quaternion.identity, "НЕФТЬ 67", true);
                // The player's garage, where every run starts, next to the station.
                Garage.Build(root, new Vector3(m, .07f, B - m - 18));
                yield return null;
            }

            // Perimeter block: a closed row of houses along every street, courtyard inside.
            float depth = R(15, 18);
            int nycLeft = core > .5f ? 2 : 0;
            for (int side = 0; side < 4; side++)
            {
                bool alongX = side < 2;
                if (carPark && !alongX) continue;
                if (station && side != 3) continue;
                float from = alongX ? m : m + depth, to = alongX ? B - m : B - m - depth;
                int baseFloors = Mathf.RoundToInt(Mathf.Lerp(R(3, 5), R(9, 16), core));
                float at = from;
                while (to - at > 8)
                {
                    // Now and then a purchased New York style building in the middle of a side.
                    if (nycLeft > 0 && rnd.NextDouble() < .35 && at > from + depth + 2)
                    {
                        float used = NycBuilding(root, rnd, side, at, to - depth - 2 - at, m);
                        if (used > 0) { at += used; nycLeft--; NycPlaced++; continue; }
                    }
                    float width = Mathf.Min(R(14, 26), to - at);
                    if (to - at - width < 12) width = to - at;
                    int floors = Mathf.Max(2, baseFloors + rnd.Next(-1, 3));
                    if (core > .75f && rnd.NextDouble() < .3) floors = Mathf.RoundToInt(R(18, 30));
                    House(root, rnd, side, at + width / 2, width, depth, m, floors);
                    at += width;
                }
                yield return null;
            }

            // Courtyard trees.
            // The island's east side had bare courtyards: more trees there.
            int courtyardTrees = GridWorld.IslandBuilding && ti >= 4 ? 18 : 6;
            if (!carPark && !station)
                for (int n = 0; n < courtyardTrees; n++)
                    Art.Broadleaf(root, new Vector3(R(m + depth + 8, B - m - depth - 8), .07f, R(m + depth + 8, B - m - depth - 8)), R(6, 10));

            // A landmark in one block of every repeat.
            if (pi == 1 && pj == 5)
            {
                Box(root, "Church nave", new(mid, 6, mid), new(22, 12, 36), Art.Facades[0], true);
                Box(root, "Church roof", new(mid, 12.6f, mid), new(23, 1.4f, 37), Art.Dark);
                Box(root, "Church tower", new(mid, 14, mid - 20), new(9, 28, 9), Art.Facades[0], true);
                Box(root, "Spire", new(mid, 32, mid - 20), new(4.2f, 8, 4.2f), Art.Dark);
                Box(root, "Clock face", new(mid, 23, mid - 24.6f), new(2.4f, 2.4f, .3f), Art.White);
            }
        }
    
        // --- big fenced city parks (the island city's "Central Park") ----------------------------
        public static int BigParkBlocks, ParkFences, ParkTrees;
        public static readonly string[] ParkNames = { "", "ЦЕНТРАЛЬНЫЙ ПАРК", "ПАРК ВОСТОЧНЫЙ" };

        // A whole block as a park: iron railings with gates in the middle of each side, paths from
        // the gates to a fountain or a pond, a ring path with benches and lamps, lawns and trees.
        static void BigPark(Transform root, System.Random rnd, float m, int variant, int ti, int tj)
        {
            BigParkBlocks++;
            float R(float a, float b) => (float)(a + rnd.NextDouble() * (b - a));
            float B = Block, mid = B / 2, lo = m + .8f, hi = B - m - .8f, side = hi - lo;
            const float gate = 9;
            bool pond = ((ti * 3 + tj) % 3) == 0;

            // Railings: posts every 2.5 m, two rails, stone gate pillars.
            for (int e = 0; e < 4; e++)
            {
                bool alongX = e < 2;
                float line = e % 2 == 0 ? lo : hi;
                Vector3 P(float along, float y) => alongX ? new Vector3(along, y, line) : new Vector3(line, y, along);
                Vector3 S(float along, float y, float across) => alongX ? new Vector3(along, y, across) : new Vector3(across, y, along);
                for (int half = 0; half < 2; half++)
                {
                    float a = half == 0 ? lo : mid + gate / 2, b = half == 0 ? mid - gate / 2 : hi;
                    // Brick plinth and piers with wrought-iron railing panels.
                    Fences.Park(root, P(a, .05f), P(b, .05f));
                    ParkFences++;
                }
                for (int s = -1; s <= 1; s += 2)
                {
                    Box(root, "Gate pillar", P(mid + s * (gate / 2 + .4f), 1.3f), new Vector3(.8f, 2.6f, .8f), Art.TrimStone, true);
                    Box(root, "Gate cap", P(mid + s * (gate / 2 + .4f), 2.7f), new Vector3(1f, .2f, 1f), Art.TrimStone);
                }
                // Path from the gate to the middle.
                Box(root, "Park path", alongX ? new Vector3(mid, .075f, (line + mid) / 2) : new Vector3((line + mid) / 2, .075f, mid),
                    alongX ? new Vector3(5, .06f, Mathf.Abs(mid - line)) : new Vector3(Mathf.Abs(mid - line), .06f, 5), Art.Shoulder);
            }

            // Ring path with benches and lamps.
            float ring = side * .32f;
            for (int k = 0; k < 28; k++)
            {
                float a0 = k / 28f * Mathf.PI * 2, a1 = (k + 1) / 28f * Mathf.PI * 2, am = (a0 + a1) / 2;
                Vector3 p = new(mid + Mathf.Cos(am) * ring, .08f, mid + Mathf.Sin(am) * ring);
                var seg = Box(root, "Park ring path", p, new Vector3(4, .06f, ring * (a1 - a0) + .6f), Art.Shoulder);
                seg.localRotation = Quaternion.Euler(0, -am * Mathf.Rad2Deg, 0);
                if (k % 4 == 1)
                {
                    Vector3 outward = new(Mathf.Cos(am), 0, Mathf.Sin(am));
                    Bench(root, p + outward * 3.2f, -am * Mathf.Rad2Deg);
                }
                if (k % 4 == 3)
                {
                    Vector3 lamp = p + new Vector3(Mathf.Cos(am), 0, Mathf.Sin(am)) * -3.2f;
                    LampPost(root, lamp, Vector3.zero, true);
                }
            }

            // The middle: a pond with an island of reeds, or a fountain on a paved square.
            if (pond)
            {
                Box(root, "Park pond edge", new Vector3(mid, .06f, mid), new Vector3(side * .42f, .1f, side * .34f), Art.TrimStone);
                Box(root, "Park pond", new Vector3(mid, .1f, mid), new Vector3(side * .4f, .06f, side * .32f), Island.Pond ? Island.Pond : Island.Sea ? Island.Sea : Art.Glass);
                for (int n = 0; n < 5; n++)
                    Art.Bush(root, new Vector3(mid + R(-6, 6), .1f, mid + R(-5, 5)), R(1.2f, 2));
            }
            else
            {
                Box(root, "Park square", new Vector3(mid, .07f, mid), new Vector3(34, .06f, 34), Art.Paving);
                Art.Primitive(root, "Fountain basin", new Vector3(mid, .35f, mid), new Vector3(14, .35f, 14), Art.TrimStone, PrimitiveType.Cylinder, true);
                Art.Primitive(root, "Fountain water", new Vector3(mid, .62f, mid), new Vector3(12.6f, .05f, 12.6f), Island.Pond ? Island.Pond : Island.Sea ? Island.Sea : Art.Glass, PrimitiveType.Cylinder);
                Art.Primitive(root, "Fountain column", new Vector3(mid, 1.8f, mid), new Vector3(1.2f, 1.6f, 1.2f), Art.TrimStone, PrimitiveType.Cylinder);
                Art.Primitive(root, "Fountain bowl", new Vector3(mid, 3.4f, mid), new Vector3(4, .25f, 4), Art.TrimStone, PrimitiveType.Cylinder);
            }

            // Name board by the gate facing the city centre.
            if (variant < ParkNames.Length && tj % 2 == 1)
            {
                var board = Box(root, "Park name board", new Vector3(mid - gate / 2 - 4, 2.2f, lo - 1.4f), new Vector3(6, 1.2f, .15f), Art.SignGreen);
                var text = Art.Text(root, ParkNames[variant], new Vector3(mid - gate / 2 - 4, 2.2f, lo - 1.5f), .22f, Color.white);
                text.transform.localRotation = Quaternion.identity;
                Box(root, "Park name post", new Vector3(mid - gate / 2 - 4, 1, lo - 1.4f), new Vector3(.12f, 2, .12f), Art.Iron);
            }

            // Trees: a row just inside the railings, then clumps on the lawns.
            for (int e = 0; e < 4; e++)
                for (float t = lo + 6; t < hi - 4; t += R(9, 13))
                {
                    if (Mathf.Abs(t - mid) < gate + 3) continue;
                    float inset = R(4, 7);
                    Vector3 p = e switch
                    {
                        0 => new Vector3(t, .07f, lo + inset),
                        1 => new Vector3(t, .07f, hi - inset),
                        2 => new Vector3(lo + inset, .07f, t),
                        _ => new Vector3(hi - inset, .07f, t),
                    };
                    Art.Broadleaf(root, p, R(8, 12));
                    ParkTrees++;
                }
            for (int n = 0; n < 70; n++)
            {
                Vector3 p = new(R(lo + 10, hi - 10), .07f, R(lo + 10, hi - 10));
                float dx = p.x - mid, dz = p.z - mid, r = Mathf.Sqrt(dx * dx + dz * dz);
                if (Mathf.Abs(dx) < 5 || Mathf.Abs(dz) < 5 || Mathf.Abs(r - ring) < 5 || r < side * (pond ? .28f : .16f)) continue;
                if (n % 5 == 0) Art.Pine(root, p, R(9, 15), false);
                else Art.Broadleaf(root, p, R(7, 12));
                ParkTrees++;
                if (n % 6 == 0) Art.Bush(root, p + new Vector3(R(-3, 3), 0, R(-3, 3)), R(1.3f, 2.4f));
            }
        }

        // One signal installation for approach k (0: heading +Z, 1: -Z, 2: +X, 3: -X).
        static void Signal(Transform root, int k)
        {
            Vector3 d = k switch { 0 => Vector3.forward, 1 => Vector3.back, 2 => Vector3.right, _ => Vector3.left };
            Vector3 r = new(d.z, 0, -d.x);
            bool alongZ = k < 2;
            var lamps = alongZ ? Art.SignalA : Art.SignalB;
            Vector3 S(float across, float up, float along) => alongZ ? new Vector3(across, up, along) : new Vector3(along, up, across);

            Vector3 foot = -d * 15.2f + r * (Half + 1.4f);
            Box(root, "Signal mast", foot + Vector3.up * 3.6f, S(.22f, 7.2f, .22f), Art.Steel, true);
            Box(root, "Signal mast base", foot + Vector3.up * .3f, S(.42f, .6f, .42f), Art.Steel);
            float armLength = Half + 1.2f;
            Box(root, "Signal arm", foot - r * (armLength / 2) + Vector3.up * 6.9f, S(armLength, .18f, .18f), Art.Steel);
            Box(root, "Signal arm brace", foot - r * 1.3f + Vector3.up * 6.3f, S(2.6f, .1f, .1f), Art.Steel);

            // Heads above both lanes, and a lower repeater on the mast facing the driver.
            Head(root, -d * 15.2f + r * LaneOffset(0) + Vector3.up * 6.05f, d, S, lamps, 1f);
            Head(root, -d * 15.2f + r * LaneOffset(1) + Vector3.up * 6.05f, d, S, lamps, 1f);
            Head(root, foot - d * .35f + Vector3.up * 3.1f, d, S, lamps, .8f);

            // Push-button box and a street-name plate for a bit of life.
            Box(root, "Signal button", foot - d * .18f + Vector3.up * 1.1f, S(.16f, .26f, .1f), Art.Amber);
        }

        static void Head(Transform root, Vector3 c, Vector3 d, System.Func<float, float, float, Vector3> S, Material[] lamps, float scale)
        {
            float w = .42f * scale, h = 1.2f * scale, t = .32f * scale, lamp = .28f * scale;
            Box(root, "Signal backplate", c + d * (t / 2 - .02f), S(.78f * scale, 1.52f * scale, .04f), Art.Dark);
            Box(root, "Signal head", c, S(w, h, t), Art.Dark);
            for (int i = 0; i < 3; i++)
            {
                float y = (.36f - i * .36f) * scale;
                Vector3 face = c + Vector3.up * y - d * (t / 2 + .015f);
                Box(root, "Signal lamp", face, S(lamp, lamp, .03f), lamps[i]);
                Box(root, "Signal visor", face + Vector3.up * (lamp * .6f) - d * .1f * scale, S(lamp + .06f, .03f, .22f * scale), Art.Dark);
            }
        }

        // A city house on one side of a grid block.
        // The purchased NYC-like buildings (Marcin's set), the lighter variants only.
        static GameObject[] nyc;
        public static int NycPlaced;
        static readonly string[] NycNames = { "building_1_1 Variant", "building_1_2 Variant", "building_2_1 Variant", "building_2_2 Variant", "building_6_1 Variant", "building_6_2 Variant" };
        static float NycBuilding(Transform root, System.Random rnd, int side, float at, float room, float m)
        {
            if (nyc == null)
            {
                var list = new List<GameObject>();
                foreach (var nycName in NycNames)
                {
                    var g = Resources.Load<GameObject>("Env/Buildings/Prefabs/Buildings/" + nycName);
                    if (g) list.Add(g);
                }
                nyc = list.ToArray();
            }
            if (nyc.Length == 0 || room < 25) return 0;
            var prefab = nyc[rnd.Next(nyc.Length)];
            var holder = new GameObject("NYC building").transform;
            holder.SetParent(root, false);
            var b = Object.Instantiate(prefab, holder).transform;
            foreach (var c in b.GetComponentsInChildren<Collider>()) Object.Destroy(c);
            // The pack's windows are 35 % see-through glass over hollow buildings: from the street
            // you looked straight through them. They get the city's own opaque window glass.
            foreach (var r in b.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] && (mats[i].renderQueue >= 2500 || mats[i].name.ToLowerInvariant().Contains("window")))
                    {
                        mats[i] = Art.WinGlass ? Art.WinGlass : Art.Glass;
                        changed = true;
                    }
                if (changed) r.sharedMaterials = mats;
            }
            Bounds bb = default; bool first = true;
            foreach (var r in b.GetComponentsInChildren<Renderer>()) { if (first) { bb = r.bounds; first = false; } else bb.Encapsulate(r.bounds); }
            if (first) { Object.Destroy(holder.gameObject); return 0; }
            float scale = Mathf.Min(1f, room / bb.size.x);
            if (scale < .7f) { Object.Destroy(holder.gameObject); return 0; }
            // In the town-house frame: +X along the street, facade at z = 0, building towards +z.
            b.localScale *= scale;
            b.localPosition = new Vector3(-(bb.center.x - holder.position.x) * scale, -(bb.min.y - holder.position.y) * scale, -(bb.min.z - holder.position.z) * scale);
            float width = bb.size.x * scale;
            float B = Block;
            Vector3 outward = side switch { 0 => Vector3.back, 1 => Vector3.forward, 2 => Vector3.left, _ => Vector3.right };
            float faceLine = side switch { 0 => m, 1 => B - m, 2 => m, _ => B - m };
            bool alongX = side < 2;
            float centre = at + width / 2;
            holder.localPosition = alongX ? new Vector3(centre, 0, faceLine) : new Vector3(faceLine, 0, centre);
            holder.localRotation = Quaternion.LookRotation(-outward);
            // Solid: the pack's colliders are removed above (they were mesh colliders on every
            // part), and without one cars drove straight through the building.
            Bounds solid = default; bool any = false;
            foreach (var r in b.GetComponentsInChildren<Renderer>())
            {
                if (!any) { solid = r.bounds; any = true; } else solid.Encapsulate(r.bounds);
            }
            if (any)
            {
                var hit = new GameObject("NYC building collider");
                hit.transform.SetParent(holder, true);
                hit.transform.SetPositionAndRotation(solid.center, Quaternion.identity);
                hit.AddComponent<BoxCollider>().size = solid.size;
            }
            return width;
        }

        static void House(Transform root, System.Random rnd, int side, float centre, float width, float depth, float m, int floors)
        {
            float B = Block;
            bool alongX = side < 2;
            Vector3 n = side switch { 0 => Vector3.back, 1 => Vector3.forward, 2 => Vector3.left, _ => Vector3.right };
            float faceLine = side switch { 0 => m, 1 => B - m, 2 => m, _ => B - m };
            Vector3 front = alongX ? new Vector3(centre, 0, faceLine) : new Vector3(faceLine, 0, centre);
            Townhouse(root, front, Quaternion.LookRotation(-n), rnd, width, depth, floors, true);
        }

        // Shop names for the fascia boards, with the kind of interior seen through the window
        // (0 shelves, 1 clothes, 2 cafe, 3 salon or office).
        static readonly (string Name, int Kind)[] ShopNames =
        {
            ("ПЕКАРНЯ", 2), ("АПТЕКА", 0), ("КОФЕЙНЯ", 2), ("ПАРИКМАХЕРСКАЯ", 3), ("ЦВЕТЫ", 0), ("КИОСК", 0),
            ("ШАУРМА", 2), ("ОПТИКА", 3), ("КНИГИ", 0), ("МЯСНАЯ ЛАВКА", 0), ("ПРОДУКТЫ 24", 0), ("МОРОЖЕНОЕ", 2),
            ("ОБУВЬ", 1), ("ОДЕЖДА", 1), ("ВИНО И СЫР", 0), ("ТУРАГЕНТСТВО", 3), ("БАНК", 3), ("НАПИТКИ", 0),
            ("ТАБАК", 0), ("БУТИК", 1), ("ПИЦЦЕРИЯ", 2), ("ХОЗТОВАРЫ", 0), ("БИСТРО", 2), ("САЛОН КРАСОТЫ", 3),
        };

        // A German town house built in its own frame: +X runs along the street, the facade
        // is at z = 0 and the building extends to +z. `front` and `rotation` are local to parent.
        public static void Townhouse(Transform parent, Vector3 front, Quaternion rotation, System.Random rnd, float width, float depth, int floors, bool shops)
        {
            float R(float a, float b) => (float)(a + rnd.NextDouble() * (b - a));
            var t = new GameObject("Town house").transform;
            t.SetParent(parent, false);
            t.localPosition = front;
            t.localRotation = rotation;
            Vector3 S(float a, float up, float d) => new(a, up, d);
            Vector3 mid = new(0, 0, depth / 2);

            const float ground = 3f;
            float height = ground + (floors - 1) * 3f;
            var facade = Art.FacadeVariants[rnd.Next(Art.FacadeVariants.Length)];
            bool brick = facade.name.StartsWith("Brick");
            // 0: old town house with stone surrounds and cornices, 1: post-war with roller
            // shutters, 2: brick with white frames and stone lintels.
            int style = brick ? 2 : floors > 9 ? 1 : rnd.NextDouble() < .55 ? 0 : 1;
            bool whiteFrames = style != 1 || rnd.NextDouble() < .5;
            bool shopFront = shops && rnd.NextDouble() < .58;
            float recess = shopFront ? .45f : 0;
            var doorMat = style == 1 ? Art.Doors[3 + rnd.Next(2)] : Art.Doors[rnd.Next(3)];

            // Ground floor in stone (set back behind piers where there are shops), upper floors in the facade.
            var plinth = Box(t, "House ground floor", new Vector3(0, ground / 2, recess + (depth - recess) / 2), S(width, ground, depth - recess), Art.Plinth, true);
            Art.WorldUV(plinth, 6);
            var body = Box(t, "House", mid + Vector3.up * (ground + (height - ground) / 2), S(width - .02f, height - ground, depth - .02f), facade, true);
            Art.WorldUV(body, 12);
            Box(t, "Floor band", new Vector3(0, ground + .1f, .05f), S(width, .35f, .3f), Art.Plinth);

            int bays = Mathf.Max(1, Mathf.RoundToInt(width / 5.5f));
            float bay = width / bays;
            int door = rnd.Next(bays);
            var stone = new MeshKit();                       // socle, steps, piers: always drawn
            var metal = new MeshKit();                       // dark ironwork, shop and ground-floor frames
            var pane = new MeshKit();                        // ground-floor glass
            var zinc = new MeshKit();                        // gutters, down pipes, bell boards
            var doorKit = new MeshKit();
            // Close-up detail: dropped with distance by the tile's LOD group.
            var trim = new MeshKit { SkipBack = true };
            var frame = new MeshKit { SkipBack = true };
            var glass = new[] { new MeshKit { SkipBack = true }, new MeshKit { SkipBack = true }, new MeshKit { SkipBack = true } };
            var litGlass = new MeshKit { SkipBack = true };
            var blinds = new MeshKit { SkipBack = true };
            var iron = new MeshKit { SkipBack = true };
            var pots = new MeshKit { SkipBack = true };
            var flowers = new MeshKit { SkipBack = true };

            // How many windows of this house have the light on after dark: most houses are
            // partly lit, some are dark, so the street reads as lived in.
            float litShare = rnd.NextDouble() < .7 ? R(.35f, .85f) : R(0f, .12f);

            // A real window: glass set back behind the frame, two casements under a fanlight,
            // then per style a stone surround, a lintel or a roller shutter part way down.
            void Window(float x, float yb, float w, float h, bool upper)
            {
                var fk = upper ? frame : metal;
                bool lit = upper && rnd.NextDouble() < litShare;
                var gk = !upper ? pane : lit ? litGlass : glass[rnd.Next(glass.Length)];
                float yc = yb + h / 2, top = yb + h;
                const float fw = .08f;
                gk.Box(new Vector3(x, yc, -.02f), new Vector3(w - .1f, h - .1f, .04f));
                fk.Box(new Vector3(x - w / 2 + fw / 2, yc, -.05f), new Vector3(fw, h, .1f));
                fk.Box(new Vector3(x + w / 2 - fw / 2, yc, -.05f), new Vector3(fw, h, .1f));
                fk.Box(new Vector3(x, top - fw / 2, -.05f), new Vector3(w - 2 * fw, fw, .1f));
                fk.Box(new Vector3(x, yb + fw / 2, -.05f), new Vector3(w - 2 * fw, fw, .1f));
                float fan = top - h * .24f;
                fk.Box(new Vector3(x, fan, -.045f), new Vector3(w - 2 * fw, .06f, .08f));
                fk.Box(new Vector3(x, (yb + fan) / 2, -.045f), new Vector3(.07f, fan - yb - fw, .08f));
                if (style == 1 && rnd.NextDouble() < .55)
                {
                    float share = (float)rnd.NextDouble();
                    share = share < .15f ? 1 : share * .8f;
                    float bh = (h - 2 * fw) * share;
                    blinds.Box(new Vector3(x, top - fw - bh / 2, -.075f), new Vector3(w - 2 * fw, bh, .02f));
                }
                var sill = upper ? trim : stone;
                sill.Box(new Vector3(x, yb - .05f, -.11f), new Vector3(w + .3f, .1f, .22f));
                if (style == 0)
                {
                    var k = upper ? trim : stone;
                    k.Box(new Vector3(x - w / 2 - .11f, yc, -.07f), new Vector3(.22f, h + .1f, .14f));
                    k.Box(new Vector3(x + w / 2 + .11f, yc, -.07f), new Vector3(.22f, h + .1f, .14f));
                    k.Box(new Vector3(x, top + .14f, -.08f), new Vector3(w + .5f, .2f, .16f));
                    k.Box(new Vector3(x, top + .3f, -.12f), new Vector3(w + .8f, .12f, .24f));
                }
                else if (style == 2)
                    (upper ? trim : stone).Box(new Vector3(x, top + .12f, -.05f), new Vector3(w + .4f, .24f, .1f));
                if (!upper) return;
                double roll = rnd.NextDouble();
                if (style == 0 && roll < .16)
                {
                    // French balcony: a low iron railing across the window.
                    iron.Box(new Vector3(x, yb + .95f, -.24f), new Vector3(w + .1f, .05f, .05f));
                    iron.Box(new Vector3(x, yb + .12f, -.24f), new Vector3(w + .1f, .04f, .04f));
                    for (int k = 0; k <= 9; k++)
                        iron.Box(new Vector3(x - w / 2 + w * k / 9f, yb + .53f, -.24f), new Vector3(.025f, .82f, .025f));
                }
                else if (style != 1 && roll < .3)
                {
                    pots.Box(new Vector3(x, yb + .06f, -.34f), new Vector3(w * .75f, .22f, .24f));
                    flowers.Box(new Vector3(x, yb + .24f, -.34f), new Vector3(w * .78f, .2f, .28f));
                }
            }

            if (shopFront)
            {
                var (shopName, kind) = ShopNames[rnd.Next(ShopNames.Length)];
                var inside = Art.ShopInteriors[kind * 2 + rnd.Next(2)];
                // Stone piers carry the house; the shop windows sit back between them under a fascia.
                stone.Box(new Vector3(0, .1f, .22f), new Vector3(width, .2f, .5f));
                for (int i = 0; i <= bays; i++)
                {
                    bool edge = i == 0 || i == bays;
                    float pw = edge ? .7f : .5f;
                    float x = i == 0 ? -width / 2 + pw / 2 : i == bays ? width / 2 - pw / 2 : -width / 2 + bay * i;
                    Box(t, "Shop pier", new Vector3(x, 1.25f, .21f), S(pw, 2.5f, .46f), Art.Plinth);
                    stone.Box(new Vector3(x, .25f, .2f), new Vector3(pw + .06f, .5f, .5f));
                }
                Box(t, "Shop fascia", new Vector3(0, 2.75f, .21f), S(width, .5f, .46f), Art.Plinth);
                void Pane(float l, float r)
                {
                    float w = r - l, c = (l + r) / 2;
                    if (w < .5f) return;
                    stone.Box(new Vector3(c, .25f, .42f), new Vector3(w, .5f, .06f));
                    metal.Box(new Vector3(c, 1.5f, .43f), new Vector3(w, 2f, .04f));
                    Box(t, "Shop window", new Vector3(c, 1.52f, .405f), S(w - .14f, 1.86f, .03f), inside);
                    metal.Box(new Vector3(c, 2.12f, .38f), new Vector3(w - .14f, .05f, .05f));
                    if (w > 2.6f) metal.Box(new Vector3(c, 1.3f, .38f), new Vector3(.06f, 1.6f, .05f));
                }
                for (int i = 0; i < bays; i++)
                {
                    float l = -width / 2 + bay * i + (i == 0 ? .7f : .25f);
                    float r = -width / 2 + bay * (i + 1) - (i == bays - 1 ? .7f : .25f);
                    if (i != door || r - l < 1.6f)
                    {
                        Pane(l, r);
                        continue;
                    }
                    // Glazed shop door with a push bar, side lights either side.
                    float c = (l + r) / 2;
                    metal.Box(new Vector3(c, 1.3f, .43f), new Vector3(1.4f, 2.6f, .04f));
                    Box(t, "Shop door", new Vector3(c, 1.25f, .405f), S(1.16f, 2.2f, .03f), inside);
                    metal.Box(new Vector3(c + .38f, 1.1f, .36f), new Vector3(.04f, .8f, .04f));
                    Pane(l, c - .75f);
                    Pane(c + .75f, r);
                }

                // Name board on the fascia; its letters glow a little at night.
                float signW = Mathf.Min(width - 2f, shopName.Length * .28f + 1.2f);
                float free = (width - signW) / 2 - .8f;
                float sx = free > .5f && rnd.NextDouble() < .5 ? R(-free, free) : 0;
                var board = Art.SignBoards[rnd.Next(Art.SignBoards.Length)];
                Box(t, "Shop sign board", new Vector3(sx, 2.75f, -.05f), S(signW, .44f, .06f), board);
                var text = Art.Text(t, shopName, new Vector3(sx, 2.75f, -.085f), .2f, rnd.NextDouble() < .7 ? new Color(.95f, .95f, .92f) : new Color(1f, .82f, .45f));
                text.name = "Shop sign text";
                if (rnd.NextDouble() < .45)
                {
                    var awning = Art.Awnings[rnd.Next(Art.Awnings.Length)];
                    var a = Box(t, "Awning", new Vector3(0, 2.36f, -.62f), S(width - 1.6f, .06f, 1.3f), awning);
                    a.localRotation = Quaternion.Euler(-16, 0, 0);
                    Box(t, "Awning valance", new Vector3(0, 2.04f, -1.25f), S(width - 1.6f, .28f, .03f), awning);
                }
                if (rnd.NextDouble() < .4)
                {
                    // Hanging sign on an iron bracket.
                    float bx = (rnd.NextDouble() < .5 ? -1 : 1) * (width / 2 - 1.1f);
                    metal.Box(new Vector3(bx, 3.55f, -.45f), new Vector3(.05f, .05f, .9f));
                    metal.Box(new Vector3(bx, 3.3f, -.02f), new Vector3(.08f, .6f, .06f));
                    Box(t, "Hanging sign", new Vector3(bx, 3.2f, -.72f), S(.06f, .6f, .6f), board);
                }
            }
            else
            {
                // Flats on the ground floor: rusticated stone, windows behind iron bars, cellar
                // windows in the socle and a panelled front door up two steps.
                stone.Box(new Vector3(0, .3f, -.07f), new Vector3(width, .6f, .16f));
                for (float y = 1.05f; y < 2.8f; y += .5f)
                    stone.Box(new Vector3(0, y, -.012f), new Vector3(width - 1.1f, .05f, .04f));
                for (int i = 0; i < bays; i++)
                {
                    float c = -width / 2 + bay * (i + .5f);
                    if (i == door)
                    {
                        bool wide = style == 0;
                        float dw = wide ? 1.5f : 1.15f;
                        stone.Box(new Vector3(c, .1f, -.55f), new Vector3(dw + 1f, .2f, .9f));
                        stone.Box(new Vector3(c, .3f, -.35f), new Vector3(dw + .8f, .2f, .5f));
                        doorKit.Box(new Vector3(c, 1.475f, -.03f), new Vector3(dw, 2.15f, .06f));
                        for (int s = -1; s <= 1; s += 2)
                        {
                            float px = c + s * dw / 4;
                            doorKit.Box(new Vector3(px, .95f, -.07f), new Vector3(dw / 2 - .2f, .7f, .03f));
                            pane.Box(new Vector3(px, 1.95f, -.065f), new Vector3(dw / 2 - .22f, .9f, .03f));
                        }
                        metal.Box(new Vector3(c + dw / 2 - .16f, 1.45f, -.09f), new Vector3(.04f, .26f, .05f));
                        pane.Box(new Vector3(c, 2.74f, -.03f), new Vector3(dw, .32f, .03f));
                        metal.Box(new Vector3(c, 2.57f, -.05f), new Vector3(dw + .1f, .06f, .06f));
                        stone.Box(new Vector3(c - dw / 2 - .15f, 1.7f, -.07f), new Vector3(.3f, 2.6f, .14f));
                        stone.Box(new Vector3(c + dw / 2 + .15f, 1.7f, -.07f), new Vector3(.3f, 2.6f, .14f));
                        zinc.Box(new Vector3(c + dw / 2 + .55f, 1.45f, -.03f), new Vector3(.16f, .34f, .05f));
                        Box(t, "House number", new Vector3(c + dw / 2 + .55f, 2.2f, -.02f), S(.24f, .18f, .03f), Art.Blue);
                        metal.Box(new Vector3(c - dw / 2 - .55f, 2.3f, -.07f), new Vector3(.2f, .3f, .1f));
                        Box(t, "Door lamp", new Vector3(c - dw / 2 - .55f, 2.28f, -.13f), S(.12f, .18f, .03f), Art.WinLit);
                        if (style == 1)
                            metal.Box(new Vector3(c, 2.92f, -.55f), new Vector3(dw + .9f, .08f, 1.1f));
                        continue;
                    }
                    float w = Mathf.Min(1.6f, bay - 1.6f);
                    Window(c, .95f, w, 1.5f, false);
                    if (rnd.NextDouble() < .75)
                    {
                        // Window grille: vertical bars and two rails.
                        int bars = Mathf.Max(4, Mathf.RoundToInt(w / .15f));
                        for (int k = 0; k <= bars; k++)
                            metal.Box(new Vector3(c - w / 2 + w * k / bars, 1.7f, -.17f), new Vector3(.035f, 1.56f, .035f));
                        metal.Box(new Vector3(c, 2.25f, -.17f), new Vector3(w + .06f, .04f, .04f));
                        metal.Box(new Vector3(c, 1.15f, -.17f), new Vector3(w + .06f, .04f, .04f));
                    }
                    if (rnd.NextDouble() < .55)
                    {
                        // Cellar window in the socle.
                        metal.Box(new Vector3(c, .33f, -.155f), new Vector3(.9f, .34f, .02f));
                        pane.Box(new Vector3(c, .33f, -.165f), new Vector3(.78f, .24f, .02f));
                        for (int k = 1; k < 4; k++)
                            metal.Box(new Vector3(c - .39f + .78f * k / 4, .33f, -.18f), new Vector3(.025f, .26f, .025f));
                    }
                }
            }

            // Real windows over the painted ones on the street facade (3 m grid, openings
            // 0.40 - 2.32 m above each floor), up to the eighth floor.
            if (shops)
            {
                float x0 = -width / 2 + .01f;
                int detailFloors = Mathf.Min(floors - 1, 8);
                for (int f = 1; f <= detailFloors; f++)
                {
                    float floorY = ground + (f - 1) * 3f;
                    if (style == 0 && f > 1)
                        trim.Box(new Vector3(0, floorY + .06f, -.05f), new Vector3(width - 1f, .16f, .12f));
                    for (float x = x0 + 1.494f; x < width / 2; x += 3f)
                    {
                        if (x - 1f < -width / 2 + .45f || x + 1f > width / 2 - .45f) continue;
                        Window(x, floorY + .4f, 2f, 1.92f, true);
                    }
                }
            }

            // Balconies on some of the residential houses.
            if (shops && floors <= 8 && facade != Art.Facades[0] && rnd.NextDouble() < .45)
            {
                var railMat = rnd.NextDouble() < .5 ? Art.Frame : Art.Plinth;
                for (int f = 1; f < floors; f++)
                    for (int i = 0; i < bays; i += 2)
                    {
                        Vector3 c = new Vector3(-width / 2 + bay * (i + .5f), ground + (f - 1) * 3f + .05f, -.55f);
                        Box(t, "Balcony", c, S(bay * .8f, .16f, 1.1f), Art.Plinth);
                        Box(t, "Balcony rail", c + new Vector3(0, .55f, -.52f), S(bay * .8f, .9f, .05f), railMat);
                        metal.Box(c + new Vector3(0, 1.02f, -.52f), new Vector3(bay * .8f + .04f, .05f, .08f));
                    }
            }

            // Relief so the facade is not a flat box: corner pilasters, a bay window, cornices.
            foreach (int e in new[] {-1, 1})
                Box(t, "Pilaster", new Vector3(e * (width / 2 - .25f), (height + 3) / 2 - 1.5f + .01f, -.08f), S(.5f, height, .3f), Art.Plinth);
            if (shops && floors >= 4 && width > 13 && rnd.NextDouble() < .45)
            {
                float top = ground + (floors - 2) * 3f;
                var erker = Box(t, "Bay window", new Vector3(R(-width / 4, width / 4), (ground + top) / 2 + .5f, -.6f), S(4.2f, top - ground - 1f, 1.2f), facade, true);
                Art.WorldUV(erker, 12);
                Box(t, "Bay window cap", new Vector3(erker.localPosition.x, top + .05f, -.65f), S(4.5f, .25f, 1.35f), Art.Plinth);
            }
            if (style == 0 && shops)
                trim.Box(new Vector3(0, height - .3f, -.06f), new Vector3(width - 1f, .35f, .12f));

            // Down pipe at one end of every house.
            float pipeX = width / 2 - .62f;
            zinc.Box(new Vector3(pipeX, (height + .3f) / 2 + .1f, -.12f), new Vector3(.1f, height + .1f, .1f));
            zinc.Box(new Vector3(pipeX, .12f, -.17f), new Vector3(.13f, .24f, .2f));

            // Cornice and roof.
            Box(t, "Cornice", mid + Vector3.up * (height + .15f), S(width + .1f, .3f, depth + .5f), Art.Plinth);
            bool roofPitched = floors <= 6 && rnd.NextDouble() < .6;
            float pitch = R(3.5f, 5.5f);
            if (roofPitched)
            {
                Gable(t, mid + Vector3.up * (height + .3f), true, width + .1f, depth + .6f, pitch, facade);
                zinc.Box(new Vector3(0, height + .33f, -.38f), new Vector3(width + .1f, .14f, .16f));
                int count = 1 + rnd.Next(2);
                for (int i = 0; i < count; i++)
                {
                    float cx = R(-width / 2 + 1.5f, width / 2 - 1.5f);
                    float cz = depth / 2 + R(-1.2f, 1.2f);
                    float baseY = height + .3f, topY = baseY + pitch + R(.5f, 1f);
                    Box(t, "Chimney", new Vector3(cx, (baseY + topY) / 2, cz), S(.7f, topY - baseY, .6f), Art.ChimneyBrick);
                    stone.Box(new Vector3(cx, topY + .05f, cz), new Vector3(.86f, .12f, .76f));
                }
            }
            else
            {
                Box(t, "Flat roof", mid + Vector3.up * (height + .32f), S(width - .8f, .08f, depth - .8f), Art.RoofGravel);
                Box(t, "Parapet", new Vector3(0, height + .6f, .2f), S(width, .6f, .3f), Art.Plinth);
                Box(t, "Stair house", mid + new Vector3(R(-width / 4, width / 4), height + 1.5f, R(-3, 3)), S(3.2f, 3f, 3.2f), Art.Plinth);
                for (int u = 0; u < 3; u++)
                    Box(t, "Roof unit", mid + new Vector3(R(-width / 2 + 2, width / 2 - 2), height + .9f, R(-depth / 3, depth / 3)), S(1.6f, 1.2f, 1.1f), Art.Steel);
                if (floors > 14)
                {
                    Box(t, "Mast", mid + Vector3.up * (height + 5), new(.35f, 10, .35f), Art.Steel);
                    Box(t, "Aircraft light", mid + Vector3.up * (height + 10.2f), new(.6f, .45f, .6f), Art.Red);
                }
            }

            if (roofPitched && shops)
            {
                int count = Mathf.Max(1, Mathf.FloorToInt(width / 6.5f));
                for (int i = 0; i < count; i++)
                {
                    float x = -width / 2 + width * (i + .5f) / count;
                    Vector3 c = new(x, height + .3f + pitch * .38f + .55f, depth * .2f);
                    Box(t, "Dormer", c, S(1.7f, 1.5f, 1.8f), facade);
                    Box(t, "Dormer window", c + new Vector3(0, 0, -.91f), S(1.1f, .95f, .04f), Art.Glass);
                    frame.Box(c + new Vector3(0, 0, -.93f), new Vector3(1.22f, .08f, .06f) + Vector3.up * 0);
                    frame.Box(c + new Vector3(0, .5f, -.93f), new Vector3(1.22f, .08f, .06f));
                    frame.Box(c + new Vector3(0, -.5f, -.93f), new Vector3(1.22f, .08f, .06f));
                    frame.Box(c + new Vector3(-.59f, 0, -.93f), new Vector3(.08f, 1.05f, .06f));
                    frame.Box(c + new Vector3(.59f, 0, -.93f), new Vector3(.08f, 1.05f, .06f));
                    Box(t, "Dormer roof", c + new Vector3(0, .82f, 0), S(2f, .14f, 2.1f), Art.RoofTile);
                }
            }

            // Lived-in details on the street side: drainpipes down both ends of the facade,
            // outdoor air-conditioning units under some windows and now and then a dish.
            for (int e = -1; e <= 1; e += 2)
            {
                zinc.Box(new Vector3(e * (width / 2 - .22f), height / 2, -.2f), new Vector3(.13f, height, .13f));
                zinc.Box(new Vector3(e * (width / 2 - .22f), height - .1f, -.35f), new Vector3(.3f, .2f, .4f));
            }
            int units = rnd.Next(0, Mathf.Min(5, floors));
            for (int k = 0; k < units; k++)
            {
                float ux = R(-width / 2 + 1.6f, width / 2 - 1.6f);
                float uy = ground + (1 + rnd.Next(0, Mathf.Max(1, floors - 1))) * 3f - 2.35f;
                zinc.Box(new Vector3(ux, uy, -.32f), new Vector3(.82f, .56f, .32f));
                metal.Box(new Vector3(ux, uy - .31f, -.3f), new Vector3(.9f, .05f, .36f));
            }
            if (rnd.NextDouble() < .35 && floors > 2)
            {
                float dx = R(-width / 2 + 1, width / 2 - 1);
                zinc.Box(new Vector3(dx, ground + 3f * (floors - 1) - .6f, -.45f), new Vector3(.7f, .7f, .1f));
                metal.Box(new Vector3(dx, ground + 3f * (floors - 1) - .6f, -.28f), new Vector3(.06f, .06f, .3f));
            }

            stone.Build(t, "House stonework", Art.Socle);
            metal.Build(t, "House ironwork", Art.Frame);
            pane.Build(t, "House ground glass", Art.Glass);
            zinc.Build(t, "House zinc", Art.Steel);
            doorKit.Build(t, "Front door", doorMat);
            trim.Build(t, "Window trim", Art.TrimStone);
            frame.Build(t, "Window frames", whiteFrames ? Art.FrameWhite : Art.FrameDark);
            for (int g = 0; g < glass.Length; g++)
                glass[g].Build(t, "Windows", Art.WinGlasses[g]);
            litGlass.Build(t, "Lit windows", Art.WinLit);
            blinds.Build(t, "Roller shutters", Art.Blind);
            iron.Build(t, "Window rails", Art.Iron);
            pots.Build(t, "Window boxes", Art.Planter);
            flowers.Build(t, "Window flowers", Art.Flowers);
        }

        // Pitched roof over a house: ridge runs along the street.
        static void Gable(Transform root, Vector3 eave, bool alongX, float length, float width, float rise, Material wall)
        {
            float l = length / 2, w = width / 2;
            Vector3 P(float a, float y, float d) => eave + (alongX ? new Vector3(a, y, d) : new Vector3(d, y, a));
            var roofV = new System.Collections.Generic.List<Vector3>();
            var gableV = new System.Collections.Generic.List<Vector3>();
            void Tri(System.Collections.Generic.List<Vector3> list, Vector3 a, Vector3 b, Vector3 c)
            {
                list.Add(a); list.Add(b); list.Add(c);
                list.Add(a); list.Add(c); list.Add(b);
            }
            Vector3 r0 = P(-l, rise, 0), r1 = P(l, rise, 0);
            Vector3 a0 = P(-l, 0, -w), a1 = P(l, 0, -w), b0 = P(-l, 0, w), b1 = P(l, 0, w);
            Tri(roofV, a0, r0, r1); Tri(roofV, a0, r1, a1);
            Tri(roofV, b0, r1, r0); Tri(roofV, b0, b1, r1);
            Tri(gableV, a0, b0, r0);
            Tri(gableV, a1, r1, b1);
            Mesh Build(System.Collections.Generic.List<Vector3> v)
            {
                var t = new int[v.Count];
                var uv = new Vector2[v.Count];
                for (int i = 0; i < t.Length; i++)
                {
                    t[i] = i;
                    uv[i] = new Vector2(v[i].x + v[i].z, v[i].y) / 4;
                }
                return Art.Mesh(v.ToArray(), t, uv);
            }
            Art.MeshObject(root, "Pitched roof", Build(roofV), Art.RoofTile);
            Art.MeshObject(root, "Gable", Build(gableV), wall);
        }
    
        // ---- Parking bays cut into the pavement ------------------------------------------
        const float BayOut = 2.5f;       // bays reach this far from the carriageway edge
        const float Stall = 5.8f;        // length of one parallel-parking stall

        // Point on one of the four kerbsides of a tile: s runs along the street, c is the
        // distance from the street's centre line.
        static Vector3 Side(int side, float s, float c, float y) => side switch
        {
            0 => new Vector3(s, y, c),
            1 => new Vector3(s, y, Block - c),
            2 => new Vector3(c, y, s),
            _ => new Vector3(Block - c, y, s),
        };
        static Vector3 SideSize(int side, float along, float h, float across) => side < 2 ? new Vector3(along, h, across) : new Vector3(across, h, along);
        static float SideYaw(int side) => side switch { 0 => 270, 1 => 90, 2 => 0, _ => 180 };

        // Parking bays between the tree and lamp islands; none near the junctions or at the bus stop.
        static System.Collections.Generic.List<Vector2> Bays(int side, float lo, float hi)
        {
            var list = new System.Collections.Generic.List<Vector2>();
            var posts = new System.Collections.Generic.List<float>();
            for (float s = lo + 18; s < hi - 10; s += Pitch) posts.Add(s);
            for (int k = 0; k < posts.Count; k++)
            {
                float a = posts[k] + 2, b = k + 1 < posts.Count ? posts[k + 1] - 2 : hi - 20;
                if (side == 2 && a < 118 && b > 92)
                {
                    if (92 - a > Stall) list.Add(new Vector2(a, 92));
                    if (b - 118 > Stall) list.Add(new Vector2(118, b));
                    continue;
                }
                if (b - a > Stall) list.Add(new Vector2(a, b));
            }
            return list;
        }

        // Kerbs, pavements and parking bays along the four sides of a block.
        // A street lamp that breaks off its foot when a car hits it fast (its light goes out).
        public static void LampPost(Transform root, Vector3 foot, Vector3 road, bool park = false)
        {
            var holder = Knockable.Begin(root, park ? "Park lamp post" : "Lamp post", foot);
            var head = Art.StreetLamp(holder, Vector3.zero, road, park);
            float h = park ? 4.4f : 7.8f;
            if (head == null)
            {
                Box(holder, "Street lamp", new(0, h / 2, 0), new(.14f, h, .14f), Art.Steel);
                Box(holder, "Street luminaire", new(0, h, 0), new(.7f, .12f, .7f), Art.White);
            }
            var anchor = new GameObject("Lamp anchor").transform;
            anchor.SetParent(root, false);
            anchor.localPosition = foot + (head ?? new Vector3(0, h - .3f, 0));
            Knockable.End(holder, park ? 120 : 260, park ? 22 : 32, true, new Bounds(new Vector3(0, 1.6f, 0), new Vector3(.4f, 3.2f, .4f)), anchor);
        }

        // A green litter bin on a short post: knocked flying at any speed.
        public static void LitterBin(Transform root, Vector3 foot)
        {
            var bin = Knockable.Begin(root, "Litter bin", foot);
            Box(bin, "Bin post", new(0, .45f, -.24f), new(.07f, .9f, .07f), Art.PaintDark);
            Box(bin, "Bin body", new(0, .62f, 0), new(.42f, .72f, .42f), Art.BinGreen);
            Box(bin, "Bin rim", new(0, 1.0f, 0), new(.46f, .05f, .46f), Art.PaintDark);
            Box(bin, "Bin slot", new(0, .88f, .215f), new(.26f, .1f, .02f), Art.SignBlack);
            Knockable.End(bin, 25);
        }

        // A bench of wooden slats on painted iron legs.
        public static void Bench(Transform root, Vector3 foot, float yaw, float length = 2)
        {
            var b = Knockable.Begin(root, "Bench", foot, yaw);
            for (int k = 0; k < 3; k++)
                Box(b, "Bench slat", new(-.2f + k * .19f, .45f, 0), new(.16f, .05f, length), Art.Wood);
            for (int k = 0; k < 2; k++)
                Box(b, "Bench back slat", new(.3f, .68f + k * .17f, 0), new(.04f, .12f, length), Art.Wood).localRotation = Quaternion.Euler(0, 0, -12);
            foreach (float z in new[] { -length / 2 + .2f, length / 2 - .2f })
            {
                Box(b, "Bench leg", new(0, .22f, z), new(.55f, .44f, .06f), Art.PaintDark);
                Box(b, "Bench back leg", new(.29f, .7f, z), new(.05f, .5f, .06f), Art.PaintDark);
            }
            Knockable.End(b, 45);
        }

        static void Kerbside(Transform root, float lo, float hi, float w1)
        {
            float inner = lo + BayOut;
            var kerb = new MeshKit();
            var fill = new MeshKit();
            var paving = new MeshKit();
            var marks = new MeshKit();
            for (int side = 0; side < 4; side++)
            {
                // The walkway behind the bays.
                var walk = Box(root, "Pavement", Side(side, (lo + hi) / 2, (inner + .3f + w1) / 2, .07f), SideSize(side, hi - lo - 1, .14f, w1 - inner - .3f), Art.Concrete, true);
                Art.WorldUV(walk, 4, 1);
                var bays = Bays(side, lo, hi);
                bays.Add(new Vector2(hi, hi));
                float at = lo;
                foreach (var bay in bays)
                {
                    // Raised island (street trees, lamps, corners) up to the next bay.
                    float a = at, b = bay.x;
                    if (b - a > .01f)
                    {
                        kerb.Box(Side(side, (a + b) / 2, lo + .25f, .075f), SideSize(side, b - a, .3f, .5f));
                        fill.Box(Side(side, (a + b) / 2, (lo + .5f + inner + .3f) / 2, .07f), SideSize(side, b - a, .14f, inner + .3f - lo - .5f));
                        if (a > lo) kerb.Box(Side(side, a + .15f, (lo + inner) / 2, .075f), SideSize(side, .3f, .3f, inner - lo));
                        if (b < hi) kerb.Box(Side(side, b - .15f, (lo + inner) / 2, .075f), SideSize(side, .3f, .3f, inner - lo));
                    }
                    if (bay.x >= hi) break;
                    // The bay: paved at road level, a flush granite edge, a kerb to the walkway
                    // and a white line between the stalls.
                    float l = bay.y - bay.x, m = (bay.x + bay.y) / 2;
                    paving.Box(Side(side, m, (lo + inner) / 2, -.04f), SideSize(side, l, .1f, inner - lo));
                    kerb.Box(Side(side, m, lo + .15f, 0), SideSize(side, l, .04f, .3f));
                    kerb.Box(Side(side, m, inner + .15f, .075f), SideSize(side, l, .3f, .3f));
                    int n = Mathf.FloorToInt(l / Stall);
                    float start = m - n * Stall / 2;
                    for (int i = 0; i <= n; i++)
                        marks.Box(Side(side, start + i * Stall, (lo + .3f + inner) / 2, .017f), SideSize(side, .12f, .02f, inner - lo - .3f));
                    at = bay.y;
                }
            }
            kerb.Build(root, "Kerbs", Art.Kerb, true);
            fill.Build(root, "Kerb islands", Art.Concrete, true);
            paving.Build(root, "Parking bays", Art.Paving, true);
            marks.Build(root, "Parking marks", Art.Line);
        }

        // Cars in the bays, blue P signs and ticket machines on the islands.
        static void Parking(Transform root, System.Random rnd, int ti, int tj, float lo, float hi)
        {
            float R(float a, float b) => (float)(a + rnd.NextDouble() * (b - a));
            float inner = lo + BayOut;
            // About half the bays that used to be taken (still the same seed → same cars).
            float fullness = Mathf.Lerp(.11f, .225f, Core(ti, tj));
            for (int side = 0; side < 4; side++)
            {
                float yaw = SideYaw(side);
                foreach (var bay in Bays(side, lo, hi))
                {
                    float l = bay.y - bay.x, m = (bay.x + bay.y) / 2;
                    int n = Mathf.FloorToInt(l / Stall);
                    float start = m - n * Stall / 2;
                    for (int i = 0; i < n; i++)
                        if (rnd.NextDouble() < fullness)
                            ParkedCar(root, Side(side, start + (i + .5f) * Stall + R(-.35f, .35f), (lo + inner) / 2 + R(-.06f, .12f), .01f), yaw + R(-1.2f, 1.2f), rnd);
                    if (rnd.NextDouble() < .55)
                        ParkingSign(root, Side(side, bay.x - .7f, inner - .35f, 0), yaw);
                    if (rnd.NextDouble() < .45)
                        TicketMachine(root, Side(side, bay.y + .9f, inner - .6f, .14f), yaw);
                }
            }
        }

        // Blue "P" sign on a post, facing the kerb-lane traffic.
        public static void ParkingSign(Transform root, Vector3 foot, float yaw, float height = 2.3f)
        {
            var h = Knockable.Begin(root, "Parking sign", foot, yaw);
            Box(h, "Sign post", new(0, height / 2 + .1f, .03f), new(.07f, height + .2f, .07f), Art.Steel);
            Vector3 c = new(0, height, 0);
            Box(h, "Parking plate", c, new(.6f, .6f, .04f), Art.Blue);
            Box(h, "P", c + new Vector3(-.1f, 0, -.03f), new(.08f, .4f, .02f), Art.SignWhite);
            Box(h, "P", c + new Vector3(0, .17f, -.03f), new(.2f, .06f, .02f), Art.SignWhite);
            Box(h, "P", c + new Vector3(0, 0, -.03f), new(.2f, .06f, .02f), Art.SignWhite);
            Box(h, "P", c + new Vector3(.1f, .085f, -.03f), new(.06f, .2f, .02f), Art.SignWhite);
            Knockable.End(h, 20);
        }

        // Parking ticket machine: heavy, it only gives way to a fast car.
        public static void TicketMachine(Transform root, Vector3 foot, float yaw)
        {
            var h = Knockable.Begin(root, "Ticket machine", foot, yaw);
            Box(h, "Machine body", new(0, .72f, 0), new(.45f, 1.44f, .32f), Art.PaintGrey);
            Box(h, "Machine top", new(0, 1.5f, 0), new(.47f, .14f, .34f), Art.Blue);
            Box(h, "Machine screen", new(0, 1.12f, -.165f), new(.26f, .18f, .02f), Art.Glass);
            Box(h, "Machine slot", new(.1f, .9f, -.165f), new(.08f, .12f, .02f), Art.SignBlack);
            Knockable.End(h, 120, 18, true);
        }

        // The car park block: two aisles with nose-in stalls, a planted median with lamps.
        static void CarPark(Transform root, System.Random rnd, float m)
        {
            float R(float a, float b) => (float)(a + rnd.NextDouble() * (b - a));
            float B = Block, mid = B / 2, length = B - 2 * m;
            float x0 = m + 30, x1 = B - m - 30;
            Art.WorldUV(Box(root, "Car park", new(mid, .07f, mid), new(length - 50, .06f, length - 50), Art.Asphalt), 5, 1);
            var lines = new MeshKit();
            const float width = 2.6f, deep = 5f;
            foreach (int aisle in new[] { -1, 1 })
            {
                float az = mid + aisle * 16;
                foreach (int row in new[] { -1, 1 })
                {
                    float rz = az + row * (3.25f + deep / 2);
                    for (float x = x0; x <= x1 + .01f; x += width)
                        lines.Box(new Vector3(x, .11f, rz), new Vector3(.1f, .02f, deep));
                    lines.Box(new Vector3((x0 + x1) / 2, .11f, rz + row * deep / 2), new Vector3(x1 - x0, .02f, .1f));
                    for (float x = x0 + width / 2; x < x1; x += width)
                        if (rnd.NextDouble() < .25)
                            ParkedCar(root, new Vector3(x + R(-.15f, .15f), .1f, rz + R(-.2f, .2f)), (row > 0 ? 0 : 180) + R(-2, 2), rnd);
                }
                // Direction arrows down the aisle.
                for (float x = x0 + 12; x < x1 - 6; x += 30)
                    lines.Box(new Vector3(x, .11f, az), new Vector3(3f, .02f, .15f));
            }
            lines.Build(root, "Car park lines", Art.Line);
            // Planted median between the two aisles: kerb, grass, trees and lamps.
            Box(root, "Car park median", new(mid, .15f, mid), new(x1 - x0, .15f, 7.4f), Art.Kerb, true);
            Box(root, "Car park median grass", new(mid, .23f, mid), new(x1 - x0 - .4f, .02f, 7f), Art.Grass);
            Art.RoadSign(root, new Vector3(x0 + 1, .24f, mid + 2.5f), Vector3.left, "Disabled_Sign", 2.3f);
            Art.RoadSign(root, new Vector3(x1 - 1, .24f, mid - 2.5f), Vector3.right, "No_Parking_Sign", 2.3f);
            for (float x = x0 + 8; x < x1 - 4; x += 16)
            {
                if (((int)((x - x0) / 16)) % 2 == 0)
                {
                    var lamp = Knockable.Begin(root, "Car park lamp post", new(x, .24f, mid));
                    Box(lamp, "Car park lamp", new(0, 4, 0), new(.14f, 8, .14f), Art.Steel);
                    Box(lamp, "Car park luminaire", new(0, 8, 0), new(1.2f, .12f, .5f), Art.White);
                    var anchor = new GameObject("Lamp anchor").transform;
                    anchor.SetParent(root, false);
                    anchor.localPosition = new Vector3(x, 7.9f, mid);
                    Knockable.End(lamp, 220, 30, true, new Bounds(new Vector3(0, 1.6f, 0), new Vector3(.4f, 3.2f, .4f)), anchor);
                }
                else
                    Art.Broadleaf(root, new Vector3(x, .24f, mid), R(6, 8.5f));
            }
            // P sign and ticket machine at the entrance side.
            ParkingSign(root, new(x0 - 3, .07f, mid - 4.5f), 90, 3.1f);
            TicketMachine(root, new(x0 - 3, .07f, mid + 4.5f), 90);
        }

        static readonly BodyStyle[] OldParked = { BodyStyle.Hatchback, BodyStyle.Estate, BodyStyle.Sedan, BodyStyle.Suv, BodyStyle.Coupe, BodyStyle.Van };
        public static readonly BodyStyle[] ParkedStyles = { BodyStyle.StyleSedan, BodyStyle.StyleCar, BodyStyle.StyleSport, BodyStyle.StyleJeep, BodyStyle.StyleSedan, BodyStyle.StyleCar };

        // Street life: cars parked half on the pavement, a bus stop, lane arrows and signs.
        static void Details(Transform root, System.Random rnd, int ti, int tj, float lo, float hi, float w0, float w1)
        {
            float B = Block, H = Half;

            Parking(root, rnd, ti, tj, lo, hi);

            // Bus stop with shelter on the east pavement of the north-south street.
            {
                float s = 105, x = w1;
                Box(root, "Shelter back", new(x - .35f, 1.25f, s), new(.06f, 2.2f, 4.2f), Art.Glass);
                Box(root, "Shelter side", new(x - 1f, 1.25f, s - 2.1f), new(1.3f, 2.2f, .06f), Art.Glass);
                Box(root, "Shelter side", new(x - 1f, 1.25f, s + 2.1f), new(1.3f, 2.2f, .06f), Art.Glass);
                Box(root, "Shelter roof", new(x - 1f, 2.42f, s), new(1.7f, .1f, 4.5f), Art.Steel);
                Box(root, "Shelter frame", new(x - 1.8f, 1.2f, s - 2.15f), new(.08f, 2.4f, .08f), Art.Steel);
                Box(root, "Shelter frame", new(x - 1.8f, 1.2f, s + 2.15f), new(.08f, 2.4f, .08f), Art.Steel);
                Box(root, "Bench", new(x - .75f, .48f, s), new(.45f, .08f, 2.8f), Art.Wood);
                Box(root, "Timetable", new(x - .4f, 1.5f, s + 1.2f), new(.04f, .9f, .7f), Art.SignWhite);
                var stop = Knockable.Begin(root, "Bus stop sign", new(w0 + .5f, .14f, s + 3.2f));
                Box(stop, "Bus stop pole", new(0, 1.26f, 0), new(.08f, 2.8f, .08f), Art.Steel);
                Box(stop, "Bus stop sign", new(0, 2.51f, 0), new(.04f, .52f, .52f), Art.SignYellow);
                Box(stop, "Bus stop sign ring", new(-.02f, 2.51f, 0), new(.04f, .56f, .56f), Art.SignGreen);
                Box(stop, "Bus stop letter", new(.04f, 2.51f, -.08f), new(.02f, .3f, .05f), Art.SignGreen);
                Box(stop, "Bus stop letter", new(.04f, 2.51f, .08f), new(.02f, .3f, .05f), Art.SignGreen);
                Box(stop, "Bus stop letter", new(.04f, 2.51f, 0), new(.02f, .05f, .16f), Art.SignGreen);
                Knockable.End(stop, 25);
                // Bay marking on the road.
                Box(root, "Bus bay line", new(H - .25f, .012f, s), new(.15f, .02f, 22), Art.SignYellow);
                Box(root, "Bus bay zigzag", new(H - 1.4f, .012f, s), new(.12f, .02f, 22), Art.SignYellow);
            }

            // Street life: an advertising pillar on one corner, bollards at every corner,
            // planters along the pavements and manhole covers in the road.
            {
                var kit = new MeshKit();
                var posters = Art.Awnings[rnd.Next(Art.Awnings.Length)];
                Vector3 pillar = new(w0 + 1.6f, 0, w0 + 1.6f);
                var column = Art.Primitive(root, "Advertising pillar", pillar + Vector3.up * 1.6f, new(1.25f, 1.6f, 1.25f), posters, PrimitiveType.Cylinder, true);
                Art.Primitive(root, "Pillar cap", pillar + Vector3.up * 3.3f, new(1.45f, .12f, 1.45f), Art.BinGreen, PrimitiveType.Cylinder);
                Art.Primitive(root, "Pillar dome", pillar + Vector3.up * 3.45f, new(.9f, .3f, .9f), Art.BinGreen, PrimitiveType.Sphere);
                Art.Primitive(root, "Pillar foot", pillar + Vector3.up * .15f, new(1.4f, .15f, 1.4f), Art.BinGreen, PrimitiveType.Cylinder);
                foreach (var corner in new[] { new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(-1, -1) })
                    for (int k = 0; k < 3; k++)
                    {
                        float along = 17 + k * 1.6f, across = H + .9f;
                        // On the island's outer streets the corner lies on the waterfront road
                        // (the bollards stood in the carriageway there): only inside the city.
                        foreach (var at in new[] { new Vector3(corner.x * across, .55f, corner.y * along), new Vector3(corner.x * along, .55f, corner.y * across) })
                            if (!GridWorld.IslandBuilding || Island.InCity(root.TransformPoint(at)))
                                kit.Box(at, new Vector3(.16f, .8f, .16f));
                    }
                kit.Build(root, "Bollards", Art.Frame);

                var planters = new MeshKit();
                for (int side = 0; side < 4; side++)
                    for (float sp = lo + 50; sp < hi - 20; sp += 64)
                    {
                        if (rnd.NextDouble() < .5) continue;
                        Vector3 p = side switch
                        {
                            0 => new Vector3(sp, 0, w1 - 1.2f),
                            1 => new Vector3(sp, 0, B - w1 + 1.2f),
                            2 => new Vector3(w1 - 1.2f, 0, sp),
                            _ => new Vector3(B - w1 + 1.2f, 0, sp),
                        };
                        bool alongX = side < 2;
                        planters.Box(p + Vector3.up * .35f, alongX ? new Vector3(2.4f, .7f, .8f) : new Vector3(.8f, .7f, 2.4f));
                        Art.Bush(root, p + Vector3.up * .7f, .9f);
                    }
                planters.Build(root, "Planters", Art.Socle);

                var covers = new MeshKit();
                for (float sp = 40; sp < B - 30; sp += 55)
                {
                    covers.Box(new Vector3(LaneOffset(0) - .6f, -.003f + .012f, sp), new Vector3(.7f, .012f, .7f));
                    covers.Box(new Vector3(sp, .009f, -LaneOffset(1) + .8f), new Vector3(.7f, .012f, .7f));
                }
                covers.Build(root, "Manhole covers", Art.Dark);
            }

            // Approaches owned by this tile: the north-south arm and the east-west arm, both ways.
            // dir -1 runs towards this tile's junction, dir +1 towards the next one.
            foreach (bool alongZ in new[] {true, false})
                foreach (int dir in new[] {-1, 1})
                {
                    Vector3 d = alongZ ? new Vector3(0, 0, dir) : new Vector3(dir, 0, 0);
                    Vector3 r = new(d.z, 0, -d.x);
                    Vector3 junction = dir < 0 ? Vector3.zero : alongZ ? new Vector3(0, 0, B) : new Vector3(B, 0, 0);
                    // Lane arrows: inner lane straight + left, kerb lane straight + right.
                    for (int lane = 0; lane < 2; lane++)
                    {
                        Vector3 c = junction - d * 29 + r * LaneOffset(lane) + Vector3.up * .013f;
                        Arrow(root, c, d, lane == 0 ? -1 : 1);
                    }
                    // Speed limit 50 on the right, well before the junction.
                    SpeedSign(root, junction - d * 50 + r * (H + BayOut + .75f), d);
                }
        }

        internal static void ParkedCar(Transform root, Vector3 p, float yaw, System.Random rnd)
        {
            var parked = new GameObject("Parked car").transform;
            parked.SetParent(root, false);
            parked.localPosition = p;
            parked.gameObject.layer = 10;
            BodyShape shape;
            if (QaFlags.Has("oldparked"))
            {
                shape = BodyShape.Of(OldParked[rnd.Next(OldParked.Length)]);
                Bodywork.Build(parked, shape, Art.CarPaints[rnd.Next(Art.CarPaints.Length)], Art.Red, false);
                for (int w = 0; w < 4; w++)
                {
                    var hub = new GameObject("Parked wheel").transform;
                    hub.SetParent(parked, false);
                    hub.localPosition = new Vector3((w % 2 == 0 ? -1 : 1) * shape.Track / 2, shape.WheelRadius, (w < 2 ? 1 : -1) * shape.Wheelbase / 2);
                    Bodywork.Wheel(hub, shape.WheelRadius, w % 2 == 0 ? -1 : 1, false, shape);
                }
            }
            else
                shape = Bodywork.StaticCar(parked, ParkedStyles[rnd.Next(ParkedStyles.Length)]);
            parked.localRotation = Quaternion.Euler(0, yaw, 0);
            var collider = parked.gameObject.AddComponent<BoxCollider>();
            collider.center = new Vector3(0, .7f, 0);
            collider.size = new Vector3(shape.Width, 1.3f, shape.Length);
            // Its own object, not part of the block's merged mesh: it can be knocked about.
            ParkedCarBody.Hold(parked);
        }

        static Transform Mark(Transform root, Vector3 centre, Quaternion rot, float u, float v, float width, float length, float angle)
        {
            var t = Box(root, "Lane arrow", centre + rot * new Vector3(u, 0, v), new(width, .02f, length), Art.Line);
            t.localRotation = rot * Quaternion.Euler(0, angle, 0);
            return t;
        }

        // Painted arrow pointing along d, with a branch to the left (-1) or right (+1).
        static void Arrow(Transform root, Vector3 c, Vector3 d, int branch)
        {
            var rot = Quaternion.LookRotation(d);
            Mark(root, c, rot, 0, -.4f, .16f, 3.4f, 0);
            Mark(root, c, rot, -.2f, 1.25f, .14f, .8f, 35);
            Mark(root, c, rot, .2f, 1.25f, .14f, .8f, -35);
            // Branch: a short bent stroke with its own head.
            float b = branch;
            Mark(root, c, rot, b * .38f, .15f, .14f, 1f, b * 50);
            Mark(root, c, rot, b * .78f, .62f, .13f, .6f, b * 50 - 35 * b + 70 * b * 0);
            Mark(root, c, rot, b * .86f, .36f, .13f, .6f, b * 50 + 45 * b);
        }

        // Round "50" limit sign on a post, facing drivers heading along d.
        static void SpeedSign(Transform root, Vector3 foot, Vector3 d)
        {
            var holder = Knockable.Begin(root, "Speed limit sign", foot);
            root = holder;
            foot = Vector3.zero;
            Box(root, "Sign post", foot + Vector3.up * 1.3f, new(.07f, 2.6f, .07f), Art.Steel);
            var face = Quaternion.LookRotation(-d) * Quaternion.Euler(90, 0, 0);
            Vector3 c = foot + Vector3.up * 2.35f - d * .05f;
            var ring = Art.Primitive(root, "Speed sign", c, new(.64f, .012f, .64f), Art.SignRed, PrimitiveType.Cylinder);
            ring.localRotation = face;
            var inner = Art.Primitive(root, "Speed sign face", c - d * .016f, new(.48f, .012f, .48f), Art.SignWhite, PrimitiveType.Cylinder);
            inner.localRotation = face;
            // "50" in seven-segment strokes, as the driver sees it (their right is r).
            Vector3 r = new(d.z, 0, -d.x);
            Vector3 f = c - d * .03f;
            void Seg(float x, float y, float w, float h) =>
                Box(root, "Sign digit", f + r * x + Vector3.up * y, new Vector3(Mathf.Abs(r.x) * w + Mathf.Abs(d.x) * .01f, h, Mathf.Abs(r.z) * w + Mathf.Abs(d.z) * .01f), Art.SignBlack);
            float t = .035f, hh = .12f, ww = .09f;
            // 5
            float x5 = -.07f;
            Seg(x5, hh, ww, t); Seg(x5 - ww / 2, hh / 2, t, hh); Seg(x5, 0, ww, t); Seg(x5 + ww / 2, -hh / 2, t, hh); Seg(x5, -hh, ww, t);
            // 0
            float x0 = .07f;
            Seg(x0, hh, ww, t); Seg(x0, -hh, ww, t); Seg(x0 - ww / 2, hh / 2, t, hh); Seg(x0 - ww / 2, -hh / 2, t, hh); Seg(x0 + ww / 2, hh / 2, t, hh); Seg(x0 + ww / 2, -hh / 2, t, hh);
            Knockable.End(holder, 20);
        }
    }
}

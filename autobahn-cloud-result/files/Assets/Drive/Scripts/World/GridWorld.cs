using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // Keeps a Window x Window patch of city tiles around the car. Tiles that fall behind
    // jump a whole window ahead; the pattern repeats every Window tiles, so a moved tile
    // is already the right one for its new place and nothing has to be rebuilt.
    // The island's city is a fixed set of blocks instead, streamed in and out round the player
    // like the rest of the island (WorldStreamer).
    public sealed class GridWorld : MonoBehaviour
    {
        public static GridWorld Active { get; private set; }
        // The island's city: a fixed patch of the same tiles, never recycled.
        public static GridWorld IslandCity { get; private set; }
        // Set while the island city's tiles are being built.
        public static bool IslandBuilding { get; private set; }
        bool bounded;

        sealed class Tile
        {
            public Transform Root;
            public int I, J;
            public Rigidbody Body;
            // Island city: its streaming unit, its trees in the instanced forest, and whether its
            // trees were counted (a block built again is not counted again).
            public StreamUnit Unit;
            public int Forest = -1;
            public bool Counted;
        }

        readonly List<Tile> tiles = new();
        Transform player, far;
        public int TileCount => tiles.Count;

        public void Build(Transform follow)
        {
            Active = this;
            player = follow;
            var saved = Random.state;
            Random.InitState(4711);
            int half = GridCity.Window / 2;
            for (int a = 0; a < GridCity.Window; a++)
                for (int b = 0; b < GridCity.Window; b++)
                {
                    int i = a - half, j = b - half;
                    var root = new GameObject($"Grid tile {GridCity.Mod(i, GridCity.Window)},{GridCity.Mod(j, GridCity.Window)}").transform;
                    root.SetParent(transform, false);
                    root.position = new Vector3(i * GridCity.Block, 0, j * GridCity.Block);
                    GridCity.BuildTile(root, i, j);
                    Art.Combine(root);
                    // The merged meshes carry the look; drop the source pieces that have no collider.
                    Art.Strip(root);
                    // One kinematic body per tile: its hundreds of colliders then move as a single
                    // compound when the tile is recycled, instead of re-inserting each static collider.
                    var body = root.gameObject.AddComponent<Rigidbody>();
                    body.isKinematic = true;
                    body.interpolation = RigidbodyInterpolation.None;
                    tiles.Add(new Tile{Root = root, I = i, J = j, Body = body});
                }
            Random.state = saved;

            // Ground beyond the patch, so the horizon never shows the void.
            far = Art.Box(transform, "Grid far ground", Vector3.down * .35f, new Vector3(9000, .2f, 9000), Art.Grass).transform;
            Recycle();
        }

        public static int CityTrees;

        // The island city: every block is a streaming unit, built when the player comes near and
        // destroyed again once far away. The block with the filling station and the garage (where
        // every run starts) always stands.
        public void BuildIsland(Transform follow)
        {
            bounded = true;
            IslandCity = this;
            player = follow;
            var streamer = WorldStreamer.Active;
            for (int j = 0; j <= Island.CityTilesZ; j++)
                for (int i = 0; i <= Island.CityTilesX; i++)
                {
                    bool inside = i < Island.CityTilesX && j < Island.CityTilesZ;
                    bool north = j < Island.CityTilesZ, east = i < Island.CityTilesX;
                    // Pattern index: downtown lands in the middle of the city.
                    // Two big fenced parks, and no others: the central one in the middle of downtown
                    // and a neighbourhood park among the houses of the east side (well away from the port).
                    int park = inside && i == 3 && (j == 1 || j == 2) ? 1 : inside && i == 5 && (j == 2 || j == 3) ? 2 : 0;
                    // The filling station: the block right by the start, on the avenue.
                    bool station = inside && i == 0 && j == 2;
                    var tile = new Tile{I = i, J = j};
                    var at = new Vector3(i * GridCity.Block, 0, j * GridCity.Block);
                    // The junction's signals stand 15 m out from the corner; the block reaches the next junction.
                    var area = Rect.MinMaxRect(at.x - 22, at.z - 22, at.x + GridCity.Block + 2, at.z + GridCity.Block + 2);
                    tile.Unit = new StreamUnit($"City tile {i},{j}", transform, at, area, root => CityTile(tile, root, inside, north, east, park, station))
                    {
                        Keep = station,
                        Unloading = root => DropTile(tile),
                    };
                    tiles.Add(tile);
                    if (streamer) streamer.Add(tile.Unit);
                    else
                    {
                        // No streaming (should not happen): build it now, as before.
                        var root = new GameObject(tile.Unit.Name).transform;
                        root.SetParent(transform, false);
                        root.position = at;
                        var steps = CityTile(tile, root, inside, north, east, park, station);
                        while (steps.MoveNext()) { }
                    }
                }
            var saved = Random.state;
            Random.InitState(4711);
            EastWoods();
            Random.state = saved;
        }

        // One block of the island city, a part per frame. Built from its own seed (GridCity), so
        // it is the same every time it comes back.
        IEnumerator<object> CityTile(Tile t, Transform root, bool inside, bool north, bool east, int park, bool station)
        {
            t.Root = root;
            // The city's trees go to the instanced forest (they were merged meshes of the
            // detailed model: several million triangles in view from a street).
            var plants = new List<ForestRenderer.Plant>();
            var steps = WorldStreamer.Scoped(GridCity.BuildTileSteps(root, t.I, t.J + 1, inside, north, east, park, station),
                () => { Foliage.Capture = plants; IslandBuilding = true; },
                () => { Foliage.Capture = null; IslandBuilding = false; });
            while (steps.MoveNext()) yield return null;
            if (plants.Count > 0)
                t.Forest = ForestRenderer.Ensure(transform).AddTile(root.position + new Vector3(GridCity.Block / 2, 0, GridCity.Block / 2), plants);
            if (!t.Counted)
            {
                CityTrees += plants.Count;
                t.Counted = true;
            }
            yield return null;
            // Parked cars merge their own model and stay out of the block's mesh; one switched
            // back on while the block was still being built would be merged into it (see
            // ParkedCarBody.Hold): they sit out the merge.
            var parked = root.GetComponentsInChildren<ParkedCarBody>(true);
            var wasOn = new bool[parked.Length];
            for (int k = 0; k < parked.Length; k++)
            {
                wasOn[k] = parked[k].gameObject.activeSelf;
                parked[k].gameObject.SetActive(false);
            }
            Art.Combine(root);
            Art.Strip(root);
            for (int k = 0; k < parked.Length; k++)
                if (wasOn[k] && parked[k]) parked[k].gameObject.SetActive(true);
            AddAnchors(root);
        }

        void DropTile(Tile t)
        {
            if (t.Forest >= 0) ForestRenderer.Active?.RemoveTile(t.Forest);
            t.Forest = -1;
            t.Root = null;
        }

        // A belt of mixed woodland along the city's east edge (it was an empty field): clear of
        // the island road, the airport and the harbour, drawn by the instanced forest.
        public static int EastTrees, WestTrees;
        void EastWoods()
        {
            EastTrees = EdgeWoods(Island.CityW + GridCity.Half + 16, Island.CityW + 260, 3131, true);
            // The seaward (west) side was bare grass down to the beach: a coastal wood of pines
            // and oaks, thinning out towards the sand.
            WestTrees = EdgeWoods(-GridCity.Half - 16, -320, 4242, false);
            Debug.Log($"AUTOBAHN edge woods: east {EastTrees}, west {WestTrees} trees");
        }

        // A belt of mixed woodland along one side of the city, from `inner` (next to the city
        // street) out to `outer`: clear of the island road, the airport, the harbour and the beach.
        int EdgeWoods(float inner, float outer, int seed, bool east)
        {
            var rnd = new System.Random(seed);
            float R(float a, float b) => (float)(a + rnd.NextDouble() * (b - a));
            var kinds = east
                ? new[] { Foliage.Kind.Oak, Foliage.Kind.Birch, Foliage.Kind.Maple, Foliage.Kind.Pine, Foliage.Kind.Spruce, Foliage.Kind.Oak }
                : new[] { Foliage.Kind.Pine, Foliage.Kind.Pine, Foliage.Kind.Oak, Foliage.Kind.Birch, Foliage.Kind.Pine, Foliage.Kind.Spruce };
            int count = 0;
            float span = Mathf.Abs(outer - inner), dir = Mathf.Sign(outer - inner);
            for (float z0 = -20; z0 < Island.CityH + 20; z0 += 200)
            {
                var plants = new List<ForestRenderer.Plant>();
                for (float z = z0; z < z0 + 200; z += 9)
                    for (float d = 0; d < span; d += 9)
                    {
                        float x = inner + dir * d;
                        var p = new Vector3(x + R(-3.5f, 3.5f), 0, z + R(-3.5f, 3.5f));
                        // Thinner towards the outside, a few clearings.
                        if (rnd.NextDouble() < d / (span * 2f) + .12f) continue;
                        if (Island.InAirport(p) || p.z < Island.QuayZ + 30) continue;
                        Vector3 road = Route.Center(Route.Locate(p));
                        if (new Vector2(road.x - p.x, road.z - p.z).magnitude < 16) continue;
                        float y = Island.GroundHeight(p.x, p.z);
                        // Not on the beach or in the water.
                        if (y < Island.SeaY + (east ? .6f : 1.6f)) continue;
                        p.y = IslandWorld.MeshHeight(p.x, p.z);
                        var kind = kinds[rnd.Next(kinds.Length)];
                        float size = kind == Foliage.Kind.Pine || kind == Foliage.Kind.Spruce ? R(10, 17) : R(7, 13);
                        plants.Add(new ForestRenderer.Plant { Position = p, Kind = kind, Size = size });
                    }
                if (plants.Count == 0) continue;
                ForestRenderer.Ensure(transform).AddTile(new Vector3((inner + outer) / 2, 0, z0 + 100), plants);
                count += plants.Count;
            }
            return count;
        }

        // Profiling: how long recycling and the night lights take, and how often tiles move.
        public static float RecycleMs, LightMs;
        public static int Moves;

        public void Recycle()
        {
            if (player == null)
                return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { RecycleBody(); } finally { RecycleMs += (float)clock.Elapsed.TotalMilliseconds; }
        }

        void RecycleBody()
        {
            if (bounded) return;
            int half = GridCity.Window / 2;
            // (Around the car: the endless city has no aircraft, and a parked car must keep its street.)
            Vector3 focus = player.position;
            int pi = Mathf.FloorToInt(focus.x / GridCity.Block + .5f);
            int pj = Mathf.FloorToInt(focus.z / GridCity.Block + .5f);
            foreach (var t in tiles)
            {
                int i = t.I, j = t.J;
                while (i < pi - half) i += GridCity.Window;
                while (i > pi + half) i -= GridCity.Window;
                while (j < pj - half) j += GridCity.Window;
                while (j > pj + half) j -= GridCity.Window;
                if (i == t.I && j == t.J)
                    continue;
                t.I = i;
                t.J = j;
                Moves++;
                // A whole row of seven tiles used to jump in one frame every block (a hitch: every
                // tile carries hundreds of colliders). Tiles that land at the far edge now move one
                // per frame; only a tile needed close by (after a teleport) moves at once.
                bool urgent = Mathf.Abs(i - pi) < half && Mathf.Abs(j - pj) < half;
                if (urgent) Place(t);
                else if (queued.Add(t)) toPlace.Enqueue(t);
            }
            if (toPlace.Count > 0)
            {
                var next = toPlace.Dequeue();
                queued.Remove(next);
                Place(next);
            }

            if (far)
                far.position = new Vector3(GridCity.Snap(focus.x), -.35f, GridCity.Snap(focus.z));
        }

        readonly Queue<Tile> toPlace = new();
        readonly HashSet<Tile> queued = new();

        static void Place(Tile t)
        {
            var p = new Vector3(t.I * GridCity.Block, 0, t.J * GridCity.Block);
            if (t.Root.position == p) return;
            t.Root.position = p;
            if (t.Body) t.Body.position = p;
        }

        void Update()
        {
            if (!bounded) Recycle();
            StreetLights();
        }

        // At night a small pool of real lights follows the lamps nearest the camera.
        readonly List<Transform> anchors = new();
        readonly HashSet<Transform> anchorSet = new();
        readonly List<Light> pool = new();
        float nextLights, poolReach;

        // The lamps of a block that has just been built (the island city streams its blocks);
        // those of unloaded blocks drop out.
        void AddAnchors(Transform root)
        {
            if (pool.Count == 0) return;      // the first night-light pass collects everything
            anchors.RemoveAll(a => !a);
            anchorSet.RemoveWhere(a => !a);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == "Lamp anchor" && anchorSet.Add(t)) anchors.Add(t);
        }

        void StreetLights()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { StreetLightsBody(); } finally { LightMs += (float)clock.Elapsed.TotalMilliseconds; }
        }

        // Night street lights. A pool of real lights is shared by the lamps round a smoothed
        // point just ahead of the camera. The brightness of every light depends only on how far
        // its lamp is from that point (full inside Inner, nothing at Outer), so a light that is
        // handed to another lamp is already dark when it moves: no lamp ever jumps or blinks,
        // however the camera turns (the old choice followed the camera's facing and flickered).
        const int PoolSize = 96;
        // Real lights in use: all of the pool, 16 on Mobile (the rest stay dark; the lamp heads
        // still glow).
        static int Lit => GraphicsQuality.Mobile ? 16 : PoolSize;
        const float LampIntensity = 55, LampRange = 22, Inner = 62, Outer = 112;
        Transform[] lampOf;
        float[] level;
        Vector3 lightCentre;
        bool centreSet;
        readonly HashSet<Transform> wanted = new();
        readonly Queue<Transform> pending = new();
        readonly List<(float d, Transform t)> near = new();
        static readonly System.Comparison<(float d, Transform t)> byDistance = (a, b) => a.d.CompareTo(b.d);

        float Fade(Vector3 p)
        {
            float d = new Vector2(p.x - lightCentre.x, p.z - lightCentre.z).magnitude;
            return 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(Inner, Outer, d));
        }

        void StreetLightsBody()
        {
            bool night = Atmosphere.Night;
            if (pool.Count == 0)
            {
                foreach (var t in GetComponentsInChildren<Transform>(true))
                    if (t.name == "Lamp anchor" && anchorSet.Add(t)) anchors.Add(t);
                for (int i = 0; i < PoolSize; i++)
                {
                    var l = new GameObject("Street light").AddComponent<Light>();
                    l.transform.SetParent(transform, false);
                    l.type = LightType.Point;
                    l.range = LampRange;
                    l.intensity = 0;
                    l.color = new Color(1, .80f, .52f);
                    l.shadows = LightShadows.None;
                    l.enabled = false;
                    pool.Add(l);
                }
                lampOf = new Transform[PoolSize];
                level = new float[PoolSize];
            }
            var cam = Camera.main ? Camera.main.transform : player;
            if (cam != null)
            {
                Vector3 f = cam.forward; f.y = 0;
                Vector3 target = cam.position + (f.sqrMagnitude > .01f ? f.normalized : Vector3.zero) * 18;
                // A jump (teleport, new run) moves the centre at once; otherwise it glides.
                if (!centreSet || (target - lightCentre).sqrMagnitude > 150 * 150) { lightCentre = target; centreSet = true; }
                else lightCentre = Vector3.Lerp(lightCentre, target, 1 - Mathf.Exp(-Time.deltaTime * 1.5f));
            }
            if (Time.time >= nextLights)
            {
                nextLights = Time.time + .25f;
                wanted.Clear();
                pending.Clear();
                if (night && cam != null)
                {
                    near.Clear();
                    foreach (var a in anchors)
                    {
                        if (!a) continue;
                        Vector3 p = a.position;
                        float d = new Vector2(p.x - lightCentre.x, p.z - lightCentre.z).sqrMagnitude;
                        if (d < Outer * Outer) near.Add((d, a));
                    }
                    int lit = Lit;
                    if (near.Count > lit) near.Sort(byDistance);
                    for (int i = 0; i < lit && i < near.Count; i++)
                        wanted.Add(near[i].t);
                    poolReach = Outer;
                    foreach (var a in wanted)
                        if (System.Array.IndexOf(lampOf, a) < 0) pending.Enqueue(a);
                }
            }
            float step = Time.deltaTime * 3f;
            for (int i = 0; i < pool.Count; i++)
            {
                bool keep = lampOf[i] && wanted.Contains(lampOf[i]);
                // A lamp whose block was unloaded meanwhile is skipped.
                while (pending.Count > 0 && !pending.Peek()) pending.Dequeue();
                if (!keep && level[i] <= 0 && pending.Count > 0)
                {
                    lampOf[i] = pending.Dequeue();
                    pool[i].transform.position = lampOf[i].position;
                    keep = true;
                }
                level[i] = Mathf.MoveTowards(level[i], keep && night ? 1 : 0, step);
                float fade = lampOf[i] ? Fade(lampOf[i].position) : 0;
                float value = LampIntensity * level[i] * fade;
                pool[i].intensity = value;
                pool[i].enabled = value > .01f;
            }
        }

        public void Clear()
        {
            foreach (var t in tiles)
            {
                if (t.Unit != null && WorldStreamer.Active) WorldStreamer.Active.Remove(t.Unit);
                else if (t.Root) Destroy(t.Root.gameObject);
            }
            tiles.Clear();
            if (far) Destroy(far.gameObject);
            if (Active == this) Active = null;
            if (IslandCity == this) IslandCity = null;
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
            if (IslandCity == this) IslandCity = null;
        }
    }
}

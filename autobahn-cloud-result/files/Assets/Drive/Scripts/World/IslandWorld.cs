using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // Builds the island around the loop road: ground tiles (grass, sand, rock, snow) with
    // their trees, the sea, the city grid with its harbour, the small town at the top and
    // rocks off the coast. Ground tiles, city blocks and the town are streamed: built when the
    // player comes near and destroyed when far away (WorldStreamer). The sea, the harbour, the
    // airport with its aircraft, the carrier and the boats always stay.
    public sealed class IslandWorld : MonoBehaviour
    {
        public static IslandWorld Active { get; private set; }
        const float Tile = 200, Res = 8;
        const float X0 = -1000, Z0 = -600;
        const int TilesX = 16, TilesZ = 31;

        // A ground tile of the island: one with land, or with sea bed above the dark floor.
        sealed class Ground
        {
            public int X, Z;
            public StreamUnit Unit;
            public int Forest = -1;
            // The hill boulders standing on it, placed once for the whole island.
            public readonly List<(int Model, Vector3 At, Vector3 Scale, Quaternion Turn)> Boulders = new();
        }

        readonly List<Ground> grounds = new();
        Ground[,] slots;
        StreamUnit town;
        public int TileCount => grounds.Count;
        public MeshRenderer SeaRenderer { get; private set; }
        // Trees on the whole island, counted from the same seeds without building it (the tiles
        // themselves come and go); and on the sea side of the road: east coast (forest) and west
        // coast (open).
        public int TreeCount { get { Census(); return treeCount; } }
        public int EastCoastTrees { get { Census(); return eastCoast; } }
        public int WestCoastTrees { get { Census(); return westCoast; } }
        int treeCount = -1, eastCoast, westCoast;
        public GridWorld City { get; private set; }
        // The town while it stands (null when the player is far from it).
        public Transform TownRoot => town?.Root;

        public void Build(Transform follow)
        {
            Active = this;
            var streamer = WorldStreamer.Active ? WorldStreamer.Active : WorldStreamer.Ensure(gameObject, follow);
            Island.Materials();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var saved = Random.state;
            Random.InitState(1957);

            // The sea: a flat grid whose UVs are world metres, so the water shader's ripples keep
            // their size everywhere; finer cells near the island for the gentle swell.
            {
                const int N = 48;
                const float size = 40000;
                var v = new Vector3[(N + 1) * (N + 1)];
                var uv = new Vector2[v.Length];
                var tris = new int[N * N * 6];
                for (int j = 0, k = 0; j <= N; j++)
                    for (int i = 0; i <= N; i++, k++)
                    {
                        float fx = i / (float)N * 2 - 1, fz = j / (float)N * 2 - 1;
                        // Denser towards the middle: cube the offset.
                        float x = 700 + Mathf.Sign(fx) * Mathf.Pow(Mathf.Abs(fx), 2.2f) * size / 2;
                        float z = 2400 + Mathf.Sign(fz) * Mathf.Pow(Mathf.Abs(fz), 2.2f) * size / 2;
                        v[k] = new Vector3(x, Island.SeaY, z);
                        uv[k] = new Vector2(x, z);
                    }
                for (int j = 0, t = 0; j < N; j++)
                    for (int i = 0; i < N; i++)
                    {
                        int a0 = j * (N + 1) + i;
                        tris[t++] = a0; tris[t++] = a0 + N + 1; tris[t++] = a0 + 1;
                        tris[t++] = a0 + 1; tris[t++] = a0 + N + 1; tris[t++] = a0 + N + 2;
                    }
                var mesh = new Mesh { name = "Sea", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.vertices = v; mesh.uv = uv; mesh.triangles = tris;
                mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
                var sea = new GameObject("Sea");
                sea.transform.SetParent(transform, false);
                sea.AddComponent<MeshFilter>().sharedMesh = mesh;
                var sr = sea.AddComponent<MeshRenderer>();
                SeaRenderer = sr;
                sr.sharedMaterial = Island.Sea;
                sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                // Underneath, a dark floor so the water always has something to be deep over.
                // The same ground shader as the sea bed of the tiles, level with its deepest part:
                // no seam where the tiles end.
                var floor = Art.Box(transform, "Sea floor", new Vector3(700, -31.2f, 2400), new Vector3(40000, .2f, 40000), Island.Ground ? Island.Ground : Art.Material("Sea floor", new Color(.02f, .05f, .07f), 0, 0));
                floor.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            long ground = clock.ElapsedMilliseconds;

            // Which tiles exist at all; they are built when the player comes near.
            slots = new Ground[TilesX, TilesZ];
            for (int iz = 0; iz < TilesZ; iz++)
                for (int ix = 0; ix < TilesX; ix++)
                {
                    if (!HasGround(ix, iz)) continue;
                    var g = new Ground { X = ix, Z = iz };
                    float ox = X0 + ix * Tile, oz = Z0 + iz * Tile;
                    g.Unit = new StreamUnit($"Island {ix},{iz}", transform, new Vector3(ox, 0, oz), new Rect(ox, oz, Tile, Tile), root => GroundTile(g, root))
                    {
                        Unloading = root => DropGround(g),
                    };
                    slots[ix, iz] = g;
                    grounds.Add(g);
                    streamer.Add(g.Unit);
                }
            ground = clock.ElapsedMilliseconds - ground;

            long city = clock.ElapsedMilliseconds;
            var cityObject = new GameObject("City").AddComponent<GridWorld>();
            cityObject.transform.SetParent(transform, false);
            cityObject.BuildIsland(follow);
            City = cityObject;
            CityEdges(cityObject.transform);
            city = clock.ElapsedMilliseconds - city;

            Harbour();
            Airport();
            // The town along the road at the top: its houses reach about 30 m off the road, its
            // name boards stand 40 m beyond the last houses.
            var townLine = new List<Vector3>();
            for (float s = Island.TownS - Island.TownHalf - 60; s <= Island.TownS + Island.TownHalf + 60; s += 20)
                townLine.Add(Route.Center(s));
            town = streamer.Add(new StreamUnit("Northern town", transform, Vector3.zero, WorldStreamer.AreaOf(townLine, 40), Town));
            Rocks();
            Random.state = saved;
            Debug.Log($"AUTOBAHN island: {grounds.Count} ground tiles to stream (found in {ground} ms), city of {City.TileCount} blocks set up in {city} ms, total {clock.ElapsedMilliseconds} ms");
            StartCoroutine(TakeMapPicture());
        }

        // The map picture (minimap, big map) once the world stands, while the title is up. It
        // needs the whole island built once: it is kept on disk for this build of the game.
        System.Collections.IEnumerator TakeMapPicture()
        {
            yield return null;
            yield return new WaitForSecondsRealtime(.8f);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (IslandSnapshot.LoadCached(1536))
            {
                Debug.Log($"AUTOBAHN map picture from the cache in {clock.ElapsedMilliseconds} ms");
                yield break;
            }
            IslandSnapshot.Render(1536);
            IslandSnapshot.SaveCached(1536);
            Debug.Log($"AUTOBAHN map picture in {clock.ElapsedMilliseconds} ms");
        }

        // --- ground ----------------------------------------------------------------------------
        // The height of the drawn ground: the tiles are an 8 m grid of GroundHeight samples, cut
        // into triangles the same way, so things planted on it neither float nor sink where the
        // true ground curves between samples (tree trunks hung in the air on the embankments).
        public static float MeshHeight(float x, float z)
        {
            float gx = (x - X0) / Res, gz = (z - Z0) / Res;
            float i = Mathf.Floor(gx), j = Mathf.Floor(gz);
            float fx = gx - i, fz = gz - j;
            float x0 = X0 + i * Res, z0 = Z0 + j * Res;
            float h00 = Island.GroundHeight(x0, z0), h10 = Island.GroundHeight(x0 + Res, z0);
            float h01 = Island.GroundHeight(x0, z0 + Res), h11 = Island.GroundHeight(x0 + Res, z0 + Res);
            return fx + fz <= 1
                ? h00 + fx * (h10 - h00) + fz * (h01 - h00)
                : h11 + (1 - fx) * (h01 - h11) + (1 - fz) * (h10 - h11);
        }

        // Sea tiles too while their bed is above the dark floor: the bed then runs on smoothly
        // instead of ending in a straight edge seen through the water.
        static bool HasGround(int ix, int iz)
        {
            float ox = X0 + ix * Tile, oz = Z0 + iz * Tile;
            int n = Mathf.RoundToInt(Tile / Res) + 1;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    if (Island.GroundHeight(ox + i * Res, oz + j * Res) > -30.5f) return true;
            return false;
        }

        // One ground tile: the ground mesh and its collider, then its trees and boulders.
        IEnumerator<object> GroundTile(Ground g, Transform root)
        {
            float ox = X0 + g.X * Tile, oz = Z0 + g.Z * Tile;
            int n = Mathf.RoundToInt(Tile / Res) + 1;
            var v = new Vector3[n * n];
            var uv = new Vector2[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float x = ox + i * Res, z = oz + j * Res;
                    float h = Island.GroundHeight(x, z);
                    v[j * n + i] = new Vector3(i * Res, h, j * Res);
                    uv[j * n + i] = new Vector2(x / 32, z / 32);
                }

            var go = new GameObject("Island ground");
            go.layer = 10;
            go.transform.SetParent(root, false);
            Mesh mesh;
            if (Island.Ground)
            {
                // One mesh, one material: the surfaces are blended in the shader by the weights
                // in the vertex colours (grass, forest floor, rock, sand).
                var tris = new int[(n - 1) * (n - 1) * 6];
                for (int j = 0, t = 0; j < n - 1; j++)
                    for (int i = 0; i < n - 1; i++)
                    {
                        int a = j * n + i, b = a + n;
                        tris[t++] = a; tris[t++] = b; tris[t++] = a + 1;
                        tris[t++] = a + 1; tris[t++] = b; tris[t++] = b + 1;
                    }
                mesh = new Mesh { name = "Island ground" };
                mesh.vertices = v;
                mesh.uv = uv;
                mesh.triangles = tris;
                mesh.RecalculateNormals();
                var normals = mesh.normals;
                var colors = new Color32[v.Length];
                for (int k = 0; k < v.Length; k++)
                    colors[k] = SurfaceWeights(ox + v[k].x, oz + v[k].z, v[k].y, normals[k].y);
                mesh.colors32 = colors;
                mesh.RecalculateBounds();
                var r = go.AddComponent<MeshRenderer>();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                r.sharedMaterial = Island.Ground;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            else
            {
                // Fallback (shader unavailable): one sub-mesh per surface, as before.
                var lists = new[] { new List<int>(), new List<int>(), new List<int>() };
                void Tri(int a, int b, int c)
                {
                    Vector3 pa = v[a], pb = v[b], pc = v[c];
                    float h = (pa.y + pb.y + pc.y) / 3;
                    float up = Vector3.Cross(pb - pa, pc - pa).normalized.y;
                    int k = h > 118 || up < .72f ? 2 : h < Island.SeaY + 1.4f ? 1 : 0;
                    lists[k].Add(a); lists[k].Add(b); lists[k].Add(c);
                }
                for (int j = 0; j < n - 1; j++)
                    for (int i = 0; i < n - 1; i++)
                    {
                        int a = j * n + i, b = a + n;
                        Tri(a, b, a + 1);
                        Tri(a + 1, b, b + 1);
                    }
                var mats = new List<Material>();
                var used = new List<List<int>>();
                Material[] all = { Art.Grass, Island.Sand, Island.Rock };
                for (int k = 0; k < 3; k++)
                    if (lists[k].Count > 0) { mats.Add(all[k]); used.Add(lists[k]); }
                mesh = new Mesh { name = "Island ground" };
                mesh.vertices = v;
                mesh.uv = uv;
                mesh.subMeshCount = used.Count;
                for (int k = 0; k < used.Count; k++) mesh.SetTriangles(used[k], k);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mats.ToArray();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            yield return null;

            // The forest on this tile: positions only; ForestRenderer draws them instanced.
            var plants = new List<ForestRenderer.Plant>();
            Plants(g.X, g.Z, plants, out _, out _);
            g.Forest = ForestRenderer.Ensure(transform).AddTile(new Vector3(ox + Tile / 2, 0, oz + Tile / 2), plants);
            // Hang the boulders on their ground tile, so they come and go with the ground under them.
            foreach (var (model, at, scale, turn) in g.Boulders)
            {
                var (mesh1, skin) = Island.Boulders[model];
                var b = Art.MeshObject(root, "Hill boulder", mesh1, skin, false).transform;
                b.localScale = scale;
                b.position = at;
                b.rotation = turn;
            }
        }

        void DropGround(Ground g)
        {
            if (g.Forest >= 0) ForestRenderer.Active?.RemoveTile(g.Forest);
            g.Forest = -1;
        }

        // Surface weights for a ground vertex: grass, forest floor, rock, sand (the shader blends).
        static Color32 SurfaceWeights(float x, float z, float h, float up)
        {
            float noise = Mathf.PerlinNoise(x * .031f + 7.7f, z * .031f + 1.3f) - .5f;
            float head = Island.Headland(x, z);
            // Sand on the beaches and the sea bed; not on the rocky headlands above the water.
            float sand = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(Island.SeaY + 1.0f, Island.SeaY + 2.8f + noise * 1.6f, h));
            if (h > Island.SeaY - .5f) sand *= 1 - head * .85f;
            // Rock on steep slopes, the high mountains and the headland cliffs.
            float steep = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.78f, .6f, up));
            float high = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(105, 165, h + noise * 45));
            float cliff = h < Island.SeaY + 9 && h > Island.SeaY - 1 ? head * Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.97f, .8f, up)) : 0;
            float rock = Mathf.Max(steep, Mathf.Max(high, cliff));
            float forest = Island.Forest(x, z, h);
            if (Island.KeepClear(x, z, h) && h > Island.SeaY + 2.3f) forest *= .35f;
            float dirt = Mathf.Clamp01(forest * .95f + noise * .3f);
            // Footpaths winding through the woods and hills.
            float trail = Island.Trail(x, z, h) * .8f;
            float rest = 1 - sand;
            float wr = rest * rock, rest2 = rest * (1 - rock);
            float wd = rest2 * dirt, wg = rest2 * (1 - dirt);
            // A path is bare sandy earth.
            wg *= 1 - trail; wd *= 1 - trail; wr *= 1 - trail; sand = sand * (1 - trail) + trail;
            return new Color32((byte)(wg * 255), (byte)(wd * 255), (byte)(wr * 255), (byte)(sand * 255));
        }

        // The trees of a tile, from the tile's own seed: the same every time it is built.
        // `east` / `west`: how many stand between the road and the sea on either coast.
        static void Plants(int ix, int iz, List<ForestRenderer.Plant> plants, out int east, out int west)
        {
            east = west = 0;
            float ox = X0 + ix * Tile, oz = Z0 + iz * Tile;
            var rnd = new System.Random(ix * 7919 + iz * 104729);
            float R(float a, float b) => (float)(a + rnd.NextDouble() * (b - a));
            int attempts = GraphicsQuality.Level == 0 ? 380 : 640;
            for (int n = 0; n < attempts; n++)
            {
                float x = ox + R(0, Tile), z = oz + R(0, Tile);
                float h = MeshHeight(x, z);
                float density = Island.Forest(x, z, h);
                if (Island.Trail(x, z, h) > .2f) continue;
                // A few lone trees on the meadows too.
                density = Mathf.Max(density, h > Island.SeaY + 3 && h < 90 ? .05f : 0);
                if (rnd.NextDouble() > density) continue;
                if (Island.KeepClear(x, z, h)) continue;
                float slope = Mathf.Abs(Island.GroundHeight(x + 3, z) - h) + Mathf.Abs(Island.GroundHeight(x, z + 3) - h);
                if (slope > 3.4f) continue;
                float size = R(9, 19) * Mathf.Lerp(1, .75f, Mathf.InverseLerp(60, 140, h));
                bool conifer = (x + z * 3) % 7 > 2.6f || h > 55;
                Foliage.Kind kind;
                if (conifer) kind = n % 4 == 0 ? Foliage.Kind.Pine : Foliage.Kind.Spruce;
                else
                {
                    float pick = Mathf.Abs(Mathf.Sin(x * 3.71f + z * 9.13f));
                    kind = pick < .18f ? Foliage.Kind.Maple : pick < .45f ? Foliage.Kind.Birch : Foliage.Kind.Oak;
                }
                plants.Add(new ForestRenderer.Plant { Position = new Vector3(x, h - .1f, z), Kind = kind, Size = size });
                float lat = z > Island.RouteStartZ ? RouteSide(x, z) : -1;
                if (lat < 0 && z > Island.RouteStartZ + 300 && z < Island.TopZ - 400)
                {
                    if (x > Island.CityW) east++;
                    else if (x < 0) west++;
                }
            }
        }

        // The island's trees counted without building it (the automated checks ask).
        void Census()
        {
            if (treeCount >= 0) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int trees = 0, east = 0, west = 0;
            var plants = new List<ForestRenderer.Plant>();
            foreach (var g in grounds)
            {
                plants.Clear();
                Plants(g.X, g.Z, plants, out int e, out int w);
                trees += plants.Count;
                east += e;
                west += w;
            }
            treeCount = trees;
            eastCoast = east;
            westCoast = west;
            Debug.Log($"AUTOBAHN island census: {trees} trees on {grounds.Count} ground tiles in {clock.ElapsedMilliseconds} ms");
        }

        static float RouteSide(float x, float z) => Island.RoadDistance(x, z) < 140 ? Route.Lateral(new Vector3(x, 0, z)) : SideFar(x, z);
        static float SideFar(float x, float z) => x > 0 && x < Island.CityW && z < Island.TopZ ? 1 : -1;

        // --- city edges: pavements and lamps round the outside of the grid -----------------------
        void CityEdges(Transform city)
        {
            float H = GridCity.Half;
            void Walk(Vector3 centre, Vector3 size)
            {
                Art.WorldUV(Art.Box(city, "City edge pavement", centre, size, Art.Kerb, true), 4, 1);
            }
            Walk(new Vector3(-H - 3.2f, .06f, Island.CityH / 2 + 4), new Vector3(6, .3f, Island.CityH + 8));
            Walk(new Vector3(Island.CityW + H + 3.2f, .06f, Island.CityH / 2 + 4), new Vector3(6, .3f, Island.CityH + 8));
            // North pavement, open at the airport gate.
            float gate0 = Island.AirGateX - 22, gate1 = Island.AirGateX + 22, from = H + 2, to = Island.CityW - H - 2;
            Walk(new Vector3((from + gate0) / 2, .06f, Island.CityH + H + 3.2f), new Vector3(gate0 - from, .3f, 6));
            Walk(new Vector3((gate1 + to) / 2, .06f, Island.CityH + H + 3.2f), new Vector3(to - gate1, .3f, 6));
            Art.WorldUV(Art.Box(city, "Airport drive", new Vector3(Island.AirGateX, .01f, Island.CityH + H + 3.2f), new Vector3(gate1 - gate0, .04f, 6.2f), Art.Asphalt, true), 5, 1);
            // Ramps down from the outer pavement to the ground outside the city (half a metre
            // lower): a car that left the city can drive back up anywhere instead of hitting a step.
            const float rampW = 5, top = .21f, low = -.32f;
            float tilt = Mathf.Atan2(top - low, rampW) * Mathf.Rad2Deg;
            float midY = (top + low) / 2 - .15f;
            Transform Ramp(Vector3 centre, Vector3 size, Quaternion turn)
            {
                var r = Art.Box(city, "City edge ramp", centre, size, Art.Kerb, true);
                r.localRotation = turn;
                Art.WorldUV(r, 4, 1);
                return r;
            }
            Ramp(new Vector3(-H - 6.2f - rampW / 2, midY, Island.CityH / 2 + 4), new Vector3(rampW + .2f, .3f, Island.CityH + 8), Quaternion.Euler(0, 0, tilt));
            Ramp(new Vector3(Island.CityW + H + 6.2f + rampW / 2, midY, Island.CityH / 2 + 4), new Vector3(rampW + .2f, .3f, Island.CityH + 8), Quaternion.Euler(0, 0, -tilt));
            Ramp(new Vector3((from + gate0) / 2, midY, Island.CityH + H + 6.2f + rampW / 2), new Vector3(gate0 - from, .3f, rampW + .2f), Quaternion.Euler(tilt, 0, 0));
            Ramp(new Vector3((gate1 + to) / 2, midY, Island.CityH + H + 6.2f + rampW / 2), new Vector3(to - gate1, .3f, rampW + .2f), Quaternion.Euler(tilt, 0, 0));
            for (float t = 20; t < Island.CityH; t += 32)
            {
                Lamp(city, new Vector3(-H - 1.5f, 0, t), Vector3.right);
                Lamp(city, new Vector3(Island.CityW + H + 1.5f, 0, t), Vector3.left);
            }
            for (float t = 30; t < Island.CityW - 20; t += 32)
                Lamp(city, new Vector3(t, 0, Island.CityH + H + 1.5f), Vector3.back);
        }

        static void Lamp(Transform parent, Vector3 foot, Vector3 road) => GridCity.LampPost(parent, foot, road);

        // --- harbour ---------------------------------------------------------------------------
        void Harbour()
        {
            var h = new GameObject("Harbour").transform;
            h.SetParent(transform, false);
            float top = .02f, depth = 3.2f;
            Art.WorldUV(Art.Box(h, "Quay", new Vector3(Island.CityW / 2, top - depth / 2, (Island.QuayZ - GridCity.Half) / 2), new Vector3(Island.CityW + 300, depth, -Island.QuayZ - GridCity.Half), Art.Concrete, true), 4, 1);
            // Three piers and a breakwater.
            float[] piers = { 250, 650, 1050 };
            foreach (float px in piers)
            {
                Art.WorldUV(Art.Box(h, "Pier", new Vector3(px, top - depth / 2, Island.QuayZ - 85), new Vector3(16, depth, 170), Art.Concrete, true), 4, 1);
                Art.RoadSign(h, new Vector3(px - 9.5f, top, Island.QuayZ + 2), Vector3.forward, px < 400 ? "Weight_Limit_Sign" : px < 800 ? "Attention_Sign" : "Maximum_Sign");
                for (float z = Island.QuayZ - 10; z > Island.QuayZ - 165; z -= 18)
                    for (int s = -1; s <= 1; s += 2)
                        Art.Box(h, "Bollard", new Vector3(px + s * 7.3f, .35f, z), new Vector3(.4f, .7f, .4f), Art.Dark);
            }
            Port.Build(h, Island.QuayZ, Island.SeaY, piers);
            // The aircraft carrier cruising off the harbour.
            Carrier.Spawn(transform);
            // A lighthouse at the end of the middle pier.
            var tower = Art.Primitive(h, "Lighthouse", new Vector3(650, 9, Island.QuayZ - 176), new Vector3(4, 9, 4), Art.Line, PrimitiveType.Cylinder, true);
            Art.Primitive(h, "Lighthouse band", new Vector3(650, 12, Island.QuayZ - 176), new Vector3(4.1f, 1.4f, 4.1f), Art.Blue, PrimitiveType.Cylinder);
            Art.Primitive(h, "Lighthouse lamp", new Vector3(650, 19.2f, Island.QuayZ - 176), new Vector3(2.6f, 1.2f, 2.6f), Art.Amber, PrimitiveType.Cylinder);
            Art.Combine(h);
            Art.Strip(h);
        }

        // --- the airport: a huge open apron to drift on, a runway, terminal, tower, hangars, planes --
        public Transform AirportRoot { get; private set; }
        void Airport()
        {
            var a = new GameObject("Airport").transform;
            a.SetParent(transform, false);
            AirportRoot = a;
            float x0 = Island.AirX0, x1 = Island.AirX1, z0 = Island.AirZ0, z1 = Island.AirZ1;
            float cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
            // The field: one concrete slab over everything, then paint and asphalt on top.
            Art.WorldUV(Art.Box(a, "Airport apron", new Vector3(cx, -.5f, cz), new Vector3(x1 - x0, 1.04f, z1 - z0), Art.Concrete, true), 6, 1);
            float rz0 = z1 - 170, rz1 = rz0 + 60;
            Art.WorldUV(Art.Box(a, "Runway", new Vector3(cx, .035f, (rz0 + rz1) / 2), new Vector3(x1 - x0 - 120, .02f, rz1 - rz0), Art.Asphalt), 5, 1);
            for (float x = x0 + 120; x < x1 - 120; x += 50)
                Art.Box(a, "Runway centre line", new Vector3(x, .05f, (rz0 + rz1) / 2), new Vector3(30, .01f, .9f), Art.Line);
            foreach (float edge in new[] { rz0 + 1.5f, rz1 - 1.5f })
                Art.Box(a, "Runway edge line", new Vector3(cx, .05f, edge), new Vector3(x1 - x0 - 130, .01f, .9f), Art.Line);
            foreach (float end in new[] { x0 + 75, x1 - 75 })
                for (int k = -5; k <= 5; k++)
                    if (k != 0) Art.Box(a, "Threshold bar", new Vector3(end, .05f, (rz0 + rz1) / 2 + k * 4.6f), new Vector3(30, .01f, 1.8f), Art.Line);
            for (float x = x0 + 70; x < x1 - 60; x += 60)
                foreach (float edge in new[] { rz0 - 2, rz1 + 2 })
                    Art.Box(a, "Runway light", new Vector3(x, .25f, edge), new Vector3(.4f, .5f, .4f), Art.Amber);
            foreach (float tx in new[] { x0 + 260, x1 - 260 })
                Art.WorldUV(Art.Box(a, "Taxiway", new Vector3(tx, .035f, (rz0 + z0 + 330) / 2), new Vector3(24, .02f, rz0 - (z0 + 330)), Art.Asphalt), 5, 1);

            // The drift zone in the middle of the apron: a painted ring and a slalom of cones.
            Vector3 ring = new(cx + 40, 0, z0 + 190);
            for (int k = 0; k < 48; k++)
            {
                float t = k / 48f * Mathf.PI * 2;
                var dash = Art.Box(a, "Drift ring", ring + new Vector3(Mathf.Cos(t) * 55, .05f, Mathf.Sin(t) * 55), new Vector3(.6f, .01f, 5), Art.Amber);
                dash.localRotation = Quaternion.Euler(0, -t * Mathf.Rad2Deg, 0);
            }
            var label = Art.Text(a, "ДРИФТ-ЗОНА", ring + new Vector3(0, .07f, 0), 5, new Color(1, .7f, .25f));
            label.transform.localRotation = Quaternion.Euler(90, 0, 0);
            for (int k = 0; k < 10; k++)
            {
                Vector3 c = new(x0 + 330 + k * 26, .4f, z0 + 60 + (k % 2) * 7);
                var cone = Knockable.Begin(a, "Traffic cone", c - new Vector3(0, .4f, 0));
                Art.Primitive(cone, "Cone", new Vector3(0, .4f, 0), new Vector3(.55f, .4f, .55f), Art.Amber, PrimitiveType.Cylinder);
                Art.Box(cone, "Cone base", new Vector3(0, .04f, 0), new Vector3(.8f, .08f, .8f), Art.Dark);
                Knockable.End(cone, 4);
            }

            // Terminal on the west side.
            float tx0 = x0 + 12, tzc = z0 + 250;
            AirportDetail.Terminal(a, tx0, tzc);
            Art.Box(a, "Terminal sign", new Vector3(tx0 + 62, 16, tzc), new Vector3(.3f, 3.2f, 60), Art.Blue);
            var sign = Art.Text(a, "АЭРОПОРТ", new Vector3(tx0 + 62.3f, 16, tzc), 2.4f, Color.white);
            sign.transform.localRotation = Quaternion.Euler(0, -90, 0);
            // Control tower.
            Vector3 tower = new(tx0 + 40, 0, tzc + 190);
            AirportDetail.Tower(a, tower);
            // Hangars on the east side.
            for (int k = 0; k < 3; k++)
            {
                Vector3 h = new(x1 - 45, 0, z0 + 120 + k * 95);
                AirportDetail.Hangar(a, h, k + 1);
            }
            // Two airliners parked by the terminal.
            // Nose-in at stands 1 and 3, with a tug, baggage carts and cones by each.
            var service = new System.Random(1717);
            for (int k = 0; k < 2; k++)
            {
                float pz = tzc - 90 + k * 120;
                AirportDetail.Airliner(a, new Vector3(tx0 + 100, 0, pz), -90, k);
                AirportDetail.ServiceKit(a, new Vector3(tx0 + 88, 0, pz - 9), 0, service);
            }
            // Runway designators at both ends.
            foreach (var (rx, runwayName, turn) in new[] { (x0 + 115f, "09", 90f), (x1 - 115f, "27", -90f) })
            {
                var num = Art.Text(a, runwayName, new Vector3(rx, .06f, (rz0 + rz1) / 2), 14, Color.white);
                num.transform.localRotation = Quaternion.Euler(90, turn, 0);
            }

            // Perimeter fence with the gate from the city.
            float gate = Island.AirGateX;
            void Fence(Vector3 from, Vector3 to)
            {
                Vector3 d = to - from;
                float len = d.magnitude;
                var rail = Art.Box(a, "Airport fence", (from + to) / 2 + Vector3.up * 2.4f, new Vector3(.08f, .08f, len), Art.Steel, true);
                rail.localRotation = Quaternion.LookRotation(d);
                var rail2 = Art.Box(a, "Airport fence", (from + to) / 2 + Vector3.up * 1.1f, new Vector3(.08f, .08f, len), Art.Steel);
                rail2.localRotation = rail.localRotation;
                for (float t = 0; t <= len; t += 4)
                    Art.Box(a, "Fence post", from + d / len * t + Vector3.up * 1.3f, new Vector3(.1f, 2.6f, .1f), Art.Steel, true);
            }
            // Warning signs at the gate for drivers coming up from the city.
            Art.RoadSign(a, new Vector3(gate - 27, 0, z0 - 2), Vector3.back, "Caution_Sign");
            Art.RoadSign(a, new Vector3(gate + 27, 0, z0 - 2), Vector3.back, "Camera_Sign");
            Art.RoadSign(a, new Vector3(gate - 40, 0, z0 - 1), Vector3.back, "Danger_Keep_Out_Sign");
            Art.RoadSign(a, new Vector3(gate + 40, 0, z0 - 1), Vector3.back, "Hard_Hat_Sign");
            // High chain-link security fence with barbed wire and razor coil; sections break
            // loose when driven into. Kept out of the merged airport mesh for that reason.
            var fences = new GameObject("Airport fences").transform;
            fences.SetParent(transform, false);
            Fences.Airport(fences, new Vector3(x0, 0, z0 + 1), new Vector3(gate - 24, 0, z0 + 1), Vector3.back);
            Fences.Airport(fences, new Vector3(gate + 24, 0, z0 + 1), new Vector3(x1, 0, z0 + 1), Vector3.back);
            Fences.Airport(fences, new Vector3(x0, 0, z1), new Vector3(x1, 0, z1), Vector3.forward);
            Fences.Airport(fences, new Vector3(x0, 0, z0 + 1), new Vector3(x0, 0, z1), Vector3.left);
            Fences.Airport(fences, new Vector3(x1, 0, z0 + 1), new Vector3(x1, 0, z1), Vector3.right);

            // Grass between the paved areas, so the field reads as an airport and not a car park.
            float gz0 = z0 + 380, gz1 = rz0 - 30;
            float[] cuts = { x0 + 8, x0 + 260 - 20, x0 + 260 + 20, x1 - 260 - 20, x1 - 260 + 20, x1 - 8 };
            for (int k = 0; k < cuts.Length; k += 2)
                Art.WorldUV(Art.Box(a, "Airfield grass", new Vector3((cuts[k] + cuts[k + 1]) / 2, .045f, (gz0 + gz1) / 2), new Vector3(cuts[k + 1] - cuts[k], .02f, gz1 - gz0), Art.Grass), 6, 1);
            Art.WorldUV(Art.Box(a, "Airfield grass", new Vector3(cx, .045f, (rz1 + 30 + z1 - 6) / 2), new Vector3(x1 - x0 - 16, .02f, z1 - 6 - rz1 - 30), Art.Grass), 6, 1);
            // Aircraft stands by the terminal: yellow lead-in lines and stop bars.
            for (int k = 0; k < 4; k++)
            {
                float sz = tzc - 90 + k * 60;
                Art.Box(a, "Stand lead-in", new Vector3(tx0 + 110, .05f, sz), new Vector3(90, .01f, .35f), Art.Amber);
                Art.Box(a, "Stand stop bar", new Vector3(tx0 + 72, .05f, sz), new Vector3(.4f, .01f, 8), Art.Amber);
                var num = Art.Text(a, "" + (k + 1), new Vector3(tx0 + 80, .06f, sz + 5), 2.5f, new Color(1f, .8f, .2f));
                num.transform.localRotation = Quaternion.Euler(90, -90, 0);
            }
            // Jet bridges from the terminal to the two airliners.
            for (int k = 0; k < 2; k++)
            {
                float jz = tzc - 90 + k * 120 + 4;
                Art.WorldUV(Art.Box(a, "Jet bridge", new Vector3(tx0 + 74, 5.2f, jz), new Vector3(26, 3.2f, 3.2f), Art.PaintGrey), 2.6f, 1);
                Art.Box(a, "Jet bridge cab", new Vector3(tx0 + 87.4f, 5.0f, jz - .4f), new Vector3(1.6f, 3.4f, 3.8f), Art.Dark);
                Art.Box(a, "Jet bridge leg", new Vector3(tx0 + 84, 2.1f, jz), new Vector3(.6f, 4.2f, .6f), Art.Dark);
                Art.Box(a, "Jet bridge wheels", new Vector3(tx0 + 84, .4f, jz), new Vector3(1.8f, .8f, 1.2f), Art.Rubber);
                foreach (int s in new[] { -1, 1 })
                    Art.Box(a, "Jet bridge glass", new Vector3(tx0 + 74, 5.5f, jz + s * 1.62f), new Vector3(24, .9f, .05f), Art.Glass);
            }
            // Terminal: entrance canopy and doors on the landside, roof plant.
            // Windsock.
            Art.Box(a, "Windsock mast", new Vector3(x1 - 120, 4, rz1 + 20), new Vector3(.2f, 8, .2f), Art.Steel);
            Art.Primitive(a, "Windsock", new Vector3(x1 - 118.5f, 7.8f, rz1 + 20), new Vector3(.8f, 1.6f, .8f), Art.Amber, PrimitiveType.Cylinder).localRotation = Quaternion.Euler(0, 0, 80);
            foreach (int s in new[] { -1, 1 })
                Art.Box(a, "Gate pillar", new Vector3(gate + s * 23, 1.8f, z0 + 1), new Vector3(1.2f, 3.6f, 1.2f), Art.Concrete, true);
            Art.Box(a, "Gate sign", new Vector3(gate, 6.2f, z0 + 1), new Vector3(46, 2.2f, .3f), Art.Blue);
            var gateText = Art.Text(a, "АЭРОПОРТ  •  ДРИФТ-ЗОНА", new Vector3(gate, 6.2f, z0 + .8f), .9f, Color.white);
            gateText.transform.localRotation = Quaternion.identity;
            foreach (int s in new[] { -1, 1 })
                Art.Box(a, "Gate post", new Vector3(gate + s * 23, 4, z0 + 1), new Vector3(.4f, 5, .4f), Art.Steel);
            AirportExtras.Build(a, transform, x0, x1, z0, z1, rz0, rz1, tx0, tzc, gate);
            Art.Combine(a);
            Art.Strip(a);
            // Aircraft to fly: the airliner at the west end of the runway, the bomber on the field
            // north of it at the east end; the two light aircraft wait beside the runway.
            float mid = (rz0 + rz1) / 2;
            Airplane.Spawn(a, a.TransformPoint(new Vector3(x0 + 150, .45f, rz0 - 22)), 90);
            Airplane.Spawn(a, a.TransformPoint(new Vector3(x0 + 190, .45f, rz0 - 22)), 90);
            // (The bomber on the flat field north of the runway: clear of the airliner's run.)
            Airplane.Spawn(a, a.TransformPoint(new Vector3(x1 - 170, .8f, rz1 + 55)), -90, AircraftKind.Bomber);
            Airplane.Spawn(a, a.TransformPoint(new Vector3(x0 + 150, .8f, mid)), 90, AircraftKind.Airliner);
        }

        static Material planeWhite;
        static void Plane(Transform parent, Vector3 at, int livery)
        {
            var p = new GameObject("Airliner").transform;
            p.SetParent(parent, false);
            p.localPosition = at;
            p.localRotation = Quaternion.Euler(0, 90, 0);
            planeWhite ??= Art.Material("Airliner white", new Color(.92f, .93f, .95f), .2f, .6f);
            var body = planeWhite;
            var stripe = livery == 0 ? Art.Blue : Art.SignRed;
            var fuselage = Art.Primitive(p, "Fuselage", new Vector3(0, 4.2f, 0), new Vector3(4.2f, 19, 4.2f), body, PrimitiveType.Capsule);
            fuselage.localRotation = Quaternion.Euler(90, 0, 0);
            Art.Box(p, "Fuselage stripe", new Vector3(0, 4.4f, 0), new Vector3(4.3f, .5f, 30), stripe);
            Art.Box(p, "Cockpit windows", new Vector3(0, 5.3f, 17), new Vector3(2.4f, .6f, 1.2f), Art.Glass);
            Art.Box(p, "Wing", new Vector3(0, 3.2f, 1), new Vector3(34, .5f, 6), body);
            Art.Box(p, "Tail fin", new Vector3(0, 9, -16.5f), new Vector3(.5f, 7, 5), stripe);
            Art.Box(p, "Tail plane", new Vector3(0, 5, -17), new Vector3(12, .4f, 3.5f), body);
            foreach (int s in new[] { -1, 1 })
            {
                var engine = Art.Primitive(p, "Engine", new Vector3(s * 7, 2.2f, 2.5f), new Vector3(2, 2, 2), Art.Steel, PrimitiveType.Cylinder);
                engine.localRotation = Quaternion.Euler(90, 0, 0);
                Art.Box(p, "Gear", new Vector3(s * 2.5f, 1, 0), new Vector3(.4f, 2, .4f), Art.Dark);
            }
            Art.Box(p, "Nose gear", new Vector3(0, 1, 14), new Vector3(.4f, 2, .4f), Art.Dark);
            // One solid block to bump into.
            var hit = new GameObject("Airliner body");
            hit.layer = 10;
            hit.transform.SetParent(p, false);
            var box = hit.AddComponent<BoxCollider>();
            box.center = new Vector3(0, 4.2f, 0);
            box.size = new Vector3(4.2f, 4.2f, 38);
            var wing = hit.AddComponent<BoxCollider>();
            wing.center = new Vector3(0, 3.2f, 1);
            wing.size = new Vector3(34, .6f, 6);
        }

        // --- the small town and its filling station at the top ------------------------------------
        // Streamed like the tiles: a few houses a frame, from its own seed.
        IEnumerator<object> Town(Transform root)
        {
            float centre = Island.TownS;
            var rnd = new System.Random(4242);
            float R(float a, float b) => (float)(a + rnd.NextDouble() * (b - a));
            var station = Station.Sites[0];
            for (int side = -1; side <= 1; side += 2)
            {
                float s = centre - Island.TownHalf;
                int houses = 0;
                while (s < centre + Island.TownHalf)
                {
                    float width = R(11, 17);
                    float mid = s + width / 2;
                    bool stationPlot = side == station.Side && Mathf.Abs(mid - station.Z) < 105;
                    if (!stationPlot)
                    {
                        Vector3 front = Route.Center(mid) + Route.Right(mid) * (side * 15.2f);
                        front.y = Route.Height(mid) - .05f;
                        var rotation = Quaternion.LookRotation(Route.Right(mid) * side, Vector3.up);
                        GridCity.Townhouse(root, root.InverseTransformPoint(front), rotation, rnd, width - .6f, R(10, 13), rnd.Next(2, 5), rnd.NextDouble() < .45);
                        if (++houses % 4 == 0) yield return null;
                    }
                    s += width;
                }
                // Pavement and lamps along the main street.
                for (float t = centre - Island.TownHalf; t < centre + Island.TownHalf; t += 10)
                {
                    if (side == station.Side && Mathf.Abs(t + 5 - station.Z) < 70) continue;
                    var walk = Art.Box(root, "Town pavement", root.InverseTransformPoint(Route.Center(t + 5) + Route.Right(t + 5) * (side * 11.4f) + Vector3.up * .07f), new Vector3(5.6f, .3f, 10.3f), Art.Kerb, true);
                    walk.rotation = Route.Rotation(t + 5);
                    if (Mathf.RoundToInt(t) % 40 == 0)
                    {
                        Vector3 foot = Route.Center(t) + Route.Right(t) * (side * 9.3f);
                        GridCity.LampPost(root, root.InverseTransformPoint(foot), root.InverseTransformDirection(-Route.Right(t) * side));
                    }
                }
                yield return null;
            }
            // Name boards at both ends of the town.
            foreach (float at in new[] { centre - Island.TownHalf - 40, centre + Island.TownHalf + 40 })
            {
                Vector3 p = Route.Center(at) + Route.Right(at) * 9.5f;
                var sign = Knockable.Begin(root, "Town sign", root.InverseTransformPoint(p));
                sign.rotation = Route.Rotation(at);
                Art.Box(sign, "Town sign", new Vector3(0, 2.6f, 0), new Vector3(3.6f, 1.3f, .12f), Art.Line);
                Art.Box(sign, "Town sign post", new Vector3(-1.2f, 1.0f, .08f), new Vector3(.14f, 2, .14f), Art.Steel);
                Art.Box(sign, "Town sign post", new Vector3(1.2f, 1.0f, .08f), new Vector3(.14f, 2, .14f), Art.Steel);
                Art.Text(sign, "СЕВЕРНЫЙ", new Vector3(0, 2.6f, -.08f), .34f, Color.black);
                Knockable.End(sign, 60, 0, false, new Bounds(new Vector3(0, 1.5f, 0), new Vector3(3.6f, 3, .4f)));
            }
            yield return null;
            Art.Combine(root);
            Art.Strip(root);
        }

        // --- sea stacks along the coast ------------------------------------------------------
        void Rocks()
        {
            var r = new GameObject("Sea rocks").transform;
            r.SetParent(transform, false);
            var rnd = new System.Random(77);
            float R(float a, float b) => (float)(a + rnd.NextDouble() * (b - a));
            for (float s = 150; s < Route.Course - 150; s += R(90, 190))
            {
                Vector3 c = Route.Center(s), out1 = -Route.Right(s);
                // Where the land meets the water here, and whether it is a rocky headland.
                float shore = 60;
                while (shore < 900 && Island.GroundHeight(c.x + out1.x * shore, c.z + out1.z * shore) > Island.SeaY - 1) shore += 8;
                Vector3 edge = c + out1 * shore;
                float head = Island.Headland(edge.x, edge.z);
                int count = head > .45f ? rnd.Next(4, 9) : rnd.Next(0, 3);
                for (int k = 0; k < count; k++)
                {
                    // Headlands: a scatter of rocks in the surf; coves: the odd rock further out.
                    float d = shore + (head > .45f ? R(-6, 40) : R(25, 150));
                    Vector3 p = c + out1 * d + Route.Forward(s) * R(-60, 60);
                    float size = head > .45f ? R(5, 17) : R(8, 26);
                    Transform rock;
                    if (Island.Boulders.Length > 0)
                    {
                        // A real rock from the purchased set, scaled to size.
                        var (mesh, skin) = Island.Boulders[rnd.Next(Island.Boulders.Length)];
                        rock = Art.MeshObject(r, "Sea rock", mesh, skin, false).transform;
                        float m = Mathf.Max(mesh.bounds.size.x, mesh.bounds.size.z);
                        float s1 = size / Mathf.Max(.1f, m);
                        // Standing on the bed, but never so deep that it is lost under the water.
                        rock.position = new Vector3(p.x, Mathf.Max(MeshHeight(p.x, p.z), Island.SeaY - size * .45f) - size * .2f, p.z);
                        rock.localScale = new Vector3(s1 * R(.8f, 1.3f), s1 * R(.8f, 1.4f), s1 * R(.8f, 1.3f));
                    }
                    else
                        rock = Art.Primitive(r, "Sea rock", new Vector3(p.x, Island.SeaY + size * R(.05f, .3f), p.z), new Vector3(size * R(.8f, 1.4f), size * R(.7f, 1.3f), size * R(.8f, 1.4f)), Island.Rock, PrimitiveType.Sphere);
                    rock.rotation = Quaternion.Euler(R(-15, 15), R(0, 360), R(-15, 15));
                }
            }
            // Boulders on the mountain slopes and in the forest, away from the road.
            int placed = 0;
            for (int k = 0; k < 4000 && placed < 260 && Island.Boulders.Length > 0; k++)
            {
                var p = new Vector3(R(X0, X0 + TilesX * Tile), 0, R(Z0, Z0 + TilesZ * Tile));
                float h = MeshHeight(p.x, p.z);
                if (h < Island.SeaY + 4 || Island.InCity(p) || Island.InAirport(p)) continue;
                float s = Route.Locate(p);
                if (Mathf.Abs(Route.Lateral(p)) < 22 || Island.InTown(s, Route.Lateral(p))) continue;
                if (h < 25 && rnd.NextDouble() < .7) continue;          // mostly up in the hills
                int model = rnd.Next(Island.Boulders.Length);
                var mesh = Island.Boulders[model].Item1;
                // Hang it on its ground tile, so it streams in and out with the ground under it.
                // Placed here for the whole island, built with its tile.
                int tx = Mathf.FloorToInt((p.x - X0) / Tile), tz = Mathf.FloorToInt((p.z - Z0) / Tile);
                var tile = tx >= 0 && tz >= 0 && tx < TilesX && tz < TilesZ ? slots[tx, tz] : null;
                if (tile == null) continue;
                float size = R(2.5f, h > 90 ? 16 : 8);
                float m = Mathf.Max(mesh.bounds.size.x, mesh.bounds.size.z);
                float k1 = size / Mathf.Max(.1f, m);
                var scale = new Vector3(k1 * R(.8f, 1.3f), k1 * R(.7f, 1.2f), k1 * R(.8f, 1.3f));
                var at = new Vector3(p.x, h - size * .15f, p.z);
                var turn = Quaternion.Euler(R(-12, 12), R(0, 360), R(-12, 12));
                tile.Boulders.Add((model, at, scale, turn));
                placed++;
            }
            HillBoulders = placed;
            Art.Combine(r);
            Art.Strip(r);
        }

        public static int HillBoulders;

        // --- streaming (WorldStreamer does the work) ------------------------------------------------

        // The whole island built (the map picture); `false` leaves the streaming to clear up
        // behind it once the picture is taken.
        public void ShowEverything(bool on)
        {
            if (on) WorldStreamer.Active?.BuildAll();
            else WorldStreamer.Active?.Refresh(false);
        }

        // `all`: act on a jump at once (a new run, a teleport).
        public void Stream(bool all) => WorldStreamer.Active?.Refresh(all);

        void OnDestroy()
        {
            if (Active == this) Active = null;
        }
    }
}

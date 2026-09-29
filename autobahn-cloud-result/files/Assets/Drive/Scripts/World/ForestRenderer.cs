using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Autobahn
{
    // The island's forests, drawn with GPU instancing instead of one merged mesh per ground tile:
    // many more trees for the same cost, the detailed purchased model near the camera and its
    // cheapest level of detail further out, switched per tree as you drive. The trees are only
    // positions (no game objects), grouped by species and variant, and filed in a grid of square
    // cells: drawing, trunks and searches look only at the cells round the place in question.
    public sealed class ForestRenderer : MonoBehaviour
    {
        public static ForestRenderer Active { get; private set; }

        public struct Plant
        {
            public Vector3 Position;
            public Foliage.Kind Kind;
            public float Size;
        }

        sealed class Group
        {
            public Foliage.Kind Kind;
            public bool Simple;
            public int Variant;
            // A far group's flat stand-in (see Board), and whether this group is one.
            public int Board = -1;
            public bool IsBoard;
            public readonly List<(Mesh, Material)> Parts = new();
            public readonly List<Matrix4x4> Frame = new();
            public readonly List<Matrix4x4> Shadowed = new();
        }

        sealed class Tile
        {
            public Vector3 Centre;
            public int[] NearGroup, FarGroup;       // per tree
            public Vector2[] Spot;                   // per tree, x/z
            public Matrix4x4[] NearMatrix, FarMatrix;
            public bool[] Down;                      // per tree: knocked down (not drawn)
            public readonly List<Cell> Cells = new(); // where its trees are filed
        }

        // A square of the grid and the trees standing in it (tile slot and index in the tile).
        public const float CellSize = 64;
        const float HalfDiagonal = CellSize * .7072f;
        sealed class Cell
        {
            public int X, Z;
            public Vector2 Centre;
            public readonly List<(int tile, int index)> Trees = new();
        }
        readonly Dictionary<long, Cell> cells = new();
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;

        static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;
        static int CellOf(float v) => Mathf.FloorToInt(v / CellSize);
        Cell CellAt(int x, int z) => cells.TryGetValue(CellKey(x, z), out var c) ? c : null;

        // Tile slots: a slot is emptied when its ground is unloaded (world streaming) and reused
        // later, so the numbers the trunks and felled trees keep stay valid.
        readonly List<Tile> tiles = new();
        readonly Stack<int> freeSlots = new();
        bool logged;
        readonly List<Group> groups = new();
        readonly Dictionary<(Foliage.Kind, bool, int), int> groupOf = new();
        public int Trees { get; private set; }
        public int DrawnNear { get; private set; }
        public int DrawnFar { get; private set; }
        public int Calls { get; private set; }
        Vector3 lastLook = Vector3.forward, lastCamera = new(1e9f, 0, 0);
        float nextRefresh;
        bool dirty = true;
        static readonly Bounds everywhere = new(new Vector3(700, 0, 2500), new Vector3(9000, 2000, 12000));

        public static ForestRenderer Ensure(Transform parent)
        {
            if (Active) return Active;
            var go = new GameObject("Forest");
            go.transform.SetParent(parent, false);
            Active = go.AddComponent<ForestRenderer>();
            return Active;
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
        }

        int GroupFor(Foliage.Kind kind, bool simple, int variant)
        {
            var key = (kind, simple, variant);
            if (groupOf.TryGetValue(key, out int g)) return g;
            var group = new Group { Kind = kind, Simple = simple, Variant = variant };
            Foliage.Parts(kind, simple, variant, group.Parts);
            groups.Add(group);
            int index = groups.Count - 1;
            groupOf[key] = index;
            if (simple) group.Board = Board(group);
            return index;
        }

        // --- far trees as flat pictures --------------------------------------------------------
        // Beyond BillboardRange a tree is two crossed cards (both faces): its crown cut from its
        // own leaf material, a strip of its bark under it. A dozen triangles where the far model
        // has sixty or more, and no shadow (only the nearest trees cast one).
        public static bool Billboards = true;
        public static float BillboardRange => Mathf.Max(NearRange + 30, GraphicsQuality.Mobile ? 110 : 150);
        // How often the leaf texture repeats across a crown (denser leaves from afar).
        public static float BoardTiling = 2;
        public int DrawnBoards { get; private set; }

        int Board(Group far)
        {
            if (far.Parts.Count < 2) return -1;
            var bounds = new Bounds();
            bool any = false;
            foreach (var (mesh, _) in far.Parts)
                if (mesh)
                {
                    if (!any) { bounds = mesh.bounds; any = true; }
                    else bounds.Encapsulate(mesh.bounds);
                }
            var bark = far.Parts[0].Item2;
            var leaf = far.Parts[1].Item2;
            if (!any || !leaf || bounds.max.y < .5f) return -1;
            float h = bounds.max.y, half = Mathf.Max(bounds.extents.x, bounds.extents.z) * .85f;
            bool conifer = far.Kind == Foliage.Kind.Pine || far.Kind == Foliage.Kind.Spruce;
            var outline = new List<Vector2>();
            float trunkTop;
            if (conifer)
            {
                // A cone seen from the side.
                outline.Add(new Vector2(-half, h * .15f));
                outline.Add(new Vector2(0, h));
                outline.Add(new Vector2(half, h * .15f));
                trunkTop = h * .3f;
            }
            else
            {
                // A rounded crown on its trunk (a bush: all crown).
                float bottom = far.Kind == Foliage.Kind.Bush ? 0 : h * .32f;
                float cy = (h + bottom) / 2, ry = (h - bottom) / 2;
                for (int k = 0; k < 8; k++)
                {
                    float a = (k + .5f) / 8 * Mathf.PI * 2;
                    outline.Add(new Vector2(Mathf.Cos(a) * half, cy + Mathf.Sin(a) * ry));
                }
                trunkTop = far.Kind == Foliage.Kind.Bush ? 0 : h * .45f;
            }
            var board = new Group { Kind = far.Kind, Simple = true, Variant = far.Variant, IsBoard = true };
            if (trunkTop > 0 && bark)
            {
                float w = Mathf.Clamp(half * .08f, .12f, .45f);
                var trunk = new List<Vector2> { new(-w, 0), new(-w, trunkTop), new(w, trunkTop), new(w, 0) };
                board.Parts.Add((Cards(trunk, 1, "Tree board trunk"), bark));
            }
            board.Parts.Add((Cards(outline, BoardTiling, "Tree board crown"), leaf));
            if (!leaf.enableInstancing) leaf.enableInstancing = true;
            groups.Add(board);
            return groups.Count - 1;
        }

        // Two upright cards at right angles, both faces, filled with `outline` (x across, y up)
        // as a fan from its middle. Normals lean up, so the sky lights both faces alike.
        static Mesh Cards(List<Vector2> outline, float tiling, string name)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            var mid = Vector2.zero;
            foreach (var p in outline)
            {
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
                mid += p;
            }
            mid /= outline.Count;
            Vector2 UV(Vector2 p) => new((p.x - x0) / Mathf.Max(.01f, x1 - x0) * tiling, (p.y - y0) / Mathf.Max(.01f, y1 - y0) * tiling);
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            var t = new List<int>();
            for (int card = 0; card < 2; card++)
                for (int face = 0; face < 2; face++)
                {
                    Vector3 across = card == 0 ? Vector3.right : Vector3.forward;
                    Vector3 facing = Vector3.Cross(across, Vector3.up) * (face == 0 ? 1 : -1);
                    Vector3 normal = (facing * .35f + Vector3.up).normalized;
                    int c = v.Count;
                    v.Add(across * mid.x + Vector3.up * mid.y); n.Add(normal); uv.Add(UV(mid));
                    foreach (var p in outline)
                    {
                        v.Add(across * p.x + Vector3.up * p.y);
                        n.Add(normal);
                        uv.Add(UV(p));
                    }
                    for (int k = 0; k < outline.Count; k++)
                    {
                        int a = c + 1 + k, b = c + 1 + (k + 1) % outline.Count;
                        if (face == 0) { t.Add(c); t.Add(a); t.Add(b); }
                        else { t.Add(c); t.Add(b); t.Add(a); }
                    }
                }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Returns the tile's id for RemoveTile (-1: no trees).
        public int AddTile(Vector3 centre, List<Plant> plants)
        {
            if (plants.Count == 0) return -1;
            var tile = new Tile
            {
                Centre = centre,
                NearGroup = new int[plants.Count], FarGroup = new int[plants.Count],
                NearMatrix = new Matrix4x4[plants.Count], FarMatrix = new Matrix4x4[plants.Count],
                Spot = new Vector2[plants.Count],
                Down = new bool[plants.Count],
            };
            for (int i = 0; i < plants.Count; i++)
            {
                var p = plants[i];
                var yaw = Quaternion.Euler(0, Foliage.Yaw(p.Position), 0);
                int nv = Foliage.Pick(p.Position, p.Kind, false, p.Size, out float ns);
                int fv = Foliage.Pick(p.Position, p.Kind, true, p.Size, out float fs);
                tile.NearGroup[i] = GroupFor(p.Kind, false, nv);
                tile.FarGroup[i] = GroupFor(p.Kind, true, fv);
                tile.NearMatrix[i] = Matrix4x4.TRS(p.Position, yaw, Vector3.one * ns);
                tile.Spot[i] = new Vector2(p.Position.x, p.Position.z);
                tile.FarMatrix[i] = Matrix4x4.TRS(p.Position, yaw, Vector3.one * fs);
            }
            int id;
            if (freeSlots.Count > 0)
            {
                id = freeSlots.Pop();
                tiles[id] = tile;
            }
            else
            {
                id = tiles.Count;
                tiles.Add(tile);
            }
            for (int i = 0; i < plants.Count; i++)
            {
                int x = CellOf(tile.Spot[i].x), z = CellOf(tile.Spot[i].y);
                long key = CellKey(x, z);
                if (!cells.TryGetValue(key, out var cell))
                {
                    cells[key] = cell = new Cell { X = x, Z = z, Centre = new Vector2((x + .5f) * CellSize, (z + .5f) * CellSize) };
                    minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                    minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
                }
                if (cell.Trees.Count == 0 || cell.Trees[cell.Trees.Count - 1].tile != id) tile.Cells.Add(cell);
                cell.Trees.Add((id, i));
            }
            if (!logged)
            {
                logged = true;
                foreach (Foliage.Kind k in System.Enum.GetValues(typeof(Foliage.Kind)))
                    Debug.Log($"AUTOBAHN forest {k}: near {Foliage.VariantCount(k, false)} variants {Foliage.Triangles(k, false, 0)} tris, far {Foliage.VariantCount(k, true)} variants {Foliage.Triangles(k, true, 0)} tris");
            }
            Trees += plants.Count;
            dirty = true;
            return id;
        }

        // The ground under a tile was unloaded: its trees go with it, felled ones included.
        public void RemoveTile(int id)
        {
            if (id < 0 || id >= tiles.Count || tiles[id] == null) return;
            Trees -= tiles[id].Spot.Length;
            for (int k = down.Count - 1; k >= 0; k--)
                if (down[k].tile == id)
                {
                    if (down[k].body) Destroy(down[k].body);
                    down.RemoveAt(k);
                }
            foreach (var c in trunks)
                if (c && c.gameObject.activeSelf && c.GetComponent<Trunk>().Tile == id) c.gameObject.SetActive(false);
            foreach (var cell in tiles[id].Cells)
            {
                cell.Trees.RemoveAll(r => r.tile == id);
                if (cell.Trees.Count == 0) cells.Remove(CellKey(cell.X, cell.Z));
            }
            tiles[id] = null;
            freeSlots.Push(id);
            dirty = true;
        }

        // Detailed trees this close to the camera (metres), chosen per tree; the cheap model out to
        // the view distance. The detailed model has a few thousand triangles, the far one 60; from
        // about 150 m they look alike on screen, and 380 m cost 30-40 ms a frame on the coast road.
        public static float NearRange => GraphicsQuality.Level == 0 ? 70 : 110;
        // Only the closest detailed trees cast real shadows.
        public const float ShadowRange = 45;
        public static bool Hidden;
        public static bool SkipNear, SkipFar;      // QA: what each half costs

        // Trees well behind the camera are not drawn (their shadows only matter close by).
        const float BehindMargin = 40;

        void Refresh(Vector3 cam, Vector3 forward)
        {
            var ahead = new Vector2(forward.x, forward.z);
            bool cullBehind = ahead.sqrMagnitude > .04f && !RoadWorld.ShowAll;
            ahead = cullBehind ? ahead.normalized : Vector2.zero;
            foreach (var g in groups) { g.Frame.Clear(); g.Shadowed.Clear(); }
            float reach = GraphicsQuality.FarReach + 150, nearRange = NearRange, near2 = nearRange * nearRange, shadow2 = ShadowRange * ShadowRange;
            var c2 = new Vector2(cam.x, cam.z);
            int nearCount = 0, farCount = 0, boardCount = 0;
            bool everything = RoadWorld.ShowAll;
            bool boards = Billboards;
            float boardRange = BillboardRange, board2 = boardRange * boardRange;

            // Beyond the detailed range: the far model, or its flat picture further out.
            void Far(Tile t, int i, float e)
            {
                var far = groups[t.FarGroup[i]];
                if (boards && far.Board >= 0 && e >= board2)
                {
                    groups[far.Board].Frame.Add(t.FarMatrix[i]);
                    boardCount++;
                }
                else
                {
                    far.Frame.Add(t.FarMatrix[i]);
                    farCount++;
                }
            }

            void Gather(Cell cell)
            {
                var toCell = cell.Centre - c2;
                float d = toCell.magnitude;
                if (d > reach && !everything) return;
                // The whole cell well behind the camera (its trees beyond their shadows' reach too).
                if (cullBehind && Vector2.Dot(toCell, ahead) < -(BehindMargin + HalfDiagonal)) return;
                if (d - HalfDiagonal > nearRange)
                {
                    // All of it beyond the detailed range: the cheap model for every tree, or
                    // (all of it further than the pictures start) the picture.
                    bool allBoards = boards && d - HalfDiagonal > boardRange;
                    foreach (var (ti, i) in cell.Trees)
                    {
                        var t = tiles[ti];
                        if (t.Down[i]) continue;
                        Far(t, i, allBoards ? board2 : (t.Spot[i] - c2).sqrMagnitude);
                    }
                    return;
                }
                foreach (var (ti, i) in cell.Trees)
                {
                    var t = tiles[ti];
                    if (t.Down[i]) continue;
                    float e = (t.Spot[i] - c2).sqrMagnitude;
                    if (cullBehind && e > shadow2 && Vector2.Dot(t.Spot[i] - c2, ahead) < -BehindMargin) continue;
                    if (e < near2)
                    {
                        var g = groups[t.NearGroup[i]];
                        (e < shadow2 ? g.Shadowed : g.Frame).Add(t.NearMatrix[i]);
                        nearCount++;
                    }
                    else Far(t, i, e);
                }
            }

            // Only the cells within reach; when that window holds more cells than there are
            // (a huge reach, the map picture) it is cheaper to go through the filled ones.
            int r = Mathf.CeilToInt(reach / CellSize) + 1;
            int cx = CellOf(cam.x), cz = CellOf(cam.z);
            if (everything || (2L * r + 1) * (2L * r + 1) > cells.Count)
                foreach (var cell in cells.Values) Gather(cell);
            else
                for (int x = cx - r; x <= cx + r; x++)
                    for (int z = cz - r; z <= cz + r; z++)
                    {
                        var cell = CellAt(x, z);
                        if (cell != null) Gather(cell);
                    }
            DrawnNear = nearCount;
            DrawnFar = farCount;
            DrawnBoards = boardCount;
        }

        // For a camera rendered by hand (screenshots, the map picture): the trees are drawn only
        // for cameras that render after the call, so call this just before camera.Render().
        public void DrawFor(Camera camera)
        {
            if (Trees == 0 || !camera) return;
            Refresh(camera.transform.position, camera.transform.forward);
            dirty = true;
            Draw(camera);
        }

        void LateUpdate()
        {
            if (Hidden || Trees == 0) return;
            var camera = Camera.main;
            if (!camera) return;
            Vector3 cam = camera.transform.position;
            Vector3 look = camera.transform.forward;
            bool turned = Vector2.Angle(new Vector2(look.x, look.z), new Vector2(lastLook.x, lastLook.z)) > 20;
            if (dirty || turned && Time.unscaledTime > nextRefresh - .15f || Time.unscaledTime > nextRefresh && (cam - lastCamera).sqrMagnitude > 25 * 25)
            {
                dirty = false;
                nextRefresh = Time.unscaledTime + .25f;
                lastCamera = cam;
                lastLook = look;
                Refresh(cam, look);
            }
            Draw(null);
        }

        // --- trunks: solid round the drivers and people, and a tree breaks off when hit fast ----
        // The trees have no game objects, so a small pool of capsule colliders is moved onto the
        // trunks near the local car and the person on foot ten times a second. Slower than
        // BreakKmh a trunk stops the car like a post; faster, the tree snaps and falls over, and
        // comes back a while later once nobody is looking.
        public const float BreakKmh = 30;
        public static int Felled { get; private set; }
        public sealed class Trunk : MonoBehaviour { public int Tile, Index; }
        readonly List<CapsuleCollider> trunks = new();
        readonly List<(int tile, int index, float at, GameObject body)> down = new();
        readonly List<Vector3> focus = new();
        float nextTrunks, nextRegrow;
        const int TrunkPool = 72;
        const float TrunkReach = 26;

        void FixedUpdate()
        {
            if (Trees == 0 || Time.time < nextTrunks) return;
            nextTrunks = Time.time + .1f;
            PlaceTrunks();
        }

        void PlaceTrunks()
        {
            if (trunks.Count == 0)
            {
                var holder = new GameObject("Tree trunks").transform;
                holder.SetParent(transform, false);
                for (int k = 0; k < TrunkPool; k++)
                {
                    var go = new GameObject("Tree trunk");
                    go.transform.SetParent(holder, false);
                    var c = go.AddComponent<CapsuleCollider>();
                    c.radius = .3f;
                    c.height = 6;
                    c.center = new Vector3(0, 3, 0);
                    go.AddComponent<Trunk>();
                    go.SetActive(false);
                    trunks.Add(c);
                }
            }
            focus.Clear();
            var run = RunSession.Active;
            if (run && run.Player) focus.Add(run.Player.transform.position);
            if (FootPlayer.Active && !FootPlayer.Active.Riding) focus.Add(FootPlayer.Active.transform.position);
            int used = 0;
            foreach (var f in focus)
            {
                var f2 = new Vector2(f.x, f.z);
                int x0 = CellOf(f.x - TrunkReach), x1 = CellOf(f.x + TrunkReach);
                int z0 = CellOf(f.z - TrunkReach), z1 = CellOf(f.z + TrunkReach);
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        var cell = CellAt(x, z);
                        if (cell == null) continue;
                        foreach (var (ti, i) in cell.Trees)
                        {
                            if (used >= trunks.Count) break;
                            var t = tiles[ti];
                            if (t.Down[i] || (t.Spot[i] - f2).sqrMagnitude > TrunkReach * TrunkReach) continue;
                            // Bushes have no trunk: an invisible post inside them stopped cars and people.
                            if (groups[t.NearGroup[i]].Kind == Foliage.Kind.Bush) continue;
                            var c = trunks[used++];
                            c.transform.position = t.NearMatrix[i].GetColumn(3);
                            var tag = c.GetComponent<Trunk>();
                            tag.Tile = ti; tag.Index = i;
                            if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
                        }
                    }
            }
            for (int k = used; k < trunks.Count; k++)
                if (trunks[k].gameObject.activeSelf) trunks[k].gameObject.SetActive(false);

            // Felled trees grow back when the camera is well away.
            if (down.Count > 0 && Time.time > nextRegrow)
            {
                nextRegrow = Time.time + 1;
                var cam = Camera.main ? Camera.main.transform.position : Vector3.zero;
                for (int k = down.Count - 1; k >= 0; k--)
                {
                    var (tile, index, at, body) = down[k];
                    Vector3 p = tiles[tile].NearMatrix[index].GetColumn(3);
                    if (Time.time - at < 45 || (p - cam).sqrMagnitude < 180 * 180) continue;
                    tiles[tile].Down[index] = false;
                    if (body) Destroy(body);
                    down.RemoveAt(k);
                    dirty = true;
                }
            }
        }

        // A car ran into a trunk. True when the tree broke (the car keeps most of its speed).
        public bool Strike(Trunk trunk, Vehicle car, Vector3 before)
        {
            if (trunk.Tile >= tiles.Count) return false;
            var t = tiles[trunk.Tile];
            if (t == null || trunk.Index >= t.Down.Length || t.Down[trunk.Index] || before.magnitude * 3.6f < BreakKmh) return false;
            Fell(trunk.Tile, trunk.Index, before);
            trunk.gameObject.SetActive(false);
            car.Body.linearVelocity = before * .72f;
            return true;
        }

        void Fell(int tile, int index, Vector3 push)
        {
            var t = tiles[tile];
            t.Down[index] = true;
            dirty = true;
            Felled++;
            var m = t.NearMatrix[index];
            var g = groups[t.NearGroup[index]];
            var go = new GameObject("Felled tree");
            go.layer = Knockable.DebrisLayer;
            go.transform.SetPositionAndRotation((Vector3)m.GetColumn(3) + Vector3.up * .08f, m.rotation);
            float scale = m.lossyScale.y;
            go.transform.localScale = Vector3.one * scale;
            float top = 4;
            foreach (var (mesh, material) in g.Parts)
            {
                if (!mesh || !material) continue;
                var part = new GameObject("Tree part");
                part.layer = Knockable.DebrisLayer;
                part.transform.SetParent(go.transform, false);
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = part.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = ShadowCastingMode.On;
                top = Mathf.Max(top, mesh.bounds.max.y);
            }
            var col = go.AddComponent<CapsuleCollider>();
            col.radius = .35f / Mathf.Max(.1f, scale);
            col.height = top;
            col.center = new Vector3(0, top / 2, 0);
            var body = go.AddComponent<Rigidbody>();
            body.mass = 400;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.angularDamping = .6f;
            Vector3 flat = new(push.x, 0, push.z);
            flat = flat.sqrMagnitude > .01f ? flat.normalized : Vector3.forward;
            body.linearVelocity = flat * Mathf.Min(6, push.magnitude * .3f) + Vector3.up * .8f;
            body.angularVelocity = Vector3.Cross(Vector3.up, flat) * 1.4f;
            down.Add((tile, index, Time.time, go));
            Net.WorldNet.Felled((Vector3)m.GetColumn(3), push);
            Debug.Log($"AUTOBAHN tree felled at {(Vector3)m.GetColumn(3)}, {push.magnitude * 3.6f:0} km/h");
        }

        // Online: a friend felled the tree standing here.
        public bool FellNear(Vector3 at, Vector3 push, float within)
        {
            int bestTile = -1, bestIndex = -1;
            float best = within * within;
            var a2 = new Vector2(at.x, at.z);
            int x0 = CellOf(at.x - within), x1 = CellOf(at.x + within);
            int z0 = CellOf(at.z - within), z1 = CellOf(at.z + within);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    var cell = CellAt(x, z);
                    if (cell == null) continue;
                    foreach (var (ti, i) in cell.Trees)
                    {
                        var t = tiles[ti];
                        if (t.Down[i]) continue;
                        float d = (t.Spot[i] - a2).sqrMagnitude;
                        if (d < best) { best = d; bestTile = ti; bestIndex = i; }
                    }
                }
            if (bestTile < 0) return false;
            Fell(bestTile, bestIndex, push);
            return true;
        }

        // A new run: every tree stands again.
        public static void RestoreAll()
        {
            var f = Active;
            if (!f) return;
            foreach (var (tile, index, _, body) in f.down)
            {
                if (f.tiles[tile] != null) f.tiles[tile].Down[index] = false;
                if (body) Destroy(body);
            }
            f.down.Clear();
            f.dirty = true;
        }

        // The nearest standing tree to p (automated checks): rings of cells outwards, until a
        // ring is further away than the best tree found.
        public bool Nearest(Vector3 p, out Vector3 at)
        {
            at = default;
            float best = float.MaxValue;
            if (cells.Count == 0) return false;
            var p2 = new Vector2(p.x, p.z);
            Vector3 found = default;
            void Look(int x, int z)
            {
                var cell = CellAt(x, z);
                if (cell == null) return;
                foreach (var (ti, i) in cell.Trees)
                {
                    var t = tiles[ti];
                    if (t.Down[i]) continue;
                    float d = (t.Spot[i] - p2).sqrMagnitude;
                    if (d < best) { best = d; found = t.NearMatrix[i].GetColumn(3); }
                }
            }
            int cx = CellOf(p.x), cz = CellOf(p.z);
            int rings = Mathf.Max(Mathf.Max(cx - minX, maxX - cx), Mathf.Max(cz - minZ, maxZ - cz));
            for (int ring = 0; ring <= rings; ring++)
            {
                // Every tree in this ring is at least (ring - 1) cells away.
                float closest = Mathf.Max(0, ring - 1) * CellSize;
                if (closest * closest > best) break;
                if (ring == 0) { Look(cx, cz); continue; }
                for (int k = -ring; k <= ring; k++)
                {
                    Look(cx + k, cz - ring);
                    Look(cx + k, cz + ring);
                }
                for (int k = -ring + 1; k <= ring - 1; k++)
                {
                    Look(cx - ring, cz + k);
                    Look(cx + ring, cz + k);
                }
            }
            at = found;
            return best < float.MaxValue;
        }

        void Draw(Camera only)
        {
            int calls = 0;
            bool shadows = GraphicsQuality.Level >= 2;
            foreach (var g in groups)
                for (int pass = 0; pass < 2; pass++)
                {
                    var list = pass == 0 ? g.Frame : g.Shadowed;
                    if (g.Simple ? SkipFar : SkipNear) continue;
                    int count = list.Count;
                    if (count == 0) continue;
                    foreach (var (mesh, material) in g.Parts)
                    {
                        if (!mesh || !material) continue;
                        var rp = new RenderParams(material)
                        {
                            worldBounds = everywhere,
                            layer = Art.TreeLayer,
                            shadowCastingMode = pass == 1 && shadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                            receiveShadows = true,
                            camera = only,
                        };
                        for (int start = 0; start < count; start += 1023)
                        {
                            Graphics.RenderMeshInstanced(rp, mesh, 0, list, Mathf.Min(1023, count - start), start);
                            calls++;
                        }
                    }
                }
            Calls = calls;
        }
    }
}

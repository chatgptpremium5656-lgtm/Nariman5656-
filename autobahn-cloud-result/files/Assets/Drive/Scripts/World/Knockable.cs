using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Autobahn
{
    // Street furniture that gives way when a car drives into it: litter bins, sign posts, street
    // lamps, parking signs, ticket machines, benches, cones. Standing, it costs almost nothing:
    // its mesh is merged into one mesh per tile and material (KnockBatch) and it only has a box
    // collider. Hit, it drops out of the merged mesh, shows its own copy and becomes a loose
    // rigid body that is shoved along, falls and rolls. When the driver is far away it is put
    // back where it stood.
    //   Light things (bins, signs, cones) are triggers: the car goes through and knocks them
    //   over at any speed. Heavy things (lamps, ticket machines) are solid: slow, they stop the
    //   car like a post; fast (above MinKmh) they break off and the car keeps most of its speed.
    public sealed class Knockable : MonoBehaviour
    {
        public static int Count { get; private set; }
        public static bool Enabled = true;
        public const int DebrisLayer = 14;
        public static int Knocked { get; private set; }
        public static int Standing => Count - loose.Count;
        public static int Batches { get; private set; }

        public float Mass = 30, MinKmh;
        public bool Solid;
        Transform lampAnchor;
        Vector3 anchorHome;
        Vector3 homePosition;
        Quaternion homeRotation;
        Rigidbody body;
        BoxCollider box;
        MeshRenderer[] own = System.Array.Empty<MeshRenderer>();
        // Where this prop's pieces sit in its tile's merged meshes (one entry per material).
        readonly List<(KnockBatch batch, int first, int length)> slots = new();
        float looseAt;
        public bool Loose => body != null;

        static readonly List<Knockable> loose = new();

        // Every prop filed by where it was put up, in square cells: finding one by place, or all
        // those round a point, looks at a few cells instead of every prop on the island. Standing
        // props far from every player have their collider off (PhysicsRange): thousands of
        // bins, signs, lamps and guardrail sections that nobody can touch.
        public const float CellSize = 32;
        static readonly CellGrid<Knockable> grid = new(CellSize, (k, on) => k.SetCollider(on));
        long cell;
        bool filed;
        public static int PhysicsCells => grid.Awake;

        void File()
        {
            cell = grid.Add(this, transform.position);
            filed = true;
        }

        void Unfile()
        {
            if (!filed) return;
            filed = false;
            grid.Remove(this, cell);
        }

        // Standing, its collider only while a player is near; loose, it always has one.
        void SetCollider(bool on)
        {
            if (box && !Loose) box.enabled = on;
        }

        // Props filed within `radius` of `at` (by where they were put up), added to `into`.
        public static void Near(Vector3 at, float radius, List<Knockable> into) => grid.Near(at, radius, into);

        // Online: pieces are identified by what they are and where they stand.
        public static int KindKey(string name)
        {
            unchecked
            {
                int h = 17;
                foreach (char ch in name) h = h * 31 + ch;
                return h;
            }
        }

        static readonly List<Knockable> found = new();

        public static Knockable FindNear(int key, Vector3 at, float within)
        {
            Knockable best = null;
            float bestD = within * within;
            void Check(List<Knockable> list)
            {
                foreach (var k in list)
                {
                    if (!k) continue;
                    float d = (k.transform.position - at).sqrMagnitude;
                    if (d < bestD && KindKey(k.name) == key) { best = k; bestD = d; }
                }
            }
            found.Clear();
            grid.Near(at, within, found);
            Check(found);
            // The endless city moves its tiles (and their props) along with the car: there a
            // prop is no longer in the cell it was filed in, so look at all of them.
            if (!best && Route.Endless)
            {
                found.Clear();
                grid.All(found);
                Check(found);
            }
            return best;
        }
        static KnockHub hub;

        // Where a prop is built: its pieces go under the returned holder (local coordinates),
        // then End(holder, ...) finishes it.
        public static Transform Begin(Transform parent, string name, Vector3 at, float yaw = 0)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = at;
            t.localRotation = Quaternion.Euler(0, yaw, 0);
            return t;
        }

        // Merges the prop's own pieces, fits its collider (or uses `hit`, in the holder's space)
        // and registers it. `anchor` is a street-light anchor that goes dark when it falls.
        public static Knockable End(Transform holder, float mass, float minKmh = 0, bool solid = false, Bounds? hit = null, Transform anchor = null)
        {
            Art.Combine(holder);
            Art.Strip(holder);
            var ownList = new List<MeshRenderer>();
            bool any = false;
            var b = new Bounds();
            foreach (var r in holder.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled) continue;
                var f = r.GetComponent<MeshFilter>();
                if (f && f.sharedMesh && f.sharedMesh.isReadable && r.sharedMaterials.Length == 1 && r.transform.parent == holder)
                    ownList.Add(r);
                r.gameObject.layer = Art.PropLayer;
                var wb = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var l = holder.InverseTransformPoint(c);
                    if (!any) { b = new Bounds(l, Vector3.zero); any = true; }
                    else b.Encapsulate(l);
                }
            }
            if (hit.HasValue) b = hit.Value;
            if (!any && !hit.HasValue) b = new Bounds(Vector3.up * .5f, Vector3.one * .5f);
            var k = holder.gameObject.AddComponent<Knockable>();
            k.Mass = mass;
            k.MinKmh = minKmh;
            k.Solid = solid;
            k.own = ownList.ToArray();
            k.box = holder.gameObject.AddComponent<BoxCollider>();
            k.box.center = b.center;
            k.box.size = Vector3.Max(b.size, new Vector3(.2f, .2f, .2f));
            k.box.isTrigger = !solid;
            holder.gameObject.layer = 0;
            k.homePosition = holder.localPosition;
            k.homeRotation = holder.localRotation;
            if (anchor)
            {
                anchor.SetParent(holder, true);
                k.lampAnchor = anchor;
                k.anchorHome = anchor.localPosition;
            }
            Count++;
            k.File();
            if (!hub)
            {
                hub = new GameObject("Knockables").AddComponent<KnockHub>();
                Physics.IgnoreLayerCollision(DebrisLayer, 9, true);      // 9: traffic
            }
            return k;
        }

        // Called at the end of Art.Combine(parent): every standing prop under `parent` that is not
        // yet merged goes into one mesh per material, and its own renderers are switched off.
        public static void MergeUnder(Transform parent)
        {
            var list = parent.GetComponentsInChildren<Knockable>();
            if (list.Length == 0) return;
            var groups = new Dictionary<Material, List<(Knockable k, MeshRenderer r)>>();
            foreach (var k in list)
            {
                if (k.slots.Count > 0 || k.Loose) continue;
                foreach (var r in k.own)
                {
                    var m = r ? r.sharedMaterial : null;
                    if (!m) continue;
                    if (!groups.TryGetValue(m, out var g)) groups[m] = g = new();
                    g.Add((k, r));
                }
            }
            foreach (var pair in groups)
            {
                var entries = pair.Value;
                if (entries.Count < 2) continue;
                var combine = new CombineInstance[entries.Count];
                int at = 0;
                var batch = new KnockBatch();
                for (int i = 0; i < entries.Count; i++)
                {
                    var (k, r) = entries[i];
                    var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                    combine[i] = new CombineInstance { mesh = mesh, transform = parent.worldToLocalMatrix * r.transform.localToWorldMatrix };
                    k.slots.Add((batch, at, mesh.vertexCount));
                    at += mesh.vertexCount;
                    r.enabled = false;
                }
                var merged = new Mesh { name = "Knockables " + pair.Key.name, indexFormat = IndexFormat.UInt32 };
                merged.CombineMeshes(combine, true, true);
                var o = Art.MeshObject(parent, "Knockables " + pair.Key.name, merged, pair.Key);
                o.gameObject.layer = Art.PropLayer;
                var mr = o.GetComponent<MeshRenderer>();
                if (Art.NoShadow(pair.Key)) mr.shadowCastingMode = ShadowCastingMode.Off;
                batch.Mesh = merged;
                batch.Material = pair.Key;
                batch.Original = merged.vertices;
                batch.Current = (Vector3[])batch.Original.Clone();
                Batches++;
            }
        }

        void OnTriggerEnter(Collider other)
        {
            if (Loose || Solid) return;
            var rb = other.attachedRigidbody;
            if (!rb || rb.isKinematic || rb.mass < 300 || rb.GetComponent<Knockable>()) return;
            Vector3 v = rb.linearVelocity;
            if (v.magnitude < .8f) return;
            Knock(v * 1.15f + Vector3.up * (1 + v.magnitude * .12f), rb.worldCenterOfMass);
            // The car barely notices a bin; a little more for heavier things.
            rb.linearVelocity *= 1 - Mathf.Clamp01(Mass / (Mass + rb.mass)) * .6f;
        }

        // Something else heavy ran into a solid prop: a parked car shoved by the driver, a traffic
        // car thrown off its lane. It breaks off (above MinKmh) and the car keeps most of its speed.
        void OnCollisionEnter(Collision c)
        {
            if (Loose || !Solid) return;
            var rb = c.rigidbody;
            if (!rb || rb.isKinematic || rb.mass < 300 || rb.GetComponent<Vehicle>()) return;
            Vector3 pre = c.relativeVelocity;
            Vector3 toward = transform.position - rb.worldCenterOfMass;
            if (Vector3.Dot(pre, toward) < 0) pre = -pre;
            if (pre.magnitude * 3.6f < Mathf.Max(6, MinKmh * .5f)) return;
            Knock(pre + Vector3.up * 1.2f, rb.worldCenterOfMass);
            rb.linearVelocity = pre * (1 - Mathf.Clamp01(Mass / (Mass + rb.mass)) * 1.3f);
        }

        // A car ran into this (solid) prop: returns how much of the blow the car takes (0..1).
        public float Struck(Vehicle car, Vector3 before)
        {
            if (Loose) return 0;
            float kmh = before.magnitude * 3.6f;
            if (kmh < MinKmh) return 1;
            Knock(before * .9f + Vector3.up * 1.5f, car.Body.worldCenterOfMass);
            // The physics step treated it as a wall: give the car back most of its speed.
            float keep = 1 - Mathf.Clamp01(Mass / (Mass + car.Body.mass)) * 1.3f;
            car.Body.linearVelocity = before * keep;
            return .25f;
        }

        // Caught in an explosion.
        public void Blast(Vector3 at, float strength)
        {
            if (strength <= .05f) return;
            Vector3 away = transform.position - at;
            away.y = 0;
            away = (away.sqrMagnitude > .01f ? away.normalized : Random.onUnitSphere) + Vector3.up * .6f;
            if (!Loose) Knock(away * (12 * strength + 2), at);
            else body.AddForce(away * (12 * strength + 2), ForceMode.VelocityChange);
        }

        public void Knock(Vector3 velocity, Vector3 from)
        {
            if (Loose) return;
            Net.WorldNet.Knocked(this, velocity);
            Knocked++;
            foreach (var (batch, first, length) in slots) batch.Hide(first, length);
            foreach (var r in own) if (r) r.enabled = true;
            box.enabled = true;
            box.isTrigger = false;
            // Loose debris does not touch the traffic (a bin lying on the road threw passing
            // cars onto the pavement); the player's car, people and the ground still hit it.
            gameObject.layer = DebrisLayer;
            body = gameObject.AddComponent<Rigidbody>();
            body.mass = Mass;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.linearDamping = .05f;
            body.angularDamping = .3f;
            body.linearVelocity = velocity;
            // Tipped over from where it was hit: spin about the horizontal axis across the push.
            Vector3 flat = Vector3.ProjectOnPlane(velocity, Vector3.up);
            Vector3 axis = flat.sqrMagnitude > .01f ? Vector3.Cross(Vector3.up, flat.normalized) : Random.onUnitSphere;
            body.angularVelocity = axis * Random.Range(3f, 7f) + Random.insideUnitSphere * 1.5f;
            if (lampAnchor) lampAnchor.localPosition = anchorHome + Vector3.down * 2000;
            looseAt = Time.time;
            loose.Add(this);
        }

        public void Restore()
        {
            if (!Loose) return;
            Destroy(body);
            body = null;
            transform.localPosition = homePosition;
            transform.localRotation = homeRotation;
            box.isTrigger = !Solid;
            box.enabled = !filed || grid.IsOn(cell);
            gameObject.layer = 0;
            foreach (var (batch, first, length) in slots)
            {
                batch.Show(first, length);
                foreach (var r in own)
                    if (r && r.sharedMaterial == batch.Material) r.enabled = false;
            }
            if (lampAnchor) lampAnchor.localPosition = anchorHome;
            loose.Remove(this);
        }

        // Puts every prop back (a new run).
        public static void RestoreAll()
        {
            for (int i = loose.Count - 1; i >= 0; i--)
                if (loose[i]) loose[i].Restore();
            loose.Clear();
        }

        internal static void SwitchPhysics() => grid.Switch();

        // Loose props go back once the driver is well away and at least a while has passed.
        internal static void Tidy()
        {
            var cam = Camera.main;
            if (!cam) return;
            Vector3 c = cam.transform.position;
            for (int i = loose.Count - 1; i >= 0; i--)
            {
                var k = loose[i];
                if (!k) { loose.RemoveAt(i); continue; }
                if (Time.time - k.looseAt > 20 && (k.transform.position - c).sqrMagnitude > 180 * 180)
                    k.Restore();
                else if (k.transform.position.y < -60)
                    k.Restore();
            }
        }

        void OnDestroy()
        {
            loose.Remove(this);
            Unfile();
            // Its tile was unloaded (world streaming): the count is of props that exist.
            Count--;
        }
    }

    // The merged mesh of the standing props of one tile and material. A fallen prop's vertices
    // are collapsed onto one point (its triangles vanish) until it is put back.
    public sealed class KnockBatch
    {
        public Mesh Mesh;
        public Material Material;
        public Vector3[] Original, Current;

        public void Hide(int first, int length)
        {
            if (Mesh == null) return;
            Vector3 p = Original[first];
            for (int i = first; i < first + length; i++) Current[i] = p;
            Mesh.SetVertices(Current);
        }

        public void Show(int first, int length)
        {
            if (Mesh == null) return;
            System.Array.Copy(Original, first, Current, first, length);
            Mesh.SetVertices(Current);
        }
    }

    public sealed class KnockHub : MonoBehaviour
    {
        float next;

        // Before physics too: a car put down next to a prop finds it solid on its first step.
        void FixedUpdate() => Knockable.SwitchPhysics();

        void Update()
        {
            Knockable.SwitchPhysics();
            if (Time.time < next) return;
            next = Time.time + 1;
            Knockable.Tidy();
        }
    }
}

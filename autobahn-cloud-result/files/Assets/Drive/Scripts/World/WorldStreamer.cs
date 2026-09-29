using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // One piece of the island that only exists while the player is near it: a ground tile, a
    // road chunk, a city block, the town. It is built from its own seed, so it comes back exactly
    // the same when the player returns (online, a friend's broken props are found by where they
    // stand, see Net.WorldNet).
    public sealed class StreamUnit
    {
        public readonly string Name;
        public readonly Transform Parent;
        public readonly Vector3 Origin;
        // The ground it covers (world x/z): distances are measured to its nearest edge.
        public readonly Rect Area;
        // Builds the unit under the root it is given, yielding between parts so a big unit is
        // spread over several frames.
        public readonly System.Func<Transform, IEnumerator> Steps;
        // Called just before the root is destroyed (trees, lights and other links go first).
        public System.Action<Transform> Unloading;
        // Never unloaded (the garage every run starts from).
        public bool Keep;

        public Transform Root { get; internal set; }
        public bool Ready { get; internal set; }
        public int Builds { get; internal set; }
        internal IEnumerator Job;
        internal HashSet<Mesh> Owned;
        internal int SweepFrame;
        internal float Distance;

        public StreamUnit(string name, Transform parent, Vector3 origin, Rect area, System.Func<Transform, IEnumerator> steps)
        {
            Name = name;
            Parent = parent;
            Origin = origin;
            Area = area;
            Steps = steps;
        }
    }

    // Builds the island around the player and throws away what is left behind. The old world was
    // built whole at start (tens of seconds, all of it in memory) and only switched pieces on and
    // off. Now a piece is built when it comes within the view distance, a few milliseconds a
    // frame and at most one finished piece a frame, and destroyed with its meshes once it is well
    // beyond it. After a jump (a new run, a teleport, a test) the ground round the player is built
    // at once, so nobody falls through a world that is still on its way.
    [DefaultExecutionOrder(-400)]
    public sealed class WorldStreamer : MonoBehaviour
    {
        public static WorldStreamer Active { get; private set; }

        // Building time per frame, in milliseconds (a single part may run over).
        public static float BudgetMs = 4;
        // How far beyond the view distance a built piece is kept (driving along the edge of the
        // reach must not build and destroy the same piece over and over).
        public static float Margin = 300;
        // Built at once, within the frame, after a jump.
        public static float UrgentRadius = 300;
        // Always built round the focus while moving: a safety net if building falls behind.
        public const float NearRadius = 90;
        // The ground round the car stays while the driver walks, flies or sails away from it.
        public const float CarKeep = 160;
        const float Jump = 250;

        readonly List<StreamUnit> units = new();
        readonly List<StreamUnit> queue = new();
        readonly List<StreamUnit> drop = new();
        static readonly System.Comparison<StreamUnit> nearestFirst = (a, b) => a.Distance.CompareTo(b.Distance);
        Transform car;
        Rigidbody carBody;
        StreamUnit current;
        Vector3 lastFocus;
        bool placed;
        float nextPass;
        int unloadedSinceSweep;

        // Numbers for the logs and the automated checks.
        public int Count => units.Count;
        public int Standing { get; private set; }
        public int Builds { get; private set; }
        public int Unloads { get; private set; }
        public float WorstStepMs { get; private set; }
        public float BuildMs { get; private set; }

        public static WorldStreamer Ensure(GameObject host, Transform follow)
        {
            var s = host.GetComponent<WorldStreamer>();
            if (!s) s = host.AddComponent<WorldStreamer>();
            s.car = follow;
            s.carBody = follow ? follow.GetComponent<Rigidbody>() : null;
            Active = s;
            // The automated checks look around right after a teleport: more of the world at once.
            if (ProfileStore.Testing) UrgentRadius = Mathf.Max(UrgentRadius, 700);
            return s;
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
        }

        public StreamUnit Add(StreamUnit unit)
        {
            units.Add(unit);
            return unit;
        }

        // Unloads a unit and forgets it (its owner is going away).
        public void Remove(StreamUnit unit)
        {
            if (unit.Root || unit.Job != null) Unload(unit);
            units.Remove(unit);
            queue.Remove(unit);
            drop.Remove(unit);
        }

        public float Reach => GraphicsQuality.FarReach + 100 + Focus.Extra(car);

        static float Distance(Rect r, Vector3 p)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0, p.x - r.xMax);
            float dz = Mathf.Max(r.yMin - p.z, 0, p.z - r.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        Vector3 CarPoint() => carBody ? carBody.position : car ? car.position : Vector3.zero;

        // --- the frame loop ----------------------------------------------------------------------

        bool Paused => Route.Endless || !car || !isActiveAndEnabled;

        // Physics runs before Update: a car put down somewhere new gets its ground before its
        // first step there.
        void FixedUpdate()
        {
            if (!Paused) Guard();
        }

        void Update()
        {
            if (Paused) return;
            Guard();
            if (Time.unscaledTime >= nextPass)
            {
                nextPass = Time.unscaledTime + .1f;
                Pass();
            }
            bool busy = Work(BudgetMs);
            // Destroying is cheaper than building, but not free: one unit, and not on every
            // frame that also builds.
            if (!busy || Time.frameCount % 4 == 0) UnloadOne();
            Sweep();
        }

        // Builds what is needed right now: everything close by after a jump, and whatever the
        // player is about to reach if building has fallen behind (a fast plane, a slow computer).
        void Guard()
        {
            Vector3 f = Focus.Of(car);
            if (!placed || (f - lastFocus).sqrMagnitude > Jump * Jump)
            {
                placed = true;
                lastFocus = f;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int before = Builds;
                BuildAround(f, UrgentRadius);
                BuildAround(CarPoint(), NearRadius);
                foreach (var u in units)
                    if (u.Keep && !u.Ready) Finish(u);
                nextPass = 0;
                if (Builds > before) Debug.Log($"AUTOBAHN streaming jump to {f}: built {Builds - before} pieces in {clock.ElapsedMilliseconds} ms");
                return;
            }
            lastFocus = f;
            BuildAround(f, NearRadius);
        }

        // Decides what should stand: within the reach of the focus (and round the parked car)
        // is wanted, nearest first; well beyond it goes.
        void Pass()
        {
            Vector3 f = Focus.Of(car), c = CarPoint();
            float reach = Reach;
            queue.Clear();
            drop.Clear();
            foreach (var u in units)
            {
                float d = Distance(u.Area, f), dc = Distance(u.Area, c);
                u.Distance = Mathf.Min(d, dc);
                bool standing = u.Root != null;
                if (u.Keep || d < reach || dc < CarKeep)
                {
                    if (!u.Ready) queue.Add(u);
                }
                else if (standing && !RoadWorld.ShowAll && d > reach + Margin && dc > CarKeep + Margin)
                    drop.Add(u);
            }
            queue.Sort(nearestFirst);
        }

        // Runs build steps for up to `budgetMs`; at most one unit is finished per frame.
        bool Work(float budgetMs)
        {
            if (current != null && current.Job == null) current = null;
            if (current == null) current = Next();
            if (current == null) return false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (current != null && clock.Elapsed.TotalMilliseconds < budgetMs)
            {
                if (Step(current)) continue;
                current = null;
                break;
            }
            return true;
        }

        StreamUnit Next()
        {
            for (int i = 0; i < queue.Count; i++)
            {
                var u = queue[i];
                if (u.Ready) continue;
                queue.RemoveAt(i);
                if (u.Job == null) Begin(u);
                return u;
            }
            return null;
        }

        void UnloadOne()
        {
            if (drop.Count == 0) return;
            // The farthest first.
            int far = 0;
            for (int i = 1; i < drop.Count; i++)
                if (drop[i].Distance > drop[far].Distance) far = i;
            var u = drop[far];
            drop.RemoveAt(far);
            if (u.Root || u.Job != null) Unload(u);
        }

        // --- building and unloading ------------------------------------------------------------

        void Begin(StreamUnit u)
        {
            var root = new GameObject(u.Name).transform;
            root.SetParent(u.Parent, false);
            root.position = u.Origin;
            u.Root = root;
            u.Ready = false;
            u.Job = u.Steps(root);
        }

        // One part of a unit. Counters the automated checks read (parks, guardrails, NYC
        // buildings...) describe the world, not how often it was built: a rebuild leaves them.
        bool Step(StreamUnit u)
        {
            if (u.Job == null) return false;
            var tally = Tally.Take();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool more;
            try { more = u.Job.MoveNext(); }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                more = false;
            }
            finally
            {
                if (u.Builds > 0) tally.Restore();
            }
            float ms = (float)clock.Elapsed.TotalMilliseconds;
            BuildMs += ms;
            if (ms > WorstStepMs) WorstStepMs = ms;
            if (!more) Done(u);
            return more;
        }

        void Done(StreamUnit u)
        {
            u.Job = null;
            u.Ready = true;
            u.Builds++;
            Builds++;
            Standing++;
            // Everything the build made for this unit alone, including the pieces Art.Strip is
            // about to destroy: their meshes go a frame later, the rest when the unit goes.
            u.Owned = OwnedMeshes(u.Root);
            u.SweepFrame = Time.frameCount + 1;
        }

        void Finish(StreamUnit u)
        {
            if (u.Ready) return;
            if (u.Job == null) Begin(u);
            while (Step(u)) { }
            if (current == u) current = null;
        }

        void Unload(StreamUnit u)
        {
            if (u.Ready) Standing--;
            if (current == u) current = null;
            var root = u.Root;
            try { u.Unloading?.Invoke(root); }
            catch (System.Exception e) { Debug.LogException(e); }
            if (root)
            {
                var meshes = OwnedMeshes(root);
                if (u.Owned != null) meshes.UnionWith(u.Owned);
                Destroy(root.gameObject);
                foreach (var m in meshes)
                    if (m) Destroy(m);
            }
            u.Root = null;
            u.Job = null;
            u.Ready = false;
            u.Owned = null;
            Unloads++;
            unloadedSinceSweep++;
        }

        // A frame after a unit is finished: the meshes of the pieces Art.Strip destroyed are freed
        // (they were copies made only to be merged, and used to stay in memory for good). Now and
        // then, after many unloads, whatever else nobody uses any more (small leftovers).
        void Sweep()
        {
            foreach (var u in units)
            {
                if (u.SweepFrame == 0 || u.SweepFrame > Time.frameCount || u.Owned == null || !u.Root) continue;
                u.SweepFrame = 0;
                var live = OwnedMeshes(u.Root);
                foreach (var m in u.Owned)
                    if (m && !live.Contains(m)) Destroy(m);
                u.Owned = live;
            }
            if (unloadedSinceSweep >= 60 && RoadWorld.ShowAll)
            {
                unloadedSinceSweep = 0;
                Resources.UnloadUnusedAssets();
            }
        }

        // Meshes made at run time for this unit alone: merged batches (unnamed), knocked-prop
        // batches, unit-cube copies with metre UVs ("(Clone)") and the island ground. Imported
        // models, trees and the parked-car templates are shared and never freed here.
        static HashSet<Mesh> OwnedMeshes(Transform root)
        {
            var set = new HashSet<Mesh>();
            if (!root) return set;
            foreach (var f in root.GetComponentsInChildren<MeshFilter>(true))
                if (!f.GetComponent<TextMesh>() && !UnderParkedModel(f.transform, root)) Own(set, f.sharedMesh);
            foreach (var c in root.GetComponentsInChildren<MeshCollider>(true))
                Own(set, c.sharedMesh);
            return set;
        }

        static void Own(HashSet<Mesh> set, Mesh m)
        {
            if (!m) return;
            string n = m.name;
            if (n.Length == 0 || n.EndsWith("(Clone)") || n.StartsWith("Knockables ") || n == "Island ground")
                set.Add(m);
        }

        static bool UnderParkedModel(Transform t, Transform root)
        {
            for (; t && t != root; t = t.parent)
                if (t.name == "Parked model") return true;
            return false;
        }

        // --- asked for by others ---------------------------------------------------------------

        // Builds, within this frame, every unit that reaches within `radius` of `at`.
        public int BuildAround(Vector3 at, float radius)
        {
            int n = 0;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u.Ready || Distance(u.Area, at) > radius) continue;
                Finish(u);
                n++;
            }
            return n;
        }

        // The whole island (the map picture). Slow: this is what the old start-up did.
        public void BuildAll()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            foreach (var u in units)
                if (!u.Ready) { Finish(u); n++; }
            Debug.Log($"AUTOBAHN streaming: built all {units.Count} pieces ({n} new) in {clock.ElapsedMilliseconds} ms");
        }

        // Distance mode: the island goes away (the garage stays).
        public void UnloadAll()
        {
            foreach (var u in units)
                if (!u.Keep && (u.Root || u.Job != null)) Unload(u);
            queue.Clear();
            drop.Clear();
            current = null;
            placed = false;
        }

        // A pass now, and a jump check on the next frame (Stream(true) of the old code).
        public void Refresh(bool jump)
        {
            nextPass = 0;
            if (jump) placed = false;
        }

        // Builds, right now, what the player needs to start: the ground round the car and the
        // pieces that always stand (the garage a run starts in). The rest follows frame by frame.
        public void Warm()
        {
            placed = false;
            if (!Paused) Guard();
        }

        // True when everything in this x/z rectangle is built (the M map draws it live).
        public bool Covers(Rect area)
        {
            foreach (var u in units)
                if (!u.Ready && u.Area.Overlaps(area)) return false;
            return true;
        }

        public string Report() => $"{Standing}/{units.Count} pieces standing, {Builds} built, {Unloads} unloaded, {BuildMs / 1000:0.0} s building, worst step {WorstStepMs:0.0} ms";

        // --- helpers for the builders --------------------------------------------------------------

        // Runs `steps` with `enter` before and `exit` after every part (static state such as a
        // capture list or a flag must only be set while this unit's own code runs).
        public static IEnumerator Scoped(IEnumerator steps, System.Action enter, System.Action exit)
        {
            while (true)
            {
                bool more;
                enter();
                try { more = steps.MoveNext(); }
                finally { exit(); }
                if (!more) yield break;
                yield return steps.Current;
            }
        }

        // Runs `steps` with UnityEngine.Random seeded for every part: the result does not depend
        // on what else was built before, or in between.
        public static IEnumerator Seeded(IEnumerator steps, int seed)
        {
            int part = 0;
            while (true)
            {
                bool more;
                var saved = Random.state;
                Random.InitState(unchecked(seed + part++ * 7919));
                try { more = steps.MoveNext(); }
                finally { Random.state = saved; }
                if (!more) yield break;
                yield return steps.Current;
            }
        }

        // The x/z rectangle of a set of points, grown by `margin`.
        public static Rect AreaOf(IEnumerable<Vector3> points, float margin)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var p in points)
            {
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                z0 = Mathf.Min(z0, p.z); z1 = Mathf.Max(z1, p.z);
            }
            return Rect.MinMaxRect(x0 - margin, z0 - margin, x1 + margin, z1 + margin);
        }

        // Counters that building bumps (the automated checks compare them with fixed numbers).
        struct Tally
        {
            int nyc, parkBlocks, parkFences, parkTrees, parkSections, guardrails;

            public static Tally Take() => new()
            {
                nyc = GridCity.NycPlaced,
                parkBlocks = GridCity.BigParkBlocks,
                parkFences = GridCity.ParkFences,
                parkTrees = GridCity.ParkTrees,
                parkSections = Fences.ParkSections,
                guardrails = RoadWorld.GuardrailSections,
            };

            public void Restore()
            {
                GridCity.NycPlaced = nyc;
                GridCity.BigParkBlocks = parkBlocks;
                GridCity.ParkFences = parkFences;
                GridCity.ParkTrees = parkTrees;
                Fences.ParkSections = parkSections;
                RoadWorld.GuardrailSections = guardrails;
            }
        }
    }
}

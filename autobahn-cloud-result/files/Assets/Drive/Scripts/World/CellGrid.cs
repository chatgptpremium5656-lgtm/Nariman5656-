using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // Things filed by where they stand, in square cells: finding them by place looks at a few
    // cells instead of everything. With a switch, their physics is on only in the cells some
    // place of PhysicsRange reaches (street props, parked cars); the endless city moves its tiles
    // with the car, so there everything stays on.
    public sealed class CellGrid<T> where T : class
    {
        readonly float size;
        readonly System.Action<T, bool> switcher;
        readonly Dictionary<long, List<T>> cells = new();
        HashSet<long> awake = new(), next = new();
        int stamp = -1;
        bool everywhere;

        public CellGrid(float size, System.Action<T, bool> switcher = null)
        {
            this.size = size;
            this.switcher = switcher;
        }

        public int Cells => cells.Count;
        public int Awake => everywhere ? cells.Count : awake.Count;

        static long Key(int x, int z) => ((long)x << 32) | (uint)z;
        int CellOf(float v) => Mathf.FloorToInt(v / size);
        public long KeyOf(Vector3 p) => Key(CellOf(p.x), CellOf(p.z));

        // Files `item` standing at `at`; returns its cell for Remove. Its physics starts on or off.
        public long Add(T item, Vector3 at)
        {
            long key = KeyOf(at);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<T>();
            list.Add(item);
            if (switcher != null)
            {
                bool on = IsOn(key);
                if (!on && PhysicsRange.Near(at))
                {
                    // Built right by a player: the whole cell wakes up now, not at the next look.
                    awake.Add(key);
                    foreach (var other in list) switcher(other, true);
                }
                else switcher(item, on);
            }
            return key;
        }

        public void Remove(T item, long key)
        {
            if (!cells.TryGetValue(key, out var list)) return;
            list.Remove(item);
            if (list.Count == 0) cells.Remove(key);
        }

        public bool IsOn(long key) => everywhere || awake.Contains(key);

        // Everything filed in the cells within `radius` of `at` (callers check the exact distance).
        public void Near(Vector3 at, float radius, List<T> into)
        {
            int x0 = CellOf(at.x - radius), x1 = CellOf(at.x + radius);
            int z0 = CellOf(at.z - radius), z1 = CellOf(at.z + radius);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (cells.TryGetValue(Key(x, z), out var list))
                        into.AddRange(list);
        }

        public void All(List<T> into)
        {
            foreach (var list in cells.Values) into.AddRange(list);
        }

        // Called every frame and physics step; switches cells when PhysicsRange looked again.
        public void Switch()
        {
            if (switcher == null) return;
            PhysicsRange.Update();
            if (stamp == PhysicsRange.Stamp) return;
            stamp = PhysicsRange.Stamp;
            bool every = Route.Endless;
            if (every != everywhere)
            {
                everywhere = every;
                foreach (var list in cells.Values)
                    foreach (var item in list) switcher(item, every);
                awake.Clear();
            }
            if (everywhere) return;
            next.Clear();
            foreach (var p in PhysicsRange.Points)
            {
                float r = p.w;
                int x0 = CellOf(p.x - r), x1 = CellOf(p.x + r);
                int z0 = CellOf(p.z - r), z1 = CellOf(p.z + r);
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = Key(x, z);
                        if (!cells.ContainsKey(key)) continue;
                        // Nearest point of the cell to the place.
                        float dx = Mathf.Max(x * size - p.x, 0, p.x - (x + 1) * size);
                        float dz = Mathf.Max(z * size - p.z, 0, p.z - (z + 1) * size);
                        if (dx * dx + dz * dz <= r * r) next.Add(key);
                    }
            }
            foreach (var key in awake)
                if (!next.Contains(key)) Set(key, false);
            foreach (var key in next)
                if (!awake.Contains(key)) Set(key, true);
            (awake, next) = (next, awake);
        }

        void Set(long key, bool on)
        {
            if (!cells.TryGetValue(key, out var list)) return;
            foreach (var item in list) switcher(item, on);
        }
    }
}

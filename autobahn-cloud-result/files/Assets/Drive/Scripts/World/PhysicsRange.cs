using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // Where physics is needed: round the players (the local car and person, friends online in
    // their cars, on foot, in planes and boats), where the local person on foot is aiming, and
    // round anything about to blow up (a missile in the air, a falling bomb, a thrown one). Street
    // props and parked cars further away keep their looks but not their colliders (CellGrid);
    // boats and planes nobody is near sleep.
    public static class PhysicsRange
    {
        public const float Radius = 120;
        // How often the places are looked at again; a jump (a teleport, a new run) at once.
        public const float Every = .25f;
        const float JumpDistance = 30;

        // x, y, z and radius of every place.
        static readonly List<Vector4> points = new();
        static readonly List<Vector3> players = new(), previous = new(), people = new();
        static readonly List<Vector4> extra = new();
        static float nextTick, nextScan;
        static int frame = -1;
        static float fixedAt = -1;

        public static IReadOnlyList<Vector4> Points => points;
        // Changes whenever the places were looked at again (the grids switch then).
        public static int Stamp { get; private set; }

        // Cheap: a handful of positions, once per frame and physics step.
        public static void Update()
        {
            if (frame == Time.frameCount && fixedAt == Time.fixedTime) return;
            frame = Time.frameCount;
            fixedAt = Time.fixedTime;
            Players();
            bool jumped = players.Count != previous.Count;
            foreach (var p in players)
            {
                float best = float.MaxValue;
                foreach (var q in previous) best = Mathf.Min(best, (p - q).sqrMagnitude);
                if (best > JumpDistance * JumpDistance) jumped = true;
            }
            if (!jumped && Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + Every;
            previous.Clear();
            previous.AddRange(players);
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + Every;
                Extra();
            }
            points.Clear();
            foreach (var p in players) points.Add(new Vector4(p.x, p.y, p.z, Radius));
            points.AddRange(extra);
            Stamp++;
        }

        static void Players()
        {
            players.Clear();
            var run = RunSession.Active;
            if (run && run.Player) players.Add(run.Player.transform.position);
            var foot = FootPlayer.Active;
            if (foot && foot.InPlane) players.Add(foot.InPlane.transform.position);
            // The local person and friends walking about.
            Net.FootNet.People(people);
            players.AddRange(people);
            if (Net.CoopNet.Online && Net.CoopNet.Active != null)
                foreach (var car in Net.CoopNet.Active.Remotes.Values)
                    if (car && car.HasState) players.Add(car.transform.position);
            // Friends flying or sailing (their planes and boats follow their games here).
            foreach (var plane in Airplane.All)
                if (plane && plane.Flown) players.Add(plane.transform.position);
            foreach (var boat in Boat.All)
                if (boat && boat.Driven) players.Add(boat.transform.position);
        }

        // Things about to hit something far from the players.
        static void Extra()
        {
            extra.Clear();
            // A missile flies a couple of hundred metres between two looks: the stretch ahead.
            foreach (var m in Object.FindObjectsByType<Missile>(FindObjectsSortMode.None))
            {
                Vector3 p = m.transform.position, f = m.transform.forward;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 a = p + f * (k * 80);
                    extra.Add(new Vector4(a.x, a.y, a.z, 60));
                }
            }
            // A falling bomb, and its blast for the moment it knocks everything round it down.
            foreach (var n in Object.FindObjectsByType<Nuke>(FindObjectsSortMode.None))
            {
                Vector3 p = n.transform.position;
                extra.Add(new Vector4(p.x, p.y, p.z, NukeBlast.KnockRadius + 80));
            }
            foreach (var n in Object.FindObjectsByType<NukeBlast>(FindObjectsSortMode.None))
            {
                Vector3 p = n.transform.position;
                extra.Add(new Vector4(p.x, p.y, p.z, NukeBlast.KnockRadius + 20));
            }
            foreach (var b in Object.FindObjectsByType<Bomb>(FindObjectsSortMode.None))
            {
                Vector3 p = b.transform.position;
                extra.Add(new Vector4(p.x, p.y, p.z, 40));
            }
            // A person on foot aims further than the players' reach (a rifle): what the view
            // centre rests on, and a little round it.
            var foot = FootPlayer.Active;
            var cam = Camera.main;
            if (foot && !foot.Riding && !foot.Aboard && cam
                && Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, 800, (1 << 0) | (1 << 10), QueryTriggerInteraction.Ignore)
                && hit.distance > Radius * .5f)
                extra.Add(new Vector4(hit.point.x, hit.point.y, hit.point.z, 30));
        }

        // Within the reach of any place (the switching of props and parked cars being built).
        public static bool Near(Vector3 p)
        {
            foreach (var q in points)
            {
                float dx = p.x - q.x, dz = p.z - q.z;
                if (dx * dx + dz * dz < q.w * q.w) return true;
            }
            return false;
        }

        // A player (or the camera) within `distance` of p: boats and planes stay awake.
        public static bool PlayerNear(Vector3 p, float distance)
        {
            Update();
            float d2 = distance * distance;
            var cam = Camera.main;
            if (cam && (cam.transform.position - p).sqrMagnitude < d2) return true;
            foreach (var q in players)
                if ((q - p).sqrMagnitude < d2) return true;
            return false;
        }
    }
}

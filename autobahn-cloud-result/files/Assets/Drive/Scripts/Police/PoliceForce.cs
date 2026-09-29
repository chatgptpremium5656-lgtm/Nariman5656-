using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Autobahn
{
    // The police. For every wanted player cars come (one at one star … six at five), out of
    // sight behind and beside them, along the suspect's own trail where there is one; in view
    // they drive straight at them, ram a car and stop across the way of a slow one. From three
    // stars the officers get out and shoot. Whoever sees the suspect keeps the stars up (Wanted).
    // On foot, unarmed (fists, or kneeling to say sorry) next to an officer for three seconds is
    // an arrest; death or arrest clears the stars. Online the host runs the police like the
    // traffic, guests see copies (PoliceNet).
    public sealed class PoliceForce : MonoBehaviour
    {
        public static PoliceForce Active { get; private set; }
        public const int MaxCars = 8, MaxOfficers = 16, OfficerBase = 64;
        static readonly int[] CarsFor = { 0, 1, 2, 3, 4, 6 };
        // Set while a police car blows up: its blast is nobody's crime.
        public static bool OwnBlast;
        // Automated checks: no police at all for a while.
        public bool Suspended;
        public float ArrestProgress { get; private set; }
        public int Spawned { get; private set; }
        public int Arrests { get; private set; }

        readonly PoliceCar[] cars = new PoliceCar[MaxCars];
        readonly PoliceOfficer[] officers = new PoliceOfficer[MaxOfficers];
        public IReadOnlyList<PoliceCar> Cars => cars;
        public IReadOnlyList<PoliceOfficer> Officers => officers;
        // Where each wanted player has been, every 10 m: roads a police car can follow.
        readonly Dictionary<int, List<Vector3>> trails = new();
        readonly int[] trailBase = new int[Wanted.MaxSlots];
        readonly Dictionary<Object, float> runOverAt = new();
        RunSession run;
        Vehicle hooked;
        CarBody hookedArt;
        bool puppets, wasDead, hadNews;
        float nextLook, nextSpawn, nextNet, nextRunOver;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Active) return;
            var go = new GameObject("Police");
            DontDestroyOnLoad(go);
            Active = go.AddComponent<PoliceForce>();
        }

        void Awake() => Active = this;

        void OnDestroy()
        {
            if (hooked) { hooked.Impact -= OnImpact; hooked.Exploded -= OnExploded; }
            if (Active == this) Active = null;
        }

        void Update()
        {
            run = RunSession.Active;
            if (run == null || run.Player == null) return;
            bool authority = Wanted.Authority;
            // Joined or left a game: real units and copies are not the same thing.
            if (authority == puppets) { DestroyAll(); puppets = !authority; }
            if (!Wanted.Enabled || run.OnTitle)
            {
                if (AnyUnit()) DismissAll();
                if (run.OnTitle) Wanted.ClearAll();
                ArrestProgress = 0;
                return;
            }
            Hook();
            WatchLocal();
            if (authority) Command();
            else DropStale();
            if (Net.CoopNet.IsHost) Broadcast();
        }

        // ---- the local player ----------------------------------------------------------------

        void Hook()
        {
            var car = run.Player;
            if (car == hooked) return;
            if (hooked) { hooked.Impact -= OnImpact; hooked.Exploded -= OnExploded; }
            hooked = car;
            hookedArt = car.GetComponent<CarBody>();
            car.Impact += OnImpact;
            car.Exploded += OnExploded;
        }

        // Our car ran into something: a police car we drove into is a crime (not one that rammed us).
        void OnImpact(float severity, Collider c)
        {
            if (!c || severity < 5 || !hooked) return;
            var police = c.GetComponentInParent<PoliceCar>();
            if (!police || police.Wrecked) return;
            Vector3 toThem = police.transform.position - hooked.transform.position;
            toThem.y = 0;
            if (toThem.sqrMagnitude < .01f || Vector3.Dot(hooked.Body.linearVelocity, toThem.normalized) < 4) return;
            Attack(Crime.RammedPolice, police.Id, severity * 2);
        }

        // Blowing up at the wheel ends the chase like a death on foot.
        void OnExploded()
        {
            if (!FootManager.OnFoot && Wanted.Stars > 0) Wanted.Reset();
        }

        void WatchLocal()
        {
            var foot = FootPlayer.Active;
            bool dead = foot && foot.Dead;
            if (dead && !wasDead && Wanted.Stars > 0) Wanted.Reset();
            wasDead = dead;
            // Arrest: on foot, no gun in hand, right next to an officer for three seconds.
            bool unarmed = foot && !foot.Dead && !foot.Riding && !foot.Aboard && (foot.Def.Melee || foot.Kneeling);
            var cop = unarmed && Wanted.Stars > 0 ? NearestOfficer(foot.transform.position, 2.6f) : null;
            ArrestProgress = cop ? ArrestProgress + Time.deltaTime / 3 : Mathf.MoveTowards(ArrestProgress, 0, Time.deltaTime);
            if (ArrestProgress >= 1)
            {
                ArrestProgress = 0;
                Arrests++;
                Wanted.Reset();
                run.Scoring.Announce("ВЫ АРЕСТОВАНЫ  •  РОЗЫСК СНЯТ");
            }
            RunOvers();
        }

        // Our car through people on foot: officers are knocked down and hurt, friends only count
        // as a crime (their own game throws them).
        void RunOvers()
        {
            if (Time.time < nextRunOver || FootManager.OnFoot || !hooked || hooked.WreckedFor > 0) return;
            nextRunOver = Time.time + .1f;
            Vector3 v = hooked.Body.linearVelocity;
            float speed = new Vector3(v.x, 0, v.z).magnitude;
            if (speed < 4.5f) return;
            float half = hookedArt && hookedArt.Shape != null ? hookedArt.Shape.Width / 2 : 1;
            float len = hookedArt && hookedArt.Shape != null ? hookedArt.Shape.Length / 2 : 2.4f;
            foreach (var o in officers)
            {
                if (!o || !o.gameObject.activeSelf || o.Dead || !Under(o.transform.position, half, len) || Recent(o)) continue;
                float damage = Mathf.Clamp((speed * 3.6f - 12) * 2.4f, 8, 250);
                if (!o.Puppet) o.RunOver(v, 0);
                Attack(Crime.RanOver, o.Id, damage);
            }
            var net = Net.CoopNet.Active;
            if (!Net.CoopNet.Online) return;
            foreach (int slot in net.Players.Keys)
            {
                var a = Net.FootNet.AvatarOf(slot);
                if (!a || !a.Visible || a.Dead || a.Riding || !Under(a.transform.position, half, len) || Recent(a)) continue;
                Wanted.Report(Crime.RanOver);
            }
        }

        bool Under(Vector3 person, float half, float len)
        {
            Vector3 local = hooked.transform.InverseTransformPoint(person + Vector3.up * .9f);
            return Mathf.Abs(local.x) < half + .45f && Mathf.Abs(local.z) < len + .45f && Mathf.Abs(local.y) < 2;
        }

        bool Recent(Object who)
        {
            if (runOverAt.TryGetValue(who, out float at) && Time.time - at < 2) return true;
            if (runOverAt.Count > 64) runOverAt.Clear();
            runOverAt[who] = Time.time;
            return false;
        }

        PoliceOfficer NearestOfficer(Vector3 p, float within)
        {
            PoliceOfficer best = null;
            float bestD = within * within;
            foreach (var o in officers)
            {
                if (!o || !o.gameObject.activeSelf || o.Dead) continue;
                float d = (o.transform.position - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        // ---- crimes against the police -------------------------------------------------------

        // The local player attacked a police unit: the crime and the damage go to whoever runs
        // the police (unit: a car id, an officer id, or 255 for none).
        public static void Attack(Crime c, int unit, float damage)
        {
            if (!Wanted.Enabled) return;
            if (Net.CoopNet.IsGuest) { PoliceNet.SendCrime(c, unit, damage); return; }
            if (Active) Active.Apply(Wanted.LocalSlot, c, unit, damage);
            else Wanted.Add(Wanted.LocalSlot, c);
        }

        // Authority: a crime by `slot`, maybe against one of our units.
        public void Apply(int slot, Crime c, int unit, float damage)
        {
            Wanted.Add(slot, c);
            if (c == Crime.Cleared || damage <= 0) return;
            if (unit >= 0 && unit < MaxCars)
            {
                var car = cars[unit];
                if (car && car.gameObject.activeSelf) car.Damage(damage);
            }
            else if (unit >= OfficerBase && unit < OfficerBase + MaxOfficers)
            {
                var o = officers[unit - OfficerBase];
                if (!o || !o.gameObject.activeSelf || o.Dead) return;
                o.Damage(damage);
                if (o.Dead) Wanted.Add(slot, Crime.KilledOfficer);
            }
        }

        // Authority: an explosion hurts the police around it (Explosion pushes the cars itself).
        public void Blast(Vector3 at)
        {
            foreach (var car in cars)
            {
                if (!car || !car.gameObject.activeSelf || car.Wrecked || car.Puppet) continue;
                float f = 1 - Vector3.Distance(car.transform.position, at) / Explosion.Radius;
                if (f > 0) car.Damage(170 * f);
            }
            foreach (var o in officers)
            {
                if (!o || !o.gameObject.activeSelf || o.Dead || o.Puppet) continue;
                Vector3 away = o.transform.position - at;
                float f = 1 - away.magnitude / Explosion.Radius;
                if (f > 0) o.RunOver(away.normalized * 10 * f, 20 + 120 * f);
            }
        }

        // ---- running the police (host / offline) ---------------------------------------------

        // Where a player is, how they move and whether they drive; false when they are not about
        // (or out of reach: at sea, in the air).
        public bool Target(int slot, out Vector3 pos, out Vector3 vel, out bool driving)
        {
            pos = vel = Vector3.zero;
            driving = false;
            if (run == null || slot < 0) return false;
            if (slot == Wanted.LocalSlot)
            {
                var foot = FootPlayer.Active;
                if (foot)
                {
                    if (foot.Aboard) return false;
                    if (foot.Riding)
                    {
                        pos = foot.Riding.transform.position;
                        vel = foot.Riding.Velocity;
                        driving = true;
                        return true;
                    }
                    pos = foot.transform.position;
                    vel = foot.Velocity;
                    return true;
                }
                var car = run.Player;
                if (!car) return false;
                pos = car.transform.position;
                vel = car.Body.linearVelocity;
                driving = true;
                return true;
            }
            var net = Net.CoopNet.Active;
            if (!Net.CoopNet.Online || !net.Players.ContainsKey(slot)) return false;
            var avatar = Net.FootNet.AvatarOf(slot);
            if (avatar && avatar.Visible && !avatar.Riding)
            {
                pos = avatar.transform.position;
                return true;
            }
            if (net.Remotes.TryGetValue(slot, out var friend) && friend && friend.HasState)
            {
                pos = friend.transform.position;
                vel = friend.Velocity;
                driving = true;
                return true;
            }
            return false;
        }

        // Is the suspect holding a gun? Kneeling to say sorry is giving up.
        bool Armed(int slot)
        {
            if (slot == Wanted.LocalSlot)
            {
                var foot = FootPlayer.Active;
                return foot && !foot.Def.Melee && !foot.Kneeling;
            }
            var avatar = Net.FootNet.AvatarOf(slot);
            var rig = avatar ? avatar.GetComponentInChildren<HumanRig>() : null;
            return !rig || rig.Armed;
        }

        void Command()
        {
            float now = Time.time;
            if (Suspended && AnyUnit()) DismissAll();
            if (now >= nextLook)
            {
                nextLook = now + .25f;
                Look();
                Wanted.Tick();
            }
            for (int slot = 0; slot < Wanted.MaxSlots; slot++)
            {
                int stars = Wanted.StarsOf(slot);
                if (stars == 0 || !Target(slot, out Vector3 pos, out _, out _))
                {
                    trails.Remove(slot);
                    continue;
                }
                Record(slot, pos);
                if (Suspended || now < nextSpawn) continue;
                int have = 0;
                foreach (var car in cars)
                    if (car && car.gameObject.activeSelf && !car.Wrecked && !car.Leaving && car.TargetSlot == slot) have++;
                if (have < CarsFor[stars])
                    nextSpawn = now + (TrySpawn(slot, pos) ? 1.2f : .5f);
            }
            foreach (var car in cars) Drive(car);
            foreach (var o in officers) Lead(o);
        }

        // Police that can see a wanted player keep the stars up.
        void Look()
        {
            for (int slot = 0; slot < Wanted.MaxSlots; slot++)
            {
                if (Wanted.StarsOf(slot) == 0 || !Target(slot, out Vector3 pos, out _, out _)) continue;
                Vector3 eye = pos + Vector3.up * 1.2f;
                bool seen = false;
                foreach (var car in cars)
                    if (!seen && car && car.gameObject.activeSelf && !car.Wrecked && (car.transform.position - pos).sqrMagnitude < 150 * 150
                        && Clear(car.transform.position + Vector3.up * 1.4f, eye))
                        seen = true;
                foreach (var o in officers)
                    if (!seen && o && o.gameObject.activeSelf && !o.Dead && (o.transform.position - pos).sqrMagnitude < 80 * 80
                        && Clear(o.transform.position + Vector3.up * 1.6f, eye))
                        seen = true;
                if (seen) Wanted.Seen(slot);
            }
        }

        // Nothing solid (houses, hills) between two points.
        static bool Clear(Vector3 a, Vector3 b) => !Physics.Linecast(a, b, 1 << 0, QueryTriggerInteraction.Ignore);

        List<Vector3> TrailOf(int slot)
        {
            if (!trails.TryGetValue(slot, out var t)) trails[slot] = t = new List<Vector3>();
            return t;
        }

        void Record(int slot, Vector3 p)
        {
            var t = TrailOf(slot);
            if (t.Count > 0)
            {
                float d = (t[t.Count - 1] - p).sqrMagnitude;
                if (d < 10 * 10) return;
                // A jump (teleport, respawn): the old trail leads nowhere.
                if (d > 80 * 80) { trailBase[slot] += t.Count; t.Clear(); }
            }
            t.Add(p);
            if (t.Count > 120) { t.RemoveAt(0); trailBase[slot]++; }
        }

        bool TrySpawn(int slot, Vector3 target)
        {
            int free = -1;
            for (int i = 0; i < MaxCars && free < 0; i++)
                if (!cars[i] || !cars[i].gameObject.activeSelf) free = i;
            if (free < 0 || !SpawnPoint(slot, target, out Vector3 at, out Quaternion rot, out int trail)) return false;
            CarAt(free).Spawn(at, rot, slot, trail);
            Spawned++;
            return true;
        }

        // A free, unseen place for a police car 90–260 m from the suspect: back along their own
        // trail first, then the island road behind or ahead of them, then anywhere around.
        bool SpawnPoint(int slot, Vector3 target, out Vector3 at, out Quaternion rot, out int trail)
        {
            var t = TrailOf(slot);
            for (int i = t.Count - 2; i >= 0; i--)
            {
                float d = (t[i] - target).magnitude;
                if (d < 110 || d > 260) continue;
                Vector3 ahead = t[i + 1] - t[i];
                ahead.y = 0;
                var r = Quaternion.LookRotation(ahead.sqrMagnitude > .01f ? ahead : Vector3.forward);
                if (Fits(t[i], r, out at)) { rot = r; trail = trailBase[slot] + i + 1; return true; }
            }
            if (!Route.Endless && Mathf.Abs(Route.Lateral(target)) < 40)
            {
                float s = Route.Locate(target);
                for (int k = 0; k < 8; k++)
                {
                    float d = Random.Range(120f, 240f) * (k % 2 == 0 ? -1 : 1);
                    float z = s + d;
                    if (z < 60 || z > Route.Length - 50) continue;
                    var r = Route.Rotation(z);
                    if (d > 0) r *= Quaternion.Euler(0, 180, 0);
                    if (Fits(Route.Position(z, Random.Range(0, 3)), r, out at)) { rot = r; trail = -1; return true; }
                }
            }
            for (int k = 0; k < 16; k++)
            {
                Vector3 dir = Quaternion.Euler(0, Random.Range(0f, 360f), 0) * Vector3.forward;
                var r = Quaternion.LookRotation(-dir);
                if (Fits(target + dir * Random.Range(90f, 170f), r, out at) && Mathf.Abs(at.y - target.y) < 10) { rot = r; trail = -1; return true; }
            }
            at = default;
            rot = Quaternion.identity;
            trail = -1;
            return false;
        }

        bool Fits(Vector3 p, Quaternion r, out Vector3 at)
        {
            at = p;
            if (!SpawnSpot.Ground(p, out Vector3 g, 4, 8) || !SpawnSpot.CarFits(g, r) || Visible(g)) return false;
            at = g + Vector3.up * .35f;
            return true;
        }

        // Would a police unit here be seen appearing or vanishing? In our camera's view, or near
        // a friend.
        bool Visible(Vector3 p)
        {
            if (TrafficFlow.InSight(p, run.Player)) return true;
            if (!Net.CoopNet.Online) return false;
            foreach (int slot in Net.CoopNet.Active.Players.Keys)
                if (slot != Wanted.LocalSlot && Target(slot, out Vector3 q, out _, out _) && (q - p).sqrMagnitude < 90 * 90) return true;
            return false;
        }

        bool FarFromAll(Vector3 p, float metres)
        {
            for (int slot = 0; slot < Wanted.MaxSlots; slot++)
                if (Target(slot, out Vector3 q, out _, out _) && (q - p).sqrMagnitude < metres * metres) return false;
            return true;
        }

        void Drive(PoliceCar car)
        {
            if (!car || !car.gameObject.activeSelf) return;
            float now = Time.time;
            if (car.Wrecked)
            {
                // The burnt wreck stays a while and is cleared away out of sight.
                if (now - car.WreckedAt > 40 || now - car.WreckedAt > 12 && !Visible(car.transform.position)) Dismiss(car);
                return;
            }
            int slot = car.TargetSlot;
            int stars = Wanted.StarsOf(slot);
            if (stars == 0 || Suspended || !Target(slot, out Vector3 pos, out Vector3 vel, out bool driving))
            {
                Retire(car);
                return;
            }
            car.Leaving = false;
            car.Siren = true;
            Vector3 cp = car.transform.position;
            Vector3 to = pos - cp;
            to.y = 0;
            float dist = to.magnitude;
            car.SlowFor = car.Speed < 2 && dist > 30 && car.Crew.Count == 0 ? car.SlowFor + Time.deltaTime : 0;
            // Left far behind, or stuck on something: brought round again, out of sight (as the
            // traffic is).
            if ((dist > 450 || car.SlowFor > 8) && !Visible(cp))
            {
                if (SpawnPoint(slot, pos, out Vector3 at, out Quaternion rot, out int trail)) car.Spawn(at, rot, slot, trail);
                return;
            }
            if (car.Crew.Count > 0)
            {
                // Parked while the officers are out.
                car.Steer(cp, 0);
                return;
            }
            // Three stars: the officers get out once the car has caught up with a slow suspect.
            if (stars >= 3 && dist < 22 && car.Speed < 3 && vel.magnitude < 6 && now - car.DeployedAt > 5)
            {
                Deploy(car);
                if (car.Crew.Count > 0) return;
            }
            bool inView = dist < 80 && Clear(cp + Vector3.up * 1.4f, pos + Vector3.up * 1.2f);
            var t = TrailOf(slot);
            int idx = car.Trail - trailBase[slot];
            if (!inView && car.Trail >= 0 && idx < t.Count)
            {
                if (idx < 0) { idx = 0; car.Trail = trailBase[slot]; }
                // Breadcrumb reached: on to the next one; the last one: straight at them from there.
                while (idx < t.Count && (t[idx] - cp).sqrMagnitude < 14 * 14) { idx++; car.Trail++; }
                if (idx < t.Count)
                {
                    car.Steer(t[idx], dist > 200 ? 55 : 38);
                    return;
                }
            }
            // Straight at them: ram a moving car, stop across the way of a slow one, stay a few
            // metres off a person on foot.
            float speed = vel.magnitude;
            Vector3 aim = pos + new Vector3(vel.x, 0, vel.z) * Mathf.Clamp(dist / 35, 0, 1.2f);
            float want = driving && speed > 5 ? Mathf.Clamp(speed + 10, 18, 62) : Mathf.Clamp((dist - (driving ? 5 : 9)) * 1.1f, 0, 30);
            car.Steer(aim, want);
        }

        void Deploy(PoliceCar car)
        {
            car.DeployedAt = Time.time;
            foreach (float side in new[] { -1f, 1f })
            {
                int free = -1;
                for (int i = 0; i < MaxOfficers && free < 0; i++)
                    if (!officers[i] || !officers[i].gameObject.activeSelf) free = i;
                if (free < 0) return;
                var ct = car.transform;
                Vector3 door = ct.position + ct.right * side * (car.Width / 2 + .7f) + ct.forward * .4f;
                if (!SpawnSpot.Ground(door, out Vector3 g, 2, 4)) g = door;
                var o = OfficerAt(free);
                o.Spawn(g + Vector3.up * .05f, ct.eulerAngles.y + side * 90, car, car.TargetSlot);
                car.Crew.Add(o);
            }
        }

        // No one to chase: the crew walk back and get in, the car drives off and is gone once
        // nobody sees it.
        void Retire(PoliceCar car)
        {
            float now = Time.time;
            if (!car.Leaving) { car.Leaving = true; car.LeftAt = now; }
            car.Siren = false;
            Vector3 cp = car.transform.position;
            for (int i = car.Crew.Count - 1; i >= 0; i--)
            {
                var o = car.Crew[i];
                if (!o || !o.gameObject.activeSelf || o.Dead) { car.Crew.RemoveAt(i); continue; }
                o.Order(cp, cp, false);
                if ((o.transform.position - cp).sqrMagnitude < 3.2f * 3.2f || now - car.LeftAt > 25 && !Visible(o.transform.position))
                {
                    o.Hide();
                    car.Crew.RemoveAt(i);
                }
            }
            if (car.Crew.Count > 0) { car.Steer(cp, 0); return; }
            car.Steer(cp + car.transform.forward * 60, 14);
            if (now - car.LeftAt > 45 || !Visible(cp) && (now - car.LeftAt > 6 || FarFromAll(cp, 120))) Dismiss(car);
        }

        void Lead(PoliceOfficer o)
        {
            if (!o || !o.gameObject.activeSelf) return;
            float now = Time.time;
            var car = o.Car;
            bool carOk = car && car.gameObject.activeSelf && !car.Wrecked;
            if (o.Dead)
            {
                if (car) car.Crew.Remove(o);
                if (now - o.DiedAt > 30 || now - o.DiedAt > 10 && !Visible(o.transform.position)) o.Hide();
                return;
            }
            if (carOk && car.Leaving) return;     // Retire walks them back
            Vector3 p = o.transform.position;
            if (Wanted.StarsOf(o.TargetSlot) == 0 || Suspended || !Target(o.TargetSlot, out Vector3 pos, out Vector3 vel, out bool driving))
            {
                // Nothing to do and no car to go back to: gone once nobody looks.
                o.Order(p, p, false);
                if (!Visible(p)) o.Hide();
                return;
            }
            Vector3 away = p - pos;
            away.y = 0;
            float dist = away.magnitude;
            Vector3 dir = dist > .1f ? away / dist : -o.transform.forward;
            // The suspect drives off: back to the car, which takes up the chase again.
            if (carOk && (driving && vel.magnitude > 8 && dist > 25 || dist > 70))
            {
                Vector3 cp = car.transform.position;
                o.Order(cp, cp, false);
                if ((p - cp).sqrMagnitude < 3.2f * 3.2f)
                {
                    o.Hide();
                    car.Crew.Remove(o);
                }
                return;
            }
            Vector3 eye = pos + Vector3.up * (driving ? .9f : 1.2f);
            bool clear = dist < 50 && Clear(p + Vector3.up * 1.5f, eye);
            bool armed = driving || Armed(o.TargetSlot);
            Vector3 stand;
            if (!armed) stand = pos + dir * 1.3f;              // unarmed: close in and arrest
            else if (!clear || dist > 14) stand = pos + dir * 10;
            else stand = p;                                    // in range with a clear shot: stand and fire
            o.Order(stand, eye, armed && clear);
        }

        // ---- the pools -----------------------------------------------------------------------

        PoliceCar CarAt(int i) => cars[i] ? cars[i] : (cars[i] = PoliceCar.Create(transform, i, puppets));

        PoliceOfficer OfficerAt(int i) => officers[i] ? officers[i] : (officers[i] = PoliceOfficer.Create(transform, OfficerBase + i, puppets));

        void Dismiss(PoliceCar car)
        {
            foreach (var o in car.Crew)
                if (o) o.Hide();
            car.Hide();
        }

        public void DismissAll()
        {
            foreach (var car in cars) if (car) car.Hide();
            foreach (var o in officers) if (o) o.Hide();
            trails.Clear();
            ArrestProgress = 0;
        }

        void DestroyAll()
        {
            for (int i = 0; i < MaxCars; i++) { if (cars[i]) Destroy(cars[i].gameObject); cars[i] = null; }
            for (int i = 0; i < MaxOfficers; i++) { if (officers[i]) Destroy(officers[i].gameObject); officers[i] = null; }
            trails.Clear();
        }

        public bool AnyUnit()
        {
            foreach (var car in cars) if (car && car.gameObject.activeSelf) return true;
            foreach (var o in officers) if (o && o.gameObject.activeSelf) return true;
            return false;
        }

        // ---- online --------------------------------------------------------------------------

        void Broadcast()
        {
            float now = Time.unscaledTime;
            if (now < nextNet) return;
            nextNet = now + .15f;
            bool news = AnyUnit();
            for (int s = 0; s < Wanted.MaxSlots && !news; s++) news = Wanted.StarsOf(s) > 0;
            // One empty snapshot after the last unit is gone, then quiet.
            if (!news && !hadNews) return;
            hadNews = news;
            Net.CoopNet.Active.Send(PoliceNet.Snapshot(this));
        }

        // Guest: the host's police as they are now ('Q', see PoliceNet.Snapshot).
        public void Mirror(byte[] data)
        {
            if (Wanted.Authority) return;
            var r = new BinaryReader(new MemoryStream(data, 2, data.Length - 2));
            int me = Wanted.LocalSlot, myStars = 0;
            bool searching = false;
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                int slot = r.ReadByte(), stars = r.ReadByte(), flags = r.ReadByte();
                if (slot == me) { myStars = stars; searching = (flags & 1) != 0; }
            }
            Wanted.SetRemote(myStars, searching);
            n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadByte(), flags = r.ReadByte();
                var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float yaw = r.ReadUInt16() / 65535f * 360;
                float speed = r.ReadUInt16() / 100f;
                if (id < MaxCars) CarAt(id).Snapshot(p, Quaternion.Euler(0, yaw, 0), speed, (flags & 1) != 0, (flags & 2) != 0);
            }
            n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadByte(), flags = r.ReadByte(), target = r.ReadByte();
                byte shots = r.ReadByte(), hits = r.ReadByte();
                var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float yaw = r.ReadUInt16() / 65535f * 360;
                float forward = r.ReadSByte() / 16f;
                int k = id - OfficerBase;
                if (k < 0 || k >= MaxOfficers) continue;
                var o = OfficerAt(k);
                bool fresh = !o.gameObject.activeSelf;
                o.Snapshot(p, yaw, forward, (flags & 1) != 0, (flags & 2) != 0);
                o.TargetSlot = target == 255 ? -1 : target;
                int newShots = fresh ? 0 : (byte)(shots - o.Shots), newHits = fresh ? 0 : (byte)(hits - o.Hits);
                o.Shots = shots;
                o.Hits = hits;
                if (newShots <= 0 || newShots > 8) continue;
                Vector3 at = Target(o.TargetSlot, out Vector3 q, out _, out bool inCar) ? q + Vector3.up * (inCar ? .9f : 1.2f) : o.GunPoint + o.transform.forward * 30;
                o.ShotEffects(o.GunPoint, at);
                // Their bullets that hit us are applied here, by our own game.
                if (o.TargetSlot == me)
                    for (int h = 0; h < Mathf.Min(newHits, 3); h++) PoliceOfficer.HitLocal(o.GunPoint);
            }
        }

        // Guest: copies the host no longer mentions are gone.
        void DropStale()
        {
            float now = Time.time;
            foreach (var car in cars)
                if (car && car.gameObject.activeSelf && now - car.LastSnapshot > 1.5f) car.Hide();
            foreach (var o in officers)
                if (o && o.gameObject.activeSelf && now - o.LastSnapshot > 1.5f) o.Hide();
        }
    }
}

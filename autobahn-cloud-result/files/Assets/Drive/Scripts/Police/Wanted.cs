using UnityEngine;

namespace Autobahn
{
    // What the police care about. The byte value goes over the network ('U'), so keep the order.
    public enum Crime : byte { ShotCar, ShotPerson, Explosion, RanOver, TrafficWrecked, RammedPolice, KilledOfficer, Cleared }

    // The wanted level, 0–5 stars, of every player (by co-op slot; slot 0 offline). Crimes add
    // heat, heat makes stars; out of police sight for CalmDelay seconds the stars go away, and
    // death or arrest clears them at once. The host (or the offline game) keeps the numbers, like
    // it runs the traffic; a guest reports its crimes to the host and is told its stars back.
    public static class Wanted
    {
        public const int MaxSlots = 8;
        public const float CalmDelay = 30;
        static readonly float[] Threshold = { 1, 4, 8, 13, 19 };
        static readonly float[] heat = new float[MaxSlots];
        static readonly float[] seenAt = new float[MaxSlots];
        static readonly float[,] lastCrime = new float[MaxSlots, 8];
        // Online guest: our stars as the host last told us.
        static int remoteStars;
        static bool remoteSearching;
        static float remoteAt;

        // Automated checks run with the police off (a chase would spoil every other measurement),
        // except for the police check itself, which may also shorten the calm-down.
        public static bool TestMode;
        public static float TestCalmDelay = -1;
        public static bool Enabled => !ProfileStore.Testing || TestMode;
        static float Calm => TestCalmDelay > 0 ? TestCalmDelay : CalmDelay;

        public static bool Authority => !Net.CoopNet.IsGuest;
        public static int LocalSlot => Net.CoopNet.Online ? Mathf.Clamp(Net.CoopNet.Active.Slot, 0, MaxSlots - 1) : 0;

        public static int Stars
        {
            get
            {
                if (!Net.CoopNet.IsGuest) return StarsOf(LocalSlot);
                return Time.time - remoteAt < 3 ? remoteStars : 0;
            }
        }

        // Out of sight for a while: the stars flash while the police search.
        public static bool Searching => Net.CoopNet.IsGuest ? remoteSearching : SearchingFor(LocalSlot);

        public static int StarsOf(int slot)
        {
            if (slot < 0 || slot >= MaxSlots) return 0;
            int s = 0;
            while (s < Threshold.Length && heat[slot] >= Threshold[s]) s++;
            return s;
        }

        public static bool SearchingFor(int slot) => StarsOf(slot) > 0 && Time.time - seenAt[slot] > 4;

        // The local player did something: counted here, or sent to the host.
        public static void Report(Crime c)
        {
            if (!Enabled) return;
            if (Net.CoopNet.IsGuest) PoliceNet.SendCrime(c);
            else Add(LocalSlot, c);
        }

        public static void Add(int slot, Crime c)
        {
            if (!Enabled || slot < 0 || slot >= MaxSlots) return;
            if (c == Crime.Cleared) { Clear(slot); return; }
            float now = Time.time;
            int k = (int)c;
            // A burst of fire at one car is one crime, not thirty.
            if (lastCrime[slot, k] > 0 && now - lastCrime[slot, k] < .5f) return;
            lastCrime[slot, k] = now;
            heat[slot] = Mathf.Min(24, Mathf.Max(heat[slot] + Amount(c), Threshold[0]));
            // The police hear about it: the calm-down starts again.
            seenAt[slot] = now;
            if (ProfileStore.Testing) Debug.Log($"AUTOBAHN police: {c} by slot {slot}, heat {heat[slot]:0.0}, {StarsOf(slot)} star(s)");
        }

        static float Amount(Crime c) => c switch
        {
            Crime.ShotCar => .6f,
            Crime.ShotPerson => 2.5f,
            Crime.Explosion => 2,
            Crime.RanOver => 2.5f,
            Crime.TrafficWrecked => 2,
            Crime.RammedPolice => 1.5f,
            Crime.KilledOfficer => 4,
            _ => 0,
        };

        // A police unit has the player in view.
        public static void Seen(int slot)
        {
            if (slot >= 0 && slot < MaxSlots) seenAt[slot] = Time.time;
        }

        // Authority, a few times a second: stars run out when nobody has seen the player.
        public static void Tick()
        {
            float now = Time.time;
            for (int s = 0; s < MaxSlots; s++)
                if (heat[s] > 0 && now - seenAt[s] > Calm)
                    Clear(s);
        }

        public static void Clear(int slot)
        {
            if (slot < 0 || slot >= MaxSlots) return;
            heat[slot] = 0;
        }

        public static void ClearAll()
        {
            for (int s = 0; s < MaxSlots; s++) heat[s] = 0;
            remoteStars = 0;
            remoteSearching = false;
        }

        // Death or arrest of the local player.
        public static void Reset()
        {
            if (Net.CoopNet.IsGuest)
            {
                PoliceNet.SendCrime(Crime.Cleared);
                remoteStars = 0;
                remoteSearching = false;
            }
            else Clear(LocalSlot);
        }

        public static void SetRemote(int stars, bool searching)
        {
            remoteStars = stars;
            remoteSearching = searching;
            remoteAt = Time.time;
        }

        // ---- hooks from the game -----------------------------------------------------------

        // FootPlayer: a bullet or a fist of the local player hit `c`. True when it was a police
        // officer (hurt here, blood), false to let the shot go on as usual.
        public static bool Shot(Collider c, float damage)
        {
            if (!Enabled || !c) return false;
            var officer = c.GetComponentInParent<PoliceOfficer>();
            if (officer)
            {
                if (!officer.Dead)
                {
                    PoliceForce.Attack(Crime.ShotPerson, officer.Id, damage);
                    var foot = FootPlayer.Active;
                    if (foot) foot.HitMarkerTime = .2f;
                }
                return true;
            }
            var police = c.GetComponentInParent<PoliceCar>();
            if (police)
            {
                if (!police.Wrecked) PoliceForce.Attack(Crime.ShotCar, police.Id, damage);
                return false;
            }
            if (c.GetComponentInParent<Net.RemoteAvatar>()) Report(Crime.ShotPerson);
            else
            {
                var traffic = c.GetComponentInParent<TrafficCar>();
                var parked = c.GetComponentInParent<ParkedCarBody>();
                if (traffic && !traffic.Wrecked || parked && !parked.Wrecked || c.GetComponentInParent<Net.RemoteCar>())
                    Report(Crime.ShotCar);
            }
            return false;
        }

        // Explosion: every blast. Police units nearby are hurt (by whoever runs them); a blast the
        // local player set off (a bomb, a parked car or a boat blown up) near them is a crime. A
        // traffic car counts when it burns (TrafficWrecked), our own car never.
        public static void Blasted(Vector3 at, GameObject source, bool shared)
        {
            if (!Enabled) return;
            bool police = PoliceForce.OwnBlast;
            if (Authority && PoliceForce.Active) PoliceForce.Active.Blast(at);
            if (!shared || police) return;
            if (source && !source.GetComponent<ParkedCarBody>()) return;
            if (Near(at, 90)) Report(Crime.Explosion);
        }

        // TrafficCar: a traffic car just burnt out. Close to the local player it is on them.
        public static void TrafficWrecked(TrafficCar car)
        {
            if (Enabled && car && Near(car.transform.position, 60)) Report(Crime.TrafficWrecked);
        }

        static bool Near(Vector3 p, float metres)
        {
            var run = RunSession.Active;
            if (run == null || run.Player == null) return false;
            return (Focus.Of(run.Player.transform) - p).sqrMagnitude < metres * metres;
        }
    }
}

using UnityEngine;

namespace Autobahn
{
    // What a shot does to the local player's aim: the spread grows over a burst (bloom) and the
    // aim climbs (Pitch goes up) and settles back once the trigger is let go. Shotgun and sniper
    // kick hardest; aiming (Q) and crouching steady the gun.
    public sealed class WeaponRecoil
    {
        public float Bloom { get; private set; }   // extra spread, degrees
        public float Climb { get; private set; }   // how far the aim is still pushed up, degrees
        float lastShot = -10, interval = .1f;

        bool InBurst => Time.time - lastShot < interval + .08f;

        // A shot: lifts `pitch` (negative is up) and widens the next shots.
        public void Shot(WeaponDef def, bool aiming, bool crouching, ref float pitch)
        {
            float steady = (aiming ? .6f : 1) * (crouching ? .75f : 1);
            float kick = def.Kick * steady * Random.Range(.85f, 1.15f);
            // Only what the aim actually moved comes back (looking straight up it cannot climb).
            float lift = Mathf.Clamp(kick, 0, pitch + 80);
            pitch -= lift;
            Climb += lift;
            Bloom = Mathf.Min(Bloom + def.Bloom * steady, def.Bloom * 6);
            lastShot = Time.time;
            interval = def.Interval;
        }

        // Every frame: the climb comes back slowly during a burst, quickly after it. Pulling the
        // mouse down against the climb counts towards it, so the aim does not end up too low.
        public void Settle(float dt, float pulledDown, ref float pitch)
        {
            if (pulledDown > 0) Climb = Mathf.Max(0, Climb - pulledDown);
            bool burst = InBurst;
            Bloom *= Mathf.Exp(-dt * (burst ? .4f : 5));
            if (Bloom < .01f) Bloom = 0;
            float back = Climb * (1 - Mathf.Exp(-dt * (burst ? 2 : 7)));
            back = Mathf.Min(back, 80 - pitch);
            if (back <= 0) return;
            pitch += back;
            Climb -= back;
            if (Climb < .01f) Climb = 0;
        }

        public void Reset() { Bloom = Climb = 0; lastShot = -10; }
    }
}

using UnityEngine;

namespace Autobahn
{
    // A police officer on foot (from three stars they get out of their car). Where the police are
    // run he walks where PoliceForce sends him and shoots at the suspect with a pistol; the damage
    // is applied by the victim's own game (FootPlayer.TakeDamage, or the car's damage model), like
    // bullets between friends. Bullets hit him (the shootable layer), cars drive through him: the
    // game of whoever drives decides a car has knocked him down.
    public sealed class PoliceOfficer : MonoBehaviour
    {
        public int Id { get; private set; }
        public PoliceCar Car;
        public int TargetSlot = -1;
        public float Health { get; private set; } = 100;
        public bool Dead => Health <= 0;
        public bool Puppet { get; private set; }
        public float DiedAt { get; private set; }
        // Shots fired and hits scored (wrapping counters): guests replay them from the snapshots.
        public byte Shots, Hits;
        public bool Aiming { get; private set; }
        public float Forward { get; private set; }
        public HumanRig Rig { get; private set; }

        CapsuleCollider body;
        Transform gun;
        Vector3 muzzle, goal, aimAt;
        bool fire;
        float nextShot, knockedFor, yaw;
        Vector3 knock;

        static WeaponDef Pistol => Weapons.All[1];
        const int WallMask = 1 << 0;

        public static PoliceOfficer Create(Transform parent, int id, bool puppet)
        {
            var go = new GameObject("Police officer " + id);
            go.transform.SetParent(parent, false);
            var o = go.AddComponent<PoliceOfficer>();
            o.Id = id;
            o.Puppet = puppet;
            o.Rig = PoliceLook.Officer(go.transform);
            o.body = go.AddComponent<CapsuleCollider>();
            o.body.height = 1.8f;
            o.body.radius = .35f;
            o.body.center = new Vector3(0, .9f, 0);
            go.layer = Net.RemoteAvatar.ShootableLayer;
            o.gun = Weapons.Model(Pistol, go.transform, out o.muzzle);
            go.SetActive(false);
            return o;
        }

        public void Spawn(Vector3 at, float heading, PoliceCar car, int slot)
        {
            Car = car;
            TargetSlot = slot;
            Health = 100;
            knockedFor = 0;
            fire = false;
            Aiming = false;
            yaw = heading;
            goal = aimAt = at;
            transform.SetPositionAndRotation(at, Quaternion.Euler(0, yaw, 0));
            Rig.Revive();
            body.enabled = true;
            gameObject.SetActive(true);
        }

        public void Hide() => gameObject.SetActive(false);

        // Where to go, what to look at, and whether to shoot at it.
        public void Order(Vector3 moveTo, Vector3 lookAt, bool shoot)
        {
            goal = moveTo;
            aimAt = lookAt;
            fire = shoot;
        }

        public void Damage(float amount)
        {
            if (Dead) return;
            Health -= amount;
            if (Health <= 0) Die();
        }

        void Die()
        {
            Health = 0;
            DiedAt = Time.time;
            fire = false;
            Rig.Die();
            body.enabled = false;
            Weapons.Sound(Weapons.Die, transform.position + Vector3.up, .8f, .85f);
        }

        // Knocked down by a car (speed in m/s along `v`).
        public void RunOver(Vector3 v, float damage)
        {
            if (Dead) return;
            knock = new Vector3(v.x, 0, v.z) * .6f;
            knockedFor = .8f;
            Damage(damage);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (Puppet) { FollowNet(dt); return; }
            Vector3 p = transform.position;
            if (knockedFor > 0)
            {
                knockedFor -= dt;
                knock = Vector3.MoveTowards(knock, Vector3.zero, dt * 12);
                p += knock * dt;
            }
            else if (!Dead)
            {
                Vector3 to = goal - p;
                to.y = 0;
                float d = to.magnitude;
                float speed = d > .8f ? Mathf.Min(4.2f, d * 1.5f) : 0;
                Vector3 step = d > .01f ? to / d * speed * dt : Vector3.zero;
                // Not through walls.
                if (step.sqrMagnitude > 0 && Physics.SphereCast(p + Vector3.up * .9f, .3f, step.normalized, out _, step.magnitude + .3f, WallMask, QueryTriggerInteraction.Ignore))
                    step = Vector3.zero;
                p += step;
                Forward = step.magnitude / Mathf.Max(dt, 1e-4f);
                Aiming = fire;
                Vector3 look = fire ? aimAt - p : step;
                look.y = 0;
                if (look.sqrMagnitude > 1e-4f)
                    yaw = Mathf.MoveTowardsAngle(yaw, Quaternion.LookRotation(look).eulerAngles.y, dt * 540);
            }
            if (SpawnSpot.Ground(p, out Vector3 g, 1.2f, 3)) p.y = g.y;
            transform.SetPositionAndRotation(p, Quaternion.Euler(0, yaw, 0));
            Animate();
            if (fire && !Dead && knockedFor <= 0 && Time.time >= nextShot)
            {
                nextShot = Time.time + Random.Range(.8f, 1.4f);
                Shoot();
            }
        }

        void Animate()
        {
            Rig.Forward = Dead ? 0 : Forward;
            Rig.Strafe = 0;
            Rig.Grounded = true;
            Rig.Armed = !Dead;
            Rig.AimDirection = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
        }

        Vector3 Muzzle => gun ? gun.TransformPoint(muzzle) : transform.position + Vector3.up * 1.4f;

        // Authority: one pistol shot at the target; the hit is rolled here, applied by the victim.
        void Shoot()
        {
            Vector3 from = Muzzle;
            Vector3 dir = aimAt - from;
            float d = dir.magnitude;
            var force = PoliceForce.Active;
            bool driving = false;
            Vector3 targetVelocity = Vector3.zero;
            if (force) force.Target(TargetSlot, out _, out targetVelocity, out driving);
            float chance = Mathf.Lerp(.55f, .12f, Mathf.InverseLerp(8, 45, d));
            if (driving && targetVelocity.magnitude > 11) chance *= .5f;
            bool hit = d < Pistol.Range && Random.value < chance;
            Shots++;
            if (hit) Hits++;
            ShotEffects(from, hit ? aimAt : aimAt + Random.insideUnitSphere * 1.5f);
            if (hit && TargetSlot == Wanted.LocalSlot) HitLocal(from);
        }

        public void ShotEffects(Vector3 from, Vector3 to)
        {
            Rig.Shoot();
            Weapons.Flash(from, (to - from).normalized);
            Weapons.Tracer(from, to);
            Weapons.Sound(Weapons.Shot, from, .85f, Pistol.Pitch * .95f);
        }

        // A police bullet reached the local player: on foot through TakeDamage, in a car on the car.
        public static void HitLocal(Vector3 from)
        {
            var foot = FootPlayer.Active;
            if (foot)
            {
                if (foot.Dead || foot.Riding || foot.Aboard) return;
                foot.TakeDamage(9, -1);
                if (foot.Dead && FootManager.Instance) FootManager.Instance.LocalDeath("ВАС ЗАСТРЕЛИЛА ПОЛИЦИЯ", -1);
                return;
            }
            var run = RunSession.Active;
            var car = run != null ? run.Player : null;
            if (car == null || car.WreckedFor > 0) return;
            Vector3 local = car.transform.InverseTransformPoint(from);
            car.Damage.Apply(local.normalized * 1.2f, 7);
            if (car.Damage.Fatal) car.Explode();
        }

        // ---- online copy ---------------------------------------------------------------------
        Vector3 netPos;
        float netYaw, netAt;
        public float LastSnapshot => netAt;

        public void Snapshot(Vector3 p, float heading, float forward, bool dead, bool aiming)
        {
            if (!gameObject.activeSelf || (transform.position - p).sqrMagnitude > 20 * 20)
            {
                Spawn(p, heading, null, -1);
                yaw = heading;
            }
            netPos = p;
            netYaw = heading;
            netAt = Time.time;
            Forward = forward;
            Aiming = aiming;
            if (dead && !Dead) Die();
        }

        void FollowNet(float dt)
        {
            transform.position = Vector3.Lerp(transform.position, netPos, 1 - Mathf.Exp(-dt * 12));
            yaw = Mathf.LerpAngle(yaw, netYaw, 1 - Mathf.Exp(-dt * 14));
            transform.rotation = Quaternion.Euler(0, yaw, 0);
            Animate();
        }

        public Vector3 GunPoint => Muzzle;

        void LateUpdate()
        {
            if (!gun) return;
            gun.gameObject.SetActive(!Dead);
            var aim = Quaternion.Euler(0, yaw, 0);
            Vector3 hand = Rig.RightHand ? Rig.RightHand.position : transform.position + Vector3.up * 1.3f;
            gun.SetPositionAndRotation(hand + aim * new Vector3(-.02f, .03f, .05f), aim);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // A police car with a light bar and a siren. Where the police are run (the host, or offline)
    // it is a real body driven like a traffic car near a driver: velocity along its heading,
    // yaw towards its goal, so a ram exchanges momentum honestly. PoliceForce tells it where to
    // go and how fast. Online guests see a kinematic copy following the host's snapshots.
    public sealed class PoliceCar : MonoBehaviour
    {
        public int Id { get; private set; }
        public int TargetSlot = -1;
        public float Health { get; private set; } = 200;
        public bool Wrecked { get; private set; }
        public bool Puppet { get; private set; }
        public bool Siren;
        public bool Leaving;
        public float Length = 4.9f, Width = 1.9f, Height = 1.45f;
        public Rigidbody Body { get; private set; }
        public float Speed => Puppet ? netSpeed : Body.linearVelocity.magnitude;
        // Its officers while they are out on foot.
        public readonly List<PoliceOfficer> Crew = new();
        // The next breadcrumb of the suspect's trail to drive to (absolute index, -1: straight at them).
        public int Trail = -1;
        // Kept by PoliceForce: when it gave up the chase, last let its crew out, has crawled.
        public float LeftAt, DeployedAt = -99, SlowFor;
        public float SpawnedAt { get; private set; }
        public float WreckedAt { get; private set; }
        public float StuckFor { get; private set; }

        PoliceLook.LightBar bar;
        AudioSource siren;
        Renderer[] ownRenderers;
        Material[][] ownMaterials;
        Vector3 goal;
        float goalSpeed, stun, upsideFor, reverseFor;
        static PhysicsMaterial slide;

        // Ground under the wheels: default and road. Walls ahead: default only (buildings, rock).
        const int GroundMask = (1 << 0) | (1 << 10);
        const int WallMask = 1 << 0;

        public static PoliceCar Create(Transform parent, int id, bool puppet)
        {
            var go = new GameObject("Police car " + id);
            go.transform.SetParent(parent, false);
            go.layer = 9;
            var body = go.AddComponent<Rigidbody>();
            var box = go.AddComponent<BoxCollider>();
            var car = go.AddComponent<PoliceCar>();
            car.Id = id;
            car.Puppet = puppet;
            car.Body = body;
            Vector3 size = PoliceLook.BuildCar(go.transform);
            car.Width = size.x;
            car.Height = size.y;
            car.Length = size.z;
            box.size = new Vector3(size.x * .96f, size.y * .92f, size.z * .96f);
            box.center = new Vector3(0, size.y * .46f + .08f, 0);
            slide ??= new PhysicsMaterial("Police body") { dynamicFriction = .45f, staticFriction = .55f, bounciness = 0, bounceCombine = PhysicsMaterialCombine.Minimum, frictionCombine = PhysicsMaterialCombine.Average };
            box.sharedMaterial = slide;
            car.bar = PoliceLook.AddLightBar(go.transform, size);
            car.siren = go.AddComponent<AudioSource>();
            car.siren.clip = PoliceLook.Siren();
            car.siren.loop = true;
            car.siren.spatialBlend = 1;
            car.siren.rolloffMode = AudioRolloffMode.Logarithmic;
            car.siren.minDistance = 14;
            car.siren.maxDistance = 600;
            car.siren.volume = .75f;
            car.siren.playOnAwake = false;
            body.mass = 1800;
            body.linearDamping = .05f;
            body.angularDamping = .6f;
            body.centerOfMass = new Vector3(0, .4f, 0);
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.maxDepenetrationVelocity = 3;
            body.isKinematic = puppet;
            body.collisionDetectionMode = puppet ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.ContinuousDynamic;
            go.SetActive(false);
            return car;
        }

        public void Spawn(Vector3 at, Quaternion rotation, int slot, int trail)
        {
            Restore();
            TargetSlot = slot;
            Trail = trail;
            Leaving = false;
            Siren = true;
            Health = 200;
            stun = upsideFor = reverseFor = StuckFor = SlowFor = 0;
            DeployedAt = -99;
            SpawnedAt = Time.time;
            transform.SetPositionAndRotation(at, rotation);
            Body.position = at;
            Body.rotation = rotation;
            goal = at;
            goalSpeed = 0;
            gameObject.SetActive(true);
            if (!Body.isKinematic)
            {
                // Already rolling, as if it came round the corner.
                Body.linearVelocity = rotation * Vector3.forward * 16;
                Body.angularVelocity = Vector3.zero;
            }
        }

        public void Steer(Vector3 point, float speed)
        {
            goal = point;
            goalSpeed = speed;
        }

        public void Damage(float amount)
        {
            if (Wrecked) return;
            Health -= amount;
            if (Health <= 0) Wreck(true);
        }

        // Burnt out: charcoal paint, lights and siren off; `blast` plays the explosion.
        public void Wreck(bool blast)
        {
            if (Wrecked) return;
            Wrecked = true;
            WreckedAt = Time.time;
            Siren = false;
            ownRenderers = GetComponentsInChildren<Renderer>(true);
            ownMaterials = new Material[ownRenderers.Length][];
            for (int i = 0; i < ownRenderers.Length; i++)
            {
                var r = ownRenderers[i];
                ownMaterials[i] = r.sharedMaterials;
                if (r is ParticleSystemRenderer) continue;
                var burnt = new Material[ownMaterials[i].Length];
                for (int k = 0; k < burnt.Length; k++) burnt[k] = Explosion.Charred;
                r.sharedMaterials = burnt;
            }
            if (!blast) return;
            if (!Body.isKinematic) Body.AddForce(Vector3.up * 6 + Random.insideUnitSphere * 2, ForceMode.VelocityChange);
            bool was = PoliceForce.OwnBlast;
            PoliceForce.OwnBlast = true;
            try { Explosion.Blast(transform.position + Vector3.up * .8f, !Puppet); }
            finally { PoliceForce.OwnBlast = was; }
        }

        void Restore()
        {
            if (ownRenderers != null)
                for (int i = 0; i < ownRenderers.Length; i++)
                    if (ownRenderers[i]) ownRenderers[i].sharedMaterials = ownMaterials[i];
            ownRenderers = null;
            Wrecked = false;
            Crew.Clear();
        }

        public void Hide()
        {
            if (siren.isPlaying) siren.Stop();
            Crew.Clear();
            gameObject.SetActive(false);
        }

        // ---- online copy ---------------------------------------------------------------------
        Vector3 netPos;
        Quaternion netRot = Quaternion.identity;
        float netSpeed, netAt;
        public float LastSnapshot => netAt;

        public void Snapshot(Vector3 p, Quaternion r, float speed, bool sirenOn, bool wrecked)
        {
            if (!gameObject.activeSelf || (transform.position - p).sqrMagnitude > 30 * 30)
            {
                Spawn(p, r, -1, -1);
                transform.SetPositionAndRotation(p, r);
            }
            netPos = p;
            netRot = r;
            netSpeed = speed;
            netAt = Time.time;
            Siren = sirenOn;
            if (wrecked && !Wrecked) Wreck(true);
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (Puppet)
            {
                // Smooth follow with a little prediction, like the traffic seen by guests.
                Vector3 predicted = netPos + netRot * Vector3.forward * netSpeed * Mathf.Min(.25f, Time.time - netAt);
                Body.MovePosition(Vector3.Lerp(Body.position, predicted, 1 - Mathf.Exp(-dt * 10)));
                Body.MoveRotation(Quaternion.Slerp(Body.rotation, netRot, 1 - Mathf.Exp(-dt * 10)));
                return;
            }
            if (Wrecked) return;
            if (stun > 0) { stun -= dt; return; }
            // Tipped over: after a moment it is set back on its wheels.
            if (transform.up.y < .5f)
            {
                upsideFor += dt;
                if (upsideFor > 2.5f)
                {
                    Body.position += Vector3.up * .8f;
                    Body.rotation = Quaternion.LookRotation(Flat(transform.forward), Vector3.up);
                    Body.angularVelocity = Vector3.zero;
                    upsideFor = 0;
                }
                return;
            }
            upsideFor = 0;
            if (!Physics.Raycast(Body.position + Vector3.up * .6f, Vector3.down, 1.5f, GroundMask, QueryTriggerInteraction.Ignore)) return;

            Vector3 fwd = Flat(transform.forward);
            Vector3 to = goal - Body.position;
            to.y = 0;
            float dist = to.magnitude;
            Vector3 aim = dist > .5f ? to / dist : fwd;
            float want = goalSpeed;
            // A wall ahead (a house, a rock): look for a way round it, a little to either side.
            if (want > 4)
            {
                float reach = 5 + Speed * 1.1f;
                Vector3 eye = Body.position + Vector3.up * .8f;
                if (Physics.Raycast(eye, aim, out RaycastHit wall, Mathf.Min(reach, dist), WallMask, QueryTriggerInteraction.Ignore) && wall.normal.y < .6f)
                {
                    bool found = false;
                    foreach (float turn in new[] { 30f, -30f, 60f, -60f, 90f, -90f })
                    {
                        Vector3 side = Quaternion.Euler(0, turn, 0) * aim;
                        if (Physics.Raycast(eye, side, reach, WallMask, QueryTriggerInteraction.Ignore)) continue;
                        aim = side;
                        found = true;
                        break;
                    }
                    if (!found) want = Mathf.Min(want, 6);
                }
            }
            Vector3 v = Body.linearVelocity;
            Vector3 flat = new(v.x, 0, v.z);
            float along = Vector3.Dot(flat, fwd);
            // Pushing but not moving: back off for a moment, steering the other way.
            StuckFor = want > 5 && along < 1.5f ? StuckFor + dt : 0;
            if (StuckFor > 1.5f) { reverseFor = 1.3f; StuckFor = 0; }
            float err = Vector3.SignedAngle(fwd, aim, Vector3.up);
            if (reverseFor > 0)
            {
                reverseFor -= dt;
                want = -6;
                err = -err;
            }
            else
                want *= Mathf.Lerp(1, .3f, Mathf.Abs(err) / 120);   // slow for sharp turns
            float next = Mathf.MoveTowards(along, want, (Mathf.Abs(want) > Mathf.Abs(along) ? 11 : 16) * dt);
            // Grip: sliding sideways dies away quickly (a car, not a hovercraft).
            Vector3 lateral = (flat - fwd * along) * Mathf.Exp(-dt * 6);
            Vector3 drive = fwd * next + lateral;
            Body.linearVelocity = new Vector3(drive.x, v.y, drive.z);
            float maxYaw = Mathf.Lerp(1.1f, 2.1f, Mathf.Clamp01(Mathf.Abs(along) / 12));
            float yaw = Mathf.Clamp(err * Mathf.Deg2Rad * 3, -maxYaw, maxYaw);
            Vector3 w = Body.angularVelocity;
            Body.angularVelocity = new Vector3(w.x * .85f, yaw, w.z * .85f);
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0;
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
        }

        void Update()
        {
            // Flash red, red, blue, blue; each car in its own rhythm. The lights that throw colour
            // round only near the camera (they are the costly part).
            bool on = Siren && !Wrecked;
            int phase = (int)((Time.time + Id * .37f) * 9) % 8;
            bool red = on && (phase == 0 || phase == 2), blue = on && (phase == 4 || phase == 6);
            bar.Red.enabled = !on || red;
            bar.Blue.enabled = !on || blue;
            var cam = Camera.main;
            bool close = on && cam && (cam.transform.position - transform.position).sqrMagnitude < 160 * 160;
            bar.RedLight.enabled = close && red;
            bar.BlueLight.enabled = close && blue;
            bar.RedLight.intensity = bar.BlueLight.intensity = 90;
            bool loud = on && !AudioListener.pause;
            if (loud && !siren.isPlaying) { siren.time = Random.Range(0, siren.clip.length * .9f); siren.Play(); }
            if (!loud && siren.isPlaying) siren.Stop();
        }

        void OnCollisionEnter(Collision c)
        {
            if (Puppet || Wrecked || Body.isKinematic) return;
            float kick = c.impulse.magnitude / Mathf.Max(1, Body.mass);
            if (kick > 6)
            {
                stun = Mathf.Clamp(.4f + kick * .08f, .5f, 1.6f);
                Damage(kick * 3);
            }
        }
    }
}

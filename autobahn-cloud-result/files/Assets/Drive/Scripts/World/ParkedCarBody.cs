using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // A car parked at the kerb or in a car park. Each is its own object with a box collider
    // (merged into one mesh per car, not into the city block). Untouched it has no rigidbody
    // and costs nothing; hit by the driver at more than LooseKmh it gets one and is shoved,
    // rolls and spins, then settles and turns static again after RestDelay seconds. A hit at
    // BlowKmh or more, or the third hit, blows it up. When its city tile is recycled (or the
    // driver is far away and cannot see it) it goes back to its bay, whole again.
    public sealed class ParkedCarBody : MonoBehaviour
    {
        public const float LooseKmh = 15, BlowKmh = 70, Mass = 1400, RestDelay = 10;
        public static int Blown { get; private set; }
        static readonly List<ParkedCarBody> touched = new();
        static readonly List<ParkedCarBody> pending = new();
        static ParkedCarBody hub;
        bool isHub;

        public bool Wrecked { get; private set; }
        public bool Loose => body != null;
        public Rigidbody Body => body;
        public int Hits { get; private set; }

        Rigidbody body;
        BoxCollider box;
        Transform home;
        Vector3 homePosition, homeTileAt;
        Quaternion homeRotation;
        float still, nextCheck;
        Renderer[] renderers;
        Material[][] paint;

        // Called where a parked car is built, before its city tile or road chunk merges its
        // meshes: the car merges its own model instead and stays out of the block's mesh
        // (Art.Combine skips inactive objects). It is switched back on the next frame.
        public static void Hold(Transform parked)
        {
            Art.Combine(parked);
            Art.Strip(parked);
            var car = parked.gameObject.AddComponent<ParkedCarBody>();
            car.enabled = false;
            car.box = parked.GetComponent<BoxCollider>();
            parked.gameObject.SetActive(false);
            pending.Add(car);
            car.cell = grid.Add(car, parked.position);
            car.filed = true;
            if (!hub)
            {
                hub = new GameObject("Parked cars").AddComponent<ParkedCarBody>();
                hub.isHub = true;
            }
        }

        // Every parked car filed by its bay: standing in a bay far from every player it has no
        // collider (PhysicsRange); once shoved about it keeps one until it is back.
        static readonly CellGrid<ParkedCarBody> grid = new(64, (c, on) => c.SetCollider(on));
        long cell;
        bool filed;
        public static int PhysicsCells => grid.Awake;

        void SetCollider(bool on)
        {
            if (box && !body) box.enabled = on;
        }

        void OnDestroy()
        {
            if (filed) grid.Remove(this, cell);
            filed = false;
        }

        // Puts every car that was knocked about back in its bay (a new run).
        public static void RestoreAll()
        {
            for (int i = touched.Count - 1; i >= 0; i--)
                if (touched[i]) touched[i].Restore();
            touched.Clear();
        }

        // The driver's car ran into this one (called from Vehicle.OnCollisionEnter).
        // `before` is the driver's velocity before the physics step of the crash.
        public void Struck(Vehicle car, Collision hit, Vector3 before)
        {
            float kmh = hit.relativeVelocity.magnitude * 3.6f;
            if (kmh < LooseKmh) return;
            Hits++;
            if (!body)
            {
                Loosen();
                // The first contact treated this car as a wall. It is a car of similar weight:
                // the driver keeps half the speed lost, and that half goes into this one.
                Vector3 lost = before - car.Body.linearVelocity;
                Vector3 point = hit.contactCount > 0 ? hit.GetContact(0).point : transform.position;
                car.Body.linearVelocity += lost * .6f;
                // Shove it along the way the driver was going, with a little lift so the kerb
                // does not stop it dead (it only moved 10 cm at 40 km/h before).
                Vector3 push = Vector3.ProjectOnPlane(before, Vector3.up) * .75f + Vector3.up * 1.2f;
                body.linearVelocity = push;
                body.AddForceAtPosition(lost * (.25f * car.Body.mass), point, ForceMode.Impulse);
            }
            if (kmh >= BlowKmh || Hits >= 3)
                Blow();
        }

        // Caught in a nearby explosion: pushed away; right next to it, it goes up too.
        public void Blast(Vector3 at, float strength)
        {
            Loosen();
            Vector3 away = transform.position - at;
            away.y = 0;
            away = (away.sqrMagnitude > .01f ? away.normalized : Random.onUnitSphere) + Vector3.up * .45f;
            body.AddForce(away * (10 * strength + 2), ForceMode.VelocityChange);
            if (strength > .85f)
                Blow();
        }

        public void Blow()
        {
            if (Wrecked) return;
            Wrecked = true;
            Blown++;
            Loosen();
            renderers = GetComponentsInChildren<Renderer>();
            paint = new Material[renderers.Length][];
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                paint[i] = r.sharedMaterials;
                if (r is ParticleSystemRenderer) continue;
                var burnt = new Material[paint[i].Length];
                for (int k = 0; k < burnt.Length; k++) burnt[k] = Explosion.Charred;
                r.sharedMaterials = burnt;
            }
            Explosion.Blow(this);
        }

        void Loosen()
        {
            still = 0;
            enabled = true;
            if (body) return;
            if (!home)
            {
                home = transform.parent;
                homePosition = transform.localPosition;
                homeRotation = transform.localRotation;
                homeTileAt = home ? home.position : Vector3.zero;
                touched.Add(this);
            }
            // Off the tile's compound body, so it moves on its own.
            transform.SetParent(null, true);
            if (box) box.enabled = true;
            body = gameObject.AddComponent<Rigidbody>();
            body.mass = Mass;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.centerOfMass = new Vector3(0, .5f, 0);
        }

        void Restore()
        {
            if (body) DestroyImmediate(body);
            body = null;
            if (Wrecked && renderers != null)
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i]) renderers[i].sharedMaterials = paint[i];
            renderers = null;
            paint = null;
            var fire = transform.Find("Wreck fire");
            if (fire) Destroy(fire.gameObject);
            Wrecked = false;
            Hits = 0;
            enabled = false;
            touched.Remove(this);
            if (!home)
            {
                Destroy(gameObject);
                return;
            }
            transform.SetParent(home, false);
            transform.localPosition = homePosition;
            transform.localRotation = homeRotation;
            home = null;
            if (box) box.enabled = !filed || grid.IsOn(cell);
        }

        // Before physics too: a car put down next to a parked one finds it solid on its first step.
        void FixedUpdate()
        {
            if (isHub) grid.Switch();
        }

        void Update()
        {
            if (isHub)
            {
                grid.Switch();
                // Tiles and chunks built last frame have merged their meshes by now.
                if (pending.Count == 0) return;
                foreach (var car in pending)
                    if (car) car.gameObject.SetActive(true);
                pending.Clear();
                return;
            }
            // Its tile was thrown away (mode change): nothing to go back to.
            if (!home)
            {
                touched.Remove(this);
                Destroy(gameObject);
                return;
            }
            // The tile was recycled somewhere else: back in the bay, whole.
            if ((home.position - homeTileAt).sqrMagnitude > 1)
            {
                Restore();
                return;
            }
            if (body)
            {
                bool resting = body.linearVelocity.sqrMagnitude < .04f && body.angularVelocity.sqrMagnitude < .04f;
                still = resting ? still + Time.deltaTime : 0;
                if (still > RestDelay)
                {
                    DestroyImmediate(body);
                    body = null;
                }
                return;
            }
            // Settled: every so often, once the driver is far off and cannot see it, tidy up.
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + .5f;
            var player = RunSession.Active ? RunSession.Active.Player : null;
            if (player && (player.transform.position - transform.position).sqrMagnitude > 600 * 600
                && !TrafficFlow.InSight(transform.position, player))
                Restore();
        }
    }
}

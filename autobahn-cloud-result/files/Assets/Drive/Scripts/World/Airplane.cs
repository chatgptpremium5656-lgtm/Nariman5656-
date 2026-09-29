using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Autobahn
{
    // A light aircraft you can fly: the Piper Meridian model at the airport, with a simple but
    // honest flight model (thrust, lift from the angle of attack with a stall, drag that grows
    // with lift and with the gear down, a nose that follows the airflow), control surfaces that
    // bite with airspeed, a little turbulence in the air, retractable gear (faster and more
    // agile with it up; land without it and it is a crash) and four seats: pilot and three
    // passengers. Online the pilot's game owns it and sends where it is, like the boats.
    public enum AircraftKind { Light, Bomber, Airliner }

    public sealed class Airplane : MonoBehaviour
    {
        // What each kind is made of and how it flies. Big aircraft are heavier and slower to
        // answer the stick; their numbers are scaled so that they still lift off within the
        // island's runway.
        public sealed class Spec
        {
            public string Model, Name;
            public float Length;              // metres, nose to tail (the model is scaled to it)
            public float Mass, WingArea, Thrust, TopUp, TopDown, BiteSpeed;
            public Vector3 Inertia;
            public float RateScale = 1;       // how quickly it answers the stick
            public float WheelRadius = .2f;
            public int Seats = 4;
            public float CamDistance = 15;
            public bool Jet, Weapons;
            public float TorqueScale => Inertia.x / 9000f;
        }

        public static readonly Dictionary<AircraftKind, Spec> Specs = new()
        {
            [AircraftKind.Light] = new Spec { Model = "Planes/Piper/PiperMeridian", Name = "Piper Meridian", Length = 8.8f, Mass = 2200, WingArea = 18, Thrust = 12000, TopUp = 118, TopDown = 98, BiteSpeed = 38, Inertia = new(9000, 14000, 8000), Seats = 4, CamDistance = 15 },
            [AircraftKind.Bomber] = new Spec { Model = "Planes/B1/b1", Name = "B-1B Lancer", Length = 44.5f, Mass = 60000, WingArea = 260, Thrust = 380000, TopUp = 300, TopDown = 150, BiteSpeed = 70, Inertia = new(3.2e6f, 5.5e6f, 2.2e6f), RateScale = .75f, WheelRadius = .45f, Seats = 4, CamDistance = 48, Jet = true, Weapons = true },
            [AircraftKind.Airliner] = new Spec { Model = "Planes/A340/a340", Name = "Airbus A340-600", Length = 75, Mass = 90000, WingArea = 380, Thrust = 440000, TopUp = 250, TopDown = 140, BiteSpeed = 72, Inertia = new(9e6f, 1.5e7f, 7e6f), RateScale = .5f, WheelRadius = .6f, Seats = 6, CamDistance = 85, Jet = true },
        };

        public static readonly List<Airplane> All = new();
        public AircraftKind Kind { get; private set; }
        public Spec Type { get; private set; }
        public float HalfWidth { get; private set; } = .8f;
        public float HalfLength { get; private set; } = 4;
        public float Height { get; private set; } = 3.3f;
        // Beside the cockpit, on the left: where people get in and out.
        public Vector3 ExitPoint => transform.position + transform.forward * HalfLength * .45f - transform.right * (HalfWidth + 2.2f);
        public int Index => All.IndexOf(this);
        public Rigidbody Body { get; private set; }
        public float Health = 100;
        public bool Wrecked { get; private set; }
        public FootPlayer Pilot;
        public bool Flown => Pilot != null || RemoteFlown;
        public bool RemoteFlown => Remote && Time.time - remoteAt < 2;
        public bool GearDown { get; private set; } = true;
        public float Throttle { get; private set; }
        public float Kmh => Body ? Body.linearVelocity.magnitude * 3.6f : 0;
        public bool Grounded { get; private set; }
        public float Altitude { get; private set; }
        public bool Stalling { get; private set; }
        public static int Blown { get; private set; }

        float Mass => Type.Mass;
        float WingArea => Type.WingArea;
        float MaxThrust => Type.Thrust;
        Vector3 home;
        Quaternion homeRotation;
        Transform prop, gear;
        float gearT = 1;                 // 1 down, 0 up
        readonly List<SphereCollider> wheels = new();
        BoxCollider belly;
        AudioSource engine;
        Renderer[] renderers;
        Material[][] ownMaterials;
        float wreckedAt;
        float pitchIn, rollIn, yawIn;
        RaycastHit groundHit;
        bool braking;
        float seed;
        // For the automated checks: fly by numbers instead of keys (NaN = keys).
        public float TestThrottle = float.NaN, TestPitch, TestRoll, TestYaw;

        // Seats: the pilot (0), the co-pilot and the rest behind.
        public int SeatCount => seatPoints.Length;
        Vector3[] seatPoints =
        {
            new(-.35f, .75f, 1.3f), new(.35f, .75f, 1.3f), new(-.35f, .75f, .1f), new(.35f, .75f, .1f),
        };
        int[] occupant = { -1, -1, -1, -1 };
        float[] occupantSeen = new float[4];

        // Online: poses from the pilot's game.
        public bool Remote { get; private set; }
        Vector3 remotePos, remoteVel;
        Quaternion remoteRot;
        float remoteAt = -99;

        static readonly Dictionary<AircraftKind, GameObject> models = new();
        GameObject model;
        static PhysicsMaterial rolling;

        public static Airplane Spawn(Transform parent, Vector3 at, float yaw, AircraftKind kind = AircraftKind.Light)
        {
            var spec = Specs[kind];
            if (!models.TryGetValue(kind, out var m) || !m) models[kind] = m = Resources.Load<GameObject>(spec.Model);
            if (!m) return null;
            var root = new GameObject(spec.Name);
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(at, Quaternion.Euler(0, yaw, 0));
            var plane = root.AddComponent<Airplane>();
            plane.Kind = kind;
            plane.Type = spec;
            plane.model = m;
            if (kind == AircraftKind.Light) plane.Build();
            else plane.BuildLarge();
            plane.home = at;
            plane.homeRotation = root.transform.rotation;
            All.Add(plane);
            return plane;
        }

        static bool IsGear(string n)
        {
            n = n.ToLowerInvariant();
            if (n.Contains("door")) return false;
            return n.Contains("landinggear") || n.Contains("wheel") || n.Contains("pistonrod") || n.Contains("hydraulic") || n.Contains("rod_ldg") || n.Contains("_lgr") || n.Contains("lgr_");
        }

        void Build()
        {
            seed = Random.value * 100;
            // Built unrotated, then turned to face the way it should.
            Quaternion keep = transform.rotation;
            transform.rotation = Quaternion.identity;
            var visual = new GameObject("Airframe").transform;
            visual.SetParent(transform, false);
            var m = Instantiate(model, visual, false);
            foreach (var c in m.GetComponentsInChildren<Collider>(true)) Destroy(c);
            gear = new GameObject("Gear").transform;
            gear.SetParent(transform, false);
            prop = new GameObject("Propeller").transform;
            prop.SetParent(transform, false);
            prop.localPosition = new Vector3(0, 1.25f, 3.9f);
            foreach (var r in m.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.name.ToLowerInvariant();
                if (n.Contains("propeller")) r.transform.SetParent(prop, true);
                else if (IsGear(r.name)) r.transform.SetParent(gear, true);
            }
            // One mesh for the airframe, one for the gear, one for the propeller.
            Art.MergeModel(visual, "Piper airframe");
            Art.MergeModel(gear, "Piper gear");
            Art.MergeModel(prop, "Piper propeller");
            renderers = GetComponentsInChildren<Renderer>(true);

            gameObject.layer = 9;
            // The fuselage tapers up towards the tail: the rear box sits higher (room to rotate).
            AddBox("Fuselage", new Vector3(0, 1.35f, 1.5f), new Vector3(1.3f, 1.5f, 5.2f));
            AddBox("Tail cone", new Vector3(0, 1.65f, -2.4f), new Vector3(.9f, 1f, 2.8f));
            AddBox("Wings", new Vector3(0, .95f, .55f), new Vector3(12.3f, .3f, 1.7f));
            AddBox("Tail", new Vector3(0, 2.2f, -3.7f), new Vector3(4.3f, 1.6f, 1f));
            belly = AddBox("Belly", new Vector3(0, .62f, .6f), new Vector3(1.2f, .3f, 5f));
            belly.enabled = false;
            if (!rolling) rolling = new PhysicsMaterial("Tyres") { dynamicFriction = 0, staticFriction = 0, frictionCombine = PhysicsMaterialCombine.Minimum, bounciness = 0 };
            foreach (var p in new[] { new Vector3(0, .2f, 2.79f), new Vector3(-1.74f, .18f, .51f), new Vector3(1.74f, .18f, .51f) })
            {
                var w = new GameObject("Tyre").AddComponent<SphereCollider>();
                w.transform.SetParent(transform, false);
                w.transform.localPosition = p;
                w.radius = .2f;
                w.sharedMaterial = rolling;
                w.gameObject.layer = 9;
                wheels.Add(w);
            }

            Body = gameObject.AddComponent<Rigidbody>();
            Body.mass = Mass;
            Body.linearDamping = 0;
            Body.angularDamping = 2f;
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            Body.centerOfMass = new Vector3(0, 1f, .62f);
            Body.inertiaTensor = Type.Inertia;
            Body.inertiaTensorRotation = Quaternion.identity;

            engine = gameObject.AddComponent<AudioSource>();
            engine.clip = EngineClip();
            engine.loop = true;
            engine.spatialBlend = 1;
            engine.minDistance = 10;
            engine.maxDistance = 400;
            engine.volume = 0;
            engine.Play();
            transform.rotation = keep;
            // The body was made while the aircraft stood unrotated: tell it the real pose too.
            Body.position = transform.position;
            Body.rotation = keep;
        }

        // A big aircraft from any model: scaled to its length, sitting on its lowest point, the
        // gear found by its tyres, and colliders and wheels worked out from the shape itself
        // (fuselage, each wing in two parts, the tail).
        void BuildLarge()
        {
            seed = Random.value * 100;
            Quaternion keep = transform.rotation;
            transform.rotation = Quaternion.identity;
            var visual = new GameObject("Airframe").transform;
            visual.SetParent(transform, false);
            var m = Instantiate(model, visual, false).transform;
            foreach (var c in m.GetComponentsInChildren<Collider>(true)) Destroy(c);
            Bounds RendererBounds()
            {
                Bounds bb = default; bool any = false;
                foreach (var r in m.GetComponentsInChildren<Renderer>(true)) { if (!any) { bb = r.bounds; any = true; } else bb.Encapsulate(r.bounds); }
                bb.center = transform.InverseTransformPoint(bb.center);
                return bb;
            }
            var b = RendererBounds();
            m.localScale *= Type.Length / Mathf.Max(.1f, b.size.z);
            b = RendererBounds();
            m.localPosition -= new Vector3(b.center.x, b.min.y, b.center.z);
            b = RendererBounds();

            // The gear: tyres and wheels, and the struts under the wings and fuselage.
            gear = new GameObject("Gear").transform;
            gear.SetParent(transform, false);
            prop = new GameObject("No propeller").transform;
            prop.SetParent(transform, false);
            foreach (var r in m.GetComponentsInChildren<Renderer>(true))
            {
                bool tyre = false;
                foreach (var mat in r.sharedMaterials) if (mat && mat.name.ToLowerInvariant().Contains("tire")) tyre = true;
                string n = r.name.ToLowerInvariant();
                var rb = r.bounds;
                bool low = rb.center.y < b.min.y + b.size.y * .12f && rb.size.x < b.size.x * .06f;
                if (tyre || n.Contains("gear") || n.Contains("wheel") || low) r.transform.SetParent(gear, true);
            }
            Art.MergeModel(visual, Type.Name + " airframe");
            Art.MergeModel(gear, Type.Name + " gear");
            renderers = GetComponentsInChildren<Renderer>(true);

            // The shape, from the vertices (every few).
            var points = new List<Vector3>();
            foreach (var f in GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = f.sharedMesh;
                if (!mesh || !mesh.isReadable) continue;
                var vs = mesh.vertices;
                var toRoot = transform.worldToLocalMatrix * f.transform.localToWorldMatrix;
                int step = Mathf.Max(1, vs.Length / 20000);
                for (int i = 0; i < vs.Length; i += step) points.Add(toRoot.MultiplyPoint3x4(vs[i]));
            }
            float L = b.size.z, W = b.size.x, zMin = b.min.z, zMax = b.max.z;
            HalfLength = L / 2; Height = b.size.y;
            // Fuselage: the centre line in the middle of the length.
            // (A low percentile, not the lowest point: a centre-line landing gear is not the belly.)
            var centre = new List<float>();
            foreach (var p in points)
                if (Mathf.Abs(p.x) < W * .03f && p.z > zMin + L * .25f && p.z < zMax - L * .2f) centre.Add(p.y);
            centre.Sort();
            float fy0 = centre.Count > 10 ? centre[centre.Count / 12] : b.size.y * .15f;
            float fy1 = centre.Count > 10 ? centre[centre.Count * 19 / 20] : b.size.y * .45f;
            // Nothing but the tyres may touch the ground: every box starts above them.
            float clear = b.min.y + Type.WheelRadius * 2 + .4f;
            fy0 = Mathf.Max(fy0, clear);
            if (fy1 < fy0 + 1) fy1 = fy0 + Mathf.Max(1, b.size.y * .2f);
            float fh = fy1 - fy0, fw = fh * .95f;
            HalfWidth = fw / 2;
            gameObject.layer = 9;
            AddBox("Fuselage", new Vector3(0, (fy0 + fy1) / 2, (zMin + zMax) / 2), new Vector3(fw, fh, L * .96f));
            // Wings, inner and outer part each side.
            foreach (int side in new[] { -1, 1 })
                foreach (var (x0, x1) in new[] { (fw / 2, W * .25f), (W * .25f, W * .5f) })
                {
                    float wz0 = float.MaxValue, wz1 = float.MinValue, wy0 = float.MaxValue, wy1 = float.MinValue;
                    foreach (var p in points)
                    {
                        float ax = p.x * side;
                        if (ax < x0 || ax > x1 || p.y > fy1 || p.y < clear) continue;
                        wz0 = Mathf.Min(wz0, p.z); wz1 = Mathf.Max(wz1, p.z); wy0 = Mathf.Min(wy0, p.y); wy1 = Mathf.Max(wy1, p.y);
                    }
                    if (wz0 > wz1) continue;
                    wy0 = Mathf.Max(wy0, clear);
                    if (wy1 < wy0 + .4f) wy1 = wy0 + .4f;
                    float thick = Mathf.Clamp(wy1 - wy0, .4f, 3f);
                    AddBox("Wing", new Vector3(side * (x0 + x1) / 2, wy0 + thick / 2, (wz0 + wz1) / 2), new Vector3(x1 - x0, thick, Mathf.Max(1, wz1 - wz0)));
                }
            // The tail: whatever stands above the fuselage at the back.
            {
                float tx = 0, ty0 = float.MaxValue, ty1 = float.MinValue, tz0 = float.MaxValue, tz1 = float.MinValue;
                foreach (var p in points)
                    if (p.z < zMin + L * .18f && p.y > fy0) { tx = Mathf.Max(tx, Mathf.Abs(p.x)); ty0 = Mathf.Min(ty0, p.y); ty1 = Mathf.Max(ty1, p.y); tz0 = Mathf.Min(tz0, p.z); tz1 = Mathf.Max(tz1, p.z); }
                if (ty0 < ty1) AddBox("Tail", new Vector3(0, (ty0 + ty1) / 2, (tz0 + tz1) / 2), new Vector3(Mathf.Max(1, tx * 2), ty1 - ty0, Mathf.Max(1, tz1 - tz0)));
            }
            belly = AddBox("Belly", new Vector3(0, fy0 + .3f, (zMin + zMax) / 2), new Vector3(fw * .8f, .6f, L * .7f));
            belly.enabled = false;

            // Wheels where the model touches the ground: the nose wheel in front, the mains behind.
            float r0 = Type.WheelRadius;
            var low2 = points.FindAll(p => p.y < b.min.y + .35f);
            Vector3 nose = new(0, 0, zMax - L * .12f), mainL = new(-W * .1f, 0, 0), mainR = new(W * .1f, 0, 0);
            if (low2.Count > 3)
            {
                float zFront = float.MinValue;
                foreach (var p in low2) zFront = Mathf.Max(zFront, p.z);
                nose = new Vector3(0, 0, zFront - r0);
                Vector3 sl = Vector3.zero, sr = Vector3.zero; int nl = 0, nr = 0;
                foreach (var p in low2)
                {
                    if (p.z > zFront - L * .2f) continue;
                    if (p.x < -.3f) { sl += p; nl++; } else if (p.x > .3f) { sr += p; nr++; }
                }
                if (nl > 0) mainL = sl / nl;
                if (nr > 0) mainR = sr / nr;
                if (nl == 0 || nr == 0) { mainL = new Vector3(-fw, 0, (zMin + zMax) / 2); mainR = new Vector3(fw, 0, (zMin + zMax) / 2); }
            }
            if (!rolling) rolling = new PhysicsMaterial("Tyres") { dynamicFriction = 0, staticFriction = 0, frictionCombine = PhysicsMaterialCombine.Minimum, bounciness = 0 };
            foreach (var p in new[] { nose, mainL, mainR })
            {
                var w = new GameObject("Tyre").AddComponent<SphereCollider>();
                w.transform.SetParent(transform, false);
                w.transform.localPosition = new Vector3(p.x, b.min.y + r0, p.z);
                w.radius = r0;
                w.sharedMaterial = rolling;
                w.gameObject.layer = 9;
                wheels.Add(w);
            }

            // Seats in the cockpit and the cabin behind it.
            int n2 = Type.Seats;
            seatPoints = new Vector3[n2];
            occupant = new int[n2];
            occupantSeen = new float[n2];
            for (int i = 0; i < n2; i++)
            {
                occupant[i] = -1;
                seatPoints[i] = new Vector3(i % 2 == 0 ? -fw * .2f : fw * .2f, fy0 + fh * .35f, zMax - L * .1f - (i / 2) * L * .08f);
            }

            Body = gameObject.AddComponent<Rigidbody>();
            Body.mass = Mass;
            Body.linearDamping = 0;
            Body.angularDamping = 2f;
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            float wheelMid = (nose.z + (mainL.z + mainR.z) / 2) / 2;
            // Balanced a little ahead of the main wheels, like the real thing.
            Body.centerOfMass = new Vector3(0, fy0 + fh * .4f, Mathf.Lerp((mainL.z + mainR.z) / 2, nose.z, .12f));
            Body.inertiaTensor = Type.Inertia;
            Body.inertiaTensorRotation = Quaternion.identity;

            engine = gameObject.AddComponent<AudioSource>();
            engine.clip = JetClip();
            engine.loop = true;
            engine.spatialBlend = 1;
            engine.minDistance = 25;
            engine.maxDistance = 1200;
            engine.volume = 0;
            engine.Play();
            transform.rotation = keep;
            Body.position = transform.position;
            Body.rotation = keep;
        }

        BoxCollider AddBox(string name, Vector3 centre, Vector3 size)
        {
            var go = new GameObject(name);
            go.layer = 9;
            go.transform.SetParent(transform, false);
            var b = go.AddComponent<BoxCollider>();
            b.center = centre;
            b.size = size;
            return b;
        }

        // --- flying ---------------------------------------------------------------------------------
        void FixedUpdate()
        {
            if (Remote && Time.time - remoteAt < 2)
            {
                float k = 1 - Mathf.Exp(-Time.fixedDeltaTime * 8);
                Vector3 predicted = remotePos + remoteVel * Mathf.Min(.25f, Time.time - remoteAt);
                Body.MovePosition(Vector3.Lerp(Body.position, predicted, k));
                Body.MoveRotation(Quaternion.Slerp(Body.rotation, remoteRot, k));
                return;
            }
            if (Remote) { Remote = false; Body.isKinematic = false; }
            // Parked, whole and nobody near (the players online too, not only this camera):
            // asleep, without even the ground checks below.
            if (!Flown && !Wrecked && Body.linearVelocity.sqrMagnitude < .09f && !PhysicsRange.PlayerNear(transform.position, 250))
            {
                if (!Body.IsSleeping()) Body.Sleep();
                return;
            }

            float dt = Time.fixedDeltaTime;
            groundHit = default;
            Vector3 v = Body.linearVelocity;
            float speed = v.magnitude;
            Grounded = false;
            if (gearT > .9f)
                foreach (var w in wheels)
                    if (Physics.Raycast(w.transform.position, -transform.up, out groundHit, w.radius + .12f, ~(1 << 9 | 1 << 2), QueryTriggerInteraction.Ignore)) Grounded = true;
            if (Physics.Raycast(transform.position + Vector3.up, Vector3.down, out RaycastHit below, 3000, (1 << 0) | (1 << 10), QueryTriggerInteraction.Ignore))
                Altitude = Mathf.Max(0, transform.position.y - below.point.y);
            else Altitude = transform.position.y - Island.SeaY;

            // Into the sea: that is a crash (fast) or a ditching; either way it goes down.
            float waterDepth = Island.SeaY - (transform.position.y + .6f);
            if (waterDepth > 0)
            {
                if (!Wrecked && !Remote) Blow();
                Body.linearVelocity *= 1 - Mathf.Clamp01(dt * 2.5f);
                Body.angularVelocity *= 1 - Mathf.Clamp01(dt * 2f);
                if (waterDepth < 6) Body.AddForce(Vector3.up * Mass * 7f);
                return;
            }

            Vector3 local = transform.InverseTransformDirection(v);
            float aoa = speed > 2 ? Mathf.Atan2(-local.y, Mathf.Max(.1f, local.z)) : 0;
            // Flaps set for an easy take-off: lifts off at about 130 km/h.
            float cl = .5f + 5.5f * aoa;
            float stallAt = .28f;
            Stalling = Mathf.Abs(aoa) > stallAt && speed > 15 && !Grounded;
            if (Mathf.Abs(aoa) > stallAt) cl *= Mathf.Lerp(1, .3f, (Mathf.Abs(aoa) - stallAt) / .25f);
            cl = Mathf.Clamp(cl, -1f, 1.35f);
            float q = .5f * 1.225f * speed * speed;
            if (Wrecked) cl *= .2f;
            if (speed > 1)
            {
                Vector3 dir = v / speed;
                Vector3 liftDir = Vector3.Cross(dir, transform.right).normalized;
                if (Vector3.Dot(liftDir, transform.up) < 0) liftDir = -liftDir;
                float gearDrag = Mathf.Lerp(0, .022f, gearT);
                float cd = .028f + gearDrag + .045f * cl * cl;
                Body.AddForce(liftDir * q * WingArea * cl - dir * q * WingArea * cd);
            }
            // Thrust falls off with speed (a propeller); the gear down costs top speed.
            float top = Mathf.Lerp(Type.TopUp, Type.TopDown, gearT);
            float thrust = Wrecked ? 0 : Throttle * MaxThrust * Mathf.Clamp01(1 - speed / top);
            Body.AddForce(transform.forward * thrust);
            // Slipping sideways through the air (the fin and the fuselage): a strong side force.
            Body.AddForce(-transform.right * local.x * Mass * (Grounded ? 6 : 1.2f));

            // Controls bite with airspeed; with the gear up the aircraft is cleaner and livelier.
            float bite = Mathf.Clamp01(speed / Type.BiteSpeed);
            float ts = Type.TorqueScale, fast = Type.BiteSpeed * 1.2f;
            bite *= bite * Mathf.Lerp(1.3f, 1f, gearT);
            if (!Wrecked && Grounded)
                Body.AddRelativeTorque(new Vector3(-pitchIn * 15000, yawIn * 7000, -rollIn * 19000) * bite * ts);
            else if (!Wrecked)
            {
                // In the air the stick asks for a turn rate (pitch up to ~30°/s, roll ~90°/s,
                // rudder a little), and the surfaces work to give it: easy to hold, hard to loop
                // by accident.
                Vector3 rate = transform.InverseTransformDirection(Body.angularVelocity);
                Vector3 want = new Vector3(-pitchIn * .55f, yawIn * .3f, -rollIn * 1.6f) * Type.RateScale;
                // Soft limits (an arcade autopilot): the nose stops at 30° up / 45° down and the
                // bank at 70°, however long the key is held; a loop takes letting go and trying.
                float noseUp = Mathf.Asin(Mathf.Clamp(transform.forward.y, -1, 1)) * Mathf.Rad2Deg;
                float bank = Mathf.Asin(Mathf.Clamp(-transform.right.y, -1, 1)) * Mathf.Rad2Deg;
                if (noseUp > 30 && pitchIn > 0) want.x = Mathf.Min(.4f, (noseUp - 30) * .03f);
                if (noseUp < -45 && pitchIn < 0) want.x = Mathf.Max(-.4f, (noseUp + 45) * .03f);
                if (bank > 70 && rollIn > 0) want.z = Mathf.Min(.6f, (bank - 70) * .05f);
                if (bank < -70 && rollIn < 0) want.z = Mathf.Max(-.6f, (bank + 70) * .05f);
                if (want.x != -pitchIn * .55f * Type.RateScale) pitchIn = Mathf.Sign(pitchIn) * .1f;
                // Hands off: the aircraft eases back towards level flight.
                if (Mathf.Abs(pitchIn) < .05f && Mathf.Abs(noseUp) > 2) { want.x = Mathf.Clamp(noseUp * .012f, -.25f, .25f); pitchIn = .1f; }
                if (Mathf.Abs(rollIn) < .05f) want.z = Mathf.Clamp(bank * .012f, -.35f, .35f);
                Vector3 inertia = Body.inertiaTensor;
                Vector3 torque = Vector3.Scale(want - rate, inertia) * 3.5f;
                // Pitch and yaw only push where the stick is (the airflow does the rest).
                if (Mathf.Abs(pitchIn) < .05f) torque.x = 0;
                if (Mathf.Abs(yawIn) < .05f) torque.y = 0;
                Body.AddRelativeTorque(torque * bite);
            }
            // The nose follows the airflow (pitch and yaw stability).
            if (speed > 12 && !Grounded)
            {
                Vector3 axis = Vector3.Cross(transform.forward, v / speed);
                Vector3 stab = Vector3.Project(axis, transform.right) + Vector3.Project(axis, transform.up);
                Body.AddTorque(stab * 18000 * ts * Mathf.Clamp01(speed / fast));
                // A banked aircraft turns: the tail swings round a little with the bank.
                Body.AddTorque(Vector3.up * -transform.right.y * 2500 * ts * Mathf.Clamp01(speed / fast));
                // Turbulence: small gusts, felt more at speed.
                float t = Time.time * .8f;
                var gust = new Vector3(Mathf.PerlinNoise(seed, t) - .5f, Mathf.PerlinNoise(seed + 7, t) - .5f, Mathf.PerlinNoise(seed + 13, t * 1.3f) - .5f);
                float strength = Mathf.Clamp01(speed / 50) * Mathf.Clamp01(Altitude / 20);
                Body.AddRelativeTorque(new Vector3(gust.x * 2600, gust.y * 1200, gust.z * 4200) * strength * ts);
                Body.AddForce(Vector3.up * (Mathf.PerlinNoise(seed + 21, t * 1.6f) - .5f) * Mass * 2.2f * strength);
            }
            if (Grounded)
            {
                // Nose wheel steering, wheel brakes and rolling resistance.
                Body.AddTorque(Vector3.up * (yawIn + rollIn) * 9000 * ts * Mathf.Clamp01(speed / 4) * Mathf.Clamp01(1 - speed / fast));
                float resist = braking ? 5.5f : .25f;
                // Rolling on a moving deck (the carrier): relative to the deck.
                var deck = groundHit.rigidbody;
                Vector3 groundVel = deck ? deck.GetPointVelocity(groundHit.point) : Vector3.zero;
                Vector3 flat = Vector3.ProjectOnPlane(v - groundVel, transform.up);
                if (flat.sqrMagnitude > .01f) Body.AddForce(-flat.normalized * Mathf.Min(flat.magnitude / dt, resist) * Mass);
            }
        }

        void Update()
        {
            pitchIn = rollIn = yawIn = 0;
            braking = false;
            if (Pilot && Pilot == FootPlayer.Active && !Wrecked)
            {
                var run = RunSession.Active;
                bool paused = run == null || run.Paused || run.OnTitle || WorldMap.Open;
                var k = Keyboard.current;
                var pad = Gamepad.current;
                if (!paused)
                {
                    float throttleRate = 0;
                    if (k != null)
                    {
                        throttleRate = (k.leftShiftKey.isPressed ? 1 : 0) - (k.leftCtrlKey.isPressed || k.rightCtrlKey.isPressed ? 1 : 0);
                        // W: nose down, S: nose up (a stick).
                        pitchIn = (k.sKey.isPressed || k.downArrowKey.isPressed ? 1 : 0) - (k.wKey.isPressed || k.upArrowKey.isPressed ? 1 : 0);
                        rollIn = (k.dKey.isPressed || k.rightArrowKey.isPressed ? 1 : 0) - (k.aKey.isPressed || k.leftArrowKey.isPressed ? 1 : 0);
                        yawIn = (k.eKey.isPressed ? 1 : 0) - (k.qKey.isPressed ? 1 : 0);
                        braking = k.spaceKey.isPressed;
                        if (k.gKey.wasPressedThisFrame) ToggleGear();
                        // The bomber's weapons: missiles (left button) and the nuclear bomb (B, high up).
                        if (Type.Weapons)
                        {
                            var mouse = Mouse.current;
                            if (mouse != null && mouse.leftButton.wasPressedThisFrame) FireMissile();
                            if (k.bKey.wasPressedThisFrame) DropNuke();
                        }
                    }
                    if (pad != null)
                    {
                        throttleRate += pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue();
                        var stick = pad.leftStick.ReadValue();
                        if (pitchIn == 0) pitchIn = -stick.y;
                        if (rollIn == 0) rollIn = stick.x;
                        yawIn += (pad.rightShoulder.isPressed ? 1 : 0) - (pad.leftShoulder.isPressed ? 1 : 0);
                        braking |= pad.buttonWest.isPressed;
                        if (pad.buttonNorth.wasPressedThisFrame) ToggleGear();
                    }
                    Throttle = Mathf.Clamp01(Throttle + throttleRate * Time.deltaTime * .6f);
                }
            }
            else if (!RemoteFlown) Throttle = Mathf.MoveTowards(Throttle, 0, Time.deltaTime * .5f);
            if (!float.IsNaN(TestThrottle)) { Throttle = TestThrottle; pitchIn = TestPitch; rollIn = TestRoll; yawIn = TestYaw; }

            // The gear swings up into the wings and the nose.
            gearT = Mathf.MoveTowards(gearT, GearDown ? 1 : 0, Time.deltaTime / 2.5f);
            gear.localPosition = new Vector3(0, (1 - gearT) * (Kind == AircraftKind.Light ? .55f : Height * .07f), 0);
            gear.localScale = new Vector3(1, Mathf.Lerp(.25f, 1, gearT), 1);
            bool gearOut = gearT > .05f;
            foreach (var r in gear.GetComponentsInChildren<Renderer>()) if (r.enabled != gearOut) r.enabled = gearOut;
            bool wheelsOn = gearT > .9f;
            foreach (var w in wheels) w.enabled = wheelsOn;
            belly.enabled = !wheelsOn;

            prop.Rotate(0, 0, (Throttle * 1900 + (Flown ? 400 : 0)) * Time.deltaTime, Space.Self);
            engine.volume = Wrecked ? 0 : Mathf.MoveTowards(engine.volume, Flown ? .3f + Throttle * .5f : 0, Time.deltaTime);
            engine.pitch = .6f + Throttle * .7f + Kmh / 500f;

            if (Wrecked && Time.time - wreckedAt > 30) Restore();
        }

        // --- weapons (the bomber) -------------------------------------------------------------------
        float nextMissile, nextNuke;
        public float NukeReady => Mathf.Max(0, nextNuke - Time.time);
        public int MissileSide { get; private set; }

        public bool FireMissile()
        {
            if (!Type.Weapons || Wrecked || Time.time < nextMissile) return false;
            nextMissile = Time.time + .7f;
            MissileSide = 1 - MissileSide;
            Vector3 at = transform.TransformPoint(new Vector3(MissileSide == 0 ? -HalfWidth - 1.5f : HalfWidth + 1.5f, Height * .2f, HalfLength * .2f));
            Missile.Fire(at, transform.rotation, Body.linearVelocity, true, transform);
            Net.PlaneNet.Launched(this, at, transform.rotation, Body.linearVelocity, false);
            return true;
        }

        public bool DropNuke()
        {
            if (!Type.Weapons || Wrecked || Time.time < nextNuke) return false;
            if (Altitude < Nuke.MinAltitude)
            {
                var run = RunSession.Active;
                run?.Scoring.Announce($"БОМБА — НАБЕРИТЕ ВЫСОТУ {Nuke.MinAltitude:0} М");
                return false;
            }
            nextNuke = Time.time + 25;
            Vector3 at = transform.position - transform.up * (Height * .15f);
            Nuke.Drop(at, Body.linearVelocity, true, transform);
            Net.PlaneNet.Launched(this, at, transform.rotation, Body.linearVelocity, true);
            return true;
        }

        public void ToggleGear()
        {
            // Never up on the ground.
            if (GearDown && (Grounded || Altitude < 3)) return;
            GearDown = !GearDown;
        }

        public void SetGear(bool down) => GearDown = down;

        // --- damage -------------------------------------------------------------------------------
        public void Damage(float amount)
        {
            if (Wrecked || Remote) return;
            Health -= amount;
            if (Health <= 0) Blow();
        }

        void OnCollisionEnter(Collision c)
        {
            if (Remote || Wrecked) return;
            // How hard it hit: the speed into the surface, not the speed sliding along it
            // (a scrape along the runway is not a crash; flying into a wall is).
            float v = 0;
            bool onWheels = false;
            foreach (var contact in c.contacts)
            {
                v = Mathf.Max(v, Mathf.Abs(Vector3.Dot(c.relativeVelocity, contact.normal)));
                if (contact.thisCollider is SphereCollider) onWheels = true;
            }
            float limit = onWheels ? 9 : 5;
            if (v > limit) Damage((v - limit) * (onWheels ? 12 : 20));
        }

        public void Blow(bool fromNetwork = false)
        {
            if (Wrecked) return;
            Wrecked = true;
            wreckedAt = Time.time;
            Blown++;
            Health = 0;
            Throttle = 0;
            var local = FootPlayer.Active;
            if (local && local.InPlane == this) local.LeavePlane(true);
            ownMaterials = new Material[renderers.Length][];
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (!r) continue;
                ownMaterials[i] = r.sharedMaterials;
                var burnt = new Material[ownMaterials[i].Length];
                for (int k = 0; k < burnt.Length; k++) burnt[k] = Explosion.Charred;
                r.sharedMaterials = burnt;
            }
            Explosion.Blast(transform.position + Vector3.up * 1.2f, !fromNetwork);
            if (!fromNetwork) Net.PlaneNet.Exploded(this);
        }

        void Restore()
        {
            Wrecked = false;
            Health = 100;
            GearDown = true;
            gearT = 1;
            Throttle = 0;
            if (ownMaterials != null)
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] && ownMaterials[i] != null) renderers[i].sharedMaterials = ownMaterials[i];
            ownMaterials = null;
            Remote = false;
            remoteAt = -99;
            Body.isKinematic = false;
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            Body.position = home;
            Body.rotation = homeRotation;
            transform.SetPositionAndRotation(home, homeRotation);
        }

        public static void RestoreAll()
        {
            foreach (var p in All)
                if (p && !p.Flown) p.Restore();
        }

        // --- seats ---------------------------------------------------------------------------------
        public Vector3 SeatPoint(int seat) => transform.TransformPoint(seatPoints[Mathf.Clamp(seat, 0, SeatCount - 1)]);

        bool Taken(int seat)
        {
            int who = occupant[seat];
            if (who < 0) return false;
            var me = FootPlayer.Active;
            if (who == Boat.LocalSlot) return me && me.InPlane == this && me.PlaneSeat == seat;
            return Time.time - occupantSeen[seat] < 3;
        }

        public int FreeSeat()
        {
            if (Wrecked) return -1;
            if (!Flown && !Taken(0)) return 0;
            for (int i = 1; i < SeatCount; i++) if (!Taken(i)) return i;
            return -1;
        }

        public void Occupy(int seat, int slot)
        {
            for (int i = 0; i < SeatCount; i++) if (occupant[i] == slot) occupant[i] = -1;
            if (seat < 0 || seat >= SeatCount) return;
            occupant[seat] = slot;
            occupantSeen[seat] = Time.time;
        }

        public void Vacate(int slot)
        {
            for (int i = 0; i < SeatCount; i++) if (occupant[i] == slot) occupant[i] = -1;
        }

        public int Aboard
        {
            get { int n = 0; for (int i = 0; i < SeatCount; i++) if (Taken(i)) n++; return n; }
        }

        // Friends aboard ride inside, unseen (their own messages lag behind a fast aircraft).
        void LateUpdate()
        {
            if (!Net.CoopNet.Online) return;
            int me = Boat.LocalSlot;
            for (int i = 0; i < SeatCount; i++)
            {
                int who = occupant[i];
                if (who < 0 || who == me || Time.time - occupantSeen[i] > 3) continue;
                var avatar = Net.FootNet.AvatarOf(who);
                if (avatar) { avatar.transform.position = SeatPoint(i); avatar.Stow(); }
            }
        }

        // --- online --------------------------------------------------------------------------------
        public void Follow(Vector3 pos, Quaternion rot, Vector3 vel, float throttle, bool gearDown)
        {
            if (Pilot && Pilot == FootPlayer.Active) return;
            Remote = true;
            Body.isKinematic = true;
            remotePos = pos; remoteRot = rot; remoteVel = vel;
            remoteAt = Time.time;
            Throttle = throttle;
            GearDown = gearDown;
        }

        void OnDestroy() => All.Remove(this);

        static AudioClip jetClip;
        static AudioClip JetClip()
        {
            if (jetClip) return jetClip;
            int rate = 22050, n = rate;
            var data = new float[n];
            var rnd = new System.Random(8);
            float low = 0, mid = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float white = (float)(rnd.NextDouble() * 2 - 1);
                low += (white - low) * .05f;
                mid += (white - mid) * .3f;
                // A jet: roaring noise with a high whine over it.
                data[i] = (low * 1.4f + (mid - low) * .35f + Mathf.Sin(2 * Mathf.PI * 1250 * t) * .04f + Mathf.Sin(2 * Mathf.PI * 2500 * t) * .02f) * .7f;
            }
            jetClip = AudioClip.Create("Jet", n, 1, rate, false);
            jetClip.SetData(data, 0);
            return jetClip;
        }

        static AudioClip engineClip;
        static AudioClip EngineClip()
        {
            if (engineClip) return engineClip;
            int rate = 22050, n = rate;
            var data = new float[n];
            var rnd = new System.Random(5);
            float low = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float white = (float)(rnd.NextDouble() * 2 - 1);
                low += (white - low) * .12f;
                // A turboprop: a steady whine over the propeller's beat.
                data[i] = (Mathf.Sin(2 * Mathf.PI * 70 * t) * .3f * (.7f + .3f * Mathf.Sin(2 * Mathf.PI * 18 * t)) + Mathf.Sin(2 * Mathf.PI * 440 * t) * .05f + low * .45f) * .6f;
            }
            engineClip = AudioClip.Create("Turboprop", n, 1, rate, false);
            engineClip.SetData(data, 0);
            return engineClip;
        }
    }
}

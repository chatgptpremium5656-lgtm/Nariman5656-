using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Autobahn
{
    // A motor boat you can drive: the user's "modern boat" model on a rigid body that floats on
    // six buoyancy points, is pushed by a propeller under the stern and steered by it, leaves a
    // foaming wake and throws spray from the bow at speed. Bullets, blasts and hard knocks damage
    // it; at the end it blows up, burns and sinks, and a fresh one is back at its mooring later.
    // Online the driver's game owns the boat and sends where it is; everybody else follows.
    public sealed class Boat : MonoBehaviour
    {
        public static readonly List<Boat> All = new();
        public int Index => All.IndexOf(this);
        public Rigidbody Body { get; private set; }
        public Transform Seat { get; private set; }
        public float Health = 100;
        public bool Wrecked { get; private set; }
        // At the helm here, or (online) somebody else's game is driving it.
        public bool Driven => Driver != null || RemoteDriven;
        public bool RemoteDriven => Remote && Time.time - remoteAt < 2;
        public FootPlayer Driver;
        // Four places: the helm (0) and three passengers. Who sits where: player slots (online;
        // offline our own is 0), refreshed by messages, forgotten a few seconds after the last.
        public const int Seats = 4;
        readonly Vector3[] seatPoints = new Vector3[Seats];
        readonly int[] occupant = { -1, -1, -1, -1 };
        readonly float[] occupantSeen = new float[Seats];
        Light[] lamps;
        Renderer lampGlass;
        public float Speed => Body ? Body.linearVelocity.magnitude * 3.6f : 0;
        public float Length { get; private set; } = 6.5f;
        public static int Blown { get; private set; }

        const float Mass = 1600, MaxThrust = 11000, TopKmh = 70;
        Vector3 home;
        Quaternion homeRotation;
        readonly Vector3[] floats = new Vector3[6];
        ParticleSystem wake, spray;
        AudioSource motor;
        Renderer[] renderers;
        Material[][] ownMaterials;
        float wreckedAt, sinkFor;
        float throttle, steer;
        // Online: poses from the driving player's game.
        public bool Remote { get; private set; }
        Vector3 remotePos, remoteVel;
        Quaternion remoteRot;
        float remoteAt = -99;

        static GameObject model;
        static Mesh merged;
        static Material[] mergedMaterials;

        static void Merge()
        {
            var probe = Instantiate(model);
            probe.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var groups = new Dictionary<Material, List<CombineInstance>>();
            var order = new List<Material>();
            var toRoot = probe.transform.worldToLocalMatrix;
            foreach (var f in probe.GetComponentsInChildren<MeshFilter>(true))
            {
                var r = f.GetComponent<MeshRenderer>();
                if (!r || !f.sharedMesh || !f.sharedMesh.isReadable) continue;
                var mats = r.sharedMaterials;
                for (int k = 0; k < f.sharedMesh.subMeshCount && k < mats.Length; k++)
                {
                    if (!mats[k]) continue;
                    if (!groups.TryGetValue(mats[k], out var list)) { groups[mats[k]] = list = new List<CombineInstance>(); order.Add(mats[k]); }
                    list.Add(new CombineInstance { mesh = f.sharedMesh, subMeshIndex = k, transform = toRoot * f.transform.localToWorldMatrix });
                }
            }
            var parts = new CombineInstance[order.Count];
            for (int i = 0; i < order.Count; i++)
            {
                var part = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                part.CombineMeshes(groups[order[i]].ToArray(), true, true);
                parts[i] = new CombineInstance { mesh = part, transform = Matrix4x4.identity };
            }
            merged = new Mesh { name = "Motor boat", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            merged.CombineMeshes(parts, false, true);
            merged.RecalculateBounds();
            merged.UploadMeshData(true);
            foreach (var p in parts) Destroy(p.mesh);
            mergedMaterials = order.ToArray();
            Destroy(probe);
        }

        public static Boat Spawn(Transform parent, Vector3 at, float yaw)
        {
            if (!model) model = Resources.Load<GameObject>("Boats/Modern/Boat");
            var root = new GameObject("Motor boat");
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(at, Quaternion.Euler(0, yaw, 0));
            var boat = root.AddComponent<Boat>();
            boat.Build();
            boat.home = at;
            boat.homeRotation = root.transform.rotation;
            All.Add(boat);
            return boat;
        }

        void Build()
        {
            var visual = new GameObject("Boat model").transform;
            visual.SetParent(transform, false);
            Bounds b = new(Vector3.zero, new Vector3(2.4f, 1.6f, 6.5f));
            if (model)
            {
                // Every boat shares one merged mesh (one draw per material instead of 51).
                if (!merged) Merge();
                var m = new GameObject("Hull").transform;
                m.SetParent(visual, false);
                m.gameObject.AddComponent<MeshFilter>().sharedMesh = merged;
                var mr = m.gameObject.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mergedMaterials;
                b = merged.bounds;
                // The hull's keel line sits a little under the water; the model's origin anywhere.
                Vector3 shift = new(b.center.x, b.min.y + .45f, b.center.z);
                m.localPosition = -shift;
                b.center -= shift;
            }
            Length = b.size.z;
            renderers = GetComponentsInChildren<Renderer>(true);
            // Traffic layer: bullets hit it, the camera and spawn checks look past it.
            gameObject.layer = 9;
            var col = gameObject.AddComponent<BoxCollider>();
            col.center = new Vector3(0, b.center.y + b.extents.y * .1f, 0);
            col.size = new Vector3(b.size.x * .92f, b.size.y * .6f, b.size.z * .95f);
            Body = gameObject.AddComponent<Rigidbody>();
            Body.mass = Mass;
            Body.linearDamping = .15f;
            Body.angularDamping = 1.6f;
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.centerOfMass = new Vector3(0, -.25f, -.3f);
            float hx = b.size.x * .36f, hz = b.size.z * .38f, y = -.35f;
            floats[0] = new Vector3(-hx, y, hz); floats[1] = new Vector3(hx, y, hz);
            floats[2] = new Vector3(-hx, y, 0); floats[3] = new Vector3(hx, y, 0);
            floats[4] = new Vector3(-hx, y, -hz); floats[5] = new Vector3(hx, y, -hz);
            Seat = new GameObject("Helm").transform;
            Seat.SetParent(transform, false);
            Seat.localPosition = new Vector3(0, .15f, -b.size.z * .12f);
            seatPoints[0] = Seat.localPosition;
            seatPoints[1] = new Vector3(-.55f, .15f, -b.size.z * .3f);
            seatPoints[2] = new Vector3(.55f, .15f, -b.size.z * .3f);
            seatPoints[3] = new Vector3(0, .25f, b.size.z * .18f);
            // Two lamps in the bow, on at night while somebody drives.
            lamps = new Light[2];
            for (int i = 0; i < 2; i++)
            {
                var l = new GameObject("Boat lamp").AddComponent<Light>();
                l.transform.SetParent(transform, false);
                l.transform.localPosition = new Vector3(i == 0 ? -.45f : .45f, b.max.y - .75f, b.max.z - .35f);
                l.transform.localRotation = Quaternion.Euler(4, i == 0 ? -4 : 4, 0);
                l.type = LightType.Spot;
                l.spotAngle = 55;
                l.innerSpotAngle = 25;
                l.range = 90;
                l.intensity = 300;
                l.color = new Color(.9f, .94f, 1f);
                l.shadows = LightShadows.None;
                l.enabled = false;
                lamps[i] = l;
            }
            lampGlass = Art.Box(transform, "Boat lamp glass", new Vector3(0, b.max.y - .75f, b.max.z - .3f), new Vector3(1.05f, .1f, .05f), Art.White).GetComponent<Renderer>();
            var lampCollider = lampGlass.GetComponent<Collider>();
            if (lampCollider) Object.Destroy(lampCollider);
            lampGlass.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lampGlass.enabled = false;
            wake = Foam("Wake", new Vector3(0, -.1f, -b.size.z * .5f), false);
            spray = Foam("Bow spray", new Vector3(0, .2f, b.size.z * .42f), true);
            motor = gameObject.AddComponent<AudioSource>();
            motor.clip = MotorClip();
            motor.loop = true;
            motor.spatialBlend = 1;
            motor.minDistance = 6;
            motor.maxDistance = 160;
            motor.volume = 0;
            motor.Play();
        }

        // --- water ------------------------------------------------------------------------------
        // The sea surface (matches the swell of the sea shader closely enough for floating).
        public static float WaterHeight(Vector3 p)
        {
            float t = Time.time;
            return Island.SeaY + Mathf.Sin(p.x * .098f + t * .9f) * .07f + Mathf.Sin(p.z * .19f - t * 1.3f) * .04f;
        }

        void FixedUpdate()
        {
            if (Remote && Time.time - remoteAt < 2)
            {
                // Somebody else is driving it: follow their game.
                float k = 1 - Mathf.Exp(-Time.fixedDeltaTime * 8);
                Vector3 predicted = remotePos + remoteVel * Mathf.Min(.2f, Time.time - remoteAt);
                Body.MovePosition(Vector3.Lerp(Body.position, predicted, k));
                Body.MoveRotation(Quaternion.Slerp(Body.rotation, remoteRot, k));
                return;
            }
            if (Remote) { Remote = false; Body.isKinematic = false; }
            // Far from everybody (the players online too, not only this camera) and moored: at
            // rest, no simulation.
            bool near = Driven || Body.linearVelocity.sqrMagnitude > .5f || PhysicsRange.PlayerNear(transform.position, 300);
            if (!near && !Wrecked) { if (!Body.IsSleeping()) Body.Sleep(); return; }

            float dt = Time.fixedDeltaTime;
            int wet = 0;
            float buoy = Wrecked ? Mathf.Lerp(1, 0, Mathf.Clamp01((Time.time - wreckedAt - 1.5f) / 3f)) : 1;
            for (int i = 0; i < floats.Length; i++)
            {
                Vector3 p = transform.TransformPoint(floats[i]);
                float depth = WaterHeight(p) - p.y;
                if (depth <= 0) continue;
                wet++;
                Vector3 v = Body.GetPointVelocity(p);
                float lift = Mathf.Clamp(depth, 0, 1.2f) * Mass * 9.81f / floats.Length * 2.2f * buoy;
                Body.AddForceAtPosition(Vector3.up * (lift - v.y * Mass / floats.Length * 1.8f), p);
            }
            if (wet > 0)
            {
                // Water drag: easy along the hull, hard sideways (the keel), a lift of the bow at speed.
                Vector3 local = transform.InverseTransformDirection(Body.linearVelocity);
                local.x *= 1 - Mathf.Clamp01(dt * 3.5f);
                local.z *= 1 - Mathf.Clamp01(dt * .25f);
                Body.linearVelocity = transform.TransformDirection(local);
                Vector3 stern = transform.TransformPoint(new Vector3(0, -.4f, -Length * .45f));
                bool propInWater = WaterHeight(stern) > stern.y;
                if (Driven && !Wrecked && propInWater)
                {
                    float fwd = local.z * 3.6f;
                    float push = throttle * MaxThrust * Mathf.Clamp01((TopKmh - Mathf.Abs(fwd)) / 15f);
                    if (throttle < 0) push *= .45f;
                    Body.AddForceAtPosition(transform.forward * push, stern);
                    // Rudder: turns with the flow past it (and a little from the propeller at rest).
                    float authority = Mathf.Clamp(Mathf.Abs(fwd) / 25f, .25f, 1) * Mathf.Sign(fwd == 0 ? 1 : fwd);
                    Body.AddTorque(Vector3.up * steer * authority * Mass * 1.6f);
                    // Leans into the turn and lifts the bow under power.
                    Body.AddRelativeTorque(new Vector3(-throttle * Mathf.Clamp01(fwd / 30f) * Mass * .35f, 0, -steer * Mathf.Clamp01(fwd / 25f) * Mass * .5f));
                }
            }
        }

        void Update()
        {
            // The local driver's keys and pad.
            throttle = steer = 0;
            if (Driver && Driver == FootPlayer.Active && !Wrecked)
            {
                var run = RunSession.Active;
                bool paused = run == null || run.Paused || run.OnTitle || WorldMap.Open;
                var k = Keyboard.current;
                var pad = Gamepad.current;
                if (!paused)
                {
                    if (k != null)
                    {
                        throttle = (k.wKey.isPressed || k.upArrowKey.isPressed ? 1 : 0) - (k.sKey.isPressed || k.downArrowKey.isPressed ? 1 : 0);
                        steer = (k.dKey.isPressed || k.rightArrowKey.isPressed ? 1 : 0) - (k.aKey.isPressed || k.leftArrowKey.isPressed ? 1 : 0);
                    }
                    if (pad != null)
                    {
                        throttle = Mathf.Clamp(throttle + pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue(), -1, 1);
                        if (steer == 0) steer = pad.leftStick.x.ReadValue();
                    }
                }
            }
            if (TestThrottle != 0 || TestSteer != 0) { throttle = TestThrottle; steer = TestSteer; }

            // Foam and spray with speed, the motor with the throttle.
            float kmh = Speed;
            bool afloat = Mathf.Abs(transform.position.y - WaterHeight(transform.position)) < 1.5f;
            var em = wake.emission;
            em.rateOverTime = afloat && !Wrecked ? Mathf.Clamp(kmh * 3.5f, 0, 200) : 0;
            var em2 = spray.emission;
            em2.rateOverTime = afloat && !Wrecked && kmh > 18 ? (kmh - 18) * 2.5f : 0;
            motor.volume = Wrecked ? 0 : Mathf.MoveTowards(motor.volume, Driven ? .25f + Mathf.Abs(throttle) * .45f : 0, Time.deltaTime * 2);
            motor.pitch = .7f + kmh / 70f + Mathf.Abs(throttle) * .15f;

            bool lit = Driven && !Wrecked && Atmosphere.Night;
            if (lamps[0].enabled != lit)
            {
                foreach (var l in lamps) l.enabled = lit;
                lampGlass.enabled = lit;
            }

            if (Wrecked && Time.time - wreckedAt > 25) Restore();
        }

        // --- seats --------------------------------------------------------------------------------
        public static int LocalSlot => Net.CoopNet.Online && Net.CoopNet.Active ? Mathf.Max(0, Net.CoopNet.Active.Slot) : 0;

        public Vector3 SeatPoint(int seat) => transform.TransformPoint(seatPoints[Mathf.Clamp(seat, 0, Seats - 1)]);

        bool Taken(int seat)
        {
            int who = occupant[seat];
            if (who < 0) return false;
            var me = FootPlayer.Active;
            if (who == LocalSlot) return me && me.OnBoat == this && me.BoatSeat == seat;
            return Time.time - occupantSeen[seat] < 3;
        }

        // The seat for somebody getting in: the helm if nobody drives, otherwise a free passenger place.
        public int FreeSeat()
        {
            if (Wrecked) return -1;
            if (!Driven && !Taken(0)) return 0;
            for (int i = 1; i < Seats; i++) if (!Taken(i)) return i;
            return -1;
        }

        public void Occupy(int seat, int slot)
        {
            for (int i = 0; i < Seats; i++) if (occupant[i] == slot) occupant[i] = -1;
            if (seat < 0 || seat >= Seats) return;
            occupant[seat] = slot;
            occupantSeen[seat] = Time.time;
        }

        public void Vacate(int slot)
        {
            for (int i = 0; i < Seats; i++) if (occupant[i] == slot) occupant[i] = -1;
        }

        public int Aboard
        {
            get { int n = 0; for (int i = 0; i < Seats; i++) if (Taken(i)) n++; return n; }
        }

        // Friends aboard sit in their places (their own position messages lag behind a fast boat).
        void LateUpdate()
        {
            if (!Net.CoopNet.Online) return;
            int me = LocalSlot;
            for (int i = 0; i < Seats; i++)
            {
                int who = occupant[i];
                if (who < 0 || who == me || Time.time - occupantSeen[i] > 3) continue;
                var avatar = Net.FootNet.AvatarOf(who);
                if (avatar) avatar.transform.position = SeatPoint(i);
            }
        }

        public float TestThrottle, TestSteer;

        // --- damage -----------------------------------------------------------------------------
        public void Damage(float amount)
        {
            if (Wrecked || Remote) return;
            Health -= amount;
            if (Health <= 0) Blow();
        }

        void OnCollisionEnter(Collision c)
        {
            if (Remote) return;
            float v = c.relativeVelocity.magnitude;
            if (v > 7) Damage((v - 7) * 6);
        }

        public void Blow(bool fromNetwork = false)
        {
            if (Wrecked) return;
            Wrecked = true;
            wreckedAt = Time.time;
            Blown++;
            Health = 0;
            var local = FootPlayer.Active;
            if (local && local.OnBoat == this) local.LeaveBoat(true);
            if (Driver) Driver.LeaveBoat(true);
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
            if (!Remote) Body.AddForce(Vector3.up * 5 + Random.insideUnitSphere, ForceMode.VelocityChange);
            Explosion.Blast(transform.position + Vector3.up * .8f, !fromNetwork);
            if (!fromNetwork) Net.BoatNet.Exploded(this);
        }

        void Restore()
        {
            Wrecked = false;
            Health = 100;
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
            foreach (var b in All)
                if (b && !b.Driven) b.Restore();
        }

        // --- online -----------------------------------------------------------------------------
        public void Follow(Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (Driver && Driver == FootPlayer.Active) return;     // we drive it here
            Remote = true;
            Body.isKinematic = true;
            remotePos = pos; remoteRot = rot; remoteVel = vel;
            remoteAt = Time.time;
        }

        void OnDestroy() => All.Remove(this);

        // --- effects -----------------------------------------------------------------------------
        static Material foam;

        ParticleSystem Foam(string name, Vector3 at, bool bow)
        {
            if (!foam)
            {
                foam = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
                const int n = 64;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float d = new Vector2(x - n / 2f + .5f, y - n / 2f + .5f).magnitude / (n / 2f);
                        float a = Mathf.Clamp01(1 - d) * (.6f + .4f * Mathf.PerlinNoise(x * .2f, y * .2f));
                        tex.SetPixel(x, y, new Color(1, 1, 1, a * a));
                    }
                tex.Apply(true);
                foam.SetTexture("_BaseMap", tex);
                foam.SetFloat("_Surface", 1);
                foam.SetFloat("_Blend", 0);
                foam.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                foam.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                foam.SetFloat("_ZWrite", 0);
                foam.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                foam.renderQueue = 3000;
            }
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = at;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = bow ? new ParticleSystem.MinMaxCurve(.5f, .9f) : new ParticleSystem.MinMaxCurve(2.5f, 4f);
            main.startSpeed = bow ? new ParticleSystem.MinMaxCurve(3, 6) : new ParticleSystem.MinMaxCurve(.3f, 1.2f);
            main.startSize = bow ? new ParticleSystem.MinMaxCurve(.5f, 1.2f) : new ParticleSystem.MinMaxCurve(.8f, 1.8f);
            main.startColor = new Color(.95f, .97f, 1f, bow ? .75f : .42f);
            main.gravityModifier = bow ? 1.2f : 0;
            main.maxParticles = 400;
            main.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = bow ? 35 : 20;
            shape.radius = bow ? .6f : .9f;
            go.transform.localRotation = bow ? Quaternion.Euler(-60, 0, 0) : Quaternion.Euler(180, 0, 0);
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, .6f, 1, bow ? 1.2f : 2.4f));
            var colour = ps.colorOverLifetime;
            colour.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                      new[] { new GradientAlphaKey(.9f, 0), new GradientAlphaKey(.5f, .5f), new GradientAlphaKey(0, 1) });
            colour.color = g;
            var emission = ps.emission;
            emission.rateOverTime = 0;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = foam;
            r.renderMode = bow ? ParticleSystemRenderMode.Billboard : ParticleSystemRenderMode.HorizontalBillboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ps.Play();
            return ps;
        }

        static AudioClip motorClip;
        static AudioClip MotorClip()
        {
            if (motorClip) return motorClip;
            int rate = 22050, n = rate;
            var data = new float[n];
            var rnd = new System.Random(9);
            float low = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float white = (float)(rnd.NextDouble() * 2 - 1);
                low += (white - low) * .08f;
                data[i] = (Mathf.Sin(2 * Mathf.PI * 55 * t) * .35f + Mathf.Sin(2 * Mathf.PI * 110 * t) * .18f + low * .5f) * .6f;
            }
            motorClip = AudioClip.Create("Outboard", n, 1, rate, false);
            motorClip.SetData(data, 0);
            return motorClip;
        }
    }
}

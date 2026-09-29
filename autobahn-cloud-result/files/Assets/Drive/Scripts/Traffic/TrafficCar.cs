using UnityEngine;

namespace Autobahn
{
    public enum Personality
    {
        Calm,
        Slow,
        Fast,
        Assertive,
        Truck
    }

    public sealed class TrafficCar : MonoBehaviour
    {
        public int Lane { get; private set; }

        public int TargetLane { get; private set; }

        public int Generation { get; private set; }

        public float Z { get; private set; }

        public float Speed { get; private set; }

        public float Cruise;
        public float Length = 4.6f, Width = 1.88f;
        public Personality Driver;
        public float Lateral { get; private set; }

        public bool Changing => TargetLane != Lane;
        public Renderer LeftLamp, RightLamp;
        public Renderer[] NightLamps;          // headlamp lenses, lit after dark
        public float PassedAt = -999;
        // Driven by the street-grid traffic (city) rather than along the road.
        public bool OnGrid;
        float committedStop = float.NaN;
        Rigidbody body;
        TrafficFlow flow;
        float cooldown, signal, hitStop, think, desired, oldLateral;
        public void Init(TrafficFlow owner)
        {
            flow = owner;
            body = GetComponent<Rigidbody>();
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            var box = GetComponent<BoxCollider>();
            if (box)
            {
                slide ??= new PhysicsMaterial("Car body") { dynamicFriction = .45f, staticFriction = .55f, bounciness = 0, bounceCombine = PhysicsMaterialCombine.Minimum, frictionCombine = PhysicsMaterialCombine.Average };
                box.sharedMaterial = slide;
            }
        }

        // ---- physics near the drivers ------------------------------------------------------
        // Far from every human driver a car is kinematic and just follows its lane (cheap). Near
        // one it becomes a real body with a real mass that tracks the lane with velocity, so a
        // hit exchanges momentum honestly instead of bouncing off an immovable object. A hard hit
        // stuns it: no control, it slides and spins with friction; then it steers itself back
        // onto its lane and drives on. Enough damage and it burns out (see Explosion).
        public enum Mode { Kinematic, Active, Stunned, Recovering }
        public Mode State { get; private set; } = Mode.Kinematic;
        public float Health { get; private set; } = 100;
        public bool Wrecked { get; private set; }
        public bool Disabled => State == Mode.Stunned || Wrecked;
        // Set once when a stun ends, so the lane planner can pick the car up where it is.
        public bool JustReleased { get; set; }
        public float WreckTime { get; private set; }
        static PhysicsMaterial slide;
        const float Bubble = 45;
        float stunTimer, stuckTimer;
        Material[][] ownMaterials;
        Renderer[] ownRenderers;

        bool NearDriver(Vector3 p)
        {
            if (flow == null || flow.Player == null) return false;
            if ((flow.Player.transform.position - p).sqrMagnitude < Bubble * Bubble) return true;
            foreach (var r in TrafficFlow.Remotes)
                if (r && r.HasState && (r.transform.position - p).sqrMagnitude < Bubble * Bubble) return true;
            return false;
        }

        void SetDynamic(bool on)
        {
            if (body.isKinematic == !on) return;
            if (on)
            {
                body.isKinematic = false;
                body.mass = Length > 5 ? 2600 : Driver == Personality.Fast ? 1350 : 1500;
                body.linearDamping = .05f;
                body.angularDamping = .6f;
                body.centerOfMass = new Vector3(0, .38f, 0);
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.maxDepenetrationVelocity = 3;
            }
            else
            {
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                body.isKinematic = true;
            }
        }

        public void Stun(float seconds)
        {
            SetDynamic(true);
            State = Mode.Stunned;
            stunTimer = Mathf.Max(stunTimer, seconds);
            Speed = 0;
        }

        public void Damage(float amount)
        {
            if (Wrecked) return;
            Health -= amount;
            if (Health <= 0)
                Explosion.Blow(this);
        }

        // Burnt out: charcoal paint, no lights, left where it stopped until it is recycled.
        public void Burn()
        {
            Stun(999);
            Char();
        }

        // Only the burnt look (and the wreck flag): used by online guests, whose copy of a
        // host's traffic car keeps following the host's snapshots instead of falling loose.
        public void Char()
        {
            if (Wrecked) return;
            Wrecked = true;
            WreckTime = Time.time;
            Wanted.TrafficWrecked(this);    // burnt out next to the player: the police want them
            ownRenderers = GetComponentsInChildren<Renderer>();
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
            if (LeftLamp) LeftLamp.enabled = false;
            if (RightLamp) RightLamp.enabled = false;
        }

        // Back to a clean, working car (before it is placed again somewhere else).
        public void Restore()
        {
            if (ownRenderers != null)
                for (int i = 0; i < ownRenderers.Length; i++)
                    if (ownRenderers[i]) ownRenderers[i].sharedMaterials = ownMaterials[i];
            ownRenderers = null;
            var fire = transform.Find("Wreck fire");
            if (fire) Destroy(fire.gameObject);
            Wrecked = false;
            Health = 100;
            stunTimer = 0;
            State = Mode.Kinematic;
            if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            SetDynamic(false);
        }

        // Every physics step: where the lane planner wants the car, and how fast.
        void Drive(Vector3 p, Quaternion r, float speed, bool snap)
        {
            float dt = Time.fixedDeltaTime;
            if (Wrecked) return;
            if (State == Mode.Stunned)
            {
                stunTimer -= dt;
                bool settled = body.linearVelocity.magnitude < 4 || stunTimer < -3;
                if (stunTimer <= 0 && settled)
                {
                    State = Mode.Recovering;
                    JustReleased = true;
                    stuckTimer = 0;
                }
                return;
            }
            bool near = NearDriver(body.position) || NearDriver(p);
            if (snap || (!near && State != Mode.Recovering) || (body.position - p).sqrMagnitude > 30 * 30)
            {
                // Far away, or a teleport: kinematic, straight onto the lane.
                if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
                SetDynamic(false);
                State = near ? Mode.Active : Mode.Kinematic;
                if (near) SetDynamic(true);
                transform.SetPositionAndRotation(p, r);
                body.position = p;
                body.rotation = r;
                return;
            }
            if (!near && State == Mode.Kinematic)
            {
                body.MovePosition(p);
                body.MoveRotation(r);
                return;
            }
            if (State == Mode.Kinematic) State = Mode.Active;
            SetDynamic(true);

            // Track the lane: velocity along it plus a pull towards it, and yaw towards its heading.
            bool recovering = State == Mode.Recovering;
            Vector3 pos = body.position;
            Vector3 error = p - pos;
            Vector3 fwd = r * Vector3.forward;
            float gain = recovering ? 1.6f : 6;
            Vector3 want = fwd * speed + new Vector3(error.x, 0, error.z) * gain;
            float cap = recovering ? 7 : speed + 8;
            if (want.magnitude > cap) want = want.normalized * cap;
            Vector3 v = body.linearVelocity;
            float blend = recovering ? 1 - Mathf.Exp(-dt * 3) : 1;
            Vector3 flat = Vector3.Lerp(new Vector3(v.x, 0, v.z), new Vector3(want.x, 0, want.z), blend);
            float vy = recovering ? v.y : Mathf.Clamp(v.y + (error.y * 8 - v.y) * .5f, -20, 6);
            body.linearVelocity = new Vector3(flat.x, vy, flat.z);

            Vector3 have = transform.forward; have.y = 0;
            Vector3 aim = fwd; aim.y = 0;
            float yawError = Vector3.SignedAngle(have, aim, Vector3.up);
            float yawRate = Mathf.Clamp(yawError * Mathf.Deg2Rad * (recovering ? 2.2f : 9), -2.5f, 2.5f);
            Vector3 w = body.angularVelocity;
            body.angularVelocity = new Vector3(w.x * .85f, yawRate, w.z * .85f);

            // Tipped over: after a moment it is set back on its wheels.
            if (transform.up.y < .5f)
            {
                stuckTimer += dt;
                if (stuckTimer > 2.5f)
                {
                    body.position = pos + Vector3.up * .6f;
                    body.rotation = Quaternion.LookRotation(aim.sqrMagnitude > .01f ? aim : Vector3.forward, Vector3.up);
                    body.angularVelocity = Vector3.zero;
                    stuckTimer = 0;
                }
            }
            else if (recovering)
            {
                stuckTimer += dt;
                // Back on the lane and pointing the right way: drive on normally.
                if ((error.sqrMagnitude < .8f * .8f && Mathf.Abs(yawError) < 8) || stuckTimer > 12)
                    State = Mode.Active;
            }
        }

        public void Place(float z, int lane, float cruise)
        {
            if (Wrecked || State != Mode.Kinematic) Restore();
            Generation++;
            committedStop = float.NaN;
            Lane = TargetLane = lane;
            Z = z;
            Cruise = cruise;
            Speed = cruise;
            desired = cruise;
            Lateral = (lane - 1) * Route.LaneWidth;
            oldLateral = Lateral;
            signal = hitStop = think = 0;
            cooldown = 2;
            PassedAt = -999;
            Pose(true);
        }

        public void Hit()
        {
            hitStop = 4;
        }

        public bool Occupies(int lane) => Lane == lane || TargetLane == lane;

        // Hooks for the street-grid traffic, which plans the route itself.
        public float HitTimer { get => hitStop; set => hitStop = value; }

        public void GridRespawn()
        {
            if (Wrecked || State != Mode.Kinematic) Restore();
            Generation++;
            PassedAt = -999;
            hitStop = 0;
        }

        public void GridPose(Vector3 p, Quaternion r, float speed, bool snap)
        {
            Speed = speed;
            // A kinematic body moved a long way in one step sweeps through everything between:
            // anything it meets is thrown at enormous speed. Jumps are teleports (inside Drive).
            Drive(p, r, speed, snap);
        }
        public void Tick(float dt)
        {
            JustReleased = false;
            // Knocked about: the lane plan follows the car, not the other way round.
            if (Disabled)
            {
                var here = body.position;
                Z = Route.Locate(here);
                Lateral = Mathf.Clamp(Route.Lateral(here), -Route.LaneWidth * 1.4f, Route.LaneWidth * 1.4f);
                Lane = TargetLane = Mathf.Clamp(Mathf.RoundToInt(Lateral / Route.LaneWidth) + 1, 0, 2);
                oldLateral = Lateral;
                Speed = 0;
                Drive(here, body.rotation, 0, false);
                return;
            }
            if (State == Mode.Recovering)
                desired = Mathf.Min(desired, 6);
            // Drift back to the middle of the lane after a knock.
            if (!Changing)
                Lateral = Mathf.MoveTowards(Lateral, (Lane - 1) * Route.LaneWidth, dt * 1.1f);
            cooldown -= dt;
            signal = Mathf.Max(0, signal - dt);
            hitStop = Mathf.Max(0, hitStop - dt);
            think -= dt;
            if (think <= 0)
            {
                think = Driver == Personality.Assertive ? .12f : .22f;
                Plan();
            }

            float a = Driver == Personality.Truck ? 1.1f : Driver == Personality.Assertive ? 3 : 2;
            Speed = Mathf.MoveTowards(Speed, hitStop > 0 ? 0 : desired, (desired < Speed || hitStop > 0 ? 7 : a) * dt);
            float travel = Speed * dt;
            if (SignalStop(out float stopZ))
            {
                float available = Mathf.Max(0, stopZ - Length / 2 - 1 - Z);
                if (travel >= available) { travel = available; Speed = 0; }
            }
            Z += travel;
            if (Changing && signal <= 0)
            {
                if (!flow.LaneSafe(this, TargetLane) && Mathf.Abs(Lateral - (Lane - 1) * Route.LaneWidth) < .25f)
                {
                    TargetLane = Lane;
                    cooldown = 2;
                }
                else
                    Lateral = Mathf.MoveTowards(Lateral, (TargetLane - 1) * Route.LaneWidth, dt * 1.15f);
            }

            if (Mathf.Abs(Lateral - (TargetLane - 1) * Route.LaneWidth) < .025f)
                Lane = TargetLane;
            bool blink = Changing && Mathf.Repeat(Time.time, .7f) < .35f;
            if (LeftLamp)
                LeftLamp.enabled = blink && TargetLane < Lane;
            if (RightLamp)
                RightLamp.enabled = blink && TargetLane > Lane;
            Pose(false);
            oldLateral = Lateral;
        }

        bool SignalStop(out float stopZ)
        {
            if (flow.SignalsGreen || Z + Length / 2 > committedStop)
                committedStop = float.NaN;
            if (float.IsNaN(committedStop) && flow.SignalStop(this, out float next))
                committedStop = next;
            stopZ = committedStop;
            return !float.IsNaN(committedStop);
        }

        void Plan()
        {
            float preferred = Cruise * Atmosphere.TrafficSpeed;
            desired = preferred;
            float headway = Driver == Personality.Assertive ? 1.05f : Driver == Personality.Truck ? 2.1f : 1.55f;
            flow.Leader(this, out float gap, out float lead);
            float stopping = Mathf.Max(0, (Speed * Speed - lead * lead) / 12);
            float safe = 7 + Speed * headway + stopping;
            if (gap < safe)
                desired = Mathf.Min(preferred, Mathf.Max(0, lead + (gap - (5 + Speed * headway)) * .45f));
            if (gap < 5)
                desired = 0;
            bool mustMerge = false;
            bool stoppingAtSignal = SignalStop(out float stopZ);
            if (stoppingAtSignal)
                desired = Mathf.Min(desired, Mathf.Sqrt(2 * 5 * Mathf.Max(0, stopZ - Z - Length / 2 - 2)));
            if (!Changing && cooldown <= 0 && !stoppingAtSignal)
            {
                if (mustMerge)
                    TryChange(1);
                else if (gap < safe && lead < preferred - 1 && Lane > 0)
                    TryChange(Lane - 1);
                else if (Lane < 2 && gap > safe * 1.5f)
                    TryChange(Lane + 1);
            }
        }

        bool TryChange(int lane)
        {
            if (!flow.LaneSafe(this, lane))
                return false;
            TargetLane = lane;
            signal = .9f;
            cooldown = 6;
            return true;
        }

        void Pose(bool snap)
        {
            Vector3 p = Route.Center(Z) + Route.Right(Z) * Lateral + Vector3.up * .02f;
            Quaternion r = Route.Rotation(Z) * Quaternion.Euler(0, Mathf.Atan2((Lateral - oldLateral) / Mathf.Max(.001f, Time.fixedDeltaTime), Mathf.Max(5, Speed)) * Mathf.Rad2Deg, 0);
            Drive(p, r, Speed, snap);
        }

        // A push (m/s) from outside: a guest's car, a blast reported by a guest.
        public void Shove(Vector3 velocityChange)
        {
            if (body && !body.isKinematic) body.AddForce(velocityChange, ForceMode.VelocityChange);
        }

        void OnCollisionEnter(Collision c)
        {
            var player = c.gameObject.GetComponent<Vehicle>();
            // Online guest: this is the host's car as seen here. The guest's own bump is sent to
            // the host, which knocks the real car for everybody; nothing is simulated here.
            if (Net.CoopNet.IsGuest)
            {
                if (player && !Wrecked)
                {
                    float rel = c.relativeVelocity.magnitude;
                    float kickGuest = rel * .5f;
                    Vector3 dir = player.Body.linearVelocity.sqrMagnitude > .1f ? player.Body.linearVelocity.normalized : -c.relativeVelocity.normalized;
                    float share = player.Body.mass / (player.Body.mass + 1800);
                    Net.CoopNet.ReportDamage(this, kickGuest > 1.6f ? kickGuest * 7 : 0, dir * rel * share, kickGuest > 1.6f ? Mathf.Clamp(1.2f + kickGuest * .35f, 1.5f, 4.5f) : 0);
                }
                return;
            }
            // Host: a friend's car is reported by the friend's own game (above), not counted twice.
            if (Net.CoopNet.IsHost && c.gameObject.GetComponent<Net.RemoteCar>()) return;
            if (player)
            {
                Hit();
                Net.CoopNet.ReportHit(this);
            }
            // How hard was the knock for this car (change of speed it received)?
            float kick = body.isKinematic ? c.relativeVelocity.magnitude * .5f : c.impulse.magnitude / Mathf.Max(1, body.mass);
            bool other = player || c.rigidbody;
            if (other && kick > 1.6f && !Wrecked)
            {
                if (ProfileStore.Testing) Debug.Log($"QA INFO stun {name} by {c.collider.name}: kick {kick:0.0} m/s, relative {c.relativeVelocity.magnitude:0.0}, kinematic {body.isKinematic}");
                Stun(Mathf.Clamp(1.2f + kick * .35f, 1.5f, 4.5f));
                Damage(kick * 7);
            }
            else if (!other && kick > 4 && !Wrecked && !body.isKinematic)
                Damage(kick * 5);    // into a wall or a post
        }
    }
}

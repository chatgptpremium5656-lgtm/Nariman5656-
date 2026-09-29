using UnityEngine;
using UnityEngine.InputSystem;

namespace Autobahn
{
    // The player on foot: walk / run / jump with a character controller, look with the mouse,
    // pick a weapon (1–8 or the wheel; 8 is the bomb), fire (left button), aim (Q held), reload (Z), kneel and
    // say sorry (G). Cars that run into us knock us down; explosions hurt.
    public sealed class FootPlayer : MonoBehaviour
    {
        public static FootPlayer Active { get; private set; }
        public HumanRig Rig { get; private set; }
        public float Yaw, Pitch;
        public float Health = 100;
        public bool Dead => Health <= 0;
        public int Weapon = 1;
        public bool Aiming { get; private set; }
        public bool Reloading => Time.time < reloadUntil;
        public int Ammo => ammo[Weapon];
        public int Kills, Deaths;
        public int LastAttacker = -1;
        public float HitMarkerTime, DamageFlash;
        public Vector3 Velocity => velocity;
        public float LocalForward, LocalStrafe;
        public bool Grounded => cc && cc.isGrounded;
        public bool Kneeling { get; set; }
        public bool Crouching { get; set; }
        public bool Firing { get; private set; }
        // Riding in a friend's car as a passenger: no body, no walking; the camera sits in the car.
        public Net.RemoteCar Riding { get; set; }
        public int RideSlot = -1;
        // At the helm of a boat: the boat takes the keys, we stand in it and look around.
        public Boat OnBoat { get; private set; }
        public int BoatSeat { get; private set; } = -1;          // 0 at the helm, 1..3 passenger
        // In an aircraft: 0 the pilot, 1..3 passengers. Inside, unseen; looking around only.
        public Airplane InPlane { get; private set; }
        public int PlaneSeat { get; private set; } = -1;
        public bool Aboard => OnBoat || InPlane;
        float boatYaw;

        CharacterController cc;
        Vector3 velocity;
        readonly int[] ammo = new int[Weapons.All.Length];
        float nextShot, reloadUntil;
        Transform gun;
        Vector3 muzzle;
        int gunFor = -1;
        public System.Action<int, Vector3> Fired;      // weapon, aim point: sent online
        public System.Action<int, int, int> HitRemote;  // slot, damage, kind (0 person, 1 car)
        // Recoil on the aim (spread, climb) and where the gun is drawn (hand / view model).
        public WeaponRecoil Recoil { get; } = new WeaponRecoil();
        public WeaponHold Hold { get; private set; }

        public static FootPlayer Spawn(Vector3 position, float yaw)
        {
            var go = new GameObject("Player on foot");
            go.layer = 2;   // ignore raycasts: our own shots start inside us
            go.transform.position = position;
            var p = go.AddComponent<FootPlayer>();
            p.Yaw = yaw;
            return p;
        }

        void Awake()
        {
            Active = this;
            cc = gameObject.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = .32f;
            cc.center = new Vector3(0, .9f, 0);
            cc.stepOffset = .45f;
            cc.slopeLimit = 50;
            cc.skinWidth = .04f;
            Rig = HumanRig.Create(transform, Color.white, false);
            foreach (var t in Rig.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
            Hold = gameObject.AddComponent<WeaponHold>();
            Hold.Rig = Rig;
            for (int i = 0; i < ammo.Length; i++) ammo[i] = Weapons.All[i].Magazine;
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
        }

        public void Respawn(Vector3 at, float yaw)
        {
            cc.enabled = false;
            transform.position = at;
            cc.enabled = true;
            Yaw = yaw; Pitch = 0;
            velocity = Vector3.zero;
            Health = 100;
            LastAttacker = -1;
            Kneeling = false;
            Crouching = false;
            knockedFor = 0;
            Rig.Revive();
            Recoil.Reset();
            for (int i = 0; i < ammo.Length; i++) ammo[i] = Weapons.All[i].Magazine;
        }

        public void BoardBoat(Boat boat, int seat = -1)
        {
            if (!boat || boat.Wrecked) return;
            if (seat < 0) seat = boat.FreeSeat();
            if (seat < 0 || seat == 0 && boat.Driven) return;
            OnBoat = boat;
            BoatSeat = seat;
            if (seat == 0) boat.Driver = this;
            boat.Occupy(seat, Boat.LocalSlot);
            Net.BoatNet.SeatChanged(boat, seat);
            cc.enabled = false;
            velocity = Vector3.zero;
            Kneeling = Crouching = false;
            boatYaw = boat.transform.eulerAngles.y;
            Yaw = boatYaw;
        }

        // Off the boat: onto the nearest dry ground within a jump or two, or (`anywhere`, when the
        // boat blew up or we are called away) just where we are.
        public bool LeaveBoat(bool anywhere = false)
        {
            var boat = OnBoat;
            if (!boat) return false;
            Vector3 p = boat.transform.position;
            Vector3 at = default;
            bool found = false;
            foreach (float d in new[] { 3f, 4.5f, 6, 8, 10, 13 })
            {
                for (int i = 0; i < 12 && !found; i++)
                {
                    Vector3 dir = Quaternion.Euler(0, boat.transform.eulerAngles.y + 90 + i * 30, 0) * Vector3.forward;
                    if (!SpawnSpot.Ground(p + dir * d + Vector3.up * 4, out Vector3 g, 6, 12)) continue;
                    if (g.y < Island.SeaY + .25f || g.y > p.y + 5) continue;
                    if (!SpawnSpot.PersonFits(g)) continue;
                    at = g + Vector3.up * .05f;
                    found = true;
                }
                if (found) break;
            }
            if (!found && !anywhere) return false;
            if (!found) at = p + Vector3.up * 1.2f;
            float yaw = Yaw;
            if (boat.Driver == this) boat.Driver = null;
            boat.Vacate(Boat.LocalSlot);
            Net.BoatNet.SeatChanged(boat, -1);
            OnBoat = null;
            BoatSeat = -1;
            cc.enabled = true;
            Teleport(at, yaw);
            return true;
        }

        public void BoardPlane(Airplane plane, int seat = -1)
        {
            if (!plane || plane.Wrecked) return;
            if (seat < 0) seat = plane.FreeSeat();
            if (seat < 0 || seat == 0 && plane.Flown) return;
            InPlane = plane;
            PlaneSeat = seat;
            if (seat == 0) plane.Pilot = this;
            plane.Occupy(seat, Boat.LocalSlot);
            Net.PlaneNet.SeatChanged(plane, seat);
            cc.enabled = false;
            velocity = Vector3.zero;
            Kneeling = Crouching = false;
            Rig.Hidden = true;
            Yaw = 0; Pitch = 10;
        }

        // Out of the aircraft: beside it on the ground (only when it has stopped, unless
        // `anywhere`: it blew up, or we are called back to the car).
        public bool LeavePlane(bool anywhere = false)
        {
            var plane = InPlane;
            if (!plane) return false;
            if (!anywhere && (plane.Kmh > 20 || !plane.Grounded)) return false;
            Vector3 side = plane.ExitPoint;
            Vector3 at = side;
            if (SpawnSpot.ForPerson(side, plane.transform.forward, new[] { .5f, 2f, 4f, 7f }, out Vector3 spot)) at = spot;
            else if (SpawnSpot.Ground(side + Vector3.up * 3, out Vector3 g, 6, 30)) at = g + Vector3.up * .05f;
            if (plane.Pilot == this) plane.Pilot = null;
            plane.Vacate(Boat.LocalSlot);
            Net.PlaneNet.SeatChanged(plane, -1);
            InPlane = null;
            PlaneSeat = -1;
            Rig.Hidden = false;
            cc.enabled = true;
            Teleport(at, plane.transform.eulerAngles.y);
            return true;
        }

        // Moved somewhere else as they are (health and ammo stay).
        public void Teleport(Vector3 at, float yaw)
        {
            cc.enabled = false;
            transform.position = at;
            cc.enabled = true;
            Yaw = yaw;
            velocity = Vector3.zero;
            knockedFor = 0;
        }

        public void TakeDamage(float damage, int attacker)
        {
            if (Dead) return;
            Health -= damage;
            DamageFlash = 1;
            PadFeel.Kick(.6f, .4f, .18f);
            if (attacker >= 0) LastAttacker = attacker;
            if (Health <= 0)
            {
                Health = 0;
                Deaths++;
                Rig.Die();
                Weapons.Sound(Weapons.Die, transform.position + Vector3.up, .9f);
            }
        }

        public WeaponDef Def => Weapons.All[Weapon];

        void Update()
        {
            var run = RunSession.Active;
            bool paused = run == null || run.Paused || run.OnTitle || WorldMap.Open;
            HitMarkerTime = Mathf.Max(0, HitMarkerTime - Time.deltaTime);
            DamageFlash = Mathf.Max(0, DamageFlash - Time.deltaTime * 1.5f);
            var k = Keyboard.current;
            var mouse = Mouse.current;
            var pad = Gamepad.current;
            Vector2 move = Vector2.zero;
            bool sprint = false, jump = false, fire = false, fireDown = false;
            if (OnBoat && (Dead || OnBoat.Wrecked)) LeaveBoat(true);
            if (InPlane && (Dead || InPlane.Wrecked)) LeavePlane(true);
            if (OnBoat)
            {
                // Aboard a boat: the view turns with the boat; aiming and shooting as on foot.
                float by = OnBoat.transform.eulerAngles.y;
                Yaw += Mathf.DeltaAngle(boatYaw, by);
                boatYaw = by;
            }
            if (Riding || InPlane)
            {
                // Passenger (or in an aircraft): look around only.
                if (!paused)
                {
                    float sensR = .09f * DriveCamera.Sensitivities[Mathf.Clamp(DriveCamera.SensitivityLevel, 0, DriveCamera.Sensitivities.Length - 1)];
                    Vector2 lookR = mouse != null ? mouse.delta.ReadValue() * sensR : Vector2.zero;
                    if (pad != null) lookR += pad.rightStick.ReadValue() * 160 * Time.deltaTime;
                    Yaw += lookR.x;
                    Pitch = Mathf.Clamp(Pitch - lookR.y, -60, 70);
                }
                Aiming = false;
                Firing = false;
                Kneeling = false;
                velocity = Vector3.zero;
                return;
            }
            if (!paused && !Dead)
            {
                float sens = .09f * DriveCamera.Sensitivities[Mathf.Clamp(DriveCamera.SensitivityLevel, 0, DriveCamera.Sensitivities.Length - 1)];
                Vector2 look = mouse != null ? mouse.delta.ReadValue() * sens : Vector2.zero;
                if (pad != null) look += pad.rightStick.ReadValue() * 160 * Time.deltaTime;
                float zoomSlow = Aiming ? Mathf.Clamp(Def.Zoom / 60f, .25f, 1) : 1;
                Yaw += look.x * zoomSlow;
                Pitch = Mathf.Clamp(Pitch - look.y * zoomSlow, -80, 80);
                Recoil.Settle(Time.deltaTime, -look.y * zoomSlow, ref Pitch);
                if (k != null)
                {
                    move.y = (k.wKey.isPressed ? 1 : 0) - (k.sKey.isPressed ? 1 : 0);
                    move.x = (k.dKey.isPressed ? 1 : 0) - (k.aKey.isPressed ? 1 : 0);
                    sprint = k.leftShiftKey.isPressed;
                    jump = k.spaceKey.wasPressedThisFrame;
                    for (int i = 0; i < Weapons.All.Length && i < 9; i++)
                        if (k[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame) Select(i);
                    // R is "back to my car" now (FootManager); Z reloads.
                    if (k.zKey.wasPressedThisFrame) StartReload();
                    if (k.gKey.wasPressedThisFrame) Kneeling = !Kneeling;
                    // Ctrl (either one): crouch down / stand up.
                    if (k.leftCtrlKey.wasPressedThisFrame || k.rightCtrlKey.wasPressedThisFrame) Crouching = !Crouching;
                }
                if (pad != null)
                {
                    move += pad.leftStick.ReadValue();
                    sprint |= pad.leftStickButton.isPressed;
                    jump |= pad.buttonSouth.wasPressedThisFrame;
                    if (pad.buttonNorth.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame) Select((Weapon + 1) % Weapons.All.Length);
                    if (pad.leftShoulder.wasPressedThisFrame) Select((Weapon + Weapons.All.Length - 1) % Weapons.All.Length);
                    if (pad.buttonWest.wasPressedThisFrame) StartReload();
                    if (pad.dpad.down.wasPressedThisFrame) Kneeling = !Kneeling;
                    if (pad.dpad.left.wasPressedThisFrame) Crouching = !Crouching;
                }
                if (mouse != null)
                {
                    float wheel = mouse.scroll.ReadValue().y;
                    if (wheel > .1f) Select((Weapon + Weapons.All.Length - 1) % Weapons.All.Length);
                    if (wheel < -.1f) Select((Weapon + 1) % Weapons.All.Length);
                    fire = mouse.leftButton.isPressed;
                    fireDown = mouse.leftButton.wasPressedThisFrame;
                    // Aiming moved from the right mouse button to Q (held).
                    Aiming = (k != null && k.qKey.isPressed) && !Def.Melee;
                }
                if (pad != null)
                {
                    fire |= pad.rightTrigger.ReadValue() > .5f;
                    fireDown |= pad.rightTrigger.wasPressedThisFrame;
                    Aiming |= pad.leftTrigger.ReadValue() > .5f && !Def.Melee;
                }
            }
            else Aiming = false;
            // Aboard a boat the keys steer the boat (at the helm) or nothing: we stand where we are.
            if (OnBoat) { move = Vector2.zero; jump = sprint = false; Kneeling = Crouching = false; }
            // Kneeling: stand up by moving or jumping; no shooting from the knees.
            if (Kneeling && (move.sqrMagnitude > .05f || jump || Dead)) Kneeling = false;
            if (Kneeling) { move = Vector2.zero; fire = fireDown = false; Aiming = false; sprint = false; }
            Firing = fire && !Def.Melee && !Def.Thrown;

            // Movement relative to where we look.
            move = Vector2.ClampMagnitude(move, 1);
            if (sprint || jump || Dead) Crouching = false;
            float speed = Dead ? 0 : Crouching ? 1.4f : Aiming ? 2.2f : sprint && move.y > .1f ? 6.2f : 2.6f;
            var yawRot = Quaternion.Euler(0, Yaw, 0);
            Vector3 wish = yawRot * new Vector3(move.x, 0, move.y) * speed;
            bool grounded = cc.isGrounded;
            float accel = grounded ? 14 : 3;
            if (knockedFor > 0) { wish = Vector3.zero; accel = grounded ? 6 : .5f; }
            velocity.x = Mathf.MoveTowards(velocity.x, wish.x, accel * Time.deltaTime * Mathf.Max(1, speed));
            velocity.z = Mathf.MoveTowards(velocity.z, wish.z, accel * Time.deltaTime * Mathf.Max(1, speed));
            if (grounded && velocity.y < 0) velocity.y = -2;
            if (grounded && jump && !Dead) velocity.y = 5.2f;
            velocity.y -= 18 * Time.deltaTime;
            if (knockedFor > 0) knockedFor -= Time.deltaTime;
            // On the moving carrier's deck: carried along with it (and turned with it).
            var carrier = Carrier.Active;
            if (carrier && cc.enabled)
            {
                bool aboard = carrier.Under(transform.position);
                Vector3 moved = carrier.Delta(transform.position, out float turned);
                if (aboard) { cc.Move(moved); Yaw += turned; }
                carrier.EndFrame();
            }
            if (cc.enabled) cc.Move(velocity * Time.deltaTime);
            if (!Dead && !OnBoat) CarHits();
            if (transform.position.y < -40) velocity = Vector3.zero;

            // The body faces where we look; the rig is told the speed in its own frame.
            transform.rotation = yawRot;
            Vector3 local = Quaternion.Inverse(yawRot) * new Vector3(velocity.x, 0, velocity.z);
            LocalForward = local.z; LocalStrafe = local.x;
            Rig.Forward = local.z;
            Rig.Strafe = local.x;
            Rig.Grounded = grounded || velocity.y > -3 && velocity.y < 0;
            Rig.Armed = !Def.Melee && !Def.Thrown && !Dead && !Kneeling;
            Rig.Kneeling = Kneeling;
            Rig.Crouching = Crouching;
            // A lower body when crouched: behind low cover bullets pass over.
            float wantHeight = Crouching ? 1.25f : 1.8f;
            if (!Mathf.Approximately(cc.height, wantHeight)) { cc.height = wantHeight; cc.center = new Vector3(0, wantHeight / 2, 0); }
            Rig.AimDirection = AimRotation * Vector3.forward;

            // Weapon.
            if (!Dead && !paused && !Reloading)
            {
                bool wants = Def.Auto ? fire : fireDown;
                if (wants && Time.time >= nextShot)
                {
                    if (!Def.Melee && ammo[Weapon] <= 0) StartReload();
                    else Fire();
                }
            }
            if (Reloading == false && reloadWeapon >= 0)
            {
                ammo[reloadWeapon] = Weapons.All[reloadWeapon].Magazine;
                reloadWeapon = -1;
            }
        }

        int reloadWeapon = -1;
        float knockedFor;

        // --- cars running into us ------------------------------------------------------------
        static readonly Collider[] near = new Collider[32];
        readonly System.Collections.Generic.HashSet<Transform> seen = new();
        readonly System.Collections.Generic.Dictionary<Transform, Vector3> lastPos = new();
        readonly System.Collections.Generic.Dictionary<Transform, float> ignoredUntil = new();
        readonly System.Collections.Generic.List<Transform> expired = new();

        // Velocity of whatever car this is: the physics body, the traffic car's own speed, the
        // friend's car as received, or (kinematic) worked out from how far it moved.
        Vector3 CarVelocity(Transform root, Rigidbody rb)
        {
            var friend = root.GetComponent<Net.RemoteCar>();
            if (friend) return friend.Velocity;
            var traffic = root.GetComponent<TrafficCar>();
            if (traffic && !traffic.Wrecked) return root.forward * traffic.Speed;
            if (rb && !rb.isKinematic) return rb.linearVelocity;
            Vector3 v = Vector3.zero;
            if (lastPos.TryGetValue(root, out var was) && Time.deltaTime > 0) v = (root.position - was) / Time.deltaTime;
            return v;
        }

        void CarHits()
        {
            // Hits we already took: that car passes through us for a moment instead of stopping dead.
            expired.Clear();
            foreach (var kv in ignoredUntil) if (Time.time > kv.Value) expired.Add(kv.Key);
            foreach (var t in expired)
            {
                ignoredUntil.Remove(t);
                if (t) foreach (var c in t.GetComponentsInChildren<Collider>()) if (c && cc) Physics.IgnoreCollision(cc, c, false);
            }
            Vector3 centre = transform.position + Vector3.up * .9f;
            int n = Physics.OverlapSphereNonAlloc(centre, 9, near, (1 << 8) | (1 << 9), QueryTriggerInteraction.Ignore);
            seen.Clear();
            for (int i = 0; i < n; i++)
            {
                var c = near[i];
                var rb = c.attachedRigidbody;
                var root = rb ? rb.transform : c.transform;
                if (!seen.Add(root)) continue;
                Vector3 v = CarVelocity(root, rb);
                lastPos[root] = root.position;
                if (ignoredUntil.ContainsKey(root)) continue;
                float speed = new Vector3(v.x, 0, v.z).magnitude;
                if (speed < 4.5f) continue;                                  // ~16 km/h: just a nudge
                // Where we are relative to the car a tenth of a second from now.
                Vector3 ahead = centre - v * .1f;
                Vector3 closest = c.ClosestPoint(ahead);
                Vector3 gap = ahead - closest; gap.y = 0;
                if (gap.magnitude > .55f) continue;
                if (Vector3.Dot(v, centre - root.position) < 0) continue;       // moving away from us
                HitByCar(root, v, speed);
            }
            if (lastPos.Count > 64) lastPos.Clear();
        }

        void HitByCar(Transform car, Vector3 v, float speed)
        {
            ignoredUntil[car] = Time.time + 1.5f;
            foreach (var c in car.GetComponentsInChildren<Collider>()) if (c && cc) Physics.IgnoreCollision(cc, c, true);
            float kmh = speed * 3.6f;
            float damage = Mathf.Clamp((kmh - 12) * 2.4f, 8, 250);
            // Thrown along with the car and up.
            Vector3 push = v * .75f; push.y = 0;
            Vector3 side = transform.position - car.position; side.y = 0;
            push += side.normalized * Mathf.Min(3, speed * .15f);
            velocity = push + Vector3.up * Mathf.Min(8, 2.5f + speed * .12f);
            knockedFor = 1.2f;
            Kneeling = false;
            int attacker = -1;
            string who = "ВАС СБИЛА МАШИНА";
            var friend = car.GetComponent<Net.RemoteCar>();
            if (friend && Net.CoopNet.Active)
                foreach (var kv in Net.CoopNet.Active.Remotes)
                    if (kv.Value == friend) { attacker = kv.Key; who = "ВАС СБИЛ  " + Net.CoopNet.Active.NameOf(kv.Key); }
            if (car.GetComponent<TrafficCar>()) who = "ВАС СБИЛА МАШИНА ИЗ ТРАФИКА";
            DriveCamera.Shake = Mathf.Max(DriveCamera.Shake, .8f);
            PadFeel.Kick(1, .8f, .45f);
            bool wasAlive = !Dead;
            TakeDamage(damage, attacker);
            HitsByCars++;
            Debug.Log($"AUTOBAHN foot hit by car {car.name} at {kmh:0} km/h: damage {damage:0}, health {Health:0}");
            if (wasAlive && Dead) FootManager.Instance?.LocalDeath(who, attacker);
        }

        public int HitsByCars { get; private set; }

        // An explosion nearby.
        public void Blast(Vector3 at, float radius)
        {
            if (Dead || Riding) return;
            Vector3 body = transform.position + Vector3.up * .9f;
            float d = Vector3.Distance(at, body);
            float f = 1 - d / (radius * 1.1f);
            if (f <= 0) return;
            Vector3 away = body - at; away.y = 0;
            velocity = away.normalized * (3 + 8 * f) + Vector3.up * (2 + 5 * f);
            knockedFor = .6f + f;
            Kneeling = false;
            PadFeel.Kick(1, 1, .5f);
            DriveCamera.Shake = Mathf.Max(DriveCamera.Shake, 1.2f * f);
            bool wasAlive = !Dead;
            TakeDamage(10 + 110 * f * f, -1);
            Debug.Log($"AUTOBAHN foot blast at {d:0.0} m: health {Health:0}");
            if (wasAlive && Dead) FootManager.Instance?.LocalDeath("ВАС НАКРЫЛО ВЗРЫВОМ", -1);
        }

        // Automated checks.
        public void TestFire() { nextShot = 0; Fire(); }

        void StartReload()
        {
            if (Def.Melee || Reloading || ammo[Weapon] >= Def.Magazine) return;
            reloadUntil = Time.time + Def.Reload;
            reloadWeapon = Weapon;
            Weapons.Sound(Weapons.ReloadSound, transform.position + Vector3.up, .7f);
        }

        public void Select(int w)
        {
            if (w == Weapon || w < 0 || w >= Weapons.All.Length) return;
            Weapon = w;
            reloadUntil = 0;
            reloadWeapon = -1;
            nextShot = Time.time + .3f;
            Weapons.Sound(Weapons.Pull, transform.position + Vector3.up, .6f);
        }

        // Where the camera looks: shots go from the middle of the screen.
        public Quaternion AimRotation => Quaternion.Euler(Pitch, Yaw, 0);

        Ray AimRay()
        {
            var cam = Camera.main;
            if (cam) return new Ray(cam.transform.position, cam.transform.forward);
            return new Ray(transform.position + Vector3.up * 1.6f, AimRotation * Vector3.forward);
        }

        // Everything but ourselves (2), loose props (11) and trees (12). Our own car (8) is hit
        // too: we are on foot whenever we shoot, and an empty car can be blown up.
        const int ShotMask = ~(1 << 2 | 1 << 11 | 1 << 12);
        static readonly RaycastHit[] shotHits = new RaycastHit[16];

        // The nearest hit, past the boat we stand in.
        bool Cast(Vector3 from, Vector3 dir, float range, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(from, dir, shotHits, range, ShotMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = shotHits[i];
                if (OnBoat && h.collider.GetComponentInParent<Boat>() == OnBoat) continue;
                if (h.distance < nearest) { nearest = h.distance; best = h; }
            }
            return nearest < float.MaxValue;
        }

        void Fire()
        {
            var def = Def;
            nextShot = Time.time + def.Interval;
            Vector3 muzzleWorld = gun ? Hold.MuzzlePoint : transform.position + Vector3.up * 1.4f;
            if (def.Thrown)
            {
                ammo[Weapon]--;
                Rig.Punch();
                ThrowStart(out Vector3 from, out Vector3 throwVelocity);
                Bomb.Throw(from, throwVelocity, cc);
                Weapons.Sound(Weapons.Pull, from, .7f, .8f);
                // Online the "aim point" of a throw is its starting velocity.
                Fired?.Invoke(Weapon, throwVelocity);
                if (ammo[Weapon] <= 0) StartReload();
                return;
            }
            if (def.Melee)
            {
                Rig.Punch();
                var chest = transform.position + Vector3.up * 1.2f;
                foreach (var hit in Physics.OverlapSphere(chest + transform.forward * 1.1f, .8f, ShotMask))
                    if (Damage(hit, hit.ClosestPoint(chest), -transform.forward, def)) break;
                return;
            }
            ammo[Weapon]--;
            Rig.Shoot();
            var ray = AimRay();
            // The camera sits behind the player in third person: start the ray at the player.
            float skip = Vector3.Dot(transform.position + Vector3.up * 1.5f - ray.origin, ray.direction);
            if (skip > 0) ray.origin += ray.direction * skip;
            float spread = (def.Spread + Recoil.Bloom) * (Aiming ? .45f : 1) * (velocity.sqrMagnitude > 4 ? 1.6f : 1);
            Vector3 lastPoint = ray.origin + ray.direction * def.Range;
            for (int p = 0; p < def.Pellets; p++)
            {
                Vector3 dir = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0) * ray.direction;
                if (Cast(ray.origin, dir, def.Range, out RaycastHit hit))
                {
                    lastPoint = hit.point;
                    bool flesh = Damage(hit.collider, hit.point, hit.normal, def);
                    Weapons.Impact(hit.point, hit.normal, flesh);
                }
                else lastPoint = ray.origin + dir * def.Range;
                if (p == 0 || p % 3 == 0) Weapons.Tracer(muzzleWorld, lastPoint);
            }
            Weapons.Flash(muzzleWorld, ray.direction);
            Weapons.Sound(Weapons.Shot, muzzleWorld, .9f, def.Pitch);
            DriveCamera.Shake = Mathf.Max(DriveCamera.Shake, def.Damage * def.Pellets / 250f);
            Recoil.Shot(def, Aiming, Crouching, ref Pitch);
            Hold.Kick(def, FootManager.FirstPerson);
            Fired?.Invoke(Weapon, lastPoint);
        }

        // A bomb leaves the right hand, along where we look and a little up.
        void ThrowStart(out Vector3 from, out Vector3 v)
        {
            var yaw = Quaternion.Euler(0, Yaw, 0);
            from = transform.position + Vector3.up * 1.55f + yaw * new Vector3(.25f, 0, .45f);
            Vector3 dir = AimRay().direction;
            v = dir * Bomb.ThrowSpeed + Vector3.up * Bomb.ThrowLift + new Vector3(velocity.x, 0, velocity.z);
        }

        // The arc the bomb will fly while aiming with it (Q / left trigger).
        LineRenderer arc;
        readonly Vector3[] arcPoints = new Vector3[40];

        void DrawArc()
        {
            bool show = Aiming && Def.Thrown && !Dead && !Riding && ammo[Weapon] > 0 && !Reloading;
            if (!show) { if (arc) arc.enabled = false; return; }
            if (!arc)
            {
                arc = new GameObject("Bomb arc").AddComponent<LineRenderer>();
                arc.transform.SetParent(transform, false);
                arc.gameObject.layer = 2;
                arc.useWorldSpace = true;
                arc.widthMultiplier = .05f;
                arc.numCapVertices = 2;
                arc.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                arc.receiveShadows = false;
                var m = Art.Material("Bomb arc", new Color(1f, .45f, .15f), 0, 0, true);
                m.SetColor("_EmissionColor", new Color(1f, .45f, .15f) * 2);
                arc.sharedMaterial = m;
            }
            ThrowStart(out Vector3 from, out Vector3 v);
            int n = 0;
            Vector3 last = from;
            arcPoints[n++] = from;
            for (int i = 1; i < arcPoints.Length; i++)
            {
                Vector3 p = Bomb.Arc(from, v, i * .06f);
                if (Physics.Linecast(last, p, out RaycastHit hit, ShotMask, QueryTriggerInteraction.Ignore))
                {
                    arcPoints[n++] = hit.point;
                    break;
                }
                arcPoints[n++] = p;
                last = p;
            }
            arc.enabled = true;
            arc.positionCount = n;
            arc.SetPositions(arcPoints);
        }

        // What a bullet (or a fist) does to what it hits. True for people (blood).
        readonly System.Collections.Generic.Dictionary<Object, float> carDamage = new();

        bool Damage(Collider c, Vector3 at, Vector3 normal, WeaponDef def)
        {
            float dmg = def.Damage;
            if (Wanted.Shot(c, dmg)) return true;    // the police: a crime, or an officer hit (Police/Wanted.cs)
            var avatar = c.GetComponentInParent<Net.RemoteAvatar>();
            if (avatar)
            {
                if (avatar.Dead) return true;
                bool head = at.y - avatar.transform.position.y > 1.5f;
                int d = Mathf.RoundToInt(dmg * (head ? 2 : 1));
                HitRemote?.Invoke(avatar.Slot, d, 0);
                HitMarkerTime = .2f;
                Weapons.Sound(Weapons.HitMarker, transform.position + Vector3.up, .5f);
                return true;
            }
            var remoteCar = c.GetComponentInParent<Net.RemoteCar>();
            if (remoteCar)
            {
                int slot = -1;
                if (Net.CoopNet.Active)
                    foreach (var pair in Net.CoopNet.Active.Remotes) if (pair.Value == remoteCar) slot = pair.Key;
                if (slot >= 0) HitRemote?.Invoke(slot, Mathf.RoundToInt(dmg), 1);
                HitMarkerTime = .15f;
                return false;
            }
            var aircraft = c.GetComponentInParent<Airplane>();
            if (aircraft)
            {
                if (aircraft.Wrecked || aircraft == InPlane) return false;
                if (aircraft.Remote) Net.PlaneNet.Damaged(aircraft, dmg * .4f);
                else aircraft.Damage(dmg * .4f);
                HitMarkerTime = .12f;
                return false;
            }
            var boat = c.GetComponentInParent<Boat>();
            if (boat)
            {
                if (boat.Wrecked || boat == OnBoat) return false;
                if (boat.Remote) Net.BoatNet.Damaged(boat, dmg * .5f);
                else boat.Damage(dmg * .5f);
                HitMarkerTime = .12f;
                return false;
            }
            var traffic = c.GetComponentInParent<TrafficCar>();
            if (traffic && !traffic.Wrecked && Net.CoopNet.IsGuest)
            {
                // The host owns the traffic: every hit goes there (about 140 damage blows a car up).
                Net.CoopNet.ReportDamage(traffic, dmg * .72f, -normal * Mathf.Min(1.5f, dmg * .02f), dmg >= 30 ? 1.5f : 0);
                return false;
            }
            if (traffic && !traffic.Wrecked)
            {
                carDamage.TryGetValue(traffic, out float sum);
                sum += dmg;
                carDamage[traffic] = sum;
                if (sum >= 140)
                {
                    carDamage.Remove(traffic);
                    if (Net.CoopNet.IsGuest) Net.CoopNet.ReportHit(traffic);
                    else Explosion.Blow(traffic);
                }
                else if (sum >= 30 && traffic.State != TrafficCar.Mode.Stunned) traffic.Stun(1.5f);
                return false;
            }
            var parked = c.GetComponentInParent<ParkedCarBody>();
            if (parked && !parked.Wrecked)
            {
                carDamage.TryGetValue(parked, out float sum);
                sum += dmg;
                carDamage[parked] = sum;
                if (sum >= 120) { carDamage.Remove(parked); Explosion.Blow(parked); }
                return false;
            }
            // Our own car, standing empty: enough bullets and it goes up like any other.
            var own = c.GetComponentInParent<Vehicle>();
            if (own && own.WreckedFor <= 0)
            {
                carDamage.TryGetValue(own, out float sum);
                sum += dmg;
                carDamage[own] = sum;
                if (sum >= 160) { carDamage.Remove(own); own.Explode(); }
                return false;
            }
            var rb = c.attachedRigidbody;
            if (rb && !rb.isKinematic && !rb.GetComponent<Vehicle>())
                rb.AddForceAtPosition(-normal * dmg * .3f, at, ForceMode.Impulse);
            return false;
        }

        void LateUpdate()
        {
            DrawArc();
            // The gun: in the right hand, pointing where we aim; in first person held in view.
            if (gunFor != Weapon)
            {
                if (gun) Destroy(gun.gameObject);
                gun = Weapons.Model(Def, transform, out muzzle);
                gunFor = Weapon;
                if (gun) foreach (var t in gun.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
            }
            if (gun) gun.gameObject.SetActive(!Dead && !Kneeling && !Riding && !InPlane);
            // Placed by WeaponHold, after the body is animated and the camera has moved.
            Hold.Gun = gun;
            Hold.Muzzle = muzzle;
            Hold.Def = Def;
            Hold.Aim = AimRotation;
            Hold.ViewModel = FootManager.FirstPerson && !Dead;
            Hold.AimAtCrosshair = true;
            Hold.Aiming = Aiming;
            Hold.Reloading = Reloading;
            Hold.Ignore = OnBoat ? OnBoat.transform : null;
        }
    }
}

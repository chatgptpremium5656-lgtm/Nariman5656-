using UnityEngine;
using UnityEngine.InputSystem;

namespace Autobahn
{
    // Getting out of the car and back in (F), the on-foot camera (first / third person: V or C),
    // the on-foot HUD (crosshair, health, ammo, kill feed), death and respawn, and damage the
    // player's own car takes from friends' guns online.
    public sealed class FootManager : MonoBehaviour
    {
        public static FootManager Instance { get; private set; }
        public static bool FirstPerson;
        public static bool OnFoot => FootPlayer.Active != null;
        public float CarHealth = 100;
        public int Kills;

        RunSession run;
        DriveCamera driveCamera;
        Transform cam;
        float deadTimer, camDistance = 3.2f;
        readonly System.Collections.Generic.List<(string Text, float Time)> feed = new();
        string deathText;
        GUIStyle big, small, tiny, centred, tinyCentred, healthStyle, ammoStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Instance) return;
            var go = new GameObject("On foot");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<FootManager>();
        }

        public void Feed(string text)
        {
            feed.Add((text, Time.unscaledTime));
            if (feed.Count > 5) feed.RemoveAt(0);
        }

        Vehicle Car => run != null ? run.Player : null;

        void Update()
        {
            run ??= RunSession.Active;
            if (run == null || Car == null) return;
            if (!driveCamera) driveCamera = FindFirstObjectByType<DriveCamera>();
            var k = Keyboard.current;
            var pad = Gamepad.current;
            bool menu = run.Paused || run.OnTitle || WorldMap.Open;
            var foot = FootPlayer.Active;

            // Mouse: captured while walking, free in menus.
            if (foot)
            {
                Cursor.lockState = menu ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = menu;
            }

            if (menu || ProfileStore.Testing && !Automated) return;
            // F, or up on the pad's cross (and circle on foot): in and out of cars.
            bool use = (k?.fKey.wasPressedThisFrame ?? false) || (pad?.dpad.up.wasPressedThisFrame ?? false) || (pad?.buttonEast.wasPressedThisFrame ?? false) && foot;
            bool view = foot && ((k?.cKey.wasPressedThisFrame ?? false) || (pad?.rightStickButton.wasPressedThisFrame ?? false));
            if (view && foot) FirstPerson = !FirstPerson;

            if (!foot)
            {
                if (use && Car.Speed < 25 && Car.WreckedFor <= 0) GetOut();
                return;
            }

            // On foot.
            if (foot.Dead)
            {
                deadTimer += Time.deltaTime;
                if (deadTimer > 4.5f) Respawn();
                return;
            }
            deadTimer = 0;
            if (foot.Riding)
            {
                // The friend's car went away (left the game) or blew up with us inside.
                var ride = foot.Riding;
                if (!ride || !ride.gameObject.activeInHierarchy) { StopRiding(); return; }
                if (ride.Charred)
                {
                    StopRiding();
                    var f = FootPlayer.Active;
                    if (f && !f.Dead) { f.TakeDamage(999, -1); LocalDeath("МАШИНА ВЗОРВАЛАСЬ ВМЕСТЕ С ВАМИ", -1); }
                    return;
                }
                if (use) StopRiding();
                return;
            }
            // R: back into our own car. Close by, we just get in; further away the car is brought
            // to a free spot beside us first (Z reloads now).
            if (k?.rKey.wasPressedThisFrame ?? false)
            {
                BackToCar();
                return;
            }
            if (foot.OnBoat)
            {
                // F at the helm: ashore, if there is dry ground within a jump.
                if (use && !foot.LeaveBoat()) run.Scoring.Announce("ПРИЧАЛЬТЕ К БЕРЕГУ");
                return;
            }
            if (foot.InPlane)
            {
                if (use && !foot.LeavePlane()) run.Scoring.Announce("СНАЧАЛА ОСТАНОВИТЕСЬ НА ЗЕМЛЕ");
                return;
            }
            if (!use) return;
            if (NearCar(foot.transform.position)) { GetIn(); return; }
            var boat = NearBoat(foot.transform.position);
            if (boat) { foot.BoardBoat(boat, boat.FreeSeat()); return; }
            var plane = NearPlane(foot.transform.position);
            if (plane) { foot.BoardPlane(plane, plane.FreeSeat()); return; }
            int friend = NearFriendCar(foot.transform.position, out var friendCar);
            if (friend >= 0)
            {
                if (FriendCarEmpty(friend)) TakeFriendCar(friend, friendCar);
                else Ride(friend, friendCar);
            }
        }

        // A boat to get into: whole, with a free place, close to us.
        public static Boat NearBoat(Vector3 p)
        {
            Boat best = null;
            float bestD = 5.5f;
            foreach (var b in Boat.All)
            {
                if (!b || b.Wrecked || b.FreeSeat() < 0) continue;
                float d = Vector3.Distance(p, b.transform.position) - b.Length * .35f;
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        // An aircraft to get into: standing by its side, with a free seat.
        public static Airplane NearPlane(Vector3 p)
        {
            Airplane best = null;
            float bestD = 4.5f;
            foreach (var a in Airplane.All)
            {
                if (!a || a.Wrecked || a.FreeSeat() < 0) continue;
                var local = a.transform.InverseTransformPoint(p);
                // Beside the fuselage (not out at a wing tip), or under a big one's nose.
                float d = Mathf.Max(Mathf.Abs(local.x) - a.HalfWidth, Mathf.Abs(local.z) - a.HalfLength, 0);
                if (d < bestD && local.y < a.Height + 2 && local.y > -3) { bestD = d; best = a; }
            }
            return best;
        }

        // R on foot: in the car, wherever it was.
        public bool BackToCar()
        {
            var foot = FootPlayer.Active;
            if (foot && foot.OnBoat) foot.LeaveBoat(true);
            if (foot && foot.InPlane) foot.LeavePlane(true);
            if (!foot || foot.Dead || foot.Riding || !Car) return false;
            bool near = NearCar(foot.transform.position) && Car.WreckedFor <= 0;
            if (!near && !CallCar()) return false;
            GetIn();
            return true;
        }

        static readonly float[] CallRings = { 4, 6.5f, 9, 13, 18 };

        // The car comes to us: parked beside the person, facing where they look. If there is no
        // room (or the person is lost off the map), the person goes to the nearest road and the
        // car stops next to them there.
        public bool CallCar()
        {
            var foot = FootPlayer.Active;
            var car = Car;
            if (!foot || foot.Dead || foot.Riding || !car) return false;
            if (car.WreckedFor > 0) car.ClearWreck();
            Vector3 p = foot.transform.position;
            Vector3 look = Quaternion.Euler(0, foot.Yaw, 0) * Vector3.forward;
            bool lost = p.y < Island.SeaY - 1 || p.y < -30 || !SpawnSpot.Ground(p + Vector3.up, out _);
            if (!lost && SpawnSpot.ForCar(p, look, CallRings, out Vector3 at, out Quaternion rot))
                car.Place(at, rot);
            else
            {
                car.Place(p + Vector3.up * 2, Quaternion.LookRotation(look));
                if (Route.Endless) GridCity.Recover(car); else Island.Recover(car, 0);
                Vector3 side = car.transform.position - car.transform.right * 2.6f;
                if (!SpawnSpot.ForPerson(car.transform.position, car.transform.forward, new[] { 2.4f, 3.5f, 5f }, out Vector3 standAt))
                    standAt = side;
                foot.Teleport(standAt, car.transform.eulerAngles.y);
            }
            car.Damage.Repair();
            car.Body.linearVelocity = Vector3.zero;
            car.Body.angularVelocity = Vector3.zero;
            run.Scoring.Announce("МАШИНА ПОДАНА");
            return true;
        }

        // What F does, for the automated checks.
        public void Use()
        {
            var foot = FootPlayer.Active;
            if (!foot)
            {
                if (Car && Car.Speed < 25 && Car.WreckedFor <= 0) GetOut();
                return;
            }
            if (foot.Riding) { StopRiding(); return; }
            if (NearCar(foot.transform.position)) { GetIn(); return; }
            int friend = NearFriendCar(foot.transform.position, out var friendCar);
            if (friend < 0) return;
            if (FriendCarEmpty(friend)) TakeFriendCar(friend, friendCar);
            else Ride(friend, friendCar);
        }

        // --- friends' cars ------------------------------------------------------------------
        public int NearFriendCar(Vector3 p, out Net.RemoteCar car)
        {
            car = null;
            var net = Net.CoopNet.Active;
            if (!Net.CoopNet.Online || net == null) return -1;
            foreach (var kv in net.Remotes)
            {
                var c = kv.Value;
                if (!c || !c.HasState || !c.gameObject.activeInHierarchy || c.Charred) continue;
                var local = c.transform.InverseTransformPoint(p);
                if (Mathf.Abs(local.x) < c.Width / 2 + 2.2f && Mathf.Abs(local.z) < c.Length / 2 + 1.2f && Mathf.Abs(local.y) < 3)
                {
                    car = c;
                    return kv.Key;
                }
            }
            return -1;
        }

        // Nobody at the wheel: the friend is walking about (or riding with somebody else).
        public static bool FriendCarEmpty(int slot)
        {
            var a = Net.FootNet.AvatarOf(slot);
            return a && a.Visible;
        }

        public string FriendName(int slot) => Net.CoopNet.Active ? Net.CoopNet.Active.NameOf(slot) : "друг";

        // The friend's car is empty: we drive it away, and the friend gets our car where we left it.
        public void TakeFriendCar(int slot, Net.RemoteCar friendCar)
        {
            var car = Car;
            if (!car || !friendCar) return;
            Net.FootNet.SendSwap(slot, run.Profile.car, run.Profile.paint, car.transform.position, car.transform.eulerAngles.y);
            Vector3 at = friendCar.transform.position + Vector3.up * .15f;
            Quaternion rot = Quaternion.Euler(0, friendCar.transform.eulerAngles.y, 0);
            run.Profile.car = Mathf.Clamp(friendCar.CarId, 0, CarCatalog.Count - 1);
            run.Profile.paint = Mathf.Max(0, friendCar.Paint);
            run.ApplyCar();
            car.Place(at, rot);
            CarHealth = 100;
            friendCar.Ghost(3);
            GetIn();
            Swaps++;
            Feed("Вы сели за руль машины  " + FriendName(slot));
            Debug.Log($"AUTOBAHN took friend {slot}'s car {run.Profile.car}");
        }

        // A friend took our empty car: ours is now the one they left, where they left it.
        public void CarTaken(int slot, int carId, int paint, Vector3 at, float yaw)
        {
            if (run == null || !Car) return;
            var foot = FootPlayer.Active;
            if (foot && foot.Riding) return;
            run.Profile.car = Mathf.Clamp(carId, 0, CarCatalog.Count - 1);
            run.Profile.paint = Mathf.Max(0, paint);
            run.ApplyCar();
            Car.Place(at + Vector3.up * .15f, Quaternion.Euler(0, yaw, 0));
            CarHealth = 100;
            var net = Net.CoopNet.Active;
            if (net && net.Remotes.TryGetValue(slot, out var theirs) && theirs) theirs.Ghost(3);
            Swaps++;
            Feed(FriendName(slot) + " угнал вашу машину — ваша теперь его, там где он её оставил");
            Debug.Log($"AUTOBAHN friend {slot} took our car; ours is now car {carId} at {at}");
        }

        public int Swaps { get; private set; }

        static Vector3 Seat(Net.RemoteCar car) => new(car.Width * .22f, .42f, .05f);

        public void Ride(int slot, Net.RemoteCar car)
        {
            var foot = FootPlayer.Active;
            if (!foot || !car) return;
            foot.Riding = car;
            foot.RideSlot = slot;
            foot.Yaw = 0;
            foot.Pitch = 8;
            foot.Kneeling = false;
            foot.Rig.Hidden = true;
            var cc = foot.GetComponent<CharacterController>();
            if (cc) cc.enabled = false;
            Feed("Вы пассажир у  " + FriendName(slot));
        }

        public void StopRiding()
        {
            var foot = FootPlayer.Active;
            if (!foot || !foot.Riding) { if (foot) foot.Riding = null; return; }
            var car = foot.Riding;
            Vector3 spot = car ? car.transform.TransformPoint(new Vector3(car.Width / 2 + .9f, .3f, 0)) : foot.transform.position;
            if (car && Physics.Raycast(spot + Vector3.up * 2, Vector3.down, out RaycastHit hit, 6, ~(1 << 8 | 1 << 2 | 1 << 9 | 1 << 13), QueryTriggerInteraction.Ignore))
                spot.y = hit.point.y + .05f;
            float yaw = car ? car.transform.eulerAngles.y + foot.Yaw : foot.Yaw;
            foot.Riding = null;
            foot.RideSlot = -1;
            foot.Rig.Hidden = false;
            var cc = foot.GetComponent<CharacterController>();
            foot.transform.position = spot;
            if (cc) cc.enabled = true;
            foot.Yaw = yaw;
            foot.Pitch = 0;
        }

        // Killed here (hit by a car, an explosion): the kill feed and the killer's score.
        public void LocalDeath(string text, int attacker)
        {
            deathText = text;
            Net.FootNet.Killed(attacker);
            if (!Net.CoopNet.Online) Feed(text);
        }

        // Automated checks drive this through the public methods.
        public static bool Automated;

        public bool NearCar(Vector3 p)
        {
            var car = Car;
            if (!car) return false;
            var local = car.transform.InverseTransformPoint(p);
            var art = car.GetComponent<CarBody>();
            float half = art && art.Shape != null ? art.Shape.Width / 2 : 1;
            float len = art && art.Shape != null ? art.Shape.Length / 2 : 2.4f;
            return Mathf.Abs(local.x) < half + 2.2f && Mathf.Abs(local.z) < len + 1.2f && Mathf.Abs(local.y) < 3;
        }

        public void GetOut()
        {
            var car = Car;
            var art = car.GetComponent<CarBody>();
            float half = art && art.Shape != null ? art.Shape.Width / 2 : 1;
            // Driver's door (left side), or the right side if something is in the way.
            Vector3 spot = car.transform.position + car.transform.right * -(half + .9f) + Vector3.up * .3f;
            if (Physics.CheckCapsule(spot + Vector3.up * .4f, spot + Vector3.up * 1.6f, .3f, ~(1 << 8 | 1 << 2), QueryTriggerInteraction.Ignore))
                spot = car.transform.position + car.transform.right * (half + .9f) + Vector3.up * .3f;
            if (Physics.Raycast(spot + Vector3.up * 2, Vector3.down, out RaycastHit hit, 6, ~(1 << 8 | 1 << 2), QueryTriggerInteraction.Ignore))
                spot.y = hit.point.y + .05f;
            var p = FootPlayer.Spawn(spot, car.transform.eulerAngles.y);
            p.Weapon = 1;
            // Out of the cockpit view: the roof and glass come back for the walk round.
            art?.Interior(false);
            DriverInput.OnFoot = true;
            car.Body.linearVelocity *= .3f;
            if (driveCamera) driveCamera.enabled = false;
            cam = Camera.main ? Camera.main.transform : null;
            camDistance = 3.2f;
        }

        public void GetIn()
        {
            var foot = FootPlayer.Active;
            if (foot && foot.Riding) foot.Riding = null;
            if (foot) Destroy(foot.gameObject);
            DriverInput.OnFoot = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (driveCamera) { driveCamera.enabled = true; driveCamera.SetMode(driveCamera.Mode); }
        }

        void Respawn()
        {
            deadTimer = 0;
            var foot = FootPlayer.Active;
            var car = Car;
            Vector3 at = car.transform.position + car.transform.right * -2.5f + Vector3.up * .5f;
            if (Physics.Raycast(at + Vector3.up * 3, Vector3.down, out RaycastHit hit, 10, ~(1 << 8 | 1 << 2), QueryTriggerInteraction.Ignore))
                at.y = hit.point.y + .05f;
            foot.Respawn(at, car.transform.eulerAngles.y);
            deathText = null;
        }

        // Called by the network when a friend's bullet hits us.
        public void Damaged(int attacker, int damage, int kind)
        {
            string killer = Net.CoopNet.Active ? Net.CoopNet.Active.NameOf(attacker) : "?";
            var foot = FootPlayer.Active;
            if (kind == 0 && foot && !foot.Dead)
            {
                foot.TakeDamage(damage, attacker);
                if (foot.Dead)
                {
                    deathText = "ВАС УБИЛ  " + killer;
                    Net.FootNet.Killed(attacker);
                }
            }
            // Our car is hit even when we are out of it (standing empty it can still be blown up).
            else if (kind == 1 && Car && Car.WreckedFor <= 0)
            {
                CarHealth -= damage * .5f;
                if (CarHealth <= 0)
                {
                    CarHealth = 100;
                    Car.Explode();
                    deathText = "ВАШУ МАШИНУ ВЗОРВАЛ  " + killer;
                    Net.FootNet.Killed(attacker);
                }
            }
        }

        // Automated checks place the camera themselves.
        public static bool FreeCamera;

        void LateUpdate()
        {
            var foot = FootPlayer.Active;
            if (!foot || FreeCamera) return;
            cam = Camera.main ? Camera.main.transform : cam;
            if (!cam) return;
            var lens = Camera.main;
            // High up, a near plane of a few centimetres leaves too little depth precision: the
            // sea showed through the runway and the roads. Pushed out with the height (and the
            // chase distance), back to normal on the ground.
            float wantNear = foot.InPlane ? Mathf.Clamp(foot.InPlane.Altitude * .015f + (FirstPerson ? .1f : 1), .1f, 12) : .04f;
            if (!Mathf.Approximately(lens.nearClipPlane, wantNear)) lens.nearClipPlane = wantNear;
            if (foot.Riding)
            {
                var car = foot.Riding.transform;
                foot.transform.SetPositionAndRotation(car.TransformPoint(Seat(foot.Riding)), Quaternion.Euler(0, car.eulerAngles.y + foot.Yaw, 0));
                var look = Quaternion.Euler(foot.Pitch, car.eulerAngles.y + foot.Yaw, 0);
                if (FirstPerson)
                    cam.SetPositionAndRotation(car.TransformPoint(Seat(foot.Riding) + new Vector3(0, .78f, 0)), look);
                else
                {
                    Vector3 pivot = car.position + Vector3.up * 1.7f;
                    Vector3 back = look * Vector3.back;
                    float dist = 6.5f;
                    if (Physics.SphereCast(pivot, .25f, back, out RaycastHit h2, dist, ~(1 << 2 | 1 << 8 | 1 << 9 | 1 << 11 | 1 << 12 | 1 << 13), QueryTriggerInteraction.Ignore))
                        dist = Mathf.Max(1.5f, h2.distance - .1f);
                    cam.SetPositionAndRotation(pivot + back * dist, look);
                }
                lens.fieldOfView = Mathf.MoveTowards(lens.fieldOfView, 65, Time.deltaTime * 160);
                return;
            }
            if (foot.InPlane)
            {
                // Chase view behind the aircraft; the mouse looks around it.
                var plane = foot.InPlane.transform;
                foot.transform.SetPositionAndRotation(foot.InPlane.SeatPoint(foot.PlaneSeat), plane.rotation);
                Vector3 flatFwd = plane.forward; flatFwd.y *= .5f;
                var baseRot = Quaternion.LookRotation(flatFwd.sqrMagnitude > .01f ? flatFwd.normalized : Vector3.forward);
                var look = baseRot * Quaternion.Euler(foot.Pitch, foot.Yaw, 0);
                if (FirstPerson)
                {
                    cam.SetPositionAndRotation(foot.InPlane.SeatPoint(foot.PlaneSeat) + plane.up * .85f, plane.rotation * Quaternion.Euler(foot.Pitch - 10, foot.Yaw, 0));
                    lens.fieldOfView = Mathf.MoveTowards(lens.fieldOfView, 70, Time.deltaTime * 60);
                    return;
                }
                float chase = foot.InPlane.Type.CamDistance;
                Vector3 pivot = plane.position + Vector3.up * (foot.InPlane.Height * .55f + 1);
                Vector3 want = pivot + look * new Vector3(0, 0, -chase);
                float dist = chase;
                if (Physics.SphereCast(pivot, .3f, want - pivot, out RaycastHit ph, dist, ~(1 << 2 | 1 << 8 | 1 << 9 | 1 << 11 | 1 << 12 | 1 << 13), QueryTriggerInteraction.Ignore))
                    dist = Mathf.Max(3, ph.distance - .2f);
                Vector3 camAt = pivot + (want - pivot).normalized * dist;
                cam.SetPositionAndRotation(Vector3.Lerp(cam.position, camAt, 1 - Mathf.Exp(-Time.deltaTime * 12)), Quaternion.LookRotation(pivot + plane.forward * 6 - camAt));
                lens.fieldOfView = Mathf.MoveTowards(lens.fieldOfView, 62 + foot.InPlane.Kmh * .03f, Time.deltaTime * 40);
                if (DriveCamera.Shake > 0)
                {
                    cam.rotation *= Quaternion.Euler(Random.Range(-1f, 1f) * DriveCamera.Shake * 2, Random.Range(-1f, 1f) * DriveCamera.Shake * 2, 0);
                    DriveCamera.Shake = Mathf.Max(0, DriveCamera.Shake - Time.deltaTime * 3);
                }
                return;
            }
            if (foot.OnBoat)
            {
                var boat = foot.OnBoat.transform;
                foot.transform.SetPositionAndRotation(foot.OnBoat.SeatPoint(foot.BoatSeat), Quaternion.Euler(0, foot.Yaw, 0));
                var look = foot.AimRotation;
                if (FirstPerson)
                    cam.SetPositionAndRotation(foot.transform.position + Vector3.up * 1.6f + look * new Vector3(0, 0, .15f), look);
                else
                {
                    Vector3 pivot = foot.transform.position + Vector3.up * 2f + look * new Vector3(.45f, 0, 0);
                    Vector3 back = look * Vector3.back;
                    float dist = foot.Aiming ? 3.5f : 7;
                    if (Physics.SphereCast(pivot, .25f, back, out RaycastHit h3, dist, ~(1 << 2 | 1 << 8 | 1 << 9 | 1 << 11 | 1 << 12 | 1 << 13), QueryTriggerInteraction.Ignore)
                        && !h3.collider.GetComponentInParent<Boat>())
                        dist = Mathf.Max(2, h3.distance - .1f);
                    cam.SetPositionAndRotation(pivot + back * dist, look);
                }
                if (foot.Hold) cam.rotation *= foot.Hold.CameraKick;
                float boatFov = foot.Aiming ? foot.Def.Zoom : 62 + foot.OnBoat.Speed * .12f;
                lens.fieldOfView = Mathf.MoveTowards(lens.fieldOfView, boatFov, Time.deltaTime * 120);
                return;
            }
            var aim = foot.AimRotation;
            if (FirstPerson && !foot.Dead)
            {
                Vector3 eye = foot.Rig.Head ? foot.Rig.Head.position + aim * new Vector3(0, .05f, .12f) : foot.transform.position + Vector3.up * 1.65f;
                cam.SetPositionAndRotation(eye, aim);
                foot.Rig.HideHead = true;
            }
            else
            {
                foot.Rig.HideHead = false;
                Vector3 pivot = foot.transform.position + Vector3.up * (foot.Crouching ? 1.2f : 1.62f);
                float want = foot.Aiming ? 1.6f : foot.Dead ? 5 : 3.2f;
                Vector3 shoulder = aim * new Vector3(foot.Aiming ? .6f : .45f, .1f, 0);
                Vector3 back = aim * Vector3.back;
                float dist = want;
                if (Physics.SphereCast(pivot + shoulder, .2f, back, out RaycastHit hit, want, ~(1 << 2 | 1 << 8 | 1 << 11 | 1 << 12), QueryTriggerInteraction.Ignore))
                    dist = Mathf.Max(.4f, hit.distance - .1f);
                camDistance = dist < camDistance ? dist : Mathf.MoveTowards(camDistance, dist, Time.deltaTime * 6);
                cam.SetPositionAndRotation(pivot + shoulder + back * camDistance, aim);
            }
            // The jolt of the last shots (stronger in first person, see WeaponHold.Kick).
            if (foot.Hold) cam.rotation *= foot.Hold.CameraKick;
            float fov = foot.Aiming ? foot.Def.Zoom : 65;
            lens.fieldOfView = Mathf.MoveTowards(lens.fieldOfView, fov, Time.deltaTime * 160);
            if (DriveCamera.Shake > 0)
            {
                cam.rotation *= Quaternion.Euler(Random.Range(-1f, 1f) * DriveCamera.Shake * 2, Random.Range(-1f, 1f) * DriveCamera.Shake * 2, 0);
                DriveCamera.Shake = Mathf.Max(0, DriveCamera.Shake - Time.deltaTime * 3);
            }
        }

        // --- HUD ----------------------------------------------------------------------------
        void Styles()
        {
            if (big != null) return;
            big = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            small = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            tiny = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleRight };
            centred = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
            tinyCentred = new GUIStyle(tiny) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            healthStyle = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, fontSize = 18 };
            ammoStyle = new GUIStyle(small) { fontSize = 24 };
            useGUILayout = false;
        }

        static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        void OnGUI()
        {
            if (run == null) return;
            Styles();
            float w = Screen.width, h = Screen.height, s = Mathf.Max(.6f, h / 900f);
            // Kill feed, top right, on foot and in the car alike.
            for (int i = 0; i < feed.Count; i++)
            {
                float age = Time.unscaledTime - feed[i].Time;
                if (age > 7) continue;
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01(7 - age));
                GUI.Label(new Rect(w - 520 * s, 120 * s + i * 26 * s, 500 * s, 24 * s), feed[i].Text, tiny);
                GUI.color = Color.white;
            }
            if (!FootPlayer.Active && Car && Car.WreckedFor <= 0 && Event.current.type == EventType.Repaint) deathText = null;
            if (!string.IsNullOrEmpty(deathText) && Car && Car.WreckedFor > 0)
                GUI.Label(new Rect(0, h * .3f, w, 60 * s), deathText, big);

            var foot = FootPlayer.Active;
            if (!foot || run.Paused || run.OnTitle || WorldMap.Open) return;
            var def = foot.Def;
            if (foot.DamageFlash > 0) Fill(new Rect(0, 0, w, h), new Color(.6f, 0, 0, foot.DamageFlash * .35f));
            if (foot.Dead)
            {
                GUI.Label(new Rect(0, h * .35f, w, 60 * s), string.IsNullOrEmpty(deathText) ? "ВЫ ПОГИБЛИ" : deathText, big);
                return;
            }
            if (foot.Riding)
            {
                GUI.Label(new Rect(0, h * .12f, w, 30 * s), "ВЫ ПАССАЖИР У  " + FriendName(foot.RideSlot) + "   •   F — ВЫЙТИ   •   C — ВИД", centred);
                return;
            }
            if (foot.InPlane)
            {
                var a = foot.InPlane;
                // Controls on two short lines under the top panels; the instruments above the bottom edge.
                string line1 = foot.PlaneSeat == 0 ? "SHIFT / CTRL  тяга    W / S  нос вниз / вверх    A / D  крен    Q / E  руль" : "ВЫ ПАССАЖИР    мышь  осмотреться";
                string line2 = foot.PlaneSeat == 0 ? "G  шасси    ПРОБЕЛ  тормоз    F  выйти на земле    R  к машине    C  кабина" : "F  выйти на земле    R  к машине    C  вид из кабины";
                if (foot.PlaneSeat == 0 && a.Type.Weapons)
                    line2 += a.NukeReady > 0 ? $"    ЛКМ  ракета    B  бомба через {a.NukeReady:0} с" : $"    ЛКМ  ракета    B  ЯДЕРНАЯ БОМБА (выше {Nuke.MinAltitude:0} м)";
                GUI.Label(new Rect(0, h * .17f, w, 24 * s), line1, tinyCentred);
                GUI.Label(new Rect(0, h * .17f + 24 * s, w, 24 * s), line2, tinyCentred);
                string status = $"{Mathf.RoundToInt(a.Kmh)} КМ/Ч    ВЫСОТА {Mathf.RoundToInt(a.Altitude)} М    ТЯГА {Mathf.RoundToInt(a.Throttle * 100)}%    ШАССИ {(a.GearDown ? "ВЫПУЩЕНО" : "УБРАНО")}    НА БОРТУ {a.Aboard}/{a.SeatCount}";
                GUI.Label(new Rect(0, h - 150 * s, w, 30 * s), status, tinyCentred);
                if (a.Stalling) GUI.Label(new Rect(0, h * .3f, w, 40 * s), "СВАЛИВАНИЕ! ОТДАЙТЕ НОС ВНИЗ (W)", big);
                return;
            }
            if (foot.OnBoat)
            {
                var b = foot.OnBoat;
                string role = foot.BoatSeat == 0 ? "КАТЕР   •   W/S — ГАЗ   •   A/D — РУЛЬ" : "КАТЕР  (ПАССАЖИР)";
                GUI.Label(new Rect(0, h * .2f, w, 30 * s), role + "   •   ЛКМ — СТРЕЛЯТЬ   •   F — НА БЕРЕГ   •   R — К МАШИНЕ", centred);
                GUI.Label(new Rect(0, h * .2f + 30 * s, w, 30 * s), "КОРПУС  " + Mathf.CeilToInt(b.Health) + "   •   " + Mathf.RoundToInt(b.Speed) + " КМ/Ч   •   НА БОРТУ " + b.Aboard + "/" + Boat.Seats, centred);
            }
            // Crosshair: four ticks, closer when aiming; red for a hit.
            float gap = (foot.Aiming ? 5 : 11) * s, len = 8 * s, th = Mathf.Max(2, 2 * s);
            Color cc = foot.HitMarkerTime > 0 ? new Color(1, .3f, .25f) : new Color(1, 1, 1, .9f);
            float cx = w / 2, cy = h / 2;
            if (!(foot.Aiming && def.Zoom < 20))
            {
                Fill(new Rect(cx - gap - len, cy - th / 2, len, th), cc);
                Fill(new Rect(cx + gap, cy - th / 2, len, th), cc);
                Fill(new Rect(cx - th / 2, cy - gap - len, th, len), cc);
                Fill(new Rect(cx - th / 2, cy + gap, th, len), cc);
            }
            else
            {
                // Sniper scope: black ring and thin lines.
                Fill(new Rect(0, cy - .5f, w, 1), Color.black);
                Fill(new Rect(cx - .5f, 0, 1, h), Color.black);
                Fill(new Rect(0, 0, cx - h * .45f, h), Color.black);
                Fill(new Rect(cx + h * .45f, 0, cx - h * .45f, h), Color.black);
            }
            // Health bottom centre (the minimap takes the bottom left).
            float hx = w / 2 - 150 * s;
            Fill(new Rect(hx, h - 46 * s, 300 * s, 16 * s), new Color(0, 0, 0, .5f));
            Fill(new Rect(hx + 2 * s, h - 44 * s, 296 * s * foot.Health / 100f, 12 * s), Color.Lerp(new Color(1, .25f, .2f), new Color(.3f, .9f, .45f), foot.Health / 100f));
            GUI.Label(new Rect(hx, h - 76 * s, 300 * s, 28 * s), "ЗДОРОВЬЕ  " + Mathf.CeilToInt(foot.Health), healthStyle);
            // Weapon and ammo bottom right.
            GUI.Label(new Rect(w - 420 * s, h - 96 * s, 390 * s, 28 * s), def.Name, small);
            string ammoText = def.Melee ? "" : foot.Reloading ? "ПЕРЕЗАРЯДКА…" : foot.Ammo + " / " + def.Magazine;
            GUI.Label(new Rect(w - 420 * s, h - 66 * s, 390 * s, 30 * s), ammoText, ammoStyle);
            // Context prompt near a car.
            var prompt = centred;
            if (foot.Riding)
                GUI.Label(new Rect(0, h * .12f, w, 30 * s), "ВЫ ПАССАЖИР У  " + FriendName(foot.RideSlot) + "   •   F — ВЫЙТИ   •   C — ВИД", prompt);
            else if (foot.OnBoat) { }
            else if (NearPlane(foot.transform.position) is Airplane np)
                GUI.Label(new Rect(0, h * .62f, w, 30 * s), np.FreeSeat() == 0 ? "F  —  СЕСТЬ ЗА ШТУРВАЛ" : "F  —  СЕСТЬ В САМОЛЁТ ПАССАЖИРОМ", prompt);
            else if (NearCar(foot.transform.position))
                GUI.Label(new Rect(0, h * .62f, w, 30 * s), "F  —  СЕСТЬ В МАШИНУ", prompt);
            else if (!foot.OnBoat && NearBoat(foot.transform.position) is Boat nb)
                GUI.Label(new Rect(0, h * .62f, w, 30 * s), nb.FreeSeat() == 0 ? "F  —  СЕСТЬ ЗА РУЛЬ КАТЕРА" : "F  —  СЕСТЬ В КАТЕР ПАССАЖИРОМ", prompt);
            else
            {
                int friend = NearFriendCar(foot.transform.position, out _);
                if (friend >= 0)
                    GUI.Label(new Rect(0, h * .62f, w, 30 * s), FriendCarEmpty(friend) ? "F  —  СЕСТЬ ЗА РУЛЬ  (машина " + FriendName(friend) + ")" : "F  —  СЕСТЬ ПАССАЖИРОМ  К " + FriendName(friend), prompt);
            }
            if (foot.Kneeling)
                GUI.Label(new Rect(0, h * .68f, w, 30 * s), "ВЫ НА КОЛЕНЯХ И ИЗВИНЯЕТЕСЬ   •   G — ВСТАТЬ", prompt);
            if (Kills > 0)
                GUI.Label(new Rect(w - 420 * s, 80 * s, 390 * s, 26 * s), "УБИЙСТВ: " + Kills, tiny);
        }
    }
}

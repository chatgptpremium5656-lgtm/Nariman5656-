using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    public sealed class TrafficFlow : MonoBehaviour
    {
        public readonly List<TrafficCar> Cars = new();
        public Vehicle Player { get; private set; }

        public int Density { get; private set; } = 1;
        public bool Simulate = true;
        // The co-op friend's car, when one is connected: traffic keeps clear of it too.
        // Co-op friends' cars (up to four): traffic keeps clear of them too.
        public static readonly List<Net.RemoteCar> Remotes = new();
        static bool Live(Net.RemoteCar r) => r && r.HasState;
        TrafficSignals signals;
        public bool SignalsGreen => !signals || signals.MainGreen;
        public bool SignalStop(TrafficCar car, out float stopZ)
        {
            stopZ = 0;
            return signals && signals.StopAhead(car.Z + car.Length / 2, car.Speed, out stopZ);
        }
        public int Count
        {
            get
            {
                int n = 0;
                foreach (var c in Cars)
                    if (c.gameObject.activeSelf)
                        n++;
                return n;
            }
        }

        public float Difficulty => 1 + Density * .2f;
        // Cars on the island road (the rest drive the city streets).
        public int HighwayCount
        {
            get
            {
                int n = 0;
                foreach (var c in Cars)
                    if (c.gameObject.activeSelf && !c.OnGrid)
                        n++;
                return n;
            }
        }
        static float S(Vector3 p) => Route.Locate(p);
        public void Init(Vehicle player)
        {
            Player = player;
            signals = FindFirstObjectByType<TrafficSignals>();
            Color[] colors = {new(.75f, .75f, .71f), new(.18f, .22f, .25f), new(.42f, .075f, .05f), new(.06f, .15f, .25f), new(.56f, .49f, .35f), new(.14f, .15f, .15f),
                new(.86f, .86f, .84f), new(.03f, .035f, .04f), new(.10f, .20f, .42f), new(.48f, .50f, .52f), new(.12f, .22f, .14f), new(.60f, .12f, .06f)};
            // Everyday cars, and every car of the garage now and then (the lorries, the van and
            // the 6x6 drive on the truck profile below).
            BodyStyle[] styles = {BodyStyle.StyleSedan, BodyStyle.Mercedes, BodyStyle.StyleCar, BodyStyle.AudiS4, BodyStyle.StyleJeep, BodyStyle.BmwX6,
                BodyStyle.StyleSedan, BodyStyle.Classic, BodyStyle.StyleCar, BodyStyle.MercedesE50, BodyStyle.StyleSport, BodyStyle.Tocus,
                BodyStyle.StyleSedan, BodyStyle.BmwM5, BodyStyle.StyleCar, BodyStyle.Mercedes300, BodyStyle.StyleJeep, BodyStyle.G63,
                BodyStyle.StyleSedan, BodyStyle.Sport, BodyStyle.StyleCar, BodyStyle.LabCoupe, BodyStyle.StyleSport, BodyStyle.Touring,
                BodyStyle.StyleSedan, BodyStyle.AudiR8, BodyStyle.StyleJeep, BodyStyle.MilitaryJeep, BodyStyle.StyleCar, BodyStyle.Supercar,
                BodyStyle.StyleSedan, BodyStyle.Porsche911};
            // Pool: 60 cars (it was 108, enough for the heaviest setting everywhere at once), 30 on
            // Mobile; the density settings share it out between the road and the city.
            int pool = PoolSize;
            for (int i = 0; i < pool; i++)
            {
                var go = new GameObject("Civilian " + i);
                go.transform.SetParent(transform);
                go.layer = 9;
                var rigid = go.AddComponent<Rigidbody>();
                var box = go.AddComponent<BoxCollider>();
                var art = go.AddComponent<CarBody>();
                // Mixed traffic: saloons, hatchbacks, estates and SUVs, plus vans on the truck profile.
                // The truck profile drives the heavy vehicles: vans, lorries and the odd minibus.
                BodyStyle[] heavy = { BodyStyle.DeliveryVan, BodyStyle.BoxTruck, BodyStyle.StyleBus, BodyStyle.DeliveryVan, BodyStyle.Levo, BodyStyle.BoxTruck };
                var style = (Personality)(i % 5) == Personality.Truck ? heavy[(i / 5) % heavy.Length] : styles[i % styles.Length];
                if (QaFlags.Has("nobought") && (style == BodyStyle.Sport || style == BodyStyle.Classic || style == BodyStyle.Tocus || style == BodyStyle.DeliveryVan || style == BodyStyle.BoxTruck)) style = BodyStyle.StyleSedan;
                // Every ninth car is an ivory German taxi.
                bool taxi = i % 9 == 4 && style != BodyStyle.StyleBus && style != BodyStyle.DeliveryVan && style != BodyStyle.BoxTruck && style != BodyStyle.Tocus;
                art.Build(taxi ? new Color(.93f, .88f, .70f) : colors[(i * 5) % colors.Length], false, style);
                var shape = art.Shape;
                float height = string.IsNullOrEmpty(shape.Prefab) ? shape.Top[3] * 1.1f : shape.Height > .5f ? shape.Height * .92f : 1.5f;
                box.size = new(shape.Width * .96f, height, shape.Length * .96f);
                box.center = new(0, height / 2 + .08f, 0);
                // Nobody looks inside a car going past: no interior, engine or suspension.
                if (string.IsNullOrEmpty(shape.Prefab))
                {
                    foreach (var wheel in art.Wheels)
                    {
                        Art.Combine(wheel);
                        Art.Strip(wheel);
                    }
                    Art.DropInside(art.Visual);
                    Art.Combine(art.Visual);
                    Art.Strip(art.Visual);
                }
                else if (shape.ExtraWheels == null && !art.Visual.GetComponentInChildren<SkinnedMeshRenderer>(true))
                {
                    // Imported models: one renderer for the body and one per wheel.
                    Art.MergeModel(art.Visual, style.ToString(), true);
                    for (int w = 0; w < art.Wheels.Length; w++) Art.MergeModel(art.Wheels[w], style + " wheel " + w, true);
                }
                else
                    Art.DropInside(art.Visual);
                FarLook(go, shape.Width * .96f, height, shape.Length * .96f, art.PaintColor);
                var c = go.AddComponent<TrafficCar>();
                c.Init(this);
                c.Driver = (Personality)(i % 5);
                Cars.Add(c);
                c.Length = shape.Length;
                c.Width = shape.Width;
                if (c.Driver == Personality.Truck)
                    go.name = "Delivery van " + i;

                // Indicators at the rear corners, on the body (not floating behind a sloped tail).
                float lampX = shape.TailX > 0 ? shape.TailX + .12f : shape.Width / 2 - .30f, lampY = shape.TailHeight > 0 ? shape.TailHeight : shape.LampHeight > 0 ? shape.LampHeight : shape.Top[1] * .78f, lampZ = shape.RearZ - .01f;
                if (taxi)
                {
                    go.name = "Taxi " + i;
                    Art.Box(go.transform, "Taxi sign", new(0, height * .93f + .1f, -.2f), new(.62f, .2f, .22f), Art.Amber);
                }
                                // Headlamp lenses at the front (the car's +Z), switched on at night.
                float lensX = shape.LampX > 0 ? shape.LampX : shape.Width / 2 - .34f, lensY = shape.LampHeight > 0 ? shape.LampHeight : Mathf.Min(.75f, height * .45f), lensZ = shape.FrontZ - .02f;
                c.NightLamps = new[]
                {
                    Art.Box(go.transform, "Headlamp", new(-lensX, lensY, lensZ), new(.32f, .14f, .05f), Art.White).GetComponent<Renderer>(),
                    Art.Box(go.transform, "Headlamp", new(lensX, lensY, lensZ), new(.32f, .14f, .05f), Art.White).GetComponent<Renderer>(),
                };
                foreach (var r in c.NightLamps) r.enabled = false;
                c.LeftLamp = Art.Box(go.transform, "Left indicator", new(-lampX, lampY, lampZ), new(.12f, .06f, .04f), Art.Amber).GetComponent<Renderer>();
                c.RightLamp = Art.Box(go.transform, "Right indicator", new(lampX, lampY, lampZ), new(.12f, .06f, .04f), Art.Amber).GetComponent<Renderer>();
                c.Place(210 + i * 45, i % 3, Cruise(c));
                go.SetActive(i < 42);
            }
        }

        // How many traffic cars there are (made once, at start): configurable, fewer on Mobile.
        public static int DefaultPool = 60, MobilePool = 30;
        public static int PoolSize => Mathf.Max(1, GraphicsQuality.Mobile ? MobilePool : DefaultPool);

        // From this far a traffic car is drawn as a box in its colour (body and cabin): all that
        // is seen of it there, for a sliver of the triangles and draw calls of the model.
        public static float FarLookDistance = 60;
        static readonly Dictionary<Color, Material> farPaint = new();
        static Material farGlass;

        static void FarLook(GameObject car, float width, float height, float length, Color colour)
        {
            var near = car.GetComponentsInChildren<Renderer>(true);
            if (near.Length == 0) return;
            if (!farPaint.TryGetValue(colour, out var paint))
                farPaint[colour] = paint = Art.Material("Traffic far paint", colour, .3f, .55f);
            if (!farGlass) farGlass = Art.Material("Traffic far glass", new Color(.05f, .06f, .07f), .2f, .8f);
            var far = new GameObject("Far look").transform;
            far.SetParent(car.transform, false);
            var body = Art.Box(far, "Far body", new Vector3(0, .12f + height * .25f, 0), new Vector3(width, height * .5f, length), paint);
            var cabin = Art.Box(far, "Far cabin", new Vector3(0, .12f + height * .68f, -length * .04f), new Vector3(width * .84f, height * .4f, length * .52f), farGlass);
            var boxes = new Renderer[] { body.GetComponent<Renderer>(), cabin.GetComponent<Renderer>() };
            foreach (var r in boxes)
            {
                r.gameObject.layer = car.layer;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            var group = car.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(.5f, near), new LOD(.001f, boxes) });
            group.RecalculateBounds();
            // Screen height of the car at FarLookDistance, with the camera's 60° view.
            float at = Mathf.Clamp(group.size / (FarLookDistance * 2 * Mathf.Tan(30 * Mathf.Deg2Rad)), .01f, .9f);
            group.SetLODs(new[] { new LOD(at, near), new LOD(.001f, boxes) });
        }

        float Cruise(TrafficCar c) => c.Driver switch
        {
            Personality.Slow => 25,
            Personality.Truck => 23,
            Personality.Fast => 39,
            Personality.Assertive => 35,
            _ => 30
        };
        public void SetDensity(int n)
        {
            Density = Mathf.Clamp(n, 0, 2);
            // Friends online spread out over the map: each extra driver brings more cars.
            int friends = 0;
            foreach (var r in Remotes) if (Live(r)) friends++;
            int road, city;
            if (Route.Endless)
            {
                // The city grid spreads traffic over many streets, so it gets a bigger share.
                road = 0;
                city = Mathf.RoundToInt((Density == 0 ? 51 : Density == 1 ? 81 : 108) * (1 + .35f * friends));
            }
            else
            {
                road = Mathf.RoundToInt((Density == 0 ? 24 : Density == 1 ? 42 : 63) * (1 + .5f * friends));
                city = Mathf.RoundToInt((Density == 0 ? 21 : Density == 1 ? 33 : 45) * (1 + .3f * friends));
            }
            // A smaller pool (Mobile, or set lower) keeps about a third of it for the city streets.
            int pool = Mathf.Min(Cars.Count, PoolSize);
            city = Mathf.Min(city, pool);
            road = Mathf.Min(road, pool - Mathf.Min(city, Mathf.RoundToInt(pool * .3f)));
            city = Mathf.Min(city, pool - road);
            for (int i = 0; i < Cars.Count; i++)
            {
                var c = Cars[i];
                bool onGrid = i >= road;
                bool active = i < road + city;
                bool wasActive = c.gameObject.activeSelf, wasGrid = c.OnGrid;
                c.OnGrid = onGrid;
                // Road cars that are new (or come over from the city) need a place on the road;
                // the city traffic places its own cars.
                if (active && !onGrid && (!wasActive || wasGrid))
                    active = TrySpawn(c);
                c.gameObject.SetActive(active);
            }
        }

        public void Restart()
        {
            int i = 0;
            foreach (var c in Cars)
            {
                if (!c.OnGrid)
                    c.Place(210 + i * 45, i % 3, Cruise(c));
                i++;
            }
        }

        // At night the traffic runs with its lamps on; a few real beams follow the cars
        // nearest the camera so they light the road around them.
        bool lampsLit;
        readonly List<Light> beams = new();
        float nextBeams;
        readonly List<TrafficCar> nearest = new();

        void Update()
        {
            bool night = Atmosphere.Night;
            if (night != lampsLit)
            {
                lampsLit = night;
                foreach (var c in Cars)
                    if (c.NightLamps != null)
                        foreach (var r in c.NightLamps) r.enabled = night;
            }
            if (beams.Count == 0)
                for (int i = 0; i < 8; i++)
                {
                    var l = new GameObject("Traffic beam").AddComponent<Light>();
                    l.transform.SetParent(transform, false);
                    l.type = LightType.Spot;
                    l.spotAngle = 70;
                    l.innerSpotAngle = 30;
                    l.range = 45;
                    l.intensity = 260;
                    l.color = new Color(1, .95f, .85f);
                    l.shadows = LightShadows.None;
                    l.enabled = false;
                    beams.Add(l);
                }
            if (!night || Player == null)
            {
                foreach (var l in beams) l.enabled = false;
                return;
            }
            if (Time.time >= nextBeams)
            {
                nextBeams = Time.time + .3f;
                nearest.Clear();
                Vector3 p = Player.transform.position;
                foreach (var c in Cars)
                    if (c.gameObject.activeSelf && !c.Wrecked && (c.transform.position - p).sqrMagnitude < 160 * 160)
                        nearest.Add(c);
                nearest.Sort((a, b) => (a.transform.position - p).sqrMagnitude.CompareTo((b.transform.position - p).sqrMagnitude));
            }
            // Each beam keeps its car while that car is still among the nearest and fades in and
            // out (the beams used to jump from car to car every 0.3 s: headlight pools blinked on
            // the road at night). Brightness also falls off with distance from the player.
            if (beamCar == null || beamCar.Length != beams.Count) { beamCar = new TrafficCar[beams.Count]; beamLevel = new float[beams.Count]; }
            nearestSet.Clear();
            for (int n = 0; n < nearest.Count && n < beams.Count; n++) nearestSet.Add(nearest[n]);
            float fade = Time.deltaTime * 3;
            for (int i = 0; i < beams.Count; i++)
            {
                var c = beamCar[i];
                bool keep = c && c.gameObject.activeSelf && !c.Wrecked && nearestSet.Contains(c);
                beamLevel[i] = Mathf.MoveTowards(beamLevel[i], keep ? 1 : 0, fade);
                if (!keep && beamLevel[i] <= 0) beamCar[i] = null;
            }
            foreach (var c in nearestSet)
            {
                if (System.Array.IndexOf(beamCar, c) >= 0) continue;
                int free = System.Array.IndexOf(beamCar, null);
                if (free < 0) break;
                beamCar[free] = c;
                beamLevel[free] = 0;
            }
            Vector3 me = Player.transform.position;
            for (int i = 0; i < beams.Count; i++)
            {
                var c = beamCar[i];
                if (!c) { beams[i].enabled = false; continue; }
                var t = c.transform;
                float far = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(90, 160, Vector3.Distance(t.position, me)));
                float value = 260 * beamLevel[i] * far;
                beams[i].intensity = value;
                beams[i].enabled = value > .5f;
                beams[i].transform.SetPositionAndRotation(t.position + t.forward * (c.Length / 2) + Vector3.up * .7f, t.rotation * Quaternion.Euler(6, 0, 0));
            }
        }

        TrafficCar[] beamCar;
        float[] beamLevel;
        readonly HashSet<TrafficCar> nearestSet = new();

        void FixedUpdate()
        {
            if (!Simulate)
                return;
            var probe = FirstFrames.Active ? System.Diagnostics.Stopwatch.StartNew() : null;
            try { FixedBody(); }
            finally { if (probe != null) FirstFrames.Log.Append($" road{probe.ElapsedMilliseconds}"); }
        }

        void FixedBody()
        {
            playerS = S(Player.transform.position);
            foreach (var c in Cars)
            {
                if (!c.gameObject.activeSelf || c.OnGrid)
                    continue;
                // A burnt-out wreck is cleared away after a while.
                if (c.Wrecked)
                {
                    if (Time.time - c.WreckTime > 15) TrySpawn(c);
                    continue;
                }
                c.Tick(Time.fixedDeltaTime);
                // A car stays while it is on somebody's stretch of road, from a little behind a
                // driver to well ahead of them; otherwise it is reused where a driver needs it.
                if (!NearADriver(c.Z) || c.Z > Route.Length + 30)
                    TrySpawn(c);
            }
            FillAhead();
            // More drivers online, more traffic: the density follows the number of players.
            int live = 0;
            foreach (var r in Remotes) if (Live(r)) live++;
            if (live != lastLive)
            {
                lastLive = live;
                SetDensity(Density);
            }
        }

        int lastLive;
        float playerS;
        readonly List<float> drivers = new();

        void CollectDrivers()
        {
            drivers.Clear();
            drivers.Add(S(Player.transform.position));
            foreach (var r in Remotes)
                if (Live(r)) drivers.Add(S(r.transform.position));
            Net.FootNet.People(people);
            foreach (var q in people) drivers.Add(S(q));
        }

        readonly List<Vector3> people = new();

        bool NearADriver(float z)
        {
            float p = playerS;
            if (z > p - (Player.Input.LookBack ? 1100 : 180) && z < p + 1650) return true;
            foreach (var r in Remotes)
                if (Live(r))
                {
                    float q = S(r.transform.position);
                    if (z > q - 180 && z < q + 1650) return true;
                }
            Net.FootNet.People(people);
            foreach (var man in people)
            {
                float q = S(man);
                if (z > q - 400 && z < q + 400) return true;
            }
            return false;
        }

        public void Leader(TrafficCar car, out float gap, out float speed)
        {
            gap = 9999;
            speed = car.Cruise;
            foreach (var other in Cars)
            {
                if (other == car || !other.gameObject.activeSelf || other.OnGrid || !(car.Occupies(other.Lane) || car.Occupies(other.TargetLane)))
                    continue;
                float dz = other.Z - car.Z;
                if (dz <= 0)
                    continue;
                float g = dz - (other.Length + car.Length) / 2;
                if (g < gap)
                {
                    gap = g;
                    speed = other.Speed;
                }
            }

            foreach (var r in Remotes)
            {
                if (!Live(r)) continue;
                float fx = Route.Lateral(r.transform.position);
                float dzf = S(r.transform.position) - car.Z;
                if (dzf > 0 && (Mathf.Abs(fx - (car.Lane - 1) * Route.LaneWidth) < 2.1f || Mathf.Abs(fx - (car.TargetLane - 1) * Route.LaneWidth) < 2.1f))
                {
                    float g = dzf - (car.Length + r.Length) / 2;
                    if (g < gap)
                    {
                        gap = g;
                        speed = Mathf.Max(0, Vector3.Dot(r.Velocity, Route.Forward(car.Z)));
                    }
                }
            }

            float playerX = Route.Lateral(Player.transform.position);
            float dzp = S(Player.transform.position) - car.Z;
            if (dzp > 0 && (Mathf.Abs(playerX - (car.Lane - 1) * Route.LaneWidth) < 2.1f || Mathf.Abs(playerX - (car.TargetLane - 1) * Route.LaneWidth) < 2.1f))
            {
                float g = dzp - (car.Length + 4.6f) / 2;
                if (g < gap)
                {
                    gap = g;
                    speed = Mathf.Max(0, Player.ForwardSpeed / 3.6f);
                }
            }
        }

        public bool LaneSafe(TrafficCar car, int lane)
        {
            if (lane < 0 || lane > 2)
                return false;
            foreach (var other in Cars)
            {
                if (other == car || !other.gameObject.activeSelf || other.OnGrid || !other.Occupies(lane))
                    continue;
                float dz = other.Z - car.Z;
                float clearance = 7 + (car.Length + other.Length) / 2 + (dz < 0 ? Mathf.Max(0, other.Speed - car.Speed) * 3 : car.Speed * .65f);
                if (Mathf.Abs(dz) < clearance)
                    return false;
            }

            foreach (var r in Remotes)
            {
                if (!Live(r)) continue;
                float fx = Route.Lateral(r.transform.position), dzf = S(r.transform.position) - car.Z;
                if (Mathf.Abs(fx - (lane - 1) * Route.LaneWidth) < 2.8f && Mathf.Abs(dzf) < 12 + car.Speed * 1.1f)
                    return false;
            }

            float x = Route.Lateral(Player.transform.position), target = (lane - 1) * Route.LaneWidth, dzp = S(Player.transform.position) - car.Z;
            if (Mathf.Abs(x - target) < 2.8f)
            {
                float safe = 12 + (dzp < 0 ? Mathf.Max(0, Player.ForwardSpeed / 3.6f - car.Speed) * 4 : car.Speed * 1.1f);
                if (Mathf.Abs(dzp) < safe)
                    return false;
            }

            return true;
        }

        // Cars to overtake: the player always has at least AheadMin cars in front, inside a ±40°
        // cone from 60 m out to AheadReach (450 m, 700 m above 150 km/h). When there are too few,
        // a car that has fallen far behind (or is far from every driver) is moved up ahead into
        // the lane with the fewest cars. Never nearer than 170 m in view of the camera, never on
        // top of another car. Only pooled cars are moved; nothing is created.
        public const float NearRadius = 250, AheadNear = 60, AheadCone = 40, HiddenWithin = 170;
        public int AheadMin => Density == 0 ? 6 : Density == 1 ? 10 : 14;
        public static float AheadReach(Vehicle player) => player && player.Speed > 150 ? 700 : 450;
        float nextFill;
        readonly int[] laneLoad = new int[3];

        // Which way "ahead" is: where the car is going, or where it points when nearly still.
        public static Vector3 AheadDirection(Vehicle player)
        {
            Vector3 f = player.Speed > 10 ? player.Body.linearVelocity : player.transform.forward;
            f.y = 0;
            return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        }

        public static bool InCone(Vehicle player, Vector3 forward, Vector3 spot, float reach)
        {
            Vector3 d = spot - player.transform.position;
            d.y = 0;
            float m = d.magnitude;
            return m >= AheadNear && m <= reach && Vector3.Angle(forward, d) <= AheadCone;
        }

        // Road cars in the cone ahead of the player right now.
        public int CountAhead()
        {
            if (Player == null) return 0;
            Vector3 f = AheadDirection(Player);
            float reach = AheadReach(Player);
            int n = 0;
            foreach (var c in Cars)
                if (c.gameObject.activeSelf && !c.OnGrid && !c.Wrecked && InCone(Player, f, c.transform.position, reach))
                    n++;
            return n;
        }

        void FillAhead()
        {
            if (Time.time < nextFill || Player == null) return;
            nextFill = Time.time + .2f;
            if (Island.InCity(Player.transform.position)) return;
            Vector3 f = AheadDirection(Player);
            float reach = AheadReach(Player);
            float sign = Vector3.Dot(f, Route.Forward(playerS)) >= 0 ? 1 : -1;
            int ahead = 0;
            laneLoad[0] = laneLoad[1] = laneLoad[2] = 0;
            TrafficCar spare = null;
            float spareScore = 0;
            CollectDrivers();
            foreach (var c in Cars)
            {
                if (!c.gameObject.activeSelf || c.OnGrid || c.Wrecked || c.State == TrafficCar.Mode.Stunned) continue;
                if (InCone(Player, f, c.transform.position, reach))
                {
                    ahead++;
                    laneLoad[Mathf.Clamp(c.Lane, 0, 2)]++;
                    continue;
                }
                // Spare: far behind the player (> 300 m), or far ahead beyond the window, and
                // nowhere near any other driver; and not in view where it stands now.
                float along = (c.Z - playerS) * sign;
                bool behind = along < -300, beyond = along > reach + 250;
                if (!behind && !beyond) continue;
                float gap = float.MaxValue;
                for (int i = 1; i < drivers.Count; i++) gap = Mathf.Min(gap, Mathf.Abs(c.Z - drivers[i]));
                if (gap < 400) continue;
                float score = behind ? 10000 - along : along;   // cars left behind go first
                // Out of sight where it stands (past ~800 m ahead it is only a speck in the haze).
                if (score <= spareScore || ((behind || along < 800) && InSight(c.transform.position, Player))) continue;
                spare = c;
                spareScore = score;
            }
            if (ahead >= AheadMin || spare == null) return;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                float d = Random.Range(AheadNear + 20, reach - 10);
                float z = playerS + sign * d;
                if (z < 60 || z > Route.Length - 50) continue;
                // The emptiest lane first, so the cars spread over all three.
                int lane = Random.Range(0, 3);
                for (int k = 0; k < 3; k++) if (laneLoad[k] < laneLoad[lane]) lane = k;
                if (attempt > 7) lane = Random.Range(0, 3);
                Vector3 spot = Route.Position(z, lane);
                if (!InCone(Player, f, spot, reach)) continue;
                bool clear = true;
                foreach (var o in Cars)
                    if (o != spare && o.gameObject.activeSelf && !o.OnGrid && o.Occupies(lane) && Mathf.Abs(o.Z - z) < 40)
                    {
                        clear = false;
                        break;
                    }
                if (!clear) continue;
                if (Vector3.Distance(spot, Player.transform.position) < HiddenWithin && InSight(spot, Player)) continue;
                spare.Place(z, lane, Cruise(spare));
                return;
            }
        }

        static readonly RaycastHit[] sightHits = new RaycastHit[8];

        // Would a car at this spot pop up in plain view of the camera? Outside the frame, or
        // behind something solid (buildings, hills), it is not seen.
        public static bool InSight(Vector3 spot, Vehicle player)
        {
            var cam = Camera.main;
            if (!cam) return false;
            Vector3 top = spot + Vector3.up * 1.2f;
            Vector3 v = cam.WorldToViewportPoint(top);
            if (v.z <= 0 || v.x < -.05f || v.x > 1.05f || v.y < -.05f || v.y > 1.05f) return false;
            Vector3 from = cam.transform.position, to = top - from;
            float length = to.magnitude;
            // Cars (player, traffic, parked) and wheels do not hide anything.
            int mask = ~((1 << 8) | (1 << 9) | (1 << 10));
            int n = Physics.RaycastNonAlloc(from, to / length, sightHits, length - 4, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var body = sightHits[i].rigidbody;
                if (player && body == player.Body) continue;
                return false;
            }
            return true;
        }

        bool TrySpawn(TrafficCar car)
        {
            // Ahead of one of the drivers, taking turns at random, so every player online gets
            // traffic wherever they are on the course, not only the host.
            CollectDrivers();
            for (int attempt = 0; attempt < 24; attempt++)
            {
                float pz = drivers[Random.Range(0, drivers.Count)];
                float z = pz + Random.Range(980, 1480);
                if (z > Route.Length - 50)
                    continue;
                // Never pop into view just in front of somebody else.
                bool seen = false;
                foreach (float d in drivers)
                    if (z > d - 200 && z < d + 900) seen = true;
                if (seen)
                    continue;
                if (z < 60)
                    continue;
                int lane = Random.Range(0, 3);
                bool clear = true;
                foreach (var o in Cars)
                    if (o != car && o.gameObject.activeSelf && !o.OnGrid && o.Occupies(lane) && Mathf.Abs(o.Z - z) < 55)
                    {
                        clear = false;
                        break;
                    }

                if (!clear)
                    continue;
                car.Place(z, lane, Cruise(car));
                return true;
            }

            return false;
        }
    }
}

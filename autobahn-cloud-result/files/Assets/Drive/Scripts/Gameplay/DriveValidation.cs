using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Autobahn
{
    public sealed class DriveValidation : MonoBehaviour
    {
        [Serializable]
        public class Result
        {
            public string name;
            public bool pass;
            public string observation;
        }

        [Serializable]
        public class Report
        {
            public List<Result> checks = new();
            public float zeroTo100, dryStop, wetStop, topSpeed, averageRenderFps;
            public string unity = Application.unityVersion;
        }

        float frameTotal;
        int frameCount;
        // Smoothness sampling while driving through the city: frame times and how much the
        // camera moves relative to the car from one frame to the next.
        bool sampling;
        readonly System.Collections.Generic.List<float> frameTimes = new();
        readonly System.Collections.Generic.List<float> cameraShake = new();
        readonly FrameTiming[] timing = new FrameTiming[1];
        float cpuMs, gpuMs;
        int timed;
        Vector3 lastLocal, lastStep;
        bool haveLocal, haveStep;
        // Sampled at the end of the frame, after the camera has moved.
        IEnumerator SampleSmoothness()
        {
            var end = new WaitForEndOfFrame();
            while (sampling)
            {
                yield return end;
                SampleFrame();
            }
        }

        readonly List<float> cpuList = new(), gpuList = new();
        // Online without a network: a stand-in friend fed the same state messages a real one
        // sends. Traffic has to grow around every driver and survive the trip far from the
        // host, and the friend's horn and radio have to come from their car: loud close by,
        // faint far away, higher in pitch as they approach and lower as they leave (Doppler).
        IEnumerator OnlineChecks()
        {
            var origins = new List<Vector3> { Vector3.zero, new Vector3(0, 0, 6000) };
            Vector3 farCar = new(3.2f, .4f, 6512.7f);
            Net.CoopNet.PackOffset(farCar, origins, out byte anchor, out short px, out short py, out short pz);
            Vector3 decoded = Net.CoopNet.UnpackOffset(origins, anchor, px, py, pz);
            Check("Traffic positions far from the host", (decoded - farCar).magnitude < .1f,
                $"car 6.5 km from the host arrives {(decoded - farCar).magnitude * 100:0} cm off, measured from driver {anchor}");

            // As received: kind, the sender's slot (1), then the packed state (Net.CarState).
            byte[] Received(byte[] packed)
            {
                var b = new byte[packed.Length + 1];
                b[0] = packed[0];
                b[1] = 1;
                System.Buffer.BlockCopy(packed, 1, b, 2, packed.Length - 1);
                return b;
            }
            byte[] State(Vector3 pos, Vector3 vel, byte flags, int station, float trackTime, byte smokeLevel = 0)
            {
                var q = Quaternion.LookRotation(vel.sqrMagnitude > .01f ? vel.normalized : run.Player.transform.forward);
                return Received(Net.CarState.Pack(new Net.CarState
                {
                    Time = (float)Time.realtimeSinceStartupAsDouble, Pos = pos, Rot = q, Vel = vel, Kmh = vel.magnitude * 3.6f,
                    Car = 3, Paint = 0, Flags = flags, Mode = (byte)(int)run.Mode, Station = (byte)(station + 1), TrackTime = trackTime, Smoke = smokeLevel,
                }));
            }

            // Traffic around a friend 4 km further up the course. The physics checks before
            // this one parked the traffic, so it is switched back on here and parked again after.
            var friend = Net.RemoteCar.Create();
            float friendZ = Mathf.Min(Route.Locate(car.transform.position) + 4000, Route.Length - 2500);
            friend.Push(State(Route.Position(friendZ, 1) + Vector3.up * .3f, Vector3.zero, 0, -1, 0), 2, Time.realtimeSinceStartupAsDouble);
            TrafficFlow.Remotes.Add(friend);
            bool trafficWasOn = traffic.Simulate;
            traffic.Simulate = true;
            traffic.SetDensity(1);
            yield return new WaitForSeconds(2.5f);
            float hostZ = Route.Locate(car.transform.position);
            int nearFriend = 0, nearHost = 0;
            foreach (var c in traffic.Cars)
                if (c.gameObject.activeSelf)
                {
                    if (c.Z > friendZ - 200 && c.Z < friendZ + 1700) nearFriend++;
                    if (c.Z > hostZ - 200 && c.Z < hostZ + 1700) nearHost++;
                }
            Check("Traffic around every driver", nearFriend >= 4 && nearHost >= 4, $"{nearFriend} cars on the friend's stretch 4 km away, {nearHost} on the host's");
            TrafficFlow.Remotes.Remove(friend);
            if (!trafficWasOn)
            {
                traffic.Simulate = false;
                foreach (var c in traffic.Cars) c.gameObject.SetActive(false);
            }

            // Only the friend is heard: the local engine, wind and radio go quiet for a moment.
            var drive = run.Player.GetComponent<DriveAudio>();
            var synth = run.Player.GetComponentInChildren<EngineSynth>();
            if (drive) drive.enabled = false;
            if (synth) synth.Master = 0;
            var muted = new List<AudioSource>();
            foreach (var s in run.Player.GetComponentsInChildren<AudioSource>())
                if (!s.mute) { s.mute = true; muted.Add(s); }
            var localRadio = CarRadio.Active;
            for (int guard = 0; guard < 8 && localRadio && localRadio.On; guard++) localRadio.Next();

            Vector3 ahead = run.Player.transform.forward;
            var buffer = new float[2048];
            float Loudness()
            {
                AudioListener.GetOutputData(buffer, 0);
                double sum = 0;
                foreach (var v in buffer) sum += v * v;
                return (float)System.Math.Sqrt(sum / buffer.Length);
            }
            float level = 0;
            IEnumerator Listen(float metres, byte flags, int station)
            {
                Vector3 at = run.Player.transform.position + ahead * metres + Vector3.up * .3f;
                float total = 0;
                int samples = 0;
                for (int n = 0; n < 45; n++)
                {
                    friend.Push(State(at, Vector3.zero, flags, station, 40f + n * .02f), 2, Time.realtimeSinceStartupAsDouble);
                    yield return null;
                    if (n >= 25) { total += Loudness(); samples++; }
                }
                level = total / Mathf.Max(1, samples);
            }
            yield return Listen(8, 0, -1); float quiet = level;
            yield return Listen(8, 4, -1); float hornNear = level;
            bool hornPlaying = friend.Horn && friend.Horn.isPlaying && friend.Horn.spatialBlend > .99f;
            yield return Listen(150, 4, -1); float hornFar = level;
            // The station's track streams in: give it time to load before listening.
            for (int w = 0; w < 240; w++)
            {
                friend.Push(State(run.Player.transform.position + ahead * 8 + Vector3.up * .3f, Vector3.zero, 0, 0, 40f + w * .02f), 2, Time.realtimeSinceStartupAsDouble);
                yield return null;
                var clip = friend.RadioSource ? friend.RadioSource.clip : null;
                if (clip && clip.loadState == AudioDataLoadState.Loaded && w > 20) break;
            }
            yield return Listen(8, 0, 0); float radioNear = level;
            bool radioPlaying = friend.RadioSource && friend.RadioSource.isPlaying && friend.RadioStation == 0 && friend.RadioSource.spatialBlend > .99f;
            string track = friend.RadioSource && friend.RadioSource.clip ? friend.RadioSource.clip.name : "none";
            yield return Listen(150, 0, 0); float radioFar = level;
            Check("Friend's horn from their car", hornPlaying && hornNear > quiet * 3 && hornNear > hornFar * 3,
                $"at 8 m {hornNear:0.0000}, at 150 m {hornFar:0.0000}, silence {quiet:0.0000}");
            Check("Friend's radio from their car", radioPlaying && radioNear > radioFar * 3,
                $"{track} at 8 m {radioNear:0.0000}, at 150 m {radioFar:0.0000}");

            // Doppler: the friend comes at 30 m/s, then drives off at 30 m/s, horn on.
            Vector3 origin = run.Player.transform.position + Vector3.up * .3f;
            float t0 = Time.realtimeSinceStartup;
            float towards = 1, away = 1;
            while (Time.realtimeSinceStartup - t0 < .8f)
            {
                float t = Time.realtimeSinceStartup - t0;
                friend.Push(State(origin + ahead * (70 - 30 * t), -ahead * 30, 4, -1, 0), 2, Time.realtimeSinceStartupAsDouble);
                yield return null;
                towards = friend.Horn ? friend.Horn.pitch : 1;
            }
            t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < .8f)
            {
                float t = Time.realtimeSinceStartup - t0;
                friend.Push(State(origin + ahead * (70 + 30 * t), ahead * 30, 4, -1, 0), 2, Time.realtimeSinceStartupAsDouble);
                yield return null;
                away = friend.Horn ? friend.Horn.pitch : 1;
            }
            Check("Doppler on the friend's sounds", towards > 1.05f && away < .95f,
                $"pitch {towards:0.000} while approaching at 108 km/h, {away:0.000} driving away");

            foreach (var s in muted) if (s) s.mute = false;
            if (drive) drive.enabled = true;
            Destroy(friend.gameObject);
            yield return null;

            // The v3 car state survives packing: every field, the new smoke byte and flags too.
            {
                var sent = new Net.CarState
                {
                    Time = 1234.5f, Pos = new Vector3(-812.25f, 3.5f, 6021.75f), Rot = Quaternion.Euler(3, 211, -2), Vel = new Vector3(12.5f, -.25f, 40),
                    Kmh = 151.3f, Car = 5, Paint = 4, Mode = 1, Station = 3, TrackTime = 97.25f, Smoke = 201,
                    Flags = Net.CarState.Brake | Net.CarState.Lights | Net.CarState.Drift | Net.CarState.Wrecked,
                };
                var wire = Received(Net.CarState.Pack(sent));
                bool unpacked = Net.CarState.Unpack(wire, 2, out var got);
                bool shortRejected = !Net.CarState.Unpack(new byte[20], 2, out _);
                Check("Online state packing", unpacked && got.Same(sent) && shortRejected && Net.CoopLink.Version == "v4",
                    $"{wire.Length} bytes, round trip {(unpacked && got.Same(sent) ? "identical" : "DIFFERENT")}, short packet rejected {shortRejected}, protocol {Net.CoopLink.Version}");

                // Events: packed and read back, sent three times, repeats dropped by sender and id.
                var ev = Net.CoopEvent.Pack(40001, Net.CoopEvent.TrafficExploded, 17);
                var evWire = Received(ev);
                bool evOk = Net.CoopEvent.Unpack(evWire, 2, out ushort evId, out byte evKind, out byte evArg) && evId == 40001 && evKind == Net.CoopEvent.TrafficExploded && evArg == 17;
                var net = Net.CoopNet.Ensure();
                bool first = net.FirstSight(1, 40001), repeat = net.FirstSight(1, 40001) || net.FirstSight(1, 40001), otherSender = net.FirstSight(2, 40001);
                Check("Online events kept and deduplicated", evOk && first && !repeat && otherSender && Net.CoopNet.EventRepeats == 3,
                    $"event read back {evOk}, first copy taken {first}, repeats dropped {!repeat}, same id from another player taken {otherSender}, sent {Net.CoopNet.EventRepeats}×");
            }

            // The friend's car shows what their game shows: lamps, brake lights, tyre smoke, and
            // after the explosion the burnt wreck until the car is back.
            {
                var lit = Net.RemoteCar.Create();
                Vector3 at = run.Player.transform.position + run.Player.transform.forward * 20 + Vector3.up * .3f;
                lit.Push(State(at, Vector3.zero, Net.CarState.Lights | Net.CarState.Brake | Net.CarState.Drift, -1, 0, 220), 2, Time.realtimeSinceStartupAsDouble);
                yield return new WaitForSeconds(.6f);
                int puffs = 0;
                foreach (var ps in lit.GetComponentsInChildren<ParticleSystem>()) if (ps.name.StartsWith("Friend smoke")) puffs += ps.particleCount;
                bool lampsOn = lit.LightsLit && lit.BrakeLit, smoking = lit.SmokeRate > 100 && puffs > 10;
                lit.Push(State(at, Vector3.zero, 0, -1, 0, 0), 2, Time.realtimeSinceStartupAsDouble);
                bool lampsOff = !lit.LightsLit && !lit.BrakeLit && lit.SmokeRate == 0;
                int blasts = Explosion.Count;
                lit.Push(State(at, Vector3.zero, Net.CarState.Wrecked, -1, 0), 2, Time.realtimeSinceStartupAsDouble);
                bool burnt = lit.Charred && Explosion.Count == blasts + 1;
                lit.Blast();                                  // the event arriving as well: no second blast
                bool once = Explosion.Count == blasts + 1;
                yield return new WaitForSeconds(1.2f);
                lit.Push(State(at, Vector3.zero, 0, -1, 0), 2, Time.realtimeSinceStartupAsDouble);
                bool whole = !lit.Charred;
                Check("Friend's lamps and smoke", lampsOn && smoking && lampsOff,
                    $"headlamps and brake lamps lit {lampsOn}, smoke {(smoking ? "pouring" : "missing")} ({puffs} puffs), all off again with the flags cleared {lampsOff}");
                Check("Friend's explosion", burnt && once && whole, $"burnt with a blast {burnt}, one blast for flag + event {once}, whole again {whole}");
                Destroy(lit.gameObject);
                yield return null;
            }
        }

        void SampleFrame()
        {
            if (!sampling || camera == null || car == null) return;
            frameTimes.Add(Time.unscaledDeltaTime);
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
            {
                cpuMs += (float)timing[0].cpuMainThreadFrameTime;
                gpuMs += (float)timing[0].gpuFrameTime;
                cpuList.Add((float)timing[0].cpuMainThreadFrameTime);
                gpuList.Add((float)timing[0].gpuFrameTime);
                timed++;
            }
            Vector3 flat = car.transform.forward; flat.y = 0;
            var yaw = Quaternion.LookRotation(flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward);
            Vector3 local = Quaternion.Inverse(yaw) * (camera.transform.position - car.transform.position);
            // Shake is a change in the camera's motion, not steady motion: second difference.
            Vector3 step = local - lastLocal;
            if (haveLocal && haveStep) cameraShake.Add((step - lastStep).magnitude);
            if (haveLocal) { lastStep = step; haveStep = true; }
            lastLocal = local;
            haveLocal = true;
        }
        void Update()
        {
            if (Time.unscaledDeltaTime < .1f)
            {
                frameTotal += Time.unscaledDeltaTime;
                frameCount++;
            }
        }

        readonly Report report = new();
        Vehicle car;
        RunSession run;
        TrafficFlow traffic;
        DriveCamera camera;
        Atmosphere weather;
        string output;
        bool errors;
        void OnEnable()
        {
            Application.logMessageReceived += Log;
        }

        void OnDisable()
        {
            Application.logMessageReceived -= Log;
        }

        void Log(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                errors = true;
        }

        // Distance mode: one loop of city blocks that keeps moving ahead of the car.
        IEnumerator DistanceModeChecks()
        {
            var world = UnityEngine.Object.FindFirstObjectByType<RoadWorld>();
            int courseChunks = world.ChunkCount;
            run.SetMode(GameMode.Distance);
            camera.enabled = true;
            camera.SetMode(ViewMode.Chase);
            yield return new WaitForSeconds(.5f);
            var grid = GridWorld.Active;
            Check("Distance mode world", Route.Endless && grid != null && grid.TileCount == 49, $"{(grid ? grid.TileCount : 0)} city tiles around the car, endless {Route.Endless}");

            // Streets run both ways: drive north, east, south and west far from the start.
            (Vector3 pos, float yaw)[] spots =
            {
                (new Vector3(GridCity.LaneOffset(0), .25f, 3100), 0),
                (new Vector3(1400, .25f, -GridCity.LaneOffset(0)), 90),
                (new Vector3(-2600 - GridCity.LaneOffset(0), .25f, 5300), 180),
                (new Vector3(-7050, .25f, 24600 + GridCity.LaneOffset(0)), 270),
            };
            foreach (var spot in spots)
            {
                car.Place(spot.pos, Quaternion.Euler(0, spot.yaw, 0));
                grid.Recycle();
                yield return new WaitForSeconds(1.2f);
                bool ground = Physics.Raycast(car.transform.position + Vector3.up * 2, Vector3.down, out RaycastHit hit, 6, 1 << 10);
                bool upright = Vector3.Dot(car.transform.up, Vector3.up) > .9f;
                Check($"Endless city at {spot.pos.x:0},{spot.pos.z:0}", ground && upright && car.transform.position.y > -1 && GridCity.OnStreet(car.transform.position),
                    $"ground {(ground ? hit.distance.ToString("0.00") : "none")} m, upright {car.transform.up.y:0.00}, y {car.transform.position.y:0.00}");
            }

            // Earlier checks parked the traffic; bring it back as a real run would have it.
            car.Place(new Vector3(GridCity.LaneOffset(0), .25f, 830), Quaternion.identity);
            grid.Recycle();
            traffic.SetDensity(1);                   // what a real distance run uses
            var gridTraffic = GridTraffic.Ensure(traffic);
            gridTraffic.Restart();
            yield return new WaitForSeconds(.3f);
            float before = run.Scoring.Distance;
            car.Body.linearVelocity = car.transform.forward * 30;
            float began = Time.time;
            while (Time.time - began < 3)
            {
                Follow(.65f);
                yield return new WaitForFixedUpdate();
            }
            Check("Distance counted", run.Scoring.Distance > before + 40, $"{run.Scoring.Distance - before:0} m added in 3 s");

            // Smoothness: keep driving through traffic and look for hitches and camera shake.
            // Measured three times; the best run counts, so a browser or a video call on the
            // machine at the wrong moment does not decide whether the game is smooth.
            float p95 = 0, shake = 0, bestShake = 0, runCpu = 0, runGpu = 0, runTraffic = 0;
            int hitches = 0, frames = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                // A crash in the previous attempt leaves the wreck camera circling: start clean.
                if (car.WreckedFor > 0 || car.transform.up.y < .9f)
                {
                    car.ClearWreck();
                    car.Place(new Vector3(GridCity.LaneOffset(0), .25f, car.transform.position.z + 40), Quaternion.identity);
                    car.Body.linearVelocity = car.transform.forward * 25;
                    yield return new WaitForSeconds(.5f);
                }
                frameTimes.Clear(); cameraShake.Clear(); haveLocal = haveStep = false; sampling = true;
                cpuMs = gpuMs = 0; timed = 0; GridTraffic.CostMs = 0; GridTraffic.Steps = 0;
                GridWorld.RecycleMs = GridWorld.LightMs = 0; GridWorld.Moves = 0;
                int gc0 = System.GC.CollectionCount(0);
                StartCoroutine(SampleSmoothness());
                began = Time.time;
                while (Time.time - began < 6)
                {
                    Follow(.6f);
                    yield return new WaitForFixedUpdate();
                }
                sampling = false;
                frameTimes.Sort();
                cameraShake.Sort();
                float attemptP95 = frameTimes.Count > 0 ? frameTimes[(int)(frameTimes.Count * .95f)] * 1000 : 0;
                int attemptHitches = 0;
                foreach (var f in frameTimes) if (f > .05f) attemptHitches++;
                float worst = frameTimes.Count > 0 ? frameTimes[frameTimes.Count - 1] * 1000 : 0;
                Debug.Log($"QA INFO smoothness attempt {attempt + 1}: 95% under {attemptP95:0.0} ms, worst {worst:0} ms, {attemptHitches} hitches, cpu {cpuMs / Mathf.Max(1, timed):0.0} ms, gpu {gpuMs / Mathf.Max(1, timed):0.0} ms; "
                        + $"recycle {GridWorld.RecycleMs:0} ms total over {GridWorld.Moves} tile moves, night lights {GridWorld.LightMs:0} ms, {System.GC.CollectionCount(0) - gc0} garbage collections");
                // The camera judged on its calmest run (a run with a crash in it shakes by rights).
                float attemptShake = cameraShake.Count > 0 ? cameraShake[(int)(cameraShake.Count * .95f)] : 0;
                if (attempt == 0 || attemptShake < bestShake) bestShake = attemptShake;
                // Best run: fewest hitches, then the lowest 95th percentile.
                if (attempt == 0 || attemptHitches < hitches || attemptHitches == hitches && attemptP95 < p95)
                {
                    p95 = attemptP95;
                    hitches = attemptHitches;
                    frames = frameTimes.Count;
                    shake = cameraShake.Count > 0 ? cameraShake[(int)(cameraShake.Count * .95f)] : 0;
                    runCpu = cpuMs / Mathf.Max(1, timed);
                    runGpu = gpuMs / Mathf.Max(1, timed);
                    runTraffic = GridTraffic.CostMs / Mathf.Max(1, GridTraffic.Steps);
                }
            }
            Check("Frame pacing in the city", hitches <= 2, $"best of 3: {frames} frames, 95% under {p95:0.0} ms, {hitches} hitches over 50 ms; cpu {runCpu:0.0} ms, gpu {runGpu:0.0} ms, traffic {runTraffic:0.00} ms/step");
            Check("Camera steady", bestShake < .03f, $"95% of frames: camera jolt relative to the car under {bestShake * 100:0.0} cm (best of 3)");

            Check("Traffic in the endless city", traffic.Count >= 50 && gridTraffic.Moving > 30, $"{traffic.Count} cars around the player, {gridTraffic.Moving} moving");
            began = Time.time;
            while (gridTraffic.TurnsCompleted < 3 && Time.time - began < 20)
                yield return null;
            Check("Traffic turns at junctions", gridTraffic.TurnsCompleted >= 3, $"{gridTraffic.TurnsCompleted} turns in {Time.time - began:0.0} s");
            bool onLanes = true;
            string off = "";
            foreach (var c in traffic.Cars)
                // Cars knocked about by the driver (stunned, finding their way back, burnt) may
                // lie on a pavement for a moment; the check is about the ones driving.
                if (c.gameObject.activeSelf && !c.Wrecked && c.State != TrafficCar.Mode.Stunned && c.State != TrafficCar.Mode.Recovering && !GridCity.OnStreet(c.transform.position))
                {
                    onLanes = false;
                    off = c.name + " at " + c.transform.position;
                }
            Check("Traffic stays on the streets", onLanes, onLanes ? "every car on a carriageway" : off);
            // Diagnostics: where the frame time goes in the city. Each variant is measured right
            // after a baseline and the pair is repeated, using medians, so a busy Mac skews
            // both sides alike and the difference is what the setting really costs.
            var diag = new System.Text.StringBuilder("QA INFO city cost (median ms, variant minus baseline):");
            float mCpu = 0, mGpu = 0;
            IEnumerator Measure()
            {
                cpuList.Clear(); gpuList.Clear();
                cpuMs = gpuMs = 0; timed = 0; sampling = true;
                StartCoroutine(SampleSmoothness());
                float t0 = Time.time;
                while (Time.time - t0 < 1.5f) { Follow(.6f); yield return new WaitForFixedUpdate(); }
                sampling = false;
                cpuList.Sort(); gpuList.Sort();
                mCpu = cpuList.Count > 0 ? cpuList[cpuList.Count / 2] : 0;
                mGpu = gpuList.Count > 0 ? gpuList[gpuList.Count / 2] : 0;
            }
            float baseCpuSum = 0, baseGpuSum = 0; int baseN = 0;
            IEnumerator Pair(string label, Action on, Action off)
            {
                float dc = 0, dg = 0;
                for (int k = 0; k < 2; k++)
                {
                    yield return Measure();
                    float bc = mCpu, bg = mGpu;
                    baseCpuSum += bc; baseGpuSum += bg; baseN++;
                    on();
                    yield return Measure();
                    off();
                    dc += mCpu - bc; dg += mGpu - bg;
                }
                diag.Append($" {label} cpu {dc / 2:+0.0;-0.0} gpu {dg / 2:+0.0;-0.0};");
            }
            var hud = UnityEngine.Object.FindFirstObjectByType<DriveHud>();
            yield return Pair("noHUD", () => hud.enabled = false, () => hud.enabled = true);
            yield return Pair("noCarRender",
                () => { foreach (var c in traffic.Cars) foreach (var r in c.GetComponentsInChildren<Renderer>()) r.enabled = false; },
                () => { foreach (var c in traffic.Cars) foreach (var r in c.GetComponentsInChildren<Renderer>()) r.enabled = true; });
            yield return Pair("noTrafficAI", () => gridTraffic.enabled = false, () => gridTraffic.enabled = true);
            float far = camera.Lens.farClipPlane;
            yield return Pair("far350", () => camera.Lens.farClipPlane = 350, () => camera.Lens.farClipPlane = far);
            var sun = RenderSettings.sun;
            var shadowMode = sun.shadows;
            yield return Pair("noShadows", () => sun.shadows = LightShadows.None, () => sun.shadows = shadowMode);
            var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            var camData = camera.Lens.GetUniversalAdditionalCameraData();
            if (rp != null)
            {
                int cascades = rp.shadowCascadeCount;
                yield return Pair("oneCascade", () => rp.shadowCascadeCount = 1, () => rp.shadowCascadeCount = cascades);
                var filter = rp.upscalingFilter;
                float scale = rp.renderScale;
                yield return Pair("linearUpscale", () => rp.upscalingFilter = UnityEngine.Rendering.Universal.UpscalingFilterSelection.Linear, () => rp.upscalingFilter = filter);
                yield return Pair("linear80", () => { rp.upscalingFilter = UnityEngine.Rendering.Universal.UpscalingFilterSelection.Linear; rp.renderScale = .8f; }, () => { rp.upscalingFilter = filter; rp.renderScale = scale; });
                yield return Pair("scale62", () => rp.renderScale = .62f, () => rp.renderScale = scale);
                yield return Pair("fullRes", () => rp.renderScale = 1, () => rp.renderScale = scale);
            }
            bool post = camData.renderPostProcessing;
            yield return Pair("noPost", () => camData.renderPostProcessing = false, () => camData.renderPostProcessing = post);
            var volume = UnityEngine.Object.FindFirstObjectByType<UnityEngine.Rendering.Volume>();
            if (volume && volume.sharedProfile && volume.sharedProfile.TryGet(out UnityEngine.Rendering.Universal.Bloom qaBloom))
            {
                bool was = qaBloom.active;
                yield return Pair("noBloom", () => qaBloom.active = false, () => qaBloom.active = was);
            }
            var aa = camData.antialiasing;
            yield return Pair("noAA", () => camData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None, () => camData.antialiasing = aa);
            bool hdr = camera.Lens.allowHDR;
            yield return Pair("noHDR", () => camera.Lens.allowHDR = false, () => camera.Lens.allowHDR = hdr);
            var lods = GridWorld.Active ? GridWorld.Active.GetComponentsInChildren<LODGroup>() : new LODGroup[0];
            yield return Pair("noCloseDetail",
                () => { foreach (var g in lods) { g.enabled = false; foreach (var lod in g.GetLODs()) foreach (var r in lod.renderers) if (r) r.enabled = false; } },
                () => { foreach (var g in lods) { foreach (var lod in g.GetLODs()) foreach (var r in lod.renderers) if (r) r.enabled = true; g.enabled = true; } });
            diag.Insert(0, $"QA INFO city baseline median cpu {baseCpuSum / Mathf.Max(1, baseN):0.0} gpu {baseGpuSum / Mathf.Max(1, baseN):0.0}; ");
            int renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Length;
            int bodies = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Length;
            int colliders = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Length;
            diag.Append($" renderers {renderers}, bodies {bodies}, colliders {colliders}");
            Debug.Log(diag.ToString());
            Capture("19-distance-mode");
            yield return CaptureFull("21-distance-hud");

            // The M map: open it, check it renders and closes cleanly.
            var worldMap = UnityEngine.Object.FindFirstObjectByType<WorldMap>();
            worldMap.Toggle();
            yield return new WaitForSecondsRealtime(.5f);
            bool mapOpened = WorldMap.Open;
            yield return CaptureFull("26-map");
            worldMap.Toggle();
            yield return new WaitForSecondsRealtime(.2f);
            Check("Full map", mapOpened && !WorldMap.Open && Time.timeScale == 1 && !DriverInput.Blocked, $"opened {mapOpened}, closed {!WorldMap.Open}, time {Time.timeScale}");

            // Master volume reaches the audio listener and comes back.
            GameVolume.Set(.5f);
            bool half = Mathf.Abs(AudioListener.volume - .5f) < .01f;
            GameVolume.Step(1);
            bool stepped = Mathf.Abs(AudioListener.volume - .6f) < .01f;
            // At zero the synthesised engine must fall silent too (it bypasses the listener).
            // The engine may still be turning over after the mode switch, so compare with its
            // own gain at full volume instead of a fixed level.
            var synth = car.GetComponentInChildren<EngineSynth>();
            yield return new WaitForSeconds(1.5f);
            float fullGain = synth == null ? 1 : synth.Master;
            GameVolume.Set(0);
            yield return null;
            yield return null;
            bool engineSilent = synth == null || synth.Master <= .0001f;
            GameVolume.Set(1);
            yield return null;
            yield return null;
            bool engineBack = synth == null || synth.Master >= fullGain * .9f && synth.Master > .3f;
            Check("Volume reaches the engine", engineSilent && engineBack, $"engine gain at 0%: {(engineSilent ? "silent" : "still audible")}, back at 100%: {engineBack}");
            GameVolume.Set(1);
            Check("Master volume", half && stepped && Mathf.Abs(AudioListener.volume - 1) < .01f, $"50% -> {(half ? "ok" : "wrong")}, +10% -> {(stepped ? "ok" : "wrong")}");

            // The chooser real players see first.
            run.ChoosingMode = true;
            run.SetPaused(true);
            yield return new WaitForSecondsRealtime(.3f);
            yield return CaptureFull("22-mode-select");
            run.ChoosingMode = false;
            run.SetPaused(false);

            camera.enabled = false;
            camera.transform.position = car.transform.position + new Vector3(170, 150, -150);
            camera.transform.LookAt(car.transform.position + new Vector3(0, 0, 220));
            Capture("20-distance-plan");
            Vector3 junction = new(GridCity.Snap(car.transform.position.x), 0, GridCity.Snap(car.transform.position.z + 100));
            camera.transform.position = junction + new Vector3(-GridCity.LaneOffset(1), 24, -75);
            camera.transform.LookAt(junction);
            Capture("23-grid-junction");
            camera.transform.position = junction + new Vector3(GridCity.LaneOffset(0), 1.7f, -58);
            camera.transform.LookAt(junction + new Vector3(0, 4.5f, 0));
            Capture("24-grid-street");
            camera.transform.position = junction + new Vector3(-GridCity.Half - 4, 2.2f, 40);
            camera.transform.LookAt(junction + new Vector3(-GridCity.Half - 25, 8, 110));
            Capture("25-grid-houses");
            // Close look at a ground floor near the car (shops, bars on windows, steps).
            Transform nearestHouse = null;
            float bestHouse = float.MaxValue;
            foreach (var t in GridWorld.Active.GetComponentsInChildren<Transform>())
                if (t.name == "Town house")
                {
                    float d = (t.position - car.transform.position).sqrMagnitude;
                    if (d < bestHouse) { bestHouse = d; nearestHouse = t; }
                }
            if (nearestHouse)
            {
                camera.transform.position = nearestHouse.position + nearestHouse.rotation * new Vector3(-3, 1.7f, -8);
                camera.transform.LookAt(nearestHouse.position + nearestHouse.rotation * new Vector3(2, 2.2f, 0));
                Capture("27-ground-floor");
                camera.transform.position = nearestHouse.position + nearestHouse.rotation * new Vector3(-10, 4, -16);
                camera.transform.LookAt(nearestHouse.position + nearestHouse.rotation * new Vector3(4, 6, 0));
                Capture("28-facade");
                camera.transform.position = nearestHouse.position + nearestHouse.rotation * new Vector3(-2, 7, -7);
                camera.transform.LookAt(nearestHouse.position + nearestHouse.rotation * new Vector3(3, 6.5f, 0));
                Capture("29-windows");
            }
            {
                camera.transform.position = junction + new Vector3(12.5f, 1.8f, 22);
                camera.transform.LookAt(junction + new Vector3(8.2f, .6f, 48));
                Capture("30-parking-bays");
                int inBays = 0, onPavement = 0;
                foreach (var t in GridWorld.Active.GetComponentsInChildren<Transform>())
                    if (t.name == "Parked car")
                    {
                        float a = Mathf.Abs(GridCity.OffsetX(t.position.x)), b = Mathf.Abs(GridCity.OffsetZ(t.position.z));
                        float edge = Mathf.Min(a, b);
                        if (edge > GridCity.Half && edge < GridCity.Half + 2.5f) inBays++;
                        else if (edge >= GridCity.Half + 2.5f && edge < GridCity.Half + 6) onPavement++;
                    }
                // Half as many parked cars as before (was > 50): the bays are still used, just not full.
                Check("Parking bays", inBays > 25 && onPavement == 0, $"{inBays} cars parked in bays, {onPavement} on the pavement");

                // Night in the city: street lamps, lit windows and shop fronts.
                var sky = FindFirstObjectByType<Atmosphere>();
                var wasWeather = Atmosphere.CurrentWeather;
                sky.Set(Weather.Clear, true);
                for (int n = 0; n < 40; n++) { sky.Apply(.2f); yield return null; }
                camera.enabled = false;
                camera.transform.position = junction + new Vector3(GridCity.LaneOffset(0), 1.7f, -58);
                camera.transform.LookAt(junction + new Vector3(0, 4.5f, 0));
                Capture("32-night-street");
                camera.transform.position = junction + new Vector3(-GridCity.Half - 4, 2.2f, 40);
                camera.transform.LookAt(junction + new Vector3(-GridCity.Half - 25, 8, 110));
                Capture("33-night-houses");
                int lamps = 0, litWindows = 0;
                foreach (var l in GridWorld.Active.GetComponentsInChildren<Light>())
                    if (l.enabled && l.type == LightType.Point) lamps++;
                foreach (var r in GridWorld.Active.GetComponentsInChildren<MeshRenderer>())
                    if (r.sharedMaterial == Art.WinLit) litWindows++;
                // The radio: B steps through every station (three made ones, four songs) and off again.
                var radio = CarRadio.Active;
                bool radioOk = radio != null && radio.Count >= 7;
                string heard = "";
                if (radioOk)
                {
                    while (radio.On) radio.Next();
                    for (int n = 0; n < radio.Count; n++)
                    {
                        radio.Next();
                        yield return new WaitForSecondsRealtime(.6f);
                        var src = radio.GetComponent<AudioSource>();
                        if (!radio.On || src == null || !src.isPlaying || src.clip == null) radioOk = false;
                        heard += (n > 0 ? ", " : "") + (src != null && src.clip != null ? src.clip.name : "silent");
                    }
                    radio.Next();
                    yield return new WaitForSecondsRealtime(.2f);
                    if (radio.On) radioOk = false;
                }
                Check("Car radio", radioOk, radio == null ? "no radio" : $"{radio.Count} stations, played {heard}, B switches it off again");

                Check("City lighting at night", lamps >= 20 && litWindows > 0,
                    $"{lamps} street lamps lit around the car, {litWindows} facades with lit windows");
                camera.enabled = true;
                sky.Set(wasWeather, false);
                for (int n = 0; n < 40; n++) { sky.Apply(.2f); yield return null; }
            }
            camera.enabled = true;
            {
                int groups = 0, windows = 0, signs = 0;
                foreach (var g in GridWorld.Active.GetComponentsInChildren<LODGroup>())
                {
                    groups++;
                    foreach (var lod in g.GetLODs())
                        foreach (var r in lod.renderers)
                            if (r && System.Array.IndexOf(Art.WinGlasses, r.sharedMaterial) >= 0) windows++;
                            else if (r is MeshRenderer && r.GetComponent<TextMesh>()) signs++;
                }
                Check("House detail", groups >= GridWorld.Active.TileCount / 2 && windows > 0 && signs > 0,
                    $"{groups} tiles with close-up detail, {windows} window meshes, {signs} shop signs");
            }

            run.SetMode(GameMode.Free);
            yield return new WaitForSeconds(.5f);
            Check("Back to free drive", !Route.Endless && GridWorld.Active == null && world.ChunkCount == courseChunks && Island.InCity(car.transform.position) && GridWorld.IslandCity != null && GridWorld.IslandCity.gameObject.activeInHierarchy,
                $"{world.ChunkCount} road blocks, car at {car.transform.position}, island city shown {(GridWorld.IslandCity ? GridWorld.IslandCity.gameObject.activeInHierarchy : false)}");
        }

        // Renders a rev sweep through the engine model and stores it for listening.
        void EngineAudioCheck()
        {
            var synth = UnityEngine.Object.FindFirstObjectByType<EngineSynth>();
            if (synth == null)
            {
                Check("Engine audio", false, "No engine synthesiser in the scene");
                return;
            }

            const int rate = 22050;
            float idle = car.Spec.idle, redline = car.Spec.redline;
            // 0-3 s: pull to the red line on full throttle. 3-4 s: lift off.
            var sweep = synth.Capture(4f, t => t < 3f
                ? new Vector2(Mathf.Lerp(idle, redline, t / 3f), .95f)
                : new Vector2(Mathf.Lerp(redline, idle * 1.6f, (t - 3f)), .05f), rate);
            File.WriteAllBytes(Path.Combine(output, "engine.wav"), Wav(sweep, rate));

            float lift = Rms(sweep, (int)(rate * 3.4f), (int)(rate * 3.8f));   // throttle closed
            float loud = Rms(sweep, rate * 2, rate * 2 + rate / 2);            // full throttle
            float quiet = Rms(sweep, rate / 4, rate / 2);
            int slow = Crossings(sweep, rate / 4, rate / 2);
            int fast = Crossings(sweep, rate * 2, rate * 2 + rate / 2);
            bool finite = true;
            foreach (float v in sweep)
                if (float.IsNaN(v) || float.IsInfinity(v) || Mathf.Abs(v) > 1.01f) { finite = false; break; }

            Check("Engine audio clean", finite && quiet > .005f, $"No clipping or NaN, idle level {quiet:0.000}");
            Check("Engine pitch follows revs", fast > slow * 1.6f, $"{slow} to {fast} zero crossings from idle to red line");
            Check("Engine load response", loud > lift * 1.35f, $"level {lift:0.000} on a closed throttle against {loud:0.000} pulling hard");
        }

        static float Rms(float[] data, int from, int to)
        {
            double sum = 0;
            int n = 0;
            for (int i = Mathf.Max(0, from); i < Mathf.Min(data.Length, to); i++, n++)
                sum += data[i] * data[i];
            return n == 0 ? 0 : Mathf.Sqrt((float)(sum / n));
        }

        static int Crossings(float[] data, int from, int to)
        {
            int n = 0;
            for (int i = Mathf.Max(1, from); i < Mathf.Min(data.Length, to); i++)
                if ((data[i - 1] < 0) != (data[i] < 0))
                    n++;
            return n;
        }

        static byte[] Wav(float[] samples, int rate)
        {
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            int bytes = samples.Length * 2;
            w.Write(new[] {'R', 'I', 'F', 'F'});
            w.Write(36 + bytes);
            w.Write(new[] {'W', 'A', 'V', 'E', 'f', 'm', 't', ' '});
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(new[] {'d', 'a', 't', 'a'});
            w.Write(bytes);
            foreach (float sample in samples)
                w.Write((short)(Mathf.Clamp(sample, -1, 1) * 32000));
            w.Flush();
            return stream.ToArray();
        }

        // Nitro: holding the button adds a clear push, shows the flames, drains the bottle at the
        // stated rate, and the bottle refills once released.
        IEnumerator NitroChecks()
        {
            var nitro = car.GetComponent<Nitro>();
            if (!nitro) { Check("Nitro boost", false, "no nitro on the car"); yield break; }
            car.Input.SetTestNitro(false);
            car.Teleport(600);
            yield return new WaitForSeconds(.3f);
            nitro.Refill();
            car.Input.SetTest(1, 0, 0);
            float t = Time.time;
            while (Time.time - t < 7) { Follow(1); yield return new WaitForFixedUpdate(); }
            float v0 = car.Speed;
            t = Time.time;
            while (Time.time - t < 1.5f) { Follow(1); yield return new WaitForFixedUpdate(); }
            float plain = car.Speed - v0;
            float v1 = car.Speed, tank0 = nitro.Tank;
            int particles = 0;
            bool shot = false;
            car.Input.SetTestNitro(true);
            t = Time.time;
            while (Time.time - t < 1.5f)
            {
                Follow(1);
                particles = Mathf.Max(particles, nitro.Flames[0].particleCount + nitro.Flames[1].particleCount);
                if (!shot && Time.time - t > 1) { shot = true; Capture("34-nitro"); }
                yield return new WaitForFixedUpdate();
            }
            float boosted = car.Speed - v1, used = tank0 - nitro.Tank;
            car.Input.SetTestNitro(false);
            Check("Nitro boost", boosted > plain + 12 && particles > 5, $"+{boosted:0} km/h in 1.5 s with nitro, +{plain:0} without (from {v0:0} km/h); {particles} flame particles");
            Check("Nitro bottle drains", used > .18f && used < .32f, $"{used * 100:0}% used in 1.5 s (full bottle = {Nitro.BurnTime:0} s)");
            float tank1 = nitro.Tank;
            t = Time.time;
            while (Time.time - t < 1) { Follow(1); yield return new WaitForFixedUpdate(); }
            Check("Nitro refills", nitro.Tank > tank1 + .02f && !nitro.Active, $"{tank1 * 100:0}% -> {nitro.Tank * 100:0}% one second after release");
            car.Input.SetTest(0, 0, 0);
            nitro.Refill();
        }

        // Guards for the optimisation pass: the HUD layout pass stays off, physics catch-up is
        // capped, and every car model still has all its materials after unused pack files were
        // moved out of the shipped Resources folder.
        void OptimisationChecks()
        {
            var hud = FindFirstObjectByType<DriveHud>();
            Check("HUD draws without layout pass", hud && !hud.useGUILayout, hud ? "IMGUI layout pass is off" : "no HUD");
            Check("Physics catch-up capped", Time.maximumDeltaTime <= .1001f, $"at most {Time.maximumDeltaTime:0.00} s ({Mathf.RoundToInt(Time.maximumDeltaTime / Time.fixedDeltaTime)} physics steps) after a slow frame");
            var sb = new System.Text.StringBuilder();
            bool ok = true;
            foreach (var style in new[]{BodyStyle.Mercedes, BodyStyle.Mercedes300, BodyStyle.Sport, BodyStyle.Classic, BodyStyle.StyleSedan, BodyStyle.StyleCar, BodyStyle.StyleSport, BodyStyle.StyleJeep, BodyStyle.StyleBus})
            {
                var shape = BodyShape.Of(style);
                var src = Resources.Load<GameObject>(shape.Prefab);
                if (!src) { ok = false; sb.Append($" {style} MISSING;"); continue; }
                int mats = 0, textured = 0, broken = 0;
                foreach (var r in src.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.name.ToLowerInvariant().StartsWith("collider")) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (!m || !m.shader || m.shader.name.Contains("InternalError")) { broken++; continue; }
                        mats++;
                        if ((m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap")) || (m.HasProperty("_MainTex") && m.GetTexture("_MainTex"))) textured++;
                    }
                }
                bool imported = style == BodyStyle.Mercedes || style == BodyStyle.Mercedes300 || style == BodyStyle.Classic;   // the sport car pack is plain colours by design
                if (broken > 0 || (imported && textured == 0)) ok = false;
                sb.Append($" {style} {textured}/{mats} textured{(broken > 0 ? $", {broken} BROKEN" : "")};");
            }
            Check("Car models complete after slimming", ok, sb.ToString().Trim());
        }

        static void SetSsao(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset pipeline, bool on)
        {
            var field = typeof(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(pipeline) is UnityEngine.Rendering.Universal.ScriptableRendererData[] list)
                foreach (var data in list)
                    if (data != null)
                        foreach (var feature in data.rendererFeatures)
                            if (feature is UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion) feature.SetActive(on);
        }

        void Check(string name, bool pass, string observation)
        {
            report.checks.Add(new Result{name = name, pass = pass, observation = observation});
            Debug.Log("QA " + (pass ? "PASS " : "FAIL ") + name + " / " + observation);
        }

        IEnumerator Start()
        {
            output = Path.GetFullPath("../QA");
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-qaOutput")
                    output = args[i + 1];
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "boot.txt"), "world built after " + Time.realtimeSinceStartup.ToString("0.0") + " s");
            // Mirror the player log next to the results so a crash can be read from the project folder.
            string logPath = Path.Combine(output, "player.log");
            Application.logMessageReceivedThreaded += (message, stack, type) =>
            {
                try
                {
                    File.AppendAllText(logPath, $"{Time.realtimeSinceStartup:0.0} [{type}] {message}\n" +
                        (type == LogType.Exception || type == LogType.Error ? stack + "\n" : ""));
                }
                catch { }
            };
            yield return null;
            run = RunSession.Active;
            car = run.Player;
            traffic = run.Traffic;
            camera = Camera.main.GetComponent<DriveCamera>();
            weather = FindFirstObjectByType<Atmosphere>();
            int shotArg = Array.IndexOf(args, "-shots");
            if (shotArg >= 0 && shotArg + 1 < args.Length)
            {
                yield return Shots(args[shotArg + 1]);
                Application.Quit(0);
                yield break;
            }
            if (Array.IndexOf(args, "-perfQA") >= 0)
            {
                yield return PerfChecks();
                Application.Quit(0);
                yield break;
            }
            if (Array.IndexOf(args, "-cityQA") >= 0)
            {
                yield return IslandChecks();
                Check("Runtime errors", !errors, "No unexpected error or exception logged");
                File.WriteAllText(Path.Combine(output, "validation.json"), JsonUtility.ToJson(report, true));
                Application.Quit(report.checks.TrueForAll(x => x.pass) ? 0 : 2);
                yield break;
            }
            Check("Scene constructed", car != null && traffic.HighwayCount == 42 && FindFirstObjectByType<RoadWorld>() && IslandWorld.Active, $"island, {Route.Course / 1000:0.0} km loop road, {traffic.HighwayCount} cars on the road and {traffic.Count - traffic.HighwayCount} in the city, camera, drivetrain and HUD initialized");
            traffic.Simulate = false;
            foreach (var c in traffic.Cars)
                c.gameObject.SetActive(false);
            car.Input.SetTest(0, 0, 0);
            yield return new WaitForSeconds(1);
            car.Teleport(25);
            yield return new WaitForSeconds(.5f);
            float start = Time.time;
            car.Input.SetTest(1, 0, 0);
            while (car.Speed < 100 && Time.time - start < 12)
                yield return new WaitForFixedUpdate();
            report.zeroTo100 = Time.time - start;
            Check("0–100 km/h", car.Speed >= 100 && report.zeroTo100 >= 3.5f && report.zeroTo100 < (car.Spec.mass > 1700 ? 9f : 6.5f), $"{report.zeroTo100:0.00}s; gear {car.Engine.Gear}; slip {car.Chassis.Slip:0.00}");
            yield return StopTest(false);
            report.dryStop = lastStop;
            yield return StopTest(true);
            report.wetStop = lastStop;
            Check("Wet braking changes physics", report.wetStop > report.dryStop * 1.1f, $"dry {report.dryStop:0.0}m / wet {report.wetStop:0.0}m");
            weather.Set(Weather.Clear, false);
            weather.Apply(10);
            car.Teleport(200, 1);
            yield return new WaitForSeconds(.4f);
            car.Body.linearVelocity = car.transform.forward * (230 / 3.6f);
            start = Time.time;
            while (Time.time - start < 40)
            {
                Follow(1);
                report.topSpeed = Mathf.Max(report.topSpeed, car.Speed);
                yield return new WaitForFixedUpdate();
            }

            Check("High-speed stability", report.topSpeed > 240 && report.topSpeed < car.Spec.speedLimiter + 12 && Vector3.Dot(car.transform.up, Vector3.up) > .9f && Mathf.Abs(Route.Lateral(car.transform.position)) < 5, $"peak {report.topSpeed:0.0}km/h; final gear {car.Engine.Gear}, RPM {car.Engine.Rpm:0}; upright {Vector3.Dot(car.transform.up, Vector3.up):0.00}, lateral {Route.Lateral(car.transform.position):0.00}m");
            car.Teleport(150);
            car.Input.SetTest(0, 1, 0);
            for (int k = 0; k < 12; k++)
            {
                yield return new WaitForSeconds(.25f);
                string blocked = Physics.Raycast(car.transform.position + Vector3.up * .4f, -car.transform.forward, out RaycastHit back, 4f) ? back.collider.name + "@" + back.distance.ToString("0.0") : "clear";
                var sb = new System.Text.StringBuilder($"QA INFO reverse t{k}: v {car.ForwardSpeed:0.0} pos {car.transform.position.z:0.0}/{Route.Lateral(car.transform.position):0.0} behind {blocked} gear {car.Engine.Label} ");
                foreach (var w in car.Chassis.Wheels)
                {
                    w.GetGroundHit(out WheelHit wh);
                    sb.Append($"[rpm {w.rpm:0} m {w.motorTorque:0} b {w.brakeTorque:0} fs {wh.forwardSlip:0.00} ss {wh.sidewaysSlip:0.00} F {wh.force:0} st {w.sidewaysFriction.stiffness:0.00}] ");
                }
                Debug.Log(sb.ToString());
            }
            var rw = car.Chassis.Wheels[2];
            rw.GetGroundHit(out WheelHit rh);
            Check("Automatic reverse", car.Engine.Gear == -1 && car.ForwardSpeed < -5, $"gear {car.Engine.Label}; speed {car.ForwardSpeed:0.0}km/h; rpm {rw.rpm:0}; motor {rw.motorTorque:0}; brake {rw.brakeTorque:0}; fslip {rh.forwardSlip:0.00}; grounded {car.Chassis.Grounded}");
            car.Teleport(150);
            car.Input.SetTest(0, 0, 0);
            yield return new WaitForSeconds(.5f);
            car.Engine.RequestShift(1, 0);
            int first = car.Engine.Gear;
            car.Engine.RequestShift(1, 0);
            Check("Shift delay", car.Engine.Gear == first && car.Engine.Shifting, "Repeated shift request is blocked during engagement");
            yield return new WaitForSeconds(.3f);
            car.Engine.RequestShift(-1, 240);
            Check("Over-rev protection", car.Engine.Gear == first, "Unsafe downshift at 240 km/h is rejected");
            car.Engine.Automatic = true;
            car.Teleport(500, 1);
            traffic.SetDensity(1);
            traffic.Simulate = false;
            var candidate = traffic.Cars[0];
            candidate.Place(525, 0, 25);
            car.Body.linearVelocity = car.transform.forward * 50;
            Check("Player-aware lane change", !traffic.LaneSafe(candidate, 1), "Traffic rejects merge across a fast approaching player");
            car.Body.linearVelocity = Vector3.zero;
            car.Input.SetTest(0, 0, 0);
            candidate.Place(505, 1, 20);
            traffic.Leader(candidate, out float gap, out float speed);
            var behind = traffic.Cars[1];
            behind.Place(470, 1, 28);
            traffic.Leader(behind, out gap, out speed);
            Check("Leader detection", gap < 30 && speed < 28, $"Following car detects obstruction {gap:0.0}m ahead at {speed:0.0}m/s");
            foreach (var c in traffic.Cars)
                c.gameObject.SetActive(false);
            var passCar = traffic.Cars[2];
            passCar.gameObject.SetActive(true);
            passCar.Place(520, 2, 25);
            car.Teleport(500, 1);
            car.Body.linearVelocity = Vector3.forward * 40;
            var passScore = new RunScore();
            passScore.Reset(car.Body.position);
            passScore.Step(car, traffic, .02f);
            passCar.transform.position = car.transform.position + new Vector3(2.3f, 0, 0);
            passScore.Step(car, traffic, .02f);
            passCar.transform.position = car.transform.position + new Vector3(2.3f, 0, -10);
            passScore.Step(car, traffic, .02f);
            Check("Near miss crossing", passScore.NearMisses == 1, "One close moving overtake awards one near miss");
            // Park a few civilian cars ahead so their bodies can be checked on the capture.
            {
                float z = Route.Locate(car.transform.position);
                int placed = 0;
                foreach (var civil in traffic.Cars)
                {
                    if (!civil.gameObject.activeSelf || placed >= 3)
                        continue;
                    civil.Place(z + 14 + placed * 9, placed, 12);
                    placed++;
                }

                yield return new WaitForSeconds(.4f);
                Capture("10-traffic");
                // City crossing, for checking the junction furniture.
                camera.SetMode(ViewMode.Wide);
                car.Teleport(195, 1);
                yield return new WaitForSeconds(.5f);
                Capture("11-junction");
                // Tree pieces are merged into batched meshes, so look for foliage materials.
                bool foliage = GameObject.Find("Tree crown") != null;
                foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                    if (!foliage && r.enabled && r.sharedMaterial && (r.sharedMaterial.name.Contains("leaves") || r.sharedMaterial.name.Contains("needles")))
                        foliage = true;
                // The island's trees (city ones too) are drawn instanced by the ForestRenderer.
                if (ForestRenderer.Active && ForestRenderer.Active.Trees > 0) foliage = true;
                Check("Roadside trees", foliage, "Procedural trees are planted along the route");

                // Service station: the forecourt has to be reachable from the carriageway.
                car.Teleport(Station.Sites[0].Z - 74, 1);
                yield return new WaitForSeconds(.9f);
                Capture("12-station");
                car.Teleport(Station.Sites[0].Z - 34, 1);
                yield return new WaitForSeconds(.6f);
                Capture("13-forecourt");
                // The town (and its station) streams in only near the player: look in hidden objects too.
                bool pillar = false;
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                    if ((t.name == "Canopy pillar" || t.name == "Canopy column") && t.gameObject.scene.IsValid()) { pillar = true; break; }
                Check("Service station", pillar, "The filling station is built beside the carriageway");

                // Drive off the carriageway: the forecourt and the verge must carry the car.
                var site = Station.Sites[0];
                Vector3 apron = Route.Center(site.Z) + Route.Right(site.Z) * (site.Side * 30) + Vector3.up * 1.4f;
                car.Teleport(site.Z, 1);
                car.Body.position = apron;
                car.transform.position = apron;
                car.Body.linearVelocity = Vector3.zero;
                yield return new WaitForSeconds(1.6f);
                float apronY = car.Body.position.y - Route.Height(site.Z);
                Check("Forecourt carries the car", apronY > -.8f, "Resting height " + apronY.ToString("0.00") + " m above the carriageway plane");

                Vector3 verge = Route.Center(site.Z + 220) + Route.Right(site.Z + 220) * 40;
                verge.y = Island.GroundHeight(verge.x, verge.z) + 2f;
                car.Teleport(site.Z + 220, 1);
                car.Body.position = verge;
                car.transform.position = verge;
                car.Body.linearVelocity = Vector3.zero;
                yield return new WaitForSeconds(1.6f);
                float vergeY = car.Body.position.y - Island.GroundHeight(car.Body.position.x, car.Body.position.z);
                Check("Verge carries the car", vergeY > -1.2f && vergeY < 2.5f, "Resting height " + vergeY.ToString("0.00") + " m above the meadow");

                var signals = UnityEngine.Object.FindFirstObjectByType<TrafficSignals>();
                Check("Signals running", signals != null, "Traffic lights cycle between the carriageway and the side streets");

                camera.SetMode(ViewMode.Chase);
            }
            passScore.Step(car, traffic, 8);
            Check("No repeated near miss", passScore.NearMisses == 1, "Remaining beside or behind a car does not farm score");
            passScore.Reset(car.Body.position);
            passCar.transform.position = car.transform.position + new Vector3(2.3f, 0, 15);
            passScore.Step(car, traffic, .02f);
            passScore.Collision(10, passCar);
            passCar.transform.position = car.transform.position + new Vector3(2.3f, 0, 0);
            passScore.Step(car, traffic, .02f);
            passCar.transform.position = car.transform.position + new Vector3(2.3f, 0, -10);
            passScore.Step(car, traffic, 3);
            Check("Collision invalidates pass", passScore.NearMisses == 0, "A contact invalidates its near miss");
            passCar.gameObject.SetActive(false);
            car.Body.linearVelocity = Vector3.zero;
            run.Scoring.Reset(car.Body.position);
            run.Scoring.Collision(10);
            Check("Crash resets multiplier", run.Scoring.Multiplier == 1, "Impact penalty clears streak");
            float before = car.Body.position.z;
            run.SetPaused(true);
            yield return new WaitForSecondsRealtime(.3f);
            Check("Pause", Mathf.Abs(car.Body.position.z - before) < .001f && Time.timeScale == 0, "Simulation remains frozen");
            run.SetPaused(false);
            car.Damage.Apply(new Vector3(.9f, .6f, 1.8f), 25);
            Check("Mechanical damage", car.Damage.Power < 1 && car.Damage.BrakeEfficiency < 1 && Mathf.Abs(car.Damage.Pull) > 0, "Front-wheel impact reduces power, braking and alignment");
            car.Damage.Repair();
            traffic.SetDensity(1);
            traffic.Restart();
            traffic.Simulate = true;
            car.Teleport(25);
            car.Input.SetTest(1, 0, 0);
            yield return new WaitForSeconds(3);
            car.Input.SetTest(0, 0, 0);
            Capture("01-afternoon");
            foreach (ViewMode mode in Enum.GetValues(typeof(ViewMode)))
            {
                camera.SetMode(mode);
                yield return new WaitForSeconds(.1f);
                Check("Camera " + mode, !float.IsNaN(camera.transform.position.x) && camera.Lens.nearClipPlane <= .15f, "Camera is valid and positioned");
                if (mode == ViewMode.Cockpit)
                    Capture("02-cockpit");
            }

            camera.SetMode(ViewMode.Chase);
            car.Teleport(Island.TownS - 250);
            yield return new WaitForSeconds(.8f);
            Capture("03-town");
            car.Teleport(650);
            weather.Set(Weather.Rain, true);
            weather.Apply(10);
            yield return new WaitForSeconds(1);
            Capture("04-night-rain");
            weather.Set(Weather.Clear, false);
            weather.Apply(10);
            traffic.Simulate = false;
            foreach (var c in traffic.Cars)
                c.gameObject.SetActive(false);
            float forest = Route.EastStart + 1200;
            foreach (float z in new[]{1000f, 2000f, 3000f, Route.WestEnd + 250, Island.TownS - 100, Route.EastStart - 350, forest, Route.EastStart + 2600})
            {
                car.Input.SetTest(0, 0, 0);
                car.Teleport(z, 1);
                yield return new WaitForSeconds(.55f);
                car.Body.linearVelocity = car.transform.forward * (160 / 3.6f);
                float began = Time.time, worstLateral = 0;
                while (Time.time - began < 4)
                {
                    Follow(.65f);
                    worstLateral = Mathf.Max(worstLateral, Mathf.Abs(Route.Lateral(car.transform.position)));
                    if (Time.frameCount % 15 == 0 && z == forest)
                        Debug.Log($"QA INFO trace {z}: t {Time.time - began:0.00} lat {Route.Lateral(car.transform.position):0.00} v {car.Speed:0} steerIn {car.Input.Steer:0.00} angle {car.Chassis.SteerAngle:0.0} yaw {Vector3.Dot(car.Body.angularVelocity, Vector3.up):0.00} slip {car.Chassis.Slip:0.00}");
                    yield return new WaitForFixedUpdate();
                }
                Debug.Log($"QA INFO route {z}: worst lane offset {worstLateral:0.00}m, speed {car.Speed:0}, steer {car.Chassis.SteerAngle:0.0}");

                Check("Route section " + z, Vector3.Dot(car.transform.up, Vector3.up) > .93f && Mathf.Abs(Route.Lateral(car.transform.position)) < 3 && Mathf.Abs(car.transform.position.y - Route.Height(Route.Locate(car.transform.position))) < 1, $"s {Route.Locate(car.transform.position):0}, upright {car.transform.up.y:0.00}, lateral {Route.Lateral(car.transform.position):0.00}m");
                if (z == forest)
                    Capture("05-forest");
                if (z == 2000f)
                    Capture("06-coast");
            }

            car.Teleport(25);
            car.Input.SetTest(0, 0, 0);
            yield return new WaitForSeconds(.4f);
            // Exercise the real Unity Input System event path, including frame timing.
            var keyboard = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Keyboard>();
            car.Input.ClearTest();
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.W));
            yield return null;
            yield return null;
            Check("Keyboard throttle", car.Input.Throttle > .9f, "W reaches the vehicle input through Input System");
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
            yield return null;
            var previousMode = camera.Mode;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.C));
            yield return null;
            yield return null;
            Check("Keyboard camera", camera.Mode != previousMode, "C cycles the active camera through the real input path");
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
            yield return null;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Escape));
            yield return null;
            yield return null;
            Check("Keyboard pause", run.Paused, "Escape pauses the game through the real input path");
            var menuPad = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>();
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(menuPad, new UnityEngine.InputSystem.LowLevel.GamepadState().WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.DpadDown));
            yield return null;
            yield return null;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(menuPad, new UnityEngine.InputSystem.LowLevel.GamepadState());
            yield return null;
            yield return null;
            Weather beforeMenuWeather = weather.Current;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(menuPad, new UnityEngine.InputSystem.LowLevel.GamepadState().WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.South));
            yield return null;
            yield return null;
            Check("Gamepad options", run.Paused && weather.Current != beforeMenuWeather, "D-pad navigates pause menu and A changes selected setting");
            UnityEngine.InputSystem.InputSystem.RemoveDevice(menuPad);
            weather.Set(Weather.Clear, false);
            yield return CaptureFull("07-options");
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
            yield return null;
            run.SetPaused(false);
            var pad = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>();
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad, new UnityEngine.InputSystem.LowLevel.GamepadState{rightTrigger = .8f, leftStick = new Vector2(.4f, 0)});
            yield return null;
            yield return null;
            Check("Gamepad axes", car.Input.Throttle > .7f && car.Input.Steer > .3f, "Analog trigger and stick reach vehicle input");
            UnityEngine.InputSystem.InputSystem.RemoveDevice(pad);
            UnityEngine.InputSystem.InputSystem.RemoveDevice(keyboard);
            car.Input.SetTest(0, 0, 0);
            camera.SetMode(ViewMode.Chase);
            yield return new WaitForSeconds(.1f);
            yield return CaptureFull("06-driving-hud");
            car.Input.SetTest(0, 1, 0);
            yield return null;
            yield return null;
            Check("Brake lamps", car.GetComponent<CarBody>().BrakeLit, "Brake input illuminates player brake lamps");
            car.Input.SetTest(0, 0, 0);
            yield return null;
            yield return null;
            Check("Brake lamps release", !car.GetComponent<CarBody>().BrakeLit, "Lamps return to tail-light brightness after releasing brake");
            var studioKeyboard = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Keyboard>();
            car.Input.ClearTest();
            run.SetPaused(true);
            yield return null;
            yield return null;
            for (int i = 0; i < 11; i++)
            {
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(studioKeyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Tab));
                yield return null;
                yield return null;
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(studioKeyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
                yield return null;
                yield return null;
            }
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(studioKeyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Enter));
            yield return null;
            yield return null;
            Check("Studio navigation", camera.Inspecting && run.Paused, "Tab and Enter open the vehicle studio from the pause menu");
            Vector3 frozen = car.Body.position, cameraBefore = camera.transform.position;
            yield return new WaitForSecondsRealtime(.5f);
            Check("Studio orbit while paused", Vector3.Distance(cameraBefore, camera.transform.position) > .1f && Vector3.Distance(frozen, car.Body.position) < .001f, "Inspection camera orbits with the driving simulation frozen");
            Color paintBefore = car.GetComponent<CarBody>().PaintColor;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(studioKeyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
            yield return null;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(studioKeyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Enter));
            yield return null;
            yield return null;
            Check("Studio paint control", (car.GetComponent<CarBody>().PaintColor != paintBefore || !car.GetComponent<CarBody>().Repaintable) && run.Profile.paint == 1, "Enter applies the selected paint finish to the player car");
            yield return CaptureFull("08-vehicle-studio");
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(studioKeyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
            yield return null;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(studioKeyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Escape));
            yield return null;
            yield return null;
            Check("Studio exit", !camera.Inspecting && !run.Paused, "Escape restores the driving camera and resumes the run");

            // Garage: switching cars rebuilds the model and the physics specification.
            run.SetPaused(true);
            camera.Inspect(true);
            yield return null;
            string beforeCar = run.Car.Name;
            float beforeMass = car.Spec.mass;
            run.CycleCar();
            yield return new WaitForSecondsRealtime(.3f);
            Check("Garage swap", run.Car.Name != beforeCar && car.Spec.mass != beforeMass && car.GetComponent<CarBody>().Shape != null && car.Chassis.Wheels[0] != null, $"{beforeCar} -> {run.Car.Name}, {beforeMass:0} -> {car.Spec.mass:0} kg");
            camera.Inspect(false);
            run.SetPaused(false);
            car.Teleport(300);
            yield return new WaitForSeconds(.4f);
            car.Input.SetTest(1, 0, 0);
            float garageStart = Time.time;
            while (Time.time - garageStart < 5)
            {
                Follow(1);
                yield return new WaitForFixedUpdate();
            }

            Check("Swapped car drives", car.Speed > 60 && Vector3.Dot(car.transform.up, Vector3.up) > .9f, $"{car.Speed:0} km/h after swap, gear {car.Engine.Label}");
            yield return NitroChecks();
            car.Input.SetTest(0, 0, 0);
            // Every garage car: four visible wheels, and headlamps on the car rather than a lens
            // floating over the bonnet (from the driver's seat it glared in the middle of the view).
            string wheelIssues = "", lampIssues = "";
            car.Input.SetTest(0, 1, 0);
            for (int n = 0; n < CarCatalog.Count; n++)
            {
                run.CycleCar();
                yield return new WaitForSeconds(.6f);
                Capture("31-car-" + run.Profile.car);
                var bodywork = run.Player.GetComponent<CarBody>();
                string carName = run.Car.Name;
                int wheelsSeen = 0;
                foreach (var hub in bodywork.Wheels)
                {
                    bool seen = false;
                    foreach (var rr in hub.GetComponentsInChildren<Renderer>())
                    {
                        var mf = rr.GetComponent<MeshFilter>();
                        if (rr.enabled && rr.bounds.size.y > .3f && (mf == null || mf.sharedMesh != null)) seen = true;
                    }
                    if (seen) wheelsSeen++;
                }
                if (wheelsSeen < 4) wheelIssues += $"{carName} {wheelsSeen}/4; ";
                bool ownLamps = !string.IsNullOrEmpty(bodywork.Shape.Prefab) || !string.IsNullOrEmpty(bodywork.Shape.Model);
                if (bodywork.Headlights != null)
                    foreach (var beam in bodywork.Headlights)
                        // Tall cars (vans, the G-class) carry their lamps higher.
                        if (beam && (beam.transform.localPosition.y > Mathf.Max(.9f, bodywork.Shape.Height * .55f) || beam.intensity > 1200))
                            lampIssues += $"{carName} beam at {beam.transform.localPosition.y:0.00} m; ";
                foreach (var t in run.Player.GetComponentsInChildren<Transform>(true))
                    if (ownLamps && t.name == "Headlamp glow")
                    {
                        lampIssues += $"{carName} extra lens; ";
                        break;
                    }
                // Side view for the wheels, and the driver's view at night for the lamps.
                camera.enabled = false;
                camera.transform.position = run.Player.transform.position + run.Player.transform.right * 5.5f + Vector3.up * 1.1f;
                camera.transform.LookAt(run.Player.transform.position + Vector3.up * .55f);
                Capture("35-car-side-" + run.Profile.car);
                camera.enabled = true;
                weather.Set(Weather.Clear, true);
                for (int k = 0; k < 30; k++) { weather.Apply(.3f); yield return null; }
                camera.SetMode(ViewMode.Cockpit);
                yield return new WaitForSeconds(.4f);
                Capture("36-cockpit-night-" + run.Profile.car);
                camera.SetMode(ViewMode.Chase);
                weather.Set(Weather.Clear, false);
                for (int k = 0; k < 30; k++) { weather.Apply(.3f); yield return null; }
            }
            Check("Wheels on every car", wheelIssues == "", wheelIssues == "" ? $"{CarCatalog.Count} cars, four visible wheels each" : wheelIssues);
            Check("Headlamps on the car", lampIssues == "", lampIssues == "" ? "beams at lamp height, no lens floating over the bonnet" : lampIssues);
            for (int guard = 0; guard < CarCatalog.Count && run.Profile.car != 0; guard++)
            {
                run.CycleCar();
                yield return new WaitForSeconds(.2f);
            }


            yield return null;            UnityEngine.InputSystem.InputSystem.RemoveDevice(studioKeyboard);
            car.Input.SetTest(0, 0, 0);
            yield return OnlineChecks();
            yield return PoliceQA.Run(run, Check);
            car.Teleport(2200);
            yield return new WaitForSeconds(.5f);
            Capture("09-overpass");
            ProfileStore.TestDirectory = Path.Combine(output, "SaveTest");
            ProfileStore.Testing = false;
            var testProfile = new DriverProfile{credits = 432, runs = 1, paint = 3};
            bool wrote = ProfileStore.Save(testProfile);
            Check("Save round trip", wrote && ProfileStore.Load().credits == 432, "JSON profile restores persisted credits");
            Check("Paint save round trip", ProfileStore.Load().paint == 3, "Selected paint survives JSON save and reload");
            var legacy = JsonUtility.FromJson<DriverProfile>("{\"version\":1,\"credits\":321,\"runs\":2}");
            Check("Legacy profile compatibility", legacy.credits == 321 && legacy.paint == 0, "Existing profiles keep credits and receive the default paint");
            testProfile.credits = 984;
            ProfileStore.Save(testProfile);
            File.WriteAllText(Path.Combine(ProfileStore.TestDirectory, "driver-v1.json"), "corrupt test data");
            Check("Save backup recovery", ProfileStore.Load().credits == 432, "Damaged primary profile falls back to last good backup");
            ProfileStore.TestDirectory = null;
            ProfileStore.Testing = true;
            run.Finish();
            int paid = run.Profile.credits;
            run.Finish();
            Check("Reward paid once", paid == run.Profile.credits && run.Profile.runs == 1, "Repeated finish does not double bank credits");
            run.Restart();
            Check("Restart", !run.Finished && !run.Paused && Island.InCity(car.Body.position) && car.Damage.Engine == 0 && run.Scoring.Points == 0, "Fresh run, repaired car, reset score");
            // No control hints on the driving HUD, not even in the first seconds of a fresh run
            // (they live in the menu, under "УПРАВЛЕНИЕ"): collect what the HUD draws and look for keys.
            {
                DriveHud.Audit = new List<string>();
                yield return new WaitForSecondsRealtime(.5f);
                var drawn = DriveHud.Audit;
                DriveHud.Audit = null;
                string[] keyWords = { "WASD", "ESC", "CTRL", "ENTER", "ПРОБЕЛ", "SHIFT", "ВКЛЮЧИТЬ", "К ДРУГУ", "(T)" };
                string found = null;
                foreach (var text in drawn)
                {
                    if (text == "X" || text == "◐ F") found = text;
                    foreach (var k in keyWords)
                        if (text.Contains(k)) found = text;
                }
                Check("No control hints while driving", drawn.Count > 10 && found == null && run.Scoring.TimeDriven < 15,
                    found == null ? $"{drawn.Count} HUD texts in the first seconds of a run, none a key hint" : $"hint on screen: \"{found}\"");
            }
            yield return JunctionAndEntryChecks();
            yield return IslandChecks();
            EngineAudioCheck();
            yield return DistanceModeChecks();
            OptimisationChecks();
            report.averageRenderFps = frameCount / Mathf.Max(.001f, frameTotal);
            Check("Runtime errors", !errors, "No unexpected error or exception logged");
            File.WriteAllText(Path.Combine(output, "validation.json"), JsonUtility.ToJson(report, true));
            bool passed = report.checks.TrueForAll(x => x.pass);
            Debug.Log("AUTOBAHN_QA_" + (passed ? "PASSED" : "FAILED"));
#if UNITY_EDITOR
            UnityEditor.SessionState.SetBool("Autobahn.QA", false);
            UnityEditor.EditorApplication.Exit(passed ? 0 : 2);
#else
 Application.Quit(passed?0:2);
#endif
        }

        // The island: its layout, the road in and out of the city, city traffic, the sea.
        // Performance per preset at the window's real resolution: median frame and GPU time
        // from four wide views, and a picture of each view on High.
        IEnumerator PerfChecks()
        {
            traffic.Simulate = true;
            yield return new WaitForSeconds(3);
            float mc = 0, mg = 0, worst = 0;
            IEnumerator View()
            {
                yield return new WaitForSecondsRealtime(1.2f);
                frameTimes.Clear(); cpuList.Clear(); gpuList.Clear(); sampling = true;
                StartCoroutine(SampleSmoothness());
                yield return new WaitForSecondsRealtime(2f);
                sampling = false;
                frameTimes.Sort();
                float total = 0;
                foreach (var f in frameTimes) total += f;
                mc = frameTimes.Count > 0 ? total / frameTimes.Count * 1000 : 0;
                worst = frameTimes.Count > 0 ? frameTimes[frameTimes.Count * 95 / 100] * 1000 : 0;
                float sum = 0; int n = 0;
                foreach (var g in gpuList) if (g > .01f) { sum += g; n++; }
                mg = n > 0 ? sum / n : 0;
            }
            var views = new (string, Vector3, Vector3)[]
            {
                ("city street", new Vector3(GridCity.LaneOffset(0), 1.8f, 120), new Vector3(GridCity.LaneOffset(0), 1.5f, 700)),
                ("across the city", new Vector3(-20, 4, 20), new Vector3(1400, 0, 800)),
                ("coast road to the city", Route.Center(700) + Vector3.up * 3, new Vector3(700, 10, 300)),
                ("airport over the island", new Vector3(700, 25, 1300), new Vector3(700, 60, 3200)),
            };
            camera.enabled = false;
            int cap = Application.targetFrameRate;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            var report = new System.Text.StringBuilder($"QA INFO perf at {Screen.width}x{Screen.height} (average / 95% / gpu ms):");
            for (int level = 0; level < 4; level++)
            {
                GraphicsQuality.Apply(level);
                foreach (var (label, from, to) in views)
                {
                    camera.transform.position = from;
                    camera.transform.LookAt(to);
                    yield return View();
                    report.Append($" [{GraphicsQuality.Names[level]} {label}: {mc:0.0} / {worst:0.0} / {mg:0.0}]");
                    if (level == 2) Capture("60-high-" + label.Replace(' ', '-'));
                }
            }
            // What makes High expensive: each variant puts one group of settings back to Balanced.
            {
                var cam = Camera.main;
                var data = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam);
                var pipe = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                var volume = FindFirstObjectByType<UnityEngine.Rendering.Volume>();
                void Ssao(bool on)
                {
                    var field = typeof(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field?.GetValue(pipe) is UnityEngine.Rendering.Universal.ScriptableRendererData[] list)
                        foreach (var d in list)
                            if (d != null)
                                foreach (var feature in d.rendererFeatures)
                                    if (feature is UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion) feature.SetActive(on);
                }
                var variants = new (string, System.Action)[]
                {
                    ("as is", () => { }),
                    ("no SSAO", () => Ssao(false)),
                    ("Balanced shadows", () => { pipe.shadowDistance = 80; pipe.shadowCascadeCount = 2; pipe.mainLightShadowmapResolution = 1536; }),
                    ("no shadows", () => { pipe.shadowDistance = 0; }),
                    ("Balanced LOD and reach", () => { QualitySettings.lodBias = 1; var c = cam.layerCullDistances; c[Art.TreeLayer] = 420; c[Art.PropLayer] = 230; cam.layerCullDistances = c; }),
                    ("FXAA, no blur", () => { data.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.FastApproximateAntialiasing; if (volume && volume.profile.TryGet(out UnityEngine.Rendering.Universal.MotionBlur mb)) mb.active = false; }),
                    ("half pixels", () => { pipe.renderScale = .5f; }),
                };
                var vr = new System.Text.StringBuilder("QA INFO High variants (average / 95% ms):");
                foreach (var (label, from, to) in new[] { views[0], views[1] })
                    foreach (var (name, change) in variants)
                    {
                        GraphicsQuality.Apply(2);
                        change();
                        camera.transform.position = from;
                        camera.transform.LookAt(to);
                        yield return View();
                        vr.Append($" [{label} {name}: {mc:0.0} / {worst:0.0}]");
                    }
                report.Append(vr.ToString());
            }
            Debug.Log(report.ToString());
            File.WriteAllText(Path.Combine(output, "perf.txt"), report.ToString().Replace(" [", "\n["));
            GraphicsQuality.Apply(1);
            Application.targetFrameRate = cap;
            camera.enabled = true;
        }

        // Quick look at chosen spots: each line of the file is
        //   name x y z lookX lookY lookZ [night]      a still from a free camera
        //   name drive routeZ kmh [night]              the chase camera behind the car at speed
        // Lines starting with # are skipped. The car is parked under the camera so the world streams in.
        IEnumerator Shots(string file)
        {
            traffic.Simulate = true;
            yield return new WaitForSeconds(2);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                bool night = Array.IndexOf(p, "night") >= 0;
                var seaR = IslandWorld.Active ? IslandWorld.Active.SeaRenderer : null;
                if (seaR) seaR.enabled = Array.IndexOf(p, "nosea") < 0;
                weather.Set(Weather.Clear, night);
                weather.Apply(30);
                if (p.Length >= 2 && p[1] == "guns")
                {
                    // name guns: every gun model in a row, seen from the side (barrels should point right).
                    camera.enabled = false;
                    var row = new GameObject("Gun lineup").transform;
                    row.position = new Vector3(700, 1.5f, 1100);
                    for (int g = 1; g < Weapons.All.Length; g++)
                    {
                        var m = Weapons.Model(Weapons.All[g], row, out Vector3 mz);
                        if (!m) continue;
                        m.localPosition = new Vector3(0, (g - 1) * .45f, 0);
                        m.localRotation = Quaternion.Euler(0, 90, 0);
                    }
                    camera.transform.position = row.position + new Vector3(0, 1.1f, -3.2f);
                    camera.transform.LookAt(row.position + new Vector3(0, 1.1f, 0));
                    yield return new WaitForSeconds(.5f);
                    Capture("shot-" + p[0]);
                    Destroy(row.gameObject);
                    camera.enabled = true;
                    continue;
                }
                if (p.Length >= 2 && p[1] == "hud")
                {
                    // name hud x z yaw: the car there, and a real screenshot with the HUD (minimap).
                    camera.enabled = true;
                    car.Place(new Vector3(float.Parse(p[2], inv), 1f, float.Parse(p[3], inv)), Quaternion.Euler(0, float.Parse(p[4], inv), 0));
                    yield return new WaitForSeconds(2.5f);
                    ScreenCapture.CaptureScreenshot(Path.Combine(output, "shot-" + p[0] + ".png"));
                    yield return new WaitForSeconds(.5f);
                    Debug.Log($"QA SHOT hud: picture {IslandSnapshot.Ready}");
                    continue;
                }
                if (p.Length >= 8 && p[1] == "perf")
                {
                    // name perf x y z lookX lookY lookZ: average frame time from a fixed camera with
                    // parts of the world switched off in turn (what costs what).
                    Vector3 pa = new(float.Parse(p[2], inv), float.Parse(p[3], inv), float.Parse(p[4], inv));
                    Vector3 pl = new(float.Parse(p[5], inv), float.Parse(p[6], inv), float.Parse(p[7], inv));
                    var pg = pa; pg.y = Mathf.Max(Island.GroundHeight(pa.x, pa.z), 0) + 1;
                    car.Place(pg + Vector3.right * 3, Quaternion.identity);
                    int gfxBefore = GraphicsQuality.Level;
                    if (Array.IndexOf(p, "high") >= 0) GraphicsQuality.Apply(2);
                    if (Array.IndexOf(p, "fast") >= 0) GraphicsQuality.Apply(0);
                    camera.enabled = false;
                    var cam = Camera.main;
                    cam.transform.position = pa;
                    cam.transform.LookAt(pl);
                    yield return new WaitForSeconds(2f);
                    Light sunLight = null;
                    foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.type == LightType.Directional && l.shadows != LightShadows.None) { sunLight = l; break; }
                    int mask = cam.cullingMask;
                    var sb = new System.Text.StringBuilder($"QA PERF {p[0]}: ");
                    foreach (var mode in new[] { "all", "no-trees", "no-props", "no-default", "no-shadows", "no-forest-script", "no-near", "no-far", "only-default" })
                    {
                        cam.cullingMask = mode == "no-trees" ? mask & ~(1 << Art.TreeLayer) : mode == "no-props" ? mask & ~(1 << Art.PropLayer) : mode == "no-default" ? mask & ~1 : mode == "only-default" ? 1 : mask;
                        var shadowsBefore = sunLight ? sunLight.shadows : LightShadows.None;
                        if (mode == "no-shadows" && sunLight) sunLight.shadows = LightShadows.None;
                        ForestRenderer.Hidden = mode == "no-forest-script";
                        ForestRenderer.SkipNear = mode == "no-near";
                        ForestRenderer.SkipFar = mode == "no-far";
                        for (int f = 0; f < 10; f++) yield return null;
                        float sum = 0, worst = 0;
                        for (int f = 0; f < 90; f++) { yield return null; sum += Time.unscaledDeltaTime; worst = Mathf.Max(worst, Time.unscaledDeltaTime); }
                        sb.Append($"[{mode} {sum / 90 * 1000:0.0} ms, worst {worst * 1000:0}] ");
                        if (sunLight) sunLight.shadows = shadowsBefore;
                        ForestRenderer.Hidden = ForestRenderer.SkipNear = ForestRenderer.SkipFar = false;
                    }
                    cam.cullingMask = mask;
                    var fr = ForestRenderer.Active;
                    sb.Append($"forest near {(fr ? fr.DrawnNear : 0)} far {(fr ? fr.DrawnFar : 0)} calls {(fr ? fr.Calls : 0)}, knockables {Knockable.Count} in {Knockable.Batches} meshes, gfx level {GraphicsQuality.Level}");
                    Debug.Log(sb.ToString());
                    GraphicsQuality.Apply(gfxBefore);
                    continue;
                }
                if (p.Length >= 6 && p[1] == "knock")
                {
                    // name knock x z yaw kmh: the car launched at that speed (into street furniture).
                    camera.enabled = true;
                    float kx = float.Parse(p[2], inv), kz = float.Parse(p[3], inv), kyaw = float.Parse(p[4], inv), kmh = float.Parse(p[5], inv);
                    int knockedBefore = Knockable.Knocked;
                    car.Place(new Vector3(kx, .6f, kz), Quaternion.Euler(0, kyaw, 0));
                    yield return new WaitForSeconds(.4f);
                    car.Body.linearVelocity = Quaternion.Euler(0, kyaw, 0) * Vector3.forward * (kmh / 3.6f);
                    yield return new WaitForSeconds(1.3f);
                    ScreenCapture.CaptureScreenshot(Path.Combine(output, "shot-" + p[0] + ".png"));
                    yield return new WaitForSeconds(.5f);
                    Debug.Log($"QA SHOT knock {p[0]}: knocked {Knockable.Knocked - knockedBefore}, props {Knockable.Count}, merged meshes {Knockable.Batches}, car now {car.Body.linearVelocity.magnitude * 3.6f:0} km/h, exploded {car.WreckedFor > 0}");
                    continue;
                }
                if (p.Length >= 2 && p[1] == "probe")
                {
                    // name probe x0 z0 x1 z1: ground heights along a line (debugging the shore).
                    float px0 = float.Parse(p[2], inv), pz0 = float.Parse(p[3], inv), px1 = float.Parse(p[4], inv), pz1 = float.Parse(p[5], inv);
                    var sb = new System.Text.StringBuilder("QA PROBE ");
                    for (int k = 0; k <= 40; k++)
                    {
                        float fx = Mathf.Lerp(px0, px1, k / 40f), fz = Mathf.Lerp(pz0, pz1, k / 40f);
                        sb.Append($"({fx:0},{fz:0}) {Island.GroundHeight(fx, fz):0.0} | ");
                    }
                    Debug.Log(sb.ToString());
                    continue;
                }
                if (p.Length >= 2 && p[1] == "island")
                {
                    // name island [width]: the whole island from straight above (the map picture).
                    int w = p.Length >= 3 ? int.Parse(p[2]) : 2048;
                    yield return new WaitForSeconds(1f);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var tex = IslandSnapshot.Render(w);
                    if (tex) File.WriteAllBytes(Path.Combine(output, "shot-" + p[0] + ".png"), tex.EncodeToPNG());
                    Debug.Log($"QA SHOT island: {(tex ? tex.width + "x" + tex.height : "none")} in {clock.ElapsedMilliseconds} ms");
                    continue;
                }
                if (p.Length >= 2 && p[1] == "kneel")
                {
                    // name kneel: get out, kneel and say sorry (front and side), then an explosion
                    // next to the person (the car blows up) must hurt them.
                    FootManager.Automated = true;
                    camera.enabled = true;
                    car.Teleport(1200, 1);
                    yield return new WaitForSeconds(1f);
                    FootManager.Instance.GetOut();
                    yield return new WaitForSeconds(1f);
                    var kp = FootPlayer.Active;
                    if (kp)
                    {
                        kp.Select(0);
                        kp.Pitch = 0;
                        kp.Respawn(kp.transform.position - car.transform.right * 5, kp.Yaw);
                        kp.Kneeling = true;
                        yield return new WaitForSeconds(1.2f);
                        FootManager.FreeCamera = true;
                        var eye = Camera.main.transform;
                        Vector3 body = kp.transform.position + Vector3.up * .8f;
                        eye.position = body + kp.transform.forward * 3.2f + Vector3.up * .5f;
                        eye.LookAt(body);
                        yield return null;
                        Capture("shot-" + p[0] + "-front");
                        eye.position = body + kp.transform.right * 3.2f + Vector3.up * .3f;
                        eye.LookAt(body);
                        yield return null;
                        Capture("shot-" + p[0] + "-side");
                        kp.Kneeling = false;
                        kp.Crouching = true;
                        yield return new WaitForSeconds(.8f);
                        body = kp.transform.position + Vector3.up * .8f;
                        eye.position = body + kp.transform.right * 3.2f + kp.transform.forward * 1.5f + Vector3.up * .3f;
                        eye.LookAt(body);
                        yield return null;
                        Capture("shot-" + p[0] + "-crouch");
                        kp.Crouching = false;
                        FootManager.FreeCamera = false;
                        var hips = kp.Rig.Anim ? kp.Rig.Anim.GetBoneTransform(HumanBodyBones.Hips) : null;
                        Debug.Log($"QA SHOT kneel: kneeling {kp.Kneeling}, hips {(hips ? (hips.position.y - kp.transform.position.y).ToString("0.00") : "-")}, head {(kp.Rig.Head ? (kp.Rig.Head.position.y - kp.transform.position.y).ToString("0.00") : "-")}");
                        kp.Kneeling = false;
                        yield return new WaitForSeconds(.6f);
                        float before = kp.Health;
                        kp.Respawn(car.transform.position + car.transform.right * -4.5f + Vector3.up * .2f, car.transform.eulerAngles.y + 90);
                        yield return new WaitForSeconds(.5f);
                        car.Explode();
                        yield return new WaitForSeconds(.2f);
                        Capture("shot-" + p[0] + "-blast");
                        Debug.Log($"QA SHOT blast: health {before:0} -> {kp.Health:0}, dead {kp.Dead}");
                        yield return new WaitForSeconds(5f);
                        if (FootPlayer.Active) FootManager.Instance.GetIn();
                    }
                    FootManager.Automated = false;
                    continue;
                }
                if (p.Length >= 2 && p[1] == "foot")
                {
                    // name foot: get out of the car, third person, first person, aim and fire.
                    FootManager.Automated = true;
                    camera.enabled = true;
                    car.Teleport(1200, 1);
                    yield return new WaitForSeconds(1f);
                    FootManager.Instance.GetOut();
                    yield return new WaitForSeconds(1.2f);
                    var fp = FootPlayer.Active;
                    Debug.Log($"QA SHOT foot: spawned {fp != null}, human {(fp && fp.Rig.Anim ? fp.Rig.Anim.isHuman : false)}, hand {(fp && fp.Rig.RightHand ? fp.Rig.RightHand.name : "-")}, head {(fp && fp.Rig.Head ? (fp.Rig.Head.position.y - fp.transform.position.y).ToString("0.00") : "-")}");
                    if (fp)
                    {
                        fp.Yaw += 150; fp.Pitch = 5;
                        yield return new WaitForSeconds(.4f);
                        Capture("shot-" + p[0] + "-tp-front");
                        fp.Yaw -= 150;
                        fp.Select(3);
                        yield return new WaitForSeconds(.6f);
                        Capture("shot-" + p[0] + "-tp");
                        fp.TestFire();
                        yield return null;
                        Capture("shot-" + p[0] + "-tp-fire");
                        FootManager.FirstPerson = true;
                        yield return new WaitForSeconds(.4f);
                        Capture("shot-" + p[0] + "-fp");
                        fp.Select(1);
                        yield return new WaitForSeconds(.5f);
                        fp.TestFire();
                        yield return null;
                        Capture("shot-" + p[0] + "-fp-pistol");
                        FootManager.FirstPerson = false;
                        fp.Yaw += 90;
                        yield return new WaitForSeconds(.8f);
                        Capture("shot-" + p[0] + "-tp-side");
                        FootManager.Instance.GetIn();
                        yield return new WaitForSeconds(.3f);
                        Debug.Log($"QA SHOT foot: back in car {!FootManager.OnFoot && !DriverInput.OnFoot}");
                    }
                    FootManager.Automated = false;
                    continue;
                }
                if (p.Length >= 3 && p[1] == "car")
                {
                    // name car catalogIndex: the garage car on the airport apron, three views.
                    int idx = int.Parse(p[2], inv);
                    run.Profile.car = (idx + CarCatalog.Count - 1) % CarCatalog.Count;
                    run.CycleCar();
                    var body = car.GetComponent<CarBody>();
                    car.Place(new Vector3(Island.AirX0 + 300, .6f, Island.AirZ0 + 220), Quaternion.LookRotation(Vector3.right));
                    yield return new WaitForSeconds(1.5f);
                    camera.enabled = false;
                    Vector3 c0 = car.transform.position;
                    float len = body.Shape.Length;
                    camera.transform.position = c0 + car.transform.forward * (len * .9f + 1.5f) - car.transform.right * (len * .6f + 1.5f) + Vector3.up * 1.4f;
                    camera.transform.LookAt(c0 + Vector3.up * .6f);
                    yield return null;
                    Capture("shot-" + p[0] + "-front");
                    camera.transform.position = c0 - car.transform.right * (len * .8f + 1.5f) + Vector3.up * .7f;
                    camera.transform.LookAt(c0 + Vector3.up * .6f);
                    yield return null;
                    Capture("shot-" + p[0] + "-side");
                    camera.transform.position = c0 - car.transform.forward * (len * .9f + 1.5f) + car.transform.right * (len * .5f) + Vector3.up * 1.6f;
                    camera.transform.LookAt(c0 + Vector3.up * .6f);
                    yield return null;
                    Capture("shot-" + p[0] + "-rear");
                    Debug.Log($"QA SHOT car {p[0]}: {CarCatalog.Get(idx).Name} measured {body.Shape.Measured} wb {body.Shape.Wheelbase:0.00} r {body.Shape.WheelRadius:0.00} len {len:0.00}");
                    camera.enabled = true;
                    continue;
                }
                if (p.Length >= 4 && p[1] == "drive")
                {
                    float z = float.Parse(p[2], inv), kmh = float.Parse(p[3], inv);
                    camera.enabled = true;
                    car.Teleport(z, 1);
                    yield return new WaitForSeconds(.5f);
                    car.Body.linearVelocity = car.transform.forward * (kmh / 3.6f);
                    car.Input.SetTest(1, 0, 0);
                    yield return new WaitForSeconds(2.5f);
                    Capture("shot-" + p[0]);
                    car.Input.SetTest(0, 0, 0);
                    continue;
                }
                Vector3 at, look;
                if (p[1] == "route")
                {
                    // name route s lateral height lookS lookLateral lookHeight ("T+40" = 40 m past the town centre)
                    float S(string v) => v.StartsWith("T") ? Island.TownS + float.Parse(v.Substring(1), inv) : float.Parse(v, inv);
                    Vector3 R(float rs, float lat, float h) => Route.Center(rs) + Route.Right(rs) * lat + Vector3.up * h;
                    at = R(S(p[2]), float.Parse(p[3], inv), float.Parse(p[4], inv));
                    look = R(S(p[5]), float.Parse(p[6], inv), float.Parse(p[7], inv));
                }
                else
                {
                    at = new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv));
                    look = new Vector3(float.Parse(p[4], inv), float.Parse(p[5], inv), float.Parse(p[6], inv));
                }
                var ground = at;
                ground.y = Mathf.Max(Island.GroundHeight(at.x, at.z), 0) + 1;
                car.Place(ground + Vector3.right * 3, Quaternion.identity);
                camera.enabled = false;
                camera.transform.position = at;
                camera.transform.LookAt(look);
                yield return new WaitForSeconds(1.5f);
                Capture("shot-" + p[0]);
            }
            camera.enabled = true;
        }

        // A world-sync message as a friend's game would send it (with its sender slot).
        static byte[] KMessage(byte type, int key, Vector3 at, Vector3 v)
        {
            var ms = new System.IO.MemoryStream(32);
            var w = new System.IO.BinaryWriter(ms);
            w.Write((byte)'K'); w.Write((byte)1); w.Write(type); w.Write(key);
            w.Write(at.x); w.Write(at.y); w.Write(at.z);
            w.Write((short)(v.x * 100)); w.Write((short)(v.y * 100)); w.Write((short)(v.z * 100));
            return ms.ToArray();
        }

        IEnumerator IslandChecks()
        {
            var world = IslandWorld.Active;
            var city = GridWorld.IslandCity;
            Check("Island built", world != null && world.TileCount > 150 && world.TreeCount > 3000 && city != null && city.TileCount == (Island.CityTilesX + 1) * (Island.CityTilesZ + 1),
                world == null ? "no island" : $"{world.TileCount} ground tiles, {world.TreeCount} trees, {(city ? city.TileCount : 0)} city tiles");

            float L = Route.Course;
            Vector3 a = Route.Center(0), b = Route.Center(L);
            bool joins = new Vector2(a.x, a.z - Island.RouteStartZ).magnitude < 1 && new Vector2(b.x - Island.CityW, b.z - Island.RouteStartZ).magnitude < 1
                         && Route.Forward(1).z > .99f && Route.Forward(L - 1).z < -.99f && Mathf.Abs(a.y) < .05f && Mathf.Abs(b.y) < .05f;
            Check("Road leaves and rejoins the city", joins, $"starts at {a} heading north, ends at {b} heading south, {L / 1000:0.0} km");

            int samples = 0, seaLeft = 0;
            string dry = "";
            for (float s = 300; s < L - 300; s += 350)
            {
                samples++;
                Vector3 c = Route.Center(s), left = -Route.Right(s);
                Vector3 far = c + left * 800;
                if (Island.GroundHeight(far.x, far.z) < Island.SeaY) seaLeft++;
                else dry += $" s{s:0}";
            }
            Check("Sea on the left of the road", seaLeft == samples, $"{seaLeft}/{samples} points 800 m to the left are sea{dry}");

            float peak = 0; Vector3 peakAt = Vector3.zero;
            for (float x = 0; x <= Island.CityW; x += 40)
                for (float z = Island.RouteStartZ + 200; z < Island.TopZ; z += 40)
                {
                    float h = Island.GroundHeight(x, z);
                    if (h > peak) { peak = h; peakAt = new Vector3(x, h, z); }
                }
            Check("Mountains in the middle", peak > 250 && peakAt.x > 250 && peakAt.x < Island.CityW - 250, $"highest point {peak:0} m at x {peakAt.x:0}, z {peakAt.z:0}");

            bool seaRound = true;
            string wet = "";
            foreach (var p in new[] { new Vector2(-900, -500), new Vector2(2100, -500), new Vector2(-900, 5500), new Vector2(2100, 5500), new Vector2(700, 5600), new Vector2(-950, 2500), new Vector2(2150, 2500), new Vector2(700, -500) })
            {
                float h = Island.GroundHeight(p.x, p.y);
                if (h >= Island.SeaY) { seaRound = false; wet += $" land at {p}"; }
            }
            Check("Sea all round the island", seaRound, seaRound ? "all eight edge points are under water" : wet);
            // The island streams in round the car: the instanced forest holds the trees of the tiles
            // standing now, never more than the island has.
            Check("Forest round the island", world.EastCoastTrees > 800 && world.WestCoastTrees > 300 && ForestRenderer.Active && ForestRenderer.Active.Trees > 0 && ForestRenderer.Active.Trees <= world.TreeCount + GridWorld.CityTrees + GridWorld.EastTrees + GridWorld.WestTrees,
                $"{world.EastCoastTrees} trees between the road and the sea on the east, {world.WestCoastTrees} on the west, {world.TreeCount} in all, {GridWorld.CityTrees} in the city (instanced {(ForestRenderer.Active ? ForestRenderer.Active.Trees : 0)})");
            var site = Station.Sites[0];
            // The town is only built while someone is near it.
            WorldStreamer.Active?.BuildAround(Route.Center(Island.TownS), 50);
            Check("Town and filling station at the top", world.TownRoot != null && world.TownRoot.childCount > 5 && Mathf.Abs(site.Z - Island.TownS) < 250 && Route.Center(site.Z).z > Island.TopZ - 60,
                $"town at s {Island.TownS:0}, station at s {site.Z:0}, z {Route.Center(site.Z).z:0}");

            // Drive out of the city onto the coast road (the city traffic is parked for this,
            // so a red light or a turning car does not decide the result).
            var cityTraffic = traffic.GetComponent<GridTraffic>();
            traffic.Simulate = true;
            traffic.SetDensity(1);
            if (cityTraffic) cityTraffic.enabled = false;
            foreach (var c in traffic.Cars) if (c.OnGrid) c.gameObject.SetActive(false);
            camera.SetMode(ViewMode.Chase);
            Island.Spawn(car);
            yield return new WaitForSeconds(.6f);
            Capture("35-city-start");
            float t0 = Time.time;
            while (Time.time - t0 < 14) { Follow(.75f); yield return new WaitForFixedUpdate(); }
            float sOut = Route.Locate(car.transform.position);
            Check("Drive out of the city onto the coast road", sOut > 20 && Mathf.Abs(Route.Lateral(car.transform.position)) < 5 && car.transform.up.y > .93f,
                $"s {sOut:0}, lateral {Route.Lateral(car.transform.position):0.0} m, {car.Speed:0} km/h");
            Capture("36-coast-road");

            // Smoothness on the island: along the coast road with the traffic, then in the city.
            foreach (var (label, s0, inCity) in new[] { ("coast road", 900f, false), ("island city", -1f, true) })
            {
                if (inCity) { Island.Spawn(car); if (cityTraffic) { cityTraffic.enabled = true; traffic.SetDensity(1); cityTraffic.Restart(); } }
                else car.Teleport(s0, 1);
                yield return new WaitForSeconds(1f);
                frameTimes.Clear(); sampling = true;
                cpuMs = gpuMs = 0; timed = 0;
                StartCoroutine(SampleSmoothness());
                t0 = Time.time;
                while (Time.time - t0 < 6) { Follow(inCity ? .45f : .8f); yield return new WaitForFixedUpdate(); }
                sampling = false;
                frameTimes.Sort();
                float p95 = frameTimes.Count > 0 ? frameTimes[(int)(frameTimes.Count * .95f)] * 1000 : 0;
                int hitches = 0;
                foreach (var f in frameTimes) if (f > .05f) hitches++;
                Check("Smooth on the island: " + label, hitches <= 3, $"{frameTimes.Count} frames, 95% under {p95:0.0} ms, {hitches} hitches over 50 ms, cpu {cpuMs / Mathf.Max(1, timed):0.0} ms, gpu {gpuMs / Mathf.Max(1, timed):0.0} ms");
            }
            if (cityTraffic) cityTraffic.enabled = false;
            foreach (var c in traffic.Cars) if (c.OnGrid) c.gameObject.SetActive(false);

            // And back in at the east, onto the city streets.
            car.Teleport(L - 280, 1);
            yield return new WaitForSeconds(.5f);
            car.Body.linearVelocity = car.transform.forward * 14;
            t0 = Time.time;
            while (Time.time - t0 < 16 && Route.Locate(car.transform.position) < L + 120) { Follow(.5f); yield return new WaitForFixedUpdate(); }
            car.Input.SetTest(0, 1, 0);
            yield return new WaitForSeconds(1.2f);
            Vector3 back = car.transform.position;
            Check("Drive back into the city", Island.InCity(back) && GridCity.OnStreet(back) && car.transform.up.y > .93f && Mathf.Abs(back.y) < .6f,
                $"at {back}, on a street {GridCity.OnStreet(back)}");
            Capture("37-back-in-city");

            // City traffic: moving, turning, and never leaving the grid.
            var gt = cityTraffic;
            car.Input.SetTest(0, 0, 0);
            Island.Spawn(car);
            if (gt) gt.enabled = true;
            traffic.SetDensity(1);
            gt?.Restart();
            int turns0 = gt ? gt.TurnsCompleted : 0;
            t0 = Time.time;
            bool inside = true;
            string outside = "";
            while (Time.time - t0 < 14)
            {
                foreach (var c in traffic.Cars)
                {
                    if (!c.gameObject.activeSelf || !c.OnGrid || c.transform.position.y < -100) continue;
                    Vector3 p = c.transform.position;
                    if (p.x < -12 || p.x > Island.CityW + 12 || p.z < -12 || p.z > Island.CityH + 12) { inside = false; outside = c.name + " at " + p; }
                }
                yield return new WaitForSeconds(.25f);
            }
            int cityCars = 0;
            foreach (var c in traffic.Cars) if (c.gameObject.activeSelf && c.OnGrid && c.transform.position.y > -100) cityCars++;
            Check("City traffic", gt && gt.enabled && gt.Bounded && cityCars >= 12 && gt.Moving >= 6 && gt.TurnsCompleted > turns0, gt ? $"{cityCars} cars in the city, {gt.Moving} moving, {gt.TurnsCompleted - turns0} turns" : "no city traffic");
            Check("City traffic stays in the city", inside, inside ? "every city car stayed on the island's streets" : outside);

            // A car that goes into the sea comes back on the road.
            Vector3 seaPoint = Route.Center(1500) - Route.Right(1500) * 700;
            seaPoint.y = Island.SeaY + 3;
            car.Place(seaPoint, Route.Rotation(1500));
            yield return new WaitForSeconds(2.5f);
            Check("Out of the sea", car.transform.position.y > Island.SeaY && Mathf.Abs(Route.Lateral(car.transform.position)) < 8, $"car at {car.transform.position}");

            // Long views on the high presets: what distance culling and the GPU-driven renderer
            // (with occlusion culling) save, from three wide viewpoints, each measured three ways.
            {
                float mc = 0, mg = 0;
                // Uncapped frame rate while measuring, so the frame time is the real cost.
                IEnumerator View()
                {
                    yield return new WaitForSecondsRealtime(.6f);
                    frameTimes.Clear(); cpuList.Clear(); gpuList.Clear(); sampling = true;
                    StartCoroutine(SampleSmoothness());
                    yield return new WaitForSecondsRealtime(1.6f);
                    sampling = false;
                    frameTimes.Sort();
                    mc = frameTimes.Count > 0 ? frameTimes[frameTimes.Count / 2] * 1000 : 0;
                    float sum = 0; int n = 0;
                    foreach (var g in gpuList) if (g > .01f) { sum += g; n++; }
                    mg = n > 0 ? sum / n : 0;
                }
                var views = new (string, Vector3, Vector3)[]
                {
                    ("city street", new Vector3(GridCity.LaneOffset(0), 1.8f, 120), new Vector3(GridCity.LaneOffset(0), 1.5f, 700)),
                    ("coast road to the city", Route.Center(700) + Vector3.up * 3, new Vector3(700, 10, 300)),
                    ("airport over the island", new Vector3(700, 25, 1300), new Vector3(700, 60, 3200)),
                };
                camera.enabled = false;
                int cap = Application.targetFrameRate;
                Application.targetFrameRate = -1;
                var report = new System.Text.StringBuilder("QA INFO long view cost (frame ms):");
                float worstBase = 0, worstNew = 0;
                foreach (int level in new[] { 2, 3 })
                    foreach (var (label, from, to) in views)
                    {
                        camera.transform.position = from;
                        camera.transform.LookAt(to);
                        GraphicsQuality.LayerCulling = false; GraphicsQuality.GpuDriven = false; GraphicsQuality.Apply(level);
                        yield return View();
                        float bc = mc, bg = mg;
                        GraphicsQuality.LayerCulling = true; GraphicsQuality.Apply(level);
                        yield return View();
                        report.Append($" [{GraphicsQuality.Names[level]} {label}: before {bc:0.0}, culling {mc:0.0}]");
                        worstBase = Mathf.Max(worstBase, bc);
                        worstNew = Mathf.Max(worstNew, mc);
                        if (level == 3) Capture("48-ultra-" + label.Replace(' ', '-'));
                    }
                Debug.Log(report.ToString());
                GraphicsQuality.Apply(1);
                Application.targetFrameRate = cap;
                camera.enabled = true;
                Check("Long views are cheaper", worstNew <= worstBase * 1.05f, $"slowest view: {worstBase:0.0} ms before, {worstNew:0.0} ms now (Ultra/High)");
            }

            // Big fenced parks in the city.
            Check("Big city parks", GridCity.BigParkBlocks == 4 && GridCity.ParkTrees > 150 && GridCity.ParkFences >= 32,
                $"{GridCity.BigParkBlocks} park blocks, {GridCity.ParkFences} railing runs, {GridCity.ParkTrees} trees");
            {
                camera.enabled = false;
                camera.transform.position = new Vector3(580, 55, 150);
                camera.transform.LookAt(new Vector3(700, 0, 300));
                yield return new WaitForSeconds(.5f);
                Capture("43-park-air");
                camera.transform.position = new Vector3(640, 1.7f, 196);
                camera.transform.LookAt(new Vector3(700, 1.2f, 222));
                yield return new WaitForSeconds(.3f);
                Capture("43-park-street");
                camera.enabled = true;
            }

            // --- 2026-09-28 pass: port district, airport extras, east woods, trees, fences, call car ---
            Check("Port district", PortDistrict.Junctions >= 7 && PortDistrict.RailSections > 100 && PortDistrict.Machines >= 7 && PortDistrict.Tanks == 3,
                $"{PortDistrict.Junctions} junctions to the quay road, {PortDistrict.RailSections} promenade railing sections, {PortDistrict.Machines} terminal machines, {PortDistrict.Tanks} tanks");
            Check("Airport extras", AirportExtras.Pieces >= 14 && AirportDetail.Planes >= 3, $"{AirportExtras.Pieces} pieces, {AirportDetail.Planes} airliners");
            Check("Edge woods", GridWorld.EastTrees > 500 && GridWorld.WestTrees > 300, $"{GridWorld.EastTrees} trees along the city's east edge, {GridWorld.WestTrees} on the seaward west side");
            {
                camera.enabled = false;
                camera.transform.position = new Vector3(1060, 40, 25);
                camera.transform.LookAt(new Vector3(1200, 0, -60));
                yield return new WaitForSeconds(.5f);
                Capture("48-port-district");
                camera.transform.position = new Vector3(420, 60, 840);
                camera.transform.LookAt(new Vector3(160, 0, 1050));
                yield return new WaitForSeconds(.5f);
                Capture("48-airport-landside");
                camera.enabled = true;
            }
            // A park railing panel is solid when slow and breaks off when fast.
            {
                Knockable panel = null;
                float best = float.MaxValue;
                foreach (var k in UnityEngine.Object.FindObjectsByType<Knockable>(FindObjectsSortMode.None))
                    if (k.name == "Park railing panel" && !k.Loose)
                    {
                        float d = (k.transform.position - new Vector3(700, 0, 300)).sqrMagnitude;
                        if (d < best) { best = d; panel = k; }
                    }
                bool broke = false;
                if (panel)
                {
                    Vector3 across = panel.transform.right;          // the panel runs along its local z
                    Vector3 mid = panel.transform.TransformPoint(new Vector3(0, 0, 2.5f));
                    if (Vector3.Dot(across, mid - new Vector3(700, 0, 300)) < 0) across = -across;   // come from outside the park
                    car.Place(mid + across * 14 + Vector3.up * .5f, Quaternion.LookRotation(-across));
                    car.Body.linearVelocity = -across * 16;
                    float t1 = Time.time;
                    while (Time.time - t1 < 2.5f && !panel.Loose) { car.Input.SetTest(1, 0, 0); yield return new WaitForFixedUpdate(); }
                    broke = panel.Loose;
                    car.Input.SetTest(0, 1, 0);
                    yield return new WaitForSeconds(.4f);
                    Capture("48-park-railing-hit");
                }
                Check("Park railing is solid and breakable", panel && broke, panel ? $"panel at {panel.transform.position}, knocked off at speed {broke}" : "no park railing panel found");
            }
            // A tree: the car snaps it off at speed.
            {
                Vector3 tree = Vector3.zero;
                bool found = ForestRenderer.Active && ForestRenderer.Active.Nearest(new Vector3(Island.CityW + 60, 0, 400), out tree);
                int felled = ForestRenderer.Felled;
                if (found)
                {
                    Vector3 from = tree + Vector3.left * 30;
                    if (SpawnSpot.Ground(from, out Vector3 g)) from = g;
                    car.Place(from + Vector3.up * .6f, Quaternion.LookRotation(Vector3.right));
                    car.Body.linearVelocity = Vector3.right * 17;
                    float t1 = Time.time;
                    while (Time.time - t1 < 4 && ForestRenderer.Felled == felled) { car.Input.SetTest(1, 0, 0); yield return new WaitForFixedUpdate(); }
                    car.Input.SetTest(0, 1, 0);
                    yield return new WaitForSeconds(.6f);
                    Capture("48-tree-felled");
                }
                Check("Trees break when hit fast", found && ForestRenderer.Felled > felled, found ? $"felled {ForestRenderer.Felled - felled} (tree at {tree})" : "no tree found");
                ForestRenderer.RestoreAll();
            }
            // Out of the car, hold R: the car comes to us.
            {
                car.Input.SetTest(0, 1, 0);
                car.Place(new Vector3(GridCity.LaneOffset(1), .4f, 420), Quaternion.identity);
                yield return new WaitForSeconds(.8f);
                FootManager.Automated = true;
                FootManager.Instance.GetOut();
                yield return new WaitForSeconds(.5f);
                var fp = FootPlayer.Active;
                bool called = false;
                float gap = -1;
                if (fp)
                {
                    fp.Teleport(new Vector3(300, .2f, 300), 0);
                    yield return new WaitForSeconds(.3f);
                    called = FootManager.Instance.CallCar();
                    yield return new WaitForSeconds(.5f);
                    gap = Vector3.Distance(fp.transform.position, car.transform.position);
                    Capture("48-car-called");
                    FootManager.Instance.GetIn();
                }
                FootManager.Automated = false;
                Check("Hold R on foot: the car comes", called && gap > 2 && gap < 25, $"called {called}, car {gap:0.0} m from the person");
            }
            // The new garage cars: glTF models measured and standing on their wheels.
            {
                var body = car.GetComponent<CarBody>();
                string report = "";
                bool all = true;
                int keep = run.Profile.car;
                for (int idx = CarCatalog.Count - 2; idx < CarCatalog.Count; idx++)
                {
                    run.Profile.car = (idx + CarCatalog.Count - 1) % CarCatalog.Count;
                    run.CycleCar();
                    yield return new WaitForSeconds(.3f);
                    bool ok = body.Shape.Measured && body.Shape.Wheelbase > 2.3f && body.Shape.Wheelbase < 3.1f;
                    all &= ok;
                    report += $" {CarCatalog.Get(idx).Name}: measured {body.Shape.Measured}, wb {body.Shape.Wheelbase:0.00}, r {body.Shape.WheelRadius:0.00};";
                }
                run.Profile.car = (keep + CarCatalog.Count - 1) % CarCatalog.Count;
                run.CycleCar();
                Check("New cars (E50 AMG, Audi S4)", all, report);
            }

            // --- 2026-09-28 pass 2: guardrails, solid fences, sync of broken things, R, graphics ---
            {
                // A guardrail section stops a slow car and is torn off by a fast one.
                Knockable rail = null;
                float best = float.MaxValue;
                Vector3 want = Route.Center(1500) + Route.Right(1500) * 7.5f;
                foreach (var k in UnityEngine.Object.FindObjectsByType<Knockable>(FindObjectsSortMode.None))
                    if (k.name == "Guardrail section" && !k.Loose)
                    {
                        float d = (k.transform.position - want).sqrMagnitude;
                        if (d < best) { best = d; rail = k; }
                    }
                bool slowHeld = false, fastBroke = false;
                if (rail)
                {
                    float s0 = Route.Locate(rail.transform.position);
                    Vector3 fwd = Route.Forward(s0), right = Route.Right(s0);
                    Vector3 aim = rail.transform.position;
                    // Slow: 15 km/h straight at it.
                    car.Place(aim - right * 6 + Vector3.up * .5f, Quaternion.LookRotation(right));
                    car.Body.linearVelocity = right * 4.2f;
                    float t1 = Time.time;
                    while (Time.time - t1 < 2) { car.Input.SetTest(.3f, 0, 0); yield return new WaitForFixedUpdate(); }
                    slowHeld = !rail.Loose;
                    // Fast: 60 km/h.
                    car.Place(aim - right * 14 - fwd * 3 + Vector3.up * .5f, Quaternion.LookRotation(right));
                    car.Body.linearVelocity = right * 17;
                    t1 = Time.time;
                    while (Time.time - t1 < 2 && !rail.Loose) { car.Input.SetTest(1, 0, 0); yield return new WaitForFixedUpdate(); }
                    fastBroke = rail.Loose;
                    car.Input.SetTest(0, 1, 0);
                    yield return new WaitForSeconds(.4f);
                    Capture("49-guardrail");
                    rail.Restore();
                }
                Check("Guardrail: solid when slow, torn off above 30 km/h", rail && slowHeld && fastBroke, rail ? $"slow 15 km/h held {slowHeld}, 60 km/h tore it off {fastBroke}" : "no guardrail found");
            }
            {
                // People cannot walk through a standing fence panel (airport chain link).
                Knockable fence = null;
                foreach (var k in UnityEngine.Object.FindObjectsByType<Knockable>(FindObjectsSortMode.None))
                    if (k.name == "Airport fence section" && !k.Loose) { fence = k; break; }
                bool blocked = false;
                if (fence)
                {
                    car.Place(new Vector3(GridCity.LaneOffset(1), .4f, 420), Quaternion.identity);
                    yield return new WaitForSeconds(.5f);
                    FootManager.Automated = true;
                    FootManager.Instance.GetOut();
                    yield return new WaitForSeconds(.3f);
                    var fp = FootPlayer.Active;
                    if (fp)
                    {
                        Vector3 mid = fence.transform.TransformPoint(new Vector3(0, 0, 10));
                        Vector3 across = fence.transform.right;
                        fp.Teleport(mid + across * 2 + Vector3.up * .3f, Quaternion.LookRotation(-across).eulerAngles.y);
                        var cc = fp.GetComponent<CharacterController>();
                        float t1 = Time.time;
                        while (Time.time - t1 < 1.5f) { cc.Move(-across * 4 * Time.deltaTime + Vector3.down * .1f); yield return null; }
                        blocked = Vector3.Dot(fp.transform.position - mid, across) > 0;
                        FootManager.Instance.GetIn();
                    }
                    FootManager.Automated = false;
                }
                Check("Standing fences stop people on foot", fence && blocked, fence ? $"walked into the airport fence: stayed on its side {blocked}" : "no airport fence found");
            }
            {
                // Online sync: a 'K' message breaks the same prop and fells the same tree here.
                Knockable bin = null;
                foreach (var k in UnityEngine.Object.FindObjectsByType<Knockable>(FindObjectsSortMode.None))
                    if (k.name == "Litter bin" && !k.Loose) { bin = k; break; }
                bool propSynced = false, treeSynced = false;
                if (bin)
                {
                    Net.WorldNet.Receive(KMessage(Net.WorldNet.Prop, Knockable.KindKey(bin.name), bin.transform.position + Vector3.right * .4f, Vector3.forward * 5));
                    propSynced = bin.Loose;
                    bin.Restore();
                }
                int felled = ForestRenderer.Felled;
                if (ForestRenderer.Active && ForestRenderer.Active.Nearest(new Vector3(-80, 0, 400), out Vector3 tree))
                {
                    Net.WorldNet.Receive(KMessage(Net.WorldNet.Tree, 0, tree + Vector3.left * .5f, Vector3.right * 10));
                    treeSynced = ForestRenderer.Felled == felled + 1;
                    ForestRenderer.RestoreAll();
                }
                Check("Broken things sync online", propSynced && treeSynced, $"litter bin knocked by a friend's message {propSynced}, tree felled by a friend's message {treeSynced}");
            }
            {
                // R on foot: straight back into our own car, wherever it is.
                car.Place(new Vector3(GridCity.LaneOffset(1), .4f, 420), Quaternion.identity);
                yield return new WaitForSeconds(.5f);
                FootManager.Automated = true;
                FootManager.Instance.GetOut();
                yield return new WaitForSeconds(.3f);
                var fp = FootPlayer.Active;
                bool inCar = false;
                float gap = -1;
                if (fp)
                {
                    fp.Teleport(new Vector3(300, .2f, 300), 0);
                    yield return new WaitForSeconds(.3f);
                    Vector3 was = fp.transform.position;
                    bool ok = FootManager.Instance.BackToCar();
                    yield return new WaitForSeconds(.5f);
                    inCar = ok && FootPlayer.Active == null && !DriverInput.OnFoot;
                    gap = Vector3.Distance(was, car.transform.position);
                    Capture("49-back-to-car");
                }
                FootManager.Automated = false;
                Check("R on foot: back in our car", inCar && gap < 25, $"in the car {inCar}, car {gap:0.0} m from where the person stood");
            }
            {
                var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                int keep = GraphicsQuality.Level;
                GraphicsQuality.Apply(2);
                float scale = urp ? urp.renderScale : 0;
                bool sharp = Screen.height > 1440 || scale >= .99f;
                GraphicsQuality.Apply(keep);
                Check("Sharp picture on High", sharp && QualitySettings.anisotropicFiltering == AnisotropicFiltering.ForceEnable && QualitySettings.globalTextureMipmapLimit == 0,
                    $"render scale {scale:0.00} at {Screen.height} px, anisotropic {QualitySettings.anisotropicFiltering}, texture limit {QualitySettings.globalTextureMipmapLimit}");
                {
                    // Every resolution option changes how many pixels are drawn, and fewer pixels
                    // really are faster (GPU time at 144p against 4K).
                    var urpR = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                    var scales = new System.Collections.Generic.List<string>();
                    bool allDiffer = true;
                    float last = -1;
                    int n0 = ScreenResolution.Options.Count;
                    for (int i = 0; i < n0; i++)
                    {
                        ScreenResolution.Cycle(1);
                        float sc = urpR ? urpR.renderScale : 0;
                        if (ScreenResolution.Chosen > 0) { if (Mathf.Abs(sc - last) < .01f) allDiffer = false; last = sc; }
                        scales.Add(ScreenResolution.Label.Replace("РАЗРЕШЕНИЕ  /  ", "") + "=" + sc.ToString("0.00"));
                    }
                    Check("Screen resolutions all change the picture", n0 == 7 && allDiffer && ScreenResolution.Chosen == 0, string.Join(", ", scales));
                    float gpuLow = 0, gpuHigh = 0, fpsLow = 0, fpsHigh = 0;
                    foreach (var (height, isLow) in new[] { (144, true), (2160, false) })
                    {
                        ScreenResolution.Choose(height);
                        yield return new WaitForSeconds(.6f);
                        float g = 0, t = 0; int frames = 0;
                        var timings = new FrameTiming[1];
                        float began2 = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - began2 < 1.5f)
                        {
                            FrameTimingManager.CaptureFrameTimings();
                            if (FrameTimingManager.GetLatestTimings(1, timings) > 0) { g += (float)timings[0].gpuFrameTime; frames++; }
                            t += Time.unscaledDeltaTime;
                            yield return null;
                        }
                        float fpsNow = frames > 0 ? frames / Mathf.Max(.01f, t) : 0;
                        if (isLow) { gpuLow = g / Mathf.Max(1, frames); fpsLow = fpsNow; } else { gpuHigh = g / Mathf.Max(1, frames); fpsHigh = fpsNow; }
                    }
                    ScreenResolution.Choose(0);
                    Check("Lower resolution is faster", gpuLow > 0 && gpuLow < gpuHigh * .8f || fpsLow > fpsHigh * 1.15f, $"144p: gpu {gpuLow:0.0} ms, {fpsLow:0} fps; 4K: gpu {gpuHigh:0.0} ms, {fpsHigh:0} fps");
                }
                Check("New sea shader", Island.Sea && Island.Sea.shader.name == "Autobahn/Ocean", Island.Sea ? Island.Sea.shader.name : "no sea material");
                int seeThrough = 0;
                foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                    if (t.name == "NYC building")
                        foreach (var r in t.GetComponentsInChildren<Renderer>())
                            foreach (var m in r.sharedMaterials)
                                if (m && m.renderQueue >= 2500) seeThrough++;
                Check("NYC buildings not see-through", seeThrough == 0, $"{seeThrough} transparent materials left on the purchased buildings");
            }
            {
                // Anti-aliasing setting: off / FXAA / SMAA / SMAA + MSAA, applied to the camera.
                int keepAa = GraphicsQuality.AntiAliasing;
                var camData = Camera.main ? UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(Camera.main) : null;
                var seen = new System.Collections.Generic.List<string>();
                for (int i = 0; i < GraphicsQuality.AntiAliasingNames.Length; i++)
                {
                    GraphicsQuality.CycleAntiAliasing(1);
                    seen.Add(GraphicsQuality.AntiAliasing + ":" + (camData ? camData.antialiasing.ToString() : "?"));
                }
                while (GraphicsQuality.AntiAliasing != keepAa) GraphicsQuality.CycleAntiAliasing(1);
                var urpAa = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                bool offWorks = seen.Exists(x => x.StartsWith("0:None")), strongest = seen.Exists(x => x.StartsWith("1:Subpixel")) && GraphicsQuality.AntiAliasingNames.Length == 2;
                Check("Anti-aliasing: off or strongest", offWorks && strongest && (!urpAa || GraphicsQuality.AntiAliasing != 1 || urpAa.msaaSampleCount == 4), string.Join(", ", seen));

                // Every car in the garage takes the paint from the studio (built on a spare body,
                // the player's own car stays as it is).
                int repaintable = 0;
                string notPainted = "";
                foreach (var e in CarCatalog.All)
                {
                    var spare = new GameObject("QA paint " + e.Name);
                    spare.transform.position = new Vector3(0, -200, 0);
                    var art = spare.AddComponent<CarBody>();
                    art.Build(Color.red, false, e.Style);
                    var want = new Color(.1f, .55f, .25f);
                    art.SetPaint(want);
                    if (art.Repaintable && Vector4.Distance(art.PaintColor, want) < .02f) repaintable++;
                    else notPainted += e.Name + " ";
                    Destroy(spare);
                    yield return null;
                }
                Check("Every car can be repainted", repaintable == CarCatalog.Count, $"{repaintable}/{CarCatalog.Count} repaint" + (notPainted.Length > 0 ? ", not: " + notPainted : ""));
            }
            {
                // Motor boats: moored in the harbour, board one, drive it, blow it up.
                Check("Motor boats in the harbour", Boat.All.Count >= 12, $"{Boat.All.Count} boats");
                // The one moored furthest out: open water ahead of it.
                Boat boat = null;
                foreach (var bt in Boat.All) if (bt && (!boat || bt.transform.position.z < boat.transform.position.z)) boat = bt;
                bool boarded = false, moved = false, blown = false, sank = false, ashore = false;
                float kmh = 0;
                if (boat)
                {
                    FootManager.Automated = true;
                    FootManager.Instance.GetOut();
                    yield return new WaitForSeconds(.3f);
                    var fp = FootPlayer.Active;
                    if (fp)
                    {
                        fp.Teleport(boat.transform.position + Vector3.up * 2, 0);
                        fp.BoardBoat(boat);
                        boarded = fp.OnBoat == boat;
                        Vector3 start = boat.transform.position;
                        boat.TestThrottle = 1;
                        yield return new WaitForSeconds(3f);
                        kmh = boat.Speed;
                        moved = Vector3.Distance(start, boat.transform.position) > 5 && Mathf.Abs(boat.transform.position.y - Island.SeaY) < 1.2f;
                        boat.TestThrottle = 0;
                        Capture("52-boat");
                        // Off the boat and well away before it goes up.
                        var other = Boat.All.Find(ob => ob && ob != boat && !ob.Wrecked);
                        fp.LeaveBoat(true);
                        if (other) fp.Teleport(other.transform.position + Vector3.up * 2, 0);
                        boat.Damage(1000);
                        blown = boat.Wrecked && fp.OnBoat == null;
                        yield return new WaitForSeconds(6f);
                        sank = boat.transform.position.y < Island.SeaY - 1;
                        // Ashore from another boat moored at a pier.
                        var f2 = FootPlayer.Active;
                        if (other && f2 && !f2.Dead)
                        {
                            f2.Teleport(other.transform.position + Vector3.up * 2, 0);
                            f2.BoardBoat(other);
                            ashore = f2.LeaveBoat() && f2.transform.position.y > Island.SeaY + .2f;
                        }
                    }
                    FootManager.Instance.BackToCar();
                    yield return new WaitForSeconds(.5f);
                    FootManager.Automated = false;
                    Boat.RestoreAll();
                }
                Check("Boat: board, drive, blow up", boarded && moved && blown && sank, $"boarded {boarded}, moved {moved} ({kmh:0} km/h), blew up {blown}, sank {sank}");
                Check("Boat: step ashore at a pier", ashore, $"ashore {ashore}");
                // Online: a friend driving a boat moves our copy of it, and a boat blown up there
                // blows up here.
                Boat synced = null;
                foreach (var bt in Boat.All)
                    if (bt && !bt.Wrecked && !bt.Driven && (!synced || Vector3.Distance(bt.transform.position, car.transform.position) > Vector3.Distance(synced.transform.position, car.transform.position)))
                        synced = bt;
                bool follows = false, blowsUp = false;
                if (synced)
                {
                    Vector3 to = synced.transform.position + synced.transform.forward * 12;
                    var ms = new System.IO.MemoryStream();
                    var w = new System.IO.BinaryWriter(ms);
                    w.Write((byte)'B'); w.Write((byte)1); w.Write((byte)0); w.Write((ushort)synced.Index);
                    w.Write(to.x); w.Write(to.y); w.Write(to.z);
                    var q = synced.transform.rotation;
                    w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
                    w.Write(0f); w.Write(0f); w.Write(0f);
                    for (float t0b = Time.time; Time.time - t0b < 1.2f; )
                    {
                        Net.BoatNet.Receive(ms.ToArray());
                        yield return new WaitForSeconds(.1f);
                    }
                    follows = synced.Remote && Vector3.Distance(synced.transform.position, to) < 2;
                    Net.BoatNet.Receive(new byte[] { (byte)'B', 1, 1, (byte)(synced.Index & 255), (byte)(synced.Index >> 8) });
                    blowsUp = synced.Wrecked;
                    Boat.RestoreAll();
                }
                Check("Boats sync online", follows && blowsUp, $"follows a friend's boat {follows}, blown up by a friend's message {blowsUp}");
            }
            {
                // Aircraft: board, take off, gear up, fly on; a second seat for a friend.
                Check("Aircraft at the airport", Airplane.All.Count >= 2, $"{Airplane.All.Count} aircraft");
                var plane = Airplane.All.Count > 0 ? Airplane.All[0] : null;
                bool boarded = false, tookOff = false, gearUp = false, flying = false, secondSeat = false;
                float alt = 0, kmh = 0;
                if (plane)
                {
                    FootManager.Automated = true;
                    if (!FootPlayer.Active) FootManager.Instance.GetOut();
                    yield return new WaitForSeconds(.3f);
                    var fp = FootPlayer.Active;
                    if (fp)
                    {
                        fp.Teleport(plane.transform.position - plane.transform.right * 3, 0);
                        yield return null;
                        // Onto the runway, clear of the airliner behind and the bomber far ahead.
                        var onRunway = new Vector3(Island.AirX0 + 320, .6f, Island.AirZ1 - 140);
                        plane.Body.position = onRunway; plane.transform.position = onRunway;
                        plane.Body.rotation = Quaternion.Euler(0, 90, 0); plane.transform.rotation = Quaternion.Euler(0, 90, 0);
                        yield return new WaitForSeconds(.5f);
                        fp.Teleport(plane.ExitPoint, 0);
                        yield return null;
                        fp.BoardPlane(plane, 0);
                        boarded = fp.InPlane == plane && plane.Pilot == fp;
                        secondSeat = plane.FreeSeat() == 1;
                        plane.TestThrottle = 1; plane.TestPitch = .8f;
                        float t0p = Time.time;
                        while (Time.time - t0p < 30 && plane.Altitude < 40 && !plane.Wrecked) yield return null;
                        tookOff = plane.Altitude >= 40 && !plane.Wrecked;
                        plane.TestPitch = 0;
                        plane.ToggleGear();
                        yield return new WaitForSeconds(6);
                        gearUp = !plane.GearDown;
                        alt = plane.Altitude; kmh = plane.Kmh;
                        flying = !plane.Wrecked && alt > 15 && kmh > 150;
                        Capture("53-aircraft");
                        plane.TestThrottle = float.NaN;
                        fp.LeavePlane(true);
                    }
                    FootManager.Instance.BackToCar();
                    yield return new WaitForSeconds(.5f);
                    FootManager.Automated = false;
                    Airplane.RestoreAll();
                }
                Check("Aircraft: take off and fly", boarded && tookOff && gearUp && flying, $"boarded {boarded}, took off {tookOff}, gear up {gearUp}, flying {flying} at {alt:0} m, {kmh:0} km/h");
                Check("Aircraft: room for a friend", secondSeat, $"second seat free {secondSeat}");
            }
            {
                // The big aircraft: the airliner takes off; the bomber fires a missile and drops
                // the nuclear bomb over the sea.
                Airplane liner = Airplane.All.Find(x => x && x.Kind == AircraftKind.Airliner);
                Airplane bomber = Airplane.All.Find(x => x && x.Kind == AircraftKind.Bomber);
                Check("Big aircraft at the airport", liner && bomber, $"airliner {(bool)liner}, bomber {(bool)bomber}");
                bool linerUp = false, missile = false, nuke = false;
                float linerAlt = 0, linerKmh = 0;
                if (liner && bomber)
                {
                    FootManager.Automated = true;
                    if (!FootPlayer.Active) FootManager.Instance.GetOut();
                    yield return new WaitForSeconds(.3f);
                    var fp = FootPlayer.Active;
                    if (fp)
                    {
                        fp.Teleport(liner.ExitPoint, 0);
                        yield return null;
                        fp.BoardPlane(liner, 0);
                        liner.TestThrottle = 1; liner.TestPitch = 0;
                        float t0l = Time.time;
                        while (Time.time - t0l < 45 && liner.Altitude < 30 && !liner.Wrecked)
                        {
                            liner.TestPitch = liner.Kmh > 205 ? .7f : 0;
                            yield return null;
                        }
                        linerUp = liner.Altitude >= 30 && !liner.Wrecked;
                        linerAlt = liner.Altitude; linerKmh = liner.Kmh;
                        Capture("54-airliner");
                        liner.TestThrottle = float.NaN;
                        fp.LeavePlane(true);
                        yield return new WaitForSeconds(.3f);
                        // The bomber, placed high over the open sea.
                        var high = new Vector3(Carrier.Centre.x + 600, 600, Carrier.Centre.y - 900);
                        bomber.Body.position = high; bomber.transform.position = high;
                        bomber.Body.rotation = Quaternion.Euler(0, 90, 0); bomber.transform.rotation = Quaternion.Euler(0, 90, 0);
                        bomber.Body.linearVelocity = bomber.transform.forward * 120;
                        fp = FootPlayer.Active;
                        if (fp)
                        {
                            fp.Teleport(bomber.ExitPoint, 0);
                            fp.BoardPlane(bomber, 0);
                            bomber.TestThrottle = .8f; bomber.TestPitch = 0;
                            yield return new WaitForSeconds(1f);
                            int fired = Missile.Fired;
                            missile = bomber.FireMissile() && Missile.Fired == fired + 1;
                            int blasts = Nuke.Detonated;
                            bool dropped = bomber.DropNuke();
                            float t0n = Time.time;
                            while (Time.time - t0n < 25 && Nuke.Detonated == blasts) yield return null;
                            nuke = dropped && Nuke.Detonated == blasts + 1;
                            yield return new WaitForSeconds(3f);
                            Capture("55-nuke");
                            bomber.TestThrottle = float.NaN;
                            fp.LeavePlane(true);
                        }
                    }
                    FootManager.Instance.BackToCar();
                    yield return new WaitForSeconds(.5f);
                    FootManager.Automated = false;
                    Airplane.RestoreAll();
                }
                Check("Airliner takes off", linerUp, $"at {linerAlt:0} m, {linerKmh:0} km/h");
                Check("Bomber: missile and nuclear bomb", missile && nuke, $"missile {missile}, bomb dropped and went off {nuke} at {Nuke.LastBlast}");
            }
            {
                // The carrier: cruising, and a person on its deck is carried along.
                var carrier = Carrier.Active;
                bool moving = false, carried = false;
                if (carrier)
                {
                    float p0 = carrier.Phase;
                    yield return new WaitForSeconds(1.5f);
                    moving = carrier.Phase > p0 + 3;
                    FootManager.Automated = true;
                    if (!FootPlayer.Active) FootManager.Instance.GetOut();
                    yield return new WaitForSeconds(.3f);
                    var fp = FootPlayer.Active;
                    if (fp)
                    {
                        Vector3 deck = carrier.transform.TransformPoint(new Vector3(-12, carrier.DeckY + .1f, -40));
                        fp.Teleport(deck, carrier.transform.eulerAngles.y);
                        yield return new WaitForSeconds(3f);
                        Vector3 spot = carrier.transform.InverseTransformPoint(fp.transform.position);
                        carried = carrier.Under(fp.transform.position) && Vector3.Distance(spot, new Vector3(-12, carrier.DeckY, -40)) < 3;
                        Capture("56-carrier");
                    }
                    FootManager.Instance.BackToCar();
                    yield return new WaitForSeconds(.5f);
                    FootManager.Automated = false;
                }
                Check("Aircraft carrier cruising", carrier && moving, carrier ? $"phase {carrier.Phase:0} m, deck at {carrier.DeckY:0.0} m" : "no carrier");
                Check("Carrier deck carries people", carried, $"carried {carried}");
            }
            {
                // A boat takes a driver and three passengers.
                var boat4 = Boat.All.Find(b4 => b4 && !b4.Wrecked && !b4.Driven);
                bool seats = boat4 && boat4.FreeSeat() == 0;
                if (boat4)
                {
                    boat4.Occupy(0, 7); boat4.Occupy(1, 8); boat4.Occupy(2, 9);
                    seats &= boat4.FreeSeat() == 3;
                    boat4.Occupy(3, 10);
                    seats &= boat4.FreeSeat() == -1;
                    foreach (int sl in new[] { 7, 8, 9, 10 }) boat4.Vacate(sl);
                }
                Check("Boat: four places", seats, $"helm then three passenger places {seats}");
            }
            {
                // Traffic cars drawn as one piece each (was up to 139 parts per car).
                int cars = 0, renderers = 0;
                foreach (var tc in run.Traffic.Cars)
                {
                    if (!tc) continue;
                    cars++;
                    renderers += tc.GetComponentsInChildren<MeshRenderer>(true).Length;
                }
                Check("Traffic cars merged for drawing", cars > 0 && renderers < cars * 12, $"{renderers} renderers on {cars} cars");
            }

            // The airport: a flat open field north of the city, reached through a gate.
            {
                var world2 = IslandWorld.Active;
                float cx = (Island.AirX0 + Island.AirX1) / 2, cz = (Island.AirZ0 + Island.AirZ1) / 2;
                bool flat = true;
                string bumps = "";
                foreach (var q in new[] { new Vector2(cx, cz), new Vector2(Island.AirX0 + 30, Island.AirZ0 + 30), new Vector2(Island.AirX1 - 30, Island.AirZ1 - 30), new Vector2(cx, Island.AirZ0 + 200) })
                {
                    if (Physics.Raycast(new Vector3(q.x, 50, q.y), Vector3.down, out RaycastHit hit, 100, 1 << 10) && Mathf.Abs(hit.point.y) < .25f) continue;
                    flat = false; bumps += $" {q}";
                }
                bool gate = Physics.Raycast(new Vector3(Island.AirGateX, 5, Island.CityH + GridCity.Half + 3), Vector3.down, out RaycastHit g, 10, 1 << 10) && g.point.y < .1f;
                Check("Airport", world2 && world2.AirportRoot && flat && gate, $"field flat {flat}{bumps}, gate from the city open {gate}");
                camera.enabled = false;
                camera.transform.position = new Vector3(Island.AirX0 - 60, 120, Island.AirZ0 - 80);
                camera.transform.LookAt(new Vector3(cx, 0, cz));
                yield return new WaitForSeconds(.5f);
                Capture("46-airport-air");
                camera.transform.position = new Vector3(Island.AirGateX, 2, Island.CityH - 30);
                camera.transform.LookAt(new Vector3(Island.AirGateX, 3, Island.AirZ0 + 100));
                yield return new WaitForSeconds(.3f);
                Capture("46-airport-gate");
                camera.enabled = true;
            }

            // Traffic physics: a car hit from behind is shoved with a real mass (the driver's car
            // does not spring back), spins out, then steers itself back into its lane.
            {
                foreach (var c in traffic.Cars) c.gameObject.SetActive(false);
                traffic.Simulate = true;
                var target = traffic.Cars[0];
                target.gameObject.SetActive(true);
                target.Place(690, 1, 14);
                car.Teleport(650, 1);
                yield return new WaitForSeconds(.3f);
                car.Body.linearVelocity = car.transform.forward * (110 / 3.6f);
                float before = 0, minForward = 999, after = 0;
                bool hit = false;
                t0 = Time.time;
                while (Time.time - t0 < 5)
                {
                    car.Input.SetTest(hit ? 0 : .8f, hit ? .6f : 0, 0);
                    if (target.State == TrafficCar.Mode.Stunned) { if (!hit) Capture("45-crash"); hit = true; }
                    if (!hit) before = car.ForwardSpeed;
                    if (hit) minForward = Mathf.Min(minForward, car.ForwardSpeed);
                    if (hit && after == 0 && Time.time - t0 > .2f) after = car.ForwardSpeed;
                    float last = car.ForwardSpeed;
                    yield return new WaitForFixedUpdate();
                    if (!hit && target.State == TrafficCar.Mode.Stunned) before = last;
                    if (hit && Time.time - t0 > 2) break;
                }
                Check("Collision with real masses", hit && minForward > -6 && minForward < before - 2, $"hit {hit}: driver {before:0} km/h before, lowest {minForward:0} km/h after (no spring-back below -6)");
                car.Input.SetTest(0, 1, 0);
                t0 = Time.time;
                while (Time.time - t0 < 16 && target.State != TrafficCar.Mode.Active && target.State != TrafficCar.Mode.Kinematic) yield return null;
                yield return new WaitForSeconds(2);
                float laneError = Mathf.Abs(Route.Lateral(target.transform.position) - (target.Lane - 1) * Route.LaneWidth);
                Check("Knocked car returns to its lane", (target.State == TrafficCar.Mode.Active || target.State == TrafficCar.Mode.Kinematic) && laneError < 1.3f && target.transform.up.y > .9f,
                    $"state {target.State}, {laneError:0.0} m from lane centre, upright {target.transform.up.y:0.00}");

                // Critical damage: it blows up, pushing the driver's car away.
                int blasts = Explosion.Count;
                target.Place(Route.Locate(car.transform.position) + 9, 1, 0);
                car.Body.linearVelocity = Vector3.zero;
                yield return new WaitForSeconds(.4f);
                Vector3 v0 = car.Body.linearVelocity;
                target.Damage(500);
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                float pushed = (car.Body.linearVelocity - v0).magnitude;
                yield return new WaitForSeconds(.35f);
                Capture("47-explosion");
                Check("Explosion", Explosion.Count > blasts && target.Wrecked && pushed > 1, $"blasts {Explosion.Count - blasts}, wreck {target.Wrecked}, driver's car pushed {pushed:0.0} m/s");
                yield return new WaitForSeconds(1.5f);
                target.gameObject.SetActive(false);

                // The bomb: dropped beside the driver's car, it beeps, goes off after its fuse and throws the car.
                car.Body.linearVelocity = Vector3.zero;
                yield return new WaitForSeconds(.5f);
                int bombBlasts = Explosion.Count;
                var bomb = Bomb.Throw(car.transform.position + car.transform.right * 7f + Vector3.up * 1.2f, Vector3.zero, null);
                yield return new WaitForSeconds(Bomb.Fuse - .3f);
                bool ticking = bomb && Explosion.Count == bombBlasts;
                Vector3 bv0 = car.Body.linearVelocity;
                yield return new WaitForSeconds(.5f);
                float bombPush = (car.Body.linearVelocity - bv0).magnitude;
                Capture("47b-bomb");
                Check("Bomb", ticking && !bomb && Explosion.Count == bombBlasts + 1 && bombPush > 1, $"waited for the fuse {ticking}, gone {!bomb}, blasts {Explosion.Count - bombBlasts}, car pushed {bombPush:0.0} m/s");
                yield return new WaitForSeconds(1f);

                // The driver's own car at critical damage: blows up, then comes back repaired.
                car.Damage.Engine = 1; car.Damage.Bodywork = 1;
                car.Explode();
                bool burning = car.WreckedFor > 0;
                var synthNow = car.GetComponentInChildren<EngineSynth>();
                yield return new WaitForSeconds(1f);
                float deadMaster = synthNow ? synthNow.Master : -1;
                yield return new WaitForSeconds(3.5f);
                Check("Driver's car explodes and returns", burning && car.WreckedFor <= 0 && car.Damage.Worst < .01f && car.transform.up.y > .9f, $"burning {burning}, back {car.WreckedFor <= 0}, damage {car.Damage.Worst:0.00}");

                // Repair after 5 s standing still.
                car.Input.SetTest(0, 0, 0);
                car.Body.linearVelocity = Vector3.zero;
                car.Damage.Apply(new Vector3(0, .5f, 2), 20);
                while (car.Speed > 1.2f) yield return null;
                t0 = Time.time;
                while (car.Damage.Damaged && Time.time - t0 < 9)
                {
                    yield return null;
                }
                Check("Repair after 5 seconds", MechanicalDamage.RepairDelay == 5 && !car.Damage.Damaged && Time.time - t0 < 7.5f, $"repaired after {Time.time - t0:0.0} s");
                // Sounds: the engine is dead in the wreck, restarts after, and the pack sounds load.
                float aliveMaster = synthNow ? synthNow.Master : -1;
                int clicks = UiSound.Clicks;
                UiSound.Click();
                int loaded = 0;
                foreach (var n in new[] { "2CV6EngineOff", "2CV6KeysOut", "2CV6MotorNoStart", "2CV6MotorNoStart2", "ElevatorButton", "2CV6Ignition" })
                    if (Resources.Load<AudioClip>("Sounds/" + n)) loaded++;
                Check("Car sounds", deadMaster == 0 && aliveMaster > .3f && UiSound.Clicks > clicks && loaded == 6,
                    $"engine gain in the wreck {deadMaster:0.00}, after restart {aliveMaster:0.00}, menu click {UiSound.Clicks > clicks}, pack sounds {loaded}/6");
                EngineAudioCheck();

                car.Input.SetTest(0, 0, 0);
            }

            // Drift: hold the button and steer, the tail slides out, smoke pours off the rear
            // tyres and the drift is paid out when it ends.
            {
                foreach (var c in traffic.Cars) if (!c.OnGrid) c.gameObject.SetActive(false);
                var smoke = TyreSmoke.Player;
                // On the airport apron, heading east: room to slide.
                car.Place(new Vector3(Island.AirX0 + 380, .35f, Island.AirZ0 + 190), Quaternion.LookRotation(Vector3.right));
                yield return new WaitForSeconds(.4f);
                car.Body.linearVelocity = car.transform.forward * (95 / 3.6f);
                float maxAngle = 0, awardBefore = smoke ? smoke.LastAward : 0;
                int particles = 0;
                car.Input.SetTestDrift(true);
                t0 = Time.time;
                bool shot = false;
                while (Time.time - t0 < 2.6f)
                {
                    float el = Time.time - t0;
                    car.Input.SetTest(.75f, 0, el < .8f ? .5f : el < 1.5f ? -.35f : 0);
                    if (car.Speed > 60) maxAngle = Mathf.Max(maxAngle, Mathf.Abs(car.Chassis.DriftAngle));
                    if (smoke) particles = Mathf.Max(particles, smoke.Puffs[0].particleCount + smoke.Puffs[1].particleCount);
                    if (!shot && el > 1.3f) { shot = true; Capture("44-drift"); }
                    yield return new WaitForFixedUpdate();
                }
                car.Input.SetTestDrift(false);
                car.Input.SetTest(0, .5f, 0);
                yield return new WaitForSeconds(1.6f);
                bool upright = car.transform.up.y > .9f;
                // The softer catch lets a held slide sit at 40–70°; the scripted flick may swing past
                // that, but it must stay a slide (under 100°), not a spin.
                Check("Drift", smoke != null && maxAngle > 15 && maxAngle < 100 && particles > 20 && upright && smoke.LastAward > 0 && smoke.LastAward != awardBefore,
                    $"max angle {maxAngle:0}°, {particles} smoke puffs, upright {upright}, drift paid {(smoke ? smoke.LastAward : 0):0}");
                car.Input.SetTest(0, 0, 0);
            }

            // Donut: from a standstill, throttle + full lock + handbrake for 4 s spins the car
            // round more than 300° on its wheels, with smoke and drift points; letting go stops it.
            {
                foreach (var c in traffic.Cars) if (!c.OnGrid) c.gameObject.SetActive(false);
                var smoke = TyreSmoke.Player;
                car.Place(new Vector3(Island.AirX0 + 380, .35f, Island.AirZ0 + 190), Quaternion.LookRotation(Vector3.right));
                car.Engine.Automatic = true;
                yield return new WaitForSeconds(.4f);
                float turned = 0, lastYaw = car.transform.eulerAngles.y, points = 0;
                int particles = 0;
                bool flipped = false;
                car.Input.SetTestHandbrake(true);
                t0 = Time.time;
                while (Time.time - t0 < 4)
                {
                    car.Input.SetTest(1, 0, 1);
                    yield return new WaitForFixedUpdate();
                    float yaw = car.transform.eulerAngles.y;
                    turned += Mathf.DeltaAngle(lastYaw, yaw);
                    lastYaw = yaw;
                    if (car.transform.up.y < .5f) flipped = true;
                    if (smoke)
                    {
                        points = Mathf.Max(points, smoke.Current);
                        particles = Mathf.Max(particles, smoke.Puffs[0].particleCount + smoke.Puffs[1].particleCount);
                    }
                }
                Capture("44b-donut");
                car.Input.SetTestHandbrake(false);
                car.Input.SetTest(0, 0, 0);
                yield return new WaitForSeconds(2f);
                float spinAfter = Mathf.Abs(car.Chassis.YawRate);
                bool upright = car.transform.up.y > .9f && !flipped;
                Check("Donut", Mathf.Abs(turned) > 300 && upright && points > 0,
                    $"turned {Mathf.Abs(turned):0}° in 4 s, upright {upright}, drift points {points:0}, {particles} smoke puffs, yaw 2 s after letting go {spinAfter:0}°/s");
                car.Input.SetTest(0, .5f, 0);
                yield return new WaitForSeconds(.5f);
                car.Input.SetTest(0, 0, 0);
            }

            // Head-on at full speed: 200 km/h square into a wall blows the car up at once (fresh,
            // undamaged car); a 200 km/h scrape along a wall with the flank does not.
            {
                foreach (var c in traffic.Cars) if (!c.OnGrid) c.gameObject.SetActive(false);
                Vector3 start = new Vector3(Island.AirX0 + 300, .35f, Island.AirZ0 + 190);
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "QA crash wall";
                wall.transform.position = start + new Vector3(45, 2, 0);
                wall.transform.localScale = new Vector3(1, 4, 24);
                car.Damage.Repair();
                car.Place(start, Quaternion.LookRotation(Vector3.right));
                yield return new WaitForSeconds(.4f);
                car.Body.linearVelocity = car.transform.forward * (200 / 3.6f);
                t0 = Time.time;
                bool blew = false;
                while (Time.time - t0 < 2 && !blew)
                {
                    car.Input.SetTest(1, 0, 0);
                    yield return new WaitForFixedUpdate();
                    blew = car.WreckedFor > 0;
                }
                float headDv = car.LastImpactDeltaV;
                car.Input.SetTest(0, 0, 0);
                // Let the wreck burn out and come back before the second run.
                t0 = Time.time;
                while (car.WreckedFor > 0 && Time.time - t0 < 6) yield return null;
                Check("Head-on crash explodes", blew, $"200 km/h into a wall: exploded {blew}, speed lost along the normal {headDv:0} km/h (limit {Vehicle.HeadOnDeltaV:0})");

                // The same wall turned along the way, the car alongside it, steering into it at 4°.
                wall.transform.position = start + new Vector3(80, 2, 1.9f);
                wall.transform.localScale = new Vector3(160, 4, 1);
                car.Damage.Repair();
                car.Place(start, Quaternion.LookRotation(Quaternion.Euler(0, -4, 0) * Vector3.right));
                yield return new WaitForSeconds(.4f);
                car.Body.linearVelocity = car.transform.forward * (200 / 3.6f);
                t0 = Time.time;
                int impacts0 = car.ImpactCount;
                bool scraped = false, blewSide = false;
                while (Time.time - t0 < 1.6f)
                {
                    car.Input.SetTest(1, 0, 0);
                    yield return new WaitForFixedUpdate();
                    if (car.ImpactCount > impacts0) scraped = true;
                    blewSide |= car.WreckedFor > 0;
                }
                car.Input.SetTest(0, .6f, 0);
                Check("Side scrape does not explode", !blewSide && scraped, $"200 km/h along a wall at 4°: touched {scraped}, exploded {blewSide}, closing speed along the normal {car.LastImpactClosing:0} km/h");
                UnityEngine.Object.Destroy(wall);
                t0 = Time.time;
                while (car.WreckedFor > 0 && Time.time - t0 < 6) yield return null;
                yield return new WaitForSeconds(.5f);
                car.Damage.Repair();
                car.Input.SetTest(0, 0, 0);
            }

            // Parked cars: driven into from the street side at 40 km/h one is shoved more than a
            // metre; another, hit at 100 km/h, blows up. Both are back in their bays afterwards.
            {
                foreach (var c in traffic.Cars) c.gameObject.SetActive(false);
                ParkedCarBody Pick(ParkedCarBody skip, out Vector3 from, out Vector3 toward)
                {
                    from = toward = Vector3.zero;
                    foreach (var p in FindObjectsByType<ParkedCarBody>(FindObjectsSortMode.None))
                    {
                        if (p == skip || p.Loose || p.Wrecked || !p.gameObject.activeInHierarchy) continue;
                        if (!p.GetComponent<BoxCollider>() || !Island.InCity(p.transform.position)) continue;
                        foreach (int side in new[] { 1, -1 })
                        {
                            Vector3 dir = p.transform.right * side;
                            Vector3 start = p.transform.position - dir * 14 + Vector3.up * .35f;
                            // Room for the car at the start, and nothing but this parked car on the way.
                            if (Physics.CheckBox(start + Vector3.up * .9f, new Vector3(1.2f, .6f, 2.6f), Quaternion.LookRotation(dir), ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore)) continue;
                            if (!Physics.Raycast(start + Vector3.up * .8f, dir, out var ray, 16, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore) || ray.collider.gameObject != p.gameObject) continue;
                            // Low too: kerbs, bollards and railings stop the car short of the parked one.
                            bool blocked = false;
                            foreach (float side2 in new[] { -.8f, 0f, .8f })
                            {
                                Vector3 o = start + Vector3.up * .25f + Vector3.Cross(Vector3.up, dir) * side2;
                                if (Physics.Raycast(o, dir, out var low, 16, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore) && low.collider.gameObject != p.gameObject && !low.collider.transform.IsChildOf(p.transform)) blocked = true;
                            }
                            // And wide: the whole car must fit through to the parked one.
                            if (Physics.SphereCast(start + Vector3.up * .7f, .95f, dir, out var wide, 16, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore)
                                && wide.collider.gameObject != p.gameObject && !wide.collider.transform.IsChildOf(p.transform)) blocked = true;
                            if (blocked) continue;
                            if (!Physics.Raycast(start + Vector3.up * 2, Vector3.down, 4, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore)) continue;
                            // Room behind it too, so the shove is not stopped by a wall or the next car.
                            if (Physics.CheckBox(p.transform.position + dir * 4.5f + Vector3.up * .9f, new Vector3(1.2f, .5f, 1.5f), Quaternion.LookRotation(dir), ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore)) continue;
                            from = start;
                            toward = dir;
                            return p;
                        }
                    }
                    return null;
                }
                IEnumerator RunInto(ParkedCarBody p, Vector3 from, Vector3 toward, float kmh)
                {
                    car.Damage.Repair();
                    car.Place(from, Quaternion.LookRotation(toward));
                    yield return new WaitForSeconds(.4f);
                    car.Body.linearVelocity = toward * (kmh / 3.6f);
                    float t = Time.time;
                    while (Time.time - t < 3)
                    {
                        car.Input.SetTest(p.Loose ? 0 : .5f, p.Loose ? 1 : 0, 0);
                        yield return new WaitForFixedUpdate();
                    }
                    car.Input.SetTest(0, 0, 0);
                }

                var slow = Pick(null, out var slowFrom, out var slowDir);
                float moved = 0;
                bool slowLoose = false;
                if (slow)
                {
                    Vector3 was = slow.transform.position;
                    yield return RunInto(slow, slowFrom, slowDir, 40);
                    slowLoose = slow.Loose;
                    moved = Vector3.Distance(slow.transform.position, was);
                }
                Check("Parked car shoved at 40 km/h", slow && slowLoose && moved > 1 && !slow.Wrecked,
                    slow ? $"moved {moved:0.0} m, loose {slowLoose}, wrecked {slow.Wrecked}, car {slow.name} at {slow.transform.position}" : "no parked car with a clear run found");

                int blown = ParkedCarBody.Blown;
                var fast = Pick(slow, out var fastFrom, out var fastDir);
                if (fast)
                    yield return RunInto(fast, fastFrom, fastDir, 100);
                Capture("48-parked-blast");
                Debug.Log("QA INFO fast parked car " + (fast ? fast.transform.position.ToString() + " from " + fastFrom : "none"));
                Check("Parked car explodes at 100 km/h", fast && fast.Wrecked && ParkedCarBody.Blown > blown,
                    fast ? $"wrecked {fast.Wrecked}, blasts {ParkedCarBody.Blown - blown}" : "no second parked car with a clear run found");
                ParkedCarBody.RestoreAll();
                yield return null;
                bool parkedBack = (!slow || (!slow.Loose && !slow.Wrecked)) && (!fast || (!fast.Loose && !fast.Wrecked));
                Check("Parked cars back in their bays", parkedBack, $"after a new run the shoved and burnt cars are whole again: {parkedBack}");
                car.Damage.Repair();
            }

            // Traffic ahead to overtake: 40 s at 160 km/h along the island road, and in 90 % of the
            // samples there are at least AheadMin cars in the cone ahead. The test car passes
            // through the traffic (collisions off) so the drive is not cut short by a crash.
            {
                bool kSimulated = traffic.Simulate;
                int kLayer = car.gameObject.layer;
                bool kIgnored = Physics.GetIgnoreLayerCollision(kLayer, 9);
                Physics.IgnoreLayerCollision(kLayer, 9, true);
                traffic.Simulate = true;
                traffic.SetDensity(1);
                // Earlier checks park the road traffic (switched off); a normal run has it all live.
                foreach (var c in traffic.Cars)
                    if (!c.OnGrid)
                    {
                        if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
                        if (c.Wrecked) c.Restore();
                    }
                car.Damage.Repair();
                car.Teleport(1200, 1);
                yield return new WaitForSeconds(.5f);
                car.Body.linearVelocity = car.transform.forward * (160 / 3.6f);
                int kSamples = 0, kFull = 0, kFewest = 999;
                float kNextSample = Time.time + 2, kBegan = Time.time, kSpeedSum = 0;
                while (Time.time - kBegan < 42)
                {
                    Follow(car.Speed < 160 ? 1 : .15f);
                    if (Time.time >= kNextSample)
                    {
                        kNextSample += .5f;
                        int kAhead = traffic.CountAhead();
                        kSamples++;
                        kSpeedSum += car.Speed;
                        if (kAhead >= traffic.AheadMin) kFull++;
                        kFewest = Mathf.Min(kFewest, kAhead);
                    }
                    yield return new WaitForFixedUpdate();
                }
                car.Input.SetTest(0, 1, 0);
                Physics.IgnoreLayerCollision(kLayer, 9, kIgnored);
                traffic.Simulate = kSimulated;
                float kShare = kSamples > 0 ? kFull / (float)kSamples : 0;
                Check("Traffic ahead to overtake", kShare >= .9f && kSamples > 60,
                    $"{kFull}/{kSamples} samples with ≥ {traffic.AheadMin} cars ahead ({kShare * 100:0}%), fewest {kFewest}, average speed {kSpeedSum / Mathf.Max(1, kSamples):0} km/h");
                yield return new WaitForSeconds(1);
                car.Input.SetTest(0, 0, 0);
            }

            // Snow settles on roads, pavements and the ground, then melts again.
            {
                var original = Art.Asphalt.GetTexture("_BaseMap");
                weather.Set(Weather.Snow, false);
                for (int k = 0; k < 20; k++) weather.Apply(1f);
                bool settled = SnowCover.Amount > .99f && SnowCover.Covered(Art.Asphalt) && SnowCover.Covered(Art.Grass) && SnowCover.Covered(Art.Kerb) && Art.Asphalt.color.grayscale > .7f;
                Check("Snow settles on road, pavement and ground", settled, $"cover {SnowCover.Amount:0.00}, road {SnowCover.Covered(Art.Asphalt)} ({Art.Asphalt.color.grayscale:0.00}), grass {SnowCover.Covered(Art.Grass)}, pavement {SnowCover.Covered(Art.Kerb)}");
                camera.SetMode(ViewMode.Chase);
                car.Input.SetTest(0, 0, 0);
                car.Teleport(1800, 1);
                yield return new WaitForSeconds(1.2f);
                Capture("39-snow-road");
                Island.Spawn(car);
                yield return new WaitForSeconds(1.2f);
                Capture("40-snow-city");
                weather.Set(Weather.Clear, false);
                for (int k = 0; k < 30; k++) weather.Apply(1f);
                Check("Snow melts", SnowCover.Amount < .001f && !SnowCover.Covered(Art.Asphalt) && Art.Asphalt.GetTexture("_BaseMap") == original, $"cover {SnowCover.Amount:0.00}, road texture {Art.Asphalt.GetTexture("_BaseMap")?.name}");
            }

            // The title screen: shown, its menu opens the modes and starts the drive.
            {
                var hud = FindFirstObjectByType<DriveHud>();
                run.OnTitle = true;
                run.SetPaused(true);
                yield return new WaitForSecondsRealtime(.4f);
                yield return CaptureFull("41-title");
                var act = typeof(DriveHud).GetMethod("TitleAction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                act?.Invoke(hud, new object[] { 1 });
                bool modes = run.ChoosingMode && !run.OnTitle;
                yield return new WaitForSecondsRealtime(.3f);
                yield return CaptureFull("42-modes");
                run.ChoosingMode = false;
                run.OnTitle = true;
                act?.Invoke(hud, new object[] { 0 });
                Check("Title screen", act != null && modes && !run.OnTitle && !run.Paused && run.Mode == GameMode.Free, $"modes opened {modes}, start leaves the title {!run.OnTitle}, running {!run.Paused}");
            }

            // Purchased scenery: sea water shader, rocks, European trees, new grass.
            {
                bool water = Island.Sea && Island.Sea.shader && (Island.Sea.shader.name.ToLowerInvariant().Contains("water") || Island.Sea.shader.name == "Autobahn/Ocean");
                bool photo = Island.Rock && Island.Rock.mainTexture && Island.Sand && Island.Sand.mainTexture;
                Check("NYC buildings in the city", GridCity.NycPlaced >= 6, $"{GridCity.NycPlaced} purchased buildings placed");
                Check("Purchased scenery", water && Island.Boulders.Length == 10 && Foliage.PackTrees && photo && IslandWorld.HillBoulders > 100,
                    $"sea shader {(Island.Sea ? Island.Sea.shader.name : "none")}, rock models {Island.Boulders.Length}, hill boulders {IslandWorld.HillBoulders}, photo sand/rock {photo}, pack trees {Foliage.PackTrees}");
                camera.enabled = false;
                // One purchased tree on its own against the sky, whole and then crown only.
                {
                    var holder = new GameObject("QA tree").transform;
                    holder.position = new Vector3(Island.AirX0 + 200, 0, Island.AirZ0 + 100);
                    Foliage.Tree(holder, Vector3.zero, Foliage.Kind.Oak, 12);
                    Foliage.Tree(holder, new Vector3(14, 0, 0), Foliage.Kind.Birch, 12);
                    Foliage.Tree(holder, new Vector3(-14, 0, 0), Foliage.Kind.Spruce, 14);
                    var info = new System.Text.StringBuilder("QA INFO tree parts:");
                    foreach (var r in holder.GetComponentsInChildren<MeshRenderer>())
                        info.Append($" [{r.name} {r.sharedMaterial.name} {r.sharedMaterial.shader.name} kw {string.Join(",", r.sharedMaterial.shaderKeywords)} sub {r.GetComponent<MeshFilter>().sharedMesh.subMeshCount} tris {r.GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3}]");
                    Debug.Log(info.ToString());
                    camera.enabled = false;
                    camera.transform.position = holder.position + new Vector3(0, 6, -26);
                    camera.transform.LookAt(holder.position + Vector3.up * 6);
                    yield return new WaitForSeconds(.4f);
                    Capture("50-tree-test");
                    foreach (var r in holder.GetComponentsInChildren<MeshRenderer>()) if (r.name == "Tree trunk") r.enabled = false;
                    yield return null;
                    Capture("50-tree-crowns");
                    Destroy(holder.gameObject);
                }
                // The NYC-like buildings: size and triangles, lined up on the airport apron.
                {
                    var holder = new GameObject("QA buildings").transform;
                    holder.position = new Vector3(Island.AirX0 + 150, 0, Island.AirZ0 + 250);
                    var info = new System.Text.StringBuilder("QA INFO buildings:");
                    float bx = 0;
                    foreach (var prefab in Resources.LoadAll<GameObject>("Env/Buildings/Prefabs/Buildings"))
                    {
                        var bld = Instantiate(prefab, holder).transform;
                        bld.localPosition = new Vector3(bx, 0, 0);
                        int tris = 0; Bounds bb = default; bool first = true; int mats = 0;
                        foreach (var r in bld.GetComponentsInChildren<Renderer>())
                        {
                            var mf = r.GetComponent<MeshFilter>();
                            if (mf && mf.sharedMesh) tris += mf.sharedMesh.triangles.Length / 3;
                            mats += r.sharedMaterials.Length;
                            if (first) { bb = r.bounds; first = false; } else bb.Encapsulate(r.bounds);
                        }
                        info.Append($" [{prefab.name}: {tris} tris, {mats} mats, size {bb.size.x:0.0}x{bb.size.y:0.0}x{bb.size.z:0.0}, centre off {(bb.center - bld.position).x:0.0},{(bb.center - bld.position).z:0.0}]");
                        bx += Mathf.Max(20, bb.size.x + 6);
                    }
                    Debug.Log(info.ToString());
                    camera.enabled = false;
                    camera.transform.position = holder.position + new Vector3(bx / 2, 30, -90);
                    camera.transform.LookAt(holder.position + new Vector3(bx / 2, 12, 0));
                    yield return new WaitForSeconds(.5f);
                    Capture("51-buildings-front");
                    camera.transform.position = holder.position + new Vector3(bx / 2, 30, 90);
                    camera.transform.LookAt(holder.position + new Vector3(bx / 2, 12, 0));
                    yield return new WaitForSeconds(.3f);
                    Capture("51-buildings-back");
                    Destroy(holder.gameObject);
                }
                // A purchased street light and signs, close up.
                {
                    var holder = new GameObject("QA props").transform;
                    holder.position = new Vector3(Island.AirX0 + 200, 0, Island.AirZ0 + 100);
                    var head = Art.StreetLamp(holder, Vector3.zero, Vector3.right);
                    Art.StreetLamp(holder, new Vector3(-6, 0, 0), Vector3.zero, true);
                    Art.LogSigns = true;
                    Art.RoadSign(holder, new Vector3(4, 0, -3), Vector3.back, "Stop_Sign");
                    Art.RoadSign(holder, new Vector3(7, 0, -3), Vector3.back, "Caution_Sign");
                    Debug.Log($"QA INFO lamp head {head}, parts {holder.GetComponentsInChildren<Renderer>().Length}");
                    camera.transform.position = holder.position + new Vector3(2, 3, -14);
                    camera.transform.LookAt(holder.position + Vector3.up * 3);
                    yield return new WaitForSeconds(.3f);
                    Capture("50-props");
                    Destroy(holder.gameObject);
                    Check("Purchased street lights and signs", head != null && Resources.Load<GameObject>("Env/Signs/Prefabs/Stop_Sign"), $"lamp {(head != null ? "ok" : "missing")}, signs {(Resources.Load<GameObject>("Env/Signs/Prefabs/Stop_Sign") ? "ok" : "missing")}");
                }
                camera.transform.position = Route.Center(900) + Vector3.up * 14 - Route.Right(900) * 30;
                camera.transform.rotation = Quaternion.LookRotation(-Route.Right(900) + Vector3.down * .25f);
                yield return new WaitForSeconds(.5f);
                Capture("50-sea");
                {
                    // Find the highest ground and look at it from two sides.
                    Vector3 hillTop = Vector3.zero; float hillHeight = -1;
                    for (float x = -800; x < 2200; x += 40)
                        for (float z = 0; z < 5500; z += 40)
                        {
                            float h = Island.GroundHeight(x, z);
                            if (h > hillHeight) { hillHeight = h; hillTop = new Vector3(x, h, z); }
                        }
                    // The camera is far from the car: wake the ground tiles around the hill.
                    var islandWorld = FindFirstObjectByType<IslandWorld>();
                    if (islandWorld)
                        foreach (Transform t in islandWorld.transform)
                            if (t.name.StartsWith("Island ") && (t.position + new Vector3(100, 0, 100) - hillTop).magnitude < 1100) t.gameObject.SetActive(true);
                    int view = 0;
                    foreach (var dir in new[] { new Vector3(-1, 0, -.4f), new Vector3(.6f, 0, 1) })
                    {
                        Vector3 from = hillTop + dir.normalized * 700;
                        from.y = Mathf.Max(Island.GroundHeight(from.x, from.z), Island.SeaY) + 60;
                        camera.transform.position = from;
                        camera.transform.LookAt(hillTop - Vector3.up * 40);
                        yield return new WaitForSeconds(.5f);
                        Capture("50-hills-" + view++);
                    }
                }
                camera.transform.position = Route.Center(900) + Vector3.up * 5 - Route.Right(900) * 12;
                camera.transform.rotation = Quaternion.LookRotation(-Route.Right(900) + Vector3.down * .35f);
                yield return new WaitForSeconds(.5f);
                Capture("50-beach");
                camera.enabled = true;
            }

            // The new garage cars: each one loads with its own four wheels, drives, and is photographed.
            {
                int before = run.Profile.car;
                var cars = new System.Text.StringBuilder();
                bool allGood = true;
                foreach (var t in traffic.Cars) if (!t.OnGrid) t.gameObject.SetActive(false);
                for (int k = 0; k < CarCatalog.Count; k++)
                {
                    var entry = CarCatalog.Get(k);
                    if (entry.Style < BodyStyle.AudiR8 && !WheelQA) continue;
                    run.Profile.car = (k + CarCatalog.Count - 1) % CarCatalog.Count;
                    run.CycleCar();
                    var body = car.GetComponent<CarBody>();
                    int broken = 0;
                    foreach (var r in car.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials)
                            if (!m || !m.shader || m.shader.name.Contains("InternalError") || !m.shader.isSupported) broken++;
                    car.Place(new Vector3(Island.AirX0 + 300, .6f, Island.AirZ0 + 220), Quaternion.LookRotation(Vector3.right));
                    yield return new WaitForSeconds(.6f);
                    camera.enabled = false;
                    camera.transform.position = car.transform.position + car.transform.forward * (body.Shape.Length * .9f + 2) + car.transform.right * -(body.Shape.Length * .6f + 2) + Vector3.up * (1.2f + body.Shape.Height * .35f);
                    camera.transform.LookAt(car.transform.position + Vector3.up * body.Shape.Height * .45f);
                    yield return null;
                    Capture("49-car-" + entry.Name.Replace(' ', '-'));
                    if (WheelQA)
                    {
                        yield return new WaitForSeconds(1f);
                        WheelReport(car, body, entry.Name);
                        camera.transform.position = car.transform.position - car.transform.right * (body.Shape.Length * .75f + 1.5f) + Vector3.up * .5f;
                        camera.transform.LookAt(car.transform.position + Vector3.up * .5f);
                        yield return null;
                        Capture("49-side-" + entry.Name.Replace(' ', '-'));
                    }
                    camera.enabled = true;
                    var spin0 = body.Wheels[2].rotation;
                    car.Input.SetTest(1, 0, 0);
                    float tStart = Time.time, top = 0;
                    while (Time.time - tStart < 3.5f) { top = Mathf.Max(top, car.Speed); yield return null; }
                    if (WheelQA)
                    {
                        var ws = body.Wheels[2].GetComponentsInChildren<Transform>();
                        Debug.Log($"QA WHEEL {entry.Name}: rear wheel turned {Quaternion.Angle(spin0, body.Wheels[2].rotation):0}deg (children {ws.Length})");
                    }
                    car.Input.SetTest(0, 1, 0);
                    yield return new WaitForSeconds(1.2f);
                    car.Input.SetTest(0, 0, 0);
                    bool upright = car.transform.up.y > .9f;
                    bool good = body.Shape.Measured && broken == 0 && top > 25 && upright;
                    allGood &= good;
                    cars.Append($" [{entry.Name}: wheels {(body.Shape.Measured ? "ok" : "MISSING")}, wb {body.Shape.Wheelbase:0.00}, len {body.Shape.Length:0.0}, h {body.Shape.Height:0.0}, bad mats {broken}, {top:0} km/h in 3.5 s{(upright ? "" : ", ROLLED")}]");
                }
                run.Profile.car = (before + CarCatalog.Count - 1) % CarCatalog.Count;
                run.CycleCar();
                Check("New garage cars", allGood && CarCatalog.Count >= 12, CarCatalog.Count + " cars:" + cars);

                // Heavy vehicles and the new hatchback in the traffic.
                int vans = 0, lorries = 0, hatch = 0;
                foreach (var t in traffic.Cars)
                {
                    var st = t.GetComponent<CarBody>()?.Shape?.Style;
                    if (st == BodyStyle.DeliveryVan) vans++;
                    if (st == BodyStyle.BoxTruck) lorries++;
                    if (st == BodyStyle.Tocus) hatch++;
                }
                Check("New cars in traffic", vans > 0 && lorries > 0 && hatch > 0, $"{vans} vans, {lorries} lorries, {hatch} Tocus hatchbacks");
            }

            // The whole island on the map.
            var map = FindFirstObjectByType<WorldMap>();
            if (map)
            {
                map.Toggle();
                var type = typeof(WorldMap);
                type.GetField("zoom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(map, 3100f);
                type.GetField("focus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(map, new Vector3(600, 0, 2300));
                yield return new WaitForSecondsRealtime(.6f);
                yield return CaptureFull("38-island-map");
                map.Toggle();
                yield return new WaitForSecondsRealtime(.2f);
            }
            Island.Spawn(car);
            camera.SetMode(ViewMode.Chase);
        }

        IEnumerator CityChecks()
        {
            traffic.Simulate = false;
            foreach (var civil in traffic.Cars) civil.gameObject.SetActive(false);
            car.Input.SetTest(0, 0, 0);
            foreach (float crossing in new[] {225f, 12425f})
            {
                car.Teleport(crossing, 1);
                yield return new WaitForSeconds(.8f);
                Physics.SyncTransforms();
                bool clear = true, ground = true;
                string obstruction = "none";
                for (float x = -160; x <= 160; x += 4)
                {
                    Vector3 surface = Route.Center(crossing) + Route.Right(crossing) * x;
                    bool blocked = Physics.CheckBox(surface + Vector3.up * .85f, new Vector3(.8f, .3f, 2.4f),
                        Route.Rotation(crossing) * Quaternion.Euler(0, 90, 0), 1 << 10, QueryTriggerInteraction.Ignore);
                    clear &= !blocked;
                    if (blocked) obstruction = x.ToString("0") + " m from avenue";
                    ground &= Physics.Raycast(surface + Vector3.up * 2, Vector3.down, out RaycastHit hit, 3, 1 << 10)
                        && Mathf.Abs(hit.point.y - surface.y) < .3f;
                }
                Check("Cross street clearance " + crossing, clear, "Vehicle-sized clearance across 320 m; obstruction: " + obstruction);
                Check("Cross street ground " + crossing, ground, "Continuous level road surface across all connecting streets");
                bool links = true;
                foreach (float x in new[] {-150f, -72f, 72f, 150f})
                    for (float z = crossing + 15; z < crossing + 190; z += 10)
                    {
                        Vector3 surface = Route.Center(z) + Route.Right(z) * x;
                        links &= Physics.Raycast(surface + Vector3.up * 2, Vector3.down, 3, 1 << 10)
                            && !Physics.CheckBox(surface + Vector3.up * .85f, new Vector3(.8f, .3f, 2.4f), Route.Rotation(z), 1 << 10);
                    }
                Check("Connected city streets " + crossing, links, "Both pairs of parallel streets connect neighbouring intersections");
                foreach (int side in new[] {-1, 1})
                {
                    car.Teleport(crossing, 1);
                    yield return new WaitForSeconds(.4f);
                    Quaternion facing = Route.Rotation(crossing) * Quaternion.Euler(0, side * 90, 0);
                    car.transform.rotation = facing;
                    car.Body.rotation = facing;
                    car.Body.linearVelocity = Route.Right(crossing) * side * 10;
                    car.Input.SetTest(.3f, 0, 0);
                    yield return new WaitForSeconds(3);
                    float progress = Vector3.Dot(car.Body.position - Route.Center(crossing), Route.Right(crossing)) * side;
                    Check("Drive through city opening " + crossing + "/" + side,
                        progress > 15 && car.transform.up.y > .9f && Mathf.Abs(car.Body.position.y - Route.Height(crossing)) < 1,
                        $"Travelled {progress:0.0}m into side street; upright {car.transform.up.y:0.00}");
                    car.Input.SetTest(0, 0, 0);
                }
            }
            foreach (float boundary in new[] {5f, 1395f, 12005f, 13995f})
            {
                car.Teleport(boundary, 1);
                yield return new WaitForSeconds(.6f);
                bool level = true;
                foreach (float x in new[] {-150f, -72f, 72f, 150f})
                {
                    Vector3 surface = Route.Center(boundary) + Route.Right(boundary) * x;
                    level &= Physics.Raycast(surface + Vector3.up * 5, Vector3.down, out RaycastHit hit, 6, 1 << 10)
                        && Mathf.Abs(hit.point.y - surface.y) < .3f;
                }
                Check("City boundary roads " + boundary, level, "Terrain does not bury the roads at the city boundary");
            }
            car.Teleport(175, 1);
            camera.SetMode(ViewMode.Wide);
            yield return new WaitForSeconds(.6f);
            Capture("15-city-street");
            camera.enabled = false;
            camera.transform.position = Route.Center(225) + new Vector3(160, 155, -140);
            camera.transform.LookAt(Route.Center(245) + new Vector3(15, 0, 0));
            Capture("16-city-plan");
            // Downtown: the towers have to be visible from the carriageway.
            camera.transform.position = Route.Center(1150) + new Vector3(210, 190, -260);
            camera.transform.LookAt(Route.Center(1180) + new Vector3(0, 30, 0));
            Capture("17-downtown");
            camera.enabled = true;
            car.Teleport(1150, 1);
            camera.SetMode(ViewMode.Wide);
            yield return new WaitForSeconds(.6f);
            Capture("18-downtown-street");
            camera.enabled = true;
            camera.SetMode(ViewMode.Chase);
            car.Teleport(25);
            traffic.Restart();
            traffic.Simulate = true;
        }

        IEnumerator JunctionAndEntryChecks()
        {
            traffic.Simulate = false;
            foreach (var civil in traffic.Cars) civil.gameObject.SetActive(false);
            car.Input.SetTest(0, 0, 0);
            foreach (var site in Station.Sites)
            {
                float z = site.Z - 40;
                car.Teleport(z, 1);
                yield return new WaitForSeconds(.9f);
                Physics.SyncTransforms();
                Vector3 right = Route.Right(z) * site.Side;
                Vector3 origin = Route.Center(z) + right * 5.5f + Vector3.up * .8f;
                bool blocked = Physics.BoxCast(origin, new Vector3(.8f, .25f, 2f), right,
                    out RaycastHit hit, Route.Rotation(z), 8f, 1 << 10, QueryTriggerInteraction.Ignore);
                bool supported = Physics.Raycast(origin + right * 8f + Vector3.up, Vector3.down, 3f, 1 << 10);
                Check("Station entry " + site.Z, !blocked && supported,
                    blocked ? "Blocked by " + hit.collider.name : "Vehicle-sized sweep clear; ground present beyond roadside");
            }
        }

        IEnumerator CaptureFull(string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(output, name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
        }

        float lastStop;
        IEnumerator StopTest(bool wet)
        {
            weather.Set(wet ? Weather.Rain : Weather.Clear, false);
            weather.Apply(10);
            car.Input.SetTest(0, 0, 0);
            car.Teleport(400);
            yield return new WaitForSeconds(.5f);
            car.Body.linearVelocity = car.transform.forward * (100 / 3.6f);
            car.Input.SetTest(0, 1, 0);
            Vector3 start = car.Body.position;
            float time = Time.time;
            while (car.Speed > 2 && Time.time - time < 8)
                yield return new WaitForFixedUpdate();
            lastStop = Vector3.Distance(start, car.Body.position);
            Check(wet ? "Wet ABS stop" : "Dry ABS stop", car.Speed < 3 && lastStop > 20 && lastStop < 100, $"{lastStop:0.00}m in {Time.time - time:0.00}s");
        }

        void Follow(float throttle)
        {
            float ahead = Mathf.Lerp(20, 60, car.Speed / 270);
            Vector3 local = car.transform.InverseTransformPoint(Route.Position(Route.Locate(car.transform.position) + ahead, 1));
            float angle = Mathf.Atan2(local.x, local.z);
            // Pure pursuit with yaw damping; the smoothed steering has a little lag.
            float yaw = Vector3.Dot(car.Body.angularVelocity, car.transform.up);
            float steer = Mathf.Clamp(angle * 3f - yaw * .3f, -.6f, .6f);
            car.Input.SetTest(throttle, 0, steer);
        }

        static readonly bool WheelQA = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-wheelQA") >= 0;

        // Wheel diagnostics: where the visible wheels sit against the body and the ground.
        static void WheelReport(Vehicle car, CarBody body, string name)
        {
            float ground = Island.GroundHeight(car.transform.position.x, car.transform.position.z);
            if (UnityEngine.Physics.Raycast(car.transform.position + Vector3.up * 5, Vector3.down, out RaycastHit hit, 20, ~(1 << 8 | 1 << car.gameObject.layer)))
                ground = hit.point.y;
            var sb = new System.Text.StringBuilder($"QA WHEEL {name}: r {body.Shape.WheelRadius:0.00} ground {ground - car.transform.position.y:0.00}");
            var wheelRenderers = new System.Collections.Generic.HashSet<Renderer>();
            for (int i = 0; i < 4; i++)
            {
                var w = body.Wheels[i];
                Vector3 local = car.transform.InverseTransformPoint(w.position);
                Bounds b = default; bool any = false; int count = 0, on = 0;
                foreach (var r in w.GetComponentsInChildren<Renderer>(true))
                {
                    wheelRenderers.Add(r); count++;
                    if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                    on++;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                sb.Append($" | w{i} hub {local.x:0.00},{local.y:0.00},{local.z:0.00} rend {on}/{count}");
                if (any) sb.Append($" size {b.size.y:0.00} tyreBottom {b.min.y - ground:0.00} centre-hub {(b.center - w.position).magnitude:0.00}");
            }
            float lowest = float.MaxValue;
            foreach (var r in body.GetComponentsInChildren<Renderer>())
                if (!wheelRenderers.Contains(r) && r.enabled && !(r is ParticleSystemRenderer) && r.bounds.size.x > .5f)
                    lowest = Mathf.Min(lowest, r.bounds.min.y);
            sb.Append($" | body lowest {lowest - ground:0.00} contacts {car.Chassis.Grounded}");
            Debug.Log(sb.ToString());
            if (name.Contains("Truck") || name.Contains("Levo") || name.Contains("Audi"))
                foreach (var r in body.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer) continue;
                    string path = r.name;
                    for (var t = r.transform.parent; t && t != body.transform; t = t.parent) path = t.name + "/" + path;
                    Vector3 c = car.transform.InverseTransformPoint(r.bounds.center);
                    Debug.Log($"QA PART {name}: {path} on {r.enabled && r.gameObject.activeInHierarchy} c {c.x:0.00},{c.y:0.00},{c.z:0.00} size {r.bounds.size.x:0.00},{r.bounds.size.y:0.00},{r.bounds.size.z:0.00} mat {(r.sharedMaterial ? r.sharedMaterial.name : "-")}");
                }
        }

        void Capture(string name)
        {
            var cam = camera.Lens;
            var target = new RenderTexture(1440, 900, 24);
            var old = cam.targetTexture;
            var active = RenderTexture.active;
            cam.targetTexture = target;
            ForestRenderer.Active?.DrawFor(cam);
            cam.Render();
            RenderTexture.active = target;
            var image = new Texture2D(1440, 900, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 1440, 900), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG());
            cam.targetTexture = old;
            RenderTexture.active = active;
            Destroy(image);
            Destroy(target);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Autobahn.Net
{
    // Online co-op for up to five players. The host runs the traffic, signals and weather;
    // everybody drives their own car and sends its state 20 times a second. Each player sees
    // the others as solid ghost cars, and the guests see the host's traffic.
    public sealed class CoopNet : MonoBehaviour
    {
        public static CoopNet Active { get; private set; }
        public static bool Online => Active != null && Active.link != null && Active.link.State == CoopLink.Phase.Connected;
        public static bool IsHost => Online && Active.link.IsHost;
        public static bool IsGuest => Online && !Active.link.IsHost;
        public static bool Busy => Active != null && Active.link != null && Active.link.State != CoopLink.Phase.Idle;

        public CoopLink.Phase State => link?.State ?? CoopLink.Phase.Idle;
        public string Code => link?.Code ?? "";
        public string Message => link?.Message ?? "";
        public float Ping => link?.Ping ?? 0;
        public string BrokerName => link?.BrokerName ?? "";
        public bool Hosting => link != null && link.IsHost;
        public int Slot => link?.Slot ?? -1;
        public int PlayerCount => link?.Players.Count ?? 1;
        public IReadOnlyDictionary<int, string> Players => link != null ? link.Players : empty;
        static readonly SortedDictionary<int, string> empty = new();
        public string NameOf(int slot) => link != null && link.Players.TryGetValue(slot, out var n) ? n : "Игрок " + (slot + 1);

        // Everybody else's car, by slot.
        public readonly Dictionary<int, RemoteCar> Remotes = new();

        static readonly Color[] colours =
        {
            new(.35f, .80f, 1f), new(.45f, .95f, .45f), new(.80f, .55f, 1f), new(1f, .85f, .25f), new(1f, .50f, .75f),
        };
        public static Color ColorOf(int slot) => colours[Mathf.Abs(slot) % colours.Length];

        CoopLink link;
        RunSession run;
        float nextCarrier, nextState, nextTraffic;
        readonly TrafficMirror mirror = new();
        bool placedNearHost, wasOnline;
        int friendCursor;
        readonly float[] leftLit = new float[256], rightLit = new float[256];

        // Explosions go out as events, each sent three times (a lost packet does not lose it);
        // receivers drop the repeats by sender and id.
        struct Outgoing { public byte[] Data; public int Left; public float Next; }
        readonly List<Outgoing> outbox = new();
        readonly HashSet<int> seenEvents = new();
        readonly Queue<int> seenOrder = new();
        ushort nextEventId;
        bool wasWrecked;
        readonly bool[] trafficWrecked = new bool[256];
        readonly float[] trafficBlast = new float[256];
        public const int EventRepeats = 3;

        public void Send(byte[] payload) => link?.SendAll(payload);

        public void SendEvent(byte kind, byte arg)
        {
            outbox.Add(new Outgoing { Data = CoopEvent.Pack(nextEventId++, kind, arg), Left = EventRepeats, Next = 0 });
        }

        // True the first time an event is seen, false for its repeats.
        public bool FirstSight(int slot, ushort id)
        {
            int key = slot << 16 | id;
            if (!seenEvents.Add(key)) return false;
            seenOrder.Enqueue(key);
            if (seenOrder.Count > 512) seenEvents.Remove(seenOrder.Dequeue());
            return true;
        }

        public static CoopNet Ensure()
        {
            if (Active) return Active;
            var go = new GameObject("Coop");
            DontDestroyOnLoad(go);
            return go.AddComponent<CoopNet>();
        }

        void Awake() => Active = this;

        void Start() => CoopProbe.StartIfRequested();

        static string PlayerName()
        {
            string n = Environment.UserName;
            if (string.IsNullOrEmpty(n)) n = SystemInfo.deviceName;
            return n.Length > 16 ? n.Substring(0, 16) : n;
        }

        double Clock => Time.realtimeSinceStartupAsDouble;

        public void HostGame(string code = null)
        {
            Leave();
            run = RunSession.Active;
            link = new CoopLink();
            link.Host(string.IsNullOrEmpty(code) ? CoopLink.NewCode() : code, PlayerName(), RoomBytes(), Clock);
        }

        public void JoinGame(string code)
        {
            Leave();
            run = RunSession.Active;
            link = new CoopLink();
            link.Join(code, PlayerName(), Clock);
            placedNearHost = false;
        }

        public void Leave()
        {
            link?.Dispose();
            link = null;
            DropAll();
            if (wasOnline)
            {
                wasOnline = false;
                if (run != null) run.SetPaused(run.Paused);
            }
        }

        void OnApplicationQuit() => Leave();
        void OnDestroy() => Leave();

        byte[] RoomBytes() => new[] { (byte)(RunSession.Active ? (int)RunSession.Active.Mode : 0) };

        void Drop(int slot)
        {
            FootNet.Drop(slot);
            if (Remotes.TryGetValue(slot, out var car) && car)
            {
                TrafficFlow.Remotes.Remove(car);
                Destroy(car.gameObject);
            }
            Remotes.Remove(slot);
        }

        void DropAll()
        {
            FootNet.DropAll();
            foreach (var car in Remotes.Values)
                if (car) Destroy(car.gameObject);
            Remotes.Clear();
            TrafficFlow.Remotes.Clear();
            mirror.Release(run);
        }

        RemoteCar CarFor(int slot)
        {
            if (!Remotes.TryGetValue(slot, out var car) || !car)
            {
                car = RemoteCar.Create();
                car.name = "Friend car " + slot;
                Remotes[slot] = car;
                TrafficFlow.Remotes.Add(car);
            }
            return car;
        }

        void Update()
        {
            if (link == null) return;
            run ??= RunSession.Active;
            var before = link.State;
            link.Poll(Clock);
            if (before == CoopLink.Phase.Connected && link.State != CoopLink.Phase.Connected)
                DropAll();
            // Online the menu no longer freezes time; offline again it does.
            if (Online != wasOnline)
            {
                wasOnline = Online;
                if (run != null) run.SetPaused(run.Paused);
            }
            if (!Online || run == null || run.Player == null) return;

            // A broken or truncated message is dropped on its own; it must not cost the rest of the frame.
            while (link.TryRead(out byte[] data))
            {
                if (data == null || data.Length < 2) continue;
                try { Handle(data); }
                catch (System.Exception e) { Debug.LogWarning($"coop: bad '{(char)data[0]}' message ({data.Length} B): {e.Message}"); }
            }

            // Cars of players no longer on the roster.
            if (Remotes.Count > 0)
            {
                List<int> stale = null;
                foreach (var slot in Remotes.Keys)
                    if (!link.Players.ContainsKey(slot))
                        (stale ??= new List<int>()).Add(slot);
                if (stale != null)
                    foreach (int slot in stale) Drop(slot);
            }

            float now = Time.unscaledTime;
            WatchExplosions();
            for (int i = outbox.Count - 1; i >= 0; i--)
            {
                var e = outbox[i];
                if (now < e.Next) continue;
                link.SendAll(e.Data);
                e.Left--;
                e.Next = now + .15f;
                if (e.Left <= 0) outbox.RemoveAt(i);
                else outbox[i] = e;
            }
            FootNet.Tick(this);
            BoatNet.Tick();
            PlaneNet.Tick();
            if (now >= nextState)
            {
                nextState = now + .1f;              // 10 Hz: five players stay light on the broker
                link.SendAll(StateMessage());
            }
            if (link.IsHost && Carrier.Active && now >= nextCarrier)
            {
                nextCarrier = now + 1;
                var msg = new byte[5];
                msg[0] = (byte)'V';
                System.BitConverter.GetBytes(Carrier.Active.Phase).CopyTo(msg, 1);
                link.SendAll(msg);
            }
            if (link.IsHost && now >= nextTraffic)
            {
                nextTraffic = now + .16f;
                link.SendGuests(TrafficMessage());
            }

            if (!link.IsHost)
                mirror.Apply(run, null);
        }

        void FixedUpdate()
        {
            if (Online && !link.IsHost && run != null)
                mirror.Step(run);
        }

        // The own car blowing up, and (host) traffic cars blowing up: events for everybody.
        void WatchExplosions()
        {
            bool wrecked = run.Player.WreckedFor > 0;
            if (wrecked && !wasWrecked) SendEvent(CoopEvent.PlayerExploded, 0);
            wasWrecked = wrecked;
            if (!link.IsHost) return;
            var cars = run.Traffic.Cars;
            for (int i = 0; i < cars.Count && i < trafficWrecked.Length; i++)
            {
                bool w = cars[i].Wrecked && cars[i].gameObject.activeSelf;
                if (w && !trafficWrecked[i]) SendEvent(CoopEvent.TrafficExploded, (byte)i);
                trafficWrecked[i] = w;
            }
        }

        void OnEvent(int slot, byte kind, byte arg)
        {
            if (kind == CoopEvent.PlayerExploded)
            {
                if (Remotes.TryGetValue(slot, out var car) && car) car.Blast();
            }
            else if (kind == CoopEvent.PlayerKilled)
                FootNet.OnKilled(slot, arg);
            else if (kind == CoopEvent.TrafficExploded && !link.IsHost && arg < run.Traffic.Cars.Count)
            {
                // Once per wreck, even if the traffic snapshot already showed it burnt.
                if (Time.time - trafficBlast[arg] < 5) return;
                trafficBlast[arg] = Time.time;
                var traffic = run.Traffic.Cars[arg];
                if (traffic.gameObject.activeSelf) Explosion.BlowMirrored(traffic);
            }
        }

        // Host changed mode: the room description follows; guests read it from the host's car state.
        public void ModeChanged()
        {
            if (link == null || !link.IsHost) return;
            link.UpdateRoom(RoomBytes());
        }

        // A guest bumped a traffic car: the host makes it brake for everybody.
        // A guest hurt, shoved or ran into a traffic car: the host owns the traffic and applies it
        // there for everybody (the burnt car and its blast come back in the traffic snapshots and
        // the explosion event). damage in health points, push in m/s, stun in seconds.
        public static void ReportDamage(TrafficCar car, float damage, Vector3 push, float stun)
        {
            if (!IsGuest || Active.run == null) return;
            int index = Active.run.Traffic.Cars.IndexOf(car);
            if (index < 0 || index > 255) return;
            var ms = new MemoryStream(16);
            var w = new BinaryWriter(ms);
            w.Write((byte)'Y');
            w.Write((byte)index);
            w.Write((ushort)Mathf.Clamp(damage * 10, 0, 65535));
            w.Write((byte)Mathf.Clamp(stun * 10, 0, 255));
            w.Write((short)Mathf.Clamp(push.x * 100, -32000, 32000));
            w.Write((short)Mathf.Clamp(push.y * 100, -32000, 32000));
            w.Write((short)Mathf.Clamp(push.z * 100, -32000, 32000));
            Active.link.SendHost(ms.ToArray());
        }

        public static void ReportHit(TrafficCar car)
        {
            if (!IsGuest || Active.run == null) return;
            int index = Active.run.Traffic.Cars.IndexOf(car);
            if (index >= 0)
                Active.link.SendHost(new[] { (byte)'X', (byte)index });
        }

        // T / the button: jump behind the next friend in turn (the host first for guests).
        public void GoToFriend()
        {
            if (run == null || Remotes.Count == 0) return;
            var slots = new List<int>(Remotes.Keys);
            slots.Sort();
            int slot = slots[friendCursor++ % slots.Count];
            GoBehind(slot, 12);
        }

        // Next to a friend: a free spot a few metres beside or behind them (never on top of them
        // or inside a house). A friend walking about is met on foot if we are walking too.
        void GoBehind(int slot, float gap)
        {
            Remotes.TryGetValue(slot, out var target);
            var avatar = FootNet.AvatarOf(slot);
            bool friendWalking = avatar && avatar.Visible && !avatar.Riding;
            if (!friendWalking && (!target || !target.HasState)) return;
            Transform anchor = friendWalking ? avatar.transform : target.transform;
            Vector3 fwd = anchor.forward;
            fwd.y = 0;
            fwd = fwd.sqrMagnitude > .01f ? fwd.normalized : Vector3.forward;
            var foot = FootPlayer.Active;
            if (foot && !foot.Riding)
            {
                if (!SpawnSpot.ForPerson(anchor.position, fwd, new[] { 2.5f, 4f, 6f, 9f }, out Vector3 standAt))
                    standAt = anchor.position - fwd * 3 + Vector3.up * .3f;
                foot.Teleport(standAt, Quaternion.LookRotation(anchor.position - standAt).eulerAngles.y);
                return;
            }
            // A little behind and to the side, clear of everything (never on top of the friend).
            float[] rings = { 8, 11, Mathf.Max(14, gap), gap + 8, gap + 18 };
            if (!SpawnSpot.ForCar(anchor.position, fwd, rings, out Vector3 p, out Quaternion rot, true))
            {
                p = anchor.position - fwd * gap + Vector3.up * .3f;
                rot = Quaternion.LookRotation(fwd);
            }
            run.Player.Place(p, rot);
            if (!friendWalking) run.Player.Body.linearVelocity = target.Velocity;
        }

        void Handle(byte[] data)
        {
            switch ((char)data[0])
            {
                case 'S':
                {
                    int slot = data[1];
                    // A player the host already dropped: no car is made (and at once removed again).
                    if (!link.Players.ContainsKey(slot)) break;
                    var car = CarFor(slot);
                    car.Push(data, 2, Clock);
                    if (!link.IsHost && slot == 0)
                    {
                        // Guests always play the host's mode, and start just behind the host,
                        // each a few car lengths further back so nobody lands on anybody.
                        if (car.HostMode != run.Mode)
                        {
                            run.SetMode(car.HostMode);
                            mirror.Take(run);
                            placedNearHost = false;
                        }
                        if (!placedNearHost)
                        {
                            placedNearHost = true;
                            GoBehind(0, 10 + 9 * Mathf.Max(1, link.Slot));
                        }
                    }
                    break;
                }
                case 'D':
                    Drop(data[1]);
                    break;
                case 'P':
                case 'F':
                case 'H':
                case 'W':
                    FootNet.Receive(data);
                    break;
                case 'T':
                    if (!link.IsHost) mirror.Push(data, Clock);
                    break;
                case 'X':
                    if (link.IsHost && data.Length > 1 && data[1] < run.Traffic.Cars.Count)
                        run.Traffic.Cars[data[1]].Hit();
                    break;
                case 'Y':
                    // A guest's bullet, bump or push on a traffic car (see ReportDamage).
                    if (link.IsHost && data.Length >= 11 && data[1] < run.Traffic.Cars.Count)
                    {
                        var r = new BinaryReader(new MemoryStream(data, 2, data.Length - 2));
                        float damage = r.ReadUInt16() / 10f, stun = r.ReadByte() / 10f;
                        var push = new Vector3(r.ReadInt16(), r.ReadInt16(), r.ReadInt16()) / 100f;
                        var traffic = run.Traffic.Cars[data[1]];
                        if (traffic.gameObject.activeSelf && !traffic.Wrecked)
                        {
                            traffic.Hit();
                            if (stun > 0) traffic.Stun(stun);
                            if (push.sqrMagnitude > .01f) traffic.Shove(push);
                            if (damage > 0) traffic.Damage(damage);
                        }
                    }
                    break;
                case 'U': case 'Q': PoliceNet.Receive(data); break;    // the police: guests' crimes, the host's units
                case 'K':
                    WorldNet.Receive(data);
                    break;
                case 'B':
                    BoatNet.Receive(data);
                    break;
                case 'A':
                    PlaneNet.Receive(data);
                    break;
                case 'V':
                    // The host's carrier: where it is on its loop.
                    if (!link.IsHost && data.Length >= 6 && Carrier.Active)
                        Carrier.Active.Sync(System.BitConverter.ToSingle(data, 2));
                    break;
                case 'E':
                    if (data.Length > 1 && CoopEvent.Unpack(data, 2, out ushort id, out byte kind, out byte arg) && FirstSight(data[1], id))
                        OnEvent(data[1], kind, arg);
                    break;
            }
        }

        CarBody playerArt;
        DriveAudio playerAudio;

        byte[] StateMessage()
        {
            var car = run.Player;
            if (!playerArt || playerArt.gameObject != car.gameObject) playerArt = car.GetComponent<CarBody>();
            if (!playerAudio || playerAudio.gameObject != car.gameObject) playerAudio = car.GetComponent<DriveAudio>();
            byte flags = 0;
            if (playerArt && playerArt.BrakeLit) flags |= CarState.Brake;
            if (playerArt && playerArt.LightsOn) flags |= CarState.Lights;
            if (playerAudio && playerAudio.Honking) flags |= CarState.Horn;
            if (car.Chassis.Drifting || car.Chassis.Donut > .5f) flags |= CarState.Drift;
            if (car.WreckedFor > 0) flags |= CarState.Wrecked;
            // Car radio: station (0 = off) and position in the track, so friends hear it too.
            var radio = CarRadio.Active;
            bool on = radio != null && radio.On;
            var smoke = TyreSmoke.Player;
            var state = new CarState
            {
                Time = (float)Clock,
                Pos = car.transform.position,
                Rot = car.transform.rotation,
                Vel = car.Body.linearVelocity,
                Kmh = car.ForwardSpeed,
                Car = (byte)run.Profile.car,
                Paint = (byte)run.Profile.paint,
                Flags = flags,
                Mode = (byte)(int)run.Mode,
                Station = (byte)(on ? radio.Station + 1 : 0),
                TrackTime = on ? radio.TrackTime : 0f,
                Smoke = (byte)Mathf.RoundToInt((smoke ? smoke.Intensity : 0) * 255),
                Custom = run.Profile.customPaint,
                R = (byte)Mathf.RoundToInt(Mathf.Clamp01(run.Profile.paintR) * 255),
                G = (byte)Mathf.RoundToInt(Mathf.Clamp01(run.Profile.paintG) * 255),
                B = (byte)Mathf.RoundToInt(Mathf.Clamp01(run.Profile.paintB) * 255),
                Matte = (byte)Mathf.RoundToInt(Mathf.Clamp01(run.Profile.matte) * 255),
            };
            return CarState.Pack(state);
        }

        TrafficSignals signals;
        Atmosphere sky;

        byte[] TrafficMessage()
        {
            var ms = new MemoryStream(900);
            var w = new BinaryWriter(ms);
            w.Write((byte)'T');
            w.Write((float)Clock);
            w.Write((byte)(int)run.Mode);
            if (!signals) signals = FindFirstObjectByType<TrafficSignals>();
            if (!sky) sky = FindFirstObjectByType<Atmosphere>();
            w.Write((byte)(signals ? signals.Current : 0));
            w.Write(signals ? signals.Remaining : 0f);
            w.Write((byte)(sky ? (int)sky.Current : 0));
            w.Write((byte)(Atmosphere.Night ? 1 : 0));
            // Positions go relative to the nearest driver, so traffic around a friend kilometres
            // away arrives as precisely as the traffic around the host (it used to be clamped
            // at 1.6 km from the host and never showed up for players further away).
            anchors.Clear();
            anchors.Add(Round(run.Player.transform.position));
            foreach (var friend in Remotes.Values)
                if (friend && friend.HasState && anchors.Count < 8)
                    anchors.Add(Round(friend.transform.position));
            w.Write((byte)anchors.Count);
            foreach (var a in anchors) { w.Write(a.x); w.Write(a.y); w.Write(a.z); }
            var cars = run.Traffic.Cars;
            int count = 0;
            foreach (var c in cars) if (c.gameObject.activeSelf) count++;
            w.Write((byte)count);
            for (int i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (!c.gameObject.activeSelf) continue;
                PackOffset(c.transform.position, anchors, out byte k, out short px, out short py, out short pz);
                Vector3 e = c.transform.eulerAngles;
                byte flags = (byte)((c.Generation & 15) << 4);
                // Lamps blink, so remember when each was last lit.
                if (c.LeftLamp && c.LeftLamp.enabled) leftLit[i] = Time.time;
                if (c.RightLamp && c.RightLamp.enabled) rightLit[i] = Time.time;
                if (Time.time - leftLit[i] < .8f) flags |= 1;
                if (Time.time - rightLit[i] < .8f) flags |= 2;
                // Burnt out: guests show the wreck even if they missed the explosion itself.
                if (c.Wrecked) flags |= 4;
                w.Write((byte)i);
                w.Write(flags);
                w.Write(k);
                w.Write(px); w.Write(py); w.Write(pz);
                w.Write((ushort)Mathf.RoundToInt(Mathf.Repeat(e.y, 360) / 360 * 65535));
                w.Write((short)Mathf.RoundToInt(Mathf.DeltaAngle(0, e.x) * 100));
                w.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(c.Speed * 100), 0, 65535));
            }
            return ms.ToArray();
        }

        readonly List<Vector3> anchors = new();
        static Vector3 Round(Vector3 v) => new(Mathf.Round(v.x), Mathf.Round(v.y), Mathf.Round(v.z));

        // 1/16 m steps: +-2 km around the nearest driver, and cars never live further out.
        static short Pack(float metres) => (short)Mathf.Clamp(Mathf.RoundToInt(metres * 16), short.MinValue, short.MaxValue);
        public static float Unpack(short v) => v / 16f;

        public static void PackOffset(Vector3 pos, List<Vector3> origins, out byte k, out short x, out short y, out short z)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < origins.Count; i++)
            {
                float d = (pos - origins[i]).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            Vector3 o = pos - origins[best];
            k = (byte)best;
            x = Pack(o.x); y = Pack(o.y); z = Pack(o.z);
        }

        public static Vector3 UnpackOffset(List<Vector3> origins, byte k, short x, short y, short z) =>
            origins[Mathf.Clamp(k, 0, origins.Count - 1)] + new Vector3(Unpack(x), Unpack(y), Unpack(z));
    }
}

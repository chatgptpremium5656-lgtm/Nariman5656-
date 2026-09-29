using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // Car explosions: a fireball, black smoke, sparks, a flash and a boom; everything nearby is
    // pushed away (other cars are knocked and damaged, which can set them off in turn); the
    // wreck burns for a while.
    public static class Explosion
    {
        public const float Radius = 14;
        static Material fire, smoke, charred;
        static AudioClip boom;
        public static int Count { get; private set; }

        public static Material Charred => charred ??= Art.Material("Charred", new Color(.035f, .032f, .03f), .1f, .15f);

        public static void Blow(TrafficCar car)
        {
            if (car.Wrecked) return;
            Vector3 at = car.transform.position + Vector3.up * .8f;
            car.Burn();
            var rb = car.GetComponent<Rigidbody>();
            if (rb && !rb.isKinematic)
            {
                rb.AddForce(Vector3.up * 7 + Random.insideUnitSphere * 2, ForceMode.VelocityChange);
                rb.AddTorque(Random.insideUnitSphere * 1.5f, ForceMode.VelocityChange);
            }
            Effects(at, car.transform, 1);
            Push(at, car.gameObject);
        }

        // Online guest: the host's traffic car blew up. The copy here is burnt and the blast is
        // played where it is; the car keeps following the host's snapshots.
        public static void BlowMirrored(TrafficCar car)
        {
            if (car.Wrecked) return;
            car.Char();
            Vector3 at = car.transform.position + Vector3.up * .8f;
            Effects(at, car.transform, 1);
            Push(at, car.gameObject, false);
        }

        // A friend's car online blew up: the same blast where their car is. It only looks
        // burnt (the friend's own game throws the car about and sends where it goes).
        public static void BlowRemote(Transform car)
        {
            Vector3 at = car.position + Vector3.up * .8f;
            Effects(at, car, 1.1f);
            Push(at, car.gameObject, false);
        }

        // The player's car: thrown up, burnt, and put back on the road a few seconds later.
        public static void Blow(Vehicle car)
        {
            Vector3 at = car.transform.position + Vector3.up * .8f;
            car.Body.AddForce(Vector3.up * 6.5f + Random.insideUnitSphere * 1.5f, ForceMode.VelocityChange);
            car.Body.AddTorque(new Vector3(Random.Range(-1f, 1f), Random.Range(-.5f, .5f), Random.Range(-1f, 1f)) * 1.2f, ForceMode.VelocityChange);
            Effects(at, car.transform, 1.1f);
            Push(at, car.gameObject);
        }

        // A parked car: thrown up, burnt, it stays a wreck until its bay is rebuilt.
        public static void Blow(ParkedCarBody car)
        {
            Vector3 at = car.transform.position + Vector3.up * .8f;
            var rb = car.Body;
            if (rb)
            {
                rb.AddForce(Vector3.up * 6 + Random.insideUnitSphere * 2, ForceMode.VelocityChange);
                rb.AddTorque(Random.insideUnitSphere * 1.5f, ForceMode.VelocityChange);
            }
            Effects(at, car.transform, 1);
            Push(at, car.gameObject);
        }

        // A bomb: the same blast in the open, nothing burns afterwards.
        public static void Blast(Vector3 at, bool shared = true)
        {
            Effects(at, null, 1);
            Push(at, null, shared);
        }

        // `shared`: false for blasts that happened in somebody else's game (online): only the
        // local car and parked cars are pushed; the traffic belongs to the host.
        static void Push(Vector3 at, GameObject source, bool shared = true)
        {
            Wanted.Blasted(at, source, shared);    // the police: hurt near it, and a crime if it was the player's
            // People on foot nearby are hurt and thrown (every game hurts only its own player).
            FootPlayer.Active?.Blast(at, Radius);
            var seen = new HashSet<Rigidbody>();
            foreach (var col in Physics.OverlapSphere(at, Radius))
            {
                // Parked cars first: the untouched ones sit on their city tile's body.
                var knock = col.GetComponent<Knockable>();
                if (knock)
                {
                    knock.Blast(at, Mathf.Clamp01(1 - Vector3.Distance(knock.transform.position, at) / Radius));
                    continue;
                }
                var parked = col.GetComponent<ParkedCarBody>();
                if (parked)
                {
                    if (parked.gameObject != source && !parked.Wrecked)
                        parked.Blast(at, Mathf.Clamp01(1 - Vector3.Distance(parked.transform.position, at) / Radius));
                    continue;
                }
                var rb = col.attachedRigidbody;
                if (!rb || rb.gameObject == source || !seen.Add(rb)) continue;
                float d = Vector3.Distance(rb.worldCenterOfMass, at);
                float f = Mathf.Clamp01(1 - d / Radius);
                if (f <= 0) continue;
                Vector3 away = rb.worldCenterOfMass - at; away.y = 0;
                away = (away.sqrMagnitude > .01f ? away.normalized : Random.onUnitSphere) + Vector3.up * .45f;
                var aircraft = rb.GetComponent<Airplane>();
                if (aircraft)
                {
                    if (!aircraft.Remote)
                    {
                        rb.AddForce(away * (6 * f + 1), ForceMode.VelocityChange);
                        aircraft.Damage(120 * f);
                    }
                    continue;
                }
                var boat = rb.GetComponent<Boat>();
                if (boat)
                {
                    if (!boat.Remote)
                    {
                        rb.AddForce(away * (8 * f + 2), ForceMode.VelocityChange);
                        boat.Damage(110 * f);
                    }
                    continue;
                }
                var traffic = rb.GetComponent<TrafficCar>();
                // The traffic belongs to the host: every blast (ours, or a friend's replayed here)
                // moves and damages it there only, and the result comes back to the guests.
                if (traffic && Net.CoopNet.IsGuest) continue;
                if (traffic)
                {
                    if (!traffic.Wrecked) traffic.Stun(2.5f + f * 2);
                    rb.AddForce(away * (13 * f + 3), ForceMode.VelocityChange);
                    rb.AddTorque(Random.insideUnitSphere * 2 * f, ForceMode.VelocityChange);
                    traffic.Damage(70 * f);
                    continue;
                }
                var player = rb.GetComponent<Vehicle>();
                if (player)
                {
                    rb.AddForce(away * (9 * f + 2), ForceMode.VelocityChange);
                    player.Damage.Apply(player.transform.InverseTransformPoint(at), 12 + 30 * f);
                    // Right next to a blast (a bomb under it, a car going up alongside) our car goes up too.
                    // An empty car (its driver on foot) goes up from a little further away.
                    if (f > (FootManager.OnFoot ? .35f : .6f) || player.Damage.Fatal) player.Explode();
                    continue;
                }
                if (!rb.isKinematic) rb.AddForce(away * 10 * f, ForceMode.VelocityChange);
            }
        }

        static void Effects(Vector3 at, Transform wreck, float scale)
        {
            Count++;
            Materials();
            var root = new GameObject("Explosion").transform;
            root.position = at;
            // Fireball.
            Burst(root, fire, 46, new Vector2(.35f, .9f), new Vector2(5, 13) * scale, new Vector2(2.2f, 5.5f) * scale,
                  new Color(1, .75f, .35f), new Color(1, .35f, .08f), 0, -.2f);
            // Black smoke rolling up.
            Burst(root, smoke, 34, new Vector2(2.6f, 4.6f), new Vector2(2, 6) * scale, new Vector2(3.5f, 7) * scale,
                  new Color(.12f, .11f, .10f, .85f), new Color(.25f, .23f, .22f, .7f), .25f, -.12f);
            // Sparks and debris.
            Burst(root, fire, 70, new Vector2(.5f, 1.4f), new Vector2(12, 26) * scale, new Vector2(.12f, .3f),
                  new Color(1, .85f, .5f), new Color(1, .55f, .2f), 0, 1.6f);
            var light = new GameObject("Blast light").AddComponent<Light>();
            light.transform.SetParent(root, false);
            light.type = LightType.Point;
            light.range = 40 * scale;
            light.color = new Color(1, .62f, .3f);
            light.intensity = 900;
            light.shadows = LightShadows.None;
            root.gameObject.AddComponent<Fade>().Init(light, 7);
            // The wreck keeps burning.
            if (wreck)
            {
                var flames = new GameObject("Wreck fire").transform;
                flames.SetParent(wreck, false);
                flames.localPosition = Vector3.up * .9f;
                Loop(flames, fire, 22, new Vector2(.5f, 1.1f), new Vector2(1.5f, 3), new Vector2(1, 2.2f), new Color(1, .6f, .25f), new Color(1, .3f, .05f), -.35f);
                Loop(flames, smoke, 9, new Vector2(3, 5), new Vector2(1.5f, 3), new Vector2(2.5f, 5), new Color(.1f, .1f, .1f, .7f), new Color(.2f, .2f, .2f, .55f), -.15f);
                flames.gameObject.AddComponent<Fade>().Init(null, 14);
            }
            // The boom.
            var sound = new GameObject("Boom").AddComponent<AudioSource>();
            sound.transform.position = at;
            sound.clip = Boom();
            sound.spatialBlend = .85f;
            sound.minDistance = 18;
            sound.maxDistance = 600;
            sound.rolloffMode = AudioRolloffMode.Logarithmic;
            sound.volume = 1;
            sound.pitch = Random.Range(.9f, 1.08f);
            sound.Play();
            Object.Destroy(sound.gameObject, 4);
            // Shake the camera if the driver is close.
            var cam = Camera.main;
            if (cam)
            {
                float d = Vector3.Distance(cam.transform.position, at);
                DriveCamera.Shake = Mathf.Max(DriveCamera.Shake, Mathf.Clamp01(1 - d / 70) * 1.2f);
            }
        }

        static ParticleSystem Burst(Transform parent, Material mat, int count, Vector2 life, Vector2 speed, Vector2 size, Color a, Color b, float gravity, float gravityEnd)
        {
            var ps = Make(parent, mat, life, speed, size, a, b, gravityEnd);
            var em = ps.emission;
            em.rateOverTime = 0;
            em.SetBursts(new[] { new ParticleSystem.Burst(0, (short)count) });
            ps.Play();
            return ps;
        }

        static void Loop(Transform parent, Material mat, float rate, Vector2 life, Vector2 speed, Vector2 size, Color a, Color b, float gravity)
        {
            var ps = Make(parent, mat, life, speed, size, a, b, gravity);
            var em = ps.emission;
            em.rateOverTime = rate;
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Box;
            sh.scale = new Vector3(1.6f, .4f, 3.6f);
            var v = ps.velocityOverLifetime;
            v.enabled = true;
            v.space = ParticleSystemSimulationSpace.World;
            v.x = new ParticleSystem.MinMaxCurve(0, 0);
            v.y = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
            v.z = new ParticleSystem.MinMaxCurve(0, 0);
            ps.Play();
        }

        static ParticleSystem Make(Transform parent, Material mat, Vector2 life, Vector2 speed, Vector2 size, Color a, Color b, float gravity)
        {
            var go = new GameObject("Blast particles");
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.duration = 1;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startRotation = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
            main.startColor = new ParticleSystem.MinMaxGradient(a, b);
            main.gravityModifier = gravity;
            main.maxParticles = 200;
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = .8f;
            var sz = ps.sizeOverLifetime;
            sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.EaseInOut(0, .5f, 1, 1.6f));
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(new Color(.55f, .5f, .45f), 1) },
                      new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(.8f, .35f), new GradientAlphaKey(0, 1) });
            col.color = g;
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = .6f;
            noise.frequency = .4f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        static void Materials()
        {
            if (fire) return;
            var tex = Soft();
            fire = Particle(tex, true);
            smoke = Particle(tex, false);
        }

        static Texture2D Soft()
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x - n / 2f + .5f, y - n / 2f + .5f).magnitude / (n / 2f);
                    float puff = .7f + .3f * Mathf.PerlinNoise(x * .14f + 3, y * .14f + 9);
                    float a = Mathf.Clamp01(1 - d) * puff;
                    t.SetPixel(x, y, new Color(1, 1, 1, a * a));
                }
            t.Apply(true);
            return t;
        }

        static Material Particle(Texture tex, bool additive)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", additive ? new Color(2.2f, 1.7f, 1.2f, 1) : Color.white);
            m.SetFloat("_Surface", 1);
            m.SetFloat("_Blend", additive ? 2 : 0);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            m.SetFloat("_ZWrite", 0);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return m;
        }

        // A deep boom: a sharp crack, then rolling low rumble.
        static AudioClip Boom()
        {
            if (boom) return boom;
            int rate = 22050, n = (int)(rate * 2.6f);
            var data = new float[n];
            var rng = new System.Random(3);
            float brown = 0, low = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float white = (float)(rng.NextDouble() * 2 - 1);
                brown = Mathf.Clamp(brown + white * .08f, -1, 1) * .995f;
                low += (brown - low) * .06f;
                float crack = white * Mathf.Exp(-t * 55) * .9f;
                float rumble = low * 3.2f * Mathf.Exp(-t * 1.5f) * Mathf.Clamp01(t * 60);
                float thump = Mathf.Sin(2 * Mathf.PI * (48 - t * 12) * t) * Mathf.Exp(-t * 4) * .8f;
                data[i] = Mathf.Clamp(crack + rumble + thump, -1, 1) * .95f;
            }
            boom = AudioClip.Create("Explosion", n, 1, rate, false);
            boom.SetData(data, 0);
            return boom;
        }

        // Fades the flash, stops the particles and cleans up.
        sealed class Fade : MonoBehaviour
        {
            Light light;
            float life, age;
            public void Init(Light l, float seconds) { light = l; life = seconds; }
            void Update()
            {
                age += Time.deltaTime;
                if (light) light.intensity = 900 * Mathf.Exp(-age * 6);
                if (age > life - 2)
                    foreach (var ps in GetComponentsInChildren<ParticleSystem>())
                    {
                        var em = ps.emission;
                        em.rateOverTime = 0;
                    }
                if (age > life) Destroy(gameObject);
            }
        }
    }
}

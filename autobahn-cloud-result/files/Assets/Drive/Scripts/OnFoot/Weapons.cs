using UnityEngine;

namespace Autobahn
{
    // The guns (Low Poly Weapons VOL.1 models, Easy FPS sounds) and what they do. Hitscan: a shot
    // is a ray from the middle of the screen; shotguns fire several pellets.
    public sealed class WeaponDef
    {
        public string Name, Model;
        public float Damage, Interval, Spread, Range, Reload, Length, Pitch, Zoom;
        // Recoil per shot: how far the aim climbs (degrees of Pitch) and how much wider the next
        // shots spread (degrees, grows over a burst). Also sets how hard the gun kicks on screen.
        public float Kick, Bloom;
        public int Magazine, Pellets = 1;
        public bool Auto, Melee, Thrown;
    }

    public static class Weapons
    {
        public static readonly WeaponDef[] All =
        {
            new() { Name = "КУЛАКИ", Melee = true, Damage = 25, Interval = .6f, Range = 1.9f, Magazine = 0 },
            new() { Name = "ПИСТОЛЕТ M1911", Model = "M1911", Damage = 34, Interval = .22f, Spread = .5f, Range = 150, Reload = 1.4f, Magazine = 8, Length = .22f, Pitch = 1.15f, Zoom = 50, Kick = 1.4f, Bloom = .35f },
            new() { Name = "UZI", Model = "Uzi", Damage = 17, Interval = .065f, Spread = 2.4f, Range = 120, Reload = 1.6f, Magazine = 25, Length = .42f, Pitch = 1.25f, Auto = true, Zoom = 50, Kick = .55f, Bloom = .3f },
            new() { Name = "АК-74", Model = "AK74", Damage = 29, Interval = .1f, Spread = 1.1f, Range = 300, Reload = 2.2f, Magazine = 30, Length = .92f, Pitch = .95f, Auto = true, Zoom = 42, Kick = .9f, Bloom = .3f },
            new() { Name = "M4", Model = "M4_8", Damage = 26, Interval = .085f, Spread = .9f, Range = 300, Reload = 2f, Magazine = 30, Length = .88f, Pitch = 1.02f, Auto = true, Zoom = 40, Kick = .75f, Bloom = .25f },
            new() { Name = "ДРОБОВИК", Model = "Bennelli_M4", Damage = 13, Pellets = 9, Interval = .75f, Spread = 5f, Range = 60, Reload = 2.6f, Magazine = 7, Length = 1.0f, Pitch = .72f, Zoom = 50, Kick = 4.5f, Bloom = 1.5f },
            new() { Name = "СНАЙПЕРКА M107", Model = "M107", Damage = 110, Interval = 1.2f, Spread = 0, Range = 700, Reload = 3f, Magazine = 5, Length = 1.4f, Pitch = .6f, Zoom = 14, Kick = 7f, Bloom = 2f },
            // Thrown along an arc (Bomb.cs); the "reload" is fetching three more.
            new() { Name = "БОМБА", Thrown = true, Damage = 0, Interval = .8f, Range = 40, Reload = 3.5f, Magazine = 3, Length = .2f, Zoom = 60 },
        };

        static AudioClip shot, reload, pull, hitMarker, die;
        public static AudioClip Shot => shot ??= Resources.Load<AudioClip>("Sounds/Foot/shotSound");
        public static AudioClip ReloadSound => reload ??= Resources.Load<AudioClip>("Sounds/Foot/reloadSound");
        public static AudioClip Pull => pull ??= Resources.Load<AudioClip>("Sounds/Foot/pullweapon");
        public static AudioClip HitMarker => hitMarker ??= Resources.Load<AudioClip>("Sounds/Foot/hitMarker");
        public static AudioClip Die => die ??= Resources.Load<AudioClip>("Sounds/Foot/playerSound/die-01");

        // A gun model, turned so its barrel points along +z, scaled to its real length, with the
        // muzzle position in its own frame.
        public static Transform Model(WeaponDef def, Transform parent, out Vector3 muzzle)
        {
            muzzle = Vector3.forward * .3f;
            if (def != null && def.Thrown) return Bomb.HeldModel(parent);
            if (def == null || string.IsNullOrEmpty(def.Model)) return null;
            var source = Resources.Load<GameObject>("Weapons/LowPoly/Prefabs/" + def.Model);
            if (!source) return null;
            var holder = new GameObject(def.Name).transform;
            holder.SetParent(parent, false);
            var gun = Object.Instantiate(source, holder, false).transform;
            foreach (var c in gun.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);
            foreach (var r in gun.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.gameObject.layer = parent.gameObject.layer;
            }
            gun.gameObject.layer = parent.gameObject.layer;
            // Bounds in the holder's frame: the longest axis is the barrel.
            Bounds b = default; bool any = false;
            foreach (var mf in gun.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh) continue;
                var m = holder.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var w = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(w, Vector3.zero); any = true; } else b.Encapsulate(w);
                }
            }
            if (!any) return holder;
            Vector3 size = b.size;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.z >= size.y ? 2 : 1;
            // Every model in this pack points its muzzle down -z with the grip below: turn it
            // round the vertical so the barrel points +z and the grip stays down.
            gun.localRotation = Quaternion.Euler(0, 180, 0) * gun.localRotation;
            Vector3 barrel = Vector3.back;
            float scale = def.Length / Mathf.Max(.01f, size[axis]);
            gun.localScale *= scale;
            // Grip near the holder: shift so the middle of the gun sits a little ahead of the hand.
            Vector3 centre = Quaternion.Euler(0, 180, 0) * b.center * scale;
            gun.localPosition = new Vector3(-centre.x, -centre.y, -centre.z + def.Length * .25f);
            muzzle = new Vector3(0, 0, def.Length * .75f);
            return holder;
        }

        // --- effects ------------------------------------------------------------------------
        static Material flashMat, tracerMat, sparkMat, bloodMat;

        static void Materials()
        {
            if (flashMat) return;
            flashMat = Art.Material("Muzzle flash", new Color(1f, .75f, .3f), 0, 0, true);
            flashMat.SetColor("_EmissionColor", new Color(1f, .55f, .15f) * 4);
            tracerMat = Art.Material("Tracer", new Color(1f, .85f, .5f), 0, 0, true);
            tracerMat.SetColor("_EmissionColor", new Color(1f, .8f, .45f) * 3);
            sparkMat = Art.Material("Bullet spark", new Color(1f, .8f, .4f), 0, 0, true);
            sparkMat.SetColor("_EmissionColor", new Color(1f, .7f, .3f) * 4);
            bloodMat = Art.Material("Blood", new Color(.45f, .02f, .02f), 0, .4f);
        }

        public static void Flash(Vector3 at, Vector3 dir)
        {
            Materials();
            var f = Art.Primitive(null, "Muzzle flash", at, Vector3.one * Random.Range(.16f, .26f), flashMat, PrimitiveType.Sphere);
            f.localScale = new Vector3(.07f, .07f, .16f) * Random.Range(.8f, 1.3f);
            f.rotation = Quaternion.LookRotation(dir);
            f.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var l = f.gameObject.AddComponent<Light>();
            l.type = LightType.Point; l.range = 7; l.intensity = 60; l.color = new Color(1, .75f, .4f); l.shadows = LightShadows.None;
            Object.Destroy(f.gameObject, .05f);
        }

        public static void Tracer(Vector3 from, Vector3 to)
        {
            Materials();
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 1) return;
            var t = Art.Box(null, "Tracer", (from + to) / 2, new Vector3(.018f, .018f, Mathf.Min(len, 60)), tracerMat);
            t.rotation = Quaternion.LookRotation(d);
            t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Object.Destroy(t.gameObject, .04f);
        }

        public static void Impact(Vector3 at, Vector3 normal, bool flesh)
        {
            Materials();
            for (int i = 0; i < (flesh ? 5 : 3); i++)
            {
                var s = Art.Box(null, flesh ? "Blood" : "Spark", at + normal * .03f, Vector3.one * (flesh ? .07f : .04f), flesh ? bloodMat : sparkMat);
                var rb = s.gameObject.AddComponent<Rigidbody>();
                rb.useGravity = true;
                rb.linearVelocity = (normal + Random.insideUnitSphere * .8f) * Random.Range(1.5f, 4f);
                s.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                Object.Destroy(s.gameObject, flesh ? .6f : .25f);
            }
        }

        public static void Sound(AudioClip clip, Vector3 at, float volume = 1, float pitch = 1)
        {
            if (!clip) return;
            var go = new GameObject("Gun sound");
            go.transform.position = at;
            var a = go.AddComponent<AudioSource>();
            a.clip = clip;
            a.volume = volume;
            a.pitch = pitch * Random.Range(.96f, 1.04f);
            a.spatialBlend = .85f;
            a.minDistance = 4;
            a.maxDistance = 180;
            a.Play();
            Object.Destroy(go, clip.length / Mathf.Max(.3f, a.pitch) + .1f);
        }
    }
}

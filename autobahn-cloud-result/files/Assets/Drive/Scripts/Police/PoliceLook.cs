using UnityEngine;

namespace Autobahn
{
    // How the police look and sound. The real models (Incoming/police_car.glb and
    // Incoming/police-man-rigged.zip) are not imported yet: once they are in a Resources folder
    // under these paths they are used. Until then the car is a black-and-white Mercedes and
    // the officer the usual person in a dark blue uniform.
    public static class PoliceLook
    {
        public const string CarModelPath = "Police/PoliceCar";
        public const string OfficerModelPath = "Police/PoliceOfficer";
        public static readonly Color Uniform = new(.10f, .14f, .30f);
        static Material white, barBase, redLit, blueLit;
        static AudioClip siren;

        // The roof lights of one car: lit lenses and the lights they throw, flashed by the car.
        public sealed class LightBar
        {
            public Renderer Red, Blue;
            public Light RedLight, BlueLight;
        }

        static void Materials()
        {
            if (white) return;
            white = Art.Material("Police white", new Color(.92f, .92f, .9f), 0, .55f);
            barBase = Art.Material("Police light bar", new Color(.08f, .08f, .09f), .3f, .5f);
            redLit = Art.Material("Police red", new Color(1, .06f, .04f), 0, .4f, true);
            redLit.SetColor("_EmissionColor", new Color(1, .06f, .04f) * 6);
            blueLit = Art.Material("Police blue", new Color(.08f, .2f, 1), 0, .4f, true);
            blueLit.SetColor("_EmissionColor", new Color(.08f, .2f, 1) * 6);
        }

        // Builds the body under `car` (unrotated) and returns its size: width, height, length.
        public static Vector3 BuildCar(Transform car)
        {
            Materials();
            var model = Resources.Load<GameObject>(CarModelPath);
            if (model)
            {
                var m = Object.Instantiate(model, car, false).transform;
                m.name = "Police model";
                foreach (var c in m.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);
                Bounds b = default;
                bool any = false;
                foreach (var r in m.GetComponentsInChildren<Renderer>(true))
                {
                    if (!any) { b = r.bounds; any = true; }
                    else b.Encapsulate(r.bounds);
                }
                if (!any) return new Vector3(1.9f, 1.45f, 4.9f);
                // Wheels on the ground, centred on the body.
                m.position -= new Vector3(b.center.x - car.position.x, b.min.y - car.position.y, b.center.z - car.position.z);
                return b.size;
            }
            var art = car.gameObject.AddComponent<CarBody>();
            art.Build(new Color(.03f, .03f, .035f), false, BodyStyle.Mercedes);
            var shape = art.Shape;
            bool imported = !string.IsNullOrEmpty(shape.Prefab);
            float height = imported ? shape.Height > .5f ? shape.Height : 1.45f : shape.Top[3] * 1.1f;
            if (imported && shape.ExtraWheels == null && !art.Visual.GetComponentInChildren<SkinnedMeshRenderer>(true))
            {
                // As the traffic: one renderer for the body and one per wheel.
                Art.MergeModel(art.Visual, "Police " + BodyStyle.Mercedes);
                for (int w = 0; w < art.Wheels.Length; w++) Art.MergeModel(art.Wheels[w], "Police " + BodyStyle.Mercedes + " wheel " + w);
            }
            // White doors on the black car.
            foreach (float side in new[] { -1f, 1f })
                Art.Box(car, "Police door", new Vector3(side * shape.Width * .5f, height * .42f, -shape.Length * .04f), new Vector3(.02f, height * .2f, shape.Length * .36f), white);
            return new Vector3(shape.Width, height, shape.Length);
        }

        public static LightBar AddLightBar(Transform car, Vector3 size)
        {
            Materials();
            float y = size.y + .04f, z = -size.z * .05f;
            var bar = new LightBar();
            Art.Box(car, "Light bar", new Vector3(0, y, z), new Vector3(size.x * .6f, .08f, .26f), barBase);
            bar.Red = Art.Box(car, "Light bar red", new Vector3(-size.x * .16f, y + .05f, z), new Vector3(size.x * .26f, .09f, .22f), redLit).GetComponent<Renderer>();
            bar.Blue = Art.Box(car, "Light bar blue", new Vector3(size.x * .16f, y + .05f, z), new Vector3(size.x * .26f, .09f, .22f), blueLit).GetComponent<Renderer>();
            bar.Red.shadowCastingMode = bar.Blue.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bar.RedLight = Lamp(car, new Vector3(-size.x * .3f, y + .35f, z), new Color(1, .1f, .06f));
            bar.BlueLight = Lamp(car, new Vector3(size.x * .3f, y + .35f, z), new Color(.15f, .3f, 1));
            return bar;
        }

        static Light Lamp(Transform car, Vector3 at, Color colour)
        {
            var l = new GameObject("Flasher").AddComponent<Light>();
            l.transform.SetParent(car, false);
            l.transform.localPosition = at;
            l.type = LightType.Point;
            l.range = 16;
            l.intensity = 0;
            l.color = colour;
            l.shadows = LightShadows.None;
            l.enabled = false;
            return l;
        }

        // The officer's body. The rigged police model will need the same humanoid setup as the
        // person (HumanRig); until then the person in uniform colours.
        public static HumanRig Officer(Transform parent) => HumanRig.Create(parent, Uniform, true);

        // German two-tone siren (Martinshorn): 440 and 585 Hz, 0.6 s each. Both tones end on a
        // whole number of cycles, so the loop joins without a click. Buzzy like the horn.
        public static AudioClip Siren()
        {
            if (siren) return siren;
            const int rate = 44100;
            var data = new float[rate * 6 / 5];
            double phase = 0;
            for (int i = 0; i < data.Length; i++)
            {
                double f = i < data.Length / 2 ? 440 : 585;
                phase += f / rate;
                float a = (float)System.Math.Sin(2 * System.Math.PI * phase);
                float b = (float)System.Math.Sin(4 * System.Math.PI * phase);
                data[i] = (Mathf.Clamp(a * 1.8f, -1, 1) * .75f + b * .2f) * .42f;
            }
            siren = AudioClip.Create("Siren", data.Length, 1, rate, false);
            siren.SetData(data, 0);
            return siren;
        }
    }
}

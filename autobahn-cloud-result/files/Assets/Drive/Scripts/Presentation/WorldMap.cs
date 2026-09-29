using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Autobahn
{
    // Full-screen map on M: a real top-down view of the world rendered by an orthographic
    // camera, with the car, the friend and the fuel stations marked. Drag or WASD to pan,
    // scroll or +/- to zoom, Space to centre on the car, M or Esc to close.
    public sealed class WorldMap : MonoBehaviour
    {
        // On foot the map centres on the person, not the parked car.
        Transform Focus => FootPlayer.Active && !FootPlayer.Active.Riding ? FootPlayer.Active.transform : run.Player.transform;

        public static bool Open { get; private set; }
        public static int ClosedFrame = -1;

        Camera mapCamera;
        RenderTexture target;
        Light fill;
        Vector3 focus;
        float zoom = 380;
        bool savedFog;
        // The island is streamed in round the player: a view reaching past what is built shows
        // the map picture instead of the live world.
        bool live = true;
        RunSession run;

        public void Init(RunSession session) => run = session;

        void Setup()
        {
            if (mapCamera) return;
            var go = new GameObject("Map camera");
            go.transform.SetParent(transform, false);
            mapCamera = go.AddComponent<Camera>();
            mapCamera.orthographic = true;
            mapCamera.clearFlags = CameraClearFlags.SolidColor;
            mapCamera.backgroundColor = new Color(.04f, .15f, .27f);
            mapCamera.nearClipPlane = 1;
            mapCamera.farClipPlane = 1600;
            mapCamera.allowHDR = false;
            mapCamera.allowMSAA = false;
            var data = mapCamera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.renderShadows = false;
            data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            data.requiresDepthOption = CameraOverrideOption.Off;
            mapCamera.enabled = false;
            target = new RenderTexture(1600, 1000, 24) { name = "World map" };
            mapCamera.targetTexture = target;

            // A soft light used only while the map renders, so the map also reads at night.
            var lightObject = new GameObject("Map light");
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.rotation = Quaternion.Euler(70, -30, 0);
            fill = lightObject.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = .9f;
            fill.shadows = LightShadows.None;
            fill.enabled = false;

            RenderPipelineManager.beginCameraRendering += Begin;
            RenderPipelineManager.endCameraRendering += End;
        }

        void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= Begin;
            RenderPipelineManager.endCameraRendering -= End;
            if (target) target.Release();
        }

        void Begin(ScriptableRenderContext context, Camera cam)
        {
            if (cam != mapCamera) return;
            savedFog = RenderSettings.fog;
            RenderSettings.fog = false;
            fill.enabled = Atmosphere.Night;
        }

        void End(ScriptableRenderContext context, Camera cam)
        {
            if (cam != mapCamera) return;
            RenderSettings.fog = savedFog;
            fill.enabled = false;
        }

        public void Toggle()
        {
            if (Open) Close();
            else Show();
        }

        void Show()
        {
            if (run == null || run.Paused || run.ChoosingMode) return;
            Setup();
            Open = true;
            focus = Focus.position;
            mapCamera.enabled = true;
            RoadWorld.ShowAll = true;
            DriverInput.Blocked = true;
            if (!Net.CoopNet.Online) Time.timeScale = 0;
        }

        public void Close()
        {
            if (!Open) return;
            Open = false;
            ClosedFrame = Time.frameCount;
            if (mapCamera) mapCamera.enabled = false;
            RoadWorld.ShowAll = false;
            DriverInput.Blocked = run != null && run.Paused;
            if (run == null || !run.Paused) Time.timeScale = 1;
        }

        void Update()
        {
            var k = Keyboard.current;
            var pad = Gamepad.current;
            if (run == null || (k == null && pad == null)) return;
            // The pad: the DualSense touchpad button (or View / Back on other pads) opens the map.
            bool padMap = pad is UnityEngine.InputSystem.DualShock.DualSenseGamepadHID ds ? ds.touchpadButton.wasPressedThisFrame : pad != null && pad.selectButton.wasPressedThisFrame && DriverInput.OnFoot;
            bool mapKey = (k?.mKey.wasPressedThisFrame ?? false) || padMap;
            if (!Open)
            {
                if (mapKey && !run.Paused && !run.ChoosingMode)
                    Show();
                return;
            }
            if (mapKey || (k?.escapeKey.wasPressedThisFrame ?? false) || (pad?.buttonEast.wasPressedThisFrame ?? false) || run.Paused)
            {
                Close();
                return;
            }

            float dt = Time.unscaledDeltaTime;
            Vector2 pan = pad != null ? pad.leftStick.ReadValue() : Vector2.zero;
            if (k != null)
            {
                if (k.wKey.isPressed || k.upArrowKey.isPressed) pan.y += 1;
                if (k.sKey.isPressed || k.downArrowKey.isPressed) pan.y -= 1;
                if (k.dKey.isPressed || k.rightArrowKey.isPressed) pan.x += 1;
                if (k.aKey.isPressed || k.leftArrowKey.isPressed) pan.x -= 1;
            }
            if (pad != null)
            {
                float z = pad.rightStick.ReadValue().y + pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue();
                if (Mathf.Abs(z) > .15f) zoom *= 1 - z * dt * 1.5f;
                if (pad.buttonSouth.wasPressedThisFrame) focus = Focus.position;
            }
            focus += new Vector3(pan.x, 0, pan.y) * zoom * 1.2f * dt;

            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.leftButton.isPressed)
                {
                    // Pixels to metres: the view is 2 * zoom metres tall.
                    Vector2 d = mouse.delta.ReadValue();
                    float perPixel = 2 * zoom / Mathf.Max(1, Screen.height * .86f);
                    focus -= new Vector3(d.x, 0, d.y) * perPixel;
                }
                float wheel = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(wheel) > .01f)
                    zoom *= Mathf.Pow(.9f, Mathf.Sign(wheel));
            }
            if (k != null && (k.equalsKey.isPressed || k.numpadPlusKey.isPressed)) zoom *= 1 - dt * 1.5f;
            if (k != null && (k.minusKey.isPressed || k.numpadMinusKey.isPressed)) zoom *= 1 + dt * 1.5f;
            zoom = Mathf.Clamp(zoom, 60, 3600);
            if (k != null && (k.spaceKey.wasPressedThisFrame || k.cKey.wasPressedThisFrame))
                focus = Focus.position;

            mapCamera.orthographicSize = zoom;
            mapCamera.aspect = 1.6f;
            mapCamera.transform.SetPositionAndRotation(new Vector3(focus.x, 1200, focus.z), Quaternion.Euler(90, 0, 0));
            var view = Rect.MinMaxRect(focus.x - zoom * 1.6f, focus.z - zoom, focus.x + zoom * 1.6f, focus.z + zoom);
            live = Route.Endless || !IslandSnapshot.Ready || WorldStreamer.Active == null || WorldStreamer.Active.Covers(view);
            mapCamera.enabled = live;
        }

        // Called from the HUD's OnGUI inside its 1440 x 900 space.
        public void Draw(System.Func<int, Color, FontStyle, TextAnchor, GUIStyle> style, Color amber, Color cream)
        {
            if (!Open || target == null) return;
            var area = new Rect(40, 60, 1360, 850);
            // The full-screen shade behind the map is drawn by the HUD, in screen space.
            if (live) GUI.DrawTexture(area, target, ScaleMode.ScaleAndCrop);
            else
            {
                // The same view cut from the map picture (both 1.6 : 1, north up).
                Vector2 lo = IslandSnapshot.ToUV(new Vector3(focus.x - zoom * 1.6f, 0, focus.z - zoom));
                Vector2 hi = IslandSnapshot.ToUV(new Vector3(focus.x + zoom * 1.6f, 0, focus.z + zoom));
                GUI.DrawTextureWithTexCoords(area, IslandSnapshot.Image, new Rect(lo.x, lo.y, hi.x - lo.x, hi.y - lo.y));
            }

            // Markers: project world points into the map rectangle.
            Vector2 ToMap(Vector3 world)
            {
                Vector3 v = mapCamera.WorldToViewportPoint(world);
                // ScaleAndCrop: the texture is 1.6:1, the area 1.6:1 as well.
                return new Vector2(area.x + v.x * area.width, area.y + (1 - v.y) * area.height);
            }
            bool Inside(Vector2 p) => area.Contains(p);

            if (!Route.Endless)
                foreach (var site in Station.Sites)
                {
                    Vector3 w = Route.Center(site.Z) + Route.Right(site.Z) * site.Side * 30;
                    Vector2 m = ToMap(w);
                    if (!Inside(m)) continue;
                    GUI.color = new Color(.05f, .07f, .07f, .9f);
                    GUI.DrawTexture(new Rect(m.x - 22, m.y - 11, 44, 22), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                    GUI.Label(new Rect(m.x - 22, m.y - 11, 44, 22), "АЗС", style(12, amber, FontStyle.Bold, TextAnchor.MiddleCenter));
                }

            // Place names, as on the island plan.
            if (!Route.Endless)
            {
                var names = new (string, Vector3)[]
                {
                    ("ГОРОД", new Vector3(Island.CityW / 2, 0, Island.CityH / 2)),
                    ("ПОРТ", new Vector3(650, 0, Island.QuayZ - 120)),
                    ("АЭРОПОРТ", new Vector3(700, 0, 1200)),
                    ("ГОРЫ", new Vector3(760, 0, 3000)),
                    ("ЛЕС", new Vector3(Island.CityW + 230, 0, 2700)),
                    ("МОРЕ", new Vector3(-520, 0, 2600)),
                    ("СЕВЕРНЫЙ", Route.Center(Island.TownS) + Vector3.forward * 90),
                };
                foreach (var (label, at) in names)
                {
                    Vector2 m = ToMap(at);
                    if (!Inside(m)) continue;
                    float w = label.Length * 13 + 30;
                    GUI.color = new Color(.05f, .07f, .07f, .85f);
                    GUI.DrawTexture(new Rect(m.x - w / 2, m.y - 15, w, 30), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                    GUI.Label(new Rect(m.x - w / 2, m.y - 15, w, 30), label, style(16, cream, FontStyle.Bold, TextAnchor.MiddleCenter));
                }
            }

            if (Net.CoopNet.Online)
                foreach (var kv in Net.CoopNet.Active.Remotes)
                    if (kv.Value && kv.Value.HasState)
                        Marker(ToMap(kv.Value.transform.position), kv.Value.transform.forward, Net.CoopNet.ColorOf(kv.Key), area);
            Marker(ToMap(Focus.position), Focus.forward, new Color(1, .42f, .26f), area);

            // Header, scale and controls.
            GUI.color = new Color(.04f, .055f, .058f, .92f);
            GUI.DrawTexture(new Rect(40, 14, 1360, 40), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(60, 14, 400, 40), "КАРТА", style(20, amber, FontStyle.Bold, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(170, 14, 500, 40), Island.District(Focus.position), style(13, cream, FontStyle.Bold, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(560, 14, 820, 40), "МЫШЬ / WASD  двигать     КОЛЕСО / + −  масштаб     ПРОБЕЛ  к машине     M / ESC  закрыть",
                style(12, cream, FontStyle.Normal, TextAnchor.MiddleRight));
            float metres = Mathf.Pow(10, Mathf.Floor(Mathf.Log10(zoom * .5f)));
            float pixels = metres / (2 * zoom) * area.height;
            GUI.color = new Color(.04f, .055f, .058f, .85f);
            GUI.DrawTexture(new Rect(60, 862, pixels + 90, 30), Texture2D.whiteTexture);
            GUI.color = cream;
            GUI.DrawTexture(new Rect(70, 880, pixels, 3), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(76 + pixels, 864, 80, 26), metres >= 1000 ? $"{metres / 1000:0} км" : $"{metres:0} м", style(12, cream, FontStyle.Bold, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(1330, 70, 50, 30), "N ↑", style(14, amber, FontStyle.Bold, TextAnchor.MiddleCenter));
        }

        static void Marker(Vector2 p, Vector3 forward, Color colour, Rect area)
        {
            if (!area.Contains(p))
            {
                p.x = Mathf.Clamp(p.x, area.xMin + 12, area.xMax - 12);
                p.y = Mathf.Clamp(p.y, area.yMin + 12, area.yMax - 12);
            }
            float angle = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            var saved = GUI.matrix;
            GUI.matrix *= Matrix4x4.TRS(p, Quaternion.Euler(0, 0, angle), Vector3.one) * Matrix4x4.TRS(-p, Quaternion.identity, Vector3.one);
            GUI.color = new Color(0, 0, 0, .8f);
            GUI.DrawTexture(new Rect(p.x - 9, p.y - 13, 18, 26), Texture2D.whiteTexture);
            GUI.color = colour;
            GUI.DrawTexture(new Rect(p.x - 7, p.y - 11, 14, 22), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(p.x - 4, p.y - 11, 8, 6), Texture2D.whiteTexture);
            GUI.matrix = saved;
        }
    }
}

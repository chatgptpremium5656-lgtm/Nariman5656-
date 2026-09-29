using UnityEngine;
using UnityEngine.InputSystem;

namespace Autobahn
{
    public sealed class DriveHud : MonoBehaviour
    {
        RunSession run;
        Atmosphere sky;
        DriveCamera view;
        bool debug, minimal, coopOpen;
        WorldMap map;
        readonly System.Collections.Generic.List<Vector2> routePoints = new();
        readonly System.Collections.Generic.List<(Vector2, Vector2)> gridSegments = new();
        string codeInput = "";
        float copiedAt = -10;
        GUIStyle codeField;
        // Frames per second, averaged over half-second windows so the number does not jitter;
        // shared by the FPS plate and the F3 telemetry.
        float fps, fpsTime;
        int fpsFrames;
        string fpsText = "";
        static bool? showFps;
        public static bool ShowFps
        {
            get
            {
                if (showFps == null)
                    try { showFps = ProfileStore.Testing || PlayerPrefs.GetInt("autobahn.showfps", 1) != 0; } catch { showFps = true; }
                return showFps.Value;
            }
            set
            {
                showFps = value;
                if (!ProfileStore.Testing)
                    try { PlayerPrefs.SetInt("autobahn.showfps", value ? 1 : 0); } catch { }
            }
        }
        int menuSelected, menuButton;
        bool wasPaused;
        GUIStyle small, normal, large, title, button, tiny, centre, numberCentre, heading;
        float shownSpeed, shownRpm, shownScore, scorePop;
        Texture2D flat, fade;
        Color cream = UiTheme.Text, amber = UiTheme.Accent, muted = UiTheme.Muted;
        // The 1440 x 900 canvas is scaled to fit the screen; these are the screen edges in canvas
        // units, so full-screen shades and edge-anchored HUD reach the real edges on any aspect.
        Matrix4x4 canvas = Matrix4x4.identity;
        float edgeL, edgeR = 1440, edgeT, edgeB = 900;
        // Smoothed highlight per menu item (0..1), eased in unscaled time.
        readonly float[] glow = new float[32];
        bool controlsOpen;
        const int MenuItems = 20;
        public void Init(RunSession session, Atmosphere weather, DriveCamera camera)
        {
            // The HUD places everything by hand, so the extra layout pass IMGUI runs every frame is wasted work.
            useGUILayout = false;
            run = session;
            sky = weather;
            view = camera;
            map = gameObject.AddComponent<WorldMap>();
            map.Init(session);
        }

        void Update()
        {
            fpsFrames++;
            fpsTime += Time.unscaledDeltaTime;
            if (fpsTime >= .5f)
            {
                fps = fpsFrames / fpsTime;
                fpsFrames = 0;
                fpsTime = 0;
                fpsText = Mathf.RoundToInt(fps) + " FPS";
            }
            if (Keyboard.current?.f3Key.wasPressedThisFrame ?? false)
                debug = !debug;
            // Minimal HUD moved to F4: H is the horn now.
            if (!DriverInput.Blocked && (Keyboard.current?.f4Key.wasPressedThisFrame ?? false))
                minimal = !minimal;
            if (run == null || WorldMap.Open)
                return;
            // T: jump to the friend in online play.
            if (!run.Paused && Net.CoopNet.Online && (Keyboard.current?.tKey.wasPressedThisFrame ?? false))
                Net.CoopNet.Active.GoToFriend();
            if (!run.Paused)
                coopOpen = controlsOpen = false;
            if (coopOpen)
                return;
            if (run.Paused && !wasPaused)
                menuSelected = run.ChoosingMode ? (int)run.Mode : 0;
            wasPaused = run.Paused;
            if (!run.Paused)
                return;
            var pad = Gamepad.current;
            int count = run.OnTitle ? TitleItems.Length : run.ChoosingMode ? 4 : view.Inspecting ? 3 : run.Finished ? 1 : controlsOpen ? 1 : MenuItems;
            var keys = Keyboard.current;
            // On the sound row, left / right change the volume instead of moving the cursor.
            bool soundRow = !run.OnTitle && !run.ChoosingMode && !view.Inspecting && !run.Finished && !coopOpen && !controlsOpen && menuSelected == 9;
            bool resolutionRow = !run.OnTitle && !run.ChoosingMode && !view.Inspecting && !run.Finished && !coopOpen && !controlsOpen && menuSelected == 18;
            bool aaRow = !run.OnTitle && !run.ChoosingMode && !view.Inspecting && !run.Finished && !coopOpen && !controlsOpen && menuSelected == 19;
            if (aaRow)
            {
                if ((keys?.leftArrowKey.wasPressedThisFrame ?? false) || (pad?.dpad.left.wasPressedThisFrame ?? false)) { GraphicsQuality.CycleAntiAliasing(-1); return; }
                if ((keys?.rightArrowKey.wasPressedThisFrame ?? false) || (pad?.dpad.right.wasPressedThisFrame ?? false)) { GraphicsQuality.CycleAntiAliasing(1); return; }
            }
            if (resolutionRow)
            {
                if ((keys?.leftArrowKey.wasPressedThisFrame ?? false) || (pad?.dpad.left.wasPressedThisFrame ?? false)) { ScreenResolution.Cycle(-1); return; }
                if ((keys?.rightArrowKey.wasPressedThisFrame ?? false) || (pad?.dpad.right.wasPressedThisFrame ?? false)) { ScreenResolution.Cycle(1); return; }
            }
            if (soundRow)
            {
                if ((keys?.leftArrowKey.wasPressedThisFrame ?? false) || (pad?.dpad.left.wasPressedThisFrame ?? false)) { GameVolume.Step(-1); return; }
                if ((keys?.rightArrowKey.wasPressedThisFrame ?? false) || (pad?.dpad.right.wasPressedThisFrame ?? false)) { GameVolume.Step(1); return; }
            }
            if ((pad?.dpad.down.wasPressedThisFrame ?? false) || (pad?.dpad.right.wasPressedThisFrame ?? false) || (keys?.tabKey.wasPressedThisFrame ?? false)
                || (keys?.rightArrowKey.wasPressedThisFrame ?? false) || (keys?.downArrowKey.wasPressedThisFrame ?? false))
                menuSelected = (menuSelected + 1) % count;
            if ((pad?.dpad.up.wasPressedThisFrame ?? false) || (pad?.dpad.left.wasPressedThisFrame ?? false)
                || (keys?.leftArrowKey.wasPressedThisFrame ?? false) || (keys?.upArrowKey.wasPressedThisFrame ?? false))
                menuSelected = (menuSelected + count - 1) % count;
            if ((pad?.buttonSouth.wasPressedThisFrame ?? false) || (Keyboard.current?.enterKey.wasPressedThisFrame ?? false))
                ActivateSelection();
        }

        void ActivateSelection(bool sound = true)
        {
            if (sound) UiSound.Click();
            if (run.OnTitle)
            {
                TitleAction(menuSelected);
                return;
            }
            if (run.ChoosingMode)
            {
                if (menuSelected == 2)
                    OpenCoop();
                else if (menuSelected == 3)
                    ModesBack();
                else
                    StartMode((GameMode)menuSelected);
                return;
            }
            if (view.Inspecting)
            {
                if (menuSelected == 0) run.CyclePaint();
                else if (menuSelected == 1) run.CycleCar();
                else { view.Inspect(false); menuSelected = 0; }
                return;
            }
            if (run.Finished)
            {
                run.Restart();
                return;
            }
            if (controlsOpen)
            {
                controlsOpen = false;
                menuSelected = 15;
                return;
            }

            switch (menuSelected)
            {
                case 0:
                    // Settings opened from the title: "continue" goes back to the title.
                    if (!run.Started) { run.OnTitle = true; menuSelected = 1; break; }
                    run.SetPaused(false);
                    break;
                case 1:
                    sky.Set((Weather)(((int)sky.Current + 1) % 4), Atmosphere.Night);
                    break;
                case 2:
                    sky.Set(sky.Current, !Atmosphere.Night);
                    break;
                case 3:
                    run.Traffic.SetDensity((run.Traffic.Density + 1) % 3);
                    break;
                case 4:
                    Atmosphere.Storms = !Atmosphere.Storms;
                    break;
                case 5:
                    run.Player.Chassis.ABS = !run.Player.Chassis.ABS;
                    break;
                case 6:
                    run.Player.Chassis.TCS = !run.Player.Chassis.TCS;
                    break;
                case 7:
                    run.Player.Chassis.ESC = !run.Player.Chassis.ESC;
                    break;
                case 8:
                    // Nothing to finish before the first drive (settings opened from the title).
                    if (run.Started) run.Finish();
                    break;
                case 9:
                    VolumeStep(1);
                    break;
                case 10:
                    GraphicsQuality.Cycle();
                    break;
                case 11:
                    // The studio camera belongs to the car: not while walking about.
                    if (FootManager.OnFoot) break;
                    view.Inspect(true);
                    menuSelected = 0;
                    break;
                case 12:
                    CarRadio.Active?.Next();
                    break;
                case 13:
                    OpenModes();
                    break;
                case 14:
                    OpenCoop();
                    break;
                case 15:
                    controlsOpen = true;
                    menuSelected = 0;
                    break;
                case 16:
                    ShowFps = !ShowFps;
                    break;
                case 17:
                    DriveCamera.SensitivityLevel = (DriveCamera.SensitivityLevel + 1) % DriveCamera.Sensitivities.Length;
                    break;
                case 18:
                    ScreenResolution.Cycle(1);
                    break;
                case 19:
                    GraphicsQuality.CycleAntiAliasing(1);
                    break;
            }
        }

        static readonly string[] Percent = { "0%", "10%", "20%", "30%", "40%", "50%", "60%", "70%", "80%", "90%", "100%" };

        void VolumeStep(int direction)
        {
            if (direction > 0) GameVolume.Cycle();
            else GameVolume.Step(-1);
        }

        // Master volume: real buttons for − and +, and a slider for the level. A short tick
        // plays at the new level so the change is heard even while the game is paused.
        void VolumeControl(float x, float y, float w)
        {
            var rect = new Rect(x, y, w, 42);
            int index = menuButton++;
            bool hover = rect.Contains(Event.current.mousePosition);
            if (hover && Event.current.type == EventType.MouseMove) menuSelected = index;
            bool selected = index == menuSelected;
            float t = Glow(index, selected || hover);
            Rounded(rect, Color.Lerp(UiTheme.Fill(.92f), UiTheme.Alpha(UiTheme.PanelHover, .98f), t), 10);
            Rounded(new Rect(x, y, w, 1), UiTheme.Edge, 0);
            if (selected)
                Rounded(new Rect(x, y + 8, 4, rect.height - 16), UiTheme.Alpha(amber, .4f + .6f * t), 2);
            float level = GameVolume.Level;
            Label(x + 20, y + 11, 60, 20, "ЗВУК", Style(15, cream, FontStyle.Bold));
            Label(x + w - 52, y + 11, 44, 20, Percent[Mathf.Clamp(Mathf.RoundToInt(level * 10), 0, 10)], Style(14, amber, FontStyle.Bold, TextAnchor.UpperRight));

            var minus = new Rect(x + 66, y + 7, 28, 28);
            var plus = new Rect(x + w - 88, y + 7, 28, 28);
            Rounded(minus, UiTheme.Alpha(UiTheme.PanelHover, 1), 14);
            Rounded(plus, UiTheme.Alpha(UiTheme.PanelHover, 1), 14);
            Label(minus.x, minus.y, minus.width, minus.height, "−", Style(18, amber, FontStyle.Bold, TextAnchor.MiddleCenter));
            Label(plus.x, plus.y, plus.width, plus.height, "+", Style(18, amber, FontStyle.Bold, TextAnchor.MiddleCenter));
            if (GUI.Button(minus, GUIContent.none, GUIStyle.none))
                GameVolume.Step(-1);
            if (GUI.Button(plus, GUIContent.none, GUIStyle.none))
                GameVolume.Step(1);

            // Track and knob; clicking or dragging anywhere on the track sets the level.
            var track = new Rect(x + 102, y + 12, w - 198, 18);
            Bar(track.x, track.y + 7, track.width, 4, level, amber);
            Disc(new Vector2(track.x + track.width * level, track.y + 9), 7, cream);
            var e = Event.current;
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && track.Contains(e.mousePosition))
            {
                GameVolume.Set(Mathf.InverseLerp(track.x, track.xMax, e.mousePosition.x));
                e.Use();
            }
        }

        void OpenCoop()
        {
            Net.CoopNet.Ensure();
            run.ChoosingMode = false;
            run.OnTitle = false;
            if (!run.Paused) run.SetPaused(true);
            coopOpen = true;
            menuSelected = -1;
        }

        void OpenModes()
        {
            // The guest drives whatever the host picked.
            if (Net.CoopNet.IsGuest) return;
            run.ChoosingMode = true;
            menuSelected = (int)run.Mode;
        }

        // "Back" on the modes screen: to the title before the first drive, else to the pause menu.
        void ModesBack()
        {
            run.ChoosingMode = false;
            if (!run.Started)
            {
                run.OnTitle = true;
                menuSelected = 1;
            }
            else
                menuSelected = 13;
        }

        void StartMode(GameMode mode)
        {
            if (mode != run.Mode)
                run.SetMode(mode);
            else if (!run.Started)
                run.Restart();
            run.OnTitle = false;
            run.ChoosingMode = false;
            run.SetPaused(false);
            menuSelected = 0;
        }

        // Styles are cached: building a new GUIStyle for every label every frame fed the
        // garbage collector and caused periodic hitches. Keep the colours passed in fixed:
        // an animated colour would add a new entry every frame (tint with GUI.contentColor).
        readonly System.Collections.Generic.Dictionary<(int, Color, FontStyle, TextAnchor), GUIStyle> styleCache = new();
        GUIStyle Style(int size, Color color, FontStyle weight = FontStyle.Normal, TextAnchor anchor = TextAnchor.UpperLeft)
        {
            var key = (size, color, weight, anchor);
            if (!styleCache.TryGetValue(key, out var style))
            {
                style = new GUIStyle(GUI.skin.label) {fontSize = size, fontStyle = weight, alignment = anchor, normal = {textColor = color}};
                styleCache[key] = style;
            }
            return style;
        }

        void Styles()
        {
            if (normal != null)
                return;
            small = Style(13, muted);
            normal = Style(17, cream);
            large = Style(68, cream, FontStyle.Bold);
            title = Style(32, cream, FontStyle.Bold);
            tiny = Style(11, muted);
            centre = Style(19, cream, FontStyle.Normal, TextAnchor.MiddleCenter);
            numberCentre = Style(64, cream, FontStyle.Bold, TextAnchor.MiddleCenter);
            heading = Style(12, amber, FontStyle.Bold);
            button = new GUIStyle{fontSize = 15, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(22, 12, 9, 9), normal = {textColor = cream}, hover = {textColor = amber}, active = {textColor = cream}};
            flat = Texture2D.whiteTexture;
            // Horizontal shade for the title screen: opaque on the left, clear on the right.
            fade = new Texture2D(256, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            for (int i = 0; i < 256; i++)
            {
                float a = 1 - i / 255f;
                fade.SetPixel(i, 0, new Color(1, 1, 1, a * a * (3 - 2 * a)));
            }
            fade.Apply(false, true);
            versionLine = $"v{Application.version}   •   фанатская игра, не связана с Rockstar Games";
        }

        string versionLine = "";

        // ---- drawing helpers --------------------------------------------------

        void Box(float x, float y, float w, float h, Color color) => Rounded(new Rect(x, y, w, h), color, 0);

        // Screen-space fill over the whole window; only called while GUI.matrix is identity.
        void ScreenFill(Color color) =>
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), flat, ScaleMode.StretchToFill, true, 0, color, 0, 0);

        // Pins what follows to a screen edge: h / v are 0 (left / top), .5 (centre) or 1 (right / bottom).
        void Anchor(float h, float v)
        {
            float dx = h < .25f ? edgeL : h > .75f ? edgeR - 1440 : 0;
            float dy = v < .25f ? edgeT : v > .75f ? edgeB - 900 : 0;
            GUI.matrix = canvas * Matrix4x4.Translate(new Vector3(dx, dy, 0));
        }

        // Eases an item's highlight towards on / off; advanced once per drawn frame.
        float Glow(int index, bool on)
        {
            if (index < 0 || index >= glow.Length)
                return on ? 1 : 0;
            if (Event.current.type == EventType.Repaint)
                glow[index] = Mathf.MoveTowards(glow[index], on ? 1 : 0, Mathf.Clamp(Time.unscaledDeltaTime, 0, .1f) * 7);
            float g = glow[index];
            return g * g * (3 - 2 * g);
        }

        void Rounded(Rect rect, Color color, float radius)
        {
            GUI.DrawTexture(rect, flat, ScaleMode.StretchToFill, true, 0, color, Vector4.zero, Vector4.one * radius);
        }

        // Rotation inside our own 1440x900 space: GUIUtility.RotateAroundPivot works in
        // screen space and would fight the letterboxing matrix, so post-multiply instead.
        void Rotate(Vector2 pivot, float angle)
        {
            GUI.matrix *= Matrix4x4.TRS(pivot, Quaternion.Euler(0, 0, angle), Vector3.one)
                        * Matrix4x4.TRS(-pivot, Quaternion.identity, Vector3.one);
        }

        // Plain quad: the rounded-corner path does not survive a rotated GUI matrix,
        // so anything drawn under a rotation uses this.
        void Quad(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, flat);
            GUI.color = previous;
        }

        // A card: soft drop shadow, translucent fill, hairline edge.
        void Panel(float x, float y, float w, float h, float alpha = .74f, float radius = 14)
        {
            Rounded(new Rect(x - 3, y - 1, w + 6, h + 7), new Color(0, 0, 0, alpha * .33f), radius + 4);
            Rounded(new Rect(x, y, w, h), UiTheme.Fill(alpha), radius);
            // Hairline edge all round (white 8 %), drawn as a rounded border.
            GUI.DrawTexture(new Rect(x, y, w, h), flat, ScaleMode.StretchToFill, true, 0, UiTheme.Edge, 1, radius);
        }

        void Accent(float x, float y, float w, float h, Color color) => Rounded(new Rect(x, y, w, h), color, h / 2);

        void Bar(float x, float y, float w, float h, float fill, Color color)
        {
            Rounded(new Rect(x, y, w, h), new Color(1, 1, 1, .10f), h / 2);
            if (fill > 0)
                Rounded(new Rect(x, y, Mathf.Max(h, w * Mathf.Clamp01(fill)), h), color, h / 2);
        }

        // Segmented arc used for the rev counter.
        void Arc(Vector2 centre, float radius, float thickness, float fromDeg, float toDeg, float fill, int segments)
        {
            var matrix = GUI.matrix;
            for (int i = 0; i < segments; i++)
            {
                float t = i / (float)(segments - 1);
                float angle = Mathf.Lerp(fromDeg, toDeg, t);
                bool lit = t <= fill;
                Color colour = !lit ? new Color(1, 1, 1, .09f)
                    : t > .86f ? new Color(1, .29f, .22f)
                    : Color.Lerp(amber, new Color(1, .55f, .25f), t);
                Rotate(centre, angle);
                float length = Mathf.Lerp(thickness * .8f, thickness * 1.5f, t);
                Quad(new Rect(centre.x - 2, centre.y - radius - length / 2, 4, length), colour);
                GUI.matrix = matrix;
            }
        }

        void Label(float x, float y, float w, float h, string text, GUIStyle style)
        {
            if (Audit != null && hudPass) Audit.Add(text);
            GUI.Label(new Rect(x, y, w, h), text, style);
        }

        // QA only: when set, every text drawn by the driving HUD (not the menus) is collected
        // here, so the automated checks can prove no control hints are shown while driving.
        public static System.Collections.Generic.List<string> Audit;
        bool hudPass;

        bool Button(float x, float y, float w, string text) => Item(menuButton++, new Rect(x, y, w, 42), text, 15);

        // A menu entry: panel that lights up smoothly on hover / selection, a thin orange bar on
        // the left of the selected one, the label easing to the right. Mouse, keys and pad share
        // menuSelected, so hovering moves the selection too.
        bool Item(int index, Rect rect, string text, int size, float idle = .92f)
        {
            var e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (hover && e.type == EventType.MouseMove) menuSelected = index;
            bool selected = index == menuSelected;
            float t = Glow(index, selected || hover);
            Rounded(rect, Color.Lerp(UiTheme.Fill(idle), UiTheme.Alpha(UiTheme.PanelHover, .98f), t), 10);
            Rounded(new Rect(rect.x, rect.y, rect.width, 1), UiTheme.Alpha(Color.white, .08f + .06f * t), 0);
            if (selected)
                Rounded(new Rect(rect.x, rect.y + 8, 4, rect.height - 16), UiTheme.Alpha(amber, .4f + .6f * t), 2);
            Label(rect.x + 22 + 8 * t, rect.y, rect.width - 30, rect.height, text, Style(size, selected || hover ? cream : UiTheme.Alpha(cream, .8f), FontStyle.Bold, TextAnchor.MiddleLeft));
            return GUI.Button(rect, GUIContent.none, GUIStyle.none) && UiSound.Click();
        }

        void OnGUI()
        {
            if (run == null)
                return;
            hudPass = false;
            Styles();
            // Menus live in a 1440 x 900 canvas scaled to fit; on other aspects the canvas does not
            // cover the whole window, so full-screen shades are drawn first, in screen space.
            float scale = Mathf.Min(Screen.width / 1440f, Screen.height / 900f);
            float padX = (Screen.width - 1440 * scale) / 2, padY = (Screen.height - 900 * scale) / 2;
            edgeL = -padX / scale;
            edgeR = 1440 + padX / scale;
            edgeT = -padY / scale;
            edgeB = 900 + padY / scale;
            canvas = Matrix4x4.TRS(new Vector3(padX, padY, 0), Quaternion.identity, new Vector3(scale, scale, 1));
            GUI.matrix = Matrix4x4.identity;
            Backdrop();
            GUI.matrix = canvas;
            var car = run.Player;
            var score = run.Scoring;
            // Needles and counters ease once per drawn frame (the rates match the old
            // behaviour, when this ran twice a frame: once for layout, once for drawing).
            if (Event.current.type == EventType.Repaint)
            {
                float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0, .1f) * 2;
                shownSpeed = Mathf.Lerp(shownSpeed, car.Speed, 1 - Mathf.Exp(-dt * 14));
                shownRpm = Mathf.Lerp(shownRpm, car.Engine.Rpm, 1 - Mathf.Exp(-dt * 16));
                float points = Mathf.FloorToInt(score.Points);
                scorePop = Mathf.Max(0, scorePop - dt * 2.4f);
                if (points > shownScore + .5f)
                    scorePop = 1;
                shownScore = Mathf.Lerp(shownScore, points, 1 - Mathf.Exp(-dt * 9));
            }

            if (WorldMap.Open)
            {
                map.Draw(mapStyle ??= (s, c, w, a) => Style(s, c, w, a), amber, cream);
                return;
            }

            if (run.OnTitle)
            {
                coopOpen = false;
                Title();
                return;
            }

            if (coopOpen && run.Paused)
            {
                CoopPanel();
                return;
            }

            if (run.ChoosingMode)
            {
                ModeSelect();
                return;
            }

            if (view.Inspecting && run.Paused)
            {
                Inspection();
                return;
            }

            // The pause menu covers the driving HUD (and its shade is already down).
            bool menu = run.Paused;
            hudPass = !menu;
            if (!minimal && !menu)
            {
                TopChips(car);
                Anchor(0, 0);
                RadioPlate();
                Anchor(0, 1);
                MiniMap();
                Anchor(1, 1);
                if (!FootManager.OnFoot) Speedometer(car);
                FriendOverlay();
                Anchor(1, 0); WantedHud.Draw(1440 - Edge - (ShowFps ? 96 : 0), Edge + 74);    // police stars under the route pill
                Anchor(.5f, 0);

                // Live drift counter while sliding.
                var smoke = TyreSmoke.Player;
                if (smoke != null && smoke.Current > 5 && !run.Paused)
                {
                    Rounded(new Rect(610, 184, 220, 70), UiTheme.Fill(.84f), 16);
                    Rounded(new Rect(610, 184, 220, 3), amber, 1.5f);
                    Label(610, 192, 220, 16, "Д Р И Ф Т", Style(12, amber, FontStyle.Bold, TextAnchor.UpperCenter));
                    Label(610, 208, 220, 40, driftText.Get(Mathf.RoundToInt(smoke.Current), v => v.ToString()), Style(32, cream, FontStyle.Bold, TextAnchor.UpperCenter));
                }

                if (score.MessageTime > 0)
                {
                    float fade = Mathf.Clamp01(score.MessageTime * 2.5f);
                    var style = Style(16, cream, FontStyle.Bold, TextAnchor.MiddleCenter);
                    messageContent.text = score.Message;
                    float width = Mathf.Max(240, style.CalcSize(messageContent).x + 60);
                    GUI.color = new Color(1, 1, 1, fade);
                    Rounded(new Rect(720 - width / 2, 124, width, 44), UiTheme.Fill(.84f), 16);
                    Label(720 - width / 2, 124, width, 44, score.Message, style);
                    GUI.color = Color.white;
                }

                Anchor(.5f, 0);
                var damage = car.Damage;
                if (damage.Damaged)
                    DamagePlate(damage);
            }

            if (view.Mode == ViewMode.Cockpit && !menu && !FootManager.OnFoot)
            {
                Anchor(.5f, 1);
                Panel(556, 700, 330, 74, .88f, 12);
                Label(576, 712, 130, 46, $"{shownSpeed:000}", Style(34, cream, FontStyle.Bold));
                Label(576, 748, 130, 18, "KM/H", tiny);
                Bar(686, 722, 180, 4, Mathf.Clamp01(Mathf.InverseLerp(car.Spec.idle, car.Spec.redline, shownRpm)), amber);
                // No oil / water degrees any more: the driver only needs to know if it is overheating.
                Label(686, 732, 190, 20, $"{shownRpm:0} RPM     ПЕРЕДАЧА {car.Engine.Label}", small);
                bool hot = car.Damage.Temperature > OverheatAt;
                Label(686, 752, 190, 18, hot ? "ДВИГАТЕЛЬ  ПЕРЕГРЕВ" : "ДВИГАТЕЛЬ  OK", Style(12, hot ? UiTheme.Bad : UiTheme.Good, FontStyle.Bold));
            }

            if (debug)
            {
                Anchor(0, 0);
                Panel(40, 128, 330, 170, .82f);
                Label(60, 144, 300, 150, $"TELEMETRY / F3\n{fps:0} FPS    {run.Traffic.Count} VEHICLES\nSLIP {car.Chassis.Slip:0.00}    STEER {car.Chassis.SteerAngle:0.0}°\nGRIP {Atmosphere.Grip:0.00}    CONTACTS {car.Chassis.Grounded}/4\nABS {(car.Chassis.ABS ? "ON" : "OFF")} / TCS {(car.Chassis.TCS ? "ON" : "OFF")} / ESC {(car.Chassis.ESC ? "ON" : "OFF")}", normal);
            }

            GUI.matrix = canvas;
            hudPass = false;
            if (run.Paused)
                Menu();
            if (ShowFps && !WorldMap.Open)
                FpsPlate();
        }

        // Damage plate, top centre: plain words and a bar instead of numbers and degrees.
        // Standing still repairs the car; meanwhile the plate turns green with a countdown.
        const float OverheatAt = 110;
        static readonly string RepairHint = $"Остановись на {MechanicalDamage.RepairDelay:0} секунд — починится";
        readonly Readout repairText = new();

        void DamagePlate(MechanicalDamage damage)
        {
            float repair = damage.StillTime / MechanicalDamage.RepairDelay;
            bool repairing = repair > 0;
            var r = new Rect(530, Edge, 380, 86);
            Rounded(r, UiTheme.Fill(.88f), 16);
            var tone = repairing ? UiTheme.Good : Color.Lerp(UiTheme.Warn, UiTheme.Bad, damage.Worst);
            Rounded(new Rect(r.x, r.y, r.width, 3), tone, 1.5f);
            if (repairing)
            {
                Label(r.x + 20, r.y + 10, 40, 30, "✓", Style(28, UiTheme.Good, FontStyle.Bold, TextAnchor.MiddleCenter));
                Label(r.x + 66, r.y + 12, r.width - 86, 24, "РЕМОНТ", Style(16, cream, FontStyle.Bold, TextAnchor.MiddleLeft));
                int left = Mathf.CeilToInt(MechanicalDamage.RepairDelay - damage.StillTime);
                Label(r.x + 66, r.y + 12, r.width - 86, 24, repairText.Get(left, v => v + " С"), Style(16, UiTheme.Good, FontStyle.Bold, TextAnchor.MiddleRight));
                Bar(r.x + 66, r.y + 46, r.width - 86, 6, repair, UiTheme.Good);
                Label(r.x + 66, r.y + 58, r.width - 86, 20, "Не трогайся с места", Style(12, muted, FontStyle.Normal, TextAnchor.MiddleLeft));
                return;
            }
            Label(r.x + 20, r.y + 10, 40, 30, "⚠", Style(28, UiTheme.Warn, FontStyle.Bold, TextAnchor.MiddleCenter));
            Label(r.x + 66, r.y + 12, r.width - 86, 24, "МАШИНА ПОВРЕЖДЕНА", Style(16, cream, FontStyle.Bold, TextAnchor.MiddleLeft));
            Bar(r.x + 66, r.y + 46, r.width - 86, 6, Mathf.Max(.04f, damage.Worst), tone);
            Label(r.x + 66, r.y + 58, r.width - 86, 20, RepairHint, Style(12, muted, FontStyle.Normal, TextAnchor.MiddleLeft));
        }

        // Small frame-rate plate in the top-right corner: green from 55, yellow from 30, red below.
        void FpsPlate()
        {
            if (fpsText.Length == 0) return;
            Anchor(1, 0);
            var rect = new Rect(1440 - Edge - 84, Edge, 84, 26);
            Rounded(rect, UiTheme.Fill(.82f), 13);
            var colour = fps >= 55 ? UiTheme.Good : fps >= 30 ? UiTheme.Warn : UiTheme.Bad;
            Label(rect.x, rect.y, rect.width, rect.height, fpsText, Style(12, colour, FontStyle.Bold, TextAnchor.MiddleCenter));
            GUI.matrix = canvas;
        }

        System.Func<int, Color, FontStyle, TextAnchor, GUIStyle> mapStyle;

        // Full-screen shades, in screen coordinates (GUI.matrix is identity here), for whatever
        // OnGUI is about to draw: they reach every edge whatever the window's aspect.
        void Backdrop()
        {
            if (WorldMap.Open)
                ScreenFill(UiTheme.Dim(.88f));
            else if (run.OnTitle)
            {
                // The city stays visible on the right; the left, where the menu sits, is dark.
                ScreenFill(UiTheme.Dim(.25f));
                GUI.DrawTexture(new Rect(0, 0, Screen.width * .68f, Screen.height), fade, ScaleMode.StretchToFill, true, 0, UiTheme.Dim(.94f), 0, 0);
            }
            else if (coopOpen && run.Paused)
                ScreenFill(UiTheme.Dim(.88f));
            else if (run.ChoosingMode)
                ScreenFill(UiTheme.Dim(.84f));
            else if (view.Inspecting && run.Paused)
            {
            }
            else if (run.Paused)
                ScreenFill(UiTheme.Dim(.86f));
        }

        static readonly string[] WeatherLabels = { "ПОГОДА  /  ЯСНО", "ПОГОДА  /  ДОЖДЬ", "ПОГОДА  /  ТУМАН", "ПОГОДА  /  СНЕГ" };
        static readonly string[] TrafficLabels = { "ТРАФИК  /  МАЛО", "ТРАФИК  /  СРЕДНЕ", "ТРАФИК  /  МНОГО" };
        static readonly string[] LookLabels = { "ОБЗОР КАМЕРЫ  /  0.5×", "ОБЗОР КАМЕРЫ  /  0.75×", "ОБЗОР КАМЕРЫ  /  1×", "ОБЗОР КАМЕРЫ  /  1.5×", "ОБЗОР КАМЕРЫ  /  2×" };
        string[] radioLabels;

        string RadioLabel(CarRadio radio)
        {
            if (!radio.On) return "РАДИО  /  ВЫКЛ";
            if (radioLabels == null || radioLabels.Length != radio.Count)
            {
                radioLabels = new string[radio.Count];
                for (int i = 0; i < radioLabels.Length; i++) radioLabels[i] = $"РАДИО  /  FM {i + 1}";
            }
            return radioLabels[Mathf.Clamp(radio.Station, 0, radioLabels.Length - 1)];
        }

        // Pause menu, and the settings screen opened from the title (the same page before the
        // first drive). Items are numbered: the number is what keys and the pad select, and
        // ActivateSelection runs the same action for it.
        void Menu()
        {
            menuButton = 0;
            if (controlsOpen)
            {
                ControlsPage();
                return;
            }
            const float x = 432, w = 576, half = 279, col2 = 729;
            float top = run.Finished ? 250 : 70;
            Panel(388, top, 664, run.Finished ? 360 : 766, .97f, 18);
            Accent(420, top + 30, 3, 24, amber);
            Label(434, top + 30, 400, 24, run.Finished ? "ЗАЕЗД ОКОНЧЕН" : run.Started ? "ПАУЗА" : "НАСТРОЙКИ", heading);
            Label(432, top + 54, 400, 44, run.Finished ? "Хорошая поездка" : run.Started ? "Дорога подождёт" : "Настройки", title);
            Rounded(new Rect(x, top + 106, w, 1), UiTheme.Edge, 0);

            if (run.Finished)
            {
                var sc = run.Scoring;
                Label(x, top + 124, w, 100, $"{sc.Distance / 1000:0.00} км   •   максимум {sc.TopSpeed:0} км/ч\n{sc.NearMisses} опасных обгонов   •   {sc.Points:N0} очков\n+ {run.Reward:N0} кредитов   /   на счету {run.Profile.credits:N0}", normal);
                if (!run.Saved)
                    Label(x, top + 222, w, 24, "Профиль не сохранился: награда хранится в памяти.", small);
                if (Item(0, new Rect(x, top + 262, w, 48), "ЕЩЁ РАЗ", 18))
                    run.Restart();
                return;
            }

            float y = top + 124;
            if (Item(0, new Rect(x, y, w, 48), run.Started ? "ПРОДОЛЖИТЬ ЕЗДУ" : "НАЗАД", 18))
                ActivateMenu(0);
            if (Item(13, new Rect(838, top + 44, 170, 42), Net.CoopNet.IsGuest ? "РЕЖИМ  /  ХОСТ" : run.Mode == GameMode.Free ? "РЕЖИМ  /  ОСТРОВ" : "РЕЖИМ  /  ГОРОД", 13))
                ActivateMenu(13);

            y += 68;
            Label(x, y, w, 20, "ДОРОГА", heading);
            y += 24;
            if (Item(1, new Rect(x, y, half, 42), WeatherLabels[Mathf.Clamp((int)sky.Current, 0, 3)], 15)) ActivateMenu(1);
            if (Item(2, new Rect(col2, y, half, 42), Atmosphere.Night ? "ВРЕМЯ  /  НОЧЬ" : "ВРЕМЯ  /  ДЕНЬ", 15)) ActivateMenu(2);
            y += 50;
            if (Item(3, new Rect(x, y, half, 42), TrafficLabels[Mathf.Clamp(run.Traffic.Density, 0, 2)], 15)) ActivateMenu(3);
            if (Item(4, new Rect(col2, y, half, 42), Atmosphere.Storms ? "ГРОЗА В ДОЖДЬ  /  ДА" : "ГРОЗА В ДОЖДЬ  /  НЕТ", 15)) ActivateMenu(4);

            y += 60;
            Label(x, y, w, 20, "ПОМОЩНИКИ ВОДИТЕЛЮ", heading);
            y += 24;
            var chassis = run.Player.Chassis;
            if (Item(5, new Rect(x, y, 180, 42), chassis.ABS ? "ABS  ВКЛ" : "ABS  ВЫКЛ", 15)) ActivateMenu(5);
            if (Item(6, new Rect(x + 198, y, 180, 42), chassis.TCS ? "TCS  ВКЛ" : "TCS  ВЫКЛ", 15)) ActivateMenu(6);
            if (Item(7, new Rect(x + 396, y, 180, 42), chassis.ESC ? "ESC  ВКЛ" : "ESC  ВЫКЛ", 15)) ActivateMenu(7);

            y += 60;
            Label(x, y, w, 20, "ИГРА", heading);
            y += 24;
            if (Item(8, new Rect(x, y, half, 42), "ЗАВЕРШИТЬ ЗАЕЗД", 15)) ActivateMenu(8);
            menuButton = 9;
            VolumeControl(col2, y, half);
            y += 50;
            // ВЫСОКАЯ → БЫСТРАЯ → МОБИЛЬНАЯ (GraphicsQuality.Cycle).
            if (Item(10, new Rect(x, y, half, 42), GraphicsQuality.Label, 15)) ActivateMenu(10);
            if (Item(11, new Rect(col2, y, half, 42), "СТУДИЯ  /  ПОКРАСКА", 15)) ActivateMenu(11);
            y += 50;
            var radio = CarRadio.Active;
            if (Item(12, new Rect(x, y, half, 42), radio != null ? RadioLabel(radio) : "РАДИО  /  НЕТ", 15)) ActivateMenu(12);
            if (Item(14, new Rect(col2, y, half, 42), Net.CoopNet.Online ? "ОНЛАЙН  /  С ДРУГОМ  ●" : "ОНЛАЙН  /  ИГРА С ДРУГОМ", 15)) ActivateMenu(14);
            y += 50;
            if (Item(15, new Rect(x, y, half, 42), "УПРАВЛЕНИЕ", 15)) ActivateMenu(15);
            if (Item(16, new Rect(col2, y, half, 42), ShowFps ? "ПОКАЗЫВАТЬ FPS  /  ДА" : "ПОКАЗЫВАТЬ FPS  /  НЕТ", 15)) ActivateMenu(16);
            y += 50;
            if (Item(17, new Rect(x, y, half, 42), LookLabels[Mathf.Clamp(DriveCamera.SensitivityLevel, 0, LookLabels.Length - 1)], 15)) ActivateMenu(17);
            if (Item(18, new Rect(col2, y, half, 42), ScreenResolution.Label, 15)) ActivateMenu(18);
            y += 50;
            if (Item(19, new Rect(x, y, half, 42), GraphicsQuality.AntiAliasingLabel, 15)) ActivateMenu(19);
        }

        // A click does what Enter does on the same item.
        void ActivateMenu(int index)
        {
            menuSelected = index;
            ActivateSelection(false);
        }

        // Key / action table, reached from the settings.
        static readonly string[] ControlKeys =
        {
            "W A S D  /  СТРЕЛКИ", "ПРОБЕЛ", "CTRL", "X", "Q", "G", "C", "SHIFT", "R", "L", "H",
            "M", "B  /  V", "−  /  =", "ПКМ + МЫШЬ", "T", "F2  /  N", "F3  /  F4",
            "F", "ЛКМ  /  Q", "1 – 8  /  Z", "ESC",
        };
        static readonly string[] ControlActions =
        {
            "газ, тормоз, руль", "ручной тормоз", "дрифт (пешком: присесть / встать)", "нитро", "пешком: прицелиться", "пешком: на колени, извиниться",
            "камера (пешком: 1-е / 3-е лицо)", "взгляд назад (пешком: бег)", "вернуться на дорогу (пешком: в свою машину)", "фары", "сигнал", "карта", "радио: следующая / предыдущая",
            "громкость радио", "обзор вокруг машины", "к другу (онлайн)", "погода / ночь", "телеметрия / мини-HUD",
            "выйти / сесть (у друга: за руль или пассажиром)", "пешком: стрелять / целиться (держать Q)", "оружие (8 — бомба) / перезарядка", "пауза",
        };

        void ControlsPage()
        {
            const float x = 388, w = 664;
            Panel(x, 92, w, 716, .97f, 18);
            Accent(420, 122, 3, 24, amber);
            Label(434, 122, 400, 24, "НАСТРОЙКИ", heading);
            Label(432, 146, 400, 44, "Управление", title);
            Rounded(new Rect(432, 198, 576, 1), UiTheme.Edge, 0);
            var keyStyle = Style(13, amber, FontStyle.Bold, TextAnchor.MiddleCenter);
            var actionStyle = Style(15, cream, FontStyle.Normal, TextAnchor.MiddleLeft);
            for (int i = 0; i < ControlKeys.Length; i++)
            {
                float y = 208 + i * 23;
                if (i % 2 == 0)
                    Rounded(new Rect(432, y, 576, 23), UiTheme.Alpha(Color.white, .025f), 6);
                Rounded(new Rect(440, y + 3, 196, 17), UiTheme.Fill(1), 6);
                Label(440, y + 3, 196, 17, ControlKeys[i], keyStyle);
                Label(656, y, 352, 23, ControlActions[i], actionStyle);
            }
            Label(432, 716, 576, 18, "Геймпад (DualSense с вибрацией и курками): стики и R2/L2 — езда и ходьба, R2 — огонь, L2 — прицел,", Style(12, muted));
            Label(432, 733, 576, 18, "✕ — ручник / прыжок, △ — нитро / оружие, крестовина: ↑ выйти-сесть, ↓ дрифт / на колени, ← фары / присесть, → коробка, тачпад — карта", Style(12, muted));
            if (Item(0, new Rect(432, 756, 200, 40), "НАЗАД", 15))
                ActivateMenu(0);
        }

        // ---- title screen ------------------------------------------------------------------
        static readonly string[] TitleItems = { "НАЧАТЬ ИГРУ", "РЕЖИМЫ", "ИГРА ПО СЕТИ", "НАСТРОЙКИ", "ВЫХОД" };

        void TitleAction(int item)
        {
            switch (item)
            {
                case 0: StartMode(GameMode.Free); break;
                case 1: run.OnTitle = false; run.ChoosingMode = true; menuSelected = (int)run.Mode; break;
                case 2: OpenCoop(); break;
                case 3: run.OnTitle = false; run.ChoosingMode = false; menuSelected = 1; break;
                case 4: Application.Quit(); break;
            }
        }

        void Title()
        {
            menuButton = 0;
            float t = Time.unscaledTime;
            // The shade behind is drawn in screen space by Backdrop().
            // Pinned to the left edge of the window, so on wide screens the city fills the rest.
            float x = edgeL + 120;

            // The logo: heavy block letters with a soft drop shadow, the "67" breathing in orange.
            var shadow = Style(132, UiTheme.Alpha(Color.black, .55f), FontStyle.Bold);
            var letters = Style(132, Color.white, FontStyle.Bold);
            Label(x + 4, 84 + 6, 320, 150, "GTA", shadow);
            Label(x + 306, 84 + 6, 220, 150, "67", shadow);
            var previous = GUI.contentColor;
            GUI.contentColor = cream;
            Label(x, 84, 320, 150, "GTA", letters);
            float pulse = .5f + .5f * Mathf.Sin(t * 1.6f);
            GUI.contentColor = Color.Lerp(amber, new Color(1, .5f, .3f), pulse * .6f);
            Label(x + 302, 84, 220, 150, "67", letters);
            GUI.contentColor = previous;
            Rounded(new Rect(x + 4, 236, 300, 40), UiTheme.Fill(.9f), 8);
            Rounded(new Rect(x + 4, 236, 5, 40), UiTheme.Accent2, 2);
            Label(x + 24, 236, 280, 40, "O N L I N E", Style(26, UiTheme.Accent2, FontStyle.Bold, TextAnchor.MiddleLeft));

            // Menu: big entries that light up smoothly.
            for (int i = 0; i < TitleItems.Length; i++)
            {
                var rect = new Rect(x, 330 + i * 70, 440, 58);
                if (Item(menuButton++, rect, TitleItems[i], 24, .55f))
                {
                    menuSelected = i;
                    TitleAction(i);
                }
            }
            bool online = Net.CoopNet.Online;
            Label(x + 2, 698, 440, 20, online ? "ОНЛАЙН  ●  ПОДКЛЮЧЕНО" : "ОДИНОЧНАЯ ИГРА", Style(12, online ? UiTheme.Accent2 : muted, FontStyle.Bold));
            Label(edgeR - 24 - 600, edgeB - 24 - 16, 600, 16, versionLine, Style(11, UiTheme.Alpha(muted, .75f), FontStyle.Normal, TextAnchor.LowerRight));
        }

        static readonly string[] ModeTitles = { "СВОБОДНАЯ ЕЗДА", "ЕЗДА НА ДИСТАНЦИЮ" };
        static readonly string[] ModeSubtitles = { "ОСТРОВ  •  ГОРОД, ТРАССА, ГОРЫ", "БЕСКОНЕЧНЫЙ ГОРОД" };
        static readonly string[] ModeLines =
        {
            "Остров: большой город с портом,\nтрасса вдоль моря, горы, лес,\nгородок с заправкой наверху.\nКруг 9 км — и обратно в город.",
            "Бесконечный город: одни и те же\nкварталы повторяются без конца.\nПлотный трафик, очки за обгоны.\nЕдь как можно дальше без аварий.",
        };

        void ModeSelect()
        {
            menuButton = 0;
            // The shade behind is drawn in screen space by Backdrop().
            Accent(250, 120, 3, 26, amber);
            Label(264, 120, 600, 24, "РЕЖИМЫ", heading);
            Label(262, 146, 900, 56, "Выбери режим", Style(40, cream, FontStyle.Bold));

            for (int i = 0; i < 2; i++)
            {
                float x = 250 + i * 480, y = 238, w = 460, h = 360;
                var rect = new Rect(x, y, w, h);
                int index = menuButton++;
                bool hover = rect.Contains(Event.current.mousePosition);
                if (hover && Event.current.type == EventType.MouseMove)
                    menuSelected = index;
                bool selected = index == menuSelected;
                float g = Glow(index, selected || hover);
                if (selected)
                    Rounded(new Rect(x - 3, y - 3, w + 6, h + 6), UiTheme.Alpha(amber, .35f + .65f * g), 22);
                Panel(x, y, w, h, .9f + .07f * g, 20);
                var ink = selected ? amber : UiTheme.Alpha(muted, .8f);
                // Pictogram: a road for the course, a looping street grid for the endless city.
                if (i == 0)
                {
                    for (int n = 0; n < 12; n++)
                    {
                        float t = n / 11f;
                        Rounded(new Rect(x + 40 + t * 380, y + 70 + Mathf.Sin(t * 5.5f) * 26, 30, 10), ink, 5);
                    }
                }
                else
                {
                    for (int bx = 0; bx < 5; bx++)
                        for (int by = 0; by < 2; by++)
                            Rounded(new Rect(x + 40 + bx * 64, y + 44 + by * 42, 50, 32), selected && bx % 2 == 0 ? UiTheme.Alpha(amber, .85f) : UiTheme.Alpha(muted, .45f), 6);
                    Label(x + 366, y + 40, 70, 70, "∞", Style(54, selected ? amber : cream, FontStyle.Bold, TextAnchor.MiddleCenter));
                }

                Label(x + 36, y + 144, w - 60, 36, ModeTitles[i], Style(26, cream, FontStyle.Bold));
                Label(x + 38, y + 180, w - 60, 18, ModeSubtitles[i], Style(12, amber, FontStyle.Bold));
                Label(x + 38, y + 212, w - 70, 100, ModeLines[i], Style(15, UiTheme.Alpha(cream, .8f)));
                if (i == 1)
                    Label(x + 38, y + 316, w - 70, 22, run.BestDistance > 1 ? $"РЕКОРД  {run.BestDistance / 1000:0.00} KM" : "РЕКОРДА ПОКА НЕТ", Style(13, amber, FontStyle.Bold));
                if (GUI.Button(rect, GUIContent.none, GUIStyle.none) && UiSound.Click())
                    StartMode((GameMode)i);
            }

            if (Item(menuButton++, new Rect(250, 618, 940, 56), Net.CoopNet.Online ? "ИГРА С ДРУГОМ  •  ПОДКЛЮЧЕНО  ●" : "ИГРАТЬ С ДРУГОМ ПО ИНТЕРНЕТУ", 18))
                OpenCoop();
            if (Item(menuButton++, new Rect(250, 694, 220, 48), "НАЗАД", 16))
                ModesBack();
        }

        void Inspection()
        {
            menuButton = 0;
            Anchor(0, .5f);
            Panel(36, 96, 372, 740, .95f, 18);
            Accent(62, 120, 3, 22, amber);
            Label(76, 118, 300, 20, "СТУДИЯ", heading);
            Label(59, 150, 330, 50, run.Car.Name, title);
            Label(61, 200, 320, 22, run.Car.Subtitle, small);
            Rounded(new Rect(61, 230, 320, 1), UiTheme.Edge, 0);

            var body = run.Player.GetComponent<CarBody>();
            Color c = run.CurrentPaint;
            // Ready colours.
            Label(61, 242, 200, 18, "ГОТОВЫЕ ЦВЕТА", tiny);
            for (int i = 0; i < RunSession.PresetCount; i++)
            {
                var r = new Rect(61 + i * 52, 264, 42, 32);
                bool on = !run.Profile.customPaint && run.Profile.paint == i;
                Rounded(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), on ? amber : UiTheme.Edge, 9);
                Rounded(r, RunSession.PaintColor(i), 8);
                if (GUI.Button(r, GUIContent.none, GUIStyle.none) && UiSound.Click()) run.PickPreset(i);
            }
            // Any colour: hue across the rainbow, then red, green and blue.
            Label(61, 312, 200, 18, "ЛЮБОЙ ЦВЕТ", tiny);
            Rounded(new Rect(301, 308, 80, 26), c, 8);
            Color.RGBToHSV(c, out float hue, out float sat, out float val);
            float h2 = PickSlider(1, new Rect(61, 338, 320, 22), hue, HueTexture(), out bool hueDone);
            if (!Mathf.Approximately(h2, hue) || hueDone)
            {
                // A grey has no hue: picking one gives it some colour.
                var picked = Color.HSVToRGB(h2, Mathf.Max(sat, .65f), Mathf.Max(val, .35f));
                run.SetCustomPaint(picked, hueDone);
                c = picked;
            }
            string[] names = { "R", "G", "B" };
            for (int ch = 0; ch < 3; ch++)
            {
                float y = 372 + ch * 36;
                Label(61, y + 2, 20, 20, names[ch], small);
                float v = c[ch];
                float nv = PickSlider(2 + ch, new Rect(84, y, 250, 20), v, ChannelTexture(ch, c), out bool done);
                Label(340, y + 2, 44, 20, Mathf.RoundToInt(nv * 255).ToString(), small);
                if (!Mathf.Approximately(nv, v) || done)
                {
                    c[ch] = nv;
                    run.SetCustomPaint(c, done);
                }
            }
            // Finish.
            Label(61, 488, 200, 18, "МАТОВОСТЬ", tiny);
            float matte = run.Profile.matte;
            float nm = PickSlider(5, new Rect(61, 510, 273, 20), matte, MatteTexture(), out bool matteDone);
            Label(340, 510, 50, 20, Mathf.RoundToInt(nm * 100) + "%", small);
            if (!Mathf.Approximately(nm, matte) || matteDone) run.SetMatte(nm, matteDone);
            Label(61, 536, 320, 18, nm < .15f ? "глянцевый лак" : nm < .6f ? "сатин" : "матовая плёнка", tiny);
            if (body && !body.Repaintable) Label(61, 556, 320, 18, "у этой машины кузов не перекрашивается", tiny);

            if (Button(61, 590, 320, "ГОТОВЫЕ ЦВЕТА  /  СЛЕДУЮЩИЙ")) run.CyclePaint();
            if (Button(61, 642, 320, "ГАРАЖ  /  ДРУГАЯ МАШИНА")) run.CycleCar();
            if (Button(61, 694, 320, "НАЗАД")) { view.Inspect(false); menuSelected = 0; }
            Label(61, 748, 318, 70, run.Saved ? "Цвет сохранён в профиле, друзья видят его онлайн.\nМЫШЬ  ползунки    TAB  выбор    ENTER  применить\nESC  вернуться на дорогу" : "Цвет применён, профиль не сохранился.", tiny);
            Anchor(1, 1);
            Label(1080, 838, 310, 25, run.Car.Name.ToUpper(), Style(12, muted, FontStyle.Bold, TextAnchor.UpperRight));
            GUI.matrix = canvas;
        }

        // A slider drawn over a gradient: click or drag anywhere on it. `done`: the mouse was let
        // go this frame (the moment to save).
        int dragging = -1;
        float PickSlider(int id, Rect r, float value, Texture2D background, out bool done)
        {
            done = false;
            var e = Event.current;
            Rect hit = new(r.x - 6, r.y - 6, r.width + 12, r.height + 12);
            if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition)) { dragging = id; e.Use(); }
            if (dragging == id && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown || e.type == EventType.Used))
                value = Mathf.Clamp01((e.mousePosition.x - r.x) / r.width);
            if (dragging == id && e.type == EventType.MouseUp) { value = Mathf.Clamp01((e.mousePosition.x - r.x) / r.width); dragging = -1; done = true; e.Use(); }
            GUI.DrawTexture(r, background, ScaleMode.StretchToFill, true, 0, Color.white, Vector4.zero, Vector4.one * (r.height / 2));
            GUI.DrawTexture(r, flat, ScaleMode.StretchToFill, true, 0, UiTheme.Edge, 1, r.height / 2);
            float kx = r.x + value * r.width;
            Rounded(new Rect(kx - 7, r.y - 4, 14, r.height + 8), Color.white, 7);
            Rounded(new Rect(kx - 4, r.y - 1, 8, r.height + 2), new Color(.1f, .1f, .12f), 4);
            return value;
        }

        Texture2D hueTexture, matteTexture;
        readonly Texture2D[] channelTextures = new Texture2D[3];

        static Texture2D Strip(int n) => new(n, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };

        Texture2D HueTexture()
        {
            if (hueTexture) return hueTexture;
            hueTexture = Strip(128);
            for (int i = 0; i < 128; i++) hueTexture.SetPixel(i, 0, Color.HSVToRGB(i / 127f, .85f, .95f));
            hueTexture.Apply();
            return hueTexture;
        }

        Texture2D ChannelTexture(int ch, Color c)
        {
            var t = channelTextures[ch] ? channelTextures[ch] : channelTextures[ch] = Strip(32);
            for (int i = 0; i < 32; i++) { var k = c; k[ch] = i / 31f; k.a = 1; t.SetPixel(i, 0, k); }
            t.Apply();
            return t;
        }

        Texture2D MatteTexture()
        {
            if (matteTexture) return matteTexture;
            matteTexture = Strip(64);
            for (int i = 0; i < 64; i++) { float f = i / 63f; matteTexture.SetPixel(i, 0, Color.Lerp(new Color(.85f, .88f, .95f), new Color(.32f, .32f, .33f), f)); }
            matteTexture.Apply();
            return matteTexture;
        }

        // ---- car-simulator style instruments ---------------------------------

        void Disc(Vector2 centre, float radius, Color colour) =>
            Rounded(new Rect(centre.x - radius, centre.y - radius, radius * 2, radius * 2), colour, radius);

        // A ring drawn from short rotated segments.
        void Ring(Vector2 centre, float radius, float thickness, Color colour, float fromDeg = -180, float toDeg = 180, int segments = 72)
        {
            var matrix = GUI.matrix;
            float step = (toDeg - fromDeg) / segments;
            float width = Mathf.Abs(Mathf.Deg2Rad * step) * radius + 1.6f;
            for (int i = 0; i <= segments; i++)
            {
                Rotate(centre, fromDeg + step * i);
                Quad(new Rect(centre.x - width / 2, centre.y - radius - thickness / 2, width, thickness), colour);
                GUI.matrix = matrix;
            }
        }

        void Spoke(Vector2 centre, float angle, float inner, float outer, float width, Color colour)
        {
            var matrix = GUI.matrix;
            Rotate(centre, angle);
            Quad(new Rect(centre.x - width / 2, centre.y - outer, width, outer - inner), colour);
            GUI.matrix = matrix;
        }

        Vector2 Polar(Vector2 centre, float angle, float radius)
        {
            float rad = (angle - 90) * Mathf.Deg2Rad;
            return new Vector2(centre.x + Mathf.Cos(rad) * radius, centre.y + Mathf.Sin(rad) * radius);
        }

        // Numbers on the HUD change every frame or so; building their text once per change
        // (not once per frame) keeps OnGUI free of per-frame string garbage.
        sealed class Readout
        {
            int key = int.MinValue;
            string text = "";
            public string Get(int value, System.Func<int, string> make)
            {
                if (value != key)
                {
                    key = value;
                    text = make(value);
                }
                return text;
            }
        }

        readonly Readout scoreText = new(), streakText = new(), creditText = new(), kmText = new(), bestText = new(),
            lapText = new(), speedText = new(), rpmText = new(), driftText = new();
        static readonly string[] WeatherNames = { "ЯСНО", "ДОЖДЬ", "ТУМАН", "СНЕГ" };
        static readonly string[] Gears = { "R", "N", "1", "2", "3", "4", "5", "6" };
        static readonly string[] TickLabels = BuildTicks();
        string[] weatherLines, stationLabels;
        GUIStyle radioTitle;
        readonly GUIContent messageContent = new();
        const float Edge = 24;          // every HUD block keeps 24 px from the screen edges
        const float FriendY = Edge + 64 + 64 + 46;   // online chip, under the radio and its volume pop-up

        static string[] BuildTicks()
        {
            var ticks = new string[41];
            for (int i = 0; i < ticks.Length; i++) ticks[i] = (i * 20).ToString();
            return ticks;
        }

        void Speedometer(Vehicle car)
        {
            Vector2 c = new(1244, 724);
            const float outer = 112, sweep = 128;
            float top = Mathf.Max(200, car.Spec.speedLimiter);
            top = Mathf.Ceil(top / 40) * 40;

            // Dial body.
            Disc(c + new Vector2(0, 4), outer + 6, new Color(0, 0, 0, .35f));
            Disc(c, outer, UiTheme.Fill(.9f));
            Ring(c, outer - 2, 2, UiTheme.Alpha(Color.white, .12f));

            // Rev ring inside the rim.
            float revs = Mathf.Clamp01(Mathf.InverseLerp(car.Spec.idle, car.Spec.redline, shownRpm));
            Ring(c, outer - 12, 7, UiTheme.Alpha(Color.white, .07f), -sweep, sweep, 48);
            if (revs > .003f)
                Ring(c, outer - 12, 7, revs > .88f ? UiTheme.Bad : amber, -sweep, -sweep + 2 * sweep * revs, Mathf.Max(2, Mathf.RoundToInt(48 * revs)));

            // Scale.
            var tickStyle = Style(12, UiTheme.Alpha(cream, .85f), FontStyle.Bold, TextAnchor.MiddleCenter);
            for (int i = 0; i * 20 <= top; i++)
            {
                float v = i * 20;
                float angle = Mathf.Lerp(-sweep, sweep, v / top);
                bool major = i % 2 == 0;
                Spoke(c, angle, outer - (major ? 42 : 33), outer - 22, major ? 4f : 2.5f,
                    v / top > .82f ? UiTheme.Alpha(UiTheme.Bad, .95f) : new Color(1, 1, 1, major ? .9f : .45f));
                if (major && i < TickLabels.Length)
                {
                    Vector2 p = Polar(c, angle, outer - 54);
                    Label(p.x - 22, p.y - 10, 44, 20, TickLabels[i], tickStyle);
                }
            }

            // Needle: bright, with a counterweight, so it reads at a glance.
            float needle = Mathf.Lerp(-sweep, sweep, Mathf.Clamp01(shownSpeed / top));
            Spoke(c, needle, -22, outer - 24, 7, amber);
            Spoke(c, needle, -22, outer - 30, 3, new Color(1, .82f, .66f));
            Disc(c, 14, UiTheme.Panel);
            Ring(c, 14, 2.5f, UiTheme.Alpha(amber, .8f));

            // Readout.
            Label(c.x - 90, c.y + 22, 180, 48, speedText.Get(Mathf.RoundToInt(shownSpeed), v => v.ToString()), Style(32, cream, FontStyle.Bold, TextAnchor.MiddleCenter));
            Label(c.x - 90, c.y + 62, 180, 18, "KM/H", Style(12, muted, FontStyle.Bold, TextAnchor.MiddleCenter));

            // Gear selector, mobile-simulator style.
            int current = car.Engine.Gear < 0 ? 0 : car.Engine.Gear + 1;
            for (int i = 0; i < Gears.Length; i++)
            {
                float y = 610 + i * 30;
                bool on = i == current;
                Rounded(new Rect(1380, y, 36, 26), on ? amber : UiTheme.Fill(.8f), 8);
                Label(1380, y + 3, 36, 20, Gears[i], Style(16, on ? UiTheme.Panel : muted, FontStyle.Bold, TextAnchor.MiddleCenter));
            }

            Label(1353, 582, 90, 18, "АВТО", Style(12, amber, FontStyle.Bold, TextAnchor.MiddleCenter));
            // Headlight tell-tale above the gearbox mode.
            var body = car.GetComponent<CarBody>();
            bool beams = body && body.LightsOn;
            Rounded(new Rect(1368, 546, 60, 26), beams ? UiTheme.Alpha(UiTheme.Good, .25f) : UiTheme.Fill(.7f), 13);
            Label(1368, 546, 60, 26, "◐", Style(12, beams ? UiTheme.Good : muted, FontStyle.Bold, TextAnchor.MiddleCenter));

            // Engine plate.
            Rounded(new Rect(1110, 842, 268, 34), UiTheme.Fill(.84f), 17);
            Label(1124, 842, 150, 34, car.Spec.carName.ToUpper(), Style(12, cream, FontStyle.Bold, TextAnchor.MiddleLeft));
            Label(1228, 842, 136, 34, rpmText.Get(Mathf.RoundToInt(shownRpm / 50) * 50, v => v + " RPM"), Style(12, muted, FontStyle.Bold, TextAnchor.MiddleRight));
            NitroGauge();
        }

        // Nitro bottle: a vertical bar left of the dial; glows while the boost is lit.
        void NitroGauge()
        {
            var n = Nitro.Player;
            if (n == null) return;
            var blue = UiTheme.Accent2;
            var frame = new Rect(1088, 624, 26, 196);
            Rounded(new Rect(frame.x - 3, frame.y - 3, frame.width + 6, frame.height + 6), new Color(0, 0, 0, .3f), 15);
            Rounded(frame, UiTheme.Fill(.9f), 13);
            float fill = Mathf.Clamp01(n.Tank) * (frame.height - 8);
            if (fill > 2)
            {
                var full = n.Tank > .15f ? Color.Lerp(blue, new Color(.8f, .96f, 1f), n.Glow) : UiTheme.Alpha(muted, .9f);
                Rounded(new Rect(frame.x + 4, frame.yMax - 4 - fill, frame.width - 8, fill), full, 9);
            }
            Label(1066, 824, 70, 18, "НИТРО", Style(12, blue, FontStyle.Bold, TextAnchor.MiddleCenter));
        }

        // Car radio, always on screen: station, track, progress, and ◀ ⏻ ▶ that work with the
        // mouse while the cursor is visible. B / V change station, - / = the radio volume.
        void RadioPlate()
        {
            var radio = CarRadio.Active;
            if (radio == null) return;
            radioTitle ??= new GUIStyle(Style(16, cream, FontStyle.Bold, TextAnchor.MiddleLeft)) { wordWrap = false, clipping = TextClipping.Clip };
            var r = new Rect(Edge, Edge + 64, 438, 64);
            Rounded(r, UiTheme.Fill(.84f), 16);
            bool on = radio.On;
            Disc(new Vector2(r.x + 22, r.y + 22), 5, on ? UiTheme.Accent2 : UiTheme.Alpha(muted, .6f));
            Label(r.x + 34, r.y + 11, 64, 22, on ? StationLabel(radio) : "РАДИО", Style(12, on ? UiTheme.Accent2 : muted, FontStyle.Bold, TextAnchor.MiddleLeft));
            if (on)
                Label(r.x + 98, r.y + 11, r.width - 98 - 126, 22, radio.Title, radioTitle);
            else
                Label(r.x + 98, r.y + 11, r.width - 98 - 126, 22, "ВЫКЛ", Style(12, muted, FontStyle.Bold, TextAnchor.MiddleLeft));
            Bar(r.x + 18, r.y + 44, r.width - 36 - 118, 4, on ? radio.Position : 0, UiTheme.Accent2);

            float bx = r.xMax - 118, by = r.y + 16;
            if (RoundButton(new Rect(bx, by, 32, 32), "◀", false)) radio.Prev();
            var power = new Rect(bx + 40, by, 32, 32);
            if (RoundButton(power, "", on)) radio.Toggle();
            // The power sign: an open ring with a bar through the gap.
            var pc = power.center;
            var ink = on ? UiTheme.Accent2 : cream;
            Ring(pc, 7, 2, ink, 40, 320, 16);
            Spoke(pc, 0, -1, 10, 2, ink);
            if (RoundButton(new Rect(bx + 80, by, 32, 32), "▶", false)) radio.Next();

            // Volume pop-up under the plate for a moment after - / =.
            float since = Time.unscaledTime - radio.VolumeChanged;
            if (since < 1.6f)
            {
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01((1.6f - since) * 3));
                var v = new Rect(r.x, r.yMax + 8, r.width, 30);
                Rounded(v, UiTheme.Fill(.84f), 15);
                Label(v.x + 18, v.y, 170, v.height, "ГРОМКОСТЬ РАДИО", Style(12, muted, FontStyle.Bold, TextAnchor.MiddleLeft));
                Bar(v.x + 170, v.y + 13, v.width - 240, 4, radio.Volume, UiTheme.Accent2);
                Label(v.xMax - 62, v.y, 46, v.height, Percent[Mathf.Clamp(Mathf.RoundToInt(radio.Volume * 10), 0, 10)], Style(12, cream, FontStyle.Bold, TextAnchor.MiddleRight));
                GUI.color = Color.white;
            }
        }

        string StationLabel(CarRadio radio)
        {
            if (stationLabels == null || stationLabels.Length != radio.Count)
            {
                stationLabels = new string[radio.Count];
                for (int i = 0; i < stationLabels.Length; i++) stationLabels[i] = $"FM {i + 1}";
            }
            return stationLabels[Mathf.Clamp(radio.Station, 0, stationLabels.Length - 1)];
        }

        // Round HUD button; only reacts while the mouse cursor is visible.
        bool RoundButton(Rect rect, string glyph, bool lit)
        {
            bool live = Cursor.visible;
            bool hover = live && rect.Contains(Event.current.mousePosition);
            Rounded(rect, hover ? UiTheme.Alpha(UiTheme.PanelHover, 1) : UiTheme.Alpha(Color.white, lit ? .12f : .06f), rect.height / 2);
            if (glyph.Length > 0)
                Label(rect.x, rect.y, rect.width, rect.height, glyph, Style(12, lit ? UiTheme.Accent2 : cream, FontStyle.Bold, TextAnchor.MiddleCenter));
            return live && GUI.Button(rect, GUIContent.none, GUIStyle.none) && UiSound.Click();
        }

        void TopChips(Vehicle car)
        {
            // Score and wallet, like the money counters in the mobile simulators.
            Anchor(0, 0);
            var chip = new Rect(Edge, Edge, 256, 56);
            Rounded(chip, UiTheme.Fill(.84f), 16);
            Disc(new Vector2(chip.x + 26, chip.y + 28), 13, amber);
            Label(chip.x + 13, chip.y + 18, 26, 20, "S", Style(12, UiTheme.Panel, FontStyle.Bold, TextAnchor.MiddleCenter));
            // The score flashes orange when it jumps: tint, so the cached style stays the same.
            var tint = GUI.contentColor;
            GUI.contentColor = Color.Lerp(cream, amber, scorePop);
            Label(chip.x + 50, chip.y + 1, 200, 36, scoreText.Get(Mathf.RoundToInt(shownScore), v => v.ToString("N0")), Style(28, Color.white, FontStyle.Bold, TextAnchor.MiddleLeft));
            GUI.contentColor = tint;
            int streak = Mathf.RoundToInt(run.Scoring.Multiplier * 10) * 10000 + Mathf.Min(9999, run.Scoring.NearMisses);
            Label(chip.x + 52, chip.y + 34, 200, 16, streakText.Get(streak, v => $"×{v / 10000 / 10f:0.0}   •   ОБГОНОВ {v % 10000}"), Style(12, muted, FontStyle.Bold));

            var wallet = new Rect(chip.xMax + 12, Edge, 170, 56);
            Rounded(wallet, UiTheme.Fill(.84f), 16);
            Label(wallet.x + 18, wallet.y + 8, 140, 24, creditText.Get(run.Profile.credits, v => v.ToString("N0")), Style(16, cream, FontStyle.Bold));
            Label(wallet.x + 18, wallet.y + 34, 140, 16, "КРЕДИТЫ", Style(12, muted, FontStyle.Bold));

            // Route / distance pill on the right, left of the FPS plate.
            Anchor(1, 0);
            float right = 1440 - Edge - (ShowFps ? 96 : 0);
            var pill = new Rect(right - 380, Edge, 380, 64);
            Rounded(pill, UiTheme.Fill(.84f), 16);
            float px = pill.x + 18, pw = pill.width - 36;
            if (run.Mode == GameMode.Distance)
            {
                float km = run.Scoring.Distance / 1000, best = Mathf.Max(run.BestDistance, run.Scoring.Distance) / 1000;
                bool record = run.Scoring.Distance >= run.BestDistance && run.Scoring.Distance > 50;
                Label(px, pill.y + 8, 220, 16, "ЕЗДА НА ДИСТАНЦИЮ", Style(12, amber, FontStyle.Bold));
                Label(px, pill.y + 20, 220, 34, kmText.Get(Mathf.FloorToInt(km * 100), v => $"{v / 100f:0.00} KM"), Style(28, cream, FontStyle.Bold));
                Label(px + pw - 180, pill.y + 8, 180, 16, record ? "НОВЫЙ РЕКОРД" : "РЕКОРД", Style(12, record ? amber : muted, FontStyle.Bold, TextAnchor.UpperRight));
                Label(px + pw - 180, pill.y + 24, 180, 22, bestText.Get(Mathf.FloorToInt(best * 100), v => $"{v / 100f:0.00} KM"), Style(16, record ? amber : cream, FontStyle.Bold, TextAnchor.UpperRight));
                Bar(px, pill.y + 54, pw, 4, best > 0 ? km / best : 0, record ? amber : UiTheme.Alpha(muted, .8f));
                return;
            }

            Label(px, pill.y + 8, pw, 20, Island.District(MapFocus.position), Style(16, cream, FontStyle.Bold));
            Label(px, pill.y + 10, pw, 16, lapText.Get(Mathf.FloorToInt(run.LapProgress / 100), v => $"КРУГ  {v / 10f:0.0} / {Route.Course / 1000:0.0} KM"), Style(12, muted, FontStyle.Bold, TextAnchor.UpperRight));
            Bar(px, pill.y + 34, pw, 5, run.LapProgress / Route.Course, amber);
            Label(px, pill.y + 43, pw, 16, WeatherLine(), Style(12, muted));
        }

        string WeatherLine()
        {
            if (weatherLines == null)
            {
                weatherLines = new string[WeatherNames.Length * 2];
                for (int i = 0; i < weatherLines.Length; i++)
                    weatherLines[i] = $"{WeatherNames[i / 2]}   •   {(i % 2 == 1 ? "23:40" : "17:24")}   •   ОСТРОВ";
            }
            return weatherLines[Mathf.Clamp((int)sky.Current, 0, WeatherNames.Length - 1) * 2 + (Atmosphere.Night ? 1 : 0)];
        }

        // Round minimap with the road ahead, as in the driving simulators.
        Transform MapFocus => FootPlayer.Active && !FootPlayer.Active.Riding ? FootPlayer.Active.transform : run.Player.transform;

        void MiniMap()
        {
            const float radius = 86;
            Vector2 c = new(Edge + radius + 5, 900 - Edge - radius - 5);
            Disc(c + new Vector2(0, 4), radius + 5, new Color(0, 0, 0, .35f));
            Disc(c, radius, UiTheme.Fill(.88f));
            Ring(c, radius - 2, 2, UiTheme.Alpha(UiTheme.Accent2, .35f));
            Ring(c, radius - 14, 1.5f, UiTheme.Alpha(UiTheme.Accent2, .08f));

            {
                GridMap(c, radius);
                return;
            }
            Vector3 origin = run.Player.transform.position;
            Vector3 right = Route.Right(origin.z);
            Vector3 forward = Route.Forward(origin.z);
            Vector2 previous = new(c.x, c.y + 40);
            var points = routePoints;
            points.Clear();
            points.Add(previous);
            for (int i = 1; i <= 30; i++)
            {
                float z = Mathf.Min(Route.Length, origin.z + i * 22);
                Vector3 delta = Route.Center(z) - Route.Center(origin.z);
                Vector2 next = new(c.x + Mathf.Clamp(Vector3.Dot(delta, right) * .30f, -56, 56),
                                   c.y + 40 - Vector3.Dot(delta, forward) * .150f);
                if (Vector2.Distance(next, c) > radius - 10)
                    break;
                points.Add(next);
            }

            // Carriageway, then the dashed centre line over it.
            for (int i = 1; i < points.Count; i++)
                Stroke(points[i - 1], points[i], 15, new Color(.16f, .18f, .19f, .95f));
            for (int i = 1; i < points.Count; i++)
                if (i % 2 == 1)
                    Stroke(points[i - 1], points[i], 2.5f, Route.RightClosed(origin.z + i * 22) ? new Color(1, .42f, .26f) : new Color(.85f, .88f, .86f, .8f));

            if (Net.CoopNet.Online)
                foreach (var kv in Net.CoopNet.Active.Remotes)
                {
                    var friendCar = kv.Value;
                    if (!friendCar || !friendCar.HasState) continue;
                    Vector3 d = friendCar.transform.position - origin;
                    Vector2 m = new(c.x + Mathf.Clamp(Vector3.Dot(d, right) * .30f, -56, 56), c.y + 40 - Vector3.Dot(d, forward) * .150f);
                    if (Vector2.Distance(m, c) > radius - 10)
                        m = c + (m - c).normalized * (radius - 10);
                    Disc(m, 6.5f, new Color(.04f, .055f, .058f, .95f));
                    Disc(m, 5, Net.CoopNet.ColorOf(kv.Key));
                }

            // The car sits still at the bottom of the dial and always points up.
            Vector2 marker = new(c.x, c.y + 40);
            Spoke(marker, 0, 0, 18, 9, new Color(1, .42f, .26f));
            Disc(marker, 4, new Color(1, .85f, .7f));

            Rounded(new Rect(c.x - 52, c.y - radius + 8, 104, 22), new Color(.04f, .055f, .058f, .9f), 11);
            Label(c.x - 52, c.y - radius + 11, 104, 18, "ROAD AHEAD", Style(11, amber, FontStyle.Bold, TextAnchor.MiddleCenter));
            Rounded(new Rect(c.x - 52, c.y + radius - 32, 104, 24), new Color(.04f, .055f, .058f, .9f), 12);
            Label(c.x - 52, c.y + radius - 29, 104, 18, $"{run.Scoring.Distance / 1000:0.00} KM", Style(13, cream, FontStyle.Bold, TextAnchor.MiddleCenter));
        }

        // Distance mode: heading-up map of the street grid with the traffic around the car.
        void GridMap(Vector2 c, float radius)
        {
            const float scale = .26f;
            // On foot the map follows the person, not the parked car.
            var focus = MapFocus;
            Vector3 origin = focus.position;
            Vector3 fwd = focus.forward;
            fwd.y = 0;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            Vector3 right = new(fwd.z, 0, -fwd.x);
            Vector2 centre = c + new Vector2(0, 22);
            float clip = radius - 9;
            Vector2 Map(Vector3 w)
            {
                Vector3 d = w - origin;
                return centre + new Vector2(Vector3.Dot(d, right), -Vector3.Dot(d, fwd)) * scale;
            }

            float reach = (clip + 30) / scale;
            float x0 = GridCity.Snap(origin.x - reach), x1 = GridCity.Snap(origin.x + reach);
            float z0 = GridCity.Snap(origin.z - reach), z1 = GridCity.Snap(origin.z + reach);
            // The island city has edges: only its own streets.
            float lx0 = origin.x - reach, lx1 = origin.x + reach, lz0 = origin.z - reach, lz1 = origin.z + reach;
            if (!Route.Endless)
            {
                x0 = Mathf.Max(x0, 0); x1 = Mathf.Min(x1, Island.CityW);
                z0 = Mathf.Max(z0, 0); z1 = Mathf.Min(z1, Island.CityH);
                lx0 = Mathf.Max(lx0, 0); lx1 = Mathf.Min(lx1, Island.CityW);
                lz0 = Mathf.Max(lz0, 0); lz1 = Mathf.Min(lz1, Island.CityH);
            }
            // The island itself under the roads: the picture of the whole island taken from above
            // (forest, beaches, mountains, city blocks), turned with the car.
            bool picture = !Route.Endless && IslandSnapshot.Ready;
            if (picture && Event.current.type == EventType.Repaint)
            {
                if (!miniMapMaterial)
                {
                    var shader = Resources.Load<Shader>("Shaders/MiniMap");
                    if (shader) miniMapMaterial = new Material(shader) { name = "Minimap" };
                }
                if (miniMapMaterial)
                {
                    float metres = clip / scale;
                    // The disc's middle is a little ahead of the car (the car sits low on the dial).
                    Vector2 uvc = IslandSnapshot.ToUV(origin + fwd * ((centre.y - c.y) / scale));
                    miniMapMaterial.SetVector("_Center", new Vector4(uvc.x, uvc.y, 0, 0));
                    miniMapMaterial.SetFloat("_Scale", metres / (IslandSnapshot.Z1 - IslandSnapshot.Z0));
                    miniMapMaterial.SetFloat("_Aspect", (IslandSnapshot.X1 - IslandSnapshot.X0) / (IslandSnapshot.Z1 - IslandSnapshot.Z0));
                    miniMapMaterial.SetFloat("_Angle", -Mathf.Atan2(fwd.x, fwd.z));
                    miniMapMaterial.SetColor("_Tint", new Color(1, 1, 1, .96f));
                    Graphics.DrawTexture(new Rect(c.x - clip, c.y - clip, clip * 2, clip * 2), IslandSnapshot.Image, miniMapMaterial);
                }
            }
            var segs = gridSegments;
            segs.Clear();
            if (lx1 > lx0 && lz1 > lz0)
            {
                for (float x = x0; x <= x1; x += GridCity.Block)
                    if (ClipToCircle(Map(new Vector3(x, 0, lz0)), Map(new Vector3(x, 0, lz1)), c, clip, out var a, out var b))
                        segs.Add((a, b));
                for (float z = z0; z <= z1; z += GridCity.Block)
                    if (ClipToCircle(Map(new Vector3(lx0, 0, z)), Map(new Vector3(lx1, 0, z)), c, clip, out var a, out var b))
                        segs.Add((a, b));
            }
            // The island road, in pieces near the car.
            if (!Route.Endless)
            {
                Vector3 prev = Route.Center(0);
                for (float s = 25; s <= Route.Course; s += 25)
                {
                    Vector3 next = Route.Center(s);
                    if ((prev - origin).sqrMagnitude < reach * reach * 1.2f || (next - origin).sqrMagnitude < reach * reach * 1.2f)
                        if (ClipToCircle(Map(prev), Map(next), c, clip, out var a, out var b))
                            segs.Add((a, b));
                    prev = next;
                }
            }
            // Roads over the picture: dark carriageway, a light line down the middle.
            foreach (var sg in segs)
                Bar(sg.Item1, sg.Item2, picture ? 5 : 8, UiTheme.Alpha(picture ? new Color(.12f, .13f, .14f) : UiTheme.PanelHover, picture ? .85f : .95f));
            foreach (var sg in segs)
                Bar(sg.Item1, sg.Item2, picture ? 1.2f : 1.5f, UiTheme.Alpha(picture ? new Color(1, .86f, .55f) : UiTheme.Accent2, picture ? .7f : .45f));

            if (run.Traffic != null)
                foreach (var car in run.Traffic.Cars)
                {
                    if (!car.gameObject.activeSelf) continue;
                    Vector2 m = Map(car.transform.position);
                    if (Vector2.Distance(m, c) < clip)
                        Disc(m, 3, UiTheme.Alpha(cream, .75f));
                }

            if (Net.CoopNet.Online)
                foreach (var kv in Net.CoopNet.Active.Remotes)
                {
                    var friend = kv.Value;
                    if (!friend || !friend.HasState) continue;
                    Vector2 m = Map(friend.transform.position);
                    if (Vector2.Distance(m, c) > clip - 4)
                        m = c + (m - c).normalized * (clip - 4);
                    Disc(m, 6.5f, UiTheme.Fill(.95f));
                    Disc(m, 5, Net.CoopNet.ColorOf(kv.Key));
                }
            Spoke(centre, 0, 0, 16, 8, amber);
            Disc(centre, 3.5f, cream);

            // North marker on the rim.
            Vector2 north = new(Vector3.Dot(Vector3.forward, right), -Vector3.Dot(Vector3.forward, fwd));
            Vector2 np = c + north * (radius - 13);
            Disc(np, 8, UiTheme.Fill(.95f));
            Label(np.x - 8, np.y - 8, 16, 16, "N", Style(12, UiTheme.Accent2, FontStyle.Bold, TextAnchor.MiddleCenter));

            Rounded(new Rect(c.x - 52, c.y + radius - 32, 104, 24), UiTheme.Fill(.92f), 12);
            Label(c.x - 52, c.y + radius - 32, 104, 24, kmText.Get(Mathf.FloorToInt(run.Scoring.Distance / 10), v => $"{v / 100f:0.00} KM"), Style(12, cream, FontStyle.Bold, TextAnchor.MiddleCenter));
        }

        static Material miniMapMaterial;

        void Bar(Vector2 a, Vector2 b, float width, Color color)
        {
            Vector2 d = b - a, mid = (a + b) / 2;
            float len = d.magnitude;
            var matrix = GUI.matrix;
            Rotate(mid, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            Quad(new Rect(mid.x - len / 2, mid.y - width / 2, len, width), color);
            GUI.matrix = matrix;
        }

        static bool ClipToCircle(Vector2 a, Vector2 b, Vector2 c, float r, out Vector2 p, out Vector2 q)
        {
            p = q = default;
            Vector2 d = b - a, f = a - c;
            float A = Vector2.Dot(d, d), B = 2 * Vector2.Dot(f, d), C = Vector2.Dot(f, f) - r * r;
            float disc = B * B - 4 * A * C;
            if (A < 1e-4f || disc <= 0) return false;
            float sq = Mathf.Sqrt(disc);
            float t0 = Mathf.Clamp01((-B - sq) / (2 * A)), t1 = Mathf.Clamp01((-B + sq) / (2 * A));
            if (t1 - t0 < 1e-3f) return false;
            p = a + d * t0;
            q = a + d * t1;
            return true;
        }

        // Online: status chip and a name tag over the friend's car.
        void FriendOverlay()
        {
            var net = Net.CoopNet.Active;
            if (net == null || net.State == Net.CoopLink.Phase.Idle)
                return;
            Anchor(0, 0);
            if (!Net.CoopNet.Online)
            {
                Rounded(new Rect(Edge, FriendY, 438, 30), UiTheme.Fill(.8f), 15);
                Label(Edge + 18, FriendY, 410, 30, "ОНЛАЙН  •  " + net.Message.ToUpper(), Style(12, muted, FontStyle.Bold, TextAnchor.MiddleLeft));
                return;
            }
            Rounded(new Rect(Edge, FriendY, 438, 30), UiTheme.Fill(.84f), 15);
            Disc(new Vector2(Edge + 18, FriendY + 15), 5, UiTheme.Accent2);
            string ping = net.Hosting ? "ХОСТ" : $"{net.Ping * 1000:0} МС";
            Label(Edge + 30, FriendY, 400, 30, $"ОНЛАЙН  •  ИГРОКОВ {net.PlayerCount}  •  {ping}", Style(12, cream, FontStyle.Bold, TextAnchor.MiddleLeft));

            // Name tags follow the cars on screen, so they use the plain centred canvas.
            GUI.matrix = canvas;
            var cam = Camera.main;
            if (cam == null) return;
            float scale = Mathf.Min(Screen.width / 1440f, Screen.height / 900f);
            foreach (var kv in net.Remotes)
            {
                var friend = kv.Value;
                if (friend == null || !friend.HasState) continue;
                Vector3 world = friend.transform.position + Vector3.up * 2.3f;
                Vector3 sp = cam.WorldToScreenPoint(world);
                float dist = Vector3.Distance(cam.transform.position, friend.transform.position);
                if (sp.z <= 0 || dist > 900) continue;
                float gx = (sp.x - (Screen.width - 1440 * scale) / 2) / scale;
                float gy = (Screen.height - sp.y - (Screen.height - 900 * scale) / 2) / scale;
                string text = $"{net.NameOf(kv.Key)}   {dist:0} м";
                var style = Style(12, cream, FontStyle.Bold, TextAnchor.MiddleCenter);
                float w = style.CalcSize(new GUIContent(text)).x + 30;
                Rounded(new Rect(gx - w / 2, gy - 26, w, 26), UiTheme.Fill(.84f), 13);
                Disc(new Vector2(gx - w / 2 + 13, gy - 13), 4, Net.CoopNet.ColorOf(kv.Key));
                Label(gx - w / 2 + 6, gy - 26, w, 26, text, style);
            }
        }

        void CoopPanel()
        {
            var net = Net.CoopNet.Ensure();
            menuButton = 0;
            menuSelected = -1;
            codeField ??= new GUIStyle(GUI.skin.textField)
            {
                fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = {textColor = cream, background = null}, focused = {textColor = amber, background = null},
                hover = {textColor = cream, background = null}, active = {textColor = amber, background = null},
            };

            // The shade behind is drawn in screen space by Backdrop().
            Panel(420, 130, 600, 620, .985f, 20);
            Accent(452, 160, 3, 26, amber);
            Label(466, 158, 520, 20, "ОНЛАЙН  /  ИГРА НА ДВОИХ", heading);
            Label(464, 186, 540, 52, "Игра с другом", title);
            Rounded(new Rect(464, 248, 512, 1), UiTheme.Edge, 0);

            var state = net.State;
            var info = Style(15, UiTheme.Alpha(cream, .8f));
            if (state == Net.CoopLink.Phase.Idle)
            {
                Label(464, 266, 512, 60, "Один создаёт игру и отправляет другу код.\nВторой вводит код и подключается.", info);
                if (Button(464, 328, 512, "СОЗДАТЬ ИГРУ"))
                    net.HostGame();
                Label(464, 400, 512, 20, "ИЛИ ВВЕДИ КОД ДРУГА", heading);
                Rounded(new Rect(464, 428, 300, 54), UiTheme.Dim(.98f), 12);
                if (codeInput.Length == 0)
                    Label(464, 428, 300, 54, "• • • • •", Style(26, new Color(1, 1, 1, .18f), FontStyle.Bold, TextAnchor.MiddleCenter));
                bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                GUI.SetNextControlName("coopcode");
                codeInput = Net.CoopLink.Clean(GUI.TextField(new Rect(470, 432, 288, 46), codeInput, 8, codeField));
                if (GUI.GetNameOfFocusedControl() != "coopcode" && codeInput.Length == 0)
                    GUI.FocusControl("coopcode");
                if (Button(780, 434, 196, "ВСТАВИТЬ"))
                    codeInput = Net.CoopLink.Clean(GUIUtility.systemCopyBuffer);
                if ((Button(464, 500, 512, "ПОДКЛЮЧИТЬСЯ") || enter) && codeInput.Length >= 5)
                    net.JoinGame(codeInput);
            }
            else if (net.Hosting && state != Net.CoopLink.Phase.Connected && state != Net.CoopLink.Phase.Failed && state != Net.CoopLink.Phase.PeerLeft)
            {
                Label(464, 266, 512, 22, "КОД ТВОЕЙ ИГРЫ", heading);
                Rounded(new Rect(464, 296, 512, 96), UiTheme.Dim(.98f), 16);
                Label(464, 296, 512, 96, string.Join("  ", net.Code.ToCharArray()), Style(58, amber, FontStyle.Bold, TextAnchor.MiddleCenter));
                if (Button(464, 410, 512, Time.unscaledTime - copiedAt < 2 ? "СКОПИРОВАНО ✓" : "СКОПИРОВАТЬ КОД"))
                {
                    GUIUtility.systemCopyBuffer = net.Code;
                    copiedAt = Time.unscaledTime;
                }
                Label(464, 470, 512, 60, "Отправь код друзьям (до 4 человек).\n" + net.Message + Dots(), info);
                if (Button(464, 560, 512, "ОТМЕНА"))
                    net.Leave();
            }
            else if (state == Net.CoopLink.Phase.Connecting || state == Net.CoopLink.Phase.Searching)
            {
                Label(464, 266, 512, 22, "ПОДКЛЮЧЕНИЕ К " + net.Code, heading);
                Label(464, 300, 512, 60, net.Message + Dots(), Style(20, cream, FontStyle.Bold));
                if (Button(464, 560, 512, "ОТМЕНА"))
                    net.Leave();
            }
            else if (state == Net.CoopLink.Phase.Connected)
            {
                Label(464, 262, 512, 30, $"В игре {net.PlayerCount} из {Net.CoopLink.MaxPlayers}", Style(20, cream, FontStyle.Bold));
                // Who is here, each with the colour of their marker.
                int row = 0;
                foreach (var p in net.Players)
                {
                    float y = 298 + row * 24;
                    Disc(new Vector2(474, y + 10), 6, p.Key == net.Slot ? amber : Net.CoopNet.ColorOf(p.Key));
                    string tag = p.Key == net.Slot ? "  (ты)" : "";
                    if (p.Key == 0) tag += "  • хост";
                    Label(488, y, 250, 22, p.Value + tag, Style(14, cream));
                    row++;
                }
                if (net.Hosting)
                {
                    // The room stays open: more friends can join with the same code.
                    Label(740, 298, 236, 18, "КОД ДЛЯ ДРУЗЕЙ", heading);
                    Label(740, 316, 236, 40, net.Code, Style(30, amber, FontStyle.Bold));
                    if (Button(740, 360, 236, Time.unscaledTime - copiedAt < 2 ? "СКОПИРОВАНО ✓" : "КОПИРОВАТЬ"))
                    {
                        GUIUtility.systemCopyBuffer = net.Code;
                        copiedAt = Time.unscaledTime;
                    }
                }
                else
                    Label(740, 298, 236, 60, $"Пинг {net.Ping * 1000:0} мс\nРежим и погоду\nвыбирает хост", info);
                if (Button(464, 420, 512, "ПОЕХАЛИ"))
                {
                    coopOpen = false;
                    run.Started = true;
                    run.SetPaused(false);
                }
                if (Button(464, 472, 512, "ПЕРЕМЕСТИТЬСЯ К ДРУГУ   (T)"))
                {
                    coopOpen = false;
                    run.Started = true;
                    run.SetPaused(false);
                    net.GoToFriend();
                }
                if (Button(464, 560, 512, "ОТКЛЮЧИТЬСЯ"))
                    net.Leave();
            }
            else
            {
                Label(464, 272, 512, 60, net.Message, Style(20, UiTheme.Bad, FontStyle.Bold));
                if (Button(464, 400, 512, "ПОПРОБОВАТЬ СНОВА"))
                    net.Leave();
            }

            if (Button(464, 676, 512, "НАЗАД"))
            {
                coopOpen = false;
                menuSelected = 0;
                if (!run.Started) run.OnTitle = true;
            }
        }

        static string Dots() => new string('.', 1 + (int)(Time.unscaledTime * 2) % 3);

        void Stroke(Vector2 a, Vector2 b, float width, Color color)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 2));
            for (int i = 0; i <= steps; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, i / (float)steps);
                Rounded(new Rect(p.x - width / 2, p.y - width / 2, width, width), color, width / 2);
            }
        }
    }
}

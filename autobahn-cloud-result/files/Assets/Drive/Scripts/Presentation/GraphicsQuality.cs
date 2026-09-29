using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Autobahn
{
    // Three graphics presets for the player, High, Fast and Mobile (F8 or the pause menu); the
    // two in between (Balanced, Ultra) are only used by the automated measurements now.
    // Mobile is Fast cut down further for weak Macs and phones: 30 frames a second, 720p, one
    // shadow cascade, the shortest view, fewer street lights, traffic cars and trees.
    // The choice is remembered between launches.
    public static class GraphicsQuality
    {
        public static readonly string[] Names = { "Fast", "Balanced", "High", "Ultra" };
        public static int Level { get; private set; } = 1;
        // Mobile always runs on the Fast level (everything that asks for Level == 0 gets it).
        static bool mobile;
        public static bool Mobile { get { LoadChoice(); return mobile; } }
        public static string Label => Mobile ? "ГРАФИКА  /  МОБИЛЬНАЯ" : Level == 0 ? "ГРАФИКА  /  БЫСТРАЯ" : "ГРАФИКА  /  ВЫСОКАЯ";
        // Anti-aliasing, chosen in the settings: off, or the strongest there is (high-quality SMAA
        // on top of 4× multisampling).
        public static readonly string[] AntiAliasingNames = { "ВЫКЛ", "МАКСИМУМ" };
        public static int AntiAliasing { get; private set; } = 1;
        public static string AntiAliasingLabel => "СГЛАЖИВАНИЕ  /  " + AntiAliasingNames[AntiAliasing];

        public static void CycleAntiAliasing(int direction = 1)
        {
            AntiAliasing = (AntiAliasing + direction + AntiAliasingNames.Length) % AntiAliasingNames.Length;
            if (!ProfileStore.Testing)
                try { PlayerPrefs.SetInt("autobahn.aa2", AntiAliasing); } catch { }
            Apply(Level);
        }
        // How far the world is drawn; fog closes in before it, so nothing pops.
        public static float ViewDistance => mobile ? 300f : Level switch { 0 => 420f, 1 => 650f, 2 => 950f, _ => 1300f };
        // Buildings, ground and roads are drawn much further than the detail (trees, props,
        // traffic), fading into the haze: before, half the city ended in a white void.
        // (It is also how far the island is built round the player: WorldStreamer.)
        public static float FarReach => mobile ? 650f : Level switch { 0 => 1000f, 1 => 1400f, 2 => 1900f, _ => 2500f };
        // Pixels actually rendered are capped per preset (Retina screens have four times the
        // pixels of a 1080p one); FSR scales the image up to the window.
        // (Sharpness pass 2026-09-28: the old caps, 720/900/1200 px and 60-90 % scale, rendered a
        // Retina screen at about 60 % and made the picture soft. Now full resolution up to 1440 p
        // on High, and FSR 1 with strong sharpening only above that.)
        static float MaxHeight => mobile ? 720f : Level switch { 0 => 1080f, 1 => 1260f, 2 => 1440f, _ => 2160f };
        static int lastHeight;
        // Mobile goes down to 720p even on a big Retina screen.
        static float MinScale => mobile ? .25f : .45f;
        static Bloom bloom;
        static MotionBlur blur;
        static Vignette vignette;
        static ChromaticAberration fringe;
        static Camera lens;
        static bool loaded;

        public static void Bind(VolumeProfile profile, Camera camera)
        {
            lens = camera;
            if (profile != null)
            {
                profile.TryGet(out bloom);
                profile.TryGet(out blur);
                profile.TryGet(out vignette);
                profile.TryGet(out fringe);
            }
            LoadChoice();
            Apply(Level);
        }

        // The player's choice from the last launch (phones start on Mobile).
        static void LoadChoice()
        {
            if (loaded) return;
            loaded = true;
            try { Level = ProfileStore.Testing ? 1 : PlayerPrefs.GetInt("autobahn.gfx2", 2) == 0 ? 0 : 2; } catch { }
            try { AntiAliasing = ProfileStore.Testing ? 1 : PlayerPrefs.GetInt("autobahn.aa2", 1) == 0 ? 0 : 1; } catch { }
            try { mobile = !ProfileStore.Testing && PlayerPrefs.GetInt("autobahn.mobile", Application.isMobilePlatform ? 1 : 0) == 1; } catch { }
            if (mobile) Level = 0;
        }

        public static void BindCamera(Camera camera)
        {
            lens = camera;
            Apply(Level);
        }

        // High → Fast → Mobile → High.
        public static void Cycle()
        {
            if (Mobile) SetMobile(false, 2);
            else if (Level == 0) SetMobile(true, 0);
            else Apply(0);
        }

        public static void SetMobile(bool on, int level = 0)
        {
            LoadChoice();
            mobile = on;
            if (!ProfileStore.Testing)
                try { PlayerPrefs.SetInt("autobahn.mobile", on ? 1 : 0); } catch { }
            Apply(on ? 0 : level);
            // Fewer traffic cars on Mobile (TrafficFlow.PoolSize).
            var run = RunSession.Active;
            if (run && run.Traffic) run.Traffic.SetDensity(run.Traffic.Density);
        }

        // Mobile holds the frame rate at 30 (even pacing, a cool phone); the other presets use
        // what Boot set up (the display's rate, or free-running for the automated checks).
        static bool capped;
        static int savedFrameRate, savedVSync;

        static void FrameRate()
        {
            if (mobile && !capped)
            {
                capped = true;
                savedFrameRate = Application.targetFrameRate;
                savedVSync = QualitySettings.vSyncCount;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 30;
            }
            else if (!mobile && capped)
            {
                capped = false;
                QualitySettings.vSyncCount = savedVSync;
                Application.targetFrameRate = savedFrameRate;
            }
        }

        // The window was resized or went full screen: the pixel cap depends on its height.
        public static void Watch()
        {
            if (Screen.height != lastHeight && lastHeight != 0)
                Apply(Level);
            Adapt();
        }

        // Adaptive resolution: when frames run long for a couple of seconds (a busy crossroads,
        // a big explosion, the Mac doing something else) the picture is rendered at fewer pixels
        // and FSR brings it back to full size; when there is room again the pixels come back.
        // Steps of 10 %, never below 70 % of the preset. Off during automated checks, which
        // measure the presets themselves.
        public static float Adaptive { get; private set; } = 1;
        public static bool AdaptiveEnabled = true;
        static float baseScale = 1, smoothDt = 1 / 60f, slowFor, fastFor;

        static void Adapt()
        {
            if (!AdaptiveEnabled || ProfileStore.Testing || ScreenResolution.ChosenHeight > 0) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
            smoothDt = Mathf.Lerp(smoothDt, dt, .05f);
            // Only when it is really struggling (under ~38 fps), and never below 85 %: a soft
            // picture is worse than a few frames less.
            slowFor = smoothDt > 1 / 38f ? slowFor + dt : 0;
            fastFor = smoothDt < 1 / 55f ? fastFor + dt : 0;
            float was = Adaptive;
            if (slowFor > 3 && Adaptive > .86f) { Adaptive -= .05f; slowFor = 0; }
            else if (fastFor > 4 && Adaptive < .99f) { Adaptive = Mathf.Min(1, Adaptive + .05f); fastFor = 0; }
            if (!Mathf.Approximately(was, Adaptive)) ApplyScale();
        }

        static void ApplyScale()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null) return;
            pipeline.renderScale = Mathf.Clamp(baseScale * Adaptive, MinScale, 1f);
            pipeline.upscalingFilter = pipeline.renderScale < .99f ? UpscalingFilterSelection.FSR : UpscalingFilterSelection.Auto;
        }

        // Distance culling per kind of object, and the GPU-driven renderer with occlusion culling
        // (buildings hide what is behind them without any baking). Switchable for measurements.
        public static bool LayerCulling = true, GpuDriven = false;
        public static float TreeReach => mobile ? 220 : Level switch { 0 => 300, 1 => 420, 2 => 520, _ => 760 };
        public static float PropReach => mobile ? 120 : Level switch { 0 => 170, 1 => 230, 2 => 280, _ => 420 };
        static readonly float[] cull = new float[32];

        public static void Apply(int level)
        {
            LoadChoice();
            Level = Mathf.Clamp(level, 0, Names.Length - 1);
            // Any other level leaves Mobile (it is built on Fast).
            if (Level != 0) mobile = false;
            if (!ProfileStore.Testing)
                try { PlayerPrefs.SetInt("autobahn.gfx2", Level); } catch { }
            FrameRate();
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline != null)
            {
                // Lower presets render fewer pixels and let FSR sharpen them back up (Mobile: 720p,
                // through MaxHeight).
                float preset = mobile ? 1f : Level switch { 0 => .85f, 1 => .92f, 2 => 1f, _ => 1f };
                // A resolution picked in the settings is drawn exactly: below the window it is
                // scaled up (FSR), above it rendered bigger and scaled down (4K on a smaller screen).
                int chosen = ScreenResolution.ChosenHeight;
                baseScale = chosen > 0
                    ? ScreenResolution.Scale
                    : Mathf.Clamp(Mathf.Min(preset, MaxHeight / Mathf.Max(1, Screen.height)), mobile ? MinScale : .5f, 1f);
                pipeline.renderScale = chosen > 0 ? Mathf.Clamp(baseScale, .05f, 2f) : Mathf.Clamp(baseScale * Adaptive, MinScale, 1f);
                lastHeight = Screen.height;
                pipeline.upscalingFilter = chosen > 0 && chosen <= 144 ? UpscalingFilterSelection.Point
                    : pipeline.renderScale < .99f ? UpscalingFilterSelection.FSR : UpscalingFilterSelection.Auto;
                pipeline.fsrOverrideSharpness = true;
                pipeline.fsrSharpness = .92f;
                // Anti-aliasing is done in the image, so multisampling only costs memory bandwidth.
                // (Unless the player asked for the strongest anti-aliasing in the settings.)
                pipeline.msaaSampleCount = AntiAliasing == 1 && !mobile ? 4 : 1;
                pipeline.shadowDistance = mobile ? 40 : Level switch { 0 => 45, 1 => 80, 2 => 150, _ => 260 };
                pipeline.shadowCascadeCount = Level switch { 0 => 1, 1 => 2, 2 => 3, _ => 4 };
                pipeline.cascade4Split = new Vector3(.05f, .15f, .38f);
                pipeline.mainLightShadowmapResolution = Level switch { 0 => 1024, 1 => 1536, 2 => 2048, _ => 4096 };
                bool gpu = GpuDriven && SystemInfo.supportsComputeShaders;
                var mode = gpu ? GPUResidentDrawerMode.InstancedDrawing : GPUResidentDrawerMode.Disabled;
                if (pipeline.gpuResidentDrawerMode != mode) pipeline.gpuResidentDrawerMode = mode;
                pipeline.gpuResidentDrawerEnableOcclusionCullingInCameras = gpu;
                // Ambient occlusion is the most expensive effect (3-4 ms measured on High): Ultra only.
                try
                {
                    var field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field?.GetValue(pipeline) is ScriptableRendererData[] list)
                        foreach (var data in list)
                            if (data != null)
                                foreach (var feature in data.rendererFeatures)
                                    if (feature is ScreenSpaceAmbientOcclusion)
                                        feature.SetActive(Level >= 3);
                }
                catch { }
            }

            if (lens)
            {
                var data = lens.GetUniversalAdditionalCameraData();
                // SMAA on every preset: FXAA (Fast) smudged every edge and texture. No TAA: it
                // softens the whole image and smears at speed.
                data.antialiasing = AntiAliasing == 0 ? AntialiasingMode.None : AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.antialiasingQuality = mobile ? AntialiasingQuality.Low : AntialiasingQuality.High;
                lens.farClipPlane = FarReach + 60;
                System.Array.Clear(cull, 0, cull.Length);
                if (LayerCulling)
                {
                    cull[Art.TreeLayer] = TreeReach;
                    cull[Art.PropLayer] = PropReach;
                    cull[9] = Mathf.Min(FarReach, 800);        // traffic: seen from far ahead at speed
                }
                lens.layerCullDistances = cull;
            }

            QualitySettings.lodBias = mobile ? .5f : Level switch { 0 => .7f, 1 => 1f, 2 => 1.25f, _ => 2.2f };
            // Full-size textures and anisotropic filtering everywhere: half-size textures and plain
            // trilinear filtering blurred roads and walls at an angle (cheap on any GPU today).
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            Texture.SetGlobalAnisotropicFilteringLimits(8, 16);
            QualitySettings.globalTextureMipmapLimit = 0;

            // Motion blur smeared the whole screen whenever the car moved: off.
            if (blur != null)
                blur.active = false;
            if (bloom != null)
            {
                bloom.active = Level > 0;
                // High-quality filtering stops small bright lamps from sparkling as the camera moves.
                bloom.highQualityFiltering.value = true;
                bloom.clamp.value = 20;
                bloom.downscale.value = Level >= 2 ? BloomDownscaleMode.Half : BloomDownscaleMode.Quarter;
                bloom.maxIterations.value = Level >= 2 ? 6 : 4;
            }
            if (vignette != null)
                vignette.active = Level > 0;
            // Chromatic aberration blurred edges towards the corners: off.
            if (fringe != null)
                fringe.active = false;
        }
    }
}

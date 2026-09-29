using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Autobahn
{
    // A picture of the whole island from straight above, rendered once from the real world
    // (terrain, forests, city, roads, airport, harbour): the minimap and the big map use it, the
    // way a satellite photo is used. Rendered again when asked (after the world changes a lot).
    public static class IslandSnapshot
    {
        // The area the picture covers (metres, world x/z).
        public const float X0 = -1100, X1 = 2500, Z0 = -1000, Z1 = 6100;
        public static Texture2D Image { get; private set; }
        public static bool Ready => Image != null;
        public static int Renders { get; private set; }

        public static Vector2 ToUV(Vector3 world) => new((world.x - X0) / (X1 - X0), (world.z - Z0) / (Z1 - Z0));

        static bool rendering;
        static float savedBias;
        static int savedMaxLod;
        static bool savedFog;
        static Camera cam;
        static Light sun;

        public static Texture2D Render(int width = 1024)
        {
            if (Route.Endless) return null;
            int height = Mathf.RoundToInt(width * (Z1 - Z0) / (X1 - X0));
            var go = new GameObject("Island snapshot camera");
            cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = (Z1 - Z0) / 2;
            cam.aspect = (X1 - X0) / (Z1 - Z0);
            cam.nearClipPlane = 10;
            cam.farClipPlane = 3000;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.03f, .16f, .30f);
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.transform.SetPositionAndRotation(new Vector3((X0 + X1) / 2, 1500, (Z0 + Z1) / 2), Quaternion.Euler(90, 0, 0));
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.renderShadows = false;
            data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            data.requiresDepthOption = CameraOverrideOption.Off;
            var lightGo = new GameObject("Island snapshot light");
            lightGo.transform.rotation = Quaternion.Euler(62, -35, 0);
            sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = Atmosphere.Night ? 1.3f : .35f;
            sun.shadows = LightShadows.None;

            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            // The water shader measures depth for a perspective camera; from straight above in an
            // orthographic view it shows blocks. A plain see-through sea over the darkening sea
            // bed gives turquoise shallows instead.
            var seaRenderer = IslandWorld.Active ? IslandWorld.Active.SeaRenderer : null;
            Material seaBefore = seaRenderer ? seaRenderer.sharedMaterial : null;
            if (seaRenderer)
            {
                mapSea ??= MapSea();
                seaRenderer.sharedMaterial = mapSea;
            }
            bool showAll = RoadWorld.ShowAll;
            RoadWorld.ShowAll = true;
            IslandWorld.Active?.ShowEverything(true);
            RenderPipelineManager.beginCameraRendering += Begin;
            RenderPipelineManager.endCameraRendering += End;
            rendering = true;
            try { ForestRenderer.Active?.DrawFor(cam); cam.Render(); }
            finally
            {
                rendering = false;
                RenderPipelineManager.beginCameraRendering -= Begin;
                RenderPipelineManager.endCameraRendering -= End;
                RoadWorld.ShowAll = showAll;
                if (seaRenderer) seaRenderer.sharedMaterial = seaBefore;
                IslandWorld.Active?.ShowEverything(false);
            }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, true) { name = "Island map", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply(true, false);
            RenderTexture.active = prev;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            Object.Destroy(go);
            Object.Destroy(lightGo);
            if (Image) Object.Destroy(Image);
            Image = tex;
            Renders++;
            return tex;
        }

        // The world is streamed in round the player, so this picture means building the whole
        // island once. It is generated the same way every time: the picture is kept on disk for
        // this build of the game (the editor, which has no build id, renders it every time).
        static string CachePath(int width)
        {
            string build = Application.buildGUID;
            if (string.IsNullOrEmpty(build)) return null;
            return System.IO.Path.Combine(Application.persistentDataPath, $"island-map-{build}-{width}-{(GraphicsQuality.Level == 0 ? "low" : "full")}.png");
        }

        public static bool LoadCached(int width)
        {
            if (Route.Endless) return false;
            try
            {
                string path = CachePath(width);
                if (path == null || !System.IO.File.Exists(path)) return false;
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = "Island map", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
                if (!tex.LoadImage(System.IO.File.ReadAllBytes(path)))
                {
                    Object.Destroy(tex);
                    return false;
                }
                tex.wrapMode = TextureWrapMode.Clamp;
                if (Image) Object.Destroy(Image);
                Image = tex;
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("AUTOBAHN map picture cache: " + e.Message);
                return false;
            }
        }

        public static void SaveCached(int width)
        {
            if (!Image) return;
            try
            {
                string path = CachePath(width);
                if (path == null) return;
                // Pictures of earlier builds are no use any more.
                foreach (var old in System.IO.Directory.GetFiles(Application.persistentDataPath, "island-map-*.png"))
                    if (old != path) System.IO.File.Delete(old);
                System.IO.File.WriteAllBytes(path, Image.EncodeToPNG());
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("AUTOBAHN map picture cache: " + e.Message);
            }
        }

        static Material mapSea;
        static Material MapSea()
        {
            var m = new Material(Resources.Load<Material>("Materials/_Lit") ?? Art.Glass) { name = "Map sea" };
            m.SetFloat("_Surface", 1);
            m.SetFloat("_Blend", 0);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetColor("_BaseColor", new Color(.10f, .42f, .55f, .55f));
            m.SetFloat("_Smoothness", .2f);
            return m;
        }

        static void Begin(ScriptableRenderContext context, Camera c)
        {
            if (!rendering || c != cam) return;
            savedFog = RenderSettings.fog;
            RenderSettings.fog = false;
            // Trees and houses keep their detail from 1.5 km up.
            savedBias = QualitySettings.lodBias;
            savedMaxLod = QualitySettings.maximumLODLevel;
            QualitySettings.lodBias = 60;
            QualitySettings.maximumLODLevel = 0;
        }

        static void End(ScriptableRenderContext context, Camera c)
        {
            if (!rendering || c != cam) return;
            RenderSettings.fog = savedFog;
            QualitySettings.lodBias = savedBias;
            QualitySettings.maximumLODLevel = savedMaxLod;
        }
    }
}

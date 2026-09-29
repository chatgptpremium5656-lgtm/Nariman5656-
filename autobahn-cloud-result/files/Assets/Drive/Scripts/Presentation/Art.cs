using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    public static class Art
    {
        public static Material Asphalt, Line, Steel, Grass, Concrete, Trunk, Leaf, Glass, Rubber, Chrome, Red, White, Amber, Blue, Dark, LampGlow;
        public static Material Shoulder, LeavesLight, Green, Kerb;
        public static Material LeafOak, LeafBirch, LeafMaple, LeafBush, Needle, BarkPale;
        public static Material[] CarPaints;
        public static Material[] SignalA, SignalB;   // 0 red, 1 amber, 2 green
        public static Material[] Facades;
        public static Material RoofGravel, RoofTile, Plinth, ShopLit, Frame, Socle;
        public static Material SignRed, SignWhite, SignYellow, SignGreen, SignBlack, BinGreen;
        public static Material Wood, PaintDark, PaintGrey, Plastic, Plaster;
        public static Material[] FacadeVariants;
        public static Material[] Awnings;
        static Mesh pine;
        public static Material Material(string name, Color color, float metallic = 0, float smooth = .3f, bool glow = false)
        {
            // URP Lit templates from Resources keep the right shader variants in the build.
            var template = Resources.Load<Material>(glow ? "Materials/_LitEmissive" : "Materials/_Lit");
            var m = template ? new Material(template) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.name = name;
            m.color = color;
            m.enableInstancing = true;
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smooth);
            if (glow)
                m.SetColor("_EmissionColor", color * 2.5f);
            return m;
        }

        // Instance of a textured material asset (falls back to a flat colour).
        public static Material Textured(string asset, string name, Color fallback, float smooth = .3f)
        {
            var src = Resources.Load<Material>("Materials/" + asset);
            if (!src)
                return Material(name, fallback, 0, smooth);
            var m = new Material(src) { name = name };
            return m;
        }

        public static void Init()
        {
            Asphalt = Textured("Asphalt", "Fine asphalt", new Color(.19f, .20f, .21f));
            Line = Material("Ivory markings", new Color(.91f, .89f, .79f), 0, .4f);
            Steel = Textured("PropSteel", "Zinc", new Color(.50f, .53f, .54f), .32f);
            Steel.color = new Color(.95f, 1f, 1.02f);
            Grass = Textured("Grass", "Meadow", new Color(.23f, .30f, .13f));
            Shoulder = Textured("Shoulder", "Weathered shoulder", new Color(.32f, .33f, .31f));
            LeavesLight = Textured("Pine", "Pine tips", new Color(.24f, .33f, .16f));
            LeavesLight.color = new Color(1.25f, 1.3f, 1.05f);
            Facades = new[] { Textured("FacadeLimestone", "Limestone", new Color(.55f, .55f, .48f)), Textured("FacadeBrick", "Brick", new Color(.33f, .20f, .15f)), Textured("FacadeRender", "Pale render", new Color(.69f, .68f, .60f)) };
            Concrete = Textured("Concrete", "Concrete", new Color(.38f, .40f, .37f));
            Trunk = Textured("Bark", "Bark", new Color(.19f, .14f, .10f));
            Leaf = Textured("Pine", "Pine", new Color(.12f, .23f, .14f));
            Glass = Material("Smoked glass", new Color(.045f, .09f, .115f), .65f, .92f);
            Rubber = Material("Rubber", new Color(.022f, .026f, .028f), 0, .2f);
            Chrome = Material("Brushed alloy", new Color(.60f, .62f, .61f), .85f, .8f);
            Red = Material("LED red", new Color(.8f, .02f, .01f), 0, .4f, true);
            White = Material("LED white", new Color(.85f, .91f, 1), 0, .4f, true);
            White.SetColor("_EmissionColor", new Color(.85f, .91f, 1) * 1.2f);   // headlamp lenses: bright, not dazzling
            // Street lamp heads: warm, only really glowing after dark (Atmosphere drives it).
            LampGlow = Material("Lamp glow", new Color(1f, .9f, .74f), 0, .4f, true);
            Amber = Material("Indicator amber", new Color(1, .32f, .025f), 0, .4f, true);
            Green = Material("LED green", new Color(.06f, .8f, .22f), 0, .4f, true);
            LeafOak = Textured("LeafBroadleaf", "Oak leaves", new Color(.17f, .30f, .12f));
            LeafBirch = Textured("LeafBirch", "Birch leaves", new Color(.33f, .44f, .18f));
            LeafMaple = Textured("LeafMaple", "Autumn leaves", new Color(.45f, .28f, .09f));
            LeafBush = Textured("LeafBush", "Shrub leaves", new Color(.16f, .28f, .11f));
            Needle = Textured("NeedlePine", "Pine needles", new Color(.11f, .22f, .14f));
            LeafOak.color = new Color(.80f, .90f, .74f);
            LeafBirch.color = new Color(.95f, 1f, .9f);
            LeafMaple.color = new Color(.95f, .92f, .85f);
            LeafBush.color = new Color(.9f, .96f, .86f);
            Needle.color = new Color(.50f, .72f, .46f);
            BarkPale = Textured("Bark", "Birch bark", new Color(.62f, .60f, .56f));
            BarkPale.color = new Color(2.1f, 2.05f, 1.95f);
            Kerb = Textured("Concrete", "Kerb", new Color(.55f, .55f, .53f));
            Kerb.color = new Color(1.25f, 1.25f, 1.2f);
            CarPaints = new[]
            {
                Material("Parked white", new Color(.72f, .73f, .70f), .5f, .82f),
                Material("Parked graphite", new Color(.12f, .13f, .14f), .5f, .82f),
                Material("Parked red", new Color(.38f, .06f, .05f), .5f, .82f),
                Material("Parked blue", new Color(.07f, .13f, .28f), .5f, .82f),
            };
            SignalA = new[]
            {
                Material("Signal A red", new Color(.85f, .05f, .03f), 0, .4f, true),
                Material("Signal A amber", new Color(1f, .42f, .03f), 0, .4f, true),
                Material("Signal A green", new Color(.07f, .85f, .24f), 0, .4f, true),
            };
            SignalB = new[]
            {
                Material("Signal B red", new Color(.85f, .05f, .03f), 0, .4f, true),
                Material("Signal B amber", new Color(1f, .42f, .03f), 0, .4f, true),
                Material("Signal B green", new Color(.07f, .85f, .24f), 0, .4f, true),
            };
            Blue = Material("Autobahn blue", new Color(.025f, .15f, .36f));
            Dark = Material("Charcoal", new Color(.055f, .065f, .063f));
            RoofGravel = Textured("Concrete", "Roof gravel", new Color(.33f, .32f, .30f));
            RoofGravel.color = new Color(.62f, .6f, .56f);
            RoofTile = Material("Clay roof tiles", new Color(.36f, .13f, .08f), 0, .35f);
            Plinth = Textured("Concrete", "Stone plinth", new Color(.45f, .43f, .40f));
            Plinth.color = new Color(.78f, .74f, .68f);
            Socle = Textured("Concrete", "Stone socle", new Color(.30f, .29f, .27f));
            Socle.color = new Color(.52f, .50f, .47f);
            ShopLit = Material("Shop interior", new Color(.10f, .09f, .075f), .3f, .9f, true);
            ShopLit.SetColor("_EmissionColor", new Color(.42f, .30f, .17f) * .55f);
            Frame = Material("Window frame", new Color(.16f, .17f, .17f), .6f, .5f);
            SignRed = Material("Sign red", new Color(.62f, .04f, .03f), 0, .45f);
            SignWhite = Material("Sign white", new Color(.86f, .86f, .83f), 0, .45f);
            SignYellow = Material("Sign yellow", new Color(.92f, .72f, .06f), 0, .45f);
            SignGreen = Material("Sign green", new Color(.04f, .36f, .18f), 0, .45f);
            SignBlack = Material("Sign black", new Color(.03f, .03f, .03f), 0, .3f);
            BinGreen = Textured("PropPaint", "Bin green", new Color(.10f, .22f, .15f), .35f);
            BinGreen.color = new Color(.14f, .30f, .21f);
            // Photo-textured street furniture (from the purchased NYC buildings set).
            Wood = Textured("PropWood", "Bench wood", new Color(.36f, .25f, .16f));
            Wood.color = new Color(1.35f, 1.2f, 1.05f);
            PaintDark = Textured("PropPaint", "Painted iron", new Color(.08f, .09f, .09f), .4f);
            PaintDark.color = new Color(.16f, .17f, .18f);
            PaintGrey = Textured("PropPaint", "Painted steel", new Color(.45f, .47f, .48f), .45f);
            PaintGrey.color = new Color(.78f, .80f, .82f);
            Plastic = Textured("PropPlastic", "Plastic", new Color(.1f, .1f, .1f), .6f);
            Plaster = Textured("PropPlaster", "Plaster", new Color(.66f, .64f, .6f), .15f);
            // Pastel renders and weathered stone, like German town streets.
            Material Tint(Material src, string name, Color c)
            {
                var m = new Material(src) { name = name };
                m.color = c;
                return m;
            }
            FacadeVariants = new[]
            {
                Facades[0], Facades[1], Facades[2],
                Tint(Facades[2], "Render yellow", new Color(1f, .90f, .66f)),
                Tint(Facades[2], "Render peach", new Color(1f, .80f, .68f)),
                Tint(Facades[2], "Render mint", new Color(.84f, .95f, .88f)),
                Tint(Facades[2], "Render white", new Color(1.1f, 1.1f, 1.08f)),
                Tint(Facades[0], "Stone grey", new Color(.80f, .82f, .84f)),
                Tint(Facades[1], "Brick dark", new Color(.72f, .62f, .60f)),
                Tint(Facades[2], "Render blue", new Color(.80f, .88f, 1f)),
            };
            Awnings = new[]
            {
                Material("Awning green", new Color(.08f, .26f, .16f), 0, .25f),
                Material("Awning red", new Color(.42f, .07f, .06f), 0, .25f),
                Material("Awning blue", new Color(.06f, .14f, .32f), 0, .25f),
                Material("Awning sand", new Color(.62f, .52f, .34f), 0, .25f),
            };
            InitHouseDetail();
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI / 6, b = (i + 1) * Mathf.PI / 6;
                int k = vertices.Count;
                // Slightly drooping, jagged skirt reads more like a real conifer tier.
                float ra = i % 2 == 0 ? 1 : .86f, rb = (i + 1) % 2 == 0 ? 1 : .86f;
                vertices.AddRange(new[] { new Vector3(Mathf.Cos(a) * ra, -.06f * ra, Mathf.Sin(a) * ra), Vector3.up, new Vector3(Mathf.Cos(b) * rb, -.06f * rb, Mathf.Sin(b) * rb) });
                uvs.AddRange(new[] { new Vector2(i / 3f, 0), new Vector2((i + .5f) / 3f, 1.6f), new Vector2((i + 1) / 3f, 0) });
                triangles.AddRange(new[] { k, k + 1, k + 2 });
            }
            pine = Mesh(vertices.ToArray(), triangles.ToArray(), uvs.ToArray());
            Foliage.Build();
        }

        // ---- House detail: window frames, shutters, shop interiors, roof tiles -------------
        public static Material Paving, TrimStone, FrameWhite, FrameDark, WinGlass, WinLit, Blind, Iron, Planter, Flowers, ChimneyBrick;
        public static Material[] Doors, ShopInteriors, SignBoards, WinGlasses;
        // Materials only worth drawing close up; Combine puts them in a distance LOD group.
        public static readonly HashSet<Material> Detail = new();
        public const float DetailReach = 230;

        static Texture2D Paint(int w, int h, Color[] px, bool repeat = true)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true)
            {
                wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }

        static void Rect(Color[] px, int w, int h, int x0, int y0, int x1, int y1, Color c, float mix = 1)
        {
            for (int y = Mathf.Max(0, y0); y < Mathf.Min(h, y1); y++)
                for (int x = Mathf.Max(0, x0); x < Mathf.Min(w, x1); x++)
                    px[y * w + x] = Color.Lerp(px[y * w + x], c, mix);
        }

        // Clay tiles in staggered rows: 12 rows per 4 m (the roof UVs are metres / 4).
        static Texture2D RoofTexture()
        {
            const int W = 256, H = 256, rows = 12, cols = 14;
            var r = new System.Random(11);
            var px = new Color[W * H];
            float rh = H / (float)rows, cw = W / (float)cols;
            var shade = new float[rows, cols + 1];
            for (int a = 0; a < rows; a++)
                for (int b = 0; b <= cols; b++)
                    shade[a, b] = r.NextDouble() < .08 ? .62f : (float)(.84 + r.NextDouble() * .26);
            for (int y = 0; y < H; y++)
            {
                int row = Mathf.Min(rows - 1, (int)(y / rh));
                float fy = y / rh - row;               // 0 at the lower edge of the tile
                float off = row % 2 == 0 ? 0 : cw / 2;
                for (int x = 0; x < W; x++)
                {
                    float u = Mathf.Repeat(x + off, W) / cw;
                    int col = Mathf.Min(cols, (int)u);
                    float fx = u - col;
                    var c = new Color(.56f, .22f, .13f) * shade[row, col];
                    c *= Mathf.Lerp(.72f, 1.04f, Mathf.SmoothStep(0, 1, fy));   // the tile above casts a shadow
                    c *= .9f + .1f * Mathf.Sin(fx * Mathf.PI);                    // rounded pan
                    if (fy < .07f) c *= .45f;
                    if (fx < .035f) c *= .7f;
                    c.a = 1;
                    px[y * W + x] = c;
                }
            }
            return Paint(W, H, px);
        }

        static Texture2D Stripes(int count, Color a, Color b, bool vertical)
        {
            const int N = 64;
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int k = (vertical ? x : y) * count / N;
                    float edge = Mathf.Repeat((vertical ? x : y) * count / (float)N, 1);
                    var c = k % 2 == 0 ? a : b;
                    if (!vertical && edge < .18f) c *= .7f;     // slat joint
                    c.a = 1;
                    px[y * N + x] = c;
                }
            return Paint(N, N, px);
        }

        static Texture2D Speckle(Color leaf, int seed)
        {
            const int N = 64;
            var r = new System.Random(seed);
            var px = new Color[N * N];
            for (int i = 0; i < px.Length; i++)
            {
                float v = (float)(.7 + r.NextDouble() * .5);
                px[i] = new Color(leaf.r * v, leaf.g * v, leaf.b * v, 1);
            }
            Color[] blooms = { new(.85f, .08f, .07f), new(.95f, .35f, .55f), new(.95f, .92f, .88f), new(.85f, .08f, .07f) };
            for (int n = 0; n < 70; n++)
            {
                int x = r.Next(N), y = r.Next(N);
                Rect(px, N, N, x, y, x + 3, y + 3, blooms[r.Next(blooms.Length)]);
            }
            return Paint(N, N, px);
        }

        // What you see through a shop window: 0 shelves, 1 clothes rails, 2 cafe, 3 salon/office.
        static Texture2D Interior(int kind, int seed)
        {
            const int W = 256, H = 128;
            var r = new System.Random(seed);
            var px = new Color[W * H];
            Color wall = kind switch { 0 => new(.62f, .58f, .50f), 1 => new(.70f, .67f, .62f), 2 => new(.50f, .36f, .24f), _ => new(.42f, .44f, .46f) };
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float k = .45f + .55f * y / H;
                    px[y * W + x] = new Color(wall.r * k, wall.g * k, wall.b * k, 1);
                }
            Rect(px, W, H, 0, 0, W, 10, wall * .45f);                                   // floor
            for (int x = 14; x < W; x += 46)
                Rect(px, W, H, x, H - 8, x + 26, H - 4, new Color(1f, .96f, .86f));    // ceiling lights
            Color Rnd() => Color.HSVToRGB((float)r.NextDouble(), (float)(.2 + r.NextDouble() * .4), (float)(.3 + r.NextDouble() * .45));
            switch (kind)
            {
                case 0:
                    for (int shelf = 0; shelf < 4; shelf++)
                    {
                        int y = 14 + shelf * 25;
                        Rect(px, W, H, 6, y, W - 6, y + 3, new Color(.30f, .22f, .15f));
                        for (int x = 8; x < W - 10;)
                        {
                            int w = 3 + r.Next(6), h = 8 + r.Next(12);
                            Rect(px, W, H, x, y + 3, x + w, y + 3 + h, Rnd());
                            x += w + r.Next(3);
                        }
                    }
                    break;
                case 1:
                    Rect(px, W, H, 10, 92, W - 10, 94, new Color(.25f, .25f, .25f));
                    for (int x = 12; x < W - 16; x += 5 + r.Next(5))
                        Rect(px, W, H, x, 38 + r.Next(14), x + 5 + r.Next(5), 91, Rnd() * .8f);
                    foreach (int mx in new[] { 60, 190 })
                    {
                        Rect(px, W, H, mx - 9, 12, mx + 9, 80, new Color(.85f, .84f, .80f));
                        Rect(px, W, H, mx - 5, 80, mx + 5, 96, new Color(.85f, .84f, .80f));
                        Rect(px, W, H, mx - 9, 40, mx + 9, 80, Rnd());
                    }
                    break;
                case 2:
                    Rect(px, W, H, 150, 10, W - 4, 42, new Color(.22f, .13f, .08f));      // counter
                    Rect(px, W, H, 150, 42, W - 4, 45, new Color(.75f, .72f, .66f));
                    for (int x = 20; x < 140; x += 42)
                    {
                        Rect(px, W, H, x, 30, x + 28, 33, new Color(.18f, .12f, .08f));      // table
                        Rect(px, W, H, x + 12, 10, x + 16, 30, new Color(.12f, .09f, .06f));
                        Rect(px, W, H, x - 6, 10, x - 2, 38, new Color(.15f, .1f, .07f));    // chair
                    }
                    for (int x = 30; x < W; x += 50)
                    {
                        Rect(px, W, H, x + 4, 100, x + 5, H - 10, new Color(.1f, .1f, .1f));
                        Rect(px, W, H, x, 92, x + 10, 100, new Color(1f, .82f, .52f));     // pendant lamp
                    }
                    break;
                default:
                    for (int y = 14; y < H - 12; y += 6)
                        Rect(px, W, H, 6, y, W - 6, y + 3, new Color(.72f, .72f, .70f), .55f);
                    Rect(px, W, H, 90, 10, 170, 36, new Color(.25f, .25f, .27f));          // desk
                    break;
            }
            // A soft diagonal reflection across the glass.
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float d = Mathf.Repeat(x + y * 1.4f, W * 1.2f);
                    if (d > 40 && d < 95)
                        px[y * W + x] = Color.Lerp(px[y * W + x], new Color(.82f, .88f, .92f), .16f);
                }
            // Cube faces map the texture upside down: flip it.
            for (int y = 0; y < H / 2; y++)
                for (int x = 0; x < W; x++)
                    (px[y * W + x], px[(H - 1 - y) * W + x]) = (px[(H - 1 - y) * W + x], px[y * W + x]);
            return Paint(W, H, px, false);
        }

        // Window panes seen from the street: dark room, a sky reflection towards the top and
        // per variant curtains at the sides or a German half-height net curtain.
        static Texture2D Pane(int variant)
        {
            const int N = 64;
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float up = 1 - y / (float)N;             // the box face maps v downwards
                    var c = Color.Lerp(new Color(.07f, .08f, .09f), new Color(.30f, .38f, .46f), up * up * .8f);
                    float d = Mathf.Repeat(x * 1.1f + y * .9f, N * 1.4f);
                    if (d > 12 && d < 22) c = Color.Lerp(c, new Color(.6f, .66f, .72f), .18f);
                    float side = Mathf.Min(x, N - 1 - x) / (float)N;
                    if (variant == 0 && side < .2f)
                        c = Color.Lerp(new Color(.55f, .50f, .44f), new Color(.45f, .41f, .36f), Mathf.Repeat(x * .5f, 1)) * (.8f + .2f * up);
                    if (variant == 1 && up < .5f)
                        c = Color.Lerp(c, new Color(.78f, .78f, .75f), .78f) * (Mathf.Repeat(x * .25f, 1) < .5f ? 1 : .93f);
                    c.a = 1;
                    px[y * N + x] = c;
                }
            return Paint(N, N, px, false);
        }

        // Grey concrete pavers for parking bays (MeshKit UVs: one repeat per 2 m).
        static Texture2D PavingTexture()
        {
            const int N = 256, rows = 10, cols = 7;
            var r = new System.Random(3);
            var px = new Color[N * N];
            float rh = N / (float)rows, cw = N / (float)cols;
            var tone = new float[rows, cols + 1];
            for (int a = 0; a < rows; a++)
                for (int b = 0; b <= cols; b++)
                    tone[a, b] = (float)(.82 + r.NextDouble() * .3);
            for (int y = 0; y < N; y++)
            {
                int row = Mathf.Min(rows - 1, (int)(y / rh));
                float fy = y / rh - row, off = row % 2 == 0 ? 0 : cw / 2;
                for (int x = 0; x < N; x++)
                {
                    float u = Mathf.Repeat(x + off, N) / cw;
                    int col = Mathf.Min(cols, (int)u);
                    float fx = u - col;
                    float g = .40f * tone[row, col] * (.94f + .06f * (float)r.NextDouble());
                    if (fy < .06f || fy > .94f || fx < .04f || fx > .96f) g *= .55f;
                    px[y * N + x] = new Color(g, g * .99f, g * .96f, 1);
                }
            }
            return Paint(N, N, px);
        }

        static void InitHouseDetail()
        {
            Paving = Material("Bay pavers", Color.white, 0, .25f);
            Paving.mainTexture = PavingTexture();
            TrimStone = Textured("Concrete", "Trim stone", new Color(.62f, .60f, .55f));
            TrimStone.color = new Color(1.02f, .98f, .9f);
            FrameWhite = Material("Window frame white", new Color(.80f, .80f, .78f), 0, .55f);
            FrameDark = Material("Window frame anthracite", new Color(.11f, .12f, .13f), .3f, .5f);
            WinGlasses = new Material[3];
            for (int k = 0; k < 3; k++)
            {
                var g = Material("Window glass " + k, Color.white, .1f, .93f);
                g.mainTexture = Pane(k);
                g.mainTextureOffset = new Vector2(.5f, .5f);
                WinGlasses[k] = g;
            }
            WinGlass = WinGlasses[2];
            WinLit = Material("Window glass lit", new Color(.12f, .11f, .09f), .1f, .93f, true);
            WinLit.SetColor("_EmissionColor", Color.black);
            Blind = Material("Roller shutter", Color.white, 0, .3f);
            Blind.mainTexture = Stripes(5, new Color(.64f, .63f, .58f), new Color(.60f, .59f, .55f), false);
            Blind.mainTextureScale = new Vector2(1, 8);
            Iron = Material("Wrought iron", new Color(.07f, .075f, .08f), .5f, .45f);
            Planter = Material("Terracotta", new Color(.46f, .21f, .12f), 0, .3f);
            Flowers = Material("Window flowers", Color.white, 0, .2f);
            Flowers.mainTexture = Speckle(new Color(.15f, .30f, .08f), 5);
            Flowers.mainTextureScale = new Vector2(4, 4);
            ChimneyBrick = Material("Chimney brick", new Color(.40f, .19f, .13f), 0, .25f);
            Doors = new[]
            {
                Material("Door oak", new Color(.20f, .11f, .06f), 0, .45f),
                Material("Door green", new Color(.05f, .17f, .11f), 0, .5f),
                Material("Door red", new Color(.32f, .06f, .05f), 0, .5f),
                Material("Door white", new Color(.80f, .80f, .78f), 0, .5f),
                Material("Door anthracite", new Color(.10f, .11f, .12f), .3f, .5f),
            };
            SignBoards = new[]
            {
                Material("Sign board black", new Color(.04f, .045f, .05f), .2f, .5f),
                Material("Sign board green", new Color(.05f, .20f, .12f), .1f, .5f),
                Material("Sign board red", new Color(.40f, .05f, .04f), .1f, .5f),
                Material("Sign board blue", new Color(.04f, .10f, .28f), .1f, .5f),
                Material("Sign board brown", new Color(.22f, .13f, .07f), .1f, .5f),
            };
            ShopInteriors = new Material[8];
            for (int k = 0; k < 8; k++)
            {
                var m = Material("Shop interior " + k, Color.white * .85f, .1f, .9f, true);
                var tex = Interior(k / 2, 100 + k);
                m.mainTexture = tex;
                m.SetTexture("_EmissionMap", tex);
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", Color.white * .3f);
                ShopInteriors[k] = m;
            }
            RoofTile.mainTexture = RoofTexture();
            RoofTile.color = Color.white;
            // Striped canvas on half of the awnings.
            Awnings[1].mainTexture = Stripes(14, new Color(.62f, .10f, .08f), new Color(.86f, .84f, .78f), true);
            Awnings[1].color = Color.white;
            Awnings[3].mainTexture = Stripes(14, new Color(.12f, .30f, .20f), new Color(.86f, .84f, .78f), true);
            Awnings[3].color = Color.white;
            Detail.Clear();
            Detail.UnionWith(WinGlasses);
            Detail.UnionWith(new[] { TrimStone, FrameWhite, FrameDark, WinLit, Blind, Iron, Planter, Flowers });
        }

        // Shrink or grow a text so it fits a sign board.
        public static void Fit(TextMesh text, float maxWidth, float maxHeight)
        {
            var r = text.GetComponent<MeshRenderer>();
            if (!r) return;
            Vector3 size = r.localBounds.size;
            if (size.x < 1e-4f || size.y < 1e-4f) return;
            text.transform.localScale = Vector3.one * Mathf.Min(maxWidth / size.x, maxHeight / size.y);
        }

        // Close-up facade detail (window frames, shutters, flowers, shop names) is dropped with
        // distance: one LOD group per tile or chunk, so far streets cost what they did before.
        static void DetailLod(Transform parent)
        {
            var near = new List<Renderer>();
            for (int i = 0; i < parent.childCount; i++)
            {
                var r = parent.GetChild(i).GetComponent<MeshRenderer>();
                if (r && r.enabled && Detail.Contains(r.sharedMaterial)) near.Add(r);
            }
            foreach (var tm in parent.GetComponentsInChildren<TextMesh>())
                if (tm.name == "Shop sign text") near.Add(tm.GetComponent<Renderer>());
            if (near.Count == 0) return;
            var group = parent.GetComponent<LODGroup>();
            if (!group) group = parent.gameObject.AddComponent<LODGroup>();
            var list = near.ToArray();
            group.SetLODs(new[] { new LOD(.01f, list) });
            group.RecalculateBounds();
            float reach = DetailReach + group.size / 2;
            group.SetLODs(new[] { new LOD(Mathf.Clamp(group.size / (reach * 1.25f), .001f, .9f), list) });
        }

        // A purchased street light (or the short park lantern), its arm turned over the road.
        // Returns where the light itself hangs, in the parent's space, or null if the model is missing.
        static readonly Dictionary<bool, GameObject> lampModels = new();
        public static Vector3? StreetLamp(Transform parent, Vector3 foot, Vector3 toward, bool park = false, float height = 0)
        {
            if (!lampModels.TryGetValue(park, out var model))
                lampModels[park] = model = Resources.Load<GameObject>(park ? "Env/StreetLights/Prefabs/Street.Lighting.4" : "Env/StreetLights/Prefabs/Street.Lighting.2");
            if (!model) return null;
            var lamp = Object.Instantiate(model, parent).transform;
            lamp.name = park ? "Park lamp" : "Street lamp";
            lamp.localPosition = foot;
            lamp.localRotation = Quaternion.identity;
            lamp.localScale = Vector3.one;
            var r = lamp.GetComponentInChildren<MeshRenderer>();
            if (!r) { Object.Destroy(lamp.gameObject); return null; }
            foreach (var c in lamp.GetComponentsInChildren<Collider>()) Object.Destroy(c);
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] && mats[i].name.Contains("White")) mats[i] = LampGlow;
            r.sharedMaterials = mats;
            var b = r.bounds;
            float want = height > 0 ? height : park ? 4.6f : 8f;
            float scale = want / Mathf.Max(.1f, b.size.y);
            lamp.localScale = Vector3.one * scale;
            // Which way does the arm reach? Turn it towards the road.
            Vector3 arm = parent.InverseTransformPoint(r.bounds.center) - foot;
            arm.y = 0;
            if (!park && arm.sqrMagnitude > .01f && toward.sqrMagnitude > .01f)
                lamp.localRotation = Quaternion.Euler(0, Vector3.SignedAngle(arm.normalized, new Vector3(toward.x, 0, toward.z).normalized, Vector3.up), 0);
            var top = parent.InverseTransformPoint(r.bounds.center);
            float reach = park ? 0 : arm.magnitude * 1.9f;
            return foot + new Vector3(toward.x, 0, toward.z).normalized * reach + Vector3.up * (want - (park ? .5f : .35f));
        }

        // A purchased sign on its post, facing the way the reader comes from.
        static readonly Dictionary<string, GameObject> signModels = new();
        public static void RoadSign(Transform parent, Vector3 foot, Vector3 facing, string sign, float plateTop = 2.5f)
        {
            GameObject Load(string n)
            {
                if (!signModels.TryGetValue(n, out var m)) signModels[n] = m = Resources.Load<GameObject>("Env/Signs/Prefabs/" + n);
                return m;
            }
            var model = Load(sign);
            if (!model) return;
            var holder = new GameObject("Sign " + sign).transform;
            holder.SetParent(parent, false);
            holder.localPosition = foot;
            var plate = Object.Instantiate(model, holder).transform;
            plate.localPosition = Vector3.zero;
            foreach (var c in plate.GetComponentsInChildren<Collider>()) Object.Destroy(c);
            var rs = plate.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) { Object.Destroy(holder.gameObject); return; }
            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            // Plates without their own post are about 60 cm across: scale to that and add a post.
            float across = Mathf.Max(b.size.x, b.size.z);
            if (b.size.y < 1.4f)
            {
                float s = .75f / Mathf.Max(.05f, Mathf.Max(across, b.size.y));
                plate.localScale *= s;
                b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                plate.position += new Vector3(holder.position.x - b.center.x, holder.position.y + plateTop - b.max.y, holder.position.z - b.center.z);
                var pole = Load("Road_Sign_Pole");
                if (pole)
                {
                    var p = Object.Instantiate(pole, holder).transform;
                    foreach (var c in p.GetComponentsInChildren<Collider>()) Object.Destroy(c);
                    var pr = p.GetComponentInChildren<Renderer>();
                    if (pr)
                    {
                        float ps = (plateTop - .1f) / Mathf.Max(.1f, pr.bounds.size.y);
                        p.localScale *= ps;
                        var pb = pr.bounds;
                        p.position += new Vector3(holder.position.x - pb.center.x, holder.position.y - pb.min.y, holder.position.z - pb.center.z);
                        // Put the pole just behind the plate.
                        p.position -= holder.TransformDirection(Vector3.forward) * .04f;
                    }
                }
                else Box(holder, "Sign post", new Vector3(0, plateTop / 2, -.05f), new Vector3(.07f, plateTop, .07f), Steel);
            }
            if (LogSigns) { var fb = rs[0].bounds; foreach (var r in rs) fb.Encapsulate(r.bounds); Debug.Log($"AUTOBAHN sign {sign}: first bounds {b.size}, renderers {rs.Length}, final {fb.center - holder.position} size {fb.size}, mats {string.Join(",", System.Array.ConvertAll(rs, r => r.sharedMaterial ? r.sharedMaterial.name + "/" + r.sharedMaterial.shader.name : "null"))}"); }
            // The plate faces along its thin axis; turn that to face the reader.
            Vector3 thin = b.size.x < b.size.z ? Vector3.right : Vector3.forward;
            holder.localRotation = Quaternion.Euler(0, Vector3.SignedAngle(thin, new Vector3(facing.x, 0, facing.z).normalized, Vector3.up) + SignFlip, 0);
            // Knocked over when driven into.
            if (Knockable.Enabled) Knockable.End(holder, 30);
        }
        public static float SignFlip = 0;
        public static bool LogSigns;

        public static void Pine(Transform parent, Vector3 position, float height, bool light)
        {
            Foliage.Tree(parent, position, light ? Foliage.Kind.Pine : Foliage.Kind.Spruce, height, height < 9);
        }

        public static void Broadleaf(Transform parent, Vector3 position, float height)
        {
            float pick = Mathf.Abs(Mathf.Sin(position.x * 3.71f + position.z * 9.13f));
            var kind = pick < .18f ? Foliage.Kind.Maple : pick < .45f ? Foliage.Kind.Birch : Foliage.Kind.Oak;
            Foliage.Tree(parent, position, kind, height);
        }

        public static void Bush(Transform parent, Vector3 position, float height)
        {
            Foliage.Tree(parent, position, Foliage.Kind.Bush, height);
        }

        // A distant, cheaper version for the far rows of the forest.
        public static void FarTree(Transform parent, Vector3 position, float height, bool conifer)
        {
            Foliage.Tree(parent, position, conifer ? Foliage.Kind.Spruce : Foliage.Kind.Oak, height, true);
        }

        public static Transform Box(Transform parent, string name, Vector3 p, Vector3 scale, Material m, bool collider = false)
        {
            return Primitive(parent, name, p, scale, m, PrimitiveType.Cube, collider);
        }

        public static Transform Primitive(Transform parent, string name, Vector3 p, Vector3 scale, Material m, PrimitiveType type, bool collider = false)
        {
            var o = GameObject.CreatePrimitive(type);
            o.name = name;
            o.layer = collider ? 10 : 0;
            o.transform.SetParent(parent, false);
            o.transform.localPosition = p;
            o.transform.localScale = scale;
            o.GetComponent<Renderer>().sharedMaterial = m;
            if (!collider)
            {
                var c = o.GetComponent<Collider>();
                c.enabled = false;
                Object.Destroy(c);
            }

            return o.transform;
        }

        // Re-map a unit cube's UVs to metres so tiling textures (facades) keep real-world scale.
        public static void WorldUV(Transform box, float metresPerTile, float topScale = .1f)
        {
            var f = box.GetComponent<MeshFilter>();
            if (!f) return;
            var mesh = Object.Instantiate(f.sharedMesh);
            var v = mesh.vertices; var n = mesh.normals; var uv = new Vector2[v.Length];
            Vector3 s = box.localScale;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 p = Vector3.Scale(v[i] + Vector3.one * .5f, s);
                Vector3 a = new(Mathf.Abs(n[i].x), Mathf.Abs(n[i].y), Mathf.Abs(n[i].z));
                Vector2 q = a.x > .5f ? new Vector2(p.z, p.y) : a.z > .5f ? new Vector2(p.x, p.y) : new Vector2(p.x, p.z) * topScale;
                uv[i] = q / metresPerTile;
            }
            mesh.uv = uv;
            f.sharedMesh = mesh;
        }

        public static Mesh Mesh(Vector3[] vertices, int[] triangles, Vector2[] uv = null)
        {
            var m = new Mesh();
            m.vertices = vertices;
            m.triangles = triangles;
            if (uv != null)
                m.uv = uv;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public static Transform MeshObject(Transform parent, string name, Mesh mesh, Material material, bool collision = false)
        {
            var o = new GameObject(name);
            o.transform.SetParent(parent, false);
            o.AddComponent<MeshFilter>().sharedMesh = mesh;
            o.AddComponent<MeshRenderer>().sharedMaterial = material;
            if (collision)
            {
                o.layer = 10;
                o.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            return o.transform;
        }

        static Font textFont;
        static Material textMaterial;

        public static TextMesh Text(Transform parent, string text, Vector3 position, float size, Color color)
        {
            var go = new GameObject(text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            var t = go.AddComponent<TextMesh>();
            t.text = text;
            t.characterSize = size * .25f;
            t.fontSize = 64;
            t.anchor = TextAnchor.MiddleCenter;
            t.alignment = TextAlignment.Center;
            t.color = color;
            // Depth-tested text material (the default font shader showed through walls).
            if (!textFont) textFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (textFont) t.font = textFont;
            if (!textMaterial && textFont)
            {
                var shader = Resources.Load<Shader>("Shaders/Text3D");
                if (shader) textMaterial = new Material(shader) { name = "Sign text", mainTexture = textFont.material.mainTexture };
                Debug.Log("AUTOBAHN sign text: shader " + (shader ? shader.name + " supported " + shader.isSupported : "MISSING"));
            }
            if (textMaterial) go.GetComponent<MeshRenderer>().sharedMaterial = textMaterial;
            return t;
        }

        public static Transform Loft(Transform parent, string name, float[] z, float[] width, float[] bottom, float[] top, Material material)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int i = 0; i < z.Length; i++)
            {
                float w = width[i], b = bottom[i], h = top[i];
                float bevel = Mathf.Min(.08f, (h - b) * .25f);
                v.AddRange(new[]{new Vector3(-w * .83f, b, z[i]), new Vector3(-w, b + bevel, z[i]), new Vector3(-w, h - bevel, z[i]), new Vector3(-w * .84f, h, z[i]), new Vector3(w * .84f, h, z[i]), new Vector3(w, h - bevel, z[i]), new Vector3(w, b + bevel, z[i]), new Vector3(w * .83f, b, z[i])});
            }

            for (int j = 0; j < z.Length - 1; j++)
                for (int k = 0; k < 8; k++)
                {
                    int a = j * 8 + k, b = j * 8 + (k + 1) % 8, c = a + 8, d = b + 8;
                    t.AddRange(new[]{a, c, b, b, c, d});
                }

            for (int k = 1; k < 7; k++)
            {
                t.AddRange(new[]{0, k, k + 1});
                int a = (z.Length - 1) * 8;
                t.AddRange(new[]{a, a + k + 1, a + k});
            }

            return MeshObject(parent, name, Mesh(v.ToArray(), t.ToArray()), material);
        }

        // After Combine: drop the source pieces that were only there to be merged.
        public static void Strip(Transform root)
        {
            // Parked cars are imported models with dozens of parts each. Once their meshes are
            // merged into the tile, the whole hierarchy is dead weight: it still has to be
            // moved and culled every time a tile is recycled, so it goes.
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "Parked model") continue;
                bool merged = true;
                foreach (var r in t.GetComponentsInChildren<MeshRenderer>(true))
                    if (r.enabled) merged = false;
                if (merged)
                    Object.Destroy(t.gameObject);
            }
            foreach (var f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var r = f.GetComponent<MeshRenderer>();
                if (!r || r.enabled || f.transform.childCount != 0) continue;
                // A knockable's own pieces are switched off while it stands (drawn from its tile's
                // merged mesh) but are what is seen once it is knocked loose: never deleted. (They
                // were, and a knocked fence panel became an invisible body that cars stuck on.)
                if (f.GetComponentInParent<Knockable>() || f.GetComponentInParent<Boat>() || f.GetComponentInParent<Airplane>()) continue;
                // Primitive() disables the collider of purely visual boxes before its deferred destroy.
                var col = f.GetComponent<Collider>();
                if (col == null || !col.enabled)
                    Object.Destroy(f.gameObject);
            }
        }

        public static bool NoShadow(Material m) =>
            m == Line || m == Asphalt || m == Grass || m == Shoulder || m == Kerb || m == Glass || m == ShopLit || m == Frame
            || m == White || m == Amber || m == Red || m == Green || m == Blue || m == SignBlack || m == SignWhite || m == SignRed
            || m == SignYellow || m == SignGreen || m == RoofGravel || (SignalA != null && System.Array.IndexOf(SignalA, m) >= 0)
            || (SignalB != null && System.Array.IndexOf(SignalB, m) >= 0)
            || m == FrameWhite || m == FrameDark || (WinGlasses != null && System.Array.IndexOf(WinGlasses, m) >= 0) || m == WinLit || m == Blind || m == Iron || m == Flowers
            || (ShopInteriors != null && System.Array.IndexOf(ShopInteriors, m) >= 0) || (SignBoards != null && System.Array.IndexOf(SignBoards, m) >= 0);

        // Render layers by what a merged mesh holds, so the camera can stop drawing small things
        // early (how open-world games keep a long view cheap): trees, and small props (parked
        // cars, lamps, bins, signals, markings) vanish at their own distance; buildings, ground
        // and roads stay to the edge of the view.
        public const int PropLayer = 11, TreeLayer = 12;
        static int Category(MeshFilter f, Renderer r)
        {
            string n = f.name;
            if (n.StartsWith("Tree ")) return 1;
            return r.bounds.size.magnitude < 9 ? 2 : 0;
        }

        // An imported model that moves as one piece (a traffic car's body, one of its wheels):
        // all its readable parts merged into a single renderer with one sub-mesh per material.
        // The parts' transforms stay (code finds them by name); only their renderers go.
        // A car with 139 parts drew 139 times, and three times more for the shadow cascades.
        static readonly Dictionary<string, Mesh> mergedModels = new();

        // Parts nobody sees on a car going past (the interior, the engine bay, the suspension):
        // a traffic car goes without them. Matched against object, mesh and material names.
        static readonly string[] insideParts =
        {
            "interior", "seat", "dash", "engine", "suspension", "steering", "pedal", "cockpit", "gauge",
            "speedo", "radiator", "gearbox", "transmission", "shock", "driveshaft", "battery", "salon",
        };

        // ...but never the outside of the body ("engine hood", "seat cover" panels).
        static readonly string[] outsideWords = { "hood", "bonnet", "cover", "lid", "body", "paint", "glass", "window" };

        public static bool InsidePart(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (var word in outsideWords)
                if (n.Contains(word)) return false;
            foreach (var part in insideParts)
                if (n.Contains(part)) return true;
            return false;
        }

        // Switches off the renderers of a car's inside parts (see InsidePart); returns how many.
        // If that would be every renderer, the names mean something else: none are switched off.
        public static int DropInside(Transform car)
        {
            var drop = new List<Renderer>();
            int visible = 0;
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                visible++;
                var f = r.GetComponent<MeshFilter>();
                bool inside = InsidePart(r.name) || (f && f.sharedMesh && InsidePart(f.sharedMesh.name));
                // A renderer all of whose materials are inside ones goes too.
                if (!inside && r.sharedMaterials.Length > 0)
                {
                    inside = true;
                    foreach (var m in r.sharedMaterials)
                        if (!m || !InsidePart(m.name)) { inside = false; break; }
                }
                if (inside) drop.Add(r);
            }
            if (drop.Count >= visible) return 0;
            foreach (var r in drop) r.enabled = false;
            return drop.Count;
        }

        // `share`: models built the same way (the same car style) reuse one merged mesh; only the
        // materials (each car's own paint) differ. `outsideOnly`: without the inside parts (traffic).
        public static void MergeModel(Transform parent, string share = null, bool outsideOnly = false)
        {
            var groups = new Dictionary<Material, List<CombineInstance>>();
            var order = new List<Material>();
            var done = new List<MeshFilter>();
            bool shadows = false;
            var toParent = parent.worldToLocalMatrix;
            if (outsideOnly) DropInside(parent);
            foreach (var f in parent.GetComponentsInChildren<MeshFilter>())
            {
                var r = f.GetComponent<MeshRenderer>();
                var mesh = f.sharedMesh;
                if (!r || !r.enabled || !mesh || !mesh.isReadable || f.GetComponent<TextMesh>()) continue;
                var mats = r.sharedMaterials;
                if (mats.Length > mesh.subMeshCount) continue;
                var matrix = toParent * f.transform.localToWorldMatrix;
                for (int k = 0; k < mesh.subMeshCount; k++)
                {
                    var mat = mats[Mathf.Min(k, mats.Length - 1)];
                    if (!mat) continue;
                    // One mesh with the body and the seats in it: only the body's sub-meshes.
                    if (outsideOnly && InsidePart(mat.name)) continue;
                    if (!groups.TryGetValue(mat, out var list)) { groups[mat] = list = new List<CombineInstance>(); order.Add(mat); }
                    list.Add(new CombineInstance { mesh = mesh, subMeshIndex = k, transform = matrix });
                }
                shadows |= r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
                done.Add(f);
            }
            if (done.Count < 2) return;
            string key = share != null ? share + "#" + order.Count + "#" + done.Count + (outsideOnly ? "#outside" : "") : null;
            if (key == null || !mergedModels.TryGetValue(key, out Mesh merged) || !merged)
            {
                merged = BuildMerged(parent.name, order, groups);
                if (key != null) mergedModels[key] = merged;
            }
            var go = new GameObject("Merged model");
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = merged;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = order.ToArray();
            mr.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            foreach (var f in done)
            {
                Object.Destroy(f.GetComponent<MeshRenderer>());
                Object.Destroy(f);
            }
        }

        static Mesh BuildMerged(string name, List<Material> order, Dictionary<Material, List<CombineInstance>> groups)
        {
            var parts = new CombineInstance[order.Count];
            for (int i = 0; i < order.Count; i++)
            {
                var part = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                part.CombineMeshes(groups[order[i]].ToArray(), true, true);
                parts[i] = new CombineInstance { mesh = part, transform = Matrix4x4.identity };
            }
            var mesh = new Mesh { name = name + " merged", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(parts, false, true);
            mesh.RecalculateBounds();
            foreach (var part in parts) Object.Destroy(part.mesh);
            return mesh;
        }

        public static void Combine(Transform parent)
        {
            var groups = new Dictionary<(Material, int), List<CombineInstance>>();
            var old = new List<GameObject>();
            foreach (var f in parent.GetComponentsInChildren<MeshFilter>())
            {
                var r = f.GetComponent<MeshRenderer>();
                if (!r || !r.enabled || f.sharedMesh == null || f.GetComponent<TextMesh>() != null)
                    continue;
                // Props that can be knocked over keep their own mesh (merged separately below).
                if (f.GetComponentInParent<Knockable>() != null || f.GetComponentInParent<Boat>() != null || f.GetComponentInParent<Airplane>() != null)
                    continue;
                var mats = r.sharedMaterials;
                // Imported models (parked cars) have one sub-mesh per material; they can be
                // merged too when their mesh is readable.
                if (mats.Length != 1 && (mats.Length != f.sharedMesh.subMeshCount || !f.sharedMesh.isReadable))
                    continue;
                var matrix = parent.worldToLocalMatrix * f.transform.localToWorldMatrix;
                int cat = Category(f, r);
                for (int k = 0; k < mats.Length; k++)
                {
                    var mat = mats[k];
                    if (!mat) continue;
                    var key = (mat, cat);
                    if (!groups.ContainsKey(key))
                        groups[key] = new();
                    groups[key].Add(new CombineInstance{mesh = f.sharedMesh, subMeshIndex = k, transform = matrix});
                }
                r.enabled = false;
                old.Add(f.gameObject);
            }

            foreach (var g in groups)
            {
                var (material, cat) = g.Key;
                var mesh = new Mesh{indexFormat = UnityEngine.Rendering.IndexFormat.UInt32};
                mesh.CombineMeshes(g.Value.ToArray());
                var batched = MeshObject(parent, "Batched " + material.name, mesh, material);
                batched.gameObject.layer = cat == 1 ? TreeLayer : cat == 2 ? PropLayer : 0;
                // Flat ground, paint and small glowing or glazed parts gain nothing from casting
                // shadows but cost a lot in the shadow maps once merged into big meshes.
                if (NoShadow(material))
                    batched.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            Knockable.MergeUnder(parent);
            DetailLod(parent);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Autobahn
{
    // The loop road round the island in 200 m chunks: asphalt, markings, guardrails, signs,
    // gantries and the filling station. Each chunk is a streaming unit (WorldStreamer): built when
    // the player comes near, destroyed once far behind, the same every time.
    public sealed class RoadWorld : MonoBehaviour
    {
        readonly List<StreamUnit> chunks = new();
        Transform player;
        WorldStreamer streamer;
        // Distance mode builds one loop of city this long and keeps moving it ahead of the car.
        public const float EndlessSpan = 2400;
        public int ChunkCount => chunks.Count;

        IslandWorld island;
        public IslandWorld Island => island;

        // Distance mode: the island and its road go (they are built again on the way back).
        public void Clear()
        {
            if (streamer) streamer.UnloadAll();
            if (island) island.gameObject.SetActive(false);
        }

        public void Rebuild()
        {
            Build(player);
        }

        // Distance mode builds no road: the street grid replaces it.
        public void Recycle() { }

        public void Build(Transform follow)
        {
            player = follow;
            if (!GetComponent<TrafficSignals>())
                gameObject.AddComponent<TrafficSignals>();
            if (Route.Endless)
                return;
            streamer = WorldStreamer.Ensure(gameObject, follow);
            if (island == null)
            {
                island = new GameObject("Island").AddComponent<IslandWorld>();
                island.transform.SetParent(transform, false);
                island.Build(follow);
            }
            else
                island.gameObject.SetActive(true);
            if (chunks.Count == 0)
            {
                float total = Route.Length;
                int index = 0;
                for (float start = 0; start < total; start += 200, index++)
                {
                    float from = start, to = Mathf.Min(total, start + 200);
                    int seed = 91827 + index * 104729;
                    // The station's forecourt and planting reach about 75 m off the road.
                    var points = new List<Vector3>();
                    for (float z = from; z <= to; z += 10) points.Add(Route.Center(z));
                    points.Add(Route.Center(to));
                    var area = WorldStreamer.AreaOf(points, 80);
                    chunks.Add(streamer.Add(new StreamUnit("Road " + start.ToString("00000"), transform, Route.Center(start), area,
                        chunk => WorldStreamer.Seeded(Chunk(chunk, from, to), seed))));
                }
            }
            // The ground round the car now, the rest as the player looks about.
            streamer.Warm();
        }

        // One chunk, a part per frame: the surfaces, the markings and guardrails 48 m at a time,
        // the signs, the station, then the merge.
        IEnumerator<object> Chunk(Transform chunk, float start, float end)
        {
            float total = Route.Length, town = Autobahn.Island.TownS;
            Ribbon(chunk, start, end, -7.2f, 7.2f, 0, Art.Asphalt, true);
            Ribbon(chunk, start, end, -7.2f, -6.1f, .005f, Art.Shoulder, false);
            Ribbon(chunk, start, end, 6.1f, 7.2f, .005f, Art.Shoulder, false);
            yield return null;
            int rows = 0;
            for (float z = start; z < end; z += 12)
            {
                for (int lane = 0; lane < 2; lane++)
                    Block(chunk, "Lane marking", z + 2.4f, (lane == 0 ? -.5f : .5f) * Route.LaneWidth, .008f, new(.13f, .012f, 4.8f), Art.Line);
                bool inTown = Mathf.Abs(z - town) < Autobahn.Island.TownHalf + 30;
                for (int s = -1; s <= 1; s += 2)
                {
                    Block(chunk, "Edge line", z + 6, s * 5.85f, .008f, new(.14f, .012f, 12.2f), Art.Line);
                    if (z < 30 || z > total - 42 || inTown || Station.Entrance(z, s))
                        continue;
                    // Each 12 m of guardrail is one breakable section: solid (a car slower than
                    // 30 km/h and people on foot are stopped), faster it is torn off and falls.
                    Guardrail(chunk, z + 6, s * 7.5f);
                    if (((int)z) % 48 == 0)
                        Delineator(chunk, z, s * 7);
                }
                if (++rows % 4 == 0) yield return null;
            }

            // Roadside signs on the shoulder (speed limit, caution, attention in turn): they
            // fall over when driven into, like the city's signs.
            for (float z = start + 90; z < end; z += 200)
            {
                int n = Mathf.RoundToInt(z / 200);
                if (z < 60 || z > total - 60 || Mathf.Abs(z - town) < Autobahn.Island.TownHalf + 40 || Station.Entrance(z, 1)) continue;
                string name = (n % 3) switch { 0 => "Maximum_Sign", 1 => "Caution_Sign", _ => "Attention_Sign" };
                Vector3 foot = Route.Center(z) + Route.Right(z) * 6.75f;
                Art.RoadSign(chunk, chunk.InverseTransformPoint(foot) + Vector3.up * .02f, -Route.Forward(z), name, 2.3f);
            }

            // Direction boards over the road.
            if (start % 1600 == 800)
            {
                float z = start + 100;
                for (int s = -1; s <= 1; s += 2)
                    Block(chunk, "Gantry", z, s * 8.4f, 4.4f, new(.26f, 8.8f, .3f), Art.Steel);
                Block(chunk, "Gantry beam", z, 0, 8.7f, new(17, .3f, .35f), Art.Steel);
                Block(chunk, "Destination board", z, 0, 7.3f, new(9.5f, 2.2f, .17f), Art.Blue);
                string text = z < town - 400
                    ? $"СЕВЕРНЫЙ   {(town - z) / 1000:0} км\n↑     АЗС     ↑"
                    : $"ГОРОД   {(total - z) / 1000:0} км\n↑     ПОРТ     ↑";
                var sign = Art.Text(chunk, text, chunk.InverseTransformPoint(Route.Center(z) + Vector3.up * 7.3f - Route.Forward(z) * .11f), .40f, Color.white);
                sign.transform.rotation = Route.Rotation(z);
            }
            yield return null;

            Station.Build(chunk, start, end);
            yield return null;

            Art.Combine(chunk);
            Art.Strip(chunk);
        }

        void Lamp(Transform chunk, float z, float x, float height, float intensity, float range)
        {
            var go = new GameObject("Road illumination");
            go.transform.SetParent(chunk, false);
            go.transform.position = Route.Center(z) + Route.Right(z) * x + Vector3.up * height;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = range;
            l.intensity = intensity * 80; // URP inverse-square falloff
            l.color = new(1, .79f, .49f);
            l.shadows = LightShadows.None;
        }

        public static int GuardrailSections;

        static void Guardrail(Transform chunk, float z, float x)
        {
            Vector3 foot = Route.Center(z) + Route.Right(z) * x;
            var holder = Knockable.Begin(chunk, "Guardrail section", chunk.InverseTransformPoint(foot));
            holder.rotation = Route.Rotation(z);
            Art.Box(holder, "Steel guardrail", new Vector3(0, .66f, 0), new Vector3(.14f, .36f, 12.1f), Art.Steel);
            foreach (float pz in new[] { -5.9f, 0f })
                Art.Box(holder, "Rail post", new Vector3(.08f * Mathf.Sign(x), .38f, pz), new Vector3(.13f, .76f, .14f), Art.Steel);
            Knockable.End(holder, 160, 30, true, new Bounds(new Vector3(0, .5f, 0), new Vector3(.3f, 1f, 12.1f)));
            GuardrailSections++;
        }

        static void Delineator(Transform chunk, float z, float x)
        {
            Vector3 foot = Route.Center(z) + Route.Right(z) * x;
            var holder = Knockable.Begin(chunk, "Delineator", chunk.InverseTransformPoint(foot));
            holder.rotation = Route.Rotation(z);
            Art.Box(holder, "Delineator", new Vector3(0, .61f, 0), new Vector3(.12f, 1.05f, .14f), Art.Line);
            Art.Box(holder, "Reflector", new Vector3(0, .88f, -.1f), new Vector3(.13f, .16f, .03f), Art.Dark);
            Knockable.End(holder, 6);
        }

        Transform Block(Transform chunk, string name, float z, float x, float y, Vector3 size, Material m, bool collision = false)
        {
            var t = Art.Box(chunk, name, chunk.InverseTransformPoint(Route.Center(z) + Route.Right(z) * x + Vector3.up * y), size, m, collision);
            t.rotation = Route.Rotation(z);
            return t;
        }

        void Ribbon(Transform chunk, float from, float to, float left, float right, float height, Material mat, bool collision)
        {
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var tris = new List<int>();
            const float step = 5;
            int count = Mathf.RoundToInt((to - from) / step);
            for (int i = 0; i <= count; i++)
            {
                float z = from + i * step;
                Vector3 center = Route.Center(z) + Vector3.up * height;
                var side = Route.Right(z);
                v.Add(chunk.InverseTransformPoint(center + side * left));
                v.Add(chunk.InverseTransformPoint(center + side * right));
                uv.Add(new(left / 5, z / 5));
                uv.Add(new(right / 5, z / 5));
                if (i < count)
                {
                    int k = i * 2;
                    tris.AddRange(new[]{k, k + 2, k + 1, k + 1, k + 2, k + 3});
                }
            }

            Art.MeshObject(chunk, "Continuous surface", Art.Mesh(v.ToArray(), tris.ToArray(), uv.ToArray()), mat, collision);
        }

        // The M map and the map picture are open: nothing is unloaded meanwhile (see WorldStreamer).
        public static bool ShowAll;
    }
}

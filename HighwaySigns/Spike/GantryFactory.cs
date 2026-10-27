using UnityEngine;

namespace HighwaySigns.Spike
{
    /// <summary>
    /// Parametric gantry meshes. Local space: X runs across the road, Z along it (traffic faces -Z),
    /// Y up from road level. Span gantries are centred on X = 0; the cantilever's post is at X = 0
    /// and its arm reaches +X.
    /// </summary>
    internal static class GantryFactory
    {
        public const float kClearance = 6.4f;      // underside of the structure (~21 ft)
        public const float kTrussDepth = 1.5f;
        public const float kMountHeight = kClearance + kTrussDepth * 0.5f;

        public static Mesh Truss(float span)
        {
            var b = new MeshBuilder();
            var half = span * 0.5f;
            const float depth = kTrussDepth;
            var y0 = kClearance;
            var y1 = kClearance + depth;
            var z0 = -depth * 0.5f;
            var z1 = depth * 0.5f;

            // Four chords (pipe), lacing in all four faces.
            foreach (var y in new[] { y0, y1 })
            foreach (var z in new[] { z0, z1 })
            {
                b.AddTube(new Vector3(-half, y, z), new Vector3(half, y, z), 0.09f, 10);
            }
            var panels = Mathf.Max(2, Mathf.RoundToInt(span / depth));
            var step = span / panels;
            for (var i = 0; i < panels; i++)
            {
                var xa = -half + i * step;
                var xb = xa + step;
                var flip = (i & 1) == 0;
                foreach (var z in new[] { z0, z1 })
                {
                    b.AddTube(new Vector3(xa, flip ? y0 : y1, z), new Vector3(xb, flip ? y1 : y0, z), 0.045f, 8);
                }
                foreach (var y in new[] { y0, y1 })
                {
                    b.AddTube(new Vector3(xa, y, flip ? z0 : z1), new Vector3(xb, y, flip ? z1 : z0), 0.04f, 8);
                }
                b.AddTube(new Vector3(xa, y0, z0), new Vector3(xa, y1, z0), 0.04f, 8);
                b.AddTube(new Vector3(xa, y0, z1), new Vector3(xa, y1, z1), 0.04f, 8);
            }

            // End supports: a pair of pipe columns per side with horizontal and diagonal bracing.
            foreach (var x in new[] { -half, half })
            {
                foreach (var z in new[] { z0, z1 })
                {
                    b.AddTube(new Vector3(x, 0, z), new Vector3(x, y1 + 0.2f, z), 0.2f, 14);
                    b.AddBox(new Vector3(x, 0.03f, z), new Vector3(0.8f, 0.06f, 0.8f), Quaternion.identity);
                }
                for (var y = 1.0f; y < y0; y += 1.6f)
                {
                    b.AddTube(new Vector3(x, y, z0), new Vector3(x, y, z1), 0.05f, 8);
                    b.AddTube(new Vector3(x, y, z0), new Vector3(x, Mathf.Min(y + 1.6f, y0), z1), 0.04f, 8);
                }
            }
            return b.Build("HighwaySigns.Truss");
        }

        public static Mesh Monotube(float span)
        {
            var b = new MeshBuilder();
            var half = span * 0.5f;
            var y = kMountHeight;
            b.AddTube(new Vector3(-half - 0.2f, y, 0), new Vector3(half + 0.2f, y, 0), 0.32f, 20);
            foreach (var x in new[] { -half, half })
            {
                b.AddTube(new Vector3(x, 0, 0), new Vector3(x, y + 0.5f, 0), 0.36f, 20);
                b.AddBox(new Vector3(x, 0.03f, 0), new Vector3(1.0f, 0.06f, 1.0f), Quaternion.identity);
            }
            return b.Build("HighwaySigns.Monotube");
        }

        public static Mesh Cantilever(float armLength)
        {
            var b = new MeshBuilder();
            var y = kMountHeight;
            b.AddTube(new Vector3(0, 0, 0), new Vector3(0, y + 0.6f, 0), 0.38f, 20);
            b.AddTube(new Vector3(-0.3f, y, 0), new Vector3(armLength, y, 0), 0.28f, 20);
            // Upper tie back to the post.
            b.AddTube(new Vector3(0, y + 0.55f, 0), new Vector3(armLength * 0.45f, y + 0.05f, 0), 0.08f, 10);
            b.AddBox(new Vector3(0, 0.03f, 0), new Vector3(1.1f, 0.06f, 1.1f), Quaternion.identity);
            return b.Build("HighwaySigns.Cantilever");
        }

        /// <summary>A thin sign panel box whose -Z face shows the whole sign texture.</summary>
        public static Mesh Panel(float width, float height)
        {
            var b = new MeshBuilder();
            // Back and edges sample a pixel inside the panel border area, not the transparent corner.
            var edgeUv = new Rect(0.5f, 0.03f, 0.001f, 0.001f);
            b.AddBox(Vector3.zero, new Vector3(width, height, 0.08f), Quaternion.identity, new Rect(0, 0, 1, 1), edgeUv);
            return b.Build("HighwaySigns.Panel");
        }
    }
}

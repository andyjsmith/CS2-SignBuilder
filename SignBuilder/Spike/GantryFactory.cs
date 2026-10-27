using UnityEngine;

namespace SignBuilder.Spike
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
            return b.Build("SignBuilder.Truss");
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
            return b.Build("SignBuilder.Monotube");
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
            return b.Build("SignBuilder.Cantilever");
        }

        public const float kCurvedPipeRadius = 0.3f;

        /// <summary>
        /// Curved tubular span: one pipe rising from each base through a large-radius 90° bend into
        /// the horizontal member, with flange rings at the joints. <paramref name="height"/> is the
        /// centre line of the horizontal pipe; signs hang below it.
        /// </summary>
        public static Mesh CurvedSpan(float span, float height, float bendRadius = 3f)
        {
            var b = new MeshBuilder();
            var half = span * 0.5f;
            var path = new System.Collections.Generic.List<Vector3>();
            path.Add(new Vector3(-half, 0, 0));
            AddArc(path, new Vector3(-half + bendRadius, height - bendRadius, 0), bendRadius, 180f, 90f);
            AddArc(path, new Vector3(half - bendRadius, height - bendRadius, 0), bendRadius, 90f, 0f);
            path.Add(new Vector3(half, 0, 0));
            b.AddSweptTube(path, kCurvedPipeRadius, 20);
            foreach (var x in new[] { -half, half })
            {
                AddFlange(b, new Vector3(x, 1.4f, 0), Vector3.up);
                AddFlange(b, new Vector3(x, height - bendRadius, 0), Vector3.up);
                b.AddBox(new Vector3(x, 0.04f, 0), new Vector3(1.1f, 0.08f, 1.1f), Quaternion.identity);
            }
            AddFlange(b, new Vector3(-half + bendRadius, height, 0), Vector3.right);
            AddFlange(b, new Vector3(half - bendRadius, height, 0), Vector3.right);
            return b.Build("SignBuilder.CurvedSpan");
        }

        /// <summary>Curved tubular cantilever: post at X = 0 bending over into an arm that reaches -X.</summary>
        public static Mesh CurvedCantilever(float armLength, float height, float bendRadius = 3f)
        {
            var b = new MeshBuilder();
            var path = new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0) };
            AddArc(path, new Vector3(-bendRadius, height - bendRadius, 0), bendRadius, 0f, 90f);
            path.Add(new Vector3(-armLength, height, 0));
            b.AddSweptTube(path, kCurvedPipeRadius, 20);
            AddFlange(b, new Vector3(0, 1.4f, 0), Vector3.up);
            AddFlange(b, new Vector3(0, height - bendRadius, 0), Vector3.up);
            AddFlange(b, new Vector3(-bendRadius, height, 0), Vector3.right);
            b.AddBox(new Vector3(0, 0.04f, 0), new Vector3(1.1f, 0.08f, 1.1f), Quaternion.identity);
            return b.Build("SignBuilder.CurvedCantilever");
        }

        /// <summary>Roadside sign bottom height above ground (about 7 ft, MUTCD urban/expressway minimum).</summary>
        public const float kGroundClearance = 2.15f;

        /// <summary>
        /// Ground-mounted support for a roadside sign: one post for narrow signs, two for wider ones.
        /// Steel W-beam posts (two flanges and a web), with concrete footings. Local space: X across the
        /// sign, posts behind it (+Z), origin at ground level under the sign's centre.
        /// </summary>
        public static Mesh GroundPosts(float signWidth, float signHeight, out int postCount)
        {
            var b = new MeshBuilder();
            postCount = signWidth < 2.5f ? 1 : 2;
            var top = kGroundClearance + signHeight - 0.1f;
            var xs = postCount == 1 ? new[] { 0f } : new[] { -signWidth * 0.3f, signWidth * 0.3f };
            const float flange = 0.2f;
            const float depth = 0.25f;
            const float t = 0.018f;
            const float z = 0.2f; // behind the sign face
            foreach (var x in xs)
            {
                var h = top + 0.4f;
                var centre = new Vector3(x, h * 0.5f - 0.4f, z);
                b.AddBox(centre + new Vector3(0, 0, -depth * 0.5f + t * 0.5f), new Vector3(flange, h, t), Quaternion.identity);
                b.AddBox(centre + new Vector3(0, 0, depth * 0.5f - t * 0.5f), new Vector3(flange, h, t), Quaternion.identity);
                b.AddBox(centre, new Vector3(t, h, depth - t * 2), Quaternion.identity);
                b.AddBox(new Vector3(x, 0.05f, z), new Vector3(0.6f, 0.1f, 0.6f), Quaternion.identity);
            }
            return b.Build("SignBuilder.GroundPosts");
        }

        /// <summary>Appends points of a circular arc in the XY plane (angles in degrees, 0 = +X, 90 = +Y).</summary>
        private static void AddArc(System.Collections.Generic.List<Vector3> path, Vector3 centre, float radius, float fromDeg, float toDeg, int steps = 12)
        {
            for (var i = 0; i <= steps; i++)
            {
                var a = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, i / (float)steps);
                path.Add(centre + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * radius);
            }
        }

        private static void AddFlange(MeshBuilder b, Vector3 centre, Vector3 axis)
        {
            b.AddTube(centre - axis * 0.04f, centre + axis * 0.04f, kCurvedPipeRadius + 0.09f, 20);
        }

        /// <summary>
        /// A thin sign panel box: submesh 0 is the -Z face showing the whole sign texture, submesh 1
        /// the back and edges (plain aluminium).
        /// </summary>
        public static Mesh Panel(float width, float height)
        {
            var b = new MeshBuilder();
            b.AddBox(Vector3.zero, new Vector3(width, height, 0.08f), Quaternion.identity, new Rect(0, 0, 1, 1), otherSubmesh: 1);
            return b.Build("SignBuilder.Panel");
        }
    }
}

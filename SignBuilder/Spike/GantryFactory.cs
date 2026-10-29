using System.Linq;
using UnityEngine;

namespace SignBuilder.Spike
{
    /// <summary>
    /// Parametric gantry meshes. Local space: X runs across the road, Z along it (traffic faces -Z),
    /// Y up from road level. Span gantries are centered on X = 0; the cantilever's post is at X = 0
    /// and its arm reaches +X.
    /// </summary>
    public static class GantryFactory
    {
        public const float kClearance = 6.4f;      // underside of the structure (~21 ft)
        public const float kTrussDepth = 1.5f;
        public const float kMountHeight = kClearance + kTrussDepth * 0.5f;
        // Member sizes the signs are mounted against (see AssemblyBuilder's sign depth).
        public const float kChordRadius = 0.09f;
        public const float kPlanarChordRadius = 0.08f;
        public const float kMonotubeRadius = 0.32f;
        public const float kCantileverArmRadius = 0.28f;
        public const float kPanelThickness = 0.08f;

        /// <summary>
        /// How far posts reach below the base plate, so uneven ground never shows a gap under them.
        /// <c>ground</c> parameters are the drop from the structure's origin (road level) to the terrain
        /// under its posts: base plates sit on the terrain and posts continue below it.
        /// </summary>
        public const float kFooting = 0.5f;

        /// <summary>Beam height adjustment for the mesh being built (SupportSpec.Lift).</summary>
        private static float s_Lift;

        /// <summary>
        /// Builds the support described by a spec (layout output, stored in records and saves). <paramref name="lowDetail"/>
        /// builds the distant (LOD) version: the same members with far fewer sides.
        /// </summary>
        public static Mesh FromSpec(Assemblies.SupportSpec spec, bool lowDetail = false)
        {
            var a = spec.Args ?? System.Array.Empty<float>();
            float Arg(int i) => i < a.Length ? a[i] : 0f;
            s_Lift = spec.Lift;
            MeshBuilder.LowDetail = lowDetail;
            Mesh mesh;
            try
            {
            mesh = spec.Kind switch
            {
                "monotube" => Monotube(Arg(0), Arg(1)),
                "curvedSpan" => CurvedSpan(Arg(0), Arg(1), Bend(Arg(1), Arg(2)), ground: Arg(2)),
                "curvedCantilever" => CurvedCantilever(Arg(0), Arg(1), Bend(Arg(1), Arg(2)), ground: Arg(2)),
                "cantilever" => Cantilever(Arg(0), Arg(1)),
                "trussCantilever" => TrussCantilever(Arg(0), Arg(1)),
                "planarTruss" => PlanarTruss(-Arg(0) * 0.5f, Arg(0) * 0.5f, new[] { -Arg(0) * 0.5f, Arg(0) * 0.5f }, Arg(1)),
                "planarTrussCantilever" => PlanarTruss(-Arg(0), 0.25f, new[] { 0f }, Arg(1)),
                "ground" => GroundPosts(Arg(0), Arg(1), out _, Arg(2), a.Skip(3).ToArray()),
                "none" => new MeshBuilder().Build("SignBuilder.NoSupport"),
                _ => Truss(Arg(0), Arg(1)),
            };
            }
            finally
            {
                s_Lift = 0f;
                MeshBuilder.LowDetail = false;
            }
            if (spec.Mirror) Mirror(mesh);
            return mesh;
        }

        /// <summary>The curved tube's bend radius: 3 m, smaller when the structure is lowered so the bend stays above its flange.</summary>
        private static float Bend(float height, float ground) => Mathf.Clamp(height + ground - 1.6f, 0.6f, 3f);

        /// <summary>Mirrors a mesh across X in place (flipping winding so faces stay outward).</summary>
        private static void Mirror(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            for (var i = 0; i < vertices.Length; i++)
            {
                vertices[i].x = -vertices[i].x;
                if (i < normals.Length) normals[i].x = -normals[i].x;
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var triangles = mesh.GetTriangles(sub);
                for (var t = 0; t + 2 < triangles.Length; t += 3) (triangles[t + 1], triangles[t + 2]) = (triangles[t + 2], triangles[t + 1]);
                mesh.SetTriangles(triangles, sub);
            }
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// Single (planar) truss: top and bottom chords with verticals and diagonals in one vertical plane, on
        /// single pipe columns. Lighter-looking than the box truss.
        /// </summary>
        public static Mesh PlanarTruss(float x0, float x1, float[] columns, float ground)
        {
            var b = new MeshBuilder();
            const float depth = 1.2f;
            var y0 = kMountHeight + s_Lift - depth * 0.5f;
            var y1 = kMountHeight + s_Lift + depth * 0.5f;
            b.AddTube(new Vector3(x0, y0, 0), new Vector3(x1, y0, 0), kPlanarChordRadius, 10);
            b.AddTube(new Vector3(x0, y1, 0), new Vector3(x1, y1, 0), kPlanarChordRadius, 10);
            var panels = Mathf.Max(2, Mathf.RoundToInt((x1 - x0) / depth));
            var step = (x1 - x0) / panels;
            for (var i = 0; i <= panels; i++)
            {
                var xa = x0 + i * step;
                b.AddTube(new Vector3(xa, y0, 0), new Vector3(xa, y1, 0), 0.04f, 8);
                if (i == panels) break;
                var flip = (i & 1) == 0;
                b.AddTube(new Vector3(xa, flip ? y0 : y1, 0), new Vector3(xa + step, flip ? y1 : y0, 0), 0.04f, 8);
            }
            // Columns stand in the truss's plane: the chords run into them.
            foreach (var x in columns)
            {
                b.AddTube(new Vector3(x, -ground - kFooting, 0), new Vector3(x, y1 + 0.25f, 0), 0.22f, 16);
            }
            return b.Build("SignBuilder.PlanarTruss");
        }

        public static Mesh Truss(float span, float ground = 0f) => TrussBeam(-span * 0.5f, span * 0.5f, new[] { -span * 0.5f, span * 0.5f }, ground, "SignBuilder.Truss");

        /// <summary>Truss cantilever: one column pair at X = 0, the truss reaching -X.</summary>
        public static Mesh TrussCantilever(float armLength, float ground = 0f) => TrussBeam(-armLength, 0.3f, new[] { 0f }, ground, "SignBuilder.TrussCantilever");

        /// <summary>A box truss from <paramref name="x0"/> to <paramref name="x1"/> on column pairs at <paramref name="columns"/>.</summary>
        private static Mesh TrussBeam(float x0, float x1, float[] columns, float ground, string name)
        {
            var b = new MeshBuilder();
            var span = x1 - x0;
            const float depth = kTrussDepth;
            var y0 = kClearance + s_Lift;
            var y1 = kClearance + s_Lift + depth;
            var z0 = -depth * 0.5f;
            var z1 = depth * 0.5f;

            // Four chords (pipe), lacing in all four faces.
            foreach (var y in new[] { y0, y1 })
            foreach (var z in new[] { z0, z1 })
            {
                b.AddTube(new Vector3(x0, y, z), new Vector3(x1, y, z), kChordRadius, 10);
            }
            var panels = Mathf.Max(2, Mathf.RoundToInt(span / depth));
            var step = span / panels;
            for (var i = 0; i < panels; i++)
            {
                var xa = x0 + i * step;
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

            // Supports: a pair of pipe columns with horizontal and diagonal bracing.
            foreach (var x in columns)
            {
                foreach (var z in new[] { z0, z1 })
                {
                    b.AddTube(new Vector3(x, -ground - kFooting, z), new Vector3(x, y1 + 0.2f, z), 0.2f, 14);
                }
                for (var y = 1.0f; y < y0; y += 1.6f)
                {
                    b.AddTube(new Vector3(x, y, z0), new Vector3(x, y, z1), 0.05f, 8);
                    b.AddTube(new Vector3(x, y, z0), new Vector3(x, Mathf.Min(y + 1.6f, y0), z1), 0.04f, 8);
                }
            }
            return b.Build(name);
        }

        public static Mesh Monotube(float span, float ground = 0f)
        {
            var b = new MeshBuilder();
            var half = span * 0.5f;
            var y = kMountHeight + s_Lift;
            b.AddTube(new Vector3(-half - 0.2f, y, 0), new Vector3(half + 0.2f, y, 0), kMonotubeRadius, 20);
            foreach (var x in new[] { -half, half })
            {
                b.AddTube(new Vector3(x, -ground - kFooting, 0), new Vector3(x, y + 0.5f, 0), 0.36f, 20);
            }
            return b.Build("SignBuilder.Monotube");
        }

        public static Mesh Cantilever(float armLength, float ground = 0f)
        {
            var b = new MeshBuilder();
            var y = kMountHeight + s_Lift;
            b.AddTube(new Vector3(0, -ground - kFooting, 0), new Vector3(0, y + 0.6f, 0), 0.38f, 20);
            b.AddTube(new Vector3(-0.3f * Mathf.Sign(armLength), y, 0), new Vector3(armLength, y, 0), kCantileverArmRadius, 20);
            return b.Build("SignBuilder.Cantilever");
        }

        public const float kCurvedPipeRadius = 0.3f;

        /// <summary>
        /// Curved tubular span: one pipe rising from each base through a large-radius 90° bend into
        /// the horizontal member, with flange rings at the joints. <paramref name="height"/> is the
        /// center line of the horizontal pipe; signs hang below it.
        /// </summary>
        public static Mesh CurvedSpan(float span, float height, float bendRadius = 3f, float ground = 0f)
        {
            var b = new MeshBuilder();
            var half = span * 0.5f;
            var path = new System.Collections.Generic.List<Vector3>();
            path.Add(new Vector3(-half, -ground - kFooting, 0));
            AddArc(path, new Vector3(-half + bendRadius, height - bendRadius, 0), bendRadius, 180f, 90f);
            AddArc(path, new Vector3(half - bendRadius, height - bendRadius, 0), bendRadius, 90f, 0f);
            path.Add(new Vector3(half, -ground - kFooting, 0));
            b.AddSweptTube(path, kCurvedPipeRadius, 20);
            foreach (var x in new[] { -half, half })
            {
                AddFlange(b, new Vector3(x, 1.4f - ground, 0), Vector3.up);
                AddFlange(b, new Vector3(x, height - bendRadius, 0), Vector3.up);
            }
            AddFlange(b, new Vector3(-half + bendRadius, height, 0), Vector3.right);
            AddFlange(b, new Vector3(half - bendRadius, height, 0), Vector3.right);
            return b.Build("SignBuilder.CurvedSpan");
        }

        /// <summary>Curved tubular cantilever: post at X = 0 bending over into an arm that reaches -X.</summary>
        public static Mesh CurvedCantilever(float armLength, float height, float bendRadius = 3f, float ground = 0f)
        {
            var b = new MeshBuilder();
            var path = new System.Collections.Generic.List<Vector3> { new Vector3(0, -ground - kFooting, 0) };
            AddArc(path, new Vector3(-bendRadius, height - bendRadius, 0), bendRadius, 0f, 90f);
            path.Add(new Vector3(-armLength, height, 0));
            b.AddSweptTube(path, kCurvedPipeRadius, 20);
            AddFlange(b, new Vector3(0, 1.4f - ground, 0), Vector3.up);
            AddFlange(b, new Vector3(0, height - bendRadius, 0), Vector3.up);
            AddFlange(b, new Vector3(-bendRadius, height, 0), Vector3.right);
            return b.Build("SignBuilder.CurvedCantilever");
        }

        /// <summary>Roadside sign bottom height above ground (about 7 ft, MUTCD urban/expressway minimum).</summary>
        public const float kGroundClearance = 2.15f;

        /// <summary>
        /// Ground-mounted support for a roadside sign: one post for narrow signs, two for wider ones.
        /// Steel W-beam posts (two flanges and a web), with concrete footings. Local space: X across the
        /// sign, posts behind it (+Z), origin at ground level under the sign's center.
        /// </summary>
        /// <summary>Where roadside posts go under a sign (group) of this width: one post, or two at 30% either side.</summary>
        public static float[] GroundPostXs(float signWidth) => signWidth < 2.5f ? new[] { 0f } : new[] { -signWidth * 0.3f, signWidth * 0.3f };

        public static Mesh GroundPosts(float signWidth, float signHeight, out int postCount, float extraDepth = 0f, float[] tops = null)
        {
            var b = new MeshBuilder();
            var xs = GroundPostXs(signWidth);
            postCount = xs.Length;
            const float flange = 0.2f;
            const float depth = 0.25f;
            const float t = 0.018f;
            const float z = 0.2f; // behind the sign face
            for (var k = 0; k < xs.Length; k++)
            {
                var x = xs[k];
                // Each post stops at the top edge of the sign in front of it (an exit tab may not cover it).
                var top = tops != null && k < tops.Length && tops[k] > 0f ? tops[k] : kGroundClearance + signHeight - 0.1f;
                // Posts run from below ground (or down to the terrain when the road is raised) to the sign top.
                var bottom = -0.4f - extraDepth;
                var h = top - bottom;
                var center = new Vector3(x, bottom + h * 0.5f, z);
                b.AddBox(center + new Vector3(0, 0, -depth * 0.5f + t * 0.5f), new Vector3(flange, h, t), Quaternion.identity);
                b.AddBox(center + new Vector3(0, 0, depth * 0.5f - t * 0.5f), new Vector3(flange, h, t), Quaternion.identity);
                b.AddBox(center, new Vector3(t, h, depth - t * 2), Quaternion.identity);
            }
            return b.Build("SignBuilder.GroundPosts");
        }

        /// <summary>Appends points of a circular arc in the XY plane (angles in degrees, 0 = +X, 90 = +Y).</summary>
        private static void AddArc(System.Collections.Generic.List<Vector3> path, Vector3 center, float radius, float fromDeg, float toDeg, int steps = 12)
        {
            for (var i = 0; i <= steps; i++)
            {
                var a = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, i / (float)steps);
                path.Add(center + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * radius);
            }
        }

        private static void AddFlange(MeshBuilder b, Vector3 center, Vector3 axis)
        {
            b.AddTube(center - axis * 0.04f, center + axis * 0.04f, kCurvedPipeRadius + 0.09f, 20);
        }

        /// <summary>
        /// Sign lighting under one sign (local to the sign's center, face toward -Z): fixtures spaced along its bottom
        /// edge, each on an arm reaching forward and down from the bottom edge, its housing tilted up at the face.
        /// </summary>
        public static Mesh SignLights(float width, float height)
        {
            var b = new MeshBuilder();
            var count = Mathf.Max(1, Mathf.RoundToInt(width / 2.4f));
            var bottom = -height * 0.5f;
            for (var i = 0; i < count; i++)
            {
                var x = (i + 0.5f) / count * width - width * 0.5f;
                var root = new Vector3(x, bottom + 0.1f, 0.06f);
                var elbow = new Vector3(x, bottom - 0.25f, 0.02f);
                var head = new Vector3(x, bottom - 0.35f, -0.85f);
                b.AddTube(root, elbow, 0.035f, 8);
                b.AddTube(elbow, head, 0.035f, 8);
                b.AddBox(head + new Vector3(0, 0.03f, -0.05f), new Vector3(0.55f, 0.1f, 0.32f), Quaternion.Euler(-25f, 0, 0));
            }
            return b.Build("SignBuilder.SignLights");
        }

        /// <summary>
        /// A thin sign panel box: submesh 0 is the -Z face showing the whole sign texture, submesh 1
        /// the back and edges (plain aluminum).
        /// </summary>
        public static Mesh Panel(float width, float height) => Panel(width, height, new[] { new Rect(0, 0, 1, 1) });

        /// <summary>
        /// A sign panel made of one slab per rectangle of <paramref name="shape"/> (UV space of the face texture),
        /// so non-rectangular signs (exit tabs) get edges and a back that follow their outline.
        /// </summary>
        public static Mesh Panel(float width, float height, System.Collections.Generic.IEnumerable<Rect> shape)
        {
            var b = new MeshBuilder();
            foreach (var r in shape)
            {
                var center = new Vector3((r.center.x - 0.5f) * width, (r.center.y - 0.5f) * height, 0);
                b.AddBox(center, new Vector3(r.width * width, r.height * height, kPanelThickness), Quaternion.identity, r, otherSubmesh: 1);
            }
            return b.Build("SignBuilder.Panel");
        }
    }
}

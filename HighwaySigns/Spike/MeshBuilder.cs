using System.Collections.Generic;
using UnityEngine;

namespace HighwaySigns.Spike
{
    /// <summary>
    /// Minimal mesh builder for boxes and tubes. Every face gets flat/smooth normals and UVs
    /// measured in metres (times <see cref="UvPerMetre"/>), so a tiling material keeps the same
    /// texel density on a 9 m post and a 0.1 m lacing bar instead of stretching.
    /// </summary>
    internal sealed class MeshBuilder
    {
        private readonly List<Vector3> m_Vertices = new();
        private readonly List<Vector3> m_Normals = new();
        private readonly List<Vector2> m_Uvs = new();
        private readonly List<int> m_Triangles = new();

        /// <summary>Texture repeats per metre for metre-mapped faces. 0 = stretch 0..1 over each face (old behaviour).</summary>
        public float UvPerMetre { get; set; } = 0.5f;

        /// <summary>
        /// Adds a box with the given center, size and rotation. If <paramref name="frontUv"/> is set,
        /// the -Z face (the one facing a camera looking down +Z) maps exactly onto that rect, used for sign faces.
        /// </summary>
        public void AddBox(Vector3 center, Vector3 size, Quaternion rotation, Rect? frontUv = null, Rect? otherUv = null)
        {
            var h = size * 0.5f;
            AddFace(center, rotation, new Vector3(0, 0, -1), new Vector3(-h.x, -h.y, -h.z), new Vector3(size.x, 0, 0), new Vector3(0, size.y, 0), frontUv);
            AddFace(center, rotation, new Vector3(0, 0, 1), new Vector3(h.x, -h.y, h.z), new Vector3(-size.x, 0, 0), new Vector3(0, size.y, 0), otherUv);
            AddFace(center, rotation, new Vector3(-1, 0, 0), new Vector3(-h.x, -h.y, h.z), new Vector3(0, 0, -size.z), new Vector3(0, size.y, 0), otherUv);
            AddFace(center, rotation, new Vector3(1, 0, 0), new Vector3(h.x, -h.y, -h.z), new Vector3(0, 0, size.z), new Vector3(0, size.y, 0), otherUv);
            AddFace(center, rotation, new Vector3(0, 1, 0), new Vector3(-h.x, h.y, -h.z), new Vector3(size.x, 0, 0), new Vector3(0, 0, size.z), otherUv);
            AddFace(center, rotation, new Vector3(0, -1, 0), new Vector3(-h.x, -h.y, h.z), new Vector3(size.x, 0, 0), new Vector3(0, 0, -size.z), otherUv);
        }

        /// <summary>Adds a box spanning from <paramref name="a"/> to <paramref name="b"/> with a square cross-section.</summary>
        public void AddBeam(Vector3 a, Vector3 b, float thickness)
        {
            var dir = b - a;
            var rotation = Quaternion.FromToRotation(Vector3.forward, dir.normalized);
            AddBox((a + b) * 0.5f, new Vector3(thickness, thickness, dir.magnitude), rotation);
        }

        /// <summary>Adds a capped round tube from <paramref name="a"/> to <paramref name="b"/>. U wraps the circumference, V runs along the length.</summary>
        public void AddTube(Vector3 a, Vector3 b, float radius, int segments = 16)
        {
            var axis = b - a;
            var length = axis.magnitude;
            var dir = axis / length;
            var side = Vector3.Cross(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            var up = Vector3.Cross(side, dir);
            var circumference = 2 * Mathf.PI * radius;
            var uvScale = UvPerMetre > 0 ? UvPerMetre : 1f / Mathf.Max(circumference, length);

            var start = m_Vertices.Count;
            for (var i = 0; i <= segments; i++)
            {
                var angle = i / (float)segments * Mathf.PI * 2;
                var n = side * Mathf.Cos(angle) + up * Mathf.Sin(angle);
                var u = i / (float)segments * circumference * uvScale;
                m_Vertices.Add(a + n * radius); m_Normals.Add(n); m_Uvs.Add(new Vector2(u, 0));
                m_Vertices.Add(b + n * radius); m_Normals.Add(n); m_Uvs.Add(new Vector2(u, length * uvScale));
            }
            for (var i = 0; i < segments; i++)
            {
                var v = start + i * 2;
                m_Triangles.AddRange(new[] { v, v + 1, v + 3, v, v + 3, v + 2 });
            }
            AddCap(a, -dir, side, up, radius, segments, uvScale);
            AddCap(b, dir, side, up, radius, segments, uvScale);
        }

        private void AddCap(Vector3 center, Vector3 normal, Vector3 side, Vector3 up, float radius, int segments, float uvScale)
        {
            var c = m_Vertices.Count;
            m_Vertices.Add(center); m_Normals.Add(normal); m_Uvs.Add(Vector2.zero);
            for (var i = 0; i <= segments; i++)
            {
                var angle = i / (float)segments * Mathf.PI * 2;
                var offset = (side * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius;
                m_Vertices.Add(center + offset); m_Normals.Add(normal);
                m_Uvs.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius * uvScale);
            }
            // Keep the cap clockwise (Unity's front face) when seen from outside along its normal.
            var flip = Vector3.Dot(Vector3.Cross(side, up), normal) < 0;
            for (var i = 0; i < segments; i++)
            {
                if (flip) m_Triangles.AddRange(new[] { c, c + 2 + i, c + 1 + i });
                else m_Triangles.AddRange(new[] { c, c + 1 + i, c + 2 + i });
            }
        }

        private void AddFace(Vector3 center, Quaternion rotation, Vector3 normal, Vector3 origin, Vector3 right, Vector3 up, Rect? uvRect)
        {
            var start = m_Vertices.Count;
            m_Vertices.Add(center + rotation * origin);
            m_Vertices.Add(center + rotation * (origin + right));
            m_Vertices.Add(center + rotation * (origin + right + up));
            m_Vertices.Add(center + rotation * (origin + up));
            var n = rotation * normal;
            for (var i = 0; i < 4; i++) m_Normals.Add(n);

            Rect uv;
            if (uvRect.HasValue) uv = uvRect.Value;
            else if (UvPerMetre > 0) uv = new Rect(0, 0, right.magnitude * UvPerMetre, up.magnitude * UvPerMetre);
            else uv = new Rect(0, 0, 1, 1);
            m_Uvs.Add(new Vector2(uv.xMin, uv.yMin));
            m_Uvs.Add(new Vector2(uv.xMax, uv.yMin));
            m_Uvs.Add(new Vector2(uv.xMax, uv.yMax));
            m_Uvs.Add(new Vector2(uv.xMin, uv.yMax));
            m_Triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
        }

        public Mesh Build(string name)
        {
            var mesh = new Mesh { name = name };
            if (m_Vertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(m_Vertices);
            mesh.SetNormals(m_Normals);
            mesh.SetUVs(0, m_Uvs);
            mesh.SetTriangles(m_Triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

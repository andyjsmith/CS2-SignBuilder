using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using SignBuilder.Core;
using SignBuilder.Rendering;
using SignBuilder.Spike;
using Unity.Mathematics;
using UnityEngine;

namespace SignBuilder.Assemblies
{
    /// <summary>Where and how an assembly sits in the world.</summary>
    public struct AssemblyPlacement
    {
        public float3 Origin;
        public quaternion Rotation;
        public Mesh Support;
        public Vector3[] SignCentres; // assembly-local, one per sign
    }

    /// <summary>
    /// Builds a sign assembly: bakes its sign designs, lays out the chosen support for the road it was
    /// placed on, merges support and sign panels into one mesh (submesh 0 steel/aluminium, submesh
    /// i + 1 the face of sign i), creates its materials and prefab, records it and spawns it.
    /// </summary>
    public static class AssemblyBuilder
    {
        public static readonly string[] Supports = { "truss", "curvedSpan", "monotube", "curvedCantilever", "cantilever", "ground" };
        private const float kSignGap = 0.6f;

        /// <summary>
        /// Lays out <paramref name="support"/> for signs of the given sizes (metres, x = width,
        /// y = height) at a road snap. Several signs sit side by side, centred over the lanes they face.
        /// </summary>
        public static AssemblyPlacement Layout(string support, RoadProbe.Snap s, IReadOnlyList<Vector2> sizes, bool reverse = false)
        {
            // Signs face the traffic of the clicked carriageway; reverse aims them at the other direction
            // (the paired carriageway of a divided highway, or the opposing lanes of a two-way road).
            var dir = s.MainDirection * (reverse ? -1 : 1);
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            var lanes = s.Lanes.Where(l => l.Direction == dir).ToList();
            var laneAbsolute = lanes.Count > 0 ? lanes.Average(l => l.Offset) : 0f;
            // Single-carriageway supports sit on the carriageway whose traffic they face: when reversing on
            // a divided highway that is the paired carriageway, not the one that was clicked.
            var onPaired = reverse && s.PairedEdge != Unity.Entities.Entity.Null && lanes.Count > 0 && lanes.All(l => l.OnPairedEdge);
            var baseOffset = onPaired ? s.PairedOffset : 0f;
            var basePoint = s.Point + s.Right * baseOffset;
            var baseWidth = onPaired ? s.PairedWidth : s.EdgeWidth;
            var laneCentre = laneAbsolute - baseOffset;

            var maxH = sizes.Count > 0 ? sizes.Max(z => z.y) : 2f;
            var total = sizes.Sum(z => z.x) + kSignGap * Math.Max(0, sizes.Count - 1);
            float overheadBottom = math.max(GantryFactory.kMountHeight - maxH * 0.5f, 5.3f);
            // Curved tube supports carry the signs bolted in front of the horizontal pipe, centred on it.
            float curvedHeight = math.max(7.0f, 5.3f + maxH * 0.5f);

            // Side-by-side row of signs centred on local X = centreX; bottoms aligned (overhead/ground) or
            // centred on the pipe (curved tube supports).
            Vector3[] Row(float centreX, float z, Func<float, float> centreY)
            {
                var result = new Vector3[sizes.Count];
                var x = centreX - total * 0.5f;
                for (var i = 0; i < sizes.Count; i++)
                {
                    result[i] = new Vector3(x + sizes[i].x * 0.5f, centreY(sizes[i].y), z);
                    x += sizes[i].x + kSignGap;
                }
                return result;
            }
            Func<float, float> standing(float bottom) => h => bottom + h * 0.5f;
            Func<float, float> onPipe(float pipeHeight) => h => pipeHeight;

            switch (support)
            {
                case "monotube":
                    return new AssemblyPlacement
                    {
                        Origin = basePoint, Rotation = rotation, Support = GantryFactory.Monotube(math.max(baseWidth, total) + 5f),
                        SignCentres = Row(laneCentre * dir, -0.45f, standing(overheadBottom)),
                    };
                case "curvedSpan":
                    return new AssemblyPlacement
                    {
                        Origin = basePoint, Rotation = rotation, Support = GantryFactory.CurvedSpan(math.max(baseWidth + 5f, total + 7f), curvedHeight),
                        SignCentres = Row(laneCentre * dir, -0.4f, onPipe(curvedHeight)),
                    };
                case "cantilever":
                case "curvedCantilever":
                {
                    // Post just off the right-hand edge (relative to traffic), arm reaching back over the lanes.
                    var postOffset = dir * (baseWidth * 0.5f + 1.5f);
                    var curved = support == "curvedCantilever";
                    // Signs on the curved arm stay clear of the bend above the post.
                    var postClear = curved ? 3.3f : 0.5f;
                    var arm = math.max(baseWidth * (curved ? 0.75f : 0.55f) + 1.5f, total + postClear + 1f);
                    var centreX = math.clamp((laneCentre - postOffset) * dir, -arm + total * 0.5f, -total * 0.5f - postClear);
                    return new AssemblyPlacement
                    {
                        Origin = basePoint + s.Right * postOffset, Rotation = rotation,
                        Support = curved ? GantryFactory.CurvedCantilever(arm, curvedHeight) : GantryFactory.Cantilever(-arm),
                        SignCentres = curved ? Row(centreX, -0.4f, onPipe(curvedHeight)) : Row(centreX, -0.45f, standing(overheadBottom)),
                    };
                }
                case "ground":
                {
                    var lateral = dir * (baseWidth * 0.5f + 2.5f + total * 0.5f);
                    var origin = basePoint + s.Right * lateral;
                    // On a raised road the posts reach down to the terrain instead of floating.
                    var drop = math.clamp(origin.y - TerrainHeight(origin), 0f, 20f);
                    return new AssemblyPlacement
                    {
                        Origin = origin, Rotation = rotation, Support = GantryFactory.GroundPosts(total, maxH, out _, drop),
                        SignCentres = Row(0f, 0f, standing(GantryFactory.kGroundClearance)),
                    };
                }
                default:
                {
                    // Truss: spans both carriageways of a divided highway.
                    var extent = s.Extent;
                    var centre = (extent.x + extent.y) * 0.5f;
                    var span = math.max(extent.y - extent.x, total) + 5f;
                    return new AssemblyPlacement
                    {
                        Origin = s.Point + s.Right * centre, Rotation = rotation, Support = GantryFactory.Truss(span),
                        SignCentres = Row((laneAbsolute - centre) * dir, -(GantryFactory.kTrussDepth * 0.5f + 0.15f), standing(overheadBottom)),
                    };
                }
            }
        }

        private static float TerrainHeight(float3 position)
        {
            var terrain = Unity.Entities.World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
            var data = terrain.GetHeightData(true);
            return Game.Simulation.TerrainUtils.SampleHeight(ref data, position);
        }

        /// <summary>Bakes every design (cached faces return immediately).</summary>
        public static IEnumerator BakeAll(IReadOnlyList<JObject> designs, Action<List<BakedFace>> onDone)
        {
            var faces = new List<BakedFace>();
            foreach (var design in designs)
            {
                BakedFace face = null;
                yield return SignBuilderRuntime.Baker.Bake(design, 3f, f => face = f);
                if (face == null)
                {
                    onDone(null);
                    yield break;
                }
                faces.Add(face);
            }
            onDone(faces);
        }

        public static List<Vector2> Sizes(IEnumerable<BakedFace> faces) => faces.Select(f => new Vector2(f.WidthMeters, f.HeightMeters)).ToList();

        /// <summary>
        /// Bakes, builds, registers, records and spawns an assembly. Yields while baking; the record is
        /// passed to <paramref name="onDone"/>, failures to <paramref name="onError"/>.
        /// </summary>
        public static IEnumerator Build(IReadOnlyList<JObject> designs, string support, bool reverse, Func<IReadOnlyList<Vector2>, AssemblyPlacement> layout,
            Action<AssemblyRecord, AssemblyPlacement> onDone, Action<string> onError)
        {
            List<BakedFace> faces = null;
            yield return BakeAll(designs, f => faces = f);
            if (faces == null || faces.Count == 0)
            {
                onError($"Could not render the sign: {SignBuilderRuntime.Baker.LastError}");
                yield break;
            }

            AssemblyRecord record;
            AssemblyPlacement placement;
            try
            {
                var prefabs = new RuntimePrefabs();
                placement = layout(Sizes(faces));
                var mesh = Merge(placement.Support, faces, placement.SignCentres);
                var id = Guid.NewGuid().ToString("N").Substring(0, 12);
                var db = SignBuilderRuntime.Database;
                var donor = SignBuilderRuntime.Donor(prefabs);
                var steel = SignBuilderRuntime.Steel(prefabs);
                var surfaces = new List<Colossal.IO.AssetDatabase.SurfaceAsset> { steel };
                surfaces.AddRange(faces.Select(f => SurfaceFactory.SignFace(db, SignBuilderRuntime.SurfaceFolder, donor, f.Texture, f.Key)));
                var bounds = new Bounds3(mesh.bounds.min, mesh.bounds.max);
                var indexCount = 0;
                for (var i = 0; i < mesh.subMeshCount; i++) indexCount += (int)mesh.GetIndexCount(i);
                var vertexCount = mesh.vertexCount;
                var geometry = RuntimePrefabs.SaveGeometry(mesh, db, SignBuilderRuntime.MeshFolder, "SignBuilder_A_" + id);
                record = new AssemblyRecord
                {
                    Id = id,
                    Support = support,
                    Reverse = reverse,
                    Designs = designs.ToList(),
                    GeometryId = geometry.id.guid.ToString(),
                    SurfaceIds = surfaces.Select(x => x.id.guid.ToString()).ToList(),
                    Bounds = new[] { bounds.min.x, bounds.min.y, bounds.min.z, bounds.max.x, bounds.max.y, bounds.max.z },
                    IndexCount = indexCount,
                    VertexCount = vertexCount,
                    Created = DateTime.UtcNow,
                };
                var prefab = prefabs.Register(record.PrefabName, geometry, surfaces.ToArray(), bounds, indexCount, vertexCount);
                AssemblyLibrary.Add(record);
                AssemblyBootstrapSystem.TrackNewPrefab(prefab);
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "Building a sign assembly failed");
                onError("Building the sign failed: " + e.Message);
                yield break;
            }

            // Let PrefabInitializeSystem create the prefab's entity data before spawning.
            for (var i = 0; i < 10; i++) yield return null;
            var runtime = new RuntimePrefabs();
            if (runtime.PrefabSystem.TryGetPrefab(new PrefabID(nameof(StaticObjectPrefab), record.PrefabName), out var created))
            {
                AssemblyBootstrapSystem.ClearOverridableNow(created as StaticObjectPrefab);
                runtime.Spawn((StaticObjectPrefab)created, placement.Origin, placement.Rotation);
            }
            onDone(record, placement);
        }

        /// <summary>
        /// Merges the support (one submesh) with a panel per sign (panel submesh 0 = face, 1 = back and
        /// edges) at <paramref name="centres"/>: output submesh 0 is everything steel/aluminium,
        /// submesh i + 1 the face of sign i.
        /// </summary>
        public static Mesh Merge(Mesh support, IReadOnlyList<BakedFace> faces, IReadOnlyList<Vector3> centres)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var submeshes = new List<List<int>> { new List<int>() };

            void Append(Mesh m, Vector3 offset, int submesh, List<int> into)
            {
                var start = vertices.Count;
                foreach (var p in m.vertices) vertices.Add(p + offset);
                normals.AddRange(m.normals);
                uvs.AddRange(m.uv);
                foreach (var i in m.GetTriangles(submesh)) into.Add(start + i);
            }

            Append(support, Vector3.zero, 0, submeshes[0]);
            for (var i = 0; i < faces.Count; i++)
            {
                var panel = GantryFactory.Panel(faces[i].WidthMeters, faces[i].HeightMeters);
                Append(panel, centres[i], 1, submeshes[0]);
                var face = new List<int>();
                Append(panel, centres[i], 0, face);
                submeshes.Add(face);
                UnityEngine.Object.Destroy(panel);
            }

            var mesh = new Mesh { name = "SignBuilder.Assembly", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = submeshes.Count;
            for (var i = 0; i < submeshes.Count; i++) mesh.SetTriangles(submeshes[i], i);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

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
        public Vector3 SignCentre; // assembly-local
    }

    /// <summary>
    /// Builds a sign assembly: bakes the design, lays out the chosen support for the road it was
    /// placed on, merges support and sign panel into one mesh (submesh 0 steel, submesh 1 the face),
    /// creates its materials and prefab, records it in the library and spawns it.
    /// </summary>
    public static class AssemblyBuilder
    {
        public static readonly string[] Supports = { "truss", "curvedSpan", "monotube", "curvedCantilever", "cantilever", "ground" };

        /// <summary>Lays out <paramref name="support"/> for a sign of the given size at a road snap.</summary>
        public static AssemblyPlacement Layout(string support, RoadProbe.Snap s, float w, float h)
        {
            var dir = s.MainDirection;
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            var lanes = s.Lanes.Where(l => !l.OnPairedEdge && l.Direction == dir).ToList();
            var laneCentre = lanes.Count > 0 ? lanes.Average(l => l.Offset) : 0f;
            // World lateral offsets are measured along s.Right; assembly-local X runs along s.Right * dir.
            Vector3 Sign(float lateralFromOrigin, float y, float z) => new Vector3(lateralFromOrigin * dir, y, z);
            float overheadY = math.max(GantryFactory.kMountHeight, 5.3f + h * 0.5f);
            float curvedHeight = math.max(7.5f, 5.3f + h + 0.45f);
            float hangY(float pipeHeight) => pipeHeight - GantryFactory.kCurvedPipeRadius - 0.15f - h * 0.5f;

            switch (support)
            {
                case "monotube":
                    return new AssemblyPlacement
                    {
                        Origin = s.Point, Rotation = rotation, Support = GantryFactory.Monotube(s.EdgeWidth + 5f),
                        SignCentre = Sign(laneCentre, overheadY, -0.45f),
                    };
                case "curvedSpan":
                    return new AssemblyPlacement
                    {
                        Origin = s.Point, Rotation = rotation, Support = GantryFactory.CurvedSpan(s.EdgeWidth + 5f, curvedHeight),
                        SignCentre = Sign(laneCentre, hangY(curvedHeight), -0.4f),
                    };
                case "cantilever":
                case "curvedCantilever":
                {
                    // Post just off the right-hand edge (relative to traffic), arm reaching back over the lanes.
                    var postOffset = dir * (s.EdgeWidth * 0.5f + 1.5f);
                    var curved = support == "curvedCantilever";
                    var arm = s.EdgeWidth * (curved ? 0.75f : 0.55f) + 1.5f;
                    var signX = math.clamp((laneCentre - postOffset) * dir, -arm + w * 0.5f, -w * 0.5f - 0.5f);
                    return new AssemblyPlacement
                    {
                        Origin = s.Point + s.Right * postOffset, Rotation = rotation,
                        Support = curved ? GantryFactory.CurvedCantilever(arm, curvedHeight) : GantryFactory.Cantilever(-arm),
                        SignCentre = new Vector3(signX, curved ? hangY(curvedHeight) : overheadY, curved ? -0.4f : -0.45f),
                    };
                }
                case "ground":
                {
                    var lateral = dir * (s.EdgeWidth * 0.5f + 2.5f + w * 0.5f);
                    var origin = s.Point + s.Right * lateral;
                    // On a raised road the posts reach down to the terrain instead of floating.
                    var drop = math.clamp(origin.y - TerrainHeight(origin), 0f, 20f);
                    return new AssemblyPlacement
                    {
                        Origin = origin, Rotation = rotation, Support = GantryFactory.GroundPosts(w, h, out _, drop),
                        SignCentre = new Vector3(0, GantryFactory.kGroundClearance + h * 0.5f, 0),
                    };
                }
                default:
                {
                    // Truss: spans both carriageways of a divided highway.
                    var extent = s.Extent;
                    var centre = (extent.x + extent.y) * 0.5f;
                    return new AssemblyPlacement
                    {
                        Origin = s.Point + s.Right * centre, Rotation = rotation, Support = GantryFactory.Truss(extent.y - extent.x + 5f),
                        SignCentre = Sign(laneCentre - centre, overheadY, -(GantryFactory.kTrussDepth * 0.5f + 0.15f)),
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

        /// <summary>
        /// Bakes, builds, registers, records and spawns an assembly. Yields while baking; the record is
        /// passed to <paramref name="onDone"/> (null on failure, with the reason in <paramref name="onError"/>).
        /// </summary>
        public static IEnumerator Build(JObject design, string support, Func<float, float, AssemblyPlacement> layout,
            Action<AssemblyRecord, AssemblyPlacement> onDone, Action<string> onError)
        {
            BakedFace face = null;
            yield return SignBuilderRuntime.Baker.Bake(design, 3f, f => face = f);
            if (face == null)
            {
                onError($"Could not render the sign: {SignBuilderRuntime.Baker.LastError}");
                yield break;
            }

            AssemblyRecord record;
            AssemblyPlacement placement;
            try
            {
                var prefabs = new RuntimePrefabs();
                placement = layout(face.WidthMeters, face.HeightMeters);
                var mesh = Merge(placement.Support, GantryFactory.Panel(face.WidthMeters, face.HeightMeters), placement.SignCentre);
                var id = Guid.NewGuid().ToString("N").Substring(0, 12);
                var db = SignBuilderRuntime.Database;
                var donor = SignBuilderRuntime.Donor(prefabs);
                var steel = SignBuilderRuntime.Steel(prefabs);
                var faceSurface = SurfaceFactory.SignFace(db, SignBuilderRuntime.SurfaceFolder, donor, face.Texture, face.Key);
                var bounds = new Bounds3(mesh.bounds.min, mesh.bounds.max);
                var indexCount = (int)(mesh.GetIndexCount(0) + mesh.GetIndexCount(1));
                var vertexCount = mesh.vertexCount;
                var geometry = RuntimePrefabs.SaveGeometry(mesh, db, SignBuilderRuntime.MeshFolder, "SignBuilder_A_" + id);
                record = new AssemblyRecord
                {
                    Id = id,
                    Support = support,
                    Design = design,
                    GeometryId = geometry.id.guid.ToString(),
                    SurfaceIds = new List<string> { steel.id.guid.ToString(), faceSurface.id.guid.ToString() },
                    Bounds = new[] { bounds.min.x, bounds.min.y, bounds.min.z, bounds.max.x, bounds.max.y, bounds.max.z },
                    IndexCount = indexCount,
                    VertexCount = vertexCount,
                    Created = DateTime.UtcNow,
                };
                var prefab = prefabs.Register(record.PrefabName, geometry, new[] { steel, faceSurface }, bounds, indexCount, vertexCount);
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
        /// Merges the support (one submesh) with the panel (submesh 0 = face, 1 = back/edges) moved to
        /// <paramref name="signCentre"/>: output submesh 0 is everything steel/aluminium, 1 the sign face.
        /// </summary>
        public static Mesh Merge(Mesh support, Mesh panel, Vector3 signCentre)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var steel = new List<int>();
            var faces = new List<int>();

            void Append(Mesh m, Vector3 offset, int submesh, List<int> into)
            {
                var start = vertices.Count;
                var v = m.vertices;
                var n = m.normals;
                var uv = m.uv;
                foreach (var p in v) vertices.Add(p + offset);
                normals.AddRange(n);
                uvs.AddRange(uv);
                foreach (var i in m.GetTriangles(submesh)) into.Add(start + i);
                // Duplicate vertices of other submeshes are unused but harmless; keep the meshes small anyway.
            }

            Append(support, Vector3.zero, 0, steel);
            Append(panel, signCentre, 1, steel);
            Append(panel, signCentre, 0, faces);

            var mesh = new Mesh { name = "SignBuilder.Assembly", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(steel, 0);
            mesh.SetTriangles(faces, 1);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

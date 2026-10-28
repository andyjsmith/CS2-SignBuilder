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
        public SupportSpec Spec;
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
        /// <summary>Road surface this far above the terrain counts as elevated (bridge, viaduct, embankment).</summary>
        private const float kElevatedDrop = 2f;
        /// <summary>Post centre distance inside the deck edge on elevated roads (on the shoulder, inside the barrier).</summary>
        private const float kDeckInset = 0.7f;

        /// <summary>
        /// Lays out <paramref name="support"/> for signs of the given sizes (metres, x = width,
        /// y = height) at a road snap. Several signs sit side by side, centred over the lanes they face.
        /// </summary>
        public static AssemblyPlacement Layout(string support, RoadProbe.Snap s, IReadOnlyList<Vector2> sizes, bool reverse = false)
        {
            var placement = LayoutSpec(support, s, sizes, reverse);
            placement.Support = GantryFactory.FromSpec(placement.Spec);
            return placement;
        }

        private static AssemblyPlacement LayoutSpec(string support, RoadProbe.Snap s, IReadOnlyList<Vector2> sizes, bool reverse)
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

            // On elevated roads and bridges there is no ground beside the deck: posts that would stand off it
            // move onto the deck, just inside its edges, and the structure narrows to match.
            bool OffDeck(float3 post) => post.y - TerrainHeight(post) > kElevatedDrop;
            float FitSpan(float3 centre, float deckWidth, float span, float minSpan)
            {
                if (!OffDeck(centre - s.Right * span * 0.5f) && !OffDeck(centre + s.Right * span * 0.5f)) return span;
                return math.max(deckWidth - 2f * kDeckInset, minSpan);
            }

            switch (support)
            {
                case "monotube":
                    return new AssemblyPlacement
                    {
                        Origin = basePoint, Rotation = rotation, Spec = SupportSpec.Of("monotube", FitSpan(basePoint, baseWidth, math.max(baseWidth, total) + 5f, total + 0.5f)),
                        SignCentres = Row(laneCentre * dir, -0.45f, standing(overheadBottom)),
                    };
                case "curvedSpan":
                    return new AssemblyPlacement
                    {
                        Origin = basePoint, Rotation = rotation, Spec = SupportSpec.Of("curvedSpan", FitSpan(basePoint, baseWidth, math.max(baseWidth + 5f, total + 7f), total + 1f), curvedHeight),
                        SignCentres = Row(laneCentre * dir, -0.4f, onPipe(curvedHeight)),
                    };
                case "cantilever":
                case "curvedCantilever":
                {
                    // Post just off the right-hand edge (relative to traffic), arm reaching back over the lanes.
                    var postOffset = dir * (baseWidth * 0.5f + 1.5f);
                    if (OffDeck(basePoint + s.Right * postOffset)) postOffset = dir * (baseWidth * 0.5f - kDeckInset);
                    var curved = support == "curvedCantilever";
                    // Signs on the curved arm stay clear of the bend above the post.
                    var postClear = curved ? 3.3f : 0.5f;
                    var arm = math.max(baseWidth * (curved ? 0.75f : 0.55f) + 1.5f, total + postClear + 1f);
                    var centreX = math.clamp((laneCentre - postOffset) * dir, -arm + total * 0.5f, -total * 0.5f - postClear);
                    return new AssemblyPlacement
                    {
                        Origin = basePoint + s.Right * postOffset, Rotation = rotation,
                        Spec = curved ? SupportSpec.Of("curvedCantilever", arm, curvedHeight) : SupportSpec.Of("cantilever", -arm),
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
                        Origin = origin, Rotation = rotation, Spec = SupportSpec.Of("ground", total, maxH, drop),
                        SignCentres = Row(0f, 0f, standing(GantryFactory.kGroundClearance)),
                    };
                }
                default:
                {
                    // Truss: spans both carriageways of a divided highway.
                    var extent = s.Extent;
                    var centre = (extent.x + extent.y) * 0.5f;
                    var span = FitSpan(s.Point + s.Right * centre, extent.y - extent.x, math.max(extent.y - extent.x, total) + 5f, total + 0.5f);
                    return new AssemblyPlacement
                    {
                        Origin = s.Point + s.Right * centre, Rotation = rotation, Spec = SupportSpec.Of("truss", span),
                        SignCentres = Row((laneAbsolute - centre) * dir, -(GantryFactory.kTrussDepth * 0.5f + 0.15f), standing(overheadBottom)),
                    };
                }
            }
        }

        internal static float TerrainHeight(float3 position)
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
                placement = layout(Sizes(faces));
                record = new AssemblyRecord
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    Support = support,
                    Reverse = reverse,
                    Designs = designs.ToList(),
                    SupportSpec = placement.Spec,
                    SignCentres = placement.SignCentres.SelectMany(c => new[] { c.x, c.y, c.z }).ToArray(),
                    Created = DateTime.UtcNow,
                };
                CreateAssets(record, placement.Support, faces);
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
        /// Creates the mesh and materials of <paramref name="record"/> in the mod's asset database, registers
        /// its prefab and stores the record (asset ids filled in) in the library.
        /// </summary>
        private static void CreateAssets(AssemblyRecord record, Mesh support, IReadOnlyList<BakedFace> faces)
        {
            var prefabs = new RuntimePrefabs();
            var mesh = Merge(support, faces, record.Centres);
            var db = SignBuilderRuntime.Database;
            var donor = SignBuilderRuntime.Donor(prefabs);
            var steel = SignBuilderRuntime.Steel(prefabs);
            var surfaces = new List<Colossal.IO.AssetDatabase.SurfaceAsset> { steel };
            surfaces.AddRange(faces.Select(f => SurfaceFactory.SignFace(db, SignBuilderRuntime.SurfaceFolder, donor, f.Texture, f.Key)));
            var bounds = new Bounds3(mesh.bounds.min, mesh.bounds.max);
            var indexCount = 0;
            for (var i = 0; i < mesh.subMeshCount; i++) indexCount += (int)mesh.GetIndexCount(i);
            var geometry = RuntimePrefabs.SaveGeometry(mesh, db, SignBuilderRuntime.MeshFolder, record.PrefabName);
            record.GeometryId = geometry.id.guid.ToString();
            record.SurfaceIds = surfaces.Select(x => x.id.guid.ToString()).ToList();
            record.Bounds = new[] { bounds.min.x, bounds.min.y, bounds.min.z, bounds.max.x, bounds.max.y, bounds.max.z };
            record.IndexCount = indexCount;
            record.VertexCount = mesh.vertexCount;
            var prefab = prefabs.Register(record.PrefabName, geometry, surfaces.ToArray(), bounds, indexCount, mesh.vertexCount);
            AssemblyLibrary.Add(record);
            AssemblyBootstrapSystem.TrackNewPrefab(prefab);
        }

        /// <summary>
        /// Re-creates an assembly from the recipe stored in a save (designs, support spec, sign positions),
        /// for cities shared from another PC. The prefab gets the same name, so the save's objects match it.
        /// </summary>
        public static IEnumerator Rebuild(AssemblyRecord record, Action<bool> onDone)
        {
            if (record.SupportSpec == null || record.SignCentres == null || record.Designs.Count == 0)
            {
                Mod.log.Warn($"{record.PrefabName}: no recipe in the save, cannot rebuild");
                onDone(false);
                yield break;
            }
            List<BakedFace> faces = null;
            yield return BakeAll(record.Designs, f => faces = f);
            if (faces == null || faces.Count != record.Centres.Length)
            {
                Mod.log.Warn($"{record.PrefabName}: could not render its signs ({SignBuilderRuntime.Baker.LastError})");
                onDone(false);
                yield break;
            }
            try
            {
                var support = GantryFactory.FromSpec(record.SupportSpec);
                CreateAssets(record, support, faces);
                UnityEngine.Object.Destroy(support);
            }
            catch (Exception e)
            {
                Mod.log.Error(e, $"Rebuilding {record.PrefabName} failed");
                onDone(false);
                yield break;
            }
            onDone(true);
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

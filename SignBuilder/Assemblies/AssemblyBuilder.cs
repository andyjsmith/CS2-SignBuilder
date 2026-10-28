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
        /// <summary>Lanes the signs can be centred over (for the lane picker).</summary>
        public int LaneCount;
        // Structure-space X (drivers' right = +) of signs, faced lanes, posts, the structure and the deck,
        // for the arrangement view.
        public float[] SignXs;
        public float[] LaneXs;
        public float[] PostXs;
        public float2 StructureX;
        public float2 DeckX;
    }

    /// <summary>
    /// Builds a sign assembly: bakes its sign designs, lays out the chosen support for the road it was
    /// placed on, merges support and sign panels into one mesh (submesh 0 steel/aluminium, submesh
    /// i + 1 the face of sign i), creates its materials and prefab, records it and spawns it.
    /// </summary>
    public static class AssemblyBuilder
    {
        /// <summary>Structure types (C# ids used by the UI).</summary>
        public static readonly string[] Types = { "doubleTruss", "singleTruss", "curved", "straight", "ground" };
        private const float kSignGap = 0.3f;
        /// <summary>Road surface this far above the terrain counts as elevated (bridge, viaduct, embankment).</summary>
        private const float kElevatedDrop = 2f;
        /// <summary>Post centre distance inside the deck edge on elevated roads (on the shoulder, inside the barrier).</summary>
        private const float kDeckInset = 0.7f;
        /// <summary>Lowest bottom edge for overhead signs (about 17.5 ft).</summary>
        private const float kMinClearance = 5.3f;

        /// <summary>
        /// Lays out a structure for signs of the given sizes (metres, x = width, y = height, already scaled)
        /// at a road snap (or a synthetic one for free placement). Each sign follows its slot in
        /// <paramref name="o"/> (side by side over the lanes, over one lane, or at an offset); overlapping
        /// signs are pushed apart, and the structure is sized to carry them all.
        /// </summary>
        public static AssemblyPlacement Layout(RoadProbe.Snap s, IReadOnlyList<Vector2> sizes, LayoutOptions o)
        {
            var placement = LayoutSpec(s, sizes, o);
            placement.Support = GantryFactory.FromSpec(placement.Spec);
            return placement;
        }

        private static AssemblyPlacement LayoutSpec(RoadProbe.Snap s, IReadOnlyList<Vector2> sizes, LayoutOptions o)
        {
            var type = o.Type;
            var spanKind = type == "ground" ? "single" : o.Span;
            // Signs face the traffic of the clicked carriageway; reverse aims them at the other direction
            // (the paired carriageway of a divided highway, or the opposing lanes of a two-way road).
            var dir = s.MainDirection * (o.Reverse ? -1 : 1);
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            // Lanes this structure's signs face, left to right as drivers see them.
            var lanes = s.Lanes.Where(l => l.Direction == dir).OrderBy(l => l.Offset * dir).ToList();
            var lanesCentre = lanes.Count > 0 ? lanes.Average(l => l.Offset) : 0f;
            // Single-carriageway structures stand on the carriageway whose traffic they face: when reversing
            // on a divided highway that is the paired carriageway, not the one that was clicked.
            var onPaired = o.Reverse && s.IsPaired && lanes.Count > 0 && lanes.All(l => l.OnPairedEdge);
            var baseOffset = onPaired ? s.PairedOffset : 0f;
            var basePoint = s.Point + s.Right * baseOffset;
            var baseWidth = onPaired ? s.PairedWidth : s.EdgeWidth;
            var maxH = sizes.Count > 0 ? sizes.Max(z => z.y) : 2f;

            var elevated = false;
            // On elevated roads and bridges there is no ground beside the deck: posts that would stand off it
            // move onto the deck, just inside its edges, and the structure narrows to match.
            bool OffDeck(float3 post) => !o.Free && post.y - TerrainHeight(post) > kElevatedDrop;
            // How far below the structure's origin the terrain is under its posts: posts reach down to it
            // (plus a footing) so sloped verges never leave them hovering. Free placement uses its elevation.
            float GroundUnder(params float3[] posts)
            {
                if (o.Free) return o.Elevation;
                if (elevated) return 0f;
                return math.clamp(posts.Max(p => p.y - TerrainHeight(p)), 0f, kElevatedDrop);
            }

            // Sign x positions in structure space, measured from a reference lateral offset (along Right).
            float[] SignXs(float reference)
            {
                float Local(float absolute) => (absolute - reference) * dir;
                var xs = new float[sizes.Count];
                var auto = Enumerable.Range(0, sizes.Count).Where(i => o.Slot(i).Mode == "auto" || (o.Slot(i).Mode == "lane" && lanes.Count == 0)).ToList();
                var groupWidth = auto.Sum(i => sizes[i].x) + kSignGap * Math.Max(0, auto.Count - 1);
                var x = Local(lanesCentre) - groupWidth * 0.5f;
                foreach (var i in auto)
                {
                    xs[i] = x + sizes[i].x * 0.5f;
                    x += sizes[i].x + kSignGap;
                }
                for (var i = 0; i < sizes.Count; i++)
                {
                    var slot = o.Slot(i);
                    if (slot.Mode == "lane" && lanes.Count > 0)
                    {
                        var index = math.clamp(slot.FromRight ? lanes.Count - 1 - slot.Lane : slot.Lane, 0, lanes.Count - 1);
                        xs[i] = Local(lanes[index].Offset) + slot.Offset;
                    }
                    else if (slot.Mode == "free") xs[i] = Local(lanesCentre) + slot.Offset;
                    else xs[i] += slot.Offset;
                }
                SpreadOut(xs, sizes);
                return xs;
            }
            Vector3[] Centres(float[] xs, float z, Func<float, float> centreY) =>
                Enumerable.Range(0, sizes.Count).Select(i => new Vector3(xs[i], centreY(sizes[i].y), z)).ToArray();
            float Reach(float[] xs) => sizes.Count == 0 ? 0f : Enumerable.Range(0, sizes.Count).Max(i => math.abs(xs[i]) + sizes[i].x * 0.5f);
            float[] LaneXs(float reference) => lanes.Select(l => (l.Offset - reference) * dir).ToArray();
            float2 Edges(float reference, float centre, float width)
            {
                var a = (centre - width * 0.5f - reference) * dir;
                var b = (centre + width * 0.5f - reference) * dir;
                return new float2(math.min(a, b), math.max(a, b));
            }

            // Mounting height and depth per structure type. Overhead signs are each centred on the beam (a short
            // sign beside a tall one still crosses it), raised only as far as their clearance needs; curved tube
            // structures carry their signs bolted in front of the horizontal pipe.
            var curvedHeight = math.max(7.0f, kMinClearance + maxH * 0.5f);
            Func<float, float> centreY = type == "curved" ? (h => curvedHeight) : (h => math.max(GantryFactory.kMountHeight, kMinClearance + h * 0.5f));
            var z = type switch
            {
                "doubleTruss" => -(GantryFactory.kTrussDepth * 0.5f + 0.15f),
                "singleTruss" => -0.3f,
                "curved" => -0.4f,
                _ => -0.45f,
            };
            // Clear distance from a post to the nearest sign edge. Signs may overlap a curved tube's bend a little
            // so they can still sit centred over the outermost lanes.
            var postClear = type switch { "curved" => 1.3f, "doubleTruss" => 1.1f, "singleTruss" => 0.6f, _ => 0.5f };

            AssemblyPlacement result;
            if (type == "ground")
            {
                var total = sizes.Sum(z2 => z2.x) + kSignGap * Math.Max(0, sizes.Count - 1);
                var lateral = o.Free ? 0f : dir * (baseWidth * 0.5f + 2.5f + total * 0.5f);
                var origin = basePoint + s.Right * lateral;
                // On a raised road the posts reach down to the terrain instead of floating.
                var drop = o.Free ? o.Elevation : math.clamp(origin.y - TerrainHeight(origin), 0f, 20f);
                var xs = new float[sizes.Count];
                var x = -total * 0.5f;
                for (var i = 0; i < sizes.Count; i++)
                {
                    xs[i] = x + sizes[i].x * 0.5f + o.Slot(i).Offset;
                    x += sizes[i].x + kSignGap;
                }
                result = new AssemblyPlacement
                {
                    Origin = origin, Rotation = rotation, Spec = SupportSpec.Of("ground", total, maxH, drop),
                    SignCentres = Centres(xs, 0f, h => GantryFactory.kGroundClearance + h * 0.5f),
                    SignXs = xs, LaneXs = LaneXs(baseOffset + lateral), PostXs = new[] { 0f },
                };
            }
            else if (spanKind == "cantilever")
            {
                // Post just off the carriageway edge on the drivers' right (or left), arm reaching over the lanes
                // only as far as the signs need. Free placement puts the post at the cursor.
                var side = o.LeftSide ? -1f : 1f;
                var postOffset = o.Free ? 0f : dir * side * (baseWidth * 0.5f + 1.5f);
                if (OffDeck(basePoint + s.Right * postOffset))
                {
                    postOffset = dir * side * (baseWidth * 0.5f - kDeckInset);
                    elevated = true;
                }
                var post = basePoint + s.Right * postOffset;
                var ground = GroundUnder(post);
                var reference = baseOffset + postOffset;
                var xs = SignXs(reference);
                // Every sign stays on the arm side of the post, clear of it.
                for (var i = 0; i < xs.Length; i++)
                {
                    var limit = postClear + sizes[i].x * 0.5f;
                    xs[i] = o.LeftSide ? math.max(xs[i], limit) : math.min(xs[i], -limit);
                }
                var reachOut = xs.Length == 0 ? 0f : Enumerable.Range(0, xs.Length).Max(i => math.abs(xs[i]) + sizes[i].x * 0.5f);
                var arm = math.max(reachOut + 0.4f, postClear + 1f);
                var spec = type switch
                {
                    "curved" => SupportSpec.Of("curvedCantilever", math.max(arm, 3.5f), curvedHeight, ground),
                    "doubleTruss" => SupportSpec.Of("trussCantilever", arm, ground),
                    "singleTruss" => SupportSpec.Of("planarTrussCantilever", arm, ground),
                    _ => SupportSpec.Of("cantilever", -arm, ground),
                };
                spec.Mirror = o.LeftSide;
                result = new AssemblyPlacement
                {
                    Origin = post, Rotation = rotation, Spec = spec,
                    SignCentres = Centres(xs, z, centreY),
                    SignXs = xs, LaneXs = LaneXs(reference), PostXs = new[] { 0f },
                    StructureX = o.LeftSide ? new float2(-0.4f, arm) : new float2(-arm, 0.4f),
                    DeckX = Edges(reference, baseOffset, baseWidth),
                };
            }
            else
            {
                // Full spans cross every carriageway (both sides of a divided highway); single spans only the
                // carriageway the signs face.
                float reference, deckWidth;
                if (spanKind == "full")
                {
                    var extent = s.Extent;
                    reference = (extent.x + extent.y) * 0.5f;
                    deckWidth = extent.y - extent.x;
                }
                else
                {
                    reference = baseOffset;
                    deckWidth = baseWidth;
                }
                var origin = s.Point + s.Right * reference;
                var xs = SignXs(reference);
                var needed = 2f * (Reach(xs) + postClear);
                var span = math.max(deckWidth + 5f, needed);
                if (OffDeck(origin - s.Right * span * 0.5f) || OffDeck(origin + s.Right * span * 0.5f))
                {
                    elevated = true;
                    span = math.max(deckWidth - 2f * kDeckInset, needed);
                }
                var ground = GroundUnder(origin - s.Right * span * 0.5f, origin + s.Right * span * 0.5f);
                result = new AssemblyPlacement
                {
                    Origin = origin, Rotation = rotation,
                    Spec = type switch
                    {
                        "curved" => SupportSpec.Of("curvedSpan", span, curvedHeight, ground),
                        "straight" => SupportSpec.Of("monotube", span, ground),
                        "singleTruss" => SupportSpec.Of("planarTruss", span, ground),
                        _ => SupportSpec.Of("truss", span, ground),
                    },
                    SignCentres = Centres(xs, z, centreY),
                    SignXs = xs, LaneXs = LaneXs(reference), PostXs = new[] { -span * 0.5f, span * 0.5f },
                    StructureX = new float2(-span * 0.5f, span * 0.5f),
                    DeckX = spanKind == "full" ? Edges(reference, (s.Extent.x + s.Extent.y) * 0.5f, deckWidth) : Edges(reference, baseOffset, baseWidth),
                };
            }
            result.LaneCount = lanes.Count;
            return result;
        }

        /// <summary>Pushes overlapping signs apart symmetrically (keeping their order), at least the sign gap apart.</summary>
        private static void SpreadOut(float[] xs, IReadOnlyList<Vector2> sizes)
        {
            if (xs.Length < 2) return;
            var order = Enumerable.Range(0, xs.Length).OrderBy(i => xs[i]).ToArray();
            for (var pass = 0; pass < 30; pass++)
            {
                var moved = false;
                for (var k = 0; k + 1 < order.Length; k++)
                {
                    int a = order[k], b = order[k + 1];
                    var overlap = xs[a] + sizes[a].x * 0.5f + kSignGap - (xs[b] - sizes[b].x * 0.5f);
                    if (overlap <= 0.001f) continue;
                    xs[a] -= overlap * 0.5f;
                    xs[b] += overlap * 0.5f;
                    moved = true;
                }
                if (!moved) break;
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

        public static List<Vector2> Sizes(IEnumerable<BakedFace> faces, float scale = 1f) => faces.Select(f => new Vector2(f.WidthMeters, f.HeightMeters) * scale).ToList();

        /// <summary>
        /// Bakes, builds, registers, records and spawns an assembly. Yields while baking; the record is
        /// passed to <paramref name="onDone"/>, failures to <paramref name="onError"/>.
        /// </summary>
        public static IEnumerator Build(IReadOnlyList<JObject> designs, Func<IReadOnlyList<Vector2>, AssemblyPlacement> layout,
            Action<AssemblyRecord, AssemblyPlacement> onDone, Action<string> onError, PlacementContext context)
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
                placement = layout(Sizes(faces, context?.Options?.SignScale ?? 1f));
                record = new AssemblyRecord
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    Support = context.Options.Support,
                    Reverse = context.Options.Reverse,
                    Designs = designs.ToList(),
                    SupportSpec = placement.Spec,
                    SignCentres = placement.SignCentres.SelectMany(c => new[] { c.x, c.y, c.z }).ToArray(),
                    Context = context,
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
            var mesh = Merge(support, faces, record.Centres, record.Context?.Options?.SignScale ?? 1f);
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
            Core.AssetNames.Add(record.PrefabName); // again, now that the record (and its legend) is stored
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
        public static Mesh Merge(Mesh support, IReadOnlyList<BakedFace> faces, IReadOnlyList<Vector3> centres, float scale = 1f)
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
                var shape = PanelShape.Rects(faces[i].Texture);
                if (shape.Count > 1) Mod.log.Debug($"Sign {i} panel shape: {string.Join(", ", shape.Select(r => $"[{r.xMin:F2}-{r.xMax:F2} x {r.yMin:F2}-{r.yMax:F2}]"))}");
                var panel = GantryFactory.Panel(faces[i].WidthMeters * scale, faces[i].HeightMeters * scale, shape);
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

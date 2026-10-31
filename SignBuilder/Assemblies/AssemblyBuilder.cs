using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using SignBuilder.Core;
using SignBuilder.Rendering;
using Unity.Mathematics;
using UnityEngine;
using SignBuilder.Geometry;
using SignBuilder.Tools;

namespace SignBuilder.Assemblies
{
    /// <summary>Where and how an assembly sits in the world.</summary>
    public struct AssemblyPlacement
    {
        public float3 Origin;
        public quaternion Rotation;
        public SupportSpec Spec;
        public Mesh Support;
        public Vector3[] SignCenters; // assembly-local, one per sign
        /// <summary>Lanes the signs can be centered over (for the lane picker).</summary>
        public int LaneCount;
        // Structure-space X (drivers' right = +) of signs, faced lanes, posts, the structure and the deck,
        // for the arrangement view.
        public float[] SignXs;
        public float[] LaneXs;
        /// <summary>Lanes on the structure's back side, in those drivers' view (their right = -X).</summary>
        public float[] BackLaneXs;
        /// <summary>Per sign: mounted on the back, facing the other way.</summary>
        public bool[] SignBack;
        /// <summary>The carriageway the structure stands on, and every road alongside (x0, x1, included 0/1) in structure space.</summary>
        public int BaseRoad;
        public float3[] Roads;
        public float[] PostXs;
        public float2 StructureX;
        public float2 DeckX;
        /// <summary>Every lane of every road alongside: x in structure space, and +1 if its traffic faces the front, -1 the back.</summary>
        public float2[] AllLanes;
    }

    /// <summary>
    /// Builds a sign assembly: bakes its sign designs, lays out the chosen support for the road it was
    /// placed on, merges support and sign panels into one mesh (submesh 0 steel/aluminum, submesh
    /// i + 1 the face of sign i), creates its materials and prefab, records it and spawns it.
    /// </summary>
    public static class AssemblyBuilder
    {
        /// <summary>Structure types (C# ids used by the UI).</summary>
        public static readonly string[] Types = { "doubleTruss", "singleTruss", "curved", "straight", "ground", "surface" };
        /// <summary>A sign hung on an elevated road's side: its top this far above the road surface.</summary>
        private const float kDeckSignTop = 0.3f;
        private const float kSignGap = 0.3f;
        /// <summary>Road surface this far above the terrain counts as elevated (bridge, viaduct, embankment).</summary>
        private const float kElevatedDrop = 2f;
        /// <summary>Post center distance inside the deck edge on elevated roads (on the shoulder, inside the barrier).</summary>
        private const float kDeckInset = 0.7f;
        /// <summary>Lowest a roadside sign may sink: its bottom edge 0.2 m above the ground.</summary>
        public const float kMinGroundLift = 0.2f - GantryFactory.kGroundClearance;
        /// <summary>Lowest bottom edge for overhead signs (about 17.5 ft).</summary>
        private const float kMinClearance = 5.3f;

        /// <summary>
        /// Lays out a structure for signs of the given sizes (meters, x = width, y = height, already scaled)
        /// at a road snap (or a synthetic one for free placement). Each sign follows its slot in
        /// <paramref name="o"/>: front or back, side by side over the lanes, over one lane, or at an offset.
        /// Overlapping signs on the same side are pushed apart, and the structure is sized to carry them all.
        /// <paramref name="shapes"/> (each face's outline, UV rects) lets roadside posts stop at the sign's top edge.
        /// </summary>
        public static AssemblyPlacement Layout(RoadProbe.Snap s, IReadOnlyList<Vector2> sizes, LayoutOptions o, IReadOnlyList<List<Rect>> shapes = null)
        {
            var placement = LayoutSpec(s, sizes, o, shapes);
            placement.Support = GantryFactory.FromSpec(placement.Spec);
            return placement;
        }

        private static AssemblyPlacement LayoutSpec(RoadProbe.Snap s, IReadOnlyList<Vector2> sizes, LayoutOptions o, IReadOnlyList<List<Rect>> shapes)
        {
            var type = o.Type;
            var cantilever = !o.Standalone && o.Span == "cantilever";
            var n = sizes.Count;
            // Signs face the traffic of the clicked carriageway; reverse aims them at the other direction.
            var dir = s.MainDirection * (o.Reverse ? -1 : 1);
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));

            // The carriageway single spans and cantilevers stand on: the clicked one if it carries traffic the signs
            // face, else the nearest one that does (reversing on a divided highway picks the other carriageway).
            var faced = s.Lanes.Where(l => l.Direction == dir).ToList();
            var baseIndex = faced.Any(l => l.Carriageway == 0) || faced.Count == 0 ? 0
                : faced.OrderBy(l => math.abs(s.Carriageways[l.Carriageway].Offset)).First().Carriageway;
            var baseCw = s.Carriageways.Count > 0 ? s.Carriageways[baseIndex] : new RoadProbe.Carriageway { Width = 10f };
            var baseOffset = baseCw.Offset;
            var basePoint = s.Point + s.Right * baseOffset;
            var baseWidth = baseCw.Width;
            // Lanes per side, left to right as each side's drivers see them. Full spans cover every carriageway.
            // Spans cross their own carriageway plus the roads alongside that the player added.
            var roads = new HashSet<int> { baseIndex };
            if (!cantilever && !o.Standalone) foreach (var r in o.ExtraRoads) if (r >= 0 && r < s.Carriageways.Count) roads.Add(r);
            // A side whose roads carry no traffic its way (a one-way road seen from behind, or after swapping
            // directions) still lines its signs up over the lanes there.
            List<RoadProbe.LaneInfo> LanesFor(int d)
            {
                var own = s.Lanes.Where(l => l.Direction == d && roads.Contains(l.Carriageway)).ToList();
                if (own.Count == 0) own = s.Lanes.Where(l => roads.Contains(l.Carriageway)).ToList();
                return own.OrderBy(l => l.Offset * d).ToList();
            }
            var frontLanes = LanesFor(dir);
            var backLanes = LanesFor(-dir);
            bool IsBack(int i) => !o.Standalone && o.Slot(i).Back;
            var maxH = n > 0 ? sizes.Max(z => z.y) : 2f;

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

            // Sign x positions in structure space (+X = front drivers' right), measured from a reference lateral
            // offset (along Right). Each side is laid out in its own drivers' view, then mapped to structure space.
            float[] SignXs(float reference)
            {
                var xs = new float[n];
                foreach (var back in new[] { false, true })
                {
                    var d = back ? -dir : dir;
                    var lanes = back ? backLanes : frontLanes;
                    float View(float absolute) => (absolute - reference) * d;
                    var side = Enumerable.Range(0, n).Where(i => IsBack(i) == back).ToList();
                    if (side.Count == 0) continue;
                    var lanesCenter = lanes.Count > 0 ? lanes.Average(l => l.Offset) : reference;
                    var auto = side.Where(i => o.Slot(i).Mode == "auto" || (o.Slot(i).Mode == "lane" && lanes.Count == 0)).ToList();
                    var groupWidth = auto.Sum(i => sizes[i].x) + kSignGap * Math.Max(0, auto.Count - 1);
                    var view = new float[n];
                    var x = View(lanesCenter) - groupWidth * 0.5f;
                    foreach (var i in auto)
                    {
                        view[i] = x + sizes[i].x * 0.5f;
                        x += sizes[i].x + kSignGap;
                    }
                    foreach (var i in side)
                    {
                        var slot = o.Slot(i);
                        if (slot.Mode == "lane" && lanes.Count > 0)
                        {
                            var index = math.clamp(slot.FromRight ? lanes.Count - 1 - slot.Lane : slot.Lane, 0, lanes.Count - 1);
                            view[i] = View(lanes[index].Offset) + slot.Offset;
                        }
                        else if (slot.Mode == "free") view[i] = View(lanesCenter) + slot.Offset;
                        else view[i] += slot.Offset;
                    }
                    SpreadOut(view, sizes, side);
                    foreach (var i in side) xs[i] = back ? -view[i] : view[i];
                }
                return xs;
            }
            float[] LaneXs(List<RoadProbe.LaneInfo> lanes, float reference, int d) => lanes.Select(l => (l.Offset - reference) * d).ToArray();
            float Reach(float[] xs) => n == 0 ? 0f : Enumerable.Range(0, n).Max(i => math.abs(xs[i]) + sizes[i].x * 0.5f);
            float2 Edges(float reference, float center, float width)
            {
                var a = (center - width * 0.5f - reference) * dir;
                var b = (center + width * 0.5f - reference) * dir;
                return new float2(math.min(a, b), math.max(a, b));
            }

            // Mounting height and depth per structure type. Overhead signs are each centered on the beam (a short
            // sign beside a tall one still crosses it), raised only as far as their clearance needs; curved tube
            // structures carry their signs bolted in front of the horizontal pipe. Back signs mirror in Z.
            // Lowering the structure lowers the signs' minimum clearance with it (down to the road surface).
            var lift = type == "ground" ? 0f : math.max(o.Lift, -kMinClearance);
            // Roadside signs rise and sink with the lift too (their bottom edge stays above the ground).
            var groundLift = type == "ground" ? math.max(o.Lift, kMinGroundLift) : 0f;
            var clearance = kMinClearance + math.min(lift, 0f);
            var curvedHeight = math.max(7.0f + lift, clearance + maxH * 0.5f);
            var beamHeight = GantryFactory.kMountHeight + lift;
            Func<float, float> centerY = type == "curved" ? (h => curvedHeight) : (h => math.max(beamHeight, clearance + h * 0.5f));
            // Signs sit just in front of the member they are bolted to: its front surface, half the panel's
            // thickness and a few centimeters, so they never clip but do not float.
            const float mount = GantryFactory.kPanelThickness * 0.5f + 0.03f;
            var depth = type switch
            {
                "doubleTruss" => GantryFactory.kTrussDepth * 0.5f + GantryFactory.kChordRadius + mount,
                "singleTruss" => GantryFactory.kPlanarChordRadius + mount,
                "curved" => GantryFactory.kCurvedPipeRadius + mount,
                _ => (cantilever ? GantryFactory.kCantileverArmRadius : GantryFactory.kMonotubeRadius) + mount,
            };
            Vector3[] Centers(float[] xs) =>
                Enumerable.Range(0, n).Select(i => new Vector3(xs[i], centerY(sizes[i].y), IsBack(i) ? depth : -depth)).ToArray();
            // Clear distance from a post to the nearest sign edge. Signs may overlap a curved tube's bend a little
            // so they can still sit centered over the outermost lanes.
            var postClear = type switch { "curved" => 1.3f, "doubleTruss" => 1.1f, "singleTruss" => 0.6f, _ => 0.5f };

            AssemblyPlacement result;
            if (type == "surface")
            {
                // Just the sign, placed like a prop: at the cursor, turned by the placement angle, its bottom on the
                // clicked surface plus the base height. On an elevated road it hangs on the side instead, facing away
                // from the road, its top just below the deck (the base height moves it up or down).
                var total = sizes.Sum(z2 => z2.x) + kSignGap * Math.Max(0, n - 1);
                var outward = o.LeftSide ? -s.Right : s.Right;
                var origin = o.Free ? s.Point : s.Point + outward * (baseWidth * 0.5f + 0.06f);
                var facing = o.Free ? quaternion.LookRotationSafe(s.Tangent, new float3(0, 1, 0)) : quaternion.LookRotationSafe(-outward, new float3(0, 1, 0));
                var xs = new float[n];
                var x = -total * 0.5f;
                for (var i = 0; i < n; i++)
                {
                    xs[i] = x + sizes[i].x * 0.5f + o.Slot(i).Offset;
                    x += sizes[i].x + kSignGap;
                }
                var centers = Enumerable.Range(0, n).Select(i => new Vector3(xs[i], o.Free ? sizes[i].y * 0.5f : kDeckSignTop + o.Elevation - sizes[i].y * 0.5f, 0f)).ToArray();
                result = new AssemblyPlacement
                {
                    Origin = origin, Rotation = facing, Spec = SupportSpec.Of("none"),
                    SignCenters = centers, SignXs = xs, LaneXs = System.Array.Empty<float>(), PostXs = System.Array.Empty<float>(),
                    StructureX = new float2(-total * 0.5f, total * 0.5f),
                };
            }
            else if (type == "ground")
            {
                var total = sizes.Sum(z2 => z2.x) + kSignGap * Math.Max(0, n - 1);
                var lateral = o.Free ? 0f : dir * (baseWidth * 0.5f + o.RoadGap + total * 0.5f);
                var origin = basePoint + s.Right * lateral;
                // On a raised road the posts reach down to the terrain instead of floating.
                var drop = o.Free ? o.Elevation : math.clamp(origin.y - TerrainHeight(origin), 0f, 20f);
                var xs = new float[n];
                var x = -total * 0.5f;
                for (var i = 0; i < n; i++)
                {
                    xs[i] = x + sizes[i].x * 0.5f + o.Slot(i).Offset;
                    x += sizes[i].x + kSignGap;
                }
                var centers = Enumerable.Range(0, n).Select(i => new Vector3(xs[i], GantryFactory.kGroundClearance + groundLift + sizes[i].y * 0.5f, 0f)).ToArray();
                // Each post stops at the top of the sign in front of it: an exit tab only covers part of the width.
                var postXs = GantryFactory.GroundPostXs(total);
                var tops = postXs.Select(px => PostTop(px, xs, sizes, shapes) + groundLift).ToArray();
                result = new AssemblyPlacement
                {
                    Origin = origin, Rotation = rotation,
                    Spec = SupportSpec.Of("ground", new[] { total, maxH, drop }.Concat(tops).ToArray()),
                    SignCenters = centers, SignXs = xs, LaneXs = LaneXs(frontLanes, baseOffset + lateral, dir), PostXs = postXs,
                    StructureX = new float2(-total * 0.5f, total * 0.5f),
                };
            }
            else if (cantilever)
            {
                // Post just off the carriageway edge on the drivers' right (or left), arm reaching over the lanes
                // only as far as the signs need. Free placement puts the post at the cursor.
                var side = o.LeftSide ? -1f : 1f;
                var postOffset = o.Free ? 0f : dir * side * (baseWidth * 0.5f + math.max(o.RoadGap, 0.5f));
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
                for (var i = 0; i < n; i++)
                {
                    var limit = postClear + sizes[i].x * 0.5f;
                    xs[i] = o.LeftSide ? math.max(xs[i], limit) : math.min(xs[i], -limit);
                }
                var arm = math.max(Reach(xs) + 0.4f, postClear + 1f);
                var spec = type switch
                {
                    "curved" => SupportSpec.Of("curvedCantilever", math.max(arm, 3.5f), curvedHeight, ground),
                    "doubleTruss" => SupportSpec.Of("trussCantilever", arm, ground),
                    "singleTruss" => SupportSpec.Of("planarTrussCantilever", arm, ground),
                    _ => SupportSpec.Of("cantilever", -arm, ground),
                };
                spec.Mirror = o.LeftSide;
                spec.Lift = type == "curved" ? 0f : lift;
                result = new AssemblyPlacement
                {
                    Origin = post, Rotation = rotation, Spec = spec, SignCenters = Centers(xs), SignXs = xs,
                    LaneXs = LaneXs(frontLanes, reference, dir), BackLaneXs = LaneXs(backLanes, reference, -dir), PostXs = new[] { 0f },
                    StructureX = o.LeftSide ? new float2(-0.4f, arm) : new float2(-arm, 0.4f),
                    DeckX = Edges(reference, baseOffset, baseWidth),
                };
            }
            else
            {
                // The span crosses the chosen roads, centered on them.
                var lo = roads.Min(r => s.Carriageways.Count > 0 ? s.Carriageways[r].Offset - s.Carriageways[r].Width * 0.5f : -5f);
                var hi = roads.Max(r => s.Carriageways.Count > 0 ? s.Carriageways[r].Offset + s.Carriageways[r].Width * 0.5f : 5f);
                var reference = (lo + hi) * 0.5f;
                var deckWidth = hi - lo;
                var origin = s.Point + s.Right * reference;
                var xs = SignXs(reference);
                var needed = 2f * (Reach(xs) + postClear);
                var span = math.max(deckWidth + 2f * math.max(o.RoadGap, 0.5f), needed);
                if (OffDeck(origin - s.Right * span * 0.5f) || OffDeck(origin + s.Right * span * 0.5f))
                {
                    elevated = true;
                    span = math.max(deckWidth - 2f * kDeckInset, needed);
                }
                var ground = GroundUnder(origin - s.Right * span * 0.5f, origin + s.Right * span * 0.5f);
                var spec = type switch
                {
                    "curved" => SupportSpec.Of("curvedSpan", span, curvedHeight, ground),
                    "straight" => SupportSpec.Of("monotube", span, ground),
                    "singleTruss" => SupportSpec.Of("planarTruss", span, ground),
                    _ => SupportSpec.Of("truss", span, ground),
                };
                spec.Lift = type == "curved" ? 0f : lift;
                result = new AssemblyPlacement
                {
                    Origin = origin, Rotation = rotation, Spec = spec, SignCenters = Centers(xs), SignXs = xs,
                    LaneXs = LaneXs(frontLanes, reference, dir), BackLaneXs = LaneXs(backLanes, reference, -dir),
                    PostXs = new[] { -span * 0.5f, span * 0.5f },
                    StructureX = new float2(-span * 0.5f, span * 0.5f),
                    DeckX = Edges(reference, reference, deckWidth),
                };
            }
            result.SignBack = Enumerable.Range(0, n).Select(IsBack).ToArray();
            result.BaseRoad = baseIndex;
            // The roads the structure crosses, in structure space (roads clicked off again are left out).
            result.Roads = s.Carriageways.Select((c, i) =>
            {
                var originOffset = math.dot(result.Origin - s.Point, s.Right);
                var e = Edges(originOffset, c.Offset, c.Width);
                return new float3(e.x, e.y, roads.Contains(i) ? 1f : 0f);
            }).Where(r => r.z > 0.5f).ToArray();
            result.LaneCount = frontLanes.Count;
            var laneOrigin = math.dot(result.Origin - s.Point, s.Right);
            result.AllLanes = s.Lanes.Where(l => roads.Contains(l.Carriageway))
                .Select(l => new float2((l.Offset - laneOrigin) * dir, l.Direction == dir ? 1f : -1f)).ToArray();
            return result;
        }

        /// <summary>Height of a roadside post's top at x: the top edge of the sign face in front of it.</summary>
        private static float PostTop(float x, float[] xs, IReadOnlyList<Vector2> sizes, IReadOnlyList<List<Rect>> shapes)
        {
            for (var i = 0; i < xs.Length; i++)
            {
                var left = xs[i] - sizes[i].x * 0.5f;
                if (x < left || x > left + sizes[i].x) continue;
                var u = (x - left) / sizes[i].x;
                var top = 1f;
                if (shapes != null && i < shapes.Count && shapes[i] != null && shapes[i].Count > 0)
                {
                    top = 0f;
                    foreach (var r in shapes[i]) if (u >= r.xMin - 0.02f && u <= r.xMax + 0.02f) top = math.max(top, r.yMax);
                    if (top <= 0f) top = 1f;
                }
                return GantryFactory.kGroundClearance + sizes[i].y * top - 0.1f;
            }
            return GantryFactory.kGroundClearance + sizes.DefaultIfEmpty(Vector2.one).Max(z => z.y) - 0.1f;
        }

        /// <summary>Pushes overlapping signs (of one side) apart symmetrically, keeping their order, at least the sign gap apart.</summary>
        private static void SpreadOut(float[] xs, IReadOnlyList<Vector2> sizes, IReadOnlyList<int> indices)
        {
            if (indices.Count < 2) return;
            var order = indices.OrderBy(i => xs[i]).ToArray();
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

        /// <summary>Each face's outline (UV rectangles), from its texture's alpha.</summary>
        public static List<List<Rect>> Shapes(IEnumerable<BakedFace> faces) => faces.Select(f => f == null ? null : PanelShape.Rects(f.Texture)).ToList();

        public static List<Vector2> Sizes(IEnumerable<BakedFace> faces, float scale = 1f) => faces.Select(f => new Vector2(f.WidthMeters, f.HeightMeters) * scale).ToList();

        /// <summary>
        /// Bakes, builds, registers, records and spawns an assembly. Yields while baking; the record is
        /// passed to <paramref name="onDone"/>, failures to <paramref name="onError"/>.
        /// </summary>
        public static IEnumerator Build(IReadOnlyList<JObject> designs, Func<IReadOnlyList<Vector2>, IReadOnlyList<List<Rect>>, AssemblyPlacement> layout,
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
                placement = layout(Sizes(faces, context?.Options?.SignScale ?? 1f), Shapes(faces));
                record = new AssemblyRecord
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    Support = context.Options.Support,
                    Reverse = context.Options.Reverse,
                    Designs = designs.Select(Formats.StampDesign).ToList(),
                    SupportSpec = placement.Spec,
                    SignCenters = placement.SignCenters.SelectMany(c => new[] { c.x, c.y, c.z }).ToArray(),
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
                SignBuilderRuntime.Mark("build: placing the object");
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
            SignBuilderRuntime.Mark("build: meshes");
            var options = record.Context?.Options;
            var back = Enumerable.Range(0, faces.Count).Select(i => options != null && !options.Standalone && options.Slot(i).Back).ToArray();
            var lights = options != null && options.Lights;
            var mesh = Merge(support, faces, record.Centers, options?.SignScale ?? 1f, back, lights);
            // The distant model: the same structure with far fewer sides on every tube.
            var lowSupport = GantryFactory.FromSpec(record.SupportSpec, lowDetail: true);
            var lowMesh = Merge(lowSupport, faces, record.Centers, options?.SignScale ?? 1f, back, lights, lowDetail: true);
            UnityEngine.Object.Destroy(lowSupport);
            var db = SignBuilderRuntime.Database;
            var donor = SignBuilderRuntime.Donor(prefabs);
            var steel = SignBuilderRuntime.Steel(prefabs);
            var surfaces = new List<Colossal.IO.AssetDatabase.SurfaceAsset> { steel };
            SignBuilderRuntime.Mark("build: materials");
            // Identical faces (here or on another structure) share one material.
            var made = new Dictionary<string, Colossal.IO.AssetDatabase.SurfaceAsset>();
            foreach (var f in faces)
            {
                if (!made.TryGetValue(f.Key, out var surface))
                {
                    surface = AssemblyLibrary.FaceSurface(f.Key) ?? SurfaceFactory.SignFace(db, SignBuilderRuntime.SurfaceFolder, donor, f.Texture, f.Key, f.Glow);
                    made[f.Key] = surface;
                }
                surfaces.Add(surface);
            }
            record.FaceKeys = faces.Select(f => f.Key).ToList();
            var bounds = new Bounds3(mesh.bounds.min, mesh.bounds.max);
            var indexCount = 0;
            for (var i = 0; i < mesh.subMeshCount; i++) indexCount += (int)mesh.GetIndexCount(i);
            SignBuilderRuntime.Mark("build: saving geometry");
            var geometry = RuntimePrefabs.SaveGeometry(mesh, db, SignBuilderRuntime.MeshFolder, record.PrefabName);
            record.GeometryId = geometry.id.guid.ToString();
            var lowGeometry = RuntimePrefabs.SaveGeometry(lowMesh, db, SignBuilderRuntime.MeshFolder, record.PrefabName + "_LOD1");
            record.LodGeometryId = lowGeometry.id.guid.ToString();
            record.LodIndexCount = 0;
            for (var i = 0; i < lowMesh.subMeshCount; i++) record.LodIndexCount += (int)lowMesh.GetIndexCount(i);
            record.LodVertexCount = lowMesh.vertexCount;
            record.SurfaceIds = surfaces.Select(x => x.id.guid.ToString()).ToList();
            record.Bounds = new[] { bounds.min.x, bounds.min.y, bounds.min.z, bounds.max.x, bounds.max.y, bounds.max.z };
            record.IndexCount = indexCount;
            record.VertexCount = mesh.vertexCount;
            record.LightSpots = lights ? LightSpots(faces, record.Centers, options.SignScale, back) : null;
            // Submesh 0 is the steel; sign i's face is submesh i + 1.
            var glowing = Enumerable.Range(0, faces.Count).Where(i => faces[i].Glow != null).Select(i => i + 1).ToArray();
            record.GlowMeshes = glowing.Length > 0 ? glowing : null;
            SignBuilderRuntime.Mark("build: registering the prefab");
            var prefab = prefabs.Register(record.PrefabName, geometry, surfaces.ToArray(), bounds, indexCount, mesh.vertexCount,
                (lowGeometry, record.LodIndexCount, record.LodVertexCount), record.LightSpots, record.GlowMeshes);
            UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(lowMesh);
            AssemblyLibrary.Add(record);
            Core.AssetNames.Add(record.PrefabName); // again, now that the record (and its legend) is stored
            AssemblyBootstrapSystem.TrackNewPrefab(prefab);
        }

        /// <summary>Every light fixture's position and aim in assembly space (signs on the back are turned around).</summary>
        private static float[] LightSpots(IReadOnlyList<BakedFace> faces, IReadOnlyList<Vector3> centers, float scale, IReadOnlyList<bool> back)
        {
            var spots = new List<float>();
            for (var i = 0; i < faces.Count; i++)
            {
                var turned = back != null && i < back.Count && back[i];
                Vector3 Turn(Vector3 v) => turned ? new Vector3(-v.x, v.y, -v.z) : v;
                var aim = Turn(GantryFactory.kLightAim);
                foreach (var head in GantryFactory.LightHeads(faces[i].WidthMeters * scale, faces[i].HeightMeters * scale))
                {
                    var p = centers[i] + Turn(head);
                    spots.AddRange(new[] { p.x, p.y, p.z, aim.x, aim.y, aim.z });
                }
            }
            return spots.ToArray();
        }

        /// <summary>
        /// Re-creates an assembly from the recipe stored in a save (designs, support spec, sign positions),
        /// for cities shared from another PC. The prefab gets the same name, so the save's objects match it.
        /// </summary>
        public static IEnumerator Rebuild(AssemblyRecord record, Action<bool> onDone)
        {
            if (record.SupportSpec == null || record.SignCenters == null || record.Designs.Count == 0)
            {
                Mod.log.Warn($"{record.PrefabName}: no recipe in the save, cannot rebuild");
                onDone(false);
                yield break;
            }
            List<BakedFace> faces = null;
            yield return BakeAll(record.Designs, f => faces = f);
            if (faces == null || faces.Count != record.Centers.Length)
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
        /// edges) at <paramref name="centers"/>: output submesh 0 is everything steel/aluminum,
        /// submesh i + 1 the face of sign i. <paramref name="lights"/> adds light fixtures under each sign.
        /// </summary>
        public static Mesh Merge(Mesh support, IReadOnlyList<BakedFace> faces, IReadOnlyList<Vector3> centers, float scale = 1f, IReadOnlyList<bool> back = null,
            bool lights = false, bool lowDetail = false)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var submeshes = new List<List<int>> { new List<int>() };

            void Append(Mesh m, Vector3 offset, int submesh, List<int> into, bool turned = false)
            {
                // Turned: rotated 180 degrees about Y (a sign on the structure's back faces the other way).
                var start = vertices.Count;
                foreach (var p in m.vertices) vertices.Add((turned ? new Vector3(-p.x, p.y, -p.z) : p) + offset);
                foreach (var nrm in m.normals) normals.Add(turned ? new Vector3(-nrm.x, nrm.y, -nrm.z) : nrm);
                uvs.AddRange(m.uv);
                foreach (var i in m.GetTriangles(submesh)) into.Add(start + i);
            }

            Append(support, Vector3.zero, 0, submeshes[0]);
            for (var i = 0; i < faces.Count; i++)
            {
                var shape = PanelShape.Rects(faces[i].Texture);
                if (shape.Count > 1) Mod.log.Debug($"Sign {i} panel shape: {string.Join(", ", shape.Select(r => $"[{r.xMin:F2}-{r.xMax:F2} x {r.yMin:F2}-{r.yMax:F2}]"))}");
                var panel = GantryFactory.Panel(faces[i].WidthMeters * scale, faces[i].HeightMeters * scale, shape);
                var turned = back != null && i < back.Count && back[i];
                Append(panel, centers[i], 1, submeshes[0], turned);
                if (lights)
                {
                    MeshBuilder.LowDetail = lowDetail;
                    var fixtures = GantryFactory.SignLights(faces[i].WidthMeters * scale, faces[i].HeightMeters * scale);
                    MeshBuilder.LowDetail = false;
                    Append(fixtures, centers[i], 0, submeshes[0], turned);
                    UnityEngine.Object.Destroy(fixtures);
                }
                var face = new List<int>();
                Append(panel, centers[i], 0, face, turned);
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

using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace HighwaySigns.Spike
{
    /// <summary>
    /// Finds a road edge near a point, reads its geometry and car lanes, and pairs it with a parallel
    /// one-way edge when the highway is divided. Everything here is read-only.
    /// </summary>
    internal sealed class RoadProbe
    {
        public struct LaneInfo
        {
            public float Offset;     // metres to the right of the edge centre line (relative to edge tangent)
            public int Direction;    // +1 = traffic along the edge tangent, -1 = against it
            public float Width;
            public bool OnPairedEdge;
        }

        public sealed class Snap
        {
            public Entity Edge;
            public string PrefabName;
            public float EdgeLength;
            public float EdgeWidth;
            public float T;
            public float3 Point;
            public float3 Tangent;        // flat, normalised
            public float3 Right;          // flat, normalised, right of Tangent
            public Entity PairedEdge;
            public float PairedOffset;    // paired edge centre, metres along Right
            public float PairedWidth;
            public readonly List<LaneInfo> Lanes = new();

            /// <summary>Lateral extent (min, max along Right) of all carriageways covered.</summary>
            public float2 Extent => PairedEdge == Entity.Null
                ? new float2(-EdgeWidth * 0.5f, EdgeWidth * 0.5f)
                : new float2(math.min(-EdgeWidth * 0.5f, PairedOffset - PairedWidth * 0.5f), math.max(EdgeWidth * 0.5f, PairedOffset + PairedWidth * 0.5f));

            /// <summary>Direction most lanes of the main edge flow (+1 along Tangent, -1 against).</summary>
            public int MainDirection => Lanes.Where(l => !l.OnPairedEdge).Sum(l => l.Direction) >= 0 ? 1 : -1;

            public override string ToString() =>
                $"{PrefabName} len={EdgeLength:F0} width={EdgeWidth:F1} t={T:F2} at {Point} tan={Tangent} paired={(PairedEdge != Entity.Null ? $"offset {PairedOffset:F1} width {PairedWidth:F1}" : "none")} extent={Extent}";
        }

        private readonly EntityManager m_EM;
        private readonly PrefabSystem m_PrefabSystem;

        public RoadProbe(EntityManager em, PrefabSystem prefabSystem)
        {
            m_EM = em;
            m_PrefabSystem = prefabSystem;
        }

        private struct EdgeCandidate
        {
            public Entity Entity;
            public Curve Curve;
            public float Width;
            public string Name;
        }

        private List<EdgeCandidate> RoadEdges()
        {
            using var query = m_EM.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Composition>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            var list = new List<EdgeCandidate>();
            foreach (var e in entities)
            {
                var prefab = m_EM.GetComponentData<PrefabRef>(e).m_Prefab;
                if (!m_EM.HasComponent<RoadData>(prefab)) continue;
                var composition = m_EM.GetComponentData<Composition>(e).m_Edge;
                if (!m_EM.HasComponent<NetCompositionData>(composition)) continue;
                list.Add(new EdgeCandidate
                {
                    Entity = e,
                    Curve = m_EM.GetComponentData<Curve>(e),
                    Width = m_EM.GetComponentData<NetCompositionData>(composition).m_Width,
                    Name = m_PrefabSystem.TryGetPrefab(prefab, out PrefabBase p) ? p.name : prefab.ToString(),
                });
            }
            return list;
        }

        /// <summary>Picks the best road edge near <paramref name="near"/>: highways first, then the widest/longest.</summary>
        public Snap FindNear(float3 near, Action<string> log)
        {
            var edges = RoadEdges();
            log($"roadEdges={edges.Count}");
            if (edges.Count == 0) return null;

            var scored = edges
                .Select(e => (e, dist: MathUtils.Distance(e.Curve.m_Bezier.xz, near.xz, out _)))
                .OrderByDescending(x => x.e.Name.IndexOf("Highway", StringComparison.OrdinalIgnoreCase) >= 0 && x.e.Curve.m_Length > 60f)
                .ThenBy(x => x.dist)
                .ToList();
            log("nearestEdges=" + string.Join(" | ", scored.Take(6).Select(x => $"{x.e.Name} d={x.dist:F0} len={x.e.Curve.m_Length:F0} w={x.e.Width:F1}")));
            return SnapAt(scored[0].e, 0.5f, edges);
        }

        /// <summary>Snaps to <paramref name="edge"/> at curve parameter <paramref name="t"/>, finds lanes and a paired carriageway.</summary>
        public Snap SnapAt(Entity edge, float t)
        {
            var all = RoadEdges();
            return SnapAt(all.First(e => e.Entity == edge), t, all);
        }

        private Snap SnapAt(EdgeCandidate edge, float t, List<EdgeCandidate> all)
        {
            var bezier = edge.Curve.m_Bezier;
            var point = MathUtils.Position(bezier, t);
            var tangent = MathUtils.Tangent(bezier, t);
            tangent.y = 0;
            tangent = math.normalizesafe(tangent, new float3(0, 0, 1));
            var right = new float3(tangent.z, 0, -tangent.x);

            var snap = new Snap
            {
                Edge = edge.Entity,
                PrefabName = edge.Name,
                EdgeLength = edge.Curve.m_Length,
                EdgeWidth = edge.Width,
                T = t,
                Point = point,
                Tangent = tangent,
                Right = right,
            };

            // Divided highway: nearest other road edge running parallel within 45 m.
            var best = float.MaxValue;
            foreach (var other in all)
            {
                if (other.Entity == edge.Entity) continue;
                var d = MathUtils.Distance(other.Curve.m_Bezier.xz, point.xz, out var ot);
                if (d > 45f || d >= best) continue;
                var otherTangent = MathUtils.Tangent(other.Curve.m_Bezier, ot);
                otherTangent.y = 0;
                if (math.abs(math.dot(math.normalizesafe(otherTangent), tangent)) < 0.95f) continue;
                var otherPoint = MathUtils.Position(other.Curve.m_Bezier, ot);
                var offset = math.dot(otherPoint - point, right);
                if (math.abs(offset) < (edge.Width + other.Width) * 0.45f) continue; // same carriageway continuation
                best = d;
                snap.PairedEdge = other.Entity;
                snap.PairedOffset = offset;
                snap.PairedWidth = other.Width;
            }

            AddLanes(snap, edge.Entity, false);
            if (snap.PairedEdge != Entity.Null) AddLanes(snap, snap.PairedEdge, true);
            return snap;
        }

        private void AddLanes(Snap snap, Entity edge, bool paired)
        {
            if (!m_EM.HasBuffer<Game.Net.SubLane>(edge)) return;
            var subLanes = m_EM.GetBuffer<Game.Net.SubLane>(edge, true);
            for (var i = 0; i < subLanes.Length; i++)
            {
                var lane = subLanes[i].m_SubLane;
                // Master lanes are virtual groupings; the real drivable lanes are their slaves.
                if (!m_EM.HasComponent<Game.Net.CarLane>(lane) || m_EM.HasComponent<MasterLane>(lane) || !m_EM.HasComponent<Curve>(lane)) continue;
                var curve = m_EM.GetComponentData<Curve>(lane).m_Bezier;
                MathUtils.Distance(curve.xz, snap.Point.xz, out var lt);
                var pos = MathUtils.Position(curve, lt);
                var laneTangent = MathUtils.Tangent(curve, lt);
                var prefab = m_EM.GetComponentData<PrefabRef>(lane).m_Prefab;
                snap.Lanes.Add(new LaneInfo
                {
                    Offset = math.dot(pos - snap.Point, snap.Right),
                    Direction = math.dot(laneTangent, snap.Tangent) >= 0 ? 1 : -1,
                    Width = m_EM.HasComponent<NetLaneData>(prefab) ? m_EM.GetComponentData<NetLaneData>(prefab).m_Width : 0f,
                    OnPairedEdge = paired,
                });
            }
            snap.Lanes.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }
    }
}

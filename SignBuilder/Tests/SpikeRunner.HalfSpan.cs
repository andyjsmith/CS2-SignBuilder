using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Net;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Systems;
using SignBuilder.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace SignBuilder.Tests
{
    /// <summary>
    /// Half-span gantries (part of "ui-e2e"): picked in the arrangement panel on a two-way road, on a one-way road alone
    /// and with its other carriageway added. Reports where the posts stand relative to the road's center and edges,
    /// checks that the road gap moves only the outer post, builds them and takes pictures.
    /// </summary>
    internal sealed partial class SpikeRunner
    {
        private IEnumerator TestHalfSpan(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var probe = new RoadProbe(em, m_Runtime.PrefabSystem);
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var (twoWay, oneWay, paired) = HalfSpanRoads(em, probe, near);
            SpikeReport.Set("halfspan.roads", $"twoWay={Describe(probe, twoWay)} oneWay={Describe(probe, oneWay)} paired={(paired != Entity.Null)}");
            var failures = new List<string>();
            var built = new List<AssemblyRecord>();
            void Check(bool ok, string what) { if (!ok) failures.Add(what); }
            var designs = new List<JObject> { Template("guide-exit-tab"), Template("guide-exit-only") };
            ui.SetEditorOpen(true);
            yield return new WaitForSecondsRealtime(1f);

            // Two-way road: picked with the panel's button; the inner post at the center line whatever the road gap.
            if (twoWay.edge != Entity.Null)
            {
                tool.ArrangeForTest(twoWay.edge, twoWay.t, designs, Options("doubleTruss", "single"));
                yield return WaitArrange(tool, ui);
                yield return new WaitForSecondsRealtime(1f);
                SpikeReport.Set("halfspan.twoWay.full", PostsReport(Arranged(ui)));
                yield return Ask(ui, ClickText + "return { clicked: clickText('Half span') };", "halfspan.twoWay.click");
                yield return WaitSpan(ui, "half");
                var a = Arranged(ui);
                Check((string)a?["span"] == "half", "two-way: the Half span button did not select it");
                var first = Posts(a);
                SpikeReport.Set("halfspan.twoWay.half", PostsReport(a));
                yield return Ask(ui, "var t = document.body.textContent; return { halfSpan: t.indexOf('Half span') >= 0, fullSpan: t.indexOf('Full span') >= 0, postSide: t.indexOf('Post left') >= 0 };", "halfspan.twoWay.ui");
                SpikeReport.Screenshot("halfspan_twoway_panel.png");
                yield return new WaitForSecondsRealtime(0.5f);
                Check(math.abs(first.innerFromCenter) < 0.05f, $"two-way: inner post {first.innerFromCenter:F2} m from the center line");
                Check(first.outerBeyondEdge >= 2.45f, $"two-way: outer post {first.outerBeyondEdge:F2} m beyond the edge (gap 2.5)");
                Check(first.signsClear, "two-way: a sign overlaps a post or leaves the span");
                Check(first.outerSide > 0f, "two-way: the span is not over the faced (drivers' right) lanes");
                foreach (var gap in new[] { 5f, 0.5f })
                {
                    tool.Arrange(new JObject { ["op"] = "set", ["field"] = "gap", ["value"] = gap }.ToString());
                    yield return new WaitForSecondsRealtime(1f);
                    var moved = Posts(Arranged(ui));
                    SpikeReport.Set($"halfspan.twoWay.gap{gap:0.#}", PostsReport(Arranged(ui)));
                    Check(math.abs(moved.innerFromCenter - first.innerFromCenter) < 0.02f, $"two-way: gap {gap} moved the inner post");
                    // The outer post at the gap, or farther out where it must clear the outermost sign.
                    var atGap = math.abs(moved.outerBeyondEdge - gap) < 0.05f;
                    Check(atGap || (moved.outerBeyondEdge > gap && math.abs(moved.outerClear - 1.1f) < 0.05f), $"two-way: gap {gap} put the outer post {moved.outerBeyondEdge:F2} m out ({moved.outerClear:F2} m from the signs)");
                }
                tool.Arrange(new JObject { ["op"] = "set", ["field"] = "gap", ["value"] = 2.5f }.ToString());
                yield return new WaitForSecondsRealtime(0.5f);
                yield return BuildHalf(tool, probe, twoWay, "twoWay", "center", built, Check);
                tool.Deactivate();
            }
            else failures.Add("no two-way road");

            // One-way road alone: no center between directions, so the inner post stands at its far edge; the side
            // choice picks the outer post's side. Then its other carriageway added: the inner post in the median.
            if (oneWay.edge != Entity.Null)
            {
                var o = Options("doubleTruss", "single");
                o.Span = "half";
                tool.ArrangeForTest(oneWay.edge, oneWay.t, designs, o);
                yield return WaitArrange(tool, ui);
                yield return new WaitForSecondsRealtime(1f);
                var alone = Posts(Arranged(ui));
                SpikeReport.Set("halfspan.oneWay.alone", PostsReport(Arranged(ui)));
                yield return Ask(ui, "var t = document.body.textContent; return { postSide: t.indexOf('Post left') >= 0 };", "halfspan.oneWay.ui");
                SpikeReport.Screenshot("halfspan_oneway_panel.png");
                yield return new WaitForSecondsRealtime(0.5f);
                Check(math.abs(alone.innerFromEdge) < 0.05f, $"one-way: inner post {alone.innerFromEdge:F2} m from the far edge");
                Check(alone.outerSide > 0f && alone.outerBeyondEdge >= 2.45f, $"one-way: outer post side {alone.outerSide} at {alone.outerBeyondEdge:F2} m");
                Check(alone.signsClear, "one-way: a sign overlaps a post or leaves the span");
                tool.Arrange("{\"op\":\"side\",\"value\":\"left\"}");
                yield return new WaitForSecondsRealtime(1f);
                var left = Posts(Arranged(ui));
                SpikeReport.Set("halfspan.oneWay.left", PostsReport(Arranged(ui)));
                Check(left.outerSide < 0f && math.abs(left.innerFromEdge) < 0.05f, "one-way: post left did not mirror the span");
                tool.Arrange("{\"op\":\"side\",\"value\":\"right\"}");
                yield return new WaitForSecondsRealtime(0.5f);

                if (paired != Entity.Null)
                {
                    var road = tool.AddRoadForTest(paired);
                    yield return new WaitForSecondsRealtime(1f);
                    var both = Posts(Arranged(ui));
                    SpikeReport.Set("halfspan.divided", $"road={road} {PostsReport(Arranged(ui))}");
                    yield return Ask(ui, "var t = document.body.textContent; return { postSide: t.indexOf('Post left') >= 0 };", "halfspan.divided.ui");
                    SpikeReport.Screenshot("halfspan_divided_panel.png");
                    yield return new WaitForSecondsRealtime(0.5f);
                    Check(road > 0, "divided: the other carriageway was not added");
                    Check(math.abs(both.innerFromCenter) < 0.05f, $"divided: inner post {both.innerFromCenter:F2} m from the center line");
                    Check(both.outerSide > 0f && both.outerBeyondEdge >= 2.45f, $"divided: outer post side {both.outerSide} at {both.outerBeyondEdge:F2} m");
                    Check(both.signsClear, "divided: a sign overlaps a post or leaves the span");
                    yield return BuildHalf(tool, probe, oneWay, "divided", "median", built, Check);
                }
                else
                {
                    SpikeReport.Set("halfspan.divided", "no paired carriageway");
                    yield return BuildHalf(tool, probe, oneWay, "oneWay", "edge", built, Check);
                }
                tool.Deactivate();
            }
            else failures.Add("no one-way road");
            ui.SetEditorOpen(false);

            // Every structure built here goes again, so the test city keeps no half spans.
            foreach (var r in built) foreach (var e in InstancesOf(r.PrefabName)) m_Runtime.Delete(e);
            for (var i = 0; i < 5; i++) yield return null;
            SpikeReport.Set("halfspan.removed", AssemblyCleanup.Remove(built));
            SpikeReport.Set("halfspan.status", failures.Count == 0 ? "ok" : "FAILED: " + string.Join("; ", failures));
        }

        /// <summary>Builds the arranged half span and reports its posts in the world, relative to the clicked road.</summary>
        /// <summary><paramref name="inner"/>: where the inner post should stand in the clicked road's drivers' view
        /// ("center", "edge" = its left edge, "median" = beyond that edge).</summary>
        private IEnumerator BuildHalf(SignPlacementToolSystem tool, RoadProbe probe, (Entity edge, float t) spot, string name, string inner, List<AssemblyRecord> built, Action<bool, string> check)
        {
            var before = SignPlacementToolSystem.LastPlaced;
            yield return PlaceAndWait(() => tool.Arrange("{\"op\":\"build\"}"));
            var r = SignPlacementToolSystem.LastPlaced;
            if (r == before) { check(false, $"{name}: not built"); yield break; }
            built.Add(r);
            var entity = InstancesOf(r.PrefabName).FirstOrDefault();
            var snap = probe.SnapAt(spot.edge, spot.t);
            var world = "no instance";
            if (entity != Entity.Null && snap != null)
            {
                var transform = World.DefaultGameObjectInjectionWorld.EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                var right = math.mul(transform.m_Rotation, new float3(1, 0, 0));
                var span = r.SupportSpec?.Args?.Length > 0 ? r.SupportSpec.Args[0] : 0f;
                float Across(float3 p) => math.dot(p - snap.Point, snap.Right);
                var a = Across(transform.m_Position - right * span * 0.5f);
                var b = Across(transform.m_Position + right * span * 0.5f);
                world = $"posts along Right from the clicked road's center line: {math.min(a, b):F2} {math.max(a, b):F2} (road edges ±{snap.EdgeWidth * 0.5f:F2})";
                // In the clicked road's drivers' view: the outer post beyond its right edge, the inner one as expected.
                var d = snap.MainDirection;
                var outerView = math.max(a * d, b * d);
                var innerView = math.min(a * d, b * d);
                var half = snap.EdgeWidth * 0.5f;
                check(outerView - half >= 2.45f, $"{name}: built outer post {outerView - half:F2} m beyond the clicked road's edge");
                check(inner == "center" ? math.abs(innerView) < 0.05f : inner == "edge" ? math.abs(innerView + half) < 0.05f : innerView < -half - 0.5f,
                    $"{name}: built inner post at {innerView:F2} (expected at the {inner})");
            }
            check(r.Context?.Options?.Span == "half", $"{name}: the record's span is {r.Context?.Options?.Span}");
            SpikeReport.Set($"halfspan.{name}.built", $"{world} {Describe(r)}");
            yield return Shoot(r, $"halfspan_{name}.png", 15f, 50f);
            yield return Shoot(r, $"halfspan_{name}_front.png", 0f, 40f);
        }

        private struct HalfPosts
        {
            /// <summary>The inner post from the center of the roads it crosses, and from their nearer edge.</summary>
            public float innerFromCenter, innerFromEdge;
            /// <summary>How far the outer post stands beyond the roads' edge, and its side (+1 = drivers' right).</summary>
            public float outerBeyondEdge, outerSide;
            /// <summary>Every sign between the posts, clear of both; the outer post's distance from the nearest sign edge.</summary>
            public bool signsClear;
            public float outerClear;
        }

        /// <summary>The arrangement's posts (in the drivers' view) relative to its deck: the crossed roads.</summary>
        private static HalfPosts Posts(JObject a)
        {
            if (a == null) return new HalfPosts { innerFromCenter = float.NaN, innerFromEdge = float.NaN, outerBeyondEdge = float.NaN };
            var deck = a["deck"].Select(x => (float)x).ToArray();
            var posts = a["posts"].Select(x => (float)x).ToArray();
            var center = (deck[0] + deck[1]) * 0.5f;
            var halfWidth = (deck[1] - deck[0]) * 0.5f;
            var outer = posts.OrderByDescending(p => math.abs(p - center)).First();
            var inner = posts.First(p => p != outer);
            var side = math.sign(outer - inner);
            var outerClear = a["signs"].Select(sg => (outer - ((float)sg["x"] + (float)sg["w"] * 0.5f * side)) * side).DefaultIfEmpty(99f).Min();
            var clear = a["signs"].All(sg =>
            {
                float x = (float)sg["x"], w = (float)sg["w"];
                // Both edges of the sign inside the posts, by at least the smallest post clearance (less a margin).
                var lo = side > 0f ? x - w * 0.5f : x + w * 0.5f;
                var hi = side > 0f ? x + w * 0.5f : x - w * 0.5f;
                return (lo - inner) * side >= 0.45f && (outer - hi) * side >= 0.45f;
            });
            return new HalfPosts
            {
                innerFromCenter = inner - center, innerFromEdge = math.abs(inner - center) - halfWidth,
                outerBeyondEdge = math.abs(outer - center) - halfWidth, outerSide = math.sign(outer - center), signsClear = clear && posts.Length == 2, outerClear = outerClear,
            };
        }

        private static string PostsReport(JObject a)
        {
            if (a == null) return "(no arrangement)";
            var p = Posts(a);
            var signs = string.Join(" ", a["signs"].Select(sg => $"[{(float)sg["x"] - (float)sg["w"] * 0.5f:F2}..{(float)sg["x"] + (float)sg["w"] * 0.5f:F2}]"));
            return $"span={a["span"]} side={a["side"]} gap={a["gap"]} deck=[{(float)a["deck"][0]:F2},{(float)a["deck"][1]:F2}] posts=[{string.Join(",", a["posts"].Select(x => ((float)x).ToString("F2")))}] " +
                $"innerFromCenter={p.innerFromCenter:F2} innerFromEdge={p.innerFromEdge:F2} outerBeyondEdge={p.outerBeyondEdge:F2} outerSide={p.outerSide} signsClear={p.signsClear} signs={signs} lanes=[{string.Join(" ", a["lanes"].Select(l => ((float)l).ToString("F1")))}]";
        }

        private static JObject Arranged(SignBuilderUISystem ui) => string.IsNullOrEmpty(ui.ArrangeInfoJson) ? null : JObject.Parse(ui.ArrangeInfoJson);

        private static IEnumerator WaitSpan(SignBuilderUISystem ui, string span)
        {
            var end = Time.realtimeSinceStartup + 3f;
            while ((string)Arranged(ui)?["span"] != span && Time.realtimeSinceStartup < end) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
        }

        private static string Describe(RoadProbe probe, (Entity edge, float t) spot)
        {
            if (spot.edge == Entity.Null) return "none";
            var s = probe.SnapAt(spot.edge, spot.t);
            return s == null ? "?" : $"{s.PrefabName}(w={s.EdgeWidth:F1} lanes={string.Join(",", s.Lanes.Select(l => $"{l.Offset:F1}{(l.Direction > 0 ? "+" : "-")}"))})";
        }

        /// <summary>
        /// A two-way road and a one-way highway carriageway (with its other carriageway, when there is one) near the
        /// camera, on the ground and long enough for a structure, highways first.
        /// </summary>
        private ((Entity edge, float t) twoWay, (Entity edge, float t) oneWay, Entity paired) HalfSpanRoads(EntityManager em, RoadProbe probe, float3 near)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Composition>());
            using var edges = query.ToEntityArray(Allocator.Temp);
            var roads = new List<(Entity e, Curve c, string name, float d)>();
            foreach (var e in edges)
            {
                var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                if (!em.HasComponent<RoadData>(prefab) || em.HasComponent<Game.Common.Deleted>(e) || em.HasComponent<Game.Tools.Temp>(e)) continue;
                var curve = em.GetComponentData<Curve>(e);
                var name = m_Runtime.PrefabSystem.TryGetPrefab(prefab, out PrefabBase p) ? p.name : "";
                roads.Add((e, curve, name, Colossal.Mathematics.MathUtils.Distance(curve.m_Bezier.xz, near.xz, out _)));
            }
            (Entity, float) twoWay = default, oneWay = default;
            var paired = Entity.Null;
            foreach (var c in roads.Where(r => r.c.m_Length >= 60f).OrderByDescending(r => r.name.Contains("Highway")).ThenBy(r => r.d).Take(300))
            {
                var mid = Colossal.Mathematics.MathUtils.Position(c.c.m_Bezier, 0.5f);
                if (mid.y - AssemblyBuilder.TerrainHeight(mid) > 1.5f) continue; // no bridges: posts stand beside the road
                var snap = probe.SnapAt(c.e, 0.5f);
                if (snap == null || snap.Lanes.Count == 0) continue;
                var directions = snap.Lanes.Select(l => l.Direction).Distinct().Count();
                if (directions == 2 && twoWay.Item1 == Entity.Null) twoWay = (c.e, 0.5f);
                if (directions == 1 && (oneWay.Item1 == Entity.Null || paired == Entity.Null) && c.name.Contains("Highway"))
                {
                    // The other carriageway: the nearest one-way road alongside within 60 m carrying the other direction.
                    var mine = snap.Lanes[0].Direction;
                    var best = float.MaxValue;
                    foreach (var other in roads.Where(r => r.e != c.e && Colossal.Mathematics.MathUtils.Distance(r.c.m_Bezier.xz, snap.Point.xz, out _) < 60f))
                    {
                        var probeSnap = probe.SnapAt(c.e, 0.5f);
                        var index = probe.AddRoad(probeSnap, other.e, 60f);
                        var lanes = index > 0 ? probeSnap.Lanes.Where(l => l.Carriageway == index).ToList() : new List<RoadProbe.LaneInfo>();
                        if (lanes.Count == 0 || lanes.Any(l => l.Direction == mine)) continue;
                        var distance = math.abs(probeSnap.Carriageways[index].Offset);
                        if (distance < best) { best = distance; paired = other.e; }
                    }
                    if (oneWay.Item1 == Entity.Null || paired != Entity.Null) oneWay = (c.e, 0.5f);
                }
                if (twoWay.Item1 != Entity.Null && oneWay.Item1 != Entity.Null && paired != Entity.Null) break;
            }
            return (twoWay, oneWay, paired);
        }
    }
}

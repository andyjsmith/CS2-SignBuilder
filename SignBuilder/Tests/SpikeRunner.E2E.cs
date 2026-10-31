using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Colossal.Entities;
using Colossal.IO.AssetDatabase;
using Game.Assets;
using Game.Net;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Menu;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Core;
using SignBuilder.Systems;
using SignBuilder.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using SignBuilder.Geometry;

namespace SignBuilder.Tests
{
    /// <summary>
    /// End-to-end run of the real feature code: every structure type and span, the gantry arrangement flow,
    /// editing placed structures, templates, then a save for the fresh-boot reload check ("reload" mode).
    /// </summary>
    internal sealed partial class SpikeRunner
    {
        private const string kE2ESave = "SignBuilder_E2E";

        private static readonly (string type, string span, bool left, string template)[] kPlacements =
        {
            ("doubleTruss", "full", false, "guide-exit-tab"), ("doubleTruss", "single", false, "guide-exit-only"), ("doubleTruss", "cantilever", false, "guide-exit-tab"),
            ("singleTruss", "full", false, "guide-exit-tab"), ("singleTruss", "single", false, "services-blue"), ("singleTruss", "cantilever", true, "guide-exit-only"),
            ("curved", "full", false, "guide-exit-only"), ("curved", "single", false, "guide-exit-tab"), ("curved", "cantilever", false, "attraction-brown"),
            ("straight", "full", false, "services-blue"), ("straight", "single", false, "guide-exit-tab"), ("straight", "cantilever", true, "advance-guide"),
            ("ground", "single", false, "gore-exit"),
        };

        private readonly List<(string support, AssemblyRecord record, Entity edge, float t)> m_Placed = new();

        private static string Key(string type, string span, bool left) => type == "ground" ? "ground" : $"{type}-{span}{(left ? "-left" : "")}";

        /// <summary>UI only (mode "ui-e2e"): the designer's responsiveness, without placing anything. A couple of minutes.</summary>
        private IEnumerator RunUIE2E()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            var ui = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SignBuilderUISystem>();
            yield return Test("e2e.editor", TestEditorOpens(ui));
            yield return Test("e2e.ui", TestDesignerPicks(ui));
            yield return Test("e2e.uipause", TestDesignerPause(ui));
            ui.SetEditorOpen(false);
            Finish();
        }

        /// <summary>
        /// LED glow only (mode "led-e2e"): an LED board on a gantry and a lit roadside LED sign, with their lights and
        /// effects by day and at night. A few minutes.
        /// </summary>
        private IEnumerator RunLedE2E()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(em, near, 2);
            if (spots.Count < 2) { SpikeReport.Set("led.status", "FAILED: no highway"); Finish(); yield break; }
            yield return PlaceAndWait(() => tool.PlaceForTest(spots[0].edge, spots[0].t, new List<JObject> { Template("lane-split"), Template("message-board") }, Options("doubleTruss", "single")));
            var board = SignPlacementToolSystem.LastPlaced;
            var litOptions = Options("ground", "single");
            litOptions.Lights = true;
            yield return PlaceAndWait(() => tool.PlaceForTest(spots[1].edge, spots[1].t, new List<JObject> { Template("message-board") }, litOptions));
            var lit = SignPlacementToolSystem.LastPlaced;
            string State(AssemblyRecord record)
            {
                var entity = InstancesOf(record.PrefabName).FirstOrDefault();
                if (entity == Entity.Null) return "no instance";
                var states = em.HasBuffer<Game.Rendering.LightState>(entity) ? string.Join(",", em.GetBuffer<Game.Rendering.LightState>(entity).AsNativeArray().Select(l => l.m_Intensity.ToString("F2"))) : "none";
                var emissive = em.HasBuffer<Game.Rendering.Emissive>(entity);
                var effects = "none";
                if (em.HasBuffer<Game.Effects.EnabledEffect>(entity))
                {
                    var control = world.GetExistingSystemManaged<Game.Effects.EffectControlSystem>();
                    var data = control.GetEnabledData(true, out var deps);
                    deps.Complete();
                    effects = string.Join(",", em.GetBuffer<Game.Effects.EnabledEffect>(entity).AsNativeArray().Select(e =>
                        $"#{e.m_EffectIndex}:{(e.m_EnabledIndex >= 0 && e.m_EnabledIndex < data.Length ? data[e.m_EnabledIndex].m_Flags.ToString() : "?")}"));
                }
                return $"glowMeshes=[{string.Join(",", record.GlowMeshes ?? new int[0])}] emissive={emissive} states=[{states}] effects=[{effects}]";
            }
            yield return Shoot(board, "led_day.png", 10f, 25f);
            SpikeReport.Set("led.day.board", State(board));
            SpikeReport.Set("led.day.lit", State(lit));
            var planet = world.GetOrCreateSystemManaged<Game.Simulation.PlanetarySystem>();
            planet.overrideTime = true;
            planet.normalizedTime = 0.02f;
            SetEffectNight(true);
            for (var i = 0; i < 4; i++)
            {
                yield return new WaitForSecondsRealtime(1.5f);
                SetEffectNight(true);
                SpikeReport.Set($"led.night{i}.board", State(board));
            }
            yield return Shoot(board, "led_night_board.png", 10f, 25f);
            SpikeReport.Set("led.night.board", State(board));
            SetEffectNight(true);
            yield return Shoot(lit, "led_night_lit.png", 15f, 20f);
            SpikeReport.Set("led.night.lit", State(lit));
            SetEffectNight(false);
            planet.overrideTime = false;
            SpikeReport.Set("led.status", "ok");
            Finish();
        }

        private IEnumerator RunE2E()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            var world = World.DefaultGameObjectInjectionWorld;
            var ui = world.GetOrCreateSystemManaged<SignBuilderUISystem>();
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();

            if (m_Mode == "reload-e2e")
            {
                yield return Test("reload", ReloadChecks());
                RemoveAllAssemblies();
                yield return new WaitForSecondsRealtime(2f);
            }
            yield return Test("e2e.editor", TestEditorOpens(ui));
            yield return Test("e2e.place", TestPlaceAll(tool));
            yield return Test("e2e.shots", TestAssemblyShots());
            yield return Test("e2e.elevated", TestElevated(tool));
            yield return Test("e2e.arrange", TestArrange(ui, tool));
            yield return Test("e2e.editgantry", TestEditGantry(ui, tool));
            yield return Test("e2e.editposts", TestEditPosts(ui, tool));
            yield return Test("e2e.infopanel", TestInfoPanel(ui, tool));
            yield return Test("e2e.free", TestFreePlacement(tool));
            yield return Test("e2e.standalone", TestStandalone(tool));
            yield return Test("e2e.features", TestFeatures(tool));
            yield return Test("e2e.perf", TestPerformance(ui, tool));
            yield return Test("e2e.templates", TestTemplates(ui));
            yield return Test("e2e.themes", TestThemes(ui, tool));
            yield return Test("e2e.uipause", TestDesignerPause(ui));
            yield return Test("e2e.save", TestSaveForReload());
            ui.SetEditorOpen(false);
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator TestEditorOpens(SignBuilderUISystem ui)
        {
            ui.SetEditorOpen(true);
            yield return new WaitForSecondsRealtime(4f);
            SpikeReport.Set("e2e.editor.open", ui.EditorOpen);
            SpikeReport.Screenshot("e2e_editor.png");
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// <summary>Test shorthand: "full" = a span over every road alongside, "single" = a span over its own road.</summary>
        private static LayoutOptions Options(string type, string span, bool left = false) => new LayoutOptions
        {
            Type = type, Span = span == "cantilever" ? "cantilever" : "span", LeftSide = left,
            ExtraRoads = span == "full" ? Enumerable.Range(0, 12).ToList() : new List<int>(),
        };

        /// <summary>One structure per type x span (plus left-hand cantilevers and roadside posts), each on its own stretch of highway.</summary>
        private IEnumerator TestPlaceAll(SignPlacementToolSystem tool)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(em, near, kPlacements.Length);
            SpikeReport.Set("e2e.place.spots", spots.Count);
            for (var i = 0; i < kPlacements.Length && i < spots.Count; i++)
            {
                var (type, span, left, template) = kPlacements[i];
                var key = Key(type, span, left);
                var designs = type == "doubleTruss" && span == "full"
                    ? new List<JObject> { Template(template), Template("guide-exit-only") } // multi-sign layout
                    : new List<JObject> { Template(template) };
                var before = SignPlacementToolSystem.LastPlaced;
                var start = Time.realtimeSinceStartup;
                tool.PlaceForTest(spots[i].edge, spots[i].t, designs, Options(type, span, left));
                while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 25f) yield return null;
                var record = SignPlacementToolSystem.LastPlaced;
                if (record == before)
                {
                    SpikeReport.Set($"e2e.place.{key}", $"FAILED: {world.GetOrCreateSystemManaged<SignBuilderUISystem>().Status}");
                    continue;
                }
                m_Placed.Add((key, record, spots[i].edge, spots[i].t));
                SpikeReport.Set($"e2e.place.{key}", $"{record.PrefabName} {Describe(record)}");
            }
            yield return new WaitForSecondsRealtime(5f);
            foreach (var p in m_Placed)
            {
                var entities = InstancesOf(p.record.PrefabName);
                var batches = entities.Count(e => em.HasBuffer<Game.Rendering.MeshBatch>(e) && em.GetBuffer<Game.Rendering.MeshBatch>(e, true).Length > 0);
                SpikeReport.Set($"e2e.instances.{p.support}", $"count={entities.Count} batches={batches}");
            }
        }

        private IEnumerator TestAssemblyShots()
        {
            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SignBuilderUISystem>().SetEditorOpen(false);
            foreach (var p in m_Placed) yield return Shoot(p.record, $"e2e_{p.support}.png", 15f, p.support == "ground" ? 30f : 50f);
        }

        /// <summary>Overhead structures on an elevated deck: posts must stand on the deck.</summary>
        private IEnumerator TestElevated(SignPlacementToolSystem tool)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Composition>());
            using var edges = query.ToEntityArray(Allocator.Temp);
            var spots = new List<(Entity edge, float t)>();
            var used = new List<float3>();
            foreach (var e in edges)
            {
                if (!em.HasComponent<RoadData>(em.GetComponentData<PrefabRef>(e).m_Prefab)) continue;
                var curve = em.GetComponentData<Curve>(e);
                if (curve.m_Length < 50f) continue;
                var p = Colossal.Mathematics.MathUtils.Position(curve.m_Bezier, 0.5f);
                if (p.y - AssemblyBuilder.TerrainHeight(p) < 8f || used.Any(u => math.distance(u, p) < 50f)) continue;
                used.Add(p);
                spots.Add((e, 0.5f));
                if (spots.Count >= 3) break;
            }
            SpikeReport.Set("e2e.elevated.spots", spots.Count);
            var cases = new[] { Options("singleTruss", "single"), Options("curved", "cantilever"), Options("curved", "single") };
            for (var i = 0; i < spots.Count && i < cases.Length; i++)
            {
                // A sign over the outermost lane: it must stay centered over it (overlapping the bend if needed).
                cases[i].Signs.Add(new SignSlot { Mode = "lane", Lane = 0, FromRight = true });
                yield return PlaceAndWait(() => tool.PlaceForTest(spots[i].edge, spots[i].t, new List<JObject> { Template("guide-exit-only") }, cases[i]));
                var r = SignPlacementToolSystem.LastPlaced;
                var snap = r.Context.ToSnap();
                var placement = AssemblyBuilder.Layout(snap, new List<Vector2> { new Vector2(r.Centers.Length > 0 ? 3f : 3f, 2f) }, r.Context.Options);
                UnityEngine.Object.Destroy(placement.Support);
                SpikeReport.Set($"e2e.elevated.{cases[i].Type}-{cases[i].Span}", $"{Describe(r)} lanes=[{string.Join(" ", placement.LaneXs.Select(x => x.ToString("F1")))}]");
                yield return Shoot(r, $"e2e_elevated_{cases[i].Type}_{cases[i].Span}.png", 35f, 40f);
            }
            // A prop sign hung on an elevated road's side.
            if (spots.Count > 0)
            {
                var side = Options("surface", "single");
                yield return PlaceAndWait(() => tool.PlaceForTest(spots[0].edge, math.clamp(spots[0].t + 0.2f, 0.05f, 0.95f), new List<JObject> { Template("services-blue") }, side));
                SpikeReport.Set("e2e.elevated.prop", Describe(SignPlacementToolSystem.LastPlaced));
                yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_elevated_prop.png", 60f, 25f);
            }
        }

        private string Arrangement(SignBuilderUISystem ui)
        {
            var json = ui.ArrangeInfoJson;
            if (string.IsNullOrEmpty(json)) return "(none)";
            var a = JObject.Parse(json);
            var signs = string.Join(" ", a["signs"].Select(sg => $"[x={(float)sg["x"]:F2} w={(float)sg["w"]:F2} {sg["mode"]} lane={sg["lane"]}{((bool)sg["fromRight"] ? "R" : "L")} off={(float)sg["offset"]:F2}]"));
            return $"type={a["type"]} span={a["span"]} side={a["side"]} lanes=[{string.Join(" ", a["lanes"].Select(l => ((float)l).ToString("F1")))}] structure=[{a["structure"][0]:F1},{a["structure"][1]:F1}] signs={signs}";
        }

        private IEnumerator WaitArrange(SignPlacementToolSystem tool, SignBuilderUISystem ui)
        {
            var end = Time.realtimeSinceStartup + 15f;
            while ((!tool.PreviewReady || string.IsNullOrEmpty(ui.ArrangeInfoJson)) && Time.realtimeSinceStartup < end) yield return null;
            for (var i = 0; i < 5; i++) yield return null;
        }

        /// <summary>
        /// The gantry flow: pin on a road with two signs, drag one onto the rightmost lane (snaps, remembered from
        /// the right), add a third sign, switch to a single-truss left cantilever and back, then build.
        /// </summary>
        private IEnumerator TestArrange(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            var spot = m_Placed.FirstOrDefault(p => p.support == "doubleTruss-single");
            if (spot.record == null) yield break;
            var t = math.clamp(spot.t + 0.3f, 0.05f, 0.95f);
            ui.SetEditorOpen(true);
            tool.ArrangeForTest(spot.edge, t, new List<JObject> { Template("guide-exit-tab"), Template("guide-exit-only") }, Options("doubleTruss", "single"));
            yield return WaitArrange(tool, ui);
            SpikeReport.Set("e2e.arrange.start", $"mode={ui.ToolMode} {Arrangement(ui)}");
            var snapForCamera = new RoadProbe(World.DefaultGameObjectInjectionWorld.EntityManager, m_Runtime.PrefabSystem).SnapAt(spot.edge, t);
            var fwd = snapForCamera.Tangent * snapForCamera.MainDirection;
            SetCamera(snapForCamera.Point + new float3(0, 5f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + 180f, 10f, 45f);
            yield return new WaitForSecondsRealtime(2.5f);
            SpikeReport.Screenshot("e2e_arrange_start.png");
            yield return new WaitForSecondsRealtime(0.5f);

            var info = JObject.Parse(ui.ArrangeInfoJson);
            var lanes = info["lanes"].Select(l => (float)l).ToArray();
            if (lanes.Length > 0)
            {
                tool.Arrange(new JObject { ["op"] = "moveSign", ["index"] = 1, ["x"] = lanes[^1] + 0.3f, ["snap"] = true }.ToString());
                tool.Arrange(new JObject { ["op"] = "moveSign", ["index"] = 0, ["x"] = lanes[0] - 0.2f, ["snap"] = true }.ToString());
            }
            if (lanes.Length == 3)
            {
                tool.Arrange(new JObject { ["op"] = "moveSign", ["index"] = 1, ["x"] = lanes[1], ["snap"] = true }.ToString());
                yield return null;
                SpikeReport.Set("e2e.arrange.middleLane", Arrangement(ui));
                tool.Arrange(new JObject { ["op"] = "moveSign", ["index"] = 1, ["x"] = lanes[^1], ["snap"] = true }.ToString());
            }
            tool.Arrange(new JObject { ["op"] = "addSign", ["design"] = Template("services-blue") }.ToString());
            yield return WaitArrange(tool, ui);
            yield return new WaitForSecondsRealtime(0.5f);
            SpikeReport.Set("e2e.arrange.dragged", Arrangement(ui));
            SpikeReport.Screenshot("e2e_arrange_dragged.png");
            yield return new WaitForSecondsRealtime(0.5f);

            tool.Arrange("{\"op\":\"type\",\"value\":\"singleTruss\"}");
            tool.Arrange("{\"op\":\"span\",\"value\":\"cantilever\"}");
            tool.Arrange("{\"op\":\"side\",\"value\":\"left\"}");
            yield return new WaitForSecondsRealtime(1f);
            SpikeReport.Set("e2e.arrange.leftCantilever", Arrangement(ui));
            SpikeReport.Screenshot("e2e_arrange_left.png");
            yield return new WaitForSecondsRealtime(0.5f);
            tool.Arrange("{\"op\":\"span\",\"value\":\"span\"}");
            tool.Arrange("{\"op\":\"side\",\"value\":\"right\"}");
            tool.Arrange("{\"op\":\"removeSign\",\"index\":2}");
            // Full span across every parallel carriageway, raised 1 m, with a sign on the back for the other direction.
            tool.Arrange("{\"op\":\"span\",\"value\":\"span\"}");
            tool.Arrange("{\"op\":\"addRoads\"}");
            tool.Arrange("{\"op\":\"lift\",\"value\":1}");
            tool.Arrange("{\"op\":\"view\",\"value\":\"back\"}");
            tool.Arrange(new JObject { ["op"] = "addSign", ["design"] = Template("guide-exit-only") }.ToString());
            yield return WaitArrange(tool, ui);
            yield return new WaitForSecondsRealtime(1f);
            SpikeReport.Set("e2e.arrange.back", Arrangement(ui));
            SpikeReport.Set("e2e.arrange.carriageways", new RoadProbe(World.DefaultGameObjectInjectionWorld.EntityManager, m_Runtime.PrefabSystem).SnapAt(spot.edge, t).Carriageways.Count);
            SpikeReport.Screenshot("e2e_arrange_back.png");
            yield return new WaitForSecondsRealtime(0.5f);
            tool.Arrange("{\"op\":\"view\",\"value\":\"front\"}");
            yield return new WaitForSecondsRealtime(1f);
            // Duplicate a sign (it lands beside the original), and lights under every sign.
            tool.Arrange("{\"op\":\"duplicateSign\",\"index\":0}");
            tool.Arrange("{\"op\":\"lights\",\"value\":true}");
            tool.Arrange("{\"op\":\"swap\"}");
            yield return new WaitForSecondsRealtime(1f);
            var swapped = JObject.Parse(tool.ArrangeInfo());
            SpikeReport.Set("e2e.arrange.swapped", $"reverse={swapped["reverse"]} lanes={swapped["lanes"]?.Count()} allLanes={swapped["allLanes"]?.Count()}");
            SpikeReport.Screenshot("e2e_arrange_swapped.png");
            yield return new WaitForSecondsRealtime(0.5f);
            tool.Arrange("{\"op\":\"swap\"}");
            yield return WaitArrange(tool, ui);
            yield return new WaitForSecondsRealtime(1f);
            var all = JObject.Parse(ui.ArrangeInfoJson);
            SpikeReport.Set("e2e.arrange.duplicate", $"total={all["total"]} allLanes={all["allLanes"]?.Count()} dirty={all["dirty"]} {Arrangement(ui)}");
            SpikeReport.Screenshot("e2e_arrange_duplicate.png");
            yield return new WaitForSecondsRealtime(0.5f);

            var before = SignPlacementToolSystem.LastPlaced;
            tool.Arrange("{\"op\":\"build\"}");
            var start = Time.realtimeSinceStartup;
            while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 20f) yield return null;
            yield return new WaitForSecondsRealtime(2f);
            var built = SignPlacementToolSystem.LastPlaced;
            SpikeReport.Set("e2e.arrange.built", built == before ? "FAILED: " + ui.Status : $"{built.PrefabName} {Describe(built)} mode={ui.ToolMode}");
            if (built != before)
            {
                m_Placed.Add(("arranged", built, spot.edge, t));
                yield return Shoot(built, "e2e_arrange_built.png", 180f, 40f);
            }
        }

        /// <summary>Info panel Edit on a gantry: back in the arrangement pinned in place; replace a sign and Save.</summary>
        private IEnumerator TestEditGantry(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            var target = m_Placed.FirstOrDefault(p => p.support == "arranged");
            if (target.record == null) yield break;
            var entity = InstancesOf(target.record.PrefabName).FirstOrDefault();
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var pos = em.GetComponentData<Game.Objects.Transform>(entity).m_Position;
            tool.EditPlaced(entity);
            yield return WaitArrange(tool, ui);
            yield return new WaitForSecondsRealtime(1f);
            SpikeReport.Set("e2e.editgantry.open", $"mode={ui.ToolMode} replacing={tool.IsReplacing} hidden={tool.ReplacingHidden} signs={tool.SignCount} {Arrangement(ui)}");
            SpikeReport.Screenshot("e2e_editgantry.png");
            yield return new WaitForSecondsRealtime(0.5f);
            var edited = (JObject)target.record.Designs[0].DeepClone();
            edited["items"].First(i => (string)i["type"] == "text")["text"] = "Edited In Place";
            tool.Arrange(new JObject { ["op"] = "replaceSign", ["index"] = 0, ["design"] = edited }.ToString());
            yield return WaitArrange(tool, ui);
            var before = SignPlacementToolSystem.LastPlaced;
            tool.Arrange("{\"op\":\"build\"}");
            var start = Time.realtimeSinceStartup;
            while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 20f) yield return null;
            yield return new WaitForSecondsRealtime(2.5f);
            var saved = SignPlacementToolSystem.LastPlaced;
            var newEntity = saved == before ? Entity.Null : InstancesOf(saved.PrefabName).FirstOrDefault();
            SpikeReport.Set("e2e.editgantry.saved", saved == before ? "FAILED: " + ui.Status
                : $"old remaining={InstancesOf(target.record.PrefabName).Count} moved={(newEntity == Entity.Null ? -1 : math.distance(em.GetComponentData<Game.Objects.Transform>(newEntity).m_Position, pos)):F2}m mode={ui.ToolMode}");
            yield return new WaitForSecondsRealtime(2f);
            SpikeReport.Set("e2e.editgantry.cleanup", $"old record kept={AssemblyLibrary.FromPrefabName(target.record.PrefabName) != null}");
            if (saved != before) yield return Shoot(saved, "e2e_editgantry_saved.png", 180f, 40f);
        }

        /// <summary>Edit a roadside sign: designer Save rebuilds it where it stands.</summary>
        private IEnumerator TestEditPosts(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            var target = m_Placed.FirstOrDefault(p => p.support == "ground");
            if (target.record == null) yield break;
            var entity = InstancesOf(target.record.PrefabName).FirstOrDefault();
            tool.EditPlaced(entity);
            yield return new WaitForSecondsRealtime(2f);
            SpikeReport.Set("e2e.editposts.open", $"editor={ui.EditorOpen} mode={ui.ToolMode} replacing={tool.IsReplacing}");
            SpikeReport.Screenshot("e2e_editposts.png");
            yield return new WaitForSecondsRealtime(0.5f);
            var edited = (JObject)target.record.Designs[0].DeepClone();
            edited["scheme"] = "blue";
            var before = SignPlacementToolSystem.LastPlaced;
            tool.SavePosts(edited, lights: true);
            var start = Time.realtimeSinceStartup;
            while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 20f) yield return null;
            yield return new WaitForSecondsRealtime(2f);
            var saved = SignPlacementToolSystem.LastPlaced;
            SpikeReport.Set("e2e.editposts.saved", saved == before ? "FAILED: " + ui.Status : $"old remaining={InstancesOf(target.record.PrefabName).Count} new={InstancesOf(saved.PrefabName).Count} lights={(AssemblyLibrary.FromPrefabName(saved.PrefabName)?.LightSpots?.Length ?? 0) / 6}");
            if (saved != before)
            {
                m_LitRoadside = saved;
                yield return Shoot(saved, "e2e_editposts_saved.png", 15f, 25f);
            }
            ui.SetEditorOpen(false);
        }

        /// <summary>Selected-object panel: name, and its Move / Duplicate / Edit buttons.</summary>
        private IEnumerator TestInfoPanel(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var target = m_Placed.FirstOrDefault(p => p.support == "straight-single");
            if (target.record == null) yield break;
            var entity = InstancesOf(target.record.PrefabName).FirstOrDefault();
            LookAt(entity);
            var info = world.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>();
            var section = world.GetOrCreateSystemManaged<SignInfoSection>();
            info.SetSelection(entity);
            yield return new WaitForSecondsRealtime(2.5f);
            GameManager.instance.localizationManager.activeDictionary.TryGetValue($"Assets.NAME[{target.record.PrefabName}]", out var displayName);
            GameManager.instance.localizationManager.activeDictionary.TryGetValue($"Assets.DESCRIPTION[{target.record.PrefabName}]", out var description);
            SpikeReport.Set("e2e.infopanel.name", $"visible={section.visible} {displayName} | {description}");
            SpikeReport.Screenshot("e2e_infopanel.png");
            yield return new WaitForSecondsRealtime(0.5f);
            section.PlaceSelectedAgain(move: true);
            yield return new WaitForSecondsRealtime(2f);
            SpikeReport.Set("e2e.infopanel.move", $"mode={ui.ToolMode} replacing={tool.IsReplacing} signs={tool.SignCount}");
            SpikeReport.Screenshot("e2e_infopanel_move.png");
            yield return new WaitForSecondsRealtime(0.5f);
            tool.Deactivate();
            info.SetSelection(entity);
            yield return new WaitForSecondsRealtime(1f);
            section.PlaceSelectedAgain(move: false);
            yield return new WaitForSecondsRealtime(1.5f);
            SpikeReport.Set("e2e.infopanel.duplicate", $"mode={ui.ToolMode} replacing={tool.IsReplacing}");
            tool.Deactivate();
            ui.SetEditorOpen(false);
        }

        /// <summary>Free placement on open ground: a rotated single truss and a raised roadside sign.</summary>
        private IEnumerator TestFreePlacement(SignPlacementToolSystem tool)
        {
            if (m_Placed.Count == 0) yield break;
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var first = InstancesOf(m_Placed[0].record.PrefabName).FirstOrDefault();
            var tr = em.GetComponentData<Game.Objects.Transform>(first);
            var right = math.mul(tr.m_Rotation, new float3(1, 0, 0));
            var p = tr.m_Position + right * 45f;
            p.y = AssemblyBuilder.TerrainHeight(p);
            var options = Options("singleTruss", "full");
            options.Angle = 30f;
            options.FreeWidth = 12f;
            yield return PlaceAndWait(() => tool.PlaceFreeForTest(p, new List<JObject> { Template("guide-exit-tab") }, options));
            SpikeReport.Set("e2e.free.truss", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_free_truss.png", 20f, 50f);
            var q = p + right * 25f;
            q.y = AssemblyBuilder.TerrainHeight(q);
            var posts = Options("ground", "single");
            posts.Elevation = 1.5f;
            yield return PlaceAndWait(() => tool.PlaceFreeForTest(q, new List<JObject> { Template("gore-exit") }, posts));
            SpikeReport.Set("e2e.free.ground", Describe(SignPlacementToolSystem.LastPlaced));
        }

        /// <summary>Standalone signs: an exit-tab sign on two posts (posts stop under the tab edge) and a short sign at least a lane wide.</summary>
        private IEnumerator TestStandalone(SignPlacementToolSystem tool)
        {
            var spot = m_Placed.FirstOrDefault(p => p.support == "straight-full");
            if (spot.record == null) yield break;
            var t = math.clamp(spot.t + 0.3f, 0.05f, 0.95f);
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, t, new List<JObject> { Template("guide-exit-tab") }, Options("ground", "single")));
            SpikeReport.Set("e2e.standalone.tab", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_standalone_tab.png", 25f, 22f);
            var small = new JObject { ["scheme"] = "green", ["minWidth"] = 213, ["items"] = new JArray(new JObject { ["type"] = "text", ["text"] = "EXIT", ["height"] = 16 }) };
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, math.clamp(t - 0.15f, 0.05f, 0.95f), new List<JObject> { small }, Options("ground", "single")));
            SpikeReport.Set("e2e.standalone.minWidth", Describe(SignPlacementToolSystem.LastPlaced));
        }

        /// <summary>
        /// Frame time spikes: the slowest frame (ms) in the two seconds after selecting a placed gantry, opening a
        /// roadside sign for editing, and opening the designer; plus an idle baseline.
        /// </summary>
        private IEnumerator TestPerformance(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var results = new List<string>();
            float worst = 0f;
            IEnumerator Watch(float seconds)
            {
                worst = 0f;
                var end = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < end)
                {
                    yield return null;
                    worst = Math.Max(worst, Time.unscaledDeltaTime * 1000f);
                }
            }
            yield return Watch(2f);
            results.Add($"idle={worst:F0}");

            var gantry = m_Placed.FirstOrDefault(p => p.support == "straight-single");
            var info = world.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>();
            if (gantry.record != null)
            {
                info.SetSelection(InstancesOf(gantry.record.PrefabName).FirstOrDefault());
                yield return Watch(2f);
                results.Add($"select={worst:F0}");
                info.SetSelection(Entity.Null);
                yield return new WaitForSecondsRealtime(1f);
                info.SetSelection(InstancesOf(gantry.record.PrefabName).FirstOrDefault());
                yield return Watch(2f);
                results.Add($"selectAgain={worst:F0}");
                info.SetSelection(Entity.Null);
                yield return new WaitForSecondsRealtime(1f);
                // A vanilla object for comparison: is the spike the game's selection panel or our sign?
                using (var trees = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Objects.Tree>(), ComponentType.ReadOnly<Game.Objects.Transform>()))
                using (var any = trees.ToEntityArray(Unity.Collections.Allocator.Temp))
                {
                    if (any.Length > 0)
                    {
                        info.SetSelection(any[0]);
                        yield return Watch(2f);
                        results.Add($"selectVanillaTree={worst:F0}");
                        info.SetSelection(Entity.Null);
                        yield return new WaitForSecondsRealtime(1f);
                    }
                }
            }
            // Any roadside sign still standing (the edit test replaced the first one).
            var posts = AssemblyLibrary.All.Where(r => r.Context?.Options?.Type == "ground").Select(r => InstancesOf(r.PrefabName).FirstOrDefault()).FirstOrDefault(e => e != Entity.Null);
            {
                var entity = posts;
                if (entity != Entity.Null)
                {
                    // Twice: a first-time cost shows as a difference. "Cs" is the C# part alone.
                    for (var k = 0; k < 2; k++)
                    {
                        var cs = System.Diagnostics.Stopwatch.StartNew();
                        tool.EditPlaced(entity);
                        results.Add($"editRoadsideCs{k}={cs.ElapsedMilliseconds}");
                        yield return Watch(2.5f);
                        results.Add($"editRoadside{k}={worst:F0}");
                        tool.Deactivate();
                        ui.SetEditorOpen(false);
                        yield return new WaitForSecondsRealtime(1f);
                    }
                }
            }
            ui.SetEditorOpen(true);
            yield return Watch(2.5f);
            results.Add($"openDesigner={worst:F0}");
            yield return Watch(2f);
            results.Add($"designerIdle={worst:F0}");
            ui.SetEditorOpen(false);
            SpikeReport.Set("e2e.perf.worstFrameMs", string.Join(" ", results));
            yield return TestDesignerPicks(ui);
        }

        /// <summary>
        /// The slowest frame (ms) after selecting parts in the designer (the properties panel changes: text, panel,
        /// arrow, nothing), after a quiet baseline with the design loaded; and after opening the designer.
        /// </summary>
        private IEnumerator TestDesignerPicks(SignBuilderUISystem ui)
        {
            var results = new List<string>();
            float worst = 0f;
            IEnumerator Watch(float seconds)
            {
                worst = 0f;
                var end = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < end)
                {
                    yield return null;
                    worst = Math.Max(worst, Time.unscaledDeltaTime * 1000f);
                }
            }
            ui.SetEditorOpen(false);
            yield return new WaitForSecondsRealtime(1f);
            // The designer as it opens for a placed roadside sign (UI only, no tool), twice: a first-time cost shows.
            for (var k = 0; k < 2; k++)
            {
                ui.OpenForEdit(new AssemblyRecord { Designs = new List<JObject> { Template("gore-exit") } }, "posts", true);
                yield return Watch(2f);
                results.Add($"postsEdit{k}={worst:F0}");
                ui.SetEditorOpen(false);
                yield return new WaitForSecondsRealtime(1f);
            }
            ui.SetEditorOpen(true);
            yield return Watch(2f);
            results.Add($"open={worst:F0}");
            var design = Template("guide-exit-only");
            ui.OpenForEdit(new AssemblyRecord { Designs = new List<JObject> { design } }, "new");
            for (var k = 0; k < 3; k++)
            {
                yield return Watch(1.2f);
                results.Add($"loaded{k}={worst:F0}");
            }
            var page = GameManager.instance.userInterface.view.View;
            foreach (var type in new[] { "text", "panel", "text", "arrow", "none", "arrow", "panel", "none", "text", "panel", "arrow", "text" })
            {
                var path = type == "none" ? null : PathOf(design, type);
                if (type != "none" && path == null) continue;
                page.ExecuteScript($"window.__signBuilderSelect && window.__signBuilderSelect({(path == null ? "null" : "\"" + path + "\"")});");
                yield return Watch(1.2f);
                results.Add($"{type}={worst:F0}");
            }
            ui.SetEditorOpen(false);
            SpikeReport.Set("e2e.ui.picksWorstFrameMs", string.Join(" ", results));
        }

        /// <summary>
        /// Newer placement options: a roadside sign close to the road, a free-placed one lowered below the default,
        /// a gantry lowered almost to the road with lights, and the split-arrow template on a lit gantry.
        /// </summary>
        private IEnumerator TestFeatures(SignPlacementToolSystem tool)
        {
            var spot = m_Placed.FirstOrDefault(p => p.support == "straight-full");
            if (spot.record == null) yield break;
            var t = math.clamp(spot.t - 0.3f, 0.05f, 0.95f);
            var close = Options("ground", "single");
            close.RoadGap = 0.5f;
            close.Lift = 1f;
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, t, new List<JObject> { Template("advance-guide") }, close));
            SpikeReport.Set("e2e.features.roadGap", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_features_roadgap.png", 25f, 22f);

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var first = InstancesOf(m_Placed[0].record.PrefabName).FirstOrDefault();
            var tr = em.GetComponentData<Game.Objects.Transform>(first);
            var p = tr.m_Position + math.mul(tr.m_Rotation, new float3(1, 0, 0)) * 85f;
            p.y = AssemblyBuilder.TerrainHeight(p);
            var low = Options("ground", "single");
            low.Elevation = -1.5f;
            yield return PlaceAndWait(() => tool.PlaceFreeForTest(p, new List<JObject> { Template("gore-exit") }, low));
            SpikeReport.Set("e2e.features.lowFree", Describe(SignPlacementToolSystem.LastPlaced));

            // Surface mount: just the sign, placed like a prop (here raised 3 m, turned 30 degrees).
            var surface = Options("surface", "single");
            surface.Free = true;
            surface.Elevation = 3f;
            surface.Angle = 30f;
            var q = p + math.mul(tr.m_Rotation, new float3(0, 0, 1)) * 12f;
            q.y = AssemblyBuilder.TerrainHeight(q);
            yield return PlaceAndWait(() => tool.PlaceFreeForTest(q, new List<JObject> { Template("services-blue") }, surface));
            SpikeReport.Set("e2e.features.surface", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_features_surface.png", 20f, 18f);

            var lowGantry = Options("singleTruss", "single");
            lowGantry.Lift = -5f;
            lowGantry.Lights = true;
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, math.clamp(t + 0.12f, 0.05f, 0.95f), new List<JObject> { Template("guide-exit-only") }, lowGantry));
            SpikeReport.Set("e2e.features.lowGantry", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_features_lowgantry.png", 160f, 25f);

            var lit = Options("straight", "single");
            lit.Lights = true;
            lit.RoadGap = 4f;
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, math.clamp(t + 0.24f, 0.05f, 0.95f), new List<JObject> { Template("split-lane-arrows") }, lit));
            SpikeReport.Set("e2e.features.splitArrows", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_features_split.png", 180f, 30f);
            // A diagrammatic sign and an LED message board side by side.
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, math.clamp(t + 0.36f, 0.05f, 0.95f), new List<JObject> { Template("lane-split"), Template("message-board") }, Options("doubleTruss", "single")));
            SpikeReport.Set("e2e.features.diagramLed", Describe(SignPlacementToolSystem.LastPlaced));
            SpikeReport.Set("e2e.features.glow", $"lit pixels in the last glow pass={Core.SignBuilderRuntime.Baker.LastGlowPixels}");
            var board = SignPlacementToolSystem.LastPlaced;
            yield return Shoot(board, "e2e_features_diagram_led.png", 10f, 35f);
            // The LED face is one of the mesh's lights, following a night-only effect: off by day, lit at night.
            string LedLight()
            {
                var entity = InstancesOf(board.PrefabName).FirstOrDefault();
                var states = em.HasBuffer<Game.Rendering.LightState>(entity) ? em.GetBuffer<Game.Rendering.LightState>(entity) : default;
                return $"emissive={em.HasBuffer<Game.Rendering.Emissive>(entity)} states=[{(states.IsCreated ? string.Join(",", Enumerable.Range(0, states.Length).Select(i => states[i].m_Intensity.ToString("F2"))) : "none")}]";
            }
            // Face materials: compressed textures, shared between structures with identical faces.
            var faceTextures = board.SurfaceIds.Skip(1).Select(id =>
            {
                if (!Colossal.IO.AssetDatabase.AssetDatabase.global.TryGetAsset(Colossal.Hash128.Parse(id), out Colossal.IO.AssetDatabase.SurfaceAsset surface)) return "missing";
                surface.LoadProperties(false);
                return string.Join("+", surface.textures.Select(t => t.Value is Colossal.IO.AssetDatabase.TextureAsset x ? $"{t.Key}:{x.width}x{x.height} {x.format}" : t.Key));
            });
            var sharedFaces = AssemblyLibrary.All.SelectMany(r => r.SurfaceIds.Skip(1)).GroupBy(id => id).Count(g => g.Count() > 1);
            var cacheBytes = new System.IO.DirectoryInfo(Core.SignBuilderCache.RootPath).EnumerateFiles("*.Texture").Sum(f => f.Length);
            SpikeReport.Set("e2e.features.faceTextures", $"{string.Join(" | ", faceTextures)} sharedFaceMaterials={sharedFaces} textureMB={cacheBytes / 1e6:F1}");
            SpikeReport.Set("e2e.features.ledLightDay", $"glowMeshes=[{string.Join(",", board.GlowMeshes ?? new int[0])}] {LedLight()}");
            // At night: the LED board glows, the lit roadside sign (edit test) has its light.
            var planet = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<Game.Simulation.PlanetarySystem>();
            var overridden = planet.overrideTime;
            var time = planet.normalizedTime;
            planet.overrideTime = true;
            planet.normalizedTime = 0.02f;
            // Night-only effects (sign lights, LED lettering) follow the simulation clock, not the sky.
            SetEffectNight(true);
            yield return new WaitForSecondsRealtime(3f);
            SetEffectNight(true);
            yield return Shoot(board, "e2e_night_led.png", 10f, 35f);
            SpikeReport.Set("e2e.features.ledLightNightFar", LedLight());
            SetEffectNight(true);
            yield return Shoot(board, "e2e_night_led_close.png", 10f, 15f);
            SpikeReport.Set("e2e.features.ledLightNight", LedLight());
            SetEffectNight(true);
            if (m_LitRoadside != null) yield return Shoot(m_LitRoadside, "e2e_night_roadside.png", 15f, 20f);
            SetEffectNight(false);
            planet.normalizedTime = time;
            planet.overrideTime = overridden;
            yield return new WaitForSecondsRealtime(2f);
        }

        /// <summary>
        /// Sets the game's night flag for effects directly (it otherwise follows the simulation clock, recomputed every
        /// 2048 frames): with no recorded change time, night-only effects switch at once.
        /// </summary>
        private static void SetEffectNight(bool night)
        {
            var flags = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<Game.Effects.EffectFlagSystem>();
            var type = typeof(Game.Effects.EffectFlagSystem);
            const System.Reflection.BindingFlags field = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            type.GetField("m_IsNightTime", field)?.SetValue(flags, night);
            type.GetField("m_LastTimeChange", field)?.SetValue(flags, 0u);
            Systems.LedLightSystem.ForceNight = night ? true : (bool?)null;
        }

        /// <summary>The designer path ("0", "1.2", ...) of the first part of <paramref name="type"/> in a design.</summary>
        private static string PathOf(JObject design, string type)
        {
            string Find(JArray items, string prefix)
            {
                if (items == null) return null;
                for (var i = 0; i < items.Count; i++)
                {
                    var path = prefix == null ? i.ToString() : prefix + "." + i;
                    if ((string)items[i]["type"] == type) return path;
                    var inner = Find(items[i]["items"] as JArray, path);
                    if (inner != null) return inner;
                }
                return null;
            }
            return Find(design["items"] as JArray, null);
        }

        /// <summary>The roadside sign saved with lights on (edit test), shown at night.</summary>
        private AssemblyRecord m_LitRoadside;

        /// <summary>
        /// Themes: every theme's templates in the designer (UI renderer), and a gantry mixing German, UK and Nordic
        /// signs (baked renderer) to check fonts, plates and metric sizes end to end.
        /// </summary>
        private IEnumerator TestThemes(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            SpikeReport.Set("e2e.themes.loaded", string.Join(", ", Themes.All.Select(t => $"{t.Id}:{(t.Def["fonts"] as JArray)?.Count}fonts")));
            ui.SetEditorOpen(true);
            // The first templates of each newer theme in the designer (UI renderer, game-provided and borrowed fonts).
            foreach (var theme in new[] { "ca", "jp", "cn", "fr", "nl", "it", "ru", "au", "ch" })
            {
                var dir = Path.Combine(SignBuilderRuntime.ResourcesDir, "Themes", theme, "templates");
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f).Take(2))
                {
                    ui.OpenForEdit(new AssemblyRecord { Designs = new List<JObject> { JObject.Parse(File.ReadAllText(file)) } }, "new");
                    yield return new WaitForSecondsRealtime(2.5f);
                    SpikeReport.Screenshot($"theme_{theme}_{Path.GetFileNameWithoutExtension(file)}.png");
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }
            ui.SetEditorOpen(false);
            var spot = m_Placed.FirstOrDefault(p => p.support == "doubleTruss-full");
            if (spot.record == null) yield break;
            var t = math.clamp(spot.t + 0.3f, 0.05f, 0.95f);
            var designs = new List<JObject> { Template("de/autobahn-vorwegweiser"), Template("uk/motorway-advance"), Template("nordic/motorvag-se") };
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, t, designs, Options("doubleTruss", "full")));
            SpikeReport.Set("e2e.themes.gantry", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_themes_gantry.png", 10f, 40f);
            var standalone = Template("uk/primary-route");
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, math.clamp(t - 0.12f, 0.05f, 0.95f), new List<JObject> { standalone }, Options("ground", "single")));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_themes_standalone.png", 20f, 22f);
            // Baked with fonts the game provides (CJK, Cyrillic) and fonts borrowed from another theme (Canada).
            var asian = new List<JObject> { Template("jp/expressway"), Template("cn/expressway"), Template("ru/magistral") };
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, math.clamp(t + 0.15f, 0.05f, 0.95f), asian, Options("doubleTruss", "full")));
            SpikeReport.Set("e2e.themes.gantry2", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_themes_gantry2.png", 10f, 40f);
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, math.clamp(t - 0.24f, 0.05f, 0.95f), new List<JObject> { Template("ca/exit-guide") }, Options("ground", "single")));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "e2e_themes_canada.png", 20f, 22f);
        }

        /// <summary>Templates on disk: save, rename, hide a built-in, delete.</summary>
        private IEnumerator TestTemplates(SignBuilderUISystem ui)
        {
            string Names() => string.Join(", ", JArray.Parse(UITemplates(ui)).Select(x => (string)x["id"]));
            var builtIns = JArray.Parse(UITemplates(ui)).Count;
            ui.SaveTemplate(new JObject { ["name"] = "E2E Test Sign", ["design"] = Template("guide-exit-tab") });
            yield return null;
            var saved = JArray.Parse(UITemplates(ui)).FirstOrDefault(x => (string)x["name"] == "E2E Test Sign");
            SpikeReport.Set("e2e.templates.saved", saved == null ? "FAILED" : (string)saved["id"]);
            if (saved != null)
            {
                ui.RenameTemplate((string)saved["id"], "E2E Renamed");
                yield return null;
                SpikeReport.Set("e2e.templates.renamed", JArray.Parse(UITemplates(ui)).Any(x => (string)x["name"] == "E2E Renamed"));
                // Keep it for the UI screenshots, deleted at the end of the designer pause.
            }
            ui.DeleteTemplate("builtin:us/advance-guide");
            yield return null;
            SpikeReport.Set("e2e.templates.hidden", $"before={builtIns} now={JArray.Parse(UITemplates(ui)).Count} ids={Names()}");
            var hidden = Path.Combine(SignBuilderRuntime.DataDir, "hidden-templates.json");
            if (File.Exists(hidden)) File.Delete(hidden);
        }

        private static string UITemplates(SignBuilderUISystem ui) => ui.TemplatesJson;

        /// <summary>
        /// The designer with a long sign loaded. With ModsData/SignBuilder/ui-wait.flag present the run pauses here so a
        /// DevTools script can click around (spike/shot-NAME.flag screenshots, spike/ui-done.flag continues).
        /// </summary>
        private IEnumerator TestDesignerPause(SignBuilderUISystem ui)
        {
            ui.SetEditorOpen(true);
            ui.OpenForEdit(new AssemblyRecord { Designs = new List<JObject> { Template("guide-long") } }, "new");
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Screenshot("e2e_designer.png");
            yield return new WaitForSecondsRealtime(0.5f);
            File.WriteAllText(Path.Combine(SpikeReport.OutputDir, "ui-ready.flag"), "");
            var end = Time.realtimeSinceStartup + 150f;
            while (File.Exists(Path.Combine(SignBuilderRuntime.DataDir, "ui-wait.flag")) && Time.realtimeSinceStartup < end
                   && !File.Exists(Path.Combine(SpikeReport.OutputDir, "ui-done.flag")))
            {
                foreach (var flag in Directory.GetFiles(SpikeReport.OutputDir, "shot-*.flag"))
                {
                    var name = Path.GetFileNameWithoutExtension(flag).Substring(5);
                    File.Delete(flag);
                    SpikeReport.Screenshot($"ui_{name}.png");
                }
                yield return null;
            }
            var test = JArray.Parse(ui.TemplatesJson).FirstOrDefault(x => ((string)x["name"])?.StartsWith("E2E") == true);
            if (test != null) ui.DeleteTemplate((string)test["id"]);
            ui.SetEditorOpen(false);
        }

        private static JObject Template(string name) =>
            JObject.Parse(File.ReadAllText(Path.Combine(SignBuilderRuntime.ResourcesDir, "Themes", name.Contains("/") ? name.Split('/')[0] : "us", "templates", (name.Contains("/") ? name.Split('/')[1] : name) + ".json")));

        private IEnumerator PlaceAndWait(Action place)
        {
            var before = SignPlacementToolSystem.LastPlaced;
            var start = Time.realtimeSinceStartup;
            place();
            var tool = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 20f) yield return null;
            yield return new WaitForSecondsRealtime(2f);
        }

        private IEnumerator Shoot(AssemblyRecord record, string name, float yawOffset = 15f, float zoom = 45f)
        {
            var entity = InstancesOf(record.PrefabName).FirstOrDefault();
            if (entity == Entity.Null) yield break;
            var t = World.DefaultGameObjectInjectionWorld.EntityManager.GetComponentData<Game.Objects.Transform>(entity);
            var fwd = math.mul(t.m_Rotation, new float3(0, 0, 1));
            SetCamera(t.m_Position + new float3(0, 5f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + yawOffset, 10f, zoom);
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Screenshot(name);
            yield return new WaitForSecondsRealtime(0.5f);
        }

        private static string Describe(AssemblyRecord r) =>
            r == null ? "(none)" : $"lod={r.LodGeometryId != null} lights={r.Context?.Options?.Lights}/{(r.LightSpots?.Length ?? 0) / 6} spec={r.SupportSpec?.Kind}{(r.SupportSpec?.Mirror == true ? "(mirrored)" : "")}({string.Join(",", r.SupportSpec?.Args?.Select(a => a.ToString("F2")) ?? Array.Empty<string>())}) centers=[{string.Join(" ", r.Centers.Select(c => $"({c.x:F2},{c.y:F2})"))}]";

        private static void LookAt(Entity entity, float distance = 55f)
        {
            var t = World.DefaultGameObjectInjectionWorld.EntityManager.GetComponentData<Game.Objects.Transform>(entity);
            var fwd = math.mul(t.m_Rotation, new float3(0, 0, 1));
            SetCamera(t.m_Position + new float3(0, 6f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + 15f, 8f, distance);
        }

        private void RemoveAllAssemblies()
        {
            var removed = 0;
            foreach (var record in AssemblyLibrary.All)
            {
                foreach (var e in InstancesOf(record.PrefabName))
                {
                    m_Runtime.Delete(e);
                    removed++;
                }
            }
            SpikeReport.Set("reload.removedForRun", removed);
        }

        private IEnumerator TestSaveForReload()
        {
            var menu = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<MenuUISystem>();
            var preview = ScreenCaptureHelper.CreateRenderTarget("SignBuilder-E2E", 680, 383);
            ScreenCaptureHelper.CaptureScreenshot(Camera.main, preview, new MenuHelpers.SaveGamePreviewSettings());
            var task = GameManager.instance.Save(kE2ESave, menu.GetSaveInfo(autoSave: false), AssetDatabase.user, preview);
            while (!task.IsCompleted) yield return null;
            SpikeReport.Set("e2e.save.result", task.Status == TaskStatus.RanToCompletion ? task.Result.ToString() : task.Exception?.ToString());
            var meta = AssetDatabase.user.GetAssets<SaveGameMetadata>().FirstOrDefault(m => m.name == kE2ESave);
            if (meta != null)
            {
                File.WriteAllText(Path.Combine(SpikeReport.OutputDir, "e2e_save.guid"), meta.id.guid.ToString());
                SpikeReport.Set("e2e.save.guid", meta.id.guid.ToString());
            }
            SpikeReport.Set("e2e.save.expected", string.Join(",", m_Placed.Select(p => p.record.PrefabName)));
            File.WriteAllText(Path.Combine(SignBuilderRuntime.DataDir, "e2e_expected.txt"), string.Join("\n", m_Placed.Select(p => p.record.PrefabName)));
        }

        /// <summary>
        /// UI test mode: opens the editor and waits while an external DevTools script drives it.
        /// Creating spike/shot-NAME.flag takes screenshot NAME.png; spike/done.flag ends the run.
        /// </summary>
        private IEnumerator RunUITest()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(4f);
            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SignBuilderUISystem>().SetEditorOpen(true);
            SpikeReport.Set("ui.ready", true);
            SpikeReport.Write();
            var end = Time.realtimeSinceStartup + 300f;
            while (Time.realtimeSinceStartup < end && !File.Exists(Path.Combine(SpikeReport.OutputDir, "done.flag")))
            {
                foreach (var flag in Directory.GetFiles(SpikeReport.OutputDir, "shot-*.flag"))
                {
                    var name = Path.GetFileNameWithoutExtension(flag).Substring(5);
                    File.Delete(flag);
                    SpikeReport.Screenshot($"ui_{name}.png");
                }
                yield return null;
            }
            Finish();
        }

        /// <summary>Reload mode: booted into the E2E save; are the assemblies back and rendering?</summary>
        private IEnumerator RunReload()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(8f);
            m_Runtime = new RuntimePrefabs();
            yield return ReloadChecks();
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator ReloadChecks()
        {
            // Shared-save path: assemblies missing on this PC are rebuilt from the save after loading.
            var wait = Time.realtimeSinceStartup + 120f;
            while ((AssemblyRestoreSystem.Running || AssemblyRestoreSystem.LastRestored < 0 && File.Exists(Path.Combine(SpikeReport.OutputDir, "..", "portable.flag")))
                   && Time.realtimeSinceStartup < wait) yield return null;
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Set("reload.restored", $"objects={AssemblyRestoreSystem.LastRestored} failedAssemblies={AssemblyRestoreSystem.LastFailed}");
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var expectedFile = Path.Combine(Application.persistentDataPath, "ModsData", "SignBuilder", "e2e_expected.txt");
            var expected = File.Exists(expectedFile) ? File.ReadAllLines(expectedFile) : Array.Empty<string>();
            SpikeReport.Set("reload.expected", expected.Length);
            var first = Entity.Null;
            foreach (var name in expected)
            {
                var entities = InstancesOf(name);
                var batches = entities.Count(e => em.HasBuffer<Game.Rendering.MeshBatch>(e) && em.GetBuffer<Game.Rendering.MeshBatch>(e, true).Length > 0);
                SpikeReport.Set($"reload.{name}", $"count={entities.Count} batches={batches}");
                if (first == Entity.Null && entities.Count > 0) first = entities[0];
            }
            if (first != Entity.Null)
            {
                var t = em.GetComponentData<Game.Objects.Transform>(first);
                var fwd = math.mul(t.m_Rotation, new float3(0, 0, 1));
                SetCamera(t.m_Position + new float3(0, 5f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + 15f, 10f, 70f);
                yield return new WaitForSecondsRealtime(4f);
                SpikeReport.Screenshot("reload.png");
                yield return new WaitForSecondsRealtime(0.5f);
            }
            // Faces that fail to load leave their batches without a material and log this error.
            SpikeReport.Set("reload.batchErrors", CountLogLines("Error when initializing batch"));
            SpikeReport.Set("reload.textureErrors", CountLogLines("Texture creation failed"));
        }

        private static int CountLogLines(string text)
        {
            try
            {
                var log = Path.Combine(Application.persistentDataPath, "Player.log");
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var count = 0;
                string line;
                while ((line = reader.ReadLine()) != null) if (line.Contains(text)) count++;
                return count;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Spread-out points on road edges near <paramref name="near"/>, highways first, 60 m apart.</summary>
        private List<(Entity edge, float t)> HighwaySpots(EntityManager em, float3 near, int count)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Composition>());
            using var edges = query.ToEntityArray(Allocator.Temp);
            var prefabSystem = m_Runtime.PrefabSystem;
            var candidates = new List<(Entity e, Curve c, string name, float d)>();
            foreach (var e in edges)
            {
                var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                if (!em.HasComponent<RoadData>(prefab)) continue;
                var curve = em.GetComponentData<Curve>(e);
                if (curve.m_Length < 40f) continue;
                var name = prefabSystem.TryGetPrefab(prefab, out PrefabBase p) ? p.name : "";
                candidates.Add((e, curve, name, Colossal.Mathematics.MathUtils.Distance(curve.m_Bezier.xz, near.xz, out _)));
            }
            var ordered = candidates.OrderByDescending(c => c.name.Contains("Highway")).ThenBy(c => c.d).ToList();
            var spots = new List<(Entity, float)>();
            var used = new List<float3>();
            foreach (var c in ordered)
            {
                foreach (var t in new[] { 0.5f, 0.2f, 0.8f })
                {
                    var pos = Colossal.Mathematics.MathUtils.Position(c.c.m_Bezier, t);
                    if (used.Any(u => math.distance(u, pos) < 60f)) continue;
                    used.Add(pos);
                    spots.Add((c.e, t));
                    if (spots.Count >= count) return spots;
                }
            }
            return spots;
        }

        private List<Entity> InstancesOf(string prefabName)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (!m_Runtime.PrefabSystem.TryGetPrefab(new PrefabID(nameof(StaticObjectPrefab), prefabName), out var prefab)) return new List<Entity>();
            var prefabEntity = m_Runtime.PrefabSystem.GetEntity(prefab);
            using var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            return entities.Where(e => em.GetComponentData<PrefabRef>(e).m_Prefab == prefabEntity).ToList();
        }
    }
}

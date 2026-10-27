using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

namespace SignBuilder.Spike
{
    /// <summary>
    /// End-to-end run of the real feature code: editor UI system, placement tool (all supports),
    /// pick-and-replace re-editing, then a save for the fresh-boot reload check ("reload" mode).
    /// </summary>
    internal sealed partial class SpikeRunner
    {
        private const string kE2ESave = "SignBuilder_E2E";

        private static readonly (string support, string template)[] kPlacements =
        {
            ("truss", "guide-exit-tab"), ("curvedSpan", "guide-exit-only"), ("monotube", "services-blue"),
            ("curvedCantilever", "attraction-brown"), ("cantilever", "advance-guide"), ("ground", "gore-exit"),
        };

        private readonly List<(string support, AssemblyRecord record, Entity edge, float t)> m_Placed = new();

        private IEnumerator RunE2E()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            var world = World.DefaultGameObjectInjectionWorld;
            var ui = world.GetOrCreateSystemManaged<SignBuilderUISystem>();
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();

            yield return Test("e2e.editor", TestEditorOpens(ui));
            yield return Test("e2e.place", TestPlaceAll(tool));
            yield return Test("e2e.shots", TestAssemblyShots());
            yield return Test("e2e.preview", TestPreviewAndReverse(tool));
            yield return Test("e2e.reedit", TestReedit(ui, tool));
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
            SpikeReport.Set("e2e.editor.status", ui.Status);
            SpikeReport.Screenshot("e2e_editor.png");
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// <summary>One assembly per support type, each on its own stretch of the nearest highway.</summary>
        private IEnumerator TestPlaceAll(SignPlacementToolSystem tool)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(em, near, kPlacements.Length);
            SpikeReport.Set("e2e.place.spots", spots.Count);
            var templates = Path.Combine(SignBuilderRuntime.ResourcesDir, "Templates");

            for (var i = 0; i < kPlacements.Length && i < spots.Count; i++)
            {
                var (support, template) = kPlacements[i];
                var design = JObject.Parse(File.ReadAllText(Path.Combine(templates, template + ".json")));
                var before = SignPlacementToolSystem.LastPlaced;
                var start = Time.realtimeSinceStartup;
                // The truss carries two signs side by side to exercise multi-sign layout.
                var designs = support == "truss"
                    ? new List<JObject> { design, JObject.Parse(File.ReadAllText(Path.Combine(templates, "guide-exit-only.json"))) }
                    : new List<JObject> { design };
                tool.PlaceForTest(spots[i].edge, spots[i].t, designs, support);
                while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 20f) yield return null;
                var record = SignPlacementToolSystem.LastPlaced;
                if (record == before)
                {
                    SpikeReport.Set($"e2e.place.{support}", $"FAILED: {world.GetOrCreateSystemManaged<SignBuilderUISystem>().Status}");
                    continue;
                }
                m_Placed.Add((support, record, spots[i].edge, spots[i].t));
                SpikeReport.Set($"e2e.place.{support}", $"{record.PrefabName} ms={(Time.realtimeSinceStartup - start) * 1000:F0}");
            }
            yield return new WaitForSecondsRealtime(5f);
            foreach (var p in m_Placed)
            {
                var entities = InstancesOf(p.record.PrefabName);
                var batches = entities.Count(e => em.HasBuffer<Game.Rendering.MeshBatch>(e) && em.GetBuffer<Game.Rendering.MeshBatch>(e, true).Length > 0);
                var overridden = entities.Count(e => em.HasComponent<Game.Common.Overridden>(e));
                SpikeReport.Set($"e2e.instances.{p.support}", $"count={entities.Count} batches={batches} overridden={overridden}");
            }
        }

        private IEnumerator TestAssemblyShots()
        {
            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SignBuilderUISystem>().SetEditorOpen(false);
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            foreach (var p in m_Placed)
            {
                var entity = InstancesOf(p.record.PrefabName).FirstOrDefault();
                if (entity == Entity.Null) continue;
                var t = em.GetComponentData<Game.Objects.Transform>(entity);
                var fwd = math.mul(t.m_Rotation, new float3(0, 0, 1));
                SetCamera(t.m_Position + new float3(0, p.support == "ground" ? 3f : 6f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + 15f, 8f, p.support == "ground" ? 30f : 55f);
                yield return new WaitForSecondsRealtime(3f);
                SpikeReport.Screenshot($"e2e_{p.support}.png");
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        /// <summary>
        /// Hover preview (drawn for a road position like the mouse would), then a reversed truss on the
        /// same spot facing the opposite carriageway.
        /// </summary>
        private IEnumerator TestPreviewAndReverse(SignPlacementToolSystem tool)
        {
            if (m_Placed.Count == 0) yield break;
            var spot = m_Placed[0];
            var design = JObject.Parse(File.ReadAllText(Path.Combine(SignBuilderRuntime.ResourcesDir, "Templates", "guide-long.json")));
            tool.BeginPlace(new List<JObject> { design }, "curvedSpan", reverse: true);
            var t = math.clamp(spot.t + 0.25f, 0.05f, 0.95f);
            var start = Time.realtimeSinceStartup;
            while (!tool.PreviewReady && Time.realtimeSinceStartup - start < 10f) yield return null;
            SpikeReport.Set("e2e.preview.ready", tool.PreviewReady);
            var snap = new RoadProbe(World.DefaultGameObjectInjectionWorld.EntityManager, m_Runtime.PrefabSystem).SnapAt(spot.edge, t);
            var fwd = -snap.Tangent * snap.MainDirection;
            SetCamera(snap.Point + new float3(0, 6f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + 15f, 8f, 55f);
            var end = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < end)
            {
                tool.PreviewForTest(spot.edge, t);
                yield return null;
            }
            SpikeReport.Screenshot("e2e_preview.png");
            for (var i = 0; i < 10; i++) { tool.PreviewForTest(spot.edge, t); yield return null; }

            var before = SignPlacementToolSystem.LastPlaced;
            start = Time.realtimeSinceStartup;
            tool.PlaceForTest(spot.edge, t, new List<JObject> { design }, "curvedSpan", reverse: true);
            while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 20f) yield return null;
            SpikeReport.Set("e2e.reverse.placed", SignPlacementToolSystem.LastPlaced == before ? "FAILED" : SignPlacementToolSystem.LastPlaced.PrefabName);
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Screenshot("e2e_reverse.png");
            yield return new WaitForSecondsRealtime(0.5f);
            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SignBuilderUISystem>().SetEditorOpen(false);
        }

        /// <summary>Pick the first assembly, change its text, place again: the original must be replaced.</summary>
        private IEnumerator TestReedit(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            if (m_Placed.Count == 0) yield break;
            var first = m_Placed[0];
            var entity = InstancesOf(first.record.PrefabName).FirstOrDefault();
            tool.PickForTest(entity);
            yield return new WaitForSecondsRealtime(2f);
            SpikeReport.Set("e2e.reedit.editorOpen", ui.EditorOpen);
            SpikeReport.Screenshot("e2e_reedit_editor.png");
            yield return new WaitForSecondsRealtime(0.5f);

            var designs = first.record.Designs.Select(d => (JObject)d.DeepClone()).ToList();
            var items = (JArray)designs[0]["items"];
            items[1]["text"] = "Edited City";
            var before = SignPlacementToolSystem.LastPlaced;
            var start = Time.realtimeSinceStartup;
            tool.PlaceForTest(first.edge, first.t, designs, first.support);
            while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 20f) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            ui.SetEditorOpen(false);
            var replacement = SignPlacementToolSystem.LastPlaced;
            SpikeReport.Set("e2e.reedit.original", $"{first.record.PrefabName} remaining={InstancesOf(first.record.PrefabName).Count}");
            SpikeReport.Set("e2e.reedit.replacement", replacement == before ? "FAILED" : $"{replacement.PrefabName} count={InstancesOf(replacement.PrefabName).Count}");
            if (replacement != before)
            {
                m_Placed[0] = (first.support, replacement, first.edge, first.t);
                var e = InstancesOf(replacement.PrefabName).FirstOrDefault();
                if (e != Entity.Null)
                {
                    var t = World.DefaultGameObjectInjectionWorld.EntityManager.GetComponentData<Game.Objects.Transform>(e);
                    var fwd = math.mul(t.m_Rotation, new float3(0, 0, 1));
                    SetCamera(t.m_Position + new float3(0, 6f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + 15f, 8f, 55f);
                    yield return new WaitForSecondsRealtime(3f);
                    SpikeReport.Screenshot("e2e_reedit.png");
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }
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
            RestoreContinueTarget();
            Finish();
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

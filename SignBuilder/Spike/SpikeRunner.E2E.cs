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

            if (m_Mode == "reload-e2e")
            {
                // Booted into the previous E2E save: check it loads, then clear it for this run's placements.
                yield return Test("reload", ReloadChecks());
                RemoveAllAssemblies();
                yield return new WaitForSecondsRealtime(2f);
            }
            yield return Test("e2e.editor", TestEditorOpens(ui));
            yield return Test("e2e.place", TestPlaceAll(tool));
            yield return Test("e2e.shots", TestAssemblyShots());
            yield return Test("e2e.elevated", TestElevated(tool));
            yield return Test("e2e.hover", TestRealRaycast(tool));
            yield return Test("e2e.infopanel", TestInfoPanelEdit(ui, tool));
            yield return Test("e2e.compact", TestCompactWhilePlacing(ui, tool));
            yield return Test("e2e.longpreview", TestLongPreview(ui));
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

        /// <summary>
        /// Overhead supports on an elevated road (bridge deck well above terrain/water): posts must stand on
        /// the deck. Reports each post's height above the terrain below it (0 = on the ground, not wanted
        /// here) and whether both posts are within the deck width.
        /// </summary>
        private IEnumerator TestElevated(SignPlacementToolSystem tool)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var probe = new RoadProbe(em, m_Runtime.PrefabSystem);
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
                if (spots.Count >= 4) break;
            }
            SpikeReport.Set("e2e.elevated.spots", spots.Count);
            var supports = new[] { "truss", "monotube", "curvedSpan", "cantilever" };
            var design = JObject.Parse(File.ReadAllText(Path.Combine(SignBuilderRuntime.ResourcesDir, "Templates", "guide-exit-tab.json")));
            for (var i = 0; i < spots.Count && i < supports.Length; i++)
            {
                var snap = probe.SnapAt(spots[i].edge, spots[i].t);
                var placement = AssemblyBuilder.Layout(supports[i], snap, new List<Vector2> { new Vector2(4.5f, 3f) });
                var b = placement.Support.bounds;
                var posts = new[] { b.min.x, b.max.x }.Select(x => (float3)placement.Origin + math.mul(placement.Rotation, new float3(x, 0, 0))).ToArray();
                UnityEngine.Object.Destroy(placement.Support);
                var aboveGround = string.Join("/", posts.Select(q => (q.y - AssemblyBuilder.TerrainHeight(q)).ToString("F1")));
                SpikeReport.Set($"e2e.elevated.{supports[i]}.layout", $"{snap.PrefabName} deck={snap.EdgeWidth:F1} postsAboveGround={aboveGround} span={b.size.x:F1}");

                var before = SignPlacementToolSystem.LastPlaced;
                var start = Time.realtimeSinceStartup;
                tool.PlaceForTest(spots[i].edge, spots[i].t, new List<JObject> { design }, supports[i]);
                while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 20f) yield return null;
                var record = SignPlacementToolSystem.LastPlaced;
                if (record == before) continue;
                yield return new WaitForSecondsRealtime(2f);
                var entity = InstancesOf(record.PrefabName).FirstOrDefault();
                if (entity == Entity.Null) continue;
                var t = em.GetComponentData<Game.Objects.Transform>(entity);
                var fwd = math.mul(t.m_Rotation, new float3(0, 0, 1));
                SetCamera(t.m_Position + new float3(0, 4f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + 35f, 18f, 45f);
                yield return new WaitForSecondsRealtime(3f);
                SpikeReport.Screenshot($"e2e_elevated_{supports[i]}.png");
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        private Entity FirstInstance(string support) =>
            m_Placed.Where(p => p.support == support).Select(p => InstancesOf(p.record.PrefabName).FirstOrDefault()).FirstOrDefault();

        /// <summary>World point at the middle of a placed assembly's bounds.</summary>
        private static float3 BoundsCentre(Entity entity, AssemblyRecord record)
        {
            var t = World.DefaultGameObjectInjectionWorld.EntityManager.GetComponentData<Game.Objects.Transform>(entity);
            var b = record.Bounds;
            var local = new float3((b[0] + b[3]) * 0.5f, (b[1] + b[4]) * 0.5f, (b[2] + b[5]) * 0.5f);
            return t.m_Position + math.mul(t.m_Rotation, local);
        }

        private static void LookAt(Entity entity, float distance = 55f)
        {
            var t = World.DefaultGameObjectInjectionWorld.EntityManager.GetComponentData<Game.Objects.Transform>(entity);
            var fwd = math.mul(t.m_Rotation, new float3(0, 0, 1));
            SetCamera(t.m_Position + new float3(0, 6f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + 15f, 8f, distance);
        }

        /// <summary>
        /// Moves the real OS cursor over each placed assembly in pick mode and checks the tool's own raycast
        /// (the path a mouse click takes) reports that assembly.
        /// </summary>
        private IEnumerator TestRealRaycast(SignPlacementToolSystem tool)
        {
            var ui = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<SignBuilderUISystem>();
            ui.SetEditorOpen(false);
            foreach (var p in m_Placed)
            {
                var entity = InstancesOf(p.record.PrefabName).FirstOrDefault();
                if (entity == Entity.Null) continue;
                LookAt(entity, p.support == "ground" ? 30f : 55f);
                yield return new WaitForSecondsRealtime(1.5f);
                tool.BeginPick();
                var screen = Camera.main.WorldToScreenPoint(BoundsCentre(entity, p.record));
                var hit = Entity.Null;
                for (var i = 0; i < 40 && hit != entity; i++)
                {
                    UnityEngine.InputSystem.Mouse.current?.WarpCursorPosition(new Vector2(screen.x, screen.y));
                    yield return null;
                    hit = tool.Hovered;
                }
                SpikeReport.Set($"e2e.hover.{p.support}", hit == entity ? "hit" : $"MISSED (hovered {hit}, screen {screen})");
                tool.Deactivate();
            }
        }

        /// <summary>Select a placed sign as the default tool would; the info panel shows our section and its button opens the editor.</summary>
        private IEnumerator TestInfoPanelEdit(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var entity = FirstInstance("curvedSpan");
            if (entity == Entity.Null) yield break;
            LookAt(entity);
            var info = world.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>();
            var section = world.GetOrCreateSystemManaged<SignInfoSection>();
            info.SetSelection(entity);
            yield return new WaitForSecondsRealtime(2.5f);
            SpikeReport.Set("e2e.infopanel.visible", section.visible);
            SpikeReport.Screenshot("e2e_infopanel.png");
            yield return new WaitForSecondsRealtime(0.5f);
            section.EditSelected();
            yield return new WaitForSecondsRealtime(2.5f);
            SpikeReport.Set("e2e.infopanel.edit", $"editorOpen={ui.EditorOpen} replacing={tool.IsReplacing} mode={ui.ToolMode}");
            SpikeReport.Screenshot("e2e_infopanel_editor.png");
            yield return new WaitForSecondsRealtime(0.5f);
            ui.SetEditorOpen(false);
        }

        /// <summary>The editor collapses to a small bar while placing and comes back on cancel.</summary>
        private IEnumerator TestCompactWhilePlacing(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            ui.SetEditorOpen(true);
            yield return new WaitForSecondsRealtime(1f);
            var design = JObject.Parse(File.ReadAllText(Path.Combine(SignBuilderRuntime.ResourcesDir, "Templates", "guide-exit-tab.json")));
            tool.BeginPlace(new List<JObject> { design }, "truss");
            yield return new WaitForSecondsRealtime(2f);
            SpikeReport.Set("e2e.compact.mode", ui.ToolMode);
            SpikeReport.Screenshot("e2e_compact.png");
            yield return new WaitForSecondsRealtime(0.5f);
            tool.BeginPick(); // what right-click and "Back to editor" do
            yield return new WaitForSecondsRealtime(1.5f);
            SpikeReport.Set("e2e.compact.after", ui.ToolMode);
            SpikeReport.Screenshot("e2e_compact_back.png");
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// <summary>
        /// The long-legend template in the editor preview (must fit, not clip). With -uiDeveloperMode the run
        /// pauses here so a DevTools script can click parts (spike/shot-NAME.flag screenshots, spike/ui-done.flag continues).
        /// </summary>
        private IEnumerator TestLongPreview(SignBuilderUISystem ui)
        {
            var design = JObject.Parse(File.ReadAllText(Path.Combine(SignBuilderRuntime.ResourcesDir, "Templates", "guide-long.json")));
            ui.SetEditorOpen(true);
            ui.OpenForEdit(new AssemblyRecord { Support = "truss", Designs = new List<JObject> { design } });
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Screenshot("e2e_longpreview.png");
            yield return new WaitForSecondsRealtime(0.5f);
            File.WriteAllText(Path.Combine(SpikeReport.OutputDir, "ui-ready.flag"), "");
            var end = Time.realtimeSinceStartup + 120f;
            while (Environment.GetCommandLineArgs().Contains("-uiDeveloperMode") && Time.realtimeSinceStartup < end
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
            ui.SetEditorOpen(false);
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

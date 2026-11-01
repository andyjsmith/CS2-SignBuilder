using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Tools;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Core;
using SignBuilder.Systems;
using SignBuilder.Tools;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace SignBuilder.Tests
{
    internal sealed partial class SpikeRunner
    {
        /// <summary>
        /// Mode "render-perf": what placed signs and Sign Builder's UI cost. Each structure's LOD setup against a
        /// vanilla prop, whether the game switches to the LOD with distance, frame times with every sign shown and
        /// hidden, and with the designer and the gantry editor open and closed again. About 4 minutes.
        /// </summary>
        private IEnumerator RunRenderPerf()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
            var ui = world.GetOrCreateSystemManaged<SignBuilderUISystem>();
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            var page = GameManager.instance.userInterface.view.View;
            page.ExecuteScript(kInstallCounters);

            static string Fields(object o) => string.Join(" ", o.GetType().GetFields().Select(f => $"{f.Name}={f.GetValue(o)}"));
            string MeshInfo(RenderPrefabBase mesh)
            {
                var entity = prefabSystem.GetEntity(mesh);
                var data = em.HasComponent<MeshData>(entity) ? Fields(em.GetComponentData<MeshData>(entity)) : "no MeshData";
                var lods = em.HasBuffer<LodMesh>(entity) ? em.GetBuffer<LodMesh>(entity, true).Length : -1;
                return $"lodMeshes={lods} {data}";
            }

            // LOD setup: ours against the vanilla prop our materials copy.
            var records = AssemblyLibrary.All.Where(r => InstancesOf(r.PrefabName).Count > 0).ToList();
            var placedHere = new List<AssemblyRecord>();
            if (records.Count < 5)
            {
                // Too few in the test city: a typical set (gantries of each kind, roadside signs), removed at the end.
                var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
                var set = new (string[] designs, LayoutOptions options)[]
                {
                    (new[] { "lane-split", "guide-exit-only" }, Options("doubleTruss", "single")),
                    (new[] { "guide-long", "message-board" }, Options("doubleTruss", "full")),
                    (new[] { "advance-guide" }, Options("curved", "single")),
                    (new[] { "guide-exit-tab" }, Options("ground", "single")),
                    (new[] { "services-blue" }, Options("ground", "single")),
                    (new[] { "split-lane-arrows" }, Options("straight", "single")),
                };
                var setSpots = HighwaySpots(em, near, set.Length);
                for (var i = 0; i < Math.Min(set.Length, setSpots.Count); i++)
                {
                    var before = SignPlacementToolSystem.LastPlaced;
                    var designs = set[i].designs.Select(Template).ToList();
                    yield return PlaceAndWait(() => tool.PlaceForTest(setSpots[i].edge, setSpots[i].t, designs, set[i].options));
                    if (SignPlacementToolSystem.LastPlaced != before) placedHere.Add(SignPlacementToolSystem.LastPlaced);
                }
                records = AssemblyLibrary.All.Where(r => InstancesOf(r.PrefabName).Count > 0).ToList();
            }
            SpikeReport.Set("render.placedForTest", placedHere.Count);
            SpikeReport.Set("render.placed", $"structures={records.Count} objects={records.Sum(r => InstancesOf(r.PrefabName).Count)}");
            SpikeReport.Set("render.lod.vanilla", MeshInfo(m_Runtime.FindMesh(new[] { "LightpolePark" }, out _)));
            foreach (var record in records.Take(4))
            {
                if (!prefabSystem.TryGetPrefab(new PrefabID(nameof(StaticObjectPrefab), record.PrefabName), out var p) || p is not StaticObjectPrefab prefab) continue;
                SpikeReport.Set($"render.lod.{record.Support}.{record.Id.Substring(0, 6)}", $"tris={record.IndexCount / 3} lodTris={record.LodIndexCount / 3} " + MeshInfo(prefab.m_Meshes[0].m_Mesh));
            }

            // The camera on the biggest structure, near and far: its batches show which LOD is drawn.
            var biggest = records.OrderByDescending(r => r.IndexCount).FirstOrDefault();
            var target = biggest == null ? Entity.Null : InstancesOf(biggest.PrefabName).First();
            var at = target == Entity.Null ? float3.zero : em.GetComponentData<Game.Objects.Transform>(target).m_Position;
            string Batches()
            {
                if (target == Entity.Null || !em.HasBuffer<MeshBatch>(target)) return "none";
                return string.Join(" | ", em.GetBuffer<MeshBatch>(target, true).AsNativeArray().ToArray().Select(b => Fields(b)));
            }
            foreach (var zoom in new[] { 40f, 400f, 1500f })
            {
                SetCamera(at, 30f, 35f, zoom);
                yield return new WaitForSecondsRealtime(3f);
                SpikeReport.Set($"render.batches.zoom{zoom:F0}", Batches());
            }

            IEnumerator Measure(string name, float seconds)
            {
                page.ExecuteScript(kResetCounters);
                var times = new List<float>();
                var end = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < end)
                {
                    yield return null;
                    times.Add(Time.unscaledDeltaTime * 1000f);
                }
                ui.TestReport = null;
                page.ExecuteScript(kReport);
                for (var i = 0; i < 30 && ui.TestReport == null; i++) yield return null;
                times.Sort();
                float Pct(float p) => times[Math.Min(times.Count - 1, (int)(times.Count * p))];
                SpikeReport.Set($"render.{name}.frames", $"n={times.Count} avg={times.Average():F2}ms p50={Pct(0.5f):F2} p99={Pct(0.99f):F2} max={times.Last():F1}");
                SpikeReport.Set($"render.{name}.uiPage", ui.TestReport ?? "no report");
            }

            // Signs shown and hidden, the camera on a group of them (hidden as the editor hides a gantry it edits).
            SetCamera(at, 30f, 35f, 250f);
            yield return new WaitForSecondsRealtime(3f);
            var all = records.SelectMany(r => InstancesOf(r.PrefabName)).ToList();
            void Hide(bool hidden)
            {
                foreach (var e in all)
                {
                    if (hidden == em.HasComponent<Hidden>(e)) continue;
                    if (hidden) em.AddComponent<Hidden>(e);
                    else em.RemoveComponent<Hidden>(e);
                    em.AddComponent<BatchesUpdated>(e);
                }
            }
            yield return Test("render.shown1", Measure("shown1", 20f));
            Hide(true);
            yield return new WaitForSecondsRealtime(1f);
            yield return Test("render.hidden", Measure("hidden", 20f));
            Hide(false);
            yield return new WaitForSecondsRealtime(1f);
            yield return Test("render.shown2", Measure("shown2", 20f));

            // The designer (templates and preview), then the gantry editor, then closed.
            ui.SetEditorOpen(true);
            yield return new WaitForSecondsRealtime(3f);
            page.ExecuteScript(kWrapRender);
            ui.OpenForEdit(new AssemblyRecord { Designs = new List<JObject> { Template("guide-long") } }, "new");
            yield return new WaitForSecondsRealtime(2f);
            yield return Test("render.designer", Measure("designer", 20f));
            var spots = HighwaySpots(em, at, 1);
            if (spots.Count > 0)
            {
                tool.ArrangeForTest(spots[0].edge, spots[0].t, new List<JObject> { Template("lane-split"), Template("guide-exit-only") }, Options("doubleTruss", "single"));
                yield return WaitArrange(tool, ui);
                SpikeReport.Set("render.gantryEditor.mode", ui.ToolMode);
                yield return Test("render.gantryEditor", Measure("gantryEditor", 20f));
            }
            ui.SetEditorOpen(false);
            yield return new WaitForSecondsRealtime(2f);
            yield return Test("render.closed", Measure("closed", 20f));
            foreach (var record in placedHere)
            {
                foreach (var e in InstancesOf(record.PrefabName)) m_Runtime.Delete(e);
            }
            yield return new WaitForSecondsRealtime(1f);
            SpikeReport.Set("render.status", "ok");
            Finish();
        }
    }
}

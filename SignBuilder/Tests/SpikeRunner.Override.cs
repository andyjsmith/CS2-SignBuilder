using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Common;
using Game.Prefabs;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Core;
using SignBuilder.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace SignBuilder.Tests
{
    internal sealed partial class SpikeRunner
    {
        private const string kOverrideSave = "SignBuilder_Override";
        private static string OverrideSignsFile => Path.Combine(SignBuilderRuntime.DataDir, "override_signs.txt");

        /// <summary>
        /// Mode "overridesave": players' signs vanish (Better Bulldozer's Net-Lanes view, which shows markers, still shows
        /// them: the game's Overridden state, as trees under roads). Places signs on every kind of road, watches every
        /// frame whether any sign prefab has the Overridable flag, updates the roads under them (an edit, or Road Builder
        /// at load) and counts the signs the game hid; saves as "SignBuilder_Override". Mode "overrideload" (a fresh boot
        /// into that save) counts the hidden signs, updates the roads again and counts them once more.
        /// </summary>
        private IEnumerator RunOverrideSave()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            yield return Test("override", OverrideSave());
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator RunOverrideLoad()
        {
            yield return WaitForGame();
            m_Runtime = new RuntimePrefabs();
            yield return Test("overrideload", OverrideLoad());
            RestoreContinueTarget();
            Finish();
        }

        /// <summary>Sign prefabs in the library whose ObjectGeometryData has Overridable or Brushable.</summary>
        private List<string> OverridablePrefabs(EntityManager em)
        {
            var result = new List<string>();
            foreach (var record in AssemblyLibrary.All)
            {
                if (!m_Runtime.PrefabSystem.TryGetPrefab(new PrefabID(nameof(StaticObjectPrefab), record.PrefabName), out var prefab)) continue;
                var entity = m_Runtime.PrefabSystem.GetEntity(prefab);
                if (entity == Entity.Null || !em.HasComponent<ObjectGeometryData>(entity)) continue;
                var flags = em.GetComponentData<ObjectGeometryData>(entity).m_Flags;
                if ((flags & (Game.Objects.GeometryFlags.Overridable | Game.Objects.GeometryFlags.Brushable)) != 0) result.Add($"{record.PrefabName}:{flags}");
            }
            return result;
        }

        /// <summary>Every sign object in the city, and how many the game has hidden (Overridden).</summary>
        private (int all, int overridden) SignObjects(EntityManager em)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Tools.Temp>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            int all = 0, overridden = 0;
            foreach (var e in entities)
            {
                var name = AssemblySaveSystem.PrefabNameOf(em, m_Runtime.PrefabSystem, em.GetComponentData<PrefabRef>(e).m_Prefab);
                if (name == null || !name.StartsWith("SignBuilder_A_")) continue;
                all++;
                if (em.HasComponent<Overridden>(e)) overridden++;
            }
            return (all, overridden);
        }

        private List<(string label, Entity entity)> OverrideSigns(EntityManager em) => !File.Exists(OverrideSignsFile) ? new List<(string, Entity)>() :
            File.ReadAllLines(OverrideSignsFile).Select(l => l.Split(' '))
                .Select(p => (p[0], ObjectsAt(em, new float3(float.Parse(p[2]), float.Parse(p[3]), float.Parse(p[4]))).FirstOrDefault())).ToList();

        private void ReportHidden(string key, EntityManager em)
        {
            var signs = OverrideSigns(em);
            var hidden = signs.Where(s => s.entity == Entity.Null || em.HasComponent<Overridden>(s.entity)).Select(s => s.label + (s.entity == Entity.Null ? "(gone)" : "")).ToList();
            var city = SignObjects(em);
            SpikeReport.Set($"{key}.hidden", $"{hidden.Count}/{signs.Count} [{string.Join(" ", hidden)}]");
            SpikeReport.Set($"{key}.city", $"signObjects={city.all} overridden={city.overridden}");
            SpikeReport.Set($"{key}.overridablePrefabs", string.Join(" ", OverridablePrefabs(em)));
            // What Better Bulldozer's marker view reveals: sign objects the renderer culls because their entry in the
            // game's object search tree lacks the normal layers (Overridden, or indexed without their prefab's layers).
            var tree = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<Game.Objects.SearchSystem>().GetStaticSearchTree(true, out var deps);
            deps.Complete();
            var masked = signs.Where(s => s.entity != Entity.Null && (!tree.TryGet(s.entity, out var b) || (b.m_Mask & BoundsMask.NormalLayers) == 0))
                .Select(s => $"{s.label}{(em.HasComponent<Overridden>(s.entity) ? "(overridden)" : "")}").ToList();
            SpikeReport.Set($"{key}.markersOnly", $"{masked.Count}/{signs.Count} [{string.Join(" ", masked)}]");
            SpikeReport.Set($"{key}.probe", $"frames={OverrideProbeSystem.Frames} windowFrames={OverrideProbeSystem.WindowFrames} withOverrideCheck={OverrideProbeSystem.WindowFramesWithCheck} first: {OverrideProbeSystem.FirstWindow}");
            SpikeReport.Write();
        }

        private IEnumerator UpdateAllRoads(string key, EntityManager em)
        {
            using var roads = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Tools.Temp>());
            em.AddComponent<Updated>(roads);
            SpikeReport.Set($"{key}.roadsUpdated", roads.CalculateEntityCount());
            yield return new WaitForSecondsRealtime(4f);
        }

        private IEnumerator OverrideSave()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            if (File.Exists(OverrideSignsFile)) File.Delete(OverrideSignsFile);
            ReportHidden("override.atLoad", em);

            // Every frame from here on: which sign prefabs ever carry Overridable or Brushable.
            var everOverridable = new HashSet<string>();
            var watching = true;
            IEnumerator Watch()
            {
                while (watching)
                {
                    foreach (var p in OverridablePrefabs(em)) everOverridable.Add(p.Split(':')[0]);
                    yield return null;
                }
            }
            SignBuilderRuntime.Run(Watch());

            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = RoadTypeSpots(em, near, 10);
            var lines = new List<string>();
            foreach (var (edge, kind) in spots)
            {
                foreach (var (mount, t, options, template) in new[]
                {
                    ("posts", 0.3f, Options("ground", "single"), "services-blue"),
                    ("gantry", 0.7f, Options("doubleTruss", "span"), "guide-exit-only"),
                    ("prop", 0.5f, Options("surface", "single"), "services-blue"),
                })
                {
                    var label = $"{mount}.{kind}";
                    var before = SignPlacementToolSystem.LastPlaced;
                    yield return PlaceAndWait(() => tool.PlaceForTest(edge, t, new List<JObject> { Template(template) }, options));
                    var record = SignPlacementToolSystem.LastPlaced;
                    if (record == before) continue;
                    var entity = InstancesOf(record.PrefabName).FirstOrDefault();
                    if (entity == Entity.Null) continue;
                    var p = em.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                    lines.Add($"{label} {record.PrefabName} {p.x} {p.y} {p.z}");
                }
            }
            File.WriteAllLines(OverrideSignsFile, lines);
            SpikeReport.Set("override.placed", lines.Count);
            yield return new WaitForSecondsRealtime(2f);
            ReportHidden("override.placed", em);

            // A road edit under the signs, or Road Builder updating its roads.
            yield return UpdateAllRoads("override.roads", em);
            ReportHidden("override.afterRoads", em);
            watching = false;
            var placedPrefabs = new HashSet<string>(lines.Select(l => l.Split(' ')[1]));
            SpikeReport.Set("override.everOverridable", $"{everOverridable.Count(placedPrefabs.Contains)}/{placedPrefabs.Count} placed prefabs, {everOverridable.Count} in all");
            yield return DespawnSaveAs(kOverrideSave, "override.save");
        }

        private IEnumerator OverrideLoad()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            yield return new WaitForSecondsRealtime(3f);
            ReportHidden("overrideload.atLoad", em);
            // The roads under the hidden signs change again: trees come back then, the signs should too.
            yield return UpdateAllRoads("overrideload.roads", em);
            ReportHidden("overrideload.afterRoads", em);
            var signs = OverrideSigns(em);
            var hidden = signs.Count(s => s.entity == Entity.Null || em.HasComponent<Overridden>(s.entity));
            SpikeReport.Set("overrideload.result", hidden == 0 ? "PASS" : $"FAIL: {hidden}/{signs.Count} signs hidden");
            SpikeReport.Set("overrideload.forcedRoadFrames", OverrideForceSystem.ForcedFrames);
            // Keep what the city looks like now, to load with a fixed build: are hidden signs shown again?
            if (m_Mode == "overrideforce") yield return DespawnSaveAs("SignBuilder_OverrideHidden", "overrideload.save");
        }
    }
}

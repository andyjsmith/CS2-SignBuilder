using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Core;
using SignBuilder.Tools;
using Colossal.Entities;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace SignBuilder.Tests
{
    internal sealed partial class SpikeRunner
    {
        private const string kRoadTypesSave = "SignBuilder_RoadTypes";
        private static string RoadTypesSignsFile => Path.Combine(SignBuilderRuntime.DataDir, "roadtypes_signs.txt");

        /// <summary>
        /// Mode "roadsave": "half of my signs are gone when I load my save; they stay on one-lane highways, not on normal
        /// roads". One roadside sign and one gantry on every kind of road near the camera (each road prefab, ground level
        /// and elevated apart), saved as "SignBuilder_RoadTypes". Mode "roadload" (a fresh boot into that save, with the
        /// guid in SignBuilder_RoadTypes.guid) counts which of them are still there over a minute.
        /// </summary>
        private IEnumerator RunRoadTypesSave()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(8f);
            m_Runtime = new RuntimePrefabs();
            yield return Test("roadsave", RoadTypesSave());
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator RunRoadTypesLoad()
        {
            yield return WaitForGame();
            m_Runtime = new RuntimePrefabs();
            yield return Test("roadload", RoadTypesLoad());
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator RoadTypesSave()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = RoadTypeSpots(em, near, 10);
            SpikeReport.Set("roadsave.roadKinds", spots.Count);
            var lines = new List<string>();
            foreach (var (edge, kind) in spots)
            {
                foreach (var (mount, t, options, template) in new[]
                {
                    ("posts", 0.3f, Options("ground", "single"), "services-blue"),
                    ("gantry", 0.7f, Options("doubleTruss", "single"), "guide-exit-only"),
                    // The prop version: just the sign, hung beside the road.
                    ("prop", 0.5f, Options("surface", "single"), "services-blue"),
                })
                {
                    var label = $"{mount}.{kind}";
                    var before = SignPlacementToolSystem.LastPlaced;
                    yield return PlaceAndWait(() => tool.PlaceForTest(edge, t, new List<JObject> { Template(template) }, options));
                    var record = SignPlacementToolSystem.LastPlaced;
                    if (record == before)
                    {
                        SpikeReport.Set($"roadsave.place.{label}", "FAILED");
                        continue;
                    }
                    var entity = InstancesOf(record.PrefabName).FirstOrDefault();
                    SpikeReport.Set($"roadsave.place.{label}", $"{record.PrefabName} {(entity == Entity.Null ? "no object" : MoveItDescribe(em, entity))}");
                    if (entity == Entity.Null) continue;
                    var p = em.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                    lines.Add($"{label} {record.PrefabName} {p.x} {p.y} {p.z}");
                }
            }
            // Props placed freely off the road (flat, raised and turned) and free posts, beside the first few roads.
            foreach (var (edge, kind) in spots.Take(3))
            {
                var curve = em.GetComponentData<Curve>(edge).m_Bezier;
                var mid = Colossal.Mathematics.MathUtils.Position(curve, 0.5f);
                var side = math.normalizesafe(new float3(-Colossal.Mathematics.MathUtils.Tangent(curve, 0.5f).z, 0f, Colossal.Mathematics.MathUtils.Tangent(curve, 0.5f).x));
                var free = new List<(string mount, float offset, LayoutOptions options)>();
                var flat = Options("surface", "single"); flat.Free = true;
                var raised = Options("surface", "single"); raised.Free = true; raised.Elevation = 3f; raised.Angle = 30f;
                var posts = Options("ground", "single"); posts.Free = true;
                free.Add(("freeProp", 22f, flat));
                free.Add(("freePropRaised", 30f, raised));
                free.Add(("freePosts", 38f, posts));
                foreach (var (mount, offset, options) in free)
                {
                    var label = $"{mount}.{kind}";
                    var q = mid + side * offset;
                    q.y = AssemblyBuilder.TerrainHeight(q);
                    var before = SignPlacementToolSystem.LastPlaced;
                    yield return PlaceAndWait(() => tool.PlaceFreeForTest(q, new List<JObject> { Template("services-blue") }, options));
                    var record = SignPlacementToolSystem.LastPlaced;
                    if (record == before)
                    {
                        SpikeReport.Set($"roadsave.place.{label}", "FAILED");
                        continue;
                    }
                    var entity = InstancesOf(record.PrefabName).FirstOrDefault();
                    SpikeReport.Set($"roadsave.place.{label}", $"{record.PrefabName} {(entity == Entity.Null ? "no object" : MoveItDescribe(em, entity))}");
                    if (entity == Entity.Null) continue;
                    var p = em.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                    lines.Add($"{label} {record.PrefabName} {p.x} {p.y} {p.z}");
                }
            }
            File.WriteAllLines(RoadTypesSignsFile, lines);
            SpikeReport.Set("roadsave.placed", lines.Count);
            // Before saving, as a player who saves right after placing.
            yield return DespawnWatchBrief("roadsave.beforeSave", RoadTypesSignsFile);
            yield return DespawnSaveAs(kRoadTypesSave, "roadsave.save");
        }

        private IEnumerator RoadTypesLoad()
        {
            // As a player does: the city runs (the harness otherwise stays paused, where simulation systems never run).
            var simulation = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<Game.Simulation.SimulationSystem>();
            if (simulation != null) simulation.selectedSpeed = 1f;
            SpikeReport.Set("roadload.unpaused", simulation != null);
            SignBuilderRuntime.Run(WatchEveryFrame("roadload.frames", RoadTypesSignsFile, 150f));
            yield return DespawnWatch("roadload", roadsUpdated: true, signsFile: RoadTypesSignsFile);
            yield return DespawnWatchBrief("roadload.final", RoadTypesSignsFile);
        }

        /// <summary>
        /// Every frame for <paramref name="seconds"/>: the first frame each listed sign is missing, deleted, overridden or
        /// moved, with the game's simulation frame and what the object carried then.
        /// </summary>
        private IEnumerator WatchEveryFrame(string key, string file, float seconds)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (!File.Exists(file)) yield break;
            var simulation = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<Game.Simulation.SimulationSystem>();
            var signs = File.ReadAllLines(file).Select(l => l.Split(' '))
                .Select(p => (label: p[0], position: new float3(float.Parse(p[2]), float.Parse(p[3]), float.Parse(p[4])))).ToList();
            var tracked = signs.ToDictionary(s => s.label, s => ObjectsAt(em, s.position).FirstOrDefault());
            SpikeReport.Set($"{key}.foundAtStart", $"{tracked.Count(t => t.Value != Entity.Null)}/{signs.Count} simFrame={simulation?.frameIndex}");
            var reported = new HashSet<string>();
            var start = Time.realtimeSinceStartup;
            for (var frame = 0; Time.realtimeSinceStartup - start < seconds; frame++)
            {
                foreach (var s in signs)
                {
                    if (reported.Contains(s.label)) continue;
                    var e = tracked[s.label];
                    var exists = e != Entity.Null && em.Exists(e);
                    var moved = exists ? math.distance(em.GetComponentData<Game.Objects.Transform>(e).m_Position, s.position) : 0f;
                    if (exists && !em.HasComponent<Deleted>(e) && !em.HasComponent<Overridden>(e) && moved < 0.5f) continue;
                    reported.Add(s.label);
                    SpikeReport.Set($"{key}.lost.{s.label}", $"frame={frame} t={Time.realtimeSinceStartup - start:F1}s simFrame={simulation?.frameIndex} exists={exists} moved={moved:F1}m" +
                        (exists ? " " + MoveItDescribe(em, e) : ""));
                    SpikeReport.Write();
                }
                yield return null;
            }
            SpikeReport.Set($"{key}.lostCount", $"{reported.Count}/{signs.Count}");
            SpikeReport.Write();
        }

        /// <summary>How many of the signs listed in <paramref name="file"/> are there now, and which are not.</summary>
        private IEnumerator DespawnWatchBrief(string key, string file)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (!File.Exists(file)) yield break;
            var signs = File.ReadAllLines(file).Select(l => l.Split(' '))
                .Select(p => (label: p[0], position: new float3(float.Parse(p[2]), float.Parse(p[3]), float.Parse(p[4])))).ToList();
            var shown = signs.Where(s => ObjectsAt(em, s.position).Any(e => !em.HasComponent<Deleted>(e) && !em.HasComponent<Overridden>(e))).ToList();
            SpikeReport.Set($"{key}.survivors", $"{shown.Count}/{signs.Count}");
            SpikeReport.Set($"{key}.missing", string.Join(" ", signs.Except(shown).Select(s => s.label)));
            SpikeReport.Write();
            yield return null;
        }

        /// <summary>
        /// One edge per kind of road near <paramref name="near"/>: each road prefab, ground level and elevated apart
        /// (highways and normal roads of every lane count the city has), nearest first.
        /// </summary>
        private List<(Entity edge, string kind)> RoadTypeSpots(EntityManager em, float3 near, int max)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Composition>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Tools.Temp>());
            using var edges = query.ToEntityArray(Allocator.Temp);
            var prefabSystem = m_Runtime.PrefabSystem;
            var best = new Dictionary<string, (Entity edge, float d)>();
            foreach (var e in edges)
            {
                var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                if (!em.HasComponent<RoadData>(prefab)) continue;
                var curve = em.GetComponentData<Curve>(e);
                if (curve.m_Length < 60f) continue;
                var name = prefabSystem.TryGetPrefab(prefab, out PrefabBase p) ? p.name : "unknown";
                var elevated = em.HasComponent<Game.Net.Elevation>(e) && math.any(math.abs(em.GetComponentData<Game.Net.Elevation>(e).m_Elevation) > 1f);
                var kind = new string(name.Where(char.IsLetterOrDigit).ToArray()) + (elevated ? "_elevated" : "_ground");
                var d = Colossal.Mathematics.MathUtils.Distance(curve.m_Bezier.xz, near.xz, out _);
                if (!best.TryGetValue(kind, out var b) || d < b.d) best[kind] = (e, d);
            }
            SpikeReport.Set("roadsave.kindsInCity", string.Join(" ", best.OrderBy(b => b.Value.d).Select(b => $"{b.Key}@{b.Value.d:F0}m")));
            return best.OrderBy(b => b.Value.d).Take(max).Select(b => (b.Value.edge, b.Key)).ToList();
        }

        private const string kRestoreResave = "SignBuilder_RestoreResave";

        /// <summary>
        /// Mode "restoresave": loads SignBuilder_RoadTypes with its structures missing on this PC (their records removed
        /// from assemblies.json before the run, so their assets were swept at boot), and saves as soon as some of them
        /// are rebuilt while others still wait, as an autosave or a player saving during the rebuild would.
        /// Mode "restoreload" (a fresh boot into that save, with the structures removed again) counts the signs that
        /// come back: every one needs its recipe from the save.
        /// </summary>
        private IEnumerator RunRestoreSave()
        {
            yield return WaitForGame();
            m_Runtime = new RuntimePrefabs();
            yield return Test("restoresave", RestoreSave());
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator RunRestoreLoad()
        {
            yield return WaitForGame();
            m_Runtime = new RuntimePrefabs();
            yield return Test("restoreload", RestoreLoad());
            RestoreContinueTarget();
            Finish();
        }

        private static List<(string label, string prefab, float3 position)> RoadTypeSigns() =>
            File.ReadAllLines(RoadTypesSignsFile).Select(l => l.Split(' '))
                .Select(p => (p[0], p[1], new float3(float.Parse(p[2]), float.Parse(p[3]), float.Parse(p[4])))).ToList();

        private IEnumerator RestoreSave()
        {
            var signs = RoadTypeSigns();
            int Built() => signs.Count(s => AssemblyLibrary.FromPrefabName(s.prefab) != null);
            SpikeReport.Set("restoresave.atStart", $"running={AssemblyRestoreSystem.Running} inLibrary={Built()}/{signs.Count}");
            var deadline = Time.realtimeSinceStartup + 120f;
            while (Time.realtimeSinceStartup < deadline && !(AssemblyRestoreSystem.Running && Built() >= 3)) yield return null;
            var built = Built();
            SpikeReport.Set("restoresave.atSave", $"running={AssemblyRestoreSystem.Running} rebuilt={built}/{signs.Count}");
            if (!AssemblyRestoreSystem.Running || built >= signs.Count)
            {
                SpikeReport.Set("restoresave.result", "INVALID: the rebuild was not running with structures still waiting");
                yield break;
            }
            yield return DespawnSaveAs(kRestoreResave, "restoresave.save");
            SpikeReport.Set("restoresave.rebuiltAfterSave", $"{Built()}/{signs.Count} running={AssemblyRestoreSystem.Running}");
            while (AssemblyRestoreSystem.Running) yield return null;
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Set("restoresave.restored", $"objects={AssemblyRestoreSystem.LastRestored} failed={AssemblyRestoreSystem.LastFailed}");
            RestoreSurvivors("restoresave.final", signs);
        }

        private IEnumerator RestoreLoad()
        {
            var signs = RoadTypeSigns();
            SpikeReport.Set("restoreload.atStart", $"running={AssemblyRestoreSystem.Running} inLibrary={signs.Count(s => AssemblyLibrary.FromPrefabName(s.prefab) != null)}/{signs.Count}");
            var deadline = Time.realtimeSinceStartup + 150f;
            while (Time.realtimeSinceStartup < deadline && (AssemblyRestoreSystem.Running || AssemblyRestoreSystem.LastRestored < 0)) yield return null;
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Set("restoreload.restored", $"objects={AssemblyRestoreSystem.LastRestored} failed={AssemblyRestoreSystem.LastFailed} running={AssemblyRestoreSystem.Running}");
            SpikeReport.Set("restoreload.unresolvedNoRecipe", CountLogLines("no recipe in the save"));
            RestoreSurvivors("restoreload.final", signs);
            var shown = signs.Count(s => Resolved(s.position, s.prefab));
            SpikeReport.Set("restoreload.result", shown == signs.Count ? "PASS" : $"FAIL: {signs.Count - shown} signs did not come back");
        }

        /// <summary>A sign object stands at <paramref name="position"/> with its prefab present (not an obsolete placeholder).</summary>
        private bool Resolved(float3 position, string prefab)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            return ObjectsAt(em, position).Any(e => !em.HasComponent<Deleted>(e)
                && em.TryGetComponent(em.GetComponentData<PrefabRef>(e).m_Prefab, out PrefabData data) && data.m_Index >= 0
                && AssemblySaveSystem.PrefabNameOf(em, m_Runtime.PrefabSystem, em.GetComponentData<PrefabRef>(e).m_Prefab) == prefab);
        }

        private void RestoreSurvivors(string key, List<(string label, string prefab, float3 position)> signs)
        {
            var missing = signs.Where(s => !Resolved(s.position, s.prefab)).ToList();
            SpikeReport.Set($"{key}.survivors", $"{signs.Count - missing.Count}/{signs.Count}");
            SpikeReport.Set($"{key}.missing", string.Join(" ", missing.Select(s => s.label)));
            SpikeReport.Write();
        }
    }
}

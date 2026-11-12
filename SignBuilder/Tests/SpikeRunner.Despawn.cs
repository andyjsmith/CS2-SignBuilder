using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Colossal.Entities;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.Assets;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Menu;
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
        private const string kDespawnSave = "SignBuilder_Despawn";
        private const string kDespawnResave = "SignBuilder_Despawn2";
        private static string DespawnSignsFile => Path.Combine(SignBuilderRuntime.DataDir, "despawn_signs.txt");

        /// <summary>
        /// Mode "despawnsave": "a few of my signs despawn whenever I first load in". Places a plain gantry, a lit
        /// roadside sign, an LED gantry, a roadside sign that is then edited (Save in place) and a gantry that is then
        /// deleted and cleaned up (as "Delete unused sign structures" does), saves "SignBuilder_Despawn" before the edit
        /// and the cleanup, and does not save after them, as a player who quits without saving. Then loads that save
        /// again in the same session and saves it as "SignBuilder_Despawn2". Modes "despawnload" and "despawnload2" (a
        /// fresh boot into either save) watch every sign for a minute.
        /// </summary>
        private IEnumerator RunDespawnSave()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(8f);
            m_Runtime = new RuntimePrefabs();
            yield return Test("despawnsave", DespawnSave());
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator RunDespawnLoad(string label)
        {
            yield return WaitForGame();
            m_Runtime = new RuntimePrefabs();
            yield return Test(label, DespawnWatch(label, roadsUpdated: label != "despawnnoroads"));
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator DespawnSave()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(em, near, 5);
            SpikeReport.Set("despawnsave.spots", spots.Count);
            if (spots.Count < 5) yield break;

            var lit = Options("ground", "single");
            lit.Lights = true;
            var cases = new (string label, List<JObject> designs, LayoutOptions options)[]
            {
                ("plain", new List<JObject> { Template("guide-exit-only") }, Options("doubleTruss", "single")),
                ("lit", new List<JObject> { Template("services-blue") }, lit),
                ("led", new List<JObject> { Template("lane-split"), Template("message-board") }, Options("doubleTruss", "single")),
                ("edited", new List<JObject> { Template("gore-exit") }, Options("ground", "single")),
                ("cleaned", new List<JObject> { Template("services-blue") }, Options("straight", "single")),
            };
            var placed = new List<(string label, AssemblyRecord record, Entity entity)>();
            for (var i = 0; i < cases.Length; i++)
            {
                var c = cases[i];
                var before = SignPlacementToolSystem.LastPlaced;
                yield return PlaceAndWait(() => tool.PlaceForTest(spots[i].edge, spots[i].t, c.designs, c.options));
                var record = SignPlacementToolSystem.LastPlaced;
                if (record == before)
                {
                    SpikeReport.Set($"despawnsave.place.{c.label}", "FAILED");
                    continue;
                }
                var entity = InstancesOf(record.PrefabName).FirstOrDefault();
                placed.Add((c.label, record, entity));
                SpikeReport.Set($"despawnsave.place.{c.label}", $"{record.PrefabName} {(entity == Entity.Null ? "no object" : MoveItDescribe(em, entity))}");
            }
            File.WriteAllLines(DespawnSignsFile, placed.Where(p => p.entity != Entity.Null).Select(p =>
            {
                var t = em.GetComponentData<Game.Objects.Transform>(p.entity).m_Position;
                return $"{p.label} {p.record.PrefabName} {t.x} {t.y} {t.z}";
            }));

            yield return DespawnSaveAs(kDespawnSave, "despawnsave.save");

            // After saving, as a player would: edit one sign in place, delete another and clean up unused structures.
            var edited = placed.FirstOrDefault(p => p.label == "edited");
            if (edited.record != null)
            {
                tool.EditPlaced(edited.entity);
                yield return new WaitForSecondsRealtime(2f);
                var design = (JObject)edited.record.Designs[0].DeepClone();
                design["scheme"] = "blue";
                var before = SignPlacementToolSystem.LastPlaced;
                tool.SavePosts(design, lights: false);
                var start = Time.realtimeSinceStartup;
                while ((tool.Busy || SignPlacementToolSystem.LastPlaced == before) && Time.realtimeSinceStartup - start < 20f) yield return null;
                world.GetOrCreateSystemManaged<Systems.SignBuilderUISystem>().SetEditorOpen(false);
                yield return new WaitForSecondsRealtime(3f);
                SpikeReport.Set("despawnsave.edit", $"new={SignPlacementToolSystem.LastPlaced?.PrefabName} old record kept={AssemblyLibrary.FromPrefabName(edited.record.PrefabName) != null}");
            }
            var cleaned = placed.FirstOrDefault(p => p.label == "cleaned");
            if (cleaned.record != null)
            {
                m_Runtime.Delete(cleaned.entity);
                for (var i = 0; i < 30; i++) yield return null;
                var removed = AssemblyCleanup.Remove(new[] { cleaned.record });
                SpikeReport.Set("despawnsave.cleanup", $"removed={removed} record kept={AssemblyLibrary.FromPrefabName(cleaned.record.PrefabName) != null}");
            }
            SpikeReport.Write();

            // Load the save again without restarting (the city was not saved after the edit and the cleanup).
            var meta = AssetDatabase.global.GetAssets<SaveGameMetadata>().FirstOrDefault(m => m.name == kDespawnSave);
            if (meta == null) yield break;
            var load = GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, meta);
            while (!load.IsCompleted) yield return null;
            while (GameManager.instance.isGameLoading) yield return null;
            m_Runtime = new RuntimePrefabs();
            yield return DespawnWatch("despawnsave.reload", roadsUpdated: false);
            yield return DespawnSaveAs(kDespawnResave, "despawnsave.resave");
        }

        private IEnumerator DespawnSaveAs(string name, string key)
        {
            var menu = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<MenuUISystem>();
            var preview = ScreenCaptureHelper.CreateRenderTarget("SignBuilder-Despawn", 680, 383);
            ScreenCaptureHelper.CaptureScreenshot(Camera.main, preview, new MenuHelpers.SaveGamePreviewSettings());
            var task = GameManager.instance.Save(name, menu.GetSaveInfo(autoSave: false), AssetDatabase.user, preview);
            while (!task.IsCompleted) yield return null;
            var meta = AssetDatabase.user.GetAssets<SaveGameMetadata>().FirstOrDefault(m => m.name == name);
            SpikeReport.Set(key, $"{(task.Status == TaskStatus.RanToCompletion ? "ok" : task.Exception?.ToString())} guid={meta?.id.guid}");
            if (meta != null) File.WriteAllText(Path.Combine(SignBuilderRuntime.DataDir, name + ".guid"), meta.id.guid.ToString());
        }

        /// <summary>Each saved sign over a minute after loading: found where it stood, what prefab, shown or not.</summary>
        private IEnumerator DespawnWatch(string key, bool roadsUpdated, string signsFile = null)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            signsFile ??= DespawnSignsFile;
            if (!File.Exists(signsFile)) yield break;
            var signs = File.ReadAllLines(signsFile).Select(l => l.Split(' '))
                .Select(p => (label: p[0], prefab: p[1], position: new float3(float.Parse(p[2]), float.Parse(p[3]), float.Parse(p[4])))).ToList();
            SpikeReport.Set($"{key}.expected", signs.Count);
            var errorsBefore = CountLogLines("Error when initializing batch");
            var start = Time.realtimeSinceStartup;
            foreach (var at in new[] { 0f, 1f, 3f, 10f, 30f, 60f })
            {
                while (Time.realtimeSinceStartup - start < at) yield return null;
                foreach (var s in signs) SpikeReport.Set($"{key}.t{at:0}s.{s.label}", DespawnDescribe(em, s.position));
                SpikeReport.Set($"{key}.t{at:0}s.restore", $"running={AssemblyRestoreSystem.Running} restored={AssemblyRestoreSystem.LastRestored} failed={AssemblyRestoreSystem.LastFailed}");
                SpikeReport.Write();
                if (at == 10f)
                {
                    foreach (var s in signs)
                    {
                        var e = ObjectsAt(em, s.position).FirstOrDefault();
                        if (e == Entity.Null) continue;
                        LookAt(e, 40f);
                        yield return new WaitForSecondsRealtime(3f);
                        SpikeReport.Screenshot($"{key.Replace('.', '_')}_{s.label}.png");
                        yield return new WaitForSecondsRealtime(0.5f);
                    }
                }
                if (at == 30f && roadsUpdated)
                {
                    // As mods that update roads after a city loads (Road Builder) do: every road edge updated.
                    using var roads = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Tools.Temp>());
                    var tracked = signs.Select(s => (s.label, entity: ObjectsAt(em, s.position).FirstOrDefault())).Where(x => x.entity != Entity.Null).ToList();
                    em.AddComponent<Updated>(roads);
                    SpikeReport.Set($"{key}.roadsUpdated", roads.CalculateEntityCount());
                    // Frame by frame: when each sign is removed or hidden, and what it carried then.
                    var reported = new HashSet<string>();
                    var origin = tracked.ToDictionary(x => x.label, x => em.GetComponentData<Game.Objects.Transform>(x.entity).m_Position);
                    var watchStart = Time.realtimeSinceStartup;
                    for (var frame = 0; Time.realtimeSinceStartup - watchStart < 28f; frame++)
                    {
                        foreach (var (label, entity) in tracked)
                        {
                            if (reported.Contains(label)) continue;
                            var exists = em.Exists(entity);
                            var moved = exists ? math.distance(em.GetComponentData<Game.Objects.Transform>(entity).m_Position, origin[label]) : 0f;
                            if (exists && !em.HasComponent<Deleted>(entity) && !em.HasComponent<Overridden>(entity) && moved < 0.5f) continue;
                            reported.Add(label);
                            SpikeReport.Set($"{key}.removed.{label}", $"frame={frame} t={Time.realtimeSinceStartup - watchStart:F1}s exists={exists} moved={moved:F1}m" +
                                (exists ? $" now={em.GetComponentData<Game.Objects.Transform>(entity).m_Position} was={origin[label]} " + MoveItDescribe(em, entity) : ""));
                        }
                        yield return null;
                    }
                }
            }
            // Every instance of each sign's prefab anywhere in the city.
            foreach (var s in signs)
                SpikeReport.Set($"{key}.instances.{s.label}", string.Join(" | ", InstancesOf(s.prefab).Select(e =>
                    $"{math.distance(em.GetComponentData<Game.Objects.Transform>(e).m_Position, s.position):F1}m from saved spot, at {em.GetComponentData<Game.Objects.Transform>(e).m_Position}")));
            SpikeReport.Set($"{key}.batchErrors", CountLogLines("Error when initializing batch") - errorsBefore);
            SpikeReport.Set($"{key}.nullMeshErrors", CountLogLines("Attempting to set null mesh"));
            SpikeReport.Set($"{key}.textureErrors", CountLogLines("Texture creation failed"));
        }

        private static List<Entity> ObjectsAt(EntityManager em, float3 position)
        {
            using var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            var prefabs = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<PrefabSystem>();
            return entities.Where(e => math.distance(em.GetComponentData<Game.Objects.Transform>(e).m_Position, position) < 1f)
                .Where(e => (AssemblySaveSystem.PrefabNameOf(em, prefabs, em.GetComponentData<PrefabRef>(e).m_Prefab) ?? "").StartsWith("SignBuilder_A_"))
                .ToList();
        }

        private static string DespawnDescribe(EntityManager em, float3 position)
        {
            var found = ObjectsAt(em, position);
            if (found.Count == 0) return "gone";
            var prefabs = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<PrefabSystem>();
            return string.Join(" | ", found.Select(e =>
            {
                var prefab = em.GetComponentData<PrefabRef>(e).m_Prefab;
                var name = AssemblySaveSystem.PrefabNameOf(em, prefabs, prefab);
                var obsolete = em.TryGetComponent(prefab, out PrefabData data) && data.m_Index < 0;
                var inLibrary = AssemblyLibrary.FromPrefabName(name) != null;
                var batches = em.HasBuffer<Game.Rendering.MeshBatch>(e) ? em.GetBuffer<Game.Rendering.MeshBatch>(e, true).Length : 0;
                var flags = em.TryGetComponent(prefab, out ObjectGeometryData geometry) ? geometry.m_Flags.ToString().Replace(", ", "+") : "none";
                return $"{name} flags={flags} obsolete={obsolete} inLibrary={inLibrary} recipe={AssemblySaveSystem.RecipeOf(name) != null} " +
                    $"batches={batches} overridden={em.HasComponent<Overridden>(e)} deleted={em.HasComponent<Deleted>(e)} hidden={em.HasComponent<Game.Tools.Hidden>(e)}";
            }));
        }
    }
}

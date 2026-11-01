using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    internal sealed partial class SpikeRunner
    {
        /// <summary>
        /// Mode "lights-perf": what switching sign lights costs. The tracked signs against a full count after load;
        /// frame times and per-pass cost of the old pass (every object in the city) and the new one (the signs only),
        /// and with both systems off; then LED signs with and without fixtures: lettering lit day and night (by the
        /// game), fixtures on only at night (tracked), and dropped once deleted.
        /// About 3 minutes.
        /// </summary>
        private IEnumerator RunLightsPerf()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var lights = world.GetOrCreateSystemManaged<SignLightSystem>();
            var tracking = world.GetOrCreateSystemManaged<SignLightTrackingSystem>();
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();

            // Every object whose prefab has lights or lettering, counted the slow way.
            int Expected()
            {
                var prefabs = new HashSet<Entity>();
                foreach (var p in SignLightSystem.Prefabs) if (prefabSystem.TryGetEntity(p, out var e)) prefabs.Add(e);
                using var query = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Object>() },
                    None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>() },
                });
                using var refs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);
                var n = 0;
                foreach (var r in refs) if (prefabs.Contains(r.m_Prefab)) n++;
                return n;
            }

            SpikeReport.Set("lights.load", $"prefabs={SignLightSystem.Prefabs.Count} tracked={SignLightSystem.s_Signs.Count} expected={Expected()} " +
                $"fullPasses={SignLightTrackingSystem.FullPasses} fullPassMs={SignLightTrackingSystem.LastFullPassMs:F2} objects={SignLightTrackingSystem.LastFullPassObjects}");

            IEnumerator Phase(string name, float seconds)
            {
                var times = new List<float>();
                var passMs = new List<double>();
                var visited = 0;
                var lastPass = SignLightSystem.Passes;
                var end = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < end)
                {
                    yield return null;
                    times.Add(Time.unscaledDeltaTime * 1000f);
                    if (SignLightSystem.Passes != lastPass)
                    {
                        lastPass = SignLightSystem.Passes;
                        passMs.Add(SignLightSystem.LastPassMs);
                        visited = SignLightSystem.LastPassVisited;
                    }
                }
                times.Sort();
                float Pct(float p) => times[Math.Min(times.Count - 1, (int)(times.Count * p))];
                SpikeReport.Set($"lights.{name}.frames", $"n={times.Count} avg={times.Average():F2}ms p50={Pct(0.5f):F2} p99={Pct(0.99f):F2} max={times.Last():F1}");
                SpikeReport.Set($"lights.{name}.pass", passMs.Count == 0 ? "no passes"
                    : $"passes={passMs.Count} avg={passMs.Average():F3}ms max={passMs.Max():F3}ms visited={visited}");
            }

            // Order alternates so warm-up or drift does not favor one side.
            SignLightSystem.FullScanForTest = true;
            yield return Test("lights.legacy1", Phase("legacy1", 25f));
            SignLightSystem.FullScanForTest = false;
            yield return Test("lights.tracked1", Phase("tracked1", 25f));
            lights.Enabled = false;
            tracking.Enabled = false;
            yield return Test("lights.off", Phase("off", 25f));
            lights.Enabled = true;
            tracking.Enabled = true;
            SignLightSystem.FullScanForTest = true;
            yield return Test("lights.legacy2", Phase("legacy2", 25f));
            SignLightSystem.FullScanForTest = false;
            yield return Test("lights.tracked2", Phase("tracked2", 25f));

            // A new LED sign: tracked once placed, its lettering on at night, dropped once deleted.
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(em, near, 2);
            if (spots.Count < 2)
            {
                SpikeReport.Set("lights.new", "FAILED: no highway");
                Finish();
                yield break;
            }
            // Two LED signs: one without light fixtures (nothing of ours runs for it), one with.
            void PlaceLed(int i, bool fixtures)
            {
                var design = Template("message-board");
                var text = design.SelectTokens("$..text").OfType<JValue>().FirstOrDefault();
                if (text != null) text.Value = (fixtures ? "Lit " : "Led ") + DateTime.Now.Ticks % 100000;
                var options = Options("ground", "single");
                options.Lights = fixtures;
                tool.PlaceForTest(spots[i].edge, spots[i].t, new List<JObject> { design }, options);
            }
            var before = SignPlacementToolSystem.LastPlaced;
            yield return PlaceAndWait(() => PlaceLed(0, false));
            var led = SignPlacementToolSystem.LastPlaced;
            yield return PlaceAndWait(() => PlaceLed(1, true));
            var lit = SignPlacementToolSystem.LastPlaced;
            if (led == before || lit == led)
            {
                SpikeReport.Set("lights.new", "FAILED: not placed");
                Finish();
                yield break;
            }
            var ledEntity = InstancesOf(led.PrefabName).FirstOrDefault();
            var litEntity = InstancesOf(lit.PrefabName).FirstOrDefault();
            string State(Entity e)
            {
                if (!em.Exists(e)) return "gone";
                var lettering = em.HasBuffer<Game.Rendering.LightState>(e)
                    ? string.Join(",", em.GetBuffer<Game.Rendering.LightState>(e).AsNativeArray().Select(l => l.m_Intensity.ToString("F2"))) : "none";
                var fixtures = em.HasComponent<Game.Objects.StreetLight>(e) ? em.GetComponentData<Game.Objects.StreetLight>(e).m_State.ToString() : "none";
                return $"lettering=[{lettering}] fixtures={fixtures} tracked={SignLightSystem.s_Signs.Contains(e)}";
            }
            var lighting = world.GetExistingSystemManaged<Game.Rendering.LightingSystem>();
            yield return new WaitForSecondsRealtime(2f);
            SpikeReport.Set("lights.new.tracking", $"signs={SignLightSystem.s_Signs.Count} expected={Expected()} fullPasses={SignLightTrackingSystem.FullPasses} " +
                $"glowMeshes led={led.GlowMeshes?.Length ?? 0} lit={lit.GlowMeshes?.Length ?? 0}");
            SpikeReport.Set("lights.new.day", $"daylight={lighting.dayLightBrightness:F2} led: {State(ledEntity)} | lit: {State(litEntity)}");
            yield return Shoot(led, "lights_led_day.png", 10f, 20f);
            var planet = world.GetOrCreateSystemManaged<Game.Simulation.PlanetarySystem>();
            planet.overrideTime = true;
            planet.normalizedTime = 0.02f;
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Set("lights.new.night", $"daylight={lighting.dayLightBrightness:F2} led: {State(ledEntity)} | lit: {State(litEntity)}");
            yield return Shoot(lit, "lights_lit_night.png", 10f, 20f);
            planet.overrideTime = false;
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Set("lights.new.dayAgain", $"daylight={lighting.dayLightBrightness:F2} led: {State(ledEntity)} | lit: {State(litEntity)}");

            m_Runtime.Delete(ledEntity);
            m_Runtime.Delete(litEntity);
            yield return new WaitForSecondsRealtime(2f);
            SpikeReport.Set("lights.new.deleted", $"tracked={SignLightSystem.s_Signs.Contains(litEntity)} signs={SignLightSystem.s_Signs.Count} expected={Expected()}");
            SpikeReport.Set("lights.status", "ok");
            Finish();
        }
    }
}

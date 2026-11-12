using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Common;
using Game.Prefabs;
using Game.Prefabs.Effects;
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
        /// Mode "night-perf": "major performance issues at night since installing this mod; fast again with night visuals
        /// off". Lit gantries in view, frame times by day, at night with their light fixtures on, with our lights kept
        /// out of the volumetric fog, with the fixtures switched off, and with the gantries removed; and how the game's
        /// own light effects are set up, to compare ours with.
        /// </summary>
        private IEnumerator RunNightPerf()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            yield return Test("night", NightPerf());
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator NightPerf()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            var rendering = world.GetExistingSystemManaged<Game.Rendering.RenderingSystem>();
            SpikeReport.Set("night.maxLightCount", rendering?.maxLightCount);

            // The game's own light effects: how many shine into the volumetric fog, their ranges and LOD bias.
            var effects = m_Runtime.PrefabSystem;
            var lightEffects = new List<(string name, LightEffect light)>();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<LightEffectData>(), ComponentType.ReadOnly<PrefabData>()))
            using (var entities = q.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                foreach (var e in entities)
                {
                    if (!effects.TryGetPrefab(em.GetComponentData<PrefabData>(e), out PrefabBase p) || p.GetComponent<LightEffect>() is not LightEffect l) continue;
                    lightEffects.Add((p.name, l));
                }
            }
            var vanilla = lightEffects.Where(l => !l.name.StartsWith("SignBuilder")).ToList();
            SpikeReport.Set("night.vanillaLights", $"n={vanilla.Count} volumetric={vanilla.Count(l => l.light.m_UseVolumetric)} lodBias>0={vanilla.Count(l => l.light.m_LodBias > 0f)} " +
                $"rangeMedian={(vanilla.Count == 0 ? 0 : vanilla.Select(l => l.light.m_Range).OrderBy(r => r).ElementAt(vanilla.Count / 2)):F0}");
            foreach (var l in vanilla.Where(l => l.name.ToLowerInvariant().Contains("street") || l.name.ToLowerInvariant().Contains("lamp")).Take(8))
            {
                SpikeReport.Set($"night.vanilla.{l.name}", $"type={l.light.m_Type} range={l.light.m_Range} volumetric={l.light.m_UseVolumetric} fade={l.light.m_VolumetricFadeDistance} lodBias={l.light.m_LodBias} intensity={l.light.m_Intensity}{l.light.m_LightUnit}");
            }
            var ours = lightEffects.FirstOrDefault(l => l.name.StartsWith("SignBuilder")).light;

            // Lit gantries, three signs each, along the nearest highways.
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(em, near, 24);
            var placed = new List<Entity>();
            var fixtures = 0;
            foreach (var (edge, t) in spots)
            {
                var options = Options("doubleTruss", "span");
                options.Lights = true;
                var before = SignPlacementToolSystem.LastPlaced;
                yield return PlaceAndWait(() => tool.PlaceForTest(edge, t,
                    new List<JObject> { Template("guide-exit-only"), Template("services-blue"), Template("guide-exit-only") }, options));
                var record = SignPlacementToolSystem.LastPlaced;
                if (record == before) continue;
                placed.AddRange(InstancesOf(record.PrefabName));
                fixtures += (record.LightSpots?.Length ?? 0) / 6;
            }
            ours ??= effects.TryGetPrefab(new PrefabID(nameof(EffectPrefab), "SignBuilder_SignLight3"), out var found) ? ((EffectPrefab)found).GetComponent<LightEffect>() : null;
            SpikeReport.Set("night.placed", $"gantries={placed.Count} fixtures={fixtures} trackedSignsInCity={SignLightSystem.s_Signs.Count}");
            SpikeReport.Set("night.ourLight", ours == null ? "not found" : $"range={ours.m_Range} volumetric={ours.m_UseVolumetric} fade={ours.m_VolumetricFadeDistance} lodBias={ours.m_LodBias}");
            if (placed.Count == 0) yield break;

            // Looking along the gantries from above, as a player watching the highway at night.
            var center = placed.Select(e => em.GetComponentData<Game.Objects.Transform>(e).m_Position).Aggregate((a, b) => a + b) / placed.Count;
            SetCamera(center, 30f, 35f, 450f);
            yield return new WaitForSecondsRealtime(4f);

            var planet = world.GetOrCreateSystemManaged<Game.Simulation.PlanetarySystem>();
            var lighting = world.GetExistingSystemManaged<Game.Rendering.LightingSystem>();
            var lights = world.GetOrCreateSystemManaged<SignLightSystem>();
            IEnumerator Phase(string name, float time)
            {
                planet.overrideTime = true;
                planet.normalizedTime = time;
                yield return new WaitForSecondsRealtime(4f);
                var sample = SampleFrames(12f);
                while (sample.MoveNext()) yield return sample.Current;
                SpikeReport.Set($"night.{name}", $"{sample.Current} daylight={lighting.dayLightBrightness:F2}");
                SpikeReport.Screenshot($"night_{name}.png");
                SpikeReport.Write();
                yield return new WaitForSecondsRealtime(1f);
            }
            void Fixtures(bool on)
            {
                lights.Enabled = on;
                foreach (var e in SignLightSystem.s_Signs)
                {
                    if (!em.Exists(e)) continue;
                    var state = new Game.Objects.StreetLight { m_State = on ? Game.Objects.StreetLightState.None : Game.Objects.StreetLightState.TurnedOff };
                    if (em.HasComponent<Game.Objects.StreetLight>(e)) em.SetComponentData(e, state);
                    else em.AddComponentData(e, state);
                    if (!em.HasComponent<EffectsUpdated>(e)) em.AddComponent<EffectsUpdated>(e);
                }
            }

            yield return Phase("day", 0.5f);
            yield return Phase("nightLit", 0.02f);
            if (ours != null)
            {
                ours.m_UseVolumetric = false;
                yield return Phase("nightLitNoVolumetric", 0.02f);
                ours.m_UseVolumetric = true;
                yield return Phase("nightLitAgain", 0.02f);
            }
            Fixtures(false);
            yield return Phase("nightFixturesOff", 0.02f);
            Fixtures(true);
            foreach (var e in placed) m_Runtime.Delete(e);
            yield return new WaitForSecondsRealtime(2f);
            yield return Phase("nightRemoved", 0.02f);
            yield return Phase("dayRemoved", 0.5f);
            planet.overrideTime = false;
        }
    }
}

using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace SignBuilder.Systems
{
    /// <summary>
    /// Switches LED lettering on at night and off by day. LED faces are lights of no purpose (RuntimePrefabs.AddGlow),
    /// which the game never animates; this sets their state from the game's clock, night being 18:00 to 06:00 as for
    /// the game's own night lights, fading over a couple of seconds. Runs just before the light states are uploaded.
    /// </summary>
    public partial class LedLightSystem : GameSystemBase
    {
        private const float kFadeSeconds = 2f;

        /// <summary>Checks for newly placed LED signs this often (in frames) while nothing is fading.</summary>
        private const int kSteadyInterval = 30;

        private static readonly HashSet<PrefabBase> s_Prefabs = new();

        /// <summary>A structure prefab with LED lettering.</summary>
        public static void Track(PrefabBase prefab) => s_Prefabs.Add(prefab);

#if TESTS
        /// <summary>Overrides the clock (the tests only darken the sky, which leaves the simulation's time as it is).</summary>
        public static bool? ForceNight;
#endif

        private TimeSystem m_Time;
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_Lit;
        private float m_Level = -1f;
        private int m_Frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Time = World.GetOrCreateSystemManaged<TimeSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Lit = GetEntityQuery(ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadWrite<LightState>(), ComponentType.ReadWrite<Emissive>(),
                ComponentType.Exclude<Deleted>());
        }

        protected override void OnUpdate()
        {
            if (s_Prefabs.Count == 0) return;
            var night = m_Time.normalizedTime >= 0.75f || m_Time.normalizedTime < 0.25f;
#if TESTS
            if (ForceNight.HasValue) night = ForceNight.Value;
#endif
            var target = night ? 1f : 0f;
            if (m_Level != target) m_Level = m_Level < 0f ? target : Mathf.MoveTowards(m_Level, target, UnityEngine.Time.deltaTime / kFadeSeconds);
            else if (++m_Frame % kSteadyInterval != 0) return;
            Apply(m_Level);
        }

        private void Apply(float level)
        {
            var prefabs = new HashSet<Entity>();
            foreach (var prefab in s_Prefabs)
            {
                if (m_PrefabSystem.TryGetEntity(prefab, out var entity)) prefabs.Add(entity);
            }
            using var entities = m_Lit.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                if (!prefabs.Contains(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab)) continue;
                var lights = EntityManager.GetBuffer<LightState>(entity);
                var changed = false;
                for (var i = 0; i < lights.Length; i++)
                {
                    if (lights[i].m_Intensity == level) continue;
                    var light = lights[i];
                    light.m_Intensity = level;
                    lights[i] = light;
                    changed = true;
                }
                if (!changed) continue;
                var emissives = EntityManager.GetBuffer<Emissive>(entity);
                for (var i = 0; i < emissives.Length; i++)
                {
                    var emissive = emissives[i];
                    emissive.m_Updated = true;
                    emissives[i] = emissive;
                }
            }
        }
    }
}

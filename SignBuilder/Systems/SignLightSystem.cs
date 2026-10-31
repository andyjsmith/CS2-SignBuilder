using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SignBuilder.Systems
{
    /// <summary>
    /// Switches sign lights on when it gets dark, the way the game switches street lights: by the daylight actually
    /// rendered (so the day/night cycle, the debug time of day and photo mode all agree), each sign at its own
    /// threshold so they come on over the dusk rather than all at once.
    ///
    /// Light fixtures are effects forbidden while the object's <see cref="StreetLight"/> state is TurnedOff (as on
    /// street light poles); LED lettering is lights of no purpose (RuntimePrefabs.AddGlow), which the game never
    /// animates, so their state is set here. Runs just before light states are uploaded.
    /// </summary>
    public partial class SignLightSystem : GameSystemBase
    {
        /// <summary>Checks for newly placed signs and changed daylight this often, in frames.</summary>
        private const int kInterval = 15;

        private static readonly HashSet<PrefabBase> s_Prefabs = new();

        /// <summary>A structure prefab with sign lights or LED lettering.</summary>
        public static void Track(PrefabBase prefab) => s_Prefabs.Add(prefab);

        private LightingSystem m_Lighting;
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_Objects;
        private int m_Frame;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Lighting = World.GetOrCreateSystemManaged<LightingSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Objects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Object>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnUpdate()
        {
            if (s_Prefabs.Count == 0 || ++m_Frame % kInterval != 0) return;
            var prefabs = new HashSet<Entity>();
            foreach (var prefab in s_Prefabs)
            {
                if (m_PrefabSystem.TryGetEntity(prefab, out var entity)) prefabs.Add(entity);
            }
            var daylight = m_Lighting.dayLightBrightness;
            using var entities = m_Objects.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                if (!prefabs.Contains(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab)) continue;
                // On below 20-30 % daylight, the same per-object spread as street lights.
                var threshold = 0.2f + 0.1f * (math.hash(new int2(entity.Index, entity.Version)) % 1000u) / 1000f;
                var on = daylight < threshold;
                SetFixtures(entity, on);
                SetLettering(entity, on ? 1f : 0f);
            }
        }

        private void SetFixtures(Entity entity, bool on)
        {
            // Placement previews (Temp) keep whatever they were created with; placed signs carry the state.
            if (EntityManager.HasComponent<Temp>(entity)) return;
            var state = on ? StreetLightState.None : StreetLightState.TurnedOff;
            if (!EntityManager.HasComponent<StreetLight>(entity))
            {
                EntityManager.AddComponentData(entity, new StreetLight { m_State = state });
            }
            else if (EntityManager.GetComponentData<StreetLight>(entity).m_State == state)
            {
                return;
            }
            else
            {
                EntityManager.SetComponentData(entity, new StreetLight { m_State = state });
            }
            if (!EntityManager.HasComponent<EffectsUpdated>(entity)) EntityManager.AddComponent<EffectsUpdated>(entity);
        }

        private void SetLettering(Entity entity, float level)
        {
            if (!EntityManager.HasBuffer<LightState>(entity) || !EntityManager.HasBuffer<Emissive>(entity)) return;
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
            if (!changed) return;
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

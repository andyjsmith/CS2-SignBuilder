using System.Collections.Generic;
using Colossal.Serialization.Entities;
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
    /// Switches sign light fixtures on when it gets dark, the way the game switches street lights: by the daylight
    /// actually rendered (so the day/night cycle, the debug time of day and photo mode all agree), each sign at its own
    /// threshold so they come on over the dusk rather than all at once.
    ///
    /// Fixtures are effects forbidden while the object's <see cref="StreetLight"/> state is TurnedOff, as on street
    /// light poles. The game sets that state only on lamps a road or building owns; a sign stands alone, so it is set
    /// here. (LED lettering needs nothing: it is a neon sign the game keeps lit, see RuntimePrefabs.AddGlow.)
    ///
    /// Only the signs themselves are visited: <see cref="SignLightTrackingSystem"/> collects them (every object once
    /// after a city loads, then only objects as they are created), never the whole city on a timer.
    /// </summary>
    public partial class SignLightSystem : GameSystemBase
    {
        /// <summary>Checks for changed daylight this often, in frames.</summary>
        private const int kInterval = 15;

        private static readonly HashSet<PrefabBase> s_Prefabs = new();

        /// <summary>Placed signs whose prefab has light fixtures (placement previews keep the state they start with).</summary>
        internal static readonly HashSet<Entity> s_Signs = new();

        /// <summary>A structure prefab with light fixtures.</summary>
        public static void Track(PrefabBase prefab)
        {
            s_Prefabs.Add(prefab);
            SignLightTrackingSystem.PrefabsChanged = true;
        }

        internal static IReadOnlyCollection<PrefabBase> Prefabs => s_Prefabs;

        private LightingSystem m_Lighting;
        private int m_Frame;
        private readonly List<Entity> m_Gone = new();

#if TESTS
        /// <summary>Test hook: the old way, every object in the city looked at on each pass.</summary>
        public static bool FullScanForTest { get; set; }
        private EntityQuery m_AllObjects;
        private PrefabSystem m_PrefabSystem;
        public static double LastPassMs { get; private set; }
        public static int LastPassVisited { get; private set; }
        public static int Passes { get; private set; }
#endif

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Lighting = World.GetOrCreateSystemManaged<LightingSystem>();
#if TESTS
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_AllObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Object>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
#endif
        }

        protected override void OnUpdate()
        {
            if (s_Prefabs.Count == 0 || ++m_Frame % kInterval != 0) return;
#if TESTS
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Passes++;
            if (FullScanForTest)
            {
                LegacyPass();
                LastPassMs = watch.Elapsed.TotalMilliseconds;
                return;
            }
            LastPassVisited = s_Signs.Count;
#endif
            var daylight = m_Lighting.dayLightBrightness;
            m_Gone.Clear();
            foreach (var entity in s_Signs)
            {
                if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity))
                {
                    m_Gone.Add(entity);
                    continue;
                }
                Apply(entity, daylight);
            }
            foreach (var entity in m_Gone) s_Signs.Remove(entity);
#if TESTS
            LastPassMs = watch.Elapsed.TotalMilliseconds;
#endif
        }

#if TESTS
        /// <summary>The pass as it was before 1.0.1: every object, its prefab read one at a time.</summary>
        private void LegacyPass()
        {
            var prefabs = new HashSet<Entity>();
            foreach (var prefab in s_Prefabs)
            {
                if (m_PrefabSystem.TryGetEntity(prefab, out var entity)) prefabs.Add(entity);
            }
            var daylight = m_Lighting.dayLightBrightness;
            using var entities = m_AllObjects.ToEntityArray(Allocator.Temp);
            LastPassVisited = entities.Length;
            foreach (var entity in entities)
            {
                if (!prefabs.Contains(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab)) continue;
                Apply(entity, daylight);
            }
        }
#endif

        private void Apply(Entity entity, float daylight)
        {
            // On below 20-30 % daylight, the same per-object spread as street lights.
            var threshold = 0.2f + 0.1f * (math.hash(new int2(entity.Index, entity.Version)) % 1000u) / 1000f;
            var on = daylight < threshold;
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
    }

    /// <summary>
    /// Collects the objects <see cref="SignLightSystem"/> switches: every object once after a city loads, then only
    /// objects created this frame (they carry the game's Created tag until the frame's modifications end).
    /// </summary>
    public partial class SignLightTrackingSystem : GameSystemBase
    {
        /// <summary>A prefab was tracked whose entity is not known here yet.</summary>
        internal static bool PrefabsChanged;

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_Objects;
        private EntityQuery m_Created;
        private bool m_FullPass;
        private readonly HashSet<Entity> m_PrefabEntities = new();

#if TESTS
        public static int FullPasses { get; private set; }
        public static double LastFullPassMs { get; private set; }
        public static int LastFullPassObjects { get; private set; }
#endif

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Objects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Object>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_Created = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<Created>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            SignLightSystem.s_Signs.Clear();
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // The loaded city's signs (their prefabs were registered before it loaded).
            m_FullPass = true;
            PrefabsChanged = true;
        }

        protected override void OnUpdate()
        {
            if (SignLightSystem.Prefabs.Count == 0) return;
            if (PrefabsChanged)
            {
                // A prefab added during play gets its entity a frame or so later, always before its first object.
                var resolved = 0;
                foreach (var prefab in SignLightSystem.Prefabs)
                {
                    if (!m_PrefabSystem.TryGetEntity(prefab, out var entity)) continue;
                    m_PrefabEntities.Add(entity);
                    resolved++;
                }
                PrefabsChanged = resolved < SignLightSystem.Prefabs.Count;
            }
            if (m_FullPass)
            {
                m_FullPass = false;
#if TESTS
                var watch = System.Diagnostics.Stopwatch.StartNew();
                LastFullPassObjects = Collect(m_Objects);
                FullPasses++;
                LastFullPassMs = watch.Elapsed.TotalMilliseconds;
#else
                Collect(m_Objects);
#endif
            }
            else if (!m_Created.IsEmptyIgnoreFilter)
            {
                Collect(m_Created);
            }
        }

        private int Collect(EntityQuery query)
        {
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var refs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                if (m_PrefabEntities.Contains(refs[i].m_Prefab)) SignLightSystem.s_Signs.Add(entities[i]);
            }
            return entities.Length;
        }
    }
}

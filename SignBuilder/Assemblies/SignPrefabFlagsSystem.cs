using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// Keeps sign structures from ever being Overridable or Brushable, and shows any sign a save has hidden.
    ///
    /// The game's ObjectInitializeSystem makes every plain static prop Overridable (an object overlapping a road is
    /// hidden in favor of the road) and Brushable (dropped onto the terrain when the ground under it changes). A gantry
    /// always spans a road and many signs stand at road height, so neither may apply. The flags are cleared right after
    /// that system, in the same prefab update, so the game never sees a sign prefab with them: before, they were cleared
    /// a frame later, and a road updated on that frame (a mod updating its roads while a city loads, which happens on
    /// the frame every sign prefab is initialized) hid every sign overlapping a road. A hidden sign then stayed hidden
    /// for good, also in the save: the game re-checks hidden objects only while their prefab is Overridable.
    ///
    /// Runs on every prefab the game (re)creates: registered before a city loads, newly built, rebuilt from a save, or
    /// re-created by any mod through PrefabSystem.UpdatePrefab.
    /// </summary>
    public partial class SignPrefabFlagsSystem : GameSystemBase
    {
        public const string kPrefix = "SignBuilder_A_";

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_CreatedPrefabs;
        private EntityQuery m_Hidden;
        private bool m_ShowHidden;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_CreatedPrefabs = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<StaticObjectData>(), ComponentType.ReadWrite<ObjectGeometryData>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_Hidden = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Overridden>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_ShowHidden = mode.IsGameOrEditor();
        }

        protected override void OnUpdate()
        {
            if (!m_CreatedPrefabs.IsEmptyIgnoreFilter) ClearFlags();
            if (m_ShowHidden)
            {
                m_ShowHidden = false;
                ShowHidden();
            }
        }

        private void ClearFlags()
        {
            using var entities = m_CreatedPrefabs.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                if (!m_PrefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(entity), out PrefabBase prefab) || !prefab.name.StartsWith(kPrefix)) continue;
                var data = EntityManager.GetComponentData<ObjectGeometryData>(entity);
                data.m_Flags &= ~(Game.Objects.GeometryFlags.Overridable | Game.Objects.GeometryFlags.Brushable);
                EntityManager.SetComponentData(entity, data);
            }
        }

        /// <summary>
        /// Signs a save holds as hidden (Overridden) were hidden by the bug above; the game never shows them again on its
        /// own, so they are shown here. Updated makes the game index and draw them again.
        /// </summary>
        private void ShowHidden()
        {
            if (m_Hidden.IsEmptyIgnoreFilter) return;
            using var entities = m_Hidden.ToEntityArray(Allocator.Temp);
            using var refs = m_Hidden.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            var isSign = new Dictionary<Entity, bool>();
            var shown = new List<Entity>();
            for (var i = 0; i < entities.Length; i++)
            {
                var prefab = refs[i].m_Prefab;
                if (!isSign.TryGetValue(prefab, out var sign))
                {
                    var name = AssemblySaveSystem.PrefabNameOf(EntityManager, m_PrefabSystem, prefab);
                    isSign[prefab] = sign = name != null && name.StartsWith(kPrefix);
                }
                if (sign) shown.Add(entities[i]);
            }
            if (shown.Count == 0) return;
            using var array = new NativeArray<Entity>(shown.ToArray(), Allocator.Temp);
            EntityManager.RemoveComponent<Overridden>(array);
            EntityManager.AddComponent<Updated>(array);
            Mod.log.Info($"Showed {shown.Count} signs the game had hidden (Overridden)");
        }
    }
}

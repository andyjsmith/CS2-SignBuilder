using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using SignBuilder.Spike;
using Unity.Entities;
using Unity.Mathematics;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// Keeps assembly prefabs alive across sessions: before any save is deserialised it re-registers
    /// every recorded assembly from its stored mesh/material asset ids (so saved instances resolve),
    /// and once prefabs are initialised it clears the Overridable flag (a gantry always overlaps a
    /// road, and Overridable props get hidden by roads).
    /// </summary>
    public partial class AssemblyBootstrapSystem : GameSystemBase
    {
        private static readonly List<StaticObjectPrefab> s_Pending = new();
        private static bool s_Registered;

        public static void TrackNewPrefab(StaticObjectPrefab prefab)
        {
            if (prefab != null) s_Pending.Add(prefab);
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            if (s_Registered || !mode.IsGameOrEditor()) return;
            s_Registered = true;
            var prefabs = new RuntimePrefabs();
            int ok = 0, missing = 0;
            foreach (var record in AssemblyLibrary.All)
            {
                if (!AssetDatabase.global.TryGetAsset(Colossal.Hash128.Parse(record.GeometryId), out GeometryAsset geometry))
                {
                    missing++;
                    continue;
                }
                var surfaces = new List<SurfaceAsset>();
                foreach (var id in record.SurfaceIds)
                {
                    if (AssetDatabase.global.TryGetAsset(Colossal.Hash128.Parse(id), out SurfaceAsset surface)) surfaces.Add(surface);
                }
                if (surfaces.Count != record.SurfaceIds.Count)
                {
                    missing++;
                    continue;
                }
                var b = record.Bounds;
                var bounds = new Bounds3(new float3(b[0], b[1], b[2]), new float3(b[3], b[4], b[5]));
                TrackNewPrefab(prefabs.Register(record.PrefabName, geometry, surfaces.ToArray(), bounds, record.IndexCount, record.VertexCount));
                ok++;
            }
            Mod.log.Info($"Registered {ok} sign assemblies before load ({missing} with missing assets)");
        }

        protected override void OnUpdate()
        {
            if (s_Pending.Count == 0) return;
            for (var i = s_Pending.Count - 1; i >= 0; i--)
            {
                if (ClearOverridableNow(s_Pending[i])) s_Pending.RemoveAt(i);
            }
        }

        /// <summary>Clears Overridable once the prefab is initialised; returns false if it is not yet.</summary>
        public static bool ClearOverridableNow(StaticObjectPrefab prefab)
        {
            if (prefab == null) return true;
            var world = World.DefaultGameObjectInjectionWorld;
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
            var em = world.EntityManager;
            var entity = prefabSystem.GetEntity(prefab);
            if (entity == Entity.Null || !em.HasComponent<ObjectGeometryData>(entity)) return false;
            var data = em.GetComponentData<ObjectGeometryData>(entity);
            data.m_Flags &= ~Game.Objects.GeometryFlags.Overridable;
            em.SetComponentData(entity, data);
            return true;
        }
    }
}

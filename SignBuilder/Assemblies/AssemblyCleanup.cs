using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using SignBuilder.Core;
using Unity.Collections;
using Unity.Entities;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// Deletes sign structures nobody uses: their generated mesh, face materials and textures (the shared steel
    /// material stays) and their library entry. Safe for other cities: saves carry their structures' recipes and
    /// rebuild anything missing on load.
    /// </summary>
    public static class AssemblyCleanup
    {
        /// <summary>A structure was replaced (edit saved, or moved): drop the old one once its object is gone.</summary>
        public static void AfterReplace(AssemblyRecord old)
        {
            if (old != null) SignBuilderRuntime.Run(RemoveLater(new[] { old }));
        }

        /// <summary>Options button: every structure without an object in the open city.</summary>
        public static void RemoveUnusedInCity()
        {
            if (!Game.SceneFlow.GameManager.instance.gameMode.IsGame()) return;
            var removed = Remove(AssemblyLibrary.All.ToList());
            Mod.log.Info($"Cleanup: removed {removed} unused sign structures");
        }

        private static IEnumerator RemoveLater(IReadOnlyList<AssemblyRecord> records)
        {
            // Let the deletion go through the game's systems first.
            for (var i = 0; i < 30; i++) yield return null;
            Remove(records);
        }

        /// <summary>Removes the records (and assets) among <paramref name="records"/> with no object in the world; returns how many.</summary>
        public static int Remove(IReadOnlyList<AssemblyRecord> records)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var prefabs = world.GetOrCreateSystemManaged<PrefabSystem>();
            var used = new HashSet<string>();
            using (var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            }))
            using (var refs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                foreach (var prefab in refs.Select(r => r.m_Prefab).Distinct())
                {
                    var name = SaveSystemNames(em, prefabs, prefab);
                    if (name != null) used.Add(name);
                }
            }
            var db = SignBuilderRuntime.Database;
            var count = 0;
            foreach (var record in records)
            {
                if (used.Contains(record.PrefabName)) continue;
                DeleteAsset<GeometryAsset>(db, record.GeometryId, null);
                if (record.LodGeometryId != null) DeleteAsset<GeometryAsset>(db, record.LodGeometryId, null);
                // Surface 0 is the shared steel; the rest are this structure's sign faces (with their textures).
                foreach (var id in record.SurfaceIds.Skip(1)) DeleteAsset<SurfaceAsset>(db, id, s =>
                {
                    s.LoadProperties(false);
                    foreach (var texture in s.textures.Values.Where(t => t != null)) db.DeleteAsset(texture);
                });
                AssemblyLibrary.Remove(record);
                count++;
            }
            return count;
        }

        private static string SaveSystemNames(EntityManager em, PrefabSystem prefabs, Entity prefab) =>
            AssemblySaveSystem.PrefabNameOf(em, prefabs, prefab);

        private static void DeleteAsset<T>(ILocalAssetDatabase db, string id, System.Action<T> before) where T : IAssetData
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                if (!db.TryGetAsset(Colossal.Hash128.Parse(id), out T asset)) return;
                before?.Invoke(asset);
                db.DeleteAsset(asset);
            }
            catch (System.Exception e)
            {
                Mod.log.Warn($"Cleanup: could not delete {typeof(T).Name} {id}: {e.Message}");
            }
        }
    }
}

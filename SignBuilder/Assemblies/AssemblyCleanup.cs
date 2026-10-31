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
using SignBuilder.Geometry;
using SignBuilder.Tools;

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
            var unused = records.Where(r => !used.Contains(r.PrefabName)).ToList();
            // Face materials are shared between structures with identical faces: keep those another structure uses.
            var gone = new HashSet<AssemblyRecord>(unused);
            var shared = new HashSet<string>(AssemblyLibrary.All.Where(r => !gone.Contains(r)).SelectMany(r => r.SurfaceIds));
            var count = 0;
            foreach (var record in unused)
            {
                DeleteAsset<GeometryAsset>(db, record.GeometryId, null);
                if (record.LodGeometryId != null) DeleteAsset<GeometryAsset>(db, record.LodGeometryId, null);
                // Surface 0 is the shared steel; the rest are this structure's sign faces (with their textures).
                foreach (var id in record.SurfaceIds.Skip(1).Distinct().Where(id => shared.Add(id))) DeleteAsset<SurfaceAsset>(db, id, s =>
                {
                    s.LoadProperties(false);
                    foreach (var texture in s.textures.Values.Where(t => t != null)) Delete(texture);
                });
                AssemblyLibrary.Remove(record);
                count++;
            }
            return count;
        }

        /// <summary>
        /// Deletes every mesh, material and texture in Sign Builder's asset cache that no recorded structure (nor the
        /// shared steel) uses: left behind by structures dropped from the library or rebuilt with new assets. Runs once
        /// per game start, before the structures' prefabs are registered.
        /// </summary>
        public static void SweepOrphans()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var keep = new HashSet<Colossal.Hash128>();
            void KeepSurface(string id)
            {
                if (string.IsNullOrEmpty(id) || !Colossal.Hash128.TryParse(id, out var guid) || !keep.Add(guid)) return;
                try
                {
                    if (!AssetDatabase.global.TryGetAsset(guid, out SurfaceAsset surface)) return;
                    surface.LoadProperties(false);
                    foreach (var texture in surface.textures.Values.Where(t => t != null)) keep.Add(texture.id.guid);
                }
                catch (System.Exception e)
                {
                    Mod.log.Warn($"Cleanup: could not read surface {id}: {e.Message}");
                }
            }
            void Keep(string id)
            {
                if (!string.IsNullOrEmpty(id) && Colossal.Hash128.TryParse(id, out var guid)) keep.Add(guid);
            }
            foreach (var record in AssemblyLibrary.All)
            {
                Keep(record.GeometryId);
                Keep(record.LodGeometryId);
                foreach (var id in record.SurfaceIds ?? new List<string>()) KeepSurface(id);
            }
            var steelFile = System.IO.Path.Combine(SignBuilderRuntime.DataDir, "steel.id");
            if (System.IO.File.Exists(steelFile)) KeepSurface(System.IO.File.ReadAllText(steelFile).Trim());

            // Every cached asset has a .cid file next to it holding its id. Reading those is far quicker than listing
            // every asset the game knows (seconds) to find ours.
            var files = 0;
            var removed = 0;
            foreach (var cid in System.IO.Directory.EnumerateFiles(SignBuilderCache.RootPath, "*.cid", System.IO.SearchOption.AllDirectories))
            {
                var path = cid.Substring(0, cid.Length - ".cid".Length);
                var extension = System.IO.Path.GetExtension(path);
                if (extension != ".Geometry" && extension != ".Surface" && extension != ".Texture") continue;
                files++;
                try
                {
                    if (!Colossal.Hash128.TryParse(System.IO.File.ReadAllText(cid).Trim(), out var guid) || keep.Contains(guid)) continue;
                    if (AssetDatabase.global.TryGetAsset(guid, out IAssetData asset)) Delete(asset);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    if (System.IO.File.Exists(cid)) System.IO.File.Delete(cid);
                    removed++;
                }
                catch (System.Exception e)
                {
                    Mod.log.Warn($"Cleanup: could not delete {System.IO.Path.GetFileName(path)}: {e.Message}");
                }
            }
            Mod.log.Info($"Cleanup: deleted {removed} unused cached assets of {files} ({keep.Count} in use) in {watch.ElapsedMilliseconds} ms");
        }

        private static string SaveSystemNames(EntityManager em, PrefabSystem prefabs, Entity prefab) =>
            AssemblySaveSystem.PrefabNameOf(em, prefabs, prefab);

        /// <summary>
        /// Deletes an asset and its files. Through the asset's own database (the instance that indexed it), then the
        /// files themselves: deleting through another handle only drops the asset from the index and leaves its files
        /// on disk (which is how the cache grew to gigabytes).
        /// </summary>
        private static void Delete(IAssetData asset)
        {
            var path = (asset as AssetData)?.path;
            (asset.database ?? SignBuilderRuntime.Database).DeleteAsset(asset);
            if (string.IsNullOrEmpty(path)) return;
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            if (System.IO.File.Exists(path + ".cid")) System.IO.File.Delete(path + ".cid");
        }

        private static void DeleteAsset<T>(ILocalAssetDatabase db, string id, System.Action<T> before) where T : IAssetData
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                if (!AssetDatabase.global.TryGetAsset(Colossal.Hash128.Parse(id), out T asset)) return;
                before?.Invoke(asset);
                Delete(asset);
            }
            catch (System.Exception e)
            {
                Mod.log.Warn($"Cleanup: could not delete {typeof(T).Name} {id}: {e.Message}");
            }
        }
    }
}

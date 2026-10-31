using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SignBuilder.Core;
using Unity.Collections;
using Unity.Entities;
using SignBuilder.Geometry;
using SignBuilder.Tools;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// Carries one assembly's recipe (designs, support spec, sign positions) inside the save, so a city
    /// shared with another player rebuilds its signs there. Same approach as Road Builder: entities with
    /// this component are created just before saving and removed again afterwards.
    /// </summary>
    public struct AssemblySaveData : IComponentData, ISerializable
    {
        public const ushort kVersion = 1;

        public FixedString128Bytes PrefabName;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            var record = AssemblyLibrary.FromPrefabName(PrefabName.ToString()) ?? AssemblyRestoreSystem.Unresolved(PrefabName.ToString());
            writer.Write(record == null ? "" : record.ToPortable().ToString(Formatting.None));
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out ushort version);
            reader.Read(out string json);
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var record = AssemblyRecord.FromPortable(JObject.Parse(json));
                PrefabName = record.PrefabName;
                AssemblyRestoreSystem.Loaded(record);
            }
            catch (Exception e)
            {
                Mod.log.Warn($"Could not read a saved sign assembly (data version {version}): {e.Message}");
            }
        }
    }

    /// <summary>Before saving: one <see cref="AssemblySaveData"/> entity per assembly placed in the city.</summary>
    public partial class AssemblySaveSystem : GameSystemBase
    {
        private EntityQuery m_Objects;
        private PrefabSystem m_PrefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Objects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnUpdate()
        {
            var names = new HashSet<string>();
            using (var refs = m_Objects.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                var seen = new HashSet<Entity>();
                foreach (var r in refs)
                {
                    if (!seen.Add(r.m_Prefab)) continue;
                    var name = PrefabNameOf(EntityManager, m_PrefabSystem, r.m_Prefab);
                    // Assemblies that could not be rebuilt here keep their recipe, so re-saving never loses them.
                    if (name != null && (AssemblyLibrary.FromPrefabName(name) != null || AssemblyRestoreSystem.Unresolved(name) != null)) names.Add(name);
                }
            }
            foreach (var name in names)
            {
                var entity = EntityManager.CreateEntity();
                EntityManager.AddComponentData(entity, new AssemblySaveData { PrefabName = name });
            }
        }

        /// <summary>A prefab entity's name, also for prefabs missing on this PC (kept as "obsolete" by the game).</summary>
        public static string PrefabNameOf(EntityManager em, PrefabSystem prefabSystem, Entity prefab)
        {
            if (!em.TryGetComponent(prefab, out PrefabData data)) return null;
            if (data.m_Index >= 0) return prefabSystem.TryGetPrefab(data, out PrefabBase p) ? p.name : null;
            try { return prefabSystem.GetObsoleteID(data).GetName(); } catch { return null; }
        }
    }

    /// <summary>Removes the save-data entities (after saving, and the ones read from a loaded save).</summary>
    public partial class AssemblySaveCleanupSystem : GameSystemBase
    {
        private EntityQuery m_Query;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(ComponentType.ReadOnly<AssemblySaveData>());
            RequireForUpdate(m_Query);
        }

        protected override void OnUpdate() => EntityManager.DestroyEntity(m_Query);
    }

    /// <summary>
    /// After a city loads: assemblies it uses that this PC does not have (a shared save, or a cleared
    /// cache) are rebuilt from the recipes in the save, and their objects, which the game kept with a
    /// missing prefab, are replaced by new ones at the same spots.
    /// </summary>
    public partial class AssemblyRestoreSystem : GameSystemBase
    {
        private static readonly List<AssemblyRecord> s_Loaded = new();
        private static readonly Dictionary<string, AssemblyRecord> s_Unresolved = new();

        public static AssemblyRecord Unresolved(string prefabName) => s_Unresolved.TryGetValue(prefabName, out var r) ? r : null;
        private PrefabSystem m_PrefabSystem;

        public static int LastRestored { get; private set; } = -1;
        public static int LastFailed { get; private set; }
        public static bool Running { get; private set; }

        public static void Loaded(AssemblyRecord record)
        {
            lock (s_Loaded) s_Loaded.Add(record);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        protected override void OnUpdate() { }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            lock (s_Loaded) s_Loaded.Clear();
            s_Unresolved.Clear();
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            List<AssemblyRecord> records;
            lock (s_Loaded)
            {
                records = s_Loaded.ToList();
                s_Loaded.Clear();
            }
            var missing = records.Where(r => !m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(StaticObjectPrefab), r.PrefabName), out _))
                .GroupBy(r => r.PrefabName).Select(g => g.First()).ToList();
            if (missing.Count == 0) return;
            Mod.log.Info($"Rebuilding {missing.Count} sign assemblies from the save");
            SignBuilderRuntime.Run(Restore(missing));
        }

        private IEnumerator Restore(List<AssemblyRecord> records)
        {
            Running = true;
            int restored = 0, failed = 0;
            var built = new List<AssemblyRecord>();
            foreach (var record in records)
            {
                var ok = false;
                yield return AssemblyBuilder.Rebuild(record, r => ok = r);
                if (ok) built.Add(record);
                else
                {
                    failed++;
                    s_Unresolved[record.PrefabName] = record;
                }
            }
            // Let the new prefabs initialize before spawning their objects.
            for (var i = 0; i < 10; i++) yield return null;
            var runtime = new RuntimePrefabs();
            foreach (var record in built)
            {
                if (!m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(StaticObjectPrefab), record.PrefabName), out var prefab)) continue;
                AssemblyBootstrapSystem.ClearOverridableNow((StaticObjectPrefab)prefab);
                foreach (var orphan in OrphansOf(record.PrefabName))
                {
                    var t = EntityManager.GetComponentData<Game.Objects.Transform>(orphan);
                    runtime.Spawn((StaticObjectPrefab)prefab, t.m_Position, t.m_Rotation);
                    runtime.Delete(orphan);
                    restored++;
                }
            }
            LastRestored = restored;
            LastFailed = failed;
            Running = false;
            Mod.log.Info($"Restored {restored} sign objects from the save ({failed} assemblies failed)");
        }

        /// <summary>Objects whose prefab is missing (obsolete) and was saved under <paramref name="prefabName"/>.</summary>
        private List<Entity> OrphansOf(string prefabName)
        {
            using var query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            var result = new List<Entity>();
            foreach (var e in entities)
            {
                var prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                if (!EntityManager.TryGetComponent(prefab, out PrefabData data) || data.m_Index >= 0) continue;
                if (AssemblySaveSystem.PrefabNameOf(EntityManager, m_PrefabSystem, prefab) == prefabName) result.Add(e);
            }
            return result;
        }
    }
}

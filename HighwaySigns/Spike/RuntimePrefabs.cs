using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.IO.AssetDatabase;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace HighwaySigns.Spike
{
    /// <summary>
    /// Turns generated meshes into real game props (GeometryAsset -> RenderPrefab -> StaticObjectPrefab)
    /// and spawns them through the object definition pipeline. Proven in runs 2-6.
    /// </summary>
    internal sealed class RuntimePrefabs
    {
        private readonly World m_World;
        private readonly PrefabSystem m_PrefabSystem;

        public RuntimePrefabs()
        {
            m_World = World.DefaultGameObjectInjectionWorld;
            m_PrefabSystem = m_World.GetOrCreateSystemManaged<PrefabSystem>();
        }

        public PrefabSystem PrefabSystem => m_PrefabSystem;

        /// <summary>
        /// Writes <paramref name="mesh"/> into <paramref name="database"/> and registers a static object
        /// prefab that renders it with <paramref name="surface"/>. The mesh is destroyed afterwards.
        /// </summary>
        public StaticObjectPrefab Create(string name, Mesh mesh, SurfaceAsset surface, ILocalAssetDatabase database, string folder)
        {
            var renderPrefab = ScriptableObject.CreateInstance<RenderPrefab>();
            renderPrefab.name = name + "_Mesh";
            renderPrefab.surfaceAssets = new[] { surface };
            renderPrefab.bounds = new Bounds3(mesh.bounds.min, mesh.bounds.max);
            renderPrefab.indexCount = (int)mesh.GetIndexCount(0);
            renderPrefab.vertexCount = mesh.vertexCount;
            renderPrefab.meshCount = 1;
            renderPrefab.surfaceArea = mesh.bounds.size.x * mesh.bounds.size.y;

            var path = AssetDataPath.Create(folder, $"{name}_{DateTime.Now:yyyyMMddHHmmss}");
            // Write the file, then dispose so the in-memory data is dropped and the
            // GeometryAssetLoadingSystem streams it back from disk (see run 2).
            using (var geometry = database.AddAsset<GeometryAsset, Mesh[]>(path, new[] { mesh }))
            {
                geometry.Save();
                renderPrefab.geometryAsset = geometry;
            }

            var objectPrefab = ScriptableObject.CreateInstance<StaticObjectPrefab>();
            objectPrefab.name = name;
            objectPrefab.m_Meshes = new[]
            {
                new ObjectMeshInfo { m_Mesh = renderPrefab, m_Position = float3.zero, m_Rotation = quaternion.identity },
            };

            if (!m_PrefabSystem.AddPrefab(renderPrefab) || !m_PrefabSystem.AddPrefab(objectPrefab))
            {
                throw new InvalidOperationException($"PrefabSystem.AddPrefab failed for {name}");
            }
            return objectPrefab;
        }

        /// <summary>
        /// Plain static props are Overridable: anything overlapping a road is hidden in favour of the road
        /// (run 4). Gantries always span roads, so opt out. Must run after prefab initialisation.
        /// Returns the flags before the change so callers can see whether it was still needed.
        /// </summary>
        public string ClearOverridable(StaticObjectPrefab prefab)
        {
            var em = m_World.EntityManager;
            var entity = m_PrefabSystem.GetEntity(prefab);
            if (!em.HasComponent<ObjectGeometryData>(entity)) return "no ObjectGeometryData";
            var data = em.GetComponentData<ObjectGeometryData>(entity);
            var before = data.m_Flags.ToString();
            data.m_Flags &= ~Game.Objects.GeometryFlags.Overridable;
            em.SetComponentData(entity, data);
            return before;
        }

        public void Spawn(StaticObjectPrefab prefab, float3 position, quaternion rotation)
        {
            var em = m_World.EntityManager;
            var prefabEntity = m_PrefabSystem.GetEntity(prefab);
            var definition = em.CreateEntity(
                ComponentType.ReadWrite<CreationDefinition>(),
                ComponentType.ReadWrite<ObjectDefinition>(),
                ComponentType.ReadWrite<Updated>(),
                ComponentType.ReadWrite<Deleted>());
            em.SetComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefabEntity,
                m_Flags = CreationFlags.Permanent,
                m_RandomSeed = 1,
            });
            em.SetComponentData(definition, new ObjectDefinition
            {
                m_ParentMesh = -1,
                m_Position = position,
                m_Rotation = rotation,
                m_LocalPosition = position,
                m_LocalRotation = rotation,
                m_Scale = new float3(1f),
                m_Probability = 100,
                m_PrefabSubIndex = -1,
            });
        }

        /// <summary>Live (non-temp) instances per prefab name, plus how many of them are hidden as Overridden.</summary>
        public Dictionary<string, (int count, int overridden, int withBatches)> CountInstances(IEnumerable<StaticObjectPrefab> prefabs)
        {
            var em = m_World.EntityManager;
            var byEntity = prefabs.Where(p => p != null).ToDictionary(p => m_PrefabSystem.GetEntity(p), p => p.name);
            var result = byEntity.Values.ToDictionary(n => n, _ => (0, 0, 0));
            using var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                if (!byEntity.TryGetValue(em.GetComponentData<PrefabRef>(e).m_Prefab, out var name)) continue;
                var (count, overridden, batches) = result[name];
                count++;
                if (em.HasComponent<Game.Common.Overridden>(e)) overridden++;
                if (em.HasBuffer<Game.Rendering.MeshBatch>(e) && em.GetBuffer<Game.Rendering.MeshBatch>(e, true).Length > 0) batches++;
                result[name] = (count, overridden, batches);
            }
            return result;
        }

        /// <summary>Finds a single-material render mesh whose name matches one of <paramref name="keywords"/> (in priority order).</summary>
        public RenderPrefab FindMesh(string[] keywords, out List<string> matches)
        {
            var em = m_World.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<MeshData>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            var candidates = new List<RenderPrefab>();
            foreach (var entity in entities)
            {
                if (m_PrefabSystem.TryGetPrefab(entity, out RenderPrefab prefab) && prefab.hasGeometryAsset && prefab.materialCount == 1)
                {
                    candidates.Add(prefab);
                }
            }
            matches = new List<string>();
            foreach (var keyword in keywords)
            {
                var hits = candidates.Where(p => p.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                matches.AddRange(hits.Take(8).Select(p => p.name));
                if (hits.Count > 0) return hits[0];
            }
            return null;
        }
    }
}

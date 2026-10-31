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
using SignBuilder.Core;
using SignBuilder.Geometry;
using SignBuilder.Tools;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// Turns generated meshes into real game props (GeometryAsset -> RenderPrefab -> StaticObjectPrefab)
    /// and spawns them through the object definition pipeline. Proven in runs 2-6.
    /// </summary>
    public sealed class RuntimePrefabs
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

        /// <summary>Saves <paramref name="mesh"/> (all submeshes) as a GeometryAsset and returns it, disposed and streamable.</summary>
        public static GeometryAsset SaveGeometry(Mesh mesh, ILocalAssetDatabase database, string folder, string name)
        {
            var path = AssetDataPath.Create(folder, name);
            // Write the file, then dispose so the in-memory data is dropped and the
            // GeometryAssetLoadingSystem streams it back from disk (see spike run 2).
            using var geometry = database.AddAsset<GeometryAsset, Mesh[]>(path, new[] { mesh });
            geometry.Save();
            return geometry;
        }

        /// <summary>
        /// Registers a static object prefab named <paramref name="name"/> that renders an existing
        /// geometry asset with one surface per submesh. Used both for new builds and to re-create
        /// prefabs from stored asset ids before a save loads. Returns null if it already exists. <paramref name="lod"/>
        /// (optional) is a low-detail geometry with the same submeshes, shown at a distance. <paramref name="glowMeshes"/>
        /// are the submeshes whose emissive map shines.
        /// </summary>
        public StaticObjectPrefab Register(string name, GeometryAsset geometry, SurfaceAsset[] surfaces, Bounds3 bounds, int indexCount, int vertexCount,
            (GeometryAsset geometry, int indexCount, int vertexCount)? lod = null, float[] lightSpots = null, int[] glowMeshes = null)
        {
            if (name.StartsWith("SignBuilder_A_")) Core.AssetNames.Add(name);
            if (m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(StaticObjectPrefab), name), out var existing)) return existing as StaticObjectPrefab;
            var renderPrefab = ScriptableObject.CreateInstance<RenderPrefab>();
            renderPrefab.name = name + "_Mesh";
            renderPrefab.geometryAsset = geometry;
            renderPrefab.surfaceAssets = surfaces;
            renderPrefab.bounds = bounds;
            renderPrefab.indexCount = indexCount;
            renderPrefab.vertexCount = vertexCount;
            renderPrefab.meshCount = surfaces.Length;
            var size = bounds.max - bounds.min;
            renderPrefab.surfaceArea = size.x * size.y;
            if (lod != null)
            {
                var lodPrefab = ScriptableObject.CreateInstance<RenderPrefab>();
                lodPrefab.name = name + "_Mesh_LOD1";
                lodPrefab.geometryAsset = lod.Value.geometry;
                lodPrefab.surfaceAssets = surfaces;
                lodPrefab.bounds = bounds;
                lodPrefab.indexCount = lod.Value.indexCount;
                lodPrefab.vertexCount = lod.Value.vertexCount;
                lodPrefab.meshCount = surfaces.Length;
                lodPrefab.surfaceArea = renderPrefab.surfaceArea;
                AddGlow(lodPrefab, glowMeshes);
                if (!m_PrefabSystem.AddPrefab(lodPrefab)) throw new InvalidOperationException($"PrefabSystem.AddPrefab failed for {lodPrefab.name}");
                var lods = renderPrefab.AddComponent<LodProperties>();
                lods.m_LodMeshes = new[] { lodPrefab };
            }
            AddGlow(renderPrefab, glowMeshes);

            var objectPrefab = ScriptableObject.CreateInstance<StaticObjectPrefab>();
            objectPrefab.name = name;
            objectPrefab.m_Meshes = new[] { new ObjectMeshInfo { m_Mesh = renderPrefab, m_Position = float3.zero, m_Rotation = quaternion.identity } };
            if (glowMeshes != null && glowMeshes.Length > 0) Systems.LedLightSystem.Track(objectPrefab);
            if (lightSpots != null && lightSpots.Length >= 6)
            {
                // Real lights: a spot light per fixture, shining onto the sign at night (as street lights do).
                var source = objectPrefab.AddComponent<EffectSource>();
                source.m_Effects = new List<EffectSource.EffectSettings>();
                source.m_AnimationCurves = new List<EffectSource.AnimationProperties>();
                var effect = SignLightEffect();
                for (var i = 0; i + 5 < lightSpots.Length; i += 6)
                {
                    var aim = new float3(lightSpots[i + 3], lightSpots[i + 4], lightSpots[i + 5]);
                    source.m_Effects.Add(new EffectSource.EffectSettings
                    {
                        m_Effect = effect,
                        m_PositionOffset = new float3(lightSpots[i], lightSpots[i + 1], lightSpots[i + 2]),
                        m_Rotation = quaternion.LookRotationSafe(aim, new float3(0, 1, 0)),
                        m_Scale = new float3(1f),
                        m_Intensity = 1f,
                        m_ParentMesh = 0,
                        m_AnimationIndex = -1,
                    });
                }
            }
            if (!m_PrefabSystem.AddPrefab(renderPrefab) || !m_PrefabSystem.AddPrefab(objectPrefab))
            {
                throw new InvalidOperationException($"PrefabSystem.AddPrefab failed for {name}");
            }
            return objectPrefab;
        }

        /// <summary>
        /// The game draws a material's emissive map only for submeshes its mesh lists as lights; without that the map
        /// is ignored. Lists <paramref name="glowMeshes"/> as lights of no purpose, which the game never animates:
        /// <see cref="Systems.LedLightSystem"/> switches them on at night.
        /// </summary>
        private static void AddGlow(RenderPrefab mesh, int[] glowMeshes)
        {
            if (glowMeshes == null || glowMeshes.Length == 0) return;
            var emissive = mesh.AddComponent<EmissiveProperties>();
            emissive.m_SingleLights = glowMeshes.Select(i => new EmissiveProperties.SingleLightMapping
            {
                purpose = EmissiveProperties.Purpose.None,
                intensity = Assemblies.SurfaceFactory.kLedGlow,
                materialId = i,
            }).ToList();
            emissive.m_MultiLights = new List<EmissiveProperties.MultiLightMapping>();
            emissive.m_AnimationCurves = new List<EmissiveProperties.AnimationProperties>();
            emissive.m_SignalGroupAnimations = new List<EmissiveProperties.SignalGroupAnimation>();
        }

        private static EffectPrefab s_SignLight;
        private const string kSignLightName = "SignBuilder_SignLight2";

        /// <summary>
        /// The light every sign fixture uses: a warm-white spot light, on at night only. Created once and registered
        /// as a prefab of its own (effects are prefabs the object's EffectSource refers to). Versioned name: a
        /// changed light is a new prefab, not a stale one from the game's prefab list.
        /// </summary>
        private EffectPrefab SignLightEffect()
        {
            if (s_SignLight != null) return s_SignLight;
            if (m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(EffectPrefab), kSignLightName), out var known) && known is EffectPrefab existing)
            {
                return s_SignLight = existing;
            }
            var effect = ScriptableObject.CreateInstance<EffectPrefab>();
            effect.name = kSignLightName;
            effect.m_Conditions = new EffectCondition { m_RequiredFlags = EffectConditionFlags.Night };
            var light = effect.AddComponent<Game.Prefabs.Effects.LightEffect>();
            light.m_Type = Game.Rendering.LightType.Spot;
            light.m_SpotShape = Game.Rendering.SpotLightShape.Cone;
            // Wide and soft: the beam covers the whole sign face without a white hot spot in the middle.
            light.m_SpotAngle = 125f;
            light.m_InnerSpotPercentage = 5f;
            light.m_Range = 6f;
            light.m_LightUnit = Game.Rendering.LightUnit.Lumen;
            light.m_Intensity = 400f;
            light.m_UseColorTemperature = true;
            light.m_ColorTemperature = 4200f;
            light.m_Color = Color.white;
            light.m_AffectDiffuse = true;
            light.m_AffectSpecular = true;
            light.m_ApplyRangeAttenuation = true;
            light.m_LightDimmer = 1f;
            light.m_LodBias = 1f;
            m_PrefabSystem.AddPrefab(effect);
            return s_SignLight = effect;
        }

        /// <summary>
        /// Deletes a placed object. Marking it Deleted (as bulldozer mods do) lets the game's own
        /// cleanup remove it; a Delete definition instead re-created it (end-to-end run 1).
        /// </summary>
        public void Delete(Entity entity)
        {
            var em = m_World.EntityManager;
            if (!em.Exists(entity) || em.HasComponent<Deleted>(entity)) return;
            em.AddComponent<Deleted>(entity);
        }

        /// <summary>
        /// Plain static props are Overridable: anything overlapping a road is hidden in favor of the road
        /// (run 4). Gantries always span roads, so opt out. Must run after prefab initialization.
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

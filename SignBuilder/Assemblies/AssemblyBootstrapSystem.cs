using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using SignBuilder.Core;
using SignBuilder.Geometry;
using SignBuilder.Tools;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// Keeps assembly prefabs alive across sessions: before any save is deserialized it re-registers
    /// every recorded assembly from its stored mesh/material asset ids (so saved instances resolve).
    /// Their Overridable and Brushable flags are cleared by <see cref="SignPrefabFlagsSystem"/>.
    /// </summary>
    public partial class AssemblyBootstrapSystem : GameSystemBase
    {
        private static bool s_Registered;

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            if (s_Registered || !mode.IsGameOrEditor()) return;
            s_Registered = true;
            AssemblyCleanup.SweepOrphans();
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
                (GeometryAsset, int, int)? lod = null;
                if (record.LodGeometryId != null && AssetDatabase.global.TryGetAsset(Colossal.Hash128.Parse(record.LodGeometryId), out GeometryAsset lodGeometry))
                {
                    lod = (lodGeometry, record.LodIndexCount, record.LodVertexCount);
                }
                prefabs.Register(record.PrefabName, geometry, surfaces.ToArray(), bounds, record.IndexCount, record.VertexCount, lod, record.LightSpots, record.GlowMeshes);
                ok++;
            }
            Mod.log.Info($"Registered {ok} sign assemblies before load ({missing} with missing assets)");
        }

        protected override void OnUpdate()
        {
        }
    }
}

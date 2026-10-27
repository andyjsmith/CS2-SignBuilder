using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;

namespace SignBuilder.Spike
{
    /// <summary>
    /// Fresh-boot reload test: a save holding our gantries references prefabs that only exist once
    /// the mod has created them. This re-creates the spike prefabs (same names) in OnGamePreload,
    /// i.e. before the save is deserialised, and records them so the runner can count instances.
    /// </summary>
    public partial class SpikeBootstrapSystem : GameSystemBase
    {
        /// <summary>Set by the runner from the spike flag; only "phase1" re-creates prefabs.</summary>
        public static string Mode;

        public static readonly List<StaticObjectPrefab> Created = new();
        public static string Log = "";

        private static readonly string[] kSpikePrefabs = { "SignBuilder_truss", "SignBuilder_trussAlt", "SignBuilder_monotube", "SignBuilder_cantilever", "SignBuilder_perfTruss" };

        protected override void OnUpdate()
        {
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            if (Mode != "phase1" || purpose != Purpose.LoadGame || Created.Count > 0) return;
            try
            {
                var runtime = new RuntimePrefabs();
                var pole = runtime.FindMesh(new[] { "LightpolePark" }, out _);
                Log += $"preload: donor={pole?.name}; ";
                if (pole == null) return;
                ILocalAssetDatabase db = AssetDatabase.user;
                var folder = "ImportedData/SignBuilder";
                try
                {
                    db = AssetDatabase<SignBuilderCache>.instance;
                    folder = "Meshes";
                }
                catch (System.Exception e)
                {
                    Log += $"cacheDb unavailable ({e.Message}); ";
                }
                foreach (var name in kSpikePrefabs)
                {
                    if (runtime.PrefabSystem.TryGetPrefab(new PrefabID(nameof(StaticObjectPrefab), name), out _))
                    {
                        Log += $"{name} already exists; ";
                        continue;
                    }
                    var mesh = name.Contains("monotube") ? GantryFactory.Monotube(18f)
                        : name.Contains("cantilever") ? GantryFactory.Cantilever(-8f)
                        : GantryFactory.Truss(name.Contains("perf") ? 22f : 37f);
                    Created.Add(runtime.Create(name, mesh, pole.GetSurfaceAsset(0), db, folder));
                }
                Log += $"created {Created.Count} prefabs before load; ";
            }
            catch (System.Exception e)
            {
                Log += "EXCEPTION " + e;
            }
        }
    }
}

using System;
using System.Collections;
using System.IO;
using Colossal.IO.AssetDatabase;
using SignBuilder.Rendering;
using SignBuilder.Spike;
using UnityEngine;

namespace SignBuilder.Core
{
    /// <summary>
    /// Process-wide services shared by the editor, placement tool and save bootstrap: the mod-owned
    /// asset database, the sign face baker, prefab helpers and the steel material.
    /// </summary>
    public static class SignBuilderRuntime
    {
        private static CoroutineHost s_Host;
        private static SignFaceBaker s_Baker;
        private static SurfaceAsset s_Steel;

        public static string ModDir { get; private set; }
        public static string ResourcesDir => Path.Combine(ModDir, "Resources");
        public static string DataDir => Path.Combine(Application.persistentDataPath, "ModsData", "SignBuilder");

        public static ILocalAssetDatabase Database => AssetDatabase<SignBuilderCache>.instance;
        public const string MeshFolder = "Meshes";
        public const string SurfaceFolder = "Surfaces";

        public static void Initialise(string modDir)
        {
            ModDir = modDir;
            Directory.CreateDirectory(DataDir);
            if (s_Host == null)
            {
                var go = new GameObject("SignBuilder.Runtime");
                UnityEngine.Object.DontDestroyOnLoad(go);
                s_Host = go.AddComponent<CoroutineHost>();
            }
        }

        public static Coroutine Run(IEnumerator routine) => s_Host.StartCoroutine(routine);

        public static SignFaceBaker Baker => s_Baker ??= new SignFaceBaker(ResourcesDir, Path.Combine(SignBuilderCache.RootPath, "Faces"));

        /// <summary>
        /// The tileable steel surface for supports, created once and reused through its asset id
        /// (stored next to the assembly library) so saves never reference a missing material.
        /// </summary>
        public static SurfaceAsset Steel(RuntimePrefabs prefabs)
        {
            if (s_Steel != null) return s_Steel;
            var idFile = Path.Combine(DataDir, "steel.id");
            if (File.Exists(idFile) && AssetDatabase.global.TryGetAsset(Colossal.Hash128.Parse(File.ReadAllText(idFile).Trim()), out SurfaceAsset existing))
            {
                return s_Steel = existing;
            }
            var donor = Donor(prefabs);
            s_Steel = SteelMaterial.Create(Database, SurfaceFolder, donor, s => Mod.log.Info($"[steel] {s}"));
            File.WriteAllText(idFile, s_Steel.id.guid.ToString());
            return s_Steel;
        }

        /// <summary>A vanilla single-material surface whose template (shader setup) our materials copy.</summary>
        public static SurfaceAsset Donor(RuntimePrefabs prefabs)
        {
            var pole = prefabs.FindMesh(new[] { "LightpolePark" }, out _) ?? throw new InvalidOperationException("No donor mesh found");
            return pole.GetSurfaceAsset(0);
        }

        private sealed class CoroutineHost : MonoBehaviour
        {
        }
    }
}

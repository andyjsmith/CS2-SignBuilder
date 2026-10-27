using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SignBuilder.Core;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// A placed sign assembly (support + sign faces) baked into one game prop. Saves reference the
    /// prop by <see cref="PrefabName"/>; the generated mesh and materials live in the mod's asset
    /// database under the stored ids, so the prefab can be re-created before a save loads.
    /// </summary>
    public sealed class AssemblyRecord
    {
        public string Id;
        public string PrefabName => "SignBuilder_A_" + Id;
        public string Support;
        public bool Reverse;
        /// <summary>The signs on this structure, left to right as seen by approaching traffic.</summary>
        public List<JObject> Designs = new();

        /// <summary>Records written before multi-sign support stored a single design.</summary>
        [JsonProperty("Design")]
        private JObject LegacyDesign
        {
            set
            {
                if (value != null && Designs.Count == 0) Designs.Add(value);
            }
        }
        public string GeometryId;
        public List<string> SurfaceIds = new();
        public float[] Bounds; // min xyz, max xyz in assembly-local space
        public int IndexCount;
        public int VertexCount;
        public DateTime Created;
    }

    /// <summary>All assemblies ever built, persisted as ModsData/SignBuilder/assemblies.json.</summary>
    public static class AssemblyLibrary
    {
        private static Dictionary<string, AssemblyRecord> s_Records;

        private static string FilePath => Path.Combine(SignBuilderRuntime.DataDir, "assemblies.json");

        public static IReadOnlyCollection<AssemblyRecord> All
        {
            get
            {
                Load();
                return s_Records.Values;
            }
        }

        public static AssemblyRecord Get(string id)
        {
            Load();
            return id != null && s_Records.TryGetValue(id, out var r) ? r : null;
        }

        public static AssemblyRecord FromPrefabName(string prefabName) =>
            prefabName != null && prefabName.StartsWith("SignBuilder_A_") ? Get(prefabName.Substring("SignBuilder_A_".Length)) : null;

        public static void Add(AssemblyRecord record)
        {
            Load();
            s_Records[record.Id] = record;
            Save();
        }

        private static void Load()
        {
            if (s_Records != null) return;
            s_Records = new Dictionary<string, AssemblyRecord>();
            if (!File.Exists(FilePath)) return;
            try
            {
                var list = JsonConvert.DeserializeObject<List<AssemblyRecord>>(File.ReadAllText(FilePath)) ?? new List<AssemblyRecord>();
                foreach (var r in list) s_Records[r.Id] = r;
            }
            catch (Exception e)
            {
                Mod.log.Error(e, "Could not read the Sign Builder assembly library");
            }
        }

        private static void Save()
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(s_Records.Values.OrderBy(r => r.Created).ToList(), Formatting.Indented));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }
    }
}

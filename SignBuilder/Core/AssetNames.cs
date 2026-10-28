using System.Collections.Generic;
using System.Linq;
using Colossal;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;

namespace SignBuilder.Core
{
    /// <summary>
    /// Display names for the generated sign props ("Sign Builder sign" instead of Assets.NAME[SignBuilder_A_…]),
    /// with the sign's legend as the description. Registered as a dictionary source for every locale (so locale
    /// reloads keep them) and added straight to the active dictionary when a new sign is built.
    /// </summary>
    public sealed class AssetNames : IDictionarySource
    {
        public const string kName = "Sign Builder sign";
        private static AssetNames s_Instance;

        public static void Register()
        {
            var manager = GameManager.instance?.localizationManager;
            if (manager == null || s_Instance != null) return;
            s_Instance = new AssetNames();
            foreach (var locale in manager.GetSupportedLocales()) manager.AddSource(locale, s_Instance);
        }

        /// <summary>Names a prefab right away (the active dictionary is not rebuilt for new prefabs).</summary>
        public static void Add(string prefabName)
        {
            var dictionary = GameManager.instance?.localizationManager?.activeDictionary;
            if (dictionary == null) return;
            foreach (var entry in EntriesFor(prefabName)) dictionary.Add(entry.Key, entry.Value);
        }

        private static IEnumerable<KeyValuePair<string, string>> EntriesFor(string prefabName)
        {
            yield return new KeyValuePair<string, string>($"Assets.NAME[{prefabName}]", kName);
            var record = AssemblyLibrary.FromPrefabName(prefabName);
            var legend = record == null ? "" : string.Join(" / ", record.Designs.Select(FirstLines).Where(t => t.Length > 0));
            yield return new KeyValuePair<string, string>($"Assets.DESCRIPTION[{prefabName}]", legend.Length > 0 ? legend : kName);
        }

        /// <summary>The first couple of text legends of a design, e.g. "Boston, Portsmouth NH".</summary>
        private static string FirstLines(JObject design)
        {
            var texts = design.SelectTokens("$..text").Select(t => (string)t).Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 2).Take(2);
            return string.Join(", ", texts);
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts) =>
            AssemblyLibrary.All.SelectMany(r => EntriesFor(r.PrefabName)).ToList();

        public void Unload() { }
    }
}

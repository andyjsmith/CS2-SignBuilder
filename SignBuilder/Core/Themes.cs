using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SignBuilder.Core
{
    /// <summary>A sign theme: one country's (or style's) sign system, from a folder with theme.json.</summary>
    public sealed class ThemeInfo
    {
        public string Id;
        public string Name;
        public bool BuiltIn;
        /// <summary>Folder on disk, and the same folder as a URL the renderer can load fonts and images from.</summary>
        public string Dir;
        public string BaseUrl;
        /// <summary>theme.json with file references resolved (optional fonts the player has not supplied removed).</summary>
        public JObject Def;
    }

    /// <summary>
    /// Sign themes. Built-in ones ship in Resources/Themes; players add their own in ModsData/SignBuilder/Themes
    /// (one folder per theme: theme.json, fonts/, shields/, templates/). A player folder with the id of a built-in
    /// theme and no theme.json only supplies files for it, such as fonts that cannot ship with the mod.
    /// </summary>
    public static class Themes
    {
        public const string DataHost = "signbuilderdata";
        public const string DefaultId = "us";

        public static string BuiltInDir => Path.Combine(SignBuilderRuntime.ResourcesDir, "Themes");
        public static string UserThemesDir => Path.Combine(SignBuilderRuntime.DataDir, "Themes");

        private static Dictionary<string, ThemeInfo> s_Themes;

        public static IReadOnlyCollection<ThemeInfo> All
        {
            get
            {
                if (s_Themes == null) Reload();
                return s_Themes.Values;
            }
        }

        public static ThemeInfo Get(string id)
        {
            if (s_Themes == null) Reload();
            return s_Themes.TryGetValue(id ?? DefaultId, out var t) ? t : s_Themes.TryGetValue(DefaultId, out var d) ? d : s_Themes.Values.FirstOrDefault();
        }

        public static void Reload()
        {
            s_Themes = new Dictionary<string, ThemeInfo>();
            Load(BuiltInDir, true, $"coui://signbuilder/Themes/");
            Load(UserThemesDir, false, $"coui://{DataHost}/Themes/");
        }

        private static void Load(string root, bool builtIn, string urlRoot)
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d))
            {
                var file = Path.Combine(dir, "theme.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var def = JObject.Parse(File.ReadAllText(file));
                    var id = (string)def["id"] ?? Path.GetFileName(dir);
                    def["id"] = id;
                    var theme = new ThemeInfo
                    {
                        Id = id, Name = (string)def["name"] ?? id, BuiltIn = builtIn, Dir = dir,
                        BaseUrl = urlRoot + Uri.EscapeDataString(Path.GetFileName(dir)) + "/", Def = def,
                    };
                    ResolveFonts(theme);
                    s_Themes[id] = theme;
                }
                catch (Exception e)
                {
                    Mod.log.Warn($"Skipping theme {dir}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Font files are looked up in the theme's folder, then in the player's folder for that theme id. Optional
        /// fonts (licensed faces players add themselves) are dropped when neither has the file.
        /// </summary>
        private static void ResolveFonts(ThemeInfo theme)
        {
            if (theme.Def["fonts"] is not JArray fonts) return;
            var extras = Path.Combine(UserThemesDir, theme.Id);
            foreach (var font in fonts.OfType<JObject>().ToList())
            {
                var file = (string)font["file"];
                if (string.IsNullOrEmpty(file)) continue;
                if (File.Exists(Path.Combine(theme.Dir, file))) continue;
                if (File.Exists(Path.Combine(extras, file)))
                {
                    font["file"] = $"coui://{DataHost}/Themes/{Uri.EscapeDataString(theme.Id)}/{file.Replace('\\', '/')}";
                    continue;
                }
                if ((bool?)font["optional"] == true) font.Remove();
                else Mod.log.Warn($"Theme {theme.Id}: font file {file} not found");
            }
        }

        /// <summary>The design with its theme attached ("_theme", "_themeBase"), as the renderer needs it to bake.</summary>
        public static JObject ForRender(JObject design)
        {
            var theme = Get((string)design["theme"]);
            if (theme == null) return design;
            var copy = (JObject)design.DeepClone();
            copy["theme"] = theme.Id;
            copy["_theme"] = theme.Def;
            copy["_themeBase"] = theme.BaseUrl;
            return copy;
        }

        /// <summary>All themes for the editor: [{ id, name, builtIn, base, def }].</summary>
        public static string Json() => new JArray(All.Select(t => new JObject
        {
            ["id"] = t.Id, ["name"] = t.Name, ["builtIn"] = t.BuiltIn, ["base"] = t.BaseUrl, ["def"] = t.Def,
        })).ToString(Formatting.None);
    }
}

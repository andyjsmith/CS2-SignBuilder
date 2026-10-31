using Newtonsoft.Json.Linq;

namespace SignBuilder.Core
{
    /// <summary>
    /// Format versions of the JSON players and theme packs keep: sign designs (in saves, templates and the library) and
    /// theme.json. Each carries "formatVersion"; a later format change bumps it here and upgrades older files on read.
    /// </summary>
    public static class Formats
    {
        public const string Key = "formatVersion";
        public const int Design = 1;
        public const int Theme = 1;

        /// <summary>Stamps the current design format on <paramref name="design"/> (being stored), and returns it.</summary>
        public static JObject StampDesign(JObject design)
        {
            if (design != null) design[Key] = Design;
            return design;
        }

        /// <summary>
        /// Checks a design read from <paramref name="source"/>: false (logged) if it is from a newer Sign Builder than this
        /// one. A design without a version is the first format.
        /// </summary>
        public static bool ReadDesign(JObject design, string source) => Read(design, Design, "design", source);

        /// <summary>Checks a theme.json read from <paramref name="source"/>, as <see cref="ReadDesign"/>.</summary>
        public static bool ReadTheme(JObject theme, string source) => Read(theme, Theme, "theme", source);

        private static bool Read(JObject json, int current, string what, string source)
        {
            var version = (int?)json?[Key] ?? 1;
            if (version <= current) return true;
            Mod.log.Warn($"Skipping {what} {source}: format {version} needs a newer Sign Builder (this one reads up to {current})");
            return false;
        }
    }
}

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
        /// <summary>
        /// 2: arrows (and road diagram arrowheads) come from the theme's symbol libraries. Designs made with 1.0 (format 1)
        /// are drawn the same way: its arrows were the same size, so its signs keep their layout.
        /// </summary>
        public const int Design = 2;
        /// <summary>2: arrows come from symbol libraries ("symbols"); 1.0 skips such themes rather than draw them wrong.</summary>
        public const int Theme = 2;
        public const int Library = 1;

        /// <summary>
        /// Stamps the format on <paramref name="design"/> (being stored), and returns it: the one it was made with, or
        /// the current one for a new design.
        /// </summary>
        public static JObject StampDesign(JObject design)
        {
            if (design != null && design[Key] == null) design[Key] = Design;
            return design;
        }

        /// <summary>A stored design with its format written out (one without is the first format), and returns it.</summary>
        public static JObject Versioned(JObject design)
        {
            if (design != null && design[Key] == null) design[Key] = 1;
            return design;
        }

        /// <summary>
        /// Checks a design read from <paramref name="source"/>: false (logged) if it is from a newer Sign Builder than this
        /// one. A design without a version is the first format.
        /// </summary>
        public static bool ReadDesign(JObject design, string source) => Read(Versioned(design), Design, "design", source);

        /// <summary>Checks a theme.json read from <paramref name="source"/>, as <see cref="ReadDesign"/>.</summary>
        public static bool ReadTheme(JObject theme, string source) => Read(theme, Theme, "theme", source);

        /// <summary>Checks a symbol library's library.json, as <see cref="ReadDesign"/>.</summary>
        public static bool ReadLibrary(JObject library, string source) => Read(library, Library, "symbol library", source);

        private static bool Read(JObject json, int current, string what, string source)
        {
            var version = (int?)json?[Key] ?? 1;
            if (version <= current) return true;
            Mod.log.Warn($"Skipping {what} {source}: format {version} needs a newer Sign Builder (this one reads up to {current})");
            return false;
        }
    }
}

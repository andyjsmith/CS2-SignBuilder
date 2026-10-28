using System.IO;

namespace SignBuilder.Core
{
    /// <summary>Sign themes (a country's or style's sign system): built-in ones ship with the mod, players add their own.</summary>
    public static class Themes
    {
        public static string UserThemesDir => Path.Combine(SignBuilderRuntime.DataDir, "Themes");
    }
}

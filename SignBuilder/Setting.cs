using System.Collections.Generic;
using System.IO;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace SignBuilder
{
    /// <summary>Sign Builder's page in the game's Options.</summary>
    [FileLocation(nameof(SignBuilder))]
    [SettingsUIGroupOrder(kMaintenanceGroup, kFoldersGroup)]
    [SettingsUIShowGroupName(kMaintenanceGroup, kFoldersGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kMaintenanceGroup = "Maintenance";
        public const string kFoldersGroup = "Folders";

        public Setting(IMod mod) : base(mod)
        {
        }

        /// <summary>Deletes sign structures not used in the open city (their meshes, materials and library entries).</summary>
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kMaintenanceGroup)]
        public bool CleanUpUnused
        {
            set => Assemblies.AssemblyCleanup.RemoveUnusedInCity();
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kFoldersGroup)]
        public bool OpenTemplatesFolder
        {
            set => OpenFolder(Systems.SignBuilderUISystem.UserTemplatesDir);
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kFoldersGroup)]
        public bool OpenThemesFolder
        {
            set => OpenFolder(Core.Themes.UserThemesDir);
        }

        public static void OpenFolder(string dir)
        {
            Directory.CreateDirectory(dir);
            UnityEngine.Application.OpenURL("file:///" + dir.Replace('\\', '/'));
        }

        public override void SetDefaults()
        {
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Sign Builder" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kMaintenanceGroup), "Maintenance" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kFoldersGroup), "Folders" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CleanUpUnused)), "Delete unused sign structures" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.CleanUpUnused)),
                    "Deletes the generated meshes and materials of sign structures that are not placed in the open city. " +
                    "Other cities still have their signs: they are rebuilt from the save when that city is loaded." },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.CleanUpUnused)),
                    "Delete every sign structure that is not used in this city? Other saved cities rebuild theirs when loaded." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.OpenTemplatesFolder)), "Open templates folder" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.OpenTemplatesFolder)), "Your saved sign templates, one JSON file each. Share them by copying the files." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.OpenThemesFolder)), "Open themes folder" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.OpenThemesFolder)), "Sign themes (countries or styles), one folder each with theme.json, fonts, shields and templates." },
            };
        }

        public void Unload()
        {
        }
    }
}

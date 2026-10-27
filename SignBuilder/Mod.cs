using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Colossal.IO.AssetDatabase;

namespace SignBuilder
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(SignBuilder)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        private Setting m_Setting;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            // Generated meshes and materials live in a mod-owned database; it must be registered
            // before any save that uses them is loaded.
            AssetDatabase.global.RegisterDatabase(AssetDatabase<Spike.SignBuilderCache>.instance).ContinueWith(t =>
            {
                if (t.IsFaulted) log.Error(t.Exception, "Registering the SignBuilder cache database failed");
            });
            updateSystem.UpdateAt<Spike.SpikeBootstrapSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateAt<Assemblies.AssemblyBootstrapSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateAt<Tools.SignPlacementToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<Systems.SignBuilderUISystem>(SystemUpdatePhase.UIUpdate);

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
                Core.SignBuilderRuntime.Initialise(System.IO.Path.GetDirectoryName(asset.path));
                Spike.SpikeRunner.StartIfFlagged(System.IO.Path.GetDirectoryName(asset.path));
            }

            m_Setting = new Setting(this);
            m_Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(m_Setting));


            AssetDatabase.global.LoadSettings(nameof(SignBuilder), m_Setting, new Setting(this));
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (m_Setting != null)
            {
                m_Setting.UnregisterInOptionsUI();
                m_Setting = null;
            }
        }
    }
}

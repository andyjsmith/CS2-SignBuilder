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
            AssetDatabase.global.RegisterDatabase(AssetDatabase<Core.SignBuilderCache>.instance).ContinueWith(t =>
            {
                if (t.IsFaulted) log.Error(t.Exception, "Registering the SignBuilder cache database failed");
            });
#if TESTS
            updateSystem.UpdateAt<Tests.SpikeBootstrapSystem>(SystemUpdatePhase.MainLoop);
#endif
            updateSystem.UpdateAt<Assemblies.AssemblyBootstrapSystem>(SystemUpdatePhase.MainLoop);
            // Sign recipes travel inside saves so shared cities rebuild their signs on other PCs.
            updateSystem.UpdateBefore<Assemblies.AssemblySaveSystem>(SystemUpdatePhase.Serialize);
            updateSystem.UpdateAt<Assemblies.AssemblySaveCleanupSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateAt<Assemblies.AssemblyRestoreSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateAt<Tools.SignPlacementToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<Systems.SignBuilderUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<Systems.SignInfoSection>(SystemUpdatePhase.UIUpdate);
            // LED lettering on at night: set after the game animates its lights, before they reach the GPU.
            updateSystem.UpdateAfter<Systems.LedLightSystem, Game.Rendering.ObjectInterpolateSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateBefore<Systems.LedLightSystem, Game.Rendering.ProceduralUploadSystem>(SystemUpdatePhase.Rendering);

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
                Core.SignBuilderRuntime.Initialize(System.IO.Path.GetDirectoryName(asset.path));
#if TESTS
                Tests.SpikeRunner.StartIfFlagged(System.IO.Path.GetDirectoryName(asset.path));
#endif
            }

            m_Setting = new Setting(this);
            m_Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(m_Setting));
            Core.AssetNames.Register();


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

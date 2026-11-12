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

        /// <summary>The key binding that opens or closes Sign Builder (unset until the player picks a key).</summary>
        public static Game.Input.ProxyAction ToggleAction { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info($"Sign Builder {typeof(Mod).Assembly.GetName().Version.ToString(3)}");

            // Generated meshes and materials live in a mod-owned database; it must be registered
            // before any save that uses them is loaded.
            AssetDatabase.global.RegisterDatabase(AssetDatabase<Core.SignBuilderCache>.instance).ContinueWith(t =>
            {
                if (t.IsFaulted) log.Error(t.Exception, "Registering the SignBuilder cache database failed");
            });
#if TESTS
            updateSystem.UpdateAt<Tests.SpikeBootstrapSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateBefore<Tests.OverrideProbeSystem, Game.Objects.OverrideSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<Tests.OverrideForceSystem>(SystemUpdatePhase.Modification1);
#endif
            updateSystem.UpdateAt<Assemblies.AssemblyBootstrapSystem>(SystemUpdatePhase.MainLoop);
            // Right after the game initializes prefabs (and makes plain props Overridable and Brushable), in the same update.
            updateSystem.UpdateAfter<Assemblies.SignPrefabFlagsSystem, Game.Prefabs.ObjectInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            // Sign recipes travel inside saves so shared cities rebuild their signs on other PCs.
            updateSystem.UpdateBefore<Assemblies.AssemblySaveSystem>(SystemUpdatePhase.Serialize);
            updateSystem.UpdateAt<Assemblies.AssemblySaveCleanupSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateAt<Assemblies.AssemblyRestoreSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateAt<Tools.SignPlacementToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<Systems.SignBuilderUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<Systems.SignInfoSection>(SystemUpdatePhase.UIUpdate);
            // Sign light fixtures on when it gets dark (by the rendered daylight, as street lights). The signs to switch
            // are collected as they are created (Created is set until modifications end).
            updateSystem.UpdateAt<Systems.SignLightTrackingSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAfter<Systems.SignLightSystem, Game.Rendering.ObjectInterpolateSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateBefore<Systems.SignLightSystem, Game.Rendering.ProceduralUploadSystem>(SystemUpdatePhase.Rendering);

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
                Core.SignBuilderRuntime.Initialize(System.IO.Path.GetDirectoryName(asset.path));
                Core.SignBuilderRuntime.RegisterUIHosts();
#if TESTS
                Tests.SpikeRunner.StartIfFlagged(System.IO.Path.GetDirectoryName(asset.path));
#endif
            }

            m_Setting = new Setting(this);
            m_Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(m_Setting));
            m_Setting.RegisterKeyBindings();
            ToggleAction = m_Setting.GetAction(Setting.kToggleAction);
            ToggleAction.shouldBeEnabled = true;
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

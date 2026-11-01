using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.Assets;
using Game.Prefabs;
using Game.Prefabs.Modes;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI;
using Game.UI.Menu;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using SignBuilder.Core;
using SignBuilder.Geometry;
using SignBuilder.Assemblies;
using SignBuilder.Tools;

namespace SignBuilder.Tests
{
    /// <summary>
    /// Automated spike harness, run 7 ("batched"). Only runs when ModsData/SignBuilder/spike.flag
    /// exists, and deletes that flag immediately so a normal launch afterwards is unaffected.
    ///
    /// Every feature is its own test: a failure or exception is recorded and the run moves on.
    /// Results are rewritten after each test. Safe tests (and their screenshots) run first; the risky
    /// ones (custom material asset, save + reload) run last so they cannot spoil earlier results.
    /// </summary>
    internal sealed partial class SpikeRunner : MonoBehaviour
    {
        private const float kWatchdogSeconds = 540f;
        private const string kSaveName = "SignBuilder_SpikeSave";

        private string m_ResourcesDir;
        private float m_StartTime;
        private bool m_Finished;
        private bool m_Smoke;
        private string m_Mode;

        private RuntimePrefabs m_Runtime;

        public static string FlagPath => Path.Combine(Application.persistentDataPath, "ModsData", "SignBuilder", "spike.flag");

        public static void StartIfFlagged(string modDir)
        {
            if (!File.Exists(FlagPath)) return;
            var mode = File.ReadAllText(FlagPath).Trim();
            File.Delete(FlagPath);

            var go = new GameObject("SignBuilder.SpikeRunner");
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<SpikeRunner>();
            runner.m_ResourcesDir = Path.Combine(modDir, "Resources");
            runner.m_Smoke = mode == "smoke";
            runner.m_Mode = mode;
            SpikeBootstrapSystem.Mode = mode;
            Mod.log.Info($"Spike runner started (mode '{mode}'), resources at {runner.m_ResourcesDir}");
        }

        private void Start()
        {
            m_StartTime = Time.realtimeSinceStartup;
            if (Directory.Exists(SpikeReport.OutputDir)) Directory.Delete(SpikeReport.OutputDir, recursive: true);
            StartCoroutine(Run());
        }

        private void Update()
        {
            if (!m_Finished && Time.realtimeSinceStartup - m_StartTime > kWatchdogSeconds)
            {
                SpikeReport.Set("watchdog", "timed out");
                Finish();
            }
        }

        private void LateUpdate()
        {
            if (s_CameraLocked) ApplyCamera();
        }

        // ------------------------------------------------------------------ orchestration

        private IEnumerator Run()
        {
            while (GameManager.instance == null || GameManager.instance.isGameLoading
                   || (GameManager.instance.gameMode != GameMode.MainMenu && !GameManager.instance.gameMode.IsGame()))
            {
                yield return null;
            }
            SpikeReport.Set("game.startedFrom", GameManager.instance.gameMode);
            SpikeReport.Set("game.commandLine", string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
            yield return new WaitForSecondsRealtime(3f);

            if (m_Mode == "e2e" || m_Mode == "reload-e2e")
            {
                yield return RunE2E();
                yield break;
            }
            if (m_Mode == "led-e2e")
            {
                yield return RunLedE2E();
                yield break;
            }
            if (m_Mode == "leak-e2e")
            {
                yield return RunLeakE2E();
                yield break;
            }
            if (m_Mode == "lights-perf")
            {
                yield return RunLightsPerf();
                yield break;
            }
            if (m_Mode == "render-perf")
            {
                yield return RunRenderPerf();
                yield break;
            }
            if (m_Mode == "ui-e2e")
            {
                yield return RunUIE2E();
                yield break;
            }
            if (m_Mode == "uitest")
            {
                yield return RunUITest();
                yield break;
            }
            if (m_Mode == "reload")
            {
                yield return RunReload();
                yield break;
            }
            // Smoke mode (and anything unknown): only checks how the game booted (e.g. auto-resume via runOnce.txt), then quits.
            if (GameManager.instance.gameMode.IsGame()) yield return WaitForGame();
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Screenshot("shot_smoke.png");
            yield return new WaitForSecondsRealtime(1f);
            Finish();
        }

        private IEnumerator Test(string name, IEnumerator body)
        {
            var start = Time.realtimeSinceStartup;
            Exception error = null;
            while (true)
            {
                object current;
                try
                {
                    if (!body.MoveNext()) break;
                    current = body.Current;
                }
                catch (Exception e)
                {
                    error = e;
                    break;
                }
                yield return current;
            }
            SpikeReport.Set($"{name}.status", error == null ? "ok" : "EXCEPTION");
            if (error != null) SpikeReport.Set($"{name}.exception", error.ToString());
            SpikeReport.Set($"{name}.seconds", (Time.realtimeSinceStartup - start).ToString("F1"));
            SpikeReport.Write();
        }

        private IEnumerator WaitForGame()
        {
            // The launcher asks for a save by id (-startGame) or "continue"; ids change when a save is overwritten,
            // so if the game is still at the main menu after a while, load the test save by name instead.
            var deadline = Time.realtimeSinceStartup + 25f;
            var requested = false;
            while (!GameManager.instance.gameMode.IsGame() || GameManager.instance.isGameLoading)
            {
                if (!requested && Time.realtimeSinceStartup > deadline && GameManager.instance.gameMode == GameMode.MainMenu && !GameManager.instance.isGameLoading)
                {
                    requested = true;
                    var name = m_Mode == "reload" ? kE2ESave : kContinueSave;
                    var meta = AssetDatabase.global.GetAssets<SaveGameMetadata>().FirstOrDefault(m => m.name == name);
                    SpikeReport.Set("game.loadByName", meta != null ? $"{name} ({meta.id.guid})" : $"{name} not found");
                    if (meta != null) GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, meta);
                }
                yield return null;
            }
            var autoSave = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<AutoSaveSystem>();
            if (autoSave != null) autoSave.Enabled = false;
            SpikeReport.Set("game.loaded", true);
            SpikeReport.Set("game.autoSaveDisabled", autoSave != null);
            // The launcher loads the test save by its id (written here for the next run).
            var test = AssetDatabase.user.GetAssets<SaveGameMetadata>().FirstOrDefault(m => m.name == kContinueSave);
            if (test != null) File.WriteAllText(Path.Combine(SignBuilderRuntime.DataDir, "test-save.guid"), test.id.guid.ToString());
            // Never test in a player's own city: anything but the test city quits at once, untouched.
            var city = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<Game.City.CityConfigurationSystem>()?.cityName;
            SpikeReport.Set("game.city", city);
            if (city != kTestCity)
            {
                SpikeReport.Set("game.refused", $"loaded city \"{city}\" is not the test city \"{kTestCity}\"; nothing was run");
                RestoreContinueTarget();
                Finish();
                while (true) yield return null;
            }
        }

        /// <summary>The test save's city: tests run only there.</summary>
        private const string kTestCity = "ModTest";

        // ------------------------------------------------------------------ tests

        /// <summary>Renders every sign template off-screen (also confirms the row-flip fix).</summary>
        private static CameraController CameraController() =>
            World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<CameraUpdateSystem>()?.gamePlayController;

        private static bool s_CameraLocked;
        private static (float3 pivot, float yaw, float pitch, float zoom) s_CameraTarget;

        /// <summary>
        /// Points the gameplay camera and keeps it there (re-applied every frame, player input off) so
        /// edge scrolling or a resting mouse cannot drift it between the request and the screenshot.
        /// </summary>
        private static void SetCamera(float3 pivot, float yaw, float pitch, float zoom)
        {
            s_CameraTarget = (pivot, yaw, pitch, zoom);
            s_CameraLocked = true;
            ApplyCamera();
            var controller = CameraController();
            SpikeReport.Set("camera.set", $"pivot={pivot} yaw={yaw:F0} pitch={pitch:F0} zoom={zoom:F0} -> rotation={controller?.rotation} zoom={controller?.zoom}");
        }

        private static void ApplyCamera()
        {
            var controller = CameraController();
            if (controller == null) return;
            controller.inputEnabled = false;
            controller.pivot = s_CameraTarget.pivot;
            controller.rotation = new Vector3(s_CameraTarget.pitch, s_CameraTarget.yaw, 0f);
            controller.zoom = s_CameraTarget.zoom;
        }

        /// <summary>Samples frame times for <paramref name="seconds"/>; Current ends as "avg/p95/max ms over n frames".</summary>
        private static IEnumerator SampleFrames(float seconds)
        {
            var samples = new List<float>();
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                samples.Add(Time.unscaledDeltaTime * 1000f);
            }
            samples.Sort();
            yield return samples.Count == 0
                ? "no frames"
                : $"avg={samples.Average():F2} p95={samples[(int)(samples.Count * 0.95f)]:F2} max={samples[samples.Count - 1]:F2} ms over {samples.Count} frames";
        }

        private static bool ContinueLastSave()
        {
            var menu = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<MenuUISystem>();
            var method = typeof(MenuUISystem).GetMethod("SafeContinueGame", BindingFlags.Instance | BindingFlags.NonPublic);
            SpikeReport.Set("game.continueMethod", method != null && menu != null);
            if (method == null || menu == null) return false;
            method.Invoke(menu, null);
            return true;
        }

        private const string kContinueSave = "ModTest";

        /// <summary>Test runs load other saves; point "Continue" back at the ModTest save afterwards.</summary>
        private static void RestoreContinueTarget()
        {
            var meta = AssetDatabase.user.GetAssets<SaveGameMetadata>().FirstOrDefault(m => m.name == kContinueSave);
            if (meta != null) GameManager.instance.settings.userState.lastSaveGameMetadata = meta;
            SpikeReport.Set("continueTarget", meta?.name ?? "ModTest not found");
        }

        private void Finish()
        {
            if (m_Finished) return;
            s_CameraLocked = false;
            var controller = CameraController();
            if (controller != null) controller.inputEnabled = true;
            m_Finished = true;
            SpikeReport.Write();
            Mod.log.Info("Spike run finished, quitting");
            Application.Quit();
        }
    }
}

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

namespace SignBuilder.Spike
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
        private const float kWatchdogSeconds = 900f;
        private const string kSaveName = "SignBuilder_SpikeSave";

        private string m_ResourcesDir;
        private float m_StartTime;
        private bool m_Finished;
        private bool m_Smoke;
        private string m_Mode;

        private readonly SignPanels m_Panels = new();
        private readonly Dictionary<string, Texture2D> m_Faces = new();
        private readonly List<StaticObjectPrefab> m_Prefabs = new();
        private RuntimePrefabs m_Runtime;
        private ILocalAssetDatabase m_Db;
        private string m_DbFolder;
        private RoadProbe m_Probe;
        private RoadProbe.Snap m_Snap;
        private SurfaceAsset m_LightpoleSurface;
        private SurfaceAsset m_AltSurface;
        private float3 m_FocusPoint;
        private float m_FocusYaw;

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
            m_Panels.Draw();
            UpdateNightGlow();
            m_Signs.Draw();
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

            if (m_Mode == "e2e")
            {
                yield return RunE2E();
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
            if (m_Mode == "debug")
            {
                yield return RunBakerDebug(300f);
                yield break;
            }
            if (m_Mode == "phase1")
            {
                yield return RunPhase1();
                yield break;
            }

            // Smoke mode: only checks how the game booted (e.g. auto-resume via runOnce.txt), then quits.
            if (m_Smoke)
            {
                if (GameManager.instance.gameMode.IsGame()) yield return WaitForGame();
                yield return new WaitForSecondsRealtime(3f);
                SpikeReport.Screenshot("shot_smoke.png");
                yield return new WaitForSecondsRealtime(1f);
                Finish();
                yield break;
            }

            yield return Test("faces", TestFaces());

            if (GameManager.instance.gameMode == GameMode.MainMenu && !ContinueLastSave()) { Finish(); yield break; }
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(10f);

            m_Runtime = new RuntimePrefabs();
            m_Probe = new RoadProbe(World.DefaultGameObjectInjectionWorld.EntityManager, m_Runtime.PrefabSystem);

            yield return Test("cacheDb", TestCacheDb());
            yield return Test("road", TestRoad());
            yield return Test("lanes", TestLanes());
            yield return Test("donors", TestDonors());
            yield return Test("gantries", TestGantries());
            yield return Test("shots", TestShots());
            yield return Test("perf", TestPerf());
            yield return Test("steelSurface", TestSteelSurface());
            yield return Test("saveReload", TestSaveReload());
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
            while (!GameManager.instance.gameMode.IsGame() || GameManager.instance.isGameLoading) yield return null;
            var autoSave = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<AutoSaveSystem>();
            if (autoSave != null) autoSave.Enabled = false;
            SpikeReport.Set("game.loaded", true);
            SpikeReport.Set("game.autoSaveDisabled", autoSave != null);
        }

        // ------------------------------------------------------------------ tests

        /// <summary>Renders every sign template off-screen (also confirms the row-flip fix).</summary>
        private IEnumerator TestFaces()
        {
            using var renderer = new OffscreenUISpike(m_ResourcesDir);
            foreach (var page in new[] { "guide", "services", "attraction", "shields" })
            {
                Texture2D face = null;
                var opaque = 0;
                var start = Time.realtimeSinceStartup;
                yield return renderer.Render($"Spike/{page}.html", 1024, 512, (t, o) => { face = t; opaque = o; });
                SpikeReport.Set($"faces.{page}", face == null ? "FAILED to load" : $"opaque={opaque} ms={(Time.realtimeSinceStartup - start) * 1000:F0}");
                if (face == null) continue;
                face.name = "SignBuilder.Face." + page;
                m_Faces[page] = face;
                SpikeReport.SavePng(face, $"face_{page}.png");
            }
        }

        /// <summary>Registers the mod-owned asset database; falls back to the user database.</summary>
        private IEnumerator TestCacheDb()
        {
            // Registered with the global database by Mod.OnLoad.
            m_Db = AssetDatabase<SignBuilderCache>.instance;
            m_DbFolder = "Meshes";
            SpikeReport.Set("cacheDb.using", SignBuilderCache.RootPath);
            yield break;
        }

        /// <summary>Snaps to the highway nearest the camera's pivot.</summary>
        private IEnumerator TestRoad()
        {
            var controller = CameraController();
            var near = controller != null ? (float3)controller.pivot : (float3)Camera.main.transform.position;
            SpikeReport.Set("road.searchFrom", near);
            m_Snap = m_Probe.FindNear(near, s => SpikeReport.Set("road.log", s));
            SpikeReport.Set("road.snap", m_Snap?.ToString() ?? "none");
            yield break;
        }

        private IEnumerator TestLanes()
        {
            if (m_Snap == null) { SpikeReport.Set("lanes.skipped", "no road"); yield break; }
            SpikeReport.Set("lanes.count", m_Snap.Lanes.Count);
            SpikeReport.Set("lanes.mainDirection", m_Snap.MainDirection);
            SpikeReport.Set("lanes.list", string.Join(" | ", m_Snap.Lanes.Select(l => $"{(l.OnPairedEdge ? "P" : "M")} off={l.Offset:F2} dir={l.Direction} w={l.Width:F2}")));
        }

        /// <summary>Picks donor surfaces: the light pole (baseline) and something likely to tile.</summary>
        private IEnumerator TestDonors()
        {
            var pole = m_Runtime.FindMesh(new[] { "LightpolePark" }, out _);
            m_LightpoleSurface = pole?.GetSurfaceAsset(0);
            SpikeReport.Set("donors.lightpole", pole?.name ?? "none");

            var alt = m_Runtime.FindMesh(new[] { "Guardrail", "GuardRail", "Railing", "Scaffold", "Metal", "Steel", "Fence" }, out var matches);
            m_AltSurface = alt?.GetSurfaceAsset(0) ?? m_LightpoleSurface;
            SpikeReport.Set("donors.alt", alt?.name ?? "none (using lightpole)");
            SpikeReport.Set("donors.altCandidates", string.Join(", ", matches));
            yield break;
        }

        /// <summary>
        /// Builds and spawns four gantry variants along the snapped highway, each carrying a different
        /// sign face: truss (light pole material, metre UVs), truss (alternate donor), monotube, cantilever.
        /// </summary>
        private IEnumerator TestGantries()
        {
            if (m_LightpoleSurface == null) throw new InvalidOperationException("no donor surface");
            var snap = m_Snap ?? FallbackSnap();
            var placements = new (string variant, float distance)[] { ("truss", 0f), ("trussAlt", 45f), ("monotube", -45f), ("cantilever", 90f) };

            foreach (var (variant, distance) in placements)
            {
                var s = SnapAlong(snap, distance);
                var dir = s.MainDirection;
                var fwd = s.Tangent * dir;
                var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
                var extent = s.Extent;
                var mainLanes = s.Lanes.Where(l => !l.OnPairedEdge && l.Direction == dir).ToList();
                var signOffset = mainLanes.Count > 0 ? mainLanes.Average(l => l.Offset) : 0f;

                Mesh mesh;
                float3 origin;
                switch (variant)
                {
                    case "monotube":
                    {
                        var span = s.EdgeWidth + 5f;
                        mesh = GantryFactory.Monotube(span);
                        origin = s.Point;
                        break;
                    }
                    case "cantilever":
                    {
                        // Post just off the right-hand edge (relative to traffic), arm reaching back over the lanes.
                        var postOffset = dir * (s.EdgeWidth * 0.5f + 1.5f);
                        var arm = s.EdgeWidth * 0.55f + 1.5f;
                        mesh = GantryFactory.Cantilever(-arm);
                        origin = s.Point + s.Right * postOffset;
                        signOffset = postOffset - dir * arm * 0.65f;
                        break;
                    }
                    default:
                    {
                        var span = extent.y - extent.x + 5f;
                        mesh = GantryFactory.Truss(span);
                        origin = s.Point + s.Right * ((extent.x + extent.y) * 0.5f);
                        break;
                    }
                }

                var surface = variant == "trussAlt" ? m_AltSurface : m_LightpoleSurface;
                var prefab = m_Runtime.Create($"SignBuilder_{variant}", mesh, surface, m_Db, m_DbFolder);
                m_Prefabs.Add(prefab);
                SpikeReport.Set($"gantries.{variant}.mesh", $"verts={mesh.vertexCount} bounds={mesh.bounds.size}");
                for (var i = 0; i < 20; i++) yield return null;
                SpikeReport.Set($"gantries.{variant}.flagsBefore", m_Runtime.ClearOverridable(prefab));
                m_Runtime.Spawn(prefab, origin, rotation);

                // Sign faces mount on the approach side (-fwd) at mount height.
                var standoff = variant == "monotube" || variant == "cantilever" ? 0.45f : GantryFactory.kTrussDepth * 0.5f + 0.15f;
                var face = variant switch { "monotube" => "services", "cantilever" => "attraction", "trussAlt" => "shields", _ => "guide" };
                var signPos = s.Point + s.Right * signOffset + new float3(0, GantryFactory.kMountHeight, 0) - fwd * standoff;
                if (m_Faces.TryGetValue(face, out var tex)) m_Panels.Add(tex, 4.8f, 2.4f, signPos, rotation);

                // The main truss also carries a sign for the opposite carriageway, facing the other way.
                if (variant == "truss" && s.PairedEdge != Entity.Null && m_Faces.TryGetValue("shields", out var back))
                {
                    var other = s.Lanes.Where(l => l.OnPairedEdge).ToList();
                    var otherOffset = other.Count > 0 ? other.Average(l => l.Offset) : s.PairedOffset;
                    var backPos = s.Point + s.Right * otherOffset + new float3(0, GantryFactory.kMountHeight, 0) + fwd * standoff;
                    m_Panels.Add(back, 4.8f, 2.4f, backPos, quaternion.LookRotationSafe(-fwd, new float3(0, 1, 0)));
                }

                if (variant == "truss")
                {
                    m_FocusPoint = origin;
                    m_FocusYaw = math.degrees(math.atan2(fwd.x, fwd.z));
                }
                SpikeReport.Set($"gantries.{variant}.placed", $"origin={origin} dir={dir} sign={face}");
            }

            yield return new WaitForSecondsRealtime(4f);
            foreach (var kv in m_Runtime.CountInstances(m_Prefabs))
            {
                SpikeReport.Set($"gantries.instances.{kv.Key}", $"count={kv.Value.count} overridden={kv.Value.overridden} withBatches={kv.Value.withBatches}");
            }
            SpikeReport.Set("gantries.panels", m_Panels.Count);
        }

        /// <summary>Fixed camera angles on the main truss, by day and by night.</summary>
        private IEnumerator TestShots()
        {
            var planetary = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<PlanetarySystem>();
            var views = new (string name, float pitch, float yawOffset, float zoom)[]
            {
                ("close", 10f, 0f, 45f), ("closeSide", 12f, 55f, 40f), ("mid", 20f, 0f, 130f), ("far", 35f, 15f, 420f),
            };
            foreach (var (label, hour) in new[] { ("day", 11f), ("night", 22.5f) })
            {
                if (planetary != null) { planetary.overrideTime = true; planetary.time = hour; }
                foreach (var v in views)
                {
                    if (label == "night" && v.name == "far") continue;
                    SetCamera(m_FocusPoint, m_FocusYaw + v.yawOffset, v.pitch, v.zoom);
                    yield return new WaitForSecondsRealtime(2.5f);
                    SpikeReport.Screenshot($"shot_{label}_{v.name}.png");
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }
            if (planetary != null) planetary.overrideTime = false;
        }

        /// <summary>Spawns 200 truss gantries with sign panels in open ground and measures frame time.</summary>
        private IEnumerator TestPerf()
        {
            var snap = m_Snap ?? FallbackSnap();
            var fwd = snap.Tangent;
            var right = snap.Right;
            // Open ground 300 m to the side of the highway.
            var gridOrigin = snap.Point + right * 300f;
            var gridCentre = gridOrigin + right * 9.5f * 32f + fwd * 4.5f * 40f;
            SetCamera(gridCentre, math.degrees(math.atan2(fwd.x, fwd.z)) + 30f, 30f, 520f);
            yield return new WaitForSecondsRealtime(3f);
            var before = SampleFrames(3f);
            yield return before;

            var prefab = m_Runtime.Create("SignBuilder_perfTruss", GantryFactory.Truss(22f), m_LightpoleSurface, m_Db, m_DbFolder);
            m_Prefabs.Add(prefab);
            for (var i = 0; i < 20; i++) yield return null;
            m_Runtime.ClearOverridable(prefab);
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            var spawnStart = Time.realtimeSinceStartup;
            m_Faces.TryGetValue("guide", out var face);
            for (var x = 0; x < 20; x++)
            for (var z = 0; z < 10; z++)
            {
                var pos = gridOrigin + right * (x * 32f) + fwd * (z * 40f);
                pos.y = snap.Point.y;
                m_Runtime.Spawn(prefab, pos, rotation);
                m_Panels.Add(face, 4.8f, 2.4f, pos + new float3(0, GantryFactory.kMountHeight, 0) - fwd * 0.9f, rotation);
            }
            SpikeReport.Set("perf.spawnMs", ((Time.realtimeSinceStartup - spawnStart) * 1000).ToString("F1"));
            yield return new WaitForSecondsRealtime(8f);
            var after = SampleFrames(3f);
            yield return after;
            SpikeReport.Set("perf.before", before.Current);
            SpikeReport.Set("perf.after200", after.Current);
            var count = m_Runtime.CountInstances(new[] { prefab }).Values.First();
            SpikeReport.Set("perf.instances", $"count={count.count} withBatches={count.withBatches}");
            SpikeReport.Set("perf.panels", m_Panels.Count);
            SpikeReport.Screenshot("shot_perf_grid.png");
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// <summary>Risky: a tileable steel SurfaceAsset created at runtime, on a truss next to the main one.</summary>
        private IEnumerator TestSteelSurface()
        {
            var snap = m_Snap ?? FallbackSnap();
            var surface = SteelMaterial.Create(m_Db, m_DbFolder, m_LightpoleSurface, s => SpikeReport.Set("steel.log", s));
            m_SteelSurface = surface;
            var s = SnapAlong(snap, -90f);
            var extent = s.Extent;
            var fwd = s.Tangent * s.MainDirection;
            var prefab = m_Runtime.Create("SignBuilder_trussSteel", GantryFactory.Truss(extent.y - extent.x + 5f), surface, m_Db, m_DbFolder);
            m_Prefabs.Add(prefab);
            for (var i = 0; i < 20; i++) yield return null;
            m_Runtime.ClearOverridable(prefab);
            var origin = s.Point + s.Right * ((extent.x + extent.y) * 0.5f);
            m_Runtime.Spawn(prefab, origin, quaternion.LookRotationSafe(fwd, new float3(0, 1, 0)));
            yield return new WaitForSecondsRealtime(5f);
            var count = m_Runtime.CountInstances(new[] { prefab }).Values.First();
            SpikeReport.Set("steel.instances", $"count={count.count} withBatches={count.withBatches}");
            SetCamera(origin, math.degrees(math.atan2(fwd.x, fwd.z)) + 35f, 12f, 45f);
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Screenshot("shot_steel_close.png");
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// <summary>
        /// Risky: saves a throwaway copy, reloads it and checks our runtime prefabs' instances survive.
        /// The player's "continue" target is restored afterwards so ModTest stays their last save.
        /// </summary>
        private IEnumerator TestSaveReload()
        {
            var before = m_Runtime.CountInstances(m_Prefabs);
            SpikeReport.Set("save.before", string.Join(" ", before.Select(kv => $"{kv.Key}={kv.Value.count}")));
            var userState = GameManager.instance.settings.userState;
            var originalLast = userState.lastSaveGameMetadata;

            var menu = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<MenuUISystem>();
            var preview = ScreenCaptureHelper.CreateRenderTarget("SignBuilder-Spike", 680, 383);
            ScreenCaptureHelper.CaptureScreenshot(Camera.main, preview, new MenuHelpers.SaveGamePreviewSettings());
            var saveTask = GameManager.instance.Save(kSaveName, menu.GetSaveInfo(autoSave: false), AssetDatabase.user, preview);
            while (!saveTask.IsCompleted) yield return null;
            SpikeReport.Set("save.result", saveTask.Status == TaskStatus.RanToCompletion ? saveTask.Result.ToString() : saveTask.Exception?.ToString());
            SpikeReport.Write();

            var meta = AssetDatabase.user.GetAssets<SaveGameMetadata>().FirstOrDefault(m => m.name.Contains(kSaveName));
            SpikeReport.Set("save.metadata", meta?.ToString() ?? "not found");
            if (meta == null) yield break;

            var world = World.DefaultGameObjectInjectionWorld;
            var mapMeta = world.GetExistingSystemManaged<MapMetadataSystem>();
            mapMeta.mapName = meta.target.mapName;
            mapMeta.prefabReferences = meta.target.prefabReferences;
            world.GetExistingSystemManaged<GameModeSystem>().overrideMode = meta.target.gameMode;
            var loadTask = GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, meta);
            while (!loadTask.IsCompleted) yield return null;
            SpikeReport.Set("reload.result", loadTask.Status == TaskStatus.RanToCompletion ? loadTask.Result.ToString() : loadTask.Exception?.ToString());
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(8f);

            userState.lastSaveGameMetadata = originalLast;
            SpikeReport.Set("reload.continueTargetRestored", originalLast?.name ?? "null");

            var after = m_Runtime.CountInstances(m_Prefabs);
            SpikeReport.Set("reload.after", string.Join(" ", after.Select(kv => $"{kv.Key}={kv.Value.count}/batches={kv.Value.withBatches}/overridden={kv.Value.overridden}")));
            foreach (var prefab in m_Prefabs)
            {
                var entity = m_Runtime.PrefabSystem.GetEntity(prefab);
                var em = world.EntityManager;
                if (em.HasComponent<ObjectGeometryData>(entity))
                {
                    SpikeReport.Set($"reload.flags.{prefab.name}", em.GetComponentData<ObjectGeometryData>(entity).m_Flags);
                }
            }
            SetCamera(m_FocusPoint, m_FocusYaw, 12f, 60f);
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Screenshot("shot_reload.png");
            yield return new WaitForSecondsRealtime(0.5f);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Snap a given distance (metres) along the road from <paramref name="origin"/>; extrapolates past the edge ends.</summary>
        private RoadProbe.Snap SnapAlong(RoadProbe.Snap origin, float distance)
        {
            if (origin.Edge == Entity.Null || distance == 0f) return Shift(origin, distance);
            var t = origin.T + distance / math.max(origin.EdgeLength, 1f);
            if (t < 0.05f || t > 0.95f) return Shift(origin, distance);
            return m_Probe.SnapAt(origin.Edge, t);
        }

        private static RoadProbe.Snap Shift(RoadProbe.Snap s, float distance)
        {
            var copy = new RoadProbe.Snap
            {
                Edge = s.Edge, PrefabName = s.PrefabName, EdgeLength = s.EdgeLength, EdgeWidth = s.EdgeWidth, T = s.T,
                Point = s.Point + s.Tangent * distance, Tangent = s.Tangent, Right = s.Right,
                PairedEdge = s.PairedEdge, PairedOffset = s.PairedOffset, PairedWidth = s.PairedWidth,
            };
            copy.Lanes.AddRange(s.Lanes);
            return copy;
        }

        /// <summary>No road found: pretend there is a 3-lane carriageway in front of the camera.</summary>
        private static RoadProbe.Snap FallbackSnap()
        {
            var cam = Camera.main.transform;
            var fwd = cam.forward;
            fwd.y = 0;
            fwd = fwd.normalized;
            var snap = new RoadProbe.Snap
            {
                PrefabName = "fallback", EdgeWidth = 12f, Point = cam.position + cam.forward * 60f,
                Tangent = fwd, Right = new float3(fwd.z, 0, -fwd.x),
            };
            for (var i = -1; i <= 1; i++) snap.Lanes.Add(new RoadProbe.LaneInfo { Offset = i * 3.5f, Direction = 1, Width = 3.5f });
            return snap;
        }

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

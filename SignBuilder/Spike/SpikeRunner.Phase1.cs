using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.IO.AssetDatabase;
using Game.Assets;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using SignBuilder.Rendering;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace SignBuilder.Spike
{
    /// <summary>
    /// Phase 1 batched run: booted straight into the spike save (-startGame) to test reloading our
    /// gantries from a fresh boot, then bakes every sign template with the production renderer,
    /// mounts them at real size on gantries over the highway, compares night glow strengths and
    /// retries the custom steel material.
    /// </summary>
    internal sealed partial class SpikeRunner
    {
        private const string kContinueSave = "ModTest";

        private readonly SignPanelRenderer m_Signs = new();
        private readonly Dictionary<string, BakedFace> m_Baked = new();
        private SignFaceBaker m_Baker;
        private float m_DefaultGlow = 0.4f;
        private SurfaceAsset m_SteelSurface;
        private float3 m_CurvedFocus;
        private float m_CurvedYaw;
        private float3 m_GroundFocus;
        private float3 m_GoreFocus;
        private float m_GroundYaw;

        /// <summary>Our own steel material once created, else the borrowed light-pole material.</summary>
        private SurfaceAsset GantrySurface => m_SteelSurface ?? m_LightpoleSurface;

        private IEnumerator RunPhase1()
        {
            yield return WaitForGame();
            m_Runtime = new RuntimePrefabs();
            m_Probe = new RoadProbe(World.DefaultGameObjectInjectionWorld.EntityManager, m_Runtime.PrefabSystem);
            m_Db = AssetDatabase<SignBuilderCache>.instance;
            m_DbFolder = "Meshes";
            m_Baker = new SignFaceBaker(m_ResourcesDir, Path.Combine(SignBuilderCache.RootPath, "Faces"));

            yield return Test("bootReload", TestBootReload());
            yield return Test("bake", TestBake("bake"));
            yield return Test("bakeCached", TestBake("bakeCached"));
            SpikeReport.Set("bake.scriptMode", $"{m_Baker.ScriptMode} {m_Baker.ScriptProbe}");
            yield return Test("road", TestRoad());
            yield return Test("lanes", TestLanes());
            yield return Test("donors", TestDonors());
            yield return Test("steelSurface", TestSteelSurface());
            yield return Test("signGantries", TestSignGantries());
            yield return Test("nightGlow", TestNightGlow());
            yield return Test("shots", TestShots());
            yield return Test("curvedShots", TestCurvedShots());
            RestoreContinueTarget();
            m_Baker.Dispose();
            Finish();
        }

        /// <summary>
        /// Debug mode: bakes one design and then keeps the game (and the baker's view) alive so the
        /// Gameface DevTools at localhost:9444 can inspect the page. Quits after <paramref name="seconds"/>.
        /// </summary>
        private IEnumerator RunBakerDebug(float seconds)
        {
            yield return WaitForGame();
            m_Baker = new SignFaceBaker(m_ResourcesDir, Path.Combine(SignBuilderCache.RootPath, "Faces"));
            var design = JObject.Parse(File.ReadAllText(Path.Combine(m_ResourcesDir, "Templates", "services-blue.json")));
            BakedFace face = null;
            yield return m_Baker.Bake(design, 3f, f => face = f);
            SpikeReport.Set("debug.bake", face == null ? $"FAILED {m_Baker.LastError} scriptMode={m_Baker.ScriptMode}" : $"{face.WidthMeters:F2}x{face.HeightMeters:F2} mode={m_Baker.LastMode}");
            SpikeReport.Write();
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end && !File.Exists(Path.Combine(SpikeReport.OutputDir, "done.flag"))) yield return null;
            Finish();
        }

        /// <summary>Were the spike save's gantries restored when their prefabs were re-created before load?</summary>
        private IEnumerator TestBootReload()
        {
            SpikeReport.Set("bootReload.preloadLog", SpikeBootstrapSystem.Log);
            var created = SpikeBootstrapSystem.Created;
            SpikeReport.Set("bootReload.prefabsCreated", created.Count);
            if (created.Count == 0) yield break;
            Report("bootReload.afterLoad", m_Runtime.CountInstances(created));

            // Flags can only be cleared once the prefabs are initialised; then nudge the instances so
            // the game re-evaluates whether the road overrides them.
            foreach (var prefab in created) SpikeReport.Set($"bootReload.flags.{prefab.name}", m_Runtime.ClearOverridable(prefab));
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var instances = Instances(created);
            foreach (var e in instances) em.AddComponent<Updated>(e);
            yield return new WaitForSecondsRealtime(4f);
            Report("bootReload.afterClearFlags", m_Runtime.CountInstances(created));

            var truss = instances.FirstOrDefault(e => em.GetComponentData<PrefabRef>(e).m_Prefab == m_Runtime.PrefabSystem.GetEntity(created[0]));
            if (truss != Entity.Null)
            {
                var t = em.GetComponentData<Game.Objects.Transform>(truss);
                var fwd = math.mul(t.m_Rotation, new float3(0, 0, 1));
                SetCamera(t.m_Position, math.degrees(math.atan2(fwd.x, fwd.z)) + 20f, 18f, 90f);
                yield return new WaitForSecondsRealtime(3f);
                SpikeReport.Screenshot("shot_bootReload.png");
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        /// <summary>Bakes every template in Resources/Templates (second pass should come from the disk cache).</summary>
        private IEnumerator TestBake(string label)
        {
            var dir = Path.Combine(m_ResourcesDir, "Templates");
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var design = JObject.Parse(File.ReadAllText(file));
                BakedFace face = null;
                var start = Time.realtimeSinceStartup;
                yield return m_Baker.Bake(design, 3f, f => face = f);
                var ms = (Time.realtimeSinceStartup - start) * 1000f;
                if (face == null)
                {
                    SpikeReport.Set($"{label}.{id}", $"FAILED: {m_Baker.LastError} (scriptMode={m_Baker.ScriptMode})");
                    continue;
                }
                m_Baked[id] = face;
                SpikeReport.Set($"{label}.{id}", $"{face.WidthMeters:F2} x {face.HeightMeters:F2} m ({face.Texture.width}x{face.Texture.height}px @ {face.PixelsPerInch:F1} ppi) cache={face.FromCache} mode={(face.FromCache ? "cache" : m_Baker.LastMode)} ms={ms:F0}");
                if (label == "bake") SpikeReport.SavePng(face.Texture, $"face_{id}.png");
            }
        }

        /// <summary>Real-size baked signs on gantries along the highway.</summary>
        private IEnumerator TestSignGantries()
        {
            if (GantrySurface == null) throw new InvalidOperationException("no donor surface");
            var snap = m_Snap ?? FallbackSnap();

            // Main truss over both carriageways: exit-tab sign for our direction, long sign facing the other way.
            yield return PlaceTruss(snap, 0f, "SignBuilder_p1_truss", "guide-exit-tab", "guide-long", true);
            yield return PlaceTruss(snap, 60f, "SignBuilder_p1_truss2", "guide-exit-only", null, false);
            yield return PlaceMonotube(snap, -60f, "services-blue");
            yield return PlaceCantilever(snap, 120f, "attraction-brown");
            yield return PlaceCurved(snap, 180f, false, new[] { "guide-exit-only", "services-blue" });
            yield return PlaceCurved(snap, 240f, true, new[] { "guide-exit-tab" });
            yield return PlaceGround(snap, -25f, "gore-exit");
            yield return PlaceGround(snap, -150f, "advance-guide");
            yield return PlaceGround(snap, 290f, "services-blue");
            yield return PlaceGround(snap, 330f, "attraction-brown");
            yield return new WaitForSecondsRealtime(4f);
            Report("signGantries.instances", m_Runtime.CountInstances(m_Prefabs));
            SpikeReport.Set("signGantries.panels", m_Signs.Count);
        }

        /// <summary>Four copies of one sign with fixed emissive strengths, judged by night and by day.</summary>
        private IEnumerator TestNightGlow()
        {
            var snap = m_Snap ?? FallbackSnap();
            var s = SnapAlong(snap, -120f);
            var dir = s.MainDirection;
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            var extent = s.Extent;
            var origin = s.Point + s.Right * ((extent.x + extent.y) * 0.5f);
            var span = extent.y - extent.x + 5f;
            var prefab = m_Runtime.Create("SignBuilder_p1_glowTruss", GantryFactory.Truss(span), GantrySurface, m_Db, m_DbFolder);
            m_Prefabs.Add(prefab);
            for (var i = 0; i < 20; i++) yield return null;
            m_Runtime.ClearOverridable(prefab);
            m_Runtime.Spawn(prefab, origin, rotation);

            if (!m_Baked.TryGetValue("services-blue", out var face)) yield break;
            var glows = new[] { 0f, 0.15f, 0.5f, 1.5f };
            var right = math.mul(rotation, new float3(1, 0, 0));
            for (var i = 0; i < glows.Length; i++)
            {
                var x = (i - (glows.Length - 1) * 0.5f) * (span - 4f) / glows.Length;
                var pos = origin + right * x + new float3(0, SignCentreHeight(face), 0) - fwd * (GantryFactory.kTrussDepth * 0.5f + 0.15f);
                m_Signs.Add(face, pos, rotation, glows[i]);
            }
            SpikeReport.Set("nightGlow.leftToRight", string.Join(", ", glows));

            var planetary = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<PlanetarySystem>();
            var yaw = math.degrees(math.atan2(fwd.x, fwd.z));
            foreach (var (label, hour) in new[] { ("night", 22.5f), ("day", 11f) })
            {
                if (planetary != null) { planetary.overrideTime = true; planetary.time = hour; }
                SetCamera(origin + new float3(0, 7f, 0), yaw, 5f, 34f);
                yield return new WaitForSecondsRealtime(3f);
                SpikeReport.Screenshot($"shot_glow_{label}.png");
                yield return new WaitForSecondsRealtime(0.5f);
            }
            if (planetary != null) planetary.overrideTime = false;
        }

        // ------------------------------------------------------------------ placement helpers

        private static float SignCentreHeight(BakedFace face) =>
            math.max(GantryFactory.kMountHeight, 5.3f + face.HeightMeters * 0.5f);

        private IEnumerator PlaceTruss(RoadProbe.Snap snap, float distance, string name, string mainSign, string backSign, bool focus)
        {
            var s = SnapAlong(snap, distance);
            var dir = s.MainDirection;
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            var extent = s.Extent;
            var origin = s.Point + s.Right * ((extent.x + extent.y) * 0.5f);
            var prefab = m_Runtime.Create(name, GantryFactory.Truss(extent.y - extent.x + 5f), GantrySurface, m_Db, m_DbFolder);
            m_Prefabs.Add(prefab);
            for (var i = 0; i < 20; i++) yield return null;
            m_Runtime.ClearOverridable(prefab);
            m_Runtime.Spawn(prefab, origin, rotation);

            var standoff = GantryFactory.kTrussDepth * 0.5f + 0.15f;
            if (m_Baked.TryGetValue(mainSign, out var face))
            {
                var lanes = s.Lanes.Where(l => !l.OnPairedEdge && l.Direction == dir).ToList();
                var offset = lanes.Count > 0 ? lanes.Average(l => l.Offset) : 0f;
                m_Signs.Add(face, s.Point + s.Right * offset + new float3(0, SignCentreHeight(face), 0) - fwd * standoff, rotation);
            }
            if (backSign != null && s.PairedEdge != Entity.Null && m_Baked.TryGetValue(backSign, out var back))
            {
                var lanes = s.Lanes.Where(l => l.OnPairedEdge).ToList();
                var offset = lanes.Count > 0 ? lanes.Average(l => l.Offset) : s.PairedOffset;
                m_Signs.Add(back, s.Point + s.Right * offset + new float3(0, SignCentreHeight(back), 0) + fwd * standoff,
                    quaternion.LookRotationSafe(-fwd, new float3(0, 1, 0)));
            }
            if (focus)
            {
                m_FocusPoint = origin;
                m_FocusYaw = math.degrees(math.atan2(fwd.x, fwd.z));
            }
            SpikeReport.Set($"signGantries.{name}", $"origin={origin} span={extent.y - extent.x + 5f:F1} sign={mainSign} {(face != null ? $"{face.WidthMeters:F1}x{face.HeightMeters:F1}m" : "missing")}");
        }

        private IEnumerator PlaceMonotube(RoadProbe.Snap snap, float distance, string sign)
        {
            var s = SnapAlong(snap, distance);
            var dir = s.MainDirection;
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            var prefab = m_Runtime.Create("SignBuilder_p1_monotube", GantryFactory.Monotube(s.EdgeWidth + 5f), GantrySurface, m_Db, m_DbFolder);
            m_Prefabs.Add(prefab);
            for (var i = 0; i < 20; i++) yield return null;
            m_Runtime.ClearOverridable(prefab);
            m_Runtime.Spawn(prefab, s.Point, rotation);
            if (m_Baked.TryGetValue(sign, out var face))
            {
                m_Signs.Add(face, s.Point + new float3(0, SignCentreHeight(face), 0) - fwd * 0.45f, rotation);
            }
        }

        private IEnumerator PlaceCantilever(RoadProbe.Snap snap, float distance, string sign)
        {
            var s = SnapAlong(snap, distance);
            var dir = s.MainDirection;
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            var postOffset = dir * (s.EdgeWidth * 0.5f + 1.5f);
            var arm = s.EdgeWidth * 0.55f + 1.5f;
            var prefab = m_Runtime.Create("SignBuilder_p1_cantilever", GantryFactory.Cantilever(-arm), GantrySurface, m_Db, m_DbFolder);
            m_Prefabs.Add(prefab);
            for (var i = 0; i < 20; i++) yield return null;
            m_Runtime.ClearOverridable(prefab);
            var origin = s.Point + s.Right * postOffset;
            m_Runtime.Spawn(prefab, origin, rotation);
            if (m_Baked.TryGetValue(sign, out var face))
            {
                var signOffset = postOffset - dir * arm * 0.6f;
                m_Signs.Add(face, s.Point + s.Right * signOffset + new float3(0, SignCentreHeight(face), 0) - fwd * 0.45f, rotation);
            }
        }

        /// <summary>Curved tubular span (over the main carriageway) or cantilever, signs hanging below the pipe.</summary>
        private IEnumerator PlaceCurved(RoadProbe.Snap snap, float distance, bool cantilever, string[] signs)
        {
            var s = SnapAlong(snap, distance);
            var dir = s.MainDirection;
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            var faces = signs.Where(m_Baked.ContainsKey).Select(id => m_Baked[id]).ToList();
            var tallest = faces.Count > 0 ? faces.Max(f => f.HeightMeters) : 2.5f;
            var height = math.max(7.5f, 5.3f + tallest + 0.45f);
            var right = math.mul(rotation, new float3(1, 0, 0));

            Mesh mesh;
            float3 origin;
            var name = cantilever ? "SignBuilder_p1_curvedCantilever" : "SignBuilder_p1_curvedSpan";
            if (cantilever)
            {
                // Post just off the right-hand edge, arm reaching back over the lanes.
                var postOffset = dir * (s.EdgeWidth * 0.5f + 1.5f);
                var arm = s.EdgeWidth * 0.75f + 1.5f;
                mesh = GantryFactory.CurvedCantilever(arm, height);
                origin = s.Point + s.Right * postOffset;
            }
            else
            {
                mesh = GantryFactory.CurvedSpan(s.EdgeWidth + 5f, height);
                origin = s.Point;
            }
            var prefab = m_Runtime.Create(name, mesh, GantrySurface, m_Db, m_DbFolder);
            m_Prefabs.Add(prefab);
            for (var i = 0; i < 20; i++) yield return null;
            m_Runtime.ClearOverridable(prefab);
            m_Runtime.Spawn(prefab, origin, rotation);

            // Hang the signs side by side under the horizontal pipe, centred over the carriageway
            // (cantilever: towards the arm's end).
            var total = faces.Sum(f => f.WidthMeters) + 0.6f * (faces.Count - 1);
            var x = cantilever ? -(s.EdgeWidth * 0.75f + 1.5f) * 0.55f - total * 0.5f : -total * 0.5f;
            foreach (var face in faces)
            {
                var centreY = height - GantryFactory.kCurvedPipeRadius - 0.15f - face.HeightMeters * 0.5f;
                var pos = origin + right * (x + face.WidthMeters * 0.5f) + new float3(0, centreY, 0) - fwd * 0.4f;
                m_Signs.Add(face, pos, rotation);
                x += face.WidthMeters + 0.6f;
            }
            SpikeReport.Set($"signGantries.{name}", $"origin={origin} height={height:F1} signs={faces.Count}");
            if (!cantilever)
            {
                m_CurvedFocus = origin;
                m_CurvedYaw = math.degrees(math.atan2(fwd.x, fwd.z));
            }
        }

        /// <summary>Roadside sign on one or two posts, set back from the right-hand edge, facing traffic.</summary>
        private IEnumerator PlaceGround(RoadProbe.Snap snap, float distance, string sign)
        {
            if (!m_Baked.TryGetValue(sign, out var face)) yield break;
            var s = SnapAlong(snap, distance);
            var dir = s.MainDirection;
            var fwd = s.Tangent * dir;
            var rotation = quaternion.LookRotationSafe(fwd, new float3(0, 1, 0));
            var lateral = dir * (s.EdgeWidth * 0.5f + 2.5f + face.WidthMeters * 0.5f);
            var origin = s.Point + s.Right * lateral;
            var mesh = GantryFactory.GroundPosts(face.WidthMeters, face.HeightMeters, out var posts);
            var prefab = m_Runtime.Create($"SignBuilder_p1_ground_{sign}", mesh, GantrySurface, m_Db, m_DbFolder);
            m_Prefabs.Add(prefab);
            for (var i = 0; i < 20; i++) yield return null;
            m_Runtime.ClearOverridable(prefab);
            m_Runtime.Spawn(prefab, origin, rotation);
            m_Signs.Add(face, origin + new float3(0, GantryFactory.kGroundClearance + face.HeightMeters * 0.5f, 0), rotation);
            SpikeReport.Set($"signGantries.ground.{sign}", $"posts={posts} size={face.WidthMeters:F2}x{face.HeightMeters:F2}m at {origin}");
            if (sign == "advance-guide")
            {
                m_GroundFocus = origin;
                m_GroundYaw = math.degrees(math.atan2(fwd.x, fwd.z));
            }
            if (sign == "gore-exit") m_GoreFocus = origin;
        }

        private IEnumerator TestCurvedShots()
        {
            foreach (var (label, yawOffset, pitch, zoom) in new[] { ("front", 0f, 6f, 40f), ("side", 60f, 10f, 45f) })
            {
                SetCamera(m_CurvedFocus + new float3(0, 6f, 0), m_CurvedYaw + yawOffset, pitch, zoom);
                yield return new WaitForSecondsRealtime(3f);
                SpikeReport.Screenshot($"shot_curved_{label}.png");
                yield return new WaitForSecondsRealtime(0.5f);
            }
            foreach (var (label, focus) in new[] { ("advance", m_GroundFocus), ("gore", m_GoreFocus) })
            {
                SetCamera(focus + new float3(0, 2f, 0), m_GroundYaw - 20f, 6f, 22f);
                yield return new WaitForSecondsRealtime(3f);
                SpikeReport.Screenshot($"shot_ground_{label}.png");
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        // ------------------------------------------------------------------ misc

        private void UpdateNightGlow()
        {
            var planetary = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<PlanetarySystem>();
            if (planetary == null) return;
            var hour = planetary.time;
            // Full glow between 20:00 and 05:00, fading over an hour at dusk and dawn.
            var night = math.saturate(math.max(hour - 19f, 6f - hour));
            m_Signs.NightGlow = night * m_DefaultGlow;
        }

        private List<Entity> Instances(IEnumerable<StaticObjectPrefab> prefabs)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var wanted = new HashSet<Entity>(prefabs.Select(p => m_Runtime.PrefabSystem.GetEntity(p)));
            using var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            return entities.Where(e => wanted.Contains(em.GetComponentData<PrefabRef>(e).m_Prefab)).ToList();
        }

        private static void Report(string key, Dictionary<string, (int count, int overridden, int withBatches)> counts) =>
            SpikeReport.Set(key, string.Join(" ", counts.Select(kv => $"{kv.Key}={kv.Value.count}/batches={kv.Value.withBatches}/overridden={kv.Value.overridden}")));

        private static void RestoreContinueTarget()
        {
            var meta = AssetDatabase.user.GetAssets<SaveGameMetadata>().FirstOrDefault(m => m.name == kContinueSave);
            if (meta != null) GameManager.instance.settings.userState.lastSaveGameMetadata = meta;
            SpikeReport.Set("continueTarget", meta?.name ?? "ModTest not found");
        }
    }
}

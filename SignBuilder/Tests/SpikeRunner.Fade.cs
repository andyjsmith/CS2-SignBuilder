using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Systems;
using SignBuilder.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SignBuilder.Tests
{
    /// <summary>
    /// The screen fading to black while placing a sign (mode "fade"): with the camera held still, samples the auto
    /// exposure and the screen's brightness and black pixels for each case: nothing, a placement preview (gantry and
    /// roadside, near and far), the same with HDRP's NaN filter on, and a built sign, by day and at night.
    /// </summary>
    internal sealed partial class SpikeRunner
    {
        private const float kFadeSeconds = 12f;

        private IEnumerator RunFade()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            var ui = world.GetOrCreateSystemManaged<SignBuilderUISystem>();
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(em, near, 3);
            if (spots.Count < 3) { SpikeReport.Set("fade.status", "FAILED: no highway"); Finish(); yield break; }
            var planet = world.GetOrCreateSystemManaged<Game.Simulation.PlanetarySystem>();
            planet.overrideTime = true;

            // A built sign to compare with the preview (at the second spot, so it is not under the preview).
            yield return PlaceAndWait(() => tool.PlaceForTest(spots[1].edge, spots[1].t, new List<JObject> { Template("guide-exit-tab"), Template("guide-exit-only") }, Options("doubleTruss", "single")));
            var built = SignPlacementToolSystem.LastPlaced;
            tool.Deactivate();

            var probe = new RoadProbe(em, m_Runtime.PrefabSystem);
            void Look(int spot, float zoom)
            {
                var snap = probe.SnapAt(spots[spot].edge, spots[spot].t);
                var fwd = snap.Tangent * snap.MainDirection;
                SetCamera(snap.Point + new float3(0, 5f, 0), math.degrees(math.atan2(fwd.x, fwd.z)) + 180f, 10f, zoom);
            }
            void Gantry(int spot) => tool.ArrangeForTest(spots[spot].edge, spots[spot].t, new List<JObject> { Template("guide-exit-tab"), Template("guide-exit-only") }, Options("doubleTruss", "single"));
            var postsSpot = -1;
            void Posts(int spot)
            {
                var options = Options("ground", "single");
                tool.BeginPosts(Template("guide-exit-tab"), options);
                postsSpot = spot;
            }

            var cases = new (string name, float time, System.Action setup, int look, float zoom, bool stopNaNs)[]
            {
                ("day.none", 0.5f, () => { }, 0, 45f, false),
                ("day.gantry", 0.5f, () => Gantry(0), 0, 45f, false),
                ("day.gantryClose", 0.5f, () => Gantry(0), 0, 18f, false),
                ("day.gantryNoNaN", 0.5f, () => Gantry(0), 0, 18f, true),
                ("day.posts", 0.5f, () => Posts(2), 2, 25f, false),
                ("day.built", 0.5f, () => { }, 1, 18f, false),
                ("night.none", 0.02f, () => { }, 0, 45f, false),
                ("night.gantry", 0.02f, () => Gantry(0), 0, 45f, false),
                ("night.gantryClose", 0.02f, () => Gantry(0), 0, 18f, false),
                ("night.gantryNoNaN", 0.02f, () => Gantry(0), 0, 18f, true),
                ("night.posts", 0.02f, () => Posts(2), 2, 25f, false),
                ("night.built", 0.02f, () => { }, 1, 18f, false),
            };
            var cameraData = Camera.main.GetComponent<HDAdditionalCameraData>();
            SpikeReport.Set("fade.camera", $"stopNaNs={cameraData?.stopNaNs} aa={cameraData?.antialiasing} built={built?.PrefabName}");
            foreach (var c in cases)
            {
                planet.normalizedTime = c.time;
                if (cameraData != null) cameraData.stopNaNs = c.stopNaNs;
                tool.Deactivate();
                postsSpot = -1;
                // Settle on the plain view first, so each case starts from the same exposure.
                Look(c.look, c.zoom);
                yield return new WaitForSecondsRealtime(4f);
                c.setup();
                yield return FadeSample(c.name, () => { if (postsSpot >= 0) tool.PreviewForTest(spots[postsSpot].edge, spots[postsSpot].t); });
            }
            tool.Deactivate();
            if (cameraData != null) cameraData.stopNaNs = false;
            planet.overrideTime = false;
            ui.SetEditorOpen(false);
            SpikeReport.Set("fade.status", "ok");
            Finish();
        }

        /// <summary>Samples exposure, mean luminance and the share of black pixels every half second for <see cref="kFadeSeconds"/>.</summary>
        private IEnumerator FadeSample(string name, System.Action everyFrame)
        {
            var exposures = new List<string>();
            var lums = new List<string>();
            var blacks = new List<string>();
            var end = Time.realtimeSinceStartup + kFadeSeconds;
            var next = 0f;
            var shot = false;
            while (Time.realtimeSinceStartup < end)
            {
                everyFrame();
                yield return new WaitForEndOfFrame();
                if (Time.realtimeSinceStartup < next) continue;
                next = Time.realtimeSinceStartup + 0.5f;
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                var (lum, black) = Brightness(tex);
                if (!shot && Time.realtimeSinceStartup > end - 1f)
                {
                    SpikeReport.SavePng(tex, $"fade_{name}.png");
                    shot = true;
                }
                Object.Destroy(tex);
                lums.Add(lum.ToString("F3"));
                blacks.Add(black.ToString("F3"));
                exposures.Add(Exposure());
            }
            SpikeReport.Set($"fade.{name}.exposure", string.Join(" ", exposures));
            SpikeReport.Set($"fade.{name}.lum", string.Join(" ", lums));
            SpikeReport.Set($"fade.{name}.black", string.Join(" ", blacks));
        }

        private static (float lum, float black) Brightness(Texture2D tex)
        {
            var pixels = tex.GetPixels32();
            double sum = 0;
            int count = 0, black = 0;
            for (var i = 0; i < pixels.Length; i += 97)
            {
                var p = pixels[i];
                var l = (0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b) / 255f;
                sum += l;
                if (p.r < 3 && p.g < 3 && p.b < 3) black++;
                count++;
            }
            return ((float)(sum / count), (float)black / count);
        }

        private static readonly FieldInfo s_ExposureTextures = typeof(HDCamera).GetField("m_ExposureTextures", BindingFlags.NonPublic | BindingFlags.Instance);
        private static AsyncGPUReadbackRequest? s_ExposureRequest;
        private static string s_LastExposure = "?";

        /// <summary>The HDRP exposure texture (multiplier, EV) as of the last finished readback; a new one is requested each call.</summary>
        private static string Exposure()
        {
            if (s_ExposureRequest is { } r && r.done)
            {
                if (r.hasError) s_LastExposure = "err";
                else if (r.layerDataSize >= 8) { var d = r.GetData<float>(); s_LastExposure = $"{d[0]:G4}/{d[1]:G4}"; }
                else { var d = r.GetData<ushort>(); s_LastExposure = $"{Mathf.HalfToFloat(d[0]):G4}/{Mathf.HalfToFloat(d[1]):G4}"; }
                s_ExposureRequest = null;
            }
            if (s_ExposureRequest == null)
            {
                var hd = HDCamera.GetOrCreate(Camera.main);
                var textures = s_ExposureTextures?.GetValue(hd);
                var current = textures?.GetType().GetField("current").GetValue(textures) as RTHandle;
                if (current?.rt != null) s_ExposureRequest = AsyncGPUReadback.Request(current.rt);
                else return "none";
            }
            return s_LastExposure;
        }
    }
}

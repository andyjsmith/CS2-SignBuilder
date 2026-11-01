using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Core;
using SignBuilder.Systems;
using SignBuilder.Tools;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace SignBuilder.Tests
{
    internal sealed partial class SpikeRunner
    {
        // Counters in the game's UI page: sign renders, Sign Builder images loaded, requestAnimationFrame calls.
        private const string kInstallCounters =
            "(function(){if(window.__sbLeak)return;var c=window.__sbLeak={renders:0,images:0,frames:0};" +
            "var raf=window.requestAnimationFrame;window.requestAnimationFrame=function(f){c.frames++;return raf.call(window,f);};" +
            "try{var d=Object.getOwnPropertyDescriptor(HTMLImageElement.prototype,'src');Object.defineProperty(HTMLImageElement.prototype,'src'," +
            "{configurable:true,get:d.get,set:function(v){if(String(v).indexOf('signbuilder')>=0)c.images++;d.set.call(this,v);}});}catch(e){c.imageHook=String(e);}})();";
        private const string kWrapRender =
            "(function(){var R=window.SignRenderer;if(!R||R.__sbCounted)return;var o=R.render;R.render=function(){window.__sbLeak.renders++;return o.apply(this,arguments);};R.__sbCounted=true;})();";
        private const string kResetCounters = "(function(){var c=window.__sbLeak;if(c){c.renders=0;c.images=0;c.frames=0;}})();";
        private const string kReport =
            "engine.trigger('SignBuilder.testReport',JSON.stringify(Object.assign({wrapped:!!(window.SignRenderer&&window.SignRenderer.__sbCounted)},window.__sbLeak||{missing:true})));";

        /// <summary>
        /// Mode "leak-e2e": whether anything of Sign Builder keeps running once its panel is closed. Frame times and
        /// memory with the editor never opened, while open (designer, template thumbnails, a fresh bake), and for a
        /// minute after closing, with the renderer and baker counters for each phase. Phase markers go to Player.log
        /// so engine warnings (JobTempAlloc, CSS) can be counted per phase. About 3 minutes.
        /// </summary>
        private IEnumerator RunLeakE2E()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            var world = World.DefaultGameObjectInjectionWorld;
            var ui = world.GetOrCreateSystemManaged<SignBuilderUISystem>();
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            var page = GameManager.instance.userInterface.view.View;
            var baker = SignBuilderRuntime.Baker;
            page.ExecuteScript(kInstallCounters);
            yield return new WaitForSecondsRealtime(1f);

            IEnumerator Phase(string name, float seconds)
            {
                page.ExecuteScript(kResetCounters);
                baker.ScriptRuns = 0;
                baker.DrawnFrames = 0;
                Debug.Log($"[SignBuilder leak-e2e] begin {name}");
                var times = new List<float>();
                var native0 = Profiler.GetTotalAllocatedMemoryLong();
                var mono0 = Profiler.GetMonoUsedSizeLong();
                var gc0 = GC.GetTotalMemory(false);
                var end = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < end)
                {
                    yield return null;
                    times.Add(Time.unscaledDeltaTime * 1000f);
                }
                Debug.Log($"[SignBuilder leak-e2e] end {name}");
                ui.TestReport = null;
                page.ExecuteScript(kReport);
                for (var i = 0; i < 30 && ui.TestReport == null; i++) yield return null;
                times.Sort();
                float Pct(float p) => times.Count == 0 ? 0 : times[Math.Min(times.Count - 1, (int)(times.Count * p))];
                SpikeReport.Set($"leak.{name}.frames", $"n={times.Count} avg={times.Average():F1}ms p50={Pct(0.5f):F1} p99={Pct(0.99f):F1} max={times.Last():F1}");
                SpikeReport.Set($"leak.{name}.memoryMB",
                    $"native {native0 / 1048576f:F0}->{Profiler.GetTotalAllocatedMemoryLong() / 1048576f:F0} " +
                    $"mono {mono0 / 1048576f:F0}->{Profiler.GetMonoUsedSizeLong() / 1048576f:F0} gc {gc0 / 1048576f:F0}->{GC.GetTotalMemory(false) / 1048576f:F0}");
                SpikeReport.Set($"leak.{name}.uiPage", ui.TestReport ?? "no report");
                SpikeReport.Set($"leak.{name}.baker", $"scripts={baker.ScriptRuns} drawnFrames={baker.DrawnFrames}");
            }

            yield return Test("leak.baseline", Phase("baseline", 45f));

            // Open: the designer with shield signs (and the template thumbnails), then a fresh bake from placing a sign.
            ui.SetEditorOpen(true);
            yield return new WaitForSecondsRealtime(3f);
            page.ExecuteScript(kWrapRender);
            ui.OpenForEdit(new AssemblyRecord { Designs = new List<JObject> { Template("guide-long") } }, "new");
            yield return Test("leak.open", Phase("open", 15f));
            var design = Template("lane-split");
            var text = design.SelectTokens("$..text").OfType<JValue>().FirstOrDefault();
            if (text != null) text.Value = "Leak " + DateTime.Now.Ticks % 100000; // never cached: a real bake
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(world.EntityManager, near, 1);
            var placedBefore = SignPlacementToolSystem.LastPlaced;
            if (spots.Count > 0)
            {
                page.ExecuteScript(kResetCounters);
                baker.ScriptRuns = 0;
                yield return PlaceAndWait(() => tool.PlaceForTest(spots[0].edge, spots[0].t, new List<JObject> { design }, Options("ground", "single")));
                SpikeReport.Set("leak.place.baker", $"scripts={baker.ScriptRuns} drawnFrames={baker.DrawnFrames} placed={SignPlacementToolSystem.LastPlaced != placedBefore}");
            }
            else
            {
                SpikeReport.Set("leak.place.baker", "no highway to place on");
            }

            // Closed: nothing of ours should run.
            ui.SetEditorOpen(false);
            yield return new WaitForSecondsRealtime(1f);
            SpikeReport.Set("leak.closed.toolActive", tool.Busy);
            yield return Test("leak.after", Phase("after", 60f));

            var placed = SignPlacementToolSystem.LastPlaced;
            if (placed != null && placed != placedBefore)
            {
                foreach (var e in InstancesOf(placed.PrefabName)) m_Runtime.Delete(e);
            }
            SpikeReport.Set("leak.status", "ok");
            Finish();
        }
    }
}

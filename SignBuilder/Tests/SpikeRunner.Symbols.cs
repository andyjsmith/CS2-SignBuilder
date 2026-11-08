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

namespace SignBuilder.Tests
{
    internal sealed partial class SpikeRunner
    {
        /// <summary>
        /// Mode "symbols-e2e": symbol libraries end to end. The libraries as read (symbols' SVGs into parts, legend
        /// colors); the designer with a symbol part (its picker, arrows picked there, an arrow borrowing another
        /// library, the symbol box); and signs baked and placed: symbols plain and boxed, the new MUTCD arrows,
        /// borrowed arrows, and a 1.0 design next to the same design made now. A few minutes.
        /// </summary>
        private IEnumerator RunSymbolsE2E()
        {
            yield return WaitForGame();
            yield return new WaitForSecondsRealtime(5f);
            m_Runtime = new RuntimePrefabs();
            var world = World.DefaultGameObjectInjectionWorld;
            var ui = world.GetOrCreateSystemManaged<SignBuilderUISystem>();
            var tool = world.GetOrCreateSystemManaged<SignPlacementToolSystem>();
            yield return Test("sym.libraries", TestLibraries());
            yield return Test("sym.designer", TestSymbolDesigner(ui));
            yield return Test("sym.place", TestSymbolPlace(tool));
            ui.SetEditorOpen(false);
            RestoreContinueTarget();
            Finish();
        }

        private IEnumerator TestLibraries()
        {
            foreach (var lib in SymbolLibraries.All.Values)
            {
                var arrows = lib["arrows"] as JObject;
                var symbols = lib["symbols"] as JObject;
                var shapes = arrows?.Properties().Sum(c => ((c.Value as JObject)?["shapes"] as JObject)?.Count ?? 0) ?? 0;
                SpikeReport.Set($"sym.lib.{lib["id"]}", $"categories={arrows?.Count ?? 0} shapes={shapes} symbols={symbols?.Count ?? 0} head={lib["diagramHead"] != null}");
                foreach (var s in symbols?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var parts = s.Value["parts"] as JArray;
                    SpikeReport.Set($"sym.symbol.{lib["id"]}/{s.Name}",
                        $"viewBox={s.Value["viewBox"]?.ToString(Newtonsoft.Json.Formatting.None)} parts={parts?.Count} fills={string.Join(",", parts?.Select(p => (string)p["fill"]).Distinct() ?? Enumerable.Empty<string>())}");
                }
            }
            SpikeReport.Set("sym.themes", string.Join(" ", Themes.All.Select(t => $"{t.Id}:{(t.Def["libraries"] as JArray)?.ToString(Newtonsoft.Json.Formatting.None) ?? "-"}")));
            yield break;
        }

        private static JObject SymbolDesign(int format) => JObject.Parse(@"{
            ""formatVersion"": " + format + @", ""theme"": ""us"", ""scheme"": ""green"", ""align"": ""center"",
            ""items"": [
              { ""type"": ""row"", ""items"": [
                { ""type"": ""symbol"", ""symbol"": ""mutcd/snowflake"", ""box"": {} },
                { ""type"": ""symbol"", ""symbol"": ""mutcd/ev-charging"" },
                { ""type"": ""symbol"", ""symbol"": ""mutcd/ev-charging"", ""box"": { ""background"": ""#1f4ea1"" } },
                { ""type"": ""text"", ""text"": ""SYMBOLS"" } ] },
              { ""type"": ""row"", ""items"": [
                { ""type"": ""arrow"", ""direction"": ""up"" },
                { ""type"": ""arrow"", ""direction"": ""up-right"" },
                { ""type"": ""arrow"", ""direction"": ""bend-right"" },
                { ""type"": ""arrow"", ""direction"": ""hook-left"" },
                { ""type"": ""arrow"", ""style"": ""wide"", ""direction"": ""left"" },
                { ""type"": ""arrow"", ""library"": ""uk"", ""direction"": ""up"" },
                { ""type"": ""arrow"", ""library"": ""sweden"", ""style"": ""lane"", ""direction"": ""add-left"" },
                { ""type"": ""arrow"", ""direction"": ""turn-left"" } ] },
              { ""type"": ""diagram"", ""shape"": ""exit-right"", ""lanes"": 2, ""exitLanes"": 1, ""height"": 50 }
            ] }");

        private static readonly JObject OldDesign = JObject.Parse(@"{
            ""formatVersion"": 1, ""theme"": ""us"", ""scheme"": ""green"", ""align"": ""center"",
            ""items"": [
              { ""type"": ""text"", ""text"": ""MADE WITH 1.0"" },
              { ""type"": ""row"", ""items"": [ { ""type"": ""arrow"", ""direction"": ""up"" }, { ""type"": ""arrow"", ""direction"": ""up-right"" },
                { ""type"": ""arrow"", ""direction"": ""turn-left"" }, { ""type"": ""arrow"", ""direction"": ""split-right"" } ] },
              { ""type"": ""diagram"", ""shape"": ""exit-right"", ""lanes"": 2, ""exitLanes"": 1, ""height"": 50 }
            ] }");

        /// <summary>Runs <paramref name="script"/> on the UI page and returns what it reports (engine.trigger testReport).</summary>
        private IEnumerator Ask(SignBuilderUISystem ui, string script, string key)
        {
            ui.TestReport = null;
            GameManager.instance.userInterface.view.View.ExecuteScript(
                "try { engine.trigger('SignBuilder.testReport', JSON.stringify((function () { " + script + " })())); } catch (e) { engine.trigger('SignBuilder.testReport', 'ERROR ' + e); }");
            for (var i = 0; i < 60 && ui.TestReport == null; i++) yield return null;
            SpikeReport.Set(key, ui.TestReport ?? "no report");
        }

        private static void Run(string script) => GameManager.instance.userInterface.view.View.ExecuteScript("try { " + script + " } catch (e) {}");

        // Clicks the innermost element whose own text is <text> (DOM click events reach React in Gameface).
        private const string ClickText = "var clickText = function (t) { var all = Array.prototype.slice.call(document.querySelectorAll('div')); " +
            "var e = all.filter(function (d) { return d.textContent === t && d.children.length === 0; })[0]; " +
            "if (e) e.dispatchEvent(new MouseEvent('click', { bubbles: true })); return !!e; };";

        private IEnumerator TestSymbolDesigner(SignBuilderUISystem ui)
        {
            ui.SetEditorOpen(true);
            yield return new WaitForSecondsRealtime(2f);
            ui.OpenForEdit(new AssemblyRecord { Designs = new List<JObject> { SymbolDesign(2) } }, "new");
            yield return new WaitForSecondsRealtime(3f);
            // The boxed snowflake: its settings.
            Run("window.__signBuilderSelect('0.0');");
            yield return new WaitForSecondsRealtime(1.5f);
            SpikeReport.Screenshot("sym_designer_symbol.png");
            yield return Ask(ui, "var t = document.body.textContent; return { symbolButton: t.indexOf('Snowflake') >= 0, display: t.indexOf('In a box') >= 0, boxBackground: t.indexOf('Box background') >= 0, borderColor: t.indexOf('Border color') >= 0 };", "sym.designer.symbolOptions");
            // The picker: symbols by category, then the theme's arrows.
            Run("window.__signBuilderPickSymbol();");
            yield return new WaitForSecondsRealtime(1.5f);
            SpikeReport.Screenshot("sym_picker_symbols.png");
            yield return Ask(ui, "var t = document.body.textContent; return { open: t.indexOf('Choose a symbol') >= 0, roadConditions: t.indexOf('Road conditions') >= 0, services: t.indexOf('Services') >= 0, arrowsStandard: t.indexOf('Arrows: Standard') >= 0, arrowsLane: t.indexOf('Arrows: Lane') >= 0, arrowsWide: t.indexOf('Arrows: Wide') >= 0 };", "sym.picker.categories");
            Run(ClickText + "clickText('Arrows: Standard');");
            yield return new WaitForSecondsRealtime(1.5f);
            SpikeReport.Screenshot("sym_picker_arrows.png");
            yield return Ask(ui, "return { cells: document.querySelectorAll('[title^=\"Standard: \"]').length };", "sym.picker.standardArrows");
            // Picking one turns the part into that arrow.
            Run("var c = document.querySelector('[title=\"Standard: hook-right\"]'); if (c) c.dispatchEvent(new MouseEvent('click', { bubbles: true }));");
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Ask(ui, "return window.__signBuilderDesign.items[0].items[0];", "sym.picker.pickedArrow");
            // Another library's arrows in the picker (the UK's lane arrows), picked onto the EV symbol.
            Run("window.__signBuilderSelect('0.1');");
            yield return new WaitForSecondsRealtime(1f);
            Run("window.__signBuilderPickSymbol();");
            yield return new WaitForSecondsRealtime(1.5f);
            Run(ClickText + "clickText(\"This theme's\");");
            yield return new WaitForSecondsRealtime(1f);
            SpikeReport.Screenshot("sym_picker_libraries.png");
            Run(ClickText + "clickText('TSRGD (United Kingdom)');");
            yield return new WaitForSecondsRealtime(1f);
            Run(ClickText + "clickText('Arrows: Lane');");
            yield return new WaitForSecondsRealtime(1.5f);
            SpikeReport.Screenshot("sym_picker_uk_lane.png");
            yield return Ask(ui, "return { cells: document.querySelectorAll('[title^=\"Lane: \"]').length };", "sym.picker.ukLaneArrows");
            Run("var c = document.querySelector('[title=\"Lane: turn-left\"]'); if (c) c.dispatchEvent(new MouseEvent('click', { bubbles: true }));");
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Ask(ui, "return window.__signBuilderDesign.items[0].items[1];", "sym.picker.ukPicked");
            // The borrowed UK arrow: "Arrows from" shows the UK library.
            Run("window.__signBuilderSelect('1.5');");
            yield return new WaitForSecondsRealtime(1.5f);
            SpikeReport.Screenshot("sym_designer_borrowed.png");
            yield return Ask(ui, "var t = document.body.textContent; return { arrowsFrom: t.indexOf('Arrows from') >= 0, uk: t.indexOf('TSRGD') >= 0 };", "sym.designer.borrowed");
            // A 1.0 design: drawn with the theme's arrows, with no note about them (note and button false).
            ui.OpenForEdit(new AssemblyRecord { Designs = new List<JObject> { OldDesign } }, "new");
            yield return new WaitForSecondsRealtime(3f);
            SpikeReport.Screenshot("sym_designer_old.png");
            yield return Ask(ui, "var t = document.body.textContent; return { note: t.indexOf('keeps the arrows it was made with') >= 0, button: t.indexOf('Use the new arrows') >= 0 };", "sym.designer.oldNote");
            ui.SetEditorOpen(false);
            yield return new WaitForSecondsRealtime(1f);
        }

        private IEnumerator TestSymbolPlace(SignPlacementToolSystem tool)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(em, near, 2);
            if (spots.Count == 0) { SpikeReport.Set("sym.place", "no highway"); yield break; }
            var spot = spots[0];
            // The same signs made with 1.0 and now, side by side on one gantry (they should look the same).
            var now = (JObject)OldDesign.DeepClone();
            now["formatVersion"] = 2;
            now["items"][0]["text"] = "MADE NOW";
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, spot.t, new List<JObject> { OldDesign, now }, Options("doubleTruss", "full")));
            SpikeReport.Set("sym.place.formats", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "sym_formats.png", 10f, 40f);
            // Symbols and the new arrows on a gantry, and a yellow sign with a boxed symbol on roadside posts.
            var yellow = JObject.Parse(@"{ ""formatVersion"": 2, ""theme"": ""us"", ""scheme"": ""yellow"", ""items"": [
                { ""type"": ""row"", ""items"": [ { ""type"": ""text"", ""text"": ""ICY"" }, { ""type"": ""symbol"", ""symbol"": ""mutcd/snowflake"", ""box"": {} } ] } ] }");
            var t2 = math.clamp(spot.t + 0.25f, 0.05f, 0.95f);
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, t2, new List<JObject> { SymbolDesign(2) }, Options("doubleTruss", "full")));
            SpikeReport.Set("sym.place.symbols", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "sym_symbols.png", 10f, 40f);
            yield return PlaceAndWait(() => tool.PlaceForTest(spot.edge, math.clamp(t2 - 0.12f, 0.05f, 0.95f), new List<JObject> { yellow }, Options("ground", "single")));
            SpikeReport.Set("sym.place.yellow", Describe(SignPlacementToolSystem.LastPlaced));
            yield return Shoot(SignPlacementToolSystem.LastPlaced, "sym_yellow.png", 20f, 18f);
            SpikeReport.Set("sym.place.batchErrors", CountLogLines("Error when initializing batch"));
            SpikeReport.Set("sym.place.textureErrors", CountLogLines("Texture creation failed"));
        }
    }
}

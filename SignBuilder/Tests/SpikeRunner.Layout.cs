using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Core;
using SignBuilder.Rendering;
using SignBuilder.Systems;
using SignBuilder.Tools;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace SignBuilder.Tests
{
    /// <summary>
    /// The editors' layout (part of mode "ui-e2e"): wrapped rows of buttons never run past their space or under what
    /// follows them, at the game's UI scale and smaller and larger ones; the margin controls stay clear of the parts
    /// panel's scrollbar; the gantry header keeps each control's buttons together; a fork's lane controls; the MUTCD
    /// symbols; and every US template bakes.
    /// </summary>
    internal sealed partial class SpikeRunner
    {
        /// <summary>
        /// Checks every wrapped row on the page: a cell past its row set's right edge (with others beside it to wrap
        /// instead), a cell outside its row, or a field running into the next one. Reports { flows, multiRow, bad }.
        /// </summary>
        private const string kLayoutCheck = @"
            var byClass = function (name) { return Array.prototype.slice.call(document.querySelectorAll('[class^=""' + name + '_""],[class*="" ' + name + '_""]')); };
            var has = function (e, name) { return (' ' + (e.className || '')).indexOf(' ' + name + '_') >= 0; };
            var flows = byClass('flow').concat(byClass('flowGrow'));
            var bad = [], multi = 0;
            var label = function (e) { return String(e.textContent || '').replace(/\s+/g, ' ').slice(0, 24); };
            flows.forEach(function (f) {
              var r = f.getBoundingClientRect();
              if (r.width <= 0) return;
              var rows = Array.prototype.slice.call(f.children);
              if (rows.length > 1) multi++;
              rows.forEach(function (row) {
                var rr = row.getBoundingClientRect();
                var cells = Array.prototype.slice.call(row.children);
                cells.forEach(function (c) {
                  var cr = c.getBoundingClientRect();
                  if (cells.length > 1 && cr.right > r.right + 1.5) bad.push('past edge: ' + label(c) + ' ' + Math.round(cr.right) + '>' + Math.round(r.right));
                  if (cr.top < rr.top - 1.5 || cr.bottom > rr.bottom + 1.5) bad.push('outside row: ' + label(c));
                });
              });
              var field = f;
              while (field && !has(field, 'field')) field = field.parentElement;
              var next = field && field.nextElementSibling;
              if (next) {
                var fr = field.getBoundingClientRect(), nr = next.getBoundingClientRect();
                if (nr.height > 0 && nr.top < fr.bottom - 1.5) bad.push('runs into next: ' + label(field) + ' / ' + label(next));
              }
            });
            return { flows: flows.length, multiRow: multi, bad: bad.slice(0, 12) };";

        private IEnumerator TestLayout(SignBuilderUISystem ui, SignPlacementToolSystem tool)
        {
            var page = GameManager.instance.userInterface.view.View;
            // A sign with a symbol part (the color rows the report was about) and a fork made before lanes left / right.
            var design = JObject.Parse(@"{ ""formatVersion"": 2, ""theme"": ""us"", ""scheme"": ""green"", ""items"": [
              { ""type"": ""text"", ""text"": ""Layout"" },
              { ""type"": ""symbol"", ""symbol"": ""mutcd/gas"", ""height"": 30 },
              { ""type"": ""diagram"", ""shape"": ""fork"", ""lanes"": 3, ""exitLanes"": 2, ""height"": 50 }
            ] }");
            ui.SetEditorOpen(true);
            ui.OpenForEdit(new AssemblyRecord { Designs = new List<JObject> { design } }, "new");
            yield return new WaitForSecondsRealtime(3f);
            yield return Ask(ui, "return { fontSize: getComputedStyle(document.documentElement).fontSize, w: window.innerWidth, h: window.innerHeight };", "layout.page");

            // Each kind of part, at the game's scale, then smaller and larger ones (as other resolutions and UI scales).
            foreach (var scale in new[] { 1f, 0.75f, 1.35f })
            {
                if (scale != 1f) Run($"document.documentElement.style.fontSize = (parseFloat(getComputedStyle(document.documentElement).fontSize) * {scale}) + 'px'; window.dispatchEvent(new Event('resize'));");
                foreach (var (path, name) in new[] { ("null", "sign"), ("'0'", "text"), ("'1'", "symbol"), ("'2'", "fork") })
                {
                    Run($"window.__signBuilderSelect({path}); window.dispatchEvent(new Event('resize'));");
                    yield return new WaitForSecondsRealtime(0.8f);
                    yield return Ask(ui, kLayoutCheck, $"layout.{scale:0.##}.{name}");
                    if (scale != 1.35f) SpikeReport.Screenshot($"layout_{scale:0.##}_{name}.png");
                }
                Run("document.documentElement.style.fontSize = '';");
                yield return new WaitForSecondsRealtime(0.5f);
            }

            // The margin controls (a text part selected) against the parts panel's scrollbar.
            Run("window.__signBuilderSelect('0'); window.dispatchEvent(new Event('resize'));");
            yield return new WaitForSecondsRealtime(1f);
            yield return Ask(ui, @"
                var byClass = function (name) { return Array.prototype.slice.call(document.querySelectorAll('[class^=""' + name + '_""],[class*="" ' + name + '_""]')); };
                var sides = byClass('marginSide');
                var right = 0;
                sides.forEach(function (s) { right = Math.max(right, s.getBoundingClientRect().right); });
                var scroll = byClass('partsScroll')[0];
                var sr = scroll ? scroll.getBoundingClientRect() : null;
                var track = null;
                if (scroll) Array.prototype.slice.call(scroll.querySelectorAll('*')).forEach(function (e) {
                  if (!track && /track/i.test(String(e.className || ''))) track = e;
                });
                var tr = track ? track.getBoundingClientRect() : null;
                return { sides: sides.length, controlsRight: Math.round(right), scrollRight: sr && Math.round(sr.right),
                  trackLeft: tr && Math.round(tr.left), trackWidth: tr && Math.round(tr.width), clear: !tr || tr.width === 0 || right <= tr.left + 0.5 };", "layout.margin");
            SpikeReport.Screenshot("layout_margin.png");

            // A fork made before lanes left / right (3 lanes, 2 each way: one shared) shows them so; + on Lanes left
            // stores lanesLeft 3, lanesRight 2, sharedLanes 1.
            Run("window.__signBuilderSelect('2');");
            yield return new WaitForSecondsRealtime(1f);
            yield return Ask(ui, "var t = document.body.textContent; return { left: t.indexOf('Lanes left') >= 0, right: t.indexOf('Lanes right') >= 0, middle: t.indexOf('Middle lane') >= 0, oneShared: t.indexOf('One shared') >= 0 };", "layout.fork.fields");
            Run(@"var labels = Array.prototype.slice.call(document.querySelectorAll('div')).filter(function (d) { return d.children.length === 0 && d.textContent === 'Lanes left'; });
                  var field = labels[0] && labels[0].parentElement;
                  var steps = field ? Array.prototype.slice.call(field.querySelectorAll('div')).filter(function (d) { return d.textContent === '+' && d.children.length === 0; }) : [];
                  if (steps[0]) steps[0].dispatchEvent(new MouseEvent('click', { bubbles: true }));");
            yield return new WaitForSecondsRealtime(1f);
            yield return Ask(ui, "return window.__signBuilderDesign.items[2];", "layout.fork.afterPlus");
            SpikeReport.Screenshot("layout_fork.png");

            // The MUTCD symbols, as the editor has them (read from their SVG files by the mod).
            yield return Ask(ui, @"var lib = window.SignRenderer && window.SignRenderer.libraries.mutcd;
                var syms = lib ? lib.def.symbols : {};
                var ids = Object.keys(syms);
                return { count: ids.length, empty: ids.filter(function (k) { return !syms[k].parts || !syms[k].parts.length; }),
                  colored: ids.filter(function (k) { return (syms[k].parts || []).some(function (p) { return p.fill !== 'currentColor'; }); }) };", "layout.symbols");
            ui.SetEditorOpen(false);
            yield return new WaitForSecondsRealtime(1f);

            // The gantry header: each label stays on a row with its buttons ("Off" next to "On").
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var near = (float3)(CameraController()?.pivot ?? Camera.main.transform.position);
            var spots = HighwaySpots(em, near, 1);
            if (spots.Count > 0)
            {
                ui.SetEditorOpen(true);
                tool.ArrangeForTest(spots[0].edge, spots[0].t, new List<JObject> { Template("guide-exit-tab"), Template("guide-exit-only") }, Options("doubleTruss", "single"));
                yield return WaitArrange(tool, ui);
                yield return new WaitForSecondsRealtime(1.5f);
                foreach (var scale in new[] { 1f, 0.75f, 1.35f })
                {
                    if (scale != 1f) Run($"document.documentElement.style.fontSize = (parseFloat(getComputedStyle(document.documentElement).fontSize) * {scale}) + 'px'; window.dispatchEvent(new Event('resize'));");
                    yield return new WaitForSecondsRealtime(1f);
                    yield return Ask(ui, @"var leaf = function (t) { return Array.prototype.slice.call(document.querySelectorAll('div')).filter(function (d) { return d.children.length === 0 && d.textContent === t; })[0]; };
                        var top = function (t) { var e = leaf(t); return e ? Math.round(e.getBoundingClientRect().top) : null; };
                        return { off: top('Off'), on: top('On'), front: top('Front'), back: top('Back'), showing: top('Showing'), lights: top('Lights') };", $"layout.arrange.{scale:0.##}");
                    yield return Ask(ui, kLayoutCheck, $"layout.arrange.{scale:0.##}.check");
                    SpikeReport.Screenshot($"layout_arrange_{scale:0.##}.png");
                    Run("document.documentElement.style.fontSize = ''; window.dispatchEvent(new Event('resize'));");
                }
                tool.Deactivate();
                ui.SetEditorOpen(false);
            }
            else SpikeReport.Set("layout.arrange", "no highway");
            yield return new WaitForSecondsRealtime(1f);
        }

        /// <summary>Bakes every US template: its face size, and a picture of each.</summary>
        private IEnumerator TestUsTemplates()
        {
            var dir = Path.Combine(SignBuilderRuntime.ResourcesDir, "Themes", "us", "templates");
            var sizes = new List<string>();
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                BakedFace face = null;
                yield return SignBuilderRuntime.Baker.Bake(JObject.Parse(File.ReadAllText(file)), 3f, f => face = f);
                sizes.Add(face == null ? $"{name}=FAILED" : $"{name}={face.WidthMeters:F2}x{face.HeightMeters:F2}");
                if (face?.Texture != null) SpikeReport.SavePng(face.Texture, $"us_{name}.png");
            }
            SpikeReport.Set("templates.us", string.Join(" ", sizes));
        }
    }
}

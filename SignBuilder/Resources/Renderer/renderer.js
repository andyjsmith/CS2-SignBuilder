// SignBuilder sign renderer.
//
// Builds a sign face from a JSON design. All dimensions in a design are inches (as in the MUTCD
// and the FHWA Standard Sign Builder book); they are converted to pixels at render time. The same
// file is meant to back the in-game editor preview, so the editor and the baked in-world texture
// always agree.
//
// The game calls window.hsRender(design, pixelsPerInch) via View.ExecuteScript. The sign is laid
// out at the top-left of the page on a transparent background; the C# side measures its pixel
// bounding box to learn the physical size, so no data needs to flow back to C#.
//
// Roadgeek 2014 fonts are normalized to a cap height of 1000 units in a 1750-unit em, so a CSS
// font-size of 1.75 x the letter height gives exact MUTCD capital heights.

(function () {
  "use strict";

  // Everything country-specific (fonts, colors, route plates, arrows, defaults, units) comes from a theme
  // (Themes/<id>/theme.json). Themes are registered by the editor, or travel inside the design as "_theme".
  var THEMES = {};
  var T = null; // theme of the sign being rendered
  var DEFAULTS = null;
  var BASE_DEFAULTS = {
    font: "", border: 2, cornerRadius: 12, textHeight: 16, shieldHeight: 36, arrowHeight: 24,
    lineGap: 0.75, // x tallest legend in the block
    margin: 0.75, // x tallest legend on the sign
    itemGap: 0.6, // horizontal gap between row items, x row height
    scheme: "green",
  };
  var DEFAULT_ARROW = "M32 0 H68 V88 H100 L50 150 L0 88 H32 Z";
  // Families that already have an @font-face rule on this page (rules are never rewritten: re-parsing a style
  // sheet makes Gameface fetch every font in it again, all at once).
  var loadedFamilies = {};

  function registerTheme(def, base) {
    if (!def || !def.id) return;
    var json = JSON.stringify(def);
    var known = THEMES[def.id];
    if (known && known.json === json && known.base === (base || "")) return;
    var fonts = {};
    (def.fonts || []).forEach(function (f) { fonts[f.family] = f; });
    THEMES[def.id] = { def: def, base: base || "", fonts: fonts, json: json, fontsLoaded: false };
  }

  // A theme's fonts are added to the page the first time one of its signs is drawn, one style element per theme.
  function ensureFonts(theme) {
    if (!theme || theme.fontsLoaded) return;
    theme.fontsLoaded = true;
    var css = "";
    (theme.def.fonts || []).forEach(function (f) {
      if (!f.file || loadedFamilies[f.family]) return;
      loadedFamilies[f.family] = true;
      var url = /^[a-z]+:\/\//.test(f.file) ? f.file : theme.base + f.file;
      css += '@font-face { font-family: "' + f.family + '"; src: url("' + url + '"); }\n';
    });
    if (!css) return;
    var style = document.createElement("style");
    style.textContent = css;
    document.head.appendChild(style);
  }

  function useTheme(doc) {
    if (doc && doc._theme) registerTheme(doc._theme, doc._themeBase);
    T = THEMES[(doc && doc.theme) || "us"] || THEMES.us || THEMES[Object.keys(THEMES)[0]] || { def: {}, base: "", fonts: {}, fontsLoaded: true };
    ensureFonts(T);
    DEFAULTS = {};
    for (var k in BASE_DEFAULTS) DEFAULTS[k] = BASE_DEFAULTS[k];
    var d = T.def.defaults || {};
    for (var j in d) DEFAULTS[j] = d[j];
    // Designs are measured in the theme's units: inches, or millimeters for metric countries.
    ppu = ppi / (T.def.units === "mm" ? 25.4 : 1);
  }

  var ppu = 3;
  var ppi = 3;
  // Assets resolve relative to this script so the renderer also works when loaded into the game's
  // main UI page (the editor preview), not only from renderer.html.
  var BASE = ((document.currentScript && document.currentScript.src) || "").replace(/[^\/]*$/, "");
  var annotate = false;

  // Tags an element with the design path of the item it renders, so the editor can map clicks back.
  function tag(e, path) {
    if (annotate && e && path != null) e.setAttribute("data-sb-path", path);
    return e;
  }

  // Theme units (inches or millimeters) to CSS pixels.
  function px(v) {
    return (Math.round(v * ppu * 100) / 100) + "px";
  }

  function el(tag, className, style) {
    var e = document.createElement(tag);
    if (className) e.className = className;
    if (style) for (var k in style) e.style[k] = style[k];
    return e;
  }

  function scheme(name) {
    var schemes = T.def.schemes || {};
    return schemes[name] || schemes[DEFAULTS.scheme] || schemes[Object.keys(schemes)[0]] || { bg: "#006b45", fg: "#ffffff" };
  }

  // ---------------------------------------------------------------- element heights

  // Nominal height (inches) of an element, used for spacing rules.
  function heightOf(item) {
    if (!item) return 0;
    switch (item.type) {
      case "text": return item.height || DEFAULTS.textHeight;
      case "shield": return item.height || DEFAULTS.shieldHeight;
      case "arrow": return item.height || DEFAULTS.arrowHeight;
      case "spacer": return item.height || 0;
      case "row":
      case "stack":
      case "panel":
        var h = 0;
        (item.items || []).forEach(function (c) { h = Math.max(h, heightOf(c)); });
        return h;
      default: return 0;
    }
  }

  // ---------------------------------------------------------------- elements

  // Font size and vertical offset per letter height: "scale" = em / cap height, "offset" puts the capitals'
  // top at the top of the box (from the font's metrics; Roadgeek 2014 is 1.75 and 0.125).
  function metrics(family) {
    var f = T.fonts[family];
    return { scale: (f && f.scale) || 1.75, offset: f && f.offset != null ? f.offset : 0.125 };
  }

  // <tracking>: extra space between letters, in hundredths of the letter height (negative tightens).
  function textBox(text, h, family, color, tracking) {
    var m = metrics(family);
    var e = el("div", "sb-text", {
      fontFamily: family, fontSize: px(h * m.scale), lineHeight: px(h), height: px(h), top: px(h * m.offset), color: color,
    });
    if (tracking) {
      // Letter spacing also follows the last letter: take it back so the text stays centered.
      e.style.letterSpacing = px(h * tracking / 100);
      e.sbMarginRight = -h * tracking / 100;
      e.style.marginRight = px(e.sbMarginRight);
    }
    e.textContent = text;
    return e;
  }

  function renderText(item, ctx) {
    var h = item.height || DEFAULTS.textHeight;
    var e = textBox(item.text || "", h, item.font || ctx.font, item.color || ctx.fg, item.tracking);
    return e;
  }

  // Route plates come from the theme: "img" (artwork, number on top), "ellipse", "rect" (colored plate with an
  // optional border) or "plain" (just colored text, e.g. UK primary route numbers).
  function renderShield(item, ctx) {
    var shields = T.def.shields || {};
    var def = shields[item.network] || shields[Object.keys(shields)[0]] || { shape: "rect", bg: "#ffffff", fg: "#000000" };
    var h = item.height || DEFAULTS.shieldHeight;
    var number = (def.prefix || "") + String(item.number != null ? item.number : "");
    var wide = (String(item.number || "").length >= 3 && def.wide) || 1;
    var font = (wide > 1 && def.wideFont) || def.font || ctx.font;
    var shape = def.shape || (def.img ? "img" : "rect");
    var nh = h * (def.num || 0.6) * (wide > 1 && def.img ? 0.9 : 1);
    if (shape === "rect" || shape === "plain") {
      // Plates size to their text: padding either side, like European route number plates.
      var padX = h * (def.padX != null ? def.padX : 0.25);
      var label = textBox(number, nh, font, def.fg || ctx.fg);
      var plate = el("div", "sb-plate", {
        display: "flex", flexDirection: "row", alignItems: "center", justifyContent: "center",
        height: px(h), padding: "0px " + px(padX), minWidth: px(h * (def.minAspect || 0)),
        backgroundColor: shape === "plain" ? "transparent" : (def.bg || "#ffffff"),
        borderRadius: px(h * (def.radius != null ? def.radius : 0.12)),
      });
      if (def.border && shape !== "plain") {
        plate.style.border = px(h * (def.borderWidth || 0.06)) + " solid " + def.border;
      }
      label.style.top = "0px";
      label.style.marginTop = px(nh * metrics(font).offset);
      plate.appendChild(label);
      return plate;
    }
    var w = h * (def.aspect || 1) * wide;
    var box = el("div", "sb-shield", { width: px(w), height: px(h) });
    if (shape === "ellipse") {
      box.appendChild(el("div", null, {
        position: "absolute", left: "0px", top: "0px", width: px(w), height: px(h),
        borderRadius: px(h / 2), backgroundColor: def.bg || "#ffffff",
      }));
      if (def.border) {
        box.appendChild(el("div", null, {
          position: "absolute", left: px(h * 0.06), top: px(h * 0.06), width: px(w - h * 0.12 - h * 0.04), height: px(h * 0.84),
          borderRadius: px(h / 2), border: px(h * 0.02) + " solid " + def.border,
        }));
      }
    } else {
      var img = el("img", null, { width: px(w), height: px(h) });
      img.src = T.base + def.img;
      box.appendChild(img);
    }
    var m = metrics(font);
    var num = el("div", "sb-num", {
      width: px(w), top: px(h * (def.top != null ? def.top : 0.3) + nh * m.offset),
      fontFamily: font, fontSize: px(nh * m.scale), lineHeight: px(nh), height: px(nh), color: def.fg || def.color || "#000000",
    });
    num.textContent = number;
    box.appendChild(num);
    return box;
  }

  var ARROWS = {
    // Shapes drawn pointing down in a 100 x 150 box, rotated for other directions.
    down: 0, up: 180, left: 90, right: -90, "down-left": 45, "down-right": -45, "up-left": 135, "up-right": -135,
  };
  // Lane-use arrows drawn pointing up in a 100 x 150 box (filled outlines, so no stroke support is needed):
  // a shaft that bends off to the left, and a straight arrow with a branch bending left. "-right" mirrors them.
  var CURVED = {
    turn: "M54 150 V82 A24 24 0 0 0 30 58 V78 L0 46 L30 14 V34 A48 48 0 0 1 78 82 V150 Z",
    split: "M54 150 V128 A24 24 0 0 0 30 104 V124 L0 92 L30 60 V80 A48 48 0 0 1 54 86.4 V34 H34 L66 0 L98 34 H78 V150 Z",
  };

  // Corner points of a path made of absolute M/L/H/V commands (enough to find a rotated arrow's bounds).
  function pathPoints(d) {
    var pts = [], x = 0, y = 0, re = /([MLHVZ])([^MLHVZ]*)/gi, m;
    while ((m = re.exec(d))) {
      var n = (m[2].match(/-?[\d.]+/g) || []).map(Number);
      var c = m[1].toUpperCase();
      if (c === "M" || c === "L") { for (var i = 0; i + 1 < n.length; i += 2) { x = n[i]; y = n[i + 1]; pts.push([x, y]); } }
      else if (c === "H") { n.forEach(function (v) { x = v; pts.push([x, y]); }); }
      else if (c === "V") { n.forEach(function (v) { y = v; pts.push([x, y]); }); }
    }
    return pts.length ? pts : [[0, 0], [100, 150]];
  }

  // Arrows are as tall as their height when pointing up or down; turned arrows keep the same shape size and
  // get a box that fits them (so diagonal and sideways arrows are never clipped).
  function renderArrow(item, ctx) {
    var h = item.height || DEFAULTS.arrowHeight;
    var dir = item.direction || "down";
    var ns = "http://www.w3.org/2000/svg";
    var svg = document.createElementNS(ns, "svg");
    var path = document.createElementNS(ns, "path");
    var curved = /^(turn|split)-(left|right)$/.exec(dir);
    var vb;
    if (curved) {
      path.setAttribute("d", CURVED[curved[1]]);
      if (curved[2] === "right") path.setAttribute("transform", "translate(100 0) scale(-1 1)");
      vb = [0, 0, 100, 150];
    } else {
      var d = T.def.arrow || DEFAULT_ARROW;
      var rot = ARROWS[dir] || 0;
      path.setAttribute("d", d);
      if (rot) path.setAttribute("transform", "rotate(" + rot + " 50 75)");
      var r = rot * Math.PI / 180, cos = Math.cos(r), sin = Math.sin(r);
      var x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
      pathPoints(d).forEach(function (p) {
        var dx = p[0] - 50, dy = p[1] - 75;
        var X = 50 + dx * cos - dy * sin, Y = 75 + dx * sin + dy * cos;
        x0 = Math.min(x0, X); y0 = Math.min(y0, Y); x1 = Math.max(x1, X); y1 = Math.max(y1, Y);
      });
      vb = [x0, y0, x1 - x0, y1 - y0];
    }
    var unit = h / 150;
    svg.setAttribute("viewBox", vb.join(" "));
    svg.setAttribute("width", String(vb[2] * unit * ppu));
    svg.setAttribute("height", String(vb[3] * unit * ppu));
    path.setAttribute("fill", item.color || ctx.fg);
    svg.appendChild(path);
    return svg;
  }

  // Height used for spacing rules: legend (letter) height, not the full height of shields or
  // arrows. MUTCD spacing is expressed in terms of letter heights, so a 36" shield next to 16"
  // legends must not inflate the gaps and margins.
  function spacingHeight(item) {
    if (!item) return 0;
    switch (item.type) {
      case "text": return item.height || DEFAULTS.textHeight;
      case "shield": return Math.min((item.height || DEFAULTS.shieldHeight) * 0.45, DEFAULTS.textHeight);
      case "arrow": return Math.min((item.height || DEFAULTS.arrowHeight) * 0.6, DEFAULTS.textHeight);
      case "row":
      case "stack":
      case "panel":
        var h = 0;
        (item.items || []).forEach(function (c) { h = Math.max(h, spacingHeight(c)); });
        return h;
      default: return 0;
    }
  }

  function renderContainer(item, ctx, horizontal, path) {
    var e = el("div", horizontal ? "sb-row" : "sb-stack");
    var align = { start: "flex-start", left: "flex-start", center: "center", end: "flex-end", right: "flex-end" }[item.align || (horizontal ? "center" : "start")];
    // A spread-out row ("between") fills the width of its column and puts the space between its parts.
    if (horizontal) e.style.justifyContent = item.justify === "between" ? "space-between" : "flex-start";
    if (horizontal && item.justify === "between") e.style.alignSelf = "stretch";
    e.style.alignItems = align;
    var items = item.items || [];
    items.forEach(function (child, i) {
      var c = renderItem(child, ctx, path == null ? null : (path === "" ? String(i) : path + "." + i), horizontal);
      if (!c) return;
      // A part in a column can override the column's alignment ("place": start, center or end).
      if (!horizontal && child.place) c.style.alignSelf = { start: "flex-start", center: "center", end: "flex-end" }[child.place] || "auto";
      var gap = 0;
      if (i > 0) {
        // Gap between neighbors, scaled from the larger legend height of the pair.
        var ref = Math.max(spacingHeight(items[i - 1]), spacingHeight(child));
        gap = child.gapBefore != null ? child.gapBefore
          : item.gap != null ? item.gap
          : ref * (horizontal ? DEFAULTS.itemGap : DEFAULTS.lineGap);
      }
      // A part's own margin ({ top, right, bottom, left } in sign units) adds to the gap before it.
      var m = child.margin || {};
      c.style.marginTop = px((horizontal ? 0 : gap) + (m.top || 0));
      c.style.marginLeft = px((horizontal ? gap : 0) + (m.left || 0));
      c.style.marginBottom = px(m.bottom || 0);
      c.style.marginRight = px((c.sbMarginRight || 0) + (m.right || 0));
      e.appendChild(c);
    });
    return e;
  }

  // <inRow>: the item sits in a row (a divider there is a vertical line between columns).
  function renderItem(item, ctx, path, inRow) {
    var e;
    switch (item && item.type) {
      case "text": e = renderText(item, ctx); break;
      case "shield": e = renderShield(item, ctx); break;
      case "arrow": e = renderArrow(item, ctx); break;
      case "row": e = renderContainer(item, ctx, true, path); break;
      case "stack": e = renderContainer(item, ctx, false, path); break;
      case "panel": e = renderPanel(item, ctx, path); break;
      case "spacer": e = el("div", null, { width: px(item.width || 0), height: px(item.height || 0) }); break;
      case "divider":
        e = el("div", null, inRow
          ? { alignSelf: "stretch", width: px(item.thickness || DEFAULTS.border), backgroundColor: item.color || ctx.fg }
          : { alignSelf: "stretch", height: px(item.thickness || DEFAULTS.border), backgroundColor: item.color || ctx.fg });
        break;
      default: return null;
    }
    return tag(e, path);
  }

  // A colored box with its own parts inside (a patch or panel): its scheme (or custom colors) sets the background
  // and the legend color of everything in it.
  function renderPanel(item, ctx, path) {
    var s = item.scheme ? scheme(item.scheme) : { bg: ctx.fg, fg: ctx.bg };
    var bg = item.background || s.bg;
    var fg = item.foreground || s.fg;
    var h = spacingHeight(item) || DEFAULTS.textHeight;
    var pad = item.padding != null ? item.padding : h * 0.3;
    var inner = renderContainer({ type: item.vertical ? "stack" : "row", items: item.items || [], align: item.align }, { font: ctx.font, fg: fg, bg: bg }, !item.vertical, path);
    var e = el("div", "sb-panel-part", {
      display: "flex", flexDirection: "row", backgroundColor: bg, padding: px(pad * 0.7) + " " + px(pad),
      borderRadius: px(item.radius != null ? item.radius : h * 0.15),
    });
    if (item.border) e.style.border = px(item.borderWidth || DEFAULTS.border) + " solid " + (item.border === true ? fg : item.border);
    e.appendChild(inner);
    return e;
  }

  // ---------------------------------------------------------------- sign-level parts

  // E11-1 style EXIT ONLY panel: "EXIT ↓ ONLY", or "↓ EXIT ONLY ↓" for two arrows. The two words are editable.
  var EXIT_ONLY_HEIGHT = 14;
  function exitOnlyItems(bar) {
    var h = bar.height || EXIT_ONLY_HEIGHT;
    var arrow = { type: "arrow", direction: "down", height: h * 1.5 };
    var cfg = T.def.exitOnly || {};
    var words = bar.words || cfg.words || ["EXIT", "ONLY"];
    var exit = { type: "text", text: words[0], height: h, font: cfg.font };
    var only = { type: "text", text: words[1], height: h, font: cfg.font };
    var arrows = bar.arrows || 1;
    if (arrows >= 2) return [arrow, exit, only, arrow];
    return [exit, arrow, only];
  }

  function renderBar(bar, ctx, innerRadius) {
    var s = scheme(bar.scheme || (bar.type === "exitOnly" ? "yellow" : "green"));
    var items = bar.type === "exitOnly" ? exitOnlyItems(bar) : (bar.items || []);
    var h = bar.type === "exitOnly" ? (bar.height || EXIT_ONLY_HEIGHT) * 1.5 : heightOf({ type: "row", items: items });
    var pad = (bar.padding != null ? bar.padding : h * 0.35);
    var e = el("div", "sb-bar", {
      backgroundColor: s.bg, padding: px(pad) + " " + px(pad * 2),
      borderBottomLeftRadius: px(innerRadius), borderBottomRightRadius: px(innerRadius),
      display: "flex", flexDirection: "row", justifyContent: "center",
    });
    e.appendChild(renderContainer({ type: "row", items: items, gap: h * 0.5 }, { font: (T.def.exitOnly || {}).font || ctx.font, fg: s.fg, bg: s.bg }, true));
    return e;
  }

  function renderSign(doc) {
    useTheme(doc);
    // Custom colors ("background" / "foreground" as CSS colors) override the scheme.
    var base = scheme(doc.scheme);
    var s = { bg: doc.background || base.bg, fg: doc.foreground || base.fg };
    var border = doc.border != null ? doc.border : DEFAULTS.border;
    var radius = doc.cornerRadius != null ? doc.cornerRadius : DEFAULTS.cornerRadius;
    var legend = spacingHeight({ type: "stack", items: doc.items || [] }) || DEFAULTS.textHeight;
    var margin = doc.margin != null ? doc.margin : legend * DEFAULTS.margin;
    var ctx = { font: doc.font || DEFAULTS.font, fg: s.fg, bg: s.bg };
    var tab = doc.exitTab;
    var tabSide = tab ? (tab.align || "right") : null;

    var sign = el("div", "sb-sign");

    if (tab) {
      // Exit plaque letters, smaller than the sign's legends so the tab stays in proportion with game-sized signs.
      var th = tab.height || (T.def.exitTab && T.def.exitTab.height) || 10;
      var tabRow = el("div", "sb-tab-row", { justifyContent: { left: "flex-start", center: "center", right: "flex-end" }[tabSide] });
      var tabBox = el("div", "sb-tab", {
        backgroundColor: s.bg, padding: px(border) + " " + px(border) + " 0px " + px(border),
        borderTopLeftRadius: px(radius), borderTopRightRadius: px(radius),
      });
      var tabInner = el("div", "sb-tab-inner", {
        borderStyle: "solid", borderColor: s.fg,
        borderTopWidth: px(border), borderLeftWidth: px(border), borderRightWidth: px(border), borderBottomWidth: "0px",
        borderTopLeftRadius: px(radius - border), borderTopRightRadius: px(radius - border),
        padding: px(th * 0.45) + " " + px(th * 0.7) + " " + px(th * 0.45) + " " + px(th * 0.7),
      });
      tabInner.appendChild(tag(renderText({ text: tab.text || "EXIT", height: th, font: tab.font || (T.def.exitTab && T.def.exitTab.font) || ctx.font }, ctx), "exitTab"));
      tabBox.appendChild(tabInner);
      tabRow.appendChild(tabBox);
      sign.appendChild(tabRow);
    }

    var panel = el("div", "sb-panel", {
      backgroundColor: s.bg, padding: px(border),
      borderRadius: px(radius),
    });
    // The corner under the exit tab is square.
    if (tabSide === "right") panel.style.borderTopRightRadius = "0px";
    if (tabSide === "left") panel.style.borderTopLeftRadius = "0px";
    if (doc.width) panel.style.width = px(doc.width - border * 2);
    if (doc.minWidth) panel.style.minWidth = px(doc.minWidth - border * 2);

    var innerRadius = Math.max(radius - border, 0);
    var inner = el("div", "sb-panel-inner", {
      borderStyle: "solid", borderColor: s.fg, borderWidth: px(border), borderRadius: px(innerRadius),
    });
    if (tabSide === "right") inner.style.borderTopRightRadius = "0px";
    if (tabSide === "left") inner.style.borderTopLeftRadius = "0px";

    var content = renderContainer({ type: "stack", items: doc.items || [], align: doc.align || "start" }, ctx, false, "");
    content.style.padding = px(margin);
    if (doc.height) content.style.minHeight = px(doc.height - border * 4 - margin * 2);
    inner.appendChild(content);
    if (doc.bottomBar) inner.appendChild(tag(renderBar(doc.bottomBar, ctx, innerRadius), "bottomBar"));
    panel.appendChild(inner);
    sign.appendChild(panel);
    return sign;
  }

  // Public API, shared by the bake page and the in-game editor preview.
  window.SignRenderer = {
    // Returns a detached element for the design. options.annotate tags elements with data-sb-path.
    render: function (doc, pixelsPerInch, options) {
      ppi = pixelsPerInch || 3;
      annotate = !!(options && options.annotate);
      try { return renderSign(doc || {}); } finally { annotate = false; }
    },
    registerTheme: registerTheme,
    themes: THEMES,
    arrows: Object.keys(ARROWS).concat(["turn-left", "turn-right", "split-left", "split-right"]),
  };

  window.hsRender = function (doc, pixelsPerInch) {
    ppi = pixelsPerInch || 3;
    var root = document.getElementById("root");
    while (root.firstChild) root.removeChild(root.firstChild);
    try {
      root.appendChild(renderSign(doc || {}));
      window.hsLastError = null;
    } catch (e) {
      window.hsLastError = String(e && e.stack || e);
      var err = el("div", "sb-error");
      err.textContent = "Render error: " + window.hsLastError;
      root.appendChild(err);
    }
    window.hsRenderCount = (window.hsRenderCount || 0) + 1;
  };

  // URL form: renderer.html#<ppi>|<url-encoded JSON>. Used by the game as a fallback when
  // ExecuteScript does not repaint, and handy for previewing in a desktop browser.
  function renderFromHash() {
    if (location.hash.length <= 1) return;
    try {
      // Gameface percent-encodes the whole hash (including the "|"), browsers do not: decode first.
      var body = decodeURIComponent(location.hash.substring(1));
      var bar = body.indexOf("|");
      var scale = bar > 0 ? parseFloat(body.substring(0, bar)) : 3;
      window.hsRender(JSON.parse(bar > 0 ? body.substring(bar + 1) : body), scale);
    } catch (e) {
      window.hsLastError = "hash: " + e;
    }
  }
  renderFromHash();
  window.addEventListener("hashchange", renderFromHash);
})();

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
  // Set while drawing a sign's glow (see hsRender).
  var glowOnly = false;
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
      case "diagram": return item.height || DEFAULTS.arrowHeight * 2.2;
      case "led": return (item.height || DEFAULTS.textHeight * 0.8) * Math.max(1, (item.lines || []).length) * 1.5;
      case "spacer": return item.height || 0;
      case "row":
      case "column":
      case "panel":
      case "chip":
      case "tab":
        var h = 0;
        (item.items || []).forEach(function (c) { h = Math.max(h, heightOf(c)); });
        return h;
      default: return 0;
    }
  }

  // ---------------------------------------------------------------- elements

  // Font size and vertical offset per letter height: "scale" = em / cap height, "offset" puts the capitals'
  // top at the top of the box (from the font's metrics; Roadgeek 2014 is 1.75 and 0.125).
  // "weight" picks a face of a family the game itself provides (e.g. "Noto Sans JP" bold), which has no font file.
  var LED_COLOR = "#ffb000";

  // Family names are quoted: some are not valid bare CSS names (e.g. "Perfect DOS VGA 437").
  function quoted(family) {
    return family ? '"' + String(family).replace(/"/g, "") + '"' : family;
  }

  function metrics(family) {
    var f = T.fonts[family];
    return { scale: (f && f.scale) || 1.75, offset: f && f.offset != null ? f.offset : 0.125, weight: (f && f.weight) || "normal" };
  }

  // <tracking>: extra space between letters, in hundredths of the letter height (negative tightens).
  function textBox(text, h, family, color, tracking) {
    var m = metrics(family);
    var e = el("div", "sb-text", {
      fontFamily: quoted(family), fontWeight: m.weight, fontSize: px(h * m.scale), lineHeight: px(h), height: px(h), top: px(h * m.offset), color: color,
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
    // On a message board, legends are LED lettering (unless the part picks a font).
    if (ctx.led && !item.font) return ledText(item.text || "", h, item.color || ctx.fg);
    return textBox(item.text || "", h, item.font || ctx.font, item.color || ctx.fg, item.tracking);
  }

  // 5 x 7 dot-matrix letters, as on LED message boards: 7 rows of 5 bits each (top row first).
  var LED_GLYPHS = {
    "A": "0e11111f111111", "B": "1e11111e11111e", "C": "0e11101010110e", "D": "1c12111111121c", "E": "1f10101e10101f",
    "F": "1f10101e101010", "G": "0e11101711110f", "H": "1111111f111111", "I": "0e04040404040e", "J": "0702020202120c",
    "K": "11121418141211", "L": "1010101010101f", "M": "111b1515111111", "N": "11111915131111", "O": "0e11111111110e",
    "P": "1e11111e101010", "Q": "0e11111115120d", "R": "1e11111e141211", "S": "0f10100e01011e", "T": "1f040404040404",
    "U": "1111111111110e", "V": "11111111110a04", "W": "1111111515150a", "X": "11110a040a1111", "Y": "1111110a040404",
    "Z": "1f01020408101f", "0": "0e11131519110e", "1": "040c040404040e", "2": "0e11010204081f", "3": "1f02040201110e",
    "4": "02060a121f0202", "5": "1f101e0101110e", "6": "0608101e11110e", "7": "1f010204080808", "8": "0e11110e11110e",
    "9": "0e11110f01020c", " ": "00000000000000", ".": "00000000000c0c", ",": "000000000c0408", ":": "000c0c000c0c00",
    "-": "0000001f000000", "/": "00010204081000", "!": "04040404040004", "?": "0e110102040004", "$": "040f140e051e04",
    "&": "0c12140815120d", "'": "0c040800000000", "(": "02040808080402", ")": "08040202020408", "+": "0004041f040400",
    "%": "18190204081303", "#": "0a0a1f0a1f0a0a", "\"": "0a0a0a00000000", "@": "0e11010d15150e",
    "=": "00001f001f0000", "<": "02040810080402", ">": "08040201020408", "*": "0004150e150400",
  };

  // LED lettering: one dot per lit pixel of each letter, <h> tall (7 dots), in <color>. Unknown characters are blank.
  function ledText(text, h, color) {
    var chars = String(text).toUpperCase().split("");
    var pitch = h / 7, dot = pitch * 0.8;
    var d = [];
    chars.forEach(function (ch, i) {
      var g = LED_GLYPHS[ch] || LED_GLYPHS[" "];
      for (var row = 0; row < 7; row++) {
        var bits = parseInt(g.substr(row * 2, 2), 16);
        for (var col = 0; col < 5; col++) {
          if (!(bits & (16 >> col))) continue;
          var x = (i * 6 + col) * pitch, y = row * pitch;
          d.push("M" + x.toFixed(2) + " " + y.toFixed(2) + "h" + dot.toFixed(2) + "v" + dot.toFixed(2) + "h-" + dot.toFixed(2) + "Z");
        }
      }
    });
    var w = Math.max(1, chars.length * 6 - 1) * pitch;
    var ns = "http://www.w3.org/2000/svg";
    var svg = document.createElementNS(ns, "svg");
    svg.setAttribute("class", "sb-led");
    svg.setAttribute("viewBox", "0 0 " + w.toFixed(2) + " " + h.toFixed(2));
    svg.setAttribute("width", String(w * ppu));
    svg.setAttribute("height", String(h * ppu));
    if (d.length) {
      var path = document.createElementNS(ns, "path");
      path.setAttribute("d", d.join(""));
      path.setAttribute("fill", color);
      svg.appendChild(path);
    }
    // The glow pass (the lit parts of the sign at night) shows only LED lettering.
    if (glowOnly) svg.style.visibility = "visible";
    return svg;
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
      fontFamily: quoted(font), fontWeight: m.weight, fontSize: px(nh * m.scale), lineHeight: px(nh), height: px(nh), color: def.fg || def.color || "#000000",
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

  // ---------------------------------------------------------------- diagrams and message boards

  // A road diagram (the arrow of a diagrammatic sign): a wide through arrow with dashed lane lines and a branch
  // curving off (exit left / right), a fork, or a roundabout with its exits. Drawn as filled shapes (lane lines are
  // separate dashes in the background color), sized by its height.
  function renderDiagram(item, ctx) {
    var L = 14, headLen = 25, headOver = 11;
    var fg = item.color || ctx.fg, gapColor = ctx.bg;
    var shapes = [], dashes = [];
    var shape = item.shape || "exit-left";
    var rect = function (x, y, w, h) { return [[x, y], [x + w, y], [x + w, y + h], [x, y + h]]; };
    // An arrowhead at <e> pointing along <d>. Its base reaches a little back into the shaft, so no seam of
    // background shows between them (anti-aliased edges that only touch leave a faint line).
    var head = function (e, d, half) {
      var n = [-d[1], d[0]], back = 0.8;
      var b = [e[0] - d[0] * back, e[1] - d[1] * back];
      return [[b[0] + n[0] * half, b[1] + n[1] * half], [e[0] + d[0] * headLen, e[1] + d[1] * headLen], [b[0] - n[0] * half, b[1] - n[1] * half]];
    };
    var dash = function (cx, cy, dx, dy) {
      // A dash 8 long and 2 wide centered on (cx, cy) along (dx, dy).
      var nx = -dy, ny = dx;
      dashes.push([[cx - dx * 4 + nx, cy - dy * 4 + ny], [cx + dx * 4 + nx, cy + dy * 4 + ny], [cx + dx * 4 - nx, cy + dy * 4 - ny], [cx - dx * 4 - nx, cy - dy * 4 - ny]]);
    };
    if (shape === "roundabout") {
      // Ring around (0, 0), entry from below, an arm with an arrowhead per exit.
      var R = 32, band = 11;
      for (var k = 0; k < 40; k++) {
        var a0 = k / 40 * Math.PI * 2, a1 = (k + 1.15) / 40 * Math.PI * 2;
        shapes.push([[Math.cos(a0) * (R - band / 2), Math.sin(a0) * (R - band / 2)], [Math.cos(a0) * (R + band / 2), Math.sin(a0) * (R + band / 2)],
          [Math.cos(a1) * (R + band / 2), Math.sin(a1) * (R + band / 2)], [Math.cos(a1) * (R - band / 2), Math.sin(a1) * (R - band / 2)]]);
      }
      shapes.push(rect(-band / 2, R, band, 50));
      var angles = { left: Math.PI, up: -Math.PI / 2, right: 0, "up-left": -Math.PI * 3 / 4, "up-right": -Math.PI / 4 };
      (item.exits || ["left", "up", "right"]).forEach(function (exit) {
        var a = angles[exit];
        if (a == null) return;
        var d = [Math.cos(a), Math.sin(a)], n = [-d[1], d[0]];
        var s0 = R, s1 = R + band / 2 + 20;
        shapes.push([[d[0] * s0 + n[0] * band / 2, d[1] * s0 + n[1] * band / 2], [d[0] * s1 + n[0] * band / 2, d[1] * s1 + n[1] * band / 2],
          [d[0] * s1 - n[0] * band / 2, d[1] * s1 - n[1] * band / 2], [d[0] * s0 - n[0] * band / 2, d[1] * s0 - n[1] * band / 2]]);
        shapes.push(head([d[0] * s1, d[1] * s1], d, band / 2 + headOver - 2));
      });
    } else {
      var lanes = Math.max(1, Math.round(item.lanes || 2));
      var exitLanes = Math.max(1, Math.round(item.exitLanes || 1));
      var sw = lanes * L, sx = -sw / 2, bottom = 150, top = 0;
      var fork = shape === "fork";
      var shaftTop = fork ? 75 : top + headLen;
      shapes.push(rect(sx, shaftTop, sw, bottom - shaftTop));
      if (!fork) shapes.push(head([0, shaftTop], [0, -1], sw / 2 + headOver));
      for (var lane = 1; lane < lanes; lane++) {
        for (var y = bottom - 7; y > shaftTop + 6; y -= 15) dash(sx + lane * L, y, 0, -1);
      }
      var sides = shape === "exit-right" ? [1] : shape === "exit-left" ? [-1] : [-1, 1];
      sides.forEach(function (side) {
        var bw = exitLanes * L, Rc = 46 + bw, sweep = 1.15;
        // The branch leaves the shaft's edge going up, then bends off to its side.
        var startX = side < 0 ? sx + bw / 2 : sx + sw - bw / 2;
        var cy = fork ? shaftTop + 4 : 122;
        var cx = startX + side * Rc;
        var at = function (phi, r) { return [cx - side * r * Math.cos(phi), cy + r * Math.sin(phi)]; };
        var outer = [], inner = [];
        for (var k2 = 0; k2 <= 14; k2++) {
          var phi = -sweep * k2 / 14;
          outer.push(at(phi, Rc + bw / 2));
          inner.push(at(phi, Rc - bw / 2));
        }
        shapes.push(outer.concat(inner.reverse()));
        var end = at(-sweep, Rc);
        var d = [side * Math.sin(sweep), -Math.cos(sweep)];
        shapes.push(head(end, d, bw / 2 + headOver));
        for (var bl = 1; bl < exitLanes; bl++) {
          var r = Rc - bw / 2 + bl * L;
          for (var k3 = 1; k3 < 5; k3++) {
            var ph = -sweep * (k3 / 5), p0 = at(ph, r), p1 = at(ph - 0.01, r);
            var len = Math.hypot(p1[0] - p0[0], p1[1] - p0[1]) || 1;
            dash(p0[0], p0[1], (p1[0] - p0[0]) / len, (p1[1] - p0[1]) / len);
          }
        }
      });
    }
    var x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
    shapes.forEach(function (poly) { poly.forEach(function (pt) { x0 = Math.min(x0, pt[0]); y0 = Math.min(y0, pt[1]); x1 = Math.max(x1, pt[0]); y1 = Math.max(y1, pt[1]); }); });
    var h = item.height || DEFAULTS.arrowHeight * 2.2;
    var unit = h / (y1 - y0);
    var ns = "http://www.w3.org/2000/svg";
    var svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", [x0, y0, x1 - x0, y1 - y0].join(" "));
    svg.setAttribute("width", String((x1 - x0) * unit * ppu));
    svg.setAttribute("height", String(h * ppu));
    var draw = function (poly, color) {
      var path = document.createElementNS(ns, "path");
      path.setAttribute("d", "M" + poly.map(function (pt) { return pt[0].toFixed(2) + " " + pt[1].toFixed(2); }).join(" L") + " Z");
      path.setAttribute("fill", color);
      svg.appendChild(path);
    };
    shapes.forEach(function (poly) { draw(poly, fg); });
    dashes.forEach(function (poly) { draw(poly, gapColor); });
    return svg;
  }

  // An LED display: lines of pixel lettering on a black panel (a variable message sign, or a price display inside a
  // sign).
  function renderMessage(item, ctx) {
    var h = item.height || DEFAULTS.textHeight * 0.8;
    var color = item.color || LED_COLOR;
    var box = el("div", "sb-message", {
      display: "flex", flexDirection: "column", alignItems: { start: "flex-start", end: "flex-end" }[item.align] || "center",
      backgroundColor: item.background || "#0b0b0b", padding: px(h * 0.35) + " " + px(h * 0.5),
    });
    (item.lines && item.lines.length ? item.lines : [""]).forEach(function (line, i) {
      var t = ledText(line, h, color);
      if (i > 0) t.style.marginTop = px(h * 0.4);
      box.appendChild(t);
    });
    return box;
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
      case "column":
      case "panel":
      case "chip":
      case "tab":
        var h = 0;
        (item.items || []).forEach(function (c) { h = Math.max(h, spacingHeight(c)); });
        return h;
      default: return 0;
    }
  }

  // Tabs sit on top of the sign; panels are bands across its full width (at the top, between parts or at the
  // bottom, like an EXIT ONLY panel). Both are top-level parts the sign lays out itself.
  function isTab(item) { return !!item && item.type === "tab"; }
  function isPanel(item) { return !!item && item.type === "panel"; }
  function inBody(item) { return !isTab(item) && !isPanel(item); }

  // <only>: indices of <item>.items to draw (a stretch of the sign's body between panels); all when absent.
  function renderContainer(item, ctx, horizontal, path, only) {
    var e = el("div", horizontal ? "sb-row" : "sb-stack");
    var align = { start: "flex-start", left: "flex-start", center: "center", end: "flex-end", right: "flex-end" }[item.align || (horizontal ? "center" : "start")];
    // A spread-out row ("between") fills the width of its column and puts the space between its parts; an "even"
    // row fills it too and centers each part in an equal share of it (dividers keep their own width).
    var spread = horizontal && (item.justify === "between" || item.justify === "even");
    if (horizontal) e.style.justifyContent = item.justify === "between" ? "space-between" : "flex-start";
    if (spread) e.style.alignSelf = "stretch";
    e.style.alignItems = align;
    var items = item.items || [];
    var previous = null;
    var even = horizontal && item.justify === "even";
    var last = null;
    items.forEach(function (child, i) {
      if (only && only.indexOf(i) < 0) return;
      var c = renderItem(child, ctx, path == null ? null : (path === "" ? String(i) : path + "." + i), horizontal);
      if (!c) return;
      // A part in a column can override the column's alignment ("place": start, center or end).
      if (!horizontal && child.place) c.style.alignSelf = { start: "flex-start", center: "center", end: "flex-end" }[child.place] || "auto";
      var gap = 0;
      if (previous) {
        // Gap between neighbors, scaled from the larger legend height of the pair.
        var ref = Math.max(spacingHeight(previous), spacingHeight(child));
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
      // The part's own margin in layout pixels (the editor's selection outline includes it).
      c.sbMargin = { top: (m.top || 0) * ppu, right: (m.right || 0) * ppu, bottom: (m.bottom || 0) * ppu, left: (m.left || 0) * ppu };
      previous = child;
      if (even) {
        var right = 0;
        if (child.type !== "divider") {
          var cell = el("div", null, {
            // Content width plus an equal share of the spare width: never narrower than its part, so a sign laid
            // out at its natural width does not collapse the cells.
            flexGrow: "1", flexShrink: "0", flexBasis: "auto", display: "flex", flexDirection: "row",
            justifyContent: "center", alignItems: align,
          });
          c.style.marginLeft = px(m.left || 0);
          cell.appendChild(c);
          c = cell;
        } else {
          c.style.marginLeft = px(gap / 2 + (m.left || 0));
          right = (c.sbMarginRight || 0) + (m.right || 0);
        }
        // Half the gap on this side, half on the previous part's side.
        if (child.type !== "divider") c.style.marginLeft = px(gap / 2);
        if (last && gap) last.el.style.marginRight = px(last.right + gap / 2);
        last = { el: c, right: right };
      }
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
      case "column": e = renderContainer(item, ctx, false, path); break;
      case "chip": e = renderChip(item, ctx, path); break;
      case "diagram": e = renderDiagram(item, ctx); break;
      case "led": e = renderMessage(item, ctx); break;
      // A panel inside a row or column fills it (only the sign's own panels reach its edges).
      case "panel": e = renderPanel(item, ctx, path); e.style.alignSelf = "stretch"; break;
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

  // Colors of a chip, panel or tab: its scheme (or custom colors), else the sign's legend and background swapped.
  function boxColors(item, ctx) {
    var s = item.scheme ? scheme(item.scheme) : { bg: ctx.fg, fg: ctx.bg };
    return { bg: item.background || s.bg, fg: item.foreground || s.fg };
  }

  // A small colored box with its own parts inside (a route patch, a TOLL or speed plate): it sets the background
  // and the legend color of everything in it. "span": "full" makes it as wide as its column.
  function renderChip(item, ctx, path) {
    var c = boxColors(item, ctx);
    var h = spacingHeight(item) || DEFAULTS.textHeight;
    var pad = item.padding != null ? item.padding : h * 0.3;
    var inner = renderContainer({ type: item.vertical ? "column" : "row", items: item.items || [], align: item.align }, { font: ctx.font, fg: c.fg, bg: c.bg, led: ctx.led }, !item.vertical, path);
    var e = el("div", "sb-chip", {
      display: "flex", flexDirection: "row", backgroundColor: c.bg, padding: px(pad * 0.7) + " " + px(pad),
      borderRadius: px(item.radius != null ? item.radius : h * 0.15),
    });
    if (item.border) e.style.border = px(item.borderWidth || DEFAULTS.border) + " solid " + (item.border === true ? c.fg : item.border);
    if (item.span === "full") {
      e.style.alignSelf = "stretch";
      e.style.justifyContent = { start: "flex-start", end: "flex-end" }[item.align] || "center";
    }
    e.appendChild(inner);
    return e;
  }

  // A band across the sign with its own parts in a row (an EXIT ONLY panel). Empty, it still shows as a strip so
  // parts can be dropped into it. <corners>: which of the sign's rounded corners it sits in ("top", "bottom").
  function renderPanel(item, ctx, path, corners, innerRadius) {
    var c = boxColors(item, ctx);
    var h = heightOf(item) || DEFAULTS.textHeight;
    var pad = item.padding != null ? item.padding : h * 0.35;
    var e = el("div", "sb-band", {
      backgroundColor: c.bg, padding: px(pad) + " " + px(pad * 2),
      display: "flex", flexDirection: "row", justifyContent: { start: "flex-start", end: "flex-end" }[item.align] || "center",
    });
    if (!(item.items || []).length) e.style.minHeight = px(DEFAULTS.textHeight * 0.9);
    if (corners && corners.top) {
      e.style.borderTopLeftRadius = px(corners.topLeft === false ? 0 : innerRadius);
      e.style.borderTopRightRadius = px(corners.topRight === false ? 0 : innerRadius);
    }
    if (corners && corners.bottom) {
      e.style.borderBottomLeftRadius = px(innerRadius);
      e.style.borderBottomRightRadius = px(innerRadius);
    }
    var gap = item.gap != null ? item.gap : (spacingHeight(item) || h) * 0.75;
    e.appendChild(renderContainer({ type: "row", items: item.items || [], gap: gap }, { font: ctx.font, fg: c.fg, bg: c.bg, led: ctx.led }, true, path));
    return e;
  }

  // A tab on top of the sign (an exit number plaque): the sign's colors and border, open at the bottom.
  function renderTab(item, ctx, path, s, border, radius) {
    var c = item.scheme ? scheme(item.scheme) : s;
    var h = spacingHeight(item) || 10;
    var box = el("div", "sb-tab", {
      backgroundColor: c.bg, padding: px(border) + " " + px(border) + " 0px " + px(border),
      borderTopLeftRadius: px(radius), borderTopRightRadius: px(radius),
    });
    var inner = el("div", "sb-tab-inner", {
      borderStyle: "solid", borderColor: c.fg,
      borderTopWidth: px(border), borderLeftWidth: px(border), borderRightWidth: px(border), borderBottomWidth: "0px",
      borderTopLeftRadius: px(radius - border), borderTopRightRadius: px(radius - border),
      padding: px(h * 0.45) + " " + px(h * 0.7) + " " + px(h * 0.45) + " " + px(h * 0.7),
    });
    var font = (T.def.exitTab && T.def.exitTab.font) || ctx.font;
    inner.appendChild(renderContainer({ type: "row", items: item.items || [], align: "center" }, { font: font, fg: c.fg, bg: c.bg, led: ctx.led }, true, path));
    box.appendChild(inner);
    return tag(box, path);
  }

  // Minimum sign width for "at least N lanes", at the default 70% sign size less a little clearance: highway lanes
  // (the theme's lane width, 4 m) or the narrower lanes of local roads (3 m).
  function laneWidth(kind) {
    var lane = kind === "local" ? 3 / 0.0254 : (T.def.laneWidth || 4 / 0.0254);
    return (lane / 0.7) * 0.92;
  }

  function renderSign(doc) {
    useTheme(doc);
    // Custom colors ("background" / "foreground" as CSS colors) override the scheme.
    var base = scheme(doc.scheme);
    var s = { bg: doc.background || base.bg, fg: doc.foreground || base.fg };
    // A message board: a black LED panel in a dark frame; its legends are lit pixel lettering.
    var led = doc.board === "led";
    if (led) s = { bg: "#0b0b0b", fg: doc.foreground || LED_COLOR };
    var frame = led ? "#3c3f44" : s.fg;
    var border = doc.border != null ? doc.border : DEFAULTS.border;
    // Message boards are square boxes.
    var radius = led ? 0 : doc.cornerRadius != null ? doc.cornerRadius : DEFAULTS.cornerRadius;
    var items = doc.items || [];
    var legend = spacingHeight({ type: "column", items: items.filter(inBody) }) || DEFAULTS.textHeight;
    var margin = doc.margin != null ? doc.margin : legend * DEFAULTS.margin;
    var ctx = { font: doc.font || DEFAULTS.font, fg: s.fg, bg: s.bg, led: led };
    var minWidth = doc.minLanes ? doc.minLanes * laneWidth(doc.laneKind) : doc.minWidth;

    var sign = el("div", "sb-sign");

    // Tabs, grouped by side: left, center, right.
    var sides = { left: [], center: [], right: [] };
    items.forEach(function (item, i) {
      if (isTab(item)) sides[{ left: "left", start: "left", center: "center" }[item.align] || "right"].push(renderTab(item, ctx, String(i), s, border, radius));
    });
    if (sides.left.length + sides.center.length + sides.right.length > 0) {
      var tabRow = el("div", "sb-tab-row", { justifyContent: "flex-start" });
      var spacer = function () { return el("div", null, { flexGrow: "1" }); };
      sides.left.forEach(function (t) { tabRow.appendChild(t); });
      tabRow.appendChild(spacer());
      sides.center.forEach(function (t) { tabRow.appendChild(t); });
      tabRow.appendChild(spacer());
      sides.right.forEach(function (t) { tabRow.appendChild(t); });
      sign.appendChild(tabRow);
    }

    var panel = el("div", "sb-panel", {
      backgroundColor: led ? frame : s.bg, padding: px(border),
      borderRadius: px(radius),
    });
    // The corner under a tab at the sign's edge is square.
    if (sides.right.length) panel.style.borderTopRightRadius = "0px";
    if (sides.left.length) panel.style.borderTopLeftRadius = "0px";
    if (doc.width) panel.style.width = px(doc.width - border * 2);
    if (minWidth) panel.style.minWidth = px(minWidth - border * 2);

    var innerRadius = Math.max(radius - border, 0);
    var inner = el("div", "sb-panel-inner", {
      borderStyle: "solid", borderColor: frame, borderWidth: px(border), borderRadius: px(innerRadius),
    });
    if (sides.right.length) inner.style.borderTopRightRadius = "0px";
    if (sides.left.length) inner.style.borderTopLeftRadius = "0px";
    if (led) inner.style.backgroundColor = s.bg;

    // The body runs in stretches between panels; each stretch has the sign's margin around it.
    var drawn = [];
    items.forEach(function (item, i) { if (!isTab(item)) drawn.push(i); });
    var stretch = [];
    var first = true;
    var flush = function () {
      if (!stretch.length) return;
      var content = renderContainer({ type: "column", items: items, align: doc.align || "start" }, ctx, false, "", stretch);
      content.style.padding = px(margin);
      if (doc.height && first) content.style.minHeight = px(doc.height - border * 4 - margin * 2);
      inner.appendChild(content);
      stretch = [];
      first = false;
    };
    drawn.forEach(function (i, k) {
      if (!isPanel(items[i])) { stretch.push(i); return; }
      flush();
      var corners = { top: k === 0, bottom: k === drawn.length - 1, topLeft: !sides.left.length, topRight: !sides.right.length };
      // The panel sits inside the legend border, so its corners follow that border's inner edge.
      inner.appendChild(tag(renderPanel(items[i], ctx, String(i), corners, Math.max(innerRadius - border, 0)), String(i)));
      first = false;
    });
    flush();
    panel.appendChild(inner);
    sign.appendChild(panel);
    if (glowOnly) sign.style.visibility = "hidden";
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

  // <glow>: draw only what is lit at night (LED lettering), laid out exactly like the sign itself.
  window.hsRender = function (doc, pixelsPerInch, glow) {
    ppi = pixelsPerInch || 3;
    var root = document.getElementById("root");
    while (root.firstChild) root.removeChild(root.firstChild);
    glowOnly = !!glow;
    try {
      root.appendChild(renderSign(doc || {}));
      window.hsLastError = null;
    } catch (e) {
      window.hsLastError = String(e && e.stack || e);
      var err = el("div", "sb-error");
      err.textContent = "Render error: " + window.hsLastError;
      root.appendChild(err);
    } finally {
      glowOnly = false;
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

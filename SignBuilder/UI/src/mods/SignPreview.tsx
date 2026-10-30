import { useEffect, useRef, useState } from "react";
import { Design } from "./designModel";

// The same renderer that bakes in-world textures, loaded into the game's UI page so the preview is
// pixel-identical to the sign that gets placed. Served from the mod folder by SignFaceBaker's host.
const RENDERER_BASE = "coui://signbuilder/Renderer/";

declare global {
  interface Window {
    SignRenderer?: {
      render: (doc: Design, ppi: number, options?: { annotate?: boolean }) => HTMLElement;
      registerTheme: (def: any, base: string) => void;
    };
  }
}

let loading: Promise<void> | null = null;
let themes: { def: any; base: string }[] = [];

/** Makes the themes (fonts, colors, plates) known to the renderer; previews re-render with them. */
export function registerThemes(list: { def: any; base: string }[]) {
  themes = list;
  if (window.SignRenderer) themes.forEach((t) => window.SignRenderer!.registerTheme(t.def, t.base));
}

function loadRenderer(): Promise<void> {
  if (window.SignRenderer) return Promise.resolve();
  if (loading) return loading;
  loading = new Promise((resolve) => {
    const css = document.createElement("link");
    css.rel = "stylesheet";
    css.href = RENDERER_BASE + "renderer.css";
    document.head.appendChild(css);
    const script = document.createElement("script");
    script.src = RENDERER_BASE + "renderer.js";
    script.onload = () => {
      themes.forEach((t) => window.SignRenderer!.registerTheme(t.def, t.base));
      resolve();
    };
    document.head.appendChild(script);
  });
  return loading;
}

// Thumbnails are drawn a couple per frame instead of all at once, so opening the designer (a page of template
// thumbnails) does not stall the game for a frame.
const pending: (() => void)[] = [];
let pumping = false;
function pump() {
  for (let i = 0; i < 2 && pending.length > 0; i++) pending.shift()!();
  if (pending.length > 0) requestAnimationFrame(pump);
  else pumping = false;
}
function later(draw: () => void) {
  pending.push(draw);
  if (!pumping) {
    pumping = true;
    requestAnimationFrame(pump);
  }
}

function pathOf(target: EventTarget | null, stop: HTMLElement): string | null {
  let node = target as HTMLElement | null;
  while (node && node !== stop) {
    const path = node.getAttribute && node.getAttribute("data-sb-path");
    if (path != null) return path;
    node = node.parentElement;
  }
  return null;
}

/**
 * Renders a design with the in-world renderer, scaled to fit its box. Interactive previews tag parts so a click
 * selects them; thumbnails (interactive = false) just draw.
 */
export const SignPreview = (props: {
  design: Design; selected?: string | null; onSelect?: (path: string | null) => void; maxWidth: number; maxHeight: number;
  interactive?: boolean; padding?: number;
}) => {
  const interactive = props.interactive ?? true;
  const padding = props.padding ?? 16;
  const host = useRef<HTMLDivElement>(null);
  const outer = useRef<HTMLDivElement>(null);
  const [ready, setReady] = useState(!!window.SignRenderer);
  const [fit, setFit] = useState({ scale: 1, x: 0, y: 0, shown: false });
  // The sign currently drawn (bumped on each new drawing, so the selection outline is placed again).
  const sign = useRef<HTMLElement | null>(null);
  const [drawn, setDrawn] = useState(0);

  useEffect(() => {
    loadRenderer().then(() => setReady(true));
  }, []);

  useEffect(() => {
    const el = host.current;
    if (!ready || !el || !window.SignRenderer) return;
    let canceled = false;
    const draw = () => {
    if (canceled || !window.SignRenderer) return;
    while (el.firstChild) el.removeChild(el.firstChild);
    const drawing = window.SignRenderer.render(props.design, 2, { annotate: interactive });
    // Absolutely positioned inside an oversized host so the sign lays out at its natural width; as a
    // flex child of the preview box it would be squeezed to the box width and its legend would overflow.
    drawing.style.position = "absolute";
    drawing.style.left = "0px";
    drawing.style.top = "0px";
    el.appendChild(drawing);
    sign.current = drawing;
    if (interactive) setDrawn((n) => n + 1);
    // Fit the sign into the preview box. Layout is not ready on the first frame (offsetWidth 0) and
    // grows when the sign fonts and shield images finish loading, so keep measuring for a while.
    let frames = 0;
    let last = "";
    let key0 = "";
    let steady = 0;
    const measure = () => {
      if (canceled) return;
      const box = outer.current;
      const w = drawing.offsetWidth;
      const h = drawing.offsetHeight;
      const bw = box?.clientWidth || props.maxWidth;
      const bh = box?.clientHeight || props.maxHeight;
      if (w > 1 && h > 1 && bw > 1 && bh > 1) {
        const scale = Math.min(1, (bw - padding) / w, (bh - padding) / h);
        const next = { scale, x: (bw - w * scale) / 2, y: (bh - h * scale) / 2, shown: true };
        const key = `${next.scale.toFixed(4)} ${next.x.toFixed(1)} ${next.y.toFixed(1)}`;
        if (key !== last) {
          last = key;
          setFit(next);
        }
      }
      // Keep measuring while the size still changes (fonts and plate images load over a few frames), then stop:
      // every measurement forces a layout of the whole game UI.
      if (key0 === last && w > 1) steady++;
      else steady = 0;
      key0 = last;
      if (steady < 15 && frames++ < 240) requestAnimationFrame(measure);
    };
    requestAnimationFrame(measure);
    };
    if (interactive) draw();
    else later(draw);
    return () => { canceled = true; };
  }, [ready, props.design, props.maxWidth, props.maxHeight]);

  // The selected part's yellow outline, around its margins too. Selecting a part only moves this outline: the sign
  // is not drawn again.
  useEffect(() => {
    const drawing = sign.current;
    if (!interactive || !drawing) return;
    let box = drawing.querySelector(".sb-selection") as HTMLDivElement | null;
    const hit = props.selected != null ? (drawing.querySelector(`[data-sb-path="${props.selected}"]`) as HTMLElement | null) : null;
    if (!hit) {
      if (box) box.style.display = "none";
      return;
    }
    if (!box) {
      box = document.createElement("div");
      box.className = "sb-selection";
      box.style.position = "absolute";
      box.style.pointerEvents = "none";
      box.style.boxShadow = "0px 0px 0px 3px #ffd400";
      drawing.appendChild(box);
    }
    const outline = box;
    const place = () => {
      // Positions in the sign's own (unscaled) pixels: the host is scaled to fit the preview box.
      const s = drawing.getBoundingClientRect();
      const r = hit.getBoundingClientRect();
      const k = drawing.offsetWidth > 0 && s.width > 0 ? drawing.offsetWidth / s.width : 1;
      const m = (hit as any).sbMargin ?? { top: 0, right: 0, bottom: 0, left: 0 };
      outline.style.display = "block";
      outline.style.left = (r.left - s.left) * k - m.left + "px";
      outline.style.top = (r.top - s.top) * k - m.top + "px";
      outline.style.width = Math.max(1, r.width * k + m.left + m.right) + "px";
      outline.style.height = Math.max(1, r.height * k + m.top + m.bottom) + "px";
    };
    place();
    const frame = requestAnimationFrame(place);
    return () => cancelAnimationFrame(frame);
  }, [props.selected, drawn, fit]);

  return (
    <div
      ref={outer}
      style={{ position: "relative", width: "100%", height: "100%", overflow: "hidden" }}
      onClick={interactive && props.onSelect ? (e) => props.onSelect!(pathOf(e.target, e.currentTarget as HTMLElement)) : undefined}
    >
      <div ref={host} style={{
        position: "absolute", left: "0px", top: "0px", width: "4000px", height: "4000px",
        transform: `translate(${fit.x}px, ${fit.y}px) scale(${fit.scale})`, transformOrigin: "0px 0px",
        visibility: fit.shown ? "visible" : "hidden",
      }} />
    </div>
  );
};

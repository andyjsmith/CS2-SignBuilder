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
    const sign = window.SignRenderer.render(props.design, 2, { annotate: interactive });
    // Absolutely positioned inside an oversized host so the sign lays out at its natural width; as a
    // flex child of the preview box it would be squeezed to the box width and its legend would overflow.
    sign.style.position = "absolute";
    sign.style.left = "0px";
    sign.style.top = "0px";
    el.appendChild(sign);
    // The selected part gets a yellow outline, and a dashed one around its margins when it has any.
    const hit = props.selected != null ? (el.querySelector(`[data-sb-path="${props.selected}"]`) as HTMLElement | null) : null;
    let marginBox: HTMLDivElement | null = null;
    if (hit) {
      hit.style.boxShadow = "0px 0px 0px 3px #ffd400";
      marginBox = document.createElement("div");
      marginBox.style.position = "absolute";
      marginBox.style.pointerEvents = "none";
      marginBox.style.border = "2px dashed #ffd400";
      marginBox.style.display = "none";
      sign.appendChild(marginBox);
    }
    const placeMarginBox = () => {
      if (!hit || !marginBox) return;
      const cs = getComputedStyle(hit);
      const m = [cs.marginTop, cs.marginRight, cs.marginBottom, cs.marginLeft].map((v) => parseFloat(v) || 0);
      if (!m.some((v) => Math.abs(v) > 0.5)) { marginBox.style.display = "none"; return; }
      let x = 0;
      let y = 0;
      let node: HTMLElement | null = hit;
      while (node && node !== sign) { x += node.offsetLeft; y += node.offsetTop; node = node.offsetParent as HTMLElement | null; }
      marginBox.style.display = "block";
      marginBox.style.left = x - m[3] + "px";
      marginBox.style.top = y - m[0] + "px";
      marginBox.style.width = hit.offsetWidth + m[1] + m[3] + "px";
      marginBox.style.height = hit.offsetHeight + m[0] + m[2] + "px";
    };
    // Fit the sign into the preview box. Layout is not ready on the first frame (offsetWidth 0) and
    // grows when the sign fonts and shield images finish loading, so keep measuring for a while.
    let frames = 0;
    let last = "";
    let key0 = "";
    let steady = 0;
    const measure = () => {
      if (canceled) return;
      const box = outer.current;
      const w = sign.offsetWidth;
      const h = sign.offsetHeight;
      const bw = box?.clientWidth || props.maxWidth;
      const bh = box?.clientHeight || props.maxHeight;
      placeMarginBox();
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
  }, [ready, props.design, props.selected, props.maxWidth, props.maxHeight]);

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

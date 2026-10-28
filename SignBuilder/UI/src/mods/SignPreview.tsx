import { useEffect, useRef, useState } from "react";
import { Design } from "./designModel";

// The same renderer that bakes in-world textures, loaded into the game's UI page so the preview is
// pixel-identical to the sign that gets placed. Served from the mod folder by SignFaceBaker's host.
const RENDERER_BASE = "coui://signbuilder/Renderer/";

declare global {
  interface Window {
    SignRenderer?: { render: (doc: Design, ppi: number, options?: { annotate?: boolean }) => HTMLElement };
  }
}

let loading: Promise<void> | null = null;

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
    script.onload = () => resolve();
    document.head.appendChild(script);
  });
  return loading;
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

export const SignPreview = (props: { design: Design; selected: string | null; onSelect: (path: string | null) => void; maxWidth: number; maxHeight: number }) => {
  const host = useRef<HTMLDivElement>(null);
  const [ready, setReady] = useState(!!window.SignRenderer);
  const [fit, setFit] = useState({ scale: 1, x: 0, y: 0 });

  useEffect(() => {
    loadRenderer().then(() => setReady(true));
  }, []);

  useEffect(() => {
    const el = host.current;
    if (!ready || !el || !window.SignRenderer) return;
    while (el.firstChild) el.removeChild(el.firstChild);
    const sign = window.SignRenderer.render(props.design, 2, { annotate: true });
    // Absolutely positioned inside an oversized host so the sign lays out at its natural width; as a
    // flex child of the preview box it would be squeezed to the box width and its legend would overflow.
    sign.style.position = "absolute";
    sign.style.left = "0px";
    sign.style.top = "0px";
    el.appendChild(sign);
    if (props.selected != null) {
      const hit = el.querySelector(`[data-sb-path="${props.selected}"]`) as HTMLElement | null;
      if (hit) hit.style.boxShadow = "0px 0px 0px 3px #ffd400";
    }
    // Fit the sign into the preview area once it has been laid out.
    requestAnimationFrame(() => {
      const w = sign.offsetWidth || 1;
      const h = sign.offsetHeight || 1;
      const scale = Math.min(1, (props.maxWidth - 16) / w, (props.maxHeight - 16) / h);
      setFit({ scale, x: (props.maxWidth - w * scale) / 2, y: (props.maxHeight - h * scale) / 2 });
    });
  }, [ready, props.design, props.selected, props.maxWidth, props.maxHeight]);

  return (
    <div
      style={{ position: "relative", width: props.maxWidth + "px", height: props.maxHeight + "px", overflow: "hidden" }}
      onClick={(e) => props.onSelect(pathOf(e.target, e.currentTarget as HTMLElement))}
    >
      <div ref={host} style={{
        position: "absolute", left: "0px", top: "0px", width: "4000px", height: "4000px",
        transform: `translate(${fit.x}px, ${fit.y}px) scale(${fit.scale})`, transformOrigin: "0px 0px",
      }} />
    </div>
  );
};

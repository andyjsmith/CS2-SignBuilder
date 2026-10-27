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
  const [scale, setScale] = useState(1);

  useEffect(() => {
    loadRenderer().then(() => setReady(true));
  }, []);

  useEffect(() => {
    const el = host.current;
    if (!ready || !el || !window.SignRenderer) return;
    while (el.firstChild) el.removeChild(el.firstChild);
    const sign = window.SignRenderer.render(props.design, 2, { annotate: true });
    el.appendChild(sign);
    if (props.selected != null) {
      const hit = el.querySelector(`[data-sb-path="${props.selected}"]`) as HTMLElement | null;
      if (hit) hit.style.boxShadow = "0px 0px 0px 3px #ffd400";
    }
    // Fit the sign into the preview area once it has been laid out.
    requestAnimationFrame(() => {
      const w = sign.offsetWidth || 1;
      const h = sign.offsetHeight || 1;
      setScale(Math.min(1, props.maxWidth / w, props.maxHeight / h));
    });
  }, [ready, props.design, props.selected, props.maxWidth, props.maxHeight]);

  return (
    <div
      style={{ width: props.maxWidth + "px", height: props.maxHeight + "px", display: "flex", alignItems: "center", justifyContent: "center", overflow: "hidden" }}
      onClick={(e) => props.onSelect(pathOf(e.target, e.currentTarget as HTMLElement))}
    >
      <div ref={host} style={{ transform: `scale(${scale})`, transformOrigin: "50% 50%" }} />
    </div>
  );
};

// Small inline icons for the editor's option buttons (Gameface renders inline SVG; no icon font needed).
import { useRenderer } from "./SignPreview";
import { LibrarySymbol } from "./themeModel";

// The editor's own arrow buttons (raise, lower, nudge): one up-pointing shape, turned.
const BUTTON_ARROW = "M96.2 65.1 L53.5 1.9 C52.7 0.7 51.5 0 50 0 C48.5 0 47.3 0.7 46.5 1.9 L3.8 65.1 " +
  "C3.4 65.7 3.2 66.5 3.2 67.4 C3.2 69.7 5 71.6 7.4 71.6 C7.7 71.6 8 71.5 8.4 71.5 L38.7 64.2 L35 150 " +
  "L65 150 L61.3 64.2 L91.6 71.5 C92 71.5 92.3 71.6 92.6 71.6 C95 71.6 96.8 69.7 96.8 67.4 C96.8 66.5 96.6 65.7 96.2 65.1 Z";
const BUTTON_TURN: Record<string, number> = {
  up: 0, "up-right": 45, right: 90, "down-right": 135, down: 180, "down-left": -135, left: -90, "up-left": -45,
};

/** An arrow for the editor's own buttons (not a theme's arrow). */
export const ArrowIcon = (props: { direction: string; size?: number; color?: string }) => {
  const size = props.size ?? 18;
  return (
    <svg width={size + "rem"} height={size + "rem"} viewBox="-25 0 150 150">
      <path d={BUTTON_ARROW} fill={props.color ?? "#ffffff"} transform={`rotate(${BUTTON_TURN[props.direction] ?? 0} 50 75)`} />
    </svg>
  );
};

/**
 * One of a theme's arrows, drawn by the sign renderer from the theme's symbol libraries (so the button shows exactly
 * what the sign gets). <box>: the view box shared by the style's buttons; the arrow's own when absent.
 */
export const ThemeArrowIcon = (props: {
  theme: string; style?: string; direction: string; library?: string; box?: number[]; size?: number; color?: string;
}) => {
  const renderer = useRenderer();
  const size = props.size ?? 18;
  if (!renderer) return <svg width={size + "rem"} height={size + "rem"} />;
  const g = renderer.arrowGeometry(props.theme, props.style, props.direction, undefined, props.library);
  const v = g.viewBox, s = Math.max(v[2], v[3]);
  const box = props.box ?? [v[0] + v[2] / 2 - s / 2, v[1] + v[3] / 2 - s / 2, s, s];
  return (
    <svg width={size + "rem"} height={size + "rem"} viewBox={box.join(" ")}>
      <path d={g.d} fill={props.color ?? "#ffffff"} transform={g.transform ?? undefined} />
    </svg>
  );
};

/** A library symbol in a square <size> (legend-colored parts in <color>). */
export const SymbolIcon = (props: { symbol?: LibrarySymbol; size?: number; color?: string }) => {
  const size = props.size ?? 28;
  const sym = props.symbol;
  if (!sym) return <svg width={size + "rem"} height={size + "rem"} />;
  const v = sym.viewBox, s = Math.max(v[2], v[3]);
  return (
    <svg width={size + "rem"} height={size + "rem"} viewBox={[v[0] + v[2] / 2 - s / 2, v[1] + v[3] / 2 - s / 2, s, s].join(" ")}>
      {sym.parts.map((p, i) => (
        <path key={i} d={p.d} fill={p.fill === "currentColor" ? props.color ?? "#ffffff" : p.fill} transform={p.transform} fillRule={p.rule as any} />
      ))}
    </svg>
  );
};

/** Built-in (read-only) template badge. */
export const LockIcon = () => (
  <svg width="10rem" height="12rem" viewBox="0 0 10 12">
    <path d="M2.5 5 V3.5 a2.5 2.5 0 0 1 5 0 V5" stroke="#ffffff" strokeWidth="1.4" fill="none" />
    <rect x="1" y="5" width="8" height="6.5" rx="1" fill="#ffffff" />
  </svg>
);

/** The player's own (editable) template badge. */
export const PencilIcon = () => (
  <svg width="12rem" height="12rem" viewBox="0 0 12 12">
    <path d="M1 11 L1.6 8.2 L8.4 1.4 L10.6 3.6 L3.8 10.4 Z" fill="#ffd400" />
  </svg>
);

/** Two overlapping sheets, for duplicate buttons. */
export const DuplicateIcon = (props: { size?: number }) => (
  <svg width={(props.size ?? 12) + "rem"} height={(props.size ?? 12) + "rem"} viewBox="0 0 12 12">
    <rect x="0.75" y="0.75" width="7" height="7" rx="1" fill="none" stroke="#ffffff" strokeWidth="1.5" />
    <rect x="4" y="4" width="7.25" height="7.25" rx="1" fill="#ffffff" />
  </svg>
);

/** A small trash can, for delete buttons. */
export const TrashIcon = (props: { size?: number }) => (
  <svg width={(props.size ?? 12) + "rem"} height={(props.size ?? 12) + "rem"} viewBox="0 0 12 12">
    <rect x="1" y="2" width="10" height="1.5" fill="#ffffff" />
    <rect x="4.5" y="0.5" width="3" height="1.5" fill="#ffffff" />
    <path d="M2.5 4 H9.5 L9 11.5 H3 Z" fill="#ffffff" />
  </svg>
);

/** Text-alignment icon: four lines aligned left, center or right. */
export const AlignIcon = (props: { align: string }) => {
  const widths = [16, 10, 14, 8];
  const x = (w: number) => (props.align === "end" ? 18 - w : props.align === "center" ? (18 - w) / 2 : 0) + 1;
  return (
    <svg width="20rem" height="16rem" viewBox="0 0 20 16">
      {widths.map((w, i) => <rect key={i} x={x(w)} y={1 + i * 4} width={w} height="2" fill="#ffffff" />)}
    </svg>
  );
};

/** A sign outline with an exit tab on the given side (or none). */
export const ExitTabIcon = (props: { side: string }) => (
  <svg width="28rem" height="18rem" viewBox="0 0 28 18">
    {props.side !== "none" && <rect x={props.side === "left" ? 1 : 15} y="1" width="12" height="6" fill="#2e9d6a" stroke="#ffffff" strokeWidth="1" />}
    <rect x="1" y="6" width="26" height="11" fill="#2e9d6a" stroke="#ffffff" strokeWidth="1" />
  </svg>
);

/** EXIT ONLY panel icon: a sign with a yellow bar and its arrows. */
export const ExitOnlyIcon = (props: { arrows: number }) => (
  <svg width="28rem" height="18rem" viewBox="0 0 28 18">
    <rect x="1" y="1" width="26" height="16" fill="#2e9d6a" stroke="#ffffff" strokeWidth="1" />
    {props.arrows > 0 && <rect x="1.5" y="11" width="25" height="5.5" fill="#ffcc00" />}
    {props.arrows > 0 && <path d="M12 12 h2 v2 h1.5 l-2.5 2.2 l-2.5 -2.2 h1.5 z" fill="#000000" />}
    {props.arrows > 1 && <path d="M4 12 h2 v2 h1.5 l-2.5 2.2 l-2.5 -2.2 h1.5 z M21 12 h2 v2 h1.5 l-2.5 2.2 l-2.5 -2.2 h1.5 z" fill="#000000" />}
  </svg>
);

/** A theme's route plate as a small icon: its artwork, or a colored plate with its prefix. */
export const ThemeShieldIcon = (props: { theme?: { base: string; def: any }; network: string }) => {
  const s = props.theme?.def?.shields?.[props.network];
  if (!s) return <span>{props.network}</span>;
  if (s.img) return <img src={props.theme!.base + s.img} style={{ width: "22rem", height: "22rem" }} />;
  if (s.shape === "ellipse") return <div style={{ width: "24rem", height: "18rem", borderRadius: "50%", backgroundColor: s.bg ?? "#fff", border: `2rem solid ${s.border ?? "#000"}` }} />;
  const label = ((s.prefix ?? "") + (s.default ?? "")).trim() || props.network;
  return (
    <div style={{ padding: "1rem 5rem", fontSize: "12rem", fontWeight: "bold", color: s.fg ?? "#000", backgroundColor: s.shape === "plain" ? "rgba(0,0,0,0.5)" : s.bg ?? "#fff",
      border: s.border ? `2rem solid ${s.border}` : undefined, borderRadius: "3rem", whiteSpace: "nowrap" }}>{label}</div>
  );
};

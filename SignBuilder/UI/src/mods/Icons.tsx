// Small inline icons for the editor's option buttons (Gameface renders inline SVG; no icon font needed).

const ARROW_ROTATION: Record<string, number> = {
  down: 0, up: 180, left: 90, right: -90, "down-left": 45, "down-right": -45, "up-left": 135, "up-right": -135,
};

// Lane-use arrows, as in the renderer: pointing up, bending left ("-right" mirrors them).
const CURVED: Record<string, string> = {
  turn: "M54 150 V82 A24 24 0 0 0 30 58 V78 L0 46 L30 14 V34 A48 48 0 0 1 78 82 V150 Z",
  split: "M54 150 V128 A24 24 0 0 0 30 104 V124 L0 92 L30 60 V80 A48 48 0 0 1 54 86.4 V34 H34 L66 0 L98 34 H78 V150 Z",
};

/** The same arrow shapes the sign renderer draws, in a square box. */
export const ArrowIcon = (props: { direction: string; size?: number; color?: string }) => {
  const size = props.size ?? 18;
  const curved = /^(turn|split)-(left|right)$/.exec(props.direction);
  return (
    <svg width={size + "rem"} height={size + "rem"} viewBox="-25 0 150 150">
      {curved ? (
        <path d={CURVED[curved[1]]} fill={props.color ?? "#ffffff"} transform={curved[2] === "right" ? "translate(100 0) scale(-1 1)" : undefined} />
      ) : (
        <path d="M32 0 H68 V88 H100 L50 150 L0 88 H32 Z" fill={props.color ?? "#ffffff"}
          transform={`rotate(${ARROW_ROTATION[props.direction] ?? 0} 50 75)`} />
      )}
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

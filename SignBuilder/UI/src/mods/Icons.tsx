// Small inline icons for the editor's option buttons (Gameface renders inline SVG; no icon font needed).

const RENDERER_BASE = "coui://signbuilder/Renderer/";

const ARROW_ROTATION: Record<string, number> = {
  down: 0, up: 180, left: 90, right: -90, "down-left": 45, "down-right": -45, "up-left": 135, "up-right": -135,
};

/** The same arrow shape the sign renderer draws. */
export const ArrowIcon = (props: { direction: string; size?: number; color?: string }) => {
  const size = props.size ?? 18;
  return (
    <svg width={size * 0.67 + "rem"} height={size + "rem"} viewBox="0 0 100 150">
      <path d="M32 0 H68 V88 H100 L50 150 L0 88 H32 Z" fill={props.color ?? "#ffffff"}
        transform={`rotate(${ARROW_ROTATION[props.direction] ?? 0} 50 75)`} />
    </svg>
  );
};

/** Text-alignment icon: four lines aligned left, centre or right. */
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

/** Route shield thumbnails, matching the renderer's artwork. */
export const ShieldIcon = (props: { network: string }) => {
  if (props.network === "I") return <img src={RENDERER_BASE + "shields/interstate.svg"} style={{ width: "22rem", height: "22rem" }} />;
  if (props.network === "US") return <img src={RENDERER_BASE + "shields/us_route.svg"} style={{ width: "22rem", height: "22rem" }} />;
  return <div style={{ width: "24rem", height: "18rem", borderRadius: "50%", backgroundColor: "#ffffff", border: "2rem solid #000000" }} />;
};

/** Sign colour swatch. */
export const SCHEME_COLOURS: Record<string, string> = {
  green: "#006b45", blue: "#1f4ea1", brown: "#6b3f1d", yellow: "#ffcc00", white: "#ffffff", black: "#000000",
};

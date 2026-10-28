import { getModule } from "cs2/modding";
import { useEffect, useRef } from "react";
import { SCHEME_COLORS } from "./Icons";
import styles from "./SignEditor.module.scss";

export type Option = { value: string; label?: string; icon?: any; title?: string; style?: any };

/** A row of option buttons showing an icon, a label, or both. */
export const Choice = (props: { options: (string | Option)[]; value: string; onChange: (v: string) => void }) => (
  <div className={styles.choices}>
    {props.options.map((raw) => {
      const o: Option = typeof raw === "string" ? { value: raw, label: raw } : raw;
      return (
        <div key={o.value} title={o.title} className={o.value === props.value ? styles.choiceOn : styles.choice} style={o.style}
          onClick={() => props.onChange(o.value)}>
          {o.icon}
          {o.icon && o.label ? <span style={{ marginLeft: "6rem" }}>{o.label}</span> : o.label}
        </div>
      );
    })}
  </div>
);

export const Field = (props: { label: string; children: any }) => (
  <div className={styles.field}>
    <div className={styles.label}>{props.label}</div>
    {props.children}
  </div>
);

const load = (path: string, name: string): any => {
  try { return getModule(path, name); } catch { return null; }
};
// The game's own inputs take keyboard focus properly (typing does not trigger game hotkeys); plain
// elements are the fallback if a game update moves the modules.
const VanillaTextInput = load("game-ui/common/input/text/text-input.tsx", "TextInput");
const VanillaColorField = load("game-ui/common/input/color-picker/color-field/color-field.tsx", "ColorField");

/**
 * Text input. <focusToken> changing (or autoFocus) puts the caret at the end of the text, so clicking a legend
 * on the preview lets you type straight away.
 */
export const TextBox = (props: { value: string; onChange: (v: string) => void; focusToken?: number; autoFocus?: boolean }) => {
  const wrap = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!props.autoFocus && !props.focusToken) return;
    const timer = setTimeout(() => {
      const input = wrap.current?.querySelector("input, textarea") as HTMLInputElement | null;
      if (!input) return;
      input.focus();
      const end = input.value.length;
      try { input.setSelectionRange(end, end); } catch { /* not supported */ }
    }, 30);
    return () => clearTimeout(timer);
  }, [props.focusToken, props.autoFocus]);
  return (
    <div ref={wrap}>
      {VanillaTextInput ? (
        <VanillaTextInput className={styles.input} type="text" multiline={1} value={props.value}
          onChange={(e: any) => props.onChange((e.target as HTMLInputElement).value)} />
      ) : (
        <input className={styles.input} type="text" value={props.value}
          onChange={(e) => props.onChange((e.target as HTMLInputElement).value)} />
      )}
    </div>
  );
};

export const NumberBox = (props: { value: number; onChange: (v: number) => void; step?: number; unit?: string }) => (
  <div className={styles.stepper}>
    <div className={styles.step} onClick={() => props.onChange(Math.max(1, props.value - (props.step ?? 1)))}>-</div>
    <div className={styles.stepValue}>{`${props.value}${props.unit ?? '"'}`}</div>
    <div className={styles.step} onClick={() => props.onChange(props.value + (props.step ?? 1))}>+</div>
  </div>
);

const hexToColor = (hex: string) => {
  const h = (hex || "#000000").replace("#", "");
  const n = parseInt(h.length === 3 ? h.split("").map((c) => c + c).join("") : h, 16) || 0;
  return { r: ((n >> 16) & 255) / 255, g: ((n >> 8) & 255) / 255, b: (n & 255) / 255, a: 1 };
};
const colorToHex = (c: { r: number; g: number; b: number }) =>
  "#" + [c.r, c.g, c.b].map((v) => Math.round(Math.max(0, Math.min(1, v)) * 255).toString(16).padStart(2, "0")).join("");

/** A color value edited with the game's color field (wheel + hex), or a hex box if it is unavailable. */
export const ColorBox = (props: { value: string; onChange: (hex: string) => void }) =>
  VanillaColorField ? (
    <VanillaColorField value={hexToColor(props.value)} colorWheel hexInput onChange={(c: any) => props.onChange(colorToHex(c))} />
  ) : (
    <TextBox value={props.value} onChange={props.onChange} />
  );

/** Standard sign colors as swatches, plus Custom (any background and legend color). */
export const Swatches = (props: { scheme: string; custom: boolean; onScheme: (s: string) => void; onCustom: () => void }) => (
  <div className={styles.choices}>
    {["green", "blue", "brown", "yellow", "white"].map((s) => (
      <div key={s} title={s} className={!props.custom && s === props.scheme ? styles.swatchOn : styles.swatch}
        style={{ backgroundColor: SCHEME_COLORS[s] }} onClick={() => props.onScheme(s)} />
    ))}
    <div title="Custom colors" className={props.custom ? styles.swatchOn : styles.swatch} onClick={props.onCustom}
      style={{ backgroundImage: "linear-gradient(90deg, #e53935, #fdd835, #43a047, #1e88e5, #8e24aa)" }} />
  </div>
);

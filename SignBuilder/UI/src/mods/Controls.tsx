import { getModule } from "cs2/modding";
import { Dropdown, DropdownToggle } from "cs2/ui";
import { Children, ReactNode, useEffect, useLayoutEffect, useRef, useState } from "react";
import styles from "./SignEditor.module.scss";

export type Option = { value: string; label?: string; icon?: any; title?: string; style?: any };

/** An element's drawn width (Gameface's rectangles have no width of their own). */
const widthOf = (e: Element) => { const r = e.getBoundingClientRect(); return r.right - r.left; };

/** Rows of item counts for items <widths> wide in <width>: as many per row as fit (at least one), at most <max>. */
export const flowRows = (widths: number[], width: number, max?: number): number[] => {
  const rows: number[] = [];
  let used = 0, count = 0;
  for (const w of widths) {
    // Half a pixel of slack: measured widths are fractional.
    if (count > 0 && (used + w > width + 0.5 || (max != null && count >= max))) { rows.push(count); used = 0; count = 0; }
    used += w;
    count++;
  }
  if (count > 0) rows.push(count);
  return rows;
};

/**
 * Items in rows, as many to a row as fit the width it has (and at most <max>): measured, because Gameface does not
 * grow a wrapped flex row's height (a wrapped item would be drawn over whatever follows). It takes the full width of
 * a column, or ("grow") the space left in a row; each item keeps its own width (margins included) and is never
 * squeezed. Measured again whenever it is drawn and when the window changes size.
 */
export const Flow = (props: { children: ReactNode; max?: number; grow?: boolean; align?: "center" | "start"; className?: string }) => {
  const items = Children.toArray(props.children);
  const box = useRef<HTMLDivElement>(null);
  const [rows, setRows] = useState<number[] | null>(null);
  const [, redraw] = useState(0);
  // Each item's last measured width, by its key: Gameface lays out a newly placed element after this effect runs, so
  // an item just moved to another row measures 0 until the next frame.
  const known = useRef(new Map<string, number>());
  const retries = useRef(0);
  useEffect(() => {
    const onResize = () => redraw((n) => n + 1);
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, []);
  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return;
    const width = widthOf(el);
    const cells: Element[] = [];
    for (const row of Array.from(el.children)) cells.push(...Array.from(row.children));
    if (cells.length !== items.length) return;
    let missing = !(width > 0);
    const widths = cells.map((c, i) => {
      const key = String((items[i] as any)?.key ?? i);
      const w = widthOf(c);
      if (w > 0) known.current.set(key, w);
      const k = known.current.get(key);
      if (k == null) missing = true;
      return k ?? 0;
    });
    // Not laid out yet: measure again next frame (for a few frames: an item that never takes any space counts as none).
    if (missing && retries.current < 10) {
      retries.current++;
      const frame = requestAnimationFrame(() => redraw((n) => n + 1));
      return () => cancelAnimationFrame(frame);
    }
    retries.current = 0;
    const next = flowRows(widths, width, props.max);
    if (!rows || next.length !== rows.length || next.some((n, i) => n !== rows[i])) setRows(next);
  });
  // Until measured (and when the items change), everything on one row: measuring it gives each item's own width.
  const counts = rows && rows.reduce((a, b) => a + b, 0) === items.length ? rows : [items.length];
  let start = 0;
  return (
    <div ref={box} className={`${props.grow ? styles.flowGrow : styles.flow} ${props.className ?? ""}`}>
      {counts.map((n, r) => {
        const row = items.slice(start, start + n);
        start += n;
        return (
          <div key={r} className={props.align === "center" ? styles.flowRowCenter : styles.flowRow}>
            {row.map((item, i) => <div key={(item as any)?.key ?? i} className={styles.flowCell}>{item}</div>)}
          </div>
        );
      })}
    </div>
  );
};

/**
 * A row of option buttons showing an icon, a label, or both, wrapped onto more rows where they do not fit (and after
 * <perRow> buttons, for lists that read as a grid, such as arrow directions).
 */
export const Choice = (props: { options: (string | Option)[]; value: string; onChange: (v: string) => void; perRow?: number; grow?: boolean }) => {
  const options: Option[] = props.options.map((raw) => (typeof raw === "string" ? { value: raw, label: raw } : raw));
  return (
    <Flow max={props.perRow} grow={props.grow}>
      {options.map((o) => (
        <div key={o.value} title={o.title} className={o.value === props.value ? styles.choiceOn : styles.choice} style={o.style}
          onClick={() => props.onChange(o.value)}>
          {o.icon}
          {o.icon && o.label ? <span style={{ marginLeft: "6rem" }}>{o.label}</span> : o.label}
        </div>
      ))}
    </Flow>
  );
};

/**
 * A drop-down list (font, sign theme): the game's own, so the list floats over the content and opens upward when
 * there is no room below. A plain in-place list is the fallback if a game update moves the module.
 */
export const Select = (props: { options: Option[]; value: string; onChange: (v: string) => void }) => {
  const [open, setOpen] = useState(false);
  const current = props.options.find((o) => o.value === props.value);
  if (VanillaDropdownItem) {
    return (
      <div className={styles.select}>
        <Dropdown theme={GameDropdownTheme ?? undefined} content={props.options.map((o) => (
          <VanillaDropdownItem key={o.value} value={o.value} selected={o.value === props.value} closeOnSelect
            onChange={(v: string) => props.onChange(v)}>
            <span style={o.style} title={o.title}>{o.label ?? o.value}</span>
          </VanillaDropdownItem>
        ))}>
          <DropdownToggle className={styles.selectVanilla}>{current?.label ?? props.value}</DropdownToggle>
        </Dropdown>
      </div>
    );
  }
  return (
    <div className={styles.select}>
      <div className={styles.selectToggle} onClick={() => setOpen(!open)}>
        <div className={styles.rowLabel}>{current?.label ?? props.value}</div>
        <svg className={styles.selectCaret} width="10rem" height="6rem" viewBox="0 0 10 6">
          <path d={open ? "M0 6 L5 0 L10 6 Z" : "M0 0 L5 6 L10 0 Z"} fill="#ffffff" />
        </svg>
      </div>
      {open && (
        <div className={styles.selectMenu}>
          {props.options.map((o) => (
            <div key={o.value} title={o.title} className={o.value === props.value ? styles.selectItemOn : styles.selectItem}
              onClick={() => { setOpen(false); if (o.value !== props.value) props.onChange(o.value); }}>{o.label ?? o.value}</div>
          ))}
        </div>
      )}
    </div>
  );
};

/** A question over the dialog's content: discard unsaved changes? */
export const Confirm = (props: { message: string; confirm: string; cancel: string; onConfirm: () => void; onCancel: () => void }) => (
  <div className={styles.confirmShade}>
    <div className={styles.confirmBox}>
      <div className={styles.confirmText}>{props.message}</div>
      <div className={styles.inlineRow}>
        <div className={styles.dangerButton} onClick={props.onConfirm}>{props.confirm}</div>
        <div className={styles.chip} style={{ alignSelf: "center" }} onClick={props.onCancel}>{props.cancel}</div>
      </div>
    </div>
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
const VanillaDropdownItem = load("game-ui/common/input/dropdown/items/dropdown-item.tsx", "DropdownItem");
// The in-game drop-down look (dark list, highlighted choice), as the game's own panels use it.
const GameDropdownTheme = load("game-ui/game/themes/game-dropdown.module.scss", "classes");

/**
 * Text input. <focusToken> changing (or autoFocus) puts the caret at the end of the text, so clicking a legend
 * on the preview lets you type straight away.
 */
export const TextBox = (props: { value: string; onChange: (v: string) => void; focusToken?: number; autoFocus?: boolean; placeholder?: string }) => {
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
        <VanillaTextInput className={styles.input} type="text" multiline={1} value={props.value} placeholder={props.placeholder}
          onChange={(e: any) => props.onChange((e.target as HTMLInputElement).value)} />
      ) : (
        <input className={styles.input} type="text" value={props.value} placeholder={props.placeholder}
          onChange={(e) => props.onChange((e.target as HTMLInputElement).value)} />
      )}
    </div>
  );
};

/** A number with - and + buttons; sizes are in the sign's own units (shown without a unit). */
/**
 * A number: type it, or step it with - and + (Shift: 10 steps, Ctrl: a tenth of a step).
 */
export const NumberBox = (props: { value: number; onChange: (v: number) => void; step?: number; unit?: string; min?: number; max?: number; compact?: boolean }) => {
  const [text, setText] = useState<string | null>(null);
  const wrap = useRef<HTMLDivElement>(null);
  const field = () => wrap.current?.querySelector("input") as HTMLInputElement | null;
  const step = props.step ?? 1;
  const clamp = (v: number) => Math.min(props.max ?? Infinity, Math.max(props.min ?? 1, Math.round(v * 100) / 100));
  const factor = (e: { shiftKey?: boolean; ctrlKey?: boolean }) => (e.shiftKey ? 10 : e.ctrlKey ? 0.1 : 1);
  const nudge = (direction: number, e: { shiftKey?: boolean; ctrlKey?: boolean }) => {
    setText(null);
    props.onChange(clamp(props.value + direction * step * factor(e)));
  };
  const commit = () => {
    if (text == null) return;
    const v = parseFloat(text.replace(",", "."));
    setText(null);
    if (!isNaN(v)) props.onChange(clamp(v));
  };
  return (
    <div className={props.compact ? styles.stepperCompact : styles.stepper}>
      <div className={styles.step} title="Shift: x10, Ctrl: x0.1" onClick={(e) => nudge(-1, e)}>-</div>
      <div ref={wrap} className={styles.stepValue}>
        {(() => {
          // The game's own text input keeps typed keys from reaching game hotkeys; a plain input is the fallback.
          const inputProps = {
            className: styles.numberInput, type: "text", value: text ?? `${props.value}`,
            onChange: (e: any) => setText((e.target as HTMLInputElement).value),
            onBlur: commit,
            onKeyDown: (e: any) => { if (e.key === "Enter") { commit(); field()?.blur(); } if (e.key === "Escape") setText(null); },
          };
          return VanillaTextInput ? <VanillaTextInput {...inputProps} /> : <input {...inputProps} />;
        })()}
        {props.unit && <span>{props.unit}</span>}
      </div>
      <div className={styles.step} title="Shift: x10, Ctrl: x0.1" onClick={(e) => nudge(1, e)}>+</div>
    </div>
  );
};

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

/** Color swatches, wrapped onto more rows where they do not fit: any leading buttons, the colors, then "any color" (rainbow). */
export const ColorRows = (props: {
  lead?: any[]; colors: { color: string; title?: string; border?: string; on: boolean; onClick: () => void }[];
  custom: { on: boolean; onClick: () => void; title?: string };
}) => (
  <Flow>
    {props.lead}
    {props.colors.map((c, i) => (
      <div key={`c${i}`} title={c.title ?? c.color} className={c.on ? styles.swatchOn : styles.swatch}
        style={{ backgroundColor: c.color, borderColor: c.on ? undefined : c.border }} onClick={c.onClick} />
    ))}
    <div key="custom" title={props.custom.title ?? "Any color"} className={props.custom.on ? styles.swatchOn : styles.swatch}
      style={{ backgroundImage: "linear-gradient(90deg, #e53935, #fdd835, #43a047, #1e88e5, #8e24aa)" }} onClick={props.custom.onClick} />
  </Flow>
);

/** The theme's sign colors as swatches (named on hover), plus Custom (any background and legend color). */
export const Swatches = (props: {
  schemes: Record<string, { bg: string; fg: string; label?: string }>; scheme: string; custom: boolean; onScheme: (s: string) => void; onCustom: () => void;
}) => (
  <ColorRows
    colors={Object.entries(props.schemes).map(([id, s]) => ({ color: s.bg, title: s.label ?? id, border: s.fg, on: !props.custom && id === props.scheme, onClick: () => props.onScheme(id) }))}
    custom={{ on: props.custom, onClick: props.onCustom, title: "Custom colors" }} />
);

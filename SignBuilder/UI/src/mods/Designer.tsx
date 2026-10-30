import { Scrollable } from "cs2/ui";
import { Dialog } from "./Dialog";
import { openThemesFolder } from "./bindings";
import { ReactNode, useState } from "react";
import { ArrowIcon, AlignIcon, ThemeShieldIcon } from "./Icons";
import { Choice, ColorBox, Field, NumberBox, Option, Select, Swatches, TextBox } from "./Controls";
import { Design, Item, clone, duplicateItem, getItem, insertItem, kindOf, removeItem, updateItem } from "./designModel";
import { PartsList } from "./PartsList";
import { SignPreview } from "./SignPreview";
import { Template, TemplateLibrary } from "./TemplateLibrary";
import { Theme, baseParts, blankFor, convertDesign, themeOf } from "./themeModel";
import styles from "./SignEditor.module.scss";

const ARROWS = ["up-left", "up", "up-right", "left", "right", "down-left", "down", "down-right", "turn-left", "split-left", "split-right", "turn-right"];
const ALIGN: Option[] = ["start", "center", "end"].map((a) => ({ value: a, icon: <AlignIcon align={a} />, title: a }));
// Parts side by side in a row line up by their tops, middles or bottoms.
const ROW_ALIGN: Option[] = [{ value: "start", label: "Top" }, { value: "center", label: "Middle" }, { value: "end", label: "Bottom" }];
const ROW_SPACING: Option[] = [
  { value: "", label: "Packed" },
  { value: "between", label: "Spread out", title: "Fill the width, with the space between the parts" },
  { value: "even", label: "Even", title: "Fill the width, each part centered in an equal share of it (dividers keep their width)" },
];
const LANE_KIND: Option[] = [
  { value: "", label: "Highway", title: "Highway lanes (4 m)" },
  { value: "local", label: "Local", title: "Local road lanes (3 m)" },
];
const PLACE: Option[] = [{ value: "", label: "Sign default" }, ...ALIGN];
const DIAGRAM_SHAPES: Option[] = [
  { value: "exit-left", label: "Exit left" }, { value: "exit-right", label: "Exit right" },
  { value: "fork", label: "Fork" }, { value: "roundabout", label: "Roundabout" },
];
const ROUNDABOUT_EXITS: Option[] = ["left", "up-left", "up", "up-right", "right"].map((d) => ({ value: d, icon: <ArrowIcon direction={d} />, title: d }));
const LED_COLORS: Option[] = [
  { value: "", label: "Amber" }, { value: "#f4f4f4", label: "White" }, { value: "#46ff74", label: "Green" }, { value: "#ff4031", label: "Red" },
];
const CHIP_SPAN: Option[] = [{ value: "", label: "Fit" }, { value: "full", label: "Full width", title: "As wide as the column it is in" }];
const TAB_SIDE: Option[] = [{ value: "left", label: "Left" }, { value: "center", label: "Center" }, { value: "right", label: "Right" }];

/**
 * Buttons laid out in rows by their label lengths (Gameface does not grow a wrapped flex row, so a second line would
 * overlap whatever follows).
 */
const Palette = (props: { entries: { label: string; onClick: () => void }[] }) => {
  const rows: { label: string; onClick: () => void }[][] = [[]];
  let used = 0;
  for (const e of props.entries) {
    const width = e.label.length * 8 + 24;
    if (used + width > 560 && rows[rows.length - 1].length > 0) { rows.push([]); used = 0; }
    rows[rows.length - 1].push(e);
    used += width;
  }
  return (
    <div className={styles.choiceRows}>
      {rows.map((row, i) => (
        <div key={i} className={styles.palette}>{row.map((e) => <div key={e.label} className={styles.chip} onClick={e.onClick}>{e.label}</div>)}</div>
      ))}
    </div>
  );
};

/** Alignment of one part within its column, overriding the sign's "Align lines". */
const PlaceField = (props: { item: Item; onChange: (patch: Partial<Item>) => void }) => (
  <Field label="Align this part"><Choice options={PLACE} value={props.item.place ?? ""} onChange={(place) => props.onChange({ place: place || undefined })} /></Field>
);

/**
 * Color of a text, arrow or divider: the default (the sign's or chip's legend color), one of the theme's colors,
 * or any color.
 */
const PartColorField = (props: { item: Item; theme?: Theme; onChange: (patch: Partial<Item>) => void }) => {
  const palette = Array.from(new Set(Object.values(props.theme?.def.schemes ?? {}).flatMap((s) => [s.fg, s.bg].map((c) => c.toLowerCase()))));
  const color = props.item.color as string | undefined;
  const custom = !!color && !palette.includes(color.toLowerCase());
  return (
    <Field label="Color">
      <div className={styles.choices}>
        <div className={!color ? styles.choiceOn : styles.choice} title="The sign's legend color" onClick={() => props.onChange({ color: undefined })}>Default</div>
        {palette.map((c) => (
          <div key={c} title={c} className={color?.toLowerCase() === c ? styles.swatchOn : styles.swatch} style={{ backgroundColor: c, width: "22rem" }}
            onClick={() => props.onChange({ color: c })} />
        ))}
        <div title="Any color" className={custom ? styles.swatchOn : styles.swatch} style={{ width: "22rem", backgroundImage: "linear-gradient(90deg, #e53935, #fdd835, #43a047, #1e88e5, #8e24aa)" }}
          onClick={() => props.onChange({ color: color ?? "#ff8800" })} />
      </div>
      {custom && <ColorBox value={color!} onChange={(c) => props.onChange({ color: c })} />}
    </Field>
  );
};

/** Extra space around a part (sign units), added to the usual spacing: top, bottom, left, right. */
const MarginField = (props: { item: Item; onChange: (patch: Partial<Item>) => void }) => {
  const m = props.item.margin ?? {};
  const side = (key: string, label: string) => (
    <div className={styles.marginSide}>
      <div className={styles.marginLabel}>{label}</div>
      <NumberBox compact value={m[key] ?? 0} min={-40} onChange={(v) => {
        const next = { ...m, [key]: v || undefined };
        props.onChange({ margin: Object.values(next).some((x) => x) ? next : undefined });
      }} />
    </div>
  );
  return (
    <Field label="Margin">
      <div className={styles.marginGrid}>
        <div className={styles.inlineRow}>{side("top", "Top")}{side("bottom", "Bottom")}</div>
        <div className={styles.inlineRow}>{side("left", "Left")}{side("right", "Right")}</div>
      </div>
    </Field>
  );
};

function ItemProperties(props: { item: Item; onChange: (patch: Partial<Item>) => void; focusToken: number; inRow: boolean; theme?: Theme }) {
  const { item, onChange, theme } = props;
  const def = theme?.def;
  const place = (
    <>
      {!props.inRow && <PlaceField item={item} onChange={onChange} />}
      <MarginField item={item} onChange={onChange} />
    </>
  );
  const fonts: Option[] = (def?.fonts ?? []).filter((f) => !f.hidden).map((f) => ({ value: f.family, label: f.label ?? f.family, style: { fontFamily: f.family } }));
  const networks: Option[] = Object.entries(def?.shields ?? {}).map(([id, s]) => ({ value: id, icon: <ThemeShieldIcon theme={theme} network={id} />, title: s.label ?? id }));
  const schemes: Option[] = Object.entries(def?.schemes ?? {}).map(([id, s]) => ({
    value: id, title: s.label ?? id, label: " ", style: { backgroundColor: s.bg, minWidth: "28rem", border: `2rem solid ${s.fg}` },
  }));
  switch (item.type) {
    case "text":
      return (
        <>
          <Field label="Text"><TextBox value={item.text ?? ""} focusToken={props.focusToken} onChange={(text) => onChange({ text })} /></Field>
          <Field label="Letter height"><NumberBox value={item.height ?? def?.defaults?.textHeight ?? 16} onChange={(height) => onChange({ height })} /></Field>
          <Field label="Letter spacing"><NumberBox value={item.tracking ?? 0} step={2} min={-20} onChange={(tracking) => onChange({ tracking: tracking || undefined })} /></Field>
          {fonts.length > 1 && <Field label="Font"><Select options={fonts} value={item.font ?? def?.defaults?.font ?? ""} onChange={(font) => onChange({ font })} /></Field>}
          <PartColorField item={item} theme={theme} onChange={onChange} />
          {place}
        </>
      );
    case "shield":
      return (
        <>
          <Field label="Route"><Choice options={networks} perRow={5} value={item.network} onChange={(network) => onChange({ network })} /></Field>
          <Field label="Number"><TextBox value={String(item.number ?? "")} focusToken={props.focusToken} onChange={(number) => onChange({ number })} /></Field>
          <Field label="Size"><NumberBox value={item.height ?? def?.defaults?.shieldHeight ?? 36} step={2} onChange={(height) => onChange({ height })} /></Field>
          {place}
        </>
      );
    case "arrow":
      return (
        <>
          <Field label="Direction">
            <Choice options={ARROWS.map((a) => ({ value: a, icon: <ArrowIcon direction={a} />, title: a }))} perRow={6}
              value={item.direction ?? "up"} onChange={(direction) => onChange({ direction })} />
          </Field>
          <Field label="Size"><NumberBox value={item.height ?? def?.defaults?.arrowHeight ?? 24} step={2} onChange={(height) => onChange({ height })} /></Field>
          <PartColorField item={item} theme={theme} onChange={onChange} />
          {place}
        </>
      );
    case "row":
      return (
        <>
          <Field label="Line up parts by their"><Choice options={ROW_ALIGN} value={item.align ?? "center"} onChange={(align) => onChange({ align })} /></Field>
          <Field label="Spacing"><Choice options={ROW_SPACING} value={item.justify ?? ""} onChange={(justify) => onChange({ justify: justify || undefined })} /></Field>
          {place}
        </>
      );
    case "stack":
      return (
        <>
          <Field label="Align within the column"><Choice options={ALIGN} value={item.align ?? "start"} onChange={(align) => onChange({ align })} /></Field>
          {place}
        </>
      );
    case "chip":
      return (
        <>
          <Field label="Chip color"><Choice options={schemes} perRow={6} value={item.scheme ?? ""} onChange={(scheme) => onChange({ scheme })} /></Field>
          <Field label="Border"><Choice options={[{ value: "no", label: "None" }, { value: "yes", label: "Legend color" }]} value={item.border ? "yes" : "no"}
            onChange={(v) => onChange({ border: v === "yes" ? true : undefined })} /></Field>
          <Field label="Width"><Choice options={CHIP_SPAN} value={item.span ?? ""} onChange={(span) => onChange({ span: span || undefined })} /></Field>
          <Field label="Align within the chip"><Choice options={ALIGN} value={item.align ?? "center"} onChange={(align) => onChange({ align })} /></Field>
          {place}
        </>
      );
    case "panel":
      return (
        <>
          <Field label="Panel color"><Choice options={schemes} perRow={6} value={item.scheme ?? ""} onChange={(scheme) => onChange({ scheme })} /></Field>
          <Field label="Align within the panel"><Choice options={ALIGN} value={item.align ?? "center"} onChange={(align) => onChange({ align })} /></Field>
        </>
      );
    case "diagram":
      return (
        <>
          <Field label="Shape"><Choice options={DIAGRAM_SHAPES} value={item.shape ?? "exit-left"} onChange={(shape) => onChange({ shape })} /></Field>
          {item.shape === "roundabout" ? (
            <Field label="Exits">
              <div className={styles.choices}>
                {ROUNDABOUT_EXITS.map((o) => {
                  const exits: string[] = item.exits ?? ["left", "up", "right"];
                  const on = exits.includes(o.value);
                  return (
                    <div key={o.value} title={o.title} className={on ? styles.choiceOn : styles.choice}
                      onClick={() => onChange({ exits: on ? exits.filter((x) => x !== o.value) : [...exits, o.value] })}>{o.icon}</div>
                  );
                })}
              </div>
            </Field>
          ) : (
            <div className={styles.inlineRow}>
              <Field label="Through lanes"><NumberBox compact value={item.lanes ?? 2} min={1} max={6} onChange={(lanes) => onChange({ lanes })} /></Field>
              <div style={{ width: "12rem" }} />
              <Field label="Exit lanes"><NumberBox compact value={item.exitLanes ?? 1} min={1} max={4} onChange={(exitLanes) => onChange({ exitLanes })} /></Field>
            </div>
          )}
          <Field label="Size"><NumberBox value={item.height ?? 50} step={2} onChange={(height) => onChange({ height })} /></Field>
          <PartColorField item={item} theme={theme} onChange={onChange} />
          {place}
        </>
      );
    case "message": {
      const lines: string[] = item.lines ?? [""];
      const setLine = (k: number, text: string) => onChange({ lines: lines.map((l, i) => (i === k ? text : l)) });
      return (
        <>
          <Field label="Lines">
            {lines.map((line, k) => (
              <div key={k} className={styles.inlineRow} style={{ marginBottom: "3rem" }}>
                <div style={{ width: "200rem" }}><TextBox value={line} focusToken={k === 0 ? props.focusToken : undefined} onChange={(t) => setLine(k, t)} /></div>
                {lines.length > 1 && <div className={styles.rowDelete} title="Remove this line" onClick={() => onChange({ lines: lines.filter((_, i) => i !== k) })}>✕</div>}
              </div>
            ))}
            <div className={styles.chip} style={{ alignSelf: "flex-start" }} onClick={() => onChange({ lines: [...lines, ""] })}>Add line</div>
          </Field>
          <Field label="Letter height"><NumberBox value={item.height ?? 12} onChange={(height) => onChange({ height })} /></Field>
          <Field label="LED color"><Choice options={LED_COLORS} value={item.color ?? ""} onChange={(color) => onChange({ color: color || undefined })} /></Field>
          {place}
        </>
      );
    }
    case "divider":
      return (
        <>
          <PartColorField item={item} theme={theme} onChange={onChange} />
          {place}
        </>
      );
    case "tab":
      return (
        <>
          <Field label="Position"><Choice options={TAB_SIDE} value={item.align ?? "right"} onChange={(align) => onChange({ align })} /></Field>
          <Field label="Color"><Choice options={[{ value: "", label: "Sign" }, ...schemes]} perRow={7} value={item.scheme ?? ""} onChange={(scheme) => onChange({ scheme: scheme || undefined })} /></Field>
        </>
      );
    default:
      return place;
  }
}

export type DesignerAction = { label: string; primary?: boolean; onSelect: () => void; title?: string };

/**
 * The sign designer: one sign at a time. Templates on the left, live preview and sign options in the middle,
 * parts on the right. Everything offered (fonts, colors, route plates, parts) comes from the sign's theme; the
 * actions row depends on what the sign is for (new sign, editing a placed one, a gantry sign).
 */
export const Designer = (props: {
  design: Design;
  setDesign: (d: Design) => void;
  /** Loads a fresh design (template or blank sign), asking first if the current one has unsaved changes; <then> runs once loaded. */
  onLoad: (d: Design, then: () => void) => void;
  onTemplateSaved: () => void;
  templates: Template[];
  themes: Theme[];
  title: string;
  actions: DesignerAction[];
  onClose: () => void;
  /** Drawn over the dialog's content (the discard-changes question). */
  overlay?: ReactNode;
}) => {
  const { design, setDesign } = props;
  const [selected, setSelected] = useState<string | null>(null);
  // Bumped when a part is picked on the preview, so its text box takes focus with the caret at the end.
  const [focusToken, setFocusToken] = useState(0);
  const theme = themeOf(props.themes, design);
  const def = theme?.def;
  const custom = !!design.background;
  const widthMode = design.minLanes ? "lanes" : design.minWidth ? "custom" : "auto";
  const labels = Object.fromEntries(Object.entries(def?.shields ?? {}).map(([id, s]) => [id, s.label ?? id]));

  const item = selected != null ? getItem(design, selected) : null;
  const update = (patch: Partial<Item>) => { if (selected != null) setDesign(updateItem(design, selected, patch)); };
  // <insert> "first": the preset goes at the top of the sign (a panel across its top, like an E-ZPass panel).
  const add = (made: Item, insert?: "first") => {
    if (insert === "first") {
      const d = clone(design);
      d.items.unshift(clone(made));
      setDesign(d);
      setSelected("0");
      return;
    }
    // Tabs and panels belong to the sign itself, so they go at its top level (a panel after the selected part's top-level part).
    const anchor = made.type === "tab" ? null : made.type === "panel" ? selected?.split(".")[0] ?? null : selected;
    const r = insertItem(design, anchor, clone(made));
    setDesign(r.design);
    setSelected(r.path);
  };
  const templates = props.templates.filter((t) => (t.design.theme ?? "us") === (theme?.id ?? "us"));

  return (
    <Dialog dock="center" title={props.title} className={styles.panel} onClose={props.onClose}>
      <div className={styles.body}>
        <div className={styles.column}>
          <div className={styles.heading}>Theme</div>
          <div className={styles.inlineRow}>
            <Select options={[...props.themes].sort((a, b) => a.name.localeCompare(b.name)).map((t) => ({ value: t.id, label: t.name, title: t.def.description }))} value={theme?.id ?? "us"}
              onChange={(id) => { const to = props.themes.find((t) => t.id === id); if (to) { setDesign(convertDesign(design, to)); setSelected(null); } }} />
            <div className={styles.chip} style={{ margin: "0 0 0 6rem" }} title="Add your own themes here (one folder each); they appear when the Sign Builder is opened again"
              onClick={() => openThemesFolder()}>Folder</div>
          </div>
          <div className={styles.heading}>Templates</div>
          <TemplateLibrary templates={templates} current={design}
            onPick={(t) => props.onLoad(clone(t.design), () => setSelected(null))} onSaved={props.onTemplateSaved} />
          <div className={styles.chip} style={{ alignSelf: "flex-start", marginTop: "6rem" }} onClick={() => props.onLoad(blankFor(theme), () => setSelected(null))}>New blank sign</div>
        </div>

        <div className={styles.center}>
          <div className={styles.preview}>
            <SignPreview design={design} selected={selected}
              onSelect={(p) => { setSelected(p); if (p) setFocusToken(focusToken + 1); }} maxWidth={560} maxHeight={270} />
          </div>
          <Scrollable vertical trackVisibility="scrollable" className={styles.optionsScroll}>
          <div className={styles.signOptions}>
            <Field label="Sign type">
              <div className={styles.inlineRow}>
                <Choice options={[{ value: "", label: "Standard" }, { value: "led", label: "Message board", title: "A black LED panel: lit pixel lettering" }]}
                  value={design.board ?? ""} onChange={(board) => { const d: Design = { ...design, board: board || undefined }; if (!board) delete d.board; setDesign(d); }} />
                {design.board === "led" && (
                  <Choice options={LED_COLORS} value={design.foreground ?? ""} onChange={(c) => { const d: Design = { ...design, foreground: c || undefined }; if (!c) delete d.foreground; setDesign(d); }} />
                )}
              </div>
            </Field>
            {design.board !== "led" && (
            <Field label="Color">
              <Swatches schemes={def?.schemes ?? {}} scheme={design.scheme ?? def?.defaults?.scheme ?? ""} custom={custom}
                onScheme={(scheme) => { const d: Design = { ...design, scheme }; delete d.background; delete d.foreground; setDesign(d); }}
                onCustom={() => setDesign({ ...design, background: design.background ?? "#2d6a4f", foreground: design.foreground ?? "#ffffff" })} />
            </Field>
            )}
            {custom && (
              <div className={styles.customColors}>
                <Field label="Background"><ColorBox value={design.background} onChange={(background) => setDesign({ ...design, background })} /></Field>
                <Field label="Legend"><ColorBox value={design.foreground ?? "#ffffff"} onChange={(foreground) => setDesign({ ...design, foreground })} /></Field>
              </div>
            )}
            <Field label="Add part">
              <Palette entries={baseParts(theme).map((p) => ({ label: p.label, onClick: () => add(p.item) }))} />
            </Field>
            {(def?.presets ?? []).length > 0 && (
              <Field label="Add preset">
                <Palette entries={def!.presets!.map((p) => ({ label: p.label, onClick: () => add(p.item, p.insert) }))} />
              </Field>
            )}
            <Field label="Width">
              <div className={styles.inlineRow}>
                <Choice options={[{ value: "auto", label: "Fit text" }, { value: "lanes", label: "Lanes", title: "At least this many lanes wide (4 m lanes at 100% size)" },
                  { value: "custom", label: "At least" }]} value={widthMode}
                  onChange={(v) => {
                    const d: Design = { ...design };
                    delete d.minLanes;
                    delete d.minWidth;
                    delete d.laneKind;
                    if (v === "lanes") d.minLanes = 1;
                    if (v === "custom") d.minWidth = 96;
                    setDesign(d);
                  }} />
                {widthMode === "lanes" && <NumberBox compact value={design.minLanes} min={1} onChange={(minLanes) => setDesign({ ...design, minLanes })} />}
                {widthMode === "lanes" && <Choice options={LANE_KIND} value={design.laneKind ?? ""} onChange={(laneKind) => setDesign({ ...design, laneKind: laneKind || undefined })} />}
                {widthMode === "custom" && <NumberBox compact value={design.minWidth} step={12} onChange={(minWidth) => setDesign({ ...design, minWidth })} />}
              </div>
            </Field>
            <Field label="Align lines"><Choice options={ALIGN} value={design.align ?? "start"} onChange={(align) => setDesign({ ...design, align })} /></Field>
          </div>
          </Scrollable>
          <div className={styles.actions}>
            {props.actions.some((a) => a.primary) && props.actions.length > 2 && <div className={styles.placeAs}>Place as:</div>}
            {props.actions.map((a, i) => a.primary ? (
              <div key={a.label} className={styles.primaryButton} title={a.title} onClick={a.onSelect}>{a.label}</div>
            ) : (
              <div key={a.label} className={styles.chip} title={a.title} style={{ alignSelf: "center", marginLeft: i === 0 ? "0rem" : "6rem" }} onClick={a.onSelect}>{a.label}</div>
            ))}
          </div>
        </div>

        <div className={styles.column}>
          <Scrollable vertical trackVisibility="scrollable" className={styles.partsScroll}>
          <div className={styles.heading}>Parts</div>
          <PartsList design={design} selected={selected} onSelect={setSelected} labels={labels}
            onChange={(d, path) => { setDesign(d); setSelected(path); }}
            onDelete={(path) => { setDesign(removeItem(design, path)); setSelected(null); }}
            onDuplicate={(path) => { const r = duplicateItem(design, path); setDesign(r.design); setSelected(r.path); }} />
          {item && (
            <>
              <div className={styles.heading}>{kindOf(item, labels)}</div>
              <ItemProperties item={item} onChange={update} focusToken={focusToken} theme={theme}
                inRow={selected!.includes(".") && getItem(design, selected!.split(".").slice(0, -1).join("."))?.type === "row"} />
            </>
          )}
          </Scrollable>
        </div>
        {props.overlay}
      </div>
    </Dialog>
  );
};

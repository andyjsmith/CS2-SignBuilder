import { Panel, Scrollable } from "cs2/ui";
import { openThemesFolder } from "./bindings";
import { ReactNode, useState } from "react";
import { ArrowIcon, AlignIcon, ExitOnlyIcon, ExitTabIcon, ThemeShieldIcon } from "./Icons";
import { Choice, ColorBox, Field, NumberBox, Option, Select, Swatches, TextBox } from "./Controls";
import { Design, Item, clone, getItem, insertItem, kindOf, removeItem, updateItem } from "./designModel";
import { PartsList } from "./PartsList";
import { SignPreview } from "./SignPreview";
import { Template, TemplateLibrary } from "./TemplateLibrary";
import { Theme, blankFor, convertDesign, laneMinWidth, themeOf } from "./themeModel";
import styles from "./SignEditor.module.scss";

const ARROWS = ["up-left", "up", "up-right", "left", "right", "down-left", "down", "down-right", "turn-left", "split-left", "split-right", "turn-right"];
const ALIGN: Option[] = ["start", "center", "end"].map((a) => ({ value: a, icon: <AlignIcon align={a} />, title: a }));
// Parts side by side in a row line up by their tops, middles or bottoms.
const ROW_ALIGN: Option[] = [{ value: "start", label: "Top" }, { value: "center", label: "Middle" }, { value: "end", label: "Bottom" }];
const ROW_SPACING: Option[] = [{ value: "", label: "Packed" }, { value: "between", label: "Spread out", title: "Fill the width, with the space between the parts" }];
const PLACE: Option[] = [{ value: "", label: "Sign default" }, ...ALIGN];

/** Alignment of one part within its column, overriding the sign's "Align lines". */
const PlaceField = (props: { item: Item; onChange: (patch: Partial<Item>) => void }) => (
  <Field label="Align this part"><Choice options={PLACE} value={props.item.place ?? ""} onChange={(place) => props.onChange({ place: place || undefined })} /></Field>
);

/** Extra space around a part (sign units), added to the usual spacing: top, bottom, left, right. */
const MarginField = (props: { item: Item; onChange: (patch: Partial<Item>) => void }) => {
  const m = props.item.margin ?? {};
  const side = (key: string, label: string) => (
    <div className={styles.marginSide}>
      <div className={styles.marginLabel}>{label}</div>
      <NumberBox value={m[key] ?? 0} min={-40} onChange={(v) => {
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
    case "panel":
      return (
        <>
          <Field label="Panel color"><Choice options={schemes} perRow={6} value={item.scheme ?? ""} onChange={(scheme) => onChange({ scheme })} /></Field>
          <Field label="Border"><Choice options={[{ value: "no", label: "None" }, { value: "yes", label: "Legend color" }]} value={item.border ? "yes" : "no"}
            onChange={(v) => onChange({ border: v === "yes" ? true : undefined })} /></Field>
          <Field label="Align within the panel"><Choice options={ALIGN} value={item.align ?? "center"} onChange={(align) => onChange({ align })} /></Field>
          {place}
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
  /** Loads a fresh design (template or blank sign), which starts out with no unsaved changes. */
  onLoad: (d: Design) => void;
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
  const lane = laneMinWidth(theme);
  const widthMode = !design.minWidth ? "auto" : design.minWidth === lane ? "lane" : "custom";
  const labels = Object.fromEntries(Object.entries(def?.shields ?? {}).map(([id, s]) => [id, s.label ?? id]));

  // The exit tab and the EXIT ONLY panel are sign-level parts, not in the items list.
  const signPart = selected === "exitTab" || selected === "bottomBar";
  const item = selected != null && !signPart ? getItem(design, selected) : null;
  const update = (patch: Partial<Item>) => { if (selected != null) setDesign(updateItem(design, selected, patch)); };
  const add = (made: Item) => {
    const r = insertItem(design, signPart ? null : selected, clone(made));
    setDesign(r.design);
    setSelected(r.path);
  };
  const templates = props.templates.filter((t) => (t.design.theme ?? "us") === (theme?.id ?? "us"));

  return (
    <Panel draggable header={props.title} className={styles.panel} onClose={props.onClose}>
      <div className={styles.body}>
        <div className={styles.column}>
          <div className={styles.heading}>Theme</div>
          <div className={styles.inlineRow}>
            <Select options={props.themes.map((t) => ({ value: t.id, label: t.name, title: t.def.description }))} value={theme?.id ?? "us"}
              onChange={(id) => { const to = props.themes.find((t) => t.id === id); if (to) { setDesign(convertDesign(design, to)); setSelected(null); } }} />
            <div className={styles.chip} style={{ margin: "0 0 0 6rem" }} title="Add your own themes here (one folder each); they appear when the Sign Builder is opened again"
              onClick={() => openThemesFolder()}>Folder</div>
          </div>
          <div className={styles.heading}>Templates</div>
          <TemplateLibrary templates={templates} current={design}
            onPick={(t) => { props.onLoad(clone(t.design)); setSelected(null); }} onSaved={props.onTemplateSaved} />
          <div className={styles.chip} style={{ alignSelf: "flex-start", marginTop: "6rem" }} onClick={() => { props.onLoad(blankFor(theme)); setSelected(null); }}>New blank sign</div>
        </div>

        <div className={styles.center}>
          <div className={styles.preview}>
            <SignPreview design={design} selected={selected}
              onSelect={(p) => { setSelected(p); if (p) setFocusToken(focusToken + 1); }} maxWidth={560} maxHeight={270} />
          </div>
          <div className={styles.hint}>Click any part of the sign to edit it.</div>
          <Scrollable vertical trackVisibility="scrollable" className={styles.optionsScroll}>
          <div className={styles.signOptions}>
            <Field label="Color">
              <Swatches schemes={def?.schemes ?? {}} scheme={design.scheme ?? def?.defaults?.scheme ?? ""} custom={custom}
                onScheme={(scheme) => { const d: Design = { ...design, scheme }; delete d.background; delete d.foreground; setDesign(d); }}
                onCustom={() => setDesign({ ...design, background: design.background ?? "#2d6a4f", foreground: design.foreground ?? "#ffffff" })} />
            </Field>
            {custom && (
              <div className={styles.customColors}>
                <Field label="Background"><ColorBox value={design.background} onChange={(background) => setDesign({ ...design, background })} /></Field>
                <Field label="Legend"><ColorBox value={design.foreground ?? "#ffffff"} onChange={(foreground) => setDesign({ ...design, foreground })} /></Field>
              </div>
            )}
            {def?.features?.exitTab && (
              <Field label="Exit tab">
                <Choice options={["none", "left", "right"].map((v) => ({ value: v, icon: <ExitTabIcon side={v} />, title: v }))}
                  value={design.exitTab ? design.exitTab.align ?? "right" : "none"}
                  onChange={(v) => setDesign({ ...design, exitTab: v === "none" ? null : { text: design.exitTab?.text ?? "EXIT 1", align: v } })} />
              </Field>
            )}
            {def?.features?.exitOnly && (
              <Field label="EXIT ONLY panel">
                <Choice options={[0, 1, 2].map((n) => ({ value: String(n), icon: <ExitOnlyIcon arrows={n} />, title: n === 0 ? "none" : `${n} arrow${n > 1 ? "s" : ""}` }))}
                  value={design.bottomBar ? String(design.bottomBar.arrows ?? 1) : "0"}
                  onChange={(v) => setDesign({ ...design, bottomBar: v === "0" ? null : { ...(design.bottomBar ?? {}), type: "exitOnly", arrows: Number(v) } })} />
              </Field>
            )}
            <Field label="Width">
              <div className={styles.inlineRow}>
                <Choice options={[{ value: "auto", label: "Fit text" }, { value: "lane", label: "At least a lane", title: "Never narrower than a 4 m lane at 100% size" },
                  { value: "custom", label: "At least" }]} value={widthMode}
                  onChange={(v) => setDesign({ ...design, minWidth: v === "auto" ? undefined : v === "lane" ? lane : design.minWidth && design.minWidth !== lane ? design.minWidth : Math.round(lane / 2) })} />
                {widthMode === "custom" && (
                  <NumberBox value={design.minWidth} step={12} onChange={(minWidth) => setDesign({ ...design, minWidth })} />
                )}
              </div>
            </Field>
            <Field label="Align lines"><Choice options={ALIGN} value={design.align ?? "start"} onChange={(align) => setDesign({ ...design, align })} /></Field>
          </div>
          </Scrollable>
          <div className={styles.actions}>
            {props.actions.map((a, i) => a.primary ? (
              <div key={a.label} className={styles.primaryButton} title={a.title} onClick={a.onSelect}>{a.label}</div>
            ) : (
              <div key={a.label} className={styles.chip} title={a.title} style={{ alignSelf: "center", marginLeft: i === 0 ? "0rem" : "6rem" }} onClick={a.onSelect}>{a.label}</div>
            ))}
          </div>
        </div>

        <div className={styles.column}>
          <div className={styles.heading}>Add part</div>
          <div className={styles.palette}>
            {(def?.parts ?? []).map((p) => <div key={p.label} className={styles.chip} onClick={() => add(p.item)}>{p.label}</div>)}
          </div>
          <Scrollable vertical trackVisibility="scrollable" className={styles.partsScroll}>
          <div className={styles.heading}>Parts</div>
          <PartsList design={design} selected={selected} onSelect={setSelected} labels={labels}
            onChange={(d, path) => { setDesign(d); setSelected(path); }}
            onDelete={(path) => {
              setDesign(path === "exitTab" ? { ...design, exitTab: null } : path === "bottomBar" ? { ...design, bottomBar: null } : removeItem(design, path));
              setSelected(null);
            }} />
          {selected === "exitTab" && design.exitTab && (
            <>
              <div className={styles.heading}>Exit tab</div>
              <Field label="Text">
                <TextBox value={design.exitTab.text} focusToken={focusToken} onChange={(text) => setDesign({ ...design, exitTab: { ...design.exitTab!, text } })} />
              </Field>
              <Field label="Letter height">
                <NumberBox value={design.exitTab.height ?? 10} onChange={(height) => setDesign({ ...design, exitTab: { ...design.exitTab!, height } })} />
              </Field>
            </>
          )}
          {selected === "bottomBar" && design.bottomBar && (
            <>
              <div className={styles.heading}>EXIT ONLY panel</div>
              <Field label="Words">
                <div className={styles.inlineRow}>
                  {[0, 1].map((k) => (
                    <div key={k} style={{ width: "120rem", marginRight: "6rem" }}>
                      <TextBox value={(design.bottomBar!.words ?? ["EXIT", "ONLY"])[k]} focusToken={k === 0 ? focusToken : undefined}
                        onChange={(text) => {
                          const words = [...(design.bottomBar!.words ?? ["EXIT", "ONLY"])];
                          words[k] = text;
                          setDesign({ ...design, bottomBar: { ...design.bottomBar!, words } });
                        }} />
                    </div>
                  ))}
                </div>
              </Field>
              <Field label="Letter height">
                <NumberBox value={design.bottomBar.height ?? 14} onChange={(height) => setDesign({ ...design, bottomBar: { ...design.bottomBar!, height } })} />
              </Field>
            </>
          )}
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
    </Panel>
  );
};

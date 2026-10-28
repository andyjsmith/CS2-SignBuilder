import { Button, Panel } from "cs2/ui";
import { useState } from "react";
import { ArrowIcon, AlignIcon, ExitOnlyIcon, ExitTabIcon, ShieldIcon } from "./Icons";
import { Choice, ColorBox, Field, NumberBox, Option, Swatches, TextBox } from "./Controls";
import { Design, Item, PALETTE, clone, describe, getItem, insertItem, removeItem, updateItem } from "./designModel";
import { PartsList } from "./PartsList";
import { SignPreview } from "./SignPreview";
import { Template, TemplateLibrary } from "./TemplateLibrary";
import styles from "./SignEditor.module.scss";

export const BLANK: Design = { scheme: "green", items: [{ type: "text", text: "Destination", height: 16 }] };

const FONTS: Option[] = [
  { value: "RG2014D", label: "Series D" }, { value: "RG2014E", label: "Series E" }, { value: "RG2014EM", label: "Series E(M)" },
  { value: "RG2014C", label: "Series C" }, { value: "RG2014F", label: "Series F" },
].map((f) => ({ ...f, style: { fontFamily: f.value } }));
const ARROWS = ["up-left", "up", "up-right", "left", "right", "down-left", "down", "down-right"];
const NETWORKS: Option[] = [
  { value: "I", icon: <ShieldIcon network="I" />, title: "Interstate" },
  { value: "US", icon: <ShieldIcon network="US" />, title: "US route" },
  { value: "STATE", icon: <ShieldIcon network="STATE" />, title: "State route" },
];
const ALIGN: Option[] = ["start", "center", "end"].map((a) => ({ value: a, icon: <AlignIcon align={a} />, title: a }));

function ItemProperties(props: { item: Item; onChange: (patch: Partial<Item>) => void; focusToken: number }) {
  const { item, onChange } = props;
  switch (item.type) {
    case "text":
      return (
        <>
          <Field label="Text"><TextBox value={item.text ?? ""} focusToken={props.focusToken} onChange={(text) => onChange({ text })} /></Field>
          <Field label="Letter height"><NumberBox value={item.height ?? 16} onChange={(height) => onChange({ height })} /></Field>
          <Field label="Font"><Choice options={FONTS} value={item.font ?? "RG2014EM"} onChange={(font) => onChange({ font })} /></Field>
        </>
      );
    case "shield":
      return (
        <>
          <Field label="Route"><Choice options={NETWORKS} value={item.network} onChange={(network) => onChange({ network })} /></Field>
          <Field label="Number"><TextBox value={String(item.number ?? "")} focusToken={props.focusToken} onChange={(number) => onChange({ number })} /></Field>
          <Field label="Size"><NumberBox value={item.height ?? 36} step={6} onChange={(height) => onChange({ height })} /></Field>
        </>
      );
    case "arrow":
      return (
        <>
          <Field label="Direction">
            <Choice options={ARROWS.map((a) => ({ value: a, icon: <ArrowIcon direction={a} />, title: a }))}
              value={item.direction ?? "up"} onChange={(direction) => onChange({ direction })} />
          </Field>
          <Field label="Size"><NumberBox value={item.height ?? 24} step={2} onChange={(height) => onChange({ height })} /></Field>
        </>
      );
    case "row":
      return <Field label="Align"><Choice options={ALIGN} value={item.align ?? "center"} onChange={(align) => onChange({ align })} /></Field>;
    default:
      return null;
  }
}

export type DesignerAction = { label: string; primary?: boolean; onSelect: () => void; title?: string };

/**
 * The sign designer: one sign at a time. Templates on the left, live preview and sign options in the middle,
 * parts on the right. The actions row depends on what the sign is for (new sign, editing a placed one, a gantry sign).
 */
export const Designer = (props: {
  design: Design;
  setDesign: (d: Design) => void;
  templates: Template[];
  title: string;
  actions: DesignerAction[];
  status: string;
  onClose: () => void;
}) => {
  const { design, setDesign } = props;
  const [selected, setSelected] = useState<string | null>(null);
  // Bumped when a part is picked on the preview, so its text box takes focus with the caret at the end.
  const [focusToken, setFocusToken] = useState(0);
  const custom = !!design.background;

  const item = selected != null && selected !== "exitTab" ? getItem(design, selected) : null;
  const update = (patch: Partial<Item>) => { if (selected != null) setDesign(updateItem(design, selected, patch)); };
  const add = (made: Item) => {
    const r = insertItem(design, selected === "exitTab" ? null : selected, made);
    setDesign(r.design);
    setSelected(r.path);
  };

  return (
    <Panel draggable header={props.title} className={styles.panel} onClose={props.onClose}>
      <div className={styles.body}>
        <div className={styles.column}>
          <div className={styles.heading}>Templates</div>
          <TemplateLibrary templates={props.templates} current={design}
            onPick={(t) => { setDesign(clone(t.design)); setSelected(null); }} />
        </div>

        <div className={styles.centre}>
          <div className={styles.preview}>
            <SignPreview design={design} selected={selected}
              onSelect={(p) => { setSelected(p); if (p) setFocusToken(focusToken + 1); }} maxWidth={560} maxHeight={300} />
          </div>
          <div className={styles.hint}>Click any part of the sign to edit it.</div>
          <div className={styles.signOptions}>
            <Field label="Colour">
              <Swatches scheme={design.scheme ?? "green"} custom={custom}
                onScheme={(scheme) => { const d: Design = { ...design, scheme }; delete d.background; delete d.foreground; setDesign(d); }}
                onCustom={() => setDesign({ ...design, background: design.background ?? "#2d6a4f", foreground: design.foreground ?? "#ffffff" })} />
            </Field>
            {custom && (
              <div className={styles.customColours}>
                <Field label="Background"><ColorBox value={design.background} onChange={(background) => setDesign({ ...design, background })} /></Field>
                <Field label="Legend"><ColorBox value={design.foreground ?? "#ffffff"} onChange={(foreground) => setDesign({ ...design, foreground })} /></Field>
              </div>
            )}
            <Field label="Exit tab">
              <Choice options={["none", "left", "right"].map((v) => ({ value: v, icon: <ExitTabIcon side={v} />, title: v }))}
                value={design.exitTab ? design.exitTab.align ?? "right" : "none"}
                onChange={(v) => setDesign({ ...design, exitTab: v === "none" ? null : { text: design.exitTab?.text ?? "EXIT 1", align: v } })} />
            </Field>
            <Field label="EXIT ONLY panel">
              <Choice options={[0, 1, 2].map((n) => ({ value: String(n), icon: <ExitOnlyIcon arrows={n} />, title: n === 0 ? "none" : `${n} arrow${n > 1 ? "s" : ""}` }))}
                value={design.bottomBar ? String(design.bottomBar.arrows ?? 1) : "0"}
                onChange={(v) => setDesign({ ...design, bottomBar: v === "0" ? null : { type: "exitOnly", arrows: Number(v), height: 15 } })} />
            </Field>
            <Field label="Align lines"><Choice options={ALIGN} value={design.align ?? "start"} onChange={(align) => setDesign({ ...design, align })} /></Field>
          </div>
          <div className={styles.actions}>
            {props.actions.map((a, i) => a.primary ? (
              <div key={a.label} style={{ marginRight: "10rem" }}><Button variant="primary" onSelect={a.onSelect}>{a.label}</Button></div>
            ) : (
              <div key={a.label} className={styles.chip} title={a.title} style={{ alignSelf: "center", marginLeft: i === 0 ? "0rem" : "6rem" }} onClick={a.onSelect}>{a.label}</div>
            ))}
          </div>
          {props.status && <div className={styles.status}>{props.status}</div>}
        </div>

        <div className={styles.column}>
          <div className={styles.heading}>Add part</div>
          <div className={styles.palette}>
            {PALETTE.map((p) => <div key={p.label} className={styles.chip} onClick={() => add(p.make())}>{p.label}</div>)}
          </div>
          <div className={styles.heading}>Parts</div>
          <PartsList design={design} selected={selected} onSelect={setSelected}
            onChange={(d, path) => { setDesign(d); setSelected(path); }} />
          {selected === "exitTab" && design.exitTab && (
            <>
              <div className={styles.heading}>Exit tab</div>
              <Field label="Text">
                <TextBox value={design.exitTab.text} focusToken={focusToken} onChange={(text) => setDesign({ ...design, exitTab: { ...design.exitTab!, text } })} />
              </Field>
            </>
          )}
          {item && (
            <>
              <div className={styles.heading}>{describe(item)}</div>
              <ItemProperties item={item} onChange={update} focusToken={focusToken} />
              <div className={styles.palette}>
                <div className={styles.chip} onClick={() => { setDesign(removeItem(design, selected!)); setSelected(null); }}>Delete part</div>
              </div>
            </>
          )}
        </div>
      </div>
    </Panel>
  );
};

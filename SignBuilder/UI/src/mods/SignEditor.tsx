import { useValue } from "cs2/api";
import { Button, Panel } from "cs2/ui";
import { useEffect, useMemo, useState } from "react";
import { cancelTool, editingDesign$, editorOpen$, setEditorOpen, startPlacement, templates$, toolStatus$ } from "./bindings";
import {
  Design, Item, PALETTE, SUPPORTS, clone, describe, getItem, insertItem, joinWithNext, moveItem, outline, removeItem, updateItem,
} from "./designModel";
import { SignPreview } from "./SignPreview";
import styles from "./SignEditor.module.scss";

type Template = { id: string; name: string; design: Design; support?: string };

const BLANK: Design = { scheme: "green", items: [{ type: "text", text: "Destination", height: 16 }] };
const SCHEMES = ["green", "blue", "brown", "yellow", "white"];
const FONTS = ["RG2014D", "RG2014E", "RG2014EM", "RG2014C", "RG2014F"];
const ARROWS = ["up", "down", "left", "right", "up-left", "up-right", "down-left", "down-right"];
const NETWORKS = ["I", "US", "STATE"];

const Choice = (props: { options: string[]; value: string; onChange: (v: string) => void }) => (
  <div className={styles.choices}>
    {props.options.map((o) => (
      <div key={o} className={o === props.value ? styles.choiceOn : styles.choice} onClick={() => props.onChange(o)}>{o}</div>
    ))}
  </div>
);

const Field = (props: { label: string; children: any }) => (
  <div className={styles.field}>
    <div className={styles.label}>{props.label}</div>
    {props.children}
  </div>
);

const TextBox = (props: { value: string; onChange: (v: string) => void }) => (
  <input className={styles.input} type="text" value={props.value}
    onChange={(e) => props.onChange((e.target as HTMLInputElement).value)} />
);

const NumberBox = (props: { value: number; onChange: (v: number) => void; step?: number }) => (
  <div className={styles.stepper}>
    <div className={styles.step} onClick={() => props.onChange(Math.max(1, props.value - (props.step ?? 1)))}>-</div>
    <div className={styles.stepValue}>{props.value}"</div>
    <div className={styles.step} onClick={() => props.onChange(props.value + (props.step ?? 1))}>+</div>
  </div>
);

function ItemProperties(props: { item: Item; onChange: (patch: Partial<Item>) => void }) {
  const { item, onChange } = props;
  switch (item.type) {
    case "text":
      return (
        <>
          <Field label="Text"><TextBox value={item.text ?? ""} onChange={(text) => onChange({ text })} /></Field>
          <Field label="Letter height"><NumberBox value={item.height ?? 16} onChange={(height) => onChange({ height })} /></Field>
          <Field label="Font"><Choice options={FONTS} value={item.font ?? "RG2014EM"} onChange={(font) => onChange({ font })} /></Field>
        </>
      );
    case "shield":
      return (
        <>
          <Field label="Route"><Choice options={NETWORKS} value={item.network} onChange={(network) => onChange({ network })} /></Field>
          <Field label="Number"><TextBox value={String(item.number ?? "")} onChange={(number) => onChange({ number })} /></Field>
          <Field label="Size"><NumberBox value={item.height ?? 36} step={6} onChange={(height) => onChange({ height })} /></Field>
        </>
      );
    case "arrow":
      return (
        <>
          <Field label="Direction"><Choice options={ARROWS} value={item.direction ?? "up"} onChange={(direction) => onChange({ direction })} /></Field>
          <Field label="Size"><NumberBox value={item.height ?? 24} step={2} onChange={(height) => onChange({ height })} /></Field>
        </>
      );
    case "row":
      return <Field label="Align"><Choice options={["start", "center", "end"]} value={item.align ?? "center"} onChange={(align) => onChange({ align })} /></Field>;
    default:
      return null;
  }
}

/** The Sign Builder editor: templates and parts on the left, live preview in the middle, properties on the right. */
export const SignEditor = () => {
  const open = useValue(editorOpen$);
  const templatesJson = useValue(templates$);
  const editing = useValue(editingDesign$);
  const status = useValue(toolStatus$);
  const templates: Template[] = useMemo(() => {
    try { return JSON.parse(templatesJson); } catch { return []; }
  }, [templatesJson]);

  const [design, setDesign] = useState<Design>(BLANK);
  const [selected, setSelected] = useState<string | null>(null);
  const [support, setSupport] = useState("truss");

  // Re-editing a placed sign: the C# side pushes { design, support } when it opens the editor.
  useEffect(() => {
    if (!editing) return;
    try {
      const payload = JSON.parse(editing);
      setDesign(payload.design ?? payload);
      if (payload.support) setSupport(payload.support);
      setSelected(null);
    } catch { /* ignore malformed payloads */ }
  }, [editing]);

  if (!open) return null;

  const item = selected != null ? getItem(design, selected) : null;
  const update = (patch: Partial<Item>) => { if (selected != null) setDesign(updateItem(design, selected, patch)); };
  const apply = (r: { design: Design; path: string }) => { setDesign(r.design); setSelected(r.path); };
  const editTab = selected === "exitTab";
  const supportLabel = SUPPORTS.find((s) => s.id === support)?.label ?? "";

  return (
    <Panel draggable header="Sign Builder" className={styles.panel} onClose={() => { cancelTool(); setEditorOpen(false); }}>
      <div className={styles.body}>
        <div className={styles.column}>
          <div className={styles.heading}>Templates</div>
          {templates.map((t) => (
            <div key={t.id} className={styles.listItem}
              onClick={() => { setDesign(clone(t.design)); if (t.support) setSupport(t.support); setSelected(null); }}>{t.name}</div>
          ))}
          <div className={styles.listItem} onClick={() => { setDesign(clone(BLANK)); setSelected(null); }}>Blank sign</div>

          <div className={styles.heading}>Add part</div>
          <div className={styles.palette}>
            {PALETTE.map((p) => (
              <div key={p.label} className={styles.chip} onClick={() => apply(insertItem(design, selected, p.make()))}>{p.label}</div>
            ))}
          </div>

          <div className={styles.heading}>Sign</div>
          <Field label="Colour"><Choice options={SCHEMES} value={design.scheme ?? "green"} onChange={(scheme) => setDesign({ ...design, scheme })} /></Field>
          <Field label="Exit tab">
            <Choice options={["none", "left", "right"]} value={design.exitTab ? design.exitTab.align ?? "right" : "none"}
              onChange={(v) => setDesign({ ...design, exitTab: v === "none" ? null : { text: design.exitTab?.text ?? "EXIT 1", align: v } })} />
          </Field>
          <Field label="EXIT ONLY panel">
            <Choice options={["none", "1 arrow", "2 arrows"]}
              value={design.bottomBar ? ((design.bottomBar.arrows ?? 1) > 1 ? "2 arrows" : "1 arrow") : "none"}
              onChange={(v) => setDesign({ ...design, bottomBar: v === "none" ? null : { type: "exitOnly", arrows: v.startsWith("2") ? 2 : 1, height: 15 } })} />
          </Field>
          <Field label="Align lines"><Choice options={["start", "center", "end"]} value={design.align ?? "start"} onChange={(align) => setDesign({ ...design, align })} /></Field>
        </div>

        <div className={styles.centre}>
          <div className={styles.preview}>
            <SignPreview design={design} selected={selected} onSelect={setSelected} maxWidth={560} maxHeight={330} />
          </div>
          <div className={styles.hint}>Click any part of the sign to edit it.</div>
          <div className={styles.heading}>Mounting</div>
          <Choice options={SUPPORTS.map((s) => s.label)} value={supportLabel}
            onChange={(label) => setSupport(SUPPORTS.find((s) => s.label === label)!.id)} />
          <div className={styles.actions}>
            <Button variant="primary" onSelect={() => startPlacement(JSON.stringify({ design, support }))}>Place on road</Button>
          </div>
          {status && <div className={styles.status}>{status}</div>}
        </div>

        <div className={styles.column}>
          <div className={styles.heading}>Parts</div>
          {design.exitTab && (
            <div className={editTab ? styles.listItemOn : styles.listItem} onClick={() => setSelected("exitTab")}>{`Exit tab "${design.exitTab.text}"`}</div>
          )}
          {outline(design).map(({ path, item: it, depth }) => (
            <div key={path} className={path === selected ? styles.listItemOn : styles.listItem}
              style={{ paddingLeft: 8 + depth * 14 + "rem" }} onClick={() => setSelected(path)}>
              {describe(it)}
            </div>
          ))}

          {editTab && design.exitTab && (
            <>
              <div className={styles.heading}>Exit tab</div>
              <Field label="Text"><TextBox value={design.exitTab.text} onChange={(text) => setDesign({ ...design, exitTab: { ...design.exitTab!, text } })} /></Field>
            </>
          )}
          {item && (
            <>
              <div className={styles.heading}>{describe(item)}</div>
              <ItemProperties item={item} onChange={update} />
              <div className={styles.palette}>
                <div className={styles.chip} onClick={() => apply(moveItem(design, selected!, -1))}>Move up</div>
                <div className={styles.chip} onClick={() => apply(moveItem(design, selected!, 1))}>Move down</div>
                <div className={styles.chip} onClick={() => apply(joinWithNext(design, selected!))}>Join with next</div>
                <div className={styles.chip} onClick={() => { setDesign(removeItem(design, selected!)); setSelected(null); }}>Delete</div>
              </div>
            </>
          )}
        </div>
      </div>
    </Panel>
  );
};

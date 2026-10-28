import { useValue } from "cs2/api";
import { getModule } from "cs2/modding";
import { Button, Panel } from "cs2/ui";
import { useEffect, useMemo, useState } from "react";
import {
  adjust, cancelTool, editingDesign$, editorOpen$, forgetEdit, placeInfo$, replacing$, saveEdit, setEditorOpen, startPlacement,
  templates$, toolMode$, toolStatus$,
} from "./bindings";
import {
  Design, Item, PALETTE, SPANS, STRUCTURES, clone, describe, getItem, insertItem, joinWithNext, moveItem, outline, parseSupport, removeItem,
  supportId, updateItem,
} from "./designModel";
import { AlignIcon, ArrowIcon, ExitOnlyIcon, ExitTabIcon, SCHEME_COLOURS, ShieldIcon } from "./Icons";
import { SignPreview } from "./SignPreview";
import styles from "./SignEditor.module.scss";

type Template = { id: string; name: string; design: Design; support?: string };
type Option = { value: string; label?: string; icon?: any; title?: string; style?: any };

const BLANK: Design = { scheme: "green", items: [{ type: "text", text: "Destination", height: 16 }] };
const SCHEMES = ["green", "blue", "brown", "yellow", "white"];
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

/** A row of option buttons showing an icon, a label, or both. */
const Choice = (props: { options: (string | Option)[]; value: string; onChange: (v: string) => void }) => (
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

const Swatches = (props: { value: string; onChange: (v: string) => void }) => (
  <div className={styles.choices}>
    {SCHEMES.map((s) => (
      <div key={s} title={s} className={s === props.value ? styles.swatchOn : styles.swatch}
        style={{ backgroundColor: SCHEME_COLOURS[s] }} onClick={() => props.onChange(s)} />
    ))}
  </div>
);

const Field = (props: { label: string; children: any }) => (
  <div className={styles.field}>
    <div className={styles.label}>{props.label}</div>
    {props.children}
  </div>
);

// The game's own text input takes keyboard focus properly (typing does not trigger game hotkeys);
// fall back to a plain <input> if a game update moves the module.
const VanillaTextInput: any = (() => {
  try { return getModule("game-ui/common/input/text/text-input.tsx", "TextInput"); } catch { return null; }
})();

const TextBox = (props: { value: string; onChange: (v: string) => void }) =>
  VanillaTextInput ? (
    <VanillaTextInput className={styles.input} type="text" multiline={1} value={props.value}
      onChange={(e: any) => props.onChange((e.target as HTMLInputElement).value)} />
  ) : (
    <input className={styles.input} type="text" value={props.value}
      onChange={(e) => props.onChange((e.target as HTMLInputElement).value)} />
  );

const NumberBox = (props: { value: number; onChange: (v: number) => void; step?: number }) => (
  <div className={styles.stepper}>
    <div className={styles.step} onClick={() => props.onChange(Math.max(1, props.value - (props.step ?? 1)))}>-</div>
    <div className={styles.stepValue}>{`${props.value}"`}</div>
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

type PlaceInfo = {
  free?: boolean; lane?: number; laneCount?: number; shift?: number; angle?: number; elevation?: number; width?: number; support?: string;
  scale?: number; signs?: number; signLanes?: number[]; signShifts?: number[];
};

/** The small bar shown at the top of the screen while placing (or picking a sign to edit). */
const PlacementBar = (props: { picking: boolean; status: string; onBack: () => void; onClose: () => void }) => {
  const infoJson = useValue(placeInfo$);
  const info: PlaceInfo = useMemo(() => { try { return JSON.parse(infoJson); } catch { return {}; } }, [infoJson]);
  const Step = (p: { action: string; children: any; title?: string }) => (
    <div className={styles.step} title={p.title} onClick={() => adjust(p.action)}>{p.children}</div>
  );
  const laneName = (lane: number, count: number) => (lane < 0 ? "With others" : `Over lane ${lane + 1} of ${count}`);
  const overhead = info.support !== "ground";
  const hint = info.free ? "Right-drag to rotate, Page Up / Page Down to raise and lower." : "";
  const PanelAny: any = Panel;
  return (
    <PanelAny draggable initialPosition={{ x: 0.5, y: 0.06 }} header="Sign Builder" className={styles.compact} onClose={props.onClose}>
      <div className={styles.compactBody}>
        <div className={styles.status}>{props.picking ? "Click a placed Sign Builder sign to edit it. Right-click to go back." : props.status}</div>
        {!props.picking && hint && <div className={styles.hint}>{hint}</div>}
        {!props.picking && (
          <>
            <div className={styles.barRow}>
              <Choice options={[{ value: "road", label: "Snap to road" }, { value: "free", label: "Free placement" }]}
                value={info.free ? "free" : "road"} onChange={(v) => adjust(v)} />
            </div>
            {info.free && (
              <div className={styles.barRow}>
                <div className={styles.barLabel}>Rotate</div>
                <Step action="rotateLeft" title="Rotate left (or right-drag)">-15°</Step>
                <div className={styles.barValue}>{`${Math.round(info.angle ?? 0)}°`}</div>
                <Step action="rotateRight" title="Rotate right (or right-drag)">+15°</Step>
                <div className={styles.barLabel} style={{ marginLeft: "16rem" }}>Height</div>
                <Step action="lower" title="Lower (Page Down)"><ArrowIcon direction="down" size={12} /></Step>
                <div className={styles.barValue}>{`${(info.elevation ?? 0).toFixed(1)} m`}</div>
                <Step action="raise" title="Raise (Page Up)"><ArrowIcon direction="up" size={12} /></Step>
              </div>
            )}
            {Array.from({ length: info.signs ?? 0 }, (_, i) => (
              <div key={i} className={styles.barRow}>
                <div className={styles.barLabel}>{(info.signs ?? 0) > 1 ? `Sign ${i + 1}` : "Sign"}</div>
                {!info.free && (info.laneCount ?? 0) > 0 && (
                  <>
                    <Step action={`signLaneLeft:${i}`} title="Previous lane"><ArrowIcon direction="left" size={12} /></Step>
                    <div className={styles.barValue}>{laneName(info.signLanes?.[i] ?? -1, info.laneCount ?? 0)}</div>
                    <Step action={`signLaneRight:${i}`} title="Next lane"><ArrowIcon direction="right" size={12} /></Step>
                  </>
                )}
                <div className={styles.barLabel} style={{ marginLeft: "12rem" }}>Nudge</div>
                <Step action={`signShiftLeft:${i}`} title="Move 0.25 m left"><ArrowIcon direction="left" size={12} /></Step>
                <div className={styles.barValue}>{`${(info.signShifts?.[i] ?? 0) >= 0 ? "+" : ""}${(info.signShifts?.[i] ?? 0).toFixed(2)} m`}</div>
                <Step action={`signShiftRight:${i}`} title="Move 0.25 m right"><ArrowIcon direction="right" size={12} /></Step>
              </div>
            ))}
            <div className={styles.barRow}>
              <div className={styles.barLabel}>Sign size</div>
              <Step action="smaller">-</Step>
              <div className={styles.barValue}>{`${Math.round((info.scale ?? 1) * 100)}%`}</div>
              <Step action="larger">+</Step>
              {info.free && overhead && (
                <>
                  <div className={styles.barLabel} style={{ marginLeft: "16rem" }}>Span</div>
                  <Step action="narrower">-</Step>
                  <div className={styles.barValue}>{`${(info.width ?? 14).toFixed(0)} m`}</div>
                  <Step action="wider">+</Step>
                </>
              )}
            </div>
          </>
        )}
        <div className={styles.actions}>
          <Button variant="flat" onSelect={props.onBack}><div className={styles.flatLabel}>Back to editor</div></Button>
        </div>
      </div>
    </PanelAny>
  );
};

/** The Sign Builder editor: templates and parts on the left, live preview in the middle, properties on the right. */
export const SignEditor = () => {
  const open = useValue(editorOpen$);
  const templatesJson = useValue(templates$);
  const editing = useValue(editingDesign$);
  const status = useValue(toolStatus$);
  const mode = useValue(toolMode$);
  const replacing = useValue(replacing$);
  // "Edit a placed sign": the editor shrinks so the world can be clicked; picking a sign restores it.
  const [picking, setPicking] = useState(false);
  useEffect(() => { if (mode === "off") setPicking(false); }, [mode]);
  const templates: Template[] = useMemo(() => {
    try { return JSON.parse(templatesJson); } catch { return []; }
  }, [templatesJson]);

  // One structure can carry several signs (left to right as seen by drivers); the editor works on one at a time.
  const [signs, setSigns] = useState<Design[]>([BLANK]);
  const [active, setActive] = useState(0);
  const current = Math.min(active, signs.length - 1);
  const design = signs[current];
  const setDesign = (d: Design) => setSigns(signs.map((s, i) => (i === current ? d : s)));
  const [selected, setSelected] = useState<string | null>(null);
  const [support, setSupport] = useState("truss");
  const [reverse, setReverse] = useState(false);
  const [free, setFree] = useState(false);

  // Re-editing a placed sign: the C# side pushes { designs, support, reverse, free } when it opens the editor.
  useEffect(() => {
    if (!editing) return;
    try {
      const payload = JSON.parse(editing);
      setSigns(payload.designs ?? [payload.design ?? payload]);
      setActive(0);
      if (payload.support) setSupport(payload.support);
      setReverse(!!payload.reverse);
      setFree(!!payload.free);
      setSelected(null);
      setPicking(false);
    } catch { /* ignore malformed payloads */ }
  }, [editing]);

  if (!open) return null;

  const close = () => { cancelTool(); setPicking(false); setEditorOpen(false); };
  const payload = () => JSON.stringify({ designs: signs, support, reverse: free ? false : reverse, free });

  // While placing (or picking a sign to edit) only a small bar stays on screen so the road is reachable.
  if (mode === "place" || (picking && mode === "pick")) {
    return (
      <PlacementBar picking={picking && mode === "pick"} status={status} onClose={close}
        onBack={() => { if (mode === "place") cancelTool(); setPicking(false); }} />
    );
  }

  const item = selected != null ? getItem(design, selected) : null;
  const update = (patch: Partial<Item>) => { if (selected != null) setDesign(updateItem(design, selected, patch)); };
  const apply = (r: { design: Design; path: string }) => { setDesign(r.design); setSelected(r.path); };
  const editTab = selected === "exitTab";
  const { structure, span } = parseSupport(support);

  return (
    <Panel draggable header="Sign Builder" className={styles.panel} onClose={close}>
      <div className={styles.body}>
        <div className={styles.column}>
          <div className={styles.heading}>Templates</div>
          {templates.map((t) => (
            <div key={t.id} className={styles.listItem}
              onClick={() => { setDesign(clone(t.design)); if (t.support) setSupport(t.support); setSelected(null); }}>{t.name}</div>
          ))}
          <div className={styles.listItem} onClick={() => { setDesign(clone(BLANK)); setSelected(null); }}>Blank sign</div>

          <div className={styles.heading}>Sign</div>
          <Field label="Colour"><Swatches value={design.scheme ?? "green"} onChange={(scheme) => setDesign({ ...design, scheme })} /></Field>
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

        <div className={styles.centre}>
          <div className={styles.choices}>
            {signs.map((s, i) => (
              <div key={i} className={i === current ? styles.choiceOn : styles.choice} onClick={() => { setActive(i); setSelected(null); }}>{`Sign ${i + 1}`}</div>
            ))}
            {signs.length < 4 && (
              <div className={styles.chip} onClick={() => { setSigns([...signs, clone(BLANK)]); setActive(signs.length); setSelected(null); }}>+ Add sign</div>
            )}
            {signs.length > 1 && (
              <div className={styles.chip} onClick={() => { setSigns(signs.filter((_, i) => i !== current)); setActive(Math.max(0, current - 1)); setSelected(null); }}>Remove sign</div>
            )}
          </div>
          <div className={styles.preview}>
            <SignPreview design={design} selected={selected} onSelect={setSelected} maxWidth={560} maxHeight={330} />
          </div>
          <div className={styles.hint}>Click any part of the sign to edit it.</div>
          <div className={styles.optionRow}>
            <div className={styles.optionGroup}>
              <div className={styles.heading}>Structure</div>
              <Choice options={STRUCTURES.map((x) => ({ value: x.id, label: x.label }))} value={structure}
                onChange={(v) => setSupport(supportId(v, span))} />
            </div>
          </div>
          {structure !== "ground" && (
            <div className={styles.optionRow}>
              <div className={styles.optionGroup}>
                <div className={styles.heading}>Span</div>
                <Choice options={SPANS.map((x) => ({ value: x.id, label: x.label, title: x.hint }))} value={span}
                  onChange={(v) => setSupport(supportId(structure, v))} />
              </div>
            </div>
          )}
          <div className={styles.optionRow}>
            <div className={styles.optionGroup}>
              <div className={styles.heading}>Placement</div>
              <Choice options={[{ value: "road", label: "Snap to road" }, { value: "free", label: "Free placement" }]}
                value={free ? "free" : "road"} onChange={(v) => setFree(v === "free")} />
            </div>
            {!free && (
              <div className={styles.optionGroup}>
                <div className={styles.heading}>Facing</div>
                <Choice options={[{ value: "forward", label: "Forward", title: "Faces the traffic on the clicked road" },
                  { value: "reverse", label: "Reverse", title: "Faces the opposite direction" }]}
                  value={reverse ? "reverse" : "forward"} onChange={(v) => setReverse(v === "reverse")} />
              </div>
            )}
          </div>
          <div className={styles.actions}>
            {replacing ? (
              <>
                <Button variant="primary" onSelect={() => saveEdit(payload())}>Save</Button>
                <div className={styles.chip} style={{ marginLeft: "12rem", alignSelf: "center" }} title="Keep the placed sign as it is; the next placement is a new sign"
                  onClick={() => forgetEdit()}>Stop editing</div>
              </>
            ) : (
              <>
                <Button variant="primary" onSelect={() => startPlacement(payload())}>{free ? "Place" : "Place on road"}</Button>
                <div className={styles.chip} style={{ marginLeft: "12rem", alignSelf: "center" }} onClick={() => { cancelTool(); setPicking(true); }}>Edit a placed sign</div>
              </>
            )}
          </div>
          {status && <div className={styles.status}>{status}</div>}
        </div>

        <div className={styles.column}>
          <div className={styles.heading}>Add part</div>
          <div className={styles.palette}>
            {PALETTE.map((p) => (
              <div key={p.label} className={styles.chip} onClick={() => apply(insertItem(design, selected, p.make()))}>{p.label}</div>
            ))}
          </div>
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

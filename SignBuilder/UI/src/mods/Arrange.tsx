import { useValue } from "cs2/api";
import { Dialog } from "./Dialog";
import { ReactNode, useEffect, useMemo, useRef, useState } from "react";
import { adjust, arrange, arrangeInfo$, toolStatus$ } from "./bindings";
import { Choice } from "./Controls";
import { Design, SPANS, STRUCTURES } from "./designModel";
import { ArrowIcon } from "./Icons";
import { SignPreview } from "./SignPreview";
import styles from "./SignEditor.module.scss";

type Sign = { index: number; x: number; w: number; h: number; ready: boolean; mode: string; lane: number; fromRight: boolean; offset: number; design: Design };
type Info = {
  lanes: number[]; allLanes: [number, boolean][]; deck: number[]; roads: [number, number, boolean][]; structure: number[]; posts: number[]; signs: Sign[];
  type: string; span: string; side: string; reverse: boolean; free: boolean; scale: number; lift: number; lights: boolean; gap: number;
  view: string; otherSide: number; total: number;
  angle: number; elevation: number; width: number; replacing: boolean; busy: boolean; dirty: boolean;
};


const nth = (n: number) => (n === 2 ? "2nd" : n === 3 ? "3rd" : `${n}th`);
/** "Rightmost lane", "2nd lane from left", "+1.25 m from center", "With others". */
export function anchorLabel(s: Sign): string {
  const shift = s.offset ? `, ${s.offset >= 0 ? "+" : ""}${s.offset.toFixed(2)} m` : "";
  if (s.mode === "lane") return (s.lane === 0 ? (s.fromRight ? "Rightmost lane" : "Leftmost lane") : `${nth(s.lane + 1)} lane from ${s.fromRight ? "right" : "left"}`) + shift;
  if (s.mode === "free") return `${s.offset >= 0 ? "+" : ""}${s.offset.toFixed(2)} m from center`;
  return "With others" + shift;
}

/**
 * The gantry arrangement panel, docked at the bottom of the screen: a to-scale view of the structure over the
 * lanes it crosses, as drivers see it. Drag signs sideways (they snap to lane centers); pick the structure; Build
 * makes it at the pinned road spot.
 */
export const Arrange = (props: {
  onEditSign: (index: number, design: Design) => void;
  onAddSign: () => void;
  /** Close (X) or Cancel; the argument says whether anything would be lost. */
  onClose: (unsaved: boolean) => void;
  onCancel: (unsaved: boolean) => void;
  overlay?: ReactNode;
}) => {
  const json = useValue(arrangeInfo$);
  const status = useValue(toolStatus$);
  const info: Info | null = useMemo(() => { try { return json ? JSON.parse(json) : null; } catch { return null; } }, [json]);
  const [selected, setSelected] = useState(0);
  const [drag, setDrag] = useState<{ index: number; x: number } | null>(null);
  const area = useRef<HTMLDivElement>(null);
  const [areaWidth, setAreaWidth] = useState(1100);
  useEffect(() => {
    const w = area.current?.clientWidth;
    if (w && Math.abs(w - areaWidth) > 2) setAreaWidth(w);
  });

  if (!info) {
    return (
      <Dialog dock="bottom" title="Gantry" className={styles.arrange} onClose={() => props.onClose(true)}>
        <div className={styles.compactBody}><div className={styles.status}>{status || "Preparing..."}</div></div>
      </Dialog>
    );
  }

  const signs = info.signs;
  // The selected sign, by its index among all signs (this side shows only some of them).
  const selPos = Math.max(0, signs.findIndex((s) => s.index === selected));
  const sel = Math.min(selPos, Math.max(0, signs.length - 1));
  const xOf = (i: number) => (drag && drag.index === i ? drag.x : signs[i].x);
  const allLanes = info.allLanes ?? info.lanes.map((x) => [x, true] as [number, boolean]);
  // Visible range: deck, structure and signs, with a margin.
  const lefts = [info.deck[0], info.structure[0], ...info.roads.map((r) => r[0]), ...signs.map((s, i) => xOf(i) - s.w / 2)];
  const rights = [info.deck[1], info.structure[1], ...info.roads.map((r) => r[1]), ...signs.map((s, i) => xOf(i) + s.w / 2)];
  const min = Math.min(...lefts) - 1.5;
  const max = Math.max(...rights) + 1.5;
  const maxH = Math.max(2.5, ...signs.map((s) => s.h));
  const scale = Math.min(areaWidth / (max - min), 100 / maxH);
  // Centered when the height limits the scale (the structure is then narrower than the view).
  const left = Math.max(0, (areaWidth - (max - min) * scale) / 2);
  const px = (x: number) => left + (x - min) * scale;
  const beamY = 12 + (maxH * scale) / 2;
  const roadY = beamY + (maxH * scale) / 2 + 26;
  const height = roadY + 30;
  const nearest = (xs: number[], x: number) => (xs.length ? xs.reduce((b, l, k) => (Math.abs(l - x) < Math.abs(xs[b] - x) ? k : b), 0) : -1);
  const nearestLane = (x: number) => nearest(info.lanes, x);

  const startDrag = (e: any, i: number) => {
    setSelected(signs[i].index);
    const startX = e.clientX;
    const origin = signs[i].x;
    let last = 0;
    let x = origin;
    const onMove = (ev: MouseEvent) => {
      x = origin + (ev.clientX - startX) / scale;
      setDrag({ index: i, x });
      const now = Date.now();
      if (now - last > 120) {
        last = now;
        arrange({ op: "moveSign", index: signs[i].index, x, snap: false });
      }
    };
    const onUp = (ev: MouseEvent) => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      if (Math.abs(ev.clientX - startX) > 3) arrange({ op: "moveSign", index: signs[i].index, x, snap: true });
      setTimeout(() => setDrag(null), 150);
    };
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  };

  const laneStep = (i: number, delta: number) => {
    const n = info.lanes.length;
    if (!n) return;
    const s = signs[i];
    const current = s.mode === "lane" ? (s.fromRight ? n - 1 - s.lane : s.lane) : nearestLane(s.x);
    const next = Math.max(0, Math.min(n - 1, current + delta));
    // Nearer edge; the middle lane of an odd count counts from the right (where exits usually are).
    const fromRight = next >= (n - 1) / 2;
    arrange({ op: "slot", index: s.index, mode: "lane", lane: fromRight ? n - 1 - next : next, fromRight, offset: 0 });
  };
  const Step = (p: { onClick: () => void; children: any; title?: string }) => <div className={styles.step} title={p.title} onClick={p.onClick}>{p.children}</div>;
  const Label = (p: { children: any }) => <div className={styles.barLabel} style={{ marginLeft: "16rem" }}>{p.children}</div>;
  const laneXs = allLanes.map((l) => l[0]);
  const dragLane = drag ? nearest(laneXs, drag.x) : -1;
  const snapping = drag && dragLane >= 0 && Math.abs(laneXs[dragLane] - drag.x) < 0.75;
  // Lanes in order across the view; markings between neighbors on the same road.
  const ordered = allLanes.map((l, k) => ({ x: l[0], faces: l[1], k })).sort((a, b) => a.x - b.x);
  const unsaved = info.dirty !== false;
  const ready = info.total > 0 && !info.busy && !signs.some((s) => !s.ready);

  return (
    <Dialog dock="bottom" title={info.replacing ? "Edit gantry" : "Gantry"} className={styles.arrange} onClose={() => props.onClose(unsaved)}
      header={
        <>
          <div className={ready ? styles.primaryButton : styles.primaryButtonOff} onClick={() => ready && arrange({ op: "build" })}>{info.replacing ? "Save" : "Build"}</div>
          <div className={styles.chip} style={{ alignSelf: "center" }} onClick={() => arrange({ op: "auto" })}>Auto arrange</div>
          <div className={styles.chip} style={{ alignSelf: "center" }} title="Click another spot on a road to move the gantry there" onClick={() => arrange({ op: "repick" })}>Move gantry</div>
          <div className={styles.chip} style={{ alignSelf: "center" }} onClick={() => props.onCancel(unsaved)}>Cancel</div>
          <Label>Showing</Label>
          <Choice options={[
            { value: "front", label: `Front (${info.view === "back" ? info.otherSide : signs.length})` },
            { value: "back", label: `Back (${info.view === "back" ? signs.length : info.otherSide})`, title: "Signs on the other face of the structure, for traffic the other way" },
          ]} value={info.view} onChange={(value) => { arrange({ op: "view", value }); setSelected(-1); }} />
          {status && <div className={styles.status} style={{ marginLeft: "12rem" }}>{status}</div>}
        </>
      }>
      <div className={styles.arrangeBody}>
        <div className={styles.barRow}>
          <div className={styles.barLabel}>Structure</div>
          <Choice options={STRUCTURES.map((s) => ({ value: s.id, label: s.label }))} value={info.type} onChange={(value) => arrange({ op: "type", value })} />
          <Label>Type</Label>
          <Choice options={SPANS.map((s) => ({ value: s.id, label: s.label, title: s.hint }))} value={info.span} onChange={(value) => arrange({ op: "span", value })} />
          {info.span === "cantilever" && (
            <Choice options={[{ value: "left", label: "Post left" }, { value: "right", label: "Post right" }]} value={info.side}
              onChange={(value) => arrange({ op: "side", value })} />
          )}
          {!info.free && (
            <div className={styles.chip} style={{ alignSelf: "center", marginLeft: "12rem" }} title="Face the signs the other way"
              onClick={() => arrange({ op: "swap" })}>Swap directions</div>
          )}
        </div>
        <div className={styles.barRow}>
          <div className={styles.barLabel}>Height</div>
          <Step title="Lower (Ctrl + mouse wheel)" onClick={() => arrange({ op: "lift", value: -0.25 })}><ArrowIcon direction="down" size={12} /></Step>
          <div className={styles.barValueSmall}>{`${info.lift >= 0 ? "+" : ""}${info.lift.toFixed(2)} m`}</div>
          <Step title="Raise (Ctrl + mouse wheel)" onClick={() => arrange({ op: "lift", value: 0.25 })}><ArrowIcon direction="up" size={12} /></Step>
          {!info.free && (
            <>
              <Label>Posts from road</Label>
              <Step title="Closer (Alt + mouse wheel)" onClick={() => adjust("closer")}>-</Step>
              <div className={styles.barValueSmall}>{`${(info.gap ?? 2).toFixed(2)} m`}</div>
              <Step title="Farther (Alt + mouse wheel)" onClick={() => adjust("farther")}>+</Step>
            </>
          )}
          <Label>Sign size</Label>
          <Step onClick={() => arrange({ op: "scale", value: -1 })}>-</Step>
          <div className={styles.barValueSmall}>{`${Math.round(info.scale * 100)}%`}</div>
          <Step onClick={() => arrange({ op: "scale", value: 1 })}>+</Step>
          <Label>Lights</Label>
          <Choice options={[{ value: "off", label: "Off" }, { value: "on", label: "On", title: "Light fixtures on arms below each sign; the signs glow brighter at night" }]}
            value={info.lights ? "on" : "off"} onChange={(v) => arrange({ op: "lights", value: v === "on" })} />
          {info.free && (
            <>
              <Label>Rotate</Label>
              <Step onClick={() => adjust("rotateLeft")}>-15°</Step>
              <div className={styles.barValueSmall}>{`${Math.round(info.angle)}°`}</div>
              <Step onClick={() => adjust("rotateRight")}>+15°</Step>
              <Label>Base</Label>
              <Step onClick={() => adjust("lower")}><ArrowIcon direction="down" size={12} /></Step>
              <div className={styles.barValueSmall}>{`${info.elevation >= 0 ? "+" : ""}${info.elevation.toFixed(2)} m`}</div>
              <Step onClick={() => adjust("raise")}><ArrowIcon direction="up" size={12} /></Step>
              <Label>Road width</Label>
              <Step onClick={() => adjust("narrower")}>-</Step>
              <div className={styles.barValueSmall}>{`${info.width.toFixed(0)} m`}</div>
              <Step onClick={() => adjust("wider")}>+</Step>
            </>
          )}
        </div>

        <div ref={area} className={styles.diagram} style={{ height: height + "px" }}>
          {(info.roads.length ? info.roads : [[info.deck[0], info.deck[1], true] as [number, number, boolean]]).map((r, k) => (
            <div key={`r${k}`} className={r[2] ? styles.deck : styles.deckOther}
              style={{ left: px(r[0]) + "px", width: (r[1] - r[0]) * scale + "px", top: roadY + "px" }} />
          ))}
          {ordered.slice(1).map((l, k) => Math.abs(l.x - ordered[k].x) < 5.5 && (
            <div key={`d${k}`} className={ordered[k].faces === l.faces ? styles.laneLine : styles.laneLineCenter}
              style={{ left: px((l.x + ordered[k].x) / 2) - 1 + "px", top: roadY + "px" }} />
          ))}
          {allLanes.map((l, k) => {
            const faced = info.lanes.findIndex((x) => Math.abs(x - l[0]) < 0.01);
            return (
              <div key={k} className={k === dragLane && snapping ? styles.laneOn : l[1] ? styles.lane : styles.laneOther}
                style={{ left: px(l[0]) - 18 + "px", top: roadY + 3 + "px" }}
                title={l[1] ? "A lane this side's signs face" : "Traffic the other way"}>
                <ArrowIcon direction={l[1] ? "up" : "down"} size={10} color={k === dragLane && snapping ? "#ffd400" : "rgba(255,255,255,0.8)"} />
                {faced >= 0 && <span style={{ marginLeft: "2px" }}>{`${faced + 1}`}</span>}
              </div>
            );
          })}
          <div className={styles.beam} style={{ left: px(info.structure[0]) + "px", width: (info.structure[1] - info.structure[0]) * scale + "px", top: beamY - 3 + "px" }} />
          {info.posts.map((p, k) => (
            <div key={`p${k}`} className={styles.post} style={{ left: px(p) - 3 + "px", top: beamY + "px", height: roadY - beamY + "px" }} />
          ))}
          {signs.map((s, i) => (
            <div key={s.index} className={i === sel ? styles.signBoxOn : styles.signBox}
              style={{ left: px(xOf(i) - s.w / 2) + "px", top: beamY - (s.h * scale) / 2 + "px", width: s.w * scale + "px", height: s.h * scale + "px" }}
              onMouseDown={(e) => startDrag(e, i)}>
              <SignPreview design={s.design} interactive={false} maxWidth={s.w * scale} maxHeight={s.h * scale} padding={0} />
            </div>
          ))}
        </div>
        {info.span !== "cantilever" && <div className={styles.hint}>Click a road to add it to the gantry's span, or click it again to remove it.</div>}

        <div className={styles.barRow}>
          <div className={styles.chip} style={{ alignSelf: "center", marginRight: "14rem" }} onClick={props.onAddSign}>Add sign</div>
          {signs.length > 0 && (
          <>
            <div className={styles.barLabel}>{`Sign ${sel + 1} of ${signs.length}${info.view === "back" ? " (back)" : ""}:`}</div>
            <div className={styles.barValue}>{anchorLabel(signs[sel])}</div>
            <div className={styles.chip} onClick={() => arrange({ op: "slot", index: signs[sel].index, mode: "auto", offset: 0 })}>With others</div>
            {info.lanes.length > 0 && (
              <>
                <Step title="One lane left" onClick={() => laneStep(sel, -1)}><ArrowIcon direction="left" size={12} /></Step>
                <div className={styles.barLabel}>lane</div>
                <Step title="One lane right" onClick={() => laneStep(sel, 1)}><ArrowIcon direction="right" size={12} /></Step>
              </>
            )}
            <div className={styles.barLabel} style={{ marginLeft: "12rem" }}>Nudge</div>
            <Step onClick={() => arrange({ op: "nudge", index: signs[sel].index, value: -0.25 })}><ArrowIcon direction="left" size={12} /></Step>
            <Step onClick={() => arrange({ op: "nudge", index: signs[sel].index, value: 0.25 })}><ArrowIcon direction="right" size={12} /></Step>
            <div className={styles.chip} style={{ marginLeft: "12rem" }} onClick={() => props.onEditSign(signs[sel].index, signs[sel].design)}>Edit sign</div>
            <div className={styles.chip} onClick={() => arrange({ op: "duplicateSign", index: signs[sel].index })}>Duplicate</div>
            <div className={styles.chip} onClick={() => { arrange({ op: "removeSign", index: signs[sel].index }); setSelected(0); }}>Remove</div>
          </>
          )}
        </div>
        {props.overlay}
      </div>
    </Dialog>
  );
};

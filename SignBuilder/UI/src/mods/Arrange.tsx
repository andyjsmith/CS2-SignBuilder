import { useValue } from "cs2/api";
import { Button, Panel } from "cs2/ui";
import { useEffect, useMemo, useRef, useState } from "react";
import { adjust, arrange, arrangeInfo$, toolStatus$ } from "./bindings";
import { Choice } from "./Controls";
import { Design, SPANS, STRUCTURES } from "./designModel";
import { ArrowIcon } from "./Icons";
import { SignPreview } from "./SignPreview";
import styles from "./SignEditor.module.scss";

type Sign = { x: number; w: number; h: number; ready: boolean; mode: string; lane: number; fromRight: boolean; offset: number; design: Design };
type Info = {
  lanes: number[]; deck: number[]; structure: number[]; posts: number[]; signs: Sign[];
  type: string; span: string; side: string; reverse: boolean; free: boolean; scale: number;
  angle: number; elevation: number; width: number; replacing: boolean; busy: boolean;
};

const nth = (n: number) => (n === 2 ? "2nd" : n === 3 ? "3rd" : `${n}th`);
/** "Rightmost lane", "2nd lane from left", "+1.25 m from centre", "With others". */
export function anchorLabel(s: Sign): string {
  const shift = s.offset ? `, ${s.offset >= 0 ? "+" : ""}${s.offset.toFixed(2)} m` : "";
  if (s.mode === "lane") return (s.lane === 0 ? (s.fromRight ? "Rightmost lane" : "Leftmost lane") : `${nth(s.lane + 1)} lane from ${s.fromRight ? "right" : "left"}`) + shift;
  if (s.mode === "free") return `${s.offset >= 0 ? "+" : ""}${s.offset.toFixed(2)} m from centre`;
  return "With others" + shift;
}

/**
 * The gantry arrangement panel: a to-scale view of the structure over the lanes it faces, as drivers see it.
 * Drag signs sideways (they snap to lane centres); pick the structure; Build makes it at the pinned road spot.
 */
export const Arrange = (props: { onEditSign: (index: number, design: Design) => void; onAddSign: () => void; onClose: () => void }) => {
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

  const PanelAny: any = Panel;
  if (!info) {
    return (
      <PanelAny draggable initialPosition={{ x: 0.5, y: 0.05 }} header="Gantry" className={styles.arrange} onClose={props.onClose}>
        <div className={styles.compactBody}><div className={styles.status}>{status || "Preparing..."}</div></div>
      </PanelAny>
    );
  }

  const signs = info.signs;
  const sel = Math.min(selected, Math.max(0, signs.length - 1));
  const xOf = (i: number) => (drag && drag.index === i ? drag.x : signs[i].x);
  // Visible range: deck, structure and signs, with a margin.
  const lefts = [info.deck[0], info.structure[0], ...signs.map((s, i) => xOf(i) - s.w / 2)];
  const rights = [info.deck[1], info.structure[1], ...signs.map((s, i) => xOf(i) + s.w / 2)];
  const min = Math.min(...lefts) - 1.5;
  const max = Math.max(...rights) + 1.5;
  const maxH = Math.max(2.5, ...signs.map((s) => s.h));
  const scale = Math.min(areaWidth / (max - min), 150 / maxH);
  const px = (x: number) => (x - min) * scale;
  const beamY = 20 + (maxH * scale) / 2;
  const roadY = beamY + (maxH * scale) / 2 + 34;
  const height = roadY + 34;
  const nearestLane = (x: number) => (info.lanes.length ? info.lanes.reduce((b, l, k) => (Math.abs(l - x) < Math.abs(info.lanes[b] - x) ? k : b), 0) : -1);

  const startDrag = (e: any, i: number) => {
    setSelected(i);
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
        arrange({ op: "moveSign", index: i, x, snap: false });
      }
    };
    const onUp = (ev: MouseEvent) => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      if (Math.abs(ev.clientX - startX) > 3) arrange({ op: "moveSign", index: i, x, snap: true });
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
    const fromRight = next > (n - 1) / 2;
    arrange({ op: "slot", index: i, mode: "lane", lane: fromRight ? n - 1 - next : next, fromRight, offset: 0 });
  };
  const Step = (p: { onClick: () => void; children: any; title?: string }) => <div className={styles.step} title={p.title} onClick={p.onClick}>{p.children}</div>;
  const dragLane = drag ? nearestLane(drag.x) : -1;
  const snapping = drag && dragLane >= 0 && Math.abs(info.lanes[dragLane] - drag.x) < 0.75;

  return (
    <PanelAny draggable initialPosition={{ x: 0.5, y: 0.05 }} header={info.replacing ? "Edit gantry" : "Gantry"} className={styles.arrange} onClose={props.onClose}>
      <div className={styles.arrangeBody}>
        <div className={styles.barRow}>
          <Choice options={STRUCTURES.map((s) => ({ value: s.id, label: s.label }))} value={info.type} onChange={(value) => arrange({ op: "type", value })} />
          <div className={styles.barGap} />
          <Choice options={SPANS.map((s) => ({ value: s.id, label: s.label, title: s.hint }))} value={info.span} onChange={(value) => arrange({ op: "span", value })} />
          {info.span === "cantilever" && (
            <>
              <div className={styles.barGap} />
              <Choice options={[{ value: "right", label: "Post on right" }, { value: "left", label: "Post on left" }]} value={info.side}
                onChange={(value) => arrange({ op: "side", value })} />
            </>
          )}
        </div>
        <div className={styles.barRow}>
          {!info.free && (
            <Choice options={[{ value: "forward", label: "Forward", title: "Faces the clicked road's traffic" }, { value: "reverse", label: "Reverse", title: "Faces the other direction" }]}
              value={info.reverse ? "reverse" : "forward"} onChange={(v) => arrange({ op: "reverse", value: v === "reverse" })} />
          )}
          <div className={styles.barLabel} style={{ marginLeft: "16rem" }}>Sign size</div>
          <Step onClick={() => arrange({ op: "scale", value: -1 })}>-</Step>
          <div className={styles.barValueSmall}>{`${Math.round(info.scale * 100)}%`}</div>
          <Step onClick={() => arrange({ op: "scale", value: 1 })}>+</Step>
          {info.free && (
            <>
              <div className={styles.barLabel} style={{ marginLeft: "16rem" }}>Rotate</div>
              <Step onClick={() => adjust("rotateLeft")}>-15°</Step>
              <div className={styles.barValueSmall}>{`${Math.round(info.angle)}°`}</div>
              <Step onClick={() => adjust("rotateRight")}>+15°</Step>
              <div className={styles.barLabel} style={{ marginLeft: "16rem" }}>Height</div>
              <Step onClick={() => adjust("lower")}><ArrowIcon direction="down" size={12} /></Step>
              <div className={styles.barValueSmall}>{`${info.elevation.toFixed(1)} m`}</div>
              <Step onClick={() => adjust("raise")}><ArrowIcon direction="up" size={12} /></Step>
              <div className={styles.barLabel} style={{ marginLeft: "16rem" }}>Road width</div>
              <Step onClick={() => adjust("narrower")}>-</Step>
              <div className={styles.barValueSmall}>{`${info.width.toFixed(0)} m`}</div>
              <Step onClick={() => adjust("wider")}>+</Step>
            </>
          )}
        </div>

        <div ref={area} className={styles.diagram} style={{ height: height + "px" }}>
          <div className={styles.deck} style={{ left: px(info.deck[0]) + "px", width: (info.deck[1] - info.deck[0]) * scale + "px", top: roadY + "px" }} />
          {info.lanes.map((l, k) => (
            <div key={k} className={k === dragLane && snapping ? styles.laneOn : styles.lane}
              style={{ left: px(l) - 18 + "px", top: roadY + 4 + "px" }}>{`L${k + 1}`}</div>
          ))}
          {info.lanes.slice(1).map((l, k) => (
            <div key={`d${k}`} className={styles.laneLine} style={{ left: px((l + info.lanes[k]) / 2) + "px", top: roadY + "px" }} />
          ))}
          <div className={styles.beam} style={{ left: px(info.structure[0]) + "px", width: (info.structure[1] - info.structure[0]) * scale + "px", top: beamY - 3 + "px" }} />
          {info.posts.map((p, k) => (
            <div key={`p${k}`} className={styles.post} style={{ left: px(p) - 3 + "px", top: beamY + "px", height: roadY - beamY + "px" }} />
          ))}
          {signs.map((s, i) => (
            <div key={i} className={i === sel ? styles.signBoxOn : styles.signBox}
              style={{ left: px(xOf(i) - s.w / 2) + "px", top: beamY - (s.h * scale) / 2 + "px", width: s.w * scale + "px", height: s.h * scale + "px" }}
              onMouseDown={(e) => startDrag(e, i)}>
              <SignPreview design={s.design} interactive={false} maxWidth={s.w * scale} maxHeight={s.h * scale} padding={0} />
            </div>
          ))}
        </div>
        <div className={styles.hint}>Drag signs sideways; they snap to the lane centres (remembered from the nearer edge).</div>

        {signs.length > 0 && (
          <div className={styles.barRow}>
            <div className={styles.barLabel}>{`Sign ${sel + 1} of ${signs.length}:`}</div>
            <div className={styles.barValue}>{anchorLabel(signs[sel])}</div>
            <div className={styles.chip} onClick={() => arrange({ op: "slot", index: sel, mode: "auto", offset: 0 })}>With others</div>
            {info.lanes.length > 0 && (
              <>
                <Step title="One lane left" onClick={() => laneStep(sel, -1)}><ArrowIcon direction="left" size={12} /></Step>
                <div className={styles.barLabel}>lane</div>
                <Step title="One lane right" onClick={() => laneStep(sel, 1)}><ArrowIcon direction="right" size={12} /></Step>
              </>
            )}
            <div className={styles.barLabel} style={{ marginLeft: "12rem" }}>Nudge</div>
            <Step onClick={() => arrange({ op: "nudge", index: sel, value: -0.25 })}><ArrowIcon direction="left" size={12} /></Step>
            <Step onClick={() => arrange({ op: "nudge", index: sel, value: 0.25 })}><ArrowIcon direction="right" size={12} /></Step>
            <div className={styles.chip} style={{ marginLeft: "12rem" }} onClick={() => props.onEditSign(sel, signs[sel].design)}>Edit sign</div>
            <div className={styles.chip} onClick={() => { arrange({ op: "removeSign", index: sel }); setSelected(Math.max(0, sel - 1)); }}>Remove</div>
          </div>
        )}

        <div className={styles.actions}>
          <div style={{ marginRight: "10rem" }}>
            <Button variant="primary" disabled={signs.length === 0 || info.busy || signs.some((s) => !s.ready)} onSelect={() => arrange({ op: "build" })}>
              {info.replacing ? "Save" : "Build"}
            </Button>
          </div>
          <div className={styles.chip} style={{ alignSelf: "center" }} onClick={props.onAddSign}>Add sign</div>
          <div className={styles.chip} style={{ alignSelf: "center" }} onClick={() => arrange({ op: "auto" })}>Auto arrange</div>
          <div className={styles.chip} style={{ alignSelf: "center" }} onClick={() => arrange({ op: "repick" })}>Pick another spot</div>
          <div className={styles.chip} style={{ alignSelf: "center" }} onClick={() => arrange({ op: "cancel" })}>Cancel</div>
        </div>
        {status && <div className={styles.status}>{status}</div>}
      </div>
    </PanelAny>
  );
};

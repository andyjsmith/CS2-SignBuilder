import { useValue } from "cs2/api";
import { Button, Panel } from "cs2/ui";
import { useMemo } from "react";
import { adjust, placeInfo$ } from "./bindings";
import { Choice } from "./Controls";
import { ArrowIcon } from "./Icons";
import styles from "./SignEditor.module.scss";

type PlaceInfo = { free?: boolean; angle?: number; elevation?: number; width?: number; scale?: number; posts?: boolean };

/** The small bar at the top of the screen while placing a roadside sign or choosing where a gantry goes. */
export const PlacementBar = (props: { status: string; onBack: () => void; onClose: () => void }) => {
  const infoJson = useValue(placeInfo$);
  const info: PlaceInfo = useMemo(() => { try { return JSON.parse(infoJson); } catch { return {}; } }, [infoJson]);
  const Step = (p: { action: string; children: any; title?: string }) => (
    <div className={styles.step} title={p.title} onClick={() => adjust(p.action)}>{p.children}</div>
  );
  const PanelAny: any = Panel;
  return (
    <PanelAny draggable initialPosition={{ x: 0.5, y: 0.05 }} header={info.posts ? "Place standalone sign" : "Place gantry"} className={styles.compact} onClose={props.onClose}>
      <div className={styles.compactBody}>
        <div className={styles.status}>{props.status}</div>
        {info.free && <div className={styles.hint}>Right-drag to rotate, Page Up / Page Down to raise and lower.</div>}
        <div className={styles.barRow}>
          <Choice options={[{ value: "road", label: "Snap to road" }, { value: "free", label: "Free placement" }]}
            value={info.free ? "free" : "road"} onChange={(v) => adjust(v)} />
        </div>
        {info.free && (
          <div className={styles.barRow}>
            <div className={styles.barLabel}>Rotate</div>
            <Step action="rotateLeft" title="Rotate left (or right-drag)">-15°</Step>
            <div className={styles.barValueSmall}>{`${Math.round(info.angle ?? 0)}°`}</div>
            <Step action="rotateRight" title="Rotate right (or right-drag)">+15°</Step>
            <div className={styles.barLabel} style={{ marginLeft: "16rem" }}>Height</div>
            <Step action="lower" title="Lower (Page Down)"><ArrowIcon direction="down" size={12} /></Step>
            <div className={styles.barValueSmall}>{`${(info.elevation ?? 0).toFixed(1)} m`}</div>
            <Step action="raise" title="Raise (Page Up)"><ArrowIcon direction="up" size={12} /></Step>
          </div>
        )}
        {info.posts && (
          <div className={styles.barRow}>
            <div className={styles.barLabel}>Sign size</div>
            <Step action="smaller">-</Step>
            <div className={styles.barValueSmall}>{`${Math.round((info.scale ?? 1) * 100)}%`}</div>
            <Step action="larger">+</Step>
          </div>
        )}
        <div className={styles.actions}>
          <Button variant="flat" onSelect={props.onBack}><div className={styles.flatLabel}>Back to editor</div></Button>
        </div>
      </div>
    </PanelAny>
  );
};

import { useRef, useState } from "react";
import { Design, DropPosition, describe, getItem, moveTo, outline } from "./designModel";
import { TrashIcon } from "./Icons";
import styles from "./SignEditor.module.scss";

type Drop = { path: string; position: DropPosition } | null;

/**
 * The sign's parts as an indented list. Click selects; drag a part (press and move) to reorder it, or drop it
 * onto a Row to put it in that row. Drag it back out of a row to separate it again.
 */
export const PartsList = (props: {
  design: Design;
  selected: string | null;
  onSelect: (path: string | null) => void;
  onChange: (design: Design, selected: string) => void;
  labels?: Record<string, string>;
  onDelete: (path: string) => void;
}) => {
  const listRef = useRef<HTMLDivElement>(null);
  const [dragging, setDragging] = useState<string | null>(null);
  const [drop, setDrop] = useState<Drop>(null);
  const exitTab = props.design.exitTab;

  const targetAt = (y: number, from: string): Drop => {
    const rows = Array.from(listRef.current?.querySelectorAll("[data-part-path]") ?? []) as HTMLElement[];
    let best: Drop = null;
    for (const row of rows) {
      const path = row.getAttribute("data-part-path")!;
      const r = row.getBoundingClientRect();
      if (y < r.top || y > r.bottom) continue;
      if (path === from || path.startsWith(from + ".")) return null;
      const item = getItem(props.design, path);
      const f = (y - r.top) / Math.max(1, r.height);
      if (item?.items && f > 0.25 && f < 0.75) best = { path, position: "inside" };
      else best = { path, position: f < 0.5 ? "before" : "after" };
    }
    if (!best && rows.length > 0) {
      const last = rows[rows.length - 1];
      if (y > last.getBoundingClientRect().bottom) best = { path: last.getAttribute("data-part-path")!, position: "after" };
    }
    return best;
  };

  // Delete button at the right end of a part's row (it must not start a drag or select the part).
  const Delete = (p: { path: string }) => (
    <div className={styles.rowDelete} title="Delete this part" onMouseDown={(e) => e.stopPropagation()}
      onClick={(e) => { e.stopPropagation(); props.onDelete(p.path); }}><TrashIcon /></div>
  );

  const startDrag = (e: any, path: string) => {
    const startY = e.clientY;
    let moved = false;
    let current: Drop = null;
    const onMove = (ev: MouseEvent) => {
      if (!moved && Math.abs(ev.clientY - startY) < 5) return;
      moved = true;
      setDragging(path);
      current = targetAt(ev.clientY, path);
      setDrop(current);
    };
    const onUp = () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      setDragging(null);
      setDrop(null);
      if (!moved) {
        props.onSelect(path);
        return;
      }
      if (current) {
        // Dropping onto a part nested at the top of the list "after" the root's last item works the same way.
        const r = moveTo(props.design, path, current.path, current.position);
        props.onChange(r.design, r.path);
      }
    };
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  };

  return (
    <div ref={listRef}>
      {exitTab && (
        <div className={props.selected === "exitTab" ? styles.listItemOn : styles.listItem} onClick={() => props.onSelect("exitTab")}>
          <div className={styles.rowLabel}>{`Exit tab "${exitTab.text}"`}</div>
          <Delete path="exitTab" />
        </div>
      )}
      {outline(props.design).map(({ path, item, depth }) => {
        const marker = drop && drop.path === path ? drop.position : null;
        const cls = path === props.selected ? styles.listItemOn : styles.listItem;
        return (
          <div key={path} data-part-path={path}
            className={`${cls} ${marker === "inside" ? styles.dropInside : ""} ${dragging === path ? styles.dragging : ""}`}
            style={{
              paddingLeft: 8 + depth * 14 + "rem",
              borderTop: marker === "before" ? "2rem solid #ffd400" : undefined,
              borderBottom: marker === "after" ? "2rem solid #ffd400" : undefined,
            }}
            onMouseDown={(e) => startDrag(e, path)}>
            <svg className={styles.grip} width="8rem" height="12rem" viewBox="0 0 8 12">
              {[0, 1, 2].map((r) => [0, 1].map((c) => <rect key={`${r}${c}`} x={c * 5} y={r * 5} width="3" height="2" fill="rgba(255,255,255,0.5)" />))}
            </svg>
            <div className={styles.rowLabel}>
              {describe(item, props.labels)}
              {item.items && item.items.length === 0 && <span className={styles.hintInline}> drop parts here</span>}
            </div>
            <Delete path={path} />
          </div>
        );
      })}
      <div className={styles.hint}>Drag parts to reorder them, or onto a Row to put them side by side.</div>
    </div>
  );
};

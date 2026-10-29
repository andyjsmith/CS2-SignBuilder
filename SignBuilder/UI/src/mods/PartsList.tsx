import { useRef, useState } from "react";
import { Design, DropPosition, describe, getItem, moveTo, outline } from "./designModel";
import { TrashIcon } from "./Icons";
import styles from "./SignEditor.module.scss";

// <path>/<position>: where the part goes; <shown>: the row the drop line is drawn on.
type Drop = { path: string; position: DropPosition; shown: string } | null;

/**
 * The sign's parts as an indented list. Click selects; drag a part (press and move) to reorder it, or drop it
 * onto a Row or Column to put it inside. Below the last part of a row or column, the pointer's indent picks the
 * level: further left drops it after the row or column instead of inside it.
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

  const rowOf = (path: string) => listRef.current?.querySelector(`[data-part-path="${path}"]`) as HTMLElement | null;
  const gripLeft = (path: string) => (rowOf(path)?.querySelector("svg") as Element | null)?.getBoundingClientRect().left ?? 0;
  const parentOf = (path: string) => path.split(".").slice(0, -1).join(".");
  const isLast = (path: string) => {
    const parent = parentOf(path);
    const siblings = parent === "" ? props.design.items : getItem(props.design, parent)?.items ?? [];
    return Number(path.split(".").pop()) === siblings.length - 1;
  };
  /** "After" a part that ends its row/column: climbs out while the pointer is left of that level's indent. */
  const climb = (path: string, x: number) => {
    let target = path;
    while (target.includes(".") && isLast(target) && x < gripLeft(target) - 4) target = parentOf(target);
    return target;
  };

  const targetAt = (x: number, y: number, from: string): Drop => {
    const rows = Array.from(listRef.current?.querySelectorAll("[data-part-path]") ?? []) as HTMLElement[];
    let best: Drop = null;
    for (const row of rows) {
      const path = row.getAttribute("data-part-path")!;
      const r = row.getBoundingClientRect();
      if (y < r.top || y > r.bottom) continue;
      if (path === from || path.startsWith(from + ".")) return null;
      const item = getItem(props.design, path);
      const f = (y - r.top) / Math.max(1, r.height);
      if (item?.items && f > 0.25 && f < 0.75) best = { path, position: "inside", shown: path };
      else if (f < 0.5) best = { path, position: "before", shown: path };
      else best = { path: climb(path, x), position: "after", shown: path };
    }
    if (!best && rows.length > 0) {
      // Below the list: the end of the sign itself, outside every row and column.
      const last = rows[rows.length - 1];
      if (y > last.getBoundingClientRect().bottom) best = { path: String(props.design.items.length - 1), position: "after", shown: last.getAttribute("data-part-path")! };
    }
    if (best && (best.path === from || best.path.startsWith(from + "."))) return null;
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
      current = targetAt(ev.clientX, ev.clientY, path);
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
      {outline(props.design).map(({ path, item, depth }) => {
        const marker = drop && drop.shown === path ? drop.position : null;
        // Dropping after a row or column (rather than inside it): the line starts at that level's indent.
        const level = drop && marker === "after" ? drop.path.split(".").length - 1 : depth;
        const cls = path === props.selected ? styles.listItemOn : styles.listItem;
        return (
          <div key={path} data-part-path={path}
            className={`${cls} ${marker === "inside" ? styles.dropInside : ""} ${dragging === path ? styles.dragging : ""}`}
            style={{
              paddingLeft: 8 + depth * 14 + "rem",
              borderTop: marker === "before" ? "2rem solid #ffd400" : undefined,
              borderBottom: marker === "after" ? `2rem solid ${level < depth ? "#ff9f1a" : "#ffd400"}` : undefined,
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
      <div className={styles.hint}>Drag parts to reorder them, or onto a Row or Column to group them. Below a row or column, move the pointer left to drop outside it.</div>
    </div>
  );
};

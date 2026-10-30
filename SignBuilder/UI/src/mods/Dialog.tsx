import { ReactNode, useState } from "react";
import styles from "./SignEditor.module.scss";

/**
 * A floating dialog in the game's style, positioned with CSS so it appears in place on its first frame: centered on
 * the screen, near the top, or docked just above the game's bottom toolbar (its height is the game's --toolbarHeight). The title bar
 * drags it and can carry controls of its own (<header>).
 */
export const Dialog = (props: {
  title: string;
  onClose: () => void;
  dock: "center" | "bottom" | "top";
  className?: string;
  header?: ReactNode;
  children: ReactNode;
}) => {
  const [offset, setOffset] = useState({ x: 0, y: 0 });

  const startDrag = (e: any) => {
    const startX = e.clientX - offset.x;
    const startY = e.clientY - offset.y;
    const onMove = (ev: MouseEvent) => setOffset({ x: ev.clientX - startX, y: ev.clientY - startY });
    const onUp = () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
    };
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  };

  return (
    <div className={{ center: styles.dockCenter, bottom: styles.dockBottom, top: styles.dockTop }[props.dock]}>
      {props.dock === "top" && <div className={styles.dockTopSpacer} />}
      <div className={`${styles.dialog} ${props.className ?? ""}`} style={{ left: offset.x + "px", top: offset.y + "px" }}>
        <div className={styles.dialogHeader} onMouseDown={startDrag}>
          <div className={styles.dialogTitle}>{props.title}</div>
          {/* Controls in the title bar must not start a drag. */}
          <div className={styles.dialogHeaderControls} onMouseDown={(e) => e.stopPropagation()}>{props.header}</div>
          <div className={styles.dialogHeaderSpace} />
          <div className={styles.dialogClose} title="Close" onMouseDown={(e) => e.stopPropagation()} onClick={props.onClose} />
        </div>
        {props.children}
      </div>
    </div>
  );
};

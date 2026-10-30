import { getModule } from "cs2/modding";
import { AutoNavigationScope } from "cs2/input";
import { ReactNode, useState } from "react";
import styles from "./SignEditor.module.scss";

const load = (path: string, name: string): any => {
  try { return getModule(path, name); } catch { return null; }
};
// The game's panels give their content a focus tree of its own (FocusRoot). Without one, every game input in the
// dialog (text and number boxes) registers with the focus tree of the whole game UI, and creating or removing one
// stalled the game for up to two seconds.
const FocusRoot = load("game-ui/common/focus/focus-root.tsx", "FocusRoot");
const ActiveFocusDiv = load("game-ui/common/focus/focus-div.tsx", "ActiveFocusDiv");

/**
 * A floating dialog in the game's style, positioned with CSS so it appears in place on its first frame: centered on
 * the screen, near the top, or docked just above the game's bottom toolbar (its height is the game's --toolbarHeight). The title bar
 * drags it and can carry controls of its own (<header>).
 */
/** The dialog box, as the game's panels build theirs: its own focus tree holding one focusable panel. */
const Focusable = (p: { className: string; style: any; children: ReactNode }) =>
  FocusRoot && ActiveFocusDiv ? (
    <FocusRoot><ActiveFocusDiv className={p.className} style={p.style}>{p.children}</ActiveFocusDiv></FocusRoot>
  ) : (
    <div className={p.className} style={p.style}>{p.children}</div>
  );

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
      <Focusable className={`${styles.dialog} ${props.className ?? ""}`} style={{ left: offset.x + "px", top: offset.y + "px" }}>
        {/* One navigation scope for everything focusable in the box: the panel accepts a single child, and the title
            bar can carry inputs too. */}
        <AutoNavigationScope>
        <div className={styles.dialogHeader} onMouseDown={startDrag}>
          <div className={styles.dialogTitle}>{props.title}</div>
          {/* Controls in the title bar must not start a drag. */}
          <div className={styles.dialogHeaderControls} onMouseDown={(e) => e.stopPropagation()}>{props.header}</div>
          <div className={styles.dialogHeaderSpace} />
          <div className={styles.dialogClose} title="Close" onMouseDown={(e) => e.stopPropagation()} onClick={props.onClose} />
        </div>
        {props.children}
        </AutoNavigationScope>
      </Focusable>
    </div>
  );
};

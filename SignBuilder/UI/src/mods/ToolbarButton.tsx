import { useValue } from "cs2/api";
import { Button } from "cs2/ui";
import { editorOpen$, setEditorOpen } from "./bindings";

/** Top-left toolbar button that opens and closes the Sign Builder editor. */
export const ToolbarButton = () => {
  const open = useValue(editorOpen$);
  return (
    <Button variant="floating" selected={open} onSelect={() => setEditorOpen(!open)} tooltipLabel="Sign Builder">
      <div style={{ fontSize: "13rem", fontWeight: "bold", color: "#fff" }}>SB</div>
    </Button>
  );
};

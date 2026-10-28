import { useValue } from "cs2/api";
import { useEffect, useMemo, useState } from "react";
import { Arrange } from "./Arrange";
import {
  arrange, cancelTool, editingDesign$, editorOpen$, replacing$, savePosts, setEditorOpen, startGantry, startPosts, templates$, themes$, toolMode$, toolStatus$,
} from "./bindings";
import { Designer, DesignerAction } from "./Designer";
import { registerThemes } from "./SignPreview";
import { Theme, blankFor } from "./themeModel";
import { Design, clone } from "./designModel";
import { PlacementBar } from "./PlacementBar";
import { Template } from "./TemplateLibrary";

/** What the designer's sign is for. */
type Context =
  | { kind: "new" }
  | { kind: "postsEdit" }
  | { kind: "gantrySign"; index: number }
  | { kind: "gantryAdd" };

/**
 * Sign Builder's UI root. The designer edits one sign; from there it goes on roadside posts (placement bar) or
 * onto a gantry (pick a road, then the arrangement panel, which can send a sign back to the designer to edit).
 */
export const SignEditor = () => {
  const open = useValue(editorOpen$);
  const templatesJson = useValue(templates$);
  const editing = useValue(editingDesign$);
  const status = useValue(toolStatus$);
  const mode = useValue(toolMode$);
  const replacing = useValue(replacing$);
  const templates: Template[] = useMemo(() => { try { return JSON.parse(templatesJson); } catch { return []; } }, [templatesJson]);
  const themesJson = useValue(themes$);
  const themes: Theme[] = useMemo(() => { try { return JSON.parse(themesJson); } catch { return []; } }, [themesJson]);
  useEffect(() => registerThemes(themes), [themes]);

  const [design, setDesign] = useState<Design>(blankFor());
  const [context, setContext] = useState<Context>({ kind: "new" });

  // A placed structure opened for editing (or a design pushed by the harness).
  useEffect(() => {
    if (!editing) return;
    try {
      const payload = JSON.parse(editing);
      const first = (payload.designs ?? [])[0];
      if (payload.kind === "posts" && first) {
        setDesign(clone(first));
        setContext({ kind: "postsEdit" });
      } else if (payload.kind === "new" && first) {
        setDesign(clone(first));
        setContext({ kind: "new" });
      } else {
        setContext({ kind: "new" });
      }
    } catch { /* ignore malformed payloads */ }
  }, [editing]);

  // Leaving the arrangement (built or canceled) returns any half-finished gantry sign edit to a plain design.
  useEffect(() => {
    if (mode !== "arrange" && (context.kind === "gantrySign" || context.kind === "gantryAdd")) setContext({ kind: "new" });
  }, [mode]);

  if (!open) return null;
  const close = () => { cancelTool(); setEditorOpen(false); };

  if (mode === "arrange" && context.kind !== "gantrySign" && context.kind !== "gantryAdd") {
    return (
      <Arrange onClose={close}
        onEditSign={(index, d) => { setDesign(clone(d)); setContext({ kind: "gantrySign", index }); }}
        onAddSign={() => setContext({ kind: "gantryAdd" })} />
    );
  }
  if (mode === "posts" || mode === "pickRoad") {
    return <PlacementBar status={status} onClose={close} onBack={() => cancelTool()} />;
  }

  let title = "Sign Builder";
  let actions: DesignerAction[];
  switch (context.kind) {
    case "postsEdit":
      title = "Edit roadside sign";
      actions = replacing
        ? [{ label: "Save", primary: true, onSelect: () => savePosts(design) },
          { label: "Stop editing", title: "Keep the placed sign as it is", onSelect: () => { cancelTool(); setContext({ kind: "new" }); } }]
        : [{ label: "Place Standalone", primary: true, onSelect: () => startPosts(design) }];
      break;
    case "gantrySign":
      title = `Edit gantry sign ${context.index + 1}`;
      actions = [
        { label: "Done", primary: true, onSelect: () => { arrange({ op: "replaceSign", index: context.index, design }); setContext({ kind: "new" }); } },
        { label: "Cancel", onSelect: () => setContext({ kind: "new" }) },
      ];
      break;
    case "gantryAdd":
      title = "Add a sign to the gantry";
      actions = [
        { label: "Add to gantry", primary: true, onSelect: () => { arrange({ op: "addSign", design }); setContext({ kind: "new" }); } },
        { label: "Cancel", onSelect: () => setContext({ kind: "new" }) },
      ];
      break;
    default:
      actions = [
        { label: "Place Standalone", primary: true, onSelect: () => startPosts(design) },
        { label: "Place on gantry", primary: true, onSelect: () => startGantry([design]) },
      ];
  }

  return (
    <Designer design={design} setDesign={setDesign} templates={templates} themes={themes} title={title} actions={actions}
      status={mode === "off" ? status : ""} onClose={close} />
  );
};

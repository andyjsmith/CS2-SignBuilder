import { useValue } from "cs2/api";
import { BackConsumer } from "cs2/input";
import { useEffect, useMemo, useState } from "react";
import { Arrange } from "./Arrange";
import {
  arrange, arrangeInfo$, cancelTool, editingDesign$, editorOpen$, replacing$, savePosts, setEditorOpen, startGantry, startPosts, templates$, themes$, toolMode$, toolStatus$,
} from "./bindings";
import { Confirm } from "./Controls";
import { Designer, DesignerAction } from "./Designer";
import { registerThemes } from "./SignPreview";
import { Theme, blankFor, themeOf } from "./themeModel";
import { Design, clone } from "./designModel";
import { PlacementBar } from "./PlacementBar";
import { Template } from "./TemplateLibrary";

/** What the designer's sign is for. */
type Context =
  | { kind: "new" }
  | { kind: "postsEdit" }
  | { kind: "gantrySign"; index: number }
  | { kind: "gantryAdd" };

type Question = { message: string; confirm: string; onConfirm: () => void };

const LOSE_GANTRY = "Close without building? The gantry and its changes will be lost.";

/**
 * Sign Builder's UI root. The designer edits one sign; from there it goes on roadside posts (placement bar) or
 * onto a gantry (pick a road, then the arrangement panel, which can send a sign back to the designer to edit).
 * Escape closes the dialog like the game's own; unsaved changes are asked about first.
 */
export const SignEditor = () => {
  const open = useValue(editorOpen$);
  const templatesJson = useValue(templates$);
  const editing = useValue(editingDesign$);
  const status = useValue(toolStatus$);
  const mode = useValue(toolMode$);
  const replacing = useValue(replacing$);
  const arrangeJson = useValue(arrangeInfo$);
  // Whether closing the arrangement would lose anything (a new gantry, or changes to a placed one).
  const gantryUnsaved = useMemo(() => { try { return arrangeJson ? JSON.parse(arrangeJson).dirty !== false : true; } catch { return true; } }, [arrangeJson]);
  const templates: Template[] = useMemo(() => { try { return JSON.parse(templatesJson); } catch { return []; } }, [templatesJson]);
  const themesJson = useValue(themes$);
  const themes: Theme[] = useMemo(() => { try { return JSON.parse(themesJson); } catch { return []; } }, [themesJson]);
  useEffect(() => registerThemes(themes), [themes]);

  const [design, setDesign] = useState<Design>(blankFor());
  const [context, setContext] = useState<Context>({ kind: "new" });
  // The design as last loaded, placed or saved: any difference is an unsaved change.
  const [baseline, setBaseline] = useState(() => JSON.stringify(blankFor()));
  const [question, setQuestion] = useState<Question | null>(null);
  const dirty = JSON.stringify(design) !== baseline;
  /** Loads a design (template, placed sign, blank): it starts out unchanged. */
  const load = (d: Design) => {
    setDesign(d);
    setBaseline(JSON.stringify(d));
  };
  const settle = () => setBaseline(JSON.stringify(design));
  /** Runs <then> now, or once the player confirms when there is something to lose. */
  const guard = (unsaved: boolean, message: string, then: () => void, confirm = "Discard") => {
    if (!unsaved) then();
    else setQuestion({ message, confirm, onConfirm: () => { setQuestion(null); then(); } });
  };

  // A placed structure opened for editing (or a design pushed by the harness).
  useEffect(() => {
    if (!editing) return;
    try {
      const payload = JSON.parse(editing);
      const first = (payload.designs ?? [])[0];
      if (payload.kind === "posts" && first) {
        load(clone(first));
        setContext({ kind: "postsEdit" });
      } else if (payload.kind === "new" && first) {
        load(clone(first));
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

  useEffect(() => { if (!open) setQuestion(null); }, [open]);

  if (!open) return null;
  const close = () => { setQuestion(null); cancelTool(); setEditorOpen(false); };
  const overlay = question && (
    <Confirm message={question.message} confirm={question.confirm} cancel="Keep editing" onConfirm={question.onConfirm} onCancel={() => setQuestion(null)} />
  );
  // Escape works like the game's own dialogs: it closes this one (or dismisses an open question).
  const withBack = (content: any, onBack: () => void) => (
    <BackConsumer onAction={() => (question ? setQuestion(null) : onBack())}>{content}</BackConsumer>
  );

  if (mode === "arrange" && context.kind !== "gantrySign" && context.kind !== "gantryAdd") {
    const closeGantry = (unsaved: boolean) => guard(unsaved, LOSE_GANTRY, close);
    return withBack(
      <Arrange overlay={overlay} onClose={closeGantry}
        onCancel={(unsaved) => guard(unsaved, "Cancel this gantry? Its changes will be lost.", () => arrange({ op: "cancel" }))}
        onEditSign={(index, d) => { load(clone(d)); setContext({ kind: "gantrySign", index }); }}
        onAddSign={() => { load(blankFor(themeOf(themes, design))); setContext({ kind: "gantryAdd" }); }} />,
      // Escape always asks (it is easy to press by accident).
      () => guard(true, gantryUnsaved ? LOSE_GANTRY : "Close the gantry editor?", close, gantryUnsaved ? "Discard" : "Close"),
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
        ? [{ label: "Save", primary: true, onSelect: () => { settle(); savePosts(design); } },
          { label: "Stop editing", title: "Keep the placed sign as it is",
            onSelect: () => guard(dirty, "Stop editing? Your changes to this sign will be lost.", () => { cancelTool(); settle(); setContext({ kind: "new" }); }) }]
        : [{ label: "Roadside", primary: true, onSelect: () => { settle(); startPosts(design); } }];
      break;
    case "gantrySign":
      title = `Edit gantry sign ${context.index + 1}`;
      actions = [
        { label: "Done", primary: true, onSelect: () => { arrange({ op: "replaceSign", index: context.index, design }); setContext({ kind: "new" }); } },
        { label: "Cancel", onSelect: () => guard(dirty, "Discard your changes to this sign?", () => setContext({ kind: "new" })) },
      ];
      break;
    case "gantryAdd":
      title = "Add a sign to the gantry";
      actions = [
        { label: "Add to gantry", primary: true, onSelect: () => { arrange({ op: "addSign", design }); setContext({ kind: "new" }); } },
        { label: "Cancel", onSelect: () => guard(dirty, "Discard this sign?", () => setContext({ kind: "new" })) },
      ];
      break;
    default:
      actions = [
        { label: "Gantry", primary: true, title: "Over the road on a truss or tube structure", onSelect: () => { settle(); startGantry([design]); } },
        { label: "Roadside", primary: true, title: "On posts beside the road", onSelect: () => { settle(); startPosts(design); } },
        { label: "Prop", primary: true, title: "Just the sign, no supports: on any surface or the side of an elevated road", onSelect: () => { settle(); startPosts(design, "surface"); } },
      ];
  }

  // Closing while one of a gantry's signs is open loses the whole gantry.
  const inGantry = context.kind === "gantrySign" || context.kind === "gantryAdd";
  const requestClose = () => guard(dirty || inGantry, inGantry ? LOSE_GANTRY : "Close the Sign Builder? Your changes to this sign will be lost.", close);
  const escape = () => (dirty || inGantry ? requestClose() : guard(true, "Close the Sign Builder?", close, "Close"));
  return withBack(
    <Designer design={design} setDesign={setDesign} onTemplateSaved={settle}
      onLoad={(d, then) => guard(dirty, "Load this? Your changes to the current sign will be lost.", () => { load(d); then(); })} templates={templates} themes={themes}
      title={title} actions={actions} onClose={requestClose} overlay={overlay} />,
    escape,
  );
};

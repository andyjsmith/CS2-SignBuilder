import { bindValue, trigger } from "cs2/api";
import mod from "mod.json";

// C# side: SignBuilder.UI.SignBuilderUISystem (binding group = mod id).
const group = mod.id;

export const editorOpen$ = bindValue<boolean>(group, "editorOpen", false);
/** JSON array of { id, name, design } from Resources/Templates. */
export const templates$ = bindValue<string>(group, "templates", "[]");
/** JSON of the design to edit when the editor opens (set when re-editing a placed sign). */
export const editingDesign$ = bindValue<string>(group, "editingDesign", "");
/** Status line from the placement tool (e.g. "Click a road to place"). */
export const toolStatus$ = bindValue<string>(group, "toolStatus", "");

/** "off", "pick" (editor open, click a placed sign to edit it) or "place" (click a road). */
export const toolMode$ = bindValue<string>(group, "toolMode", "off");

export const setEditorOpen = (open: boolean) => trigger(group, "setEditorOpen", open);
/** Starts the placement tool with { design, support }. */
export const startPlacement = (payload: string) => trigger(group, "startPlacement", payload);
/** Leaves the placement tool (back to the default tool). */
export const cancelTool = () => trigger(group, "cancelTool");
/** Opens the editor on the structure selected in the game's selected-object panel. */
export const editSelected = () => trigger(group, "editSelected");

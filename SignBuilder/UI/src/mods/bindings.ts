import { bindValue, trigger } from "cs2/api";
import mod from "mod.json";

// C# side: SignBuilder.Systems.SignBuilderUISystem (binding group = mod id).
const group = mod.id;

export const editorOpen$ = bindValue<boolean>(group, "editorOpen", false);
/** JSON array of { id, name, builtIn, design }: built-in templates, then the player's own. */
export const templates$ = bindValue<string>(group, "templates", "[]");
/** JSON array of { id, name, builtIn, base, def }: the sign themes (countries / styles). */
export const themes$ = bindValue<string>(group, "themes", "[]");
/** JSON { designs, kind: "posts" | "gantry" | "new", nonce } pushed when a placed structure is opened for editing. */
export const editingDesign$ = bindValue<string>(group, "editingDesign", "");
/** Status line from the tool. */
export const toolStatus$ = bindValue<string>(group, "toolStatus", "");
/** "off", "posts" (placing a roadside sign), "pickRoad" (choosing where a gantry goes) or "arrange". */
export const toolMode$ = bindValue<string>(group, "toolMode", "off");
/** JSON { free, angle, elevation, width, scale, posts } while placing posts or picking a road. */
export const placeInfo$ = bindValue<string>(group, "placeInfo", "{}");
/** JSON arrangement (lanes, deck, structure, posts, signs with x/w/h/slot/design, options) while arranging. */
export const arrangeInfo$ = bindValue<string>(group, "arrangeInfo", "");
/** True while a placed structure is being edited (its next build replaces it). */
export const replacing$ = bindValue<boolean>(group, "replacing", false);

export const setEditorOpen = (open: boolean) => trigger(group, "setEditorOpen", open);
/** Place one sign on roadside posts. */
export const startPosts = (design: object, free = false) => trigger(group, "startPosts", JSON.stringify({ design, free }));
/** Pick a road for a gantry, starting with these signs (may be empty). */
export const startGantry = (designs: object[]) => trigger(group, "startGantry", JSON.stringify({ designs }));
/** Placement bar: free, road, rotateLeft, rotateRight, raise, lower, wider, narrower, smaller, larger. */
export const adjust = (action: string) => trigger(group, "adjust", action);
/** Arrangement panel operation, e.g. { op: "moveSign", index, x, snap }. */
export const arrange = (op: object) => trigger(group, "arrange", JSON.stringify(op));
/** Save an edited roadside sign where it stands. */
export const savePosts = (design: object) => trigger(group, "savePosts", JSON.stringify(design));
/** Stop the tool. */
export const cancelTool = () => trigger(group, "cancelTool");
export const saveTemplate = (name: string, design: object, id?: string) => trigger(group, "saveTemplate", JSON.stringify({ name, design, id }));
export const renameTemplate = (id: string, name: string) => trigger(group, "renameTemplate", id, name);
export const deleteTemplate = (id: string) => trigger(group, "deleteTemplate", id);
export const openTemplatesFolder = () => trigger(group, "openTemplatesFolder");

/** Selected-object panel actions on a placed structure. */
export const editSelected = () => trigger(group, "editSelected");
export const moveSelected = () => trigger(group, "moveSelected");
export const duplicateSelected = () => trigger(group, "duplicateSelected");

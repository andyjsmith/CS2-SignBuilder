// Helpers for editing sign designs (see Resources/Renderer/renderer.js for the format).
// Items are addressed by dotted paths into nested "items" arrays: "2" or "0.1".

export type Item = { type: string; [key: string]: any };
export type Design = {
  id?: string;
  name?: string;
  scheme?: string;
  align?: string;
  exitTab?: { text: string; align?: string } | null;
  bottomBar?: { type: string; arrows?: number; height?: number } | null;
  items: Item[];
  [key: string]: any;
};

/** Gantry structure types and spans (C# LayoutOptions.Type / Span). */
export const STRUCTURES = [
  { id: "doubleTruss", label: "Double truss" },
  { id: "singleTruss", label: "Single truss" },
  { id: "curved", label: "Curved tube" },
  { id: "straight", label: "Straight tube" },
];
export const SPANS = [
  { id: "full", label: "Full span", hint: "Across every carriageway" },
  { id: "single", label: "Single span", hint: "Across one carriageway" },
  { id: "cantilever", label: "Cantilever", hint: "One post beside the road" },
];
/** Summary for a support id such as "curved/cantilever/left" or "ground". */
export function supportLabel(id: string): string {
  if (id === "ground") return "Roadside posts";
  const [type, span, side] = id.split("/");
  const t = STRUCTURES.find((x) => x.id === type)?.label ?? type;
  const s = SPANS.find((x) => x.id === span)?.label.toLowerCase() ?? span ?? "";
  return `${t}, ${s}${side === "left" ? " (left side)" : ""}`;
}

export const PALETTE: { label: string; make: () => Item }[] = [
  { label: "Text", make: () => ({ type: "text", text: "Destination", height: 16 }) },
  { label: "Shield", make: () => ({ type: "shield", network: "I", number: "95", height: 36 }) },
  { label: "Cardinal", make: () => ({ type: "text", text: "NORTH", height: 15, font: "RG2014D" }) },
  { label: "Arrow", make: () => ({ type: "arrow", direction: "up", height: 24 }) },
  { label: "Distance", make: () => ({ type: "text", text: "1 MILE", height: 12, font: "RG2014D" }) },
  { label: "Divider", make: () => ({ type: "divider" }) },
  { label: "Row", make: () => ({ type: "row", align: "center", items: [] }) },
];

export function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value));
}

function split(path: string): number[] {
  return path === "" ? [] : path.split(".").map((p) => parseInt(p, 10));
}

/** The items array that contains the item at `path`, and its index. */
function parentOf(design: Design, path: string): { list: Item[]; index: number } | null {
  const parts = split(path);
  if (parts.length === 0) return null;
  let list = design.items;
  for (let i = 0; i < parts.length - 1; i++) {
    const next = list[parts[i]];
    if (!next || !next.items) return null;
    list = next.items;
  }
  return { list, index: parts[parts.length - 1] };
}

export function getItem(design: Design, path: string): Item | null {
  const p = parentOf(design, path);
  return p ? p.list[p.index] ?? null : null;
}

export function updateItem(design: Design, path: string, patch: Partial<Item>): Design {
  const next = clone(design);
  const p = parentOf(next, path);
  if (p && p.list[p.index]) p.list[p.index] = { ...p.list[p.index], ...patch };
  return next;
}

export function removeItem(design: Design, path: string): Design {
  const next = clone(design);
  const p = parentOf(next, path);
  if (p) p.list.splice(p.index, 1);
  return next;
}

export function moveItem(design: Design, path: string, delta: number): { design: Design; path: string } {
  const next = clone(design);
  const p = parentOf(next, path);
  if (!p) return { design, path };
  const target = p.index + delta;
  if (target < 0 || target >= p.list.length) return { design, path };
  const [item] = p.list.splice(p.index, 1);
  p.list.splice(target, 0, item);
  const parts = split(path);
  parts[parts.length - 1] = target;
  return { design: next, path: parts.join(".") };
}

/** Inserts after the selected item (inside the same row/stack), or at the end of the sign. */
export function insertItem(design: Design, selected: string | null, item: Item): { design: Design; path: string } {
  const next = clone(design);
  const p = selected ? parentOf(next, selected) : null;
  if (p) {
    p.list.splice(p.index + 1, 0, item);
    const parts = split(selected!);
    parts[parts.length - 1] = p.index + 1;
    return { design: next, path: parts.join(".") };
  }
  next.items.push(item);
  return { design: next, path: String(next.items.length - 1) };
}

/** Where something is dropped relative to a target part: before it, after it, or inside it (rows and stacks). */
export type DropPosition = "before" | "after" | "inside";

function findPath(items: Item[], target: Item, prefix = ""): string | null {
  for (let i = 0; i < items.length; i++) {
    const path = prefix === "" ? String(i) : `${prefix}.${i}`;
    if (items[i] === target) return path;
    if (items[i].items) {
      const inner = findPath(items[i].items, target, path);
      if (inner) return inner;
    }
  }
  return null;
}

/** Moves the part at <from> next to (or into) the part at <target>; returns the new design and the moved part's path. */
export function moveTo(design: Design, from: string, target: string, position: DropPosition): { design: Design; path: string } {
  if (from === target || target.startsWith(from + ".")) return { design, path: from };
  const next = clone(design);
  const src = parentOf(next, from);
  const tgt = parentOf(next, target);
  if (!src || !tgt) return { design, path: from };
  const moving = src.list[src.index];
  const anchor = tgt.list[tgt.index];
  const fromParts = from.split(".");
  const oldParent = fromParts.length > 1 ? getItem(next, fromParts.slice(0, -1).join(".")) : null;
  src.list.splice(src.index, 1);
  if (position === "inside" && anchor.items) {
    anchor.items.push(moving);
  } else {
    const where = parentOf(next, findPath(next.items, anchor) ?? target);
    if (!where) return { design, path: from };
    where.list.splice(where.index + (position === "after" ? 1 : 0), 0, moving);
  }
  // A row emptied by dragging its last part out goes away.
  if (oldParent && oldParent.items && oldParent.items.length === 0) {
    const emptied = parentOf(next, findPath(next.items, oldParent) ?? "");
    if (emptied) emptied.list.splice(emptied.index, 1);
  }
  return { design: next, path: findPath(next.items, moving) ?? from };
}

export function describe(item: Item): string {
  switch (item.type) {
    case "text": return `Text "${item.text}"`;
    case "shield": return `${item.network === "I" ? "Interstate" : item.network === "US" ? "US route" : "State route"} ${item.number}`;
    case "arrow": return `Arrow ${item.direction}`;
    case "row": return `Row (${item.items.length})`;
    case "stack": return `Stack (${item.items.length})`;
    default: return item.type;
  }
}

/** Flattened list of (path, item, depth) for the outline. */
export function outline(design: Design): { path: string; item: Item; depth: number }[] {
  const out: { path: string; item: Item; depth: number }[] = [];
  const walk = (items: Item[], prefix: string, depth: number) =>
    items.forEach((item, i) => {
      const path = prefix === "" ? String(i) : `${prefix}.${i}`;
      out.push({ path, item, depth });
      if (item.items) walk(item.items, path, depth + 1);
    });
  walk(design.items, "", 0);
  return out;
}

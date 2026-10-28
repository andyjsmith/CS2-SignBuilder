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

/** Structure types and spans; a support id (C# AssemblyBuilder.Supports) is one combination of the two. */
export const STRUCTURES = [
  { id: "truss", label: "Truss" },
  { id: "curved", label: "Curved tube" },
  { id: "straight", label: "Straight tube" },
  { id: "ground", label: "Roadside posts" },
];
export const SPANS = [
  { id: "full", label: "Full span", hint: "Across every carriageway" },
  { id: "single", label: "Single span", hint: "Across one carriageway" },
  { id: "cantilever", label: "Cantilever", hint: "One post at the roadside" },
];
const SUPPORT_IDS: Record<string, Record<string, string>> = {
  truss: { full: "truss", single: "trussSingle", cantilever: "trussCantilever" },
  curved: { full: "curvedFull", single: "curvedSpan", cantilever: "curvedCantilever" },
  straight: { full: "monotubeFull", single: "monotube", cantilever: "cantilever" },
  ground: { full: "ground", single: "ground", cantilever: "ground" },
};
export const supportId = (structure: string, span: string) => SUPPORT_IDS[structure]?.[span] ?? "truss";
export function parseSupport(id: string): { structure: string; span: string } {
  for (const structure of Object.keys(SUPPORT_IDS)) {
    for (const span of Object.keys(SUPPORT_IDS[structure])) {
      if (SUPPORT_IDS[structure][span] === id) return { structure, span };
    }
  }
  return { structure: "truss", span: "full" };
}
export function supportLabel(id: string): string {
  const { structure, span } = parseSupport(id);
  const s = STRUCTURES.find((x) => x.id === structure)?.label ?? structure;
  return structure === "ground" ? s : `${s}, ${SPANS.find((x) => x.id === span)?.label.toLowerCase() ?? span}`;
}

export const PALETTE: { label: string; make: () => Item }[] = [
  { label: "Text", make: () => ({ type: "text", text: "Destination", height: 16 }) },
  { label: "Shield", make: () => ({ type: "shield", network: "I", number: "95", height: 36 }) },
  { label: "Cardinal", make: () => ({ type: "text", text: "NORTH", height: 15, font: "RG2014D" }) },
  { label: "Arrow", make: () => ({ type: "arrow", direction: "up", height: 24 }) },
  { label: "Distance", make: () => ({ type: "text", text: "1 MILE", height: 12, font: "RG2014D" }) },
  { label: "Divider", make: () => ({ type: "divider" }) },
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

/** Puts the selected item and the next one side by side in a row (e.g. shield + cardinal). */
export function joinWithNext(design: Design, path: string): { design: Design; path: string } {
  const next = clone(design);
  const p = parentOf(next, path);
  if (!p || p.index + 1 >= p.list.length) return { design, path };
  const a = p.list[p.index];
  const b = p.list[p.index + 1];
  const row: Item = a.type === "row" ? { ...a, items: [...a.items, b] } : { type: "row", items: [a, b] };
  p.list.splice(p.index, 2, row);
  return { design: next, path };
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

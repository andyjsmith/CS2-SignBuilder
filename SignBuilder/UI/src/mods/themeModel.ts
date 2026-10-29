import { Design, Item, clone } from "./designModel";

// Sign themes (see Resources/Themes/<id>/theme.json): everything country-specific the editor offers.

export type ThemeFont = { family: string; label?: string; hidden?: boolean };
export type ThemeShield = { label?: string; shape?: string; img?: string; bg?: string; fg?: string; border?: string; prefix?: string; default?: string };
export type ThemeDef = {
  id: string;
  name?: string;
  description?: string;
  defaults?: { font?: string; textHeight?: number; shieldHeight?: number; arrowHeight?: number; scheme?: string };
  fonts?: ThemeFont[];
  schemes?: Record<string, { bg: string; fg: string; label?: string }>;
  shields?: Record<string, ThemeShield>;
  exitTab?: { font?: string; height?: number };
  exitOnly?: { font?: string; words?: string[] };
  parts?: { label: string; item: Item }[];
  laneWidth?: number;
};
export type Theme = { id: string; name: string; builtIn: boolean; base: string; def: ThemeDef };

export const themeOf = (themes: Theme[], design: Design): Theme | undefined =>
  themes.find((t) => t.id === (design.theme ?? "us")) ?? themes.find((t) => t.id === "us") ?? themes[0];


/** A new sign in the theme: its default colors and one line of text. */
export function blankFor(theme?: Theme): Design {
  const d = theme?.def;
  return {
    theme: theme?.id ?? "us",
    scheme: d?.defaults?.scheme ?? "green",
    items: [{ type: "text", text: "Destination", height: d?.defaults?.textHeight ?? 16 }],
  };
}

/**
 * Moves a sign to another theme. Every theme uses the same size unit, so sizes carry over as they are; colors,
 * fonts and plates the new theme lacks fall back to its defaults; US-only parts (exit tab, EXIT ONLY panel) are
 * dropped where the theme has none. Text carries over unchanged.
 */
export function convertDesign(design: Design, to: Theme): Design {
  const d = clone(design);
  const fonts = new Set((to.def.fonts ?? []).map((f) => f.family));
  const shields = Object.keys(to.def.shields ?? {});
  const fix = (obj: any) => {
    if (obj.font && !fonts.has(obj.font)) delete obj.font;
    if (obj.type === "shield" && !shields.includes(obj.network)) obj.network = shields[0];
    if (obj.scheme && !(to.def.schemes ?? {})[obj.scheme]) delete obj.scheme;
    (obj.items ?? []).forEach(fix);
  };
  fix(d);
  d.theme = to.id;
  d.scheme = d.scheme && (to.def.schemes ?? {})[d.scheme] ? d.scheme : to.def.defaults?.scheme ?? Object.keys(to.def.schemes ?? {})[0];
  return d;
}

/**
 * Older designs (placed signs, shared templates) kept the exit tab and the EXIT ONLY panel as sign settings; they
 * are parts now: a tab, and a panel across the bottom edge.
 */
export function upgradeDesign(design: Design, theme?: Theme): Design {
  if (!design.exitTab && !design.bottomBar) return design;
  const d = clone(design);
  const tab = d.exitTab;
  const bar = d.bottomBar;
  delete d.exitTab;
  delete d.bottomBar;
  const tabFont = theme?.def.exitTab?.font;
  const barFont = theme?.def.exitOnly?.font;
  if (tab) d.items.unshift({ type: "tab", align: tab.align ?? "right", items: [{ type: "text", text: tab.text ?? "EXIT", height: tab.height ?? 10, font: tab.font ?? tabFont }] });
  if (bar) {
    const h = bar.height ?? 12;
    const words: string[] = bar.words ?? theme?.def.exitOnly?.words ?? ["EXIT", "ONLY"];
    const arrow = { type: "arrow", direction: "down", height: h * 1.5 };
    const items = (bar.arrows ?? 1) >= 2
      ? [arrow, { type: "text", text: words.join(" "), height: h, font: barFont }, clone(arrow)]
      : [{ type: "text", text: words[0], height: h, font: barFont }, arrow, { type: "text", text: words[1], height: h, font: barFont }];
    d.items.push({ type: "panel", span: "bottom", scheme: bar.scheme ?? "yellow", align: "center", items });
  }
  return d;
}

/** A lane (4 m, at the default 100% sign size) in sign units, less a little clearance: "at least a lane" width. */
export const laneMinWidth = (theme?: Theme) => Math.round(((theme?.def.laneWidth ?? 4 / 0.0254) / 0.7) * 0.92);

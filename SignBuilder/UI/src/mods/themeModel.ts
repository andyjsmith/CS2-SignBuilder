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
  exitTab?: { font?: string };
  /** Ready-made combinations of the basic parts that suit the theme's signs ("Add preset"). */
  presets?: { label: string; item: Item; insert?: "first" }[];
  laneWidth?: number;
};
export type Theme = { id: string; name: string; builtIn: boolean; base: string; def: ThemeDef };

export const themeOf = (themes: Theme[], design: Design): Theme | undefined =>
  themes.find((t) => t.id === (design.theme ?? "us")) ?? themes.find((t) => t.id === "us") ?? themes[0];


/**
 * The basic parts every theme has, sized and colored for the theme: text at its letter height, its first route plate,
 * a panel in its yellow (or white), a tab with exit text where the theme has exit tabs.
 */
export function baseParts(theme?: Theme): { label: string; item: Item }[] {
  const d = theme?.def;
  const th = d?.defaults?.textHeight ?? 16;
  const first = Object.keys(d?.shields ?? {})[0];
  const schemes = d?.schemes ?? {};
  const small = Math.round(th * 0.75 * 10) / 10;
  const tabFont = d?.exitTab?.font;
  return [
    { label: "Text", item: { type: "text", text: "Destination", height: th } },
    { label: "Shield", item: { type: "shield", network: first, number: (d?.shields ?? {})[first]?.default ?? "1", height: d?.defaults?.shieldHeight } },
    { label: "Arrow", item: { type: "arrow", direction: "up", height: d?.defaults?.arrowHeight } },
    { label: "Diagram", item: { type: "diagram", shape: "exit-left", lanes: 2, exitLanes: 1, height: Math.round((d?.defaults?.arrowHeight ?? 24) * 2.2) } },
    { label: "LED text", item: { type: "led", lines: ["ACCIDENT AHEAD"], height: small } },
    { label: "Divider", item: { type: "divider" } },
    { label: "Row", item: { type: "row", align: "center", items: [] } },
    { label: "Column", item: { type: "column", align: "center", items: [] } },
    { label: "Chip", item: { type: "chip", scheme: schemes.white ? "white" : undefined, items: [{ type: "text", text: "Text", height: small }] } },
    { label: "Panel", item: { type: "panel", scheme: schemes.yellow ? "yellow" : schemes.white ? "white" : undefined, align: "center", items: [] } },
    { label: "Tab", item: { type: "tab", align: "right", items: [{ type: "text", text: tabFont ? "EXIT 1" : "1", height: tabFont ? 10 : Math.round(th * 0.65 * 10) / 10, font: tabFont }] } },
  ];
}

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
 * fonts and plates the new theme lacks fall back to its defaults. Text carries over unchanged.
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

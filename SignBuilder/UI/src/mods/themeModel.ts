import { Design, Item, clone } from "./designModel";

// Sign themes (see Resources/Themes/<id>/theme.json): everything country-specific the editor offers.

export type ThemeFont = { family: string; label?: string; hidden?: boolean };
export type ThemeShield = { label?: string; shape?: string; img?: string; bg?: string; fg?: string; border?: string; prefix?: string; default?: string };
export type ThemeDef = {
  id: string;
  name?: string;
  description?: string;
  units?: "in" | "mm";
  defaults?: { font?: string; textHeight?: number; shieldHeight?: number; arrowHeight?: number; scheme?: string };
  fonts?: ThemeFont[];
  schemes?: Record<string, { bg: string; fg: string; label?: string }>;
  shields?: Record<string, ThemeShield>;
  features?: { exitTab?: boolean; exitOnly?: boolean };
  parts?: { label: string; item: Item }[];
  laneWidth?: number;
};
export type Theme = { id: string; name: string; builtIn: boolean; base: string; def: ThemeDef };

export const themeOf = (themes: Theme[], design: Design): Theme | undefined =>
  themes.find((t) => t.id === (design.theme ?? "us")) ?? themes.find((t) => t.id === "us") ?? themes[0];

export const unitLabel = (theme?: Theme) => (theme?.def.units === "mm" ? " mm" : '"');

/** A new sign in the theme: its default colors and one line of text. */
export function blankFor(theme?: Theme): Design {
  const d = theme?.def;
  return {
    theme: theme?.id ?? "us",
    scheme: d?.defaults?.scheme ?? "green",
    items: [{ type: "text", text: "Destination", height: d?.defaults?.textHeight ?? 16 }],
  };
}

const SIZE_KEYS = ["height", "width", "minWidth", "border", "cornerRadius", "margin", "gap", "gapBefore", "thickness", "padding", "radius", "borderWidth"];

/**
 * Moves a sign to another theme. Sizes scale with the two themes' standard letter heights (which also converts
 * inches and millimeters); colors, fonts and plates the new theme lacks fall back to its defaults; US-only parts
 * (exit tab, EXIT ONLY panel) are dropped where the theme has none. Plain text carries over unchanged.
 */
export function convertDesign(design: Design, from: Theme | undefined, to: Theme): Design {
  const d = clone(design);
  const ratio = (to.def.defaults?.textHeight ?? 16) / (from?.def.defaults?.textHeight ?? 16);
  const fonts = new Set((to.def.fonts ?? []).map((f) => f.family));
  const shields = Object.keys(to.def.shields ?? {});
  const fix = (obj: any) => {
    for (const k of SIZE_KEYS) if (typeof obj[k] === "number") obj[k] = Math.round(obj[k] * ratio * 10) / 10;
    if (obj.font && !fonts.has(obj.font)) delete obj.font;
    if (obj.type === "shield" && !shields.includes(obj.network)) obj.network = shields[0];
    if (obj.scheme && !(to.def.schemes ?? {})[obj.scheme]) delete obj.scheme;
    (obj.items ?? []).forEach(fix);
  };
  fix(d);
  if (d.exitTab) fix(d.exitTab);
  if (d.bottomBar) fix(d.bottomBar);
  d.theme = to.id;
  d.scheme = d.scheme && (to.def.schemes ?? {})[d.scheme] ? d.scheme : to.def.defaults?.scheme ?? Object.keys(to.def.schemes ?? {})[0];
  if (!to.def.features?.exitTab) d.exitTab = null;
  if (!to.def.features?.exitOnly) d.bottomBar = null;
  return d;
}

/** A lane (4 m, at the default 100% sign size) in the theme's units, less a little clearance: "at least a lane" width. */
export const laneMinWidth = (theme?: Theme) => Math.round(((theme?.def.laneWidth ?? 4 / 0.0254) / 0.7) * 0.92);

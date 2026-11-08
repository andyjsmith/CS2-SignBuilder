import { Scrollable } from "cs2/ui";
import { Dialog } from "./Dialog";
import { openThemesFolder } from "./bindings";
import { ReactNode, useEffect, useState } from "react";
import { ArrowIcon, AlignIcon, SymbolIcon, ThemeArrowIcon, ThemeShieldIcon } from "./Icons";
import { Choice, ColorBox, ColorRows, Field, Flow, NumberBox, Option, Select, Swatches, TextBox } from "./Controls";
import { Design, Item, clone, duplicateItem, getItem, insertItem, kindOf, removeItem, updateItem } from "./designModel";
import { PartsList } from "./PartsList";
import { SignPreview, useRenderer } from "./SignPreview";
import { Template, TemplateLibrary } from "./TemplateLibrary";
import { DESIGN_FORMAT, LibrarySymbol, SymbolLibrary, Theme, baseParts, blankFor, convertDesign, themeOf } from "./themeModel";
import styles from "./SignEditor.module.scss";

/** Whether a design has parts whose look depends on its format (arrows and road diagrams). */
const usesArrows = (design: { items?: Item[]; [key: string]: any }): boolean =>
  (design.items ?? []).some((i) => i.type === "arrow" || i.type === "diagram" || usesArrows(i));

/** An arrow's style ("" for straight arrows): lane arrows saved before there were styles have none. */
const arrowStyleOf = (item: Item): string =>
  item.style ?? (/^((turn|slight|split|corner|add)-(left|right)|fork)$/.test(item.direction ?? "") ? "lane" : "");
const ALIGN: Option[] = ["start", "center", "end"].map((a) => ({ value: a, icon: <AlignIcon align={a} />, title: a }));
// Parts side by side in a row line up by their tops, middles or bottoms.
const ROW_ALIGN: Option[] = [{ value: "start", label: "Top" }, { value: "center", label: "Middle" }, { value: "end", label: "Bottom" }];
const ROW_SPACING: Option[] = [
  { value: "", label: "Packed" },
  { value: "between", label: "Spread out", title: "Fill the width, with the space between the parts" },
  { value: "even", label: "Even", title: "Fill the width, each part centered in an equal share of it (dividers keep their width)" },
];
const LANE_KIND: Option[] = [
  { value: "", label: "Highway", title: "Highway lanes (4 m)" },
  { value: "local", label: "Local", title: "Local road lanes (3 m)" },
];
const PLACE: Option[] = [{ value: "", label: "Sign default" }, ...ALIGN];
const DIAGRAM_SHAPES: Option[] = [
  { value: "exit-left", label: "Exit left" }, { value: "exit-right", label: "Exit right" },
  { value: "fork", label: "Fork" }, { value: "roundabout", label: "Roundabout" },
];
// How many of an exit's lanes are through lanes too. Diagrams made before this setting draw every exit lane from
// the through lanes (no value: neither button is on unless that is one lane).
const SHARED_LANES: Option[] = [
  { value: "0", label: "Exit only", title: "The exit lanes are extra lanes beside the through lanes, ending at the exit" },
  { value: "1", label: "One shared", title: "The nearest exit lane is also a through lane (an option lane); any others are exit only" },
];
// Whether a fork's branches share a lane (the middle lane goes either way).
const FORK_SHARED: Option[] = [
  { value: "0", label: "Exit only", title: "Each lane goes one way only" },
  { value: "1", label: "One shared", title: "The middle lane goes either way (an option lane)" },
];

/**
 * A fork's lanes: how many go left and right, and whether they share one. Forks made before these settings have
 * "exitLanes" each way from the edges of their "lanes" (and are drawn so until changed here).
 */
const ForkLanesField = (props: { item: Item; onChange: (patch: Partial<Item>) => void }) => {
  const { item } = props;
  const old = item.lanesLeft == null;
  const exits = item.exitLanes ?? 1, total = item.lanes ?? 2;
  const left: number = old ? exits : item.lanesLeft;
  const right: number = old ? exits : item.lanesRight ?? left;
  const shared = old ? (total === exits * 2 - 1 ? "1" : total === exits * 2 ? "0" : "") : String(item.sharedLanes ?? 0);
  const set = (l: number, r: number, s: string) =>
    props.onChange({ lanesLeft: l, lanesRight: r, sharedLanes: Number(s || 0), lanes: undefined, exitLanes: undefined });
  return (
    <>
      <div className={styles.inlineRow}>
        <Field label="Lanes left"><NumberBox compact value={left} min={1} max={4} onChange={(l) => set(l, right, shared)} /></Field>
        <div style={{ width: "12rem" }} />
        <Field label="Lanes right"><NumberBox compact value={right} min={1} max={4} onChange={(r) => set(left, r, shared)} /></Field>
      </div>
      <Field label="Middle lane"><Choice options={FORK_SHARED} value={shared} onChange={(s) => set(left, right, s)} /></Field>
    </>
  );
};
const ROUNDABOUT_EXITS: Option[] = ["left", "up-left", "up", "up-right", "right"].map((d) => ({ value: d, icon: <ArrowIcon direction={d} />, title: d }));
// LED colors: amber (the default, no value), white, green, red.
/**
 * A panel's or chip's colors: one of the theme's sign colors, or Custom (any background and legend color, stored as
 * "background" / "foreground" and taking precedence over the scheme).
 */
const BoxColorField = (props: { label: string; item: Item; theme?: Theme; onChange: (patch: Partial<Item>) => void }) => {
  const { item } = props;
  const custom = !!item.background;
  return (
    <>
      <Field label={props.label}>
        <Swatches schemes={props.theme?.def?.schemes ?? {}} scheme={item.scheme ?? ""} custom={custom}
          onScheme={(scheme) => props.onChange({ scheme, background: undefined, foreground: undefined })}
          onCustom={() => props.onChange({ background: item.background ?? "#000000", foreground: item.foreground ?? "#ffffff" })} />
      </Field>
      {custom && (
        <div className={styles.customColors}>
          <Field label="Background"><ColorBox value={item.background} onChange={(background) => props.onChange({ background })} /></Field>
          <Field label="Legend"><ColorBox value={item.foreground ?? "#ffffff"} onChange={(foreground) => props.onChange({ foreground })} /></Field>
        </div>
      )}
    </>
  );
};

const LED_COLORS = [{ value: "", color: "#ffb000", title: "Amber" }, { value: "#f4f4f4", title: "White" }, { value: "#46ff74", title: "Green" }, { value: "#ff4031", title: "Red" }];

/** An LED color: one of the usual ones or any color. <value> undefined is the default amber. */
const LedColorField = (props: { label: string; value?: string; onChange: (color: string | undefined) => void }) => {
  const known = LED_COLORS.some((c) => c.value && c.value === props.value?.toLowerCase());
  const custom = !!props.value && !known;
  return (
    <Field label={props.label}>
      <ColorRows colors={LED_COLORS.map((c) => ({
        color: c.color ?? c.value, title: c.title, on: c.value ? c.value === props.value?.toLowerCase() : !props.value, onClick: () => props.onChange(c.value || undefined),
      }))} custom={{ on: custom, onClick: () => props.onChange(props.value ?? "#ff8800") }} />
      {custom && <ColorBox value={props.value!} onChange={(c) => props.onChange(c)} />}
    </Field>
  );
};
const CHIP_SPAN: Option[] = [{ value: "", label: "Fit" }, { value: "full", label: "Full width", title: "As wide as the column it is in" }];
const TAB_SIDE: Option[] = [{ value: "left", label: "Left" }, { value: "center", label: "Center" }, { value: "right", label: "Right" }];
// A panel across the sign (a band) or down one of its sides, its full height (e.g. the black HOV diamond panel).
const PANEL_SIDE: Option[] = [
  { value: "", label: "Across", title: "A band across the sign's full width" },
  { value: "left", label: "Left side", title: "Down the sign's left edge, its full height" },
  { value: "right", label: "Right side", title: "Down the sign's right edge, its full height" },
];

/** Buttons (parts and presets to add), wrapped onto more rows where they do not fit. */
const Palette = (props: { entries: { label: string; onClick: () => void }[] }) => (
  <Flow>{props.entries.map((e) => <div key={e.label} className={styles.chip} onClick={e.onClick}>{e.label}</div>)}</Flow>
);

/** Alignment of one part within its column, overriding the sign's "Align lines". */
const PlaceField = (props: { item: Item; onChange: (patch: Partial<Item>) => void }) => (
  <Field label="Align this part"><Choice options={PLACE} value={props.item.place ?? ""} onChange={(place) => props.onChange({ place: place || undefined })} /></Field>
);

/**
 * A color: its default (no value: the sign's or chip's legend color, or none), one of the theme's colors, or any color.
 */
const ColorField = (props: {
  label: string; value?: string; theme?: Theme; defaultLabel: string; defaultTitle: string; onChange: (color: string | undefined) => void;
}) => {
  const palette = Array.from(new Set(Object.values(props.theme?.def.schemes ?? {}).flatMap((s) => [s.fg, s.bg].map((c) => c.toLowerCase()))));
  const color = props.value;
  const custom = !!color && !palette.includes(color.toLowerCase());
  return (
    <Field label={props.label}>
      <ColorRows
        lead={[<div key="default" className={!color ? styles.choiceOn : styles.choice} title={props.defaultTitle} onClick={() => props.onChange(undefined)}>{props.defaultLabel}</div>]}
        colors={palette.map((c) => ({ color: c, on: color?.toLowerCase() === c, onClick: () => props.onChange(c) }))}
        custom={{ on: custom, onClick: () => props.onChange(color ?? "#ff8800") }} />
      {custom && <ColorBox value={color!} onChange={(c) => props.onChange(c)} />}
    </Field>
  );
};

/** Color of a text, arrow, symbol or divider: the default (the sign's or chip's legend color), a theme color, or any. */
const PartColorField = (props: { item: Item; theme?: Theme; onChange: (patch: Partial<Item>) => void }) => (
  <ColorField label="Color" value={props.item.color} theme={props.theme} defaultLabel="Default" defaultTitle="The sign's legend color"
    onChange={(color) => props.onChange({ color })} />
);

const SYMBOL_DISPLAY: Option[] = [
  { value: "", label: "Symbol only" },
  { value: "box", label: "In a box", title: "Centered in a square box with rounded corners" },
];
const BOX_BORDER: Option[] = [{ value: "", label: "Line" }, { value: "none", label: "None" }];

/** A symbol from its reference ("<library>/<id>"). */
const symbolOf = (libraries: Record<string, SymbolLibrary>, ref?: string): LibrarySymbol | undefined => {
  if (!ref) return undefined;
  const [lib, ...id] = ref.split("/");
  return libraries[lib]?.symbols?.[id.join("/")];
};

// Symbols picked lately, most recent first (kept between sessions where the page can store them).
const RECENT_KEY = "signBuilder.recentSymbols";
const recentSymbols = (): string[] => {
  try { return JSON.parse(localStorage.getItem(RECENT_KEY) ?? "[]"); } catch { return []; }
};
const rememberSymbol = (ref: string) => {
  try { localStorage.setItem(RECENT_KEY, JSON.stringify([ref, ...recentSymbols().filter((r) => r !== ref)].slice(0, 12))); } catch { /* not stored */ }
};

/** An arrow picked in the symbol picker: the part becomes an Arrow part ("library": another library's arrows). */
export type PickedArrow = { library?: string; style: string; direction: string };

/**
 * Picks a symbol, or an arrow: the theme's own libraries by default, any other library from the list. Symbols are
 * grouped by category, arrows by style; only the category shown is drawn. Search covers the symbols of the library
 * shown, or of every library on request. Drawn as a shade over the whole designer (as its discard question is):
 * inside the parts panel it would be laid out and clipped there.
 */
const SymbolPicker = (props: {
  libraries: Record<string, SymbolLibrary>; own: string[]; value?: string; themeId: string; version: number;
  onPick: (ref: string) => void; onPickArrow: (arrow: PickedArrow) => void; onClose: () => void;
}) => {
  const renderer = useRenderer();
  const others = Object.values(props.libraries)
    .filter((l) => !props.own.includes(l.id) && (Object.keys(l.symbols ?? {}).length > 0 || Object.keys(l.arrows ?? {}).length > 0))
    .sort((a, b) => a.name.localeCompare(b.name));
  const [library, setLibrary] = useState(props.value && !props.own.includes(props.value.split("/")[0]) ? props.value.split("/")[0] : "");
  const [search, setSearch] = useState("");
  const [everywhere, setEverywhere] = useState(false);
  const shown = library ? [library] : props.own;
  const all = (ids: string[]) => ids.flatMap((id) => Object.entries(props.libraries[id]?.symbols ?? {}).map(([key, sym]) => ({ ref: `${id}/${key}`, lib: id, sym })));
  const entries = all(shown);
  // The arrows of the theme (as the sign draws them), or of the library shown.
  const arrowStyles = renderer ? renderer.arrowStyles(props.themeId, library ? 2 : props.version, library || undefined) : [];
  const arrowCategory = (label: string) => "Arrows: " + label;
  const categories = [...Array.from(new Set(entries.map((e) => e.sym.category ?? "Other"))).sort(), ...arrowStyles.map((s) => arrowCategory(s.label))];
  const [category, setCategory] = useState<string | null>(null);
  const current = category && categories.includes(category) ? category : categories[0];
  const term = search.trim().toLowerCase();
  const matches = (e: { ref: string; sym: LibrarySymbol }) => !term || e.sym.name.toLowerCase().includes(term) || (e.sym.number ?? "").toLowerCase().includes(term) || e.ref.toLowerCase().includes(term);
  const list = term ? all(everywhere ? Object.keys(props.libraries) : shown).filter(matches) : entries.filter((e) => (e.sym.category ?? "Other") === current);
  const recent = recentSymbols().filter((r) => symbolOf(props.libraries, r));
  const pick = (ref: string) => { rememberSymbol(ref); props.onPick(ref); props.onClose(); };
  const arrowStyle = !term ? arrowStyles.find((s) => arrowCategory(s.label) === current) : undefined;
  const arrowCells = arrowStyle ? arrowStyle.directions.map((d) => (
    <div key={d} className={styles.symbolCell} title={`${arrowStyle.label}: ${d}`}
      onClick={() => { props.onPickArrow({ library: library || undefined, style: arrowStyle.id, direction: d }); props.onClose(); }}>
      <ThemeArrowIcon theme={props.themeId} style={arrowStyle.id} direction={d} version={library ? 2 : props.version} library={library || undefined} box={arrowStyle.box} size={36} />
    </div>
  )) : null;
  const cell = (e: { ref: string; lib: string; sym: LibrarySymbol }) => (
    <div key={e.ref} className={e.ref === props.value ? styles.symbolCellOn : styles.symbolCell} onClick={() => pick(e.ref)}
      title={e.sym.name + (e.sym.number ? ` (${e.sym.number})` : "") + (props.own.includes(e.lib) ? "" : ` · ${props.libraries[e.lib]?.name ?? e.lib}`)}>
      <SymbolIcon symbol={e.sym} size={40} />
    </div>
  );
  const cells = arrowCells ?? list.map(cell);
  return (
    <div className={styles.confirmShade} onClick={props.onClose}>
    <div className={`${styles.dialog} ${styles.symbolDialog}`} onClick={(e) => e.stopPropagation()}>
      <div className={styles.dialogHeader}>
        <div className={styles.dialogTitle}>Choose a symbol</div>
        <div className={styles.dialogHeaderSpace} />
        <div className={styles.dialogClose} title="Close" onClick={props.onClose} />
      </div>
      <div className={styles.symbolTop}>
        <div className={styles.symbolSearch}><TextBox value={search} onChange={(v) => { setSearch(v); setEverywhere(false); }} /></div>
        <div className={styles.symbolLibrary}>
          <Select value={library} onChange={(v) => { setLibrary(v); setCategory(null); }}
            options={[{ value: "", label: "This theme's" }, ...others.map((l) => ({ value: l.id, label: l.name, title: l.description }))]} />
        </div>
      </div>
      {recent.length > 0 && !term && (
        <div className={styles.symbolRecent}>
          {recent.map((ref) => cell({ ref, lib: ref.split("/")[0], sym: symbolOf(props.libraries, ref)! }))}
        </div>
      )}
      <div className={styles.symbolBody}>
        {!term && (
          <div className={styles.symbolCategories}>
            {categories.map((c) => <div key={c} className={c === current ? styles.choiceOn : styles.choice} onClick={() => setCategory(c)}>{c}</div>)}
          </div>
        )}
        <Scrollable vertical trackVisibility="scrollable" className={styles.symbolGrid}>
          {/* Rows of eight: a wrapped flex row does not grow in Gameface. */}
          {Array.from({ length: Math.ceil(cells.length / 8) }, (_, r) => <div key={r} className={styles.symbolCells}>{cells.slice(r * 8, r * 8 + 8)}</div>)}
          {cells.length === 0 && (
            <div className={styles.symbolEmpty}>
              {term && !everywhere ? <div className={styles.chip} onClick={() => setEverywhere(true)}>Search all libraries</div>
                : term ? "No symbol matches." : "This library has no symbols."}
            </div>
          )}
        </Scrollable>
      </div>
    </div>
    </div>
  );
};

/** The symbol part's button: the chosen symbol and its name; asks the designer to open the picker. */
const SymbolField = (props: { item: Item; theme?: Theme; libraries: Record<string, SymbolLibrary>; onOpen: () => void }) => {
  const renderer = useRenderer();
  const sym = symbolOf(props.libraries, props.item.symbol);
  const lib = props.item.symbol ? String(props.item.symbol).split("/")[0] : "";
  const own = renderer && props.theme ? renderer.symbolLibraries(props.theme.id) : [];
  const missing = !!props.item.symbol && !sym;
  return (
    <Field label="Symbol">
      <div className={styles.symbolButton} onClick={props.onOpen}>
        <SymbolIcon symbol={sym} size={28} />
        <div className={styles.symbolButtonText}>
          {sym ? sym.name : missing ? `Missing: needs the "${lib}" library` : "Choose symbol…"}
          {sym && !own.includes(lib) && <span className={styles.symbolButtonLib}> · {props.libraries[lib]?.name ?? lib}</span>}
        </div>
      </div>
    </Field>
  );
};

/** Extra space around a part (sign units), added to the usual spacing: top, bottom, left, right. */
const MarginField = (props: { item: Item; onChange: (patch: Partial<Item>) => void }) => {
  const m = props.item.margin ?? {};
  const side = (key: string, label: string) => (
    <div className={styles.marginSide}>
      <div className={styles.marginLabel}>{label}</div>
      <NumberBox compact value={m[key] ?? 0} min={-40} onChange={(v) => {
        const next = { ...m, [key]: v || undefined };
        props.onChange({ margin: Object.values(next).some((x) => x) ? next : undefined });
      }} />
    </div>
  );
  return (
    <Field label="Margin">
      <div className={styles.marginGrid}>
        <div className={styles.inlineRow}>{side("top", "Top")}{side("bottom", "Bottom")}</div>
        <div className={styles.inlineRow}>{side("left", "Left")}{side("right", "Right")}</div>
      </div>
    </Field>
  );
};

function ItemProperties(props: {
  item: Item; onChange: (patch: Partial<Item>) => void; focusToken: number; inRow: boolean; theme?: Theme; libraries: Record<string, SymbolLibrary>;
  /** The sign's design format (its arrows: 1.0's, or the theme's libraries'). */
  version?: number;
  /** Opens the symbol picker for this part. */
  onPickSymbol: () => void;
}) {
  const { item, onChange, theme } = props;
  const renderer = useRenderer();
  const def = theme?.def;
  const place = (
    <>
      {!props.inRow && <PlaceField item={item} onChange={onChange} />}
      <MarginField item={item} onChange={onChange} />
    </>
  );
  const fonts: Option[] = (def?.fonts ?? []).filter((f) => !f.hidden).map((f) => ({ value: f.family, label: f.label ?? f.family, style: { fontFamily: f.family } }));
  const networks: Option[] = Object.entries(def?.shields ?? {}).map(([id, s]) => ({ value: id, icon: <ThemeShieldIcon theme={theme} network={id} />, title: s.label ?? id }));
  const schemes: Option[] = Object.entries(def?.schemes ?? {}).map(([id, s]) => ({
    value: id, title: s.label ?? id, label: " ", style: { backgroundColor: s.bg, minWidth: "28rem", border: `2rem solid ${s.fg}` },
  }));
  switch (item.type) {
    case "text":
      return (
        <>
          <Field label="Text"><TextBox value={item.text ?? ""} focusToken={props.focusToken} onChange={(text) => onChange({ text })} /></Field>
          <Field label="Letter height"><NumberBox value={item.height ?? def?.defaults?.textHeight ?? 16} onChange={(height) => onChange({ height })} /></Field>
          <Field label="Letter spacing"><NumberBox value={item.tracking ?? 0} step={2} min={-20} onChange={(tracking) => onChange({ tracking: tracking || undefined })} /></Field>
          {fonts.length > 1 && <Field label="Font"><Select options={fonts} value={item.font ?? def?.defaults?.font ?? ""} onChange={(font) => onChange({ font })} /></Field>}
          <PartColorField item={item} theme={theme} onChange={onChange} />
          {place}
        </>
      );
    case "shield":
      return (
        <>
          <Field label="Route"><Choice options={networks} value={item.network} onChange={(network) => onChange({ network })} /></Field>
          <Field label="Number"><TextBox value={String(item.number ?? "")} focusToken={props.focusToken} onChange={(number) => onChange({ number })} /></Field>
          <Field label="Size"><NumberBox value={item.height ?? def?.defaults?.shieldHeight ?? 36} step={2} onChange={(height) => onChange({ height })} /></Field>
          {place}
        </>
      );
    case "arrow": {
      // The theme's arrow styles and their directions come from its symbol libraries (through the renderer).
      const library = item.library as string | undefined;
      const styles_ = renderer && theme ? renderer.arrowStyles(theme.id, library ? 2 : props.version, library) : [];
      const sources: Option[] = [{ value: "", label: "This theme's" }, ...Object.values(props.libraries)
        .filter((l) => Object.keys(l.arrows ?? {}).length > 0).sort((a, b) => a.name.localeCompare(b.name))
        .map((l) => ({ value: l.id, label: l.name, title: l.description }))];
      const version = library ? 2 : props.version;
      const styleId = arrowStyleOf(item);
      const arrowStyle = styles_.find((s) => s.id === styleId) ?? styles_[0];
      const directionsOf = (id: string) => styles_.find((s) => s.id === id)?.directions ?? [];
      // An arrow already on the sign stays selectable even where its style does not offer it.
      const directions = [...(arrowStyle?.directions ?? [])];
      if (item.direction && !directions.includes(item.direction)) directions.push(item.direction);
      return (
        <>
          <Field label="Arrows from">
            <Select options={sources} value={library ?? ""} onChange={(lib) => {
              // The new set's styles and directions: keep the arrow's where they exist, otherwise its first.
              const next = renderer && theme ? renderer.arrowStyles(theme.id, lib ? 2 : props.version, lib || undefined) : [];
              const st = next.find((s) => s.id === arrowStyleOf(item)) ?? next[0];
              onChange({ library: lib || undefined, style: st?.id || undefined, direction: st?.directions.includes(item.direction ?? "") ? item.direction : st?.directions[0] });
            }} />
          </Field>
          <Field label="Style">
            <Choice options={styles_.map((s) => ({
              value: s.id, label: s.label, title: s.title,
              icon: <ThemeArrowIcon theme={theme!.id} style={s.id} direction={s.directions.includes("up") ? "up" : s.directions[0]} version={version} library={library} size={14} />,
            }))}
              value={styleId} onChange={(style) => onChange({
                style: style || undefined,
                // Styles share few directions: keep the arrow's if the new style has it, otherwise its first.
                direction: directionsOf(style).includes(item.direction ?? "") ? item.direction : directionsOf(style)[0],
              })} />
          </Field>
          <Field label="Direction">
            <Choice options={directions.map((a) => ({ value: a, icon: <ThemeArrowIcon theme={theme!.id} style={styleId} direction={a} version={version} library={library} box={arrowStyle?.box} />, title: a }))}
              perRow={arrowStyle?.perRow ?? 4} value={item.direction ?? "up"} onChange={(direction) => onChange({ direction })} />
          </Field>
          <Field label="Size"><NumberBox value={item.height ?? def?.defaults?.arrowHeight ?? 24} step={2} onChange={(height) => onChange({ height })} /></Field>
          <PartColorField item={item} theme={theme} onChange={onChange} />
          {place}
        </>
      );
    }
    case "row":
      return (
        <>
          <Field label="Line up parts by their"><Choice options={ROW_ALIGN} value={item.align ?? "center"} onChange={(align) => onChange({ align })} /></Field>
          <Field label="Spacing"><Choice options={ROW_SPACING} value={item.justify ?? ""} onChange={(justify) => onChange({ justify: justify || undefined })} /></Field>
          {place}
        </>
      );
    case "column":
      return (
        <>
          <Field label="Align within the column"><Choice options={ALIGN} value={item.align ?? "start"} onChange={(align) => onChange({ align })} /></Field>
          {place}
        </>
      );
    case "chip":
      return (
        <>
          <BoxColorField label="Chip color" item={item} theme={theme} onChange={onChange} />
          <Field label="Border"><Choice options={[{ value: "no", label: "None" }, { value: "yes", label: "Legend color" }]} value={item.border ? "yes" : "no"}
            onChange={(v) => onChange({ border: v === "yes" ? true : undefined })} /></Field>
          <Field label="Width"><Choice options={CHIP_SPAN} value={item.span ?? ""} onChange={(span) => onChange({ span: span || undefined })} /></Field>
          <Field label="Align within the chip"><Choice options={ALIGN} value={item.align ?? "center"} onChange={(align) => onChange({ align })} /></Field>
          {place}
        </>
      );
    case "panel":
      return (
        <>
          <BoxColorField label="Panel color" item={item} theme={theme} onChange={onChange} />
          <Field label="Position"><Choice options={PANEL_SIDE} value={item.side ?? ""} onChange={(side) => onChange({ side: side || undefined })} /></Field>
          <Field label="Align within the panel"><Choice options={ALIGN} value={item.align ?? "center"} onChange={(align) => onChange({ align })} /></Field>
        </>
      );
    case "symbol":
      return (
        <>
          <SymbolField item={item} theme={theme} libraries={props.libraries} onOpen={props.onPickSymbol} />
          <Field label="Size"><NumberBox value={item.height ?? def?.defaults?.shieldHeight ?? 36} step={2} onChange={(height) => onChange({ height })} /></Field>
          <PartColorField item={item} theme={theme} onChange={onChange} />
          <Field label="Display"><Choice options={SYMBOL_DISPLAY} value={item.box ? "box" : ""} onChange={(v) => onChange({ box: v === "box" ? item.box ?? {} : undefined })} /></Field>
          {item.box && (
            <>
              <ColorField label="Box background" value={item.box.background} theme={theme} defaultLabel="None" defaultTitle="No background: the sign shows through"
                onChange={(background) => onChange({ box: { ...item.box, background } })} />
              <Field label="Box border"><Choice options={BOX_BORDER} value={item.box.border === false ? "none" : ""}
                onChange={(v) => onChange({ box: { ...item.box, border: v === "none" ? false : undefined } })} /></Field>
              {item.box.border !== false && (
                <ColorField label="Border color" value={item.box.borderColor} theme={theme} defaultLabel="Legend" defaultTitle="The sign's legend color"
                  onChange={(borderColor) => onChange({ box: { ...item.box, borderColor } })} />
              )}
            </>
          )}
          {place}
        </>
      );
    case "diagram":
      return (
        <>
          <Field label="Shape"><Choice options={DIAGRAM_SHAPES} value={item.shape ?? "exit-left"} onChange={(shape) => onChange({ shape })} /></Field>
          {item.shape === "roundabout" ? (
            <Field label="Exits">
              <Flow>
                {ROUNDABOUT_EXITS.map((o) => {
                  const exits: string[] = item.exits ?? ["left", "up", "right"];
                  const on = exits.includes(o.value);
                  return (
                    <div key={o.value} title={o.title} className={on ? styles.choiceOn : styles.choice}
                      onClick={() => onChange({ exits: on ? exits.filter((x) => x !== o.value) : [...exits, o.value] })}>{o.icon}</div>
                  );
                })}
              </Flow>
            </Field>
          ) : item.shape === "fork" ? (
            <ForkLanesField item={item} onChange={onChange} />
          ) : (
            <div className={styles.inlineRow}>
              <Field label="Through lanes"><NumberBox compact value={item.lanes ?? 2} min={1} max={6} onChange={(lanes) => onChange({ lanes })} /></Field>
              <div style={{ width: "12rem" }} />
              <Field label="Exit lanes"><NumberBox compact value={item.exitLanes ?? 1} min={1} max={4} onChange={(exitLanes) => onChange({ exitLanes })} /></Field>
            </div>
          )}
          {item.shape !== "roundabout" && item.shape !== "fork" && (
            <Field label="Exit lanes from through lanes">
              <Choice options={SHARED_LANES} value={item.sharedLanes != null ? String(item.sharedLanes) : (item.exitLanes ?? 1) === 1 ? "1" : ""}
                onChange={(v) => onChange({ sharedLanes: Number(v) })} />
            </Field>
          )}
          <Field label="Size"><NumberBox value={item.height ?? 50} step={2} onChange={(height) => onChange({ height })} /></Field>
          <PartColorField item={item} theme={theme} onChange={onChange} />
          {place}
        </>
      );
    case "led": {
      const lines: string[] = item.lines ?? [""];
      const setLine = (k: number, text: string) => onChange({ lines: lines.map((l, i) => (i === k ? text : l)) });
      return (
        <>
          <Field label="Lines">
            {lines.map((line, k) => (
              <div key={k} className={styles.inlineRow} style={{ marginBottom: "3rem" }}>
                <div style={{ width: "200rem" }}><TextBox value={line} focusToken={k === 0 ? props.focusToken : undefined} onChange={(t) => setLine(k, t)} /></div>
                {lines.length > 1 && <div className={styles.rowDelete} title="Remove this line" onClick={() => onChange({ lines: lines.filter((_, i) => i !== k) })}>✕</div>}
              </div>
            ))}
            <div className={styles.chip} style={{ alignSelf: "flex-start" }} onClick={() => onChange({ lines: [...lines, ""] })}>Add line</div>
          </Field>
          <Field label="Letter height"><NumberBox value={item.height ?? 12} onChange={(height) => onChange({ height })} /></Field>
          <LedColorField label="LED color" value={item.color} onChange={(color) => onChange({ color })} />
          {place}
        </>
      );
    }
    case "divider":
      return (
        <>
          <PartColorField item={item} theme={theme} onChange={onChange} />
          {place}
        </>
      );
    case "tab":
      return (
        <>
          <Field label="Position"><Choice options={TAB_SIDE} value={item.align ?? "right"} onChange={(align) => onChange({ align })} /></Field>
          <Field label="Color"><Choice options={[{ value: "", label: "Sign" }, ...schemes]} value={item.scheme ?? ""} onChange={(scheme) => onChange({ scheme: scheme || undefined })} /></Field>
        </>
      );
    default:
      return place;
  }
}

export type DesignerAction = { label: string; primary?: boolean; onSelect: () => void; title?: string };

/**
 * The sign designer: one sign at a time. Templates on the left, live preview and sign options in the middle,
 * parts on the right. Everything offered (fonts, colors, route plates, parts) comes from the sign's theme; the
 * actions row depends on what the sign is for (new sign, editing a placed one, a gantry sign).
 */
export const Designer = (props: {
  design: Design;
  setDesign: (d: Design) => void;
  /** Loads a fresh design (template or blank sign), asking first if the current one has unsaved changes; <then> runs once loaded. */
  onLoad: (d: Design, then: () => void) => void;
  onTemplateSaved: () => void;
  templates: Template[];
  themes: Theme[];
  /** The symbol libraries (arrows, symbols), by id. */
  libraries: Record<string, SymbolLibrary>;
  title: string;
  actions: DesignerAction[];
  onClose: () => void;
  /** Drawn over the dialog's content (the discard-changes question). */
  overlay?: ReactNode;
  /** More options under the sign's own (a placed roadside sign's lights). */
  extra?: ReactNode;
}) => {
  const { design, setDesign } = props;
  const [selected, setSelected] = useState<string | null>(null);
  // The symbol picker, open for the selected symbol part.
  const [picking, setPicking] = useState(false);
  const renderer = useRenderer();
  useEffect(() => setPicking(false), [selected]);
  // For the in-game tests: select a part the way a click does, open the symbol picker, read the design (test builds only).
  useEffect(() => {
    if (!__SIGNBUILDER_TESTS__) return;
    (window as any).__signBuilderSelect = setSelected;
    (window as any).__signBuilderPickSymbol = () => setPicking(true);
    return () => { delete (window as any).__signBuilderSelect; delete (window as any).__signBuilderPickSymbol; };
  }, []);
  useEffect(() => { if (__SIGNBUILDER_TESTS__) (window as any).__signBuilderDesign = design; }, [design]);
  // Bumped when a part is picked on the preview, so its text box takes focus with the caret at the end.
  const [focusToken, setFocusToken] = useState(0);
  const theme = themeOf(props.themes, design);
  const def = theme?.def;
  const custom = !!design.background;
  const widthMode = design.minLanes ? "lanes" : design.minWidth ? "custom" : "auto";
  const labels = Object.fromEntries(Object.entries(def?.shields ?? {}).map(([id, s]) => [id, s.label ?? id]));

  const item = selected != null ? getItem(design, selected) : null;
  const update = (patch: Partial<Item>) => { if (selected != null) setDesign(updateItem(design, selected, patch)); };
  // <insert> "first": the preset goes at the top of the sign (a panel across its top, like an E-ZPass panel).
  const add = (made: Item, insert?: "first") => {
    if (insert === "first") {
      const d = clone(design);
      d.items.unshift(clone(made));
      setDesign(d);
      setSelected("0");
      return;
    }
    // Tabs and panels belong to the sign itself, so they go at its top level (a panel after the selected part's top-level part).
    const anchor = made.type === "tab" ? null : made.type === "panel" ? selected?.split(".")[0] ?? null : selected;
    const r = insertItem(design, anchor, clone(made));
    setDesign(r.design);
    setSelected(r.path);
  };
  const templates = props.templates.filter((t) => (t.design.theme ?? "us") === (theme?.id ?? "us"));

  return (
    <Dialog dock="center" title={props.title} className={styles.panel} onClose={props.onClose}>
      <div className={styles.body}>
        <div className={styles.column}>
          <div className={styles.heading}>Theme</div>
          <div className={styles.inlineRow}>
            <Select options={[...props.themes].sort((a, b) => a.name.localeCompare(b.name)).map((t) => ({ value: t.id, label: t.name, title: t.def.description }))} value={theme?.id ?? "us"}
              onChange={(id) => { const to = props.themes.find((t) => t.id === id); if (to) { setDesign(convertDesign(design, to)); setSelected(null); } }} />
            <div className={styles.chip} style={{ margin: "0 0 0 6rem" }} title="Add your own themes here (one folder each); they appear when the Sign Builder is opened again"
              onClick={() => openThemesFolder()}>Folder</div>
          </div>
          <div className={styles.heading}>Templates</div>
          <TemplateLibrary templates={templates} current={design}
            onPick={(t) => props.onLoad(clone(t.design), () => setSelected(null))} onSaved={props.onTemplateSaved} />
          <div className={styles.chip} style={{ alignSelf: "flex-start", marginTop: "6rem" }} onClick={() => props.onLoad(blankFor(theme), () => setSelected(null))}>New blank sign</div>
        </div>

        <div className={styles.center}>
          {(design.formatVersion ?? 1) < DESIGN_FORMAT && usesArrows(design) && (
            <div className={styles.legacyNote}>
              <div className={styles.legacyNoteText}>This sign keeps the arrows it was made with (Sign Builder 1.0).</div>
              <div className={styles.chip} title="Draw its arrows and road diagrams with the theme's current arrows"
                onClick={() => setDesign({ ...design, formatVersion: DESIGN_FORMAT })}>Use the new arrows</div>
            </div>
          )}
          <div className={styles.preview}>
            <SignPreview design={design} selected={selected}
              onSelect={(p) => { setSelected(p); if (p) setFocusToken(focusToken + 1); }} maxWidth={560} maxHeight={270} />
          </div>
          <Scrollable vertical trackVisibility="scrollable" className={styles.optionsScroll}>
          <div className={styles.signOptions}>
            <Field label="Sign type">
              <Choice options={[{ value: "", label: "Standard" }, { value: "led", label: "Message board", title: "A black LED panel: lit dot-matrix lettering" }]}
                value={design.board ?? ""} onChange={(board) => {
                  const d: Design = { ...design, board: board || undefined };
                  if (!board) delete d.board;
                  delete d.foreground;
                  delete d.background;
                  setDesign(d);
                }} />
            </Field>
            {design.board === "led" && (
              <LedColorField label="LED color" value={design.foreground}
                onChange={(c) => { const d: Design = { ...design, foreground: c }; if (!c) delete d.foreground; setDesign(d); }} />
            )}
            {design.board !== "led" && (
            <Field label="Color">
              <Swatches schemes={def?.schemes ?? {}} scheme={design.scheme ?? def?.defaults?.scheme ?? ""} custom={custom}
                onScheme={(scheme) => { const d: Design = { ...design, scheme }; delete d.background; delete d.foreground; setDesign(d); }}
                onCustom={() => setDesign({ ...design, background: design.background ?? "#2d6a4f", foreground: design.foreground ?? "#ffffff" })} />
            </Field>
            )}
            {custom && design.board !== "led" && (
              <div className={styles.customColors}>
                <Field label="Background"><ColorBox value={design.background} onChange={(background) => setDesign({ ...design, background })} /></Field>
                <Field label="Legend"><ColorBox value={design.foreground ?? "#ffffff"} onChange={(foreground) => setDesign({ ...design, foreground })} /></Field>
              </div>
            )}
            <Field label="Add part">
              <Palette entries={baseParts(theme).map((p) => ({ label: p.label, onClick: () => add(p.item) }))} />
            </Field>
            {(def?.presets ?? []).length > 0 && (
              <Field label="Add preset">
                <Palette entries={def!.presets!.map((p) => ({ label: p.label, onClick: () => add(p.item, p.insert) }))} />
              </Field>
            )}
            <Field label="Width">
              <div className={styles.inlineRow}>
                <Choice options={[{ value: "auto", label: "Fit text" }, { value: "lanes", label: "Lanes", title: "At least this many lanes wide (4 m lanes at 100% size)" },
                  { value: "custom", label: "At least" }]} value={widthMode}
                  onChange={(v) => {
                    const d: Design = { ...design };
                    delete d.minLanes;
                    delete d.minWidth;
                    delete d.laneKind;
                    if (v === "lanes") d.minLanes = 1;
                    if (v === "custom") d.minWidth = 96;
                    setDesign(d);
                  }} />
                {widthMode === "lanes" && <NumberBox compact value={design.minLanes} min={1} onChange={(minLanes) => setDesign({ ...design, minLanes })} />}
                {widthMode === "lanes" && <Choice options={LANE_KIND} value={design.laneKind ?? ""} onChange={(laneKind) => setDesign({ ...design, laneKind: laneKind || undefined })} />}
                {widthMode === "custom" && <NumberBox compact value={design.minWidth} step={12} onChange={(minWidth) => setDesign({ ...design, minWidth })} />}
              </div>
            </Field>
            <Field label="Align lines"><Choice options={ALIGN} value={design.align ?? "start"} onChange={(align) => setDesign({ ...design, align })} /></Field>
            {props.extra}
          </div>
          </Scrollable>
          <div className={styles.actions}>
            {props.actions.some((a) => a.primary) && props.actions.length > 2 && <div className={styles.placeAs}>Place as:</div>}
            {props.actions.map((a, i) => a.primary ? (
              <div key={a.label} className={styles.primaryButton} title={a.title} onClick={a.onSelect}>{a.label}</div>
            ) : (
              <div key={a.label} className={styles.chip} title={a.title} style={{ alignSelf: "center", marginLeft: i === 0 ? "0rem" : "6rem" }} onClick={a.onSelect}>{a.label}</div>
            ))}
          </div>
        </div>

        <div className={styles.column}>
          {/* The scrollbar keeps its own space: drawn over the parts, it covered the controls at their right edge. */}
          <Scrollable vertical trackVisibility="reserve" className={styles.partsScroll}>
          <div className={styles.heading}>Parts</div>
          <PartsList design={design} selected={selected} onSelect={setSelected} labels={labels}
            onChange={(d, path) => { setDesign(d); setSelected(path); }}
            onDelete={(path) => { setDesign(removeItem(design, path)); setSelected(null); }}
            onDuplicate={(path) => { const r = duplicateItem(design, path); setDesign(r.design); setSelected(r.path); }} />
          {item && (
            <>
              <div className={styles.heading}>{kindOf(item, labels)}</div>
              <ItemProperties item={item} onChange={update} focusToken={focusToken} theme={theme} libraries={props.libraries} version={design.formatVersion ?? 1}
                onPickSymbol={() => setPicking(true)}
                inRow={selected!.includes(".") && getItem(design, selected!.split(".").slice(0, -1).join("."))?.type === "row"} />
            </>
          )}
          </Scrollable>
        </div>
        {picking && item?.type === "symbol" && (
          <SymbolPicker libraries={props.libraries} own={renderer && theme ? renderer.symbolLibraries(theme.id) : []} value={item.symbol}
            themeId={theme?.id ?? "us"} version={design.formatVersion ?? 1}
            onPick={(symbol) => update({ symbol })} onClose={() => setPicking(false)}
            onPickArrow={(a) => update({
              // The part becomes an arrow, at the symbol's size.
              type: "arrow", library: a.library, style: a.style || undefined, direction: a.direction, symbol: undefined, box: undefined, color: item.color,
            })} />
        )}
        {props.overlay}
      </div>
    </Dialog>
  );
};

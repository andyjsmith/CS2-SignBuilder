# Making a Sign Builder theme

A theme gives Sign Builder a country's (or your own) sign style:

- fonts
- sign colors
- route plates (shields)
- arrows and symbols (from symbol libraries, shared between themes)
- spacing
- ready-made parts (presets)
- templates

This folder is a working starter: `MyThemePack` is a theme pack mod with one example theme, "Exampleland".

```
ThemeTemplate/
  MyThemePack/                      a mod whose only content is its themes
    MyThemePack.csproj, Mod.cs      build and publish it with the game's modding toolchain
    Properties/                     Paradox Mods page (PublishConfiguration.xml), thumbnail, publish profiles
    SignBuilderThemes/
      example/                      one folder per theme; the folder name is a fallback id
        theme.json                  the theme
        shields/motorway.svg        route plate artwork
        templates/*.json            ready-made signs shown in the designer
        fonts/                      font files, if the theme ships any
  tools/font_metrics.py             works out a font's "scale" and "offset"
```

## 1. Try it locally first

You don't need to build anything to make a theme:

1. Copy `MyThemePack/SignBuilderThemes/example` to
   `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\SignBuilder\Themes\<your id>`.
   Sign Builder's options have **Open themes folder**, and the designer has a **Folder** button next to the
   theme list.
2. Set `"id"` and `"name"` in `theme.json`. Ids are short and unique, such as `"pl"` or `"my-city"`.
3. Open the Sign Builder again; it reads themes each time it opens. Pick your theme from the **Theme** list.

Edit, close and reopen the designer to see changes. A folder there with the same id as a published theme overrides
it, which is handy while you update a theme you've already published.

## 2. theme.json

Only `id`, `name` and `schemes` are required; everything else has defaults.

```jsonc
{
  "formatVersion": 2,              // the theme.json format; Sign Builder skips formats newer than it reads
  "id": "example",                 // unique id, stored in every sign made with the theme
  "name": "Exampleland",           // shown in the Theme list
  "description": "…",              // shown on hover
  "units": "in",                   // size unit of every number below: "in" (inches) or "mm"
  "laneWidth": 157.5,              // one lane, in units (157.5 in = 4 m): used by "Width: Lanes"
  "defaults": {
    "font": "Noto Sans",           // a family from "fonts"
    "textHeight": 16,              // capital-letter height of a normal legend
    "shieldHeight": 24,            // route plate height
    "arrowHeight": 24,
    "border": 1.5,                 // sign border width
    "cornerRadius": 4,             // sign corner radius
    "lineGap": 0.6,                // gap between lines, × the larger legend height
    "margin": 0.65,                // space inside the border, × the tallest legend
    "itemGap": 0.5,                // gap between parts side by side, × legend height
    "scheme": "motorway"           // default color scheme
  },
  "fonts": [ … ],
  "schemes": { … },
  "shields": { … },
  "libraries": ["mutcd"],          // symbol libraries for arrows, diagram arrowheads and symbols
  "arrow": "M32 0 H68 V88 H100 L50 150 L0 88 H32 Z",
  "exitTab": { "font": "…" },      // font for text in tabs (optional)
  "presets": [ … ]
}
```

Sizes are in the theme's units at full size. Placed signs are scaled to fit the game (the sign size setting, 100% by
default). If your country's sign manual gives sizes in millimeters, use `"units": "mm"` and copy them straight from
the manual.

### Fonts

```json
"fonts": [
  { "family": "MyFont", "file": "fonts/MyFont-Bold.ttf", "label": "My Font Bold", "scale": 1.43, "offset": 0.02 },
  { "family": "Noto Sans", "label": "Noto Sans Bold", "weight": "bold", "scale": 1.4008, "offset": -0.0434 },
  { "family": "MyFontCondensed", "file": "fonts/MyFont-Condensed.ttf", "scale": 1.43, "offset": 0.02, "hidden": true }
]
```

- **`family`:** the name parts and templates use (`"font": "MyFont"`).
- **`file`:** a TTF or OTF in the theme folder.
  - `@us/fonts/RG2014D.ttf` borrows a file from another installed theme.
  - Leave `file` out to use a font the game ships: "Noto Sans", "Noto Sans JP" or
    "Noto Sans SC" (as the built-in Japan, China and Russia themes do). Add `"weight": "bold"` for the bold face.
- **`scale` and `offset`** make a text height mean the height of its capital letters, with the capitals' top
  exactly at the top of the part. Run `python tools/font_metrics.py MyFont-Bold.ttf`, which needs
  `pip install fonttools`.
- **`hidden: true`** keeps a font out of the Font list. It's still usable from templates and presets, for
  example for exit tab numbers.
- Only ship fonts whose license allows redistribution, and add a `SOURCES.txt` with their source and license.

### Color schemes

```json
"schemes": {
  "motorway": { "bg": "#1d4f9c", "fg": "#ffffff", "label": "Motorway" },
  "detour":   { "bg": "#f5c400", "fg": "#111111", "label": "Detour" }
}
```

- `bg` is the sign background and `fg` is the legend and border color.
- Schemes are the sign colors in the designer. Panels, chips and tabs pick from them too.

### Route plates (shields)

Each entry is one kind of plate. The key (`"M"`) is stored in signs, and `label` is shown on hover.

| Field | Meaning |
|---|---|
| `shape` | `img` (artwork with the number drawn on top), `rect` (a colored plate sized to its text), `ellipse`, or `plain` (colored text only) |
| `img` | Artwork file for `img` plates (SVG or PNG), relative to the theme folder |
| `wideImg` | Optional artwork for three-character numbers on `img` plates (drawn `wide` × as wide) |
| `onLight` | Settings that replace these on light backgrounds (white, yellow), e.g. `{ "img": "…", "wideImg": "…" }` for outlined artwork |
| `onWide` | Settings that replace these for three-character numbers, e.g. `{ "num": 0.5, "top": 0.25 }` for a larger number |
| `aspect` | Width ÷ height of `img` and `ellipse` plates |
| `bg`, `fg`, `border`, `borderWidth` | Plate color, number color, optional border color and width (`borderWidth` × height) |
| `num` | Number height × plate height |
| `top` | Where the number sits on `img` plates: its top, × plate height |
| `font`, `wideFont`, `wide` | Number font. Three-digit numbers use `wideFont` and `wide` (width × that) on `img` plates |
| `prefix` | Text before the number, such as `"A "` or `"E "` |
| `radius`, `padX`, `minAspect` | Corner radius, side padding and minimum width ÷ height of `rect` plates (all × height) |
| `default` | Number a new plate starts with |

### Arrows and symbols

Arrows, the arrowhead of road diagrams and symbols come from **symbol libraries** (see
[Symbol libraries](#4-symbol-libraries)), which themes share. List the ones your theme uses, most important first:

```json
"libraries": ["mutcd"]
```

- `"mutcd/overhead"` takes only one arrow category from a library (France uses the U.S. overhead arrows that way).
- Built-in libraries: `mutcd` (United States), `uk`, `china`, `sweden`, and `basic`.
- `basic` holds Sign Builder 1.0's arrows. Every theme gets it last, so a theme without libraries still has arrows.
- Players can still pick symbols and arrows from any other library in the designer.

`arrow` is a single SVG path, drawn pointing **down** in a 100 × 150 box (`M`, `L`, `H`, `V`, `C` and `Z` commands,
all absolute), and turned for the other directions. It's Sign Builder 1.0's way of giving a theme its arrow:

- It draws the straight arrows of signs made with 1.0, so they keep their look.
- In new signs it comes after the theme's libraries, so it's only used for directions they don't have.

### Presets

Presets are ready-made combinations of parts, shown under **Add preset** in the designer:

```json
"presets": [
  { "label": "Exit panel", "item": { "type": "panel", "scheme": "detour", "items": [ … ] } },
  { "label": "Detour banner", "insert": "first", "item": { "type": "panel", "scheme": "detour", "items": [ … ] } }
]
```

- `item` is one part, in the same format as in templates (see below).
- `"insert": "first"` puts the part at the top of the sign instead of after the selected part.

## 3. Templates

Each file in `templates/` is one sign: the same JSON the designer saves. The easiest way to make one:

1. Design the sign in the game with your theme.
2. Click **Save current as template**.
3. Copy the file from `ModsData/SignBuilder/Templates` into your theme's `templates` folder.
4. Give it a unique `"id"` and a `"name"`.

```json
{
  "formatVersion": 2, "theme": "example", "id": "motorway-exit", "name": "Motorway exit", "scheme": "motorway", "align": "center",
  "items": [
    { "type": "tab", "align": "right", "items": [{ "type": "text", "text": "12", "height": 12 }] },
    { "type": "row", "items": [{ "type": "shield", "network": "M", "number": "1" }, { "type": "text", "text": "North" }] },
    { "type": "text", "text": "Capital City" }
  ]
}
```

| Part type | Fields |
|---|---|
| `text` | `text`, `height`, `font`, `color`, `tracking` |
| `shield` | `network` (a key from `shields`), `number`, `height` |
| `arrow` | `direction` (`up`, `down-left`…, or a named one such as `turn-left`, `split-right`, `fork`), `style` (an arrow category of the theme's libraries: `lane`, `overhead`…; none for straight arrows), `library` (another library's arrows), `height`, `color` |
| `symbol` | `symbol` (`"<library>/<id>"`), `height`, `color`, `box` (`{ "background", "border": false, "borderColor" }`: centered in a square box with rounded corners) |
| `diagram` | `shape` (`exit-left`, `exit-right`, `fork`, `roundabout`), `lanes`, `exitLanes`, `sharedLanes` (`0` or `1`), `exits`, `height` |
| `led` (LED text) | `lines`, `height`, `color` |
| `row`, `column` | `items`, `align`; rows also take `justify` (`between`, `even`) |
| `chip` | `items`, `scheme` (or `background` / `foreground`), `border`, `span` (`full`), `align` |
| `panel` (a band across the sign, or down one side with `side`) | `items`, `scheme` (or `background` / `foreground`), `align`, `side` (`left` or `right`: the panel's full height on that edge, its parts stacked) |
| `tab` (on top of the sign) | `items`, `align` (`left`, `center`, `right`), `scheme` |
| `divider` | `color` |

Any part also takes `margin` (`{ "top", "bottom", "left", "right" }`) and `place` (`start`, `center`, `end`: its
alignment in a column). Sign-level fields are:

- `scheme`, or `background` / `foreground` for custom colors;
- `align`;
- `board: "led"` for a message board;
- `minLanes`, `laneKind` (`"local"`) or `minWidth`.

A sign's `formatVersion` is the design format it was made with, and it never changes on its own. Signs made with
Sign Builder 1.0 (format 1) keep 1.0's arrows; the designer offers to switch them to the current ones. Give new
templates `"formatVersion": 2`.

## 4. Symbol libraries

A symbol library is a folder with a `library.json` and the SVG files of its symbols. Put one in
`ModsData/SignBuilder/Symbols/<id>` to try it, or in a `SignBuilderSymbols` folder of your mod to publish it. A library
with the id of an existing one replaces it (the log says so), so pick a unique id.

```jsonc
{
  "formatVersion": 1,
  "id": "example",                    // stored in signs that use it: never change it once published
  "name": "Exampleland",              // shown in the designer's library lists
  "description": "…",
  "legend": ["#ffffff"],              // fills in the SVGs that take the sign's legend color (the rest keep theirs)
  "arrows": {
    "standard": {                     // a category: "standard" is the straight arrows (no "style"); others are styles
      "label": "Standard", "title": "…", "perRow": 4,
      "order": ["up-left", "up", "up-right", "left", "right", "down-left", "down", "down-right"],
      "shapes": {
        "up":       { "frame": "straight", "d": "…", "points": "up", "rotate": [180] },
        "left":     { "frame": "straight", "d": "…", "points": "left", "rotate": [180] },
        "diagonal": { "frame": "straight", "d": "…", "points": "up-right", "mirror": "both" }
      }
    },
    "lane": {
      "label": "Lane", "perRow": 6,
      "shapes": {
        "up":   { "frame": "straight", "d": "…", "points": "up" },
        "turn": { "frame": "standing", "d": "…", "side": "left", "mirror": true },
        "fork": { "frame": "standing", "d": "…" }
      }
    }
  },
  "diagramHead": { "outline": [[0, 0], …], "backSlope": 0.24 },
  "symbols": {
    "winter-recreation": { "name": "Winter recreation area", "category": "Recreation", "number": "RS-077", "file": "symbols/winter-recreation.svg" }
  }
}
```

**Arrow shapes** are SVG paths with absolute commands (`M`, `L`, `H`, `V`, `C`, `Z`; `A` too for standing arrows),
drawn one way. Their `frame` says how:

- `"straight"`: 150 units long in the direction it `points`, centered on (50, 75).
  - `rotate`: turns (degrees clockwise) that give more directions, e.g. `[180]` makes `up` give `down` too.
  - `mirror` (`"x"`, `"y"` or `"both"`): mirror images. A diagonal is mirrored rather than turned, so it need not be
    at 45°.
- `"standing"`: pointing up, standing on the bottom of a box 150 tall, so arrows of one size stand level in a row.
  - With a `side` (`"left"` or `"right"`), its direction is `<name>-<side>`, such as `turn-left`.
  - `"mirror": true` gives the other side.
  - Without a side, its direction is its name, such as `fork`.

A direction comes from the first library in the theme's list that has it, drawn that way if a shape is, otherwise
turned or mirrored.

**Symbols** are SVG files exported from a sign manual: paths, rectangles, circles, ellipses and polygons, with their
groups' transforms.

- Export each symbol on its own, without a box: the designer draws boxes itself.
- Fills listed in `legend` (the library's, or a symbol's own `"legend"`) take the sign's legend color, as text does.
  Other colors stay as drawn.
- `category` groups symbols in the designer's picker. `number` (the manual's sign number) is searchable.

**Changing a published library:** signs store shape and symbol ids and are redrawn from the library whenever they're
rebuilt (on another PC, or after "Delete unused sign structures").

- Never reuse an id for a different shape.
- Correcting a shape is fine while its size stays within about 5%.
- Anything bigger gets a new id, so placed signs don't change.

## 5. Publishing on Paradox Mods

A theme pack is a small mod. Sign Builder looks for a `SignBuilderThemes` folder in every installed mod,
subscribed or local, so players just subscribe to your pack.

1. Install the modding toolchain from the game: **Options → Modding**.
2. Rename `MyThemePack` (the folder, `.csproj`, and the namespace in `Mod.cs`) and put your theme folders in
   `SignBuilderThemes`.
3. Fill in `Properties/PublishConfiguration.xml`:
   - display name and descriptions;
   - a thumbnail (replace `Properties/Thumbnail.png`; 16:9 works well);
   - screenshots of your signs in game;
   - Sign Builder's Paradox Mods id under `Dependency`, so subscribers get it too.
4. Build once:

   ```bash
   dotnet build -c Release
   ```

   The pack is copied to the game's local `Mods` folder, and your themes show up in Sign Builder as if
   subscribed. Remove your local `ModsData` copy from step 1 if you made one, or it will override the pack.
5. Publish with the `PublishNewMod` profile (in Visual Studio: **Publish**). For example:

   ```bash
   dotnet publish -c Release /p:PublishProfile=PublishNewMod
   ```

   You'll be asked to sign in to Paradox Mods. Put the mod id you get back into `ModId`. Use `PublishNewVersion`
   for updates, and `UpdatePublishedConfiguration` to change the page only.

Keep each theme's `id` stable once published. Signs store it, and placed signs and templates look their theme up
by it. Several themes can share one pack, one folder each.

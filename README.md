# Sign Builder

A Cities: Skylines II code mod for designing road signs in game and placing them standalone or on
gantries. Signs come in themes (one per country or style): United States (MUTCD guide signs with
Interstate, US and state shields, exit tabs and EXIT ONLY panels), Canada, Germany, United Kingdom,
Nordic (Norway and Sweden), France, Netherlands, Italy, Switzerland, Russia, Japan, China and Australia.
Players can add their own themes and templates as files, or publish them as mods.

## Using it

1. Click **SB** in the top-left toolbar to open the designer.
2. Pick a theme and start from one of its templates (or a blank sign). Click any part of the preview to
   select it (its text box takes the caret) and edit it on the right. Signs are built from a few basic
   parts: Text, Shield, Arrow (including curved turn and split lane arrows), Divider, Row, Column, **Chip**
   (a small colored box such as a TOLL or speed plate), **Panel** (a band across the sign's full width, at
   the top, between parts or at the bottom, like an EXIT ONLY panel) and **Tab** (on top of the sign, like
   an exit number plaque). Drag parts in the list to reorder them or drop them into a Row, Column, Chip,
   Panel or Tab. **Diagram** draws the road itself for diagrammatic signs (exit left or right with lane
   lines, fork, roundabout with chosen exits); **LED text** is a dot-matrix LED display (prices, messages), and
   **Sign type: Message board** makes the whole sign a black LED board; LED lettering glows like the game's neon signs. A row can be spread out across the sign (lane arrows under their lanes); text, arrows and
   dividers can take any color; text has a letter spacing setting; any part can have margins. Set the
   colors (or custom colors), width (fit, a number of lanes, or at least a size) and alignment.
   **Save current as template** keeps the sign as a JSON file in
   `ModsData/SignBuilder/Templates`.
3. **Place as:** **Roadside** puts the sign on posts, snapped beside a road or freely placed (right-drag
   rotates); **Prop** places just the sign, no supports, on any surface or hung on the side of an elevated
   road. While placing, Alt + mouse wheel sets the distance from the road and Ctrl + mouse wheel (or
   Page Up / Page Down) the height; **Lights** adds a light fixture below the sign, lit at night.
4. **Gantry** asks for a road, pins the structure there with a live preview and opens the
   arrangement panel: a to-scale view of the lanes as drivers see them. Add signs (any theme), drag them
   sideways (they snap to lane centers, remembered from the nearer edge: "rightmost lane", "2nd lane from
   left"), choose the structure (double truss, single truss, curved or straight tube), Span or Cantilever
   (post on the right or left), Swap directions, height, posts' distance from the road, sign size and
   lights, and signs on the back for the other direction. A span crosses only the road it was placed on:
   click other roads in the world to add them to (or remove them from) the span. **Build** makes it.
   Esc or the close button asks before discarding anything.
5. Click a placed sign with the normal selection tool: its panel has **Edit** (gantries reopen in the
   arrangement panel in place, standalone signs in the designer, with their lights setting; **Save** rebuilds
   them where they stand),
   **Move** and **Duplicate**.

Placed signs are ordinary props: they save with the city, can be bulldozed, and render through the
game's own batching (shadows, distance culling). Saves are shareable: each structure's recipe (sign
designs, support shape, sign positions) is stored inside the save, and a player who loads the city
without your local sign cache gets the signs rebuilt automatically (they need Sign Builder installed).

## How it works

| Part | Where |
| --- | --- |
| Sign renderer: JSON design to HTML, laid out with the design's theme | `SignBuilder/Resources/Renderer/renderer.js` |
| Face baker: renders designs off-screen with the game's UI engine, caches PNGs | `SignBuilder/Rendering/SignFaceBaker.cs` |
| Editor UI (React, built into the mod folder) | `SignBuilder/UI/src/mods/` |
| UI bindings | `SignBuilder/Systems/SignBuilderUISystem.cs` |
| Placement / pick tool | `SignBuilder/Tools/SignPlacementToolSystem.cs` |
| Support geometry (truss, tubes, posts) | `SignBuilder/Spike/GantryFactory.cs`, `MeshBuilder.cs` |
| Assembly build: layout, merged mesh, materials, prefab | `SignBuilder/Assemblies/AssemblyBuilder.cs` |
| Persistence: assembly library re-registered before saves load | `SignBuilder/Assemblies/AssemblyLibrary.cs`, `AssemblyBootstrapSystem.cs` |
| Themes and their templates | `SignBuilder/Resources/Themes/<id>/` (`theme.json`, `fonts/`, `shields/`, `templates/`) |

Each placed structure becomes one prop prefab (`SignBuilder_A_<id>`): the support and sign panels
are merged into one mesh with a steel material and one material per sign face. The meshes and
materials are stored in a mod-owned asset database (`ModsData/SignBuilder/Cache`), and
`ModsData/SignBuilder/assemblies.json` lists every structure so its prefab can be re-created before a
save is deserialized. Recipes also travel in the save (`AssemblySaveData.cs`); after loading, any
structure this PC lacks is rebuilt from its recipe and its objects (kept by the game with a missing
prefab) are respawned in place.

## Building

Requires the official CS2 modding toolchain (Options > Modding in game) and Node.js.

```bash
dotnet build SignBuilder/SignBuilder.csproj -c Debug
```

This compiles the mod, deploys it to `Mods/SignBuilder`, copies `Resources/` and builds the UI
module into the same folder.

## Testing in game

`scripts/run-spike.sh <mode> [save-guid]` launches the game through Steam straight into a save with
the test harness armed, waits for it to quit, and leaves results and screenshots in
`ModsData/SignBuilder/spike`. It needs the Steam launch option
`"<game dir>\Cities2.exe" %command%` (skips the Paradox launcher).

| Mode | What it does |
| --- | --- |
| `e2e` | Places every structure type, runs the arrangement, edit, template and theme checks, saves `SignBuilder_E2E` |
| `reload` | Boots into the E2E save and checks the placed structures came back |
| `reload-e2e` | Both, in one run |
| `uitest` | Opens the editor and idles for a DevTools script |
| `smoke` | Loads, screenshots, quits |

If the requested save id is stale the harness loads the test save (`ModTest`) by name. Runs quit after
9 minutes in game, and the script kills the game at 10 minutes, so a run is never left open.

## Themes

Making a theme, step by step (with a ready-to-copy theme pack to publish on Paradox Mods):
[ThemeTemplate/README.md](ThemeTemplate/README.md). How the mod works inside: [DEVELOPING.md](DEVELOPING.md).


A theme is a folder with `theme.json`: its name, default sizes (every built-in theme uses the same size unit,
roughly inches of a full-size sign), fonts (file,
label, and `scale`/`offset`: em size and vertical offset per letter height, from the font's cap height;
no file and a `weight` uses a family the game ships, such as "Noto Sans JP"; `@us/fonts/…` borrows another
theme's file),
color schemes, route plates (`img` artwork, `ellipse`, colored `rect` plates with optional border and
prefix such as "A " or "E ", or `plain` colored text), the arrow outline, `presets` (ready-made
combinations of the basic parts shown under **Add preset**, such as an EXIT ONLY panel) and a
`templates/` folder. Built-in themes live in `SignBuilder/Resources/Themes`;
players add theirs in `ModsData/SignBuilder/Themes/<id>`. A player folder with a built-in theme's id and
no `theme.json` supplies extra files for it, such as fonts that cannot ship with the mod (the Nordic theme
picks up `Tratex.ttf` or `Trafikkalfabetet.ttf` there). Switching a sign's theme in the designer keeps its
text and sizes; plates and colors the new theme lacks fall back to its defaults.

**Publishing a theme.** Any installed mod can carry themes: put them in a `SignBuilderThemes` folder at the
mod's root (`SignBuilderThemes/<id>/theme.json`, `fonts/`, `shields/`, `templates/`) and publish it to
Paradox Mods. Sign Builder scans subscribed mods (`.cache/Mods/pdx_mods`) and local mods (`Mods`) for that
folder, so players only subscribe to the theme pack. A folder with the same id in
`ModsData/SignBuilder/Themes` overrides a published theme (handy while editing one).

## Credits

- Roadgeek 2014 fonts by Sammi De Guzman (MIT), `Resources/Themes/us/fonts/LICENSE-roadgeek.txt`
- Interstate and US route shield shapes from OpenStreetMap Americana (CC0)
- Alte DIN 1451 Mittelschrift by Peter Wiegel (SIL OFL 1.1), Germany theme
- Transport Medium, Transport Heavy and Motorway Permanent from roads.org.uk: Crown Copyright, Open
  Government Licence v1.0, United Kingdom theme
- D-DIN by Datto Inc. (SIL OFL 1.1), Nordic theme stand-in for Tratex / Trafikkalfabetet
- Canada, Netherlands and Australia reuse the Roadgeek fonts; Italy reuses the Transport fonts
- Japan, China, Russia, France and Switzerland use Noto Sans fonts that ship with the game
- Ontario, Quebec, Trans-Canada and Japanese route plate artwork drawn for Sign Builder (CC0)

## Roadmap

- More themes (and per-country spacing rules); lane arrows placed automatically over the detected lanes

## License

MIT, see [LICENSE](LICENSE). Bundled fonts and artwork keep their own licenses: see
[THIRD-PARTY-NOTICES.md](SignBuilder/Resources/THIRD-PARTY-NOTICES.md).

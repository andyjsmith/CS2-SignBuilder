# Sign Builder: how it works

Developer notes for Sign Builder, a Cities: Skylines II code mod. [README.md](README.md) covers using the mod;
[ThemeTemplate/README.md](ThemeTemplate/README.md) covers making and publishing themes.

## Overview

A sign is a **design**: JSON describing its parts, such as text, route shields, arrows, rows, panels and LED text.
The same design goes through two paths:

1. **In the editor.** The React UI draws the design live with the sign renderer (HTML and CSS), inside the game's
   own UI page.
2. **In the world.** The same renderer draws the design in a hidden, off-screen Gameface view. The pixels are read
   back from the GPU into a texture: the sign's **face**. The face goes onto a panel mesh, which is merged with a
   generated support structure (gantry, posts or none) into one mesh. That mesh is registered as a new static
   object prefab at runtime and placed in the city.

So yes, it is a web page rendered and captured. It is not a screen capture, though: the view is drawn off-screen,
straight into a render texture, and only the sign's own pixels are kept.

```
design JSON ──► renderer.js ──► (editor) live preview in the game UI
                     │
                     └──► off-screen Gameface view ──► GPU readback ──► face texture (+ glow texture)
                                                                              │
road probe ──► layout (where signs go) ──► support mesh + sign panels ──► one mesh, materials
                                                                              │
                                                     runtime StaticObjectPrefab ──► object in the city
```

## Repository layout

| Path | What |
|---|---|
| `SignBuilder/Mod.cs` | Entry point: registers systems, settings and the UI module |
| `SignBuilder/Resources/Renderer/` | `renderer.js` / `renderer.css`: the sign renderer, shared by the editor and the baker. `renderer.html` and `renderer-b.html` are the bake pages; `preview.html` is for desktop browsers |
| `SignBuilder/Resources/Themes/<id>/` | Built-in themes: `theme.json`, `fonts/`, `shields/`, `templates/` |
| `SignBuilder/Rendering/SignFaceBaker.cs` | Off-screen rendering of designs into textures, and the face cache |
| `SignBuilder/Assemblies/` | Building structures: layout (`PlacementContext`), meshes and materials (`AssemblyBuilder`, `SurfaceFactory`), the library of built structures (`AssemblyLibrary`), save data, start-up registration (`AssemblyBootstrapSystem`) and cleanup (`AssemblyCleanup`) |
| `SignBuilder/Spike/` | Lower-level building blocks that grew out of the early experiments: mesh generation (`GantryFactory`, `MeshBuilder`), road probing (`RoadProbe`), runtime prefabs (`RuntimePrefabs`), the steel material, and the in-game test harness (`SpikeRunner*`) |
| `SignBuilder/Tools/SignPlacementToolSystem.cs` | The placement tool: picking roads, the live preview, arranging signs on a gantry, building, editing placed structures |
| `SignBuilder/Systems/` | `SignBuilderUISystem` (bindings between C# and the React UI) and `SignInfoSection` (the selected-object panel section) |
| `SignBuilder/Core/` | Shared services (`SignBuilderRuntime`: asset database, baker, coroutines), theme loading (`Themes`), localized names |
| `SignBuilder/UI/` | The React UI module (TypeScript, built with the game's modding toolchain) |
| `scripts/run-spike.sh` | Launches the game through Steam into a test run and waits for it to finish |
| `ThemeTemplate/` | A starter theme pack to copy |

## The design format

A design is the JSON that `renderer.js` draws:

```json
{
  "theme": "us", "scheme": "green", "align": "center",
  "items": [
    { "type": "tab", "align": "right", "items": [{ "type": "text", "text": "EXIT 27", "height": 10 }] },
    { "type": "row", "items": [{ "type": "shield", "network": "I", "number": "95" }, { "type": "text", "text": "SOUTH" }] },
    { "type": "text", "text": "Washington" },
    { "type": "panel", "scheme": "yellow", "items": [{ "type": "text", "text": "EXIT ONLY" }] }
  ]
}
```

- **Part types:** `text`, `shield`, `arrow`, `diagram`, `message` (LED text), `divider`, `row`, `stack` (a
  column), `chip` (a small colored box), `panel` (a band across the sign) and `tab` (on top of the sign).
- **Sizes** are in the theme's units: roughly inches of a full-size sign for every built-in theme.
- **Spacing** follows the theme's `defaults`. Gaps scale from the legend (letter) height, as sign manuals specify.
- **Sign-level fields:**
  - `board: "led"` makes the sign a message board.
  - `minLanes` / `laneKind` set a minimum width in lanes.
  - `minWidth` sets a minimum width.
- **Per-part fields** are `margin` and `place`.

`UI/src/mods/designModel.ts` edits designs: paths such as `"1.0"` address parts. `themeModel.ts` maps between
themes.

## Rendering a face (SignFaceBaker)

1. **Start-up.** A `UIView` is created on the game's `UISystem` with a dummy, disabled camera. The game never draws
   or presents it. It loads `coui://signbuilder/Renderer/renderer.html`, served from the mod's `Resources` folder.
   This happens once, after a city loads (`Warmup`).
2. **Rendering.** For each design, `window.hsRender(json, pixelsPerInch)` runs in that view (`View.ExecuteScript`).
   The renderer builds the sign as DOM, top-left on a transparent page.
3. **Drawing.** After a few frames for layout, fonts and images, the view is drawn from
   `RenderPipelineManager.endFrameRendering`. The baker issues `BackendBeginFrame`, `View.Draw()` and
   `BackendEndFrame` itself (cohtml render events, reached by reflection).
4. **Readback.** The view's render texture is read back (`AsyncGPUReadback`). The bounding box of non-transparent
   pixels is the sign; its pixel size divided by pixels per inch gives the size in meters. A sign touching the
   view's edge was clipped, so it is drawn again at a smaller scale.
5. **Glow.** Designs with LED lettering get a second pass, `hsRender(json, ppi, true)`. Everything is hidden
   except the LED dots, with the same layout and the same crop. The result is the glow texture.
6. **Cache.** Faces are cached as PNG and JSON in `Cache/Faces`, keyed by a hash of the design, the resolution,
   the renderer sources and a cache version. A design is rendered once, however often it is placed.

If `ExecuteScript` ever stops repainting in a game build, the baker falls back to navigating to the page with the
design in the URL hash. It alternates between two identical pages, so every bake is a full load.

Slow bakes are logged with a breakdown of where the time went. A frame longer than 0.75 s is logged with the last
step Sign Builder was doing (`SignBuilderRuntime.Mark`), so a freeze can be traced afterwards.

### The renderer (renderer.js)

The renderer is plain ES5 with no build step, because the bake page loads it directly. It renders to DOM: flexbox
layout, SVG for arrows, diagrams and LED dots, and `<img>` for shield artwork.

Gameface supports a subset of CSS:

- `gap` is not supported.
- Wrapped flex rows don't grow their container.
- There is no `stroke-dasharray`, so dashes are filled polygons.
- `var(--x, fallback)` drops the whole declaration.
- Font family names containing digits must be quoted.

Each theme's fonts are registered as `@font-face` rules. A font without a file uses a family the game ships, such
as Noto Sans.

LED lettering is the renderer's own 5×7 dot-matrix font (`LED_GLYPHS`), drawn as one SVG path per line.

## Building a structure

1. **Probing the road** (`RoadProbe`). The clicked road edge gives its direction, lanes (positions and travel
   direction), width and elevation. Clicking more roads adds them to a gantry's span.
2. **Layout** (`PlacementContext`, `LayoutOptions`). Sign slots are positioned by lane, offset or automatically,
   on the front or back of the structure, with a scale and height. The result is a span length, post positions
   and sign centers.
3. **Meshes:**
   - `GantryFactory` generates the support for the structure type: double or single truss, curved or straight
     tube, cantilevers, roadside posts, or nothing for props.
   - Each sign gets a panel shaped to its face (tabs leave notches, from `PanelShape`) and optional light
     fixtures.
   - Everything merges into one mesh: submesh 0 is the steel, then one submesh per sign face. A low-detail copy
     becomes the LOD mesh.
4. **Materials** (`SurfaceFactory`):
   - Materials copy a vanilla surface's template (a park light pole's), so the game's batch renderer accepts them.
   - Each face is its own material. Faces with LED lettering also get a glow (emissive) map.
   - The game draws an emissive map only for submeshes that the mesh's `EmissiveProperties` lists as lights. LED
     faces are listed as always-on neon lights (`RuntimePrefabs.AddGlow`, the record's `GlowMeshes`), as the game's
     asset importer does for neon signs. An emissive map on any other material is ignored.
   - The steel material is shared by every structure.
5. **Prefab** (`RuntimePrefabs`). A `StaticObjectPrefab` with a `RenderPrefab` (mesh and materials, and the LOD) is
   added through `PrefabSystem.AddPrefab`. Lights are `EffectSource` entries pointing at a runtime `EffectPrefab`
   with a spot `LightEffect` that is on at night. The object is then spawned at the placement transform.

The meshes, materials and textures are saved as assets in Sign Builder's own asset database
(`ModsData/SignBuilder/Cache`). The structure's recipe goes into the library (`ModsData/SignBuilder/assemblies.json`).

## Persistence and sharing

- **Library.** `AssemblyLibrary` records every built structure: prefab name, asset ids, designs, support spec,
  sign centers and placement context.
- **Start-up.** `AssemblyBootstrapSystem` deletes cached assets no structure uses (`AssemblyCleanup.SweepOrphans`).
  It then registers every recorded structure's prefab from its stored asset ids, before any save is loaded, so
  saved objects resolve.
- **Saves.** Each save also carries the recipes of the structures it uses (`AssemblySaveData`). On a PC without
  those assets, Sign Builder rebuilds them from the recipe: it bakes the faces and regenerates the meshes. A city
  shared with another player only needs the mod installed.
- **Cleanup.**
  - An edited or moved structure's old assets are removed once its object is gone.
  - **Delete unused sign structures** in the options removes structures that have no object in the open city.
  - The start-up sweep removes anything left over. It reads the `.cid` file next to each cached asset (it holds
    the asset's id) rather than listing every asset the game knows, which takes seconds.
  - Delete an asset through its own database (`asset.database`), then remove its files.
    `SignBuilderRuntime.Database.DeleteAsset` on an asset indexed by another instance only drops it from the index
    and leaves the files on disk. That's how the cache once grew to 3 GB.
- **Per face on disk:** the base texture, a tiny mask, and for LED lettering a glow map at half the size. Template textures such as the game's blue noise are not copied into our materials.

## The UI (SignBuilder/UI)

- React, built by the game's modding toolchain into `SignBuilder.mjs`.
- **Bindings** in `bindings.ts` mirror `SignBuilderUISystem`. Values are JSON strings (tool mode, arrangement
  state, templates, themes). Triggers are actions (`startGantry`, `arrange`, `savePosts` and so on).
- **`SignEditor.tsx`** is the root. It shows one of:
  - the designer (`Designer.tsx`: templates, preview, sign options, parts list, part properties);
  - the gantry arrangement panel (`Arrange.tsx`);
  - the placement bar (`PlacementBar.tsx`).
- **`SignPreview.tsx`** draws designs with the renderer. Clicks map back to part paths through `data-sb-path`
  attributes. Template thumbnails are drawn two per frame.
- **Dialogs** (`Dialog.tsx`) are positioned with CSS, so they're in place from the first frame. Each gets its own
  focus tree, the way the game's `Panel` builds one: `FocusRoot`, then an `ActiveFocusDiv`, then one
  `AutoNavigationScope` around all content.
- **Focus rules.** The game's inputs and buttons register with the nearest focus node, and each node accepts a
  **single** child. Without a focus root, inputs land in the game UI's root controller. Creating or removing one
  then fails and logs an error, which stalled the game for 150 ms to 2 s per input. Wrap any group of game inputs
  or buttons in one `AutoNavigationScope`, as `SignInfoSection` does for its buttons.
- **Components** must be declared at module level. A component declared inside another's render function is a new
  type on every render, so React recreates it (and its focus registrations) each time.
- **Vanilla modules** come through `getModule`: the text input, color field, drop-down items and focus components.
  Each falls back to plain elements if a game update moves it.

## Building

Requirements: the Cities: Skylines II modding toolchain (`CSII_TOOLPATH`) and Node.js.

```bash
dotnet build SignBuilder/SignBuilder.csproj -c Release
```

The build compiles the mod, copies `Resources` and builds the UI into the game's local `Mods/SignBuilder` folder.
`npm run build` in `SignBuilder/UI` rebuilds only the UI. Publishing uses the toolchain's profiles in
`Properties/PublishProfiles` (`PublishNewMod`, `PublishNewVersion`) with `Properties/PublishConfiguration.xml`.

## Testing

The in-game harness (`Spike/SpikeRunner*.cs`) only runs when `ModsData/SignBuilder/spike.flag` exists.
`scripts/run-spike.sh <mode> [save-guid]` writes the flag, launches the game through Steam (straight into a save),
waits up to 5 minutes for it to start, and kills it after 10 minutes at most.

| Mode | What it covers |
|---|---|
| `e2e` | Everything: every structure type, gantry arranging and editing, roadside editing, free and prop placement, the selected-object panel, performance (worst frame after selecting objects, opening the designer, selecting parts), templates, all themes, and a save. About 8 minutes |
| `ui-e2e` | Only the designer: opening it and switching between parts, with the worst frame time of each. About 2 minutes |
| `reload-e2e` | Loads the save written by `e2e` and checks that every structure came back |

Results go to `ModsData/SignBuilder/spike/results.json`, with screenshots next to it. The game's `Logs/UI.log`
shows UI errors (focus errors included). `Logs/SignBuilder.Mod.log` has Sign Builder's own log: bake timings and
long frames.

For the renderer alone, open `Resources/Renderer/preview.html` in a desktop browser (served over HTTP, so the
fonts load).

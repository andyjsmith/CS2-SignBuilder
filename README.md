# Sign Builder

A Cities: Skylines II code mod for designing road signs in game and placing them on gantries or
posts. North American guide signs come first (green, blue and brown signs with Interstate, US and
state route shields, exit tabs and EXIT ONLY panels); other countries are planned as data packs.

## Using it

1. Click **SB** in the top-left toolbar to open the editor.
2. Start from a template or a blank sign. Click any part of the preview to select it, edit it on
   the right, and add parts (text, shields, cardinal directions, arrows, distances, dividers) from
   the left. Toggle an exit tab or an EXIT ONLY panel and pick the sign colour.
3. Use the sign tabs to put up to four signs on one structure.
4. Choose the mounting: overhead truss, curved or straight tube span, curved or straight
   cantilever, or roadside posts. Choose whether the signs face the clicked road's traffic or the
   opposite direction.
5. Press **Place on road** and hover a road: a preview follows the mouse. Click to build it; it is
   snapped across the road and sized to the carriageway. Right-click leaves placement.
6. To change a placed sign, open the editor and click the sign in the world. It loads back into the
   editor; press **Place on road** and click a road to replace it.

Placed signs are ordinary props: they save with the city, can be bulldozed, and render through the
game's own batching (shadows, distance culling).

## How it works

| Part | Where |
| --- | --- |
| Sign renderer: JSON design to HTML, MUTCD-style layout, Roadgeek fonts, shields | `SignBuilder/Resources/Renderer/renderer.js` |
| Face baker: renders designs off-screen with the game's UI engine, caches PNGs | `SignBuilder/Rendering/SignFaceBaker.cs` |
| Editor UI (React, built into the mod folder) | `SignBuilder/UI/src/mods/` |
| UI bindings | `SignBuilder/Systems/SignBuilderUISystem.cs` |
| Placement / pick tool | `SignBuilder/Tools/SignPlacementToolSystem.cs` |
| Support geometry (truss, tubes, posts) | `SignBuilder/Spike/GantryFactory.cs`, `MeshBuilder.cs` |
| Assembly build: layout, merged mesh, materials, prefab | `SignBuilder/Assemblies/AssemblyBuilder.cs` |
| Persistence: assembly library re-registered before saves load | `SignBuilder/Assemblies/AssemblyLibrary.cs`, `AssemblyBootstrapSystem.cs` |
| Templates | `SignBuilder/Resources/Templates/*.json` |

Each placed structure becomes one prop prefab (`SignBuilder_A_<id>`): the support and sign panels
are merged into one mesh with a steel material and one material per sign face. The meshes and
materials are stored in a mod-owned asset database (`ModsData/SignBuilder/Cache`), and
`ModsData/SignBuilder/assemblies.json` lists every structure so its prefab can be re-created before a
save is deserialised.

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
| `e2e` | Opens the editor, places every mounting type, previews, reverses, re-edits, saves `SignBuilder_E2E` |
| `reload` | Boots into a save and checks the placed structures came back |
| `phase1` | Older renderer/gantry batch |
| `debug` | Bakes one sign and idles with the UI DevTools on `localhost:9444` |
| `smoke` | Loads, screenshots, quits |

For quick renderer work without the game, serve `SignBuilder/Resources` (see `.claude/launch.json`)
and open `Renderer/preview.html`.

## Credits

- Roadgeek 2014 fonts by Sammi De Guzman (MIT), `SignBuilder/Resources/Fonts/LICENSE-roadgeek.txt`
- Interstate and US route shield shapes from OpenStreetMap Americana (CC0)

## Roadmap

- Country packs (Norway, Sweden, UK, Germany first): schemes, fonts, route markers, spacing rules and
  templates as data
- Diagrammatic and lane-arrow signs aligned to detected lanes
- Library cleanup for structures no longer used by any save

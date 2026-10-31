# Custom Buildings for Shadows of Doubt

Version 1.0.1 (editor and plugin). The builds before the 1.0.0 release were numbered 1.1 to 1.9; the setup window offers "Use 1.0.1" if one of those is installed.

Two parts, no other mods needed:

- **CustomBuildings plugin** (BepInEx 6 IL2CPP): loads your buildings and floor plans into the game.
- **SodBuildingEditor** (Windows app): create buildings and paint their floor plans.

The plugin has **no dependencies** besides BepInEx itself (no Asset Bundle Loader, no SOD.Common, no CitiesV2).

## How it works

A custom building is a **copy of a game building** (its outside model, windows, stairs) with **your own floor plans** inside and optional changes to where and how often it spawns.

- Floor plans use the game's own format (`FloorSaveData` JSON). The plugin adds them to `AssetLoader.allFloorData` and `CityData.floorData`.
- The building is a clone of a vanilla `BuildingPreset`, renamed to your Id, with new `floorLayouts`, added to `AssetLoader.allBuildingData`.
- On first start the plugin exports every game floor plan and a catalog of buildings to `Export/`. The editor uses them as templates, dropdown values and for checks.

Everything was written against the game's real class signatures (from the interop assemblies), but it hasn't been compiled or tested in game yet. Expect to fix a few small things on the first build.

## 1. Build

Needs the **.NET 8 SDK** (https://dotnet.microsoft.com/download) and BepInEx 6 IL2CPP installed in the game, started once (so `BepInEx\interop` exists).

```
build.bat
```
or, if the game isn't in the default Steam folder:
```
build.bat "D:\SteamLibrary\steamapps\common\Shadows of Doubt"
```

This copies `CustomBuildings.dll` into your game and makes **`Release\SodBuildingEditor.exe`**: one file that contains the editor *and* the plugin, and runs without installing anything.

## Giving the editor to someone else

Send them just `Release\SodBuildingEditor.exe` (it's large because .NET is inside). When they start it, a setup window:

1. finds Shadows of Doubt through Steam (or lets them pick the folder),
2. checks for BepInEx IL2CPP and can download and install it for them (BepInExPack IL2CPP 6.0.788 from Thunderstore),
3. installs or updates the CustomBuildings plugin from inside the editor,
4. asks them to start the game once so the plugin hands the game's floors to the editor,
5. shows the **optional** mods for the other mod loaders (Asset Bundle Loader, Community Case Loader, DDSLoader, SOD Common, UniversalObjectLoader) with "Install for me" and "Page" buttons. They go into `BepInEx\plugins\<Author>-<Name>`; UniversalObjectLoader brings Asset Bundle Loader and SOD Common along.

Only BepInEx and the CustomBuildings plugin are required (steps 2 and 3); "Continue" never waits for the optional ones.

After that the editor opens the right folder by itself. **File > Game folder and mod setup** shows the setup again. The game has to be closed while installing. Windows may warn about an unknown app the first time ("More info" → "Run anyway"), because the file isn't signed.

For multiplayer, they also need the same `Buildings` folder as you (compare **Tools > Show content hash**).

## 2. First start

Start the game to the main menu (or load a city). Check `BepInEx\LogOutput.log` for:

```
[CustomBuildings] Read 0 building(s) and 0 floor(s). Content hash ...
[CustomBuildings] Exported N vanilla floor(s) and M building(s) to ...\Export
```

## 3. Make a building

1. Open `SodBuildingEditor.exe`. The first time, the setup checks everything; afterwards it opens your mod folder by itself.
2. **Building > New building**: give it an Id (keep a prefix like `cb_`) and pick the game building whose outside you want.
3. On the left, under **Game floors**, select a floor plan and use **Floor > Copy game floor into building**. Starting from a game floor is much safer than drawing from nothing: it keeps all the details the game expects.
4. Edit the copy:
   - **Paint address**: assign cells to an address (an apartment, shop, hallway…). Each address has a preset (`p_n`).
   - **Paint room**: split an address into rooms. Right-click picks the room under the cursor.
   - **Walls**: click near a cell edge to place the selected wall/door/window preset; right-click removes.
   - **Entrances & stairs**: set entrance, main entrance, stairwell and elevator flags on tiles.
   - **Tile type**: floor/ceiling type of cells.
   - Addresses can have several **variations** (alternative room layouts); the game picks one.
5. Select the building and set its **floor layouts**: each layout is a run of floors and the floor plans the game can pick for them. The total number of floors should match the base building's model (the editor shows this).
6. **File > Save**, start the game, and **generate a new city**. Buildings only appear in newly generated cities.

Undo/redo: Ctrl+Z / Ctrl+Y on the floor editor. Ctrl+mouse wheel zooms.

## Which shop or business goes into a unit

A floor plan only says what *type* each unit is (shop, restaurant, home, office…). When the game makes a city it picks the actual kind of place for each unit (pawn shop, chemist, diner…), like for its own buildings, including kinds of places other mods add at game start (e.g. NewLife's).

- Places the game only allows in certain buildings are allowed in your building too when they're allowed in its base building.
- To fix a unit to one kind of place, select the address in the floor editor and pick it under **Always make it this kind of place**. The list shows the places that fit the address type first (start the game once so it can list them). It applies to every storey that uses this floor plan. In the log: `[places] <floor> address N is now <place>`.

## Stairs and lifts

Place only **Entrances & stairs > Stairwell**, on the same tile and with the same rotation on every storey. The **lift comes with the stairs**: it is part of the building's stairwell (tested with TownhouseShops: stairs plus a lift in the middle). Don't add the *Elevator* flag on top; the game only uses that for the Hotel's double stairwell, and on a normal stairs tile it blocks the stairs. **Overrides > Stairs and lift > StairsFrom** can swap in another game building's stairwell for a different look.

## Balconies you can walk on

The **Balcony** tool in the floor editor makes an open-air terrace the way the game's own floors do (for example `MixedIndustrial_ThirdFloor01`):

1. Open an upper floor of your building and pick **Balcony** in the tool bar.
2. Choose the **Edge** (the railing on the open sides: DecoHandrail, Bannister, a wooden fence, or NothingWall for an open edge like the game's terraces) and the **Door** (InteriorDoorway is a normal door).
3. Drag over cells along an outer wall, e.g. one tile (3×3 cells) in a corner, or a strip 1–2 cells deep along a wall.

The cells become a **Rooftop** address with floor but no ceiling (tile type 2). Walls go between the balcony and the building, with the door in the middle of the longest wall to one room. It doesn't work on the pink outside ring, over stairs or lifts, or in the middle of the building. Undo works. You can move the door afterwards with the **Walls** tool.

The balcony is cut into the building, not stuck on the outside. **Make exterior** then sees it as open air: the outer wall steps back around it, the balcony floor is the roof of the storey below, the storey above (if there is one) forms its ceiling, and the door shows on the outside. *Low brick walls around balconies, terraces and setbacks* (on by default) adds a 1-high wall along its open edges; untick it if the game's railing looks better alone.

Not tested in game yet. Things to check: you can walk out through the door, you don't fall off the edge (if the railing doesn't stop you, please report it), and the room behind it still works (power, lights, the address still counts as a home or shop). As always, generate a new city.

## Shopping building in one step

**Building > New shopping building…** (Ctrl+M):

1. Pick what it looks like (any game building; TownhouseShops is a good small one, OneFIfthAve or BrandyNetherland for a tall one).
2. Tick the storeys that should hold shops. Each line says how many homes on it become shops.
3. Choose how many of them become restaurants, and optionally a building name.
4. **Create building**, then **File > Save**, start the game and generate a **new** city.

It copies the building's own floor plans and turns the homes (and, if ticked, offices) into shops and restaurants. Walls, doors, windows, hallways and stairs stay exactly where they were, so everything still lines up with the model. Rooms are renamed to what a shop uses (shop floor, storeroom, office, toilet; a restaurant gets a dining room and kitchen), and a home's hallway is joined to the shop floor.

The game itself decides which kind of shop each one becomes (supermarket, chemist, hardware store, pawn shop…) when it creates the city, the same way it does for its own shops. Every converted floor is a normal floor you can still change in the editor.

## 4. Look at it in 3D (no game needed)

**Building > 3D preview** (Ctrl+3), or the **3D preview…** button on a building, opens a 3D view of the whole building built from your current, unsaved edits. **3D view** on the floor editor's toolbar shows a single floor (game floors too).

### The game's real model

On first start the plugin also exports every game building's **exterior model and textures** to `Export/models/<building>/` (`model.obj`, `model.mtl`, PNGs, `model.json`). The preview puts your floors inside that model, so the building looks like it does in game.

- **Solid / See-through model**, **Night (lit windows)** and **Water and pier** change the look.
- The **Show storeys up to** slider cuts the model and the floors open from the top, so you can look inside.
- **Line the model up with the floors**: the model is fitted to your floor plans automatically. If it's turned, mirrored, too big or the storeys don't match its windows, use **Rotate 90°**, **Mirror**, **Scale %**, **Move X/Z** and **Storey height**. These are saved per base building (`Editor/preview.json`), so you only do it once.
- The exported models are normal OBJ files; you can also open them in Blender.
- Exporting happens once per game version. Delete `Export/models` to export again. `ModelTextureMaxSize` in the plugin config limits texture size (default 1024 px).
- Neon signs, cables and other extras the game adds at runtime aren't part of the export.

### Floors

- Floors are coloured by address, with lighter/darker shades per room. Walls are grey, doors orange, windows blue, stairs are brown ramps, lifts are purple shafts, entrances are green (main entrance gold).
- Each storey lists the floor plan it shows. Where a layout has several plans, pick one or press **Shuffle floor plans** to see other combinations the game could choose. **Random address variations** does the same for address variations.
- **Spread storeys apart** pulls the floors apart to see each one (the model is hidden while spread).
- **Refresh from editor** picks up edits made since the window opened.
- Mouse: left-drag rotates, right-drag pans, wheel zooms, double-click resets.

## Your own exterior

**Building > Make exterior…** (Ctrl+E), or **Make exterior…** on a building, builds an outside for the building from its floor plans, in a neon-arcade style:

- brick walls with neon trims and storey lines, pilasters on every tile, from the first floor up
- awnings over your entrances
- the **ground floor** is left to the game: like for its own buildings, the game builds the ground floor's outside walls itself, from your floor plan (its windows, doors and entrances) in the base building's wall material, and puts the shop signs on it
- windows on the upper storeys, a lit name sign on the roof facing the main entrance, a vertical blade sign, roof vents
- separate night lighting: windows glow at random, neon is always on

Options: the **sign text**, the number of **storeys** (more than now repeats the top floor plan), and **adding windows** to your floor plans where the outer walls have none (so the inside windows match the outside). It is saved in `Buildings/<id>/exterior/` (OBJ + PNGs + exterior.json) and the 3D preview shows it. Make it again after changing the floor plans.

**Style**: *Neon arcade* (as above) or *Plain brick*: brick walls, windows and doors only, with blank signs, as a base to paint yourself.

In game the plugin builds the model from those files and uses it instead of the base building's. If anything goes wrong it keeps the base building's look and says so in the log. The log line `using its own exterior … The game found N of M windows` tells whether the game understood the window map; if N is 0, send that line.

## Paint the exterior

**Building > Paint exterior…** (Ctrl+P), or **Paint exterior…** on a building, opens the painter. It needs an exterior made with **Make exterior** first (pick *Plain brick* there for an empty base).

- On the left are the **walls**: each one is a flat picture of one side of the building, storeys stacked like on the real building with the roof edge on top. "Top side of the plan" is the wall along the top row of your floor plan, and so on; *(front)* is the main entrance side. Below them are the **rooftop sign**, the **blade sign** and **awnings, roof and vents**.
- **Paint on** chooses what a brush stroke means:
  - *Wall colour (daytime)*: the normal look.
  - *Neon*: glows day and night (looks pale in daylight).
  - *Night light*: glows only at night, like lit windows.
  - *Window map*: white where the game treats it as a window it can light up.
- **Tools**: pencil (B), line (L), rectangle (R), fill (F), pick colour (I), and stamps you drag as a box: window (W), shop window (S), door (D), text (T, glows on the neon layer), bricks (K) and picture (P, puts a PNG or JPG such as a logo or poster on the wall; transparent parts are kept). **Snap to cells** lines windows up with the floor grid. Right-click erases glow and windows (on the daytime layer it picks the colour).
- **View**: Day, Night, or *Show window map*. The yellow lines are tile edges (every 3 cells), dotted lines are cells.
- Ctrl+Z / Ctrl+Y undo and redo, Ctrl+mouse wheel zooms, Ctrl+S saves.
- **Save and show in 3D** saves and opens (or refreshes) the 3D preview.

### 3D shapes: ledges, pillars, balconies, bay windows, window frames

The second tool bar in the painter adds shapes that stick out of a wall. Pick one, set **Sticks out** (a cell is 1.8 wide, a storey 5.4 high) and drag a box on a wall:

- **Ledge**: a band, e.g. along a storey line or as a cornice under the roof edge. Snaps to whole cells.
- **Pillar**: a column or pilaster, as tall as you drag it (it can cross storeys).
- **Balcony**: drag from the balcony floor up to the top of the railing (about 1.2). Makes a slab, a railing with bars and side rails. Paint a door or window on the wall behind it.
- **Bay window**: a closed box with glass sides. Paint windows on the wall where you put it; they show on its front.
- **Window frame**: drag around a window or door. Adds a surround and a sill that stick out, so the window looks set into the wall. (The wall itself isn't cut open.)

The front of a shape shows whatever is painted on the wall behind it, and its edges continue the wall's colours, so you paint shapes like the wall. Balcony railings, bay window sides and slabs use the small **Shape colours** sheet (trim, railing metal, glass, concrete), which you can repaint.

**Select** a shape to change how far it sticks out, **Delete** removes it, right-click with any shape tool deletes the shape under the mouse. Undo works for shapes too. Shapes are drawn as hatched boxes on the wall; **Show shapes** hides them. They appear in 3D and in game after **Save** (they're written as the `Shapes` part of `exterior.obj` and listed in `exterior.json`). Making the exterior again keeps them on walls that still exist.

Shapes are only looks: the game doesn't know about balconies, so you can't walk onto them.

**Painting in another program** (Photoshop, GIMP, Paint.NET, Aseprite, Krita…): **Export this wall as PNGs** (or **Export all walls to a folder**) writes for each wall:

```
wall_S17_3-17.png           the daytime colours (paint this one)
wall_S17_3-17_night.png     what lights up at night
wall_S17_3-17_glow.png      what always glows (neon)
wall_S17_3-17_windows.png   white where the game puts window lights
wall_S17_3-17_guide.png     grid and window outlines, to put over your picture as a layer (not imported)
```

Keep the file names and sizes, then **Import PNG into this wall** (pick one or several) or **Import all from a folder**. The name ending decides which layer a file goes to. Pictures of another size are scaled to fit, and transparent pixels leave what's there alone, so you can import just a logo on a transparent background.

Window lights: the game lights windows where the window map is white. If you paint new windows, put them in the window map too (the window stamps do that for you). Doors you can walk through still come from the entrances in your floor plans; a painted door is only a picture.

**Making the exterior again** (after changing floor plans) keeps your painting on every wall and sign that still has the same size and place, if *Keep my painting* is ticked. Walls that changed get the generated look again.

For multiplayer, the painted textures are part of the `Buildings` folder, so friends need the new files too (the content hash changes).

## City wall pieces and piers (My edge pieces)

The city's edge is built from the game's own wall pieces: **BoundaryCoastal01** (straight sea wall), **BoundaryCoastal02** (with a square platform) and **BoundaryCorner01** (corners). You can add your own: **Edge > New wall piece** (default Id `BoundaryCoastal03`). Your wall pieces are in the tree under **My edge pieces**; select one to edit it on the right. Saved with **File > Save** to `Edge\<Id>\piece.json`.

- On the right: a **3D view** of the piece as it is now (an import waiting for Save, its own model, the starter with its pier, or the game's plain wall), with the game's wall pieces next to it, the sea and the street; tick *Collision* to see the invisible collision objects. Beside it, **measured on the model**: size, height at the street, the pier deck's height above the sea (and a warning below the wading height), how far it reaches out, lowest point vs. the sea, triangles, materials, textures, collision, and what the import check said.
- **Game edge pieces (read-only)** in the tree: BoundaryCoastal01, BoundaryCoastal02 and BoundaryCorner01 with their 3D view and measurements. *Export for Blender…* exports the game's model (the corner's model can't be read back, so it has none); *Make a wall piece from this* (or Ctrl+C, then Ctrl+V on My edge pieces) makes one of yours with that Role.
- **Platform pieces**: set *Role* to *Platform* and the piece is a copy of BoundaryCoastal02 (the square platform); its template, preview and import check use that model. The plugin can put either kind on any straight edge tile.
- *Role* (straight / platform / corner): it's a copy of the game's piece of that kind. *Minimum* / *HardLimit* (1 / 1 to start): how many each new city gets. The game itself ignores limits on edge pieces, so the plugin picks the tiles while a city is generated (random straight edge tiles that depend only on the city). New cities only.
- **The pier is part of the wall piece's model.** A new wall piece starts with the game's wall and a pier the plugin builds (stairs down to a deck above the water, cut into the wall's railing where they start). Once a city with it has loaded, the plugin writes exactly that, wall + pier, to `Export\edge\<Id>\` (with invisible `collision…` objects for walking: the wall with its opening, smooth stair ramps, railing walls).
- **Export for Blender…** gives you that (or the piece's own model, or the game's plain wall piece) as OBJ + MTL + PNGs with a `README-wall-piece.txt`. Edit it in Blender (File > Import / Export > Wavefront OBJ, default axes, metres; don't move the whole thing).
- **Import model…** checks it against the game's wall piece before taking it: units (cm/mm) and Z-up are spotted and can be fixed with a tick, the length along the wall (one tile, not into the next), both ends (same cross-section, so it connects to the next piece), not further toward the city than the template, walkway height at the street, down to the water, textures, UVs, triangles. It draws your model over the template from above and at both ends. **File > Save** copies it to `Edge\<Id>\model\`.
- In game, every copy of the piece then gets your model, the pier included; the game's wall there is hidden (or kept, with *Keep the game's wall too*). Objects named `collision…` are only for walking and bumping into; without any, the whole model is solid. **Use the game's wall** goes back to the starter.
- Only you and other players walk on piers; NPCs don't use them. If you fall into the sea (the screen goes black under water), you're put back on the nearest pier's deck.
- The log shows it in lines starting with `[edge]` and `[pier]`.

Everything about the edge is in the **Edge** folder next to Buildings, and it's part of the multiplayer content hash. For adding more later (cranes, boats, kiosks…), `Shared/EdgePieces.cs` explains how the parts fit together, and `Plugin/EdgePieces.cs` → `OnPlaced` is where things are built at each placed piece.

## Using other mod loaders instead

**Building > Export for other mod loaders…** writes the building for people who'd rather not use the CustomBuildings plugin:

- **`<Id>-AssetBundleLoader`**: a content-only mod for Piepieonline's *Asset Bundle Loader* + *Community Case Loader* (+ *DDSLoader* for the name), like PiesConstructionCo: `murdermanifest.sodso.json`, the building as `<Id>.sodso.json` (copied from the base building, floors as `FLOOR:`), the floor plans, and your exterior as a `.sodprefab.json` with OBJ meshes and PNG textures (matt, no glow). Copy its `plugins` folder into `BepInEx`, or zip the folder (it has `manifest.json`, `icon.png`, `README.md`) to upload it to Thunderstore.
- **`<Id>-UniversalObjectLoader`**: truedartt's UniversalObjectLoader is a library without a content folder, so this is the building as a UOL JSON (`{"<Base>__clone": {...}}`) plus the floor plans, with a code sample for a modder's plugin. It keeps the base building's look.

The loaders these need can be installed from the setup window (step 5, optional). Only the CustomBuildings plugin does: reserved places, working out the exterior's windows, letting shops limited to the base building in, and keeping the exterior matt and glow-free at runtime. Don't install the same building both ways.

## Copy, paste, delete

In the tree (click it first): **Ctrl+C** copies the selected building, floor, wall piece (also the game's floors and wall pieces), **Ctrl+V** pastes it, **Ctrl+D** duplicates, **F2** renames, **Del** deletes. Right-click shows the same and more (3D preview, make/paint exterior, fix stairs, export…).

- A pasted building comes with copies of its floors (renamed to the new Id, and its layouts point to the copies).
- A floor is pasted into the selected building (or the selected floor's building).
- A pasted wall piece gets the next free BoundaryCoastalNN, with its own model copied along when you save.
- Deleting happens on disk when you save, like everything else.

## Releasing on Thunderstore

`build.bat` also makes **`Release\Ranelle-CustomBuildings-1.0.1.zip`** (by `package-thunderstore.ps1`), ready to upload at thunderstore.io in the Shadows of Doubt community under the team *Ranelle*:

```
manifest.json      name CustomBuildings, version 1.0.1, depends on BepInEx-BepInExPack_IL2CPP-6.0.788
icon.png           256 × 256
README.md          the Thunderstore page (Thunderstore\README.md)
CHANGELOG.md
plugins/CustomBuildings/CustomBuildings.dll
plugins/CustomBuildings/Edge/BoundaryCoastal03/piece.json + model/   the example wall piece with the pier
```

Everything except the DLL lives in the `Thunderstore` folder; change the version in `Thunderstore\manifest.json` together with `Plugin\CustomBuildings.csproj`, `Plugin\Plugin.cs` and `Editor\BuildingEditor.csproj`. Fill in `website_url` and the editor link in `Thunderstore\README.md` (where you put `SodBuildingEditor.exe` for download: GitHub, Nexus…). The editor isn't in the Thunderstore package: mod managers install into their profile, and the editor is a program you run yourself.

With a mod manager the plugin is in the profile (`…\BepInEx\plugins\Ranelle-CustomBuildings\CustomBuildings`); open that folder in the editor with **File > Open mod folder…**.

## Files

```
BepInEx\plugins\CustomBuildings\
  CustomBuildings.dll
  Buildings\
    cb_MyTower\
      building.json          <- id, base building, layouts, overrides
      floors\
        cb_MyTower_lobby.json  <- game FloorSaveData format; file name = floor name
      exterior\               <- own exterior (Make exterior / Paint exterior)
        exterior.json, exterior.obj, exterior.mtl, diffuse.png, lit.png, unlit.png, windows.png
  Export\                    <- written by the plugin; read by the editor
    catalog.json
    floors\*.json
    models\<building>\model.obj, model.mtl, *.png, model.json (also windowmap.png, addonmap.png)
  Editor\preview.json        <- 3D preview model alignment, per base building
```

`building.json` example:

```json
{
  "Format": 1,
  "Id": "cb_MyTower",
  "BasePreset": "OneFIfthAve",
  "ReplaceFloorLayouts": true,
  "FloorLayouts": [
    { "Floors": 1, "Blueprints": [ "cb_MyTower_lobby" ] },
    { "Floors": 10, "Blueprints": [ "cb_MyTower_apartments", "SomeGameFloor" ] }
  ],
  "ReplaceBasementLayouts": false,
  "BasementLayouts": [],
  "Overrides": { "Zone": "residential", "HardLimit": 2, "PossibleNames": [ "Margin Tower" ] }
}
```

Floor names must be unique across the whole game. The editor and plugin warn about clashes.

## Multiplayer (SOD MP)

- Every player needs the plugin **and identical Buildings and Edge folders**. The log prints a content hash on start, and the editor shows the same hash under **Tools > Show content hash**. Compare it with your friends.
- The host should generate the city with the buildings in it.
- Whether SOD MP's launcher allows extra BepInEx plugins is unknown; ask in its Discord before relying on it.

## Troubleshooting

- **Loading takes very long or hangs with a custom exterior**: the log shows each step for every building with its own exterior (`cb_X: loading its own exterior (0 ms)`, `model built`, `textures loaded`, `measured`, `working out the windows…`, `windows worked out`). The last line printed is where it got stuck; send that part of `BepInEx\LogOutput.log`. The plugin finds the windows itself in milliseconds (`ExteriorWindowMethod = Fast`; checked to give exactly the game's result). `Game` uses the game's own routine instead, which can take minutes; its result is cached in `BepInEx\cache\CustomBuildings`. If it hangs on "working out the windows", set `ExteriorWindowLights = false` in `BepInEx\config\Ranelle.sod.custombuildings.cfg` (the building then has no window lights at night). `ExteriorTextureMaxSize` (default 1024, like the game's buildings) shrinks big exterior textures when loading.

- **Build fails**: report it with the error lines (for example as an issue on the GitHub page). Most likely a game type or property name differs slightly in your game version.
- **Building never appears**: check the log for `Registered building ...`. If it says the base building wasn't found, the `BasePreset` name is wrong (pick it from the editor's dropdown).
- **Floor errors in the log**: a layout names a floor that doesn't exist, or the floor file isn't valid. Use **Tools > Check for problems** in the editor.
- **Windows don't match floors**: the layouts' floor total differs from the base model's floor count.
- **City edge report (for piers)**: with `EdgeReport = true` (off by default) the log gets an `[edge]` report once a city has loaded: the city's boundary, the sea's height and colliders, which buildings form the edge and where each piece sits. Standing at the edge and pressing **Ctrl+Shift+9** logs the heights below you and up to 60 m in front of you. `VerboseLogging = true` also logs a `[diag]` report about the buildings' materials.
- Turn on `VerboseLogging` in `BepInEx\config\Ranelle.sod.custombuildings.cfg` for detailed logs. Turn off `ExportOnStart` once you have the export.

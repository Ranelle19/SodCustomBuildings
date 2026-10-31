# Custom Buildings

Your own buildings for Shadows of Doubt: your own floor plans inside, your own outside if you want, shops and restaurants the game fills like its own, and your own pieces of the city's sea wall, like a pier down to the water.

New cities only: custom buildings and wall pieces appear in cities generated after they were added, not in old saves.

## What's in this package

- **The CustomBuildings plugin** (BepInEx 6 IL2CPP). It loads the buildings and wall pieces in `BepInEx\plugins\…\CustomBuildings\Buildings` and `…\Edge`. It needs nothing but BepInEx.
- **An example wall piece, `BoundaryCoastal03` ("Pier wall")**: the game's straight sea wall with an opening in the railing and stairs down to a wooden pier 3 m above the water. Every new city gets one. You can walk on it; NPCs don't use it. If you fall into the sea next to it, you're put back on the pier. Don't want it? Delete `Edge\BoundaryCoastal03`, or set `"Enabled": false` in its `piece.json`.

## Making your own

Buildings and wall pieces are made with the **editor, `SodBuildingEditor.exe`** (Windows, one file, nothing to install). It has a floor plan editor, a 3D preview, an exterior painter, wall pieces with export to Blender and a checked import, and it can set up BepInEx and the plugin by itself.

<!-- Put the link to where you upload the editor here (GitHub, Nexus…) and in manifest.json's website_url. -->
Get the editor from the mod's website (link above).

With a mod manager (r2modman, Thunderstore Mod Manager) the plugin lives in the manager's profile, not in the game folder. In the editor, use **File → Open mod folder…** and pick the profile's `BepInEx\plugins\Ranelle-CustomBuildings\CustomBuildings` folder. (Open the profile folder from the mod manager's settings.) Don't install the plugin a second time from the editor's setup into the game folder while you use a mod manager.

## Playing someone's buildings

Copy the folders you got into the plugin's folder: buildings (like `cb_MyTower`) into `CustomBuildings\Buildings\`, wall pieces (like `BoundaryCoastal04`) into `CustomBuildings\Edge\`. Then generate a new city.

## Multiplayer (SOD MP)

Every player needs this mod **and exactly the same `Buildings` and `Edge` folders**. The game log shows a content hash at start (`Content hash …`); it must be the same for everyone. The host generates the city.

## Works with

SOD MP, SOD API, SOD-Optimizer, NewLife, SodDebugMenu. Optional: the editor can also export buildings for Asset Bundle Loader / UniversalObjectLoader.

## For mod makers

Your mod adds its own kinds of places, rooms or furniture? With a few lines they work with custom buildings: building makers can reserve a unit for your place in the editor, and saved cities keep it. See **MODDING.md** on the GitHub page (checklist, example code, and a small optional API, `CustomBuildings.Api`).

## Settings and help

Settings: `BepInEx\config\Ranelle.sod.custombuildings.cfg`. If something's wrong, send `BepInEx\LogOutput.log` (close the game first); the plugin's lines start with `[CustomBuildings]`, `[edge]`, `[pier]`.

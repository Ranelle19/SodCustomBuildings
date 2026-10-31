# Changelog

## 1.0.1

- Furniture by hand: a furnishing window (Building → Furnish in 3D…, or **Furnish (3D)…** in the floor editor) with a catalog of all furniture (the game's and mods'), with pictures, search and filters, and the building in 3D with the game's real models. Click to put a piece down, drag to move, R turns, Del removes, undo/redo, walls full/low/off. A unit with hand-placed furniture gets exactly that in the game.
- Editor: a Shadows of Doubt look (Tools → Look).
- For mod makers: an API (`CustomBuildings.Api`) and a guide (MODDING.md) for making your own kinds of places, rooms and furniture work with custom buildings, so building makers can reserve a unit for them.
- Saved cities find presets that other mods made (rooms, furniture, items), so cities with them load again.
- Reserved units are matched correctly (address numbers per floor).
- The editor lists places from other mods (marked "from a mod") and warns that players need that mod.
- Editor: dark mode (Tools → Dark mode).

## 1.0.0

First release.

- Custom buildings: your own floor plans in a copy of any game building, with its outside or your own (generated and painted in the editor, with ledges, pillars, balconies and bay windows).
- Shops and restaurants the game fills like its own; units can be fixed to one kind of place.
- Stairs, lifts and entrance checks so NPCs don't get stuck.
- City wall pieces: your own pieces of the sea wall that new cities get, with their own model. Comes with an example, `BoundaryCoastal03`: the sea wall with stairs down to a pier.
- The editor (SodBuildingEditor.exe): building and floor editor, 3D preview, exterior painter, wall pieces with Blender export / checked import, copy and paste, game setup.
- Multiplayer (SOD MP): a content hash every player can compare.

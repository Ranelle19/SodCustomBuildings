# Custom Buildings for Shadows of Doubt

**Version 1.0.0** · by Ranelle

Make your own buildings for Shadows of Doubt: your own floor plans inside, your own outside if you want, and shops, flats, offices and restaurants that the game fills like its own. Everything is in **one file: `SodBuildingEditor.exe`**. It contains the building editor *and* the game plugin, and sets the game up for you.

---

## What you need

- Windows 10 or 11 (64-bit)
- Shadows of Doubt (Steam)
- Nothing else. The editor installs **BepInEx** (the mod loader) and the **CustomBuildings** plugin for you. You don't need .NET or anything else.

---

## Install

1. **Close Shadows of Doubt.**
2. Put `SodBuildingEditor.exe` anywhere you like (e.g. your Desktop) and start it.
   - Windows may say "Windows protected your PC" because the file isn't signed. Click **More info → Run anyway**.
3. The **setup window** opens and walks you through it:

   | Step | What it does | Needed? |
   |---|---|---|
   | 1. Game folder | Finds Shadows of Doubt through Steam, or you pick the folder with `Shadows of Doubt.exe` | **Required** |
   | 2. BepInEx | **Install for me** downloads BepInEx IL2CPP 6.0.788 and installs it | **Required** |
   | 3. CustomBuildings plugin | **Install** puts the plugin into the game (it's inside the editor) | **Required** |
   | 4. Start the game once | Wait for the main menu (the very first start with BepInEx takes a few minutes), then go back to the editor | **Required** once |
   | 5. Other mod loaders | Asset Bundle Loader, Community Case Loader, DDSLoader, SOD Common, UniversalObjectLoader | *Optional*: only for "Export for other mod loaders" |

4. Click **Continue**. Next time the editor opens straight away.
   **File → Game folder and mod setup** brings the setup back (e.g. to update the plugin after you get a newer editor).

---

## Just want to play someone's buildings?

1. Do the install above (steps 1–4).
2. Copy the folders you got into `Shadows of Doubt\BepInEx\plugins\CustomBuildings\`:
   - buildings (e.g. `cb_MyTower`) into its `Buildings` folder,
   - wall pieces of the sea wall (e.g. `BoundaryCoastal03`) into its `Edge` folder.
3. Start the game and **generate a new city**. Custom buildings and wall pieces only appear in newly generated cities, not in old saves.

---

## Make your own building (short version)

1. **Building → New building**: give it an Id (start it with `cb_`) and pick the game building whose outside you want.
2. On the left, under **Game floors**, pick a floor and use **Floor → Copy game floor into building**. Starting from a game floor is much easier than starting from nothing.
3. Change it: paint addresses (flats, shops, hallways), rooms, walls, doors and windows, stairs.
4. Set the building's **floor layouts** (which floor plans go on which storeys).
5. Optional: **Building → Make exterior** and **Paint exterior** (Ctrl+P) for your own outside, with ledges, pillars, balconies and bay windows.
6. **Building → 3D preview** (Ctrl+3) shows it without starting the game.
7. **Tools → Check for problems**, then **File → Save**.
8. Start the game and **generate a new city**.

Shortcut: **Building → New shopping building** (Ctrl+M) turns a game building into one full of shops and restaurants in one step.

The list on the left:
- **My buildings** (with their floors), **My edge pieces** (your wall pieces), and read-only **Game edge pieces** and **Game floors** to look at and copy from.
- **Ctrl+C** / **Ctrl+V** copy and paste, **Ctrl+D** duplicates, **F2** renames, **Del** deletes (click in the list first). **Right-click** shows the same and more. A copied building brings copies of its floors along. Nothing changes on disk until **File → Save**.

Tips:
- **Stairs**: place only the *Stairwell* on the same tile and with the same rotation on every storey. The lift comes with it. Don't add the *Elevator* flag on top, it blocks the stairs.
- **Floor → Fix stairs, lifts and entrances** (or **Tools → Fix stairs, lifts and entrances in all my floors**) repairs the usual things that make NPCs get stuck.
- Ctrl+Z / Ctrl+Y undo and redo. Ctrl + mouse wheel zooms.

---

## Wall pieces and piers (the edge of the city)

The edge of the city is made of the game's wall pieces: **BoundaryCoastal01** (straight sea wall), **BoundaryCoastal02** (with a square platform) and **BoundaryCorner01** (corners). You can add your own:

1. **Edge → New wall piece** makes one (like `BoundaryCoastal03`), listed under **My edge pieces**. Its *Role* says which of the game's pieces it copies (straight or platform), *Minimum* / *HardLimit* how many every new city gets.
2. It starts with the game's wall and a **pier**: stairs down the sea wall to a deck above the water. After a city with it has loaded once, **Export for Blender…** gives you exactly that, wall + pier, as an OBJ to change in Blender. The game's own pieces (under **Game edge pieces**) can be exported the same way.
3. **Import model…** checks your model before taking it: size and units, which way is up, the ends (so it fits the wall next to it), the height at the street, down to the water, textures. Your piece then uses your model in game, pier included.
4. The panel shows a **3D view** of the piece with the wall next to it and the sea, and the **measurements**: size, height at the street, the pier deck's height above the water, and more.

It comes with an **example**: `BoundaryCoastal03`, the straight sea wall with an opening in the railing and stairs down to a pier. The setup puts it in the `Edge` folder on the first install, so every new city gets one. **Edge → New wall piece from the pier example** makes another copy to change. Don't want it? Delete it in the list (Del, then File → Save).

**Use a game wall…** turns a wall piece back into a plain copy of BoundaryCoastal01 (straight) or BoundaryCoastal02 (with the platform), whichever you pick.

You can walk on piers; NPCs don't use them. Fall into the sea and you're put back on the pier. New cities only.

## Share your buildings

Your buildings are in
`Shadows of Doubt\BepInEx\plugins\CustomBuildings\Buildings\<your Id>\`, your wall pieces in
`Shadows of Doubt\BepInEx\plugins\CustomBuildings\Edge\<Id>\`.
Zip those folders and send them. The other person needs the plugin too (send them `SodBuildingEditor.exe` and this README).

Don't want people to need the CustomBuildings plugin? **Building → Export for other mod loaders** writes a version for Asset Bundle Loader or UniversalObjectLoader. Some things only work with the CustomBuildings plugin: fixed shop types, window lights on your own exterior, and the no-glow fix. Install a building only one way, not both.

---

## Multiplayer (SOD MP)

- Every player needs the plugin **and exactly the same `Buildings` and `Edge` folders** (Edge holds your wall pieces and their models).
- Compare **Tools → Show content hash** in the editor (it's also in the game log). It must be the same for everyone.
- The host generates the city.

---

## Works with

The plugin needs only BepInEx. It's been used alongside SOD MP, SOD API, SOD-Optimizer, NewLife and SodDebugMenu.

---

## Something's wrong?

| Problem | Try this |
|---|---|
| My building never shows up | Generate a **new** city. Then look in `BepInEx\LogOutput.log` for `Registered building …` |
| NPCs get stuck | **Tools → Fix stairs, lifts and entrances in all my floors**, and check that the stairs line up on every storey |
| Loading takes very long | Make sure the plugin is up to date (setup window, step 3) |
| Windows don't match the floors | The floor layouts add up to a different number of storeys than the building's model. The editor shows the right number |
| The setup says "older" or "Update" | Click **Update**. Your buildings are kept |
| The setup says the installed plugin is "newer" (you used a test build, 1.1–1.9) | Click **Use 1.0.0** |
| The game freezes for a moment now and then | Make sure the plugin is 1.0.0 (setup window, step 3); older test builds searched the whole city every few seconds |

The settings are in `BepInEx\config\Ranelle.sod.custombuildings.cfg` (created on the first game start). Settings from older versions (`felix.sod.custombuildings.cfg`) are carried over automatically.

When asking for help, send `BepInEx\LogOutput.log` (close the game first so it's complete) and, if it's about one building, its folder.

---

## Remove it

Delete `Shadows of Doubt\BepInEx\plugins\CustomBuildings`. To remove all mods, also delete the `BepInEx` folder and the `dotnet` folder, `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version` in the game folder (or use Steam's *Verify integrity of game files*).

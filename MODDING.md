# Making your mod work with CustomBuildings

CustomBuildings lets players build their own buildings for Shadows of Doubt. Building makers can **reserve a unit** in their building for a kind of place, for example "this shop is always an animal shelter". The place can be the game's own or one that **your mod adds**.

This guide is for mod makers who add their own kinds of places (AddressPresets), rooms (RoomConfigurations), furniture or items, and want them to:

- show up in the CustomBuildings editor, so building makers can pick them,
- appear in the unit a building reserves for them,
- still be there when a saved city is loaded again.

NewLife's animal shelter (`NewLifeAnimalShelter`) is a real example that works this way.

---

## The short version (checklist)

1. **Add your place to the game's list** `Toolbox.Instance.allAddressPresets`, before the player makes or loads a city. Doing it at the main menu is fine.
2. **Fill in `compatible`** with the address types (LayoutConfigurations) your place fits into, e.g. `Retail` for a shop. The editor shows the place first for those address types.
3. **Register everything you create with the game's preset cache.** That means the AddressPreset and also its RoomConfigurations, FurniturePresets, FurnitureClusters, InteractablePresets, RetailItemPresets and so on:
   ```csharp
   Toolbox.Instance.ProcessLoadedScriptableObject(myPreset);
   ```
   This is the step most mods miss (see "Why step 3 matters" below).
4. **Use the same names every time.** Saves store presets by name (`presetName` / `name`), so keep them stable between versions of your mod.
5. *(Optional)* **Keep it out of normal buildings** if it should only appear where a building reserves it. See "Only for reserved units" below.

That's all. With 1 to 3 done, your place is in the editor's "Always make it this kind of place" list after the player has started the game with your mod and loaded or created a city once.

---

## Why step 3 matters

When the game **makes** a city, it uses the preset objects directly, so everything you add works.

When it **loads** a saved city (also right after making one, because a new city is saved and loaded again), it finds every building, room type, place and piece of furniture **by name**. It looks them up in `Toolbox.resourcesCache`, which it fills **once at start-up**. Anything a mod creates later isn't in it, so the lookup returns null and the load breaks. The log then shows errors like these, and the loading screen never finishes:

```
NullReferenceException  at NewRoom.SetConfiguration (RoomConfiguration newPreset)  at NewRoom.Load (...)
NullReferenceException  at NewRoom.AddFurniture (...)                              at NewRoom.Load (...)
```

`Toolbox.Instance.ProcessLoadedScriptableObject(preset)` puts your preset into that cache.

CustomBuildings 1.0.1 and later also fill the cache for every mod right before each city load, as a safety net. Don't rely on that: your mod should also work for players without CustomBuildings.

---

## Example

```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

[BepInPlugin("you.sod.yourmod", "YourMod", "1.0.0")]
// soft: your mod also runs without CustomBuildings
[BepInDependency("Ranelle.sod.custombuildings", BepInDependency.DependencyFlags.SoftDependency)]
public class YourPlugin : BasePlugin
{
    public override void Load() { /* patch something that runs at the main menu, then call MakePlace() */ }

    internal static void MakePlace()
    {
        var tb = Toolbox.Instance;
        var pawnShop = tb.allAddressPresets.ToArray().First(a => a.name == "PawnShop");

        var place = Object.Instantiate(pawnShop);
        place.name = "YourModPetShop";           // an example: use your own stable, unique name
        place.presetName = "YourModPetShop";
        // ...change what it sells, its rooms, furniture...

        var room = Object.Instantiate(/* a RoomConfiguration to start from */);
        room.name = "YourModPetShopRoom";

        tb.allAddressPresets.Add(place);         // 1. the game's list of places

        // 3. the game's preset cache: everything you made, so saved cities find it again
        tb.ProcessLoadedScriptableObject(place);
        tb.ProcessLoadedScriptableObject(room);
        // ...and every FurniturePreset / FurnitureCluster / InteractablePreset you made
    }
}
```

---

## Only for reserved units

If your place should **only** appear where a custom building reserves it, keep it out of the game's normal choice. Either:

- give it a very low `baseScore` (e.g. `-1000000`), or
- set `limitToBuildings` to a list that contains no building that really spawns.

When the game picks what a reserved unit becomes, CustomBuildings temporarily:
- makes your place the clear favourite (`baseScore`, `baseScoreFrequencyPenalty`),
- lifts its floor limits (`minMaxFloors`),
- clears `limitToBuildings`,
- adds the unit's address type to `compatible`.

It puts everything back right after that one choice, so your place still won't appear anywhere else.

To decide this yourself in your own patch, ask CustomBuildings (see the API below):

```csharp
if (CbBridge.Present && CbBridge.IsReservedFor(address, "YourModPetShop")) { /* allow it here */ }
```

---

## API (optional)

CustomBuildings has a small public class, `CustomBuildings.Api`. None of its methods throw.

| Method | What it does |
|---|---|
| `Api.ApiVersion` | `1`. Goes up when something is added; nothing is removed. |
| `Api.RegisterWithGame(ScriptableObject preset)` | Same as `ProcessLoadedScriptableObject`, with a check and a log line. Returns true if it worked. |
| `Api.IsCustomBuilding(BuildingPreset preset)` | True for buildings and wall pieces made with CustomBuildings. |
| `Api.GetReservedPlace(NewAddress address)` | The place name a custom building reserves for this unit, or null. Works during `NewAddress.AssignPurpose` and later. |
| `Api.IsReservedFor(NewAddress address, string placeName)` | True if this unit is reserved for exactly that place. |
| `Api.BuildingsReserving(string placeName)` | Every reservation for that place, as `"BuildingId / floor / address"`. Useful for a log line. |

To use it, reference `CustomBuildings.dll` from `BepInEx\plugins\CustomBuildings\` with `Private="false"`, so it isn't copied into your mod. Then keep every call in one small class, so nothing breaks when CustomBuildings isn't installed:

```csharp
using System.Runtime.CompilerServices;
using BepInEx.Unity.IL2CPP;

internal static class CbBridge
{
    public static bool Present => IL2CPPChainloader.Instance.Plugins.ContainsKey("Ranelle.sod.custombuildings");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsReservedFor(NewAddress a, string place) => CustomBuildings.Api.IsReservedFor(a, place);
}
```

Only call `CbBridge.IsReservedFor` after checking `CbBridge.Present`.

---

## How building makers use your place

1. They start the game once with your mod and load or create a city. CustomBuildings then writes every kind of place, including yours, for the editor.
2. In the editor they pick a floor and click a unit (address). Under **"Always make it this kind of place"** your place is listed by its own name with `[from a mod]` after it (for the example above: `YourModPetShop  [from a mod]`). The editor adds the tag itself; don't put it in your preset's name.
3. The editor warns them that players need your mod too. Tell them your mod's name in your description.

In their building's `building.json` it looks like this:

```json
"ReservedPlaces": [
  { "Floor": "Shopping_center_floor1", "Address": 4, "Place": "YourModPetShop" }
]
```

`Address` is the unit's number in the editor's address list for that floor (1 = the first entry).

If a player doesn't have your mod, the game just picks something else for that unit. Nothing breaks, except that a city made **with** your mod's place can only be loaded **with** your mod.

---

## Checking it works (LogOutput.log)

| Line | Means |
|---|---|
| `Kinds of places for the editor updated (…; from other mods: YourModPetShop)` | CustomBuildings found your place. |
| `[places] … unit Retail is address 4, reserved for YourModPetShop` | The reserved unit was found. |
| `[places] Shopping_center_floor1 address 4 is now YourModPetShop.` | The game made it your place. |
| `[load start] put N preset(s) made by mods into the game's preset cache …` | CustomBuildings had to register presets for a mod. If yours are listed, add step 3 to your mod. |
| `[load] the save has a room type 'X' that the game can't find …` | A saved city uses a preset that isn't registered or installed. |

Questions or problems: open an issue on the CustomBuildings GitHub page.

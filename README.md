# Undoer

Undo/redo mod for [Flyout](https://store.steampowered.com/app/777390/Flyout/) using [MelonLoader](https://github.com/LavaGang/MelonLoader).

Adds full undo and redo support to the Craft Editor — every edit you make is saved as a snapshot so you can step backwards and forwards through your build history.

## Features

- **Ctrl+Z** to undo, **Ctrl+Y** or **Ctrl+Shift+Z** to redo
- **F7** to cycle undo memory size (50 / 100 / 200 / 500 steps)
- Covers all editor actions:
  - Part placement, deletion, duplication, rotation, scaling
  - Gizmo drags, mirroring, symmetry
  - Fuselage edits (cross sections, segments, loop cuts, splits, skin thickness)
  - Wing edits (span, chord, twist, reflex, control surfaces, flaps, etc)
  - Part module properties (engine bore, fuel fill, ammo count, and ~90 more)
  - Materials and paint
- On-screen HUD showing undo/redo position
- Debounced snapshots so rapid slider drags don't flood the stack

## Requirements

- [Flyout](https://store.steampowered.com/app/777390/Flyout/) (tested on v0.225)
- [MelonLoader](https://github.com/LavaGang/MelonLoader) v0.7.x (Open-Beta, .NET 6)

## Install

1. Install MelonLoader into your Flyout game directory
2. Download `UndoMod.dll` from [Releases](../../releases)
3. Drop it into `Flyout/Mods/`
4. Launch the game

## Building from source

The project expects Flyout + MelonLoader to be installed. It resolves references via the `FlyoutDir` MSBuild property which defaults to `$(HOME)/.local/share/Steam/steamapps/common/Flyout`.

**Linux:**
```bash
dotnet build -c Release
```

**Windows:**
```bash
dotnet build -c Release -p:FlyoutDir="C:/Program Files (x86)/Steam/steamapps/common/Flyout"
```

The built DLL will be in `UndoMod/bin/Release/net6.0/UndoMod.dll`.

## How it works

The mod uses Harmony to patch ~50 game methods across the craft editor. After each edit, it serializes the entire craft to a temp directory using the game's own `Persistence.SerializeCraft`. Undo/redo just reloads the appropriate snapshot with `CEManager.LoadCraft`. An additional ~90 `Set*` methods on part module subclasses are patched dynamically at runtime to catch property panel changes.

## Project structure

```
UndoMod/
  UndoMod.cs           - core mod (init, input, snapshots, undo/redo, dynamic patching)
  SnapHelper.cs         - shared postfix used by all harmony patches
  Patches/
    CraftPatches.cs     - start, new craft, load craft
    PartPatches.cs      - delete, duplicate, place, rotate, scale, mirror, etc
    FuselagePatches.cs  - fuselage + cross section editor
    WingPatches.cs      - procedural wing edits
    MaterialPatches.cs  - materials and paint
```

## License

[MIT](LICENSE)

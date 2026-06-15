# Aero Toolbox — SimplePlanes 2

An in-editor aerodynamics readout panel for [SimplePlanes 2](https://www.simpleplanes.com/). It injects an **Aero** tab into the wing editor that shows live, physics-accurate curves and stats for the wing you're building — driven by SP2's *own* aerodynamics solver, not a re-implementation.

![screenshot](docs/screenshot.png)

## What it does

- **CL / Lift / CD vs AoA or Speed** graphs for the selected wing, computed with the game's real wing-physics solver
- **Stats**: lift slope, zero-lift AoA, CLmax, stall angles, best L/D, stall speed, takeoff/rotate speed, t/c
- **Control-surface analysis** — highlights every flap / slat / aileron the wing uses and shows their live deflection (including custom Funky Trees expressions), faithfully matching how they move in flight
- **Test sliders** — sweep Test Speed / AoA / Flap / Slat and watch the curves and surfaces respond
- **Scope toggle** — analyse the selected wing + its mirror + spanwise-connected sections, or every lifting surface on the craft
- Native-styled UI: clones the game's own buttons and uses SP2's UI sounds, so it looks and sounds like part of the game

## Is it safe?

It's a **read-only** tool. It reads the geometry of the wing you've selected and runs the game's aero solver to draw graphs. It does **not** modify your craft, write save files, touch the network, or change anything in the game world. Everything it does is client-side and visual.

The full source is right here — read `AeroReadout.cs` and `Plugin.cs` before running anything. That's the whole mod.

## Install

1. Install [BepInEx](https://github.com/BepInEx/BepInEx) (5.x, x64) into your SimplePlanes 2 folder
2. Download `AeroToolbox.dll` from the [Releases](../../releases) page
3. Drop it in `SimplePlanes 2/BepInEx/plugins/`
4. Launch the game, open the wing editor, click the **Aero** tab

## Building from source

This is a standard .NET `net472` class library. It references SP2 / Unity / BepInEx assemblies, which are **not** included (they're copyrighted — you provide them from your own install).

1. Create a `lib/` folder next to the project and copy these DLLs into it from your install:
   - From `SimplePlanes 2/SimplePlanes 2_Data/Managed/`: `Game.dll`, `UnityEngine*.dll`, `Unity.TextMeshPro.dll`
   - From `SimplePlanes 2/BepInEx/core/`: `BepInEx.dll`, `0Harmony.dll`
2. `dotnet build -c Release`
3. The plugin lands in `bin/Release/net472/AeroToolbox.dll`

(The `.csproj` HintPaths point at `..\lib\` — adjust if you put the DLLs elsewhere.)

## License

MIT — see [LICENSE](LICENSE). Not affiliated with or endorsed by Jundroo. SimplePlanes 2 and its assets are property of their respective owners.

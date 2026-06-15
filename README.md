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

## Known bugs

- Sometimes when switching from flying an aircraft back into the editor, the solver fails to pick up aero data, and the plots are left empty. Annoying, but a restart fixes it

- Often the sweep for flaps / slats will not return if the surface has custom FunkyTrees inputs, no fix for this just yet

## Install

***BEFORE INSTALLING PLEASE BACK UP CRAFTS. protect your work first. Your crafts and game data live separately from the game install, here:

%USERPROFILE%\AppData\LocalLow\Jundroo\SimplePlanes 2\

## Step 1 — Find your SimplePlanes 2 folder
In Steam: right-click **SimplePlanes 2** → **Manage** → **Browse local files**. A folder opens containing `SimplePlanes 2.exe`. Keep this window open — this is where everything goes.

## Step 2 — Install BepInEx
1. Download **BepInEx 5.x (x64)** for Windows: https://github.com/BepInEx/BepInEx/releases
   - Get the file named like `BepInEx_win_x64_5.4.23.x.zip` (the **x64** one — SP2 is 64-bit).
2. Open the zip and **extract its contents directly into the SimplePlanes 2 folder** (the one with `SimplePlanes 2.exe`).
3. When done, that folder should now also contain `winhttp.dll`, `doorstop_config.ini`, and a `BepInEx` folder — sitting **next to** the .exe, not in a subfolder.

## Step 3 — Run the game once
Launch SimplePlanes 2 normally, let it reach the main menu, then quit. This first run makes BepInEx generate its folders — including **`BepInEx/plugins/`**, where mods go.

## Step 4 — Install Aero Toolbox
1. Download **`AeroToolbox.dll`** from the releases page: https://github.com/sj414/SP2-Aero-Toolbox/releases/latest
2. Move `AeroToolbox.dll` into:
   ```
   SimplePlanes 2/BepInEx/plugins/
   ```

## Step 5 — Verify it works
1. Launch the game and open the **wing editor** (select a wing → wrench/edit).
2. You'll see a new **Aero** tab/button in the wing-editor panel. Click it — graphs and stats appear.

That's it.

---

## Troubleshooting

**No "Aero" tab shows up**
- Confirm the DLL is at `BepInEx/plugins/AeroToolbox.dll` (not in a subfolder, not still in Downloads).
- Confirm you used the **x64** BepInEx, and that `winhttp.dll` sits next to `SimplePlanes 2.exe`.
- Check `BepInEx/LogOutput.log` — search for `Aero Toolbox loaded`. If it's there, the mod loaded fine; reopen the wing editor.

**BepInEx folder never appeared**
- You extracted into the wrong place. The `BepInEx` folder must be next to `SimplePlanes 2.exe`. Re-extract and run the game once more.

**Want to uninstall**
- Delete `AeroToolbox.dll` from `BepInEx/plugins/`. To remove modding entirely, delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and the `BepInEx` folder.

**Updating**
- Replace `AeroToolbox.dll` in `plugins/` with the newer one. SP2 must be closed (the file locks while running).

---

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

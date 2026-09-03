# Landscape Seam Fix

Goal: detect (and eventually auto-patch) Skyrim landscape seams/holes caused by
two plugins editing neighboring cells' heightmaps (`LAND` -> `VHGT`)
independently, so the shared edge vertices no longer match. No manual CK work.

## Status

- [x] Read-only seam detector written for xEdit (`xedit-scripts/`) - **abandoned**
- [x] Read-only seam detector rewritten as a C#/Mutagen tool - **working**
- [x] Detection logic validated against real data (decoded heights land on
      clean round numbers; ran across 51,998 real cells with zero crashes)
- [x] Solved seeing the **real** load order without needing MO2's/Vortex's own
      VFS/integration (both turned out unreliable in practice - see history
      below) - `Mo2Resolver` reads an MO2 profile's own text files directly
      and resolves each active plugin's winning file itself
- [x] Validated against a real 194-plugin MO2 modlist (`C:\Wabbajack\TBA`,
      "tabula rasa!" profile): 0 missing plugins, correctly picked cleaned-
      master replacer mods over vanilla, produced a report with real
      cross-mod seams (e.g. `Landscape Fixes For Grass Mods.esp` vs
      `Landscape and Water Fixes.esp`)
- [x] WPF UI (`SeamFinder.UI/`) with MO2 / Vortex / Direct-path mode
      selection, MO2 mod-folder output option
- [ ] UI not yet visually tested interactively (built and confirmed it
      launches without crashing; needs a real run-through)
- [ ] Sanity-check report rows against a spot you already know has a visible
      seam in-game
- [ ] Write the **fix** logic (patch the losing cell's edge vertices to match
      the winner) - holding off until detection against your real load order
      is confirmed correct via in-game spot check

## Why this can't just be "smooth it at runtime" (SKSE)

Seams come from the two neighboring cells' `LAND` records disagreeing on the
absolute height of the vertices they share. Fixing it at runtime would mean
hooking undocumented terrain-streaming internals and only patching the visual
mesh - the collision mesh and navmesh would still be misaligned, trading a
visible seam for fall-through-the-world / pathing bugs. Patching the actual
`LAND` data at build time fixes the cause once, in a regular ESP, no runtime
risk.

## How the detector works

For every pair of adjacent exterior cells: decode both cells' winning
`VertexHeightMap` (VHGT) into absolute per-vertex heights (`Offset` +
delta-encoded 33x33 grid, one raw step = 8 game units), then compare the 33
vertices along the shared edge. Any edge where the max mismatch exceeds
tolerance (8 units) gets a row in `LandscapeSeamReport.csv` - worldspace, cell
coords, which plugin owns each side, and the worst vertex.

`Landscape` is a property embedded directly in each plugin's own copy of a
`CELL` record, not a separately-linked record - so the overall "winning CELL
override" is not necessarily the plugin that owns the winning landscape edit.
`ResolveWinningLandscape` in `SeamFinder.Core/SeamDetector.cs` resolves that
independently by walking every plugin's copy of the cell and picking the
highest-priority one that actually has non-null landscape data.

## How the MO2 resolver works

`SeamFinder.Core/Mo2Resolver.cs` reads an MO2 profile's own text files
directly and resolves the real, active load order without needing MO2's VFS
at all (validated against a real 194-plugin instance - see status above):

- `plugins.txt` - **only lists regular toggleable plugins**. A line starting
  with `*` is explicitly active; a line with no `*` is explicitly INACTIVE.
  Master files (Skyrim.esm, DLC .esm's) and ALL Creation Club content
  (.esm/.esl) are never listed here at all - they're implicitly always
  active. Getting this backwards (treating "not mentioned" as inactive)
  silently drops every master and CC plugin - caught by testing against real
  data before shipping this.
- `loadorder.txt` - full load order (top = loaded first = lowest priority),
  every plugin MO2 knows about regardless of active state.
- `modlist.txt` - mod folder priority order, top = HIGHEST priority (wins
  file conflicts). `+` = enabled folder, `-` = disabled.

For each active plugin filename: search enabled mod folders in priority
order for a file with that name (first match wins), falling back to the base
game's Data folder. Then the resolved files get copied (not
symlinked/hardlinked, for reliability regardless of which drives are
involved) into one temp merged folder that Mutagen's `GameEnvironmentBuilder`
loads from directly.

## History: why this isn't an xEdit script

`xedit-scripts/` has the original prototype (Pascal Script). It got the
VHGT byte-decode logic right (confirmed via hand-decoded IEEE-754 floats
landing on clean round numbers like -796.0, -461.0, -1247.0), but reliably
**crashed xEdit itself** (native access violations, not catchable script
errors) once run across a real, several-thousand-cell load order - even
after multiple rounds of hardening (bounds checks, avoiding repeated
GetNativeValue calls, restructuring away from array-of-records-with-strings).
Kept as a reference for the byte-format research, not meant to be run again.

## History: why this isn't a Synthesis patcher

Rewriting in C#/Mutagen fixed the crash problem completely - Mutagen gives
typed access to `VertexHeightMap` (a real `float` Offset and a real
`sbyte[,]` HeightMap, no manual byte-blob parsing) and the tool ran cleanly
across 51,998 cells with zero crashes. The plan was to wire it up as a proper
Synthesis patcher so it runs automatically through MO2, but that integration
turned out to be unreliable in practice on this setup:

- Synthesis explicitly disallows building .NET source *while* running
  through MO2's virtual file system (`Mo2BuildBlockedException` - "MO2's
  virtual file system (VFS) is incompatible with .NET SDK builds"). This is
  documented, intentional behavior, not a bug.
- The documented workaround (build once outside MO2 - "Solution" patchers
  are explicitly for local IDE development per Synthesis's own UI text -
  "Running from this UI is not recommended") pointed at Synthesis's other
  supported mechanism: an "External Program"/CLI patcher pointed at a
  precompiled exe, which needs no build step at all.
- That worked mechanically (the exe launched fine through Synthesis/MO2,
  produced a real output ESP), but the load order it actually received kept
  resolving to a small vanilla+CreationClub list (6-11 plugins) instead of
  the real ~100+ mod list, even when `--LoadOrderFilePath` pointed at a real
  MO2-profile Plugins.txt, and even after moving the patcher into an
  already-correctly-configured existing group. Root cause never found -
  parked as a Synthesis-specific issue, not a bug in the detection code.
- A simpler standalone-mode workaround (calling `GameEnvironment.Typical`
  directly, relying on being launched through MO2's own Executables list to
  get VFS access) was tried next but never verified to actually resolve the
  real load order either, before `Mo2Resolver` made the whole VFS-dependency
  question moot by reading MO2's config directly instead.

## Project layout

- `xedit-scripts/` - abandoned Pascal Script prototype, kept for reference
- `SeamFinder.Core/` - shared logic, referenced by both the console app and
  the UI
  - `Mo2Resolver.cs` - MO2 profile parsing + plugin file resolution
  - `SeamDetector.cs` - the VHGT decode + seam comparison algorithm
- `SeamFinder/` - console CLI
  - `Program.cs` - entry point; `--mo2 <instancePath> <profileName>
    [gameDataPath]` is the main supported mode (see comments at top of file
    for the other two, less reliable modes kept for reference)
  - `LandscapeSeamFinder.synth` - leftover from the abandoned Synthesis
    attempt, not currently used
- `SeamFinder.UI/` - WPF UI wrapping the same `SeamFinder.Core` logic
  - Mode selector: MO2 (instance/profile/game path, with an Advanced section
    to override individual plugins.txt/loadorder.txt/modlist.txt paths) /
    Vortex (best-effort: assumes default hardlink deployment, so it's just
    the game's own Data folder - "Auto-detect" button finds it via Mutagen's
    own Steam/GOG/registry detection) / Direct game path (no mod manager)
  - Output: a folder, or "create as an MO2 mod folder" (writes a minimal
    `meta.ini` so it shows up as an installable mod inside MO2's mods list)
  - Built and confirmed to launch without crashing; not yet run through
    interactively - next step is an actual click-through test

## Next steps

1. Run `SeamFinder.UI.exe`, pick MO2 mode, fill in your real Tabula Rasa
   instance/profile, and confirm the log shows your real plugin count/list
   (not a small vanilla-only one) and a real `LandscapeSeamReport.csv` comes
   out.
2. Sanity-check a handful of report rows against a spot you already know has
   a visible seam/hole in-game.
3. Write the fix logic once detection is confirmed correct against real data
   and an in-game spot check.

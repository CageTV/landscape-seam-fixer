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
- [x] UI tested interactively against a real ~1200-plugin load order, many
      iterations, across multiple sessions
- [x] Fix logic written, rewritten from scratch once (see "Fixing logic"
      section below), and validated in-game repeatedly
- [x] Console CLI has fix-mode flags too, not just detection - see "Project
      layout" below for the exact flag list
- [ ] Water fixing is landscape-only's sibling feature, newer and less
      battle-tested in-game so far (bridges/river crossings specifically were
      spot-checked and looked correct, but it hasn't had the same volume of
      real-world testing landscape restoration has)

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

## Fixing logic: two completely different approaches, in order

### Approach 1 (abandoned): detect a mismatch, blend it smooth

The first working version of the fixer found cell-edge mismatches the same
way the detector does, picked whichever side was "base game" (untouched
vanilla, or later, ITM-identical to a trusted plugin - see below), and
smoothly tapered that side's height/normal data toward the other side across
a handful of vertices inward from the shared edge (a smoothstep ease, zero
slope at both ends, inspired by how nudging-then-undoing terrain in the CK
snaps edges together).

This actually worked for a lot of real cases. But across many rounds of
real in-game testing it kept surfacing new failure modes, each one requiring
a new special-case check bolted onto the last:
- A cell needing correction on two edges at once needed the two pulls
  blended together (weighted by each taper's own strength), or the
  "abandoned" edge ended up worse than before the fix.
- Forcing a taper onto already-rugged/mountainous terrain folded the mesh
  and tore worse than the seam it was meant to fix - needed a roughness cap
  that skipped correction entirely above a measured threshold.
- Correcting a cell whose blend footprint overlapped a placed static
  reference (a wall piece, a rock) left the reference floating over moved
  ground - needed a reference-position exclusion check.
- The same for water: a nearby water plane calibrated against the
  pre-correction terrain could end up stranded above/below the new ground.
- **The one that ultimately ended the approach:** even after all of the
  above, the taper still only smooths *inward from the edge* (perpendicular
  to it) - nothing smooths *along* the edge. If the target boundary being
  blended toward was itself jagged (real mountain terrain, or a rugged mod
  edit), each row along the edge got its own, independently-tapered target
  delta, and the interior inherited that raw row-to-row jaggedness as a
  visible fold/tear - confirmed in-game as a literal black void/hole, not
  just a steep slope. A fix for *that* (smoothing the deltas along the edge
  before tapering them inward, handing off gradually from exact-at-boundary
  to smoothed-inward) worked, but by this point the pattern was clear: this
  approach could not be trusted to stay fixed, only patched one symptom at a
  time.

### Approach 2 (current): don't compute anything, restore what a trusted
### plugin actually authored

The insight that led here: **the vast majority of real "seams" aren't two
mods disagreeing about what terrain should look like - they're one mod
silently discarding another mod's already-correct fix.** Bethesda's CELL
format doesn't do partial overrides. Any plugin that touches a cell for
*any* reason (placing an NPC, adding a chest, anything - not just terrain)
carries forward a **complete copy** of every sub-record on that cell,
including `Landscape`, whether or not it meant to change it. If that plugin
loads after a trusted mod's real terrain repair, its untouched carried-
forward copy silently buries the repair - Creation Kit calls this an
"ITM" (Identical To Master) override when the copy is byte-identical to
what it inherited, but the game doesn't care why the record won, only that
it did.

Once framed that way, the fix is almost embarrassingly simple compared to
approach 1: for every cell, ask "does a trusted plugin have its own real
data for this cell that differs from what's actually winning?" If yes, copy
that trusted plugin's *entire* `LAND` record back verbatim - heights,
normals, textures, everything, exactly as authored. No blending, no
computed geometry, nothing that can introduce a new artifact, because
nothing is being computed at all. A cell with no trusted data of its own to
restore is left alone, even if it's a real edit bordering plain vanilla with
no vanilla-vs-mod conflict *for that specific cell* - approach 1 could
attempt to fix that case and approach 2 structurally can't, which is the
one real capability regression, accepted deliberately in exchange for
approach 2 being unable to introduce the class of bug approach 1 kept
producing.

Two safety checks still apply before writing a restoration (this tool never
touches `Cell.Water`/`WaterHeight`, and a static reference doesn't move just
because the ground under it does): a cell with its own water plane is
skipped, and a cell where a placed reference would end up floating more
than ~20 units is skipped and logged. Northern Roads is the one exception
that bypasses both (see below).

## The trust system

"Trusted" means: this plugin's data is assumed correct without checking, and
is the source copied *from*, never the target corrected *to*.

**Always trusted:** the game masters, USSEP, Legacy of the Dragonborn,
Landscape Seam Fixes.esp, the Landscape and Water Fixes family,
UniqueLocationsRiverwoodForest.esp, Lux Via.

**Compatibility patches are trusted automatically, and outrank their own
base mod.** Most Nexus patches are named `<Base Mod Name> - <what it
patches>.esp` - `IsPatchOfTrustedBase` in `SeamFixer.cs` auto-derives this
prefix from every trusted base name, so a new patch for an already-trusted
mod doesn't need a code change to be recognized. A patch is guaranteed to
win over its own base mod for any cell they both touch, *regardless of load
order* - not just "usually wins because patches load after their target."
A few mods use a totally unrelated prefix scheme instead (no way to derive
it from the filename); those need an explicit entry in
`NonStandardPatchPrefixes` - currently just Legacy of the Dragonborn
(`DBM_`/`DBM_CC_`/`LOTD_`/`LOTD_TCC_`). If you find another mod like this,
add it there.

**Northern Roads is opt-in** (a UI checkbox / `--trust-northern-roads` CLI
flag), because its Nexus page ships two downloads that both install as a
plugin literally named `Northern Roads.esp` (full terrain-reshaping vs.
clutter-only) - the filename alone can't tell them apart, so trusting it is
a decision only the user can make. Once enabled, it gets explicit priority
handling well beyond "just another trusted mod":
- It wins the trusted baseline for any cell it touches, *regardless of load
  order* (not just "wins ties") - because a later-loading trusted plugin
  that merely carried forward an untouched, ITM copy of that same cell
  (nothing to do with the road) would otherwise silently outrank it.
- `UniqueLocationsRiverwoodForest.esp` ranks *above* plain Northern Roads
  (confirmed by the user: it's built specifically to match Northern Roads'
  own road texturing in the areas it touches).
- Northern Roads' own compatibility-patch family ranks *above both* - a
  patch exists specifically to reconcile the two, so it's more authoritative
  than either alone wherever they overlap. (Real example that surfaced
  this: `Northern Roads - Unique Locations Riverwood patch.esp` was getting
  silently overwritten by plain URF data before this tier existed, undoing
  the patch and producing holes/dips right at the seam.)
- It skips both safety checks above (water plane, reference movement) -
  appropriate for a mod reshaping the entire road network, where leaving a
  handful of conflicted cells unfixed means visibly disconnected road
  segments, not an acceptable "rare edge case."

**Water mods are a completely separate trust system and code path**
(`ResolveTrustedWaterOnlyForCell`/`ResolveTrustedWorldspaceWaterOnly` in
`SeamFixer.cs`) from everything above - they only ever touch
`Cell.Water`/`WaterHeight`/`Flags.HasWater` and
`Worldspace.Water`/`LodWater`, never `Landscape`. Three independently-
toggleable mods, in a fixed priority order the user chose (their own CS
Water Mod first, then Water for ENB, then RealisticWaterTwo), each with its
compatibility-patch family trusted the same auto-derived-prefix way as
landscape. Modeled directly on a reference Synthesis patcher the user
already had and had validated (`CSWaterModPatcher` -
github.com/CageTV/CSwaterMod): forward a trusted mod's own
water/water-height/flags onto whatever's currently winning, unconditionally,
whenever that trusted mod isn't already the winner. No "does it differ"
check needed the way landscape needs one, since water fields are simple
scalars/references, not a sub-record complex enough to need an ITM
diff - if the trusted mod's own copy of the cell never touched water at
all, there's nothing to forward and it's skipped.

**User-added custom trust** (`Additional trusted plugins` text box, or
repeatable `--trust-plugin=` CLI flags) joins the ordinary ("always
trusted") pool above - exact name, or `Name -*` for a prefix/patch-family
match. It is never given Northern-Roads-style special priority or
safety-check bypass automatically; if a user's custom mod needs that too,
it needs a real code change (ask what the specific problem is first, same
as every other special case above started).

## Debugging lessons worth knowing before touching this code again

- **Cell grid `(X,Y)` coordinates are per-worldspace, not global.** A real
  load order has ~89 worldspaces (Tamriel plus every Creation Club/quest
  mod's own worldspace), and they all reuse the same small coordinate range
  near `(0,0)`. Every log line and diagnostic naming a bare `(X,Y)` is
  ambiguous between dozens of candidates unless it also states which
  worldspace - this cost real debugging time more than once before every
  log line got a `[WorldspaceName]` tag.
- **Don't log a per-cell diagnostic that fires for the vast majority of
  cells.** Custom-worldspace cells (no vanilla Tamriel-style trusted
  baseline to compare against) are the normal case for ~85 of ~89
  worldspaces - logging "nothing to compare against" for each one produced
  a 57,000-line log that was 99% one repeated line, burying the handful of
  lines that actually mattered. Only log the informative branches.
- **A cell's real, final winning value for a nullable field isn't
  necessarily on whichever context "wins the cell overall."** Mutagen
  represents "this override didn't touch this sub-field" as null/absent,
  not as a carried-forward copy of the previous value - so resolving
  "what's actually in effect" for `Landscape` (or `WaterHeight`/`Water`,
  same reasoning) requires walking every plugin's own context for that cell
  and picking the highest-priority one that actually has non-null data for
  that specific field, not just reading the field off whichever context won
  the cell record as a whole. `ResolveWinningLandscape`/
  `ResolveTrustedOnlyLandscape` in `SeamFixer.cs` both do this walk
  explicitly for exactly this reason.
- **"Trusted" and "safe to reshape toward a neighbor" are not the same
  question**, and conflating them was a real, shipped bug this session:
  comparing a cell's actual data against the *whole trusted chain*
  (including real fix-mod edits like Landscape and Water Fixes) to decide
  whether it was "default terrain" led to forcibly reshaping a trusted
  mod's own deliberate repair to match an unrelated mod next door, because
  being on the trusted list got conflated with being untouched/default.
  Comparing against literal pure-vanilla-masters-only data instead (a
  separate, narrower resolution) fixed it - see git history around the
  `PureVanillaMasters`/`ResolvePureVanillaLandscape` naming if it still
  exists at the point you're reading this (approach 2 above superseded the
  need for this distinction entirely, since it no longer reshapes anything).
- **When a special-case rule (Northern Roads) makes one plugin always win
  regardless of load order, make sure that rule's "always" doesn't also
  swallow a plugin that should have priority *over* it.** The
  Northern-Roads-patch-family tier exists purely because the simpler "Northern
  Roads always wins" rule, added first, was itself burying the very patches
  meant to reconcile Northern Roads with other mods.

## Project layout

- `xedit-scripts/` - abandoned Pascal Script prototype, kept for reference
- `SeamFinder.Core/` - shared logic, referenced by both the console app and
  the UI
  - `Mo2Resolver.cs` - MO2 profile parsing + plugin file resolution
  - `SeamDetector.cs` - the VHGT decode + seam comparison algorithm (report/
    detection only - does not share code with the fixer's restoration logic)
  - `SeamFixer.cs` - the trusted-data restoration logic described above:
    landscape restoration, the whole trust/patch-family system, and the
    separate water-restoration pass
- `SeamFinder/` - console CLI
  - `Program.cs` - entry point. `--mo2 <instancePath> <profileName>
    [gameDataPath]` runs detection only; `--fix <instancePath> <profileName>
    [gameDataPath] [flags]` runs the fixer. Fix-mode flags:
    `--trust-northern-roads`, `--trust-cs-water-mod`,
    `--trust-water-for-enb`, `--trust-realistic-water-two`, and repeatable
    `--trust-plugin="Some Mod.esp"` / `--trust-plugin="Some Family -*"`.
    (See comments at top of file for the other, less-reliable no-args mode
    kept for a quick vanilla-only sanity check.)
  - `LandscapeSeamFinder.synth` - leftover from the abandoned Synthesis
    attempt, not currently used
- `SeamFinder.UI/` - WPF UI wrapping the same `SeamFinder.Core` logic
  - Mode selector: MO2 (instance/profile/game path, with an Advanced section
    to override individual plugins.txt/loadorder.txt/modlist.txt paths) /
    Vortex (best-effort: assumes default hardlink deployment, so it's just
    the game's own Data folder - "Auto-detect" button finds it via Mutagen's
    own Steam/GOG/registry detection) / Direct game path (no mod manager)
  - Trust checkboxes (Northern Roads, the three water mods) and the
    "Additional trusted plugins" text box, all threaded through to
    `SeamFixer` as described above
  - Output: a folder, or "create as an MO2 mod folder" (writes a minimal
    `meta.ini` so it shows up as an installable mod inside MO2's mods list).
    A `log.txt` is written into that same output folder alongside the esp/
    csv on every run (mirrors everything the on-screen log shows), and a
    `settings-used.json` records the exact settings that run used.
  - All form fields/checkboxes/custom-trust-list persist across app
    launches automatically (`%AppData%\SeamFinder\settings.json`) - see
    `LoadPersistedSettings`/`SavePersistedSettings` in `MainWindow.xaml.cs`.

## If you're picking this codebase up cold

Read this whole file first, then `README.md` for user-facing behavior, then
skim `SeamFixer.cs` top-to-bottom - it's the one file with all of the actual
fixing logic and its own extensive inline comments explaining *why*, not
just what. Before adding a new "trust this mod" special case, check whether
the mod's patches follow the ordinary `<Name> - ...` convention first
(nothing to do, it's automatic) before reaching for
`NonStandardPatchPrefixes` or a Northern-Roads-style bespoke priority tier -
the latter should stay rare, reserved for cases with a real, specific,
confirmed-in-game reason for it, the same way every existing one was added.

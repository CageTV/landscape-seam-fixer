# Landscape Seam Fixer

**Current version: 2.1.0** — see [CHANGELOG.md](CHANGELOG.md) for what's new.

A standalone tool for Skyrim Special Edition / Anniversary Edition that fixes
**landscape seams** — the cracks, cliffs, holes, and broken roads that appear
where a mod's terrain edit gets silently overwritten by another plugin
loading after it. No Creation Kit work required.

It works entirely offline against your mod manager's own config files — it
does **not** need MO2 or Vortex running, and does not rely on either tool's
virtual file system.

> **Before you regenerate `LandscapeSeamFixes.esp` / `LandscapeSeamReport.csv`
> with a new version of this tool, delete the old ones first** (and remove
> the old `LandscapeSeamFixes.esp` from your load order before re-enabling
> the new one). The tool always writes a fresh file from scratch, but a
> stale copy left over from an older version sitting in your load order —
> especially one built before a fix in this changelog — can reintroduce
> exactly the problem the new version just solved.

## What it does

**Restores landscape a trusted plugin actually authored, wherever something
else has silently taken over the record.** Creation Kit carries forward a
full copy of a cell's terrain into *every* plugin that touches that cell for
*any* reason — placing an NPC, adding a chest, anything — even if that
plugin never meant to touch the ground at all. If that plugin loads after a
trusted mod's real terrain fix, its untouched copy silently buries the fix,
and the result is a seam, a hole, or a cliff that looks like a bug in the
trusted mod when it's really just load-order bad luck. This tool finds every
cell where that happened and copies the trusted plugin's own `LAND` record
back, verbatim — no blending, no computed geometry, just the exact data that
mod authored.

**Trusted by default:** the official Skyrim/DLC masters, the Unofficial
Skyrim Special Edition Patch, the Landscape and Water Fixes family,
Landscape Seam Fixes.esp, Lux Via, and UniqueLocationsRiverwoodForest.esp —
along with each of their compatibility patches, detected automatically (see
below).

**Opt-in (tick a box first):**
- **Northern Roads** — its Nexus page ships two same-named downloads (full
  terrain-reshaping vs. clutter-only), so this tool can't tell which one you
  have just from the filename; only enable this for the full version. Once
  on, Northern Roads (and its own compatibility-patch hub) gets priority
  over every other trusted plugin for any cell it touches, and skips the
  water-plane/reference-safety caution described below — appropriate for a
  mod that reshapes the entire road network, not just an occasional patch.
- **Water mods** (separate from landscape entirely) — CS Water Mod, Water
  for ENB, RealisticWaterTwo, in that priority order. Restores only
  `Water`/`WaterHeight`/water flags, never terrain. Independent of this,
  the tool *always* restores water from any trusted plugin (the same trust
  pool as landscape) whose water genuinely differs from vanilla, whenever a
  later, unrelated override has silently lost it — no toggle needed.
- **Your own additional trusted plugins** — a text box for anything not on
  the list above (exact name, or `Name -*` for a whole patch family). A
  second box lets you list plugins that should instead **win over** Northern
  Roads/UniqueLocationsRiverwoodForest, for a mod whose edit should take
  final priority over both.

**Recognizes sibling-tool output.** Road Mask Merger's, Landscape Texture
Fixer's, and Floating Object Fixer's generated plugins are treated as
trusted automatically, so this tool won't undo their work regardless of
which order you run them in.

**Patch detection is masters-based.** A plugin counts as a trusted mod's
patch if that mod is one of its literal ESP masters *and* the patch has at
least one genuine (vanilla-differing) landscape edit of its own — not
filename guessing. A few mods with non-standard patch-naming conventions
(e.g. Legacy of the Dragonborn's `DBM_`/`DBM_CC_`/`LOTD_`/`LOTD_TCC_`
prefixes) are still special-cased on top of that. Open an issue if you find
another mod like this and it'll get added.

**Settings are remembered.** Every path, checkbox, and custom trusted entry
is saved automatically after each run and restored next time you open the
app - no need to re-enter your setup every session.

## What it doesn't (yet) do

- If a mod's real, deliberate terrain edit borders plain untouched vanilla
  with **no trusted plugin's own data to restore for that specific cell**,
  this tool leaves it alone — there's nothing authored to copy back. An
  earlier version of this tool tried to smoothly blend these cases instead;
  that approach kept surfacing new failure modes in rugged terrain and was
  removed in favor of only ever doing something it can guarantee is safe.
- **Skyrim LE (Legendary Edition)** is not currently supported — the plugin
  format differs from SE/AE and hasn't been tested.

## Requirements

- Windows
- To just **run** the pre-built release: nothing extra — it's published
  self-contained (bundles its own .NET runtime).
- To **build from source**: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Building from source

```
git clone <this repo's URL>
cd "Landscape Seam Fixer"
```

Build everything:

```
dotnet build
```

Publish the desktop UI as a standalone folder (no separate .NET install
needed to run it):

```
dotnet publish SeamFinder.UI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o SeamFinder.UI/publish
```

`SeamFinder.UI/publish/SeamFinder.UI.exe` is then a self-contained,
double-clickable app (bundles the runtime, so it's a fairly large folder).

The console CLI (`SeamFinder/`) builds the same way and runs straight out of
`bin/Debug/net10.0/SeamFinder.exe` or `bin/Release/net10.0/SeamFinder.exe` —
no publish step needed for local use.

## Usage — desktop app (recommended)

1. Launch `SeamFinder.UI.exe`.
2. Pick how your mods are managed:
   - **Mod Organizer 2** — point it at your MO2 instance folder; it
     auto-lists profiles and reads `plugins.txt`/`loadorder.txt`/`modlist.txt`
     directly (an "Advanced" section lets you override any of those three
     paths individually if needed).
   - **Vortex** — point it at your game's Data folder (this is where Vortex
     deploys mods by default), or click "Auto-detect."
   - **Direct game path** — no mod manager; just your game's Data folder.
3. The **Output folder** auto-fills with a sensible default per mode (an MO2
   mod folder with an auto-generated `meta.ini`, or straight into
   Data for Vortex/Direct) — change it if you want it elsewhere.
4. Tick **Trust Northern Roads.esp**, any of the three **water mod**
   checkboxes, and/or fill in **Additional trusted plugins** for anything
   else you want restored — all optional, all off by default.
5. Click **Run Detection** to generate `LandscapeSeamReport.csv` in the
   output folder — a full list of every mismatched edge, which plugins own
   each side, and how severe it is.
6. Click **Generate Fix Plugin** to produce `LandscapeSeamFixes.esp` (and a
   `log.txt` explaining every cell it touched or skipped, and why) in the
   same folder. This is independent of step 5 — you don't need to run
   detection first. **If you're regenerating over an old run, delete the
   previous `LandscapeSeamFixes.esp`/`LandscapeSeamReport.csv` first.**
7. Install/activate the generated files like any other mod (in MO2 mode with
   the default output path, it's already recognized as an installable mod —
   just enable it and load it **after** the mods it's correcting, i.e. give
   it a high priority / late load order position).
8. **Test in-game** at a few spots before trusting it broadly — teleport
   near a worldspace edge with `coc <worldspace> <x> <y>` or the console
   `tcl`/`coc` combo, and eyeball it.

## Usage — command line

```
SeamFinder.exe --mo2 <instancePath> <profileName> [gameDataPath]
```
Runs detection only, writes `LandscapeSeamReport.csv` next to the exe.
`gameDataPath` is optional — if omitted, it's read from
`<instancePath>\ModOrganizer.ini`.

```
SeamFinder.exe --fix <instancePath> <profileName> [gameDataPath] [flags]
```
Generates `LandscapeSeamFixes.esp` next to the exe. Optional flags:

- `--trust-northern-roads`
- `--trust-cs-water-mod`, `--trust-water-for-enb`, `--trust-realistic-water-two`
- `--trust-plugin="Some Mod.esp"` (repeatable; a trailing `*` matches a prefix)

```
SeamFinder.exe
```
No-args standalone mode: only sees a plain Data folder auto-detected via the
usual Steam/GOG/registry lookup — **not** MO2/Vortex-aware. Prefer `--mo2`
above for a real modded load order; this mode mainly exists for a quick
vanilla-only sanity check.

## How it works

**Detection:** for every pair of adjacent exterior cells, decode both cells'
winning `VertexHeightMap` into absolute per-vertex heights (`Offset` +
delta-encoded 33×33 signed-byte grid, one raw step = 8 game units), then
compare the 33 vertices along the shared edge. Because `Landscape` is a
distinct record embedded in each plugin's own copy of a `CELL` (not a
separately-linked, independently-resolved record), the plugin that wins the
overall cell override isn't necessarily the one that owns the winning
terrain edit — the detector resolves that independently by walking every
plugin's own copy of the cell and picking the highest-priority one that
actually carries landscape data.

**MO2 resolution:** rather than depending on MO2's own virtual file system
(unreliable to drive programmatically in practice), `Mo2Resolver` reads a
profile's `plugins.txt`/`loadorder.txt`/`modlist.txt` directly and resolves
each active plugin's winning file itself, then stages the resolved files
into one merged folder for the detector/fixer to load from.

**Fixing:** for every exterior cell, resolve what the *trusted-only* plugin
chain (see "What it does" above) would produce, independently of whatever
plugin actually won the cell overall. If the trusted chain has real data for
that cell and it doesn't match what's actually winning, the trusted
plugin's entire `LAND` record — heights, normals, textures, everything — is
copied forward verbatim into the output plugin. Two safety checks run
before writing (skipped only for Northern Roads, per above): a cell with its
own water plane is left alone (this tool never touches `WaterHeight`, and
swapping terrain out from under an already-placed water plane can strand
it), and a cell where a placed static reference would end up floating more
than a trivial amount is also left alone and logged for manual review.

Water restoration (opt-in, separate machinery entirely) works the same
way but for `Cell.Water`/`WaterHeight`/`Flags.HasWater` and
`Worldspace.Water`/`LodWater` instead of terrain — never blended, never
computed, just the trusted mod's own value forwarded whenever something
else has taken over the record.

**Patch detection** (used by both this tool and its sibling, Landscape
Texture Fixer) recognizes a plugin as a trusted mod's patch when that mod is
a literal ESP master of it *and* the patch has at least one genuine,
vanilla-differing edit of its own — not by guessing filename prefixes.

## Project layout

- `SeamFinder.Core/` — shared library: VHGT decode/trust-detection logic
  this tool needs (`Mo2Resolver`, `TrustResolver`, `HeightmapDecoder`,
  `SeamDetector`, `SeamFixer`)
- `SeamFinder/` — console CLI
- `SeamFinder.UI/` — WPF desktop app

`SeamFinder.Core` here is a trimmed copy shared with two sibling tools
(Landscape Texture Fixer, Floating Object Fixer) that live in their own
separate repos — each repo only carries the subset of `SeamFinder.Core`
it actually uses.

## Contributing

Issues and PRs welcome, especially:
- Reports of specific seam locations the fixer didn't resolve (a
  `LandscapeSeamReport.csv` row plus an in-game screenshot is the most
  useful bug report)
- Other trusted mods worth adding by default, or mods whose compatibility
  patches use a non-standard naming scheme (like Legacy of the Dragonborn's)
- Ideas for safely extending the fixer beyond the "restore trusted data"
  case

## License

CC BY-NC-SA 4.0 — see [LICENSE](LICENSE). Free to use, modify, and share
(with attribution and under the same license), but not for commercial
purposes — no selling this tool or a modified version of it.

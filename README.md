# Landscape Seam Fixer

A standalone tool for Skyrim Special Edition / Anniversary Edition that finds
and fixes **landscape seams** — the cracks, cliffs, and holes that appear
where two mods independently edit neighboring worldspace cells' terrain
(`LAND`/`VHGT`) and the shared edge no longer lines up. No Creation Kit work
required.

It works entirely offline against your mod manager's own config files — it
does **not** need MO2 or Vortex running, and does not rely on either tool's
virtual file system.

## What it does

- **Detects** every exterior-cell edge in your load order where the winning
  terrain heights on either side disagree by more than one raw height step,
  and writes a full `LandscapeSeamReport.csv` you can review or search.
- **Fixes** the specific, most reliable case: a mod's isolated terrain edit
  bordering *untouched base-game terrain* (Skyrim.esm/Update.esm/the official
  DLCs). It generates a small patch plugin (`LandscapeSeamFixes.esp`) that
  leaves the mod's data untouched and blends the base-game side's edge
  toward it — a smooth taper across a few vertices, not a hard snap, so it
  doesn't leave a sharp crease of its own.

## What it doesn't (yet) do

- **Multi-mod pileups** — several overhaul mods all editing the same cluster
  of cells (a common case around heavily-modded locations) — are out of
  scope for the fixer right now. Detection still reports these edges; they
  just aren't auto-fixed, since "which mod is actually right" isn't a safe
  call to make automatically there.
- Where a single base-game cell needs correcting on more than one side at
  once (bordering two different mods), the blend averages both pulls rather
  than perfectly satisfying either — geometrically, no local blend can fully
  satisfy two independently-authored edges meeting at one shared corner. In
  practice this still substantially reduces the mismatch; it just doesn't
  always zero it out.
- **Skyrim LE (Legendary Edition)** is not currently supported — the plugin
  format differs from SE/AE and hasn't been tested.

## Requirements

- Windows
- To just **run** the pre-built release: nothing extra — it's published
  self-contained (bundles its own .NET runtime).
- To **build from source**: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Building from source

```
git clone https://github.com/CageTV/landscape-seam-fixer.git
cd LandscapeSeamFix
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
double-clickable app (~170 MB, since it bundles the runtime).

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
4. Click **Run Detection** to generate `LandscapeSeamReport.csv` in the
   output folder — a full list of every mismatched edge, which plugins own
   each side, and how severe it is.
5. Click **Generate Fix Plugin** to produce `LandscapeSeamFixes.esp` in the
   same folder. This is independent of step 4 — you don't need to run
   detection first.
6. Install/activate the generated files like any other mod (in MO2 mode with
   the default output path, it's already recognized as an installable mod —
   just enable it and load it **after** the mods it's correcting, i.e. give
   it a high priority / late load order position).
7. **Test in-game** at a few spots before trusting it broadly — teleport
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
SeamFinder.exe --fix <instancePath> <profileName> [gameDataPath]
```
Generates `LandscapeSeamFixes.esp` next to the exe.

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

**Fixing:** for a base-game cell bordering a mod's isolated edit, the mod's
edge data is treated as authoritative and copied toward across a short
smoothstep-eased taper (zero slope at both ends — no sharp kink), inspired
by how manually nudging-then-undoing terrain in the Creation Kit snaps edges
together with only a small, smooth angle change. Where a cell needs this on
more than one side at once, the overlapping corrections are blended together
(a weighted average by each correction's own taper strength) rather than one
silently overriding the other at the shared corner.

## Project layout

- `SeamFinder.Core/` — shared library: VHGT decode, `Mo2Resolver`
  (MO2 profile parsing), `SeamDetector`, `SeamFixer`
- `SeamFinder/` — console CLI
- `SeamFinder.UI/` — WPF desktop app
- `docs/DEVELOPMENT_HISTORY.md` — the fuller story of how this ended up as a
  standalone Mutagen-based tool, including two earlier approaches that were
  tried and abandoned (an xEdit/Pascal-Script prototype that reliably
  crashed xEdit itself at scale, and a Synthesis-patcher integration whose
  MO2 load-order resolution proved unreliable)

## Contributing

Issues and PRs welcome, especially:
- Reports of specific seam locations the fixer didn't resolve (a
  `LandscapeSeamReport.csv` row plus an in-game screenshot is the most
  useful bug report)
- Ideas for safely extending the fixer beyond isolated mod-vs-base cases

## License

MIT — see [LICENSE](LICENSE).

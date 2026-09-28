# Landscape Seam Fixer — Changelog

## v2.1.4 — 2026-09-28

- **Fixed: pointing the Game Data path at the game's install folder broke the run.** Using
  `...\Skyrim Special Edition` instead of `...\Skyrim Special Edition\Data` made every base-game, DLC and
  Creation Club plugin count as missing. The install folder is now accepted: the tool uses its `Data`
  subfolder and says so in the log.
- **New: the run stops up front when the load order can't be built.** If Skyrim.esm still can't be found,
  or an active plugin needs a master that isn't in any enabled mod, MO2's overwrite folder or the Data
  folder, the tool stops immediately with a message naming what's missing and how to fix it, instead of
  failing later with a bare "Could not find file" error.
- **Fixed: plugins in MO2's `overwrite` folder weren't found.** Generated plugins that live there
  (Synthesis.esp, a Bashed Patch, other tool output) counted as missing. `overwrite` is now checked first,
  since it outranks every mod folder.
- **Crash logs no longer contain the build machine's folder path** (which included its Windows user
  name). Stack traces still show file and line numbers.

## v2.1.3 — 2026-09-27

- **Fixed: VHGT `Offset` scaling** ([#2](https://github.com/CageTV/landscape-seam-fixer/issues/2)). The
  heightmap offset uses the same 8-unit step as the per-vertex deltas; reading it unscaled produced false
  seam detections. Same fix Road Mask Merger shipped in v1.4.3 — it had never reached this repo.
- **New water mod: Simplicity of Sea** (`water mod.esp`), as a fourth checkbox ranked below CS Water Mod,
  Water for ENB and RealisticWaterTwo.
- **New: "Other trusted water mods" box** in the UI for any other water replacer — one per line, exact
  filename or a `*`-prefix, ranked below the four named mods in list order.
- The app now uses the tool family's shared icon.

## v2.1.2 — 2026-09-19

Version-number-only release - no code change from v2.2.0 (this repo's own numbering had drifted
a minor version ahead of the number used on Nexus for the identical ESL-flagging fix). Renumbered
to stay on the same track as the published Nexus file for consistency across platforms.

## v2.2.0 — 2026-09-15

**New: automatic ESL flagging.** The generated fix plugin is now checked for ESL eligibility every run
(same logic as SSEEdit's own "Find ESP plugins which could be turned into ESL" script) and automatically
flagged as an ESL if it qualifies — this tool's output almost never adds brand-new records, so it's
eligible essentially every time. The log reports whether the flag was set and why, so you can always see
what happened.

## v2.1.0 — 2026-09-11

**New: "override" trust box.** The existing "Additional trusted plugins" box
still always loses to Northern Roads/UniqueLocationsRiverwoodForest when
both touch the same cell. A second box now lets you list plugins that
should **win** over Northern Roads/URF instead — useful if you have a mod
whose own landscape edit should take priority over both.

**New: recognizes sibling-tool output as trusted.** This tool no longer
tries to "restore" over cells that Road Mask Merger, Landscape Texture
Fixer, or Floating Object Fixer already produced — previously it could
silently undo Road Mask Merger's carefully-merged road terrain, which read
as a patchwork of broken cells right next to fine ones ("swiss cheese").

**New: general water restoration.** Water restoration used to only work
for three specific, named water-overhaul mods (CS Water Mod, Water for
ENB, RealisticWaterTwo). It now also restores water from *any* trusted
plugin whose water genuinely differs from vanilla, whenever a later,
unrelated override has silently lost it — the same trust logic the
terrain restoration already used. Fixed 43 cells on a real ~1250-plugin
profile in testing, including a case where a completely unrelated
compatibility patch's own broken override had wiped out a mod-added
creek's water.

---

## v2.0.5 — 2026-09-10

**Patch detection overhauled:** replaced fragile filename-prefix guessing
with real masters-based detection — a plugin now counts as a trusted mod's
patch if it's a literal ESP master of that mod **and** it has at least one
genuine (vanilla-differing) landscape edit, not just a name that happens to
start the right way.

**Fixes:**
- An unrelated compatibility patch carrying an unedited (ITM) copy of
  Northern Roads' or Unique Locations Riverwood Forest's terrain could
  silently outrank and bury a *real* edit from another trusted mod, just by
  loading later. Both now require a genuine-edit test before taking
  priority.
- A patch with nothing to do with the actual conflict at hand (e.g. an
  unrelated Northern Roads compatibility patch) could unconditionally win
  over a real edit. Fixed the same way — genuine-edit test required.
- The tool's own previous output could occasionally be mistaken for "a
  patch of everything," because it had inherited masters from everything it
  had ever copied forward. Fixed with an explicit self-exclusion plus a
  sane cap on how many masters a real hand-authored patch is expected to
  have.

**Internal:** trust/patch-detection logic and VHGT terrain decoding are now
shared with the sibling Landscape Texture Fixer and Floating Object Fixer
tools (previously duplicated, with real risk of drifting out of sync).

**Verified** against a real ~1250-plugin load order: 113 cells correctly
restored (up from 15 before this update).

---

## Earlier versions

v2.0.0 / v2.0.1 — see prior release notes (no changelog kept before this
version).

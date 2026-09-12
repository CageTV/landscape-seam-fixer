# Landscape Seam Fixer — Changelog

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

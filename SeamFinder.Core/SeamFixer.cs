// Generates a patch plugin that restores landscape a trusted plugin (a base
// game master, USSEP, the Landscape and Water Fixes family, Landscape Seam
// Fixes.esp, ...) actually authored for a cell, whenever a later, untrusted
// plugin has silently discarded it - e.g. a settlement mod that placed an
// NPC in the same cell without ever touching terrain itself, but which
// still wins the Landscape sub-record because Creation Kit carried forward
// its own (unedited, pre-trusted-repair) copy.
//
// Earlier versions of this tool instead tried to detect and smoothly blend
// height/normal MISMATCHES between neighboring cells - useful in principle
// (it could fix cases with no trusted data to restore at all, like a mod's
// real edit bordering plain untouched vanilla), but the blend math kept
// surfacing new failure modes in rugged terrain (row-to-row jaggedness,
// then normal-vector artifacts baked from that jaggedness) that were hard
// to fully trust. This version is deliberately much narrower and simpler in
// exchange for being unambiguously safe: it never computes or blends
// anything - it only ever copies a trusted plugin's own, already
// internally-consistent LAND record verbatim over a cell where something
// else has overwritten it. A cell with no trusted data of its own to
// restore is left untouched, even if its neighbor is a genuine, real edit
// with no vanilla-vs-mod conflict for THIS specific cell.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SeamFinder.Core;

public record SeamFixResult(int CellsPatched, int EdgesFixed, string OutputPath, float WorstSlopeDegrees, List<string> VerificationWarnings);

// Which water mods (each user-toggled independently) get the same
// "restore what this mod actually authored, verbatim, whenever something
// else has silently taken over the record" treatment as landscape gets -
// entirely separate machinery, though, since Cell.Water/WaterHeight/Flags
// and Worldspace.Water/LodWater are plain fields, not a distinct
// sub-record the way Landscape is. In priority order: CS Water Mod wins
// over Water for ENB, which wins over RealisticWaterTwo, wherever more
// than one has touched the same cell/worldspace - confirmed by the user
// as the intended order (their own mod first).
public record WaterTrustOptions(bool TrustCsWaterMod = false, bool TrustWaterForEnb = false, bool TrustRealisticWaterTwo = false)
{
    public static readonly WaterTrustOptions None = new();
    public bool Any => TrustCsWaterMod || TrustWaterForEnb || TrustRealisticWaterTwo;
}

public static class SeamFixer
{
    // Each family's base plugin name plus the prefix its own compatibility
    // patches are named with - mirrors the "Landscape and Water Fixes"
    // prefix-trust convention elsewhere in this file. Index order IS
    // priority order (lower index wins when more than one family has
    // touched the same record) - see WaterFamilyRank.
    static readonly (string BaseName, string Prefix)[] WaterModFamilies =
    [
        ("CS Water Mod.esp", "CS Water Mod"),
        ("Water for ENB.esp", "Water for ENB"),
        ("RealisticWaterTwo.esp", "RealisticWaterTwo"),
    ];

    // Rank of `plugin` among the enabled water families (0 = highest
    // priority), or -1 if it isn't a trusted, enabled water plugin at all.
    static int WaterFamilyRank(string plugin, WaterTrustOptions waterTrust)
    {
        Span<bool> enabled = [waterTrust.TrustCsWaterMod, waterTrust.TrustWaterForEnb, waterTrust.TrustRealisticWaterTwo];
        for (int i = 0; i < WaterModFamilies.Length; i++)
        {
            if (!enabled[i]) continue;
            var (baseName, prefix) = WaterModFamilies[i];
            if (plugin.Equals(baseName, StringComparison.OrdinalIgnoreCase) || plugin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
    static readonly HashSet<string> BaseGamePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
        "Unofficial Skyrim Special Edition Patch.esp", "Legacy of the Dragonborn.esm",
        // Third-party Nexus mod (spaces in the name - distinct from this
        // tool's own no-spaces output filename), built specifically as a
        // companion to Landscape and Water Fixes: nexusmods.com/.../59687.
        "Landscape Seam Fixes.esp",
        // Falkreath-area landscape overhaul, built to work WITH Northern
        // Roads (matches its road textures/area rather than fighting it) -
        // trusted unconditionally, and given priority ABOVE Northern Roads
        // itself in ResolveTrustedOnlyLandscape below, since it's the one
        // mod the user has confirmed should win over Northern Roads rather
        // than the other way around.
        "UniqueLocationsRiverwoodForest.esp",
        "Landscape and Water Fixes.esp",
        "Lux Via.esp",
    };

    // Every entry above is trusted alongside its own compatibility-patch
    // family too, not just standalone - most Nexus mods name patches
    // "<Mod Name> - <what it patches>.esp" (LWF's LFfGM/GotT/Myrwatch/...
    // patches, Lux Via's dozens of same-prefix patches, ...), so that
    // convention is auto-derived from each trusted base name below rather
    // than hardcoded per mod. A few mods use a totally unrelated prefix
    // scheme instead and need an explicit entry here - confirmed by the
    // user for Legacy of the Dragonborn specifically (its patches use
    // "DBM_"/"DBM_CC_"/"LOTD_"/"LOTD_TCC_", nothing derivable from its own
    // filename). Add future non-standard cases here as they turn up.
    static readonly Dictionary<string, string[]> NonStandardPatchPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Legacy of the Dragonborn.esm"] = ["DBM_", "DBM_CC_", "LOTD_", "LOTD_TCC_"],
    };

    // True if `plugin` is a compatibility patch belonging to `baseName`'s
    // family (NOT `baseName` itself) - either the common "<base> - ..."
    // naming convention, or one of the non-standard prefix sets above.
    static bool IsPatchOfTrustedBase(string plugin, string baseName)
    {
        if (plugin.Equals(baseName, StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(baseName);
        if (plugin.StartsWith(stem + " -", StringComparison.OrdinalIgnoreCase)) return true;
        if (NonStandardPatchPrefixes.TryGetValue(baseName, out var prefixes))
            foreach (var prefix in prefixes)
                if (plugin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // True if `plugin` is `baseName` itself or a patch of it, for any
    // trusted base name - the actual membership test IsBaseGamePlugin uses
    // for the "ordinary" trusted pool (everything except Northern Roads,
    // which is opt-in/ambiguous and handled separately below).
    static bool IsTrustedBaseOrPatch(string plugin) =>
        BaseGamePlugins.Any(b => plugin.Equals(b, StringComparison.OrdinalIgnoreCase) || IsPatchOfTrustedBase(plugin, b));

    // User-supplied additions to the trusted list (the UI's "Additional
    // trusted plugins" box) - each entry is either an exact plugin name, or
    // a prefix ending in "*" to trust a whole patch family the same way
    // the families above are matched. Kept as a plain ordered list rather
    // than a HashSet since prefix entries need StartsWith, not just exact
    // lookup; checked linearly, which is fine at the handful-of-entries
    // scale a text box realistically holds.
    static bool MatchesCustomTrust(string plugin, IReadOnlyList<string> customTrustedPlugins)
    {
        foreach (var pattern in customTrustedPlugins)
        {
            if (pattern.EndsWith('*'))
            {
                if (plugin.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (plugin.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Northern Roads is opt-in, not in the always-on BaseGamePlugins set:
    // its Nexus page ships two downloads that both install as a plugin
    // literally named "Northern Roads.esp" - the full terrain-reshaping
    // version and a clutter-only version - so the filename alone can't
    // distinguish them. Trusting the wrong one would blend other mods
    // toward a version that never actually reshaped this terrain. The
    // caller states which one is actually installed. Its own compatibility-
    // patch hub ("Northern Roads - <other mod> Patch.esp", e.g. "Northern
    // Roads - Unique Locations Riverwood patch.esp") is trusted alongside
    // it the same auto-derived way as everything else, and additionally
    // given priority ABOVE both plain Northern Roads and whatever it's
    // patching in ResolveTrustedOnlyLandscape below - a patch exists
    // specifically to reconcile the two, so it's more authoritative than
    // either alone wherever they actually overlap. Confirmed necessary in
    // practice: without that priority, a cell the patch legitimately won
    // (with its careful reconciliation) looked "untrusted" to this tool,
    // which overwrote it with plain Northern Roads or plain
    // UniqueLocationsRiverwoodForest.esp data, undoing the patch and
    // producing holes/dips at the seam between the two mods.
    static bool IsNorthernRoadsOrItsPatch(string plugin, bool trustNorthernRoads) =>
        trustNorthernRoads && (plugin.Equals("Northern Roads.esp", StringComparison.OrdinalIgnoreCase) || IsPatchOfTrustedBase(plugin, "Northern Roads.esp"));

    static bool IsBaseGamePlugin(string plugin, bool trustNorthernRoads, IReadOnlyList<string> customTrustedPlugins) =>
        IsTrustedBaseOrPatch(plugin)
        || MatchesCustomTrust(plugin, customTrustedPlugins)
        || IsNorthernRoadsOrItsPatch(plugin, trustNorthernRoads);

    /// Generates a fix plugin from an already-resolved list of active plugin
    /// files (e.g. from Mo2Resolver), by materializing them into a single
    /// merged folder and building a Mutagen environment from it - mirrors
    /// SeamDetector.RunForResolvedPlugins.
    public static SeamFixResult GenerateFixPluginForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool trustNorthernRoads = false,
        WaterTrustOptions? waterTrust = null,
        IReadOnlyList<string>? customTrustedPlugins = null)
    {
        var mergedFolder = Path.Combine(Path.GetTempPath(), "SeamFixerMerged-" + Guid.NewGuid().ToString("N"));
        log($"Staging {loadOrder.Count} plugin files into {mergedFolder} ...");
        Mo2Resolver.MaterializeMergedFolder(loadOrder, mergedFolder);

        try
        {
            var modKeys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
                .Create(GameRelease.SkyrimSE)
                .WithLoadOrder(modKeys)
                .WithTargetDataFolder(mergedFolder)
                .Build();

            var priorityIndex = modKeys.Select((k, idx) => (k, idx)).ToDictionary(x => x.k, x => x.idx);
            return GenerateFixPluginCore(env.LinkCache, priorityIndex, mergedFolder, outputPluginName, outputDirectory, log, trustNorthernRoads, waterTrust ?? WaterTrustOptions.None, customTrustedPlugins ?? []);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    /// Generates a fix plugin against a plain Data folder (no mod-manager
    /// awareness - e.g. Vortex's default deployment, or direct game path),
    /// using whatever plugins.txt that folder's install normally resolves
    /// to - mirrors SeamDetector.RunForDirectDataFolder.
    public static SeamFixResult GenerateFixPluginForDirectDataFolder(
        string dataFolderPath,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool trustNorthernRoads = false,
        WaterTrustOptions? waterTrust = null,
        IReadOnlyList<string>? customTrustedPlugins = null)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        return GenerateFixPluginCore(env.LinkCache, priorityIndex, dataFolderPath, outputPluginName, outputDirectory, log, trustNorthernRoads, waterTrust ?? WaterTrustOptions.None, customTrustedPlugins ?? []);
    }

    static SeamFixResult GenerateFixPluginCore(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        string dataFolderForWrite,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool trustNorthernRoads,
        WaterTrustOptions waterTrust,
        IReadOnlyList<string> customTrustedPlugins)
    {
        var outputModKey = ModKey.FromNameAndExtension(outputPluginName);
        var patchMod = new SkyrimMod(outputModKey, SkyrimRelease.SkyrimSE);

        int cellsPatched = 0;
        int cellsSkippedWater = 0;
        int cellsSkippedReference = 0;
        var verificationWarnings = new List<string>();

        foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            var cell = context.Record;
            if (cell.Grid is null) continue;
            if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext)) continue;
            // Cell grid (X,Y) coordinates are per-worldspace, not global -
            // Tamriel's (5,4) and Wyrmstooth's (5,4) are unrelated cells
            // that just happen to share coordinates. A real load order
            // carries ~89 worldspaces, so every log line naming bare (X,Y)
            // needs to say which worldspace too.
            var wsName = wsContext.Record.EditorID ?? wsContext.Record.FormKey.ToString();

            var (landscape, ownerModKey) = ResolveWinningLandscape(cell.FormKey, linkCache, priorityIndex);
            if (landscape?.VertexHeightMap is null) continue;

            // The winning owner is already trusted - this IS the
            // authoritative data, nothing to restore.
            if (IsBaseGamePlugin(ownerModKey.FileName, trustNorthernRoads, customTrustedPlugins)) continue;

            // Is there a trusted plugin's own repair for this exact cell
            // that the actual winner has silently discarded? "Discarded"
            // here doesn't require the untrusted winner to have made a
            // deliberate edit - Creation Kit carries forward an ITM copy of
            // whatever the winning chain had at the time a mod touches
            // anything else in a cell (e.g. placing an NPC), so a mod that
            // never meant to touch terrain at all can still end up "in
            // front of" a trusted repair in load order and silently hide
            // it. Either way, if the trusted plugin's data differs from
            // what's actually winning, that repair isn't visible in-game
            // right now.
            var (trustedLandscape, trustedOwnerModKey) = ResolveTrustedOnlyLandscape(cell.FormKey, linkCache, priorityIndex, trustNorthernRoads, customTrustedPlugins);
            if (trustedLandscape?.VertexHeightMap is null) continue; // no trusted data exists for this cell at all - nothing to restore

            var actualHeights = DecodeHeights(landscape.VertexHeightMap);
            var trustedHeights = DecodeHeights(trustedLandscape.VertexHeightMap);
            if (HeightsMatch(actualHeights, trustedHeights)) continue; // ITM - already matches the trusted repair, nothing to restore

            // Northern Roads reshapes terrain across the whole map, not as
            // an isolated patch like the rest of the trusted list - the
            // water/reference caution below exists for small, occasional
            // repairs where leaving a rare conflicted cell unfixed is a
            // fine trade. For a full road-network overhaul it isn't: it
            // means entire stretches of road are left broken (confirmed in
            // practice - several consecutive road cells right outside
            // Whiterun all skipped for the same reference-move reason,
            // producing a visibly disconnected road). The user explicitly
            // opts into this per-run via trustNorthernRoads, so once that's
            // on, Northern Roads' own restorations skip both safety gates
            // below entirely - every OTHER trusted plugin still goes
            // through them unchanged.
            string trustedOwnerFileName = trustedOwnerModKey.FileName;
            var isNorthernRoads = trustedOwnerFileName.Equals("Northern Roads.esp", StringComparison.OrdinalIgnoreCase)
                || (trustNorthernRoads && trustedOwnerFileName.StartsWith("Northern Roads -", StringComparison.OrdinalIgnoreCase));

            // Water safety: this tool never touches Cell.Water/WaterHeight.
            // A cell with its own water plane may have that water
            // calibrated against whatever terrain is CURRENTLY winning;
            // swapping the terrain out from under it without also fixing
            // the water risks stranding it above or below the new ground.
            var hasWater = (cell.WaterHeight.HasValue && cell.WaterHeight.Value < 1_000_000f)
                || cell.Water.FormKeyNullable.HasValue;
            if (hasWater && !isNorthernRoads)
            {
                cellsSkippedWater++;
                continue;
            }

            // Reference safety: a static reference (a wall piece, a rock,
            // anything placed against the CURRENT terrain) doesn't move
            // just because we restore different ground under it. Confirmed
            // necessary in practice earlier this session (a vanilla
            // ImpExtBldgStraight02 ruin piece ended up floating once its
            // surrounding ground moved by several hundred units) - same
            // risk here, just checked across the whole cell instead of a
            // narrow blend band, since a full-cell swap can move ground
            // anywhere in it, not just near an edge.
            var cellOriginX = cell.Grid.Point.X * 4096f;
            var cellOriginY = cell.Grid.Point.Y * 4096f;
            var worstReferenceMove = 0f;
            foreach (var r in cell.Persistent.Concat(cell.Temporary))
            {
                if (r.Placement is null) continue;
                var localX = r.Placement.Position.X - cellOriginX;
                var localY = r.Placement.Position.Y - cellOriginY;
                if (localX is < 0f or > 4096f || localY is < 0f or > 4096f) continue;
                var vx = Math.Clamp((int)Math.Round(localX / 128f), 0, 32);
                var vy = Math.Clamp((int)Math.Round(localY / 128f), 0, 32);
                var moved = Math.Abs(trustedHeights[vx, vy] - actualHeights[vx, vy]);
                if (moved > worstReferenceMove) worstReferenceMove = moved;
            }
            if (worstReferenceMove > 20f && !isNorthernRoads)
            {
                cellsSkippedReference++;
                verificationWarnings.Add($"[{wsName}] ({cell.Grid.Point.X},{cell.Grid.Point.Y}): skipped - restoring {trustedOwnerModKey.FileName}'s terrain would move a placed reference's ground by {worstReferenceMove:F0} units");
                continue;
            }

            // The actual fix: no blending, no computed normals - just
            // carry the trusted plugin's own Landscape record forward
            // verbatim, exactly as it authored it. Written under the TRUE
            // winning Cell's own context (not the trusted plugin's), so
            // whichever plugin actually won the Cell overall (e.g. by
            // placing an NPC) keeps owning its Persistent/Temporary lists -
            // only the Landscape sub-record is replaced.
            var writableCell = context.GetOrAddAsOverride(patchMod);
            foreach (var p in context.Record.Persistent) writableCell.Persistent.Add((IPlaced)p.DeepCopy());
            foreach (var t in context.Record.Temporary) writableCell.Temporary.Add((IPlaced)t.DeepCopy());
            writableCell.Landscape = trustedLandscape.DeepCopy();

            var (maxDiff, atX, atY) = MaxHeightDiff(actualHeights, trustedHeights);
            var bypassNote = isNorthernRoads && (hasWater || worstReferenceMove > 20f)
                ? $" [Northern Roads bypass: {(hasWater ? "has its own water plane" : $"would move a reference by {worstReferenceMove:F0} units")}, restored anyway]"
                : "";
            log($"  Restored [{wsName}] ({cell.Grid.Point.X},{cell.Grid.Point.Y}): {ownerModKey.FileName} had overwritten {trustedOwnerModKey.FileName}'s terrain by up to {maxDiff:F0} units (worst at vertex ({atX},{atY})){bypassNote}");
            cellsPatched++;
        }

        // Water pass: completely separate from everything above - never
        // reads or writes Landscape, only Cell.Water/WaterHeight/Flags and
        // Worldspace.Water/LodWater. A no-op entirely when no water mod is
        // ticked (WaterTrustOptions.None), so it can't affect a run that
        // doesn't ask for it.
        int cellsWaterPatched = 0;
        int worldspacesWaterPatched = 0;
        if (waterTrust.Any)
        {
            foreach (var wsContext in linkCache.WinningContextOverrides<Worldspace, IWorldspaceGetter>(linkCache))
            {
                var ws = wsContext.Record;
                var trustedWsWater = ResolveTrustedWorldspaceWaterOnly(ws.FormKey, linkCache, priorityIndex, waterTrust);
                if (trustedWsWater is null) continue;
                if (wsContext.ModKey.Equals(trustedWsWater.Value.OwnerModKey)) continue; // trusted mod already wins this worldspace outright

                var writableWs = wsContext.GetOrAddAsOverride(patchMod);
                bool changedAnything = false;
                if (trustedWsWater.Value.HasWater) { writableWs.Water = trustedWsWater.Value.Water.AsSetter().AsNullable(); changedAnything = true; }
                if (trustedWsWater.Value.HasLodWater) { writableWs.LodWater = trustedWsWater.Value.LodWater.AsSetter().AsNullable(); changedAnything = true; }
                if (!changedAnything) continue;

                log($"  Restored worldspace water [{ws.EditorID}]: {wsContext.ModKey.FileName} had overwritten {trustedWsWater.Value.OwnerModKey.FileName}'s water");
                worldspacesWaterPatched++;
            }

            foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
            {
                var cell = context.Record;
                if (cell.Grid is null) continue;

                var trustedWater = ResolveTrustedWaterOnlyForCell(cell.FormKey, linkCache, priorityIndex, waterTrust);
                if (trustedWater is null) continue; // no trusted+enabled water mod has any water data for this cell
                if (context.ModKey.Equals(trustedWater.Value.OwnerModKey)) continue; // trusted mod already wins this cell outright

                if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext)) continue;
                var wsName = wsContext.Record.EditorID ?? wsContext.Record.FormKey.ToString();

                var writableCell = context.GetOrAddAsOverride(patchMod);
                if (writableCell.Persistent.Count == 0 && writableCell.Temporary.Count == 0 && context.Record.Persistent.Count + context.Record.Temporary.Count > 0)
                {
                    // Only the landscape pass above already populates these -
                    // if this cell wasn't touched there, this override is
                    // brand new and needs its object lists carried forward
                    // too, same reasoning as the landscape write.
                    foreach (var p in context.Record.Persistent) writableCell.Persistent.Add((IPlaced)p.DeepCopy());
                    foreach (var t in context.Record.Temporary) writableCell.Temporary.Add((IPlaced)t.DeepCopy());
                }

                if (trustedWater.Value.HasWaterFlag) writableCell.Flags |= Cell.Flag.HasWater;
                if (trustedWater.Value.HasWaterLink) writableCell.Water = trustedWater.Value.Water!.AsSetter().AsNullable();
                writableCell.WaterHeight = trustedWater.Value.WaterHeight;

                log($"  Restored water [{wsName}] ({cell.Grid.Point.X},{cell.Grid.Point.Y}): {context.ModKey.FileName} had overwritten {trustedWater.Value.OwnerModKey.FileName}'s water");
                cellsWaterPatched++;
            }

            log($"Restored water in {cellsWaterPatched} cell(s) and {worldspacesWaterPatched} worldspace(s).");
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, outputPluginName);
        log($"Writing patch plugin to {outputPath} ...");
        SkyrimMod.WriteBuilder(SkyrimRelease.SkyrimSE)
            .ToPath(outputPath, fileSystem: null)
            .WithNoLoadOrder()
            .WithDataFolder(dataFolderForWrite)
            .WithAllParentMasters()
            .Write(patchMod);

        log($"Restored {cellsPatched} cell(s) to their trusted plugin's terrain" +
            (cellsSkippedWater > 0 ? $"; skipped {cellsSkippedWater} with their own water plane" : "") +
            (cellsSkippedReference > 0 ? $"; skipped {cellsSkippedReference} where a placed reference would move" : "") + ".");
        if (verificationWarnings.Count > 0)
        {
            log($"{verificationWarnings.Count} cell(s) skipped for a reference conflict - worth a manual look if that terrain still looks wrong:");
            foreach (var w in verificationWarnings) log("  " + w);
        }
        return new SeamFixResult(cellsPatched, cellsPatched, outputPath, 0f, verificationWarnings);
    }

    // Resolves the winning Landscape sub-record for a cell. Note this can be
    // owned by a DIFFERENT plugin than the one that wins the Cell overall -
    // a later plugin can win the Cell (e.g. by adding an NPC) without ever
    // redeclaring Landscape, leaving an earlier plugin's Landscape as the
    // one actually in effect. That's exactly why the write path (see
    // WriteCorrectedCell) targets the outer, TRUE winning Cell context, and
    // only uses this function's result as the source data to deep-copy and
    // correct - never as the context to write into.
    static (ILandscapeGetter? Landscape, ModKey OwnerModKey) ResolveWinningLandscape(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        int bestIndex = -1;

        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape is null) continue;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (idx > bestIndex)
            {
                bestIndex = idx;
                best = ctx.Record.Landscape;
                bestModKey = ctx.ModKey;
            }
        }

        return (best, bestModKey);
    }

    // Same walk as ResolveWinningLandscape, but restricted to contexts whose
    // owning plugin is itself trusted/base-equivalent - i.e. "what would this
    // cell's terrain be if only Skyrim.esm/DLC/USSEP/the Landscape and Water
    // Fixes family/etc. existed, ignoring any untrusted mod's override."
    // Used by the ITM check below: comparing this against the ACTUAL winning
    // landscape (which may come from an untrusted plugin) tells us whether
    // that untrusted plugin really edited the terrain here, or just carried
    // forward an unchanged copy of what the trusted chain already had.
    static (ILandscapeGetter? Landscape, ModKey OwnerModKey) ResolveTrustedOnlyLandscape(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        bool trustNorthernRoads,
        IReadOnlyList<string> customTrustedPlugins)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        int bestIndex = -1;
        ILandscapeGetter? northernRoads = null;
        ModKey northernRoadsModKey = default;
        ILandscapeGetter? riverwoodForest = null;
        ModKey riverwoodForestModKey = default;
        ILandscapeGetter? northernRoadsPatch = null;
        ModKey northernRoadsPatchModKey = default;
        int northernRoadsPatchIndex = -1;

        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape is null) continue;
            if (!IsBaseGamePlugin(ctx.ModKey.FileName, trustNorthernRoads, customTrustedPlugins)) continue;
            string fileName = ctx.ModKey.FileName;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (trustNorthernRoads && fileName.Equals("Northern Roads.esp", StringComparison.OrdinalIgnoreCase))
            {
                northernRoads = ctx.Record.Landscape;
                northernRoadsModKey = ctx.ModKey;
            }
            if (fileName.Equals("UniqueLocationsRiverwoodForest.esp", StringComparison.OrdinalIgnoreCase))
            {
                riverwoodForest = ctx.Record.Landscape;
                riverwoodForestModKey = ctx.ModKey;
            }
            if (trustNorthernRoads && IsPatchOfTrustedBase(fileName, "Northern Roads.esp") && idx > northernRoadsPatchIndex)
            {
                northernRoadsPatchIndex = idx;
                northernRoadsPatch = ctx.Record.Landscape;
                northernRoadsPatchModKey = ctx.ModKey;
            }
            // A patch always beats its own base mod here, regardless of
            // load order - not just "usually wins because patches tend to
            // load after their target," but guaranteed, since a patch's
            // whole purpose is to be more authoritative than the plain mod
            // it's patching. Unrelated trusted plugins (different families,
            // or no family at all) still fall back to ordinary load-order
            // priority against each other.
            if (best is null
                || IsPatchOfTrustedBase(fileName, bestModKey.FileName)
                || (!IsPatchOfTrustedBase(bestModKey.FileName, fileName) && idx > bestIndex))
            {
                bestIndex = idx;
                best = ctx.Record.Landscape;
                bestModKey = ctx.ModKey;
            }
        }

        // Northern Roads wins the trusted-only baseline whenever it has any
        // override for this cell at all, regardless of load order - the
        // ordinary "highest load-order index among the trusted set" rule
        // treats every trusted plugin as equally authoritative, which is
        // right for a handful of occasional patches but wrong for a
        // full-map road overhaul: a later-loading trusted plugin (e.g. an
        // LWF-family patch) can carry an ITM/incidental override for the
        // exact same cell that has nothing to do with the road, and would
        // otherwise silently outrank - and bury - Northern Roads' actual
        // road edit just by loading later. Confirmed necessary in practice:
        // a road cell (grid 4,-5) where Northern Roads' terrain never got
        // restored even with the tool otherwise working correctly nearby.
        //
        // UniqueLocationsRiverwoodForest.esp is ranked ABOVE plain Northern
        // Roads, not just alongside it - it's a Falkreath landscape
        // overhaul specifically built to match Northern Roads' own
        // textures/road area, so where the two disagree for the same cell,
        // its data is the one meant to be seen, confirmed directly by the
        // user.
        //
        // Northern Roads' own official compatibility patches (see
        // IsBaseGamePlugin's "Northern Roads -" prefix) rank ABOVE BOTH -
        // a patch exists specifically to reconcile Northern Roads against
        // whatever it's patching (URF included), so it's more authoritative
        // than either mod alone wherever they actually overlap. Confirmed
        // necessary in practice: without this tier, cells the official
        // "Northern Roads - Unique Locations Riverwood patch.esp" legitimately
        // won got silently overwritten with plain URF or plain Northern
        // Roads data, undoing the patch's reconciliation and producing
        // holes/dips right at the seam between the two mods.
        if (northernRoadsPatch is not null) return (northernRoadsPatch, northernRoadsPatchModKey);
        if (riverwoodForest is not null) return (riverwoodForest, riverwoodForestModKey);
        if (northernRoads is not null) return (northernRoads, northernRoadsModKey);

        return (best, bestModKey);
    }

    // Finds the highest-priority enabled water mod (see WaterFamilyRank)
    // that has its OWN real water data for this cell - mirrors the
    // reference CS Water Mod Synthesis patcher's logic (forward this
    // plugin's own Flags/Water/WaterHeight whenever it isn't already the
    // winner), generalized to a ranked set instead of a single mod. A
    // plugin whose own copy of this cell never touched water at all
    // (WaterHeight unset, no Water link, HasWater flag unset) has nothing
    // to contribute and is skipped, same as the script's "nothing to
    // forward from this cell" early-out.
    static (bool HasWaterFlag, bool HasWaterLink, IFormLinkNullableGetter<IWaterGetter>? Water, float? WaterHeight, ModKey OwnerModKey)? ResolveTrustedWaterOnlyForCell(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        WaterTrustOptions waterTrust)
    {
        int bestRank = int.MaxValue;
        int bestIndex = -1;
        (bool HasWaterFlag, bool HasWaterLink, IFormLinkNullableGetter<IWaterGetter>? Water, float? WaterHeight, ModKey OwnerModKey)? best = null;

        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            var rank = WaterFamilyRank(ctx.ModKey.FileName, waterTrust);
            if (rank < 0) continue;

            var hasFlag = ctx.Record.Flags.HasFlag(Cell.Flag.HasWater);
            var hasLink = ctx.Record.Water.FormKeyNullable.HasValue;
            if (!hasFlag && !hasLink && !ctx.Record.WaterHeight.HasValue) continue;

            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (rank < bestRank || (rank == bestRank && idx > bestIndex))
            {
                bestRank = rank;
                bestIndex = idx;
                best = (hasFlag, hasLink, ctx.Record.Water, ctx.Record.WaterHeight, ctx.ModKey);
            }
        }

        return best;
    }

    // Same idea as ResolveTrustedWaterOnlyForCell, but for the worldspace-
    // level Water/LodWater fields (the deep-water body and its LOD
    // counterpart) rather than a specific cell's shallow water.
    static (bool HasWater, bool HasLodWater, IFormLinkNullableGetter<IWaterGetter> Water, IFormLinkNullableGetter<IWaterGetter> LodWater, ModKey OwnerModKey)? ResolveTrustedWorldspaceWaterOnly(
        FormKey worldspaceFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        WaterTrustOptions waterTrust)
    {
        int bestRank = int.MaxValue;
        int bestIndex = -1;
        (bool HasWater, bool HasLodWater, IFormLinkNullableGetter<IWaterGetter> Water, IFormLinkNullableGetter<IWaterGetter> LodWater, ModKey OwnerModKey)? best = null;

        foreach (var ctx in linkCache.ResolveAllContexts<Worldspace, IWorldspaceGetter>(worldspaceFormKey, ResolveTarget.Winner))
        {
            var rank = WaterFamilyRank(ctx.ModKey.FileName, waterTrust);
            if (rank < 0) continue;

            var hasWater = ctx.Record.Water.FormKeyNullable.HasValue;
            var hasLodWater = ctx.Record.LodWater.FormKeyNullable.HasValue;
            if (!hasWater && !hasLodWater) continue;

            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (rank < bestRank || (rank == bestRank && idx > bestIndex))
            {
                bestRank = rank;
                bestIndex = idx;
                best = (hasWater, hasLodWater, ctx.Record.Water, ctx.Record.LodWater, ctx.ModKey);
            }
        }

        return best;
    }

    // True if every one of the 1089 vertices matches within a tiny epsilon -
    // "tiny" rather than exact-zero purely as float-safety margin, since both
    // sides were decoded through the same delta-step math and a genuinely
    // unedited/ITM copy reproduces the exact same bytes, not just similar
    // values. Real edits are never this close by accident - the smallest
    // legitimate correction this tool itself makes is many units, and hand
    // authored terrain edits are larger still.
    static bool HeightsMatch(float[,] a, float[,] b)
    {
        for (int y = 0; y <= 32; y++)
        for (int x = 0; x <= 32; x++)
            if (Math.Abs(a[x, y] - b[x, y]) > 0.5f) return false;
        return true;
    }

    // Diagnostic companion to HeightsMatch - finds the single worst-mismatched
    // vertex and where it is, so a failed ITM check can be understood instead
    // of just trusted as a bare "not identical" verdict.
    static (float MaxDiff, int X, int Y) MaxHeightDiff(float[,] a, float[,] b)
    {
        float maxDiff = 0f;
        int atX = -1, atY = -1;
        for (int y = 0; y <= 32; y++)
        for (int x = 0; x <= 32; x++)
        {
            var d = Math.Abs(a[x, y] - b[x, y]);
            if (d > maxDiff) { maxDiff = d; atX = x; atY = y; }
        }
        return (maxDiff, atX, atY);
    }

    static float[,] DecodeHeights(ILandscapeVertexHeightMapGetter vhgt)
    {
        var heights = new float[33, 33];
        var map = vhgt.HeightMap;
        for (int y = 0; y <= 32; y++)
        {
            for (int x = 0; x <= 32; x++)
            {
                sbyte delta = map[x, y];
                if (x == 0)
                {
                    heights[0, y] = y == 0
                        ? vhgt.Offset + delta * 8f
                        : heights[0, y - 1] + delta * 8f;
                }
                else
                {
                    heights[x, y] = heights[x - 1, y] + delta * 8f;
                }
            }
        }
        return heights;
    }

}

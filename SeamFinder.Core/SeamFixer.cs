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
    // Trust/patch-detection logic (BaseGamePlugins, masters-based patch
    // detection, custom trust, Northern Roads opt-in) now lives in
    // TrustResolver.cs, shared with TextureLayerFixer - see that file.

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
        IReadOnlyList<string>? customTrustedPlugins = null,
        IReadOnlyList<string>? priorityOverNorthernRoadsPlugins = null)
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
            var mastersByPlugin = BuildMastersByPlugin(env.LoadOrder.ListedOrder);
            return GenerateFixPluginCore(env.LinkCache, priorityIndex, mergedFolder, outputPluginName, outputDirectory, log, trustNorthernRoads, waterTrust ?? WaterTrustOptions.None, customTrustedPlugins ?? [], priorityOverNorthernRoadsPlugins ?? [], mastersByPlugin);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    // ESP masters, keyed by filename (not ModKey - matches how every other
    // trust check in this file compares plugins) - the data-driven signal
    // HasMasterRelationship uses instead of guessing prefixes.
    static Dictionary<string, HashSet<string>> BuildMastersByPlugin(IEnumerable<Mutagen.Bethesda.Plugins.Order.IModListingGetter<ISkyrimModGetter>> listedOrder)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in listedOrder)
        {
            if (listing.Mod is null) continue;
            result[listing.ModKey.FileName] = listing.Mod.ModHeader.MasterReferences
                .Select(m => m.Master.FileName.String)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return result;
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
        IReadOnlyList<string>? customTrustedPlugins = null,
        IReadOnlyList<string>? priorityOverNorthernRoadsPlugins = null)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        var mastersByPlugin = BuildMastersByPlugin(env.LoadOrder.ListedOrder);
        return GenerateFixPluginCore(env.LinkCache, priorityIndex, dataFolderPath, outputPluginName, outputDirectory, log, trustNorthernRoads, waterTrust ?? WaterTrustOptions.None, customTrustedPlugins ?? [], priorityOverNorthernRoadsPlugins ?? [], mastersByPlugin);
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
        IReadOnlyList<string> customTrustedPlugins,
        IReadOnlyList<string> priorityOverNorthernRoadsPlugins,
        Dictionary<string, HashSet<string>> mastersByPlugin)
    {
        TrustResolver.SetMastersContext(mastersByPlugin, outputPluginName);
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

            var (landscape, ownerModKey) = HeightmapDecoder.ResolveWinningLandscape(cell.FormKey, linkCache, priorityIndex);
            if (landscape?.VertexHeightMap is null) continue;

            // The winning owner is already trusted - this IS the
            // authoritative data, nothing to restore. A priority-override
            // plugin counts as trusted here too (it's just ranked ABOVE
            // Northern Roads/URF instead of below, in ResolveTrustedOnlyLandscape).
            if (TrustResolver.IsBaseGamePlugin(ownerModKey.FileName, trustNorthernRoads, customTrustedPlugins)
                || TrustResolver.MatchesCustomTrust(ownerModKey.FileName, priorityOverNorthernRoadsPlugins)) continue;

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
            var (trustedLandscape, trustedOwnerModKey) = ResolveTrustedOnlyLandscape(cell.FormKey, linkCache, priorityIndex, trustNorthernRoads, customTrustedPlugins, priorityOverNorthernRoadsPlugins);
            if (trustedLandscape?.VertexHeightMap is null) continue; // no trusted data exists for this cell at all - nothing to restore

            var actualHeights = HeightmapDecoder.DecodeHeights(landscape.VertexHeightMap);
            var trustedHeights = HeightmapDecoder.DecodeHeights(trustedLandscape.VertexHeightMap);
            if (HeightmapDecoder.HeightsMatch(actualHeights, trustedHeights)) continue; // ITM - already matches the trusted repair, nothing to restore

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
            // Same reasoning applies to UniqueLocationsRiverwoodForest.esp
            // (and its own "UniqueLocationsRiverwood -" patch family, see
            // NonStandardPatchPrefixes) as to Northern Roads: it's a
            // full-map landscape overhaul ("adding new hills, waterfalls,
            // ruins... drastically changing the landscape", per its own
            // Nexus page), not a narrow occasional patch - a carved
            // river/gorge cell is EXPECTED to sit below the water plane
            // (that's the point of the carve), so the water-safety gate's
            // "don't strand the water" caution is backwards here: leaving
            // vanilla's dry, above-water terrain in place is the actual bug.
            // Confirmed in practice: cell (1,-13) has vanilla terrain 656
            // units ABOVE this mod's carved floor at the vertex the player
            // was standing on, well past the water plane either way -
            // exactly the "solid ground where there should be a submerged
            // gorge" symptom this whole investigation started from.
            string trustedOwnerFileName = trustedOwnerModKey.FileName;
            var isFullLandscapeOverhaul = trustedOwnerFileName.Equals("Northern Roads.esp", StringComparison.OrdinalIgnoreCase)
                || (trustNorthernRoads && trustedOwnerFileName.StartsWith("Northern Roads -", StringComparison.OrdinalIgnoreCase))
                || trustedOwnerFileName.Equals("UniqueLocationsRiverwoodForest.esp", StringComparison.OrdinalIgnoreCase)
                || TrustResolver.IsPatchOfTrustedBase(trustedOwnerFileName, "UniqueLocationsRiverwoodForest.esp");
            var isNorthernRoads = isFullLandscapeOverhaul;

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

            var (maxDiff, atX, atY) = HeightmapDecoder.MaxHeightDiff(actualHeights, trustedHeights);
            var bypassNote = isFullLandscapeOverhaul && (hasWater || worstReferenceMove > 20f)
                ? $" [full-overhaul bypass: {(hasWater ? "has its own water plane" : $"would move a reference by {worstReferenceMove:F0} units")}, restored anyway]"
                : "";
            log($"  Restored [{wsName}] ({cell.Grid.Point.X},{cell.Grid.Point.Y}): {ownerModKey.FileName} had overwritten {trustedOwnerModKey.FileName}'s terrain by up to {maxDiff:F0} units (worst at vertex ({atX},{atY})){bypassNote}");
            cellsPatched++;
        }

        // Genuine trusted water pass: independent of (and runs BEFORE) the
        // CS Water Mod/Water for ENB/RealisticWaterTwo priority system
        // below - that system answers "which of these 3 specific overhaul
        // mods should win when they compete," this answers a DIFFERENT
        // question: "did some legitimate water-adding mod's real water get
        // silently lost by a LATER, unrelated override that didn't
        // preserve it?" Confirmed as a real need 2026-09-11: Half Moon
        // Creek.esp genuinely added water to a cell (a real WaterHeight and
        // Water-type link, differing from vanilla), and a LATER-loading,
        // completely unrelated compatibility patch's own generated
        // override reset it back to vanilla's blank state (that patch
        // itself carries the exact same "an override that doesn't
        // explicitly copy a field loses it" bug this toolkit's own
        // RoadMaskMerger was just fixed for - see its NOTES.md). Neither
        // Half Moon Creek nor its Northern Roads patch was ever a
        // configured "water mod" in the 3-mod system below, so nothing
        // caught this - the height-restoration trust pool is what SHOULD
        // have had an equivalent for water all along, and now does.
        //
        // Uses the SAME general trust pool height restoration already uses
        // (TrustResolver.IsBaseGamePlugin: game masters, USSEP, LWF family,
        // URF, Northern Roads/its patch family if opted in, custom-trusted
        // plugins) - "Northern Roads - Half Moon Creek patch.esp" already
        // qualifies via the "Northern Roads -" patch-family prefix, no new
        // trust-list entry needed. Same genuine-edit gate as height (must
        // actually differ from vanilla, not just an inert ITM copy) so an
        // incidentally-touching trusted plugin with no real water opinion
        // here can't silently override a legitimate, deliberate design.
        // Always runs (not gated by waterTrust.Any - this answers a
        // different question than the specific-3-mod system) and runs
        // FIRST so that system, when the user has enabled it, still gets
        // final say over any cell it also has an opinion on.
        int cellsGenuineWaterRestored = 0;
        foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            var cell = context.Record;
            if (cell.Grid is null) continue;

            var genuineWater = ResolveGenuineTrustedWaterForCell(cell.FormKey, linkCache, priorityIndex, trustNorthernRoads, customTrustedPlugins);
            if (genuineWater is null) continue;
            if (context.ModKey.Equals(genuineWater.Value.OwnerModKey)) continue; // trusted source already wins this cell outright

            // Only actually restore if the CURRENT winner's water really
            // differs from what the trusted source has - avoids a no-op
            // override for a cell that happens to already match.
            var currentHasLink = cell.Water.FormKeyNullable.HasValue;
            var matches = currentHasLink == genuineWater.Value.HasWaterLink
                && cell.WaterHeight == genuineWater.Value.WaterHeight
                && cell.Water.FormKeyNullable == genuineWater.Value.Water?.FormKeyNullable;
            if (matches) continue;

            if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext)) continue;
            var wsName = wsContext.Record.EditorID ?? wsContext.Record.FormKey.ToString();

            var writableCell = context.GetOrAddAsOverride(patchMod);
            if (writableCell.Persistent.Count == 0 && writableCell.Temporary.Count == 0 && context.Record.Persistent.Count + context.Record.Temporary.Count > 0)
            {
                // Same reasoning as the existing water pass below - a brand
                // new override needs its object lists carried forward too.
                foreach (var p in context.Record.Persistent) writableCell.Persistent.Add((IPlaced)p.DeepCopy());
                foreach (var t in context.Record.Temporary) writableCell.Temporary.Add((IPlaced)t.DeepCopy());
            }

            if (genuineWater.Value.HasWaterFlag) writableCell.Flags |= Cell.Flag.HasWater;
            if (genuineWater.Value.HasWaterLink) writableCell.Water = genuineWater.Value.Water!.AsSetter().AsNullable();
            writableCell.WaterHeight = genuineWater.Value.WaterHeight;

            log($"  Restored genuine water [{wsName}] ({cell.Grid.Point.X},{cell.Grid.Point.Y}): {context.ModKey.FileName} had lost {genuineWater.Value.OwnerModKey.FileName}'s water data");
            cellsGenuineWaterRestored++;
        }
        if (cellsGenuineWaterRestored > 0)
            log($"Restored genuinely-trusted water in {cellsGenuineWaterRestored} cell(s) (independent of the CS Water Mod/Water for ENB/RealisticWaterTwo system below).");

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

    // ResolveWinningLandscape now lives in HeightmapDecoder.cs, shared with
    // TextureLayerFixer.cs (note: it can resolve to a DIFFERENT plugin than
    // the one that wins the Cell overall - a later plugin can win the Cell,
    // e.g. by adding an NPC, without ever redeclaring Landscape. That's
    // exactly why the write path below targets the outer, TRUE winning Cell
    // context, and only uses this function's result as source data to
    // deep-copy and correct - never as the context to write into).

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
        IReadOnlyList<string> customTrustedPlugins,
        IReadOnlyList<string> priorityOverNorthernRoadsPlugins)
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
        // User-designated plugins that should win over BOTH Northern Roads
        // and UniqueLocationsRiverwoodForest.esp wherever they genuinely
        // conflict - the "other side" of the Additional Trusted Plugins box
        // (see the UI's two boxes / NOTES.md). Ranked among themselves by
        // ordinary load order (highest idx wins) same as the "best" pool
        // below, since the user gave no other ordering signal for this set.
        ILandscapeGetter? priorityOverride = null;
        ModKey priorityOverrideModKey = default;
        int priorityOverrideIndex = -1;
        // Vanilla's own copy, captured so the unconditional URF/Northern-Roads
        // priority below can tell a REAL edit apart from Creation Kit's inert
        // ITM carry-forward - see IsGenuineEdit.
        ILandscapeGetter? vanilla = null;

        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape is null) continue;
            string fileName = ctx.ModKey.FileName;
            bool isPriorityOverride = TrustResolver.MatchesCustomTrust(fileName, priorityOverNorthernRoadsPlugins);
            if (!TrustResolver.IsBaseGamePlugin(fileName, trustNorthernRoads, customTrustedPlugins) && !isPriorityOverride) continue;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (fileName.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
            {
                vanilla = ctx.Record.Landscape;
            }
            if (isPriorityOverride && idx > priorityOverrideIndex)
            {
                priorityOverrideIndex = idx;
                priorityOverride = ctx.Record.Landscape;
                priorityOverrideModKey = ctx.ModKey;
            }
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
            if (trustNorthernRoads && TrustResolver.IsPatchOfTrustedBase(fileName, "Northern Roads.esp") && idx > northernRoadsPatchIndex)
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
                || TrustResolver.IsPatchOfTrustedBase(fileName, bestModKey.FileName)
                || (!TrustResolver.IsPatchOfTrustedBase(bestModKey.FileName, fileName) && idx > bestIndex))
            {
                bestIndex = idx;
                best = ctx.Record.Landscape;
                bestModKey = ctx.ModKey;
            }
        }

        // 2026-09-11 addition: BEFORE any of the tiers below, a user-listed
        // priority-override plugin (the UI's second trust box) wins over
        // ALL of them - Northern Roads' own patch family included - the
        // same "unconditional regardless of load order" way Northern Roads/
        // URF win over the ordinary trusted pool. This is the mirror image
        // of that same problem one level up: without it, there was no way
        // for the user to say "I want THIS mod's edit to be the one that
        // sticks, even where it conflicts with Northern Roads/URF" - every
        // custom-trusted plugin was permanently capped below both. See the
        // priorityOverride check right after IsGenuineEdit below.
        //
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
        //
        // Second bug fixed here: the patch tier was originally exempt from
        // the genuine-edit check on the theory that "a compat patch
        // deliberately matching vanilla is still an authoritative decision."
        // That's true for a patch that actually RECONCILES the two mods in
        // dispute here - but "Northern Roads -" only tells us the patch's
        // OTHER side is Northern Roads, not that its other side is whatever
        // is actually being restored (URF, in this case). A patch for a
        // totally unrelated conflict (confirmed in practice: "Northern
        // Roads - Skyrim Wayshrines patch.esp" touching a cell it has
        // nothing to say about, carrying vanilla-identical data there
        // purely incidentally) would otherwise unconditionally bury a real
        // URF carve underneath its own irrelevant ITM copy. So this tier
        // now requires the SAME genuine-edit test as plain URF/Northern
        // Roads below - a patch that actually reconciles something still
        // passes it (its whole purpose is to differ from at least one
        // side), and an incidental, uninvolved patch now correctly falls
        // through instead of masking the real trusted edit.
        bool IsGenuineEdit(ILandscapeGetter? candidate) =>
            candidate?.VertexHeightMap is not null && vanilla?.VertexHeightMap is not null
            && !HeightmapDecoder.HeightsMatch(HeightmapDecoder.DecodeHeights(candidate.VertexHeightMap), HeightmapDecoder.DecodeHeights(vanilla.VertexHeightMap));

        // Highest tier of all: a user-designated priority-override plugin
        // wins unconditionally over Northern Roads/URF/the NR patch family
        // below, wherever it genuinely edited this cell itself - checked
        // BEFORE every other tier, on purpose. Same IsGenuineEdit gate as
        // the rest: an override plugin that merely carries an inert ITM
        // copy of vanilla forward here still shouldn't get to silently bury
        // a real Northern Roads/URF edit just for being in this list.
        if (priorityOverride is not null && IsGenuineEdit(priorityOverride)) return (priorityOverride, priorityOverrideModKey);

        if (northernRoadsPatch is not null && IsGenuineEdit(northernRoadsPatch)) return (northernRoadsPatch, northernRoadsPatchModKey);

        // Plain URF/Northern Roads only get this unconditional priority when
        // they actually EDITED this cell - not when they're merely carrying
        // an unedited ITM copy of vanilla forward (which Creation Kit does
        // constantly for any cell a mod touches for an unrelated reason,
        // e.g. placing an NPC). Bug fixed here: this used to fire on ANY
        // entry at all, so an inert URF/Northern Roads ITM could silently
        // outrank - and bury - a REAL edit from another trusted mod (LWF,
        // the real "Landscape Seam Fixes.esp", etc.) that "best" above would
        // otherwise have correctly picked, on any cell URF/Northern Roads
        // never actually touched. Same IsGenuineEdit test as the patch tier
        // above.
        if (riverwoodForest is not null && IsGenuineEdit(riverwoodForest)) return (riverwoodForest, riverwoodForestModKey);
        if (northernRoads is not null && IsGenuineEdit(northernRoads)) return (northernRoads, northernRoadsModKey);

        return (best, bestModKey);
    }

    // The general-trust counterpart to ResolveTrustedOnlyLandscape, for
    // water instead of height - see the "Genuine trusted water pass"
    // comment at its call site for the full reasoning. Walks every context
    // for this cell, finds vanilla's own water baseline plus the highest-
    // priority TRUSTED (TrustResolver.IsBaseGamePlugin) plugin whose water
    // genuinely differs from vanilla, and returns that as the restoration
    // source - or null if no trusted plugin has a genuine water edit here
    // at all.
    static (bool HasWaterFlag, bool HasWaterLink, IFormLinkNullableGetter<IWaterGetter>? Water, float? WaterHeight, ModKey OwnerModKey)? ResolveGenuineTrustedWaterForCell(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        bool trustNorthernRoads,
        IReadOnlyList<string> customTrustedPlugins)
    {
        var vanilla = ((bool HasWaterFlag, bool HasWaterLink, IFormLinkNullableGetter<IWaterGetter>? Water, float? WaterHeight, ModKey OwnerModKey)?)null;
        var trustedCandidates = new List<(bool HasWaterFlag, bool HasWaterLink, IFormLinkNullableGetter<IWaterGetter>? Water, float? WaterHeight, ModKey OwnerModKey, int Index)>();

        // Pass 1: collect vanilla's own baseline plus every TRUSTED
        // candidate with any water data at all - genuineness can only be
        // judged once vanilla is known, which isn't guaranteed to appear
        // before a trusted candidate in iteration order.
        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            var hasFlag = ctx.Record.Flags.HasFlag(Cell.Flag.HasWater);
            var hasLink = ctx.Record.Water.FormKeyNullable.HasValue;
            var waterHeight = ctx.Record.WaterHeight;
            if (!hasFlag && !hasLink && !waterHeight.HasValue) continue; // nothing water-related here at all

            string fileName = ctx.ModKey.FileName;
            var entry = (hasFlag, hasLink, (IFormLinkNullableGetter<IWaterGetter>?)ctx.Record.Water, waterHeight, ctx.ModKey);

            if (fileName.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                vanilla = entry;

            if (!TrustResolver.IsBaseGamePlugin(fileName, trustNorthernRoads, customTrustedPlugins)) continue;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            trustedCandidates.Add((entry.hasFlag, entry.hasLink, entry.Item3, entry.waterHeight, entry.ModKey, idx));
        }

        if (vanilla is null || trustedCandidates.Count == 0) return null;

        // Pass 2: among the TRUSTED candidates, pick the highest-priority
        // one that GENUINELY differs from vanilla - checked DURING
        // selection, not after picking "whichever trusted plugin loads
        // latest" and hoping it happens to be genuine. Confirmed as a real
        // bug in an earlier version of this method 2026-09-11: a later-
        // loading trusted plugin ("Northern Roads - Rocks Patch.esp") with
        // no real water opinion here (an inert ITM copy of vanilla) was
        // being picked purely for loading later, silently hiding an
        // EARLIER-loading trusted plugin's ("Northern Roads - Half Moon
        // Creek patch.esp") genuine water a few positions before it in
        // load order - exactly the case this whole pass exists to catch.
        (bool HasWaterFlag, bool HasWaterLink, IFormLinkNullableGetter<IWaterGetter>? Water, float? WaterHeight, ModKey OwnerModKey)? best = null;
        int bestIndex = -1;
        foreach (var c in trustedCandidates)
        {
            if (c.Index <= bestIndex) continue;
            bool differsFromVanilla = c.HasWaterLink != vanilla.Value.HasWaterLink
                || c.WaterHeight != vanilla.Value.WaterHeight
                || c.Water?.FormKeyNullable != vanilla.Value.Water?.FormKeyNullable;
            if (!differsFromVanilla) continue;
            bestIndex = c.Index;
            best = (c.HasWaterFlag, c.HasWaterLink, c.Water, c.WaterHeight, c.OwnerModKey);
        }

        return best;
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

    // VHGT decode/compare logic now lives in HeightmapDecoder.cs, shared
    // with SeamDetector.cs (was duplicated identically in both before).
}

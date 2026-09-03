// Core landscape-seam detection logic, shared between the console CLI and
// the WPF UI. See Mo2Resolver.cs for how an MO2 instance gets turned into
// a real load order without needing MO2's own VFS.
//
// Landscape is a property embedded directly in each plugin's own copy of a
// CELL record (not a separately FormID-linked/resolved record), so the
// overall "winning CELL override" is not guaranteed to be the same plugin
// that actually owns the winning landscape edit - some cell overrides don't
// touch landscape at all and have a null Landscape. ResolveWinningLandscape
// resolves that independently by walking every plugin's own copy of the
// cell and picking the highest-priority one that actually has non-null
// landscape data.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

public record DecodedCell(float[,] Heights, string Plugin);

public record DetectionResult(
    List<string> ReportCsvLines,
    int PluginCount,
    int CellsProcessed,
    int WorldspaceCount,
    int SkippedInterior,
    int SkippedNoWorldspace,
    int SkippedNoLandscape,
    int OrderingMismatchWarnings,
    int SeamCount);

public static class SeamDetector
{
    public const float ToleranceUnits = 8.0f; // one raw heightmap step; ignore anything smaller as float noise

    /// Runs detection against an already-resolved list of active plugin
    /// files (e.g. from Mo2Resolver), by materializing them into a single
    /// merged folder and building a Mutagen environment from it.
    public static DetectionResult RunForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        Action<string> log)
    {
        var mergedFolder = Path.Combine(Path.GetTempPath(), "SeamFinderMerged-" + Guid.NewGuid().ToString("N"));
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
            return RunDetection(env.LinkCache, priorityIndex, modKeys.Length, log);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    /// Runs detection against a plain Data folder (no mod-manager awareness -
    /// e.g. a non-MO2-managed install, or a folder where mods were dumped
    /// directly into Data). Uses whatever plugins.txt that folder's install
    /// normally resolves to via Mutagen's own auto-detection.
    public static DetectionResult RunForDirectDataFolder(string dataFolderPath, Action<string> log)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        return RunDetection(env.LinkCache, priorityIndex, env.LoadOrder.Count, log);
    }

    public static DetectionResult RunDetection(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        int loadOrderCount,
        Action<string> log)
    {
        log($"Load order: {loadOrderCount} plugins.");

        var cellsByWorldspace = new Dictionary<FormKey, Dictionary<(int X, int Y), DecodedCell>>();
        int processed = 0, skippedInterior = 0, skippedNoLandscape = 0, skippedNoWorldspace = 0, orderingMismatchWarnings = 0;

        foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            var cell = context.Record;
            if (cell.Grid is null)
            {
                skippedInterior++;
                continue;
            }

            if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext))
            {
                skippedNoWorldspace++;
                continue;
            }
            var worldspaceKey = wsContext.Record.FormKey;

            var (landscape, ownerModKey) = ResolveWinningLandscape(cell.FormKey, linkCache, priorityIndex);
            if (landscape?.VertexHeightMap is null)
            {
                skippedNoLandscape++;
                continue;
            }

            // Sanity cross-check: the plugin we picked as Landscape's own
            // winner should never be *later* in load order than the plugin
            // that won the overall CELL record - if it is, the "highest
            // load-order index" selection below has gone wrong somehow.
            var cellWinnerIndex = priorityIndex.GetValueOrDefault(context.ModKey, -1);
            var landscapeWinnerIndex = priorityIndex.GetValueOrDefault(ownerModKey, -1);
            if (landscapeWinnerIndex > cellWinnerIndex)
                orderingMismatchWarnings++;

            var heights = DecodeHeights(landscape.VertexHeightMap);

            if (!cellsByWorldspace.TryGetValue(worldspaceKey, out var cellDict))
            {
                cellDict = new Dictionary<(int, int), DecodedCell>();
                cellsByWorldspace[worldspaceKey] = cellDict;
            }
            cellDict[(cell.Grid.Point.X, cell.Grid.Point.Y)] = new DecodedCell(heights, ownerModKey.FileName);
            processed++;
        }

        log($"Processed {processed} exterior cells across {cellsByWorldspace.Count} worldspace(s).");
        log($"Skipped: {skippedInterior} interior, {skippedNoWorldspace} no worldspace parent, {skippedNoLandscape} no landscape data.");
        if (orderingMismatchWarnings > 0)
            log($"WARNING: {orderingMismatchWarnings} cells had an unexpected override-priority ordering - " +
                "decode results for those may be unreliable, investigate before trusting the report.");

        var report = new List<string> { "Worldspace,Edge,CellA_X,CellA_Y,PluginA,CellB_X,CellB_Y,PluginB,SamePlugin,MaxDeltaUnits,WorstVertex" };

        foreach (var (wsKey, cellDict) in cellsByWorldspace)
        {
            foreach (var ((x, y), cellA) in cellDict)
            {
                if (cellDict.TryGetValue((x + 1, y), out var cellBEast))
                    CompareEdge(report, wsKey, "East", x, y, cellA, x + 1, y, cellBEast);

                if (cellDict.TryGetValue((x, y + 1), out var cellBNorth))
                    CompareEdge(report, wsKey, "North", x, y, cellA, x, y + 1, cellBNorth);
            }
        }

        var seamCount = report.Count - 1;
        log($"Found {seamCount} seam edges above {ToleranceUnits} unit tolerance.");

        return new DetectionResult(
            report, loadOrderCount, processed, cellsByWorldspace.Count,
            skippedInterior, skippedNoWorldspace, skippedNoLandscape,
            orderingMismatchWarnings, seamCount);
    }

    static (ILandscapeGetter? Landscape, ModKey OwnerModKey) ResolveWinningLandscape(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        int bestIndex = -1;

        foreach (var ctx in linkCache.ResolveAllSimpleContexts<ICellGetter>(cellFormKey, ResolveTarget.Winner))
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

    static void CompareEdge(List<string> report, FormKey worldspace, string edgeName,
        int ax, int ay, DecodedCell a, int bx, int by, DecodedCell b)
    {
        float maxDelta = 0;
        int worst = -1;
        for (int i = 0; i <= 32; i++)
        {
            float ha, hb;
            if (edgeName == "East")
            {
                ha = a.Heights[32, i];
                hb = b.Heights[0, i];
            }
            else
            {
                ha = a.Heights[i, 32];
                hb = b.Heights[i, 0];
            }
            var d = Math.Abs(ha - hb);
            if (d > maxDelta) { maxDelta = d; worst = i; }
        }

        if (maxDelta > ToleranceUnits)
        {
            report.Add($"{worldspace},{edgeName},{ax},{ay},{a.Plugin},{bx},{by},{b.Plugin}," +
                $"{a.Plugin == b.Plugin},{maxDelta:0.0},{worst}");
        }
    }
}

// Generates a patch plugin that fixes "isolated mod-vs-base" landscape
// seams: cases where a mod's landscape edit borders untouched base-game
// terrain (Skyrim.esm/Update.esm/official DLC masters) and the shared edge
// doesn't line up. The mod's side is treated as authoritative and left
// untouched; the base-game cell gets a new override with its edge blended
// toward the mod's data.
//
// The blend isn't a hard copy-and-crease at the boundary - it tapers over
// a few vertices inward using a smoothstep ease (zero slope at both ends),
// based on the observation that manually nudging-then-undoing landscape in
// CK snaps edges together with only a small-angle taper, not a sharp kink.
//
// Deliberately narrower in scope than the full seam report for a first
// pass: multi-mod pileups (like a whole region merged by the user's own
// xEdit-generated patch) have a much murkier "who's actually right" question
// and are left for later.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SeamFinder.Core;

public record SeamFixResult(int CellsPatched, int EdgesFixed, string OutputPath);

public static class SeamFixer
{
    // How many vertices inward from the shared edge the blend correction
    // tapers across. Larger = gentler but touches more terrain.
    const int BlendWidth = 6;

    static readonly HashSet<string> BaseGamePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"
    };

    record CellData(
        float[,] Heights,
        string Plugin,
        IModContext<ISkyrimMod, ISkyrimModGetter, Cell, ICellGetter> CellContext);

    /// Generates a fix plugin from an already-resolved list of active plugin
    /// files (e.g. from Mo2Resolver), by materializing them into a single
    /// merged folder and building a Mutagen environment from it - mirrors
    /// SeamDetector.RunForResolvedPlugins.
    public static SeamFixResult GenerateFixPluginForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        string outputPluginName,
        string outputDirectory,
        Action<string> log)
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
            return GenerateFixPluginCore(env.LinkCache, priorityIndex, mergedFolder, outputPluginName, outputDirectory, log);
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
        Action<string> log)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        return GenerateFixPluginCore(env.LinkCache, priorityIndex, dataFolderPath, outputPluginName, outputDirectory, log);
    }

    static SeamFixResult GenerateFixPluginCore(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        string dataFolderForWrite,
        string outputPluginName,
        string outputDirectory,
        Action<string> log)
    {
        {
            var outputModKey = ModKey.FromNameAndExtension(outputPluginName);
            var patchMod = new SkyrimMod(outputModKey, SkyrimRelease.SkyrimSE);

            // Pass 1: decode every exterior cell, keeping the winning CELL's
            // own mod-context (not just its data) so we can write an
            // override for it later without re-walking the worldspace
            // hierarchy by hand.
            var cellsByWorldspace = new Dictionary<FormKey, Dictionary<(int X, int Y), CellData>>();

            foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
            {
                var cell = context.Record;
                if (cell.Grid is null) continue;
                if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext)) continue;
                var worldspaceKey = wsContext.Record.FormKey;

                var (landscape, ownerModKey, ownerCellContext) =
                    ResolveWinningLandscapeWithContext(cell.FormKey, linkCache, priorityIndex);
                if (landscape?.VertexHeightMap is null || ownerCellContext is null) continue;

                var heights = DecodeHeights(landscape.VertexHeightMap);

                if (!cellsByWorldspace.TryGetValue(worldspaceKey, out var cellDict))
                {
                    cellDict = new Dictionary<(int, int), CellData>();
                    cellsByWorldspace[worldspaceKey] = cellDict;
                }
                cellDict[(cell.Grid.Point.X, cell.Grid.Point.Y)] = new CellData(heights, ownerModKey.FileName, ownerCellContext);
            }

            log($"Decoded {cellsByWorldspace.Sum(kv => kv.Value.Count)} cells across {cellsByWorldspace.Count} worldspace(s).");

            // Pass 2: find ModVsBaseEdge seams, keyed by the base-game cell
            // that needs fixing and which of ITS OWN edges (East/West/
            // North/South) faces the mod's terrain.
            var corrections = new Dictionary<(FormKey Worldspace, int X, int Y), List<(string Edge, float[,] SourceHeights, float Severity)>>();

            foreach (var (wsKey, cellDict) in cellsByWorldspace)
            {
                foreach (var ((x, y), cellA) in cellDict)
                {
                    if (cellDict.TryGetValue((x + 1, y), out var cellBEast))
                        RecordIfModVsBase(corrections, wsKey, x, y, cellA, x + 1, y, cellBEast, "East");
                    if (cellDict.TryGetValue((x, y + 1), out var cellBNorth))
                        RecordIfModVsBase(corrections, wsKey, x, y, cellA, x, y + 1, cellBNorth, "North");
                }
            }

            log($"Found {corrections.Count} base-game cells bordering a mod edit that need a blend fix.");

            int edgesFixed = 0;
            foreach (var ((wsKey, x, y), edgeList) in corrections)
            {
                var cellData = cellsByWorldspace[wsKey][(x, y)];
                var working = (float[,])cellData.Heights.Clone();

                // A cell needing fixes on more than one side (e.g. both East
                // and North) has overlapping blend footprints near the
                // shared corner. Rather than letting one correction claim
                // that region exclusively (which leaves it matching neither
                // neighbor well - sometimes making the abandoned edge worse
                // than before the fix), accumulate every applicable
                // correction's pull per-vertex and apply a weighted average,
                // weighted by each one's own taper strength. A corner
                // touched by two edges' blends ends up as a compromise
                // between both targets, fading smoothly to zero at the
                // inward edge of each taper - never a hard cutoff.
                var deltaAccum = new float[33, 33];
                var weightAccum = new float[33, 33];
                foreach (var (myEdge, sourceHeights, _) in edgeList)
                {
                    AccumulateBlend(cellData.Heights, myEdge, sourceHeights, deltaAccum, weightAccum);
                    edgesFixed++;
                }

                for (int vy = 0; vy <= 32; vy++)
                for (int vx = 0; vx <= 32; vx++)
                {
                    if (weightAccum[vx, vy] <= 0f) continue;
                    working[vx, vy] += deltaAccum[vx, vy] / weightAccum[vx, vy];
                }

                WriteCorrectedCell(cellData.CellContext, patchMod, working);
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

            log($"Patched {corrections.Count} cells ({edgesFixed} edges).");
            return new SeamFixResult(corrections.Count, edgesFixed, outputPath);
        }
    }

    static (ILandscapeGetter? Landscape, ModKey OwnerModKey, IModContext<ISkyrimMod, ISkyrimModGetter, Cell, ICellGetter>? Context)
        ResolveWinningLandscapeWithContext(
            FormKey cellFormKey,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
            Dictionary<ModKey, int> priorityIndex)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        IModContext<ISkyrimMod, ISkyrimModGetter, Cell, ICellGetter>? bestContext = null;
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
                bestContext = ctx;
            }
        }

        return (best, bestModKey, bestContext);
    }

    // A cell only gets queued for fixing if exactly one side is base game
    // AND the mismatch actually exceeds tolerance (mirrors SeamDetector's
    // own check, so this only ever touches edges the report also flagged).
    static void RecordIfModVsBase(
        Dictionary<(FormKey, int, int), List<(string Edge, float[,] SourceHeights, float Severity)>> corrections,
        FormKey ws, int ax, int ay, CellData a, int bx, int by, CellData b, string edgeBetweenAandB)
    {
        var aIsBase = BaseGamePlugins.Contains(a.Plugin);
        var bIsBase = BaseGamePlugins.Contains(b.Plugin);
        if (aIsBase == bIsBase) return; // both base or both mod - not our scope for this pass

        var maxDelta = ComputeMaxDelta(a.Heights, b.Heights, edgeBetweenAandB);
        if (maxDelta <= SeamDetector.ToleranceUnits) return;

        if (aIsBase)
        {
            AddCorrection(corrections, ws, ax, ay, edgeBetweenAandB, b.Heights, maxDelta);
        }
        else
        {
            var oppositeEdge = edgeBetweenAandB == "East" ? "West" : "South";
            AddCorrection(corrections, ws, bx, by, oppositeEdge, a.Heights, maxDelta);
        }
    }

    static void AddCorrection(
        Dictionary<(FormKey, int, int), List<(string Edge, float[,] SourceHeights, float Severity)>> corrections,
        FormKey ws, int x, int y, string myEdge, float[,] sourceHeights, float severity)
    {
        var key = (ws, x, y);
        if (!corrections.TryGetValue(key, out var list))
        {
            list = new List<(string, float[,], float)>();
            corrections[key] = list;
        }
        list.Add((myEdge, sourceHeights, severity));
    }

    static float ComputeMaxDelta(float[,] a, float[,] b, string edgeName)
    {
        float maxDelta = 0;
        for (int i = 0; i <= 32; i++)
        {
            float ha, hb;
            if (edgeName == "East") { ha = a[32, i]; hb = b[0, i]; }
            else { ha = a[i, 32]; hb = b[i, 0]; }
            var d = Math.Abs(ha - hb);
            if (d > maxDelta) maxDelta = d;
        }
        return maxDelta;
    }

    // Computes the correction this edge alone would want to make - pulling
    // the cell's boundary toward `source` (the authoritative neighbor),
    // tapering inward across BlendWidth vertices with a smoothstep ease
    // (full correction exactly at the boundary, fading to zero with no
    // slope discontinuity at either end of the transition band) - and adds
    // its (weight * delta, weight) into the running accumulators instead of
    // writing to the cell directly. Multiple edges (e.g. a cell needing both
    // a South and a West correction) naturally overlap near a shared
    // corner; accumulating lets the final weighted average there reflect a
    // bit of both targets rather than one excluding the other.
    //
    // `originalHeights` (never mutated) is the reference every edge's
    // target/delta is measured against, so overlapping corrections are
    // computed independently of each other and only combined at the end.
    static void AccumulateBlend(float[,] originalHeights, string myEdge, float[,] source, float[,] deltaAccum, float[,] weightAccum)
    {
        for (int i = 0; i <= 32; i++)
        {
            var (boundaryRow, boundaryCol) = myEdge switch
            {
                "East" => (32, i),
                "West" => (0, i),
                "North" => (i, 32),
                "South" => (i, 0),
                _ => throw new InvalidOperationException($"Unknown edge {myEdge}")
            };

            float targetVal = myEdge switch
            {
                "East" => source[0, i],
                "West" => source[32, i],
                "North" => source[i, 0],
                "South" => source[i, 32],
                _ => throw new InvalidOperationException($"Unknown edge {myEdge}")
            };
            var delta = targetVal - originalHeights[boundaryRow, boundaryCol];

            for (int step = 0; step < BlendWidth; step++)
            {
                var (row, col) = myEdge switch
                {
                    "East" => (32 - step, i),
                    "West" => (step, i),
                    "North" => (i, 32 - step),
                    "South" => (i, step),
                    _ => throw new InvalidOperationException($"Unknown edge {myEdge}")
                };

                var t = 1f - (float)step / BlendWidth; // 1.0 at the boundary, ->0 at BlendWidth in
                var weight = Smoothstep(t);
                deltaAccum[row, col] += delta * weight;
                weightAccum[row, col] += weight;
            }
        }
    }

    static float Smoothstep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Re-encodes corrected absolute heights back into the raw delta-byte
    // format (Offset + 33x33 signed-byte grid, 8 units/step) and writes it
    // as a new Cell override in patchMod.
    //
    // Deltas are computed against the ACTUAL reconstructed running value at
    // each step (not the ideal corrected target), matching how the game
    // will really decode them - otherwise rounding to the nearest 8-unit
    // step could drift away from the intended shape across the 33-vertex
    // chain. Untouched regions (outside any blend band) reproduce the
    // exact original bytes with zero rounding error, since the original
    // heights were themselves exact multiples of 8 apart.
    static void WriteCorrectedCell(
        IModContext<ISkyrimMod, ISkyrimModGetter, Cell, ICellGetter> cellContext,
        SkyrimMod patchMod,
        float[,] correctedHeights)
    {
        // GetOrAddAsOverride deep-copies the Cell's own fields, but Landscape
        // is itself a distinct major record (its own FormKey) embedded
        // inside Cell, not a plain data field - it doesn't cascade
        // automatically and comes back null. Deep-copy it explicitly,
        // preserving its FormKey, before touching it.
        var sourceLandscape = cellContext.Record.Landscape
            ?? throw new InvalidOperationException("Source cell has no Landscape - should have been filtered out already.");
        var writableCell = cellContext.GetOrAddAsOverride(patchMod);
        writableCell.Landscape = sourceLandscape.DeepCopy();
        var vhgt = writableCell.Landscape!.VertexHeightMap!;
        var offset = vhgt.Offset;
        var unknownBytes = vhgt.Unknown;

        var actual = new float[33, 33];
        var newDeltas = new sbyte[33, 33];

        for (int y = 0; y <= 32; y++)
        {
            for (int x = 0; x <= 32; x++)
            {
                float prevActual = x == 0
                    ? (y == 0 ? offset : actual[0, y - 1])
                    : actual[x - 1, y];

                var diff = correctedHeights[x, y] - prevActual;
                var deltaSteps = (int)Math.Round(diff / 8.0, MidpointRounding.AwayFromZero);
                deltaSteps = Math.Clamp(deltaSteps, -128, 127);

                newDeltas[x, y] = (sbyte)deltaSteps;
                actual[x, y] = prevActual + deltaSteps * 8f;
            }
        }

        // HeightMap is init-only on LandscapeVertexHeightMap - can't mutate
        // the existing instance in place, so build a whole replacement
        // (Offset and Unknown carried over unchanged) and swap it in.
        writableCell.Landscape!.VertexHeightMap = new LandscapeVertexHeightMap
        {
            Offset = offset,
            Unknown = unknownBytes,
            HeightMap = new Array2d<sbyte>(newDeltas),
        };
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

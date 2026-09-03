// LandscapeSeamFinder CLI - detects landscape seams: adjacent exterior
// cells whose winning heightmap edges don't line up, caused by two mods
// editing neighboring cells' terrain independently. Read-only - never
// writes any game records, only a LandscapeSeamReport.csv. Core logic
// lives in SeamFinder.Core (shared with SeamFinder.UI).
//
// Three ways to run this exe:
//   - No args: STANDALONE mode. Auto-detects the game via
//     GameEnvironment.Typical - this sees your real, merged mod list ONLY
//     if the exe is actually launched through MO2 (e.g. added as an MO2
//     "Executable" and run with MO2's Run button, which activates its
//     virtual file system for this process). Double-click/run outside MO2
//     and it only sees the vanilla install.
//   - "--mo2 <instancePath> <profileName> [gameDataPath]": reads the MO2
//     profile's plugins.txt/loadorder.txt/modlist.txt directly and
//     resolves each active plugin's winning file itself (SeamFinder.Core's
//     Mo2Resolver) - no VFS needed at all, works regardless of how the
//     exe was launched. This is the reliable path; prefer it over
//     standalone-via-MO2-Executable.
//   - "run-patcher ..." (Synthesis's CLI protocol): runs through
//     SynthesisPipeline instead. Kept in case that integration is worth
//     revisiting; in practice Synthesis's own MO2 integration did not
//     reliably resolve the real profile's load order when tested - see
//     README.md for the history.
//
// See SeamFinder.Core for why this isn't an xEdit script (crashed xEdit
// itself at scale) and the VHGT decode details.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using SeamFinder.Core;

var synthesisVerbs = new[] { "run-patcher", "settings-query", "check-runnability", "open-for-settings" };
if (args.Length > 0 && synthesisVerbs.Contains(args[0]))
{
    await SynthesisPipeline.Instance
        .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunAsSynthesisPatch)
        .SetTypicalOpen(GameRelease.SkyrimSE, "LandscapeSeamFinder.esp")
        .Run(args);
}
else if (args.Length > 0 && args[0] == "--mo2")
{
    RunMo2Mode(args);
}
else
{
    RunStandalone();
}

void RunStandalone()
{
    Console.WriteLine("LandscapeSeamFinder - standalone mode.");
    Console.WriteLine("If run outside MO2 (double-clicked, or run directly), this only sees your");
    Console.WriteLine("vanilla install. Prefer --mo2 <instancePath> <profileName> instead - see");
    Console.WriteLine("README.md - which works regardless of how the exe is launched.");
    Console.WriteLine();

    try
    {
        using var env = GameEnvironment.Typical.Construct<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE);
        Console.WriteLine($"Detected Data folder: {env.DataFolderPath}");

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        var result = SeamDetector.RunDetection(env.LinkCache, priorityIndex, env.LoadOrder.Count, Console.WriteLine);
        WriteReport(result.ReportCsvLines, Path.Combine(AppContext.BaseDirectory, "LandscapeSeamReport.csv"));
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine("ERROR: " + ex);
    }

    Pause();
}

void RunMo2Mode(string[] mo2Args)
{
    if (mo2Args.Length < 3)
    {
        Console.WriteLine("Usage: SeamFinder.exe --mo2 <instancePath> <profileName> [gameDataPath]");
        Console.WriteLine("  gameDataPath is optional - if omitted, reads gamePath from <instancePath>\\ModOrganizer.ini");
        return;
    }
    var instancePath = mo2Args[1];
    var profileName = mo2Args[2];
    var gameDataPath = mo2Args.Length > 3 ? mo2Args[3] : ReadGamePathFromIni(instancePath);

    Console.WriteLine($"MO2 instance: {instancePath}");
    Console.WriteLine($"Profile: {profileName}");
    Console.WriteLine($"Game Data path: {gameDataPath}");
    Console.WriteLine();

    try
    {
        var resolved = Mo2Resolver.Resolve(instancePath, profileName, gameDataPath);
        Console.WriteLine($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
        if (resolved.MissingPlugins.Count > 0)
        {
            Console.WriteLine($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found in any enabled mod folder or the game Data folder:");
            foreach (var m in resolved.MissingPlugins) Console.WriteLine("  " + m);
        }

        var result = SeamDetector.RunForResolvedPlugins(resolved.LoadOrder, Console.WriteLine);
        WriteReport(result.ReportCsvLines, Path.Combine(AppContext.BaseDirectory, "LandscapeSeamReport.csv"));
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine("ERROR: " + ex);
    }

    Pause();
}

void RunAsSynthesisPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
{
    Console.WriteLine($"DataFolderPath (resolved): {state.DataFolderPath.Path}");
    Console.WriteLine($"LoadOrderFilePath (resolved): {state.LoadOrderFilePath.Path}");

    var priorityIndex = state.RawLoadOrder
        .Select((listing, idx) => (listing.ModKey, idx))
        .ToDictionary(x => x.ModKey, x => x.idx);

    var result = SeamDetector.RunDetection(state.LinkCache, priorityIndex, state.RawLoadOrder.Count, Console.WriteLine);

    var outPath = Path.GetFullPath(Path.Combine(state.DataFolderPath.Path, "..", "LandscapeSeamReport.csv"));
    WriteReport(result.ReportCsvLines, outPath);
}

void WriteReport(List<string> lines, string outPath)
{
    File.WriteAllLines(outPath, lines);
    Console.WriteLine();
    Console.WriteLine($"Report written to: {outPath}");
}

void Pause()
{
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    try { Console.ReadKey(); } catch (InvalidOperationException) { /* no console input (e.g. redirected/piped) - just exit */ }
}

static string ReadGamePathFromIni(string instancePath)
{
    var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
    if (!File.Exists(iniPath))
        throw new FileNotFoundException("No gameDataPath given and ModOrganizer.ini not found to read it from.", iniPath);

    foreach (var line in File.ReadAllLines(iniPath))
    {
        if (!line.StartsWith("gamePath=")) continue;
        // Stored as gamePath=@ByteArray(C:\\Program Files (x86)\\...\\Skyrim Special Edition)
        var value = line["gamePath=".Length..].Trim();
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start >= 0 && end > start)
            value = value[(start + 1)..end];
        value = value.Replace("\\\\", "\\");
        return Path.Combine(value, "Data");
    }

    throw new InvalidOperationException("Could not find gamePath= in ModOrganizer.ini - pass gameDataPath explicitly instead.");
}

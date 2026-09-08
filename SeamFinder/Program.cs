// LandscapeSeamFinder CLI - detects landscape seams: adjacent exterior
// cells whose winning heightmap edges don't line up, caused by two mods
// editing neighboring cells' terrain independently. Core logic lives in
// SeamFinder.Core (shared with SeamFinder.UI).
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
//   - "--fix <instancePath> <profileName> [gameDataPath]": generates
//     LandscapeSeamFixes.esp for the isolated mod-vs-base-game cases.
//
// Add --trust-northern-roads anywhere in the args to treat Northern
// Roads.esp as base-game-equivalent too. Opt-in rather than always-on:
// its Nexus page ships two downloads that both install as a plugin
// literally named "Northern Roads.esp" (full terrain-reshaping version vs.
// clutter-only), so the tool can't tell which one is installed on its own -
// only pass this if you have the FULL version.
//
// See SeamFinder.Core for the VHGT decode details.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SeamFinder.Core;

if (args.Length > 0 && args[0] == "--mo2")
{
    RunMo2Mode(args);
}
else if (args.Length > 0 && args[0] == "--fix")
{
    RunFixMode(args);
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
        Console.WriteLine("Usage: SeamFinder.exe --mo2 <instancePath> <profileName> [gameDataPath] [--trust-northern-roads]");
        Console.WriteLine("  gameDataPath is optional - if omitted, reads gamePath from <instancePath>\\ModOrganizer.ini");
        return;
    }
    var instancePath = mo2Args[1];
    var profileName = mo2Args[2];
    var gameDataPath = mo2Args.Length > 3 && !mo2Args[3].StartsWith("--") ? mo2Args[3] : ReadGamePathFromIni(instancePath);

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

        var result = SeamDetector.RunForResolvedPlugins(resolved.LoadOrder, Console.WriteLine, TrustNorthernRoads(mo2Args));
        WriteReport(result.ReportCsvLines, Path.Combine(AppContext.BaseDirectory, "LandscapeSeamReport.csv"));
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine("ERROR: " + ex);
    }

    Pause();
}

void RunFixMode(string[] fixArgs)
{
    if (fixArgs.Length < 3)
    {
        Console.WriteLine("Usage: SeamFinder.exe --fix <instancePath> <profileName> [gameDataPath] [--trust-northern-roads]");
        Console.WriteLine("  Generates LandscapeSeamFixes.esp fixing isolated mod-vs-base-game seams only.");
        Console.WriteLine("  gameDataPath is optional - if omitted, reads gamePath from <instancePath>\\ModOrganizer.ini");
        return;
    }
    var instancePath = fixArgs[1];
    var profileName = fixArgs[2];
    var gameDataPath = fixArgs.Length > 3 && !fixArgs[3].StartsWith("--") ? fixArgs[3] : ReadGamePathFromIni(instancePath);

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
            Console.WriteLine($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
            foreach (var m in resolved.MissingPlugins) Console.WriteLine("  " + m);
        }

        var fixResult = SeamFixer.GenerateFixPluginForResolvedPlugins(
            resolved.LoadOrder, "LandscapeSeamFixes.esp", AppContext.BaseDirectory, Console.WriteLine, TrustNorthernRoads(fixArgs), WaterTrust(fixArgs), CustomTrustedPlugins(fixArgs));
        Console.WriteLine();
        Console.WriteLine($"Restored {fixResult.CellsPatched} cell(s) to their trusted plugin's terrain.");
        Console.WriteLine($"Output: {fixResult.OutputPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine("ERROR: " + ex);
    }

    Pause();
}

bool TrustNorthernRoads(string[] a) => a.Contains("--trust-northern-roads", StringComparer.OrdinalIgnoreCase);

WaterTrustOptions WaterTrust(string[] a) => new(
    a.Contains("--trust-cs-water-mod", StringComparer.OrdinalIgnoreCase),
    a.Contains("--trust-water-for-enb", StringComparer.OrdinalIgnoreCase),
    a.Contains("--trust-realistic-water-two", StringComparer.OrdinalIgnoreCase));

// Repeatable: --trust-plugin="Some Mod.esp" --trust-plugin="Some Family -*"
List<string> CustomTrustedPlugins(string[] a) => a
    .Where(arg => arg.StartsWith("--trust-plugin=", StringComparison.OrdinalIgnoreCase))
    .Select(arg => arg["--trust-plugin=".Length..].Trim('"'))
    .Where(p => p.Length > 0)
    .ToList();

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

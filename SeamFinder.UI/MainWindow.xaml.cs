using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Skyrim;
using SeamFinder.Core;

namespace SeamFinder.UI;

public partial class MainWindow : Window
{
    string? _lastReportPath;
    string? _lastFixPluginPath;
    string? _lastOutputFolder;

    // Path AppendLog also mirrors every line to, alongside the LogBox -
    // set fresh at the start of each run so the log ends up sitting right
    // next to that run's own esp/csv instead of only living in the UI
    // (which resets on every relaunch, unlike the output folder's files).
    string? _currentLogFilePath;

    // Whether OutputFolderBox's current text was set by RefreshOutputFolderDefault
    // rather than typed by the user - stays true (keep auto-updating the default)
    // until the user actually edits the field themselves.
    bool _outputFolderAutoSet = true;
    bool _suppressOutputTextChanged;

    public MainWindow()
    {
        InitializeComponent();
        LoadPersistedSettings();
        RefreshOutputFolderDefault();
    }

    // --- Settings persistence ---
    //
    // Remembers everything typed into the form (paths, trust checkboxes,
    // the custom trusted-plugins box) across app launches, so a real load
    // order's worth of settings only needs entering once. Kept in a stable
    // per-user location rather than next to the exe - this app gets
    // rebuilt/republished in place during development, which would
    // otherwise silently wipe saved settings on every update.
    static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SeamFinder", "settings.json");

    record PersistedSettings(
        bool IsMo2Mode, bool IsVortexMode,
        string Mo2InstancePath, string Mo2GameDataPath, string Mo2PluginsTxt, string Mo2LoadOrderTxt, string Mo2ModlistTxt,
        string VortexGameDataPath, string DirectGameDataPath, string OutputFolder,
        bool TrustNorthernRoads, bool TrustCsWaterMod, bool TrustWaterForEnb, bool TrustRealisticWaterTwo,
        string CustomTrustedPluginsText, string? PriorityOverNorthernRoadsPluginsText = null);

    void LoadPersistedSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var s = System.Text.Json.JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(SettingsFilePath));
            if (s is null) return;

            (ModeMo2.IsChecked, ModeVortex.IsChecked, ModeDirect.IsChecked) = s switch
            {
                { IsMo2Mode: true } => (true, false, false),
                { IsVortexMode: true } => (false, true, false),
                _ => (false, false, true),
            };
            Mo2InstancePathBox.Text = s.Mo2InstancePath;
            Mo2GameDataPathBox.Text = s.Mo2GameDataPath;
            Mo2PluginsTxtBox.Text = s.Mo2PluginsTxt;
            Mo2LoadOrderTxtBox.Text = s.Mo2LoadOrderTxt;
            Mo2ModlistTxtBox.Text = s.Mo2ModlistTxt;
            VortexGameDataPathBox.Text = s.VortexGameDataPath;
            DirectGameDataPathBox.Text = s.DirectGameDataPath;
            TrustNorthernRoadsCheck.IsChecked = s.TrustNorthernRoads;
            TrustCsWaterModCheck.IsChecked = s.TrustCsWaterMod;
            TrustWaterForEnbCheck.IsChecked = s.TrustWaterForEnb;
            TrustRealisticWaterTwoCheck.IsChecked = s.TrustRealisticWaterTwo;
            CustomTrustedPluginsBox.Text = s.CustomTrustedPluginsText;
            PriorityOverNorthernRoadsPluginsBox.Text = s.PriorityOverNorthernRoadsPluginsText ?? ""; // older settings.json files predate this box
            if (!string.IsNullOrEmpty(s.OutputFolder)) OutputFolderBox.Text = s.OutputFolder; // marks _outputFolderAutoSet false via its own TextChanged handler
        }
        catch
        {
            // Corrupt or unreadable settings file - start fresh rather than
            // block the app from opening at all.
        }
    }

    void SavePersistedSettings(RunSettings s)
    {
        try
        {
            var persisted = new PersistedSettings(
                s.IsMo2Mode, s.IsVortexMode,
                s.Mo2InstancePath, s.Mo2GameDataPath, s.Mo2PluginsTxt, s.Mo2LoadOrderTxt, s.Mo2ModlistTxt,
                s.VortexGameDataPath, s.DirectGameDataPath, OutputFolderBox.Text.Trim(),
                s.TrustNorthernRoads, s.TrustCsWaterMod, s.TrustWaterForEnb, s.TrustRealisticWaterTwo,
                CustomTrustedPluginsBox.Text, PriorityOverNorthernRoadsPluginsBox.Text);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, System.Text.Json.JsonSerializer.Serialize(persisted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - a locked/inaccessible AppData shouldn't stop the run itself.
        }
    }

    // Writes the exact settings a run used into that run's own output
    // folder too (alongside log.txt/esp/csv), separate from the
    // always-on-launch copy above - a record of what config produced this
    // particular output, portable with it if the folder is shared/moved.
    void SaveSettingsSnapshotToOutputFolder(RunSettings s, string outputFolder)
    {
        try
        {
            Directory.CreateDirectory(outputFolder);
            var snapshot = new
            {
                OutputFolder = outputFolder,
                s.IsMo2Mode, s.IsVortexMode, s.Mo2InstancePath, s.Mo2GameDataPath,
                s.VortexGameDataPath, s.DirectGameDataPath, s.TrustNorthernRoads,
                s.TrustCsWaterMod, s.TrustWaterForEnb, s.TrustRealisticWaterTwo,
                s.CustomTrustedPlugins, s.PriorityOverNorthernRoadsPlugins,
            };
            File.WriteAllText(Path.Combine(outputFolder, "settings-used.json"),
                System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort, same reasoning as SavePersistedSettings.
        }
    }

    // --- Mode switching ---

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        // Fields are wired in the constructor's InitializeComponent() call,
        // so guard against this firing during window construction before
        // the panels exist yet.
        if (Mo2Panel is null) return;

        Mo2Panel.Visibility = ModeMo2.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        VortexPanel.Visibility = ModeVortex.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DirectPanel.Visibility = ModeDirect.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshOutputFolderDefault();
    }

    // --- Output folder: smart per-mode default, stays editable ---

    void ModeDataPathBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshOutputFolderDefault();

    void OutputFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOutputTextChanged) return;
        _outputFolderAutoSet = false; // user typed something themselves - stop auto-updating
    }

    void RefreshOutputFolderDefault()
    {
        if (OutputFolderBox is null) return; // not constructed yet

        string? defaultPath = null;
        string hint = "";

        if (ModeMo2?.IsChecked == true)
        {
            var instancePath = Mo2InstancePathBox?.Text.Trim();
            if (!string.IsNullOrEmpty(instancePath))
            {
                defaultPath = Path.Combine(instancePath, "mods", "Landscape Seam Report");
                hint = "Writes into your MO2 instance's mods folder, so it shows up as an installable mod (a meta.ini is added automatically).";
            }
        }
        else if (ModeVortex?.IsChecked == true)
        {
            defaultPath = VortexGameDataPathBox?.Text.Trim();
            hint = "Writes directly into your game's Data folder, matching where Vortex deploys mods by default.";
        }
        else if (ModeDirect?.IsChecked == true)
        {
            defaultPath = DirectGameDataPathBox?.Text.Trim();
            hint = "Writes directly into your game's Data folder.";
        }

        OutputHintText.Text = hint + " You can change this to any folder you like.";

        if (_outputFolderAutoSet && !string.IsNullOrEmpty(defaultPath))
        {
            _suppressOutputTextChanged = true;
            OutputFolderBox.Text = defaultPath;
            _suppressOutputTextChanged = false;
        }
    }

    // --- MO2 panel ---

    void Mo2BrowseInstance_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select your MO2 instance folder (where ModOrganizer.exe lives)" };
        if (dlg.ShowDialog() == true)
            Mo2InstancePathBox.Text = dlg.FolderName;
    }

    void Mo2BrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            Mo2GameDataPathBox.Text = dlg.FolderName;
    }

    void Mo2BrowsePluginsTxt_Click(object sender, RoutedEventArgs e) => BrowseForFile(Mo2PluginsTxtBox, "plugins.txt");
    void Mo2BrowseLoadOrderTxt_Click(object sender, RoutedEventArgs e) => BrowseForFile(Mo2LoadOrderTxtBox, "loadorder.txt");
    void Mo2BrowseModlistTxt_Click(object sender, RoutedEventArgs e) => BrowseForFile(Mo2ModlistTxtBox, "modlist.txt");

    static void BrowseForFile(TextBox target, string suggestedName)
    {
        var dlg = new OpenFileDialog { FileName = suggestedName, Filter = "Text files|*.txt|All files|*.*" };
        if (dlg.ShowDialog() == true)
            target.Text = dlg.FileName;
    }

    void Mo2InstancePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshMo2Instance();
        RefreshOutputFolderDefault();
    }

    void RefreshMo2Instance()
    {
        var instancePath = Mo2InstancePathBox.Text.Trim();
        if (string.IsNullOrEmpty(instancePath) || !Directory.Exists(instancePath)) return;

        var profilesDir = Path.Combine(instancePath, "profiles");
        Mo2ProfileCombo.Items.Clear();
        if (Directory.Exists(profilesDir))
        {
            foreach (var dir in Directory.GetDirectories(profilesDir))
                Mo2ProfileCombo.Items.Add(Path.GetFileName(dir));
        }

        var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
        if (File.Exists(iniPath))
        {
            var gamePath = ReadIniGamePath(iniPath);
            if (gamePath != null)
                Mo2GameDataPathBox.Text = Path.Combine(gamePath, "Data");

            var selectedProfile = ReadIniSelectedProfile(iniPath);
            if (selectedProfile != null && Mo2ProfileCombo.Items.Contains(selectedProfile))
                Mo2ProfileCombo.SelectedItem = selectedProfile;
        }

        if (Mo2ProfileCombo.SelectedItem is null && Mo2ProfileCombo.Items.Count > 0)
            Mo2ProfileCombo.SelectedIndex = 0;

        RefreshMo2ProfileFiles();
    }

    void RefreshMo2ProfileFiles()
    {
        var instancePath = Mo2InstancePathBox.Text.Trim();
        var profile = Mo2ProfileCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(instancePath) || string.IsNullOrEmpty(profile)) return;

        var profileDir = Path.Combine(instancePath, "profiles", profile);
        Mo2PluginsTxtBox.Text = Path.Combine(profileDir, "plugins.txt");
        Mo2LoadOrderTxtBox.Text = Path.Combine(profileDir, "loadorder.txt");
        Mo2ModlistTxtBox.Text = Path.Combine(profileDir, "modlist.txt");
    }

    static string? ReadIniGamePath(string iniPath)
    {
        foreach (var line in File.ReadAllLines(iniPath))
        {
            if (!line.StartsWith("gamePath=")) continue;
            var value = line["gamePath=".Length..].Trim();
            var start = value.IndexOf('(');
            var end = value.LastIndexOf(')');
            if (start >= 0 && end > start) value = value[(start + 1)..end];
            return value.Replace("\\\\", "\\");
        }
        return null;
    }

    static string? ReadIniSelectedProfile(string iniPath)
    {
        foreach (var line in File.ReadAllLines(iniPath))
        {
            if (!line.StartsWith("selected_profile=")) continue;
            var value = line["selected_profile=".Length..].Trim();
            var start = value.IndexOf('(');
            var end = value.LastIndexOf(')');
            if (start >= 0 && end > start) value = value[(start + 1)..end];
            return value;
        }
        return null;
    }

    // --- Vortex panel ---

    void VortexBrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            VortexGameDataPathBox.Text = dlg.FolderName;
    }

    void VortexAutoDetect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var env = GameEnvironment.Typical.Construct<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE);
            VortexGameDataPathBox.Text = env.DataFolderPath.Path;
            AppendLog($"Auto-detected game Data folder: {env.DataFolderPath.Path}");
            AppendLog("(This finds your Skyrim SE install via Steam/GOG/registry - correct as long as");
            AppendLog(" Vortex is using its default deployment method, which links mods directly into it.)");
        }
        catch (Exception ex)
        {
            AppendLog("Auto-detect failed: " + ex.Message);
            MessageBox.Show(this, "Could not auto-detect your game install. Please browse to your Data folder manually.",
                "Auto-detect failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Direct panel ---

    void DirectBrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            DirectGameDataPathBox.Text = dlg.FolderName;
    }

    // --- Output ---

    void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select an output folder" };
        if (dlg.ShowDialog() == true)
            OutputFolderBox.Text = dlg.FolderName;
    }

    // --- Run ---

    // Plain data snapshot of every UI value the background thread needs, taken
    // on the UI thread before Task.Run starts. WPF controls can only be read
    // from the thread that owns them (InvalidOperationException otherwise) -
    // snapshotting everything up front means the background method never
    // touches a UI element directly, so there's nothing to get wrong later.
    record RunSettings(
        bool IsMo2Mode, bool IsVortexMode,
        string Mo2InstancePath, string Mo2GameDataPath, string Mo2PluginsTxt, string Mo2LoadOrderTxt, string Mo2ModlistTxt,
        string VortexGameDataPath, string DirectGameDataPath, bool TrustNorthernRoads,
        bool TrustCsWaterMod, bool TrustWaterForEnb, bool TrustRealisticWaterTwo,
        IReadOnlyList<string> CustomTrustedPlugins, IReadOnlyList<string> PriorityOverNorthernRoadsPlugins)
    {
        public WaterTrustOptions WaterTrust => new(TrustCsWaterMod, TrustWaterForEnb, TrustRealisticWaterTwo);
    }

    void SetBusy(bool busy)
    {
        RunButton.IsEnabled = !busy;
        FixButton.IsEnabled = !busy;
        RunProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // One pattern per line - blank lines ignored, no other syntax (a
    // trailing "*" for prefix matching is interpreted downstream in
    // SeamFixer, not here).
    static IReadOnlyList<string> ParseCustomTrustedPlugins(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    RunSettings SnapshotSettings() => new(
        ModeMo2.IsChecked == true, ModeVortex.IsChecked == true,
        Mo2InstancePathBox.Text.Trim(), Mo2GameDataPathBox.Text.Trim(), Mo2PluginsTxtBox.Text.Trim(),
        Mo2LoadOrderTxtBox.Text.Trim(), Mo2ModlistTxtBox.Text.Trim(),
        VortexGameDataPathBox.Text.Trim(), DirectGameDataPathBox.Text.Trim(),
        TrustNorthernRoadsCheck.IsChecked == true,
        TrustCsWaterModCheck.IsChecked == true, TrustWaterForEnbCheck.IsChecked == true, TrustRealisticWaterTwoCheck.IsChecked == true,
        ParseCustomTrustedPlugins(CustomTrustedPluginsBox.Text), ParseCustomTrustedPlugins(PriorityOverNorthernRoadsPluginsBox.Text));

    async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        ResultText.Text = "";
        OpenReportButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        SetBusy(true);
        StartLogFile(OutputFolderBox.Text.Trim());

        var settings = SnapshotSettings();
        SavePersistedSettings(settings);
        SaveSettingsSnapshotToOutputFolder(settings, OutputFolderBox.Text.Trim());

        try
        {
            var result = await Task.Run(() => RunDetectionForSelectedMode(settings));
            if (result is null) return; // validation error already shown

            var (report, seamCount, pluginCount) = result.Value;
            var outputFolder = OutputFolderBox.Text.Trim();
            Directory.CreateDirectory(outputFolder);
            EnsureMo2MetaIni(outputFolder);

            var outPath = Path.Combine(outputFolder, "LandscapeSeamReport.csv");
            File.WriteAllLines(outPath, report);
            AppendLog($"Report written to: {outPath}");

            _lastReportPath = outPath;
            _lastOutputFolder = outputFolder;
            ResultText.Text = $"Found {seamCount} seam edges across {pluginCount} plugins.";
            OpenReportButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppendLog("");
            AppendLog("ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void FixButton_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        ResultText.Text = "";
        OpenFixPluginButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        SetBusy(true);

        var settings = SnapshotSettings();
        var outputFolder = OutputFolderBox.Text.Trim();
        StartLogFile(outputFolder);
        SavePersistedSettings(settings);
        SaveSettingsSnapshotToOutputFolder(settings, outputFolder);

        try
        {
            if (string.IsNullOrEmpty(outputFolder))
            {
                ShowValidation("Please choose an output folder.");
                return;
            }

            var result = await Task.Run(() => GenerateFixForSelectedMode(settings, outputFolder));
            if (result is null) return; // validation error already shown

            Dispatcher.Invoke(() => EnsureMo2MetaIni(outputFolder));

            _lastFixPluginPath = result.OutputPath;
            _lastOutputFolder = outputFolder;
            var verificationSummary = result.VerificationWarnings.Count > 0
                ? $"{result.VerificationWarnings.Count} cell(s) skipped for a reference conflict (see log)."
                : "";
            ResultText.Text = $"Restored {result.CellsPatched} cell(s) to their trusted plugin's terrain. " +
                $"{verificationSummary} Test in-game before relying on this.";
            OpenFixPluginButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppendLog("");
            AppendLog("ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    SeamFixResult? GenerateFixForSelectedMode(RunSettings s, string outputFolder)
    {
        void Log(string line) => Dispatcher.Invoke(() => AppendLog(line));
        const string pluginName = "LandscapeSeamFixes.esp";

        if (s.IsMo2Mode)
        {
            if (string.IsNullOrEmpty(s.Mo2InstancePath) || string.IsNullOrEmpty(s.Mo2GameDataPath))
            {
                ShowValidation("Please fill in the MO2 instance folder and game Data folder.");
                return null;
            }

            Log($"MO2 instance: {s.Mo2InstancePath}");
            Log($"Game Data path: {s.Mo2GameDataPath}");
            Log("");

            var resolved = Mo2Resolver.ResolveFromExplicitPaths(
                s.Mo2PluginsTxt, s.Mo2LoadOrderTxt, s.Mo2ModlistTxt, s.Mo2InstancePath, s.Mo2GameDataPath);
            Log($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
            if (resolved.MissingPlugins.Count > 0)
            {
                Log($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
                foreach (var m in resolved.MissingPlugins) Log("  " + m);
            }

            return SeamFixer.GenerateFixPluginForResolvedPlugins(resolved.LoadOrder, pluginName, outputFolder, Log, s.TrustNorthernRoads, s.WaterTrust, s.CustomTrustedPlugins, s.PriorityOverNorthernRoadsPlugins);
        }
        else
        {
            var dataFolder = s.IsVortexMode ? s.VortexGameDataPath : s.DirectGameDataPath;

            if (string.IsNullOrEmpty(dataFolder))
            {
                ShowValidation("Please fill in the game Data folder.");
                return null;
            }

            Log($"Game Data path: {dataFolder}");
            Log("");

            return SeamFixer.GenerateFixPluginForDirectDataFolder(dataFolder, pluginName, outputFolder, Log, s.TrustNorthernRoads, s.WaterTrust, s.CustomTrustedPlugins, s.PriorityOverNorthernRoadsPlugins);
        }
    }

    (List<string> Report, int SeamCount, int PluginCount)? RunDetectionForSelectedMode(RunSettings s)
    {
        void Log(string line) => Dispatcher.Invoke(() => AppendLog(line));

        if (s.IsMo2Mode)
        {
            if (string.IsNullOrEmpty(s.Mo2InstancePath) || string.IsNullOrEmpty(s.Mo2GameDataPath))
            {
                ShowValidation("Please fill in the MO2 instance folder and game Data folder.");
                return null;
            }

            Log($"MO2 instance: {s.Mo2InstancePath}");
            Log($"plugins.txt: {s.Mo2PluginsTxt}");
            Log($"loadorder.txt: {s.Mo2LoadOrderTxt}");
            Log($"modlist.txt: {s.Mo2ModlistTxt}");
            Log($"Game Data path: {s.Mo2GameDataPath}");
            Log("");

            var resolved = Mo2Resolver.ResolveFromExplicitPaths(
                s.Mo2PluginsTxt, s.Mo2LoadOrderTxt, s.Mo2ModlistTxt, s.Mo2InstancePath, s.Mo2GameDataPath);
            Log($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
            if (resolved.MissingPlugins.Count > 0)
            {
                Log($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found in any enabled mod folder or the game Data folder:");
                foreach (var m in resolved.MissingPlugins) Log("  " + m);
            }

            var detection = SeamDetector.RunForResolvedPlugins(resolved.LoadOrder, Log, s.TrustNorthernRoads);
            return (detection.ReportCsvLines, detection.SeamCount, detection.PluginCount);
        }
        else
        {
            var dataFolder = s.IsVortexMode ? s.VortexGameDataPath : s.DirectGameDataPath;

            if (string.IsNullOrEmpty(dataFolder))
            {
                ShowValidation("Please fill in the game Data folder.");
                return null;
            }

            Log($"Game Data path: {dataFolder}");
            Log("");

            var detection = SeamDetector.RunForDirectDataFolder(dataFolder, Log, s.TrustNorthernRoads);
            return (detection.ReportCsvLines, detection.SeamCount, detection.PluginCount);
        }
    }

    void ShowValidation(string message)
    {
        Dispatcher.Invoke(() => MessageBox.Show(this, message, "Missing information", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    // If we're in MO2 mode and the chosen output folder is actually inside
    // this instance's mods folder (the default, but the user may have
    // redirected elsewhere), add a meta.ini so MO2 recognizes it as an
    // installable mod. Skipped entirely if they pointed output somewhere
    // else - no meta.ini dropped into an unrelated folder. Shared by both
    // the detection report and the fix plugin, since either can land there.
    void EnsureMo2MetaIni(string outputFolder)
    {
        if (ModeMo2.IsChecked != true) return;

        var instancePath = Mo2InstancePathBox.Text.Trim();
        if (string.IsNullOrEmpty(instancePath)) return;

        var modsDir = Path.Combine(instancePath, "mods") + Path.DirectorySeparatorChar;
        var fullOutput = Path.GetFullPath(outputFolder) + Path.DirectorySeparatorChar;
        if (!fullOutput.StartsWith(Path.GetFullPath(modsDir), StringComparison.OrdinalIgnoreCase)) return;

        var metaPath = Path.Combine(outputFolder, "meta.ini");
        if (!File.Exists(metaPath))
        {
            File.WriteAllText(metaPath, "[General]\r\ngameName=SkyrimSE\r\nmodid=0\r\nversion=1.0.0\r\ninstalled=true\r\n");
            AppendLog($"Wrote meta.ini so this shows up as an MO2 mod: {metaPath}");
        }
    }

    // Points AppendLog's file mirror at <outputFolder>/log.txt and starts it
    // fresh (matching LogBox.Clear() for the UI copy) - same folder the
    // esp/csv for this run land in, so all three show up together instead
    // of the log only ever existing transiently in the UI.
    void StartLogFile(string outputFolder)
    {
        if (string.IsNullOrEmpty(outputFolder)) { _currentLogFilePath = null; return; }
        try
        {
            Directory.CreateDirectory(outputFolder);
            _currentLogFilePath = Path.Combine(outputFolder, "log.txt");
            File.WriteAllText(_currentLogFilePath, "");
        }
        catch
        {
            // Best-effort - a locked/inaccessible output folder shouldn't
            // stop the run itself, just the file mirror of its log.
            _currentLogFilePath = null;
        }
    }

    void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        if (_currentLogFilePath is not null)
        {
            try { File.AppendAllText(_currentLogFilePath, line + Environment.NewLine); }
            catch { /* best-effort, see StartLogFile */ }
        }
    }

    // --- Result bar ---

    void OpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath is null) return;
        Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true });
    }

    void OpenFixPluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFixPluginPath is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFixPluginPath}\"") { UseShellExecute = true });
    }

    void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputFolder is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutputFolder}\"") { UseShellExecute = true });
    }
}

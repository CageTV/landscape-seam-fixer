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

    public MainWindow()
    {
        InitializeComponent();
        OutputFolderBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
        CreateMo2ModFolderCheck.IsEnabled = ModeMo2.IsChecked == true;
        if (ModeMo2.IsChecked != true) CreateMo2ModFolderCheck.IsChecked = false;
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

    void Mo2InstancePathBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshMo2Instance();

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

    void CreateMo2ModFolderCheck_Changed(object sender, RoutedEventArgs e)
    {
        Mo2ModFolderRow.Visibility = CreateMo2ModFolderCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- Run ---

    async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        ResultText.Text = "";
        OpenReportButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        RunButton.IsEnabled = false;
        RunProgress.Visibility = Visibility.Visible;

        try
        {
            var result = await Task.Run(RunDetectionForSelectedMode);
            if (result is null) return; // validation error already shown

            var (report, seamCount, pluginCount) = result.Value;
            var outPath = WriteOutput(report);

            _lastReportPath = outPath;
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
            RunButton.IsEnabled = true;
            RunProgress.Visibility = Visibility.Collapsed;
        }
    }

    (List<string> Report, int SeamCount, int PluginCount)? RunDetectionForSelectedMode()
    {
        void Log(string line) => Dispatcher.Invoke(() => AppendLog(line));

        if (ModeMo2.IsChecked == true)
        {
            var instancePath = Dispatcher.Invoke(() => Mo2InstancePathBox.Text.Trim());
            var gameDataPath = Dispatcher.Invoke(() => Mo2GameDataPathBox.Text.Trim());
            var pluginsTxt = Dispatcher.Invoke(() => Mo2PluginsTxtBox.Text.Trim());
            var loadOrderTxt = Dispatcher.Invoke(() => Mo2LoadOrderTxtBox.Text.Trim());
            var modlistTxt = Dispatcher.Invoke(() => Mo2ModlistTxtBox.Text.Trim());

            if (string.IsNullOrEmpty(instancePath) || string.IsNullOrEmpty(gameDataPath))
            {
                ShowValidation("Please fill in the MO2 instance folder and game Data folder.");
                return null;
            }

            Log($"MO2 instance: {instancePath}");
            Log($"plugins.txt: {pluginsTxt}");
            Log($"loadorder.txt: {loadOrderTxt}");
            Log($"modlist.txt: {modlistTxt}");
            Log($"Game Data path: {gameDataPath}");
            Log("");

            var resolved = Mo2Resolver.ResolveFromExplicitPaths(pluginsTxt, loadOrderTxt, modlistTxt, instancePath, gameDataPath);
            Log($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
            if (resolved.MissingPlugins.Count > 0)
            {
                Log($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found in any enabled mod folder or the game Data folder:");
                foreach (var m in resolved.MissingPlugins) Log("  " + m);
            }

            var detection = SeamDetector.RunForResolvedPlugins(resolved.LoadOrder, Log);
            return (detection.ReportCsvLines, detection.SeamCount, detection.PluginCount);
        }
        else
        {
            var dataFolder = Dispatcher.Invoke(() =>
                ModeVortex.IsChecked == true ? VortexGameDataPathBox.Text.Trim() : DirectGameDataPathBox.Text.Trim());

            if (string.IsNullOrEmpty(dataFolder))
            {
                ShowValidation("Please fill in the game Data folder.");
                return null;
            }

            Log($"Game Data path: {dataFolder}");
            Log("");

            var detection = SeamDetector.RunForDirectDataFolder(dataFolder, Log);
            return (detection.ReportCsvLines, detection.SeamCount, detection.PluginCount);
        }
    }

    void ShowValidation(string message)
    {
        Dispatcher.Invoke(() => MessageBox.Show(this, message, "Missing information", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    string WriteOutput(List<string> report)
    {
        var outputFolder = OutputFolderBox.Text.Trim();
        Directory.CreateDirectory(outputFolder);

        string targetFolder;
        if (CreateMo2ModFolderCheck.IsChecked == true)
        {
            var instancePath = Mo2InstancePathBox.Text.Trim();
            var modName = string.IsNullOrWhiteSpace(Mo2ModNameBox.Text) ? "Landscape Seam Report" : Mo2ModNameBox.Text.Trim();
            var modsDir = Path.Combine(instancePath, "mods", modName);
            Directory.CreateDirectory(modsDir);
            File.WriteAllText(Path.Combine(modsDir, "meta.ini"),
                "[General]\r\ngameName=SkyrimSE\r\nmodid=0\r\nversion=1.0.0\r\ninstalled=true\r\n");
            targetFolder = modsDir;
            AppendLog($"Created MO2 mod folder: {modsDir}");
        }
        else
        {
            targetFolder = outputFolder;
        }

        var outPath = Path.Combine(targetFolder, "LandscapeSeamReport.csv");
        File.WriteAllLines(outPath, report);
        AppendLog($"Report written to: {outPath}");
        return outPath;
    }

    void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    // --- Result bar ---

    void OpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath is null) return;
        Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true });
    }

    void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastReportPath}\"") { UseShellExecute = true });
    }
}

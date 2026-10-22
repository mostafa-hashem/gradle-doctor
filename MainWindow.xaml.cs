using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GradleDoctor;

public partial class MainWindow : Window
{
    List<ProjectInfo> _projects = new();
    bool _scanned;
    readonly AppSettings _s = AppSettings.Load();

    static readonly Regex VersionFormat = new(@"^\d+(\.\d+)+$", RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        if (Directory.Exists(_s.LastFolder)) PathBox.Text = _s.LastFolder;
        GradleHomeBox.Text = _s.GradleHome;
        SkipBox.Text = _s.SkipFolders;
        KeepBox.Text = _s.KeepVersions;
        VerifyCheck.IsChecked = _s.VerifyBuild;
        TaskBox.Text = _s.VerifyTask;
        TimeoutBox.Text = _s.TimeoutMinutes.ToString();
        Closing += (_, _) => SaveSettings();
        UpdateHomeLabels();
        SetButtons(true);
        Log($"Gradle home in use: {Home}");
    }

    // ---------- settings helpers ----------

    string Home => string.IsNullOrWhiteSpace(GradleHomeBox.Text) ? GradleCleaner.DefaultHome : GradleHomeBox.Text.Trim();
    string? CustomHome => string.IsNullOrWhiteSpace(GradleHomeBox.Text) ? null : GradleHomeBox.Text.Trim();
    int Timeout => int.TryParse(TimeoutBox.Text, out var t) && t > 0 ? t : 15;
    string VerifyTask => string.IsNullOrWhiteSpace(TaskBox.Text) ? "assembleDebug" : TaskBox.Text.Trim();

    void UpdateHomeLabels()
    {
        if (HomeLabel != null) HomeLabel.Text = $"Home: {Home}";
        if (HomeBadgeText != null)
        {
            var folderName = System.IO.Path.GetFileName(Home.TrimEnd('\\', '/'));
            HomeBadgeText.Text = string.IsNullOrEmpty(folderName) ? "Gradle Home" : folderName;
        }
    }

    HashSet<string> SkipSet() =>
        SkipBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    HashSet<string> KeepSet() =>
        KeepBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();

    void SaveSettings()
    {
        _s.LastFolder = PathBox.Text;
        _s.GradleHome = GradleHomeBox.Text.Trim();
        _s.SkipFolders = SkipBox.Text.Trim();
        _s.KeepVersions = KeepBox.Text.Trim();
        _s.VerifyBuild = VerifyCheck.IsChecked == true;
        _s.VerifyTask = VerifyTask;
        _s.TimeoutMinutes = Timeout;
        _s.Save();
        UpdateHomeLabels();
    }

    // ---------- UI helpers ----------

    void Log(string msg)
    {
        LogBox.AppendText(msg + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    void LogUi(string msg) => Dispatcher.Invoke(() => Log(msg));

    void SetButtons(bool enabled)
    {
        ScanBtn.IsEnabled = enabled;
        foreach (var b in new Button[] { SelAllBtn, SelNoneBtn, SetTargetBtn, DryBtn, ApplyBtn, CleanBtn, StopBtn })
            b.IsEnabled = enabled && _scanned;

        if (WorkProgress != null)
            WorkProgress.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        if (StatusDot != null)
        {
            StatusDot.Fill = enabled
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
        }
    }

    void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
    }

    void Commit() => ProjectsGrid.CommitEdit(DataGridEditingUnit.Row, true);

    void RefreshGrid()
    {
        ProjectsGrid.ItemsSource = null;
        ProjectsGrid.ItemsSource = _projects;
        if (ProjectCountBadge != null)
            ProjectCountBadge.Text = $"{_projects.Count} project{(_projects.Count == 1 ? "" : "s")}";
    }

    string? ReadGlobalTarget(bool showError = true)
    {
        var t = (TargetBox.Text ?? "").Trim();
        if (!VersionFormat.IsMatch(t))
        {
            if (showError) MessageBox.Show("Enter a valid Gradle version in 'Target Gradle', e.g. 9.1.0", "Gradle Doctor");
            return null;
        }
        return t;
    }

    // ---------- folder pickers ----------

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select your projects folder" };
        if (Directory.Exists(PathBox.Text)) dlg.InitialDirectory = PathBox.Text;

        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName;
            _scanned = false;
            _projects.Clear();
            RefreshGrid();
            SetButtons(true);
            SaveSettings();
            StatusText.Text = "Folder changed. Click Scan.";
        }
    }

    void BrowseHome_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the Gradle user home (.gradle) folder" };
        if (Directory.Exists(Home)) dlg.InitialDirectory = Home;
        if (dlg.ShowDialog(this) == true) { GradleHomeBox.Text = dlg.FolderName; SaveSettings(); }
    }

    void ResetHome_Click(object sender, RoutedEventArgs e)
    {
        GradleHomeBox.Text = "";
        SaveSettings();
        Log($"Gradle home in use: {Home}");
    }

    void EditCompat_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Compat.EnsureUserCopy();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Compat.UserFile}\"") { UseShellExecute = true });
            Log($"Editing rules: {Compat.UserFile} (saved changes apply on the next Dry Run / Apply)");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Gradle Doctor"); }
    }

    // ---------- scan ----------

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var root = PathBox.Text;
        if (!Directory.Exists(root))
        {
            MessageBox.Show("Choose a valid projects folder first (Browse...).", "Gradle Doctor");
            return;
        }

        SaveSettings();
        SetButtons(false);
        StatusText.Text = "Scanning...";
        Log($"Scanning {root} ...");

        var skip = SkipSet();
        _projects = await Task.Run(() => ProjectScanner.Scan(root, skip));
        _scanned = true;

        var compat = Compat.Load();
        foreach (var p in _projects) p.Warning = compat.Check(p.Agp, p.CurrentGradle);
        RefreshGrid();

        var home = Home;
        var versions = _projects.Select(p => p.CurrentGradle)
            .Where(v => v != "?")
            .Concat(GradleCleaner.InstalledVersions(home))
            .Distinct()
            .OrderByDescending(v => v, Comparer<string>.Create(ProjectScanner.CompareVersions))
            .ToList();
        TargetBox.ItemsSource = versions;
        if (versions.Count > 0 && string.IsNullOrWhiteSpace(TargetBox.Text))
            TargetBox.Text = versions[0];

        Log($"Found {_projects.Count} Gradle project(s).");
        StatusText.Text = $"{_projects.Count} project(s) found. Tick the ones you want, set targets, then Dry Run.";
        SetButtons(true);
    }

    // ---------- selection ----------

    void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var p in _projects) p.Selected = true; }
    void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var p in _projects) p.Selected = false; }

    void SetTarget_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var t = ReadGlobalTarget();
        if (t == null) return;
        var compat = Compat.Load();
        foreach (var p in _projects.Where(p => p.Selected))
        {
            p.Target = t;
            p.Warning = compat.Check(p.Agp, t);
        }
    }

    // ---------- dry run ----------

    async void DryRun_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var rows = _projects.Where(p => p.Selected).ToList();
        if (rows.Count == 0) { MessageBox.Show("Tick at least one project (Use column).", "Gradle Doctor"); return; }

        if (rows.Any(p => string.IsNullOrWhiteSpace(p.Target)))
        {
            var g = ReadGlobalTarget();
            if (g == null) return;
            foreach (var p in rows.Where(p => string.IsNullOrWhiteSpace(p.Target))) p.Target = g;
        }

        SetButtons(false);
        var compat = Compat.Load();
        int changes = 0;
        foreach (var p in rows)
        {
            if (!VersionFormat.IsMatch(p.Target)) { p.Status = "Bad target"; continue; }
            p.Warning = compat.Check(p.Agp, p.Target);
            p.Status = p.CurrentGradle == p.Target ? "Up to date" : "Will change";
            if (p.Status == "Will change") changes++;
        }

        Log($"[Dry run] {changes} of {rows.Count} selected project(s) would change. Nothing was modified.");
        foreach (var p in rows.Where(p => p.Warning.Length > 0))
            Log($"   WARNING {p.Name}: {p.Warning}");

        // What would be kept/deleted after the plan.
        var keep = KeepSet();
        foreach (var p in _projects)
            keep.Add(p.Selected && VersionFormat.IsMatch(p.Target) ? p.Target : p.CurrentGradle);
        keep.Remove("?");

        if (keep.Count > 0)
        {
            var home = Home;
            var unused = await Task.Run(() => GradleCleaner.FindUnused(home, keep));
            Log($"[Dry run] Afterwards {unused.Count} item(s) in {home} become unused ({Format.Size(unused.Sum(i => i.Size))}).");
        }

        StatusText.Text = $"Dry run: {changes} change(s).";
        SetButtons(true);
    }

    // ---------- apply ----------

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var rows = _projects.Where(p => p.Selected).ToList();
        if (rows.Count == 0) { MessageBox.Show("Tick at least one project (Use column).", "Gradle Doctor"); return; }

        if (rows.Any(p => string.IsNullOrWhiteSpace(p.Target)))
        {
            var g = ReadGlobalTarget();
            if (g == null) return;
            foreach (var p in rows.Where(p => string.IsNullOrWhiteSpace(p.Target))) p.Target = g;
        }

        var compat = Compat.Load();
        foreach (var p in rows)
            if (VersionFormat.IsMatch(p.Target)) p.Warning = compat.Check(p.Agp, p.Target);

        var todo = rows.Where(p => VersionFormat.IsMatch(p.Target) && p.CurrentGradle != p.Target).ToList();
        foreach (var p in rows.Where(p => !VersionFormat.IsMatch(p.Target))) p.Status = "Bad target";

        if (todo.Count == 0)
        {
            MessageBox.Show("Nothing to change: selected projects already match their target.", "Gradle Doctor");
            return;
        }

        bool verify = VerifyCheck.IsChecked == true;
        int warns = todo.Count(p => p.Warning.Length > 0);
        var ok = MessageBox.Show(
            $"Update {todo.Count} project(s)?\n\n" +
            (warns > 0 ? $"{warns} project(s) have compatibility warnings (see the Warning column).\n\n" : "") +
            "A backup (gradle-wrapper.properties.gradledoctor.bak) is created next to each file.\n" +
            (verify
                ? $"After each update 'gradlew {VerifyTask}' runs. If it fails, the project is rolled back.\n(The new Gradle version will be downloaded, so you need internet.)"
                : "Builds will NOT be run. Tick the verify option in Settings to build and roll back on failure."),
            "Gradle Doctor", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        SaveSettings();
        SetButtons(false);
        int success = 0, rolled = 0, failed = 0;

        foreach (var p in todo)
        {
            try
            {
                p.Status = "Updating...";
                if (!ProjectScanner.UpdateWrapper(p, p.Target)) { p.Status = "No change"; continue; }
                Log($"{p.Name}: Gradle -> {p.Target}");

                if (!verify) { p.Status = "Updated"; success++; continue; }

                p.Status = "Building...";
                StatusText.Text = $"Building {p.Name} ...";
                Log($"Building {p.Name} (gradlew {VerifyTask}) ...");
                var result = await Runner.Gradlew(p.Path, VerifyTask, CustomHome, Timeout, LogUi);

                if (result == false)
                {
                    ProjectScanner.Restore(p);
                    p.Status = "Rolled back";
                    Log($"{p.Name}: build FAILED, rolled back to Gradle {p.CurrentGradle}.");
                    rolled++;
                }
                else if (result == null) { p.Status = "Updated (not built)"; success++; }
                else { p.Status = "Updated, build OK"; success++; }
            }
            catch (Exception ex)
            {
                p.Status = "Failed";
                Log($"FAILED {p.Name}: {ex.Message}");
                failed++;
            }
        }

        Log($"Done: {success} updated, {rolled} rolled back, {failed} failed.");
        StatusText.Text = $"Done: {success} updated, {rolled} rolled back, {failed} failed.";
        SetButtons(true);
    }

    // ---------- clean ----------

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        // Safety: always keep every version still used by a scanned project, plus the user's own keep list.
        var keep = KeepSet();
        foreach (var p in _projects) keep.Add(p.CurrentGradle);
        keep.Remove("?");

        if (keep.Count == 0)
        {
            MessageBox.Show("No project with a known Gradle version was found, so nothing is safe to delete.", "Gradle Doctor");
            return;
        }

        SaveSettings();
        SetButtons(false);
        StatusText.Text = "Calculating sizes...";
        var home = Home;
        var unused = await Task.Run(() => GradleCleaner.FindUnused(home, keep));

        if (unused.Count == 0)
        {
            MessageBox.Show("Nothing to clean.", "Gradle Doctor");
            StatusText.Text = "Nothing to clean.";
            SetButtons(true);
            return;
        }

        var dlg = new CleanupDialog(unused) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Selected.Count > 0)
        {
            var chosen = dlg.Selected;
            StatusText.Text = "Deleting...";
            var (freed, errors) = await Task.Run(() => GradleCleaner.Delete(chosen));
            Log($"Freed {Format.Size(freed)} from {home}.");
            foreach (var er in errors) Log("ERROR " + er);
            StatusText.Text = $"Freed {Format.Size(freed)}" + (errors.Count > 0 ? $" ({errors.Count} error(s), see log)" : "");
        }
        else StatusText.Text = "Cancelled.";

        SetButtons(true);
    }

    // ---------- stop daemons ----------

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var p = _projects.FirstOrDefault();
        if (p == null) return;
        SetButtons(false);
        Log("Stopping Gradle daemons ...");
        var r = await Runner.Gradlew(p.Path, "--stop", CustomHome, 2, LogUi);
        Log(r == true ? "Daemons stopped." : "Could not stop daemons (see messages above).");
        SetButtons(true);
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace GradleDoctor;

public partial class MainWindow : Window
{
    static readonly Regex VersionFormat = new(@"^\d+(\.\d+){1,3}(-[A-Za-z0-9.-]+)?$", RegexOptions.Compiled);
    static readonly Regex AgpFormat = new(@"^\d+(\.\d+){1,3}(-[A-Za-z0-9.-]+)?$", RegexOptions.Compiled);
    const string LatestTestedGradle = "9.6.0";

    readonly AppSettings _s = AppSettings.Load();
    List<ProjectInfo> _projects = new();
    List<GradleRelease> _remoteVersions = new();
    ICollectionView? _view;
    Compat _compat = Compat.Load();
    CancellationTokenSource? _cts;
    bool _scanned;
    bool _busy;
    bool _loadingUi;
    bool _detailsVisible = true;
    bool _terminalCollapsed;
    double _terminalHeight = 170;
    const double TerminalCollapsedHeight = 42;

    public MainWindow()
    {
        InitializeComponent();

        _loadingUi = true;
        ThemeCombo.SelectedIndex = ThemeService.Current switch
        {
            ThemeMode.Dark => 1,
            ThemeMode.System => 2,
            _ => 0
        };
        _loadingUi = false;

        if (Directory.Exists(_s.LastFolder)) PathBox.Text = _s.LastFolder;
        GradleHomeBox.Text = _s.GradleHome;
        SkipBox.Text = string.IsNullOrWhiteSpace(_s.SkipFolders) || !_s.SkipFolders.Contains("ephemeral")
            ? AppSettings.DefaultSkipFolders
            : _s.SkipFolders;
        KeepBox.Text = _s.KeepVersions;
        VerifyCheck.IsChecked = _s.VerifyBuild;
        UpdateAgpCheck.IsChecked = _s.UpdateAgp;
        TaskBox.Text = _s.VerifyTask;
        TimeoutBox.Text = _s.TimeoutMinutes.ToString();
        TargetBox.Text = !string.IsNullOrWhiteSpace(_s.DefaultGradleVersion) ? _s.DefaultGradleVersion : "9.8.2";

        Closing += (_, _) =>
        {
            CancelCurrent();
            SaveSettings();
        };
        StateChanged += (_, _) => UpdateWindowState();
        SourceInitialized += (_, _) => EnableRoundedCorners();
        Loaded += async (_, _) =>
        {
            UpdateHomeLabels();
            UpdateButtonStates();
            UpdateEmptyState();
            Log($"Gradle home in use: {Home}");
            await LoadVersionsAsync(false, false);
            if (_projects.Count == 0) SetStatus("Choose your projects folder, then click Scan.", "TextMutedBrush");
        };

        UpdateHomeLabels();
        RefreshGrid();
        UpdateButtonStates();
        SetStatus("Choose your projects folder, then click Scan.", "TextMutedBrush");
    }

    // ---------- theme / window chrome ----------

    void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        var tag = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var mode = ThemeService.Parse(tag);
        ThemeService.Apply(mode);
        _s.ThemeMode = mode.ToString();
        _s.Save();
        ToastService.Show($"Theme changed to {ThemeService.Label(mode)}.", ToastKind.Info);
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        try { DragMove(); } catch { }
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void UpdateWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxBtn.Content = maximized ? "\uE923" : "\uE922";
        MaxBtn.ToolTip = maximized ? "Restore" : "Maximize";
        WindowRoot.Margin = maximized ? new Thickness(7) : new Thickness(0);
        WindowRoot.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
    }

    void EnableRoundedCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = 2;
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- settings helpers ----------

    string Home => string.IsNullOrWhiteSpace(GradleHomeBox.Text) ? GradleCleaner.DefaultHome : GradleHomeBox.Text.Trim();
    string? CustomHome => string.IsNullOrWhiteSpace(GradleHomeBox.Text) ? null : GradleHomeBox.Text.Trim();
    int Timeout => int.TryParse(TimeoutBox.Text, out var t) && t > 0 ? t : 15;
    string VerifyTask => string.IsNullOrWhiteSpace(TaskBox.Text) ? "assembleDebug" : TaskBox.Text.Trim();
    bool AutoAgp => UpdateAgpCheck.IsChecked == true;

    void UpdateHomeLabels()
    {
        HomeLabel.Text = $"Gradle home: {Home}";
        var folderName = System.IO.Path.GetFileName(Home.TrimEnd('\\', '/'));
        HomeBadgeText.Text = string.IsNullOrEmpty(folderName) ? "Gradle Home" : folderName;
    }

    HashSet<string> SkipSet()
    {
        var set = SkipBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in ProjectScanner.DefaultSkipSet) set.Add(s);
        return set;
    }

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
        _s.UpdateAgp = UpdateAgpCheck.IsChecked == true;
        _s.VerifyTask = VerifyTask;
        _s.TimeoutMinutes = Timeout;
        _s.DefaultGradleVersion = (SelectedVersion(DefaultTargetBox) ?? TargetBox.Text).Trim();
        _s.ThemeMode = ThemeService.Current.ToString();
        _s.Save();
        UpdateHomeLabels();
    }

    // ---------- UI state ----------

    void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
        LogEmpty.Visibility = Visibility.Collapsed;
    }

    void LogUi(string message) => Dispatcher.Invoke(() => Log(message));

    void SetStatus(string text, string brushKey = "SuccessBrush")
    {
        StatusText.Text = text;
        try { StatusDot.SetResourceReference(Shape.FillProperty, brushKey); } catch { }
    }

    void SetBusy(bool busy, string text = "Working...")
    {
        _busy = busy;
        BusyText.Text = text;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtonStates();
    }

    void UpdateButtonStates()
    {
        ScanBtn.IsEnabled = !_busy;
        RefreshVersionsBtn.IsEnabled = !_busy;
        DownloadBtn.IsEnabled = !_busy && _projects.Count > 0;
        UseDefaultBtn.IsEnabled = !_busy;
        CancelBtn.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;

        var canUse = _scanned && !_busy;
        foreach (var button in new[] { SelAllBtn, SelNoneBtn, SetTargetBtn, DryBtn, ApplyBtn, CleanBtn, StopBtn })
            button.IsEnabled = canUse;
    }

    void CancelCurrent()
    {
        try { _cts?.Cancel(); } catch { }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelCurrent();
        Log("Cancellation requested...");
        SetStatus("Cancelling...", "WarningBrush");
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            Scan_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            Apply_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            DryRun_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape && _busy)
        {
            CancelCurrent();
        }
    }

    void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        LogEmpty.Visibility = Visibility.Visible;
    }

    void TerminalToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_terminalCollapsed)
        {
            _terminalHeight = TerminalRow.ActualHeight > TerminalCollapsedHeight ? TerminalRow.ActualHeight : 170;
            _terminalCollapsed = true;
            TerminalRow.Height = new GridLength(TerminalCollapsedHeight);
            TerminalSplitter.IsEnabled = false;
            LogBox.Visibility = Visibility.Collapsed;
            TerminalToggleBtn.Content = "\uE70D";
            TerminalToggleBtn.ToolTip = "Expand log";
        }
        else
        {
            _terminalCollapsed = false;
            TerminalRow.Height = new GridLength(Math.Max(_terminalHeight, 80));
            TerminalSplitter.IsEnabled = true;
            LogBox.Visibility = Visibility.Visible;
            TerminalToggleBtn.Content = "\uE70E";
            TerminalToggleBtn.ToolTip = "Collapse log";
        }
    }

    void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
            ToastService.Show("Log copied to clipboard.", ToastKind.Success);
        }
        catch (Exception ex) { DialogService.Error(ex); }
    }

    void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Gradle Doctor log",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"gradle-doctor-{DateTime.Now:yyyyMMdd-HHmm}.log"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, LogBox.Text);
            ToastService.Show("Log saved.", ToastKind.Success);
        }
        catch (Exception ex) { DialogService.Error(ex); }
    }

    void Commit() => ProjectsGrid.CommitEdit(DataGridEditingUnit.Row, true);

    // ---------- version catalogue ----------

    async Task LoadVersionsAsync(bool force, bool showBusy = true)
    {
        if (showBusy) SetBusy(true, "Fetching Gradle versions...");
        else RefreshVersionsBtn.IsEnabled = false;
        try
        {
            _remoteVersions = (await GradleVersionService.GetVersionsAsync(force))
                .Where(v => !string.IsNullOrWhiteSpace(v.Version))
                .ToList();
            PopulateVersionLists();

            var latest = _remoteVersions.FirstOrDefault()?.Version;
            if (!string.IsNullOrWhiteSpace(latest))
            {
                if (string.IsNullOrWhiteSpace(SelectedVersion(DefaultTargetBox)) || force)
                    SelectVersion(DefaultTargetBox, latest);
                if (string.IsNullOrWhiteSpace(TargetBox.Text))
                    TargetBox.Text = latest;
            }

            Log($"Loaded {_remoteVersions.Count} stable Gradle version(s). Latest: {_remoteVersions.FirstOrDefault()?.Version ?? "unknown"}.");
        }
        catch (Exception ex)
        {
            Log("Could not refresh versions: " + ex.Message);
        }
        finally
        {
            if (showBusy) SetBusy(false);
            else RefreshVersionsBtn.IsEnabled = true;
            UpdateButtonStates();
        }
    }

    void PopulateVersionLists()
    {
        var target = TargetBox.Text;
        var defaultTarget = SelectedVersion(DefaultTargetBox)?.Trim();
        var installed = GradleCleaner.InstalledVersions(Home).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var versions = _remoteVersions.Select(v => v.Version)
            .Concat(installed)
            .Concat(_projects.Select(p => p.CurrentGradle).Where(v => v != "?"))
            .Where(v => VersionFormat.IsMatch(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v, Comparer<string>.Create(ProjectScanner.CompareVersions))
            .Select(v => new VersionOption(v, installed.Contains(v)))
            .ToList();

        _loadingUi = true;
        VersionPicker.ItemsSource = versions;
        VersionPicker.SelectedIndex = -1;
        DefaultTargetBox.ItemsSource = versions;

        if (!string.IsNullOrWhiteSpace(defaultTarget))
            SelectVersion(DefaultTargetBox, defaultTarget);
        if (DefaultTargetBox.SelectedItem == null && versions.Count > 0)
            DefaultTargetBox.SelectedIndex = 0;

        TargetBox.Text = target;
        if (!string.IsNullOrWhiteSpace(target))
            SelectVersion(VersionPicker, target);
        _loadingUi = false;
    }

    static string? SelectedVersion(ComboBox box) => box.SelectedItem switch
    {
        VersionOption option => option.Version,
        string text => text,
        _ => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim()
    };

    static void SelectVersion(ComboBox box, string version)
    {
        var match = box.Items.OfType<VersionOption>()
            .FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));
        if (match != null) box.SelectedItem = match;
    }

    async void RefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        try { await LoadVersionsAsync(true); ToastService.Show("Gradle version list refreshed.", ToastKind.Success); }
        finally { _cts?.Dispose(); _cts = null; }
    }

    void VersionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        if (VersionPicker.SelectedItem is VersionOption option)
        {
            TargetBox.Text = option.Version;
            if (option.WillDownload)
                SetStatus($"Gradle {option.Version} is not downloaded yet.", "WarningBrush");
        }
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
            UpdateButtonStates();
            SaveSettings();
            SetStatus("Folder changed. Click Scan.", "TextMutedBrush");
        }
    }

    void BrowseHome_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the Gradle user home (.gradle) folder" };
        if (Directory.Exists(Home)) dlg.InitialDirectory = Home;
        if (dlg.ShowDialog(this) == true)
        {
            GradleHomeBox.Text = dlg.FolderName;
            SaveSettings();
            PopulateVersionLists();
        }
    }

    void ResetHome_Click(object sender, RoutedEventArgs e)
    {
        GradleHomeBox.Text = "";
        SaveSettings();
        PopulateVersionLists();
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
        catch (Exception ex) { DialogService.Error(ex); }
    }

    // ---------- scan ----------

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var root = PathBox.Text;
        if (!Directory.Exists(root))
        {
            DialogService.Warning("Choose a valid projects folder first (Browse...).");
            return;
        }

        CancelCurrent();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SaveSettings();
        SetBusy(true, "Scanning projects...");
        SetStatus("Scanning...", "PrimaryBrush");
        Log($"Scanning {root} ...");

        try
        {
            var skip = SkipSet();
            _projects = await Task.Run(() => ProjectScanner.Scan(root, skip, ct), ct);
            _scanned = true;
            _compat = Compat.Load();

            var target = ReadGlobalTarget(showError: false) ?? _s.DefaultGradleVersion;
            if (string.IsNullOrWhiteSpace(target)) target = _remoteVersions.FirstOrDefault()?.Version ?? "9.8.2";
            TargetBox.Text = target;

            foreach (var p in _projects)
            {
                if (string.IsNullOrWhiteSpace(p.Target)) p.Target = target;
                RecalculatePlan(p, _compat);
            }

            PopulateVersionLists();
            RefreshGrid();
            if (_projects.Count > 0) ProjectsGrid.SelectedIndex = 0;

            Log($"Found {_projects.Count} Gradle project(s).");
            SetStatus($"{_projects.Count} project(s) found. Set targets, then Dry Run.", "SuccessBrush");
            ToastService.Show($"Found {_projects.Count} project(s).", _projects.Count > 0 ? ToastKind.Success : ToastKind.Warning);
        }
        catch (OperationCanceledException)
        {
            Log("Scan cancelled.");
            SetStatus("Scan cancelled.", "WarningBrush");
        }
        catch (Exception ex)
        {
            Log("Scan failed: " + ex.Message);
            SetStatus("Scan failed. See the log.", "DangerBrush");
            DialogService.Error(ex, "Scan failed");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---------- selection ----------

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _projects) p.Selected = true;
        UpdateSummary();
        UpdateDetails();
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _projects) p.Selected = false;
        UpdateSummary();
        UpdateDetails();
    }

    void SetTarget_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var target = ReadGlobalTarget();
        if (target == null) return;
        _compat = Compat.Load();

        foreach (var p in _projects.Where(p => p.Selected))
        {
            p.Target = target;
            RecalculatePlan(p, _compat);
        }

        UpdateSummary();
        UpdateDetails();
        SetStatus($"Target set to {target} on {_projects.Count(p => p.Selected)} selected project(s).", "SuccessBrush");
        ToastService.Show("Target applied to selected projects.", ToastKind.Success);
    }

    void UseDefault_Click(object sender, RoutedEventArgs e)
    {
        var target = (SelectedVersion(DefaultTargetBox) ?? TargetBox.Text).Trim();
        if (!VersionFormat.IsMatch(target))
        {
            DialogService.Warning("Choose a valid default Gradle version first.");
            return;
        }
        TargetBox.Text = target;
        SetTarget_Click(sender, e);
        ToastService.Show($"Default target is {target}.", ToastKind.Success);
    }

    // ---------- planning ----------

    string? ReadGlobalTarget(bool showError = true)
    {
        var target = (TargetBox.Text ?? "").Trim();
        if (VersionFormat.IsMatch(target)) return target;
        if (showError)
            DialogService.Warning("Enter a valid Gradle version in 'Target Gradle', for example 9.6.0.", "Invalid target");
        return null;
    }

    void RecalculatePlan(ProjectInfo p, Compat compat)
    {
        var target = (p.Target ?? "").Trim();
        if (string.IsNullOrWhiteSpace(target)) target = (TargetBox.Text ?? "").Trim();

        if (!VersionFormat.IsMatch(target))
        {
            p.Status = "Bad target";
            p.Warning = "Enter a valid target Gradle version.";
            return;
        }

        p.Target = target;
        if (AutoAgp && p.Agp != "-")
            p.TargetAgp = compat.BestAgpForGradle(target, p.Agp);
        else if (string.IsNullOrWhiteSpace(p.TargetAgp))
            p.TargetAgp = p.Agp;

        var targetAgp = (p.TargetAgp ?? "").Trim();
        var messages = new List<string>();

        if (!string.IsNullOrWhiteSpace(targetAgp) && targetAgp != "-" && !AgpFormat.IsMatch(targetAgp))
            messages.Add("Target AGP is not a valid version.");
        if (p.Agp == "-" && string.IsNullOrWhiteSpace(targetAgp))
            messages.Add("AGP was not detected; set Target AGP manually.");
        if (p.Agp != "-" && !string.IsNullOrWhiteSpace(targetAgp))
        {
            var incompatibility = compat.Check(targetAgp, target);
            if (!string.IsNullOrEmpty(incompatibility)) messages.Add(incompatibility);
            if (Compat.IsMajorUpgrade(p.Agp, targetAgp))
                messages.Add("Major AGP update: verify Gradle/AGP compatibility and JDK.");
            if (targetAgp.Split('.')[0] == "9")
                messages.Add("AGP 9.x requires JDK 17 or newer.");
        }

        if (ProjectScanner.CompareVersions(target, LatestTestedGradle) > 0)
            messages.Add($"Gradle {target} is newer than the latest AGP-tested version ({LatestTestedGradle}); verify before releasing.");

        p.Warning = string.Join("; ", messages.Distinct());

        var agpDiff = p.Agp != "-" && !string.IsNullOrWhiteSpace(targetAgp) &&
                      targetAgp != "-" && p.Agp != targetAgp;
        var gradleDiff = p.CurrentGradle != target;
        p.Status = agpDiff || gradleDiff ? "Will change" : "Up to date";
    }

    void RecalculateAll(Compat compat)
    {
        foreach (var p in _projects) RecalculatePlan(p, compat);
    }

    async void DryRun_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var rows = _projects.Where(p => p.Selected).ToList();
        if (rows.Count == 0)
        {
            DialogService.Warning("Tick at least one project (Use column).");
            return;
        }

        if (!EnsureTargets(rows)) return;
        _compat = Compat.Load();
        RecalculateAll(_compat);

        var changes = rows.Count(p => p.Status == "Will change");
        Log($"[Dry run] {changes} of {rows.Count} selected project(s) would change. Nothing was modified.");
        foreach (var p in rows.Where(p => p.Status == "Will change"))
        {
            var details = new List<string>();
            if (p.CurrentGradle != p.Target) details.Add($"Gradle {p.CurrentGradle} -> {p.Target}");
            if (p.Agp != "-" && p.Agp != p.TargetAgp && !string.IsNullOrWhiteSpace(p.TargetAgp))
                details.Add($"AGP {p.Agp} -> {p.TargetAgp}");
            Log($"   {p.Name}: {string.Join(", ", details)}");
        }
        foreach (var p in rows.Where(p => p.Warning.Length > 0))
            Log($"   WARNING {p.Name}: {p.Warning}");

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

        UpdateSummary();
        SetStatus($"Dry run: {changes} change(s).", changes > 0 ? "WarningBrush" : "SuccessBrush");
        ToastService.Show($"{changes} project(s) would change.", changes > 0 ? ToastKind.Warning : ToastKind.Success);
    }

    bool EnsureTargets(List<ProjectInfo> rows)
    {
        var invalid = rows.Where(p => !string.IsNullOrWhiteSpace(p.Target) && !VersionFormat.IsMatch(p.Target)).ToList();
        if (invalid.Count > 0)
        {
            DialogService.Warning("One or more projects have an invalid target Gradle version. Fix the Target column first.");
            return false;
        }

        if (rows.All(p => !string.IsNullOrWhiteSpace(p.Target))) return true;
        var target = ReadGlobalTarget();
        if (target == null) return false;
        foreach (var p in rows.Where(p => string.IsNullOrWhiteSpace(p.Target))) p.Target = target;
        return true;
    }

    // ---------- apply ----------

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        var rows = _projects.Where(p => p.Selected).ToList();
        if (rows.Count == 0)
        {
            DialogService.Warning("Tick at least one project (Use column).");
            return;
        }

        if (!EnsureTargets(rows)) return;
        _compat = Compat.Load();
        RecalculateAll(_compat);

        var todo = rows.Where(p => p.Status == "Will change").ToList();
        if (todo.Count == 0)
        {
            DialogService.Info("Nothing to change: selected projects already match their target Gradle and AGP.");
            return;
        }

        var plan = todo.Select(p => new ApplyPlanItem(
            p,
            p.CurrentGradle != p.Target ? $"{p.CurrentGradle} → {p.Target}" : "—",
            p.Agp != "-" && p.Agp != p.TargetAgp && !string.IsNullOrWhiteSpace(p.TargetAgp)
                ? $"{p.Agp} → {p.TargetAgp}" : "—",
            p.Warning)).ToList();

        var warnings = todo.Count(p => p.Warning.Length > 0);
        var agpCount = todo.Count(p => p.Agp != "-" && p.Agp != p.TargetAgp && !string.IsNullOrWhiteSpace(p.TargetAgp));

        string? jdkNote = null;
        if (todo.Any(p => p.TargetAgp.Split('.')[0] == "9"))
        {
            var (major, text) = await JavaService.DetectAsync();
            if (major is int value && value < 17)
                jdkNote = $"{text}. AGP 9.x requires JDK 17 or newer; the build may fail until the JDK is upgraded.";
            else if (major is null)
                jdkNote = text + " The build may fail until a JDK is available on PATH.";
        }

        if (!DialogService.ShowApply(plan, VerifyCheck.IsChecked == true, VerifyTask, warnings, agpCount, jdkNote))
            return;

        SaveSettings();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "Applying updates...");
        int success = 0, rolled = 0, failed = 0;

        try
        {
            var checksums = new Dictionary<string, string?>();
            foreach (var p in todo)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    p.Status = "Updating...";
                    UpdateDetails();

                    if (!checksums.TryGetValue(p.Target, out var checksum))
                    {
                        checksum = await GradleVersionService.GetChecksumAsync(p.Target, ct);
                        checksums[p.Target] = checksum;
                    }

                    var gradleChanged = ProjectScanner.UpdateWrapper(p, p.Target, checksum);
                    var agpChanged = false;
                    if (p.Agp != "-" && !string.IsNullOrWhiteSpace(p.TargetAgp) &&
                        p.TargetAgp != "-" && p.Agp != p.TargetAgp)
                    {
                        var agpResult = ProjectScanner.UpdateAgp(p, p.TargetAgp);
                        agpChanged = agpResult.Changed;
                        if (!agpChanged)
                            Log($"   {p.Name}: AGP declaration was not updated ({agpResult.Error ?? "not found"}).");
                    }

                    if (!gradleChanged && !agpChanged)
                    {
                        p.Status = "No change";
                        continue;
                    }

                    var message = $"{p.Name}:";
                    if (gradleChanged) message += $" Gradle -> {p.Target}";
                    if (agpChanged) message += $" | AGP -> {p.TargetAgp}";
                    Log(message);

                    if (VerifyCheck.IsChecked != true)
                    {
                        p.Status = "Updated";
                        success++;
                        continue;
                    }

                    p.Status = "Building...";
                    SetStatus($"Building {p.Name} ...", "PrimaryBrush");
                    Log($"Building {p.Name} (gradlew {VerifyTask}) ...");
                    var result = await Runner.Gradlew(p.Path, VerifyTask, CustomHome, Timeout, LogUi, ct);

                    if (result == false)
                    {
                        ProjectScanner.Restore(p);
                        p.TargetAgp = p.Agp;
                        p.Status = "Rolled back";
                        Log($"{p.Name}: build FAILED, rolled back to Gradle {p.CurrentGradle} / AGP {p.Agp}.");
                        rolled++;
                    }
                    else if (result == null)
                    {
                        p.Status = "Updated (not built)";
                        success++;
                    }
                    else
                    {
                        p.Status = "Updated, build OK";
                        success++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    p.Status = "Failed";
                    Log($"FAILED {p.Name}: {ex.Message}");
                    failed++;
                }
                finally
                {
                    UpdateSummary();
                    UpdateDetails();
                }
            }

            Log($"Done: {success} updated, {rolled} rolled back, {failed} failed.");
            SetStatus($"Done: {success} updated, {rolled} rolled back, {failed} failed.",
                failed + rolled > 0 ? "WarningBrush" : "SuccessBrush");
            ToastService.Show($"Update complete: {success} updated, {rolled} rolled back, {failed} failed.",
                failed + rolled > 0 ? ToastKind.Warning : ToastKind.Success);
            PopulateVersionLists();
            RefreshGrid();
        }
        catch (OperationCanceledException)
        {
            Log("Apply cancelled. Projects already updated remain updated.");
            SetStatus("Apply cancelled.", "WarningBrush");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            UpdateSummary();
            UpdateDetails();
        }
    }

    // ---------- download ----------

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        var target = ReadGlobalTarget();
        if (target == null) return;

        var project = _projects.FirstOrDefault(p => p.Selected && File.Exists(System.IO.Path.Combine(p.Path, "gradlew.bat")))
                      ?? _projects.FirstOrDefault(p => File.Exists(System.IO.Path.Combine(p.Path, "gradlew.bat")));
        if (project == null)
        {
            DialogService.Warning("Scan a project that contains gradlew.bat first, then try again.",
                "No Gradle wrapper found");
            return;
        }

        CancelCurrent();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, $"Preparing Gradle {target}...");
        SetStatus($"Preparing Gradle {target}...", "PrimaryBrush");
        try
        {
            var (ok, message) = await WrapperDownloader.EnsureAsync(project, target, CustomHome, LogUi, ct);
            Log(message);
            if (ok)
            {
                ToastService.Show(message, ToastKind.Success);
                SetStatus(message, "SuccessBrush");
            }
            else
            {
                ToastService.Show(message, ToastKind.Error);
                SetStatus(message, "DangerBrush");
            }
            PopulateVersionLists();
        }
        catch (OperationCanceledException)
        {
            Log("Download cancelled.");
            SetStatus("Download cancelled.", "WarningBrush");
        }
        catch (Exception ex)
        {
            Log("Download failed: " + ex.Message);
            SetStatus("Download failed. See the log.", "DangerBrush");
            DialogService.Error(ex, "Download failed");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---------- cleanup ----------

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var keep = KeepSet();
        foreach (var p in _projects) keep.Add(p.CurrentGradle);
        keep.Remove("?");

        if (keep.Count == 0)
        {
            DialogService.Warning("No project with a known Gradle version was found, so nothing is safe to delete.");
            return;
        }

        SaveSettings();
        SetBusy(true, "Calculating cache sizes...");
        SetStatus("Calculating sizes...", "PrimaryBrush");
        try
        {
            var home = Home;
            var unused = await Task.Run(() => GradleCleaner.FindUnused(home, keep));
            if (unused.Count == 0)
            {
                DialogService.Info("Nothing to clean. Every cached Gradle version is still in use.");
                SetStatus("Nothing to clean.", "SuccessBrush");
                return;
            }

            var dlg = new CleanupDialog(unused, _s.UseRecycleBin) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Selected.Count == 0)
            {
                SetStatus("Cleanup cancelled.", "TextMutedBrush");
                return;
            }

            _s.UseRecycleBin = dlg.UseRecycleBin;
            var chosen = dlg.Selected;
            SetBusy(true, "Deleting selected items...");
            SetStatus("Deleting...", "DangerBrush");
            var (freed, errors) = await Task.Run(() => GradleCleaner.Delete(chosen, dlg.UseRecycleBin));
            Log($"Freed {Format.Size(freed)} from {home}.");
            foreach (var error in errors) Log("ERROR " + error);
            SetStatus($"Freed {Format.Size(freed)}" + (errors.Count > 0 ? $" ({errors.Count} error(s), see log)" : ""),
                errors.Count > 0 ? "WarningBrush" : "SuccessBrush");
            ToastService.Show($"Freed {Format.Size(freed)}.", errors.Count > 0 ? ToastKind.Warning : ToastKind.Success);
            SaveSettings();
        }
        catch (Exception ex)
        {
            Log("Cleanup failed: " + ex.Message);
            SetStatus("Cleanup failed. See the log.", "DangerBrush");
            DialogService.Error(ex, "Cleanup failed");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- daemons ----------

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_projects.Count == 0)
        {
            DialogService.Warning("Scan at least one project before stopping Gradle daemons.", "No projects");
            return;
        }

        CancelCurrent();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "Stopping Gradle daemons...");
        Log("Stopping Gradle daemons for all scanned projects ...");
        var failed = 0;
        try
        {
            foreach (var path in _projects.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var result = await Runner.Gradlew(path, "--stop", CustomHome, 2, LogUi, ct);
                if (result == false) failed++;
            }
            Log(failed == 0 ? "Daemons stopped." : $"Daemons stopped with {failed} failure(s).");
            SetStatus(failed == 0 ? "Daemons stopped." : $"Daemons stopped with {failed} failure(s).",
                failed == 0 ? "SuccessBrush" : "WarningBrush");
            ToastService.Show(failed == 0 ? "Gradle daemons stopped." : "Some daemons could not be stopped.",
                failed == 0 ? ToastKind.Success : ToastKind.Warning);
        }
        catch (OperationCanceledException)
        {
            Log("Stop daemons cancelled.");
            SetStatus("Stop daemons cancelled.", "WarningBrush");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---------- grid / details ----------

    void RefreshGrid()
    {
        _view = CollectionViewSource.GetDefaultView(_projects);
        _view.Filter = FilterProject;
        ProjectsGrid.ItemsSource = _view;
        _view.Refresh();
        UpdateSummary();
        UpdateEmptyState();
    }

    bool FilterProject(object obj)
    {
        if (obj is not ProjectInfo p) return false;
        var query = (SearchBox.Text ?? "").Trim();
        if (query.Length > 0 &&
            !p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !p.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        var type = (FilterCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        return type == "All" || p.Type.Equals(type, StringComparison.OrdinalIgnoreCase);
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateSummary();
        UpdateEmptyState();
    }

    void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateSummary();
        UpdateEmptyState();
    }

    void UpdateSummary()
    {
        if (ProjectCountBadge == null) return;
        var selected = _projects.Count(p => p.Selected);
        var changes = _projects.Count(p => p.Status == "Will change");
        var warnings = _projects.Count(p => !string.IsNullOrWhiteSpace(p.Warning));
        ProjectCountBadge.Text = $"{_projects.Count} project{(_projects.Count == 1 ? "" : "s")}";
        SummarySelected.Text = $"{selected} selected";
        SummaryChanges.Text = $"{changes} change{(changes == 1 ? "" : "s")}";
        SummaryWarnings.Text = $"{warnings} warning{(warnings == 1 ? "" : "s")}";
    }

    void UpdateEmptyState()
    {
        if (EmptyState == null) return;
        EmptyState.Visibility = _projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ProjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();

    void UpdateDetails()
    {
        if (DetailContent == null) return;
        if (ProjectsGrid.SelectedItem is not ProjectInfo p)
        {
            DetailEmpty.Visibility = Visibility.Visible;
            DetailContent.Visibility = Visibility.Collapsed;
            return;
        }

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;
        DetailName.Text = p.Name;
        DetailType.Text = $"{p.Type} project";
        DetailPath.Text = p.Path;
        DetailGradle.Text = p.CurrentGradle;
        DetailAgp.Text = p.Agp;
        DetailTargetGradle.Text = string.IsNullOrWhiteSpace(p.Target) ? "—" : p.Target;
        DetailTargetAgp.Text = string.IsNullOrWhiteSpace(p.TargetAgp) ? "—" : p.TargetAgp;
        DetailStatus.Text = string.IsNullOrWhiteSpace(p.Status) ? "Ready" : p.Status;
        try { DetailStatusDot.SetResourceReference(Shape.FillProperty, StatusBrush(p.Status)); } catch { }

        var hasWarning = !string.IsNullOrWhiteSpace(p.Warning);
        DetailWarningBorder.Visibility = hasWarning ? Visibility.Visible : Visibility.Collapsed;
        DetailWarning.Text = p.Warning;
    }

    static string StatusBrush(string status) => status switch
    {
        "Will change" => "WarningBrush",
        "Updating..." or "Building..." => "PrimaryBrush",
        "Rolled back" or "Failed" or "Bad target" => "DangerBrush",
        "Updated" or "Updated, build OK" or "Updated (not built)" or "Up to date" => "SuccessBrush",
        _ => "TextMutedBrush"
    };

    void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        _detailsVisible = !_detailsVisible;
        DetailsColumn.Width = _detailsVisible ? new GridLength(330) : new GridLength(0);
        DetailsPanel.Visibility = _detailsVisible ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggleBtn.Content = _detailsVisible ? "Details" : "Show details";
    }

    void ProjectsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectsGrid.SelectedItem is ProjectInfo p) OpenFolder(p.Path);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsGrid.SelectedItem is ProjectInfo p) OpenFolder(p.Path);
    }

    void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsGrid.SelectedItem is not ProjectInfo p) return;
        try
        {
            Clipboard.SetText(p.Path);
            ToastService.Show("Project path copied.", ToastKind.Success);
        }
        catch (Exception ex) { DialogService.Error(ex); }
    }

    void SelectOnly_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsGrid.SelectedItem is not ProjectInfo selected) return;
        foreach (var p in _projects) p.Selected = ReferenceEquals(p, selected);
        UpdateSummary();
        UpdateDetails();
    }

    static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { DialogService.Error(ex); }
    }
}

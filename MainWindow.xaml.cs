using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    readonly ObservableCollection<string> _skipFolders = new();
    List<ProjectInfo> _projects = new();
    List<GradleRelease> _remoteVersions = new();
    ICollectionView? _view;
    Compat _compat = Compat.Load();
    CancellationTokenSource? _cts;
    bool _scanned;
    bool _busy;
    bool _loadingUi;
    bool _syncingTarget;
    bool _detailsVisible = true;
    bool _terminalCollapsed;
    bool _terminalAutoCollapsed;
    double _terminalHeight = 170;
    const double TerminalCollapsedHeight = 42;
    bool _isDraggingCard;
    Point _dragStartPoint;
    double _dragStartX;
    double _dragStartY;
    bool _busyCardMinimized;

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
        SkipChips.ItemsSource = _skipFolders;
        LoadTokenEditors();
        VerifyCheck.IsChecked = _s.VerifyBuild;
        RollbackCheck.IsChecked = _s.RollbackOnFailure;
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
        SizeChanged += (_, _) => UpdateSettingsHeight();
        SourceInitialized += (_, _) =>
        {
            EnableRoundedCorners();
            HookMinMaxInfo();
        };
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
        UpdateSettingsHeight();
    }

    void UpdateSettingsHeight()
    {
        SettingsScroll.MaxHeight = Math.Clamp(ActualHeight - 540, 180, 360);
    }

    void SettingsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        UpdateSettingsHeight();
        if (_terminalCollapsed) return;
        _terminalAutoCollapsed = true;
        SetTerminalCollapsed(true);
    }

    void SettingsExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        UpdateSettingsHeight();
        if (!_terminalAutoCollapsed) return;
        _terminalAutoCollapsed = false;
        SetTerminalCollapsed(false);
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
        WindowRoot.Margin = new Thickness(0);
        WindowRoot.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
    }

    void HookMinMaxInfo()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(HwndHook);
    }

    IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO();
            if (GetMonitorInfo(monitor, mi))
            {
                var rcWork = mi.rcWork;
                var rcMonitor = mi.rcMonitor;

                mmi.ptMaxPosition.X = Math.Abs(rcWork.Left - rcMonitor.Left);
                mmi.ptMaxPosition.Y = Math.Abs(rcWork.Top - rcMonitor.Top);
                mmi.ptMaxSize.X = Math.Abs(rcWork.Right - rcWork.Left);
                mmi.ptMaxSize.Y = Math.Abs(rcWork.Bottom - rcWork.Top);
                mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, mmi.ptMaxSize.X);
                mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, mmi.ptMaxSize.Y);
            }
        }
        Marshal.StructureToPtr(mmi, lParam, true);
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

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    class MONITORINFO
    {
        public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        public RECT rcMonitor = new();
        public RECT rcWork = new();
        public int dwFlags = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO lpmi);

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

        var auto = string.IsNullOrWhiteSpace(GradleHomeBox.Text);
        GradleHomeHint.Text = auto
            ? $"Auto-detected: {GradleCleaner.DefaultHome}"
            : "Custom Gradle home in use.";
        GradleHomeBox.ToolTip = auto
            ? $"Empty = auto-detect. Currently using: {GradleCleaner.DefaultHome}"
            : "Custom Gradle home. Click Auto to use the detected location.";
    }

    static readonly char[] TokenSeparators = { ',', ';', ' ', '\r', '\n', '\t' };

    static IEnumerable<string> SplitTokens(string? text) =>
        (text ?? "").Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool AddToken(ObservableCollection<string> tokens, string token, bool ignoreCase)
    {
        token = token.Trim();
        if (token.Length == 0) return false;
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (tokens.Any(t => comparer.Equals(t, token))) return false;
        tokens.Add(token);
        return true;
    }

    void LoadTokenEditors()
    {
        var needsSave = _s.SettingsVersion < AppSettings.CurrentSettingsVersion;
        var skip = SplitTokens(_s.SkipFolders).ToList();
        if (skip.Count == 0)
            skip = SplitTokens(AppSettings.DefaultSkipFolders).ToList();
        else if (needsSave &&
                 string.Equals(_s.SkipFolders.Trim(), AppSettings.LegacyDefaultSkipFolders,
                     StringComparison.OrdinalIgnoreCase))
            foreach (var token in SplitTokens(AppSettings.DefaultSkipFolders))
                if (!skip.Contains(token, StringComparer.OrdinalIgnoreCase))
                    skip.Add(token);

        if (needsSave)
        {
            _s.SettingsVersion = AppSettings.CurrentSettingsVersion;
            _s.SkipFolders = string.Join(",", skip);
            _s.Save();
        }
        foreach (var token in skip) AddToken(_skipFolders, token, true);
    }

    HashSet<string> SkipSet() => _skipFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);

    void SaveSettings()
    {
        _s.LastFolder = PathBox.Text;
        _s.GradleHome = GradleHomeBox.Text.Trim();
        _s.SkipFolders = string.Join(",", _skipFolders);
        _s.VerifyBuild = VerifyCheck.IsChecked == true;
        _s.RollbackOnFailure = RollbackCheck.IsChecked == true;
        _s.UpdateAgp = UpdateAgpCheck.IsChecked == true;
        _s.VerifyTask = VerifyTask;
        _s.TimeoutMinutes = Timeout;
        _s.DefaultGradleVersion = (SelectedVersion(DefaultTargetBox) ?? TargetBox.Text).Trim();
        _s.ThemeMode = ThemeService.Current.ToString();
        _s.Save();
        UpdateHomeLabels();
    }

    void AddSkipFromInput()
    {
        var added = 0;
        var rejected = false;
        foreach (var token in SplitTokens(SkipInput.Text))
        {
            if (token.Contains('\\') || token.Contains('/'))
            {
                rejected = true;
                continue;
            }
            if (AddToken(_skipFolders, token, true)) added++;
        }

        SkipInput.Clear();
        if (added > 0) SaveSettings();
        if (rejected)
            ToastService.Show("Use folder names only (for example .git), not full paths.", ToastKind.Warning);
    }

    void SkipInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.OemComma or Key.OemSemicolon)) return;
        AddSkipFromInput();
        e.Handled = true;
    }

    void AddSkip_Click(object sender, RoutedEventArgs e) => AddSkipFromInput();

    void SkipInput_TextChanged(object sender, TextChangedEventArgs e) =>
        SkipInputHint.Visibility = string.IsNullOrEmpty(SkipInput.Text) ? Visibility.Visible : Visibility.Collapsed;

    void RemoveSkipChip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string token) return;
        _skipFolders.Remove(token);
        SaveSettings();
    }

    void ResetSkip_Click(object sender, RoutedEventArgs e)
    {
        _skipFolders.Clear();
        foreach (var token in SplitTokens(AppSettings.DefaultSkipFolders))
            AddToken(_skipFolders, token, true);
        SaveSettings();
        ToastService.Show("Skip folders restored to defaults.", ToastKind.Info);
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

    void SetBusy(bool busy, string text = "Working...", bool isIndeterminate = true, double percent = 0, string? detail = null, string? countText = null)
    {
        _busy = busy;
        BusyText.Text = text;
        BusyDetailText.Text = detail ?? "";
        BusyDetailText.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        BusyProgress.IsIndeterminate = isIndeterminate;
        if (!isIndeterminate)
        {
            BusyProgress.Value = Math.Clamp(percent, 0, 100);
            BusyPercentText.Text = $"{(int)percent}%";
            BusyPercentText.Visibility = Visibility.Visible;
            BusyCountText.Text = countText ?? "";
            BusyCountText.Visibility = string.IsNullOrWhiteSpace(countText) ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            BusyPercentText.Visibility = Visibility.Collapsed;
            BusyCountText.Visibility = Visibility.Collapsed;
        }

        if (busy)
        {
            BusyExpandedContent.Visibility = _busyCardMinimized ? Visibility.Collapsed : Visibility.Visible;
            BusyCard.Width = _busyCardMinimized ? 280 : 410;
        }
        else
        {
            BusyCardTransform.X = 0;
            BusyCardTransform.Y = 0;
        }

        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtonStates();
    }

    void SetProgress(double percent, string? detail = null, string? countText = null)
    {
        BusyProgress.IsIndeterminate = false;
        BusyProgress.Value = Math.Clamp(percent, 0, 100);
        BusyPercentText.Text = $"{(int)percent}%";
        BusyPercentText.Visibility = Visibility.Visible;
        if (detail != null)
        {
            BusyDetailText.Text = detail;
            BusyDetailText.Visibility = Visibility.Visible;
        }
        if (countText != null)
        {
            BusyCountText.Text = countText;
            BusyCountText.Visibility = Visibility.Visible;
        }
    }

    void BusyCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null)
            return;

        _isDraggingCard = true;
        _dragStartPoint = e.GetPosition(this);
        _dragStartX = BusyCardTransform.X;
        _dragStartY = BusyCardTransform.Y;
        BusyCard.CaptureMouse();
    }

    void BusyCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingCard) return;
        var current = e.GetPosition(this);
        var diff = current - _dragStartPoint;
        BusyCardTransform.X = _dragStartX + diff.X;
        BusyCardTransform.Y = _dragStartY + diff.Y;
    }

    void BusyCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingCard)
        {
            _isDraggingCard = false;
            BusyCard.ReleaseMouseCapture();
        }
    }

    void BusyMiniToggle_Click(object sender, RoutedEventArgs e)
    {
        _busyCardMinimized = !_busyCardMinimized;
        BusyExpandedContent.Visibility = _busyCardMinimized ? Visibility.Collapsed : Visibility.Visible;
        BusyMiniToggleBtn.Content = _busyCardMinimized ? "\uE922" : "\uE921";
        BusyMiniToggleBtn.ToolTip = _busyCardMinimized ? "Expand progress card" : "Minimize progress card";
        BusyCard.Width = _busyCardMinimized ? 280 : 410;
    }

    static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;
        while (current != null)
        {
            if (current is T parent) return parent;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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
        _terminalAutoCollapsed = false;
        SetTerminalCollapsed(!_terminalCollapsed);
    }

    void TerminalSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var max = Math.Max(120, ActualHeight - 360);
        var height = Math.Clamp(TerminalRow.ActualHeight - e.VerticalChange, 80, max);
        TerminalRow.Height = new GridLength(height);
        _terminalHeight = height;
    }

    void SetTerminalCollapsed(bool collapsed)
    {
        if (collapsed == _terminalCollapsed) return;
        if (collapsed)
        {
            _terminalHeight = TerminalRow.ActualHeight > TerminalCollapsedHeight ? TerminalRow.ActualHeight : 170;
            TerminalRow.Height = new GridLength(TerminalCollapsedHeight);
            TerminalSplitter.IsEnabled = false;
            LogBox.Visibility = Visibility.Collapsed;
            TerminalToggleBtn.Content = "\uE70E";
            TerminalToggleBtn.ToolTip = "Expand log";
        }
        else
        {
            TerminalRow.Height = new GridLength(Math.Max(_terminalHeight, 80));
            TerminalSplitter.IsEnabled = true;
            LogBox.Visibility = Visibility.Visible;
            TerminalToggleBtn.Content = "\uE70D";
            TerminalToggleBtn.ToolTip = "Collapse log";
        }
        _terminalCollapsed = collapsed;
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
        if (_loadingUi || _syncingTarget) return;
        if (VersionPicker.SelectedItem is VersionOption option)
        {
            _syncingTarget = true;
            try
            {
                TargetBox.Text = option.Version;
                ApplyTargetToProjects(option.Version);
                if (option.WillDownload)
                    SetStatus($"Gradle {option.Version} is not downloaded yet.", "WarningBrush");
            }
            finally
            {
                _syncingTarget = false;
            }
        }
    }

    void TargetBox_LostFocus(object sender, RoutedEventArgs e) => CommitTargetBox();

    void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTargetBox();
            e.Handled = true;
        }
    }

    void CommitTargetBox()
    {
        if (_loadingUi || _syncingTarget) return;
        var target = (TargetBox.Text ?? "").Trim();
        if (!VersionFormat.IsMatch(target)) return;

        _syncingTarget = true;
        try
        {
            SelectVersion(VersionPicker, target);
            ApplyTargetToProjects(target);
        }
        finally
        {
            _syncingTarget = false;
        }
    }

    void DefaultTargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        var version = SelectedVersion(DefaultTargetBox)?.Trim();
        if (!string.IsNullOrWhiteSpace(version) && VersionFormat.IsMatch(version))
        {
            _s.DefaultGradleVersion = version;
        }
    }

    void ApplyTargetToProjects(string target)
    {
        if (!VersionFormat.IsMatch(target) || _projects.Count == 0) return;
        _compat = Compat.Load();

        var affected = _projects.Where(p => p.Selected).ToList();
        if (affected.Count == 0 && ProjectsGrid.SelectedItem is ProjectInfo current)
            affected.Add(current);

        if (affected.Count == 0) return;

        foreach (var p in affected)
        {
            p.Target = target;
            RecalculatePlan(p, _compat);
        }

        UpdateSummary();
        UpdateDetails();
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

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        int total = todo.Count;
        SetTerminalCollapsed(false);
        SetBusy(true, "Applying updates...", isIndeterminate: false, percent: 0,
            detail: $"Preparing {total} project(s)...", countText: $"0 / {total}");
        int success = 0, rolled = 0, failed = 0;

        try
        {
            var checksums = new Dictionary<string, string?>();
            for (int i = 0; i < total; i++)
            {
                var p = todo[i];
                ct.ThrowIfCancellationRequested();
                double startPct = (double)i / total * 100;
                SetProgress(startPct, $"Updating {p.Name} ...", $"{i + 1} / {total}");
                try
                {
                    p.Status = "Updating...";
                    UpdateDetails();

                    var distType = !string.IsNullOrWhiteSpace(p.DistributionType) ? p.DistributionType : "bin";
                    var cacheKey = $"{p.Target}:{distType}";
                    if (!checksums.TryGetValue(cacheKey, out var checksum))
                    {
                        checksum = await GradleVersionService.GetChecksumAsync(p.Target, distType, ct);
                        checksums[cacheKey] = checksum;
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
                    SetProgress(startPct, $"Building {p.Name} ({VerifyTask}) ...", $"{i + 1} / {total}");
                    var result = await Runner.Gradlew(p.Path, VerifyTask, CustomHome, Timeout, LogUi, ct);

                    if (result == false)
                    {
                        if (RollbackCheck.IsChecked == true)
                        {
                            ProjectScanner.Restore(p);
                            p.TargetAgp = p.Agp;
                            p.Status = "Rolled back";
                            Log($"{p.Name}: build FAILED, rolled back to Gradle {p.CurrentGradle} / AGP {p.Agp}.");
                            rolled++;
                        }
                        else
                        {
                            p.Status = "Build FAILED (kept)";
                            Log($"{p.Name}: build FAILED, but changes KEPT (rollback disabled). Check build output.");
                            failed++;
                        }
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
                    double endPct = (double)(i + 1) / total * 100;
                    SetProgress(endPct, $"{p.Name} completed", $"{i + 1} / {total}");
                    UpdateSummary();
                    UpdateDetails();
                }
            }

            SetProgress(100, "Done", $"{total} / {total}");

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
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var useRecycleBin = dlg.UseRecycleBin;

            CancelCurrent();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            SetBusy(true, "Deleting selected items...", isIndeterminate: false, percent: 0,
                detail: $"Starting cleanup of {chosen.Count} item(s)...", countText: $"0 of {chosen.Count}");
            SetStatus("Deleting...", "DangerBrush");
            Log($"Starting cleanup: deleting {chosen.Count} unused item(s) from {home} (Recycle Bin: {(useRecycleBin ? "Enabled" : "Disabled")}) ...");

            var (freed, errors) = await Task.Run(() => GradleCleaner.Delete(chosen, useRecycleBin, progress =>
            {
                var pct = Math.Round((double)(progress.CurrentIndex - 1) / progress.TotalCount * 100.0, 1);
                var itemLabel = $"{progress.CurrentItem.Kind.ToUpperInvariant()} {progress.CurrentItem.Version} ({Format.Size(progress.CurrentItem.Size)})";
                Dispatcher.Invoke(() =>
                {
                    SetProgress(pct, detail: $"Deleting {itemLabel}...", countText: $"{progress.CurrentIndex} of {progress.TotalCount}");
                    Log($"  [{progress.CurrentIndex}/{progress.TotalCount}] Deleting {itemLabel} -> {progress.CurrentItem.Path}");
                });
            }, ct));

            if (ct.IsCancellationRequested)
            {
                Log($"Cleanup cancelled by user. Partially freed: {Format.Size(freed)}.");
                SetStatus($"Cleanup cancelled. Freed {Format.Size(freed)}.", "WarningBrush");
                ToastService.Show($"Cleanup cancelled. Freed {Format.Size(freed)}.", ToastKind.Warning);
                return;
            }

            SetProgress(100, detail: "Cleanup complete", countText: $"{chosen.Count} of {chosen.Count}");
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

    void ProjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetails();
        if (_loadingUi || _syncingTarget) return;
        if (ProjectsGrid.SelectedItem is ProjectInfo p && !string.IsNullOrWhiteSpace(p.Target) && VersionFormat.IsMatch(p.Target))
        {
            _syncingTarget = true;
            try
            {
                TargetBox.Text = p.Target;
                SelectVersion(VersionPicker, p.Target);
            }
            finally
            {
                _syncingTarget = false;
            }
        }
    }

    void ProjectsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not ProjectInfo p) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _compat = Compat.Load();
            RecalculatePlan(p, _compat);
            UpdateSummary();
            UpdateDetails();

            if (!string.IsNullOrWhiteSpace(p.Target) && VersionFormat.IsMatch(p.Target))
            {
                _syncingTarget = true;
                try
                {
                    TargetBox.Text = p.Target;
                    SelectVersion(VersionPicker, p.Target);
                }
                finally
                {
                    _syncingTarget = false;
                }
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

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

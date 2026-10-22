using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GradleDoctor;

public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}


// ---------- Models ----------

public class ProjectInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    bool _selected = true;
    string _current = "?", _target = "", _targetAgp = "", _status = "", _warning = "", _distType = "bin";

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string PropsFile { get; set; } = "";
    public string Type { get; set; } = "Android";
    public string Agp { get; set; } = "-";
    public string CurrentGradle { get => _current; set => Set(ref _current, value); }
    public string Target { get => _target; set => Set(ref _target, value); }
    public string TargetAgp { get => _targetAgp; set => Set(ref _targetAgp, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Warning { get => _warning; set => Set(ref _warning, value); }
    public string DistributionType { get => _distType; set => Set(ref _distType, value); }
}

public record CleanItem(string Path, string Kind, string Version, long Size)
{
    public string Display => $"{Kind,-6} {Version,-10} {Format.Size(Size),10}   {Path}";
}

public static class Format
{
    public static string Size(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}

// ---------- Settings (per Windows user) ----------

public class AppSettings
{
    public static readonly string DefaultSkipFolders =
        "node_modules,.git,.github,.dart_tool,build,.gradle,.idea,Pods,.pub-cache,ephemeral,.plugin_symlinks,plugin_symlinks,.symlinks,symlinks,windows,linux,macos,ios,web,.cxx,.externalNativeBuild,bin,obj,.vscode,DerivedData";

    public const string LegacyDefaultSkipFolders =
        "node_modules,.git,.dart_tool,build,.gradle,.idea,Pods,.pub-cache,ephemeral,.plugin_symlinks,plugin_symlinks,windows,linux,macos,ios,.cxx,.externalNativeBuild";

    public const int CurrentSettingsVersion = 2;

    public string LastFolder { get; set; } = "";
    public string GradleHome { get; set; } = "";          // empty = auto-detect
    public string SkipFolders { get; set; } = DefaultSkipFolders;
    public string DefaultGradleVersion { get; set; } = ""; // empty = latest stable
    public string ThemeMode { get; set; } = "Light";       // Light, Dark, System
    public int SettingsVersion { get; set; }
    public bool UseRecycleBin { get; set; } = true;
    public bool ForceDeleteLocked { get; set; } = false;
    public bool AutoDownload { get; set; } = true;
    public bool VerifyBuild { get; set; } = false;
    public bool RollbackOnFailure { get; set; } = false;
    public bool UpdateAgp { get; set; } = true;
    public string VerifyTask { get; set; } = "assembleDebug";
    public int TimeoutMinutes { get; set; } = 15;

    static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GradleDoctor", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not critical */ }
    }
}

// ---------- Compatibility rules (editable JSON) ----------

public class Compat
{
    public string Note { get; set; } = "";
    public Dictionary<string, string> AgpMinGradle { get; set; } = new();
    public Dictionary<string, string> AgpDefaultGradle { get; set; } = new();
    public Dictionary<string, string> GradleMinAgp { get; set; } = new();
    public Dictionary<string, int> AgpMinJdk { get; set; } = new();

    public static string UserFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GradleDoctor", "compat.json");
    static string DefaultFile => System.IO.Path.Combine(AppContext.BaseDirectory, "compat.json");

    public static Compat Load()
    {
        var f = File.Exists(UserFile) ? UserFile : DefaultFile;
        try
        {
            if (File.Exists(f))
            {
                return JsonSerializer.Deserialize<Compat>(File.ReadAllText(f),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }

            using var stream = typeof(Compat).Assembly.GetManifestResourceStream("GradleDoctor.compat.json");
            if (stream != null)
            {
                return JsonSerializer.Deserialize<Compat>(stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static void EnsureUserCopy()
    {
        if (File.Exists(UserFile)) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UserFile)!);
        if (File.Exists(DefaultFile))
        {
            File.Copy(DefaultFile, UserFile);
            return;
        }

        using var stream = typeof(Compat).Assembly.GetManifestResourceStream("GradleDoctor.compat.json");
        if (stream != null)
        {
            using var file = File.Create(UserFile);
            stream.CopyTo(file);
            return;
        }

        File.WriteAllText(UserFile, "{\n  \"agpMinGradle\": {},\n  \"gradleMinAgp\": {}\n}\n");
    }

    internal static string MajorMinor(string v)
    {
        var p = v.Split('.');
        return p.Length >= 2 ? $"{p[0]}.{p[1]}" : v;
    }

    /// Returns a warning text, or "" if nothing suspicious.
    public string Check(string agp, string gradle)
    {
        if (agp == "-" || string.IsNullOrWhiteSpace(gradle) || gradle == "?") return "";
        var cmp = Comparer<string>.Create(ProjectScanner.CompareVersions);
        var msgs = new List<string>();

        if (AgpMinGradle.TryGetValue(MajorMinor(agp), out var min)
            && ProjectScanner.CompareVersions(gradle, min) < 0)
            msgs.Add($"AGP {agp} needs Gradle >= {min}");

        string? best = null;
        foreach (var k in GradleMinAgp.Keys.Where(k => ProjectScanner.CompareVersions(gradle, k) >= 0).OrderBy(k => k, cmp))
            best = k;
        if (best != null && ProjectScanner.CompareVersions(agp, GradleMinAgp[best]) < 0)
            msgs.Add($"Gradle {best}+ needs AGP >= {GradleMinAgp[best]}");

        return string.Join("; ", msgs);
    }

    /// Finds the newest AGP that is compatible with the target Gradle version.
    public string BestAgpForGradle(string gradle, string currentAgp = "-")
    {
        var cmp = Comparer<string>.Create(ProjectScanner.CompareVersions);
        var candidates = AgpMinGradle
            .Where(kv => ProjectScanner.CompareVersions(gradle, kv.Value) >= 0)
            .OrderByDescending(kv => kv.Key, cmp)
            .ToList();

        var best = candidates.Count > 0 ? NormalizeAgp(candidates[0].Key) : null;

        if (!string.IsNullOrWhiteSpace(currentAgp) && currentAgp != "-")
        {
            var currentOk = string.IsNullOrEmpty(Check(currentAgp, gradle));
            if (best == null) return currentAgp;
            if (currentOk && ProjectScanner.CompareVersions(currentAgp, best) >= 0) return currentAgp;
            if (currentOk && ProjectScanner.CompareVersions(currentAgp, "9.0") < 0 &&
                ProjectScanner.CompareVersions(best, "9.0") >= 0) return best;
        }

        return best ?? (currentAgp != "-" ? currentAgp : "8.7.0");
    }

    public string? BestGradleForAgp(string agp)
    {
        var key = MajorMinor(agp);
        if (AgpDefaultGradle.TryGetValue(key, out var exact)) return exact;
        if (AgpMinGradle.TryGetValue(key, out var min)) return min;
        return null;
    }

    public int? MinJdkForAgp(string agp)
    {
        return AgpMinJdk.TryGetValue(MajorMinor(agp), out var jdk) ? jdk : null;
    }

    public static bool IsMajorUpgrade(string from, string to) =>
        int.TryParse(from.Split('.')[0], out var a) && int.TryParse(to.Split('.')[0], out var b) && b > a;

    static string NormalizeAgp(string value)
    {
        var core = value.Split('-')[0];
        var parts = core.Split('.');
        return parts.Length switch
        {
            1 => core + ".0.0",
            2 => core + ".0",
            _ => value
        };
    }
}

// ---------- Scanner ----------

public static class ProjectScanner
{
    static readonly Regex VersionRx = new(@"gradle-(?<v>\d+(?:\.\d+)*(?:-[A-Za-z0-9]+)*?)-(?<t>bin|all)\.zip",
        RegexOptions.Compiled);

    static readonly Regex AgpPluginRx = new(
        @"com\.android\.(?:application|library)[""']\)?\s*(?:version\s*)?[""'](?<v>\d+(?:\.\d+)+)",
        RegexOptions.Compiled);

    static readonly Regex AgpClasspathRx = new(
        @"com\.android\.tools\.build:gradle:(?<v>\d+(?:\.\d+)+)", RegexOptions.Compiled);

    public static readonly HashSet<string> DefaultSkipSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".github", ".dart_tool", "build", ".gradle", ".idea",
        "Pods", ".pub-cache", "ephemeral", ".plugin_symlinks", "plugin_symlinks",
        ".symlinks", "symlinks", "windows", "linux", "macos", "ios", "web",
        ".cxx", ".externalNativeBuild", "bin", "obj", ".vscode", "DerivedData"
    };

    public static List<ProjectInfo> Scan(string root, ISet<string> skip, CancellationToken ct = default)
    {
        var result = new List<ProjectInfo>();
        var stack = new Stack<string>();
        stack.Push(root);

        var allSkip = new HashSet<string>(skip, StringComparer.OrdinalIgnoreCase);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            // 1. Check if this folder is a Flutter App Root
            var pubspec = System.IO.Path.Combine(dir, "pubspec.yaml");
            if (File.Exists(pubspec))
            {
                var androidDir = System.IO.Path.Combine(dir, "android");
                var props = System.IO.Path.Combine(androidDir, "gradle", "wrapper", "gradle-wrapper.properties");
                if (File.Exists(props))
                {
                    result.Add(Read(androidDir, props, isFlutter: true, flutterAppName: System.IO.Path.GetFileName(dir)));
                }
                // Do NOT recurse into Flutter app's internal folders (windows, linux, ephemeral, etc.)
                continue;
            }

            // 3. Check if this folder is a standalone Android/Gradle project
            var wrapperProps = System.IO.Path.Combine(dir, "gradle", "wrapper", "gradle-wrapper.properties");
            if (File.Exists(wrapperProps))
            {
                result.Add(Read(dir, wrapperProps, isFlutter: false));
                // Do not recurse into subdirectories of an already identified Gradle project
                continue;
            }

            // 4. Container directory: Recurse into child directories
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = System.IO.Path.GetFileName(sub);
                    if (allSkip.Contains(name)) continue;

                    // Skip Windows directory junctions, symlinks, and reparse points
                    var di = new DirectoryInfo(sub);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    stack.Push(sub);
                }
            }
            catch { /* access denied etc. */ }
        }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ReadVersion(string propsFile)
    {
        try
        {
            var m = VersionRx.Match(File.ReadAllText(propsFile));
            if (m.Success) return m.Groups["v"].Value;
        }
        catch { }
        return "?";
    }

    public static string ReadDistributionType(string propsFile)
    {
        try
        {
            var m = VersionRx.Match(File.ReadAllText(propsFile));
            if (m.Success && m.Groups["t"].Success) return m.Groups["t"].Value.ToLowerInvariant();
        }
        catch { }
        return "bin";
    }

    static ProjectInfo Read(string dir, string props, bool isFlutter = false, string? flutterAppName = null)
    {
        var info = new ProjectInfo { Path = dir, PropsFile = props };
        if (isFlutter)
        {
            info.Type = "Flutter";
            info.Name = flutterAppName ?? Directory.GetParent(dir)?.Name ?? System.IO.Path.GetFileName(dir);
        }
        else
        {
            var parent = Directory.GetParent(dir);
            bool flutterDetected = parent != null
                && string.Equals(System.IO.Path.GetFileName(dir), "android", StringComparison.OrdinalIgnoreCase)
                && File.Exists(System.IO.Path.Combine(parent.FullName, "pubspec.yaml"));
            info.Type = flutterDetected ? "Flutter" : "Android";
            info.Name = flutterDetected ? parent!.Name : System.IO.Path.GetFileName(dir);
        }
        info.CurrentGradle = ReadVersion(props);
        info.DistributionType = ReadDistributionType(props);
        info.Agp = AgpResolver.Detect(dir)?.Version ?? "-";
        return info;
    }

    static string BakPath(ProjectInfo p) => p.PropsFile + ".gradledoctor.bak";

    /// Rewrites distributionUrl to the target version and optionally adds its checksum.
    /// Keeps the original .bak copy. Returns true if changed.
    public static bool UpdateWrapper(ProjectInfo p, string target, string? sha256 = null)
    {
        var text = File.ReadAllText(p.PropsFile);
        var updated = VersionRx.Replace(text, m => $"gradle-{target}-{m.Groups["t"].Value}.zip");

        var shaLine = new Regex(@"^\s*distributionSha256Sum\s*=.*$", RegexOptions.Multiline);
        updated = shaLine.Replace(updated, "").TrimEnd();
        if (!string.IsNullOrWhiteSpace(sha256))
            updated += Environment.NewLine + $"distributionSha256Sum={sha256}" + Environment.NewLine;
        else
            updated += Environment.NewLine;

        if (updated == text) return false;
        if (!File.Exists(BakPath(p))) File.Copy(p.PropsFile, BakPath(p));
        File.WriteAllText(p.PropsFile, updated);
        p.CurrentGradle = target;
        p.DistributionType = ReadDistributionType(p.PropsFile);
        return true;
    }

    /// Updates AGP in settings.gradle, build.gradle, or libs.versions.toml. Keeps .bak copy.
    public static AgpUpdateResult UpdateAgp(ProjectInfo p, string newAgp)
    {
        var result = AgpResolver.Update(p.Path, newAgp);
        if (result.Changed) p.Agp = newAgp;
        return result;
    }

    /// Restores wrapper and any AGP build files from backups.
    public static void Restore(ProjectInfo p)
    {
        var bak = BakPath(p);
        if (File.Exists(bak))
        {
            File.Copy(bak, p.PropsFile, overwrite: true);
            File.Delete(bak);
        }
        AgpResolver.Restore(p.Path);
        p.CurrentGradle = ReadVersion(p.PropsFile);
        p.Agp = AgpResolver.Detect(p.Path)?.Version ?? "-";
    }

    /// Compares Gradle/AGP versions with 4 numeric parts, so 9.6 == 9.6.0.
    /// Pre-release suffixes sort before the final release (9.0-rc-1 < 9.0).
    public static int CompareVersions(string? a, string? b)
    {
        Parse(a, out var an, out var ap);
        Parse(b, out var bn, out var bp);
        for (var i = 0; i < 4; i++)
        {
            var c = an[i].CompareTo(bn[i]);
            if (c != 0) return c;
        }

        if (ap.Count == 0 && bp.Count == 0) return 0;
        if (ap.Count == 0) return 1;
        if (bp.Count == 0) return -1;

        for (var i = 0; i < Math.Max(ap.Count, bp.Count); i++)
        {
            if (i >= ap.Count) return -1;
            if (i >= bp.Count) return 1;
            var x = ap[i];
            var y = bp[i];
            var xn = long.TryParse(x, out var nx);
            var yn = long.TryParse(y, out var ny);
            var c = xn && yn ? nx.CompareTo(ny) : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    }

    public static string NormalizeVersion(string? value)
    {
        Parse(value, out var nums, out var pre);
        var core = string.Join('.', nums);
        return pre.Count > 0 ? core + "-" + string.Join('.', pre) : core;
    }

    static void Parse(string? value, out int[] numbers, out List<string> pre)
    {
        numbers = new int[4];
        pre = new List<string>();
        var text = (value ?? "").Trim();
        if (text.Length == 0) return;

        var dash = text.IndexOf('-');
        var core = dash >= 0 ? text[..dash] : text;
        var suffix = dash >= 0 ? text[(dash + 1)..] : "";
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Min(4, parts.Length); i++)
            if (int.TryParse(parts[i], out var n)) numbers[i] = n;

        if (suffix.Length > 0)
            pre = suffix.Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}

// ---------- Cleaner ----------

public static class GradleCleaner
{
    static readonly Regex DistRx = new(@"^gradle-(?<v>\d+(?:\.\d+)*(?:-[A-Za-z0-9]+)*?)-(bin|all)$", RegexOptions.Compiled);
    static readonly Regex CacheVerRx = new(@"^\d+(\.\d+)+$", RegexOptions.Compiled);

    public static string DefaultHome =>
        Environment.GetEnvironmentVariable("GRADLE_USER_HOME")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle");

    public static List<string> InstalledVersions(string home)
    {
        var set = new HashSet<string>();
        var dists = System.IO.Path.Combine(home, "wrapper", "dists");
        if (Directory.Exists(dists))
            foreach (var d in Directory.GetDirectories(dists))
            {
                var m = DistRx.Match(System.IO.Path.GetFileName(d));
                if (m.Success) set.Add(m.Groups["v"].Value);
            }
        return set.ToList();
    }

    /// Everything whose version is NOT in 'keep'. Never call with an empty keep set.
    public static List<CleanItem> FindUnused(string home, ISet<string> keep)
    {
        var items = new List<CleanItem>();

        var dists = System.IO.Path.Combine(home, "wrapper", "dists");
        if (Directory.Exists(dists))
            foreach (var d in Directory.GetDirectories(dists))
            {
                var m = DistRx.Match(System.IO.Path.GetFileName(d));
                if (m.Success && !keep.Contains(m.Groups["v"].Value))
                    items.Add(new CleanItem(d, "dist", m.Groups["v"].Value, DirSize(d)));
            }

        var caches = System.IO.Path.Combine(home, "caches");
        if (Directory.Exists(caches))
            foreach (var d in Directory.GetDirectories(caches))
            {
                var name = System.IO.Path.GetFileName(d);
                if (CacheVerRx.IsMatch(name) && !keep.Contains(name))
                    items.Add(new CleanItem(d, "cache", name, DirSize(d)));
            }

        return items.OrderBy(i => i.Version, Comparer<string>.Create(ProjectScanner.CompareVersions)).ToList();
    }

    public static long DirSize(string path)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                    try { total += new FileInfo(f).Length; } catch { }
                foreach (var s in Directory.EnumerateDirectories(d)) stack.Push(s);
            }
            catch { }
        }
        return total;
    }

    public record CleanProgress(int CurrentIndex, int TotalCount, CleanItem CurrentItem, long TotalFreedBytes);

    public static (long freed, List<string> errors) Delete(
        IReadOnlyList<CleanItem> items,
        bool useRecycleBin = false,
        bool forceDelete = false,
        Action<CleanProgress>? onProgress = null,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        long freed = 0;
        var errors = new List<string>();

        if (forceDelete)
        {
            onLog?.Invoke("Force delete enabled: checking and closing background Gradle/Kotlin daemons...");
            FileLockService.KillGradleAndKotlinDaemons(onLog);
        }

        for (int idx = 0; idx < items.Count; idx++)
        {
            if (ct.IsCancellationRequested) break;
            var i = items[idx];
            onProgress?.Invoke(new CleanProgress(idx + 1, items.Count, i, freed));

            try
            {
                bool deleted = false;

                // Try Recycle Bin only if requested and forceDelete is false
                if (useRecycleBin && !forceDelete)
                {
                    deleted = ShellDelete.SendToRecycleBin(i.Path);
                    if (!deleted && !Directory.Exists(i.Path)) deleted = true;
                }

                // If not moved to recycle bin or if permanent/force delete requested
                if (!deleted)
                {
                    DeleteDirectoryRobust(i.Path, forceDelete, onLog, ct);
                    deleted = !Directory.Exists(i.Path);
                }

                if (deleted || !Directory.Exists(i.Path))
                {
                    freed += i.Size;
                }
                else
                {
                    errors.Add($"{i.Path}: Could not delete folder (still exists).");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                errors.Add($"{i.Path}: {ex.Message}");
                onLog?.Invoke($"Failed to delete {i.Path}: {ex.Message}");
            }
        }
        return (freed, errors);
    }

    public static void DeleteDirectoryRobust(
        string dirPath,
        bool forceDelete,
        Action<string>? onLog,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(dirPath)) return;

        if (forceDelete)
        {
            FileLockService.UnlockPath(dirPath, onLog);
        }

        DeleteDirectoryInternal(new DirectoryInfo(dirPath), forceDelete, onLog, ct);

        ct.ThrowIfCancellationRequested();
        try
        {
            var di = new DirectoryInfo(dirPath);
            if (di.Exists)
            {
                di.Attributes = FileAttributes.Directory;
                di.Delete(false);
            }
        }
        catch (Exception)
        {
            if (forceDelete)
            {
                FileLockService.UnlockPath(dirPath, onLog);
                Thread.Sleep(50);
                var di = new DirectoryInfo(dirPath);
                if (di.Exists)
                {
                    di.Attributes = FileAttributes.Directory;
                    di.Delete(false);
                }
            }
            else throw;
        }
    }

    static void DeleteDirectoryInternal(DirectoryInfo dir, bool forceDelete, Action<string>? onLog, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            if ((dir.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                dir.Attributes = FileAttributes.Directory;
        }
        catch { }

        FileInfo[] files;
        try { files = dir.GetFiles(); }
        catch (Exception)
        {
            if (forceDelete)
            {
                FileLockService.UnlockPath(dir.FullName, onLog);
                files = dir.GetFiles();
            }
            else throw;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            DeleteFileInternal(file, forceDelete, onLog, ct);
        }

        DirectoryInfo[] subDirs;
        try { subDirs = dir.GetDirectories(); }
        catch (Exception)
        {
            if (forceDelete)
            {
                FileLockService.UnlockPath(dir.FullName, onLog);
                subDirs = dir.GetDirectories();
            }
            else throw;
        }

        foreach (var subDir in subDirs)
        {
            ct.ThrowIfCancellationRequested();
            DeleteDirectoryInternal(subDir, forceDelete, onLog, ct);
            ct.ThrowIfCancellationRequested();
            try
            {
                if (subDir.Exists)
                {
                    subDir.Attributes = FileAttributes.Directory;
                    subDir.Delete(false);
                }
            }
            catch (Exception)
            {
                if (forceDelete)
                {
                    FileLockService.UnlockPath(subDir.FullName, onLog);
                    Thread.Sleep(50);
                    if (subDir.Exists)
                    {
                        subDir.Attributes = FileAttributes.Directory;
                        subDir.Delete(false);
                    }
                }
                else throw;
            }
        }
    }

    static void DeleteFileInternal(FileInfo file, bool forceDelete, Action<string>? onLog, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (file.IsReadOnly || (file.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                file.Attributes = FileAttributes.Normal;
            }
            file.Delete();
        }
        catch (Exception ex) when (forceDelete && (ex is IOException or UnauthorizedAccessException))
        {
            onLog?.Invoke($"Unlocking and terminating processes for: {file.Name}");
            var lockers = FileLockService.FindLockingProcesses(file.FullName);
            if (lockers.Count > 0)
            {
                foreach (var locker in lockers)
                {
                    onLog?.Invoke($"Terminating locking process: {locker.ProcessName} (PID {locker.ProcessId})");
                    FileLockService.KillProcess(locker.ProcessId, onLog);
                }
            }
            else
            {
                FileLockService.KillGradleAndKotlinDaemons(onLog);
            }

            try { file.Attributes = FileAttributes.Normal; } catch { }
            Thread.Sleep(50);
            ct.ThrowIfCancellationRequested();
            file.Delete();
        }
    }
}

public static class ShellDelete
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    const uint FO_DELETE = 0x0003;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_SILENT = 0x0004;
    const ushort FOF_NOERRORUI = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    public static bool SendToRecycleBin(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return true;

        var op = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            fAnyOperationsAborted = false,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        int ret = SHFileOperation(ref op);
        return ret == 0 && !op.fAnyOperationsAborted && !Directory.Exists(path) && !File.Exists(path);
    }
}

// ---------- Gradle wrapper runner ----------

public static class Runner
{
    static readonly Regex SafeArgsRx = new(@"^[A-Za-z0-9_:\-. /=\[\]]+$", RegexOptions.Compiled);
    static readonly Regex LiveLineRx = new(
        @"^> Task|^BUILD |^FAILURE|^What went wrong|^Caused by|^e: |^error:|^Execution failed|^\* What went wrong",
        RegexOptions.Compiled);

    /// true = success, false = failed/timeout, null = no gradlew.bat (skipped)
    public static async Task<bool?> Gradlew(string dir, string args, string? gradleHome,
        int timeoutMinutes, Action<string> log, CancellationToken ct = default)
    {
        var bat = System.IO.Path.Combine(dir, "gradlew.bat");
        if (!File.Exists(bat))
        {
            log("  gradlew.bat not found, skipped.");
            return null;
        }

        if (!SafeArgsRx.IsMatch(args))
        {
            log("  Refusing to run suspicious Gradle arguments.");
            return false;
        }

        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{bat}\" {args}\"")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(gradleHome))
        {
            Directory.CreateDirectory(gradleHome);
            psi.Environment["GRADLE_USER_HOME"] = gradleHome;
        }

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            log("  Could not start the Gradle wrapper.");
            return false;
        }

        var tail = new Queue<string>();
        var live = 0;
        void H(string? l)
        {
            if (l == null) return;
            lock (tail)
            {
                tail.Enqueue(l);
                if (tail.Count > 30) tail.Dequeue();
            }
            if (live < 120 && LiveLineRx.IsMatch(l))
            {
                live++;
                log("  | " + l);
            }
        }
        proc.OutputDataReceived += (_, e) => H(e.Data);
        proc.ErrorDataReceived += (_, e) => H(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes)));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            log(ct.IsCancellationRequested ? "  cancelled." : "  timed out.");
            return false;
        }

        var ok = proc.ExitCode == 0;
        if (!ok && !ct.IsCancellationRequested)
            lock (tail) foreach (var l in tail) log("  | " + l);
        return ok;
    }
}

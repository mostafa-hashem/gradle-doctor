using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    string _current = "?", _target = "", _status = "", _warning = "";

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string PropsFile { get; set; } = "";
    public string Type { get; set; } = "Android";
    public string Agp { get; set; } = "-";
    public string CurrentGradle { get => _current; set => Set(ref _current, value); }
    public string Target { get => _target; set => Set(ref _target, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Warning { get => _warning; set => Set(ref _warning, value); }
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
        "node_modules,.git,.dart_tool,build,.gradle,.idea,Pods,.pub-cache,ephemeral,.plugin_symlinks,plugin_symlinks,windows,linux,macos,ios,.cxx,.externalNativeBuild";

    public string LastFolder { get; set; } = "";
    public string GradleHome { get; set; } = "";          // empty = auto-detect
    public string SkipFolders { get; set; } = DefaultSkipFolders;
    public string KeepVersions { get; set; } = "";        // extra versions never deleted
    public bool VerifyBuild { get; set; } = false;
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
    public Dictionary<string, string> GradleMinAgp { get; set; } = new();

    public static string UserFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GradleDoctor", "compat.json");
    static string DefaultFile => System.IO.Path.Combine(AppContext.BaseDirectory, "compat.json");

    public static Compat Load()
    {
        var f = File.Exists(UserFile) ? UserFile : DefaultFile;
        try
        {
            return JsonSerializer.Deserialize<Compat>(File.ReadAllText(f),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }

    public static void EnsureUserCopy()
    {
        if (File.Exists(UserFile)) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UserFile)!);
        if (File.Exists(DefaultFile)) File.Copy(DefaultFile, UserFile);
        else File.WriteAllText(UserFile, "{\n  \"agpMinGradle\": {},\n  \"gradleMinAgp\": {}\n}\n");
    }

    static string MajorMinor(string v)
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

    static bool ContainsForbiddenSegment(string path, ISet<string> skip)
    {
        var parts = path.Split(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (DefaultSkipSet.Contains(p) || skip.Contains(p)) return true;
        }
        return false;
    }

    public static List<ProjectInfo> Scan(string root, ISet<string> skip)
    {
        var result = new List<ProjectInfo>();
        var stack = new Stack<string>();
        stack.Push(root);

        var allSkip = new HashSet<string>(skip, StringComparer.OrdinalIgnoreCase);
        foreach (var s in DefaultSkipSet) allSkip.Add(s);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // 1. Skip if dir path contains any forbidden segment (e.g. ephemeral, .plugin_symlinks)
            if (ContainsForbiddenSegment(dir, allSkip)) continue;

            // 2. Check if this folder is a Flutter App Root
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

        foreach (var f in new[] { "settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts" })
        {
            var p = System.IO.Path.Combine(dir, f);
            if (!File.Exists(p)) continue;
            try
            {
                var txt = File.ReadAllText(p);
                var m = AgpPluginRx.Match(txt);
                if (!m.Success) m = AgpClasspathRx.Match(txt);
                if (m.Success) { info.Agp = m.Groups["v"].Value; break; }
            }
            catch { }
        }
        return info;
    }


    static string BakPath(ProjectInfo p) => p.PropsFile + ".gradledoctor.bak";

    /// Rewrites distributionUrl to the target version. Keeps a .bak copy. Returns true if changed.
    public static bool UpdateWrapper(ProjectInfo p, string target)
    {
        var text = File.ReadAllText(p.PropsFile);
        var updated = VersionRx.Replace(text, m => $"gradle-{target}-{m.Groups["t"].Value}.zip");
        if (updated == text) return false;
        File.Copy(p.PropsFile, BakPath(p), overwrite: true);
        File.WriteAllText(p.PropsFile, updated);
        p.CurrentGradle = target;
        return true;
    }

    /// Restores the properties file from its backup.
    public static void Restore(ProjectInfo p)
    {
        var bak = BakPath(p);
        if (!File.Exists(bak)) return;
        File.Copy(bak, p.PropsFile, overwrite: true);
        File.Delete(bak);
        p.CurrentGradle = ReadVersion(p.PropsFile);
    }

    public static int CompareVersions(string a, string b)
    {
        static Version P(string s)
        {
            var core = Regex.Match(s ?? "", @"^\d+(\.\d+)*").Value;
            if (core.Length == 0) return new Version(0, 0);
            if (!core.Contains('.')) core += ".0";
            return Version.TryParse(core, out var v) ? v : new Version(0, 0);
        }
        return P(a).CompareTo(P(b));
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

    public static (long, List<string>) Delete(IEnumerable<CleanItem> items)
    {
        long freed = 0; var errors = new List<string>();
        foreach (var i in items)
        {
            try { Directory.Delete(i.Path, recursive: true); freed += i.Size; }
            catch (Exception ex) { errors.Add($"{i.Path}: {ex.Message}"); }
        }
        return (freed, errors);
    }
}

// ---------- Gradle wrapper runner ----------

public static class Runner
{
    /// true = success, false = failed/timeout, null = no gradlew.bat (skipped)
    public static async Task<bool?> Gradlew(string dir, string args, string? gradleHome,
        int timeoutMinutes, Action<string> log)
    {
        if (!File.Exists(System.IO.Path.Combine(dir, "gradlew.bat")))
        {
            log("  gradlew.bat not found, skipped.");
            return null;
        }

        var psi = new ProcessStartInfo("cmd.exe", $"/c gradlew.bat {args}")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(gradleHome)) psi.Environment["GRADLE_USER_HOME"] = gradleHome;

        using var proc = Process.Start(psi)!;
        var tail = new Queue<string>();
        void H(string? l)
        {
            if (l == null) return;
            lock (tail) { tail.Enqueue(l); if (tail.Count > 15) tail.Dequeue(); }
        }
        proc.OutputDataReceived += (_, e) => H(e.Data);
        proc.ErrorDataReceived += (_, e) => H(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes)));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            log("  timed out.");
            return false;
        }

        bool ok = proc.ExitCode == 0;
        if (!ok) lock (tail) foreach (var l in tail) log("  | " + l);
        return ok;
    }
}

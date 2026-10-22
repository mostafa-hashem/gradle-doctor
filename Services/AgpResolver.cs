using System.Text.RegularExpressions;

namespace GradleDoctor;

public record AgpInfo(string Version, string Source, string? TomlFile = null, string? TomlKey = null);

public record AgpUpdateResult(bool Changed, IReadOnlyList<string> Files, string? Error = null);

/// <summary>
/// Finds the Android Gradle Plugin version in version catalogs and Gradle scripts,
/// including alias(...) usage, and updates every declaration that can be identified.
/// </summary>
public static class AgpResolver
{
    static readonly string[] GradleFileNames =
    {
        "settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts"
    };

    static readonly string[] ModuleFolders = { "app", "android", "feature", "core", "lib", "library" };

    static readonly Regex PluginVersionParenRx = new(
        @"id\s*\(\s*[""'](?<id>com\.android\.[\w.-]+)[""']\s*\)\s*version\s*[""'](?<v>[^""']+)[""']",
        RegexOptions.Compiled);

    static readonly Regex PluginVersionRx = new(
        @"id\s+[""'](?<id>com\.android\.[\w.-]+)[""']\s+version\s+[""'](?<v>[^""']+)[""']",
        RegexOptions.Compiled);

    static readonly Regex ClasspathParenRx = new(
        @"classpath\s*\(\s*[""']com\.android\.tools\.build:gradle:(?<v>[^""']+)[""']",
        RegexOptions.Compiled);

    static readonly Regex ClasspathRx = new(
        @"classpath\s+[""']com\.android\.tools\.build:gradle:(?<v>[^""']+)[""']",
        RegexOptions.Compiled);

    static readonly Regex AliasRx = new(
        @"alias\s*\(\s*libs\.plugins\.(?<alias>[\w.-]+)\s*\)", RegexOptions.Compiled);

    static readonly Regex TomlPluginRx = new(
        @"^\s*(?<alias>[\w.-]+)\s*=\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex TomlIdRx = new(
        @"id\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.Compiled);

    static readonly Regex TomlVersionRefRx = new(
        @"version\.ref\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.Compiled);

    static readonly Regex TomlVersionLiteralRx = new(
        @"(?<!\.)\bversion\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.Compiled);

    static readonly Regex TomlInlineAgpVersionRx = new(
        @"(?<pre>id\s*=\s*[""']com\.android\.[\w.-]+[""'][^}]*?version\s*=\s*[""'])(?<v>[^""']+)(?<post>[""'])",
        RegexOptions.Compiled);

    static readonly Regex PluginReplaceParenRx = new(
        @"(?<pre>id\s*\(\s*[""']com\.android\.[\w.-]+[""']\s*\)\s*version\s*[""'])(?<v>[^""']+)(?<post>[""'])",
        RegexOptions.Compiled);

    static readonly Regex PluginReplaceRx = new(
        @"(?<pre>id\s+[""']com\.android\.[\w.-]+[""']\s+version\s+[""'])(?<v>[^""']+)(?<post>[""'])",
        RegexOptions.Compiled);

    static readonly Regex ClasspathReplaceParenRx = new(
        @"(?<pre>classpath\s*\(\s*[""']com\.android\.tools\.build:gradle:)(?<v>[^""']+)(?<post>[""'])",
        RegexOptions.Compiled);

    static readonly Regex ClasspathReplaceRx = new(
        @"(?<pre>classpath\s+[""']com\.android\.tools\.build:gradle:)(?<v>[^""']+)(?<post>[""'])",
        RegexOptions.Compiled);

    public static string? FindToml(string projectDir)
    {
        foreach (var path in new[]
                 {
                     System.IO.Path.Combine(projectDir, "gradle", "libs.versions.toml"),
                     System.IO.Path.Combine(projectDir, "libs.versions.toml"),
                     System.IO.Path.Combine(projectDir, "..", "gradle", "libs.versions.toml")
                 })
        {
            try
            {
                var full = System.IO.Path.GetFullPath(path);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    public static AgpInfo? Detect(string projectDir)
    {
        var tomlPath = FindToml(projectDir);
        var toml = tomlPath != null ? TryParseToml(tomlPath) : null;

        if (toml != null)
        {
            foreach (var plugin in toml.Plugins.Where(p => IsAgpId(p.Id)))
            {
                if (plugin.VersionRef != null && toml.Versions.TryGetValue(plugin.VersionRef, out var version))
                    return new AgpInfo(version, "toml", tomlPath, plugin.VersionRef);
                if (!string.IsNullOrWhiteSpace(plugin.Version))
                    return new AgpInfo(plugin.Version, "toml-inline", tomlPath, plugin.Alias);
            }
        }

        foreach (var file in EnumerateGradleFiles(projectDir))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var m = PluginVersionParenRx.Match(text);
            if (!m.Success) m = PluginVersionRx.Match(text);
            if (m.Success) return new AgpInfo(m.Groups["v"].Value, file, null, null);

            foreach (Match am in AliasRx.Matches(text))
            {
                var alias = am.Groups["alias"].Value;
                var plugin = toml?.Plugins.FirstOrDefault(p => AliasesEqual(p.Alias, alias));
                if (plugin == null) continue;
                if (plugin.VersionRef != null && toml!.Versions.TryGetValue(plugin.VersionRef, out var version))
                    return new AgpInfo(version, "alias", tomlPath, plugin.VersionRef);
                if (!string.IsNullOrWhiteSpace(plugin.Version))
                    return new AgpInfo(plugin.Version, "alias-inline", tomlPath, plugin.Alias);
            }

            m = ClasspathParenRx.Match(text);
            if (!m.Success) m = ClasspathRx.Match(text);
            if (m.Success) return new AgpInfo(m.Groups["v"].Value, file, null, null);
        }

        return null;
    }

    public static AgpUpdateResult Update(string projectDir, string newAgp, AgpInfo? info = null)
    {
        info ??= Detect(projectDir);
        var changed = new List<string>();
        var tomlPath = info?.TomlFile ?? FindToml(projectDir);
        var toml = tomlPath != null ? TryParseToml(tomlPath) : null;

        if (tomlPath != null && toml != null)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (info?.TomlKey != null) keys.Add(info.TomlKey);

            foreach (var plugin in toml.Plugins.Where(p => IsAgpId(p.Id)))
            {
                if (plugin.VersionRef != null) keys.Add(plugin.VersionRef);
            }

            foreach (var key in keys)
            {
                if (!toml.Versions.ContainsKey(key)) continue;
                if (ReplaceTomlVersion(tomlPath, key, newAgp))
                    changed.Add(tomlPath);
            }

            try
            {
                var text = File.ReadAllText(tomlPath);
                var updated = TomlInlineAgpVersionRx.Replace(text, m =>
                    m.Groups["pre"].Value + newAgp + m.Groups["post"].Value);
                if (updated != text)
                {
                    Backup(tomlPath);
                    File.WriteAllText(tomlPath, updated);
                    if (!changed.Contains(tomlPath, StringComparer.OrdinalIgnoreCase)) changed.Add(tomlPath);
                }
            }
            catch { }
        }

        foreach (var file in EnumerateGradleFiles(projectDir))
        {
            try
            {
                var text = File.ReadAllText(file);
                var updated = PluginReplaceParenRx.Replace(text, m =>
                    m.Groups["pre"].Value + newAgp + m.Groups["post"].Value);
                updated = PluginReplaceRx.Replace(updated, m =>
                    m.Groups["pre"].Value + newAgp + m.Groups["post"].Value);
                updated = ClasspathReplaceParenRx.Replace(updated, m =>
                    m.Groups["pre"].Value + newAgp + m.Groups["post"].Value);
                updated = ClasspathReplaceRx.Replace(updated, m =>
                    m.Groups["pre"].Value + newAgp + m.Groups["post"].Value);

                if (updated == text) continue;
                Backup(file);
                File.WriteAllText(file, updated);
                if (!changed.Contains(file, StringComparer.OrdinalIgnoreCase)) changed.Add(file);
            }
            catch { }
        }

        if (changed.Count > 0 && int.TryParse(newAgp.Split('.')[0], out var agpMajor) && agpMajor >= 9)
        {
            try
            {
                if (ApplyAgp9CompatibilityPatch(projectDir))
                {
                    var bg = System.IO.Path.Combine(projectDir, "build.gradle");
                    if (!changed.Contains(bg, StringComparer.OrdinalIgnoreCase)) changed.Add(bg);
                }
            }
            catch { }
        }

        if (changed.Count == 0)
            return new AgpUpdateResult(false, Array.Empty<string>(), "Could not find an AGP declaration to update.");
        return new AgpUpdateResult(true, changed);
    }

    /// <summary>
    /// When upgrading AGP to 9.0+, some Flutter plugins (like file_picker, firebase_analytics) skip
    /// applying the Kotlin Android plugin due to conditional version checks in their build.gradle.
    /// This injects a subprojects hook into the root build.gradle to ensure their Kotlin sources compile.
    /// </summary>
    public static bool ApplyAgp9CompatibilityPatch(string projectDir)
    {
        var buildGradle = System.IO.Path.Combine(projectDir, "build.gradle");
        if (!File.Exists(buildGradle)) return false;

        var text = File.ReadAllText(buildGradle);
        if (text.Contains("file_picker") && text.Contains("org.jetbrains.kotlin.android"))
            return false;

        var patch = Environment.NewLine +
            "// Added by GradleDoctor: Ensure Kotlin Android plugin is applied to Flutter plugins under AGP 9+" + Environment.NewLine +
            "subprojects { project ->" + Environment.NewLine +
            "    if (project.name in ['file_picker', 'firebase_analytics']) {" + Environment.NewLine +
            "        project.pluginManager.withPlugin('com.android.library') {" + Environment.NewLine +
            "            if (!project.plugins.hasPlugin('org.jetbrains.kotlin.android')) {" + Environment.NewLine +
            "                project.apply plugin: 'org.jetbrains.kotlin.android'" + Environment.NewLine +
            "            }" + Environment.NewLine +
            "        }" + Environment.NewLine +
            "    }" + Environment.NewLine +
            "}" + Environment.NewLine;

        Backup(buildGradle);
        File.WriteAllText(buildGradle, text + patch);
        return true;
    }

    public static bool Restore(string projectDir)
    {
        var restored = false;
        foreach (var file in EnumerateGradleFiles(projectDir).Concat(
                     new[] { FindToml(projectDir) }.Where(p => p != null).Cast<string>()))
        {
            var bak = file + ".gradledoctor.bak";
            if (!File.Exists(bak)) continue;
            try
            {
                File.Copy(bak, file, overwrite: true);
                File.Delete(bak);
                restored = true;
            }
            catch { }
        }
        return restored;
    }

    static IEnumerable<string> EnumerateGradleFiles(string projectDir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> FilesIn(string dir)
        {
            foreach (var name in GradleFileNames)
            {
                var path = System.IO.Path.Combine(dir, name);
                if (File.Exists(path) && seen.Add(path)) yield return path;
            }
        }

        foreach (var file in FilesIn(projectDir)) yield return file;

        foreach (var folder in ModuleFolders)
        {
            var dir = System.IO.Path.Combine(projectDir, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in FilesIn(dir)) yield return file;
        }

        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(projectDir).ToList(); }
        catch { dirs = new List<string>(); }

        foreach (var dir in dirs)
        {
            var name = System.IO.Path.GetFileName(dir);
            if (name.StartsWith('.') || name.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("gradle", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in FilesIn(dir)) yield return file;
        }
    }

    static void Backup(string file)
    {
        var bak = file + ".gradledoctor.bak";
        if (!File.Exists(bak)) File.Copy(file, bak);
    }

    static bool ReplaceTomlVersion(string file, string key, string newVersion)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            var inVersions = false;
            var changed = false;
            var rx = new Regex(@"^(?<pre>\s*" + Regex.Escape(key) + @"\s*=\s*[""'])(?<v>[^""']*)(?<post>[""'])",
                RegexOptions.Compiled);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = StripComment(lines[i]).Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inVersions = trimmed.Equals("[versions]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inVersions) continue;
                var m = rx.Match(lines[i]);
                if (!m.Success) continue;
                lines[i] = m.Groups["pre"].Value + newVersion + m.Groups["post"].Value;
                changed = true;
                break;
            }
            if (changed) File.WriteAllLines(file, lines);
            return changed;
        }
        catch { return false; }
    }

    static TomlInfo? TryParseToml(string path)
    {
        try
        {
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var plugins = new List<TomlPlugin>();
            var section = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = StripComment(raw).Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }
                if (section.Equals("versions", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(line, @"^(?<k>[\w.-]+)\s*=\s*[""'](?<v>[^""']+)[""']");
                    if (m.Success) versions[m.Groups["k"].Value] = m.Groups["v"].Value;
                }
                else if (section.Equals("plugins", StringComparison.OrdinalIgnoreCase))
                {
                    var m = TomlPluginRx.Match(line);
                    if (!m.Success) continue;
                    var body = m.Groups["body"].Value;
                    var id = TomlIdRx.Match(body).Groups["v"].Value;
                    var verRef = TomlVersionRefRx.Match(body).Groups["v"].Value;
                    var ver = TomlVersionLiteralRx.Match(body).Groups["v"].Value;
                    plugins.Add(new TomlPlugin(m.Groups["alias"].Value, id,
                        NullIfEmpty(verRef), NullIfEmpty(ver)));
                }
            }
            return new TomlInfo(versions, plugins);
        }
        catch { return null; }
    }

    static string StripComment(string line)
    {
        var idx = line.IndexOf('#');
        return idx >= 0 ? line[..idx] : line;
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    static bool IsAgpId(string id) =>
        id.StartsWith("com.android.application", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("com.android.library", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("com.android.dynamic-feature", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("com.android.test", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("com.android.instantapp", StringComparison.OrdinalIgnoreCase);

    static bool AliasesEqual(string a, string b) =>
        NormalizeAlias(a).Equals(NormalizeAlias(b), StringComparison.OrdinalIgnoreCase);

    static string NormalizeAlias(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    sealed record TomlPlugin(string Alias, string Id, string? VersionRef, string? Version);
    sealed record TomlInfo(Dictionary<string, string> Versions, List<TomlPlugin> Plugins);
}

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GradleDoctor;

public class ProjectHealthInspection
{
    public List<string> Issues { get; } = new();
    public List<string> Warnings { get; } = new();
}

public class ProjectHealthResult
{
    public List<string> FixesApplied { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool HasModifications => FixesApplied.Count > 0;
}

/// <summary>
/// Verifies and repairs project files (Gradle wrapper, gradlew scripts, gradle-wrapper.jar,
/// local.properties SDK paths, gradle.properties JVM/AndroidX settings, AGP 9+ compatibility patches,
/// and Kotlin plugin compatibility) to ensure projects build and run without errors.
/// </summary>
public static class ProjectDoctor
{
    static readonly Regex ShaLineRx = new(@"^\s*distributionSha256Sum\s*=.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    static Stream? GetEmbeddedResourceStream(string resourceName)
    {
        var assembly = typeof(ProjectDoctor).Assembly;
        var match = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
        return match != null ? assembly.GetManifestResourceStream(match) : null;
    }

    static bool ExtractEmbeddedResource(string resourceName, string destinationPath)
    {
        try
        {
            using var stream = GetEmbeddedResourceStream(resourceName);
            if (stream == null) return false;
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            using var fs = File.Create(destinationPath);
            stream.CopyTo(fs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static ProjectHealthInspection InspectHealth(ProjectInfo p, string targetGradle, IEnumerable<ProjectInfo>? knownProjects)
    {
        var report = new ProjectHealthInspection();
        var wrapperDir = Path.Combine(p.Path, "gradle", "wrapper");
        var propsFile = !string.IsNullOrWhiteSpace(p.PropsFile) ? p.PropsFile : Path.Combine(wrapperDir, "gradle-wrapper.properties");
        var jarFile = Path.Combine(wrapperDir, "gradle-wrapper.jar");
        var batFile = Path.Combine(p.Path, "gradlew.bat");
        var shFile = Path.Combine(p.Path, "gradlew");

        if (!File.Exists(propsFile))
            report.Issues.Add("Missing gradle-wrapper.properties");
        else
        {
            var text = File.ReadAllText(propsFile);
            if (!text.Contains($"gradle-{targetGradle}-") || !text.Contains(@"https\:"))
                report.Issues.Add("gradle-wrapper.properties distributionUrl needs alignment");
        }

        if (!File.Exists(jarFile) || new FileInfo(jarFile).Length == 0)
            report.Issues.Add("Missing or empty gradle-wrapper.jar");

        if (!File.Exists(batFile) || new FileInfo(batFile).Length == 0)
            report.Issues.Add("Missing gradlew.bat");

        if (!File.Exists(shFile) || new FileInfo(shFile).Length == 0)
            report.Issues.Add("Missing gradlew (Unix script)");
        else
        {
            try
            {
                if (File.ReadAllText(shFile).Contains("\r\n"))
                    report.Issues.Add("gradlew contains Windows CRLF line endings");
            }
            catch { }
        }

        var localProp = Path.Combine(p.Path, "local.properties");
        if (!File.Exists(localProp))
            report.Issues.Add("Missing local.properties");
        else
        {
            var lpText = File.ReadAllText(localProp);
            if (!Regex.IsMatch(lpText, @"^\s*sdk\.dir\s*=", RegexOptions.Multiline))
                report.Issues.Add("Missing sdk.dir in local.properties");
            if (p.Type == "Flutter" && !Regex.IsMatch(lpText, @"^\s*flutter\.sdk\s*=", RegexOptions.Multiline))
                report.Issues.Add("Missing flutter.sdk in local.properties");
        }

        var effectiveAgp = !string.IsNullOrWhiteSpace(p.TargetAgp) && p.TargetAgp != "-" ? p.TargetAgp : p.Agp;
        if (effectiveAgp != "-" && int.TryParse(effectiveAgp.Split('.')[0], out var agpMajor) && agpMajor >= 9)
        {
            var bg = Path.Combine(p.Path, "build.gradle");
            if (File.Exists(bg))
            {
                var bgText = File.ReadAllText(bg);
                if (!bgText.Contains("file_picker") || !bgText.Contains("org.jetbrains.kotlin.android"))
                    report.Issues.Add("Missing AGP 9+ compatibility patch in build.gradle");
            }
        }

        return report;
    }

    public static ProjectHealthResult CheckAndFix(
        ProjectInfo p,
        string targetGradle,
        string? checksum,
        IEnumerable<ProjectInfo>? knownProjects,
        Action<string>? log)
    {
        var result = new ProjectHealthResult();

        EnsureWrapper(p, targetGradle, checksum, knownProjects, result);
        EnsureLocalProperties(p, knownProjects, result);
        EnsureGradleProperties(p, result);
        EnsureAgpAndKotlinCompatibility(p, result);

        return result;
    }

    static void EnsureWrapper(
        ProjectInfo p,
        string targetGradle,
        string? checksum,
        IEnumerable<ProjectInfo>? knownProjects,
        ProjectHealthResult result)
    {
        var wrapperDir = Path.Combine(p.Path, "gradle", "wrapper");
        if (!Directory.Exists(wrapperDir))
            Directory.CreateDirectory(wrapperDir);

        var propsFile = !string.IsNullOrWhiteSpace(p.PropsFile) ? p.PropsFile : Path.Combine(wrapperDir, "gradle-wrapper.properties");
        p.PropsFile = propsFile;
        var distType = !string.IsNullOrWhiteSpace(p.DistributionType) ? p.DistributionType : "bin";
        var expectedUrl = $"distributionUrl=https\\://services.gradle.org/distributions/gradle-{targetGradle}-{distType}.zip";

        if (!File.Exists(propsFile))
        {
            var text = "distributionBase=GRADLE_USER_HOME" + Environment.NewLine +
                       "distributionPath=wrapper/dists" + Environment.NewLine +
                       "zipStoreBase=GRADLE_USER_HOME" + Environment.NewLine +
                       "zipStorePath=wrapper/dists" + Environment.NewLine +
                       expectedUrl + Environment.NewLine;
            if (!string.IsNullOrWhiteSpace(checksum))
                text += $"distributionSha256Sum={checksum}" + Environment.NewLine;

            File.WriteAllText(propsFile, text);
            result.FixesApplied.Add("Created gradle-wrapper.properties");
        }
        else
        {
            var text = File.ReadAllText(propsFile);
            var modified = false;

            var urlRx = new Regex(@"^\s*distributionUrl\s*=.*$", RegexOptions.Multiline);
            if (!urlRx.IsMatch(text))
            {
                text += (text.EndsWith("\n") ? "" : Environment.NewLine) + expectedUrl + Environment.NewLine;
                modified = true;
                result.FixesApplied.Add("Configured distributionUrl");
            }
            else
            {
                var curUrl = urlRx.Match(text).Value.Trim();
                if (!curUrl.Contains($"gradle-{targetGradle}-") || !curUrl.Contains(@"https\:"))
                {
                    text = urlRx.Replace(text, expectedUrl);
                    modified = true;
                    result.FixesApplied.Add("Aligned distributionUrl to target Gradle");
                }
            }

            var stdProps = new Dictionary<string, string>
            {
                ["distributionBase"] = "GRADLE_USER_HOME",
                ["distributionPath"] = "wrapper/dists",
                ["zipStoreBase"] = "GRADLE_USER_HOME",
                ["zipStorePath"] = "wrapper/dists"
            };

            foreach (var (k, v) in stdProps)
            {
                var rx = new Regex(@"^\s*" + Regex.Escape(k) + @"\s*=.*$", RegexOptions.Multiline);
                if (!rx.IsMatch(text))
                {
                    text += (text.EndsWith("\n") ? "" : Environment.NewLine) + $"{k}={v}" + Environment.NewLine;
                    modified = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(checksum))
            {
                var expectedSha = $"distributionSha256Sum={checksum}";
                if (ShaLineRx.IsMatch(text))
                {
                    var curSha = ShaLineRx.Match(text).Value.Trim();
                    if (curSha != expectedSha)
                    {
                        text = ShaLineRx.Replace(text, expectedSha);
                        modified = true;
                        result.FixesApplied.Add("Updated distributionSha256Sum");
                    }
                }
                else
                {
                    text += (text.EndsWith("\n") ? "" : Environment.NewLine) + expectedSha + Environment.NewLine;
                    modified = true;
                    result.FixesApplied.Add("Added distributionSha256Sum");
                }
            }

            if (modified)
            {
                Backup(propsFile);
                File.WriteAllText(propsFile, text);
            }
        }

        // gradle-wrapper.jar
        var jarFile = Path.Combine(wrapperDir, "gradle-wrapper.jar");
        if (!File.Exists(jarFile) || new FileInfo(jarFile).Length == 0)
        {
            if (ExtractEmbeddedResource("gradle-wrapper.jar", jarFile))
            {
                result.FixesApplied.Add("Restored gradle-wrapper.jar");
            }
            else
            {
                var sibling = knownProjects?
                    .Select(kp => Path.Combine(kp.Path, "gradle", "wrapper", "gradle-wrapper.jar"))
                    .FirstOrDefault(f => File.Exists(f) && new FileInfo(f).Length > 0);
                if (sibling != null)
                {
                    File.Copy(sibling, jarFile, true);
                    result.FixesApplied.Add("Restored gradle-wrapper.jar from sibling project");
                }
                else
                {
                    result.Warnings.Add("gradle-wrapper.jar is missing");
                }
            }
        }

        // gradlew.bat
        var batFile = Path.Combine(p.Path, "gradlew.bat");
        if (!File.Exists(batFile) || new FileInfo(batFile).Length == 0)
        {
            if (ExtractEmbeddedResource("gradlew.bat", batFile))
            {
                result.FixesApplied.Add("Restored gradlew.bat");
            }
        }

        // gradlew
        var shFile = Path.Combine(p.Path, "gradlew");
        if (!File.Exists(shFile) || new FileInfo(shFile).Length == 0)
        {
            if (ExtractEmbeddedResource("gradlew", shFile))
            {
                var shText = File.ReadAllText(shFile).Replace("\r\n", "\n");
                File.WriteAllText(shFile, shText);
                result.FixesApplied.Add("Restored gradlew (Unix script)");
            }
        }
        else
        {
            try
            {
                var shText = File.ReadAllText(shFile);
                if (shText.Contains("\r\n"))
                {
                    File.WriteAllText(shFile, shText.Replace("\r\n", "\n"));
                    result.FixesApplied.Add("Converted gradlew to Unix LF line endings");
                }
            }
            catch { }
        }
    }

    static void EnsureLocalProperties(ProjectInfo p, IEnumerable<ProjectInfo>? knownProjects, ProjectHealthResult result)
    {
        var localProp = Path.Combine(p.Path, "local.properties");
        var text = File.Exists(localProp) ? File.ReadAllText(localProp) : "";
        var modified = false;

        var sdkRx = new Regex(@"^\s*sdk\.dir\s*=\s*(?<dir>.*)$", RegexOptions.Multiline);
        var sdkMatch = sdkRx.Match(text);
        var hasValidSdk = false;
        if (sdkMatch.Success)
        {
            var rawDir = sdkMatch.Groups["dir"].Value.Trim().Replace("\\:", ":").Replace("\\\\", "\\");
            if (Directory.Exists(rawDir)) hasValidSdk = true;
        }

        if (!hasValidSdk)
        {
            var resolvedSdk = ResolveAndroidSdk(knownProjects);
            if (!string.IsNullOrEmpty(resolvedSdk))
            {
                var escaped = EscapePropertiesValue(resolvedSdk);
                var newLine = $"sdk.dir={escaped}";
                if (sdkMatch.Success)
                    text = sdkRx.Replace(text, newLine);
                else
                    text += (text.Length > 0 && !text.EndsWith("\n") ? Environment.NewLine : "") + newLine + Environment.NewLine;
                modified = true;
                result.FixesApplied.Add($"Configured sdk.dir in local.properties");
            }
            else
            {
                result.Warnings.Add("Android SDK path not detected. Make sure ANDROID_HOME is set.");
            }
        }

        if (p.Type == "Flutter")
        {
            var flutterRx = new Regex(@"^\s*flutter\.sdk\s*=\s*(?<dir>.*)$", RegexOptions.Multiline);
            var flutterMatch = flutterRx.Match(text);
            var hasValidFlutter = false;
            if (flutterMatch.Success)
            {
                var rawDir = flutterMatch.Groups["dir"].Value.Trim().Replace("\\:", ":").Replace("\\\\", "\\");
                if (Directory.Exists(rawDir)) hasValidFlutter = true;
            }

            if (!hasValidFlutter)
            {
                var resolvedFlutter = ResolveFlutterSdk(knownProjects);
                if (!string.IsNullOrEmpty(resolvedFlutter))
                {
                    var escaped = EscapePropertiesValue(resolvedFlutter);
                    var newLine = $"flutter.sdk={escaped}";
                    if (flutterMatch.Success)
                        text = flutterRx.Replace(text, newLine);
                    else
                        text += (text.Length > 0 && !text.EndsWith("\n") ? Environment.NewLine : "") + newLine + Environment.NewLine;
                    modified = true;
                    result.FixesApplied.Add($"Configured flutter.sdk in local.properties");
                }
                else
                {
                    result.Warnings.Add("Flutter SDK path not detected in environment or local.properties.");
                }
            }
        }

        if (modified)
        {
            if (File.Exists(localProp)) Backup(localProp);
            File.WriteAllText(localProp, text);
        }
    }

    static void EnsureGradleProperties(ProjectInfo p, ProjectHealthResult result)
    {
        var propFile = Path.Combine(p.Path, "gradle.properties");
        if (File.Exists(propFile))
        {
            var text = File.ReadAllText(propFile);
            var modified = false;

            if (!text.Contains("android.useAndroidX"))
            {
                text += (text.EndsWith("\n") ? "" : Environment.NewLine) + "android.useAndroidX=true" + Environment.NewLine;
                modified = true;
                result.FixesApplied.Add("Enabled android.useAndroidX in gradle.properties");
            }

            if (!text.Contains("org.gradle.jvmargs"))
            {
                text += (text.EndsWith("\n") ? "" : Environment.NewLine) + "org.gradle.jvmargs=-Xmx2048m -Dfile.encoding=UTF-8" + Environment.NewLine;
                modified = true;
                result.FixesApplied.Add("Configured JVM memory (-Xmx2048m) in gradle.properties");
            }

            if (modified)
            {
                Backup(propFile);
                File.WriteAllText(propFile, text);
            }
        }
        else
        {
            var initial = "org.gradle.jvmargs=-Xmx2048m -Dfile.encoding=UTF-8" + Environment.NewLine +
                          "android.useAndroidX=true" + Environment.NewLine;
            File.WriteAllText(propFile, initial);
            result.FixesApplied.Add("Created gradle.properties with AndroidX and JVM options");
        }
    }

    static void EnsureAgpAndKotlinCompatibility(ProjectInfo p, ProjectHealthResult result)
    {
        var effectiveAgp = !string.IsNullOrWhiteSpace(p.TargetAgp) && p.TargetAgp != "-" ? p.TargetAgp : p.Agp;
        if (effectiveAgp == "-") return;

        var agpMajor = int.TryParse(effectiveAgp.Split('.')[0], out var m) ? m : 0;
        if (agpMajor >= 9)
        {
            if (AgpResolver.ApplyAgp9CompatibilityPatch(p.Path))
            {
                result.FixesApplied.Add("Applied AGP 9+ Flutter plugin compatibility patch to build.gradle");
            }
        }

        // Kotlin plugin compatibility: AGP 8.3+ and AGP 9.x require Kotlin 1.9.20+ or 2.0+
        if (agpMajor >= 8)
        {
            EnsureCompatibleKotlin(p, agpMajor, result);
        }
    }

    static void EnsureCompatibleKotlin(ProjectInfo p, int agpMajor, ProjectHealthResult result)
    {
        var targetKotlin = agpMajor >= 9 ? "2.0.21" : "1.9.24";

        var filesToCheck = new[]
        {
            Path.Combine(p.Path, "settings.gradle"),
            Path.Combine(p.Path, "settings.gradle.kts"),
            Path.Combine(p.Path, "build.gradle"),
            Path.Combine(p.Path, "build.gradle.kts")
        };

        var kotlinPluginRx = new Regex(
            @"(?<pre>id\s*\(?[""']org\.jetbrains\.kotlin\.android[""']\)?\s*version\s*[""'])(?<v>[^""']+)(?<post>[""'])",
            RegexOptions.Compiled);

        var kotlinClasspathRx = new Regex(
            @"(?<pre>org\.jetbrains\.kotlin:kotlin-gradle-plugin:)(?<v>[^""']+)(?<post>[""'])",
            RegexOptions.Compiled);

        var kotlinExtRx = new Regex(
            @"(?<pre>ext\.kotlin_version\s*=\s*['""])(?<v>[^'""]+)(?<post>['""])",
            RegexOptions.Compiled);

        foreach (var file in filesToCheck)
        {
            if (!File.Exists(file)) continue;
            try
            {
                var text = File.ReadAllText(file);
                var modified = false;

                text = kotlinPluginRx.Replace(text, match =>
                {
                    var ver = match.Groups["v"].Value;
                    if (ProjectScanner.CompareVersions(ver, "1.9.0") < 0)
                    {
                        modified = true;
                        result.FixesApplied.Add($"Updated Kotlin Android plugin from {ver} to {targetKotlin}");
                        return match.Groups["pre"].Value + targetKotlin + match.Groups["post"].Value;
                    }
                    return match.Value;
                });

                text = kotlinClasspathRx.Replace(text, match =>
                {
                    var ver = match.Groups["v"].Value;
                    if (ProjectScanner.CompareVersions(ver, "1.9.0") < 0)
                    {
                        modified = true;
                        result.FixesApplied.Add($"Updated Kotlin classpath plugin from {ver} to {targetKotlin}");
                        return match.Groups["pre"].Value + targetKotlin + match.Groups["post"].Value;
                    }
                    return match.Value;
                });

                text = kotlinExtRx.Replace(text, match =>
                {
                    var ver = match.Groups["v"].Value;
                    if (ProjectScanner.CompareVersions(ver, "1.9.0") < 0)
                    {
                        modified = true;
                        result.FixesApplied.Add($"Updated ext.kotlin_version from {ver} to {targetKotlin}");
                        return match.Groups["pre"].Value + targetKotlin + match.Groups["post"].Value;
                    }
                    return match.Value;
                });

                if (modified)
                {
                    Backup(file);
                    File.WriteAllText(file, text);
                }
            }
            catch { }
        }

        var tomlPath = AgpResolver.FindToml(p.Path);
        if (tomlPath != null && File.Exists(tomlPath))
        {
            try
            {
                var tomlText = File.ReadAllText(tomlPath);
                var kotlinTomlRx = new Regex(@"(?<pre>^\s*kotlin\s*=\s*[""'])(?<v>[^""']+)(?<post>[""'])", RegexOptions.Multiline);
                if (kotlinTomlRx.IsMatch(tomlText))
                {
                    var match = kotlinTomlRx.Match(tomlText);
                    var curVer = match.Groups["v"].Value;
                    if (ProjectScanner.CompareVersions(curVer, "1.9.0") < 0)
                    {
                        Backup(tomlPath);
                        tomlText = kotlinTomlRx.Replace(tomlText, m => m.Groups["pre"].Value + targetKotlin + m.Groups["post"].Value);
                        File.WriteAllText(tomlPath, tomlText);
                        result.FixesApplied.Add($"Updated Kotlin version in TOML from {curVer} to {targetKotlin}");
                    }
                }
            }
            catch { }
        }
    }

    public static string? ResolveAndroidSdk(IEnumerable<ProjectInfo>? knownProjects)
    {
        var envHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (!string.IsNullOrWhiteSpace(envHome) && Directory.Exists(envHome)) return envHome;

        var envRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot)) return envRoot;

        var localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
        if (Directory.Exists(localApp)) return localApp;

        var progFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Android", "android-sdk");
        if (Directory.Exists(progFiles)) return progFiles;

        if (knownProjects != null)
        {
            foreach (var kp in knownProjects)
            {
                var lp = Path.Combine(kp.Path, "local.properties");
                if (!File.Exists(lp)) continue;
                try
                {
                    var match = Regex.Match(File.ReadAllText(lp), @"^\s*sdk\.dir\s*=\s*(?<dir>.*)$", RegexOptions.Multiline);
                    if (match.Success)
                    {
                        var dir = match.Groups["dir"].Value.Trim().Replace("\\:", ":").Replace("\\\\", "\\");
                        if (Directory.Exists(dir)) return dir;
                    }
                }
                catch { }
            }
        }

        return null;
    }

    public static string? ResolveFlutterSdk(IEnumerable<ProjectInfo>? knownProjects)
    {
        var envFlutter = Environment.GetEnvironmentVariable("FLUTTER_ROOT");
        if (!string.IsNullOrWhiteSpace(envFlutter) && Directory.Exists(envFlutter)) return envFlutter;

        foreach (var candidate in new[] { @"C:\src\flutter", @"C:\flutter", @"D:\flutter", @"E:\flutter" })
        {
            if (Directory.Exists(candidate) && (File.Exists(Path.Combine(candidate, "bin", "flutter")) || File.Exists(Path.Combine(candidate, "bin", "flutter.bat"))))
                return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var part in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (File.Exists(Path.Combine(trimmed, "flutter")) || File.Exists(Path.Combine(trimmed, "flutter.bat")))
            {
                var parent = Path.GetDirectoryName(trimmed);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;
            }
        }

        if (knownProjects != null)
        {
            foreach (var kp in knownProjects)
            {
                var lp = Path.Combine(kp.Path, "local.properties");
                if (!File.Exists(lp)) continue;
                try
                {
                    var match = Regex.Match(File.ReadAllText(lp), @"^\s*flutter\.sdk\s*=\s*(?<dir>.*)$", RegexOptions.Multiline);
                    if (match.Success)
                    {
                        var dir = match.Groups["dir"].Value.Trim().Replace("\\:", ":").Replace("\\\\", "\\");
                        if (Directory.Exists(dir)) return dir;
                    }
                }
                catch { }
            }
        }

        return null;
    }

    static string EscapePropertiesValue(string val)
    {
        return val.Replace("\\", "\\\\").Replace(":", "\\:");
    }

    static void Backup(string file)
    {
        var bak = file + ".gradledoctor.bak";
        if (!File.Exists(bak)) File.Copy(file, bak);
    }
}

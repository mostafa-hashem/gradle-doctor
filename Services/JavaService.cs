using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GradleDoctor;

public static class JavaService
{
    static readonly Regex VersionRx = new(@"version\s+[""'](?<v>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<(int? Major, string Text)> DetectAsync(CancellationToken ct = default)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var release = System.IO.Path.Combine(javaHome, "release");
            try
            {
                if (File.Exists(release))
                {
                    foreach (var line in await File.ReadAllLinesAsync(release, ct))
                    {
                        if (!line.StartsWith("JAVA_VERSION", StringComparison.OrdinalIgnoreCase)) continue;
                        var value = line.Split('=', 2).LastOrDefault()?.Trim().Trim('"');
                        var major = ParseMajor(value);
                        if (major != null) return (major, $"Java {value} (from JAVA_HOME)");
                    }
                }
            }
            catch { }
        }

        try
        {
            var psi = new ProcessStartInfo("java.exe", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (null, "Java was not found on PATH.");
            var output = await proc.StandardError.ReadToEndAsync(ct) + await proc.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await proc.WaitForExitAsync(timeout.Token); } catch { try { proc.Kill(); } catch { } }
            var m = VersionRx.Match(output);
            var version = m.Success ? m.Groups["v"].Value : "";
            var major = ParseMajor(version);
            return (major, major != null ? $"Java {version}" : "Java version could not be detected.");
        }
        catch
        {
            return (null, "Java was not found. Install JDK 17 or newer for AGP 9.x.");
        }
    }

    static int? ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Trim().Trim('"').Split('.');
        if (!int.TryParse(parts[0], out var major)) return null;
        if (major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy)) return legacy;
        return major;
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GradleDoctor;

/// <summary>
/// Pre-downloads a Gradle distribution through a throw-away copy of a project's wrapper,
/// so the selected Gradle home receives the wrapper/dists entry without touching the project.
/// </summary>
public static class WrapperDownloader
{
    static readonly Regex ShaLineRx = new(@"^\s*distributionSha256Sum\s*=.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static async Task<(bool Ok, string Message)> EnsureAsync(ProjectInfo project, string version,
        string? gradleHome, Action<string> log, CancellationToken ct = default)
    {
        var gradlew = System.IO.Path.Combine(project.Path, "gradlew.bat");
        if (!File.Exists(gradlew))
            return (false, "gradlew.bat was not found in this project.");

        var wrapperDir = System.IO.Path.Combine(project.Path, "gradle", "wrapper");
        var wrapperJar = System.IO.Path.Combine(wrapperDir, "gradle-wrapper.jar");
        var wrapperProps = System.IO.Path.Combine(wrapperDir, "gradle-wrapper.properties");
        if (!File.Exists(wrapperJar) || !File.Exists(wrapperProps))
            return (false, "The project does not contain a complete Gradle wrapper.");

        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GradleDoctor", "download-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(System.IO.Path.Combine(temp, "gradle", "wrapper"));
            File.Copy(gradlew, System.IO.Path.Combine(temp, "gradlew.bat"));
            var gradlewUnix = System.IO.Path.Combine(project.Path, "gradlew");
            if (File.Exists(gradlewUnix)) File.Copy(gradlewUnix, System.IO.Path.Combine(temp, "gradlew"));
            foreach (var file in Directory.EnumerateFiles(wrapperDir))
                File.Copy(file, System.IO.Path.Combine(temp, "gradle", "wrapper", System.IO.Path.GetFileName(file)), true);

            var props = System.IO.Path.Combine(temp, "gradle", "wrapper", "gradle-wrapper.properties");
            var text = File.ReadAllText(props);
            text = Regex.Replace(text, @"^\s*distributionUrl\s*=.*$",
                $"distributionUrl=https\\://services.gradle.org/distributions/gradle-{version}-bin.zip",
                RegexOptions.Multiline);
            text = ShaLineRx.Replace(text, "").TrimEnd();

            log($"Resolving checksum for Gradle {version} ...");
            var checksum = await GradleVersionService.GetChecksumAsync(version, ct);
            if (!string.IsNullOrWhiteSpace(checksum))
                text += Environment.NewLine + $"distributionSha256Sum={checksum}" + Environment.NewLine;
            else
                text += Environment.NewLine;
            File.WriteAllText(props, text);

            var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"\"gradlew.bat\" --version\"")
            {
                WorkingDirectory = temp,
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

            using var proc = Process.Start(psi)!;
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) log("  | " + e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log("  ! " + e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return (false, ct.IsCancellationRequested ? "Download cancelled." : "The download timed out.");
            }

            return proc.ExitCode == 0
                ? (true, $"Gradle {version} is ready in the selected Gradle home.")
                : (false, $"Gradle wrapper exited with code {proc.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Download cancelled.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }
}

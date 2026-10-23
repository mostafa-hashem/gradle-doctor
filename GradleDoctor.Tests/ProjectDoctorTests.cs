using Xunit;

namespace GradleDoctor.Tests;

public class ProjectDoctorTests
{
    [Fact]
    public void CheckAndFixRestoresMissingWrapperJarAndScripts()
    {
        using var temp = new TempDirectory();
        var wrapperDir = Path.Combine(temp.Path, "gradle", "wrapper");
        Directory.CreateDirectory(wrapperDir);
        var propsFile = Path.Combine(wrapperDir, "gradle-wrapper.properties");
        File.WriteAllText(propsFile, "distributionUrl=https\\://services.gradle.org/distributions/gradle-9.8.2-bin.zip\n");

        var project = new ProjectInfo
        {
            Name = "TestApp",
            Path = temp.Path,
            PropsFile = propsFile,
            CurrentGradle = "9.8.2",
            Target = "9.8.2",
            Agp = "8.7.0",
            TargetAgp = "8.7.0"
        };

        var result = ProjectDoctor.CheckAndFix(project, "9.8.2", "dummy-checksum-123", null, null);

        Assert.True(File.Exists(Path.Combine(wrapperDir, "gradle-wrapper.jar")));
        Assert.True(new FileInfo(Path.Combine(wrapperDir, "gradle-wrapper.jar")).Length > 0);
        Assert.True(File.Exists(Path.Combine(temp.Path, "gradlew.bat")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "gradlew")));

        var updatedProps = File.ReadAllText(propsFile);
        Assert.Contains("distributionSha256Sum=dummy-checksum-123", updatedProps);
    }

    [Fact]
    public void CheckAndFixAppliesAgp9PatchEvenWhenVersionsMatch()
    {
        using var temp = new TempDirectory();
        var buildGradle = Path.Combine(temp.Path, "build.gradle");
        File.WriteAllText(buildGradle, "allprojects { repositories { google() } }\n");

        var project = new ProjectInfo
        {
            Name = "FlutterAgp9App",
            Path = temp.Path,
            PropsFile = Path.Combine(temp.Path, "gradle-wrapper.properties"),
            CurrentGradle = "9.8.2",
            Target = "9.8.2",
            Agp = "9.4.0",
            TargetAgp = "9.4.0",
            Type = "Flutter"
        };

        var result = ProjectDoctor.CheckAndFix(project, "9.8.2", null, null, null);

        var updatedBuild = File.ReadAllText(buildGradle);
        Assert.Contains("file_picker", updatedBuild);
        Assert.Contains("org.jetbrains.kotlin.android", updatedBuild);
    }

    [Fact]
    public void CheckAndFixUpdatesIncompatibleKotlinForAgp9()
    {
        using var temp = new TempDirectory();
        var settingsGradle = Path.Combine(temp.Path, "settings.gradle");
        File.WriteAllText(settingsGradle, "plugins { id \"org.jetbrains.kotlin.android\" version \"1.8.22\" apply false }\n");

        var project = new ProjectInfo
        {
            Name = "KotlinApp",
            Path = temp.Path,
            PropsFile = Path.Combine(temp.Path, "gradle-wrapper.properties"),
            CurrentGradle = "9.8.2",
            Target = "9.8.2",
            Agp = "9.4.0",
            TargetAgp = "9.4.0"
        };

        var result = ProjectDoctor.CheckAndFix(project, "9.8.2", null, null, null);

        var updatedSettings = File.ReadAllText(settingsGradle);
        Assert.Contains("2.0.21", updatedSettings);
        Assert.DoesNotContain("1.8.22", updatedSettings);
    }

    [Fact]
    public void InspectHealthReportsMissingFiles()
    {
        using var temp = new TempDirectory();
        var project = new ProjectInfo
        {
            Name = "EmptyApp",
            Path = temp.Path,
            PropsFile = Path.Combine(temp.Path, "gradle", "wrapper", "gradle-wrapper.properties"),
            CurrentGradle = "9.8.2",
            Target = "9.8.2"
        };

        var inspection = ProjectDoctor.InspectHealth(project, "9.8.2", null);

        Assert.NotEmpty(inspection.Issues);
        Assert.Contains(inspection.Issues, i => i.Contains("Missing gradle-wrapper.properties"));
        Assert.Contains(inspection.Issues, i => i.Contains("Missing gradlew.bat"));
    }

    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "GradleDoctorTests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

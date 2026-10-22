using Xunit;

namespace GradleDoctor.Tests;

public class ProjectScannerTests
{
    const string Gradle83 = "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.3-bin.zip\n";
    const string Gradle86 = "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.6-bin.zip\n";

    [Fact]
    public void ReadVersionExtractsDistributionVersion()
    {
        using var temp = new TempDirectory();
        var props = WriteWrapper(temp.Path, Gradle86);

        Assert.Equal("8.6", ProjectScanner.ReadVersion(props));
    }

    [Fact]
    public void UpdateWrapperWritesChecksumAndRestoreRevertsIt()
    {
        using var temp = new TempDirectory();
        var props = WriteWrapper(temp.Path, Gradle83);
        var project = new ProjectInfo { Path = temp.Path, PropsFile = props, CurrentGradle = "8.3" };
        var checksum = new string('a', 64);

        Assert.True(ProjectScanner.UpdateWrapper(project, "9.8.2", checksum));

        var updated = File.ReadAllText(props);
        Assert.Contains("gradle-9.8.2-bin.zip", updated);
        Assert.Contains("distributionSha256Sum=" + checksum, updated);
        Assert.Equal("9.8.2", project.CurrentGradle);
        Assert.True(File.Exists(props + ".gradledoctor.bak"));

        ProjectScanner.Restore(project);

        var restored = File.ReadAllText(props);
        Assert.Contains("gradle-8.3-bin.zip", restored);
        Assert.DoesNotContain("distributionSha256Sum", restored);
        Assert.Equal("8.3", project.CurrentGradle);
        Assert.False(File.Exists(props + ".gradledoctor.bak"));
    }

    [Fact]
    public void ScanFindsStandaloneAndroidProject()
    {
        using var temp = new TempDirectory();
        var app = Path.Combine(temp.Path, "DemoApp");
        WriteWrapper(app, Gradle86);

        var projects = ProjectScanner.Scan(temp.Path, new HashSet<string>());

        var project = Assert.Single(projects);
        Assert.Equal("DemoApp", project.Name);
        Assert.Equal("Android", project.Type);
        Assert.Equal("8.6", project.CurrentGradle);
    }

    [Fact]
    public void ScanHonorsExplicitSkipSet()
    {
        using var temp = new TempDirectory();
        var git = Path.Combine(temp.Path, ".git");
        WriteWrapper(git, Gradle86);

        Assert.Single(ProjectScanner.Scan(temp.Path, new HashSet<string>()));
        Assert.Empty(ProjectScanner.Scan(temp.Path, new HashSet<string> { ".git" }));
    }

    [Fact]
    public void ScanFindsFlutterProjectWithoutRecursingIntoPlatforms()
    {
        using var temp = new TempDirectory();
        var flutter = Path.Combine(temp.Path, "flutter_app");
        Directory.CreateDirectory(flutter);
        File.WriteAllText(Path.Combine(flutter, "pubspec.yaml"), "name: flutter_app\n");
        var android = Path.Combine(flutter, "android");
        WriteWrapper(android, Gradle86);
        Directory.CreateDirectory(Path.Combine(flutter, "windows"));

        var projects = ProjectScanner.Scan(temp.Path, new HashSet<string>());

        var project = Assert.Single(projects);
        Assert.Equal("flutter_app", project.Name);
        Assert.Equal("Flutter", project.Type);
        Assert.Equal(android, project.Path);
        Assert.Equal("8.6", project.CurrentGradle);
    }

    static string WriteWrapper(string root, string contents)
    {
        var wrapper = Path.Combine(root, "gradle", "wrapper");
        Directory.CreateDirectory(wrapper);
        var props = Path.Combine(wrapper, "gradle-wrapper.properties");
        File.WriteAllText(props, contents);
        return props;
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

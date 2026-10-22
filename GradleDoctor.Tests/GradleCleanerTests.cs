using Xunit;

namespace GradleDoctor.Tests;

public class GradleCleanerTests
{
    [Fact]
    public void InstalledVersionsOnlyIncludesDistributionFolders()
    {
        using var temp = new TempDirectory();
        CreateDistribution(temp.Path, "8.6");
        CreateDistribution(temp.Path, "9.0");
        Directory.CreateDirectory(Path.Combine(temp.Path, "wrapper", "dists", "not-a-distribution"));

        var versions = GradleCleaner.InstalledVersions(temp.Path);

        Assert.Equal(new[] { "8.6", "9.0" }, versions.OrderBy(v => v).ToArray());
    }

    [Fact]
    public void FindUnusedKeepsRequestedVersions()
    {
        using var temp = new TempDirectory();
        CreateDistribution(temp.Path, "8.6");
        CreateDistribution(temp.Path, "9.0");
        CreateDistribution(temp.Path, "9.1-rc-1");

        var unused = GradleCleaner.FindUnused(temp.Path, new HashSet<string> { "9.0" });

        Assert.Equal(new[] { "8.6", "9.1-rc-1" }, unused.Select(i => i.Version).ToArray());
        Assert.All(unused, item => Assert.Equal("dist", item.Kind));
    }

    static void CreateDistribution(string home, string version)
    {
        Directory.CreateDirectory(Path.Combine(home, "wrapper", "dists", $"gradle-{version}-bin"));
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

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

    [Fact]
    public void DeleteReportsProgressAndFreesSpace()
    {
        using var temp = new TempDirectory();
        CreateDistribution(temp.Path, "8.6");
        CreateDistribution(temp.Path, "9.0");

        var unused = GradleCleaner.FindUnused(temp.Path, new HashSet<string>());
        var progressReports = new List<GradleCleaner.CleanProgress>();

        var (freed, errors) = GradleCleaner.Delete(unused, useRecycleBin: false, onProgress: p => progressReports.Add(p));

        Assert.Empty(errors);
        Assert.Equal(2, progressReports.Count);
        Assert.Equal(1, progressReports[0].CurrentIndex);
        Assert.Equal(2, progressReports[0].TotalCount);
        Assert.Equal(2, progressReports[1].CurrentIndex);
        Assert.Equal(2, progressReports[1].TotalCount);
    }

    [Fact]
    public void DeleteSupportsCancellation()
    {
        using var temp = new TempDirectory();
        CreateDistribution(temp.Path, "8.6");
        CreateDistribution(temp.Path, "9.0");

        var unused = GradleCleaner.FindUnused(temp.Path, new HashSet<string>());
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var (freed, errors) = GradleCleaner.Delete(unused, useRecycleBin: false, ct: cts.Token);

        Assert.Equal(0, freed);
        Assert.Empty(errors);
        // Folders should still exist because cancellation stopped execution before any delete
        Assert.True(Directory.Exists(unused[0].Path));
    }

    [Fact]
    public void DeleteSupportsForceDelete()
    {
        using var temp = new TempDirectory();
        CreateDistribution(temp.Path, "8.6");
        CreateDistribution(temp.Path, "9.0");

        var unused = GradleCleaner.FindUnused(temp.Path, new HashSet<string>());
        var (freed, errors) = GradleCleaner.Delete(unused, useRecycleBin: false, forceDelete: true);

        Assert.Empty(errors);
        Assert.False(Directory.Exists(unused[0].Path));
        Assert.False(Directory.Exists(unused[1].Path));
    }

    [Fact]
    public void DeleteStopsImmediatelyOnCancellationDuringProgress()
    {
        using var temp = new TempDirectory();
        CreateDistribution(temp.Path, "8.6");
        CreateDistribution(temp.Path, "9.0");

        var unused = GradleCleaner.FindUnused(temp.Path, new HashSet<string>());
        using var cts = new CancellationTokenSource();

        var (freed, errors) = GradleCleaner.Delete(
            unused,
            useRecycleBin: false,
            onProgress: _ => cts.Cancel(),
            ct: cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(Directory.Exists(unused[1].Path));
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

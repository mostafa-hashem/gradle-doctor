using Xunit;

namespace GradleDoctor.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("9.6", "9.6.0", 0)]
    [InlineData("9.8.2", "9.7.0", 1)]
    [InlineData("8.14.6", "8.14.3", 1)]
    [InlineData("9.0-rc-1", "9.0", -1)]
    [InlineData("9.0.0-rc-1", "9.0.0", -1)]
    [InlineData("10.0", "9.9.9", 1)]
    [InlineData("", "1.0", -1)]
    [InlineData(null, "", 0)]
    public void CompareVersionsHandlesNumericAndPrereleaseParts(string? a, string? b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(ProjectScanner.CompareVersions(a, b)));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void FormatSizeUsesBinaryUnits(long bytes, string expected)
    {
        Assert.Equal(expected, Format.Size(bytes));
    }

    [Fact]
    public void VersionOptionMarksMissingDistributionsForDownload()
    {
        Assert.True(new VersionOption("9.8.2", false).WillDownload);
        Assert.False(new VersionOption("9.8.2", true).WillDownload);
        Assert.Equal("Not downloaded yet", new VersionOption("9.8.2", false).Hint);
    }
}

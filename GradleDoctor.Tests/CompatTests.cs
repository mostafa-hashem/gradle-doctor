using Xunit;

namespace GradleDoctor.Tests;

public class CompatTests
{
    static Compat Create() => new()
    {
        AgpMinGradle =
        {
            ["8.7"] = "8.9",
            ["9.0"] = "9.1.0"
        },
        AgpDefaultGradle =
        {
            ["9.0"] = "9.6.0"
        },
        GradleMinAgp =
        {
            ["9.0"] = "8.0"
        },
        AgpMinJdk =
        {
            ["9.0"] = 17
        }
    };

    [Fact]
    public void CheckReportsAgpThatNeedsNewerGradle()
    {
        var warning = Create().Check("9.0.0", "9.0.0");

        Assert.Contains("needs Gradle >= 9.1.0", warning);
    }

    [Fact]
    public void CheckAcceptsCompatiblePair()
    {
        Assert.Equal("", Create().Check("8.7.3", "9.0.0"));
    }

    [Fact]
    public void BestAgpUsesNewestCompatibleRule()
    {
        Assert.Equal("9.0.0", Create().BestAgpForGradle("9.6.0", "8.1.0"));
    }

    [Fact]
    public void LookupsNormalizeMajorMinor()
    {
        var compat = Create();

        Assert.Equal("9.6.0", compat.BestGradleForAgp("9.0.2"));
        Assert.Equal(17, compat.MinJdkForAgp("9.0.2"));
    }

    [Theory]
    [InlineData("8.13", "9.0", true)]
    [InlineData("9.0", "9.1", false)]
    [InlineData("9.0", "8.9", false)]
    public void IsMajorUpgradeDetectsNewMajor(string from, string to, bool expected)
    {
        Assert.Equal(expected, Compat.IsMajorUpgrade(from, to));
    }

    [Fact]
    public void SettingsHaveSafeDefaults()
    {
        var settings = new AppSettings();

        Assert.Equal("Light", settings.ThemeMode);
        Assert.Equal(15, settings.TimeoutMinutes);
        Assert.Equal("assembleDebug", settings.VerifyTask);
        Assert.False(settings.VerifyBuild);
        Assert.True(settings.UpdateAgp);
    }
}

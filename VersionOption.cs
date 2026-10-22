namespace GradleDoctor;

public sealed record VersionOption(string Version, bool IsInstalled)
{
    public bool WillDownload => !IsInstalled;
    public string Hint => IsInstalled ? "Already in the Gradle home" : "Not downloaded yet";
}

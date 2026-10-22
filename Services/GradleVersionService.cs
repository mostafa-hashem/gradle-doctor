using System.Net.Http;
using System.Text.Json;

namespace GradleDoctor;

public record GradleRelease(
    string Version,
    string DownloadUrl,
    string? ChecksumUrl,
    bool Final,
    DateTimeOffset? Released)
{
    public int Major => int.TryParse(Version.Split('.')[0], out var major) ? major : 0;
    public bool IsInstalled { get; set; }
}

public static class GradleVersionService
{
    public const string VersionsUrl = "https://services.gradle.org/versions/all";
    public const string DistributionsUrl = "https://services.gradle.org/distributions";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static List<GradleRelease>? _memory;
    static DateTime _memoryAt;

    public static string CacheFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GradleDoctor", "gradle-versions.json");

    public static async Task<List<GradleRelease>> GetVersionsAsync(bool force = false,
        CancellationToken ct = default)
    {
        if (!force && _memory != null && DateTime.UtcNow - _memoryAt < TimeSpan.FromHours(6))
            return _memory;

        if (!force && TryReadDiskCache(out var disk) && disk!.Count > 0)
        {
            _memory = disk;
            _memoryAt = DateTime.UtcNow;
            return disk;
        }

        try
        {
            var json = await Http.GetStringAsync(VersionsUrl, ct);
            var list = Parse(json);
            if (list.Count == 0) throw new InvalidOperationException("No stable Gradle versions returned.");
            _memory = list;
            _memoryAt = DateTime.UtcNow;
            SaveDiskCache(list);
            return list;
        }
        catch
        {
            if (TryReadDiskCache(out var stale) && stale!.Count > 0)
            {
                _memory = stale;
                _memoryAt = DateTime.UtcNow;
                return stale;
            }
            var fallback = Fallback();
            _memory = fallback;
            _memoryAt = DateTime.UtcNow;
            return fallback;
        }
    }

    public static async Task<string?> GetChecksumAsync(string version, string distType = "bin", CancellationToken ct = default)
    {
        try
        {
            distType = string.Equals(distType, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "bin";
            string url;
            if (distType == "bin")
            {
                var versions = await GetVersionsAsync(false, ct);
                var release = versions.FirstOrDefault(v => v.Version == version);
                url = release?.ChecksumUrl ?? $"{DistributionsUrl}/gradle-{version}-bin.zip.sha256";
            }
            else
            {
                url = $"{DistributionsUrl}/gradle-{version}-all.zip.sha256";
            }

            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            return text.Length >= 64 ? text[..64] : null;
        }
        catch { return null; }
    }

    public static Task<string?> GetChecksumAsync(string version, CancellationToken ct) =>
        GetChecksumAsync(version, "bin", ct);

    public static async Task<(bool Ok, string Message, string? File)> DownloadDistributionAsync(
        string version, string destinationDirectory, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            var fileName = $"gradle-{version}-bin.zip";
            var target = System.IO.Path.Combine(destinationDirectory, fileName);
            var url = $"{DistributionsUrl}/{fileName}";
            progress?.Report($"Downloading {fileName} ...");

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(target))
                await input.CopyToAsync(output, ct);

            var checksum = await GetChecksumAsync(version, ct);
            if (checksum != null)
            {
                progress?.Report("Verifying SHA-256 ...");
                var actual = await Sha256Async(target, ct);
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(target); } catch { }
                    return (false, "Checksum verification failed; the download was deleted.", null);
                }
            }

            return (true, $"Downloaded {fileName}.", target);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    public static async Task<string> Sha256Async(string file, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(file);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static List<GradleRelease> Parse(string json)
    {
        var result = new List<GradleRelease>();
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (GetBool(item, "snapshot") || GetBool(item, "nightly") || GetBool(item, "releaseNightly") ||
                GetBool(item, "broken")) continue;

            var version = GetString(item, "version");
            if (string.IsNullOrWhiteSpace(version)) continue;

            var rcFor = GetString(item, "rcFor");
            var milestoneFor = GetString(item, "milestoneFor");
            var final = item.TryGetProperty("final", out var f) ? f.GetBoolean() : string.IsNullOrEmpty(rcFor) && string.IsNullOrEmpty(milestoneFor);
            if (!final) continue;

            var download = GetString(item, "downloadUrl") ?? $"{DistributionsUrl}/gradle-{version}-bin.zip";
            var checksum = GetString(item, "checksumUrl") ?? $"{DistributionsUrl}/gradle-{version}-bin.zip.sha256";
            var released = TryDate(item, "released");

            result.Add(new GradleRelease(version, download, checksum, true, released));
        }

        return result
            .GroupBy(v => v.Version)
            .Select(g => g.First())
            .OrderByDescending(v => v.Version, Comparer<string>.Create(ProjectScanner.CompareVersions))
            .ToList();
    }

    static bool TryReadDiskCache(out List<GradleRelease>? list)
    {
        list = null;
        try
        {
            if (!File.Exists(CacheFile)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) > TimeSpan.FromDays(7)) return false;

            var text = File.ReadAllText(CacheFile);
            var cached = text.Contains("\"buildTime\"", StringComparison.Ordinal)
                ? Parse(text)
                : JsonSerializer.Deserialize<List<GradleRelease>>(text);

            cached = cached?.Where(v => !string.IsNullOrWhiteSpace(v.Version)).ToList();
            if (cached == null || cached.Count == 0) return false;
            list = cached; return true;
        }
        catch { return false; }
    }

    static void SaveDiskCache(List<GradleRelease> releases)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(releases));
        }
        catch { }
    }

    static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static DateTimeOffset? TryDate(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(p.GetString(), out var d) ? d : null;

    static List<GradleRelease> Fallback()
    {
        string[] versions =
        {
            "9.8.2", "9.7.0", "9.6.0", "9.5.0", "9.4.1", "9.3.1", "9.2.0", "9.1.0", "9.0.0",
            "8.14.6", "8.14.3", "8.13", "8.12", "8.11.1", "8.10.2", "8.9", "8.8", "8.7"
        };
        return versions.Select(v => new GradleRelease(v,
            $"{DistributionsUrl}/gradle-{v}-bin.zip",
            $"{DistributionsUrl}/gradle-{v}-bin.zip.sha256", true, null)).ToList();
    }
}

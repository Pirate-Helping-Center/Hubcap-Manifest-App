using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using HubcapManifestApp.Helpers;

namespace HubcapManifestApp.Services.CloudRedirect;

/// <summary>
/// Manages cloud_redirect.dll — downloads from GitHub releases on demand and
/// deploys to the Steam directory. Replaces the old embedded-resource approach.
/// The DLL is cached locally in AppData between runs.
/// </summary>
internal static class EmbeddedDll
{
    private const string GitHubRepo = "Selectively11/CloudRedirect";
    private const string DllFileName = "cloud_redirect.dll";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppConstants.AppDataFolderName, "cloud_redirect_cache");

    private static readonly string CachedDllPath = Path.Combine(CacheDir, DllFileName);
    private static readonly string CachedVersionPath = Path.Combine(CacheDir, "version.txt");
    private static readonly string CachedReleaseHashPath = Path.Combine(CacheDir, "release_hash.txt");

    private static string? _cachedHash;

    /// <summary>
    /// Shared HttpClient for all GitHub API and download requests.
    /// Using a static instance avoids socket exhaustion from creating new clients per call.
    /// </summary>
    private static readonly HttpClient SharedClient = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "HubcapManifestApp");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>
    /// Returns true if a cached copy of the DLL exists locally.
    /// </summary>
    public static bool IsAvailable()
    {
        return File.Exists(CachedDllPath);
    }

    /// <summary>
    /// Returns the SHA-256 hash of the locally cached DLL, or null if not cached.
    /// </summary>
    public static string? GetEmbeddedHash()
    {
        if (_cachedHash != null)
            return _cachedHash;

        if (!File.Exists(CachedDllPath))
            return null;

        try
        {
            using var fs = new FileStream(CachedDllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _cachedHash = ComputeSha256(fs);
            return _cachedHash;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks whether the deployed DLL matches the cached version.
    /// Returns null if either file doesn't exist, true if matching, false if different.
    /// </summary>
    public static bool? IsDeployedCurrent(string deployedPath)
    {
        if (!File.Exists(deployedPath))
            return null;

        var cachedHash = GetEmbeddedHash();
        if (cachedHash == null)
            return null;

        try
        {
            using var fs = new FileStream(deployedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var deployedHash = ComputeSha256(fs);
            return string.Equals(cachedHash, deployedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the latest cloud_redirect.dll from GitHub releases into the local cache.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public static async Task<string?> FetchLatestAsync(Action<string>? onProgress = null)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);

            onProgress?.Invoke("Checking for latest cloud_redirect.dll release...");

            var apiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            var json = await SharedClient.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "unknown";

            // Find the DLL asset
            string? downloadUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (string.Equals(asset.GetProperty("name").GetString(), DllFileName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (downloadUrl == null)
                return $"Release {tagName} does not contain {DllFileName}";

            onProgress?.Invoke($"Downloading {DllFileName} ({tagName})...");

            var dllBytes = await SharedClient.GetByteArrayAsync(downloadUrl);

            // Atomic write: tmp file then rename
            var tmpPath = CachedDllPath + ".tmp";
            await File.WriteAllBytesAsync(tmpPath, dllBytes);
            File.Move(tmpPath, CachedDllPath, overwrite: true);
            await File.WriteAllTextAsync(CachedVersionPath, tagName);

            // Cache the release hash so IsDeployedCurrentRemoteAsync can compare without re-downloading
            using (var ms = new MemoryStream(dllBytes))
                FileUtils.AtomicWriteAllText(CachedReleaseHashPath, $"{tagName}:{ComputeSha256(ms)}");

            // Invalidate cached hash
            _cachedHash = null;

            onProgress?.Invoke($"Downloaded {DllFileName} ({tagName}, {dllBytes.Length:N0} bytes)");
            return null;
        }
        catch (HttpRequestException ex)
        {
            return $"Download failed: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Download timed out";
        }
        catch (Exception ex)
        {
            return $"Failed to fetch cloud_redirect.dll: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns the cached release tag (e.g. "v1.0.3"), or null if not cached.
    /// </summary>
    public static string? GetCachedVersion()
    {
        try
        {
            return File.Exists(CachedVersionPath) ? File.ReadAllText(CachedVersionPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a newer release exists on GitHub compared to the local cache.
    /// Returns (hasUpdate, latestTag) or (false, null) on error.
    /// </summary>
    public static async Task<(bool hasUpdate, string? latestTag)> CheckForUpdateAsync()
    {
        try
        {
            var currentTag = GetCachedVersion();

            var apiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            var json = await SharedClient.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(json);
            var latestTag = doc.RootElement.GetProperty("tag_name").GetString();

            if (string.IsNullOrEmpty(currentTag) || !string.Equals(currentTag, latestTag, StringComparison.OrdinalIgnoreCase))
                return (true, latestTag);

            return (false, latestTag);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Checks whether the deployed DLL matches the latest GitHub release by comparing
    /// SHA-256 hashes. Uses a cached release hash when possible to avoid downloading
    /// the full DLL binary on every check. Falls back to downloading the asset only
    /// when the release tag has changed since the last fetch.
    /// Returns true if the deployed DLL matches the latest release, false if it differs
    /// or the deployed file doesn't exist, null on network/IO error.
    /// </summary>
    public static async Task<bool?> IsDeployedCurrentRemoteAsync(string deployedPath)
    {
        if (!File.Exists(deployedPath))
            return false;

        try
        {
            // Hash the deployed DLL
            string deployedHash;
            using (var fs = new FileStream(deployedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                deployedHash = ComputeSha256(fs);

            // Get the latest release tag from GitHub API
            var apiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            var json = await SharedClient.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var latestTag = root.GetProperty("tag_name").GetString() ?? "unknown";

            // Try to use the cached release hash if the tag matches
            string? cachedReleaseHash = ReadCachedReleaseHash(latestTag);
            if (cachedReleaseHash != null)
                return string.Equals(deployedHash, cachedReleaseHash, StringComparison.OrdinalIgnoreCase);

            // Tag changed or no cache — need to download the asset to compute its hash
            string? downloadUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (string.Equals(asset.GetProperty("name").GetString(), DllFileName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (downloadUrl == null)
                return null; // Can't determine — no asset in release

            var latestBytes = await SharedClient.GetByteArrayAsync(downloadUrl);
            string latestHash;
            using (var ms = new MemoryStream(latestBytes))
                latestHash = ComputeSha256(ms);

            // Cache the hash for future checks
            try
            {
                Directory.CreateDirectory(CacheDir);
                FileUtils.AtomicWriteAllText(CachedReleaseHashPath, $"{latestTag}:{latestHash}");
            }
            catch { }

            return string.Equals(deployedHash, latestHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the cached release hash file. Returns the hash if the cached tag matches
    /// the given tag, otherwise null (cache miss / stale).
    /// Format: "tag:hash"
    /// </summary>
    private static string? ReadCachedReleaseHash(string expectedTag)
    {
        try
        {
            if (!File.Exists(CachedReleaseHashPath)) return null;
            var content = File.ReadAllText(CachedReleaseHashPath).Trim();
            var colonIdx = content.IndexOf(':');
            if (colonIdx < 0) return null;
            var tag = content[..colonIdx];
            var hash = content[(colonIdx + 1)..];
            if (string.IsNullOrEmpty(hash)) return null;
            return string.Equals(tag, expectedTag, StringComparison.OrdinalIgnoreCase) ? hash : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically deploys the cached cloud_redirect.dll to the given destination path.
    /// If no cached copy exists, returns an error prompting the user to fetch first.
    /// </summary>
    /// <returns>null on success, or an error message string on failure.</returns>
    public static string? DeployTo(string destPath)
    {
        if (!File.Exists(CachedDllPath))
            return "cloud_redirect.dll not downloaded yet. Please deploy from the Cloud Redirect setup.";

        try
        {
            var tmpPath = destPath + ".tmp";
            File.Copy(CachedDllPath, tmpPath, overwrite: true);
            File.Move(tmpPath, destPath, overwrite: true);
            return null;
        }
        catch (IOException ex) when (ex.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
                                  || ex.HResult == unchecked((int)0x80070020)) // ERROR_SHARING_VIOLATION
        {
            return S.Get("EmbeddedDll_InUse");
        }
    }

    /// <summary>
    /// Fetches the DLL if not cached, then deploys to the destination.
    /// Combines FetchLatestAsync + DeployTo into a single async operation.
    /// </summary>
    public static async Task<string?> FetchAndDeployAsync(string destPath, Action<string>? onProgress = null)
    {
        if (!File.Exists(CachedDllPath))
        {
            var fetchErr = await FetchLatestAsync(onProgress);
            if (fetchErr != null)
                return fetchErr;
        }

        return DeployTo(destPath);
    }

    private static string ComputeSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}

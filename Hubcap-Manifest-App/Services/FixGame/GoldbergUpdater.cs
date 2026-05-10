using Newtonsoft.Json.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services.FixGame
{
    /// <summary>
    /// Downloads and caches Goldberg Steam Emulator DLLs from the gbe_fork GitHub releases.
    /// </summary>
    public class GoldbergUpdater
    {
        private const string ReleasesUrl = "https://api.github.com/repos/Detanup01/gbe_fork/releases/latest";
        private readonly FixGameCacheService _cache;
        private readonly LoggerService _logger;

        public event Action<string>? Log;

        public GoldbergUpdater(FixGameCacheService cache)
        {
            _cache = cache;
            _logger = new LoggerService("GoldbergUpdater");
        }

        /// <summary>
        /// Checks if Goldberg DLLs are cached and up to date. Downloads if needed.
        /// Returns true if DLLs are ready.
        /// </summary>
        public async Task<bool> EnsureGoldbergAsync(bool forceUpdate = false)
        {
            try
            {
                LogMsg("Checking Goldberg emulator...");

                // Get latest release info
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("HubcapManifestApp/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);

                var response = await client.GetStringAsync(ReleasesUrl);
                var release = JObject.Parse(response);
                var latestVersion = release["tag_name"]?.ToString() ?? "";

                var currentVersion = _cache.GetGoldbergVersion();

                if (!forceUpdate && _cache.HasGoldbergDlls() && currentVersion == latestVersion)
                {
                    LogMsg($"Goldberg {latestVersion} is up to date");
                    return true;
                }

                LogMsg($"Downloading Goldberg {latestVersion}...");

                // Find the emu-win-release asset
                var assets = release["assets"] as JArray;
                var winAsset = assets?.FirstOrDefault(a =>
                    a["name"]?.ToString().Contains("emu-win-release") == true);

                if (winAsset == null)
                {
                    LogMsg("Could not find emu-win-release asset in release");
                    return false;
                }

                var downloadUrl = winAsset["browser_download_url"]?.ToString();
                if (string.IsNullOrEmpty(downloadUrl))
                    return false;

                // Download the 7z archive
                var tempFile = Path.Combine(Path.GetTempPath(), "gbe_emu_win.7z");
                var tempExtract = Path.Combine(Path.GetTempPath(), "gbe_extract_" + Guid.NewGuid().ToString("N")[..8]);

                try
                {
                    LogMsg("Downloading archive...");
                    var data = await client.GetByteArrayAsync(downloadUrl);
                    await File.WriteAllBytesAsync(tempFile, data);
                    LogMsg($"Downloaded {data.Length / 1024 / 1024}MB");

                    // Extract using SharpSevenZip
                    LogMsg("Extracting...");
                    Directory.CreateDirectory(tempExtract);

                    // Use SharpSevenZip on a dedicated MTA thread
                    // (WPF's STA dispatcher causes COM marshalling failures)
                    var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    var dllPath = Path.Combine(exeDir, "x64", "7z.dll");
                    if (!File.Exists(dllPath))
                        dllPath = @"C:\Program Files\7-Zip\7z.dll";

                    var tcs = new TaskCompletionSource<bool>();
                    var extractThread = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            SharpSevenZip.SharpSevenZipBase.SetLibraryPath(dllPath);
                            using var ext = new SharpSevenZip.SharpSevenZipExtractor(tempFile);
                            ext.ExtractArchive(tempExtract);
                            tcs.SetResult(true);
                        }
                        catch (Exception ex)
                        {
                            tcs.SetException(ex);
                        }
                    });
                    extractThread.SetApartmentState(System.Threading.ApartmentState.MTA);
                    extractThread.Start();
                    await tcs.Task;

                    // Find steam_api.dll and steam_api64.dll in extracted files
                    var api32 = Directory.GetFiles(tempExtract, "steam_api.dll", SearchOption.AllDirectories)
                        .FirstOrDefault(f => !f.Contains("debug", StringComparison.OrdinalIgnoreCase)
                                          && !f.Contains("experimental", StringComparison.OrdinalIgnoreCase));
                    var api64 = Directory.GetFiles(tempExtract, "steam_api64.dll", SearchOption.AllDirectories)
                        .FirstOrDefault(f => !f.Contains("debug", StringComparison.OrdinalIgnoreCase)
                                          && !f.Contains("experimental", StringComparison.OrdinalIgnoreCase));

                    if (api32 == null || api64 == null)
                    {
                        // Fallback: just find any matching DLL
                        api32 = Directory.GetFiles(tempExtract, "steam_api.dll", SearchOption.AllDirectories).FirstOrDefault();
                        api64 = Directory.GetFiles(tempExtract, "steam_api64.dll", SearchOption.AllDirectories).FirstOrDefault();
                    }

                    if (api32 == null || api64 == null)
                    {
                        LogMsg("Could not find steam_api DLLs in archive");
                        return false;
                    }

                    // Copy to cache
                    File.Copy(api32, _cache.GetSteamApiDllPath(false), true);
                    File.Copy(api64, _cache.GetSteamApiDllPath(true), true);

                    // ColdClient files — these live in steamclient_experimental/
                    var client32 = Directory.GetFiles(tempExtract, "steamclient.dll", SearchOption.AllDirectories)
                        .FirstOrDefault(f => f.Contains("steamclient_experimental", StringComparison.OrdinalIgnoreCase));
                    var client64 = Directory.GetFiles(tempExtract, "steamclient64.dll", SearchOption.AllDirectories)
                        .FirstOrDefault(f => f.Contains("steamclient_experimental", StringComparison.OrdinalIgnoreCase));
                    var coldLoader = Directory.GetFiles(tempExtract, "steamclient_loader_x64.exe", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (coldLoader == null)
                        coldLoader = Directory.GetFiles(tempExtract, "steamclient_loader_x32.exe", SearchOption.AllDirectories)
                            .FirstOrDefault();

                    if (client32 != null)
                        File.Copy(client32, Path.Combine(_cache.GoldbergDir, "steamclient.dll"), true);
                    if (client64 != null)
                        File.Copy(client64, Path.Combine(_cache.GoldbergDir, "steamclient64.dll"), true);
                    if (coldLoader != null)
                    {
                        var loader32 = Directory.GetFiles(tempExtract, "steamclient_loader_x32.exe", SearchOption.AllDirectories).FirstOrDefault();
                        var loader64 = Directory.GetFiles(tempExtract, "steamclient_loader_x64.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (loader32 != null) File.Copy(loader32, Path.Combine(_cache.GoldbergDir, "steamclient_loader_x32.exe"), true);
                        if (loader64 != null) File.Copy(loader64, Path.Combine(_cache.GoldbergDir, "steamclient_loader_x64.exe"), true);
                    }

                    // Extra DLLs for ColdClient
                    var extra32 = Directory.GetFiles(tempExtract, "steamclient_extra_x32.dll", SearchOption.AllDirectories).FirstOrDefault();
                    var extra64 = Directory.GetFiles(tempExtract, "steamclient_extra_x64.dll", SearchOption.AllDirectories).FirstOrDefault();
                    if (extra32 != null) File.Copy(extra32, Path.Combine(_cache.GoldbergDir, "steamclient_extra_x32.dll"), true);
                    if (extra64 != null) File.Copy(extra64, Path.Combine(_cache.GoldbergDir, "steamclient_extra_x64.dll"), true);

                    var coldCount = (client32 != null ? 1 : 0) + (client64 != null ? 1 : 0) + (coldLoader != null ? 1 : 0);
                    LogMsg($"  ColdClient files: {coldCount}/3 found");

                    _cache.SetGoldbergVersion(latestVersion);

                    LogMsg($"Goldberg {latestVersion} ready");
                    return true;
                }
                finally
                {
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                    try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogMsg($"Goldberg update failed: {ex.Message}");
                _logger.Error($"GoldbergUpdater error: {ex}");
                return _cache.HasGoldbergDlls(); // Fall back to cached if available
            }
        }

        private void LogMsg(string msg)
        {
            _logger.Info(msg);
            Log?.Invoke(msg);
        }
    }
}

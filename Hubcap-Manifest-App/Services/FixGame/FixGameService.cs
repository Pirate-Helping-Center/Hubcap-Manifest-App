using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services.FixGame
{
    /// <summary>
    /// Orchestrates the full Fix Game pipeline:
    /// 1. Download/update Goldberg emulator DLLs
    /// 2. Generate Goldberg config (achievements, stats, DLC)
    /// 3. Unpack SteamStub DRM (if present)
    /// 4. Apply Goldberg emulator (replace steam_api DLLs)
    /// </summary>
    public class FixGameService
    {
        private readonly FixGameCacheService _cache;
        private readonly GoldbergUpdater _updater;
        private readonly GoldbergConfigGenerator _configGen;
        private readonly GoldbergApplier _applier;
        private readonly SteamStubUnpacker _unpacker;
        private readonly LoggerService _logger;

        public event Action<string>? Log;

        public FixGameService(FixGameCacheService cache)
        {
            _cache = cache;
            _updater = new GoldbergUpdater(cache);
            _configGen = new GoldbergConfigGenerator(cache);
            _applier = new GoldbergApplier(cache);
            _unpacker = new SteamStubUnpacker();
            _logger = new LoggerService("FixGame");

            // Wire log events
            _updater.Log += msg => LogMsg(msg);
            _configGen.Log += msg => LogMsg(msg);
            _applier.Log += msg => LogMsg(msg);
            _unpacker.Log += msg => LogMsg(msg);
        }

        /// <summary>
        /// Runs the full Fix Game pipeline on a game directory.
        /// </summary>
        public async Task<bool> FixGameAsync(
            string appId,
            string gameDir,
            string? steamWebApiKey,
            string language = "english",
            string steamId = "76561198001737783",
            string playerName = "Player",
            string emuMode = "regular")
        {
            try
            {
                LogMsg($"Fix Game started for App {appId}");
                LogMsg($"  Directory: {gameDir}");

                // Check for DRM/external account requirements via Steam Store API
                LogMsg("  Checking for DRM...");
                try
                {
                    using var httpClient = new System.Net.Http.HttpClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    var storeJson = await httpClient.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}");
                    var storeData = Newtonsoft.Json.Linq.JObject.Parse(storeJson);
                    var gameData = storeData[appId]?["data"];
                    var drmNotice = gameData?["drm_notice"]?.ToString() ?? "";
                    var extAccount = gameData?["ext_user_account_notice"]?.ToString() ?? "";

                    if (!string.IsNullOrEmpty(extAccount))
                    {
                        LogMsg($"  External account required: \"{extAccount}\"");
                        LogMsg("  Skipping — this game requires a third-party account.");
                        return false;
                    }

                    if (!string.IsNullOrEmpty(drmNotice))
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(drmNotice, @"(?i)denuvo"))
                        {
                            LogMsg($"  Denuvo detected: \"{drmNotice}\"");
                            LogMsg("  Skipping — Denuvo cannot be bypassed.");
                            return false;
                        }
                        else
                        {
                            LogMsg($"  DRM detected: \"{drmNotice}\"");
                            LogMsg("  Forcing ColdClient mode.");
                            emuMode = "coldclient";
                        }
                    }
                    else
                    {
                        LogMsg("  No DRM or external account requirements detected");
                    }
                }
                catch
                {
                    LogMsg("  Could not check DRM status (continuing anyway)");
                }

                // Step 0: Validate
                if (!Directory.Exists(gameDir))
                {
                    LogMsg("  Directory does not exist");
                    return false;
                }

                var (has32, has64, apiPaths) = GoldbergApplier.DetectSteamApi(gameDir);
                bool hasSteamApi = has32 || has64;

                if (hasSteamApi)
                    LogMsg($"  Detected: {(has32 ? "32-bit " : "")}{(has64 ? "64-bit" : "")} ({apiPaths.Length} DLL(s))");
                else if (emuMode == "coldclient")
                    LogMsg("  No steam_api DLLs found — ColdClient will handle Steam integration");
                else
                    LogMsg("  No steam_api DLLs found — will only attempt SteamStub removal");

                // Step 1: Ensure Goldberg DLLs are downloaded
                LogMsg("\n[Step 1/4] Checking Goldberg emulator...");
                var goldbergReady = await _updater.EnsureGoldbergAsync();
                if (!goldbergReady)
                {
                    LogMsg("  Cannot proceed without Goldberg DLLs");
                    return false;
                }

                // Step 2: Generate config
                LogMsg("\n[Step 2/4] Generating Goldberg configuration...");
                var configOk = await _configGen.GenerateAsync(appId, gameDir, steamWebApiKey, language, steamId, playerName);
                if (!configOk)
                    LogMsg("  Config generation had issues (continuing anyway)");

                // Step 3: Unpack SteamStub
                LogMsg("\n[Step 3/4] Scanning for SteamStub DRM...");
                var unpackedCount = await _unpacker.UnpackDirectory(gameDir);
                if (unpackedCount > 0)
                    LogMsg($"  Unpacked {unpackedCount} executable(s)");
                else
                    LogMsg("  No SteamStub DRM found (good)");

                // Step 4: Apply Goldberg
                bool success;
                string message;

                if (emuMode == "coldclient")
                {
                    LogMsg("\n[Step 4/4] Applying ColdClient emulator...");
                    (success, message) = ApplyColdClient(gameDir, appId);
                }
                else if (hasSteamApi)
                {
                    LogMsg("\n[Step 4/4] Applying Goldberg emulator...");
                    (success, message) = _applier.Apply(gameDir);
                }
                else
                {
                    LogMsg("\n[Step 4/4] Skipping Goldberg — no steam_api DLLs to replace");
                    success = true;
                    message = "SteamStub removal only (no DLL replacement needed)";
                }
                LogMsg($"  {message}");

                // Step 5: Generate launch batch file
                if (success)
                {
                    LogMsg("\n[Step 5] Generating launcher...");

                    // Check for pre-selected launch configs from the download flow
                    var savedLaunchJson = _cache.LoadPicsJson(appId + "_launch");
                    if (!string.IsNullOrEmpty(savedLaunchJson))
                    {
                        try
                        {
                            var savedConfigs = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SavedLaunchConfig>>(savedLaunchJson);
                            if (savedConfigs != null && savedConfigs.Count > 0)
                            {
                                foreach (var lc in savedConfigs)
                                {
                                    var batName = savedConfigs.Count == 1
                                        ? "Launch.bat"
                                        : $"Launch - {SanitizeFileName(lc.Description ?? lc.Executable)}.bat";

                                    var exePath = lc.Executable.Replace("/", "\\");
                                    var workDir = string.IsNullOrEmpty(lc.WorkingDir)
                                        ? "\"%~dp0\""
                                        : $"\"%~dp0{lc.WorkingDir.Replace("/", "\\")}\"";

                                    var args = lc.Arguments ?? "";
                                    var batContent = $"@echo off\ncd /d {workDir}\nstart \"\" \"{exePath}\" {args}\n";
                                    File.WriteAllText(Path.Combine(gameDir, batName), batContent);
                                    LogMsg($"  Created {batName} -> {lc.Executable} {args}".TrimEnd());
                                }
                            }
                            else
                            {
                                LogMsg("  No launch arguments needed — run the game exe directly");
                            }
                        }
                        catch
                        {
                            LogMsg("  No launch arguments needed — run the game exe directly");
                        }
                    }
                    else
                    {
                        LogMsg("  No launch arguments needed — run the game exe directly");
                    }
                }

                LogMsg($"\n{'─', 0}────────────────────────────────────────────────────");
                if (success)
                    LogMsg("Fix Game complete! The game should now be playable.");
                else
                    LogMsg("Fix Game finished with errors. Check the log above.");

                return success;
            }
            catch (Exception ex)
            {
                LogMsg($"\nFix Game failed: {ex.Message}");
                _logger.Error($"FixGame error: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Restores a game to its original state (reverses Fix Game).
        /// </summary>
        public (bool success, string message) RestoreGame(string gameDir)
        {
            LogMsg($"Restoring game at {gameDir}...");
            var result = _applier.Restore(gameDir);
            LogMsg(result.message);
            return result;
        }

        /// <summary>
        /// Caches app info from a lua file for future Fix Game use.
        /// Called automatically when DepotDownloader downloads a game.
        /// </summary>
        public void CacheFromLua(string luaContent, string appId)
        {
            var info = FixGameCacheService.ParseLuaForCache(luaContent, appId);
            _cache.SaveAppInfo(info);
            _logger.Info($"Cached app info for {appId}: {info.GameName}, {info.DlcList.Count} DLC(s)");
        }

        private (bool success, string message) ApplyColdClient(string gameDir, string appId)
        {
            try
            {
                var goldbergDir = _cache.GoldbergDir;
                var client32 = Path.Combine(goldbergDir, "steamclient.dll");
                var client64 = Path.Combine(goldbergDir, "steamclient64.dll");
                var loader32 = Path.Combine(goldbergDir, "steamclient_loader_x32.exe");
                var loader64 = Path.Combine(goldbergDir, "steamclient_loader_x64.exe");

                if (!File.Exists(client32) || !File.Exists(client64))
                {
                    return (false, "ColdClient DLLs not found. Re-download Goldberg from Settings.");
                }

                if (!File.Exists(loader32) && !File.Exists(loader64))
                {
                    return (false, "steamclient_loader not found. Re-download Goldberg from Settings.");
                }

                // Pick loader: check steam_api DLLs first, fallback to scanning game exe PE header
                var (has32, has64, _) = GoldbergApplier.DetectSteamApi(gameDir);
                bool use64;
                if (has32 || has64)
                {
                    use64 = has64;
                }
                else
                {
                    // No steam_api DLLs — scan first exe in game dir for architecture
                    var firstExe = Directory.GetFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(f => !Path.GetFileName(f).Equals("steamclient_loader_x32.exe", StringComparison.OrdinalIgnoreCase)
                                          && !Path.GetFileName(f).Equals("steamclient_loader_x64.exe", StringComparison.OrdinalIgnoreCase));
                    use64 = firstExe != null && IsExe64Bit(firstExe);
                }
                var useLoader = use64 && File.Exists(loader64) ? loader64 : loader32;
                var loaderName = Path.GetFileName(useLoader);

                // Copy steamclient DLLs and loader to game directory
                File.Copy(client32, Path.Combine(gameDir, "steamclient.dll"), true);
                LogMsg($"  Copied steamclient.dll");
                File.Copy(client64, Path.Combine(gameDir, "steamclient64.dll"), true);
                LogMsg($"  Copied steamclient64.dll");
                File.Copy(useLoader, Path.Combine(gameDir, loaderName), true);
                LogMsg($"  Copied {loaderName}");

                // Copy extra DLLs for injection
                var extraDir = Path.Combine(gameDir, "extra_dlls");
                Directory.CreateDirectory(extraDir);
                var extra32 = Path.Combine(goldbergDir, "steamclient_extra_x32.dll");
                var extra64 = Path.Combine(goldbergDir, "steamclient_extra_x64.dll");
                if (File.Exists(extra32)) { File.Copy(extra32, Path.Combine(extraDir, "steamclient_extra_x32.dll"), true); LogMsg($"  Copied extra_dlls/steamclient_extra_x32.dll"); }
                if (File.Exists(extra64)) { File.Copy(extra64, Path.Combine(extraDir, "steamclient_extra_x64.dll"), true); LogMsg($"  Copied extra_dlls/steamclient_extra_x64.dll"); }

                // Find the game exe
                var exeFiles = Directory.GetFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        var name = Path.GetFileName(f).ToLower();
                        return name != "steamclient_loader.exe" &&
                               !name.Contains("unins") && !name.Contains("setup") &&
                               !name.Contains("redist") && !name.Contains("vcredist") &&
                               !name.Contains("dxsetup") && !name.Contains("crashreport");
                    })
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .ToArray();

                var mainExe = exeFiles.FirstOrDefault();
                if (mainExe == null)
                    return (false, "No game executable found for ColdClient config");

                var exeName = Path.GetFileName(mainExe);

                // Check for saved launch args
                var launchArgs = "";
                var savedLaunch = _cache.LoadPicsJson(appId + "_launch");
                if (!string.IsNullOrEmpty(savedLaunch))
                {
                    try
                    {
                        var configs = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SavedLaunchConfig>>(savedLaunch);
                        var first = configs?.FirstOrDefault();
                        if (first != null)
                        {
                            if (!string.IsNullOrEmpty(first.Executable))
                                exeName = first.Executable.Replace("/", "\\");
                            launchArgs = first.Arguments ?? "";
                        }
                    }
                    catch { }
                }

                // Generate ColdClientLoader.ini
                var ini = "[SteamClient]\n" +
                          $"Exe={exeName}\n" +
                          $"ExeRunDir=.\n" +
                          $"ExeCommandLine={launchArgs}\n" +
                          $"AppId={appId}\n" +
                          $"SteamClientDll=steamclient.dll\n" +
                          $"SteamClient64Dll=steamclient64.dll\n" +
                          $"DllsToInjectFolder=extra_dlls\n";

                File.WriteAllText(Path.Combine(gameDir, "ColdClientLoader.ini"), ini);
                LogMsg($"  Created ColdClientLoader.ini -> {exeName}");

                // steam_settings should be next to steamclient DLLs (already there from config gen)
                LogMsg($"  ColdClient mode applied — launch via steamclient_loader.exe");

                return (true, "ColdClient applied — use steamclient_loader.exe to launch");
            }
            catch (Exception ex)
            {
                LogMsg($"  ColdClient apply failed: {ex.Message}");
                return (false, $"ColdClient failed: {ex.Message}");
            }
        }

        private void GenerateLaunchBat(string gameDir, string appId)
        {
            try
            {
                // Try to get launch configs from PICS via DepotDownloader's session
                var launchConfigs = GetLaunchConfigs(uint.Parse(appId));

                if (launchConfigs.Count > 0)
                {
                    // Filter to Windows launch configs
                    var windowsLaunches = launchConfigs
                        .Where(lc => string.IsNullOrEmpty(lc.OsType) || lc.OsType == "windows")
                        .ToList();

                    if (windowsLaunches.Count > 0)
                    {
                        List<LaunchConfig> selectedLaunches;

                        if (windowsLaunches.Count == 1)
                        {
                            selectedLaunches = windowsLaunches;
                        }
                        else
                        {
                            // Ask user which launch options to create
                            selectedLaunches = new List<LaunchConfig>();
                            var message = "Multiple launch options found. Select which to create:\n\n";
                            for (int i = 0; i < windowsLaunches.Count; i++)
                            {
                                var lc = windowsLaunches[i];
                                var desc = lc.Description ?? lc.Executable;
                                var args = string.IsNullOrEmpty(lc.Arguments) ? "" : $" {lc.Arguments}";
                                message += $"{i + 1}. {desc}{args}\n";
                            }
                            message += "\nEnter numbers separated by commas (e.g. 1,3), or 'all':";

                            string? input = null;
                            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                            {
                                var dialog = new Helpers.InputDialog("Launch Options", message);
                                if (dialog.ShowDialog() == true)
                                    input = dialog.Result;
                            });

                            if (string.IsNullOrEmpty(input) || input.Trim().ToLower() == "all")
                            {
                                selectedLaunches = windowsLaunches;
                            }
                            else
                            {
                                foreach (var part in input.Split(','))
                                {
                                    if (int.TryParse(part.Trim(), out var idx) && idx >= 1 && idx <= windowsLaunches.Count)
                                        selectedLaunches.Add(windowsLaunches[idx - 1]);
                                }
                                if (selectedLaunches.Count == 0)
                                    selectedLaunches = windowsLaunches;
                            }
                        }

                        foreach (var lc in selectedLaunches)
                        {
                            var batName = selectedLaunches.Count == 1
                                ? "Launch.bat"
                                : $"Launch - {SanitizeFileName(lc.Description ?? lc.Executable)}.bat";

                            var exePath = lc.Executable.Replace("/", "\\");
                            var workDir = string.IsNullOrEmpty(lc.WorkingDir)
                                ? "\"%~dp0\""
                                : $"\"%~dp0{lc.WorkingDir.Replace("/", "\\")}\"";

                            var args = lc.Arguments ?? "";

                            var batContent = $"@echo off\n" +
                                             $"cd /d {workDir}\n" +
                                             $"start \"\" \"{exePath}\" {args}\n";

                            var batPath = Path.Combine(gameDir, batName);
                            File.WriteAllText(batPath, batContent);
                            LogMsg($"  Created {batName} -> {lc.Executable} {args}".TrimEnd());
                        }
                        return;
                    }
                }

                // Fallback: scan for exe files
                var exeFiles = Directory.GetFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        var name = Path.GetFileName(f).ToLower();
                        return !name.Contains("unins") && !name.Contains("setup") &&
                               !name.Contains("redist") && !name.Contains("vcredist") &&
                               !name.Contains("dxsetup") && !name.Contains("dotnet") &&
                               !name.Contains("crashreport");
                    })
                    .ToArray();

                if (exeFiles.Length == 0)
                {
                    LogMsg("  No executables found for launcher");
                    return;
                }

                var mainExe = exeFiles.OrderByDescending(f => new FileInfo(f).Length).First();
                var exeName = Path.GetFileName(mainExe);

                var fallbackBat = $"@echo off\ncd /d \"%~dp0\"\nstart \"\" \"{exeName}\"\n";
                File.WriteAllText(Path.Combine(gameDir, "Launch.bat"), fallbackBat);
                LogMsg($"  Created Launch.bat -> {exeName}");

                if (exeFiles.Length > 1)
                {
                    LogMsg($"  Other executables found:");
                    foreach (var exe in exeFiles.Where(e => e != mainExe))
                        LogMsg($"    {Path.GetFileName(exe)}");
                }
            }
            catch (Exception ex)
            {
                LogMsg($"  Launcher generation failed: {ex.Message}");
            }
        }

        private static List<LaunchConfig> GetLaunchConfigs(uint appId)
        {
            var results = new List<LaunchConfig>();
            try
            {
                // Try to read from cached PICS JSON (saved during download)
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var picsPath = Path.Combine(appData, "HubcapManifestApp", "fix_game_cache", $"{appId}_pics.json");

                if (!File.Exists(picsPath))
                {
                    // Try to get from DepotDownloader's live session
                    // Live PICS not available without cached data — fall back to exe scan
                    return results;
                }

                // Parse from cached PICS JSON
                var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(picsPath));
                var launchSection = json["config"]?["launch"];
                if (launchSection == null) return results;

                foreach (var prop in launchSection.Children<Newtonsoft.Json.Linq.JProperty>())
                {
                    var entry = prop.Value;
                    var exe = entry["executable"]?.ToString();
                    if (string.IsNullOrEmpty(exe)) continue;

                    results.Add(new LaunchConfig
                    {
                        Executable = exe,
                        Arguments = entry["arguments"]?.ToString(),
                        Description = entry["description"]?.ToString()
                                      ?? entry["description_loc"]?["english"]?.ToString(),
                        WorkingDir = entry["workingdir"]?.ToString(),
                        OsType = entry["config"]?["oslist"]?.ToString(),
                        Type = entry["type"]?.ToString()
                    });
                }
            }
            catch { }
            return results;
        }

        private static bool IsExe64Bit(string exePath)
        {
            try
            {
                using var fs = File.OpenRead(exePath);
                using var br = new System.IO.BinaryReader(fs);
                if (br.ReadUInt16() != 0x5A4D) return false; // MZ
                fs.Seek(0x3C, SeekOrigin.Begin);
                var peOffset = br.ReadInt32();
                fs.Seek(peOffset + 4, SeekOrigin.Begin);
                var machine = br.ReadUInt16();
                return machine == 0x8664; // IMAGE_FILE_MACHINE_AMD64
            }
            catch { return false; }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Game";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        }

        private class SavedLaunchConfig
        {
            [Newtonsoft.Json.JsonProperty("executable")]
            public string Executable { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("arguments")]
            public string? Arguments { get; set; }
            [Newtonsoft.Json.JsonProperty("description")]
            public string? Description { get; set; }
            [Newtonsoft.Json.JsonProperty("workingdir")]
            public string? WorkingDir { get; set; }
            [Newtonsoft.Json.JsonProperty("ostype")]
            public string? OsType { get; set; }
        }

        private class LaunchConfig
        {
            public string Executable { get; set; } = "";
            public string? Arguments { get; set; }
            public string? Description { get; set; }
            public string? WorkingDir { get; set; }
            public string? OsType { get; set; }
            public string? Type { get; set; }
        }

        private void LogMsg(string msg)
        {
            _logger.Info(msg);
            Log?.Invoke(msg);
        }
    }
}

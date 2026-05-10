using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HubcapManifestApp.Services
{
    /// <summary>
    /// Holds the prepared data from Phase 1 (lua parsing, Steam metadata, depot keys).
    /// The caller shows UI dialogs using this data, then passes user selections back
    /// to Phase 2 to build and start the download.
    /// </summary>
    public class DepotInstallPrepResult
    {
        public required string AppId { get; init; }
        public required string LuaContent { get; init; }
        public required Dictionary<string, string> ParsedDepotKeys { get; init; }
        public required SteamCmdDepotData SteamCmdData { get; init; }
        public required List<string> AvailableLanguages { get; init; }
        public required Dictionary<string, string> DepotOwnerMap { get; init; }
        public required Dictionary<string, string> DepotNameMap { get; init; }
        public required List<(string AppId, string Token)> PicsTokens { get; init; }

        /// <summary>Launch configurations fetched from PICS before Steam disconnect. Empty if fetch failed.</summary>
        public List<LaunchConfigInfo> LaunchConfigs { get; init; } = new();

        /// <summary>Cloud save directories fetched from PICS before Steam disconnect. Empty if fetch failed.</summary>
        public List<CloudSaveDir> CloudSaveDirs { get; init; } = new();

        /// <summary>Cloud save overrides fetched from PICS before Steam disconnect. Empty if fetch failed.</summary>
        public List<CloudSaveOverride> CloudSaveOverrides { get; init; } = new();
    }

    /// <summary>
    /// Result from Phase 2 — everything the caller needs after the user selects
    /// language + depots.
    /// </summary>
    public class DepotDownloadStartResult
    {
        public required string GameName { get; init; }
        public required string OutputPath { get; init; }
    }

    /// <summary>
    /// Orchestrates the DepotDownloader install flow shared between DownloadsViewModel
    /// and LuaInstallerViewModel. Extracts ~250 lines of duplicated logic into a
    /// single reusable service.
    /// 
    /// Flow:
    ///   1. Call PrepareDepotDataAsync — parses lua, connects to Steam, fetches metadata
    ///   2. Caller shows LanguageSelectionDialog / DepotSelectionDialog using returned data
    ///   3. Call BuildDepotsForSelection — builds the DepotInfo list for the selection dialog
    ///   4. Call StartDownloadAsync — builds final depot list and kicks off the download
    /// </summary>
    public class DepotInstallOrchestrator
    {
        private readonly DownloadService _downloadService;
        private readonly SettingsService _settingsService;
        private readonly LoggerService _logger;
        private readonly LuaParser _luaParser;
        private readonly DepotFilterService _depotFilterService;
        private readonly SteamKitAppInfoService _steamKitAppInfoService;

        public DepotInstallOrchestrator(
            DownloadService downloadService,
            SettingsService settingsService,
            LoggerService logger,
            LuaParser luaParser,
            DepotFilterService depotFilterService,
            SteamKitAppInfoService steamKitAppInfoService)
        {
            _downloadService = downloadService;
            _settingsService = settingsService;
            _logger = logger;
            _luaParser = luaParser;
            _depotFilterService = depotFilterService;
            _steamKitAppInfoService = steamKitAppInfoService;
        }

        /// <summary>
        /// Phase 1: Extract lua content from the zip, parse PICS tokens and depot keys,
        /// connect to Steam, fetch depot metadata (including DLC), and return a prep
        /// result containing everything the caller needs to show UI dialogs.
        /// </summary>
        /// <param name="zipFilePath">Path to the .zip file containing lua + manifests.</param>
        /// <param name="appId">The Steam app ID (derived from the zip filename).</param>
        /// <param name="onStatus">Callback for status message updates.</param>
        /// <returns>A prep result, or null if a fatal error occurred (error already reported via onError).</returns>
        /// <param name="onError">Callback for error messages (caller decides MessageBox vs notification).</param>
        public async Task<DepotInstallPrepResult?> PrepareDepotDataAsync(
            string zipFilePath,
            string appId,
            Action<string> onStatus,
            Action<string> onError)
        {
            _logger.Info("=== Starting DepotDownloader Info Gathering Phase ===");
            _logger.Info($"App ID: {appId}");
            _logger.Info($"Zip file: {System.IO.Path.GetFileName(zipFilePath)}");

            onStatus("Extracting depot information from lua file...");
            var luaContent = _downloadService.ExtractLuaContentFromZip(zipFilePath, appId);
            _logger.Info($"Lua content extracted successfully ({luaContent.Length} characters)");

            // Parse PICS tokens
            var picsTokens = _luaParser.ParseTokens(luaContent);
            if (picsTokens.Count > 0)
            {
                var mainToken = picsTokens.FirstOrDefault(t => t.AppId == appId);
                if (mainToken != default && ulong.TryParse(mainToken.Token, out ulong tokenValue))
                {
                    DepotDownloader.TokenCFG.useAppToken = true;
                    DepotDownloader.TokenCFG.appToken = tokenValue;
                    _logger.Info($"Set PICS token for app {appId}: {tokenValue}");
                }

                foreach (var tokenEntry in picsTokens)
                {
                    if (uint.TryParse(tokenEntry.AppId, out var tAppId) && ulong.TryParse(tokenEntry.Token, out var tValue))
                    {
                        DepotDownloader.TokenCFG.AppTokens[tAppId] = tValue;
                    }
                }
            }

            // Extract depot keys
            var parsedDepotKeys = _depotFilterService.ExtractDepotKeysFromLua(luaContent);
            if (parsedDepotKeys.Count == 0)
            {
                onError("No depot keys found in the lua file. Cannot proceed with download.");
                return null;
            }

            onStatus($"Found {parsedDepotKeys.Count} depot keys. Fetching depot metadata...");

            // Connect to Steam and fetch depot info
            onStatus("Connecting to Steam...");
            var initResult = await _steamKitAppInfoService.InitializeAsync();
            if (!initResult)
            {
                onError("Failed to connect to Steam. Please check your internet connection and try again.");
                return null;
            }

            onStatus("Fetching depot metadata from Steam...");
            var steamCmdData = await _steamKitAppInfoService.GetDepotInfoAsync(appId);
            if (steamCmdData == null)
            {
                onError($"Failed to fetch depot information for app {appId} from Steam.");
                _steamKitAppInfoService.Disconnect();
                return null;
            }

            // Parse DLC depots and build owner map
            var allLuaDepots = _luaParser.ParseDepotsFromLua(luaContent, appId);
            var dlcAppIds = allLuaDepots
                .Where(d => !string.IsNullOrEmpty(d.DlcAppId) && d.DlcAppId != appId && parsedDepotKeys.ContainsKey(d.DepotId))
                .Select(d => d.DlcAppId!)
                .Distinct()
                .ToList();

            var depotOwnerMap = new Dictionary<string, string>();
            foreach (var luaDepot in allLuaDepots)
            {
                if (!string.IsNullOrEmpty(luaDepot.DlcAppId) && luaDepot.DlcAppId != appId)
                    depotOwnerMap[luaDepot.DepotId] = luaDepot.DlcAppId;
                else
                    depotOwnerMap[luaDepot.DepotId] = appId;
            }

            // Fetch and merge DLC depot metadata
            if (dlcAppIds.Count > 0)
            {
                _logger.Info($"Found {dlcAppIds.Count} DLC app(s) with inner depots: {string.Join(", ", dlcAppIds)}");
                onStatus($"Fetching metadata for {dlcAppIds.Count} DLC app(s)...");

                foreach (var dlcAppIdStr in dlcAppIds)
                {
                    ulong? dlcToken = null;
                    var dlcTokenEntry = picsTokens.FirstOrDefault(t => t.AppId == dlcAppIdStr);
                    if (dlcTokenEntry != default && ulong.TryParse(dlcTokenEntry.Token, out ulong tv))
                        dlcToken = tv;

                    _logger.Info($"Fetching depot info for DLC app {dlcAppIdStr}...");
                    var dlcData = await _steamKitAppInfoService.GetDepotInfoAsync(dlcAppIdStr, dlcToken);

                    if (dlcData?.Data != null && dlcData.Data.TryGetValue(dlcAppIdStr, out var dlcAppData) && dlcAppData.Depots != null)
                    {
                        int mergedCount = 0;
                        foreach (var depotKvp in dlcAppData.Depots)
                        {
                            if (!steamCmdData.Data[appId].Depots.ContainsKey(depotKvp.Key))
                            {
                                var depotData = depotKvp.Value;
                                if (string.IsNullOrEmpty(depotData.DlcAppId))
                                    depotData.DlcAppId = dlcAppIdStr;
                                steamCmdData.Data[appId].Depots[depotKvp.Key] = depotData;
                                mergedCount++;
                            }
                        }
                        _logger.Info($"Merged {mergedCount} depot(s) from DLC app {dlcAppIdStr}");
                    }
                    else
                    {
                        _logger.Warning($"Could not fetch depot info for DLC app {dlcAppIdStr}");
                    }
                }
            }

            // Fetch launch configs and cloud save dirs before disconnecting (needs active session)
            List<LaunchConfigInfo> launchConfigs = new();
            List<CloudSaveDir> cloudSaveDirs = new();
            List<CloudSaveOverride> cloudSaveOverrides = new();
            try
            {
                _logger.Info("Fetching launch configs from PICS...");
                launchConfigs = await _steamKitAppInfoService.GetLaunchConfigsAsync(appId);
                _logger.Info($"Found {launchConfigs.Count} launch config(s)");
            }
            catch (Exception lcEx)
            {
                _logger.Warning($"Could not fetch launch configs: {lcEx.Message}");
            }

            try
            {
                _logger.Info("Fetching cloud save dirs from PICS...");
                (cloudSaveDirs, cloudSaveOverrides) = await _steamKitAppInfoService.GetCloudSaveDirsAsync(appId);
                _logger.Info($"Found {cloudSaveDirs.Count} cloud save dir(s), {cloudSaveOverrides.Count} override(s)");
            }
            catch (Exception csEx)
            {
                _logger.Warning($"Could not fetch cloud save dirs: {csEx.Message}");
            }

            _steamKitAppInfoService.Disconnect();

            // Get available languages
            var availableLanguages = _depotFilterService.GetAvailableLanguages(steamCmdData, appId, parsedDepotKeys);
            if (availableLanguages.Count == 0)
            {
                availableLanguages = new List<string> { "all" };
            }

            // Build depot name map
            var depotNameMap = allLuaDepots.ToDictionary(d => d.DepotId, d => d.Name);

            return new DepotInstallPrepResult
            {
                AppId = appId,
                LuaContent = luaContent,
                ParsedDepotKeys = parsedDepotKeys,
                SteamCmdData = steamCmdData,
                AvailableLanguages = availableLanguages,
                DepotOwnerMap = depotOwnerMap,
                DepotNameMap = depotNameMap,
                PicsTokens = picsTokens,
                LaunchConfigs = launchConfigs,
                CloudSaveDirs = cloudSaveDirs,
                CloudSaveOverrides = cloudSaveOverrides
            };
        }

        /// <summary>
        /// Builds the list of <see cref="DepotInfo"/> for the depot selection dialog,
        /// after the user has chosen a language and the depot list has been filtered.
        /// </summary>
        public List<DepotInfo> BuildDepotsForSelection(
            DepotInstallPrepResult prep,
            List<string> filteredDepotIds)
        {
            var depotsForSelection = new List<DepotInfo>();
            foreach (var depotIdStr in filteredDepotIds)
            {
                if (uint.TryParse(depotIdStr, out _) && prep.ParsedDepotKeys.ContainsKey(depotIdStr))
                {
                    string depotName = prep.DepotNameMap.TryGetValue(depotIdStr, out var name)
                        ? name
                        : $"Depot {depotIdStr}";
                    string depotLanguage = "";
                    long depotSize = 0;

                    if (prep.SteamCmdData.Data.TryGetValue(prep.AppId, out var appData) &&
                        appData.Depots?.TryGetValue(depotIdStr, out var depotData) == true)
                    {
                        depotLanguage = depotData.Config?.Language ?? "";
                        if (depotData.Manifests?.TryGetValue("public", out var manifestData) == true)
                        {
                            depotSize = manifestData.Size;
                        }
                    }

                    depotsForSelection.Add(new DepotInfo
                    {
                        DepotId = depotIdStr,
                        Name = depotName,
                        Size = depotSize,
                        Language = depotLanguage
                    });
                }
            }

            return depotsForSelection;
        }

        /// <summary>
        /// Filters depots by the selected language. Handles both the "All (Skip Filter)"
        /// case and specific language filtering with OS filter support.
        /// </summary>
        public List<string> FilterDepotsByLanguage(
            DepotInstallPrepResult prep,
            string selectedLanguage)
        {
            var settings = _settingsService.LoadSettings();

            if (selectedLanguage == AppConstants.AllLanguagesLabel)
            {
                var allDepotIds = prep.ParsedDepotKeys.Keys.ToList();
                if (!settings.DisableDepotOsFilter)
                {
                    allDepotIds = _depotFilterService.FilterNonWindowsDepots(
                        prep.SteamCmdData, allDepotIds, prep.AppId);
                }
                return allDepotIds;
            }

            return _depotFilterService.GetDepotsForLanguage(
                prep.SteamCmdData,
                prep.ParsedDepotKeys,
                selectedLanguage,
                prep.AppId,
                skipOsFilter: settings.DisableDepotOsFilter);
        }

        /// <summary>
        /// Phase 2: After dialog selections, builds the final depot list and starts
        /// the download via DepotDownloaderWrapperService.
        /// </summary>
        /// <returns>The game name resolved from Steam metadata, or null if the download could not start.</returns>
        public async Task<DepotDownloadStartResult?> StartDownloadAsync(
            DepotInstallPrepResult prep,
            string zipFilePath,
            List<string> selectedDepotIds,
            Action<string> onStatus,
            Action<string> onError)
        {
            var settings = _settingsService.LoadSettings();
            var outputPath = settings.DepotDownloaderOutputPath;

            if (string.IsNullOrEmpty(outputPath))
            {
                onError("DepotDownloader output path not configured. Please set it in Settings.");
                return null;
            }

            onStatus("Extracting manifest files...");
            var manifestFiles = _downloadService.ExtractManifestFilesFromZip(zipFilePath, prep.AppId);

            var depotsToDownload = new List<(uint depotId, string depotKey, string? manifestFile, uint ownerAppId)>();
            foreach (var selectedDepotId in selectedDepotIds)
            {
                if (uint.TryParse(selectedDepotId, out var depotId) &&
                    prep.ParsedDepotKeys.TryGetValue(selectedDepotId, out var depotKey))
                {
                    string? manifestFilePath = manifestFiles.TryGetValue(selectedDepotId, out var manifestPath)
                        ? manifestPath
                        : null;

                    // Owner resolution:
                    //  - If the base game's PICS data lists this depot with a `dlcappid` flag,
                    //    the depot is hosted under the parent app (the DLC entry itself is often
                    //    `activationonlydlc` with no depots section), so we MUST call DownloadAppAsync
                    //    with the parent (main) appId or SteamKit's appinfo.depots[depotId] lookup
                    //    returns null and throws NullReferenceException.
                    //  - Otherwise fall back to the lua-derived owner map (sub-DLCs that ship their
                    //    own depots), and finally to the main appId.
                    string resolvedOwnerId = prep.AppId;
                    bool hasDlcAppIdFlag = false;
                    if (prep.SteamCmdData.Data.TryGetValue(prep.AppId, out var baseAppData) &&
                        baseAppData.Depots?.TryGetValue(selectedDepotId, out var baseDepotData) == true &&
                        !string.IsNullOrEmpty(baseDepotData.DlcAppId))
                    {
                        hasDlcAppIdFlag = true;
                        _logger.Info($"Depot {selectedDepotId} has dlcappid={baseDepotData.DlcAppId} in base game PICS — using parent app {prep.AppId} as owner");
                    }
                    if (!hasDlcAppIdFlag && prep.DepotOwnerMap.TryGetValue(selectedDepotId, out var ownerId))
                    {
                        resolvedOwnerId = ownerId;
                    }
                    uint ownerAppId = uint.Parse(resolvedOwnerId);
                    depotsToDownload.Add((depotId, depotKey, manifestFilePath, ownerAppId));
                }
            }

            string gameName = prep.AppId;
            if (prep.SteamCmdData.Data.TryGetValue(prep.AppId, out var gameData))
            {
                gameName = gameData.Common?.Name ?? prep.AppId;
            }

            // Pull the primary manifest id from the lua so the install marker can record it
            ulong primaryManifestId = 0;
            try
            {
                primaryManifestId = _luaParser.GetPrimaryManifestId(prep.LuaContent, prep.AppId);
            }
            catch { /* best effort */ }

            onStatus("Starting download...");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _downloadService.DownloadViaDepotDownloaderAsync(
                        prep.AppId,
                        gameName,
                        depotsToDownload,
                        outputPath,
                        settings.VerifyFilesAfterDownload,
                        settings.MaxConcurrentDownloads,
                        primaryManifestId
                    );
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Background download failed for {prep.AppId}: {ex.Message}");
                }
            });

            return new DepotDownloadStartResult
            {
                GameName = gameName,
                OutputPath = outputPath
            };
        }
    }
}

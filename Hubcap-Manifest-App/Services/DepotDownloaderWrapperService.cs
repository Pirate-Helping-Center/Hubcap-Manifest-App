using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DepotDownloader;
using HubcapManifestApp.Helpers;
using SteamKit2;

namespace HubcapManifestApp.Services
{
    public class DownloadProgressEventArgs : EventArgs
    {
        public string JobId { get; set; } = "";
        public double Progress { get; set; }
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public double Speed { get; set; }
        public int ProcessedFiles { get; set; }
        public int TotalFiles { get; set; }
        public string CurrentFile { get; set; } = "";
        public long CurrentFileSize { get; set; }
    }

    public class DownloadStatusEventArgs : EventArgs
    {
        public string JobId { get; set; } = "";
        public string Status { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class DownloadCompletedEventArgs : EventArgs
    {
        public string JobId { get; set; } = "";
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    public class LogMessageEventArgs : EventArgs
    {
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class DepotDownloaderWrapperService
    {
        private readonly LoggerService _logger;
        private readonly NotificationService _notificationService;

        // Events
        public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
        public event EventHandler<DownloadStatusEventArgs>? StatusChanged;
        public event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;
        public event EventHandler<LogMessageEventArgs>? LogMessage;

        private bool _isInitialized = false;
        private static bool _configInitialized = false;
        private static readonly SemaphoreSlim _initLock = new(1, 1);

        public DepotDownloaderWrapperService(LoggerService logger, NotificationService notificationService)
        {
            _logger = logger;
            _notificationService = notificationService;
        }

        public async Task<bool> InitializeAsync(string username = "", string password = "")
        {
            if (_isInitialized)
                return true;

            await _initLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (_isInitialized)
                    return true;

                LogInfo("Initializing Steam session...");

                try
                {
                    // Initialize account settings store and config only once
                    if (!_configInitialized)
                    {
                    var appDataPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        AppConstants.AppDataFolderName,
                        "DepotDownloader"
                    );

                    System.IO.Directory.CreateDirectory(appDataPath);

                    // Initialize stores
                    AccountSettingsStore.LoadFromFile(System.IO.Path.Combine(appDataPath, "account.config"));
                    DepotConfigStore.LoadFromFile(System.IO.Path.Combine(appDataPath, "depot.config"));

                    // Initialize ContentDownloader config with defaults
                    ContentDownloader.Config.MaxDownloads = 8;
                    ContentDownloader.Config.CellID = 0;
                    ContentDownloader.Config.DownloadManifestOnly = false;
                    ContentDownloader.Config.RememberPassword = false;
                    ContentDownloader.Config.UseQrCode = false;
                    ContentDownloader.Config.SkipAppConfirmation = true;
                    ContentDownloader.Config.UsingFileList = false;
                    ContentDownloader.Config.FilesToDownload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ContentDownloader.Config.FilesToDownloadRegex = new List<System.Text.RegularExpressions.Regex>();

                    _configInitialized = true;
                }

                // Use anonymous login if no credentials provided
                var result = await Task.Run(() => ContentDownloader.InitializeSteam3(
                    string.IsNullOrEmpty(username) ? null : username,
                    string.IsNullOrEmpty(password) ? null : password
                ));

                if (result)
                {
                    _isInitialized = true;
                    LogInfo("Steam session initialized successfully");
                }
                else
                {
                    LogInfo("Failed to initialize Steam session");
                }

                return result;
            }
            catch (Exception ex)
            {
                LogInfo($"Error initializing Steam: {ex.Message}");
                _logger.Error($"DepotDownloader initialization error: {ex.Message}");
                return false;
            }
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<bool> DownloadDepotsAsync(
            uint appId,
            List<(uint depotId, string depotKey, string? manifestFile, uint ownerAppId)> depots,
            string targetDirectory,
            bool verifyFiles = true,
            int maxDownloads = 8,
            bool isUgc = false,
            CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                LogInfo("Steam session not initialized. Please login first.");
                return false;
            }

            try
            {
                LogInfo($"Starting download for App ID: {appId}");
                StatusChanged?.Invoke(this, new DownloadStatusEventArgs
                {
                    JobId = appId.ToString(),
                    Status = "Downloading",
                    Message = $"Preparing to download App {appId}"
                });

                // Configure the download
                ContentDownloader.Config.InstallDirectory = targetDirectory;
                ContentDownloader.Config.VerifyAll = verifyFiles;
                ContentDownloader.Config.MaxDownloads = maxDownloads;
                ContentDownloader.Config.DownloadAllPlatforms = false;
                ContentDownloader.Config.DownloadAllArchs = false;
                ContentDownloader.Config.DownloadAllLanguages = false;

                // Set cancellation token (dispose any previous one)
                ContentDownloader.ExternalCancellationTokenSource?.Dispose();
                ContentDownloader.ExternalCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // Track depot progress
                int totalDepots = depots.Count;
                int currentDepotIndex = 0;

                // Subscribe to progress events
                EventHandler<DepotDownloader.DownloadProgressEventArgs>? progressHandler = null;
                progressHandler = (sender, e) =>
                {
                    // Calculate overall progress: (completed depots + current depot progress) / total depots
                    double overallProgress = ((currentDepotIndex + (e.Progress / 100.0)) / totalDepots) * 100.0;

                    // Clamp progress to 100% to prevent overflow when depot completes
                    overallProgress = Math.Min(overallProgress, 100.0);

                    ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                    {
                        JobId = appId.ToString(),
                        Progress = overallProgress,
                        DownloadedBytes = (long)e.DownloadedBytes,
                        TotalBytes = (long)e.TotalBytes,
                        ProcessedFiles = e.ProcessedFiles,
                        TotalFiles = e.TotalFiles,
                        CurrentFile = e.CurrentFile ?? ""
                    });
                };

                ContentDownloader.ProgressUpdated += progressHandler;

                try
                {
                    // === Owner resolution: PRIMARY = live PICS, fallback = VM-supplied (SteamCMD cache → lua → main) ===
                    // Build a map of depotId -> resolved ownerAppId by querying live PICS for the main app.
                    // For each depot we read appinfo["depots"][depotId] and check:
                    //   - dlcappid present  → owner = main appId (parent-hosted DLC depot, e.g. activationonlydlc)
                    //   - depotfromapp set  → owner = that app (shared/proxy depot, e.g. Steamworks Common Redists)
                    //   - else              → owner = main appId
                    // If PICS is unreachable / returns nothing for a depot, we fall back to the
                    // ownerAppId already supplied by the VM (which has its own SteamCMD cache fallback).
                    var liveOwnerOverride = new Dictionary<uint, uint>();
                    KeyValue? liveDepotsKv = null;
                    LogInfo($"[OwnerResolve] Requesting live PICS data for app {appId}...");
                    var picsOk = await ContentDownloader.EnsureAppInfoAsync(appId);
                    if (picsOk)
                    {
                        liveDepotsKv = ContentDownloader.GetSteam3AppSection(appId, EAppInfoSection.Depots);
                        if (liveDepotsKv != null && liveDepotsKv != KeyValue.Invalid)
                        {
                            LogInfo($"[OwnerResolve] PICS depots section available for {appId} ({liveDepotsKv.Children.Count} entries)");
                        }
                        else
                        {
                            LogInfo($"[OwnerResolve] PICS returned no depots section for {appId} — will use VM fallback for all depots");
                        }
                    }
                    else
                    {
                        LogInfo($"[OwnerResolve] PICS request failed for {appId} — will use VM fallback for all depots");
                    }

                    foreach (var (depotId, _, _, vmFallbackOwner) in depots)
                    {
                        uint resolved = vmFallbackOwner;
                        string source = "vm-fallback";

                        if (liveDepotsKv != null && liveDepotsKv != KeyValue.Invalid)
                        {
                            var d = liveDepotsKv[depotId.ToString()];
                            if (d != null && d != KeyValue.Invalid)
                            {
                                if (d["dlcappid"] != KeyValue.Invalid)
                                {
                                    // Parent-hosted DLC depot — must use main app (depot is defined under it)
                                    resolved = appId;
                                    source = $"pics:dlcappid={d["dlcappid"].AsUnsignedInteger()}";
                                }
                                else if (d["depotfromapp"] != KeyValue.Invalid)
                                {
                                    // Shared/proxy depot — owner is the source app
                                    resolved = d["depotfromapp"].AsUnsignedInteger();
                                    source = $"pics:depotfromapp";
                                }
                                else
                                {
                                    resolved = appId;
                                    source = "pics:plain";
                                }
                            }
                            else
                            {
                                source = "vm-fallback (depot not in PICS)";
                            }
                        }

                        liveOwnerOverride[depotId] = resolved;
                        if (resolved != vmFallbackOwner)
                        {
                            LogInfo($"[OwnerResolve] Depot {depotId}: {vmFallbackOwner} → {resolved} (source: {source})");
                        }
                        else
                        {
                            LogInfo($"[OwnerResolve] Depot {depotId}: {resolved} (source: {source})");
                        }
                    }

                    // Load depot keys
                    foreach (var (depotId, depotKey, manifestFile, ownerAppId) in depots)
                    {
                        if (!string.IsNullOrEmpty(depotKey))
                        {
                            DepotKeyStore.AddKey($"{depotId};{depotKey}");
                            LogInfo($"Loaded depot key for {depotId}");
                        }
                        else
                        {
                            LogInfo($"WARNING: depot {depotId} has no depot key!");
                        }
                    }

                    // Download each depot
                    foreach (var (depotId, depotKey, manifestFile, vmOwnerAppId) in depots)
                    {
                        // Use the live-PICS-resolved owner if we have one, else the VM fallback
                        uint ownerAppId = liveOwnerOverride.TryGetValue(depotId, out var picsOwner) ? picsOwner : vmOwnerAppId;
                        LogInfo($"Starting depot {depotId} download ({currentDepotIndex + 1}/{totalDepots}) using app {ownerAppId}...");

                        // If a manifest file is provided, try to extract the manifest ID from
                        // the filename pattern: {depotId}_{manifestId}.manifest
                        ulong depotManifestId = ContentDownloader.INVALID_MANIFEST_ID;
                        if (!string.IsNullOrEmpty(manifestFile))
                        {
                            var mfName = System.IO.Path.GetFileNameWithoutExtension(manifestFile);
                            var parts = mfName.Split('_');
                            if (parts.Length >= 2 && ulong.TryParse(parts[^1], out var parsedMid))
                                depotManifestId = parsedMid;
                        }

                        var depotList = new List<(uint depotId, ulong manifestId)>
                        {
                            (depotId, depotManifestId)
                        };

                        // If manifest file is provided, use it
                        if (!string.IsNullOrEmpty(manifestFile) && System.IO.File.Exists(manifestFile))
                        {
                            ContentDownloader.Config.UseManifestFile = true;
                            ContentDownloader.Config.ManifestFile = manifestFile;
                        }

                        try
                        {
                            await ContentDownloader.DownloadAppAsync(
                                ownerAppId,
                                depotList,
                                ContentDownloader.DEFAULT_BRANCH,
                                null, // os
                                null, // arch
                                null, // language
                                false,
                                isUgc
                            );
                        }
                        catch (Exception innerEx)
                        {
                            LogInfo($"DownloadAppAsync threw: {innerEx.GetType().FullName}: {innerEx.Message}");
                            if (innerEx.InnerException != null)
                            {
                                LogInfo($"  Inner: {innerEx.InnerException.GetType().FullName}: {innerEx.InnerException.Message}");
                            }
                            throw;
                        }

                        // Reset manifest file config
                        ContentDownloader.Config.UseManifestFile = false;
                        ContentDownloader.Config.ManifestFile = null;

                        // Increment depot index for next depot
                        currentDepotIndex++;
                        LogInfo($"Completed depot {depotId} ({currentDepotIndex}/{totalDepots})");
                    }

                    LogInfo($"Download completed for App ID: {appId}");

                    // Show completion notification
                    _notificationService.ShowSuccess(
                        $"Successfully downloaded {totalDepots} depot{(totalDepots != 1 ? "s" : "")} for App {appId}",
                        "Download Complete"
                    );

                    DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs
                    {
                        JobId = appId.ToString(),
                        Success = true,
                        Message = "Download completed successfully"
                    });

                    return true;
                }
                finally
                {
                    ContentDownloader.ProgressUpdated -= progressHandler;
                }
            }
            catch (Exception ex)
            {
                LogInfo($"Download failed: {ex.Message}");
                _logger.Error($"DepotDownloader download error: {ex.Message}");

                DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs
                {
                    JobId = appId.ToString(),
                    Success = false,
                    Message = $"Download failed: {ex.Message}"
                });

                return false;
            }
        }

        private void LogInfo(string message)
        {
            _logger.Info($"[DepotDownloader] {message}");
            LogMessage?.Invoke(this, new LogMessageEventArgs { Message = message });
        }

        public void Shutdown()
        {
            if (_isInitialized)
            {
                ContentDownloader.ShutdownSteam3();
                _isInitialized = false;
                LogInfo("Steam session shutdown");
            }
        }
    }
}

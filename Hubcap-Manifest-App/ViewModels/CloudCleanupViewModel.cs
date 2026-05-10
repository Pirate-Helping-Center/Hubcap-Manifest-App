using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HubcapManifestApp.Services;
using HubcapManifestApp.Services.CloudRedirect;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;


namespace HubcapManifestApp.ViewModels
{
    public partial class CloudCleanupViewModel : ObservableObject
    {
        private readonly NotificationService _notificationService;
        private readonly CacheService _cacheService;

        [ObservableProperty] private string _logText = string.Empty;
        [ObservableProperty] private bool _isScanning;
        [ObservableProperty] private string _statusMessage = "Scan for SteamTools cloud contamination";
        [ObservableProperty] private ObservableCollection<ScanResultItem> _scanResults = new();
        [ObservableProperty] private ObservableCollection<BackupItem> _backups = new();
        [ObservableProperty] private bool _hasScanResults;
        [ObservableProperty] private bool _hasBackups;
        [ObservableProperty] private bool _showingScanResults;
        [ObservableProperty] private bool _showingBackups;
        [ObservableProperty] private int _totalPollutedFiles;
        [ObservableProperty] private string _totalPollutedSize = "";

        // Held between scan and clean so we can pass results to CleanFiles
        private List<AppScanResult>? _lastScanResults;
        private string? _steamPath;

        public CloudCleanupViewModel(NotificationService notificationService, CacheService cacheService)
        {
            _notificationService = notificationService;
            _cacheService = cacheService;
        }

        private void Log(string msg)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => LogText += msg + "\n");
        }

        [RelayCommand]
        private async Task RunScan()
        {
            if (IsScanning) return;
            IsScanning = true;
            LogText = string.Empty;
            ScanResults.Clear();
            HasScanResults = false;
            ShowingScanResults = false;
            ShowingBackups = false;
            _lastScanResults = null;
            TotalPollutedFiles = 0;
            TotalPollutedSize = "";
            StatusMessage = "Scanning...";

            try
            {
                _steamPath = SteamDetector.FindSteamPath();
                if (_steamPath == null) { Log("Steam not found."); StatusMessage = "Steam not found"; return; }

                Log("Scanning for SteamTools cloud contamination...");
                Log("Parsing appinfo.vdf and building cross-app remotecache map...");

                var results = await Task.Run(() =>
                {
                    var cleanup = new CloudCleanup(_steamPath, msg => Log(msg));
                    return cleanup.ScanApps();
                });

                _lastScanResults = results;

                if (results.Count == 0)
                {
                    Log("\nNo namespace apps found to scan.");
                    StatusMessage = "No namespace apps found";
                    return;
                }

                Log($"\nScan returned {results.Count} app(s). Resolving names...");

                // Resolve app names + header URLs
                var appIds = results.Select(r => r.AppId).Distinct().ToList();
                Dictionary<uint, StoreAppInfo> nameMap = new();
                try
                {
                    nameMap = await SteamStoreClient.Shared.GetAppInfoAsync(appIds);
                }
                catch (Exception ex) { Log($"Name resolution failed: {ex.Message}"); }

                Log($"Building UI results...");

                int totalPolluted = 0;
                long totalPollutedBytes = 0;
                int totalFileItems = 0;

                // Sort: polluted apps first (by pollution count desc), then by total bytes desc
                var sorted = results
                    .OrderByDescending(r => r.PollutedCount)
                    .ThenByDescending(r => r.TotalBytes)
                    .ToList();

                foreach (var result in sorted)
                {
                    var name = nameMap.TryGetValue(result.AppId, out var info) && !string.IsNullOrEmpty(info.Name)
                        ? info.Name : $"App {result.AppId}";
                    var headerUrl = nameMap.TryGetValue(result.AppId, out var storeInfo) && SteamStoreClient.IsValidSteamCdnUrl(storeInfo.HeaderUrl)
                        ? storeInfo.HeaderUrl : null;

                    totalPolluted += result.PollutedCount;
                    totalPollutedBytes += result.PollutedBytes;

                    var item = new ScanResultItem(this)
                    {
                        AppId = result.AppId,
                        AccountId = result.AccountId,
                        RemoteDir = result.RemoteDir,
                        Name = name,
                        HeaderImageUrl = headerUrl,
                        FileCount = result.Files.Count,
                        TotalSize = FileUtils.FormatSize(result.TotalBytes),
                        PollutedCount = result.PollutedCount,
                        PollutedSize = FileUtils.FormatSize(result.PollutedBytes),
                        LegitimateCount = result.LegitimateCount,
                        UnknownCount = result.UnknownCount,
                        HasPollution = result.PollutedCount > 0,
                    };

                    // Build file items grouped into 3 sections
                    var suspectFiles = result.Files
                        .Where(f => f.Classification != FileClassification.Legitimate && f.Classification != FileClassification.Unknown)
                        .OrderBy(f => f.RelativePath)
                        .Select(f => new ClassifiedFileItem(f, hasCheckbox: true, isChecked: true))
                        .ToList();

                    var unknownFiles = result.Files
                        .Where(f => f.Classification == FileClassification.Unknown)
                        .OrderBy(f => f.RelativePath)
                        .Select(f => new ClassifiedFileItem(f, hasCheckbox: true, isChecked: false))
                        .ToList();

                    var legitimateFiles = result.Files
                        .Where(f => f.Classification == FileClassification.Legitimate)
                        .OrderBy(f => f.RelativePath)
                        .Select(f => new ClassifiedFileItem(f, hasCheckbox: false, isChecked: false))
                        .ToList();

                    item.SuspectFiles = new ObservableCollection<ClassifiedFileItem>(suspectFiles);
                    item.UnknownFiles = new ObservableCollection<ClassifiedFileItem>(unknownFiles);
                    item.LegitimateFiles = new ObservableCollection<ClassifiedFileItem>(legitimateFiles);
                    item.HasSuspectFiles = suspectFiles.Count > 0;
                    item.HasUnknownFiles = unknownFiles.Count > 0;
                    item.HasLegitimateFiles = legitimateFiles.Count > 0;
                    item.HasCheckboxes = suspectFiles.Count > 0 || unknownFiles.Count > 0;
                    item.SuspectHeader = $"Suspect Files ({suspectFiles.Count})";
                    item.UnknownHeader = $"Unknown Files ({unknownFiles.Count})";
                    item.LegitimateHeader = $"Legitimate Files ({legitimateFiles.Count})";

                    totalFileItems += suspectFiles.Count + unknownFiles.Count + legitimateFiles.Count;
                    ScanResults.Add(item);

                    if (result.PollutedCount > 0)
                    {
                        Log($"\n  {name} (App {result.AppId}):");
                        Log($"    {result.PollutedCount} polluted file(s) ({FileUtils.FormatSize(result.PollutedBytes)})");
                        Log($"    {result.LegitimateCount} legitimate, {result.UnknownCount} unknown");
                    }
                }

                TotalPollutedFiles = totalPolluted;
                TotalPollutedSize = FileUtils.FormatSize(totalPollutedBytes);
                HasScanResults = ScanResults.Count > 0;
                ShowingScanResults = HasScanResults;
                ShowingBackups = false;

                Log($"Created {ScanResults.Count} app cards with {totalFileItems} total file items.");

                if (totalPolluted == 0)
                {
                    Log($"\nScan complete. No contamination detected across {results.Count} app(s).");
                    StatusMessage = $"Clean — no contamination found in {results.Count} app(s)";
                }
                else
                {
                    int appsWithIssues = sorted.Count(r => r.PollutedCount > 0);
                    Log($"\nScan complete. {totalPolluted} polluted file(s) across {appsWithIssues} app(s).");
                    StatusMessage = $"Found {totalPolluted} polluted file(s) in {appsWithIssues} app(s) — expand apps to review";
                }

                // Load header images in background
                _ = LoadHeaderImagesAsync();
            }
            catch (Exception ex)
            {
                Log($"Scan error: {ex.Message}");
                StatusMessage = "Scan failed";
            }
            finally
            {
                IsScanning = false;
            }
        }

        private async Task LoadHeaderImagesAsync()
        {
            foreach (var item in ScanResults.ToList())
            {
                try
                {
                    var appIdStr = item.AppId.ToString();
                    var cachedPath = await _cacheService.GetSteamGameIconAsync(
                        appIdStr,
                        localSteamIconPath: null,
                        cdnIconUrl: item.HeaderImageUrl ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appIdStr}/header.jpg");

                    if (!string.IsNullOrEmpty(cachedPath))
                        item.CachedIconPath = cachedPath;
                }
                catch { }
            }
        }

        /// <summary>
        /// Called by ScanResultItem when user clicks "Clean Selected" on a specific app.
        /// Only cleans the checked files for that one app.
        /// </summary>
        internal async Task CleanAppFiles(ScanResultItem appItem)
        {
            if (_steamPath == null || IsScanning) return;

            // Collect checked files from suspect + unknown sections
            var checkedFiles = appItem.SuspectFiles
                .Concat(appItem.UnknownFiles)
                .Where(f => f.IsChecked)
                .ToList();

            if (checkedFiles.Count == 0)
            {
                _notificationService.ShowWarning("No files selected. Check the files you want to clean.");
                return;
            }

            long totalBytes = checkedFiles.Sum(f => f.SizeBytes);
            var confirmed = await Dialog.ConfirmDangerAsync(
                "Clean Selected Files",
                $"Move {checkedFiles.Count} file(s) ({FileUtils.FormatSize(totalBytes)}) from {appItem.Name} to backup?\n\n" +
                "Files are backed up and can be restored later.");

            if (!confirmed) return;

            IsScanning = true;
            appItem.IsCleaning = true;
            StatusMessage = $"Cleaning {appItem.Name}...";

            try
            {
                // Map ClassifiedFileItems back to ClassifiedFile objects
                var filesToRemove = checkedFiles.Select(f => f.OriginalFile).ToList();

                int moved = await Task.Run(() =>
                {
                    var cleanup = new CloudCleanup(_steamPath, msg => Log(msg));
                    cleanup.BeginBatch();
                    string appDir = Path.GetDirectoryName(appItem.RemoteDir) ?? appItem.RemoteDir;
                    int result = cleanup.CleanFiles(appItem.AccountId, appItem.AppId, appDir, filesToRemove);
                    cleanup.EndBatch(appItem.AccountId);
                    return result;
                });

                Log($"\nCleaned {moved} file(s) from {appItem.Name}.");
                _notificationService.ShowSuccess($"Cleaned {moved} file(s) from {appItem.Name}. Backup saved for restore.");

                // Refresh backup list (silently, don't switch view)
                await LoadBackupsInternal();

                // Release the scanning lock so RunScan's guard doesn't skip the rescan
                IsScanning = false;

                // Re-run scan to get fresh results
                await RunScan();
            }
            catch (Exception ex)
            {
                Log($"Cleanup error: {ex.Message}");
                _notificationService.ShowError($"Cleanup failed: {ex.Message}");
            }
            finally
            {
                appItem.IsCleaning = false;
                IsScanning = false;
                // RunScan sets its own status on success; only override on error/skip
                if (StatusMessage.StartsWith("Scanning") || StatusMessage.StartsWith("Cleaning"))
                    StatusMessage = "Cleanup complete";
            }
        }

        [RelayCommand]
        private async Task ViewBackups()
        {
            ShowingScanResults = false;
            ShowingBackups = false;
            StatusMessage = "Loading backups...";
            await LoadBackupsInternal();
            ShowingBackups = HasBackups;
            StatusMessage = HasBackups ? $"{Backups.Count} backup(s) found" : "No backups found";
        }

        private async Task LoadBackupsInternal()
        {
            var steam = _steamPath ?? SteamDetector.FindSteamPath();
            if (steam == null) return;

            try
            {
                var backupInfos = await Task.Run(() => BackupDiscovery.ListCleanupBackups(steam));

                // Resolve app names + header URLs
                var allAppIds = backupInfos.SelectMany(b => b.AppIds).Distinct().ToList();
                Dictionary<uint, StoreAppInfo> nameMap = new();
                try
                {
                    nameMap = await SteamStoreClient.Shared.GetAppInfoAsync(allAppIds);
                }
                catch { }

                Backups.Clear();
                foreach (var info in backupInfos)
                {
                    var appNames = info.AppIds.Select(id =>
                        nameMap.TryGetValue(id, out var si) && !string.IsNullOrEmpty(si.Name)
                            ? si.Name : $"App {id}").ToList();

                    // Primary name: single app → app name, multiple → "N apps"
                    var primaryName = appNames.Count == 1
                        ? appNames[0]
                        : $"{appNames.Count} apps";

                    // Header URL from first app
                    var firstAppId = info.AppIds.FirstOrDefault();
                    var headerUrl = firstAppId != 0 && nameMap.TryGetValue(firstAppId, out var storeInfo)
                        && SteamStoreClient.IsValidSteamCdnUrl(storeInfo.HeaderUrl)
                        ? storeInfo.HeaderUrl : null;

                    // Stats line matching scan card format
                    var appIdsPart = info.AppIds.Count == 1
                        ? $"AppID {info.AppIds[0]}"
                        : $"{info.AppIds.Count} apps";
                    var statsLine = $"{appIdsPart}  ·  {info.FileCount} file(s)  ·  {FileUtils.FormatSize(info.TotalBytes)}";

                    Backups.Add(new BackupItem
                    {
                        Id = info.Id,
                        UndoLogPath = info.UndoLogPath,
                        AccountId = info.AccountId,
                        Timestamp = info.Timestamp,
                        TimestampFormatted = info.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        FileCount = info.FileCount,
                        TotalOperations = info.TotalOperations,
                        TotalSize = FileUtils.FormatSize(info.TotalBytes),
                        AppIds = info.AppIds,
                        Name = primaryName,
                        AppNames = string.Join(", ", appNames),
                        HeaderImageUrl = headerUrl,
                        StatsLine = statsLine,
                    });
                }

                HasBackups = Backups.Count > 0;

                // Load header images in background
                _ = LoadBackupHeaderImagesAsync();
            }
            catch (Exception ex)
            {
                Log($"Failed to load backups: {ex.Message}");
            }
        }

        private async Task LoadBackupHeaderImagesAsync()
        {
            foreach (var item in Backups.ToList())
            {
                var firstAppId = item.AppIds.FirstOrDefault();
                if (firstAppId == 0) continue;
                try
                {
                    var appIdStr = firstAppId.ToString();
                    var cachedPath = await _cacheService.GetSteamGameIconAsync(
                        appIdStr,
                        localSteamIconPath: null,
                        cdnIconUrl: item.HeaderImageUrl ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appIdStr}/header.jpg");

                    if (!string.IsNullOrEmpty(cachedPath))
                        item.CachedIconPath = cachedPath;
                }
                catch { }
            }
        }

        [RelayCommand]
        private async Task RestoreBackup(BackupItem? backup)
        {
            if (backup == null || IsScanning) return;

            var steam = _steamPath ?? SteamDetector.FindSteamPath();
            if (steam == null) return;

            // First do a dry run
            Log($"\n=== Dry-Run Restore: {backup.TimestampFormatted} ===");
            string dryRunSummary = "";

            await Task.Run(() =>
            {
                var revert = new CloudCleanupRevert(steam, RevertConflictMode.Skip, msg => Log(msg));
                var result = revert.RestoreFromLog(backup.UndoLogPath, dryRun: true);
                dryRunSummary = $"{result.FilesRestored} file(s), {result.RemotecachesRestored} remotecache(s)";
                if (result.FilesConflict > 0) dryRunSummary += $", {result.FilesConflict} conflict(s)";
            });

            var confirmed = await Dialog.ConfirmAsync(
                "Restore Backup",
                $"Restore backup from {backup.TimestampFormatted}?\n\n" +
                $"Apps: {backup.AppNames}\n" +
                $"Preview: {dryRunSummary}\n\n" +
                "Existing files at original locations will be skipped.");

            if (!confirmed) return;

            IsScanning = true;
            StatusMessage = "Restoring...";

            try
            {
                await Task.Run(() =>
                {
                    Log($"\n=== Restoring Backup: {backup.TimestampFormatted} ===");
                    var revert = new CloudCleanupRevert(steam, RevertConflictMode.Skip, msg => Log(msg));
                    var result = revert.RestoreFromLog(backup.UndoLogPath, dryRun: false);
                    Log($"\nRestore complete. {result.FilesRestored} file(s) restored.");
                });

                StatusMessage = "Restore complete";
                _notificationService.ShowSuccess("Backup restored successfully.");
            }
            catch (Exception ex)
            {
                Log($"Restore error: {ex.Message}");
                StatusMessage = "Restore failed";
                _notificationService.ShowError($"Restore failed: {ex.Message}");
            }
            finally
            {
                IsScanning = false;
            }
        }

        [RelayCommand]
        private void ClearLog() => LogText = string.Empty;
    }

    // ─── Per-file view model ───────────────────────────────────────────

    public class ClassifiedFileItem : ObservableObject
    {
        internal ClassifiedFileItem(ClassifiedFile file, bool hasCheckbox, bool isChecked)
        {
            OriginalFile = file;
            RelativePath = file.RelativePath;
            Reason = file.Reason ?? "";
            SizeBytes = file.SizeBytes;
            SizeFormatted = FileUtils.FormatSize(file.SizeBytes);
            HasCheckbox = hasCheckbox;
            _isChecked = isChecked;

            // Badge label
            ClassificationLabel = file.Classification switch
            {
                FileClassification.PollutionCrossApp => "Cross-App",
                FileClassification.PollutionAppIdDir => "Wrong AppID",
                FileClassification.PollutionMangled => "Mangled",
                FileClassification.PollutionOrphan => "Orphan",
                FileClassification.Legitimate => "Legit",
                _ => "Unknown"
            };

            // Badge color — use theme brushes so badges adapt to the active Hubcap theme
            BadgeColor = file.Classification switch
            {
                FileClassification.PollutionCrossApp => "DangerBrush",
                FileClassification.PollutionAppIdDir => "WarningBrush",
                FileClassification.PollutionMangled => "WarningBrush",
                FileClassification.PollutionOrphan => "WarningBrush",
                FileClassification.Legitimate => "SuccessBrush",
                _ => "TextSecondaryBrush"
            };
        }

        internal ClassifiedFile OriginalFile { get; }

        public string RelativePath { get; }
        public string Reason { get; }
        public long SizeBytes { get; }
        public string SizeFormatted { get; }
        public string ClassificationLabel { get; }
        public string BadgeColor { get; }
        public bool HasCheckbox { get; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value);
        }
    }

    // ─── Per-app view model ────────────────────────────────────────────

    public partial class ScanResultItem : ObservableObject
    {
        private readonly CloudCleanupViewModel _parent;

        public ScanResultItem(CloudCleanupViewModel parent)
        {
            _parent = parent;
        }

        public uint AppId { get; set; }
        public string AccountId { get; set; } = "";
        public string RemoteDir { get; set; } = "";
        public string Name { get; set; } = "";
        public string? HeaderImageUrl { get; set; }
        public int FileCount { get; set; }
        public string TotalSize { get; set; } = "";
        public int PollutedCount { get; set; }
        public string PollutedSize { get; set; } = "";
        public int LegitimateCount { get; set; }
        public int UnknownCount { get; set; }
        public bool HasPollution { get; set; }

        // File sections
        [ObservableProperty] private ObservableCollection<ClassifiedFileItem> _suspectFiles = new();
        [ObservableProperty] private ObservableCollection<ClassifiedFileItem> _unknownFiles = new();
        [ObservableProperty] private ObservableCollection<ClassifiedFileItem> _legitimateFiles = new();

        public bool HasSuspectFiles { get; set; }
        public bool HasUnknownFiles { get; set; }
        public bool HasLegitimateFiles { get; set; }
        public bool HasCheckboxes { get; set; }
        public string SuspectHeader { get; set; } = "";
        public string UnknownHeader { get; set; } = "";
        public string LegitimateHeader { get; set; } = "";

        private string? _cachedIconPath;
        public string? CachedIconPath
        {
            get => _cachedIconPath;
            set => SetProperty(ref _cachedIconPath, value);
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                    OnPropertyChanged(nameof(ExpandButtonText));
            }
        }

        private bool _isCleaning;
        public bool IsCleaning
        {
            get => _isCleaning;
            set => SetProperty(ref _isCleaning, value);
        }

        public string ExpandButtonText => IsExpanded ? "Collapse"
            : HasPollution ? "Review Files" : "View Files";

        // Stats line: "AppID 12345 · 15 files · 2.3 MB"
        public string StatsLine => $"AppID {AppId}  ·  {FileCount} files  ·  {TotalSize}";

        // Suspect stats (only shown when HasPollution): "3 suspect · 45 KB"
        public string SuspectStatsLine => HasPollution ? $"{PollutedCount} suspect  ·  {PollutedSize}" : "Clean";

        [RelayCommand]
        private void ToggleExpand() => IsExpanded = !IsExpanded;

        [RelayCommand]
        private void SelectAllSuspect()
        {
            foreach (var f in SuspectFiles) f.IsChecked = true;
        }

        [RelayCommand]
        private void DeselectAll()
        {
            foreach (var f in SuspectFiles) f.IsChecked = false;
            foreach (var f in UnknownFiles) f.IsChecked = false;
        }

        [RelayCommand]
        private async Task CleanSelected() => await _parent.CleanAppFiles(this);
    }

    // ─── Backup item ────────────────────────────────────────────────

    public class BackupItem : ObservableObject
    {
        public string Id { get; set; } = "";
        public string UndoLogPath { get; set; } = "";
        public string AccountId { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string TimestampFormatted { get; set; } = "";
        public int FileCount { get; set; }
        public int TotalOperations { get; set; }
        public string TotalSize { get; set; } = "";

        // Per-app data
        public List<uint> AppIds { get; set; } = new();
        public string Name { get; set; } = "";          // Primary app name (or "N apps" if multiple)
        public string AppNames { get; set; } = "";       // All app names comma-separated
        public string? HeaderImageUrl { get; set; }      // First app's header URL

        // Stats line matching scan card format
        public string StatsLine { get; set; } = "";

        private string? _cachedIconPath;
        public string? CachedIconPath
        {
            get => _cachedIconPath;
            set => SetProperty(ref _cachedIconPath, value);
        }
    }
}

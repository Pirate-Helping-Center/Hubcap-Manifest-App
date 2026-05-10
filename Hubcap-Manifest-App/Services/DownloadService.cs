using HubcapManifestApp.Helpers;
using SteamKit2;
using HubcapManifestApp.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;

namespace HubcapManifestApp.Services
{
    public class DownloadService
    {
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloadCancellations;
        private readonly object _collectionLock = new object();
        private readonly ManifestApiService _manifestApiService;
        private readonly LoggerService _logger;
        private readonly DepotDownloaderWrapperService _depotDownloaderWrapper;

        public ObservableCollection<DownloadItem> ActiveDownloads { get; }
        public ObservableCollection<DownloadItem> QueuedDownloads { get; }
        public ObservableCollection<DownloadItem> CompletedDownloads { get; }
        public ObservableCollection<DownloadItem> FailedDownloads { get; }

        public event EventHandler<DownloadItem>? DownloadCompleted;
        public event EventHandler<DownloadItem>? DownloadFailed;

        private int _isProcessingQueue = 0; // 0 = not processing, 1 = processing (interlocked)
        private readonly SemaphoreSlim _depotDownloaderGate = new SemaphoreSlim(1, 1);

        public DownloadService(ManifestApiService manifestApiService, LoggerService logger, DepotDownloaderWrapperService depotDownloaderWrapper, IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("Default");
            _httpClient.Timeout = TimeSpan.FromMinutes(30);
            _downloadCancellations = new ConcurrentDictionary<string, CancellationTokenSource>();
            _manifestApiService = manifestApiService;
            _logger = logger;
            _depotDownloaderWrapper = depotDownloaderWrapper;
            ActiveDownloads = new ObservableCollection<DownloadItem>();
            QueuedDownloads = new ObservableCollection<DownloadItem>();
            CompletedDownloads = new ObservableCollection<DownloadItem>();
            FailedDownloads = new ObservableCollection<DownloadItem>();

            // Enable collection synchronization for cross-thread access
            BindingOperations.EnableCollectionSynchronization(ActiveDownloads, _collectionLock);
            BindingOperations.EnableCollectionSynchronization(QueuedDownloads, _collectionLock);
            BindingOperations.EnableCollectionSynchronization(CompletedDownloads, _collectionLock);
            BindingOperations.EnableCollectionSynchronization(FailedDownloads, _collectionLock);
        }

        private async Task WaitForServerReady(string appId, string apiKey, DownloadItem downloadItem, CancellationToken cancellationToken)
        {
            const int MaxWaitTimeSeconds = 600; // 10 minute maximum wait
            var waitStopwatch = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (waitStopwatch.Elapsed.TotalSeconds > MaxWaitTimeSeconds)
                {
                    _logger.Warning($"Server readiness check for app {appId} timed out after {MaxWaitTimeSeconds} seconds. Proceeding anyway.");
                    return;
                }

                _ = App.Current?.Dispatcher.BeginInvoke(() =>
                    downloadItem.StatusMessage = "Checking server status...");

                _logger.Debug($"Checking status for app {appId}...");
                var status = await _manifestApiService.GetGameStatusAsync(appId, apiKey);
                _logger.Debug($"Status result: UpdateInProgress={status?.UpdateInProgress}, Status={status?.Status}");

                if (status == null || status.UpdateInProgress != true)
                {
                    _logger.Debug("Server is ready, continuing with download");
                    // Server is ready (null or false means not updating)
                    return;
                }

                _logger.Debug("Server is updating, waiting 5 seconds before next check...");
                // Server is updating, wait and poll again
                _ = App.Current?.Dispatcher.BeginInvoke(() =>
                    downloadItem.StatusMessage = "Server updating manifest, waiting...");

                await Task.Delay(5000, cancellationToken);
            }
        }

        /// <summary>
        /// Creates a DownloadItem, prepares the target file path, ensures the destination
        /// directory exists, deletes any pre-existing file, and adds the item to ActiveDownloads.
        /// Returns the item and a CancellationTokenSource for the download.
        /// </summary>
        private (DownloadItem item, string filePath, CancellationTokenSource cts) PrepareDownload(
            Manifest manifest, string destinationFolder, string apiKey)
        {
            var downloadItem = new DownloadItem
            {
                AppId = manifest.AppId,
                GameName = manifest.Name,
                DownloadUrl = $"{manifest.DownloadUrl}?api_key={apiKey}",
                StartTime = DateTime.Now,
                Status = DownloadStatus.Queued,
                TotalBytes = manifest.Size
            };

            var fileName = $"{manifest.AppId}.zip";
            var filePath = Path.Combine(destinationFolder, fileName);
            downloadItem.DestinationPath = filePath;

            Directory.CreateDirectory(destinationFolder);

            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch
                {
                    throw new Exception($"Cannot download - file {fileName} is locked by another process. Please close any programs using this file.");
                }
            }

            _ = App.Current?.Dispatcher.BeginInvoke(() => ActiveDownloads.Add(downloadItem));

            var cts = new CancellationTokenSource();
            _downloadCancellations[downloadItem.Id] = cts;

            return (downloadItem, filePath, cts);
        }

        /// <summary>
        /// Downloads the HTTP response body to a file, updating the DownloadItem's progress
        /// on the UI thread (throttled to 100ms).
        /// </summary>
        private async Task DownloadStreamToFileAsync(
            HttpResponseMessage response,
            string filePath,
            long fallbackTotalBytes,
            DownloadItem downloadItem,
            CancellationToken cancellationToken,
            string logPrefix = "")
        {
            var totalBytes = response.Content.Headers.ContentLength ?? fallbackTotalBytes;
            _logger.Debug($"{logPrefix}Download started - Total bytes: {totalBytes}");
            _ = App.Current?.Dispatcher.BeginInvoke(() =>
            {
                downloadItem.TotalBytes = totalBytes;
                downloadItem.Progress = 0;
                downloadItem.StatusMessage = "Downloading... 0.0%";
            });

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalBytesRead = 0;
            int bytesRead;
            var lastUpdate = DateTime.Now;

            _logger.Debug($"{logPrefix}Starting download loop...");
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesRead += bytesRead;

                var now = DateTime.Now;
                if ((now - lastUpdate).TotalMilliseconds >= 100)
                {
                    var currentBytesRead = totalBytesRead;
                    var progress = (double)currentBytesRead / totalBytes * 100;
                    _ = App.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        downloadItem.DownloadedBytes = currentBytesRead;
                        downloadItem.Progress = progress;
                        downloadItem.StatusMessage = $"Downloading... {progress:F1}%";
                    });
                    lastUpdate = now;
                }
            }

            _logger.Debug($"{logPrefix}Download complete - Total read: {totalBytesRead} bytes");
            _ = App.Current?.Dispatcher.BeginInvoke(() =>
            {
                downloadItem.DownloadedBytes = totalBytesRead;
                downloadItem.Progress = 100;
                downloadItem.StatusMessage = "Download complete";
            });
        }

        /// <summary>
        /// Marks a DownloadItem as completed, moves it from ActiveDownloads to CompletedDownloads,
        /// fires the DownloadCompleted event, and removes the CTS.
        /// </summary>
        private void CompleteDownload(DownloadItem downloadItem, string statusMessage = "Completed")
        {
            // Use actual file size on disk when available (most accurate)
            try
            {
                if (!string.IsNullOrEmpty(downloadItem.DestinationPath))
                {
                    if (File.Exists(downloadItem.DestinationPath))
                    {
                        downloadItem.TotalBytes = new FileInfo(downloadItem.DestinationPath).Length;
                    }
                    else if (Directory.Exists(downloadItem.DestinationPath))
                    {
                        // DepotDownloader downloads to a folder
                        var dirInfo = new DirectoryInfo(downloadItem.DestinationPath);
                        downloadItem.TotalBytes = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    }
                }
            }
            catch { /* keep whatever TotalBytes we had */ }

            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                downloadItem.Status = DownloadStatus.Completed;
                downloadItem.EndTime = DateTime.Now;
                downloadItem.Progress = 100;
                downloadItem.DownloadedBytes = downloadItem.TotalBytes;
                downloadItem.StatusMessage = statusMessage;
                ActiveDownloads.Remove(downloadItem);
                CompletedDownloads.Add(downloadItem);
            });

            DownloadCompleted?.Invoke(this, downloadItem);
        }

        /// <summary>
        /// Marks a DownloadItem as failed, moves it from ActiveDownloads to FailedDownloads,
        /// fires the DownloadFailed event, and removes the CTS.
        /// </summary>
        private void FailDownload(DownloadItem downloadItem, string errorMessage)
        {
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                downloadItem.Status = DownloadStatus.Failed;
                downloadItem.StatusMessage = $"Failed: {errorMessage}";
                downloadItem.EndTime = DateTime.Now;
                ActiveDownloads.Remove(downloadItem);
                FailedDownloads.Add(downloadItem);
            });

            DownloadFailed?.Invoke(this, downloadItem);
        }

        /// <summary>
        /// Cleans up the CancellationTokenSource for a download item.
        /// </summary>
        private void CleanupDownloadCts(DownloadItem downloadItem)
        {
            if (_downloadCancellations.TryRemove(downloadItem.Id, out var removedCts))
                removedCts.Dispose();
        }

        public async Task<string> DownloadGameAsync(Manifest manifest, string destinationFolder, string apiKey, string steamPath)
        {
            var (downloadItem, filePath, cts) = PrepareDownload(manifest, destinationFolder, apiKey);

            try
            {
                _ = App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    downloadItem.Status = DownloadStatus.Downloading;
                });

                // Wait for server to be ready (poll status API)
                await WaitForServerReady(manifest.AppId, apiKey, downloadItem, cts.Token);

                _ = App.Current?.Dispatcher.BeginInvoke(() =>
                    downloadItem.StatusMessage = "Download starting...");

                // Retry logic for server timeouts
                HttpResponseMessage? response = null;
                int maxRetries = 3;
                for (int retry = 0; retry <= maxRetries; retry++)
                {
                    try
                    {
                        if (retry > 0)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Pow(2, retry)); // Exponential backoff: 2s, 4s, 8s
                             App.Current?.Dispatcher.BeginInvoke(() =>
                                downloadItem.StatusMessage = $"Server timeout, retrying in {delay.TotalSeconds}s... (Attempt {retry + 1}/{maxRetries + 1})");
                            await Task.Delay(delay, cts.Token);
                        }

                        response = await _httpClient.GetAsync(downloadItem.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                        // Check for Cloudflare timeout (524) or Gateway timeout (504)
                        if ((int)response.StatusCode == 524 || response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout)
                        {
                            if (retry < maxRetries)
                            {
                                response?.Dispose();
                                continue; // Retry
                            }
                        }

                        response.EnsureSuccessStatusCode();
                        break; // Success, exit retry loop
                    }
                    catch (HttpRequestException) when (retry < maxRetries)
                    {
                        response?.Dispose();
                        continue; // Retry on connection errors
                    }
                }

                if (response == null)
                {
                    throw new Exception("Failed to connect to server after multiple retries");
                }

                // Download the file
                using (response)
                {
                    await DownloadStreamToFileAsync(response, filePath, manifest.Size, downloadItem, cts.Token);
                }

                // Extract files to Steam directories after file is fully written and closed
                _ = App.Current?.Dispatcher.BeginInvoke(() => downloadItem.StatusMessage = "Extracting files...");
                await ExtractToSteamDirectoriesAsync(filePath, steamPath, manifest.AppId);

                CompleteDownload(downloadItem);

                return filePath;
            }
            catch (OperationCanceledException)
            {
                _ = App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    downloadItem.Status = DownloadStatus.Cancelled;
                    downloadItem.StatusMessage = "Cancelled";
                });
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                throw;
            }
            catch (Exception ex)
            {
                FailDownload(downloadItem, ex.Message);
                throw new Exception($"Download failed: {ex.Message}", ex);
            }
            finally
            {
                CleanupDownloadCts(downloadItem);
            }
        }

        public async Task ExtractToSteamDirectoriesAsync(string zipFilePath, string steamPath, string appId)
        {
            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
            {
                throw new Exception("Invalid Steam path. Please configure Steam path in settings.");
            }

            var tempExtractPath = Path.Combine(Path.GetTempPath(), $"hubcap_extract_{appId}");

            try
            {
                // Extract to temp directory first
                if (Directory.Exists(tempExtractPath))
                {
                    Directory.Delete(tempExtractPath, true);
                }
                Directory.CreateDirectory(tempExtractPath);

                await Task.Run(() => ZipFile.ExtractToDirectory(zipFilePath, tempExtractPath));

                // Steam directories
                var luaPath = Path.Combine(steamPath, SteamPaths.ConfigDir, SteamPaths.StPluginDir);
                var manifestPath = Path.Combine(steamPath, SteamPaths.DepotCacheDir);

                // Ensure directories exist
                Directory.CreateDirectory(luaPath);
                Directory.CreateDirectory(manifestPath);

                // Extract the single Lua file from root: {appId}.lua
                var luaFileName = $"{appId}.lua";
                var luaFilePath = Path.Combine(tempExtractPath, luaFileName);

                if (File.Exists(luaFilePath))
                {
                    var destPath = Path.Combine(luaPath, luaFileName);
                    File.Copy(luaFilePath, destPath, true);
                }
                else
                {
                    throw new Exception($"Lua file not found in ZIP: {luaFileName}");
                }

                // Extract manifest files to {steamdir}/depotcache
                var manifestFiles = Directory.GetFiles(tempExtractPath, "*.manifest", SearchOption.AllDirectories);
                foreach (var manifestFile in manifestFiles)
                {
                    var fileName = Path.GetFileName(manifestFile);
                    var destPath = Path.Combine(manifestPath, fileName);
                    File.Copy(manifestFile, destPath, true);
                }
            }
            finally
            {
                // Clean up temp directory
                if (Directory.Exists(tempExtractPath))
                {
                    try
                    {
                        Directory.Delete(tempExtractPath, true);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
            }
        }

        public void CancelDownload(string downloadId)
        {
            if (_downloadCancellations.TryGetValue(downloadId, out var cts))
            {
                cts.Cancel();
            }
        }

        public void RemoveDownload(DownloadItem item)
        {
            _ = App.Current?.Dispatcher.BeginInvoke(() => ActiveDownloads.Remove(item));
        }

        public void ClearCompletedDownloads()
        {
            var completed = CompletedDownloads.ToList();
            foreach (var item in completed)
            {
                _ = App.Current?.Dispatcher.BeginInvoke(() => CompletedDownloads.Remove(item));
            }
        }

        public void AddToQueue(Manifest manifest, string destinationFolder, string apiKey, string steamPath)
        {
            var downloadItem = new DownloadItem
            {
                AppId = manifest.AppId,
                GameName = manifest.Name,
                DownloadUrl = $"{manifest.DownloadUrl}?api_key={apiKey}",
                StartTime = DateTime.Now,
                Status = DownloadStatus.Queued,
                TotalBytes = manifest.Size
            };

            var fileName = $"{manifest.AppId}.zip";
            downloadItem.DestinationPath = Path.Combine(destinationFolder, fileName);

            _ = App.Current?.Dispatcher.BeginInvoke(() => QueuedDownloads.Add(downloadItem));

            // Start processing queue if not already running (thread-safe check)
            if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessQueue(destinationFolder, apiKey, steamPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ProcessQueue failed: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isProcessingQueue, 0);
                    }
                });
            }
        }

        private async Task ProcessQueue(string destinationFolder, string apiKey, string steamPath)
        {
            while (QueuedDownloads.Count > 0)
            {
                DownloadItem? item = null;
                App.Current?.Dispatcher.Invoke(() =>
                {
                    if (QueuedDownloads.Count > 0)
                    {
                        item = QueuedDownloads[0];
                        QueuedDownloads.RemoveAt(0);
                        ActiveDownloads.Add(item);
                    }
                });

                if (item != null)
                {
                    // Extract manifest info from the download item
                    var manifest = new Manifest
                    {
                        AppId = item.AppId,
                        Name = item.GameName,
                        DownloadUrl = item.DownloadUrl.Replace($"?api_key={apiKey}", ""),
                        Size = item.TotalBytes
                    };

                    try
                    {
                        await DownloadGameAsync(manifest, destinationFolder, apiKey, steamPath);
                    }
                    catch
                    {
                        // Error already handled in DownloadGameAsync
                    }
                }
            }
        }

        public void RemoveFromQueue(DownloadItem item)
        {
            _ = App.Current?.Dispatcher.BeginInvoke(() =>
            {
                QueuedDownloads.Remove(item);
                ActiveDownloads.Remove(item);
                CompletedDownloads.Remove(item);
                FailedDownloads.Remove(item);
            });
        }

        /// <summary>
        /// Downloads the game zip file without extracting it
        /// </summary>
        public async Task<string> DownloadGameFileOnlyAsync(Manifest manifest, string destinationFolder, string apiKey)
        {
            var (downloadItem, filePath, cts) = PrepareDownload(manifest, destinationFolder, apiKey);

            try
            {
                _ = App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    downloadItem.Status = DownloadStatus.Downloading;
                });

                // Wait for server to be ready (poll status API)
                await WaitForServerReady(manifest.AppId, apiKey, downloadItem, cts.Token);

                _ = App.Current?.Dispatcher.BeginInvoke(() =>
                    downloadItem.StatusMessage = "Download starting...");

                // Download the file with 15-second response timeout
                _logger.Debug($"FileOnly: Requesting download: {downloadItem.DownloadUrl}");

                using var downloadCts = new CancellationTokenSource();
                downloadCts.CancelAfter(15000); // 15 second timeout for initial response

                HttpResponseMessage? response = null;
                try
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, downloadCts.Token);
                    response = await _httpClient.GetAsync(downloadItem.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                }
                catch (OperationCanceledException) when (downloadCts.IsCancellationRequested && !cts.IsCancellationRequested)
                {
                    // Download request timed out after 5 seconds, check status again
                    _logger.Debug("FileOnly: Download request timed out, checking status...");

                    try
                    {
                        var status = await _manifestApiService.GetGameStatusAsync(manifest.AppId, apiKey);
                        _logger.Debug($"FileOnly: Status check result: UpdateInProgress={status?.UpdateInProgress}, Status={status?.Status}");

                        if (status?.UpdateInProgress == true)
                        {
                            _logger.Debug("FileOnly: Server is updating, going back to polling...");
                            // Server is still updating, go back to polling
                            await WaitForServerReady(manifest.AppId, apiKey, downloadItem, cts.Token);

                            _ = App.Current?.Dispatcher.BeginInvoke(() =>
                                downloadItem.StatusMessage = "Download starting...");

                            _logger.Debug("FileOnly: Retrying download after waiting...");
                            // Try download again
                            response = await _httpClient.GetAsync(downloadItem.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        }
                        else
                        {
                            _logger.Debug("FileOnly: Server not updating, but timeout occurred - retrying with longer timeout...");
                            // Server not updating, just retry with 30-second timeout
                            using var retryCts = new CancellationTokenSource();
                            retryCts.CancelAfter(30000); // 30 second timeout
                            using var retryLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, retryCts.Token);
                            response = await _httpClient.GetAsync(downloadItem.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, retryLinkedCts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"FileOnly: Status check failed: {ex.Message}");
                        throw;
                    }
                }

                using (response)
                {
                    response!.EnsureSuccessStatusCode();
                    await DownloadStreamToFileAsync(response, filePath, manifest.Size, downloadItem, cts.Token, "FileOnly: ");
                }

                CompleteDownload(downloadItem, "Download Complete - Ready for depot selection");

                return filePath;
            }
            catch (Exception ex)
            {
                FailDownload(downloadItem, ex.Message);
                throw;
            }
            finally
            {
                CleanupDownloadCts(downloadItem);
            }
        }

        /// <summary>
        /// Extracts and reads the lua file content from a downloaded zip
        /// </summary>
        public string ExtractLuaContentFromZip(string zipFilePath, string appId)
        {
            var luaFileName = $"{appId}.lua";

            using var archive = ZipFile.OpenRead(zipFilePath);
            var luaEntry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals(luaFileName, StringComparison.OrdinalIgnoreCase) ||
                e.Name.Equals(luaFileName, StringComparison.OrdinalIgnoreCase));

            if (luaEntry == null)
            {
                throw new Exception($"Lua file '{luaFileName}' not found in zip archive.");
            }

            using var stream = luaEntry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Extracts manifest files from the zip to a temporary directory
        /// Returns a dictionary mapping depotId to manifest file path
        /// </summary>
        public Dictionary<string, string> ExtractManifestFilesFromZip(string zipFilePath, string appId)
        {
            var manifestFiles = new Dictionary<string, string>();
            var tempDir = Path.Combine(Path.GetTempPath(), $"HubcapManifests_{appId}_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);

            using var archive = ZipFile.OpenRead(zipFilePath);
            foreach (var entry in archive.Entries)
            {
                // Look for .manifest files
                if (entry.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract manifest file to temp directory
                    var destPath = Path.Combine(tempDir, entry.Name);
                    entry.ExtractToFile(destPath, true);

                    // Try to extract depot ID from filename (format: depotId_manifestId.manifest)
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(entry.Name);
                    var parts = fileNameWithoutExt.Split('_');
                    if (parts.Length >= 1 && uint.TryParse(parts[0], out var depotId))
                    {
                        manifestFiles[parts[0]] = destPath;
                    }
                }
            }

            return manifestFiles;
        }

        public async Task<bool> DownloadViaDepotDownloaderAsync(
            string appId,
            string gameName,
            List<(uint depotId, string depotKey, string? manifestFile, uint ownerAppId)> depots,
            string outputPath,
            bool verifyFiles = true,
            int maxDownloads = 8,
            ulong primaryManifestId = 0,
            bool useRawTargetDirectory = false)
        {
            _logger.Info($"[DepotDownloader] Queuing download for {gameName} (App ID: {appId})");
            _logger.Info($"[DepotDownloader] Depots to download: {depots.Count}");
            _logger.Info($"[DepotDownloader] Output path: {outputPath}");
            _logger.Info($"[DepotDownloader] Verify files: {verifyFiles}");
            _logger.Info($"[DepotDownloader] Max concurrent downloads: {maxDownloads}");

            // Serialize DepotDownloader jobs — only one game downloads at a time
            await _depotDownloaderGate.WaitAsync();
            try
            {
            _logger.Info($"[DepotDownloader] Gate acquired, starting download for {gameName}");

            // Sanitize game name to remove invalid path characters (: < > " / \ | ? *)
            var sanitizedGameName = SanitizeFileName(gameName);
            _logger.Info($"[DepotDownloader] Sanitized game name: '{gameName}' -> '{sanitizedGameName}'");

            // For fresh installs, build {outputPath}/{GameName} ({AppId})/{GameName}.
            // For verify-and-update against an existing install (useRawTargetDirectory=true),
            // write directly into the user-supplied path so DepotDownloader's verify-all
            // pass sees the existing files and only patches the diff.
            string gameDownloadPath;
            if (useRawTargetDirectory)
            {
                gameDownloadPath = outputPath;
                _logger.Info($"[DepotDownloader] Using raw target directory (verify mode): {gameDownloadPath}");
            }
            else
            {
                var gameFolderName = $"{sanitizedGameName} ({appId})";
                gameDownloadPath = Path.Combine(outputPath, gameFolderName, sanitizedGameName);
            }

            var downloadItem = new DownloadItem
            {
                AppId = appId,
                GameName = gameName,
                Status = DownloadStatus.Downloading,
                StartTime = DateTime.Now,
                StatusMessage = "Initializing Steam session...",
                DestinationPath = gameDownloadPath,
                IsDepotDownloaderMode = true, // Mark as DepotDownloader to skip auto-install
                InstallManifestId = primaryManifestId,
                InstallDepotIds = depots.Select(d => d.depotId).ToList()
            };

            var cancellationTokenSource = new CancellationTokenSource();
            _downloadCancellations[downloadItem.Id] = cancellationTokenSource;

            _logger.Info($"[DepotDownloader] Adding download item to ActiveDownloads (ID: {downloadItem.Id})");
            lock (_collectionLock)
            {
                ActiveDownloads.Add(downloadItem);
            }
            _logger.Info($"[DepotDownloader] Download item added successfully. ActiveDownloads count: {ActiveDownloads.Count}");

            try
            {
                Directory.CreateDirectory(downloadItem.DestinationPath);

                var depotDownloaderService = _depotDownloaderWrapper;

                // Subscribe to events
                EventHandler<DownloadProgressEventArgs>? progressHandler = null;
                EventHandler<DownloadStatusEventArgs>? statusHandler = null;
                EventHandler<LogMessageEventArgs>? logHandler = null;

                long cumulativeDownloaded = 0;
                long cumulativeTotal = 0;
                long previousDepotDownloaded = 0;
                long previousDepotTotal = 0;

                progressHandler = (sender, e) =>
                {
                    App.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        var currentDepotDownloaded = (long)e.DownloadedBytes;
                        var currentDepotTotal = (long)e.TotalBytes;

                        if (currentDepotDownloaded < previousDepotDownloaded && previousDepotTotal > 0)
                        {
                            // New depot started — finalize previous depot
                            cumulativeDownloaded += previousDepotTotal;
                            cumulativeTotal += currentDepotTotal;
                        }
                        else if (previousDepotTotal == 0)
                        {
                            // First depot
                            cumulativeTotal = currentDepotTotal;
                        }
                        else if (currentDepotTotal != previousDepotTotal)
                        {
                            // Same depot but total changed (e.g. size refined)
                            cumulativeTotal = cumulativeTotal - previousDepotTotal + currentDepotTotal;
                        }

                        previousDepotDownloaded = currentDepotDownloaded;
                        previousDepotTotal = currentDepotTotal;

                        downloadItem.DownloadedBytes = cumulativeDownloaded + currentDepotDownloaded;
                        downloadItem.TotalBytes = cumulativeTotal;
                        downloadItem.Progress = cumulativeTotal > 0
                            ? (double)downloadItem.DownloadedBytes / cumulativeTotal * 100.0
                            : e.Progress;
                        var progressPercent = (int)downloadItem.Progress;
                        downloadItem.StatusMessage = $"Downloading: {e.CurrentFile} ({progressPercent}% - {e.ProcessedFiles}/{e.TotalFiles} files)";
                    });
                };

                statusHandler = (sender, e) =>
                {
                    App.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        downloadItem.StatusMessage = e.Message;
                    });
                };

                logHandler = (sender, e) =>
                {
                    _logger.Debug($"[DepotDownloader] {e.Message}");
                };

                depotDownloaderService.ProgressChanged += progressHandler;
                depotDownloaderService.StatusChanged += statusHandler;
                depotDownloaderService.LogMessage += logHandler;

                try
                {
                    // Always use anonymous login
                    _logger.Info($"[DepotDownloader] Initializing Steam session (anonymous)...");
                    _ = App.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        downloadItem.StatusMessage = "Connecting to Steam (anonymous)...";
                    });

                    var initialized = await depotDownloaderService.InitializeAsync("", "");
                    _logger.Info($"[DepotDownloader] Steam initialization result: {initialized}");

                    if (!initialized)
                    {
                        _logger.Error($"[DepotDownloader] Steam initialization failed!");
                        throw new Exception("Failed to initialize Steam session");
                    }

                    _logger.Info($"[DepotDownloader] Steam session initialized successfully");

                    // Save PICS data for Fix Game launch config generation
                    try
                    {
                        var picsAppId = uint.Parse(appId);
                        await DepotDownloader.ContentDownloader.EnsureAppInfoAsync(picsAppId);
                        // Use reflection to get the enum value since EAppInfoSection isn't directly accessible
                        var enumType = typeof(DepotDownloader.ContentDownloader).Assembly.GetType("DepotDownloader.EAppInfoSection")
                                     ?? typeof(DepotDownloader.ContentDownloader).Assembly.GetType("EAppInfoSection");
                        SteamKit2.KeyValue? configKv = null;
                        SteamKit2.KeyValue? commonKv = null;
                        if (enumType != null)
                        {
                            var configEnum = Enum.Parse(enumType, "Config");
                            var commonEnum = Enum.Parse(enumType, "Common");
                            var method = typeof(DepotDownloader.ContentDownloader).GetMethod("GetSteam3AppSection",
                                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                            configKv = method?.Invoke(null, new object[] { picsAppId, configEnum }) as SteamKit2.KeyValue;
                            commonKv = method?.Invoke(null, new object[] { picsAppId, commonEnum }) as SteamKit2.KeyValue;
                        }

                        if (configKv != null || commonKv != null)
                        {
                            var picsJson = new Newtonsoft.Json.Linq.JObject();
                            picsJson["appid"] = appId;

                            if (commonKv != null)
                            {
                                var commonObj = new Newtonsoft.Json.Linq.JObject();
                                foreach (var child in commonKv.Children)
                                    WriteKeyValueToJson(commonObj, child);
                                picsJson["common"] = commonObj;
                            }

                            if (configKv != null)
                            {
                                var configObj = new Newtonsoft.Json.Linq.JObject();
                                foreach (var child in configKv.Children)
                                    WriteKeyValueToJson(configObj, child);
                                picsJson["config"] = configObj;
                            }

                            var cacheService = new FixGame.FixGameCacheService();
                            cacheService.SavePicsJson(appId, picsJson.ToString());
                            _logger.Info($"[DepotDownloader] Saved PICS data for Fix Game");
                        }
                    }
                    catch (Exception picsEx)
                    {
                        _logger.Warning($"[DepotDownloader] Could not save PICS data: {picsEx.Message}");
                    }

                    _ = App.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        downloadItem.StatusMessage = $"Downloading {depots.Count} depots...";
                    });

                    _logger.Info($"[DepotDownloader] Starting depot download...");
                    var success = await depotDownloaderService.DownloadDepotsAsync(
                        uint.Parse(appId),
                        depots,
                        downloadItem.DestinationPath,
                        verifyFiles,
                        maxDownloads,
                        isUgc: false,
                        cancellationTokenSource.Token
                    );
                    _logger.Info($"[DepotDownloader] Download completed with result: {success}");

                    if (success)
                    {
                        _logger.Info($"[DepotDownloader] Download successful! Moving to CompletedDownloads");
                        // Use actual folder size for accurate history
                        try
                        {
                            if (Directory.Exists(downloadItem.DestinationPath))
                            {
                                var dirInfo = new DirectoryInfo(downloadItem.DestinationPath);
                                downloadItem.TotalBytes = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                                downloadItem.DownloadedBytes = downloadItem.TotalBytes;
                            }
                        }
                        catch { /* keep whatever TotalBytes we had */ }

                        _ = App.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            downloadItem.Status = DownloadStatus.Completed;
                            downloadItem.Progress = 100;
                            downloadItem.StatusMessage = "Download completed successfully";
                            downloadItem.EndTime = DateTime.Now;

                            lock (_collectionLock)
                            {
                                ActiveDownloads.Remove(downloadItem);
                                CompletedDownloads.Add(downloadItem);
                            }

                            DownloadCompleted?.Invoke(this, downloadItem);
                        });

                        return true;
                    }
                    else
                    {
                        throw new Exception("Download failed");
                    }
                }
                finally
                {
                    depotDownloaderService.ProgressChanged -= progressHandler;
                    depotDownloaderService.StatusChanged -= statusHandler;
                    depotDownloaderService.LogMessage -= logHandler;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[DepotDownloader] Download failed for {gameName} (App ID: {appId})");
                _logger.Error($"[DepotDownloader] Exception: {ex.GetType().Name} - {ex.Message}");
                _logger.Error($"[DepotDownloader] Stack trace: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    _logger.Error($"[DepotDownloader] Inner exception: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
                }

                _ = App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    downloadItem.Status = DownloadStatus.Failed;
                    downloadItem.StatusMessage = $"Failed: {ex.Message}";
                    downloadItem.EndTime = DateTime.Now;

                    lock (_collectionLock)
                    {
                        ActiveDownloads.Remove(downloadItem);
                        FailedDownloads.Add(downloadItem);
                    }

                    DownloadFailed?.Invoke(this, downloadItem);
                });

                return false;
            }
            finally
            {
                if (_downloadCancellations.TryRemove(downloadItem.Id, out var removedCts3))
                    removedCts3.Dispose();
            }
            }
            finally
            {
                _depotDownloaderGate.Release();
                _logger.Info($"[DepotDownloader] Gate released for {gameName}");
            }
        }

        /// <summary>
        /// Sanitizes a file/folder name by removing invalid characters
        /// </summary>
        private string SanitizeFileName(string fileName)
        {
            // Windows invalid path characters: < > : " / \ | ? *
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = fileName;

            foreach (var c in invalidChars)
            {
                sanitized = sanitized.Replace(c.ToString(), "");
            }

            return sanitized.Trim();
        }

        /// <summary>
        /// Recursively converts a SteamKit KeyValue tree to JObject/JValue.
        /// </summary>
        private static void WriteKeyValueToJson(Newtonsoft.Json.Linq.JObject parent, SteamKit2.KeyValue kv)
        {
            if (kv.Children.Count > 0)
            {
                var obj = new Newtonsoft.Json.Linq.JObject();
                foreach (var child in kv.Children)
                    WriteKeyValueToJson(obj, child);
                parent[kv.Name ?? ""] = obj;
            }
            else
            {
                parent[kv.Name ?? ""] = kv.Value;
            }
        }
    }
}

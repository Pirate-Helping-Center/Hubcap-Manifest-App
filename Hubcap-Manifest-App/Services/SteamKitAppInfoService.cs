using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace HubcapManifestApp.Services
{
    /// <summary>
    /// Service for fetching app information directly from Steam using SteamKit2
    /// This replaces the dependency on the SteamCMD API
    /// </summary>
    public class SteamKitAppInfoService : IDisposable
    {
        private readonly LoggerService _logger;
        private readonly SemaphoreSlim _connectionGate = new SemaphoreSlim(1, 1);
        private SteamClient? _steamClient;
        private SteamApps? _steamApps;
        private CallbackManager? _callbacks;
        private bool _isConnected = false;
        private bool _isAnonymousLoggedIn = false;
        private bool _disposed = false;

        public SteamKitAppInfoService(LoggerService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Initialize anonymous Steam connection
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            await _connectionGate.WaitAsync();
            try
            {
                if (_isConnected && _isAnonymousLoggedIn)
                    return true;

                // Disconnect any previous session to prevent callback loop compounding
                if (_steamClient != null)
                {
                    Disconnect();
                }

                _logger.Info("[SteamKit] Initializing anonymous Steam connection...");

                var clientConfiguration = SteamConfiguration.Create(config =>
                    config.WithHttpClientFactory(DepotDownloader.HttpClientFactory.CreateHttpClient)
                );

                _steamClient = new SteamClient(clientConfiguration);
                var steamUser = _steamClient.GetHandler<SteamUser>();
                _steamApps = _steamClient.GetHandler<SteamApps>();
                _callbacks = new CallbackManager(_steamClient);

                var connectedTcs = new TaskCompletionSource<bool>();
                var loggedOnTcs = new TaskCompletionSource<bool>();

                // Subscribe to connection callback
                _callbacks.Subscribe<SteamClient.ConnectedCallback>(callback =>
                {
                    _logger.Info("[SteamKit] Connected to Steam. Logging in anonymously...");
                    _isConnected = true;
                    steamUser?.LogOnAnonymous();
                    connectedTcs.TrySetResult(true);
                });

                // Subscribe to logon callback
                _callbacks.Subscribe<SteamUser.LoggedOnCallback>(callback =>
                {
                    if (callback.Result == EResult.OK)
                    {
                        _logger.Info("[SteamKit] Logged in anonymously successfully");
                        _isAnonymousLoggedIn = true;
                        loggedOnTcs.TrySetResult(true);
                    }
                    else
                    {
                        _logger.Error($"[SteamKit] Failed to log in anonymously: {callback.Result}");
                        loggedOnTcs.TrySetResult(false);
                    }
                });

                // Subscribe to disconnection callback
                _callbacks.Subscribe<SteamClient.DisconnectedCallback>(callback =>
                {
                    _logger.Warning("[SteamKit] Disconnected from Steam");
                    _isConnected = false;
                    _isAnonymousLoggedIn = false;
                    connectedTcs.TrySetCanceled();
                    loggedOnTcs.TrySetCanceled();
                });

                // Start callback processing BEFORE connecting
                var callbackTask = Task.Run(() =>
                {
                    _logger.Info("[SteamKit] Starting callback manager loop...");
                    while (!connectedTcs.Task.IsCompleted || !loggedOnTcs.Task.IsCompleted)
                    {
                        _callbacks?.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                    }
                    _logger.Info("[SteamKit] Callback manager loop finished initialization phase");
                });

                // Give callback loop time to start
                await Task.Delay(100);

                // Now connect to Steam
                _logger.Info("[SteamKit] Connecting to Steam...");
                _steamClient.Connect();

                // Wait for connection (increased timeout to 30 seconds)
                var connectionTask = await Task.WhenAny(connectedTcs.Task, Task.Delay(30000));
                if (connectionTask != connectedTcs.Task || !await connectedTcs.Task)
                {
                    _logger.Error("[SteamKit] Failed to connect to Steam (timeout after 30 seconds)");
                    _steamClient?.Disconnect();
                    return false;
                }

                // Wait for login (increased timeout to 30 seconds)
                var loginTask = await Task.WhenAny(loggedOnTcs.Task, Task.Delay(30000));
                if (loginTask != loggedOnTcs.Task || !await loggedOnTcs.Task)
                {
                    _logger.Error("[SteamKit] Failed to log in anonymously (timeout after 30 seconds)");
                    _steamClient?.Disconnect();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"[SteamKit] Error initializing: {ex.Message}");
                return false;
            }
            finally
            {
                _connectionGate.Release();
            }
        }

        /// <summary>
        /// Get app info from Steam directly
        /// </summary>
        public async Task<AppInfoResult?> GetAppInfoAsync(uint appId, ulong? accessToken = null)
        {
            try
            {
                if (!_isAnonymousLoggedIn)
                {
                    _logger.Warning("[SteamKit] Not logged in, initializing...");
                    if (!await InitializeAsync())
                    {
                        _logger.Error("[SteamKit] Failed to initialize Steam connection");
                        return null;
                    }
                }

                if (_steamApps == null)
                {
                    _logger.Error("[SteamKit] SteamApps handler is null");
                    return null;
                }

                _logger.Info($"[SteamKit] Requesting app info for {appId}...");

                var picsRequest = new SteamApps.PICSRequest(appId);
                if (accessToken.HasValue)
                {
                    picsRequest.AccessToken = accessToken.Value;
                }

                var requestTask = _steamApps.PICSGetProductInfo(
                    new List<SteamApps.PICSRequest> { picsRequest },
                    new List<SteamApps.PICSRequest>()
                ).ToTask();

                var completedTask = await Task.WhenAny(requestTask, Task.Delay(30000));
                if (completedTask != requestTask)
                {
                    _logger.Error($"[SteamKit] App info request timed out after 30 seconds for app {appId}");
                    return null;
                }

                var appInfoResponse = await requestTask;

                if (appInfoResponse == null)
                {
                    _logger.Error($"[SteamKit] App info response is null for app {appId}");
                    return null;
                }

                if (appInfoResponse.Results == null || !appInfoResponse.Results.Any())
                {
                    _logger.Error($"[SteamKit] No results returned for app {appId}");
                    return null;
                }

                var firstResult = appInfoResponse.Results.First();

                if (firstResult.Apps == null || !firstResult.Apps.ContainsKey(appId))
                {
                    _logger.Error($"[SteamKit] App {appId} not found in results. This app may not exist or may be restricted.");
                    return null;
                }

                var appInfo = firstResult.Apps[appId];
                _logger.Info($"[SteamKit] Successfully retrieved app info for {appId}");

                // Parse the KeyValues data
                return ParseAppInfo(appId, appInfo);
            }
            catch (Exception ex)
            {
                _logger.Error($"[SteamKit] Exception getting app info for {appId}: {ex.GetType().Name} - {ex.Message}");
                _logger.Error($"[SteamKit] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Parse SteamKit2 KeyValues into our app info structure
        /// </summary>
        private AppInfoResult ParseAppInfo(uint appId, SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo)
        {
            var result = new AppInfoResult
            {
                AppId = appId,
                AppName = "Unknown"
            };

            try
            {
                var keyValues = appInfo.KeyValues;

                // Get app name from common section
                var common = keyValues["common"];
                if (common != KeyValue.Invalid && common["name"] != KeyValue.Invalid)
                {
                    result.AppName = common["name"].Value ?? "Unknown";
                }

                _logger.Info($"[SteamKit] App Name: {result.AppName}");

                // Get depots section
                var depots = keyValues["depots"];
                if (depots == KeyValue.Invalid)
                {
                    _logger.Warning($"[SteamKit] No depots section found for app {appId}");
                    return result;
                }

                // Parse each depot
                foreach (var depotKv in depots.Children)
                {
                    // Skip non-numeric keys (like "branches")
                    if (!uint.TryParse(depotKv.Name, out var depotId))
                        continue;

                    var depotInfo = new DepotInfoResult
                    {
                        DepotId = depotId.ToString()
                    };

                    // Get depot config
                    var config = depotKv["config"];
                    if (config != KeyValue.Invalid)
                    {
                        // Language
                        if (config["language"] != KeyValue.Invalid)
                        {
                            depotInfo.Language = config["language"].Value;
                        }

                        // OS list
                        if (config["oslist"] != KeyValue.Invalid)
                        {
                            depotInfo.OsList = config["oslist"].Value;
                        }

                        // OS arch
                        if (config["osarch"] != KeyValue.Invalid)
                        {
                            depotInfo.OsArch = config["osarch"].Value;
                        }

                        // Low violence
                        if (config["lowviolence"] != KeyValue.Invalid)
                        {
                            depotInfo.LowViolence = config["lowviolence"].AsBoolean();
                        }
                    }

                    // Get depot from app (shared depot)
                    if (depotKv["depotfromapp"] != KeyValue.Invalid)
                    {
                        depotInfo.DepotFromApp = depotKv["depotfromapp"].AsUnsignedInteger().ToString();
                    }

                    // Get shared install
                    if (depotKv["sharedinstall"] != KeyValue.Invalid)
                    {
                        depotInfo.SharedInstall = depotKv["sharedinstall"].Value;
                    }

                    // Get DLC appid
                    if (depotKv["dlcappid"] != KeyValue.Invalid)
                    {
                        depotInfo.DlcAppId = depotKv["dlcappid"].AsUnsignedInteger().ToString();
                    }

                    // Check if depot has manifests
                    var manifests = depotKv["manifests"];
                    if (manifests != KeyValue.Invalid && manifests.Children.Any())
                    {
                        depotInfo.HasManifests = true;

                        // Get public manifest if available
                        if (manifests["public"] != KeyValue.Invalid)
                        {
                            if (manifests["public"]["gid"] != KeyValue.Invalid)
                            {
                                depotInfo.ManifestGid = manifests["public"]["gid"].Value;
                            }

                            // Get manifest size if available
                            if (manifests["public"]["size"] != KeyValue.Invalid)
                            {
                                if (long.TryParse(manifests["public"]["size"].Value, out var size))
                                {
                                    depotInfo.Size = size;
                                }
                            }

                            // Get download size if available
                            if (manifests["public"]["download"] != KeyValue.Invalid)
                            {
                                if (long.TryParse(manifests["public"]["download"].Value, out var downloadSize))
                                {
                                    depotInfo.DownloadSize = downloadSize;
                                }
                            }
                        }
                    }

                    _logger.Debug($"[SteamKit]   Depot {depotId}: Lang={depotInfo.Language ?? "none"}, OS={depotInfo.OsList ?? "any"}, DLC={depotInfo.DlcAppId ?? "no"}, Shared={depotInfo.DepotFromApp ?? "no"}");

                    result.Depots.Add(depotInfo);
                }

                _logger.Info($"[SteamKit] Parsed {result.Depots.Count} depots");
            }
            catch (Exception ex)
            {
                _logger.Error($"[SteamKit] Error parsing app info: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Gets launch configurations from PICS config section.
        /// Must be called while the Steam session is still connected.
        /// </summary>
        public async Task<List<LaunchConfigInfo>> GetLaunchConfigsAsync(string appId, ulong? accessToken = null)
        {
            var results = new List<LaunchConfigInfo>();
            try
            {
                if (!uint.TryParse(appId, out var id)) return results;

                var picsRequest = new SteamKit2.SteamApps.PICSRequest(id);
                if (accessToken.HasValue) picsRequest.AccessToken = accessToken.Value;

                var response = await _steamApps!.PICSGetProductInfo(
                    new List<SteamKit2.SteamApps.PICSRequest> { picsRequest },
                    new List<SteamKit2.SteamApps.PICSRequest>()
                ).ToTask();

                var firstResult = response?.Results?.FirstOrDefault();
                if (firstResult?.Apps?.ContainsKey(id) != true) return results;

                var appInfo = firstResult.Apps[id];
                var config = appInfo.KeyValues["config"];
                if (config == null || config == SteamKit2.KeyValue.Invalid) return results;

                var launch = config["launch"];
                if (launch == null || launch == SteamKit2.KeyValue.Invalid) return results;

                foreach (var entry in launch.Children)
                {
                    var exe = entry["executable"]?.Value;
                    if (string.IsNullOrEmpty(exe)) continue;

                    results.Add(new LaunchConfigInfo
                    {
                        Executable = exe,
                        Arguments = entry["arguments"]?.Value ?? "",
                        Description = entry["description"]?.Value ?? "",
                        WorkingDir = entry["workingdir"]?.Value ?? "",
                        OsType = entry["config"]?["oslist"]?.Value ?? "",
                        BetaKey = entry["config"]?["betakey"]?.Value ?? ""
                    });
                }

                _logger.Info($"[SteamKit] Found {results.Count} launch config(s) for app {appId}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[SteamKit] Could not get launch configs: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Gets cloud save directory info from PICS ufs section.
        /// </summary>
        public async Task<(List<CloudSaveDir> saves, List<CloudSaveOverride> overrides)> GetCloudSaveDirsAsync(string appId, ulong? accessToken = null)
        {
            var saves = new List<CloudSaveDir>();
            var overrides = new List<CloudSaveOverride>();
            try
            {
                if (!uint.TryParse(appId, out var id)) return (saves, overrides);

                var picsRequest = new SteamKit2.SteamApps.PICSRequest(id);
                if (accessToken.HasValue) picsRequest.AccessToken = accessToken.Value;

                var response = await _steamApps!.PICSGetProductInfo(
                    new List<SteamKit2.SteamApps.PICSRequest> { picsRequest },
                    new List<SteamKit2.SteamApps.PICSRequest>()
                ).ToTask();

                var firstResult = response?.Results?.FirstOrDefault();
                if (firstResult?.Apps?.ContainsKey(id) != true) return (saves, overrides);

                var appInfo = firstResult.Apps[id];
                var ufs = appInfo.KeyValues["ufs"];
                if (ufs == null || ufs == SteamKit2.KeyValue.Invalid) return (saves, overrides);

                // Parse savefiles
                var savefiles = ufs["savefiles"];
                if (savefiles != null && savefiles != SteamKit2.KeyValue.Invalid)
                {
                    foreach (var entry in savefiles.Children)
                    {
                        var root = entry["root"]?.Value ?? "";
                        if (string.IsNullOrEmpty(root)) continue;

                        var path = entry["path"]?.Value ?? "";
                        var platforms = new List<string>();
                        var platformsKv = entry["platforms"];
                        if (platformsKv != null)
                            foreach (var p in platformsKv.Children)
                                if (!string.IsNullOrEmpty(p.Value))
                                    platforms.Add(p.Value);

                        saves.Add(new CloudSaveDir { Root = root, Path = path, Platforms = platforms });
                    }
                }

                // Parse rootoverrides
                var rootoverrides = ufs["rootoverrides"];
                if (rootoverrides != null && rootoverrides != SteamKit2.KeyValue.Invalid)
                {
                    foreach (var entry in rootoverrides.Children)
                    {
                        var rootOrig = entry["root"]?.Value ?? "";
                        var rootNew = entry["useinstead"]?.Value ?? "";
                        var platform = entry["os"]?.Value ?? "";
                        var addpath = entry["addpath"]?.Value ?? "";

                        var transforms = new List<(string, string)>();
                        var pathTransforms = entry["pathtransforms"];
                        if (pathTransforms != null)
                            foreach (var t in pathTransforms.Children)
                                transforms.Add((t["find"]?.Value ?? "", t["replace"]?.Value ?? ""));

                        overrides.Add(new CloudSaveOverride
                        {
                            RootOriginal = rootOrig,
                            RootNew = rootNew,
                            PathAfterRoot = addpath,
                            Platform = platform,
                            Transforms = transforms
                        });
                    }
                }

                _logger.Info($"[SteamKit] Found {saves.Count} cloud save dir(s), {overrides.Count} override(s) for app {appId}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[SteamKit] Could not get cloud save dirs: {ex.Message}");
            }
            return (saves, overrides);
        }

        /// <summary>
        /// Get app info in SteamCMD API compatible format (drop-in replacement)
        /// </summary>
        public async Task<SteamCmdDepotData?> GetDepotInfoAsync(string appId, ulong? accessToken = null)
        {
            if (!uint.TryParse(appId, out var appIdUint))
            {
                _logger.Error($"[SteamKit] Invalid app ID: {appId}");
                return null;
            }

            var appInfo = await GetAppInfoAsync(appIdUint, accessToken);
            if (appInfo == null)
                return null;

            // Convert to SteamCMD API format
            return ConvertToSteamCmdFormat(appInfo, appId);
        }

        /// <summary>
        /// Convert SteamKit app info to SteamCMD API format
        /// </summary>
        private SteamCmdDepotData ConvertToSteamCmdFormat(AppInfoResult appInfo, string appId)
        {
            var result = new SteamCmdDepotData
            {
                Status = "success",
                Data = new Dictionary<string, AppData>()
            };

            var appData = new AppData
            {
                Common = new CommonData
                {
                    Name = appInfo.AppName
                },
                Depots = new Dictionary<string, DepotData>()
            };

            foreach (var depot in appInfo.Depots)
            {
                var depotData = new DepotData
                {
                    DlcAppId = depot.DlcAppId,
                    DepotFromApp = depot.DepotFromApp,
                    SharedInstall = depot.SharedInstall
                };

                // Convert depot config
                if (!string.IsNullOrEmpty(depot.Language) ||
                    !string.IsNullOrEmpty(depot.OsList) ||
                    depot.LowViolence)
                {
                    depotData.Config = new DepotConfig
                    {
                        Language = depot.Language,
                        OsList = depot.OsList,
                        LowViolence = depot.LowViolence ? "1" : null
                    };
                }

                // Convert manifests
                if (depot.HasManifests && !string.IsNullOrEmpty(depot.ManifestGid))
                {
                    depotData.Manifests = new Dictionary<string, ManifestData>
                    {
                        ["public"] = new ManifestData
                        {
                            Gid = depot.ManifestGid,
                            Size = depot.Size,
                            Download = depot.DownloadSize
                        }
                    };
                }

                appData.Depots[depot.DepotId] = depotData;
            }

            result.Data[appId] = appData;

            return result;
        }

        /// <summary>
        /// Disconnect from Steam
        /// </summary>
        public void Disconnect()
        {
            _connectionGate.Wait();
            try
            {
                _logger.Info("[SteamKit] Disconnecting from Steam...");
                _steamClient?.Disconnect();
                _isConnected = false;
                _isAnonymousLoggedIn = false;
            }
            catch (Exception ex)
            {
                _logger.Error($"[SteamKit] Error disconnecting: {ex.Message}");
            }
            finally
            {
                _connectionGate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _connectionGate.Dispose();
            _steamClient = null;
            _callbacks = null;
            _steamApps = null;
        }
    }

    /// <summary>
    /// App info result from SteamKit2
    /// </summary>
    public class CloudSaveDir
    {
        public string Root { get; set; } = "";
        public string Path { get; set; } = "";
        public List<string> Platforms { get; set; } = new();
    }

    public class CloudSaveOverride
    {
        public string RootOriginal { get; set; } = "";
        public string RootNew { get; set; } = "";
        public string PathAfterRoot { get; set; } = "";
        public string Platform { get; set; } = "";
        public List<(string Find, string Replace)> Transforms { get; set; } = new();
    }

    public class LaunchConfigInfo
    {
        public string Executable { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Description { get; set; } = "";
        public string WorkingDir { get; set; } = "";
        public string OsType { get; set; } = "";
        public string BetaKey { get; set; } = "";
    }

    public class AppInfoResult
    {
        public uint AppId { get; set; }
        public string AppName { get; set; } = "";
        public List<DepotInfoResult> Depots { get; set; } = new();
    }

    /// <summary>
    /// Depot info from SteamKit2
    /// </summary>
    public class DepotInfoResult
    {
        public string DepotId { get; set; } = "";
        public string? Language { get; set; }
        public string? OsList { get; set; }
        public string? OsArch { get; set; }
        public bool LowViolence { get; set; }
        public string? DepotFromApp { get; set; }
        public string? SharedInstall { get; set; }
        public string? DlcAppId { get; set; }
        public bool HasManifests { get; set; }
        public string? ManifestGid { get; set; }
        public long Size { get; set; }
        public long DownloadSize { get; set; }
    }
}

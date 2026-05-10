using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace HubcapManifestApp.ViewModels
{
    // Library loading, scanning, background enrichment, and single-game add.
    public partial class LibraryViewModel
    {
        public async Task LoadFromCache()
        {
            _logger.Info("[LoadFromCache] ========== START ==========");
            FilterOptions.Clear();
            FilterOptions.Add("All");
            FilterOptions.Add("Lua Only");
            FilterOptions.Add("Steam Games Only");

            if (!FilterOptions.Contains(SelectedFilter))
            {
                SelectedFilter = "All";
            }

            // ── Heavy I/O off the UI thread ──────────────────────────────
            // DB read, file-exists pruning, and Steam .acf scanning all
            // happen on a worker thread so the UI stays responsive.
            var cachedItems = await Task.Run(() => _dbService.GetAllLibraryItems());
            _logger.Info($"[LoadFromCache] Loaded {cachedItems.Count} items from database");

            if (cachedItems.Count > 0)
            {
                lock (_allItemsLock)
                {
                    _allItems = cachedItems;
                }

                // Prune + mark on background thread — both do disk I/O.
                await Task.Run(() =>
                {
                    PruneMissingLuaFiles();
                    MarkLuaInstallStatus();
                });

                TotalLua = _allItems.Count(i => i.ItemType == LibraryItemType.Lua);
                TotalSteamGames = _allItems.Count(i => i.ItemType == LibraryItemType.SteamGame);
                TotalSize = _allItems.Sum(i => i.SizeBytes);

                ApplyFilters();
                StatusMessage = $"{_allItems.Count} item(s) - Click 'Update Library' to refresh";

                _ = LoadMissingIconsAsync();
                _ = CacheImagesAsync();
                // Previously the resolver only ran during a full scan, so any lua whose
                // name never resolved first time around stuck as the AppID forever. Run it
                // on cache load too — the app list is cached, so this is basically free.
                _ = ResolveUnknownLuaNamesAsync(persist: false);
                _ = MarkLuaForeignFormatAsync();

                // Also detect any lua files the user added manually since last scan.
                // We only look at filenames so it's near-instant, and we only kick off a
                // full scan if we actually find something new.
                _ = DetectNewLuaFilesAsync();
            }
            else
            {
                StatusMessage = "Library is empty - Click 'Update Library' to scan";
                lock (_allItemsLock)
                {
                    _allItems.Clear();
                }
                ApplyFilters();
            }

            _logger.Info("[LoadFromCache] ========== END ==========");
        }

        [RelayCommand]
        public async Task RefreshLibrary()
        {
            _logger.Info("[RefreshLibrary] ========== USER CLICKED REFRESH ==========");
            await RefreshLibrary(forceFullScan: false);
        }

        public async Task RefreshLibrary(bool forceFullScan)
        {
            _logger.Info($"[RefreshLibrary] called (forceFullScan={forceFullScan})");
            IsLoading = true;
            StatusMessage = "Loading library...";

            var settings = _settingsService.LoadSettings();

            FilterOptions.Clear();
            FilterOptions.Add("All");
            FilterOptions.Add("Lua Only");
            FilterOptions.Add("Steam Games Only");

            // Reset filter to "All" if current filter doesn't exist in new options
            if (!FilterOptions.Contains(SelectedFilter))
            {
                SelectedFilter = "All";
            }

            try
            {
                var hasRecentCache = !forceFullScan && _dbService.HasRecentData(TimeSpan.FromMinutes(5));
                _logger.Debug($"[LibraryViewModel] Cache check: forceFullScan={forceFullScan}, hasRecentCache={hasRecentCache}");
                if (hasRecentCache)
                {
                    _logger.Info("[RefreshLibrary] Loading library from database cache (fast path)");
                    var cachedItems = await Task.Run(() => _dbService.GetAllLibraryItems());
                    _logger.Info($"[RefreshLibrary] DB returned {cachedItems.Count} items");

                    // Only use cache if it has items
                    if (cachedItems.Count > 0)
                    {
                        lock (_allItemsLock)
                        {
                            _allItems = cachedItems;
                        }

                        // Same post-load sweeps as LoadFromCache: prune missing luas,
                        // flip IsInstalledOnSteam from a fresh .acf scan, resolve names,
                        // detect format mismatches, and pick up newly-dropped luas.
                        // Without this the Refresh button was a no-op inside the 5-min
                        // cache window.
                        _logger.Info("[RefreshLibrary] cache-path: calling PruneMissingLuaFiles...");
                        await Task.Run(() =>
                        {
                            PruneMissingLuaFiles();
                            _logger.Info("[RefreshLibrary] cache-path: calling MarkLuaInstallStatus...");
                            MarkLuaInstallStatus();
                        });
                        _logger.Info("[RefreshLibrary] cache-path: sweeps complete");

                        TotalLua = _allItems.Count(i => i.ItemType == LibraryItemType.Lua);
                        TotalSteamGames = _allItems.Count(i => i.ItemType == LibraryItemType.SteamGame);
                        TotalSize = _allItems.Sum(i => i.SizeBytes);

                        ApplyFilters();
                        StatusMessage = $"{_allItems.Count} item(s) loaded from cache";
                        IsLoading = false;

                        // Background work: icons, cache warm, lua name resolution,
                        // foreign-format detection, manual-drop detection.
                        _ = LoadMissingIconsAsync();
                        _ = CacheImagesAsync();
                        _ = ResolveUnknownLuaNamesAsync(persist: false);
                        _ = MarkLuaForeignFormatAsync();
                        _ = DetectNewLuaFilesAsync();
                        return;
                    }
                    else
                    {
                        _logger.Info("Database cache is empty, performing full scan instead");
                    }
                }

                _logger.Info("Performing full library scan");
                lock (_allItemsLock)
                {
                    _allItems.Clear();
                }

                // Validate and clean up deleted Lua files from database
                var stpluginPath = _steamService.GetStPluginPath();
                if (!string.IsNullOrEmpty(stpluginPath))
                {
                    var dbItems = _dbService.GetAllLibraryItems();

                    // Validate Lua files
                    foreach (var item in dbItems.Where(i => i.ItemType == LibraryItemType.Lua))
                    {
                        var luaFile = Path.Combine(stpluginPath, $"{item.AppId}.lua");
                        var disabledFile = Path.Combine(stpluginPath, $"{item.AppId}.lua.disabled");

                        // If neither file exists, remove from database
                        if (!File.Exists(luaFile) && !File.Exists(disabledFile))
                        {
                            _logger.Info($"Removing deleted Lua file from library: {item.AppId}");
                            _dbService.DeleteLibraryItem(item.AppId);
                        }
                    }
                }

                var steamGames = await Task.Run(() => _steamGamesService.GetInstalledGames());
                _logger.Info($"[LibraryViewModel] Steam games found: {steamGames.Count}");
                var steamGameDict = steamGames.ToDictionary(g => g.AppId, g => g);

                var luaGames = await Task.Run(() => _fileInstallService.GetInstalledGames());
                _logger.Info($"[LibraryViewModel] Lua games found: {luaGames.Count}");

                foreach (var mod in luaGames)
                {
                    var cachedManifest = _cacheService.GetCachedManifest(mod.AppId);
                    if (cachedManifest != null)
                    {
                        _logger.Debug($"[LibraryViewModel] Lua game {mod.AppId}: name from manifest cache = {cachedManifest.Name}");
                        mod.Name = cachedManifest.Name;
                        mod.Description = cachedManifest.Description;
                        mod.Version = cachedManifest.Version;
                        mod.IconUrl = cachedManifest.IconUrl;
                    }
                    else if (steamGameDict.TryGetValue(mod.AppId, out var matchedSteamGame))
                    {
                        _logger.Debug($"[LibraryViewModel] Lua game {mod.AppId}: name from Steam manifest = {matchedSteamGame.Name}");
                        mod.Name = matchedSteamGame.Name;
                    }
                    else
                    {
                        _logger.Debug($"[LibraryViewModel] Lua game {mod.AppId}: no local name available, will resolve from API");
                    }

                    if (steamGameDict.TryGetValue(mod.AppId, out var steamGame))
                    {
                        mod.SizeBytes = steamGame.SizeOnDisk;
                    }
                    else
                    {
                        mod.SizeBytes = 0;
                    }

                    var item = LibraryItem.FromGame(mod);
                    lock (_allItemsLock) { _allItems.Add(item); }
                }

                // Load icons in background with throttling
                List<LibraryItem> luaSnapshot;
                lock (_allItemsLock) { luaSnapshot = _allItems.Where(i => i.ItemType == LibraryItemType.Lua).ToList(); }
                _ = Task.Run(async () =>
                {
                    var semaphore = new System.Threading.SemaphoreSlim(5, 5); // Limit to 5 concurrent downloads
                    var tasks = luaSnapshot.Select(async item =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            _logger.Info($"Loading icon for {item.Name} (AppId: {item.AppId})");
                            var cdnIconUrl = _steamGamesService.GetSteamCdnIconUrl(item.AppId);
                            _logger.Debug($"Using CDN URL: {cdnIconUrl}");

                            var iconPath = await _cacheService.GetSteamGameIconAsync(item.AppId, null, cdnIconUrl);

                            if (!string.IsNullOrEmpty(iconPath))
                            {
                                _logger.Info($"✓ Icon loaded successfully for {item.Name}: {iconPath}");
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    item.CachedIconPath = iconPath;
                                });
                                _dbService.UpdateIconPath(item.AppId, iconPath);
                            }
                            else
                            {
                                _logger.Warning($"✗ Failed to load icon for {item.Name} - No path returned");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"✗ Exception loading icon for {item.Name}: {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    await Task.WhenAll(tasks);
                });

                // Add Steam games that don't have lua files
                try
                {
                    if (steamGames.Count == 0)
                    {
                        StatusMessage = "No Steam games found. Check Steam installation.";
                    }

                    var luaAppIds = new HashSet<string>();
                    lock (_allItemsLock)
                    {
                        luaAppIds = _allItems.Where(i => i.ItemType == LibraryItemType.Lua)
                                              .Select(i => i.AppId)
                                              .ToHashSet();
                    }

                    var steamOnlyCount = 0;
                    foreach (var steamGame in steamGames)
                    {
                        if (!luaAppIds.Contains(steamGame.AppId))
                        {
                            var item = LibraryItem.FromSteamGame(steamGame);
                            lock (_allItemsLock) { _allItems.Add(item); }
                            steamOnlyCount++;
                        }
                    }
                    _logger.Info($"[LibraryViewModel] Added {steamOnlyCount} Steam-only games (no lua files)");

                    // Load Steam game icons in background with throttling
                    List<LibraryItem> steamSnapshot;
                    lock (_allItemsLock) { steamSnapshot = _allItems.Where(i => i.ItemType == LibraryItemType.SteamGame).ToList(); }
                    _ = Task.Run(async () =>
                    {
                        var semaphore = new System.Threading.SemaphoreSlim(5, 5);
                        var tasks = steamSnapshot.Select(async item =>
                        {
                            await semaphore.WaitAsync();
                            try
                            {
                                var localIconPath = _steamGamesService.GetLocalIconPath(item.AppId);
                                var cdnIconUrl = _steamGamesService.GetSteamCdnIconUrl(item.AppId);
                                var iconPath = await _cacheService.GetSteamGameIconAsync(item.AppId, localIconPath, cdnIconUrl);

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    item.CachedIconPath = iconPath;
                                });
                                if (!string.IsNullOrEmpty(iconPath))
                                {
                                    _dbService.UpdateIconPath(item.AppId, iconPath);
                                }
                            }
                            catch { }
                            finally
                            {
                                semaphore.Release();
                            }
                        });

                        await Task.WhenAll(tasks);
                    });
                }
                catch (Exception ex)
                {
                    _notificationService.ShowError($"Failed to load Steam games: {ex.Message}");
                }

                // Flip IsInstalledOnSteam (runtime-only) on every lua so the Install
                // button shows for luas whose game files aren't actually on disk.
                MarkLuaInstallStatus();
                _ = MarkLuaForeignFormatAsync();

                TotalLua = _allItems.Count(i => i.ItemType == LibraryItemType.Lua);
                TotalSteamGames = _allItems.Count(i => i.ItemType == LibraryItemType.SteamGame);
                TotalSize = _allItems.Sum(i => i.SizeBytes);
                _logger.Info($"[LibraryViewModel] Library totals: {TotalLua} lua, {TotalSteamGames} steam games, {_allItems.Count} total, {TotalSize} bytes");

                ApplyFilters();

                StatusMessage = $"{_allItems.Count} item(s) loaded";

                // Resolve unknown game names in the background, then save to DB.
                _ = ResolveUnknownLuaNamesAsync(persist: true);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading library: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Removes any lua-type items from _allItems whose corresponding .lua file no
        /// longer exists in stplug-in. Runs synchronously on cache load so deleted luas
        /// never show up as "installed" in the Library even for a frame. Also cleans the
        /// DB so the cache stays consistent.
        /// </summary>
        private void PruneMissingLuaFiles()
        {
            try
            {
                var stpluginPath = _steamService.GetStPluginPath();
                if (string.IsNullOrEmpty(stpluginPath) || !Directory.Exists(stpluginPath))
                    return;

                var toRemove = new List<LibraryItem>();
                List<LibraryItem> luaItems;
                lock (_allItemsLock) { luaItems = _allItems.Where(i => i.ItemType == LibraryItemType.Lua).ToList(); }
                foreach (var item in luaItems)
                {
                    var luaFile = Path.Combine(stpluginPath, $"{item.AppId}.lua");
                    var disabledFile = Path.Combine(stpluginPath, $"{item.AppId}.lua.disabled");
                    if (!File.Exists(luaFile) && !File.Exists(disabledFile))
                    {
                        toRemove.Add(item);
                    }
                }

                if (toRemove.Count > 0)
                {
                    lock (_allItemsLock)
                    {
                        foreach (var item in toRemove)
                        {
                            _allItems.Remove(item);
                        }
                    }
                    foreach (var item in toRemove)
                    {
                        try { _dbService.DeleteLibraryItem(item.AppId); } catch { }
                    }
                    _logger.Info($"[LibraryViewModel] Pruned {toRemove.Count} lua item(s) whose files are no longer in stplug-in.");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[LibraryViewModel] PruneMissingLuaFiles failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Peeks at the first few hundred bytes of every Lua file on disk and flags
        /// items whose header doesn't match the tool's expected `-- {AppId}'s Lua and
        /// Manifest Created by Hubcap` signature (or the legacy `Created by Morrenus`
        /// signature from pre-rebrand builds). Runs in the background so cold loads
        /// with ~1k lua files stay responsive. Runtime-only; never written to DB.
        /// </summary>
        private Task MarkLuaForeignFormatAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var stpluginPath = _steamService.GetStPluginPath();
                    if (string.IsNullOrEmpty(stpluginPath) || !Directory.Exists(stpluginPath))
                        return;

                    List<LibraryItem> luaItems;
                    lock (_allItemsLock) { luaItems = _allItems.Where(i => i.ItemType == LibraryItemType.Lua).ToList(); }

                    // Compute all flags on this thread, then dispatch once.
                    var updates = new List<(LibraryItem item, bool foreign)>();
                    foreach (var item in luaItems)
                    {
                        var luaPath = Path.Combine(stpluginPath, $"{item.AppId}.lua");
                        if (!File.Exists(luaPath))
                        {
                            var disabled = Path.Combine(stpluginPath, $"{item.AppId}.lua.disabled");
                            if (File.Exists(disabled)) luaPath = disabled;
                            else continue;
                        }

                        bool foreign = true;
                        try
                        {
                            // Read the first 512 bytes — more than enough to catch the header line.
                            using var fs = new FileStream(luaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            var buf = new byte[512];
                            var read = fs.Read(buf, 0, buf.Length);
                            var head = System.Text.Encoding.UTF8.GetString(buf, 0, read);
                            // Accept either the new Hubcap header or the legacy Morrenus header
                            // (pre-rebrand luas are still "ours" and should not be flagged as foreign).
                            if (head.IndexOf("Created by Hubcap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                head.IndexOf("Created by Morrenus", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foreign = false;
                            }
                        }
                        catch
                        {
                            // Unreadable file = leave it as foreign so the user sees the refetch option.
                        }

                        if (item.IsForeignFormat != foreign)
                        {
                            updates.Add((item, foreign));
                        }
                    }

                    // Single dispatch for all property updates.
                    if (updates.Count > 0)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var (item, foreign) in updates)
                            {
                                item.IsForeignFormat = foreign;
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[LibraryViewModel] MarkLuaForeignFormatAsync failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Sets IsInstalledOnSteam on every Lua item by cross-referencing the installed
        /// Steam games list (scanned from Steam's appmanifest_*.acf files). EXTREMELY
        /// verbose logging on purpose so we can debug the stale-install-state bug.
        /// </summary>
        public void MarkLuaInstallStatus()
        {
            _logger.Info("[MarkLuaInstallStatus] ========== START ==========");
            try
            {
                _logger.Info("[MarkLuaInstallStatus] Calling _steamGamesService.GetInstalledGames()...");
                var installedGames = _steamGamesService.GetInstalledGames();
                _logger.Info($"[MarkLuaInstallStatus] GetInstalledGames returned {installedGames.Count} games.");

                // Log EVERY AppID returned so we can see if our target is in the list.
                var allAppIds = string.Join(", ", installedGames.Select(g => g.AppId).OrderBy(id => id));
                _logger.Info($"[MarkLuaInstallStatus] AppIDs: [{allAppIds}]");

                var installed = installedGames
                    .Select(g => g.AppId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                _logger.Info($"[MarkLuaInstallStatus] Built HashSet with {installed.Count} entries.");

                var allLuaItems = new List<LibraryItem>();
                lock (_allItemsLock) { allLuaItems = _allItems.Where(i => i.ItemType == LibraryItemType.Lua).ToList(); }
                _logger.Info($"[MarkLuaInstallStatus] _allItems has {allLuaItems.Count} lua items (under lock).");

                // Compute all changes on this thread, then dispatch once.
                var flipsToApply = new List<(LibraryItem item, bool newValue)>();
                var luaCount = 0;
                var unchanged = 0;
                foreach (var item in allLuaItems)
                {
                    luaCount++;
                    var newValue = installed.Contains(item.AppId);
                    var oldValue = item.IsInstalledOnSteam;

                    _logger.Info($"[MarkLuaInstallStatus] lua item AppId='{item.AppId}' Name='{item.Name}' " +
                                $"currentIsInstalledOnSteam={oldValue} newValueFromSteamScan={newValue}");

                    if (newValue != oldValue)
                    {
                        flipsToApply.Add((item, newValue));
                    }
                    else
                    {
                        unchanged++;
                    }
                }

                // Single dispatch for all property flips — avoids per-item
                // UI-thread round-trips that were causing micro-stutters.
                if (flipsToApply.Count > 0)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        foreach (var (item, newValue) in flipsToApply)
                        {
                            item.IsInstalledOnSteam = newValue;
                            _logger.Info($"[MarkLuaInstallStatus]   -> flipped: AppId='{item.AppId}' to IsInstalledOnSteam={newValue}, IsLuaUninstalled is now {item.IsLuaUninstalled}");
                        }
                    });
                }

                var flippedTrue = flipsToApply.Count(f => f.newValue);
                var flippedFalse = flipsToApply.Count - flippedTrue;
                _logger.Info($"[MarkLuaInstallStatus] SUMMARY: {luaCount} lua items checked, " +
                            $"{flippedTrue} flipped to installed, {flippedFalse} flipped to uninstalled, {unchanged} unchanged.");
            }
            catch (Exception ex)
            {
                _logger.Error($"[MarkLuaInstallStatus] FAILED: {ex.Message}\n{ex.StackTrace}");
            }
            _logger.Info("[MarkLuaInstallStatus] ========== END ==========");
        }

        /// <summary>
        /// Lightweight check: list the lua filenames in the stplug-in folder and compare
        /// them to the current _allItems set. If there are any we don't know about yet,
        /// trigger a full scan in the background so the Library picks them up without the
        /// user having to manually click Refresh. Runs on every navigate-to-Library.
        /// </summary>
        private Task DetectNewLuaFilesAsync()
        {
            return Task.Run(async () =>
            {
                try
                {
                    var stpluginPath = _steamService.GetStPluginPath();
                    if (string.IsNullOrEmpty(stpluginPath) || !Directory.Exists(stpluginPath))
                        return;

                    var onDisk = Directory.GetFiles(stpluginPath, "*.lua")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    HashSet<string> known;
                    lock (_allItemsLock)
                    {
                        known = _allItems
                            .Where(i => i.ItemType == LibraryItemType.Lua)
                            .Select(i => i.AppId)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }

                    var missingFromLibrary = onDisk.Except(known).ToList();
                    var deletedFromDisk = known.Except(onDisk).ToList();

                    if (missingFromLibrary.Count > 0 || deletedFromDisk.Count > 0)
                    {
                        _logger.Info($"[LibraryViewModel] Detected changes in stplug-in ({missingFromLibrary.Count} new, {deletedFromDisk.Count} removed) — refreshing library.");
                        await Application.Current.Dispatcher.InvokeAsync(() => RefreshLibrary(forceFullScan: true));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[LibraryViewModel] DetectNewLuaFilesAsync failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Runs a background pass over every Lua item whose Name is still just the AppID
        /// and tries to resolve it via the Steam app list (which is cached in-memory and
        /// on disk, so this is almost always free). Safe to call from both LoadFromCache
        /// and the full-scan path — the worst case is no items need resolving.
        /// </summary>
        private Task ResolveUnknownLuaNamesAsync(bool persist)
        {
            return Task.Run(async () =>
            {
                try
                {
                    List<LibraryItem> unknownItems;
                    lock (_allItemsLock)
                    {
                        unknownItems = _allItems
                            .Where(i => i.ItemType == LibraryItemType.Lua &&
                                       (i.Name == i.AppId || i.Name == AppConstants.UnknownGame || string.IsNullOrWhiteSpace(i.Name)))
                            .ToList();
                    }

                    if (unknownItems.Count > 0)
                    {
                        _logger.Info($"[LibraryViewModel] Resolving {unknownItems.Count} unknown lua name(s) from cached app list...");
                        var appList = await _steamApiService.GetAppListAsync();
                        if (appList != null)
                        {
                            var resolvedCount = 0;
                            foreach (var item in unknownItems)
                            {
                                var name = _steamApiService.GetGameName(item.AppId, appList);
                                if (name != AppConstants.UnknownGame && name != item.AppId)
                                {
                                    var captured = item;
                                    var capturedName = name;
                                    Application.Current.Dispatcher.Invoke(() => { captured.Name = capturedName; });
                                    resolvedCount++;
                                }
                            }
                            _logger.Info($"[LibraryViewModel] Resolved {resolvedCount}/{unknownItems.Count} lua name(s).");
                        }
                    }

                    if (persist)
                    {
                        List<LibraryItem> itemsToSave;
                        lock (_allItemsLock) { itemsToSave = _allItems.ToList(); }
                        _logger.Info($"Saving {itemsToSave.Count} items to database");
                        _dbService.BulkUpsertLibraryItems(itemsToSave);
                        _logger.Info("Database save complete");
                    }
                    else
                    {
                        // Cache-path: only save if we actually updated anything, so we don't
                        // rewrite the whole DB on every navigation.
                        List<LibraryItem> dirtyItems;
                        lock (_allItemsLock)
                        {
                            dirtyItems = _allItems
                                .Where(i => i.ItemType == LibraryItemType.Lua && i.Name != i.AppId)
                                .ToList();
                        }
                        if (dirtyItems.Count > 0)
                        {
                            _dbService.BulkUpsertLibraryItems(dirtyItems);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed lua name resolution: {ex.Message}");
                    if (persist)
                    {
                        List<LibraryItem> fallbackItems;
                        lock (_allItemsLock) { fallbackItems = _allItems.ToList(); }
                        try { _dbService.BulkUpsertLibraryItems(fallbackItems); } catch { }
                    }
                }
            });
        }

        private async Task LoadMissingIconsAsync()
        {
            List<LibraryItem> itemsMissingIcons;
            lock (_allItemsLock) { itemsMissingIcons = _allItems.Where(i => string.IsNullOrEmpty(i.CachedIconPath)).ToList(); }
            if (itemsMissingIcons.Count == 0)
                return;

            _logger.Info($"Loading {itemsMissingIcons.Count} missing icons in background");

            var semaphore = new System.Threading.SemaphoreSlim(5, 5);
            var tasks = itemsMissingIcons.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    string? iconPath = null;
                    if (item.ItemType == LibraryItemType.SteamGame)
                    {
                        var localIconPath = _steamGamesService.GetLocalIconPath(item.AppId);
                        var cdnIconUrl = _steamGamesService.GetSteamCdnIconUrl(item.AppId);
                        iconPath = await _cacheService.GetSteamGameIconAsync(item.AppId, localIconPath, cdnIconUrl);
                    }
                    else
                    {
                        var cdnIconUrl = _steamGamesService.GetSteamCdnIconUrl(item.AppId);
                        iconPath = await _cacheService.GetSteamGameIconAsync(item.AppId, null, cdnIconUrl);
                    }

                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            item.CachedIconPath = iconPath;
                        });

                        // Update database with new icon path
                        _dbService.UpdateIconPath(item.AppId, iconPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to load icon for {item.Name}: {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            _logger.Info("Background icon loading complete");
        }

        // Method to instantly add newly installed game to library (no full scan needed)
        public async Task AddGameToLibraryAsync(string appId)
        {
            try
            {
                _logger.Info($"Adding game {appId} to library instantly");

                // Check if already exists
                bool alreadyExists;
                lock (_allItemsLock)
                {
                    alreadyExists = _allItems.Any(i => i.AppId == appId);
                }
                if (alreadyExists)
                {
                    _logger.Info($"Game {appId} already in library");
                    return;
                }

                // Load the game data
                var luaGames = await Task.Run(() => _fileInstallService.GetInstalledGames());
                var game = luaGames.FirstOrDefault(g => g.AppId == appId);

                if (game == null)
                {
                    _logger.Warning($"Could not find installed game {appId}");
                    return;
                }

                // Get Steam app list for name enrichment
                var steamAppList = await _steamApiService.GetAppListAsync();

                // Try cache first
                var cachedManifest = _cacheService.GetCachedManifest(appId);
                if (cachedManifest != null)
                {
                    game.Name = cachedManifest.Name;
                    game.Description = cachedManifest.Description;
                    game.Version = cachedManifest.Version;
                    game.IconUrl = cachedManifest.IconUrl;
                }
                else
                {
                    // Get name from Steam app list. Never save the literal "Unknown Game"
                    // sentinel — fall back to the AppId so the background resolver can
                    // retry on the next load (its filter picks up Name == AppId).
                    var resolved = _steamApiService.GetGameName(appId, steamAppList);
                    game.Name = (resolved == AppConstants.UnknownGame) ? appId : resolved;
                }

                // Check if game is installed via Steam for size
                var steamGames = await Task.Run(() => _steamGamesService.GetInstalledGames());
                var steamGame = steamGames.FirstOrDefault(g => g.AppId == appId);
                if (steamGame != null)
                {
                    game.SizeBytes = steamGame.SizeOnDisk;
                }

                // Create library item
                var item = LibraryItem.FromGame(game);

                // Add to memory
                lock (_allItemsLock)
                {
                    _allItems.Add(item);
                }

                // Save to database
                _dbService.UpsertLibraryItem(item);

                // Update UI
                ApplyFilters();
                lock (_allItemsLock)
                {
                    TotalLua = _allItems.Count(i => i.ItemType == LibraryItemType.Lua);
                    TotalSize = _allItems.Sum(i => i.SizeBytes);
                }

                _logger.Info($"✓ Game {appId} added to library");

                // Load icon in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var cdnIconUrl = _steamGamesService.GetSteamCdnIconUrl(appId);
                        var iconPath = await _cacheService.GetSteamGameIconAsync(appId, null, cdnIconUrl);

                        if (!string.IsNullOrEmpty(iconPath))
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                item.CachedIconPath = iconPath;
                            });
                            _dbService.UpdateIconPath(appId, iconPath);
                            _logger.Info($"✓ Icon loaded for {game.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to load icon for {appId}: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to add game to library: {ex.Message}");
            }
        }
    }
}

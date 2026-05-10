using HubcapManifestApp.Helpers;
using HubcapManifestApp.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace HubcapManifestApp.ViewModels
{
    public partial class StoreViewModel
    {
        private async Task LoadGamesAsync()
        {
            var settings = _settingsService.LoadSettings();

            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                StatusMessage = "Please enter API key in settings";
                return;
            }

            IsLoading = true;
            StatusMessage = "Loading games...";

            try
            {
                var result = await _manifestApiService.GetLibraryAsync(
                    settings.ApiKey,
                    limit: PageSize,
                    offset: CurrentOffset,
                    sortBy: SortBy);

                if (result != null && result.Games.Count > 0)
                {
                    var newGames = new ObservableCollection<LibraryGame>();

                    foreach (var game in result.Games)
                    {
                        game.PropertyChanged += (s, e) =>
                        {
                            if (e.PropertyName == nameof(LibraryGame.IsSelected))
                                SelectedCount = Games.Count(g => g.IsSelected);
                        };
                        newGames.Add(game);
                    }

                    Games = newGames;

                    TotalCount = result.TotalCount;
                    TotalPages = (int)System.Math.Ceiling((double)TotalCount / PageSize);

                    CanGoPrevious = CurrentPage > 1;
                    CanGoNext = CurrentPage < TotalPages;
                    UpdatePageNumbers();

                    var startIndex = CurrentOffset + 1;
                    var endIndex = System.Math.Min(CurrentOffset + result.Games.Count, TotalCount);
                    StatusMessage = $"Showing {startIndex}-{endIndex} of {TotalCount} games (Page {CurrentPage} of {TotalPages})";

                    // Check installation status
                    UpdateInstallationStatus(result.Games);

                    // Load all icons in parallel
                    _ = LoadAllGameIconsAsync(result.Games);
                }
                else
                {
                    StatusMessage = "No games available";
                    TotalCount = 0;
                    TotalPages = 0;
                    CanGoPrevious = false;
                    CanGoNext = false;
                    UpdatePageNumbers();
                }
            }
            catch (System.Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                MessageBoxHelper.Show(
                    $"Failed to load games: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task LoadAllGameIconsAsync(List<LibraryGame> games)
        {
            // Create tasks for all games
            var iconTasks = games
                .Where(g => !string.IsNullOrEmpty(g.HeaderImage))
                .Select(game => LoadGameIconAsync(game))
                .ToList();

            // Wait for all to complete (with semaphore limiting concurrency)
            await Task.WhenAll(iconTasks);
        }

        private async Task LoadGameIconAsync(LibraryGame game)
        {
            await _iconLoadSemaphore.WaitAsync();
            try
            {
                var iconPath = await _cacheService.GetIconAsync(game.GameId, game.HeaderImage);

                // Update on UI thread
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    game.CachedIconPath = iconPath;
                });
            }
            catch
            {
                // Silently fail for individual icons
            }
            finally
            {
                _iconLoadSemaphore.Release();
            }
        }

        private async void UpdateInstallationStatus(List<LibraryGame> games)
        {
            var settings = _settingsService.LoadSettings();

            // Gather file-system data on a background thread
            var (stpluginLuaAppIds, libraryAppIds, customDirAppIds) = await Task.Run(() =>
            {
                var stplugin = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var library = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var customDir = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (settings.Mode == ToolMode.SteamTools)
                {
                    try
                    {
                        var stpluginPath = _steamService.GetStPluginPath();
                        if (!string.IsNullOrEmpty(stpluginPath) && Directory.Exists(stpluginPath))
                        {
                            foreach (var file in Directory.EnumerateFiles(stpluginPath, "*.lua"))
                            {
                                stplugin.Add(Path.GetFileNameWithoutExtension(file));
                            }
                            foreach (var file in Directory.EnumerateFiles(stpluginPath, "*.lua.disabled"))
                            {
                                var name = Path.GetFileNameWithoutExtension(file);
                                if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                                    name = name.Substring(0, name.Length - 4);
                                stplugin.Add(name);
                            }
                        }
                    }
                    catch { /* treat as empty set */ }
                }
                else
                {
                    var libraryItems = _libraryDatabaseService.GetAllLibraryItems();
                    foreach (var i in libraryItems) library.Add(i.AppId);

                    if (!string.IsNullOrEmpty(settings.CustomGameDirectory) && Directory.Exists(settings.CustomGameDirectory))
                    {
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(settings.CustomGameDirectory))
                            {
                                var ext = Path.GetExtension(file).ToLowerInvariant();
                                if (ext == ".zip" || ext == ".lua")
                                {
                                    customDir.Add(Path.GetFileNameWithoutExtension(file));
                                }
                            }
                        }
                        catch { }
                    }
                }

                return (stplugin, library, customDir);
            });

            foreach (var game in games)
            {
                bool installed;
                if (settings.Mode == ToolMode.SteamTools)
                {
                    installed = stpluginLuaAppIds.Contains(game.GameId);
                }
                else
                {
                    var installedInfo = _manifestStorageService.GetInstalledManifest(game.GameId);
                    installed = installedInfo != null
                        || libraryAppIds.Contains(game.GameId)
                        || customDirAppIds.Contains(game.GameId);
                }

                game.IsInstalled = installed;
                game.HasUpdate = false;
            }
        }
    }
}

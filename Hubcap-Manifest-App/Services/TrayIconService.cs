using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using MahApps.Metro.IconPacks;
using HubcapManifestApp.Models;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Services
{
    /// <summary>
    /// Tray icon service that uses Hardcodet.NotifyIcon.Wpf so the context menu
    /// is a real WPF ContextMenu and inherits the app's themed
    /// ModernContextMenuStyle / ModernMenuItemStyle automatically. No custom
    /// WinForms renderers needed.
    /// </summary>
    public class TrayIconService : IDisposable
    {
        private TaskbarIcon? _taskbarIcon;
        private readonly Window _mainWindow;
        private readonly SettingsService _settingsService;
        private readonly RecentGamesService _recentGamesService;
        private readonly SteamService _steamService;
        private readonly MainViewModel _mainViewModel;
        private readonly ThemeService _themeService;

        public TrayIconService(
            Window mainWindow,
            SettingsService settingsService,
            RecentGamesService recentGamesService,
            SteamService steamService,
            MainViewModel mainViewModel,
            ThemeService themeService)
        {
            _mainWindow = mainWindow;
            _settingsService = settingsService;
            _recentGamesService = recentGamesService;
            _steamService = steamService;
            _mainViewModel = mainViewModel;
            _themeService = themeService;
        }

        public void Initialize()
        {
            var settings = _settingsService.LoadSettings();

            _taskbarIcon = new TaskbarIcon
            {
                ToolTipText = "Hubcap Manifest App",
                Visibility = settings.AlwaysShowTrayIcon ? Visibility.Visible : Visibility.Collapsed,
                IconSource = LoadIcon()
            };

            // Rebuild on every open so Recent games and menu state stay fresh.
            var menu = new ContextMenu();
            menu.Opened += (_, _) => RebuildContextMenu(menu);
            RebuildContextMenu(menu);
            _taskbarIcon.ContextMenu = menu;

            _taskbarIcon.TrayMouseDoubleClick += (_, _) => ShowWindow();
        }

        private ImageSource? LoadIcon()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("HubcapManifestApp.icon.ico");
                if (stream != null)
                {
                    var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    return decoder.Frames[0];
                }

                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    return new BitmapImage(new Uri(iconPath, UriKind.Absolute));
                }
            }
            catch
            {
                // Fall through to null → TaskbarIcon will show a blank icon which is fine.
            }
            return null;
        }

        private void RebuildContextMenu(ContextMenu menu)
        {
            menu.Items.Clear();

            // Recent section
            var recentGames = _recentGamesService.GetRecentGames(5);
            if (recentGames.Count > 0)
            {
                menu.Items.Add(BuildHeader("Recent"));

                foreach (var game in recentGames)
                {
                    var item = new MenuItem { Header = game.Name };

                    // Load the game icon into a small Image if we have one cached on disk.
                    if (!string.IsNullOrEmpty(game.IconPath) && File.Exists(game.IconPath))
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.UriSource = new Uri(game.IconPath, UriKind.Absolute);
                            bitmap.DecodePixelWidth = 16;
                            bitmap.EndInit();
                            bitmap.Freeze();

                            item.Icon = new Image
                            {
                                Source = bitmap,
                                Width = 16,
                                Height = 16
                            };
                        }
                        catch
                        {
                            // Ignore icon load errors — the menu entry still works without an icon.
                        }
                    }

                    var localAppId = game.AppId;
                    var localPath = game.LocalPath;
                    item.Click += (_, _) => OpenGameLocation(localAppId, localPath);
                    menu.Items.Add(item);
                }

                menu.Items.Add(new Separator());
            }

            // Tasks section
            menu.Items.Add(BuildHeader("Tasks"));
            menu.Items.Add(BuildNavItem("Store", PackIconLucideKind.ShoppingCart));
            menu.Items.Add(BuildNavItem("Library", PackIconLucideKind.Library));
            menu.Items.Add(BuildNavItem("Downloads", PackIconLucideKind.Download));
            menu.Items.Add(BuildNavItem("Settings", PackIconLucideKind.Settings));
            menu.Items.Add(BuildNavItem("Tools", PackIconLucideKind.Wrench));

            menu.Items.Add(new Separator());

            var showItem = new MenuItem
            {
                Header = "Show App",
                Icon = BuildIcon(PackIconLucideKind.Eye)
            };
            showItem.Click += (_, _) => ShowWindow();
            menu.Items.Add(showItem);

            var exitItem = new MenuItem
            {
                Header = "Exit",
                Icon = BuildIcon(PackIconLucideKind.X)
            };
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(exitItem);
        }

        private static MenuItem BuildHeader(string text)
        {
            // Disabled bold header — WPF styled ContextMenu will fade it via the
            // ModernMenuItemStyle IsEnabled=false opacity trigger.
            return new MenuItem
            {
                Header = text,
                IsEnabled = false,
                FontWeight = FontWeights.Bold
            };
        }

        private MenuItem BuildNavItem(string pageName, PackIconLucideKind icon)
        {
            var item = new MenuItem
            {
                Header = pageName,
                Icon = BuildIcon(icon)
            };
            item.Click += (_, _) => NavigateToPage(pageName);
            return item;
        }

        private static PackIconLucide BuildIcon(PackIconLucideKind kind)
        {
            return new PackIconLucide
            {
                Kind = kind,
                Width = 14,
                Height = 14
            };
        }

        private void NavigateToPage(string pageName)
        {
            ShowWindow();
            _mainViewModel.NavigateTo(pageName);
        }

        private void OpenGameLocation(string appId, string localPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(localPath) && (File.Exists(localPath) || Directory.Exists(localPath)))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = File.Exists(localPath) ? $"/select,\"{localPath}\"" : $"\"{localPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    var stpluginPath = _steamService.GetStPluginPath();
                    if (!string.IsNullOrEmpty(stpluginPath))
                    {
                        var luaFile = Path.Combine(stpluginPath, $"{appId}.lua");
                        if (File.Exists(luaFile))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"/select,\"{luaFile}\"",
                                UseShellExecute = true
                            });
                        }
                    }
                }
            }
            catch
            {
                // Silent fail — nothing actionable to show the user.
            }
        }

        public void ShowInTray()
        {
            if (_taskbarIcon != null)
            {
                _mainWindow.Hide();
                _taskbarIcon.Visibility = Visibility.Visible;
            }
        }

        public void HideFromTray()
        {
            if (_taskbarIcon != null)
            {
                var settings = _settingsService.LoadSettings();
                if (!settings.AlwaysShowTrayIcon)
                {
                    _taskbarIcon.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void ShowWindow()
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            HideFromTray();
        }

        private void ExitApplication()
        {
            _taskbarIcon?.Dispose();
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            _taskbarIcon?.Dispose();
        }
    }
}

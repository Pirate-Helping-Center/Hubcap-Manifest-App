using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using HubcapManifestApp.Models;
using HubcapManifestApp.Services;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Views.Dialogs
{
    public partial class ThemeEditorWindow : Window
    {
        private readonly SettingsService _settingsService;
        private readonly ThemeService _themeService;
        private readonly NotificationService? _notificationService;
        private readonly ThemeEditorViewModel _vm;
        private CustomThemePreset? _watchedPreset;

        /// <summary>Exposed to the XAML for the image-fit ComboBox.</summary>
        public Array ImageFitOptions { get; } = Enum.GetValues(typeof(ThemeImageFit));

        public ThemeEditorWindow(SettingsService settingsService,
                                 ThemeService themeService,
                                 NotificationService? notificationService = null)
        {
            _settingsService = settingsService;
            _themeService = themeService;
            _notificationService = notificationService;

            InitializeComponent();

            var settings = _settingsService.LoadSettings();

            _vm = new ThemeEditorViewModel();
            foreach (var p in settings.CustomThemes)
            {
                _vm.Presets.Add(p);
            }

            // First launch / no presets yet: seed with the default preset template.
            if (_vm.Presets.Count == 0)
            {
                _vm.Presets.Add(new CustomThemePreset { Name = "My Theme" });
            }

            _vm.SelectedPreset = _vm.Presets.FirstOrDefault(p => p.Id == settings.ActiveCustomThemeId)
                                 ?? _vm.Presets[0];

            _vm.PropertyChanged += OnVmPropertyChanged;
            HookPresetChanges(_vm.SelectedPreset);

            DataContext = _vm;

            // Apply the currently-selected preset immediately so the window shows a live preview.
            _themeService.ApplyPreset(_vm.SelectedPreset);
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ThemeEditorViewModel.SelectedPreset))
            {
                HookPresetChanges(_vm.SelectedPreset);
                if (_vm.SelectedPreset != null)
                {
                    _themeService.ApplyPreset(_vm.SelectedPreset);
                }
            }
        }

        private void HookPresetChanges(CustomThemePreset? preset)
        {
            if (_watchedPreset != null)
            {
                _watchedPreset.PropertyChanged -= OnPresetPropertyChanged;
                foreach (var stop in _watchedPreset.AccentGradientStops)
                {
                    stop.PropertyChanged -= OnGradientStopPropertyChanged;
                }
            }
            _watchedPreset = preset;
            if (_watchedPreset != null)
            {
                _watchedPreset.PropertyChanged += OnPresetPropertyChanged;
                foreach (var stop in _watchedPreset.AccentGradientStops)
                {
                    stop.PropertyChanged += OnGradientStopPropertyChanged;
                }
            }
        }

        private void OnPresetPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_vm.SelectedPreset == null) return;

            // Live-apply on every edit. We also rehook gradient stops in case the list itself changed.
            if (e.PropertyName == nameof(CustomThemePreset.AccentGradientStops))
            {
                foreach (var stop in _vm.SelectedPreset.AccentGradientStops)
                {
                    stop.PropertyChanged -= OnGradientStopPropertyChanged;
                    stop.PropertyChanged += OnGradientStopPropertyChanged;
                }
            }

            _themeService.ApplyPreset(_vm.SelectedPreset);
        }

        private void OnGradientStopPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_vm.SelectedPreset != null) _themeService.ApplyPreset(_vm.SelectedPreset);
        }

        // ---- Title bar window chrome ----
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            // Closing without "Save" restores whatever was active before.
            var settings = _settingsService.LoadSettings();
            _themeService.ApplyTheme(settings.Theme, settings);
            Close();
        }

        // ---- Preset CRUD ----
        private void NewPreset_Click(object sender, RoutedEventArgs e)
        {
            var preset = new CustomThemePreset { Name = $"Theme {_vm.Presets.Count + 1}" };
            _vm.Presets.Add(preset);
            _vm.SelectedPreset = preset;
        }

        private void DuplicatePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset == null) return;

            // Round-trip through JSON for a deep clone that also brings gradient stops along.
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(_vm.SelectedPreset);
            var clone = Newtonsoft.Json.JsonConvert.DeserializeObject<CustomThemePreset>(json);
            if (clone == null) return;

            clone.Id = Guid.NewGuid().ToString("N");
            clone.Name = _vm.SelectedPreset.Name + " (copy)";
            _vm.Presets.Add(clone);
            _vm.SelectedPreset = clone;
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset == null || _vm.Presets.Count <= 1) return;

            var toRemove = _vm.SelectedPreset;
            var idx = _vm.Presets.IndexOf(toRemove);
            _vm.Presets.Remove(toRemove);
            _vm.SelectedPreset = _vm.Presets[Math.Max(0, Math.Min(idx, _vm.Presets.Count - 1))];
        }

        // ---- Image pickers ----
        private string? PickImage()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose a background image",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*"
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private void ChoosePageImage_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset == null) return;
            var path = PickImage();
            if (path != null) _vm.SelectedPreset.PageBackgroundImagePath = path;
        }

        private void ClearPageImage_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset != null) _vm.SelectedPreset.PageBackgroundImagePath = null;
        }

        private void ChooseSidebarImage_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset == null) return;
            var path = PickImage();
            if (path != null) _vm.SelectedPreset.SidebarBackgroundImagePath = path;
        }

        private void ClearSidebarImage_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset != null) _vm.SelectedPreset.SidebarBackgroundImagePath = null;
        }

        // ---- Gradient stops ----
        private void AddStop_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset == null) return;

            var stops = _vm.SelectedPreset.AccentGradientStops;
            double offset;
            if (stops.Count == 0)
            {
                offset = 0;
            }
            else if (stops.Count == 1)
            {
                offset = 1;
            }
            else
            {
                // Drop the new stop into the largest gap so adding repeatedly builds a sane distribution.
                var sorted = stops.OrderBy(s => s.Offset).ToList();
                double bestGap = 0;
                double bestMid = (sorted[0].Offset + sorted[^1].Offset) / 2;
                for (int i = 0; i < sorted.Count - 1; i++)
                {
                    var gap = sorted[i + 1].Offset - sorted[i].Offset;
                    if (gap > bestGap)
                    {
                        bestGap = gap;
                        bestMid = (sorted[i].Offset + sorted[i + 1].Offset) / 2;
                    }
                }
                offset = bestMid;
            }

            var stop = new ThemeGradientStop { Offset = offset, Color = "#FFFFFFFF" };
            stop.PropertyChanged += OnGradientStopPropertyChanged;
            _vm.SelectedPreset.AccentGradientStops.Add(stop);
            // Observable list substitute: re-assign to trigger PropertyChanged on the preset.
            _vm.SelectedPreset.AccentGradientStops = _vm.SelectedPreset.AccentGradientStops.ToList();
        }

        private void RemoveStop_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset == null) return;
            if (sender is Button btn && btn.Tag is ThemeGradientStop stop)
            {
                stop.PropertyChanged -= OnGradientStopPropertyChanged;
                _vm.SelectedPreset.AccentGradientStops.Remove(stop);
                _vm.SelectedPreset.AccentGradientStops = _vm.SelectedPreset.AccentGradientStops.ToList();
            }
        }

        /// <summary>Respaces every stop evenly between 0 and 1 in their current order.</summary>
        private void DistributeStops_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset == null) return;
            var stops = _vm.SelectedPreset.AccentGradientStops;
            if (stops.Count == 0) return;
            if (stops.Count == 1)
            {
                stops[0].Offset = 0.5;
            }
            else
            {
                for (int i = 0; i < stops.Count; i++)
                {
                    stops[i].Offset = (double)i / (stops.Count - 1);
                }
            }
            _vm.SelectedPreset.AccentGradientStops = _vm.SelectedPreset.AccentGradientStops.ToList();
        }

        // ---- Export / import ----
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedPreset == null) return;

            var hadImage = !string.IsNullOrWhiteSpace(_vm.SelectedPreset.PageBackgroundImagePath)
                        || !string.IsNullOrWhiteSpace(_vm.SelectedPreset.SidebarBackgroundImagePath);

            var str = ThemeShareService.Export(_vm.SelectedPreset);
            try
            {
                Clipboard.SetText(str);
                var msg = "Theme copied to clipboard.";
                if (hadImage) msg += " Background images are NOT included in share strings.";
                _notificationService?.ShowSuccess(msg);
            }
            catch (Exception ex)
            {
                _notificationService?.ShowError($"Couldn't copy to clipboard: {ex.Message}");
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            string clip;
            try { clip = Clipboard.GetText(); }
            catch (Exception ex) { _notificationService?.ShowError($"Couldn't read clipboard: {ex.Message}"); return; }

            if (!ThemeShareService.TryImport(clip, out var imported, out var error))
            {
                _notificationService?.ShowWarning(error ?? "Import failed.");
                return;
            }

            if (imported == null) return;

            _vm.Presets.Add(imported);
            _vm.SelectedPreset = imported;
            _notificationService?.ShowSuccess($"Imported '{imported.Name}' as a new preset.");
        }

        // ---- Apply / Save ----
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            Persist();
            _notificationService?.ShowSuccess("Theme applied.");
        }

        private void SaveAndClose_Click(object sender, RoutedEventArgs e)
        {
            Persist();
            Close();
        }

        private void Persist()
        {
            if (_vm.SelectedPreset == null) return;

            var settings = _settingsService.LoadSettings();
            settings.CustomThemes = _vm.Presets.ToList();
            settings.ActiveCustomThemeId = _vm.SelectedPreset.Id;
            settings.Theme = AppTheme.Custom;
            _settingsService.SaveSettings(settings);

            _themeService.ApplyPreset(_vm.SelectedPreset);
        }

        protected override void OnClosed(EventArgs e)
        {
            HookPresetChanges(null);
            _vm.PropertyChanged -= OnVmPropertyChanged;
            base.OnClosed(e);
        }
    }
}

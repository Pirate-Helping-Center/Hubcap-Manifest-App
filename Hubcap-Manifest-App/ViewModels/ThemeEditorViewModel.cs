using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HubcapManifestApp.Models;

namespace HubcapManifestApp.ViewModels
{
    /// <summary>
    /// Lightweight host for the ThemeEditorWindow. Owns the preset list and the
    /// currently-selected preset. All live-preview plumbing lives in the code-behind
    /// (hooks PropertyChanged on the selected preset and calls ThemeService.ApplyPreset).
    /// </summary>
    public partial class ThemeEditorViewModel : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<CustomThemePreset> _presets = new();

        [ObservableProperty]
        private CustomThemePreset? _selectedPreset;
    }
}

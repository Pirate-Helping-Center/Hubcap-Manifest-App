using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;

namespace HubcapManifestApp.Models
{
    /// <summary>
    /// How a background image is fitted to its target region.
    /// </summary>
    public enum ThemeImageFit
    {
        Stretch,
        Uniform,
        UniformToFill,
        Tile,
        Center
    }

    /// <summary>
    /// Whether the button/accent color is a single solid color or a multi-stop gradient.
    /// </summary>
    public enum ThemeAccentMode
    {
        Solid,
        Gradient
    }

    /// <summary>
    /// A single stop in an accent gradient. Color is AARRGGBB hex, Offset is 0..1.
    /// </summary>
    public partial class ThemeGradientStop : ObservableObject
    {
        [ObservableProperty] private string _color = "#FFFFFFFF";
        [ObservableProperty] private double _offset;
    }

    /// <summary>
    /// A user-authored theme preset. Every color is stored as AARRGGBB hex so it survives
    /// JSON serialization and round-trips through the share string cleanly. Background
    /// image paths point to files on disk; they are NOT included in export strings
    /// (we drop them on export and flag the missing image to the user on import).
    /// Properties are observable so the Theme Editor can live-preview every edit.
    /// </summary>
    public partial class CustomThemePreset : ObservableObject
    {
        [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
        [ObservableProperty] private string _name = "New Theme";

        // ---- Backgrounds ----
        [ObservableProperty] private string _pageBackground = "#FF1b2838";
        [ObservableProperty] private string _sidebarBackground = "#FF2a475e";
        [ObservableProperty] private string _titleBarBackground = "#FF16202d";
        [ObservableProperty] private string _cardBackground = "#FF16202d";
        [ObservableProperty] private string _cardHover = "#FF1b2838";
        [ObservableProperty] private string _secondaryBackground = "#FF2a475e";
        [ObservableProperty] private string _border = "#FF3d5a73";

        // ---- Text ----
        [ObservableProperty] private string _textPrimary = "#FFc7d5e0";
        [ObservableProperty] private string _textSecondary = "#FF8f98a0";
        [ObservableProperty] private string _titleBarText = "#FF3d8ec9";
        [ObservableProperty] private string _buttonText = "#FFffffff";

        // ---- Buttons & accents ----
        [ObservableProperty] private ThemeAccentMode _accentMode = ThemeAccentMode.Solid;
        [ObservableProperty] private string _accent = "#FF3d8ec9";
        [ObservableProperty] private double _accentGradientAngle;
        [ObservableProperty] private List<ThemeGradientStop> _accentGradientStops = new();

        // ---- Sidebar active item ----
        [ObservableProperty] private string _sidebarActive = "#FF3d8ec9";
        [ObservableProperty] private string _sidebarActiveText = "#FFffffff";

        // ---- Status ----
        [ObservableProperty] private string _success = "#FF5cb85c";
        [ObservableProperty] private string _warning = "#FFf0ad4e";
        [ObservableProperty] private string _danger = "#FFd9534f";

        // ---- Background images (not exported) ----
        [ObservableProperty] private string? _pageBackgroundImagePath;
        [ObservableProperty] private ThemeImageFit _pageBackgroundImageFit = ThemeImageFit.UniformToFill;
        [ObservableProperty] private string? _sidebarBackgroundImagePath;
        [ObservableProperty] private ThemeImageFit _sidebarBackgroundImageFit = ThemeImageFit.UniformToFill;
    }
}

using HubcapManifestApp.Models;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HubcapManifestApp.Services
{
    public class ThemeService
    {
        public void ApplyTheme(AppTheme theme, AppSettings? settings = null)
        {
            if (theme == AppTheme.Custom && settings != null)
            {
                ApplyCustomTheme(settings);
                return;
            }

            var themeFile = GetThemeFileName(theme);
            var themeUri = new Uri($"pack://application:,,,/Resources/Themes/{themeFile}", UriKind.Absolute);

            Application.Current.Dispatcher.Invoke(() =>
            {
                var otherDictionaries = Application.Current.Resources.MergedDictionaries
                    .Skip(1)
                    .ToList();

                Application.Current.Resources.MergedDictionaries.Clear();

                var newTheme = new ResourceDictionary { Source = themeUri };
                Application.Current.Resources.MergedDictionaries.Add(newTheme);

                foreach (var dict in otherDictionaries)
                {
                    Application.Current.Resources.MergedDictionaries.Add(dict);
                }
            });
        }

        /// <summary>
        /// Resolves the preset that should be active and applies it. Falls back to the
        /// legacy Custom* single-slot properties if no preset library exists yet.
        /// </summary>
        public void ApplyCustomTheme(AppSettings settings)
        {
            var preset = ResolveActivePreset(settings);
            ApplyPreset(preset);
        }

        public void ApplyPreset(CustomThemePreset preset)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dict = new ResourceDictionary();

                // ---- Solid color brushes ----
                dict["TextPrimaryBrush"] = BuildBrush(preset.TextPrimary);
                dict["TextSecondaryBrush"] = BuildBrush(preset.TextSecondary);
                dict["TitleBarTextBrush"] = BuildBrush(preset.TitleBarText);
                dict["ButtonForegroundBrush"] = BuildBrush(preset.ButtonText);
                dict["SidebarActiveTextBrush"] = BuildBrush(preset.SidebarActiveText);

                dict["SecondaryDarkBrush"] = BuildBrush(preset.SecondaryBackground);
                dict["CardBackgroundBrush"] = BuildBrush(preset.CardBackground);
                dict["CardHoverBrush"] = BuildBrush(preset.CardHover);
                dict["TitleBarBackgroundBrush"] = BuildBrush(preset.TitleBarBackground);
                dict["BorderBrush"] = BuildBrush(preset.Border);
                dict["SidebarActiveBrush"] = BuildBrush(preset.SidebarActive);

                dict["SuccessBrush"] = BuildBrush(preset.Success);
                dict["WarningBrush"] = BuildBrush(preset.Warning);
                dict["DangerBrush"] = BuildBrush(preset.Danger);

                // ---- Accent: either a single color or a gradient ----
                Brush accentBrush;
                if (preset.AccentMode == ThemeAccentMode.Gradient && preset.AccentGradientStops.Count >= 2)
                {
                    accentBrush = BuildGradient(preset.AccentGradientStops, preset.AccentGradientAngle);
                }
                else
                {
                    accentBrush = BuildBrush(preset.Accent);
                }
                if (accentBrush.CanFreeze) accentBrush.Freeze();
                dict["AccentBrush"] = accentBrush;

                // Auto-derive hover (+/- 15% L in HSL) so hovers are always visibly different.
                dict["AccentHoverBrush"] = BuildHoverBrush(accentBrush);

                // ---- Page and sidebar backgrounds: image if set, otherwise color ----
                dict["PrimaryDarkBrush"] = BuildBackgroundBrush(
                    preset.PageBackground,
                    preset.PageBackgroundImagePath,
                    preset.PageBackgroundImageFit);

                dict["SidebarBackgroundBrush"] = BuildBackgroundBrush(
                    preset.SidebarBackground,
                    preset.SidebarBackgroundImagePath,
                    preset.SidebarBackgroundImageFit);

                // ---- Merge into Application.Resources, keeping the non-theme dictionaries ----
                var otherDictionaries = Application.Current.Resources.MergedDictionaries
                    .Skip(1)
                    .ToList();

                Application.Current.Resources.MergedDictionaries.Clear();
                Application.Current.Resources.MergedDictionaries.Add(dict);

                foreach (var d in otherDictionaries)
                {
                    Application.Current.Resources.MergedDictionaries.Add(d);
                }
            });
        }

        /// <summary>
        /// Returns the active preset if one exists in the library. Otherwise migrates
        /// the legacy Custom* single-slot settings into a new "Imported Custom" preset.
        /// </summary>
        private static CustomThemePreset ResolveActivePreset(AppSettings settings)
        {
            if (settings.CustomThemes.Count > 0)
            {
                var active = settings.CustomThemes.FirstOrDefault(p => p.Id == settings.ActiveCustomThemeId);
                if (active != null) return active;
                return settings.CustomThemes[0];
            }

            // Migrate legacy Custom* fields
            var migrated = new CustomThemePreset
            {
                Name = "Imported Custom",
                PageBackground = EnsureAlpha(settings.CustomPrimaryDark),
                SecondaryBackground = EnsureAlpha(settings.CustomSecondaryDark),
                SidebarBackground = EnsureAlpha(settings.CustomSecondaryDark),
                CardBackground = EnsureAlpha(settings.CustomCardBackground),
                CardHover = EnsureAlpha(settings.CustomCardHover),
                TitleBarBackground = EnsureAlpha(settings.CustomCardBackground),
                Accent = EnsureAlpha(settings.CustomAccent),
                SidebarActive = EnsureAlpha(settings.CustomAccent),
                TitleBarText = EnsureAlpha(settings.CustomAccent),
                TextPrimary = EnsureAlpha(settings.CustomTextPrimary),
                TextSecondary = EnsureAlpha(settings.CustomTextSecondary)
            };
            settings.CustomThemes.Add(migrated);
            settings.ActiveCustomThemeId = migrated.Id;
            return migrated;
        }

        /// <summary>
        /// Parses a hex color and returns a frozen SolidColorBrush. Accepts both
        /// #RRGGBB (assumes FF alpha) and #AARRGGBB.
        /// </summary>
        private static SolidColorBrush BuildBrush(string hex)
        {
            var color = ParseColor(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush BuildGradient(System.Collections.Generic.List<ThemeGradientStop> stops, double angleDegrees)
        {
            var angle = angleDegrees * Math.PI / 180.0;
            var x = Math.Cos(angle);
            var y = Math.Sin(angle);
            // Map the angle to start/end points in the unit box.
            var start = new Point(0.5 - x * 0.5, 0.5 - y * 0.5);
            var end = new Point(0.5 + x * 0.5, 0.5 + y * 0.5);

            var gradient = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end,
                ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation
            };

            foreach (var stop in stops.OrderBy(s => s.Offset))
            {
                gradient.GradientStops.Add(new GradientStop(ParseColor(stop.Color), stop.Offset));
            }

            return gradient;
        }

        /// <summary>
        /// Builds an ImageBrush backed by a file on disk, or falls back to the solid
        /// color if the image is missing or fails to load.
        /// </summary>
        private static Brush BuildBackgroundBrush(string fallbackHex, string? imagePath, ThemeImageFit fit)
        {
            if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var imgBrush = new ImageBrush(bitmap)
                    {
                        Stretch = fit switch
                        {
                            ThemeImageFit.Stretch => Stretch.Fill,
                            ThemeImageFit.Uniform => Stretch.Uniform,
                            ThemeImageFit.UniformToFill => Stretch.UniformToFill,
                            ThemeImageFit.Center => Stretch.None,
                            ThemeImageFit.Tile => Stretch.None,
                            _ => Stretch.UniformToFill
                        }
                    };

                    if (fit == ThemeImageFit.Tile)
                    {
                        imgBrush.TileMode = TileMode.Tile;
                        imgBrush.ViewportUnits = BrushMappingMode.Absolute;
                        imgBrush.Viewport = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
                    }
                    else if (fit == ThemeImageFit.Center)
                    {
                        imgBrush.AlignmentX = AlignmentX.Center;
                        imgBrush.AlignmentY = AlignmentY.Center;
                    }

                    imgBrush.Freeze();
                    return imgBrush;
                }
                catch
                {
                    // Fall through to the solid color fallback.
                }
            }

            return BuildBrush(fallbackHex);
        }

        /// <summary>
        /// Auto-derives a hover color by shifting the base ±15% lightness in HSL.
        /// For gradients, derives from the first stop.
        /// </summary>
        private static Brush BuildHoverBrush(Brush baseBrush)
        {
            Color baseColor = Colors.Gray;
            if (baseBrush is SolidColorBrush scb) baseColor = scb.Color;
            else if (baseBrush is GradientBrush gb && gb.GradientStops.Count > 0) baseColor = gb.GradientStops[0].Color;

            var (h, s, l) = RgbToHsl(baseColor);
            // If it's already quite light, go darker; otherwise go lighter.
            var newL = l > 0.5 ? Math.Max(0, l - 0.15) : Math.Min(1, l + 0.15);
            var shifted = HslToRgb(h, s, newL, baseColor.A);

            var brush = new SolidColorBrush(shifted);
            brush.Freeze();
            return brush;
        }

        private static Color ParseColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Colors.Gray;
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                return Colors.Gray;
            }
        }

        private static string EnsureAlpha(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "#FF808080";
            hex = hex.Trim();
            if (!hex.StartsWith("#")) hex = "#" + hex;
            if (hex.Length == 7) return "#FF" + hex.Substring(1); // #RRGGBB -> #FFRRGGBB
            return hex;
        }

        private static (double h, double s, double l) RgbToHsl(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double h = 0, s, l = (max + min) / 2;

            if (max == min)
            {
                h = s = 0;
            }
            else
            {
                double d = max - min;
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6;
            }

            return (h, s, l);
        }

        private static Color HslToRgb(double h, double s, double l, byte alpha)
        {
            double r, g, b;
            if (s == 0)
            {
                r = g = b = l;
            }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = HueToRgb(p, q, h + 1.0 / 3.0);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3.0);
            }
            return Color.FromArgb(alpha, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }

        private string GetThemeFileName(AppTheme theme)
        {
            return theme switch
            {
                AppTheme.Default => "DefaultTheme.xaml",
                AppTheme.Dark => "DarkTheme.xaml",
                AppTheme.Light => "LightTheme.xaml",
                AppTheme.Cherry => "CherryTheme.xaml",
                AppTheme.Sunset => "SunsetTheme.xaml",
                AppTheme.Forest => "ForestTheme.xaml",
                AppTheme.Grape => "GrapeTheme.xaml",
                AppTheme.Cyberpunk => "CyberpunkTheme.xaml",
                AppTheme.Pink => "PinkTheme.xaml",
                AppTheme.Pastel => "PastelTheme.xaml",
                AppTheme.Rainbow => "RainbowTheme.xaml",
                _ => "DefaultTheme.xaml"
            };
        }
    }
}

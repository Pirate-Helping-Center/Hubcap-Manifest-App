using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HubcapManifestApp.Views
{
    public partial class LuaInstallerPage : UserControl
    {
        public LuaInstallerPage()
        {
            InitializeComponent();
        }

        private bool HasValidFilesOrFolders(string[] paths)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    if (path.EndsWith(".lua", System.StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".manifest", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (Directory.Exists(path))
                {
                    return true;
                }
            }
            return false;
        }

        private List<string> GetValidFilesFromPaths(string[] paths)
        {
            var validFiles = new List<string>();
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    if (path.EndsWith(".lua", System.StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".manifest", System.StringComparison.OrdinalIgnoreCase))
                    {
                        validFiles.Add(path);
                    }
                }
                else if (Directory.Exists(path))
                {
                    var filesInFolder = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".lua", System.StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".manifest", System.StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    validFiles.AddRange(filesInFolder);
                }
            }
            return validFiles;
        }

        private void DropZone_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (HasValidFilesOrFolders(files))
                {
                    e.Effects = DragDropEffects.Copy;

                    if (sender is Border border)
                    {
                        // Highlight using the active theme's accent so it matches Light/Dark/etc.
                        var accent = TryGetAccentColor();
                        border.Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
                        border.BorderBrush = new SolidColorBrush(accent);
                        border.BorderThickness = new Thickness(2);
                    }
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
            }
            e.Handled = true;
        }

        private static Color TryGetAccentColor()
        {
            // AccentBrush may be a SolidColorBrush (most themes) or a LinearGradientBrush (Rainbow).
            if (System.Windows.Application.Current.Resources["AccentBrush"] is SolidColorBrush solid)
                return solid.Color;
            if (System.Windows.Application.Current.Resources["AccentBrush"] is LinearGradientBrush grad && grad.GradientStops.Count > 0)
                return grad.GradientStops[0].Color;
            return Color.FromRgb(74, 144, 226);
        }

        private static void ResetDropZone(Border border)
        {
            // Clear local values so the current theme's style/resources re-apply.
            border.ClearValue(Border.BackgroundProperty);
            border.ClearValue(Border.BorderBrushProperty);
            border.ClearValue(Border.BorderThicknessProperty);
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border)
            {
                ResetDropZone(border);
            }
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (sender is Border border)
            {
                ResetDropZone(border);
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var droppedPaths = (string[])e.Data.GetData(DataFormats.FileDrop);
                var validFiles = GetValidFilesFromPaths(droppedPaths);

                if (validFiles.Count > 0 && DataContext is ViewModels.LuaInstallerViewModel viewModel &&
                    viewModel.ProcessDroppedFilesCommand.CanExecute(validFiles.ToArray()))
                {
                    viewModel.ProcessDroppedFilesCommand.Execute(validFiles.ToArray());
                }
            }
            e.Handled = true;
        }
    }
}

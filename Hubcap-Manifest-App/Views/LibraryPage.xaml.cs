using HubcapManifestApp.ViewModels;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HubcapManifestApp.Views
{
    public partial class LibraryPage : UserControl
    {
        public LibraryPage()
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
                        path.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
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
                        path.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
                    {
                        validFiles.Add(path);
                    }
                }
                else if (Directory.Exists(path))
                {
                    var filesInFolder = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".lua", System.StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    validFiles.AddRange(filesInFolder);
                }
            }
            return validFiles;
        }

        private void Grid_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (HasValidFilesOrFolders(files))
                {
                    e.Effects = DragDropEffects.Copy;

                    if (sender is Grid grid)
                    {
                        var accent = TryGetAccentColor();
                        grid.Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
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
            if (System.Windows.Application.Current.Resources["AccentBrush"] is SolidColorBrush solid)
                return solid.Color;
            if (System.Windows.Application.Current.Resources["AccentBrush"] is LinearGradientBrush grad && grad.GradientStops.Count > 0)
                return grad.GradientStops[0].Color;
            return Color.FromRgb(74, 144, 226);
        }

        private void Grid_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Grid grid)
            {
                grid.ClearValue(Grid.BackgroundProperty);
            }
        }

        private void Grid_Drop(object sender, DragEventArgs e)
        {
            if (sender is Grid grid)
            {
                grid.ClearValue(Grid.BackgroundProperty);
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var droppedPaths = (string[])e.Data.GetData(DataFormats.FileDrop);
                var validFiles = GetValidFilesFromPaths(droppedPaths);

                if (validFiles.Count > 0 && DataContext is LibraryViewModel viewModel &&
                    viewModel.ProcessDroppedFilesCommand.CanExecute(validFiles.ToArray()))
                {
                    viewModel.ProcessDroppedFilesCommand.Execute(validFiles.ToArray());
                }
            }
            e.Handled = true;
        }
    }
}

using HubcapManifestApp.Services;
using HubcapManifestApp.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HubcapManifestApp.Views
{
    public partial class StorePage : UserControl
    {
        public StorePage()
        {
            InitializeComponent();
            DataContextChanged += StorePage_DataContextChanged;
        }

        private void StorePage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is StoreViewModel viewModel)
            {
                viewModel.ScrollToTopAction = ScrollToTop;
            }
        }

        public void ScrollToTop()
        {
            StoreScrollViewer.ScrollToTop();
        }

        private void SuggestionItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item && item.DataContext is AppListEntry entry)
            {
                if (DataContext is StoreViewModel viewModel)
                {
                    viewModel.SelectSuggestionCommand.Execute(entry);
                }
            }
        }

        private async void SearchTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            await System.Threading.Tasks.Task.Delay(200);
            if (DataContext is StoreViewModel viewModel)
            {
                viewModel.HideSuggestionsCommand.Execute(null);
            }
        }
        private void GameCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is StoreViewModel vm && vm.IsSelectMode
                && sender is FrameworkElement fe && fe.DataContext is Models.LibraryGame game)
            {
                game.IsSelected = !game.IsSelected;
            }
        }
    }
}

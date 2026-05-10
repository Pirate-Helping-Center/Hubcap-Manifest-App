using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using HubcapManifestApp.ViewModels;

namespace HubcapManifestApp.Views
{
    public partial class SettingsPage : UserControl
    {
        // Reentrancy guard so VM->PasswordBox sync isn't echoed back as a user edit.
        private bool _suppressPasswordSync;
        private bool _singlePageBuilt;

        public SettingsPage()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is INotifyPropertyChanged oldVm)
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            if (e.NewValue is INotifyPropertyChanged newVm)
                newVm.PropertyChanged += OnViewModelPropertyChanged;

            SyncPasswordBoxFromViewModel();

            if (e.NewValue is SettingsViewModel vm && vm.SinglePageSettings)
                BuildSinglePageView();
        }

        private void BuildSinglePageView()
        {
            if (_singlePageBuilt) return;
            _singlePageBuilt = true;

            var stack = new StackPanel { MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Left };

            foreach (TabItem tab in TabbedView.Items)
            {
                if (tab.Visibility == Visibility.Collapsed) continue;

                // Section header
                var header = new TextBlock
                {
                    Text = tab.Header?.ToString() ?? "",
                    FontSize = 24,
                    FontWeight = FontWeights.Bold,
                    Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                    Margin = new Thickness(0, 20, 0, 10)
                };
                stack.Children.Add(header);

                // Extract the content — it's typically a ScrollViewer wrapping a StackPanel
                if (tab.Content is ScrollViewer sv && sv.Content is UIElement inner)
                {
                    sv.Content = null; // detach from TabItem
                    stack.Children.Add(inner);
                }
                else if (tab.Content is UIElement content)
                {
                    tab.Content = null;
                    stack.Children.Add(content);
                }
            }

            SinglePageView.Content = stack;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.ApiKey) ||
                e.PropertyName == nameof(SettingsViewModel.FixGameSteamWebApiKey))
            {
                SyncPasswordBoxFromViewModel();
            }
        }

        // Note: SinglePageSettings takes effect after save — toggling the checkbox
        // sets the flag, and next time Settings page loads it will render in the
        // chosen layout. No live toggle needed.

        private void SyncPasswordBoxFromViewModel()
        {
            if (DataContext is not SettingsViewModel vm) return;
            if (ApiKeyPasswordBox == null) return;
            if (ApiKeyPasswordBox.Password == vm.ApiKey) return;

            _suppressPasswordSync = true;
            try
            {
                ApiKeyPasswordBox.Password = vm.ApiKey ?? string.Empty;
                if (FixGameApiKeyPasswordBox != null)
                    FixGameApiKeyPasswordBox.Password = vm.FixGameSteamWebApiKey ?? string.Empty;
            }
            finally
            {
                _suppressPasswordSync = false;
            }
        }

        private void ApiKeyPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_suppressPasswordSync) return;
            if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
            {
                vm.ApiKey = pb.Password;
            }
        }

        private void FixGameApiKeyPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_suppressPasswordSync) return;
            if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
            {
                vm.FixGameSteamWebApiKey = pb.Password;
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }

        private void UiScaleSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm)
                vm.ApplyCurrentScaleCommand.Execute(null);
        }
    }
}

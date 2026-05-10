using Microsoft.Toolkit.Uwp.Notifications;
using HubcapManifestApp.Helpers;
using HubcapManifestApp.Interfaces;
using System;
using System.Windows;

namespace HubcapManifestApp.Services
{
    public class NotificationService : INotificationService
    {
        private readonly SettingsService _settingsService;
        private static bool _isInitialized = false;

        public NotificationService(SettingsService settingsService)
        {
            _settingsService = settingsService;

            if (!_isInitialized)
            {
                ToastNotificationManagerCompat.OnActivated += toastArgs =>
                {
                    // Handle toast activation if needed
                };
                _isInitialized = true;
            }
        }

        public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info)
        {
            var settings = _settingsService.LoadSettings();

            // If all notifications are disabled, skip completely
            if (settings.DisableAllNotifications)
            {
                return;
            }

            // If toast notifications are enabled, use them
            if (settings.ShowNotifications)
            {
                try
                {
                    new ToastContentBuilder()
                        .AddText(title)
                        .AddText(message)
                        .Show();
                    return;
                }
                catch (Exception ex)
                {
                    // If toast fails, fall through to MessageBox
                    System.Diagnostics.Debug.WriteLine($"Toast notification failed: {ex.Message}");
                }
            }

            // Fall back to MessageBox when notifications disabled or toast fails
            Application.Current.Dispatcher.Invoke(() =>
            {
                var icon = type switch
                {
                    NotificationType.Success => MessageBoxImage.Information,
                    NotificationType.Warning => MessageBoxImage.Warning,
                    NotificationType.Error => MessageBoxImage.Error,
                    _ => MessageBoxImage.Information
                };

                MessageBoxHelper.Show(message, title, MessageBoxButton.OK, icon);
            });
        }

        public void ShowSuccess(string message, string title = "Success")
        {
            ShowNotification(title, message, NotificationType.Success);
        }

        public void ShowWarning(string message, string title = "Warning")
        {
            ShowNotification(title, message, NotificationType.Warning);
        }

        public void ShowError(string message, string title = "Error")
        {
            ShowNotification(title, message, NotificationType.Error);
        }

    }

    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error
    }
}

using HubcapManifestApp.Models;
using HubcapManifestApp.Views.Dialogs;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Windows;

namespace HubcapManifestApp.Helpers
{
    public static class MessageBoxHelper
    {
        /// <summary>
        /// Shows a message box dialog. When DisableAllNotifications is enabled, auto-confirms with Yes/OK.
        /// </summary>
        /// <param name="forceShow">If true, always shows the dialog even when notifications are disabled (use for critical operations like updates)</param>
        public static MessageBoxResult Show(string message, string title = "Message", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None, bool forceShow = false)
        {
            // Check if all notifications are disabled - auto-confirm if so (unless forceShow is true)
            if (!forceShow && AreNotificationsDisabled())
            {
                // Return the affirmative response based on button type
                return buttons switch
                {
                    MessageBoxButton.OK => MessageBoxResult.OK,
                    MessageBoxButton.OKCancel => MessageBoxResult.OK,
                    MessageBoxButton.YesNo => MessageBoxResult.Yes,
                    MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
                    _ => MessageBoxResult.OK
                };
            }

            var customButtons = buttons switch
            {
                MessageBoxButton.OK => CustomMessageBoxButton.OK,
                MessageBoxButton.OKCancel => CustomMessageBoxButton.OKCancel,
                MessageBoxButton.YesNo => CustomMessageBoxButton.YesNo,
                MessageBoxButton.YesNoCancel => CustomMessageBoxButton.YesNoCancel,
                _ => CustomMessageBoxButton.OK
            };

            var result = CustomMessageBox.Show(message, title, customButtons);

            return result switch
            {
                CustomMessageBoxResult.OK => MessageBoxResult.OK,
                CustomMessageBoxResult.Cancel => MessageBoxResult.Cancel,
                CustomMessageBoxResult.Yes => MessageBoxResult.Yes,
                CustomMessageBoxResult.No => MessageBoxResult.No,
                _ => MessageBoxResult.None
            };
        }

        private static bool AreNotificationsDisabled()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var settingsPath = Path.Combine(appData, AppConstants.AppDataFolderName, "settings.json");

                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    return settings?.DisableAllNotifications ?? false;
                }
            }
            catch
            {
                // If we can't read settings, default to showing dialogs
            }
            return false;
        }
    }
}

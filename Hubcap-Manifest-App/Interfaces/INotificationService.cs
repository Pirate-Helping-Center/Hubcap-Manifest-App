using HubcapManifestApp.Services;

namespace HubcapManifestApp.Interfaces
{
    public interface INotificationService
    {
        void ShowNotification(string title, string message, NotificationType type = NotificationType.Info);
        void ShowSuccess(string message, string title = "Success");
        void ShowWarning(string message, string title = "Warning");
        void ShowError(string message, string title = "Error");
    }
}

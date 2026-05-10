using System.Threading.Tasks;
using System.Windows;
using HubcapManifestApp.Helpers;

namespace HubcapManifestApp.Services.CloudRedirect;

public static class Dialog
{
    public static Task ShowInfoAsync(string title, string message)
    {
        MessageBoxHelper.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    public static Task ShowWarningAsync(string title, string message)
    {
        MessageBoxHelper.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return Task.CompletedTask;
    }

    public static Task ShowErrorAsync(string title, string message)
    {
        MessageBoxHelper.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    public static Task<bool> ConfirmAsync(string title, string message)
    {
        var result = MessageBoxHelper.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public static Task<bool> ConfirmDangerAsync(string title, string message)
    {
        var result = MessageBoxHelper.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public static Task<bool> ConfirmDangerAsync(string title, UIElement content)
    {
        var msg = content is System.Windows.Controls.TextBlock tb ? tb.Text : "Are you sure?";
        return ConfirmDangerAsync(title, msg);
    }

    public static Task<bool> ConfirmDangerCountdownAsync(string title, string message, int countdownSeconds = 3)
    {
        return ConfirmDangerAsync(title, message);
    }

    public static Task<bool> ChoiceAsync(string title, string message,
        string primaryText, string secondaryText)
    {
        var result = MessageBoxHelper.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }
}

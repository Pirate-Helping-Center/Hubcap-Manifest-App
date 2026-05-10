using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HubcapManifestApp.Helpers
{
    /// <summary>
    /// Simple themed input dialog for getting text from the user.
    /// </summary>
    public class InputDialog : Window
    {
        private readonly TextBox _inputBox;
        public string Result { get; private set; } = "";

        public InputDialog(string title, string message)
        {
            Title = title;
            Width = 500;
            Height = 350;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = (Brush)Application.Current.FindResource("PrimaryDarkBrush");
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush");

            var panel = new StackPanel { Margin = new Thickness(20) };

            var msgBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 15)
            };
            panel.Children.Add(msgBlock);

            _inputBox = new TextBox
            {
                Text = "all",
                Padding = new Thickness(10, 8, 10, 8),
                FontSize = 14
            };
            panel.Children.Add(_inputBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };

            var okBtn = new Button
            {
                Content = "OK",
                Padding = new Thickness(30, 8, 30, 8),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            okBtn.Click += (_, _) =>
            {
                Result = _inputBox.Text;
                DialogResult = true;
            };

            var cancelBtn = new Button
            {
                Content = "Create All",
                Padding = new Thickness(20, 8, 20, 8)
            };
            cancelBtn.Click += (_, _) =>
            {
                Result = "all";
                DialogResult = true;
            };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            Content = panel;
        }
    }
}

using System.Windows;

namespace HubcapManifestApp.Views.Dialogs
{
    public enum CustomMessageBoxButton
    {
        OK,
        OKCancel,
        YesNo,
        YesNoCancel
    }

    public enum CustomMessageBoxResult
    {
        None,
        OK,
        Cancel,
        Yes,
        No
    }

    public partial class CustomMessageBox : Window
    {
        public CustomMessageBoxResult Result { get; private set; }

        private CustomMessageBox(string message, string title, CustomMessageBoxButton buttons)
        {
            InitializeComponent();

            TitleTextBlock.Text = title;
            MessageTextBlock.Text = message;

            ConfigureButtons(buttons);
        }

        private void ConfigureButtons(CustomMessageBoxButton buttons)
        {
            switch (buttons)
            {
                case CustomMessageBoxButton.OK:
                    OkButton.Visibility = Visibility.Visible;
                    break;

                case CustomMessageBoxButton.OKCancel:
                    OkButton.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Visible;
                    break;

                case CustomMessageBoxButton.YesNo:
                    YesButton.Visibility = Visibility.Visible;
                    NoButton.Visibility = Visibility.Visible;
                    break;

                case CustomMessageBoxButton.YesNoCancel:
                    YesButton.Visibility = Visibility.Visible;
                    NoButton.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Visible;
                    break;
            }
        }

        public static CustomMessageBoxResult Show(string message, string title = "Message", CustomMessageBoxButton buttons = CustomMessageBoxButton.OK, Window? owner = null)
        {
            var dialog = new CustomMessageBox(message, title, buttons);

            if (owner != null)
            {
                dialog.Owner = owner;
            }
            else if (Application.Current.MainWindow != null)
            {
                dialog.Owner = Application.Current.MainWindow;
            }

            dialog.ShowDialog();
            return dialog.Result;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                DragMove();
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = CustomMessageBoxResult.OK;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = CustomMessageBoxResult.Cancel;
            DialogResult = false;
            Close();
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            Result = CustomMessageBoxResult.Yes;
            DialogResult = true;
            Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            Result = CustomMessageBoxResult.No;
            DialogResult = false;
            Close();
        }
    }
}

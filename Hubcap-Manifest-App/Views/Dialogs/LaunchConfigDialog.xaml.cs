using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace HubcapManifestApp.Views.Dialogs
{
    public partial class LaunchConfigDialog : Window
    {
        public List<LaunchConfigItem> Items { get; }
        public List<LaunchConfigItem> SelectedItems { get; private set; } = new();
        public bool Skipped { get; private set; }

        public LaunchConfigDialog(List<LaunchConfigItem> items)
        {
            InitializeComponent();
            Items = items;
            LaunchList.ItemsSource = Items;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            SelectedItems = Items.Where(i => i.IsSelected).ToList();
            Skipped = false;
            DialogResult = true;
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Skipped = true;
            DialogResult = true;
        }
    }

    public class LaunchConfigItem : INotifyPropertyChanged
    {
        public string Executable { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Description { get; set; } = "";
        public string WorkingDir { get; set; } = "";
        public string OsType { get; set; } = "";
        public string BetaKey { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }

        public string DisplayName => !string.IsNullOrEmpty(Description) ? Description : Executable;
        public string Platform => string.IsNullOrEmpty(OsType) ? "All platforms" : OsType;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

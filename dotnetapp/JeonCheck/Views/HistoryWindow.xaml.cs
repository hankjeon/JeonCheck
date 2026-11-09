using System.Windows;
using JeonCheck.Services;

namespace JeonCheck.Views;

public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();
        DarkTitleBarHelper.Apply(this);
        VersionBadge.Text = AppInfo.Version;
        var history = VersionHistory.Load();
        HistoryList.ItemsSource = history.Entries.Select(h => new HistoryItemViewModel(h)).ToList();
        HistoryMessage.Text = history.Message;
        HistoryMessage.Visibility = string.IsNullOrEmpty(history.Message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class HistoryItemViewModel
    {
        public HistoryItemViewModel(VersionHistoryEntry entry)
        {
            Version = entry.Version;
            ReleaseDateDisplay = entry.ReleaseDateDisplay;
            Added = entry.Added.Select(a => $"• {a}").ToList();
            Changed = entry.Changed.Select(c => $"• {c}").ToList();
        }

        public string Version { get; }
        public string ReleaseDateDisplay { get; }
        public List<string> Added { get; }
        public List<string> Changed { get; }
        public bool HasAdded => Added.Count > 0;
        public bool HasChanged => Changed.Count > 0;
    }
}

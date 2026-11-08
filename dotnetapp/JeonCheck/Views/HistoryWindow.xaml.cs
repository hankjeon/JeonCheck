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
        HistoryList.ItemsSource = AppInfo.History.Select(h => new HistoryItemViewModel(h)).ToList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class HistoryItemViewModel
    {
        public HistoryItemViewModel(AppInfo.HistoryEntry entry)
        {
            Version = entry.Version;
            Added = entry.Added.Select(a => $"• {a}").ToList();
            Changed = entry.Changed.Select(c => $"• {c}").ToList();
        }

        public string Version { get; }
        public List<string> Added { get; }
        public List<string> Changed { get; }
        public bool HasAdded => Added.Count > 0;
        public bool HasChanged => Changed.Count > 0;
    }
}

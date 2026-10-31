using System.Windows;
using JeonCheck.Services;

namespace JeonCheck.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DarkTitleBarHelper.Apply(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

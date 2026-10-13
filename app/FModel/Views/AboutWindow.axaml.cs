using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FModel.Views;

public partial class AboutWindow : Window
{
    // Designer only
    public AboutWindow() : this("0.0.0") { }

    public AboutWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = $"Version {version}";
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}

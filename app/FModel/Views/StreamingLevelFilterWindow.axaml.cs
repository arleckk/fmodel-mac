using Avalonia.Controls;
using Avalonia.Interactivity;
using FModel.ViewModels;

namespace FModel.Views;

// Asked by the Export Session when a world streams other levels (FModel's StreamingLevelFilterWindow)
public partial class StreamingLevelFilterWindow : Window
{
    private readonly StreamingLevelFilterViewModel _vm;

    // Designer only
    public StreamingLevelFilterWindow() : this(null!) { }

    public StreamingLevelFilterWindow(StreamingLevelFilterViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => _vm.SelectAll();

    private void OnSelectNoneClick(object? sender, RoutedEventArgs e) => _vm.SkipAll();

    private void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        _vm.SkipAll();
        Close();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}

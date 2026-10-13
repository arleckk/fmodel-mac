using Avalonia.Controls;
using FModel.ViewModels;

namespace FModel.Views;

public enum SearchTab { ByPath, References }

// Packages > Search and Packages > References (FModel's SearchView): every file of the mounted game filtered by
// path, and the packages that import the one Find References was used on
public partial class SearchWindow : Window
{
    // Designer only
    public SearchWindow() : this(new MainViewModel()) { }

    public SearchWindow(MainViewModel vm)
    {
        InitializeComponent();
        PathPanel.Vm = ReferencesPanel.Vm = vm;
        PathPanel.Files = vm.AllFiles().OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        ShowReferences(vm.References);
        Opened += (_, _) => CurrentPanel.FocusQuery();
    }

    private FileSearchPanel CurrentPanel => Tabs.SelectedIndex == 1 ? ReferencesPanel : PathPanel;

    public void FocusTab(SearchTab tab)
    {
        Tabs.SelectedIndex = (int) tab;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        CurrentPanel.FocusQuery();
    }

    public void ShowReferences(PackageReferences? references)
    {
        ReferencesTab.Header = references is null ? "References" : $"References of {references.Package.Name.Replace("_", "__")}"; // '_' marks an access key
        ReferencesText.Text = references is null
            ? "Right-click a package and choose Find References to list the packages that import it."
            : $"Packages that import {references.Package.Path}";
        ReferencesPanel.Files = references?.Files ?? [];
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Tabs) && IsVisible) CurrentPanel.FocusQuery();
    }
}

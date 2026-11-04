using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Pages;

namespace Mirador.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> PagesByTag = new()
    {
        ["Home"] = typeof(HomePage),
        ["EyeTracking"] = typeof(EyeTrackingPage),
        ["MonadoSettings"] = typeof(MonadoSettingsPage),
        ["Profiles"] = typeof(ProfilesPage),
        ["Diagnostics"] = typeof(DiagnosticsPage),
    };

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : PagesByTag[(string)((NavigationViewItem)args.SelectedItem).Tag];
        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }
}

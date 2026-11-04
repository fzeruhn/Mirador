using Microsoft.UI.Windowing;
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
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Mirador.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        AppWindow.Closing += OnClosing;
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>Whether the window is on screen (not hidden in the tray).</summary>
    public bool IsShown => AppWindow.IsVisible;

    /// <summary>Brings the window back from the tray (or from being minimized) and to the front.</summary>
    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
    }

    /// <summary>Shows the page with <paramref name="tag"/> (Home, EyeTracking, …).</summary>
    public void Navigate(string tag)
    {
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
    }

    /// <summary>Closing hides the window when Mirador stays in the tray; otherwise it quits, asking first while Monado runs.</summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.Current.Quitting)
        {
            return;
        }

        args.Cancel = true;
        if (App.Current.Host.CloseToTray())
        {
            sender.Hide();
        }
        else
        {
            _ = App.Current.QuitAsync();
        }
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

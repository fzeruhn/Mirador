using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class HomePage : Page
{
    private bool current;

    public HomePage()
    {
        ViewModel = App.Current.Home;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public HomeViewModel ViewModel { get; }

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.DataContext is NoticeItem notice)
        {
            ViewModel.Dismiss(notice);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    /// <summary>Windows' default graphics settings, where hardware-accelerated GPU scheduling is turned on.</summary>
    private async void OnGraphicsSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:display-advancedgraphics-default")))
            {
                Services.AppLog.Write("Windows didn't open its graphics settings");
            }
        }
        catch (Exception ex)
        {
            Services.AppLog.Write("Windows couldn't open its graphics settings", ex);
        }
    }

    /// <summary>
    /// Home listens to the base stations while it is the current page (in the frame) and the window is on screen. The
    /// window is looked up here, not in OnNavigatedTo: the first navigation happens while the window is being made.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        current = true;
        var window = App.Current.MainWindow;
        window.AppWindow.Changed -= OnWindowChanged;
        window.AppWindow.Changed += OnWindowChanged;
        UpdateShown();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        current = false;
        App.Current.MainWindow.AppWindow.Changed -= OnWindowChanged;
        UpdateShown();
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange)
        {
            UpdateShown();
        }
    }

    private void UpdateShown() => ViewModel.SetShown(current && App.Current.MainWindow.IsShown);
}

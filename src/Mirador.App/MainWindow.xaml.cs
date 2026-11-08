using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mirador.App.Pages;
using Mirador.App.Services;
using Mirador.Core;

namespace Mirador.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> PagesByTag = new()
    {
        ["Home"] = typeof(HomePage),
        ["EyeTracking"] = typeof(EyeTrackingPage),
        ["Lighthouse"] = typeof(LighthousePage),
        ["MonadoSettings"] = typeof(MonadoSettingsPage),
        ["Profiles"] = typeof(ProfilesPage),
        ["Diagnostics"] = typeof(DiagnosticsPage),
        ["Settings"] = typeof(SettingsPage),
    };

    /// <summary>The pane's items: the pages, then App settings at the foot.</summary>
    private IEnumerable<NavigationViewItem> PaneItems =>
        NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Mirador.ico"));

        // The icon left of the title, decoded at the title bar's 16 px times the display's scale so it stays sharp.
        AppTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Mirador.png")))
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 16,
            },
        };

        // AppWindow sizes are in physical pixels: scale the size meant at 100 % to the window's display.
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(handle) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(760 * scale)));
        minimumSize = new MinimumSize(handle, MinimumWidth, MinimumHeight);
        deviceChanges = new DeviceChanges(handle);
        deviceChanges.Changed += App.Current.Host.NotifyDevicesChanged;
        endSession = new EndSession(handle, App.Current.Host);
        AppWindow.Closing += OnClosing;
        Activated += OnActivated;
        NavView.SelectedItem = NavView.MenuItems[0];
        App.Current.Host.ProfilesChanged += ShowHeadsetPages;
        ShowHeadsetPages();
    }

    /// <summary>The smallest window at 100 % scale: every page still fits next to the open pane.</summary>
    private const int MinimumWidth = 760;

    private const int MinimumHeight = 500;

    private readonly MinimumSize minimumSize;

    private readonly DeviceChanges deviceChanges;

    private readonly EndSession endSession;

    /// <summary>Whether the window is on screen (not hidden in the tray).</summary>
    public bool IsShown => AppWindow.IsVisible;

    /// <summary>Whether the window is shown and the one the user works in.</summary>
    public bool IsInForeground =>
        AppWindow.IsVisible && GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>The window's content once it has loaded (a window started hidden hasn't), for a dialog's XamlRoot.</summary>
    public Task<FrameworkElement> LoadedRootAsync()
    {
        var root = (FrameworkElement)Content;
        if (root.IsLoaded && root.XamlRoot is not null)
        {
            return Task.FromResult(root);
        }

        var loaded = new TaskCompletionSource<FrameworkElement>();
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            root.Loaded -= OnLoaded;
            loaded.TrySetResult(root);
        }

        root.Loaded += OnLoaded;
        return loaded.Task;
    }

    /// <summary>Brings the window back from the tray (or from being minimized) and to the front.</summary>
    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();

        // Activate() alone leaves a window that is shown but behind others where it is; the tray icon's click, or the
        // pipe's SHOW from a second Mirador, gives this process the right to take the foreground.
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    /// <summary>When the window last lost the focus: a click on the tray icon deactivates it before Mirador hears of the click.</summary>
    private long deactivatedAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 60;

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            deactivatedAt = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>A left click on the tray icon: hides the window when it is in front, else brings it to the front.</summary>
    public void ToggleFromTray()
    {
        var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        var since = Stopwatch.GetElapsedTime(deactivatedAt);
        if (TrayClick.ShouldHide(AppWindow.IsVisible, minimized, IsInForeground, since))
        {
            AppWindow.Hide();
        }
        else
        {
            ShowAndActivate();
        }
    }

    /// <summary>
    /// Shows the window on the taskbar, minimized, without taking focus (a start at sign-in). It is shown first, not
    /// activated, and minimized once its content has loaded: WinUI crashes (a stowed COMException from
    /// Microsoft.ui.xaml.dll) when a window that has never been laid out is minimized.
    /// </summary>
    public async void ShowMinimized()
    {
        try
        {
            AppWindow.Show(activateWindow: false);
            await LoadedRootAsync();
            const int ShowMinNoActive = 7;
            ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), ShowMinNoActive);
        }
        catch (Exception ex)
        {
            Services.AppLog.Write("Couldn't start minimized", ex);
        }
    }

    /// <summary>The Eye tracking page is there only for a Dream Air profile; leaving one while on it goes Home.</summary>
    private void ShowHeadsetPages()
    {
        var dreamAir = App.Current.Host.SelectedIsDreamAir();
        EyeTrackingItem.Visibility = dreamAir ? Visibility.Visible : Visibility.Collapsed;
        if (!dreamAir && ReferenceEquals(NavView.SelectedItem, EyeTrackingItem))
        {
            Navigate("Home");
        }

        // The labels shown have changed: fit the pane to them once they are laid out.
        NavView.LayoutUpdated -= FitPaneOnce;
        NavView.LayoutUpdated += FitPaneOnce;
    }

    private void FitPaneOnce(object? sender, object e)
    {
        if (FitPaneToLabels())
        {
            NavView.LayoutUpdated -= FitPaneOnce;
        }
    }

    /// <summary>
    /// Makes the open pane as wide as its widest label needs: where the label starts in the pane, plus the label's
    /// whole width, plus the room the item keeps after its label (padding, margins). Closed, the pane still shows the
    /// icons alone. False while the pane is closed or its items aren't laid out yet.
    /// </summary>
    private bool FitPaneToLabels()
    {
        if (!NavView.IsPaneOpen)
        {
            return false;
        }

        var widest = 0.0;
        foreach (var item in PaneItems)
        {
            if (item is not { Visibility: Visibility.Visible, Content: string text })
            {
                continue;
            }

            if (FindLabel(item, text) is not { } label || VisualTreeHelper.GetParent(label) is not UIElement holder)
            {
                return false;
            }

            // A label in a narrow pane is cut short, so measure the same text on its own.
            var whole = new TextBlock
            {
                Text = text,
                FontFamily = label.FontFamily,
                FontSize = label.FontSize,
                FontWeight = label.FontWeight,
                FontStretch = label.FontStretch,
                CharacterSpacing = label.CharacterSpacing,
            };
            whole.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

            // The label's room ends where its layout slot does; the pane goes on past that by the item's padding.
            var slot = LayoutInformation.GetLayoutSlot(label);
            var toPane = holder.TransformToVisual(NavView);
            var start = toPane.TransformPoint(new Windows.Foundation.Point(slot.Left, 0)).X;
            var roomEnd = toPane.TransformPoint(new Windows.Foundation.Point(slot.Right, 0)).X;
            var after = NavView.OpenPaneLength - roomEnd;
            widest = Math.Max(widest, start + whole.DesiredSize.Width + after);
        }

        if (widest <= 0)
        {
            return false;
        }

        NavView.OpenPaneLength = Math.Ceiling(widest);
        return true;
    }

    /// <summary>The text block an item shows its label in (the icon is a text block too, with the glyph).</summary>
    private static TextBlock? FindLabel(DependencyObject parent, string text)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock block && block.Text == text)
            {
                return block;
            }

            if (FindLabel(child, text) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Shows the page with <paramref name="tag"/> (Home, EyeTracking, …).</summary>
    public void Navigate(string tag)
    {
        NavView.SelectedItem = PaneItems.FirstOrDefault(i => (string)i.Tag == tag);
    }

    /// <summary>Closing hides the window when Mirador stays in the tray; otherwise it quits, asking first while Monado runs.</summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.Current.ClosingForGood)
        {
            return;
        }

        args.Cancel = true;
        if (App.Current.Quitting || App.Current.Host.CloseToTray())
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
        var page = PagesByTag[(string)((NavigationViewItem)args.SelectedItem).Tag];
        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}

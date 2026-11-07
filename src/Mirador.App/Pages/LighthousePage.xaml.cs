using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.Services;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class LighthousePage : Page
{
    private bool current;

    public LighthousePage()
    {
        ViewModel = new LighthouseViewModel(App.Current.Host, DispatcherQueue);
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public LighthouseViewModel ViewModel { get; }

    /// <summary>
    /// The page listens to the stations while it is the current page (in the frame) and the window is on screen. The
    /// window is looked up on Loaded, not on navigation: a first navigation happens while the window is being made.
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

    private async void OnBluetoothSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth")))
            {
                AppLog.Write("Windows didn't open its Bluetooth settings");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Windows couldn't open its Bluetooth settings", ex);
        }
    }

    /// <summary>The station's "…" menu: switch it, rename it, its ID (1.0), forget it.</summary>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StationItem item } button)
        {
            return;
        }

        var menu = new MenuFlyout();
        Add("Switch on", item.SwitchOn, item.CanSwitch);
        if (item.IsV2)
        {
            Add("Standby", item.Standby, item.CanSwitch);
        }

        Add("Sleep", item.Sleep, item.CanSwitch);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Rename…", () => _ = RenameAsync(item), true);
        if (item.IsV1)
        {
            Add("Set ID…", () => _ = SetIdAsync(item), true);
        }

        Add("Forget", item.Forget, true);
        menu.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });

        void Add(string text, Action action, bool enabled)
        {
            var entry = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
    }

    private Task RenameAsync(StationItem item) => AskAsync(
        "Rename base station",
        null,
        new TextBox { Text = item.CustomName ?? "", PlaceholderText = item.Name },
        text => ViewModel.Rename(item, text));

    private Task SetIdAsync(StationItem item) => AskAsync(
        "Base station ID",
        "The 8 letters and digits on the back of the base station. With it, Off is immediate.",
        new TextBox { Text = item.Id ?? "", PlaceholderText = "AABBCCDD" },
        text => ViewModel.SetId(item, text));

    /// <summary>
    /// A dialog with one text box, Save and Cancel. Save hands the text to <paramref name="save"/>; what it says went
    /// wrong is shown under the box and the dialog stays open.
    /// </summary>
    private async Task AskAsync(string title, string? text, TextBox box, Func<string, string?> save)
    {
        var problem = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 8 };
        if (text is not null)
        {
            content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        }

        content.Children.Add(box);
        content.Children.Add(problem);
        box.SelectionStart = 0;
        box.SelectionLength = box.Text.Length;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (save(box.Text) is { } wrong)
            {
                problem.Text = wrong;
                problem.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Most likely another dialog is open: the user is busy with it.
            AppLog.Write($"Couldn't show \"{title}\"", ex);
        }
    }
}

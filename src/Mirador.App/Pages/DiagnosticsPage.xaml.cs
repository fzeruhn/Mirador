using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class DiagnosticsPage : Page
{
    /// <summary>Text that came while the user was selecting in a box; it goes in once they stop.</summary>
    private readonly Dictionary<TextBox, string> waiting = [];

    public DiagnosticsPage()
    {
        ViewModel = new DiagnosticsViewModel(App.Current.Host, App.Current.Ui);
        InitializeComponent();
    }

    public DiagnosticsViewModel ViewModel { get; }

    // An instance method: x:Bind calls functions on the page instance.
    public InfoBarSeverity SeverityFor(bool passed) => passed ? InfoBarSeverity.Success : InfoBarSeverity.Error;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LogsChanged += OnLogsChanged;
        var sentFor = VerifyNext;
        VerifyNext = null;
        if (sentFor is { } verify)
        {
            ViewModel.ShowVerify(verify.ProfileId, verify.LogFile);
        }

        ViewModel.Attach();
        if (sentFor is not null)
        {
            VerifyExpander.IsExpanded = true;
            LogPicker.SelectedItem = LogPicker.Items[^1];
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        ViewModel.LogsChanged -= OnLogsChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnCheckClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissCheck();

    private void OnSavedClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissSaved();

    private void OnMessageClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissMessage();

    private void OnLogsChanged(string? monadoText, string? miradorText)
    {
        if (monadoText is not null)
        {
            Show(MonadoBox, monadoText);
        }

        if (miradorText is not null)
        {
            Show(MiradorBox, miradorText);
        }
    }

    /// <summary>The logs' least height; a window too short for it scrolls the page instead.</summary>
    private const double LogsMinHeight = 240;

    /// <summary>
    /// Makes the logs fill what the window has left under the tools, but never less than <see cref="LogsMinHeight"/>:
    /// a star row can't do that inside a scrolling page.
    /// </summary>
    private void FitLogs(object sender, SizeChangedEventArgs e)
    {
        if (LogsArea is null || PageGrid is null || PageScroller is null)
        {
            return;
        }

        var top = LogsArea.TransformToVisual(PageGrid).TransformPoint(default).Y;
        var left = PageScroller.ViewportHeight - top - PageGrid.Padding.Bottom;
        LogsArea.Height = Math.Max(LogsMinHeight, Math.Floor(left));
    }

    /// <summary>
    /// Set before navigating here from a profile's Verify card: the page then opens on that profile's result and the log
    /// of its last run.
    /// </summary>
    public static (string? ProfileId, string? LogFile)? VerifyNext { get; set; }

    /// <summary>Shows the log picked above the box, at its end.</summary>
    private void OnLogPicked(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (MonadoBox is null || MiradorBox is null || VerifyBox is null)
        {
            // The first item is picked while the page is still being built.
            return;
        }

        TextBox[] boxes = [MonadoBox, MiradorBox, VerifyBox];
        var picked = Math.Max(0, sender.Items.IndexOf(sender.SelectedItem));
        for (var i = 0; i < boxes.Length; i++)
        {
            boxes[i].Visibility = i == picked ? Visibility.Visible : Visibility.Collapsed;
        }

        var shown = boxes[picked];
        shown.UpdateLayout();
        if (Find<ScrollViewer>(shown) is { } viewer)
        {
            viewer.ChangeView(null, viewer.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { SelectionLength: 0 } box && waiting.Remove(box, out var text))
        {
            Show(box, text);
        }
    }

    /// <summary>
    /// Puts new text into a box, unless the user is selecting there (it waits for them); follows the end only when the
    /// box was at its end, so reading further up isn't interrupted.
    /// </summary>
    private void Show(TextBox box, string text)
    {
        if (box.SelectionLength > 0)
        {
            waiting[box] = text;
            return;
        }

        var viewer = Find<ScrollViewer>(box);
        var atEnd = viewer is null || viewer.VerticalOffset >= viewer.ScrollableHeight - 4;
        var offset = viewer?.VerticalOffset ?? 0;
        box.Text = text;
        if (viewer is null)
        {
            // The page is still opening (the box has no scroll viewer yet): show the end once it is laid out.
            box.Loaded += ToEnd;
            return;
        }

        // Measured now, so that the end is that of the new text (a queued scroll could still see the old one).
        box.UpdateLayout();
        viewer.ChangeView(null, atEnd ? viewer.ScrollableHeight : offset, null, disableAnimation: true);
    }

    private static void ToEnd(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        box.Loaded -= ToEnd;
        box.UpdateLayout();
        if (Find<ScrollViewer>(box) is { } viewer)
        {
            viewer.ChangeView(null, viewer.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private static T? Find<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }

            if (Find<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}

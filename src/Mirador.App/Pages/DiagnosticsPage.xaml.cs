using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        ViewModel = new DiagnosticsViewModel(App.Current.Host, App.Current.Ui);
        InitializeComponent();
    }

    public DiagnosticsViewModel ViewModel { get; }

    public static InfoBarSeverity Severity(bool passed) => passed ? InfoBarSeverity.Success : InfoBarSeverity.Error;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LogsGrew += OnLogsGrew;
        ViewModel.Attach();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        ViewModel.LogsGrew -= OnLogsGrew;
        base.OnNavigatedFrom(e);
    }

    /// <summary>Follows new lines: keeps each box at its end once it has laid out its new text.</summary>
    private void OnLogsGrew()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ScrollToEnd(MonadoBox);
            ScrollToEnd(MiradorBox);
        });
    }

    private static void ScrollToEnd(TextBox box)
    {
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

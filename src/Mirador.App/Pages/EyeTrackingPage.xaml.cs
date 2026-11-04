using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class EyeTrackingPage : Page
{
    public EyeTrackingPage()
    {
        ViewModel = new EyeTrackingViewModel(App.Current.Host);
        InitializeComponent();
    }

    public EyeTrackingViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    private void OnResultClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissResult();
}

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        ViewModel = App.Current.Home;
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }
}

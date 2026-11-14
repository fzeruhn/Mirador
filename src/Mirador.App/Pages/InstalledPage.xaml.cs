using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

/// <summary>What Mirador has installed, and at which version.</summary>
public sealed partial class InstalledPage : Page
{
    public InstalledPage()
    {
        ViewModel = new InstalledViewModel(App.Current.Host);
        InitializeComponent();
    }

    public InstalledViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshAsync();
    }
}

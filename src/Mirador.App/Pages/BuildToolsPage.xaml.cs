using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

/// <summary>"Check build tools" (Mirador.md §4.11 item 49), reached from a branch profile's build card.</summary>
public sealed partial class BuildToolsPage : Page
{
    public BuildToolsPage()
    {
        ViewModel = BuildToolsViewModel.Shared;
        InitializeComponent();
    }

    public BuildToolsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.IsShown = true;
        if (!ViewModel.IsBusy)
        {
            await ViewModel.CheckAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.IsShown = false;
        base.OnNavigatedFrom(e);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }
}

using Microsoft.UI.Xaml.Controls;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(App.Current.Host);
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public SettingsViewModel ViewModel { get; }
}
